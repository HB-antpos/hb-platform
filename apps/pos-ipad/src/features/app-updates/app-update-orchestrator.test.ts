import assert from "node:assert/strict";
import test from "node:test";

import {
  AppUpdateOrchestrator,
  chooseAppUpdatePresentation,
} from "./app-update-orchestrator";
import {
  UPDATE_TRANSITION_IN_PROGRESS,
  UpdateTransitionLeaseCoordinator,
} from "./update-transition-lease-coordinator";

import {
  deriveNewTransactionGate,
  type NewTransactionGate,
  type PosIpadUpdatePolicy,
} from "@/core/contracts/app-updates";
import type { PosIpadOtaUpdatePolicy } from "@/core/contracts/ota-app-updates";

const nativeEnabled: PosIpadUpdatePolicy = Object.freeze({
  enabled: true,
  minimumSupportedVersion: "1.0.0",
  latestVersion: "1.0.0",
  forceUpdate: false,
  appStoreUrl: "https://apps.apple.com/au/app/hb-pos/id123456789",
  releaseMessage: null,
});
const nativeOptional = Object.freeze({
  ...nativeEnabled,
  latestVersion: "1.1.0",
});
const nativeRequired = Object.freeze({
  ...nativeOptional,
  forceUpdate: true,
});
const nativeDisabled = Object.freeze({
  ...nativeEnabled,
  enabled: false,
});
const otaOptional: PosIpadOtaUpdatePolicy = Object.freeze({
  state: "optional",
  policyVersion: "policy-optional",
  channel: "store-s001",
  runtimeVersion: "1.0.0",
  iosUpdateId: "123e4567-e89b-42d3-a456-426614174000",
  updateGroupId: "223e4567-e89b-42d3-a456-426614174000",
  releaseMessage: null,
});
const otaRequired = Object.freeze({
  ...otaOptional,
  state: "required" as const,
  policyVersion: "policy-required",
});

test("统一展示严格遵循 native required > OTA required > native optional > OTA optional", () => {
  assert.equal(
    chooseAppUpdatePresentation(nativeRequired, otaRequired, "1.0.0").kind,
    "native",
  );
  assert.equal(
    chooseAppUpdatePresentation(nativeOptional, otaRequired, "1.0.0").kind,
    "ota",
  );
  assert.equal(
    chooseAppUpdatePresentation(nativeOptional, otaOptional, "1.0.0").kind,
    "native",
  );
  assert.equal(
    chooseAppUpdatePresentation(nativeEnabled, otaOptional, "1.0.0").kind,
    "ota",
  );
});

test("required 在交易未安全时只阻止新交易，安全后升级为全屏 blocking gate", async () => {
  let safe = false;
  const native = new FakeNative(nativeEnabled);
  const ota = new FakeOta(otaRequired);
  const orchestrator = new AppUpdateOrchestrator({
    installedVersion: "1.0.0",
    native,
    ota,
    ...transitionDependencies(),
    safety: {
      getSafetySnapshot() {
        return {
          hasActiveCart: !safe,
          hasUnresolvedPayment: false,
          hasPendingDurableWrite: false,
          hasRecoveryRequired: false,
          hasCatalogRefreshInFlight: false,
          hasSyncOrAuditInFlight: false,
          hasFulfilmentInFlight: false,
        };
      },
    },
  });

  await orchestrator.refreshSafety();
  assert.deepEqual(orchestrator.getGate(), {
    state: "ota-update",
    canStartNewTransaction: false,
    canContinueRecovery: true,
  });
  assert.equal(orchestrator.getPresentation().phase, "waiting-for-safe");
  assert.equal(orchestrator.getPresentation().blocking, false);

  safe = true;
  await orchestrator.refreshSafety();
  assert.equal(orchestrator.getPresentation().phase, "blocking");
  assert.equal(orchestrator.getPresentation().blocking, true);
});

