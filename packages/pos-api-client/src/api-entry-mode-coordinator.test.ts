import assert from "node:assert/strict";
import test from "node:test";

import {
  API_ENTRY_MODE_STORAGE_KEY,
  API_ENTRY_OVERRIDE_STORAGE_KEY,
  createApiEntryModeCoordinator,
  createAsyncStorageApiEntryModeStorage,
  type ApiEntryModeStoragePort,
  type AsyncKeyValueStorage,
} from "./api-entry-mode-coordinator";
import { ApiTransportSelector, type ApiEntryMode } from "./transport-failover";

/** 内存假存储：记录每次写入，可注入读 / 写失败与读延迟。 */
function createStorage(initial: string | null = null) {
  const state = {
    value: initial,
    writes: [] as (ApiEntryMode | null)[],
    failWrites: false,
    failReads: false,
    readGate: null as Promise<void> | null,
  };
  const port: ApiEntryModeStoragePort = {
    async read() {
      if (state.readGate) {
        await state.readGate;
      }
      if (state.failReads) {
        throw new Error("secure store unavailable");
      }
      return state.value;
    },
    async write(mode) {
      if (state.failWrites) {
        throw new Error("secure store full");
      }
      state.writes.push(mode);
      state.value = mode;
    },
  };
  return { state, port };
}

function setup(initial: string | null = null) {
  const storage = createStorage(initial);
  const selector = new ApiTransportSelector();
  const logs: string[] = [];
  const coordinator = createApiEntryModeCoordinator({
    storage: storage.port,
    selector,
    log: (message) => logs.push(message),
  });
  return { ...storage, selector, coordinator, logs };
}

test("合法取值：交给选择器并持久化；相同值不重复写入", async () => {
  const { coordinator, selector, state } = setup();
  assert.equal(coordinator.apply("aws"), "applied");
  assert.equal(selector.getPreferredMode(), "aws");
  assert.equal(coordinator.apply("aws"), "applied");
  await coordinator.flush();
  assert.deepEqual(state.writes, ["aws"]);

  assert.equal(coordinator.apply("direct"), "applied");
  await coordinator.flush();
  assert.equal(selector.getPreferredMode(), "direct");
  assert.deepEqual(state.writes, ["aws", "direct"]);
});

test("null / 缺失：按 edge，并持久化「未设置」（write(null)），不是显式 edge", async () => {
  const { coordinator, selector, state } = setup("aws");
  await coordinator.restore();
  assert.equal(selector.getPreferredMode(), "aws");

  assert.equal(coordinator.apply(null), "applied");
  assert.equal(selector.getPreferredMode(), "edge");
  await coordinator.flush();
  assert.deepEqual(state.writes, [null]);
  assert.equal(state.value, null);

  // 缺失字段（旧服务端）与 null 等价，且已是「未设置」不再重复写。
  assert.equal(coordinator.apply(undefined), "applied");
  await coordinator.flush();
  assert.deepEqual(state.writes, [null]);

  // 显式 edge 与「未设置」在本机记录里是两种状态。
  assert.equal(coordinator.apply("edge"), "applied");
  await coordinator.flush();
  assert.deepEqual(state.writes, [null, "edge"]);
});

test("未知取值：保持当前设置不变，也不写本机，不抛错", async () => {
  const { coordinator, selector, state, logs } = setup();
  coordinator.apply("direct");
  await coordinator.flush();

  for (const unknown of ["cloudflare", "", "EDGE", 3, {}, [], true]) {
    assert.equal(coordinator.apply(unknown), "ignored", `未知值 ${JSON.stringify(unknown)} 应被忽略`);
  }
  assert.equal(selector.getPreferredMode(), "direct");
  await coordinator.flush();
  assert.deepEqual(state.writes, ["direct"]);
  assert.ok(logs.length >= 1);
});

