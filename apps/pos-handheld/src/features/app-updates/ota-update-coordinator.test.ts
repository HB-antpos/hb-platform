import assert from "node:assert/strict";
import test from "node:test";

import {
  OtaUpdateCoordinator,
  shouldCheckOtaPolicy,
} from "./ota-update-coordinator";

import {
  POS_HANDHELD_OTA_NONE_POLICY,
  type PosHandheldOtaUpdatePolicy,
  type PosHandheldOtaUpdatePolicyStorePort,
} from "@/core/contracts/ota-app-updates";

const metadata = Object.freeze({
  runtimeVersion: "1.2.3",
  currentUpdateId: null,
  currentUpdateGroupId: null,
});

const policy: PosHandheldOtaUpdatePolicy = Object.freeze({
  state: "optional",
  policyVersion: "policy-42",
  appKey: "pos-handheld",
  projectName: "hb-pos-handheld",
  platform: "iOS",
  required: false,
  channel: "store-s001",
  runtimeVersion: "1.2.3",
  updateId: "ios-update-42",
  updateGroupId: "223e4567-e89b-42d3-a456-426614174000",
  releaseMessage: null,
});

class MemoryStore implements PosHandheldOtaUpdatePolicyStorePort {
  public saves = 0;

  public constructor(private value: PosHandheldOtaUpdatePolicy | null = null) {}

  public async get(): Promise<PosHandheldOtaUpdatePolicy | null> {
    return this.value;
  }

  public async save(
    next: PosHandheldOtaUpdatePolicy,
  ): Promise<PosHandheldOtaUpdatePolicy> {
    this.saves += 1;
    this.value = next;
    return next;
  }
}

test("生产策略检查不因 expo-updates 执行器失效而被绕过", () => {
  assert.equal(
    shouldCheckOtaPolicy({
      automaticChecksConfigured: true,
      updatesEnabled: false,
    }),
    true,
  );
  assert.equal(
    shouldCheckOtaPolicy({
      automaticChecksConfigured: false,
      updatesEnabled: true,
    }),
    false,
  );
});

test("Android OTA 使用同一严格合同，可刷新并交给 Expo installer", async () => {
  const androidPolicy: PosHandheldOtaUpdatePolicy = Object.freeze({
    ...policy,
    platform: "Android",
    updateId: "android-update-42",
  });
  let remoteCalls = 0;
  let cacheReads = 0;
  let installs = 0;
  const coordinator = new OtaUpdateCoordinator({
    platform: "Android",
    automaticChecksEnabled: true,
    metadata,
    policyStore: {
      async get() {
        cacheReads += 1;
        return androidPolicy;
      },
      async save(next) {
        return next;
      },
    },
    remote: {
      async getPolicy() {
        remoteCalls += 1;
        return androidPolicy;
      },
    },
    installer: {
      async apply() {
        installs += 1;
        return { state: "reloaded", reason: null };
      },
    },
  });

  assert.equal(coordinator.getPolicy(), null);
  assert.deepEqual(await coordinator.refreshOnStartup(), {
    reason: "startup",
    source: "remote",
    policy: androidPolicy,
  });
  assert.deepEqual(await coordinator.apply(androidPolicy, () => true), {
    state: "reloaded",
    reason: null,
  });
  assert.equal(remoteCalls, 1);
  assert.equal(cacheReads, 0);
  assert.equal(installs, 1);
});

test("prepare 委托给 OTA installer；旧 mock 缺少 prepare 时安全返回 unavailable", async () => {
  let prepares = 0;
  const androidPolicy: PosHandheldOtaUpdatePolicy = Object.freeze({
    ...policy,
    platform: "Android",
    updateId: "android-prepare-42",
  });
  const coordinator = new OtaUpdateCoordinator({
    platform: "Android",
    automaticChecksEnabled: true,
    metadata,
    policyStore: new MemoryStore(),
    remote: { async getPolicy() { return androidPolicy; } },
    installer: {
      async prepare(actual) {
        prepares += 1;
        assert.deepEqual(actual, androidPolicy);
        return { state: "ready", reason: null };
      },
      async apply() { return { state: "reloaded", reason: null }; },
    },
  });

  assert.deepEqual(await coordinator.prepare(androidPolicy), {
    state: "ready",
    reason: null,
  });
  assert.deepEqual(await coordinator.prepare(androidPolicy), {
    state: "ready",
    reason: null,
  });
  assert.equal(prepares, 2);

  const legacy = new OtaUpdateCoordinator({
    platform: "Android",
    automaticChecksEnabled: true,
    metadata,
    policyStore: new MemoryStore(),
    remote: { async getPolicy() { return androidPolicy; } },
    installer: { async apply() { return { state: "reloaded", reason: null }; } },
  });
  assert.deepEqual(await legacy.prepare(androidPolicy), {
    state: "unavailable",
    reason: "updates-disabled",
  });
});