test("两种策略分别刷新；optional 主动提示但绝不阻止业务页面", async () => {
  const native = new FakeNative(nativeOptional);
  const ota = new FakeOta(otaOptional);
  const orchestrator = new AppUpdateOrchestrator({
    installedVersion: "1.0.0",
    native,
    ota,
    ...transitionDependencies(),
    safety: {
      getSafetySnapshot() {
        throw new Error("optional must not read transaction safety");
      },
    },
  });
  const observed: string[] = [];
  orchestrator.subscribePresentation((value) => observed.push(value.key));

  await orchestrator.refreshOnStartup();
  assert.equal(native.refreshes, 1);
  assert.equal(ota.refreshes, 1);
  assert.equal(orchestrator.getPresentation().phase, "prompt");
  assert.equal(orchestrator.getPresentation().kind, "native");
  assert.equal(orchestrator.getGate().canStartNewTransaction, true);
  assert.ok(observed.length >= 1);
});

test("设备交易开关关闭时即使 OTA 策略缺失也必须阻止新交易", () => {
  const orchestrator = new AppUpdateOrchestrator({
    installedVersion: "1.0.0",
    native: new FakeNative(nativeDisabled),
    ota: new FakeOta(null),
    ...transitionDependencies(),
    safety: {
      getSafetySnapshot: () => safeSnapshot(),
    },
  });

  assert.deepEqual(orchestrator.getGate(), {
    state: "disabled",
    canStartNewTransaction: false,
    canContinueRecovery: true,
  });
});

test("交易门禁快照在语义未变时保持身份，并与订阅者共享同一对象", () => {
  const native = new FakeNative(nativeEnabled);
  const ota = new FakeOta(otaOptional);
  const orchestrator = new AppUpdateOrchestrator({
    installedVersion: "1.0.0",
    native,
    ota,
    ...transitionDependencies(),
    safety: {
      getSafetySnapshot: () => safeSnapshot(),
    },
  });
  const observed: NewTransactionGate[] = [];
  const first = orchestrator.getGate();
  const unsubscribe = orchestrator.subscribe((gate) => observed.push(gate));

  assert.strictEqual(orchestrator.getGate(), first);
  assert.strictEqual(observed.at(-1), first);

  native.setPolicy(Object.freeze({ ...nativeEnabled }));
  assert.strictEqual(orchestrator.getGate(), first);
  assert.strictEqual(observed.at(-1), first);

  native.setPolicy(null);
  const changed = orchestrator.getGate();
  assert.notStrictEqual(changed, first);
  assert.strictEqual(observed.at(-1), changed);
  unsubscribe();
});

test("四种门禁语义的重复 getGate 均复用同一快照", () => {
  const cases = [
    { native: null, ota: null, state: "unchecked" },
    { native: nativeRequired, ota: otaOptional, state: "force-update" },
    { native: nativeEnabled, ota: otaRequired, state: "ota-update" },
    { native: nativeDisabled, ota: null, state: "disabled" },
    { native: nativeEnabled, ota: otaOptional, state: "enabled" },
  ] as const;

  for (const scenario of cases) {
    const orchestrator = new AppUpdateOrchestrator({
      installedVersion: "1.0.0",
      native: new FakeNative(scenario.native),
      ota: new FakeOta(scenario.ota),
      ...transitionDependencies(),
      safety: {
        getSafetySnapshot: () => safeSnapshot(),
      },
    });
    const first = orchestrator.getGate();

    assert.equal(first.state, scenario.state);
    assert.strictEqual(orchestrator.getGate(), first);
  }
});

test("真实 transition 状态切换更换门禁快照并向订阅者发送当前对象", async () => {
  const native = new FakeNative(nativeEnabled);
  const ota = new FakeOta(otaOptional);
  const transition = configuredTransition();
  const completion = deferred<void>();
  const orchestrator = new AppUpdateOrchestrator({
    installedVersion: "1.0.0",
    native,
    ota,
    transition,
    appStore: {
      async open() {},
    },
    safety: {
      getSafetySnapshot: () => safeSnapshot(),
    },
  });
  const observed: NewTransactionGate[] = [];
  const unsubscribe = orchestrator.subscribe((gate) => observed.push(gate));
  const beforeTransition = orchestrator.getGate();

  const transitionRun = transition.runTransition(() => completion.promise);
  const duringTransition = orchestrator.getGate();
  assert.notStrictEqual(duringTransition, beforeTransition);
  assert.strictEqual(observed.at(-1), duringTransition);

  completion.resolve();
  await transitionRun;
  const afterTransition = orchestrator.getGate();
  assert.notStrictEqual(afterTransition, duringTransition);
  assert.strictEqual(observed.at(-1), afterTransition);
  unsubscribe();
});