test("持久化失败不影响生效，也不抛错；下一次同步会重试", async () => {
  const { coordinator, selector, state, logs } = setup();
  state.failWrites = true;
  assert.equal(coordinator.apply("aws"), "applied");
  await coordinator.flush();
  assert.equal(selector.getPreferredMode(), "aws", "内存里的选择照样生效");
  assert.deepEqual(state.writes, []);
  assert.ok(logs.some((message) => message.includes("could not be saved")));

  state.failWrites = false;
  assert.equal(coordinator.apply("aws"), "applied");
  await coordinator.flush();
  assert.deepEqual(state.writes, ["aws"], "失败后同一个值会再写一次");
});

test("写入串行：快速连续变化时最后落盘的是最新值", async () => {
  const { coordinator, state } = setup();
  coordinator.apply("aws");
  coordinator.apply("direct");
  coordinator.apply(null);
  await coordinator.flush();
  assert.deepEqual(state.writes, ["aws", "direct", null]);
  assert.equal(state.value, null);
});

test("冷启动：读取本机记住的值喂给选择器；从未收到过则保持 edge", async () => {
  const stored = setup("direct");
  await stored.coordinator.restore();
  assert.equal(stored.selector.getPreferredMode(), "direct");
  assert.deepEqual(stored.state.writes, [], "恢复不应回写");

  const empty = setup(null);
  await empty.coordinator.restore();
  assert.equal(empty.selector.getPreferredMode(), "edge");
});

test("冷启动读到无法识别的值（版本回退 / 损坏）：忽略，保持 edge", async () => {
  for (const garbage of ["cloudflare", "", "Edge", "aws "]) {
    const { coordinator, selector } = setup(garbage);
    await coordinator.restore();
    assert.equal(selector.getPreferredMode(), "edge", `本机值 ${JSON.stringify(garbage)} 不应生效`);
  }
});

test("冷启动读取失败：静默，保持 edge，之后服务端值照常生效", async () => {
  const { coordinator, selector, state } = setup("aws");
  state.failReads = true;
  await coordinator.restore();
  assert.equal(selector.getPreferredMode(), "edge");
  assert.equal(coordinator.apply("direct"), "applied");
  assert.equal(selector.getPreferredMode(), "direct");
});

test("服务端值先到、本机读取后到：不得用旧的本机值覆盖服务端值", async () => {
  const { coordinator, selector, state } = setup("aws");
  let release: () => void = () => undefined;
  state.readGate = new Promise<void>((resolve) => {
    release = resolve;
  });
  const restoring = coordinator.restore();
  assert.equal(coordinator.apply("direct"), "applied");
  release();
  await restoring;
  assert.equal(selector.getPreferredMode(), "direct");
  await coordinator.flush();
  assert.equal(state.value, "direct");
});

test("恢复到的首选会被服务端值覆盖，且各入口冷却被清零", async () => {
  const { coordinator, selector } = setup("aws");
  await coordinator.restore();
  assert.equal(selector.getPreferredMode(), "aws");
  selector.reportFailure("pos-syd.hotbargain.vip", "network");
  assert.equal(selector.isUsingFallback(), true);

  coordinator.apply("edge");
  assert.equal(selector.getPreferredMode(), "edge");
  assert.equal(selector.isUsingFallback(), false);
});

/** AsyncStorage 形状的内存假实现，记录调用并可注入失败。 */
function createKeyValueStorage(initial: Record<string, string> = {}) {
  const values = new Map(Object.entries(initial));
  const calls: string[] = [];
  const state = { fail: false };
  const storage: AsyncKeyValueStorage = {
    async getItem(key) {
      calls.push(`get:${key}`);
      if (state.fail) throw new Error("storage unavailable");
      return values.get(key) ?? null;
    },
    async setItem(key, value) {
      calls.push(`set:${key}=${value}`);
      if (state.fail) throw new Error("storage unavailable");
      values.set(key, value);
    },
    async removeItem(key) {
      calls.push(`remove:${key}`);
      if (state.fail) throw new Error("storage unavailable");
      values.delete(key);
    },
  };
  return { storage, values, calls, state };
}

