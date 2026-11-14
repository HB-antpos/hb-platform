import assert from "node:assert/strict";
import test from "node:test";

import {
  EMPTY_POS_SETTINGS_STATE,
  POS_SETTINGS_UNSUPPORTED_BACKOFF_MS,
  PosSettingsService,
  type PosSettingBinding,
  type PosSettingOfflineOverrideAudit,
  type PosSettingsAckPayload,
  type PosSettingsLocalState,
  type PosSettingsLogEvent,
  type PosSettingsSyncResponse,
} from "./pos-settings-service";

const CASH = "cashMovement.enabled";
const CARD = "payment.manualCard";
const VOUCHER = "payment.voucher";

function httpError(status: number) {
  return Object.assign(new Error(`http ${status}`), { name: "HbposApiError", kind: "http", status });
}

function networkError() {
  return Object.assign(new Error("offline"), { name: "HbposApiError", kind: "transport" });
}

function changed(
  hash: string,
  settings: Record<string, { value: boolean; source: string }>,
): PosSettingsSyncResponse {
  return { changed: true, hash, settings };
}

function unchanged(hash: string): PosSettingsSyncResponse {
  return { changed: false, hash, settings: null };
}

/** 假绑定：模拟「本机原来的开关存储」，记录每次写入。 */
class FakeBinding implements PosSettingBinding {
  public readonly writes: boolean[] = [];
  public failWrite = false;
  public failRead = false;
  public constructor(public readonly key: string, public value = false) {}
  public async read(): Promise<boolean> {
    if (this.failRead) throw new Error("read boom");
    return this.value;
  }
  public async write(value: boolean): Promise<void> {
    if (this.failWrite) throw new Error("write boom");
    this.value = value;
    this.writes.push(value);
  }
}

class Harness {
  public storeCode: string | null = "S001";
  public nowMs = Date.parse("2026-11-14T00:00:00Z");
  public saved: PosSettingsLocalState | null = null;
  public saveCount = 0;
  public failSave = false;
  public readonly syncCalls: string[] = [];
  public readonly acks: PosSettingsAckPayload[] = [];
  public readonly audits: PosSettingOfflineOverrideAudit[] = [];
  public readonly logs: PosSettingsLogEvent[] = [];
  public syncImpl: (knownHash: string) => Promise<PosSettingsSyncResponse> = async () => unchanged("");
  public ackImpl: (payload: PosSettingsAckPayload) => Promise<void> = async () => undefined;
  public readonly cash = new FakeBinding(CASH, false);
  public readonly card = new FakeBinding(CARD, true);
  public readonly voucher = new FakeBinding(VOUCHER, false);

  /** 读取当前落盘快照；避免 assert.equal(h.saved, null) 把类型收窄成 never。 */
  public snapshot(): PosSettingsLocalState | null {
    return this.saved;
  }

  public build(initial: PosSettingsLocalState | null = null): PosSettingsService {
    this.saved = initial;
    return new PosSettingsService({
      api: {
        sync: async (knownHash) => {
          this.syncCalls.push(knownHash);
          return this.syncImpl(knownHash);
        },
        ack: async (payload) => {
          this.acks.push(payload);
          await this.ackImpl(payload);
        },
      },
      store: {
        load: async () => this.saved,
        save: async (state) => {
          if (this.failSave) throw new Error("save boom");
          this.saved = state;
          this.saveCount++;
        },
      },
      bindings: [this.cash, this.card, this.voucher],
      boundStoreCode: () => this.storeCode,
      appVersion: () => "1.0.13",
      now: () => this.nowMs,
      audit: { recordOfflineOverride: async (entry) => void this.audits.push(entry) },
      log: (event) => void this.logs.push(event),
    });
  }
}

const HQ = changed("h1", {
  [CASH]: { value: true, source: "store" },
  [CARD]: { value: false, source: "global" },
});

