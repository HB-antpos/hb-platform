import assert from "node:assert/strict";
import test from "node:test";

import {
  AppUpdateCoordinator,
  decideAppUpdateRestart,
} from "./app-update-coordinator";

import {
  normalizePosHandheldUpdatePolicy,
  type PosHandheldUpdatePolicy,
  type PosHandheldUpdatePolicyStorePort,
} from "@/core/contracts/app-updates";

const metadata = Object.freeze({
  version: "1.2.3",
  build: "42",
});

const enabledPolicy: PosHandheldUpdatePolicy = Object.freeze({
  enabled: true,
  state: "optional",
  policyVersion: "ios-optional-130",
  platform: "iOS",
  required: false,
  latestVersion: "1.3.0",
  latestBuild: "130",
  minimumSupportedVersion: "1.2.0",
  distribution: "app-store",
  downloadUrl: "https://apps.apple.com/au/app/hb-pos/id123456789",
  fileSize: null,
  sha256: null,
  packageName: null,
  signingCertificateSha256: null,
  bundleIdentifier: "com.hbweb.poshandheld",
  appStoreId: "123456789",
  releaseMessage: null,
});

class MemoryPolicyStore implements PosHandheldUpdatePolicyStorePort {
  public saves = 0;

  public constructor(private value: PosHandheldUpdatePolicy | null = null) {}

  public async get(): Promise<PosHandheldUpdatePolicy | null> {
    return this.value;
  }

  public async save(policy: PosHandheldUpdatePolicy): Promise<PosHandheldUpdatePolicy> {
    this.saves += 1;
    this.value = policy;
    return policy;
  }
}

test("开发包忽略远端和缓存升级，但仍保留设备交易权限", async () => {
  for (const enabled of [true, false]) {
    for (const offline of [true, false]) {
      const policy: PosHandheldUpdatePolicy = {
        ...enabledPolicy, enabled, state: "required", required: true,
      };
      const store = new MemoryPolicyStore(policy);
      const coordinator = new AppUpdateCoordinator({
        updatesEnabled: false,
        metadata,
        policyStore: store,
        remote: { async getPolicy() {
          if (offline) throw new Error("offline");
          return policy;
        } },
      });
      for (const refresh of ["refreshOnStartup", "refreshOnForeground", "refreshOnNetworkAvailable"] as const) {
        await coordinator[refresh]();
        const selected = coordinator.getPolicy();
        assert.equal(selected?.state, "none");
        assert.equal(selected?.downloadUrl, null);
        assert.equal(selected?.required, false);
        assert.deepEqual(normalizePosHandheldUpdatePolicy(selected), selected);
        assert.equal(coordinator.getGate().canStartNewTransaction, enabled);
        assert.deepEqual(await store.get(), policy);
      }
    }
  }
});

test("启动、前台和联网刷新共享 single-flight，并向订阅者发布可开始交易的门禁", async () => {
  let calls = 0;
  let release!: () => void;
  const pending = new Promise<void>((resolve) => {
    release = resolve;
  });
  const coordinator = new AppUpdateCoordinator({
    metadata,
    policyStore: new MemoryPolicyStore(),
    remote: {
      async getPolicy(actualMetadata) {
        calls += 1;
        assert.equal(actualMetadata, metadata);
        await pending;
        return enabledPolicy;
      },
    },
  });
  const observed: string[] = [];
  const unsubscribe = coordinator.subscribe((gate) => observed.push(gate.state));

  const startup = coordinator.refreshOnStartup();
  const foreground = coordinator.refreshOnForeground();
  const network = coordinator.refreshOnNetworkAvailable();
  assert.equal(startup, foreground);
  assert.equal(startup, network);
  assert.equal(calls, 1);
  assert.deepEqual(coordinator.getGate(), {
    state: "unchecked",
    canStartNewTransaction: true,
    canContinueRecovery: true,
  });
  release();
  await startup;

  assert.deepEqual(observed, ["unchecked", "enabled"]);
  assert.deepEqual(coordinator.getGate(), {
    state: "enabled",
    canStartNewTransaction: true,
    canContinueRecovery: true,
  });
  unsubscribe();
});

test("首次策略检查完成前默认允许，enabled false 在验证后阻止新交易", async () => {
  const coordinator = new AppUpdateCoordinator({
    metadata,
    policyStore: new MemoryPolicyStore(),
    remote: {
      async getPolicy() {
        return { ...enabledPolicy, enabled: false };
      },
    },
  });

  assert.deepEqual(coordinator.getGate(), {
    state: "unchecked",
    canStartNewTransaction: true,
    canContinueRecovery: true,
  });
  await coordinator.refreshOnStartup();
  assert.deepEqual(coordinator.getGate(), {
    state: "disabled",
    canStartNewTransaction: false,
    canContinueRecovery: true,
  });
});