test("不同 OTA 目标并发 prepare 不共享第一个目标的结果", async () => {
  const androidPolicy: PosHandheldOtaUpdatePolicy = Object.freeze({
    ...policy,
    platform: "Android",
    channel: "android-a",
    updateId: "android-a",
  });
  const secondPolicy = Object.freeze({
    ...androidPolicy,
    channel: "android-b",
    updateId: "android-b",
  });
  let calls = 0;
  let release!: () => void;
  const pending = new Promise<void>((resolve) => { release = resolve; });
  const coordinator = new OtaUpdateCoordinator({
    platform: "Android",
    automaticChecksEnabled: true,
    metadata,
    policyStore: new MemoryStore(),
    remote: { async getPolicy() { return androidPolicy; } },
    installer: {
      async prepare(actual) {
        calls += 1;
        await pending;
        return { state: "ready", reason: null };
      },
      async apply() { return { state: "reloaded", reason: null }; },
    },
  });

  const first = coordinator.prepare(androidPolicy);
  const second = coordinator.prepare(secondPolicy);
  assert.notEqual(first, second);
  assert.equal(calls, 2);
  release();
  assert.deepEqual(await Promise.all([first, second]), [
    { state: "ready", reason: null },
    { state: "ready", reason: null },
  ]);
});

test("OTA 启动/前台/联网刷新 single-flight，远端与缓存状态独立保存", async () => {
  let calls = 0;
  let release!: () => void;
  const pending = new Promise<void>((resolve) => {
    release = resolve;
  });
  const store = new MemoryStore();
  const coordinator = new OtaUpdateCoordinator({
    platform: "iOS",
    automaticChecksEnabled: true,
    metadata,
    policyStore: store,
    remote: {
      async getPolicy(actual) {
        calls += 1;
        assert.equal(actual, metadata);
        await pending;
        return policy;
      },
    },
  });
  const observed: (PosHandheldOtaUpdatePolicy | null)[] = [];
  coordinator.subscribe((next) => observed.push(next));

  const startup = coordinator.refreshOnStartup();
  const foreground = coordinator.refreshOnForeground();
  const network = coordinator.refreshOnNetworkAvailable();
  assert.equal(startup, foreground);
  assert.equal(startup, network);
  assert.equal(calls, 1);
  release();
  assert.equal((await startup).source, "remote");
  assert.equal(store.saves, 1);
  assert.deepEqual(observed, [null, policy]);
});

test("OTA 刷新失败保留内存，否则只回退同 scope 的合法缓存", async () => {
  let remoteCalls = 0;
  const coordinator = new OtaUpdateCoordinator({
    platform: "iOS",
    automaticChecksEnabled: true,
    metadata,
    policyStore: new MemoryStore(POS_HANDHELD_OTA_NONE_POLICY),
    remote: {
      async getPolicy() {
        remoteCalls += 1;
        if (remoteCalls === 1) return policy;
        throw new Error("offline");
      },
    },
  });
  assert.equal((await coordinator.refreshOnStartup()).source, "remote");
  assert.equal((await coordinator.refreshOnForeground()).source, "memory");
  assert.deepEqual(coordinator.getPolicy(), policy);

  const cached = new OtaUpdateCoordinator({
    platform: "iOS",
    automaticChecksEnabled: true,
    metadata,
    policyStore: new MemoryStore(POS_HANDHELD_OTA_NONE_POLICY),
    remote: { async getPolicy() { throw new Error("offline"); } },
  });
  assert.equal((await cached.refreshOnStartup()).source, "cache");
  assert.deepEqual(cached.getPolicy(), POS_HANDHELD_OTA_NONE_POLICY);
});

test("development/test mode 使用显式 none，不触网、不读取生产缓存", async () => {
  let remoteCalls = 0;
  let cacheReads = 0;
  const coordinator = new OtaUpdateCoordinator({
    platform: "iOS",
    automaticChecksEnabled: false,
    metadata,
    policyStore: {
      async get() {
        cacheReads += 1;
        return policy;
      },
      async save(next) {
        return next;
      },
    },
    remote: {
      async getPolicy() {
        remoteCalls += 1;
        return policy;
      },
    },
  });

  assert.deepEqual(coordinator.getPolicy(), POS_HANDHELD_OTA_NONE_POLICY);
  assert.equal((await coordinator.refreshOnStartup()).source, "disabled");
  assert.equal(remoteCalls, 0);
  assert.equal(cacheReads, 0);
});