test("安全快照 await 期间策略变化时 fail-closed，绝不使用已过期 App Store 目标", async () => {
  const safety = deferred<{
    hasActiveCart: boolean;
    hasUnresolvedPayment: boolean;
    hasPendingDurableWrite: boolean;
    hasRecoveryRequired: boolean;
    hasCatalogRefreshInFlight: boolean;
    hasSyncOrAuditInFlight: boolean;
    hasFulfilmentInFlight: boolean;
  }>();
  const native = new FakeNative(nativeRequired);
  const ota = new FakeOta(otaOptional);
  const opened: string[] = [];
  const transition = configuredTransition();
  const orchestrator = new AppUpdateOrchestrator({
    installedVersion: "1.0.0",
    native,
    ota,
    transition,
    appStore: {
      async open(url) {
        opened.push(url);
      },
    },
    safety: {
      getSafetySnapshot: () => safety.promise,
    },
  });

  const action = orchestrator.performSelectedUpdate();
  native.setPolicy(
    Object.freeze({
      ...nativeRequired,
      latestVersion: "2.0.0",
      appStoreUrl:
        "https://apps.apple.com/au/app/hb-pos/id987654321",
    }),
  );
  safety.resolve(safeSnapshot());

  assert.deepEqual(await action, {
    action: "blocked",
    reason: "selection-changed",
  });
  assert.deepEqual(opened, []);
  assert.equal(transition.isTransitionActive(), false);
});

test("原生 App Store handoff 完成前持续持有 transition lease", async () => {
  const native = new FakeNative(nativeRequired);
  const ota = new FakeOta(otaOptional);
  const handoff = deferred<void>();
  const transition = configuredTransition();
  const orchestrator = new AppUpdateOrchestrator({
    installedVersion: "1.0.0",
    native,
    ota,
    transition,
    appStore: {
      open: () => handoff.promise,
    },
    safety: {
      getSafetySnapshot: () => safeSnapshot(),
    },
  });

  const action = orchestrator.performSelectedUpdate();
  await Promise.resolve();
  await Promise.resolve();
  assert.equal(transition.isTransitionActive(), true);
  await assert.rejects(
    transition.runOperation(async () => undefined),
    (error: unknown) =>
      error instanceof Error &&
      (error as Error & { code?: string }).code ===
        UPDATE_TRANSITION_IN_PROGRESS,
  );

  handoff.resolve();
  assert.deepEqual(await action, {
    action: "open-app-store",
    url: nativeRequired.appStoreUrl,
  });
  assert.equal(transition.isTransitionActive(), false);
});

test("OTA 使用冻结策略，并在下载前发现策略替换时拒绝", async () => {
  const native = new FakeNative(nativeEnabled);
  const ota = new FakeOta(otaRequired);
  const transition = configuredTransition();
  const orchestrator = new AppUpdateOrchestrator({
    installedVersion: "1.0.0",
    native,
    ota,
    transition,
    appStore: {
      async open() {
        throw new Error("OTA must not open App Store");
      },
    },
    safety: {
      getSafetySnapshot: () => safeSnapshot(),
    },
  });
  ota.onApply = async (selected, beforeDownload) => {
    assert.deepEqual(selected, otaRequired);
    ota.setPolicy(
      Object.freeze({
        ...otaRequired,
        policyVersion: "policy-replaced",
        iosUpdateId: "323e4567-e89b-42d3-a456-426614174000",
      }),
    );
    return (await beforeDownload()) === true
      ? { state: "downloaded", reason: null }
      : { state: "rejected", reason: "selection-changed" };
  };

  assert.deepEqual(await orchestrator.performSelectedUpdate(), {
    action: "ota",
    result: {
      state: "rejected",
      reason: "selection-changed",
    },
  });
  assert.equal(transition.isTransitionActive(), false);
});

