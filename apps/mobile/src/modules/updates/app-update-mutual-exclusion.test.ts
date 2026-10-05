import assert from "node:assert/strict";
import test from "node:test";

import {
  createAppUpdateMutualExclusion,
  createUpdateLaneRetryGate,
} from "./app-update-mutual-exclusion";

test("APK 与 OTA 下载、提示、安装/reload 共享互斥", () => {
  const coordinator = createAppUpdateMutualExclusion({ otaInitializationPending: false });
  const nativeDownload = coordinator.tryStartOperation("native");
  assert.ok(nativeDownload);
  assert.equal(coordinator.tryStartOperation("ota"), null);
  nativeDownload?.finish();

  assert.equal(coordinator.tryOwnPrompt("native"), true);
  assert.equal(coordinator.tryOwnPrompt("ota"), false);
  assert.equal(coordinator.canReloadOta(), false);

  coordinator.activateNativeInstaller();
  assert.equal(coordinator.tryStartOperation("ota"), null);
  assert.equal(coordinator.canReloadOta(), false);

  coordinator.clearNativeInstaller();
  assert.equal(coordinator.tryOwnPrompt("ota"), true);
  assert.equal(coordinator.canReloadOta(), true);
  coordinator.releasePrompt("ota");
});

test("旧 lease 结束不能释放新操作", () => {
  const coordinator = createAppUpdateMutualExclusion({ otaInitializationPending: false });
  const first = coordinator.tryStartOperation("ota");
  assert.ok(first);
  first?.finish();
  const second = coordinator.tryStartOperation("native");
  assert.ok(second);
  first?.finish();
  assert.equal(coordinator.tryStartOperation("ota"), null);
  second?.finish();
});

test("状态释放会通知等待中的另一条更新 lane", () => {
  const coordinator = createAppUpdateMutualExclusion({ otaInitializationPending: false });
  let notifications = 0;
  const unsubscribe = coordinator.subscribe(() => {
    notifications += 1;
  });
  const lease = coordinator.tryStartOperation("native");
  lease?.finish();
  coordinator.tryOwnPrompt("native");
  coordinator.releasePrompt("native");
  unsubscribe();
  coordinator.tryOwnPrompt("ota");
  assert.equal(notifications, 4);
});

test("Mobile scope 未初始化或 required 时原生 optional 不得抢占", () => {
  const coordinator = createAppUpdateMutualExclusion();
  assert.equal(coordinator.tryStartOperation("native"), null);
  coordinator.setOtaInitializationPending(false);
  const native = coordinator.tryStartOperation("native");
  assert.ok(native);
  native?.finish();

  coordinator.setOtaRequiredGate(true);
  assert.equal(coordinator.tryStartOperation("native"), null);
  assert.equal(coordinator.tryOwnPrompt("native"), false);
  assert.equal(coordinator.isOtaRequiredGateActive(), true);
  coordinator.setOtaRequiredGate(false);
  assert.ok(coordinator.tryStartOperation("native"));
});

test("lane 只在 lease 曾被阻塞后消费一次重试，不响应自身 acquire/finish 无限自唤醒", () => {
  const gate = createUpdateLaneRetryGate();
  assert.equal(gate.consumeRetry(), false);

  gate.markBlocked();
  assert.equal(gate.consumeRetry(), true);
  assert.equal(gate.consumeRetry(), false);

  gate.markBlocked();
  gate.clear();
  assert.equal(gate.consumeRetry(), false);
});

test("iOS 原生 optional 提示等 Mobile 决策完成，OTA required 时继续 fail-closed", () => {
  const coordinator = createAppUpdateMutualExclusion();
  assert.equal(coordinator.tryOwnPrompt("native"), false);

  coordinator.setOtaRequiredGate(true);
  coordinator.setOtaInitializationPending(false);
  assert.equal(coordinator.tryOwnPrompt("native"), false);

  coordinator.setOtaRequiredGate(false);
  assert.equal(coordinator.tryOwnPrompt("native"), true);
  coordinator.releasePrompt("native");
});

test("安卓原生强制更新优先于 OTA 初始化与 OTA required，且禁止 OTA 抢占与 reload", () => {
  const coordinator = createAppUpdateMutualExclusion();
  coordinator.setOtaRequiredGate(true);
  assert.equal(coordinator.tryStartOperation("native"), null, "默认仍让 OTA 门禁优先");

  coordinator.setNativeRequiredGate(true);
  assert.equal(coordinator.isNativeRequiredGateActive(), true);
  const nativeDownload = coordinator.tryStartOperation("native");
  assert.ok(nativeDownload, "原生 required 时不再等待 OTA 初始化或 OTA required");
  nativeDownload?.finish();
  assert.equal(coordinator.tryStartOperation("ota"), null);
  assert.equal(coordinator.tryOwnPrompt("ota"), false);
  assert.equal(coordinator.canReloadOta(), false, "原生 required 时 OTA 不得 reload");
  assert.equal(coordinator.tryOwnPrompt("native"), true);
  coordinator.releasePrompt("native");

  coordinator.setNativeRequiredGate(false);
  assert.equal(coordinator.tryStartOperation("native"), null, "关闭后恢复原有优先级");
});