test("首次同步：总部值写穿到本机存储、落盘快照并回执实际值", async () => {
  const h = new Harness();
  h.syncImpl = async () => HQ;
  const service = h.build();
  const result = await service.requestSync("startup");

  assert.deepEqual(result, { status: "updated", hash: "h1", ackPending: false });
  assert.equal(h.cash.value, true);
  assert.equal(h.card.value, false);
  // 总部没管的项沿用本机值，不被写入。
  assert.equal(h.voucher.value, false);
  assert.deepEqual(h.voucher.writes, []);
  assert.equal(h.syncCalls[0], "");
  assert.equal(h.saved?.hash, "h1");
  assert.equal(h.saved?.storeCode, "S001");
  assert.equal(h.saved?.ackedHash, "h1");
  assert.deepEqual(h.acks, [
    {
      hash: "h1",
      applied: { [CASH]: true, [CARD]: false, [VOUCHER]: false },
      appVersion: "1.0.13",
      offlineOverrideCount: 0,
    },
  ]);
});

test("锁定：总部管理的项在线时拒绝修改，整批不写入；没管的项放行", async () => {
  const h = new Harness();
  h.syncImpl = async () => HQ;
  const service = h.build();
  await service.requestSync("startup");

  const locked = await service.trySet([
    { key: VOUCHER, value: true },
    { key: CARD, value: true },
  ]);
  assert.deepEqual(locked, { outcome: "locked", key: CARD });
  // 全有或全无：被拒的整批里，没被锁的券开关也不能写入。
  assert.equal(h.voucher.value, false);
  assert.equal(h.card.value, false);

  assert.deepEqual(await service.trySet([{ key: VOUCHER, value: true }]), { outcome: "applied" });
  assert.equal(h.voucher.value, true);
  assert.equal(h.audits.length, 0);
});

test("启动早期状态没加载时，trySet 也要先读快照再判锁（重启后不能绕过锁）", async () => {
  const h = new Harness();
  const service = h.build({
    ...EMPTY_POS_SETTINGS_STATE,
    storeCode: "S001",
    hash: "h1",
    values: { [CARD]: { value: false, source: "global" } },
    ackedHash: "h1",
  });
  const result = await service.trySet([{ key: CARD, value: true }]);
  assert.deepEqual(result, { outcome: "locked", key: CARD });
  assert.equal(h.card.value, true);
  assert.deepEqual(h.card.writes, []);
});

test("联网状态未知按在线处理；报告离线后放行，并登记覆盖、次数与审计", async () => {
  const h = new Harness();
  h.syncImpl = async () => HQ;
  const service = h.build();
  await service.requestSync("startup");
  assert.equal(service.getView().isOnline, true);

  service.reportConnectivity(false);
  assert.equal(service.getView().showsOfflineBanner, true);
  assert.equal(service.getView().hasLockedItems, false);

  assert.deepEqual(await service.trySet([{ key: CARD, value: true }]), { outcome: "applied" });
  assert.equal(h.card.value, true);
  assert.equal(h.saved?.offlineOverrideCount, 1);
  assert.deepEqual(h.saved?.overrides, { [CARD]: true });
  assert.deepEqual(h.audits, [
    { key: CARD, previous: false, current: true, managedValue: false, managedSource: "global" },
  ]);
  const item = service.getView().items.find((entry) => entry.key === CARD)!;
  assert.equal(item.isOfflineOverride, true);
  assert.equal(item.isLocked, false);
});

test("离线时改成和当前值一样不计次数；改没管的项不计次数", async () => {
  const h = new Harness();
  h.syncImpl = async () => HQ;
  const service = h.build();
  await service.requestSync("startup");
  service.reportConnectivity(false);

  await service.trySet([{ key: CARD, value: false }]);
  await service.trySet([{ key: VOUCHER, value: true }]);
  assert.equal(h.saved?.offlineOverrideCount, 0);
  assert.equal(h.audits.length, 0);
});

test("恢复联网后的同步用总部值覆盖离线修改，并回执累计次数（只增不减）", async () => {
  const h = new Harness();
  h.syncImpl = async () => HQ;
  const service = h.build();
  await service.requestSync("startup");
  service.reportConnectivity(false);
  await service.trySet([{ key: CARD, value: true }]);
  await service.trySet([{ key: CARD, value: false }]);
  await service.trySet([{ key: CARD, value: true }]);
  assert.equal(h.saved?.offlineOverrideCount, 3);

  service.reportConnectivity(true);
  h.acks.length = 0;
  h.syncImpl = async () => unchanged("h1");
  const result = await service.requestSync("network");

  assert.equal(result.status, "up-to-date");
  assert.equal(h.card.value, false, "离线修改被总部值覆盖");
  assert.deepEqual(h.saved?.overrides, {});
  assert.equal(h.saved?.offlineOverrideCount, 3);
  assert.equal(h.acks.length, 1);
  assert.equal(h.acks[0]!.offlineOverrideCount, 3);
  assert.equal(h.acks[0]!.applied[CARD], false);
  assert.equal(service.getView().items.find((item) => item.key === CARD)!.isOfflineOverride, false);
});