test("网络刷新失败回退合法缓存；无有效缓存时保持未检查门禁", async () => {
  const cachedForcePolicy: PosHandheldUpdatePolicy = Object.freeze({
    ...enabledPolicy,
    state: "required",
    policyVersion: "ios-required-130",
    required: true,
  });
  const fromCache = new AppUpdateCoordinator({
    metadata,
    policyStore: new MemoryPolicyStore(cachedForcePolicy),
    remote: { async getPolicy() { throw new Error("offline"); } },
  });
  await fromCache.refreshOnNetworkAvailable();
  assert.deepEqual(fromCache.getGate(), {
    state: "force-update",
    canStartNewTransaction: false,
    canContinueRecovery: true,
  });

  const withoutCache = new AppUpdateCoordinator({
    metadata,
    policyStore: new MemoryPolicyStore(),
    remote: { async getPolicy() { throw new Error("offline"); } },
  });
  await withoutCache.refreshOnStartup();
  assert.deepEqual(withoutCache.getGate(), {
    state: "unchecked",
    canStartNewTransaction: true,
    canContinueRecovery: true,
  });

  const malformedCache = new AppUpdateCoordinator({
    metadata,
    policyStore: {
      async get() {
        return {
          enabled: true,
          forceUpdate: false,
          accessToken: "forbidden",
        } as unknown as PosHandheldUpdatePolicy;
      },
      async save(value) {
        return value;
      },
    },
    remote: { async getPolicy() { throw new Error("offline"); } },
  });
  await malformedCache.refreshOnStartup();
  assert.equal(malformedCache.getGate().state, "unchecked");
});

test("已验证的内存更新策略优先于旧缓存，交易开关和强制升级均继续限制新交易", async () => {
  for (const [remotePolicy, expectedState, canStartNewTransaction] of [
    [{ ...enabledPolicy, enabled: false }, "disabled", false],
    [
      {
        ...enabledPolicy,
        state: "required" as const,
        policyVersion: "ios-required-130",
        required: true,
      },
      "force-update",
      false,
    ],
  ] as const) {
    let remoteCalls = 0;
    let cacheReads = 0;
    let cacheWrites = 0;
    const coordinator = new AppUpdateCoordinator({
      metadata,
      policyStore: {
        async get() {
          cacheReads += 1;
          return enabledPolicy;
        },
        async save() {
          cacheWrites += 1;
          throw new Error("disk full");
        },
      },
      remote: {
        async getPolicy() {
          remoteCalls += 1;
          if (remoteCalls === 1) return remotePolicy;
          throw new Error("offline");
        },
      },
    });

    assert.equal((await coordinator.refreshOnStartup()).source, "remote");
    assert.equal(coordinator.getGate().state, expectedState);
    assert.equal((await coordinator.refreshOnForeground()).source, "memory");
    assert.equal(coordinator.getGate().state, expectedState);
    assert.equal(
      coordinator.getGate().canStartNewTransaction,
      canStartNewTransaction,
    );
    assert.equal(coordinator.getGate().canContinueRecovery, true);
    assert.equal(cacheReads, 0);
    assert.equal(cacheWrites, 1);
  }
});

test("enabled false 和 required 都阻止新交易且恢复永远开放", async () => {
  for (const [policy, canStartNewTransaction] of [
    [{ ...enabledPolicy, enabled: false }, false],
    [
      {
        ...enabledPolicy,
        state: "required" as const,
        policyVersion: "ios-required-130",
        required: true,
      },
      false,
    ],
  ] as const) {
    const coordinator = new AppUpdateCoordinator({
      metadata,
      policyStore: new MemoryPolicyStore(),
      remote: { async getPolicy() { return policy; } },
    });
    await coordinator.refreshOnStartup();
    assert.equal(
      coordinator.getGate().canStartNewTransaction,
      canStartNewTransaction,
    );
    assert.equal(coordinator.getGate().canContinueRecovery, true);
  }
});

