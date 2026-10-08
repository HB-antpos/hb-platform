import assert from "node:assert/strict";
import test from "node:test";

import {
  RECEIPT_PROFILE_UNSUPPORTED_BACKOFF_MS,
  ReceiptProfileSyncController,
  type ReceiptProfileApplyOutcome,
  type ReceiptProfileLocalState,
  type ReceiptProfileSnapshot,
  type ReceiptProfileSyncLogEvent,
  type ReceiptProfileSyncResponse,
} from "./receipt-profile-sync-controller";

function snapshot(overrides: Partial<ReceiptProfileSnapshot> = {}): ReceiptProfileSnapshot {
  return {
    version: 3,
    storeCode: "S001",
    storeName: "Hot Bargain Bankstown",
    brandName: "Hot Bargain",
    address: "1 Main Street\nBankstown NSW",
    phone: "02 1234 5678",
    abn: "12 345 678 901",
    returnPolicy: "Returns within 14 days.",
    voucherTerms: "",
    installmentTerms: "",
    ...overrides,
  };
}

function httpError(status: number, code?: string) {
  return Object.assign(new Error(`http ${status}`), {
    name: "HbposApiError",
    kind: "http",
    status,
    ...(code ? { code } : {}),
  });
}

function networkError() {
  return Object.assign(new Error("offline"), {
    name: "HbposApiError",
    kind: "transport",
    code: "NO_HTTP_RESPONSE",
  });
}

class Harness {
  public local: ReceiptProfileLocalState = { profileVersion: 0, profileAckedVersion: 0 };
  public boundStoreCode: string | null = "S001";
  public nowMs = 1_000_000;
  public readonly syncCalls: number[] = [];
  public readonly ackCalls: number[] = [];
  public readonly applied: ReceiptProfileSnapshot[] = [];
  public readonly logs: ReceiptProfileSyncLogEvent[] = [];
  public syncImpl: (knownVersion: number) => Promise<ReceiptProfileSyncResponse> = async () => ({
    changed: false,
    version: 0,
    profile: null,
  });
  public ackImpl: (version: number) => Promise<void> = async () => undefined;
  public applyOutcome: ReceiptProfileApplyOutcome = "applied";
  public applyError: Error | null = null;
  public readError: Error | null = null;
  public markAckedError: Error | null = null;

  public readonly controller = new ReceiptProfileSyncController({
    api: {
      sync: async (knownVersion) => {
        this.syncCalls.push(knownVersion);
        return this.syncImpl(knownVersion);
      },
      ack: async (version) => {
        this.ackCalls.push(version);
        await this.ackImpl(version);
      },
    },
    store: {
      read: async () => {
        if (this.readError) throw this.readError;
        return this.local;
      },
      apply: async (profile) => {
        if (this.applyError) throw this.applyError;
        if (this.applyOutcome === "applied") {
          this.applied.push(profile);
          this.local = {
            profileVersion: profile.version,
            profileAckedVersion: Math.min(this.local.profileAckedVersion, profile.version - 1),
          };
        }
        return this.applyOutcome;
      },
      markAcked: async (version) => {
        if (this.markAckedError) throw this.markAckedError;
        if (this.local.profileVersion === version) {
          this.local = { ...this.local, profileAckedVersion: version };
        }
      },
    },
    boundStoreCode: () => this.boundStoreCode,
    now: () => this.nowMs,
    log: (event) => this.logs.push(event),
  });
}

test("changed=true：校验通过后原子写入、回执并记录已回执版本；日志只含版本号不含资料内容", async () => {
  const h = new Harness();
  h.syncImpl = async () => ({ changed: true, version: 3, profile: snapshot() });

  const result = await h.controller.requestSync("startup");

  assert.deepEqual(result, { status: "updated", version: 3 });
  assert.deepEqual(h.syncCalls, [0]);
  assert.equal(h.applied.length, 1);
  assert.equal(h.applied[0]?.version, 3);
  assert.deepEqual(h.ackCalls, [3]);
  assert.deepEqual(h.local, { profileVersion: 3, profileAckedVersion: 3 });
  const serialized = JSON.stringify(h.logs);
  assert.match(serialized, /"version":3/);
  for (const secret of ["Main Street", "1234 5678", "345 678", "Bankstown", "Returns within"]) {
    assert.equal(serialized.includes(secret), false, `日志不得包含资料内容：${secret}`);
  }
});