test("稳态同步（指纹没变、无需核对）不写库也不重复回执", async () => {
  const h = new Harness();
  h.syncImpl = async () => HQ;
  const service = h.build();
  await service.requestSync("startup");
  const saves = h.saveCount;
  const acks = h.acks.length;

  h.syncImpl = async () => unchanged("h1");
  const result = await service.requestSync("timer");
  assert.equal(result.status, "up-to-date");
  assert.equal(h.saveCount, saves);
  assert.equal(h.acks.length, acks);
  assert.equal(h.syncCalls.at(-1), "h1", "带上已应用指纹");
});

test("重启后第一次成功同步会核对：本机被改过的值被总部值写回", async () => {
  const h = new Harness();
  const service = h.build({
    ...EMPTY_POS_SETTINGS_STATE,
    storeCode: "S001",
    hash: "h1",
    values: { [CARD]: { value: false, source: "global" } },
    ackedHash: "h1",
  });
  h.syncImpl = async () => unchanged("h1");
  await service.requestSync("startup");
  assert.equal(h.card.value, false);
  assert.deepEqual(h.card.writes, [false]);
});

test("总部清除某项后，该项解锁且本机沿用当前值", async () => {
  const h = new Harness();
  h.syncImpl = async () => HQ;
  const service = h.build();
  await service.requestSync("startup");

  h.syncImpl = async () => changed("h2", { [CASH]: { value: true, source: "store" } });
  await service.requestSync("timer");
  const card = service.getView().items.find((item) => item.key === CARD)!;
  assert.equal(card.isManaged, false);
  assert.equal(card.isLocked, false);
  assert.equal(h.card.value, false, "沿用清除时的本机值，不回滚");
  assert.deepEqual(await service.trySet([{ key: CARD, value: true }]), { outcome: "applied" });
});

test("写穿失败：不落盘快照，下一轮重试并最终收敛", async () => {
  const h = new Harness();
  h.syncImpl = async () => HQ;
  h.card.failWrite = true;
  const service = h.build();
  const first = await service.requestSync("startup");
  assert.deepEqual(first, { status: "failed", reason: "storage" });
  assert.equal(h.saved, null);
  assert.equal(h.acks.length, 0);

  h.card.failWrite = false;
  const second = await service.requestSync("timer");
  assert.equal(second.status, "updated");
  assert.equal(h.snapshot()?.hash, "h1");
});

test("快照落盘失败：不更新内存状态，不回执", async () => {
  const h = new Harness();
  h.syncImpl = async () => HQ;
  h.failSave = true;
  const service = h.build();
  const result = await service.requestSync("startup");
  assert.deepEqual(result, { status: "failed", reason: "storage" });
  assert.equal(h.acks.length, 0);
  assert.equal(service.getView().items.every((item) => !item.isManaged), true);
});

test("404：暂不支持，不锁任何项，退避 10 分钟后才再试；手动同步无视退避", async () => {
  const h = new Harness();
  h.syncImpl = async () => {
    throw httpError(404);
  };
  const service = h.build();
  assert.deepEqual(await service.requestSync("startup"), { status: "failed", reason: "unsupported" });
  assert.equal(service.getView().hasLockedItems, false);

  assert.deepEqual(await service.requestSync("timer"), { status: "skipped", reason: "backoff" });
  assert.equal(h.syncCalls.length, 1);

  h.nowMs += POS_SETTINGS_UNSUPPORTED_BACKOFF_MS + 1;
  h.syncImpl = async () => HQ;
  assert.equal((await service.requestSync("timer")).status, "updated");

  h.syncImpl = async () => {
    throw httpError(404);
  };
  await service.requestSync("timer");
  // 退避期内定时同步被跳过，手动同步照常发请求。
  const callsBefore = h.syncCalls.length;
  assert.deepEqual(await service.requestSync("timer"), { status: "skipped", reason: "backoff" });
  assert.equal(h.syncCalls.length, callsBefore);
  h.syncImpl = async () => unchanged("h1");
  assert.equal((await service.syncNow()).status, "up-to-date");
  assert.equal(h.syncCalls.length, callsBefore + 1);
});