test("AsyncStorage 适配：键带版本后缀；写入只存入口代号；write(null) 删除该键", async () => {
  assert.equal(API_ENTRY_MODE_STORAGE_KEY, "hbpos.api-entry-mode.v1");
  const { storage, values, calls } = createKeyValueStorage();
  const port = createAsyncStorageApiEntryModeStorage(storage);
  await port.write("aws");
  assert.equal(values.get(API_ENTRY_MODE_STORAGE_KEY), "aws");
  assert.equal(await port.read(), "aws");
  await port.write(null);
  assert.equal(values.has(API_ENTRY_MODE_STORAGE_KEY), false);
  assert.equal(await port.read(), null);
  assert.deepEqual(calls, [
    "set:hbpos.api-entry-mode.v1=aws",
    "get:hbpos.api-entry-mode.v1",
    "remove:hbpos.api-entry-mode.v1",
    "get:hbpos.api-entry-mode.v1",
  ]);
});

test("端到端（AsyncStorage 形状）：冷启动恢复 → 服务端值覆盖并落盘 → 重启后恢复服务端值；null 删键", async () => {
  const kv = createKeyValueStorage({ [API_ENTRY_MODE_STORAGE_KEY]: "aws" });
  const selector = new ApiTransportSelector();
  const coordinator = createApiEntryModeCoordinator({
    selector,
    storage: createAsyncStorageApiEntryModeStorage(kv.storage),
  });
  await coordinator.restore();
  assert.equal(selector.getPreferredMode(), "aws");

  coordinator.apply("direct");
  await coordinator.flush();
  assert.equal(kv.values.get(API_ENTRY_MODE_STORAGE_KEY), "direct");

  const next = new ApiTransportSelector();
  await createApiEntryModeCoordinator({
    selector: next,
    storage: createAsyncStorageApiEntryModeStorage(kv.storage),
  }).restore();
  assert.equal(next.getPreferredMode(), "direct");

  coordinator.apply(null);
  await coordinator.flush();
  assert.equal(kv.values.has(API_ENTRY_MODE_STORAGE_KEY), false);
});

test("端到端（AsyncStorage 形状）：读写全部失败时静默，选择器照常按服务端值工作；未知值不写盘", async () => {
  const kv = createKeyValueStorage({ [API_ENTRY_MODE_STORAGE_KEY]: "aws" });
  const selector = new ApiTransportSelector();
  const coordinator = createApiEntryModeCoordinator({
    selector,
    storage: createAsyncStorageApiEntryModeStorage(kv.storage),
  });
  kv.state.fail = true;
  await coordinator.restore();
  assert.equal(selector.getPreferredMode(), "edge");
  assert.equal(coordinator.apply("direct"), "applied");
  await coordinator.flush();
  assert.equal(selector.getPreferredMode(), "direct");

  kv.state.fail = false;
  kv.calls.length = 0;
  assert.equal(coordinator.apply("nonsense"), "ignored");
  await coordinator.flush();
  assert.deepEqual(kv.calls, [], "未知值不触碰存储");
  assert.equal(selector.getPreferredMode(), "direct");
});

// ───────────────────────── 本机手动覆盖（设置页「收银入口」）─────────────────────────

/** 同一份 AsyncStorage 上同时挂后台值与手动覆盖两个键，模拟真机接线。 */
function setupWithOverride(initial: Record<string, string> = {}) {
  const kv = createKeyValueStorage(initial);
  const selector = new ApiTransportSelector();
  const logs: string[] = [];
  const coordinator = createApiEntryModeCoordinator({
    selector,
    storage: createAsyncStorageApiEntryModeStorage(kv.storage),
    overrideStorage: createAsyncStorageApiEntryModeStorage(kv.storage, API_ENTRY_OVERRIDE_STORAGE_KEY),
    log: (message) => logs.push(message),
  });
  return { ...kv, selector, coordinator, logs };
}