test("券使用说明与分期条款随资料一起原子写入并回执；正文原样透传，日志不含正文内容", async () => {
  const h = new Harness();
  const voucherTerms = "Use at the issuing store only.\r\n\r\n  Valid for 90 days.  ";
  const installmentTerms = "Minimum deposit $30.\nLater payments from $10.";
  h.syncImpl = async () => ({
    changed: true,
    version: 5,
    profile: snapshot({ version: 5, voucherTerms, installmentTerms }),
  });

  const result = await h.controller.requestSync("startup");

  assert.deepEqual(result, { status: "updated", version: 5 });
  assert.equal(h.applied.length, 1);
  // 控制器只透传，不改写正文（拆行、trim、回落默认由打印层统一处理）。
  assert.equal(h.applied[0]?.voucherTerms, voucherTerms);
  assert.equal(h.applied[0]?.installmentTerms, installmentTerms);
  assert.deepEqual(h.ackCalls, [5]);
  assert.deepEqual(h.local, { profileVersion: 5, profileAckedVersion: 5 });
  const serialized = JSON.stringify(h.logs);
  for (const secret of ["issuing store", "Valid for 90", "Minimum deposit", "Later payments"]) {
    assert.equal(serialized.includes(secret), false, `日志不得包含条款正文：${secret}`);
  }
});

test("未定制的旧快照（两个条款字段为空串）照常写入并回执，不影响其它字段", async () => {
  const h = new Harness();
  h.syncImpl = async () => ({ changed: true, version: 2, profile: snapshot({ version: 2 }) });

  const result = await h.controller.requestSync("timer");

  assert.deepEqual(result, { status: "updated", version: 2 });
  assert.equal(h.applied[0]?.voucherTerms, "");
  assert.equal(h.applied[0]?.installmentTerms, "");
  assert.equal(h.applied[0]?.returnPolicy, "Returns within 14 days.");
  assert.deepEqual(h.ackCalls, [2]);
});

test("条款字段本机校验不通过（rejected）时整份不写入不回执，同一版本不再重试", async () => {
  const h = new Harness();
  h.applyOutcome = "rejected";
  h.syncImpl = async () => ({
    changed: true,
    version: 6,
    profile: snapshot({ version: 6, voucherTerms: "x".repeat(601) }),
  });

  assert.deepEqual(await h.controller.requestSync("startup"), {
    status: "failed",
    reason: "invalid-profile",
  });
  assert.deepEqual(h.ackCalls, []);
  assert.deepEqual(h.local, { profileVersion: 0, profileAckedVersion: 0 });
  // 同一版本第二次返回：本进程内直接判定不合规，不再写入。
  assert.deepEqual(await h.controller.syncNow(), { status: "failed", reason: "invalid-profile" });
  assert.equal(h.applied.length, 0);
});

test("落盘时统一使用本机绑定门店代码（大小写不同也视为同店）", async () => {
  const h = new Harness();
  h.boundStoreCode = "S001";
  h.syncImpl = async () => ({
    changed: true,
    version: 1,
    profile: snapshot({ version: 1, storeCode: " s001 " }),
  });

  const result = await h.controller.requestSync("foreground");

  assert.deepEqual(result, { status: "updated", version: 1 });
  assert.equal(h.applied[0]?.storeCode, "S001");
});

test("changed=false 且版本一致：不写入也不回执；版本高于已回执版本时补发回执", async () => {
  const h = new Harness();
  h.local = { profileVersion: 3, profileAckedVersion: 3 };
  h.syncImpl = async () => ({ changed: false, version: 3, profile: null });
  assert.deepEqual(await h.controller.requestSync("timer"), { status: "up-to-date", version: 3 });
  assert.deepEqual(h.syncCalls, [3]);
  assert.deepEqual(h.ackCalls, []);
  assert.deepEqual(h.applied, []);

  h.local = { profileVersion: 3, profileAckedVersion: 2 };
  assert.deepEqual(await h.controller.requestSync("timer"), { status: "up-to-date", version: 3 });
  assert.deepEqual(h.ackCalls, [3]);
  assert.deepEqual(h.local, { profileVersion: 3, profileAckedVersion: 3 });
});

