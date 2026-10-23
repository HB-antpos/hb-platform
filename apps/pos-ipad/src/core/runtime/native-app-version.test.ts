import assert from "node:assert/strict";
import test from "node:test";

import {
  formatAppVersionLabel,
  otaVersionLabel,
  resolveNativeAppVersion,
  UNKNOWN_NATIVE_APP_VERSION,
} from "./native-app-version";

test("优先采用原生壳的版本号与 build 号", () => {
  assert.deepEqual(
    resolveNativeAppVersion({
      applicationVersion: "0.2.0",
      constantsVersion: "0.1.0",
      configVersion: "0.0.9",
      applicationBuild: "12",
      constantsBuild: "1",
    }),
    { appVersion: "0.2.0", buildNumber: "12" },
  );
});

test("原生值缺失或为空白时依次回退到 expo-constants 与配置版本", () => {
  assert.deepEqual(
    resolveNativeAppVersion({
      applicationVersion: "  ",
      constantsVersion: null,
      configVersion: "0.2.0",
      applicationBuild: null,
      constantsBuild: " 11 ",
    }),
    { appVersion: "0.2.0", buildNumber: "11" },
  );
});

test("全部缺失时显示 unknown，界面不会出现空白", () => {
  assert.deepEqual(resolveNativeAppVersion({}), UNKNOWN_NATIVE_APP_VERSION);
});

test("内嵌包显示 embedded，OTA 显示更新 ID 前 8 位小写", () => {
  assert.equal(otaVersionLabel(null), "embedded");
  assert.equal(
    otaVersionLabel({
      runtimeVersion: "0.2.0",
      updateId: "ABCDEF12-3456",
      isEmbeddedLaunch: true,
    }),
    "embedded",
  );
  assert.equal(
    otaVersionLabel({
      runtimeVersion: "0.2.0",
      updateId: " ABCDEF12-3456 ",
      isEmbeddedLaunch: false,
    }),
    "abcdef12",
  );
});

test("收银页版本行只在运行 OTA 时附加更新 ID", () => {
  const version = { appVersion: "0.2.0", buildNumber: "12" };
  assert.equal(
    formatAppVersionLabel(version, "embedded"),
    "Version 0.2.0 · Build 12",
  );
  assert.equal(
    formatAppVersionLabel(version, "abcdef12"),
    "Version 0.2.0 · Build 12 · OTA abcdef12",
  );
});
