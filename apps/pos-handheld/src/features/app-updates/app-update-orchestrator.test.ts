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
  type PosHandheldUpdatePolicy,
} from "@/core/contracts/app-updates";
import type { PosHandheldOtaUpdatePolicy } from "@/core/contracts/ota-app-updates";

const nativeEnabled: PosHandheldUpdatePolicy = Object.freeze({
  enabled: true,
  state: "none",
  policyVersion: "none",
  platform: "iOS",
  required: false,
  latestVersion: null,
  latestBuild: null,
  minimumSupportedVersion: null,
  distribution: null,
  downloadUrl: null,
  fileSize: null,
  sha256: null,
  packageName: null,
  signingCertificateSha256: null,
  bundleIdentifier: null,
  appStoreId: null,
  releaseMessage: null,
});
const nativeOptional: PosHandheldUpdatePolicy = Object.freeze({
  enabled: true,
  state: "optional",
  policyVersion: "ios-native-110",
  platform: "iOS",
  required: false,
  latestVersion: "1.1.0",
  latestBuild: "110",
  minimumSupportedVersion: "1.0.0",
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
const nativeRequired = Object.freeze({
  ...nativeOptional,
  state: "required" as const,
  policyVersion: "ios-native-required-110",
  required: true,
});
const nativeDisabled = Object.freeze({
  ...nativeEnabled,
  enabled: false,
});
const androidRequired: PosHandheldUpdatePolicy = Object.freeze({
  enabled: true,
  state: "required",
  policyVersion: "android-native-required-200",
  platform: "Android",
  required: true,
  latestVersion: "2.0.0",
  latestBuild: "200",
  minimumSupportedVersion: "1.5.0",
  distribution: "apk",
  downloadUrl: "https://updates.example.test/hb-pos-handheld-200.apk",
  fileSize: 2_048,
  sha256: "a".repeat(64),
  packageName: "com.hbweb.poshandheld",
  signingCertificateSha256: "b".repeat(64),
  bundleIdentifier: null,
  appStoreId: null,
  releaseMessage: "Security update",
});
const otaOptional: PosHandheldOtaUpdatePolicy = Object.freeze({
  state: "optional",
  policyVersion: "policy-optional",
  appKey: "pos-handheld",
  projectName: "hb-pos-handheld",
  platform: "iOS",
  required: false,
  channel: "store-s001",
  runtimeVersion: "1.0.0",
  updateId: "123e4567-e89b-42d3-a456-426614174000",
  updateGroupId: "223e4567-e89b-42d3-a456-426614174000",
  releaseMessage: null,
});
const otaRequired = Object.freeze({
  ...otaOptional,
  state: "required" as const,
  policyVersion: "policy-required",
  required: true,
});
const androidOtaRequired: PosHandheldOtaUpdatePolicy = Object.freeze({
  ...otaRequired,
  platform: "Android",
  policyVersion: "android-ota-required",
  updateId: "android-update-required",
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

test("Android APK 决策独立于 iOS OTA/App Store 分支", async () => {
  const presentation = chooseAppUpdatePresentation(
    androidRequired,
    otaRequired,
    "1.0.0",
  );
  assert.deepEqual(
    {
      kind: presentation.kind,
      platform: presentation.platform,
      appStoreUrl: presentation.appStoreUrl,
      downloadUrl: presentation.downloadUrl,
    },
    {
      kind: "native",
      platform: "Android",
      appStoreUrl: null,
      downloadUrl: androidRequired.downloadUrl,
    },
  );

  const installed: PosHandheldUpdatePolicy[] = [];
  const orchestrator = new AppUpdateOrchestrator({
    installedVersion: "1.0.0",
    native: new FakeNative(androidRequired),
    ota: new FakeOta(otaRequired),
    ...transitionDependencies(),
    appStore: {
      async open() {
        throw new Error("Android must not open App Store");
      },
    },
    androidNative: {
      async prepare() {},
      async getInstallPermissionStatus() {
        return "granted" as const;
      },
      async openInstallPermissionSettings() {},
      async install(decision) {
        installed.push(decision);
        return {
          launched: true,
          packageName: "com.hbweb.poshandheld",
          versionCode: 200,
        };
      },
    },
    safety: { getSafetySnapshot: () => safeSnapshot() },
  });

  await orchestrator.prepareSelectedUpdate();
  assert.deepEqual(await orchestrator.performSelectedUpdate(), {
    action: "install-android-apk",
  });
  assert.equal(installed.length, 1);
  assert.strictEqual(installed[0], androidRequired);
  assert.deepEqual(installed[0], androidRequired);

  const noNativeUpdate: PosHandheldUpdatePolicy = Object.freeze({
    ...nativeEnabled,
    platform: "Android",
  });
  assert.equal(
    chooseAppUpdatePresentation(noNativeUpdate, otaRequired, "1.0.0").kind,
    "none",
    "Android 不得消费 iOS OTA 决策",
  );
});

test("Android 返回前台可后台准备 APK，但授权设置与安装始终由用户触发", async () => {
  const events: string[] = [];
  const native = new FakeNative(androidRequired);
  const ota = new FakeOta(androidOtaRequired);
  const orchestrator = new AppUpdateOrchestrator({
    installedVersion: "1.0.0",
    native,
    ota,
    ...transitionDependencies(),
    safety: { getSafetySnapshot: () => safeSnapshot() },
    androidNative: {
      async prepare() { events.push("download"); },
      async getInstallPermissionStatus() {
        events.push("permission");
        return "denied" as const;
      },
      async openInstallPermissionSettings() {
        events.push("settings");
      },
      async install() {
        events.push("install");
        throw new Error("foreground must never install an APK");
      },
    },
  });

  assert.equal(await orchestrator.getAndroidInstallPermissionStatus(), "denied");
  await orchestrator.openAndroidInstallPermissionSettings();
  await orchestrator.refreshOnForeground();

  await orchestrator.prepareSelectedUpdate();
  assert.deepEqual(events, ["permission", "settings", "download"]);
  assert.equal(native.refreshes, 1);
  assert.equal(ota.refreshes, 1);
});

test("Android 可选择同平台 OTA，且不会调用 APK installer 或 App Store", async () => {
  const androidNoNative: PosHandheldUpdatePolicy = Object.freeze({
    ...nativeEnabled,
    platform: "Android",
  });
  const ota = new FakeOta(androidOtaRequired);
  const orchestrator = new AppUpdateOrchestrator({
    installedVersion: "1.0.0",
    native: new FakeNative(androidNoNative),
    ota,
    ...transitionDependencies(),
    appStore: {
      async open() {
        throw new Error("Android OTA must not open App Store");
      },
    },
    androidNative: {
      async prepare() {},
      async getInstallPermissionStatus() {
        return "granted" as const;
      },
      async openInstallPermissionSettings() {},
      async install() {
        throw new Error("Android OTA must not invoke APK installer");
      },
    },
    safety: { getSafetySnapshot: () => safeSnapshot() },
  });

  assert.equal(orchestrator.getPresentation().kind, "ota");
  assert.equal(orchestrator.getPresentation().platform, "Android");
  await orchestrator.prepareSelectedUpdate();
  assert.deepEqual(await orchestrator.performSelectedUpdate(), {
    action: "ota",
    result: { state: "unavailable", reason: "not-available" },
  });
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

test("五种门禁语义的重复 getGate 均复用同一快照", () => {
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

  await orchestrator.prepareSelectedUpdate();
  const action = orchestrator.performSelectedUpdate();
  native.setPolicy(
    Object.freeze({
      ...nativeRequired,
      policyVersion: "ios-native-required-200",
      latestVersion: "2.0.0",
      latestBuild: "200",
      downloadUrl:
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
    androidNative: {
      async prepare() {},
      async getInstallPermissionStatus() {
        return "granted" as const;
      },
      async openInstallPermissionSettings() {},
      async install() {
        throw new Error("iOS must not call Android APK installer");
      },
    },
    safety: {
      getSafetySnapshot: () => safeSnapshot(),
    },
  });

  await orchestrator.prepareSelectedUpdate();
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
    url: nativeRequired.downloadUrl,
  });
  assert.equal(transition.isTransitionActive(), false);
});

test("OTA 使用冻结策略，并在 fetch 后 reload 前发现策略替换时拒绝", async () => {
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
  ota.onApply = async (selected, beforeReload) => {
    assert.deepEqual(selected, otaRequired);
    ota.setPolicy(
      Object.freeze({
        ...otaRequired,
        policyVersion: "policy-replaced",
        updateId: "323e4567-e89b-42d3-a456-426614174000",
      }),
    );
    return (await beforeReload()) === true
      ? { state: "reloaded", reason: null }
      : { state: "rejected", reason: "selection-changed" };
  };

  await orchestrator.prepareSelectedUpdate();
  assert.deepEqual(await orchestrator.performSelectedUpdate(), {
    action: "ota",
    result: {
      state: "rejected",
      reason: "selection-changed",
    },
  });
  assert.equal(transition.isTransitionActive(), false);
});

test("OTA fetch 期间安全状态变坏时 reload 前再次核验并拒绝", async () => {
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
  ota.onApply = async (_selected, beforeReload) => {
    safe = false;
    const decision = await beforeReload();
    return decision === true
      ? { state: "reloaded", reason: null }
      : { state: "rejected", reason: decision };
  };

  await orchestrator.prepareSelectedUpdate();
  assert.deepEqual(await orchestrator.performSelectedUpdate(), {
    action: "ota",
    result: {
      state: "rejected",
      reason: "restart-unsafe",
    },
  });
  assert.equal(transition.isTransitionActive(), false);
});

class FakeNative {
  public refreshes = 0;
  private readonly listeners = new Set<(gate: NewTransactionGate) => void>();

  public constructor(private policy: PosHandheldUpdatePolicy | null) {}

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

  public setPolicy(policy: PosHandheldUpdatePolicy | null): void {
    this.policy = policy;
    for (const listener of this.listeners) listener(this.getGate());
  }
}

class FakeOta {
  public async prepare() {
    return { state: "ready" as const, reason: null };
  }
  public refreshes = 0;
  public onApply:
    | ((
        policy: PosHandheldOtaUpdatePolicy,
        beforeReload: () =>
          | true
          | "selection-changed"
          | "restart-unsafe"
          | Promise<
              true | "selection-changed" | "restart-unsafe"
            >,
      ) => Promise<
        | { state: "reloaded"; reason: null }
        | { state: "unavailable"; reason: "not-available" }
        | {
            state: "rejected";
            reason: "selection-changed" | "restart-unsafe";
          }
      >)
    | null = null;
  private readonly listeners =
    new Set<(policy: PosHandheldOtaUpdatePolicy | null) => void>();

  public constructor(private policy: PosHandheldOtaUpdatePolicy | null) {}

  public getPolicy() {
    return this.policy;
  }

  public subscribe(listener: (policy: PosHandheldOtaUpdatePolicy | null) => void) {
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
    policy: PosHandheldOtaUpdatePolicy,
    beforeReload: () =>
      | true
      | "selection-changed"
      | "restart-unsafe"
      | Promise<true | "selection-changed" | "restart-unsafe">,
  ) {
    if (this.onApply) return this.onApply(policy, beforeReload);
    return { state: "unavailable" as const, reason: "not-available" as const };
  }

  public setPolicy(policy: PosHandheldOtaUpdatePolicy | null): void {
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

for (const kind of ["native", "ota"] as const) {
  test(`${kind} 后台下载不持交易租约，完成前禁止安装，完成后才允许用户确认`, async () => {
    const download = deferred<void>();
    let downloads = 0;
    let installs = 0;
    const native = new FakeNative(kind === "native"
      ? { ...androidRequired, state: "optional", required: false }
      : { ...nativeEnabled, platform: "Android" });
    const ota = new FakeOta(kind === "ota"
      ? { ...androidOtaRequired, state: "optional", required: false }
      : { state: "none", platform: "Android", policyVersion: "none" } as PosHandheldOtaUpdatePolicy);
    const prepare = async () => { downloads++; await download.promise; };
    ota.prepare = async () => { await prepare(); return { state: "ready", reason: null }; };
    ota.onApply = async (_policy, beforeReload) => {
      assert.equal(await beforeReload(), true);
      installs++;
      return { state: "reloaded", reason: null };
    };
    const transition = configuredTransition();
    const orchestrator = new AppUpdateOrchestrator({
      installedVersion: "1.0.0", native, ota, transition,
      safety: { getSafetySnapshot: () => safeSnapshot() },
      appStore: { async open() { throw new Error("Android cannot open App Store"); } },
      androidNative: {
        prepare,
        async getInstallPermissionStatus() { return "granted"; },
        async openInstallPermissionSettings() {},
        async install() { installs++; return { launched: true, packageName: "com.hbweb.poshandheld", versionCode: 200 }; },
      },
    });
    await orchestrator.refreshOnStartup();
    const duplicate = orchestrator.prepareSelectedUpdate();
    assert.equal(orchestrator.getPresentation().downloadState, "downloading");
    assert.equal(transition.isTransitionActive(), false);
    assert.equal(orchestrator.getGate().canStartNewTransaction, true);
    assert.equal((await orchestrator.performSelectedUpdate()).action, "blocked");
    assert.equal(installs, 0);
    download.resolve();
    await duplicate;
    assert.equal(orchestrator.getPresentation().downloadState, "ready");
    await orchestrator.refreshOnForeground();
    assert.equal(downloads, 1);
    await orchestrator.performSelectedUpdate();
    assert.equal(installs, 1);
    assert.equal(transition.isTransitionActive(), false);
  });
}

test("下载失败可重试，同版本元数据变化必须重新下载", async () => {
  const native = new FakeNative({ ...androidRequired, state: "optional", required: false });
  const ota = new FakeOta({ ...otaOptional, platform: "Android" });
  let downloads = 0;
  const orchestrator = new AppUpdateOrchestrator({
    installedVersion: "1.0.0", native, ota, ...transitionDependencies(),
    safety: { getSafetySnapshot: () => safeSnapshot() },
    androidNative: {
      async prepare() { if (++downloads === 1) throw new Error("offline"); },
      async getInstallPermissionStatus() { return "granted"; },
      async openInstallPermissionSettings() {},
      async install() { throw new Error("must not install"); },
    },
  });
  await orchestrator.prepareSelectedUpdate();
  assert.equal(orchestrator.getPresentation().downloadState, "failed");
  await orchestrator.prepareSelectedUpdate();
  assert.equal(orchestrator.getPresentation().downloadState, "ready");
  const previousTargetKey = orchestrator.getPresentation().downloadTargetKey;
  assert.ok(previousTargetKey);
  native.setPolicy({ ...native.getPolicy()!, sha256: "c".repeat(64) });
  assert.equal(orchestrator.getPresentation().downloadState, "idle");
  assert.notEqual(orchestrator.getPresentation().downloadTargetKey, previousTargetKey);
  assert.equal((await orchestrator.performSelectedUpdate()).action, "blocked");
  await orchestrator.prepareSelectedUpdate();
  assert.equal(downloads, 3);
  await assert.rejects(orchestrator.performSelectedUpdate(), /must not install/);
  assert.equal(orchestrator.getPresentation().downloadState, "failed");
  assert.equal((await orchestrator.performSelectedUpdate()).action, "blocked");
  assert.equal(downloads, 3, "安装失败后先展示重试，不能再次点击安装就隐式下载");
});

test("用户确认时重新检查策略，已撤回的下载目标不能安装", async () => {
  const native = new FakeNative(androidRequired);
  const ota = new FakeOta(androidOtaRequired);
  let installs = 0;
  const orchestrator = new AppUpdateOrchestrator({
    installedVersion: "1.0.0", native, ota, ...transitionDependencies(),
    safety: { getSafetySnapshot: () => safeSnapshot() },
    androidNative: {
      async prepare() {},
      async getInstallPermissionStatus() { return "granted"; },
      async openInstallPermissionSettings() {},
      async install() { installs++; return { launched: true, packageName: "com.hbweb.poshandheld", versionCode: 200 }; },
    },
  });
  await orchestrator.prepareSelectedUpdate();
  native.refreshOnForeground = async () => {
    native.setPolicy({ ...nativeEnabled, platform: "Android" });
  };
  assert.deepEqual(await orchestrator.performSelectedUpdate(), { action: "blocked", reason: "selection-changed" });
  assert.equal(installs, 0);
});

test("APK 进度走独立通道：只接受准备中的当前目标，不触发门禁重算，结束后清空", async () => {
  const download = deferred<void>();
  const captured: {
    onProgress?: ((progress: { bytesWritten: number; totalBytes: number }) => void) | undefined;
  } = {};
  const orchestrator = new AppUpdateOrchestrator({
    installedVersion: "1.0.0",
    native: new FakeNative({ ...androidRequired, state: "optional", required: false }),
    ota: new FakeOta({
      state: "none",
      platform: "Android",
      policyVersion: "none",
    } as PosHandheldOtaUpdatePolicy),
    ...transitionDependencies(),
    safety: { getSafetySnapshot: () => safeSnapshot() },
    androidNative: {
      async prepare(_decision, options) {
        captured.onProgress = options?.onProgress;
        await download.promise;
      },
      async getInstallPermissionStatus() {
        return "granted" as const;
      },
      async openInstallPermissionSettings() {},
      async install() {
        throw new Error("progress test must not install");
      },
    },
  });
  const seen: unknown[] = [];
  let gateNotifications = 0;
  orchestrator.subscribeDownloadProgress((progress) => seen.push(progress));
  orchestrator.subscribe(() => {
    gateNotifications += 1;
  });

  const preparing = orchestrator.prepareSelectedUpdate();
  while (!captured.onProgress) await Promise.resolve();
  const targetKey = orchestrator.getPresentation().downloadTargetKey;
  assert.equal(orchestrator.getPresentation().downloadState, "downloading");
  const gateNotificationsBefore = gateNotifications;

  captured.onProgress({ bytesWritten: 512, totalBytes: 2_048 });
  assert.deepEqual(orchestrator.getDownloadProgress(), {
    targetKey,
    bytesWritten: 512,
    totalBytes: 2_048,
  });
  assert.equal(gateNotifications, gateNotificationsBefore);

  download.resolve();
  await preparing;
  assert.equal(orchestrator.getDownloadProgress(), null);
  // 准备结束后迟到的原生事件直接丢弃。
  captured.onProgress({ bytesWritten: 2_048, totalBytes: 2_048 });
  assert.equal(orchestrator.getDownloadProgress(), null);
  assert.deepEqual(seen, [
    null,
    { targetKey, bytesWritten: 512, totalBytes: 2_048 },
    null,
  ]);
});
