import assert from "node:assert/strict";
import test from "node:test";

import {
  OtaUpdateCoordinator,
  shouldCheckOtaPolicy,
} from "./ota-update-coordinator";

import {
  POS_IPAD_OTA_NONE_POLICY,
  type PosIpadOtaUpdatePolicy,
  type PosIpadOtaUpdatePolicyStorePort,
} from "@/core/contracts/ota-app-updates";

const metadata = Object.freeze({
  runtimeVersion: "1.2.3",
  currentUpdateId: null,
  currentUpdateGroupId: null,
});

const policy: PosIpadOtaUpdatePolicy = Object.freeze({
  state: "optional",
  policyVersion: "policy-42",
  channel: "store-s001",
  runtimeVersion: "1.2.3",
  iosUpdateId: "123e4567-e89b-42d3-a456-426614174000",
  updateGroupId: "223e4567-e89b-42d3-a456-426614174000",
  releaseMessage: null,
});

class MemoryStore implements PosIpadOtaUpdatePolicyStorePort {
  public saves = 0;

  public constructor(private value: PosIpadOtaUpdatePolicy | null = null) {}

  public async get(): Promise<PosIpadOtaUpdatePolicy | null> {
    return this.value;
  }

  public async save(
    next: PosIpadOtaUpdatePolicy,
  ): Promise<PosIpadOtaUpdatePolicy> {
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

test("安卓平板不查 iPad OTA 策略，iPad 保持原行为", () => {
  assert.equal(
    shouldCheckOtaPolicy({
      automaticChecksConfigured: true,
      updatesEnabled: true,
      deviceSystem: "AndroidTablet",
    }),
    false,
  );
  assert.equal(
    shouldCheckOtaPolicy({
      automaticChecksConfigured: true,
      updatesEnabled: true,
      deviceSystem: "iPadOS",
    }),
    true,
  );
});

test("OTA 启动/前台/联网刷新 single-flight，远端与缓存状态独立保存", async () => {
  let calls = 0;
  let release!: () => void;
  const pending = new Promise<void>((resolve) => {
    release = resolve;
  });
  const store = new MemoryStore();
  const coordinator = new OtaUpdateCoordinator({
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
  const observed: (PosIpadOtaUpdatePolicy | null)[] = [];
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
    automaticChecksEnabled: true,
    metadata,
    policyStore: new MemoryStore(POS_IPAD_OTA_NONE_POLICY),
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
    automaticChecksEnabled: true,
    metadata,
    policyStore: new MemoryStore(POS_IPAD_OTA_NONE_POLICY),
    remote: { async getPolicy() { throw new Error("offline"); } },
  });
  assert.equal((await cached.refreshOnStartup()).source, "cache");
  assert.deepEqual(cached.getPolicy(), POS_IPAD_OTA_NONE_POLICY);
});

test("development/test mode 使用显式 none，不触网、不读取生产缓存", async () => {
  let remoteCalls = 0;
  let cacheReads = 0;
  const coordinator = new OtaUpdateCoordinator({
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

  assert.deepEqual(coordinator.getPolicy(), POS_IPAD_OTA_NONE_POLICY);
  assert.equal((await coordinator.refreshOnStartup()).source, "disabled");
  assert.equal(remoteCalls, 0);
  assert.equal(cacheReads, 0);
});