test("OTA 检查期间安全状态变坏时下载前再次核验并拒绝", async () => {
  let safe = true;
  const native = new FakeNative(nativeEnabled);
  const ota = new FakeOta(otaRequired);
  const transition = configuredTransition();
  const orchestrator = new AppUpdateOrchestrator({
    installedVersion: "1.0.0",
    native,
    ota,
    transition,
    appStore: {
      async open() {
        throw new Error("OTA must not open App Store");
      },
    },
    safety: {
      getSafetySnapshot: () => ({
        ...safeSnapshot(),
        hasSyncOrAuditInFlight: !safe,
      }),
    },
  });
  ota.onApply = async (_selected, beforeDownload) => {
    safe = false;
    const decision = await beforeDownload();
    return decision === true
      ? { state: "downloaded", reason: null }
      : { state: "rejected", reason: decision };
  };

  assert.deepEqual(await orchestrator.performSelectedUpdate(), {
    action: "ota",
    result: {
      state: "rejected",
      reason: "restart-unsafe",
    },
  });
  assert.equal(transition.isTransitionActive(), false);
});

test("OTA 下载完成后提示重开：可选更新可收起、不重复安装", async () => {
  const ota = new FakeOta(otaOptional);
  const orchestrator = new AppUpdateOrchestrator({
    installedVersion: "1.0.0",
    native: new FakeNative(nativeEnabled),
    ota,
    transition: configuredTransition(),
    appStore: {
      async open() {
        throw new Error("OTA must not open App Store");
      },
    },
    safety: {
      getSafetySnapshot: () => safeSnapshot(),
    },
  });
  let applies = 0;
  ota.onApply = async (_selected, beforeDownload) => {
    applies += 1;
    assert.equal(await beforeDownload(), true);
    return { state: "downloaded", reason: null };
  };

  assert.equal(orchestrator.getPresentation().phase, "prompt");
  assert.deepEqual(await orchestrator.performSelectedUpdate(), {
    action: "ota",
    result: { state: "downloaded", reason: null },
  });
  const pending = orchestrator.getPresentation();
  assert.equal(pending.phase, "restart-pending");
  assert.equal(pending.blocking, false);
  assert.equal(orchestrator.getGate().canStartNewTransaction, true);

  // 再点一次只确认「已下载」，不再设置 channel 覆盖或重复下载。
  assert.deepEqual(await orchestrator.performSelectedUpdate(), {
    action: "ota",
    result: { state: "downloaded", reason: null },
  });
  assert.equal(applies, 1);

  // 前台刷新拿到同一目标时仍保持「请重开」。
  await orchestrator.refreshOnForeground();
  assert.equal(orchestrator.getPresentation().phase, "restart-pending");
});

test("强制 OTA 下载完成后仍全屏阻断新交易，直到重开换上新版本", async () => {
  const ota = new FakeOta(otaRequired);
  const orchestrator = new AppUpdateOrchestrator({
    installedVersion: "1.0.0",
    native: new FakeNative(nativeEnabled),
    ota,
    transition: configuredTransition(),
    appStore: {
      async open() {
        throw new Error("OTA must not open App Store");
      },
    },
    safety: {
      getSafetySnapshot: () => safeSnapshot(),
    },
  });
  ota.onApply = async () => ({ state: "downloaded", reason: null });

  await orchestrator.performSelectedUpdate();
  const pending = orchestrator.getPresentation();
  assert.equal(pending.phase, "restart-pending");
  assert.equal(pending.blocking, true);
  assert.equal(orchestrator.getGate().canStartNewTransaction, false);
  assert.deepEqual(await orchestrator.restartIfSafe(), {
    canRestart: true,
    reason: null,
  });

  // 后台改投新目标时，旧的「已下载」不能冒充新目标。
  ota.setPolicy(
    Object.freeze({
      ...otaRequired,
      policyVersion: "policy-next",
      iosUpdateId: "423e4567-e89b-42d3-a456-426614174000",
    }),
  );
  assert.notEqual(orchestrator.getPresentation().phase, "restart-pending");
});

