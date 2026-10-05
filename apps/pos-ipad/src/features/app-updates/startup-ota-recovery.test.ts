import assert from "node:assert/strict";
import test from "node:test";

import {
  ExpoOtaUpdatePort,
  type ExpoOtaUpdateManifest,
} from "./expo-ota-update-port";
import type { PosIpadOtaUpdatePolicyRemotePort } from "./hbpos-pos-ipad-ota-update-api";
import { OtaUpdateCoordinator } from "./ota-update-coordinator";
import {
  createStartupOtaRecoveryPolicyStore,
  StartupOtaRecovery,
} from "./startup-ota-recovery";

import {
  POS_IPAD_OTA_NONE_POLICY,
  type PosIpadOtaUpdatePolicy,
} from "@/core/contracts/ota-app-updates";

const RELEASE_CHANNEL = "pos-ipad-production-store-1013";

const fixPolicy: PosIpadOtaUpdatePolicy = Object.freeze({
  state: "optional",
  policyVersion: "11",
  channel: RELEASE_CHANNEL,
  runtimeVersion: "1.0.7",
  iosUpdateId: "09e7dd11-0000-4000-8000-000000000001",
  updateGroupId: "86a1abec-0000-4000-8000-000000000001",
  releaseMessage: "修复收款中途重启后无法启动",
});

type Harness = Readonly<{
  recovery: StartupOtaRecovery;
  calls: string[];
  remotePolicies: (PosIpadOtaUpdatePolicy | Error)[];
  overrides: (Record<string, string> | null)[];
  setCanReload(value: boolean): void;
  setManifest(manifest: ExpoOtaUpdateManifest): void;
}>;

function createHarness(
  options: Readonly<{
    enabled?: boolean;
    hasDevice?: boolean;
    installedRuntimeVersion?: string;
    remotePolicies?: (PosIpadOtaUpdatePolicy | Error)[];
  }> = {},
): Harness {
  const calls: string[] = [];
  const overrides: (Record<string, string> | null)[] = [];
  const remotePolicies = [...(options.remotePolicies ?? [fixPolicy, fixPolicy])];
  let canReload = true;
  let manifest: ExpoOtaUpdateManifest = {
    id: fixPolicy.iosUpdateId,
    runtimeVersion: fixPolicy.runtimeVersion,
  };
  const remote: PosIpadOtaUpdatePolicyRemotePort = {
    async getPolicy(metadata) {
      calls.push(`remote:${metadata.runtimeVersion}`);
      const next = remotePolicies.shift();
      if (!next) throw new Error("no more remote policies");
      if (next instanceof Error) throw next;
      return next;
    },
  };
  const installer = new ExpoOtaUpdatePort({
    enabled: true,
    runtimeVersion: options.installedRuntimeVersion ?? "1.0.7",
    updates: {
      setUpdateRequestHeadersOverride(headers) {
        overrides.push(headers);
      },
      async checkForUpdateAsync() {
        calls.push("native:check");
        return { isAvailable: true, manifest };
      },
      async fetchUpdateAsync() {
        calls.push("native:fetch");
        return { isNew: true, manifest };
      },
      async reloadAsync() {
        calls.push("native:reload");
      },
    },
  });
  const enabled = options.enabled ?? true;
  const coordinator = new OtaUpdateCoordinator({
    automaticChecksEnabled: enabled,
    metadata: {
      runtimeVersion: "1.0.7",
      currentUpdateId: null,
      currentUpdateGroupId: null,
    },
    policyStore: createStartupOtaRecoveryPolicyStore(),
    remote,
    installer,
  });
  const recovery = new StartupOtaRecovery({
    enabled,
    hasDeviceCredentials: async () => {
      calls.push("keychain:device");
      return options.hasDevice ?? true;
    },
    coordinator,
    canReload: () => canReload,
  });
  return {
    recovery,
    calls,
    remotePolicies,
    overrides,
    setCanReload(value) {
      canReload = value;
    },
    setManifest(next) {
      manifest = next;
    },
  };
}

test("未启用 OTA 的构建不读凭据、不出网", async () => {
  const harness = createHarness({ enabled: false });

  const state = await harness.recovery.check();

  assert.deepEqual(state, {
    phase: "unavailable",
    reason: "updates-disabled",
  });
  assert.deepEqual(harness.calls, []);
});

test("没有可出站的设备凭据时不请求策略", async () => {
  const harness = createHarness({ hasDevice: false });

  const state = await harness.recovery.check();

  assert.deepEqual(state, {
    phase: "unavailable",
    reason: "device-unavailable",
  });
  assert.deepEqual(harness.calls, ["keychain:device"]);
});

