import assert from "node:assert/strict";
import test from "node:test";

import {
  androidApiLevel,
  androidBluetoothScanPermissions,
  requestAndroidBluetoothScanPermissions,
} from "./android-bluetooth-permissions";

test("Android 10/11 扫描蓝牙请求精确定位权限，与原生 requiredPermissions 分界一致", () => {
  for (const version of [29, 30]) {
    assert.deepEqual(androidBluetoothScanPermissions({ os: "android", version }), [
      "android.permission.ACCESS_FINE_LOCATION",
    ]);
  }
});

test("Android 12+ 扫描蓝牙请求附近的设备权限", () => {
  for (const version of [31, 33, 35]) {
    assert.deepEqual(androidBluetoothScanPermissions({ os: "android", version }), [
      "android.permission.BLUETOOTH_SCAN",
      "android.permission.BLUETOOTH_CONNECT",
    ]);
  }
});

test("非 Android 平台不请求任何权限", async () => {
  let requested = 0;
  await requestAndroidBluetoothScanPermissions(
    { os: "ios", version: "18.0" },
    async () => {
      requested += 1;
    },
  );
  assert.equal(requested, 0);
});

test("Android 扫描前把对应权限交给系统授权框", async () => {
  const calls: string[][] = [];
  await requestAndroidBluetoothScanPermissions(
    { os: "android", version: 29 },
    async (permissions) => {
      calls.push([...permissions]);
      return { "android.permission.ACCESS_FINE_LOCATION": "never_ask_again" };
    },
  );
  assert.deepEqual(calls, [["android.permission.ACCESS_FINE_LOCATION"]]);
});

test("授权框请求失败不阻断扫描，最终以原生权限检查为准", async () => {
  await assert.doesNotReject(() =>
    requestAndroidBluetoothScanPermissions(
      { os: "android", version: 34 },
      async () => {
        throw new Error("Activity unavailable");
      },
    ),
  );
});

test("API 级别兼容字符串并把无法解析的值视为旧系统", () => {
  assert.equal(androidApiLevel({ os: "android", version: "31" }), 31);
  assert.equal(androidApiLevel({ os: "android", version: "unknown" }), 0);
  assert.deepEqual(
    androidBluetoothScanPermissions({ os: "android", version: "unknown" }),
    ["android.permission.ACCESS_FINE_LOCATION"],
  );
});