test("立即重启：原生包未带 reload 修复（或未注入重启端口）时一律不可用，不会调 reload", async () => {
  let reloads = 0;
  type RestartPort = Readonly<{
    isAvailable(): boolean;
    reload(): Promise<void>;
  }>;
  const restartPorts: readonly (RestartPort | undefined)[] = [
    undefined,
    {
      isAvailable: () => false,
      reload: async () => {
        reloads += 1;
      },
    },
    {
      isAvailable: () => {
        throw new Error("native probe failed");
      },
      reload: async () => {
        reloads += 1;
      },
    },
  ];
  for (const restart of restartPorts) {
    const ota: FakeOta = new FakeOta(otaOptional);
    ota.onApply = async () => ({ state: "downloaded", reason: null });
    const orchestrator: AppUpdateOrchestrator = new AppUpdateOrchestrator({
      installedVersion: "1.0.0",
      native: new FakeNative(nativeEnabled),
      ota,
      ...transitionDependencies(),
      safety: { getSafetySnapshot: () => safeSnapshot() },
      ...(restart ? { restart } : {}),
    });
    await orchestrator.performSelectedUpdate();
    assert.equal(orchestrator.getPresentation().phase, "restart-pending");
    assert.equal(orchestrator.canRestartInProcess(), false);
    assert.deepEqual(await orchestrator.restartToApplyDownloadedUpdate(), {
      action: "blocked",
      reason: "restart-unavailable",
    });
  }
  assert.equal(reloads, 0);
});

test("立即重启：没下载过当前目标时拒绝，不会 reload 出原生自带版本", async () => {
  let reloads = 0;
  const orchestrator = new AppUpdateOrchestrator({
    installedVersion: "1.0.0",
    native: new FakeNative(nativeEnabled),
    ota: new FakeOta(otaOptional),
    ...transitionDependencies(),
    safety: { getSafetySnapshot: () => safeSnapshot() },
    restart: {
      isAvailable: () => true,
      reload: async () => {
        reloads += 1;
      },
    },
  });
  assert.equal(orchestrator.getPresentation().phase, "prompt");
  assert.deepEqual(await orchestrator.restartToApplyDownloadedUpdate(), {
    action: "blocked",
    reason: "not-downloaded",
  });
  assert.equal(reloads, 0);
});

test("立即重启：有未完成交易时在租约内拒绝，安全后才 reload 且租约保持封门", async () => {
  let reloads = 0;
  let snapshot: ReturnType<typeof safeSnapshot> | Record<string, boolean> =
    safeSnapshot();
  const transition = configuredTransition();
  const ota = new FakeOta(otaOptional);
  ota.onApply = async () => ({ state: "downloaded", reason: null });
  const orchestrator = new AppUpdateOrchestrator({
    installedVersion: "1.0.0",
    native: new FakeNative(nativeEnabled),
    ota,
    transition,
    appStore: { async open() {} },
    safety: { getSafetySnapshot: () => snapshot as never },
    restart: {
      isAvailable: () => true,
      reload: async () => {
        reloads += 1;
      },
    },
  });
  await orchestrator.performSelectedUpdate();
  assert.equal(orchestrator.canRestartInProcess(), true);

  snapshot = { ...safeSnapshot(), hasActiveCart: true };
  const blocked = await orchestrator.restartToApplyDownloadedUpdate();
  assert.equal(blocked.action, "blocked");
  assert.notEqual(blocked.reason, "restart-unavailable");
  assert.equal(reloads, 0);
  assert.equal(transition.isTransitionActive(), false);

  snapshot = safeSnapshot();
  let settled = false;
  void orchestrator.restartToApplyDownloadedUpdate().then(
    () => {
      settled = true;
    },
    () => {
      settled = true;
    },
  );
  await new Promise((resolve) => setImmediate(resolve));
  await new Promise((resolve) => setImmediate(resolve));
  assert.equal(reloads, 1);
  // reload 后旧 JS 可能还会跑一会儿：Promise 不返回、租约不释放，新交易一直被挡住。
  assert.equal(settled, false);
  assert.equal(transition.isTransitionActive(), true);
  assert.equal(orchestrator.getGate().canStartNewTransaction, false);
});

test("立即重启：reload 失败时原样抛出并释放租约，界面可改为提示手动重开", async () => {
  const transition = configuredTransition();
  const ota = new FakeOta(otaRequired);
  ota.onApply = async () => ({ state: "downloaded", reason: null });
  const orchestrator = new AppUpdateOrchestrator({
    installedVersion: "1.0.0",
    native: new FakeNative(nativeEnabled),
    ota,
    transition,
    appStore: { async open() {} },
    safety: { getSafetySnapshot: () => safeSnapshot() },
    restart: {
      isAvailable: () => true,
      reload: async () => {
        throw new Error("reload failed");
      },
    },
  });
  await orchestrator.performSelectedUpdate();
  await assert.rejects(
    orchestrator.restartToApplyDownloadedUpdate(),
    /reload failed/u,
  );
  assert.equal(transition.isTransitionActive(), false);
  // 强制更新仍保持阻断，直到真正换上新版本。
  assert.equal(orchestrator.getPresentation().phase, "restart-pending");
  assert.equal(orchestrator.getGate().canStartNewTransaction, false);
});