test("失败分类：离线 / 未授权 / 服务端错误；失败日志只在状态变化时记一次", async () => {
  const h = new Harness();
  const service = h.build();
  h.syncImpl = async () => {
    throw networkError();
  };
  assert.deepEqual(await service.requestSync("timer"), { status: "failed", reason: "offline" });
  await service.requestSync("timer");
  await service.requestSync("timer");
  assert.equal(h.logs.filter((log) => log.message === "POS settings sync failed.").length, 1);

  h.syncImpl = async () => {
    throw httpError(403);
  };
  assert.deepEqual(await service.requestSync("timer"), { status: "failed", reason: "unauthorized" });
  h.syncImpl = async () => {
    throw httpError(503);
  };
  assert.deepEqual(await service.requestSync("timer"), { status: "failed", reason: "server-error" });
  assert.equal(h.saved, null, "失败时不改动本机任何设置");
});

test("无效响应一律丢弃：缺指纹、changed 但没有 settings、changed=false 却指纹不同", async () => {
  const h = new Harness();
  const service = h.build();
  h.syncImpl = async () => ({ changed: true, hash: "", settings: {} });
  assert.deepEqual(await service.requestSync("timer"), { status: "failed", reason: "invalid-response" });
  h.syncImpl = async () => ({ changed: true, hash: "h9", settings: null });
  assert.deepEqual(await service.requestSync("timer"), { status: "failed", reason: "invalid-response" });
  h.syncImpl = async () => unchanged("h9");
  assert.deepEqual(await service.requestSync("timer"), { status: "failed", reason: "invalid-response" });
  assert.equal(h.saved, null);
  assert.equal(h.cash.writes.length + h.card.writes.length, 0);
});

test("只认本机支持的键；来源缺失按 global；未知键忽略", async () => {
  const h = new Harness();
  h.syncImpl = async () =>
    changed("h1", {
      [VOUCHER]: { value: true, source: "" },
      "returns.rescan": { value: true, source: "store" },
    });
  const service = h.build();
  await service.requestSync("startup");
  assert.deepEqual(h.saved?.values, { [VOUCHER]: { value: true, source: "global" } });
  assert.deepEqual(h.acks[0]!.applied, { [CASH]: false, [CARD]: true, [VOUCHER]: true });
});

test("换店：旧店快照作废，不沿用旧店的锁定和累计次数", async () => {
  const h = new Harness();
  h.syncImpl = async () => HQ;
  const service = h.build({
    ...EMPTY_POS_SETTINGS_STATE,
    storeCode: "S999",
    hash: "old",
    values: { [VOUCHER]: { value: true, source: "store" } },
    offlineOverrideCount: 7,
  });
  await service.load();
  assert.equal(service.getView().items.find((item) => item.key === VOUCHER)!.isManaged, false);
  await service.requestSync("startup");
  assert.equal(h.syncCalls[0], "", "换店后按未同步过拉全量");
  assert.equal(h.saved?.offlineOverrideCount, 0);
});

test("请求期间设备换了店：丢弃响应，不写入", async () => {
  const h = new Harness();
  h.syncImpl = async () => {
    h.storeCode = "S002";
    return HQ;
  };
  const service = h.build();
  assert.deepEqual(await service.requestSync("timer"), { status: "failed", reason: "store-mismatch" });
  assert.equal(h.cash.writes.length, 0);
});

test("设备未就绪（没有门店代码）：跳过，不发请求", async () => {
  const h = new Harness();
  h.storeCode = null;
  const service = h.build();
  assert.deepEqual(await service.requestSync("startup"), { status: "skipped", reason: "not-ready" });
  assert.equal(h.syncCalls.length, 0);
});

test("回执 400：同一份不再重试；回执 5xx：下一轮重试；回执成功才记已回执", async () => {
  const h = new Harness();
  h.syncImpl = async () => HQ;
  h.ackImpl = async () => {
    throw httpError(500);
  };
  const service = h.build();
  const first = await service.requestSync("startup");
  assert.deepEqual(first, { status: "updated", hash: "h1", ackPending: true });
  assert.equal(h.saved?.ackedHash, "");

  h.syncImpl = async () => unchanged("h1");
  await service.requestSync("timer");
  assert.equal(h.acks.length, 2, "5xx 下一轮重试");

  h.ackImpl = async () => {
    throw httpError(400);
  };
  await service.requestSync("timer");
  const callsAfterReject = h.acks.length;
  await service.requestSync("timer");
  await service.requestSync("timer");
  assert.equal(h.acks.length, callsAfterReject, "400 后本进程不再重试同一份");

  h.ackImpl = async () => undefined;
  service.reportConnectivity(false);
  service.reportConnectivity(true);
  await service.trySet([{ key: VOUCHER, value: true }]);
  // 次数没变、指纹没变，且已被 400 拒绝的签名不变 → 仍不重试。
  await service.requestSync("network");
  assert.equal(h.acks.length, callsAfterReject);
});