test("服务端从未下发（version=0）：返回 not-published，不覆盖本机、不回执", async () => {
  const h = new Harness();
  h.local = { profileVersion: 0, profileAckedVersion: 0 };
  h.syncImpl = async () => ({ changed: false, version: 0, profile: null });

  assert.deepEqual(await h.controller.syncNow(), { status: "not-published" });
  assert.deepEqual(h.applied, []);
  assert.deepEqual(h.ackCalls, []);
});

test("changed=true 但 profile 缺失或 version 缺失/≤0：视为无效响应，丢弃且不写入", async () => {
  for (const response of [
    { changed: true, version: 4, profile: null },
    { changed: true, version: 4, profile: snapshot({ version: 0 }) },
    { changed: true, version: 4, profile: snapshot({ version: -1 }) },
    { changed: true, version: 4, profile: snapshot({ version: 1.5 }) },
  ] satisfies ReceiptProfileSyncResponse[]) {
    const h = new Harness();
    h.syncImpl = async () => response;
    assert.deepEqual(await h.controller.requestSync("timer"), {
      status: "failed",
      reason: "invalid-response",
    });
    assert.deepEqual(h.applied, []);
    assert.deepEqual(h.ackCalls, []);
  }
});

test("服务端声称有版本却报告无变化而本机从未应用过：响应自相矛盾，丢弃", async () => {
  const h = new Harness();
  h.syncImpl = async () => ({ changed: false, version: 5, profile: null });
  assert.deepEqual(await h.controller.requestSync("timer"), {
    status: "failed",
    reason: "invalid-response",
  });
  assert.deepEqual(h.ackCalls, []);
});

test("返回资料的门店与本机绑定门店不一致：丢弃防串店", async () => {
  const h = new Harness();
  h.syncImpl = async () => ({
    changed: true,
    version: 2,
    profile: snapshot({ version: 2, storeCode: "S999" }),
  });

  assert.deepEqual(await h.controller.requestSync("startup"), {
    status: "failed",
    reason: "store-mismatch",
  });
  assert.deepEqual(h.applied, []);
  assert.deepEqual(h.ackCalls, []);
});

test("本机校验不通过（rejected）：不写入不回执，同一版本不再重复写入与重复记日志，新版本照常处理", async () => {
  const h = new Harness();
  h.applyOutcome = "rejected";
  h.syncImpl = async () => ({ changed: true, version: 2, profile: snapshot({ version: 2 }) });

  assert.deepEqual(await h.controller.requestSync("timer"), { status: "failed", reason: "invalid-profile" });
  const logsAfterFirst = h.logs.length;
  assert.equal(logsAfterFirst, 1);
  assert.deepEqual(await h.controller.requestSync("timer"), { status: "failed", reason: "invalid-profile" });
  assert.equal(h.logs.length, logsAfterFirst);
  assert.deepEqual(h.ackCalls, []);

  // 总部下发了新的合规版本
  h.applyOutcome = "applied";
  h.syncImpl = async () => ({ changed: true, version: 3, profile: snapshot({ version: 3 }) });
  assert.deepEqual(await h.controller.requestSync("timer"), { status: "updated", version: 3 });
});

test("存储层 I/O 故障：本轮失败、下一轮重试写入（不同于 rejected）", async () => {
  const h = new Harness();
  h.syncImpl = async () => ({ changed: true, version: 2, profile: snapshot({ version: 2 }) });
  h.applyError = new Error("database is locked");

  assert.deepEqual(await h.controller.requestSync("timer"), { status: "failed", reason: "storage" });
  assert.deepEqual(h.ackCalls, []);

  h.applyError = null;
  assert.deepEqual(await h.controller.requestSync("timer"), { status: "updated", version: 2 });

  const reader = new Harness();
  reader.readError = new Error("read failed");
  assert.deepEqual(await reader.controller.requestSync("timer"), { status: "failed", reason: "storage" });
  assert.deepEqual(reader.syncCalls, []);
});