test("后台无更新时显示已是最新，不触碰原生更新模块", async () => {
  const harness = createHarness({
    remotePolicies: [POS_IPAD_OTA_NONE_POLICY],
  });

  const state = await harness.recovery.check();

  assert.deepEqual(state, { phase: "up-to-date" });
  assert.equal(harness.calls.some((call) => call.startsWith("native:")), false);
});

test("卡死设备可检查并套用受控 OTA：安装前复核策略，channel 只在核验期间覆盖", async () => {
  const harness = createHarness();
  const observed: string[] = [];
  harness.recovery.subscribe((state) => observed.push(state.phase));

  const checked = await harness.recovery.check();
  assert.deepEqual(checked, {
    phase: "available",
    requirement: "optional",
    releaseMessage: fixPolicy.releaseMessage,
  });

  await harness.recovery.apply();

  assert.deepEqual(harness.calls, [
    "keychain:device",
    "remote:1.0.7",
    "keychain:device",
    "remote:1.0.7",
    "native:check",
    "native:fetch",
    "native:reload",
  ]);
  // 构造时清除遗留覆盖 → 核验时定向 channel → 核验后清除，reload 前不再保留分店 channel。
  assert.deepEqual(harness.overrides, [
    null,
    { "expo-channel-name": RELEASE_CHANNEL },
    null,
  ]);
  assert.deepEqual(observed, ["checking", "available", "applying"]);
  assert.equal(harness.recovery.getState().phase, "applying");
});

test("检查后后台撤回或替换目标时放弃安装", async () => {
  const replaced: PosIpadOtaUpdatePolicy = Object.freeze({
    ...fixPolicy,
    policyVersion: "12",
    iosUpdateId: "0d4273b2-0000-4000-8000-000000000002",
  });
  const harness = createHarness({ remotePolicies: [fixPolicy, replaced] });

  await harness.recovery.check();
  const state = await harness.recovery.apply();

  assert.deepEqual(state, { phase: "failed", reason: "selection-changed" });
  assert.equal(harness.calls.some((call) => call.startsWith("native:")), false);
});

test("安装前复核策略失败时不使用首次检查结果", async () => {
  const harness = createHarness({
    remotePolicies: [fixPolicy, new Error("offline")],
  });

  await harness.recovery.check();
  const state = await harness.recovery.apply();

  assert.deepEqual(state, { phase: "failed", reason: "check-failed" });
  assert.equal(harness.calls.some((call) => call.startsWith("native:")), false);
});

test("首次成功后再次检查失败不回退到内存旧策略", async () => {
  const harness = createHarness({
    remotePolicies: [fixPolicy, new Error("offline")],
  });

  await harness.recovery.check();
  const state = await harness.recovery.check();

  assert.deepEqual(state, { phase: "failed", reason: "check-failed" });
  // 失败后不能直接安装旧目标。
  assert.deepEqual(await harness.recovery.apply(), state);
});

test("runtime 已被重试拉起时拒绝 reload，并清除 channel 覆盖", async () => {
  const harness = createHarness();
  await harness.recovery.check();
  harness.setCanReload(false);

  const state = await harness.recovery.apply();

  assert.deepEqual(state, { phase: "failed", reason: "restart-unsafe" });
  assert.equal(harness.calls.includes("native:reload"), false);
  assert.equal(harness.overrides.at(-1), null);
});

test("策略 runtimeVersion 与本机安装包不符时在原生检查前拒绝", async () => {
  const harness = createHarness({ installedRuntimeVersion: "1.0.6" });
  await harness.recovery.check();

  const state = await harness.recovery.apply();

  assert.deepEqual(state, { phase: "failed", reason: "runtime-mismatch" });
  assert.equal(harness.calls.some((call) => call.startsWith("native:")), false);
});

test("服务器返回的 manifest 与策略 updateId 不符时不 reload", async () => {
  const harness = createHarness();
  harness.setManifest({ id: "other-update", runtimeVersion: "1.0.7" });
  await harness.recovery.check();

  const state = await harness.recovery.apply();

  assert.deepEqual(state, { phase: "failed", reason: "update-id-mismatch" });
  assert.equal(harness.calls.includes("native:reload"), false);
  assert.equal(harness.overrides.at(-1), null);
});

test("重复点击安装只执行一次原生下载与 reload", async () => {
  const harness = createHarness();
  await harness.recovery.check();

  await Promise.all([harness.recovery.apply(), harness.recovery.apply()]);

  assert.equal(
    harness.calls.filter((call) => call === "native:reload").length,
    1,
  );
});

test("恢复通道的策略库不返回任何缓存", async () => {
  const store = createStartupOtaRecoveryPolicyStore();

  await store.save(fixPolicy);

  assert.equal(await store.get(), null);
});