test("单飞：同一时刻只有一个同步在飞，并复用其结果", async () => {
  const h = new Harness();
  let release: (() => void) | undefined;
  h.syncImpl = () =>
    new Promise((resolve) => {
      release = () => resolve(HQ);
    });
  const service = h.build();
  const first = service.requestSync("startup");
  const second = service.requestSync("foreground");
  await new Promise((resolve) => setTimeout(resolve, 5));
  release!();
  const [a, b] = await Promise.all([first, second]);
  assert.equal(h.syncCalls.length, 1);
  assert.equal(a, b);
});

test("订阅：视图变化才通知；相同内容的刷新不通知；订阅方抛错不影响同步", async () => {
  const h = new Harness();
  h.syncImpl = async () => HQ;
  const service = h.build();
  let notified = 0;
  service.subscribe(() => {
    notified++;
    throw new Error("listener boom");
  });
  await service.load();
  const afterLoad = notified;
  await service.load();
  assert.equal(notified, afterLoad, "内容没变不通知");

  const result = await service.requestSync("startup");
  assert.equal(result.status, "updated");
  assert.ok(notified > afterLoad);
  service.reportConnectivity(true);
  const afterOnline = notified;
  service.reportConnectivity(true);
  assert.equal(notified, afterOnline);
});

test("审计写入失败不影响离线修改", async () => {
  const h = new Harness();
  h.syncImpl = async () => HQ;
  const service = new PosSettingsService({
    api: { sync: async () => HQ, ack: async () => undefined },
    store: { load: async () => null, save: async (state) => void (h.saved = state) },
    bindings: [h.cash, h.card],
    boundStoreCode: () => "S001",
    appVersion: () => null,
    now: () => h.nowMs,
    audit: {
      recordOfflineOverride: async () => {
        throw new Error("audit boom");
      },
    },
    log: (event) => void h.logs.push(event),
  });
  await service.requestSync("startup");
  service.reportConnectivity(false);
  assert.deepEqual(await service.trySet([{ key: CARD, value: true }]), { outcome: "applied" });
  assert.equal(h.card.value, true);
  assert.equal(h.saved?.offlineOverrideCount, 1);
  assert.ok(h.logs.some((log) => log.message === "POS setting audit failed."));
});

test("未知键：拒绝且不写任何内容", async () => {
  const h = new Harness();
  const service = h.build();
  assert.deepEqual(await service.trySet([{ key: "nope", value: true }]), {
    outcome: "unknown-key",
    key: "nope",
  });
});

test("本机写入失败：返回 failed，不登记覆盖、不计次数", async () => {
  const h = new Harness();
  h.syncImpl = async () => HQ;
  const service = h.build();
  await service.requestSync("startup");
  service.reportConnectivity(false);
  h.card.failWrite = true;
  assert.deepEqual(await service.trySet([{ key: CARD, value: true }]), { outcome: "failed" });
  assert.equal(h.saved?.offlineOverrideCount, 0);
  assert.equal(h.audits.length, 0);
});

test("离线过一次：恢复联网后的第一次成功同步必定核对，即使没有登记过覆盖", async () => {
  const h = new Harness();
  h.syncImpl = async () => HQ;
  const service = h.build();
  await service.requestSync("startup");

  // 稳态：指纹没变时不核对，本机被外部改动的值不会被碰（这里只证明稳态确实不写）。
  h.syncImpl = async () => unchanged("h1");
  h.card.value = true;
  await service.requestSync("timer");
  assert.equal(h.card.value, true);

  // 离线一次后恢复：必须核对一遍，把总部值写回。
  service.reportConnectivity(false);
  service.reportConnectivity(true);
  await service.requestSync("network");
  assert.equal(h.card.value, false);
});