test("404：视为服务端尚未支持，后台退避 10 分钟，到期再试；立即同步无视退避；成功后恢复", async () => {
  const h = new Harness();
  h.syncImpl = async () => {
    throw httpError(404);
  };

  assert.deepEqual(await h.controller.requestSync("startup"), { status: "failed", reason: "unsupported" });
  assert.equal(h.syncCalls.length, 1);

  // 退避窗口内的后台触发直接跳过，不发请求
  h.nowMs += 60_000;
  assert.deepEqual(await h.controller.requestSync("timer"), { status: "skipped", reason: "backoff" });
  h.nowMs += RECEIPT_PROFILE_UNSUPPORTED_BACKOFF_MS - 60_000 - 1;
  assert.deepEqual(await h.controller.requestSync("foreground"), { status: "skipped", reason: "backoff" });
  assert.equal(h.syncCalls.length, 1);

  // 手动「立即同步」无视退避
  assert.deepEqual(await h.controller.syncNow(), { status: "failed", reason: "unsupported" });
  assert.equal(h.syncCalls.length, 2);

  // 退避到期后恢复尝试；服务端升级后成功即解除退避
  h.nowMs += RECEIPT_PROFILE_UNSUPPORTED_BACKOFF_MS;
  h.syncImpl = async () => ({ changed: false, version: 0, profile: null });
  assert.deepEqual(await h.controller.requestSync("timer"), { status: "not-published" });
  assert.deepEqual(await h.controller.requestSync("timer"), { status: "not-published" });
  assert.equal(h.syncCalls.length, 4);
});

test("网络错误、5xx、401/403：只失败不抛出，下一轮重试；同一失败连续出现只记一条日志，成功后重新计", async () => {
  const h = new Harness();
  h.syncImpl = async () => {
    throw networkError();
  };
  assert.deepEqual(await h.controller.requestSync("timer"), { status: "failed", reason: "offline" });
  assert.deepEqual(await h.controller.requestSync("timer"), { status: "failed", reason: "offline" });
  assert.equal(h.logs.length, 1);

  h.syncImpl = async () => {
    throw httpError(503);
  };
  assert.deepEqual(await h.controller.requestSync("timer"), { status: "failed", reason: "server-error" });
  h.syncImpl = async () => {
    throw httpError(403);
  };
  assert.deepEqual(await h.controller.requestSync("timer"), { status: "failed", reason: "unauthorized" });
  h.syncImpl = async () => {
    throw httpError(401);
  };
  assert.deepEqual(await h.controller.requestSync("timer"), { status: "failed", reason: "unauthorized" });

  // 恢复后能继续同步，且之前的失败不会残留退避
  h.syncImpl = async () => ({ changed: true, version: 1, profile: snapshot({ version: 1 }) });
  assert.deepEqual(await h.controller.requestSync("timer"), { status: "updated", version: 1 });
  const before = h.logs.length;
  h.syncImpl = async () => {
    throw networkError();
  };
  await h.controller.requestSync("timer");
  assert.equal(h.logs.length, before + 1);
});

test("ack 返回 400：本进程内不再对同一版本重试，也不回滚已写入资料；新版本照常回执", async () => {
  const h = new Harness();
  h.ackImpl = async () => {
    throw httpError(400, "RECEIPT_PROFILE_VERSION_INVALID");
  };
  h.syncImpl = async () => ({ changed: true, version: 2, profile: snapshot({ version: 2 }) });

  assert.deepEqual(await h.controller.requestSync("timer"), { status: "updated", version: 2 });
  assert.deepEqual(h.ackCalls, [2]);
  assert.deepEqual(h.local, { profileVersion: 2, profileAckedVersion: 0 });

  // 之后 changed=false，本机 2 > 已回执 0，但 2 已被服务端拒绝，不再重试
  h.syncImpl = async () => ({ changed: false, version: 2, profile: null });
  await h.controller.requestSync("timer");
  await h.controller.requestSync("timer");
  assert.deepEqual(h.ackCalls, [2]);
  assert.equal(h.local.profileVersion, 2);

  // 新的下发会再发 ack
  h.ackImpl = async () => undefined;
  h.syncImpl = async () => ({ changed: true, version: 3, profile: snapshot({ version: 3 }) });
  assert.deepEqual(await h.controller.requestSync("timer"), { status: "updated", version: 3 });
  assert.deepEqual(h.ackCalls, [2, 3]);
  assert.deepEqual(h.local, { profileVersion: 3, profileAckedVersion: 3 });
});