class FakeNative {
  public refreshes = 0;
  private readonly listeners = new Set<(gate: NewTransactionGate) => void>();

  public constructor(private policy: PosIpadUpdatePolicy | null) {}

  public getPolicy() {
    return this.policy;
  }

  public getGate(): NewTransactionGate {
    return deriveNewTransactionGate(this.policy);
  }

  public subscribe(listener: (gate: NewTransactionGate) => void) {
    this.listeners.add(listener);
    listener(this.getGate());
    return () => this.listeners.delete(listener);
  }

  public async refreshOnStartup() {
    this.refreshes += 1;
  }

  public async refreshOnForeground() {
    this.refreshes += 1;
  }

  public async refreshOnNetworkAvailable() {
    this.refreshes += 1;
  }

  public setPolicy(policy: PosIpadUpdatePolicy | null): void {
    this.policy = policy;
    for (const listener of this.listeners) listener(this.getGate());
  }
}

class FakeOta {
  public refreshes = 0;
  public onApply:
    | ((
        policy: PosIpadOtaUpdatePolicy,
        beforeDownload: () =>
          | true
          | "selection-changed"
          | "restart-unsafe"
          | Promise<
              true | "selection-changed" | "restart-unsafe"
            >,
      ) => Promise<
        | { state: "downloaded"; reason: null }
        | { state: "unavailable"; reason: "not-available" }
        | {
            state: "rejected";
            reason: "selection-changed" | "restart-unsafe";
          }
      >)
    | null = null;
  private readonly listeners =
    new Set<(policy: PosIpadOtaUpdatePolicy | null) => void>();

  public constructor(private policy: PosIpadOtaUpdatePolicy | null) {}

  public getPolicy() {
    return this.policy;
  }

  public subscribe(listener: (policy: PosIpadOtaUpdatePolicy | null) => void) {
    this.listeners.add(listener);
    listener(this.policy);
    return () => this.listeners.delete(listener);
  }

  public async refreshOnStartup() {
    this.refreshes += 1;
  }

  public async refreshOnForeground() {
    this.refreshes += 1;
  }

  public async refreshOnNetworkAvailable() {
    this.refreshes += 1;
  }

  public async apply(
    policy: PosIpadOtaUpdatePolicy,
    beforeDownload: () =>
      | true
      | "selection-changed"
      | "restart-unsafe"
      | Promise<true | "selection-changed" | "restart-unsafe">,
  ) {
    if (this.onApply) return this.onApply(policy, beforeDownload);
    return { state: "unavailable" as const, reason: "not-available" as const };
  }

  public setPolicy(policy: PosIpadOtaUpdatePolicy | null): void {
    this.policy = policy;
    for (const listener of this.listeners) listener(policy);
  }
}

function transitionDependencies(): Readonly<{
  transition: UpdateTransitionLeaseCoordinator;
  appStore: Readonly<{ open(url: string): Promise<void> }>;
}> {
  return {
    transition: configuredTransition(),
    appStore: {
      async open() {},
    },
  };
}

function configuredTransition(): UpdateTransitionLeaseCoordinator {
  const transition = new UpdateTransitionLeaseCoordinator();
  transition.bindTransitionBarrier((operation) => operation());
  return transition;
}

function safeSnapshot() {
  return {
    hasActiveCart: false,
    hasUnresolvedPayment: false,
    hasPendingDurableWrite: false,
    hasRecoveryRequired: false,
    hasCatalogRefreshInFlight: false,
    hasSyncOrAuditInFlight: false,
    hasFulfilmentInFlight: false,
  } as const;
}

function deferred<T>(): Readonly<{
  promise: Promise<T>;
  resolve(value: T | PromiseLike<T>): void;
}> {
  let resolve!: (value: T | PromiseLike<T>) => void;
  const promise = new Promise<T>((accept) => {
    resolve = accept;
  });
  return { promise, resolve };
}