test("重启决策由注入的风险快照控制，活动购物车、未决支付或耐久写入都不得重启", async () => {
  assert.deepEqual(
    decideAppUpdateRestart({
      hasActiveCart: true,
      hasUnresolvedPayment: false,
      hasPendingDurableWrite: false,
      hasRecoveryRequired: false,
      hasCatalogRefreshInFlight: false,
      hasSyncOrAuditInFlight: false,
      hasFulfilmentInFlight: false,
    }),
    { canRestart: false, reason: "active-cart" },
  );
  assert.deepEqual(
    decideAppUpdateRestart({
      hasActiveCart: false,
      hasUnresolvedPayment: true,
      hasPendingDurableWrite: false,
      hasRecoveryRequired: false,
      hasCatalogRefreshInFlight: false,
      hasSyncOrAuditInFlight: false,
      hasFulfilmentInFlight: false,
    }),
    { canRestart: false, reason: "unresolved-payment" },
  );
  assert.deepEqual(
    decideAppUpdateRestart({
      hasActiveCart: false,
      hasUnresolvedPayment: false,
      hasPendingDurableWrite: true,
      hasRecoveryRequired: false,
      hasCatalogRefreshInFlight: false,
      hasSyncOrAuditInFlight: false,
      hasFulfilmentInFlight: false,
    }),
    { canRestart: false, reason: "pending-durable-write" },
  );

  let restarts = 0;
  const coordinator = new AppUpdateCoordinator({
    metadata,
    policyStore: new MemoryPolicyStore(),
    remote: { async getPolicy() { return enabledPolicy; } },
    restart: {
      getSafetySnapshot() {
        return {
          hasActiveCart: false,
          hasUnresolvedPayment: false,
          hasPendingDurableWrite: false,
          hasRecoveryRequired: false,
          hasCatalogRefreshInFlight: false,
          hasSyncOrAuditInFlight: false,
          hasFulfilmentInFlight: false,
        };
      },
      async restart() {
        restarts += 1;
      },
    },
  });

  assert.deepEqual(await coordinator.restartIfSafe(), {
    canRestart: true,
    reason: null,
  });
  assert.equal(restarts, 1);
});

test("恢复、同步审计或外设动作仍在进行时不得进入完整更新门禁", () => {
  const safe = {
    hasActiveCart: false,
    hasUnresolvedPayment: false,
    hasPendingDurableWrite: false,
    hasRecoveryRequired: false,
    hasCatalogRefreshInFlight: false,
    hasSyncOrAuditInFlight: false,
    hasFulfilmentInFlight: false,
  };
  for (const [field, reason] of [
    ["hasRecoveryRequired", "recovery-required"],
    ["hasCatalogRefreshInFlight", "catalog-refresh-in-flight"],
    ["hasSyncOrAuditInFlight", "sync-audit-in-flight"],
    ["hasFulfilmentInFlight", "fulfilment-in-flight"],
  ] as const) {
    assert.deepEqual(
      decideAppUpdateRestart({
        ...safe,
        [field]: true,
      } as never),
      { canRestart: false, reason },
    );
  }
  assert.deepEqual(
    decideAppUpdateRestart({
      hasActiveCart: false,
      hasUnresolvedPayment: false,
      hasPendingDurableWrite: false,
      hasRecoveryRequired: false,
      hasSyncOrAuditInFlight: false,
      hasFulfilmentInFlight: false,
    } as never),
    { canRestart: false, reason: "invalid-safety-snapshot" },
  );
});

test("并发安全重启共享 single-flight，只执行一次 snapshot 与 restart", async () => {
  let safetyReads = 0;
  let restarts = 0;
  let release!: () => void;
  const pendingRestart = new Promise<void>((resolve) => {
    release = resolve;
  });
  const coordinator = new AppUpdateCoordinator({
    metadata,
    policyStore: new MemoryPolicyStore(),
    remote: { async getPolicy() { return enabledPolicy; } },
    restart: {
      getSafetySnapshot() {
        safetyReads += 1;
        return {
          hasActiveCart: false,
          hasUnresolvedPayment: false,
          hasPendingDurableWrite: false,
          hasRecoveryRequired: false,
          hasCatalogRefreshInFlight: false,
          hasSyncOrAuditInFlight: false,
          hasFulfilmentInFlight: false,
        };
      },
      async restart() {
        restarts += 1;
        await pendingRestart;
      },
    },
  });

  const first = coordinator.restartIfSafe();
  const second = coordinator.restartIfSafe();
  const sharedPromise = first === second;
  release();
  await Promise.all([first, second]);

  assert.equal(sharedPromise, true);
  assert.equal(safetyReads, 1);
  assert.equal(restarts, 1);
});

test("并发 restart 拒绝只执行一次，清理 single-flight 后允许显式重试", async () => {
  let restarts = 0;
  const coordinator = new AppUpdateCoordinator({
    metadata,
    policyStore: new MemoryPolicyStore(),
    remote: { async getPolicy() { return enabledPolicy; } },
    restart: {
      getSafetySnapshot() {
        return {
          hasActiveCart: false,
          hasUnresolvedPayment: false,
          hasPendingDurableWrite: false,
          hasRecoveryRequired: false,
          hasCatalogRefreshInFlight: false,
          hasSyncOrAuditInFlight: false,
          hasFulfilmentInFlight: false,
        };
      },
      async restart() {
        restarts += 1;
        if (restarts === 1) throw new Error("restart rejected");
      },
    },
  });

  const first = coordinator.restartIfSafe();
  const second = coordinator.restartIfSafe();
  const sharedPromise = first === second;
  const settled = await Promise.allSettled([first, second]);

  assert.equal(sharedPromise, true);
  assert.deepEqual(settled.map((item) => item.status), ["rejected", "rejected"]);
  assert.equal(restarts, 1);
  assert.deepEqual(await coordinator.restartIfSafe(), {
    canRestart: true,
    reason: null,
  });
  assert.equal(restarts, 2);
});