test("ack 遇到 401/403/网络错误：下一轮用「已回执版本 < 本机版本」补发，且失败日志不刷屏", async () => {
  const h = new Harness();
  let failure: Error | null = networkError();
  h.ackImpl = async () => {
    if (failure) throw failure;
  };
  h.syncImpl = async () => ({ changed: true, version: 2, profile: snapshot({ version: 2 }) });
  assert.deepEqual(await h.controller.requestSync("timer"), { status: "updated", version: 2 });
  assert.deepEqual(h.local, { profileVersion: 2, profileAckedVersion: 0 });

  h.syncImpl = async () => ({ changed: false, version: 2, profile: null });
  failure = httpError(403);
  await h.controller.requestSync("timer");
  failure = httpError(401);
  await h.controller.requestSync("timer");
  assert.deepEqual(h.ackCalls, [2, 2, 2]);

  failure = null;
  assert.deepEqual(await h.controller.requestSync("timer"), { status: "up-to-date", version: 2 });
  assert.deepEqual(h.ackCalls, [2, 2, 2, 2]);
  assert.deepEqual(h.local, { profileVersion: 2, profileAckedVersion: 2 });

  // 已回执后不再发
  await h.controller.requestSync("timer");
  assert.deepEqual(h.ackCalls, [2, 2, 2, 2]);
});

test("回执已送达但本机没记上：不影响已更新结论，下一轮幂等补发", async () => {
  const h = new Harness();
  h.markAckedError = new Error("db busy");
  h.syncImpl = async () => ({ changed: true, version: 2, profile: snapshot({ version: 2 }) });
  assert.deepEqual(await h.controller.requestSync("timer"), { status: "updated", version: 2 });
  assert.deepEqual(h.local, { profileVersion: 2, profileAckedVersion: 0 });

  h.markAckedError = null;
  h.syncImpl = async () => ({ changed: false, version: 2, profile: null });
  await h.controller.requestSync("timer");
  assert.deepEqual(h.local, { profileVersion: 2, profileAckedVersion: 2 });
});

test("同一时刻只有一个同步在飞：并发触发复用同一次请求；立即同步等在途结束后再跑新的一轮", async () => {
  const h = new Harness();
  let release!: () => void;
  const gate = new Promise<void>((resolve) => {
    release = resolve;
  });
  let concurrent = 0;
  let maxConcurrent = 0;
  h.syncImpl = async () => {
    concurrent += 1;
    maxConcurrent = Math.max(maxConcurrent, concurrent);
    await gate;
    concurrent -= 1;
    return { changed: false, version: 0, profile: null };
  };

  const first = h.controller.requestSync("startup");
  const second = h.controller.requestSync("network");
  const manual = h.controller.syncNow();
  const manualToo = h.controller.syncNow();
  await Promise.resolve();
  assert.equal(h.syncCalls.length, 1, "在途期间只发出一次请求");
  release();
  await Promise.all([first, second, manual, manualToo]);

  assert.equal(maxConcurrent, 1);
  // 在途的 1 次 + 两个立即同步各自在前一轮结束后串行跑一轮 = 3
  assert.equal(h.syncCalls.length, 3);
});

test("设备尚未就绪（无绑定门店）：跳过，不发请求", async () => {
  const h = new Harness();
  h.boundStoreCode = null;
  assert.deepEqual(await h.controller.requestSync("startup"), { status: "skipped", reason: "not-ready" });
  h.boundStoreCode = "  ";
  assert.deepEqual(await h.controller.syncNow(), { status: "skipped", reason: "not-ready" });
  assert.deepEqual(h.syncCalls, []);
});

test("dispose 后跳过后续触发，在途请求被中止且不再写入", async () => {
  const h = new Harness();
  let aborted = false;
  const controller = new ReceiptProfileSyncController({
    api: {
      sync: (_known, signal) =>
        new Promise((_resolve, reject) => {
          signal.addEventListener("abort", () => {
            aborted = true;
            reject(Object.assign(new Error("aborted"), { kind: "transport", code: "REQUEST_ABORTED" }));
          });
        }),
      ack: async () => undefined,
    },
    store: {
      read: async () => ({ profileVersion: 0, profileAckedVersion: 0 }),
      apply: async () => {
        throw new Error("不应写入");
      },
      markAcked: async () => undefined,
    },
    boundStoreCode: () => "S001",
    now: () => 0,
  });
  const running = controller.requestSync("timer");
  await Promise.resolve();
  await Promise.resolve();
  controller.dispose();
  assert.deepEqual(await running, { status: "skipped", reason: "disposed" });
  assert.equal(aborted, true);
  assert.deepEqual(await controller.requestSync("timer"), { status: "skipped", reason: "disposed" });
  assert.deepEqual(h.syncCalls, []);
});
