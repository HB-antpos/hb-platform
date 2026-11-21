import assert from "node:assert/strict";
import test from "node:test";

import type { BleScannerStatus } from "./ble-scanner-controller";
import type { SavedBleScanner } from "./ble-scanner-core";
import {
  bleScannerDisplayName,
  buildBleScannerRowSubtitle,
  describeBleRssi,
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