test("覆盖存在：后台下发值不得覆盖它，但仍被记下用于展示与落盘", async () => {
  const { coordinator, selector, values } = setupWithOverride();
  assert.equal(coordinator.setManualMode("direct"), "applied");
  assert.equal(selector.getPreferredMode(), "direct");

  assert.equal(coordinator.apply("aws"), "applied");
  assert.equal(selector.getPreferredMode(), "direct", "后台值不得顶掉本机手动选择");
  await coordinator.flush();
  const snapshot = coordinator.getSnapshot();
  assert.equal(snapshot.source, "manual");
  assert.equal(snapshot.manualMode, "direct");
  assert.equal(snapshot.backendMode, "aws", "后台指定值仍记下，用于显示「后台指定为 X」");
  assert.equal(values.get(API_ENTRY_MODE_STORAGE_KEY), "aws", "后台值照旧落盘");
  assert.equal(values.get(API_ENTRY_OVERRIDE_STORAGE_KEY), "direct", "覆盖单独落盘");
});

test("覆盖清除（选自动）：立即回到后台值；后台未指定则回到默认 edge；覆盖键被删除", async () => {
  const { coordinator, selector, values } = setupWithOverride();
  coordinator.apply("aws");
  coordinator.setManualMode("direct");
  assert.equal(selector.getPreferredMode(), "direct");

  assert.equal(coordinator.setManualMode(null), "applied");
  assert.equal(selector.getPreferredMode(), "aws", "清除覆盖后立刻跟随后台值");
  assert.equal(coordinator.getSnapshot().source, "backend");
  await coordinator.flush();
  assert.equal(values.has(API_ENTRY_OVERRIDE_STORAGE_KEY), false);
  assert.equal(values.get(API_ENTRY_MODE_STORAGE_KEY), "aws", "清除覆盖不动后台值的记录");

  // 后台未指定（null）→ 清除覆盖后是默认 edge。
  coordinator.apply(null);
  coordinator.setManualMode("direct");
  coordinator.setManualMode(null);
  assert.equal(selector.getPreferredMode(), "edge");
  assert.equal(coordinator.getSnapshot().source, "default");
  assert.equal(coordinator.getSnapshot().backendMode, null);
});

test("后台值反复变化（含 null / 未知值）：存在覆盖期间选择器始终保持覆盖，清除后用最新后台值", async () => {
  const { coordinator, selector } = setupWithOverride();
  coordinator.setManualMode("aws");
  for (const raw of ["edge", "direct", null, undefined, "bogus", "edge", "direct"]) {
    coordinator.apply(raw);
    assert.equal(selector.getPreferredMode(), "aws", `后台下发 ${JSON.stringify(raw)} 时手动选择不变`);
  }
  await coordinator.flush();
  assert.equal(coordinator.getSnapshot().backendMode, "direct", "未知值被忽略，不冲掉已记下的后台值");
  coordinator.setManualMode(null);
  assert.equal(selector.getPreferredMode(), "direct");
});

test("覆盖先于后台值：手动选择后才第一次收到后台值，选择器不被后台值改写", async () => {
  const { coordinator, selector } = setupWithOverride();
  coordinator.setManualMode("direct");
  coordinator.apply("edge");
  assert.equal(selector.getPreferredMode(), "direct");
});

test("冷启动恢复：本机手动覆盖优先于本机记着的后台值，并立即喂给选择器", async () => {
  const { coordinator, selector } = setupWithOverride({
    [API_ENTRY_MODE_STORAGE_KEY]: "aws",
    [API_ENTRY_OVERRIDE_STORAGE_KEY]: "direct",
  });
  await coordinator.restore();
  assert.equal(selector.getPreferredMode(), "direct");
  const snapshot = coordinator.getSnapshot();
  assert.equal(snapshot.manualMode, "direct");
  assert.equal(snapshot.backendMode, "aws");
  assert.equal(snapshot.source, "manual");

  // 重启后第一次后台同步也不能顶掉它；选「自动」后才回到后台值。
  coordinator.apply("edge");
  assert.equal(selector.getPreferredMode(), "direct");
  coordinator.setManualMode(null);
  assert.equal(selector.getPreferredMode(), "edge");
});

