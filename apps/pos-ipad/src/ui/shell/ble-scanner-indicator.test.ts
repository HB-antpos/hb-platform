import assert from "node:assert/strict";
import test from "node:test";

import {
  type BleScannerIndicatorSnapshot,
  describeBleScannerIndicator,
  isBleScannerReconnectable,
  isSameBleScannerIndicator,
  toBleScannerIndicatorSnapshot,
} from "./ble-scanner-indicator";

import type {
  BleScannerConnectionState,
  BleScannerStatus,
} from "@/core/peripherals/scanner/ble/ble-scanner-controller";

function snapshot(
  state: BleScannerConnectionState,
  extra: Partial<Omit<BleScannerIndicatorSnapshot, "state">> = {},
): BleScannerIndicatorSnapshot {
  return { state, batteryLevel: null, attention: false, ...extra };
}

test("已连接显示绿色并带电量；电量 ≤20% 改黄色提醒", () => {
  assert.deepEqual(describeBleScannerIndicator(snapshot("ready", { batteryLevel: 85 })), {
    icon: "bluetooth-connect",
    tone: "good",
    labelKey: "status.bleScanner.ready",
    batteryLevel: 85,
  });
  assert.equal(
    describeBleScannerIndicator(snapshot("ready", { batteryLevel: 20 }))?.tone,
    "warning",
  );
  // 读不到电量（部分型号没有电池服务）时按正常已连接处理，不附带百分比。
  const unknownBattery = describeBleScannerIndicator(snapshot("ready"));
  assert.equal(unknownBattery?.tone, "good");
  assert.equal(unknownBattery?.batteryLevel, null);
});

test("连接中 / 学习中 / 重连中是黄色；重连太久需处理时升为红色", () => {
  for (const state of ["connecting", "learning", "reconnecting"] as const) {
    assert.equal(describeBleScannerIndicator(snapshot(state))?.tone, "warning", state);
  }
  assert.equal(
    describeBleScannerIndicator(snapshot("reconnecting", { attention: true }))?.tone,
    "danger",
  );
});

test("休眠、已断开、未配对是灰色，休眠不会被当成异常", () => {
  for (const state of ["sleeping", "disconnected", "not-paired"] as const) {
    assert.equal(describeBleScannerIndicator(snapshot(state))?.tone, "neutral", state);
  }
  // 休眠时即使控制器标了 attention（例如电量低），也不应变红吓到收银员。
  assert.equal(
    describeBleScannerIndicator(snapshot("sleeping", { attention: true }))?.tone,
    "neutral",
  );
});

test("蓝牙关闭 / 无权限是红色；本机不支持蓝牙则不显示图标", () => {
  for (const state of ["bluetooth-off", "unauthorized"] as const) {
    const view = describeBleScannerIndicator(snapshot(state));
    assert.equal(view?.tone, "danger", state);
    assert.equal(view?.icon, "bluetooth-off", state);
  }
  assert.equal(describeBleScannerIndicator(snapshot("unsupported")), null);
});

test("每个状态的文案键都按 status.bleScanner.<state> 生成", () => {
  const states: readonly BleScannerConnectionState[] = [
    "bluetooth-off",
    "connecting",
    "disconnected",
    "learning",
    "not-paired",
    "ready",
    "reconnecting",
    "sleeping",
    "unauthorized",
  ];
  for (const state of states) {
    assert.equal(
      describeBleScannerIndicator(snapshot(state))?.labelKey,
      `status.bleScanner.${state}`,
    );
  }
});

test("投影只取顶栏关心的三个字段；RSSI、最近扫码时间变化不会让快照变化", () => {
  const status = {
    state: "ready",
    saved: null,
    batteryLevel: 70,
    rssi: -60,
    lastScanAt: 1,
    lastScanValue: "123",
    attention: false,
    lastErrorCode: null,
  } as const satisfies BleScannerStatus;
  const first = toBleScannerIndicatorSnapshot(status);
  const second = toBleScannerIndicatorSnapshot({
    ...status,
    rssi: -72,
    lastScanAt: 99,
    lastScanValue: "456",
  });
  assert.deepEqual(first, { state: "ready", batteryLevel: 70, attention: false });
  assert.equal(isSameBleScannerIndicator(first, second), true);
  assert.equal(
    isSameBleScannerIndicator(first, toBleScannerIndicatorSnapshot({ ...status, batteryLevel: 65 })),
    false,
  );
  assert.equal(isSameBleScannerIndicator(null, first), false);
  assert.equal(isSameBleScannerIndicator(null, null), true);
});

test("只有休眠 / 重连中 / 已断开时顶栏可点重连", () => {
  const reconnectable: BleScannerConnectionState[] = ["sleeping", "reconnecting", "disconnected"];
  const readOnly: BleScannerConnectionState[] = [
    "ready",
    "learning",
    "connecting",
    "not-paired",
    "bluetooth-off",
    "unauthorized",
    "unsupported",
  ];
  for (const state of reconnectable) {
    assert.equal(isBleScannerReconnectable(snapshot(state)), true, state);
  }
  for (const state of readOnly) {
    assert.equal(isBleScannerReconnectable(snapshot(state)), false, state);
  }
  // 重连太久需处理（红色）时也要能点。
  assert.equal(isBleScannerReconnectable(snapshot("reconnecting", { attention: true })), true);
});
