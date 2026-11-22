import assert from "node:assert/strict";
import test from "node:test";

import type { BleScannerStatus } from "./ble-scanner-controller";
import type { SavedBleScanner } from "./ble-scanner-core";
import {
  bleScannerDisplayName,
  buildBleScannerRowSubtitle,
  describeBleRssi,
  describeBleScannerIndicator,
  maskSensitiveScanValue,
  resolveBleScannerBadge,
} from "./ble-scanner-presentation";

const saved: SavedBleScanner = Object.freeze({
  version: 1,
  peripheralId: "A1B2C3D4-0000-1111-2222-333344449C2E",
  name: "BarCode Scanner BLE",
  alias: null,
  profileId: "yichip-feea",
  serviceUuid: "0000FEEA-0000-1000-8000-00805F9B34FB",
  notifyUuid: "00002AA1-0000-1000-8000-00805F9B34FB",
  autoReconnect: true,
  savedAt: "2026-11-21T00:00:00.000Z",
});

const base: BleScannerStatus = Object.freeze({
  state: "ready",
  saved,
  batteryLevel: 82,
  rssi: -61,
  lastScanAt: 1,
  lastScanValue: "9313559558795",
  attention: false,
  lastErrorCode: null,
});

const t = (key: string, options?: Record<string, unknown>) =>
  options ? `${key}:${Object.values(options).join(",")}` : key;

test("状态点：已连接绿、休眠灰、重连黄；需处理优先显示红色", () => {
  assert.deepEqual(resolveBleScannerBadge(base), { key: "ready", tone: "success" });
  assert.deepEqual(resolveBleScannerBadge({ ...base, state: "sleeping" }), { key: "sleeping", tone: "neutral" });
  assert.deepEqual(resolveBleScannerBadge({ ...base, state: "reconnecting" }), { key: "reconnecting", tone: "warning" });
  assert.deepEqual(resolveBleScannerBadge({ ...base, state: "reconnecting", attention: true }), {
    key: "attention",
    tone: "danger",
  });
  assert.deepEqual(resolveBleScannerBadge({ ...base, saved: null, state: "not-paired" }), {
    key: "notPaired",
    tone: "neutral",
  });
});

test("名字：有备注名用备注名，否则广播名 + 设备 ID 尾 4 位", () => {
  assert.equal(bleScannerDisplayName(saved, "?"), "BarCode Scanner BLE · 9C2E");
  assert.equal(bleScannerDisplayName({ ...saved, alias: "1 号背夹" }, "?"), "1 号背夹");
  assert.equal(bleScannerDisplayName({ ...saved, name: "" }, "未命名"), "未命名 · 9C2E");
});

test("设置行副标题：未配对 / 电量 / 休眠提示 / 最近一次扫码尾 6 位", () => {
  assert.equal(buildBleScannerRowSubtitle({ ...base, saved: null }, t), "bleScanner.rowNotPaired");
  assert.equal(
    buildBleScannerRowSubtitle({ ...base, saved: { ...saved, alias: "1 号背夹" } }, t),
    "1 号背夹 · bleScanner.batteryShort:82 · bleScanner.lastScanShort:…558795",
  );
  assert.equal(
    buildBleScannerRowSubtitle({ ...base, state: "sleeping", lastScanValue: null }, t),
    "BarCode Scanner BLE · 9C2E · bleScanner.sleepingHint",
  );
});

test("信号分档与敏感码屏蔽", () => {
  assert.equal(describeBleRssi(null), "unknown");
  assert.equal(describeBleRssi(-55), "strong");
  assert.equal(describeBleRssi(-70), "good");
  assert.equal(describeBleRssi(-90), "weak");
  assert.equal(maskSensitiveScanValue("HBDEV1-ABC-DEF"), "HBDEV1-••••••");
  assert.equal(maskSensitiveScanValue("9313559558795"), "9313559558795");
});

test("扫码页状态按钮：没配对不显示；休眠 / 重连中 / 已断开点按直接重连，其余打开设置", () => {
  assert.equal(describeBleScannerIndicator({ ...base, saved: null, state: "not-paired" }), null, "没配对过不显示");
  assert.equal(describeBleScannerIndicator({ ...base, state: "unsupported" }), null);

  const ready = describeBleScannerIndicator(base);
  assert.deepEqual(ready, {
    icon: "bluetooth-connect",
    tone: "success",
    labelKey: "bleScanner.state.ready",
    batteryLevel: 82,
    action: "open-settings",
  });
  const low = describeBleScannerIndicator({ ...base, batteryLevel: 15, attention: true });
  assert.equal(low?.tone, "danger", "低电量变红");
  assert.equal(low?.labelKey, "bleScanner.state.ready", "低电量仍显示已连接");
  assert.equal(
    describeBleScannerIndicator({ ...base, state: "sleeping", attention: true })?.labelKey,
    "bleScanner.state.sleeping",
    "休眠时低电量不改成需处理",
  );

  for (const state of ["sleeping", "reconnecting", "disconnected"] as const) {
    const view = describeBleScannerIndicator({ ...base, state, batteryLevel: null });
    assert.equal(view?.action, "reconnect", `${state} 点按直接重连`);
    assert.equal(view?.labelKey, `bleScanner.state.${state}`);
  }
  assert.equal(describeBleScannerIndicator({ ...base, state: "sleeping" })?.tone, "neutral", "休眠灰色，不算异常");
  assert.equal(describeBleScannerIndicator({ ...base, state: "sleeping" })?.batteryLevel, null, "没连上不显示电量");
  const stuck = describeBleScannerIndicator({ ...base, state: "reconnecting", attention: true });
  assert.equal(stuck?.tone, "danger");
  assert.equal(stuck?.labelKey, "bleScanner.state.attention");

  assert.equal(describeBleScannerIndicator({ ...base, state: "connecting" })?.action, "open-settings");
  assert.equal(describeBleScannerIndicator({ ...base, state: "bluetooth-off" })?.tone, "danger");
  assert.equal(describeBleScannerIndicator({ ...base, state: "bluetooth-off" })?.action, "open-settings");
});