test("冷启动恢复：只有覆盖 / 只有后台值 / 都没有 / 覆盖键损坏", async () => {
  const onlyOverride = setupWithOverride({ [API_ENTRY_OVERRIDE_STORAGE_KEY]: "aws" });
  await onlyOverride.coordinator.restore();
  assert.equal(onlyOverride.selector.getPreferredMode(), "aws");

  const onlyBackend = setupWithOverride({ [API_ENTRY_MODE_STORAGE_KEY]: "direct" });
  await onlyBackend.coordinator.restore();
  assert.equal(onlyBackend.selector.getPreferredMode(), "direct");
  assert.equal(onlyBackend.coordinator.getSnapshot().manualMode, null);

  const none = setupWithOverride();
  await none.coordinator.restore();
  assert.equal(none.selector.getPreferredMode(), "edge");
  assert.equal(none.coordinator.getSnapshot().source, "default");

  for (const garbage of ["cloudflare", "", "AWS", "aws "]) {
    const broken = setupWithOverride({
      [API_ENTRY_OVERRIDE_STORAGE_KEY]: garbage,
      [API_ENTRY_MODE_STORAGE_KEY]: "direct",
    });
    await broken.coordinator.restore();
    assert.equal(broken.coordinator.getSnapshot().manualMode, null, `覆盖值 ${JSON.stringify(garbage)} 当作自动`);
    assert.equal(broken.selector.getPreferredMode(), "direct", "损坏的覆盖不影响后台值");
  }
});

test("冷启动恢复：恢复不回写任何存储；存储读取失败时静默，后台值照常", async () => {
  const quiet = setupWithOverride({ [API_ENTRY_OVERRIDE_STORAGE_KEY]: "aws" });
  await quiet.coordinator.restore();
  await quiet.coordinator.flush();
  assert.deepEqual(quiet.calls.filter((call) => !call.startsWith("get:")), []);

  const failing = setupWithOverride({ [API_ENTRY_OVERRIDE_STORAGE_KEY]: "aws" });
  failing.state.fail = true;
  await failing.coordinator.restore();
  assert.equal(failing.selector.getPreferredMode(), "edge");
  assert.ok(failing.logs.length >= 1);
  assert.equal(failing.coordinator.apply("direct"), "applied");
  assert.equal(failing.selector.getPreferredMode(), "direct");
});

test("覆盖读取在途时用户已手动选择：旧的本机覆盖不得盖掉新选择", async () => {
  const kv = createKeyValueStorage({ [API_ENTRY_OVERRIDE_STORAGE_KEY]: "aws" });
  let release: () => void = () => undefined;
  const gate = new Promise<void>((resolve) => {
    release = resolve;
  });
  const slow: AsyncKeyValueStorage = {
    ...kv.storage,
    getItem: async (key) => {
      if (key === API_ENTRY_OVERRIDE_STORAGE_KEY) await gate;
      return kv.storage.getItem(key);
    },
  };
  const selector = new ApiTransportSelector();
  const coordinator = createApiEntryModeCoordinator({
    selector,
    storage: createAsyncStorageApiEntryModeStorage(slow),
    overrideStorage: createAsyncStorageApiEntryModeStorage(slow, API_ENTRY_OVERRIDE_STORAGE_KEY),
  });
  const restoring = coordinator.restore();
  coordinator.setManualMode("direct");
  release();
  await restoring;
  await coordinator.flush();
  assert.equal(selector.getPreferredMode(), "direct");
  assert.equal(kv.values.get(API_ENTRY_OVERRIDE_STORAGE_KEY), "direct");
});

