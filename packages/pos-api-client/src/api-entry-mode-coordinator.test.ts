import assert from "node:assert/strict";
import test from "node:test";

import {
  API_ENTRY_MODE_STORAGE_KEY,
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