test("两个键互不污染：后台同步只写后台键，手动设置只写覆盖键", async () => {
  assert.equal(API_ENTRY_OVERRIDE_STORAGE_KEY, "hbpos.api-entry-override.v1");
  assert.notEqual(API_ENTRY_OVERRIDE_STORAGE_KEY, API_ENTRY_MODE_STORAGE_KEY);
  const { coordinator, calls } = setupWithOverride();
  coordinator.apply("aws");
  await coordinator.flush();
  assert.deepEqual(calls, [`set:${API_ENTRY_MODE_STORAGE_KEY}=aws`]);

  calls.length = 0;
  coordinator.setManualMode("direct");
  await coordinator.flush();
  assert.deepEqual(calls, [`set:${API_ENTRY_OVERRIDE_STORAGE_KEY}=direct`]);

  calls.length = 0;
  coordinator.setManualMode(null);
  await coordinator.flush();
  assert.deepEqual(calls, [`remove:${API_ENTRY_OVERRIDE_STORAGE_KEY}`]);
});

test("手动设置写盘失败：本次运行照样生效，标记保存失败；下次成功写入后清除标记", async () => {
  const { coordinator, selector, state } = setupWithOverride();
  state.fail = true;
  assert.equal(coordinator.setManualMode("aws"), "applied");
  await coordinator.flush();
  assert.equal(selector.getPreferredMode(), "aws");
  assert.equal(coordinator.getSnapshot().manualSaveFailed, true);

  state.fail = false;
  coordinator.setManualMode("direct");
  await coordinator.flush();
  assert.equal(coordinator.getSnapshot().manualSaveFailed, false);
});

test("非法的手动取值被忽略；没有覆盖存储端口时仍可在本次运行内手动指定", async () => {
  const { coordinator, selector } = setupWithOverride();
  coordinator.setManualMode("aws");
  for (const bad of ["EDGE", "", "cloudflare", 3, {}, undefined] as unknown[]) {
    assert.equal(coordinator.setManualMode(bad as never), "ignored");
  }
  assert.equal(selector.getPreferredMode(), "aws");
  assert.equal(coordinator.getSnapshot().manualMode, "aws");

  const memoryOnly = new ApiTransportSelector();
  const plain = createApiEntryModeCoordinator({ selector: memoryOnly, storage: createStorage().port });
  assert.equal(plain.setManualMode("direct"), "applied");
  assert.equal(memoryOnly.getPreferredMode(), "direct");
  await plain.flush();
});

test("快照：实际在用线路跟随故障转移；订阅者在后台值 / 覆盖变化时被通知并可取消", async () => {
  const selector = new ApiTransportSelector({ now: () => 0 });
  const coordinator = createApiEntryModeCoordinator({ selector, storage: createStorage().port });
  let notified = 0;
  const unsubscribe = coordinator.subscribe(() => {
    notified += 1;
  });

  coordinator.setManualMode("direct");
  assert.equal(notified, 1);
  coordinator.apply("aws");
  assert.equal(notified, 2);

  let snapshot = coordinator.getSnapshot();
  assert.equal(snapshot.preferredMode, "direct");
  assert.equal(snapshot.activeMode, "direct");
  assert.equal(snapshot.usingFallback, false);

  // 手动指定的直连不通：首选仍是直连，但实际在用的线路已落到备用（aws 优先）。
  selector.reportFailure("hotbargain.vip", "network");
  snapshot = coordinator.getSnapshot();
  assert.equal(snapshot.preferredMode, "direct");
  assert.equal(snapshot.activeMode, "aws");
  assert.equal(snapshot.usingFallback, true);

  unsubscribe();
  coordinator.setManualMode(null);
  assert.equal(notified, 2, "取消订阅后不再通知");
});

test("订阅者抛错不影响协调器", async () => {
  const { coordinator, selector } = setupWithOverride();
  coordinator.subscribe(() => {
    throw new Error("ui crashed");
  });
  assert.equal(coordinator.setManualMode("aws"), "applied");
  assert.equal(coordinator.apply("direct"), "applied");
  assert.equal(selector.getPreferredMode(), "aws");
});
