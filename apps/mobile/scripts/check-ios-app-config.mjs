import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { readFileSync } from "node:fs";
import { resolve } from "node:path";

const forbiddenSecret = "forbidden-sentinel";
const expoCli = resolve(process.cwd(), "node_modules/expo/bin/cli");
const result = spawnSync(
  process.execPath,
  [expoCli, "config", "--type", "public", "--json"],
  {
    cwd: process.cwd(),
    encoding: "utf8",
    env: {
      ...process.env,
      HB_LOG_CENTER_KEY: forbiddenSecret,
    },
  }
);

assert.equal(result.status, 0, result.stderr || "Expo public config 生成失败");
assert.equal(result.stdout.includes(forbiddenSecret), false, "public Expo config 不得包含日志中心密钥");

const config = JSON.parse(result.stdout);
const easConfig = JSON.parse(
  readFileSync(resolve(process.cwd(), "eas.json"), "utf8")
);
assert.equal(
  easConfig.build?.production?.ios?.image,
  "macos-sequoia-15.6-xcode-26.0",
  "production iOS 构建必须固定使用 Xcode 26.0 镜像"
);
assert.equal("key" in (config.extra?.logCenter ?? {}), false, "客户端 extra.logCenter 不得包含 key 字段");
assert.equal(config.ios?.infoPlist?.NSMicrophoneUsageDescription, undefined, "iOS 不得声明麦克风用途");
assert.match(config.ios?.infoPlist?.NSCameraUsageDescription ?? "", /barcode|scan/i, "相机文案应覆盖扫码");
assert.match(config.ios?.infoPlist?.NSCameraUsageDescription ?? "", /silent advertisement videos/i, "相机文案应覆盖静音广告录像");
assert.match(config.ios?.infoPlist?.NSPhotoLibraryUsageDescription ?? "", /advertisement photos or videos/i, "照片库文案应覆盖广告媒体");
assert.equal(
  config.android?.permissions?.includes("android.permission.RECORD_AUDIO") ?? false,
  false,
  "Android 配置不得声明录音权限"
);
const androidManifest = readFileSync(
  resolve(process.cwd(), "android/app/src/main/AndroidManifest.xml"),
  "utf8"
);
assert.match(
  androidManifest,
  /<uses-permission\s+android:name="android\.permission\.RECORD_AUDIO"\s+tools:node="remove"\s*\/>/,
  "现有 Android 原生工程必须在 Manifest merge 时显式移除录音权限"
);

const pluginMap = new Map(
  (config.plugins ?? [])
    .filter(Array.isArray)
    .map(([name, options]) => [name, options])
);
for (const pluginName of ["expo-camera", "expo-image-picker", "expo-audio"]) {
  const options = pluginMap.get(pluginName);
  assert.equal(options?.microphonePermission, false, `${pluginName} 必须关闭 iOS 麦克风权限`);
}
assert.equal(pluginMap.get("expo-camera")?.recordAudioAndroid, false, "expo-camera 必须关闭 Android 录音");
assert.equal(pluginMap.get("expo-audio")?.recordAudioAndroid, false, "expo-audio 必须关闭 Android 录音");

const manifest = config.ios?.privacyManifests;
assert.equal(manifest?.NSPrivacyTracking, false, "隐私清单必须明确不跟踪");
assert.deepEqual(manifest?.NSPrivacyTrackingDomains, [], "不跟踪时不得声明跟踪域名");

const expectedRequiredReasons = new Map([
  ["NSPrivacyAccessedAPICategoryFileTimestamp", ["C617.1", "0A2A.1", "3B52.1"]],
  ["NSPrivacyAccessedAPICategoryUserDefaults", ["CA92.1"]],
  ["NSPrivacyAccessedAPICategoryDiskSpace", ["E174.1", "85F4.1"]],
  ["NSPrivacyAccessedAPICategorySystemBootTime", ["35F9.1"]],
]);
const actualRequiredReasons = new Map(
  (manifest?.NSPrivacyAccessedAPITypes ?? []).map((entry) => [
    entry.NSPrivacyAccessedAPIType,
    entry.NSPrivacyAccessedAPITypeReasons,
  ])
);
assert.deepEqual(actualRequiredReasons, expectedRequiredReasons, "Required Reason API 声明必须与审核清单一致");

const expectedCollectedTypes = [
  "NSPrivacyCollectedDataTypeName",
  "NSPrivacyCollectedDataTypeEmailAddress",
  "NSPrivacyCollectedDataTypePhoneNumber",
  "NSPrivacyCollectedDataTypePhysicalAddress",
  "NSPrivacyCollectedDataTypeHealth",
  "NSPrivacyCollectedDataTypePaymentInfo",
  "NSPrivacyCollectedDataTypeOtherFinancialInfo",
  "NSPrivacyCollectedDataTypePreciseLocation",
  "NSPrivacyCollectedDataTypePhotosorVideos",
  "NSPrivacyCollectedDataTypeAudioData",
  "NSPrivacyCollectedDataTypeUserID",
  "NSPrivacyCollectedDataTypeDeviceID",
  "NSPrivacyCollectedDataTypeOtherUserContent",
  "NSPrivacyCollectedDataTypeOtherUsageData",
  "NSPrivacyCollectedDataTypeOtherDiagnosticData",
  "NSPrivacyCollectedDataTypeOtherDataTypes",
];
const collectedEntries = manifest?.NSPrivacyCollectedDataTypes ?? [];
assert.deepEqual(
  collectedEntries.map((entry) => entry.NSPrivacyCollectedDataType),
  expectedCollectedTypes,
  "收集数据类型必须与审核数据清单一致"
);

const developerAdvertisingTypes = new Set([
  "NSPrivacyCollectedDataTypePhotosorVideos",
  "NSPrivacyCollectedDataTypeAudioData",
  "NSPrivacyCollectedDataTypeOtherUserContent",
]);
for (const entry of collectedEntries) {
  assert.equal(entry.NSPrivacyCollectedDataTypeLinked, true, `${entry.NSPrivacyCollectedDataType} 必须声明关联用户`);
  assert.equal(entry.NSPrivacyCollectedDataTypeTracking, false, `${entry.NSPrivacyCollectedDataType} 不得声明跟踪`);
  assert.equal(
    entry.NSPrivacyCollectedDataTypePurposes.includes("NSPrivacyCollectedDataTypePurposeAppFunctionality"),
    true,
    `${entry.NSPrivacyCollectedDataType} 必须包含 App 功能用途`
  );
  assert.equal(
    entry.NSPrivacyCollectedDataTypePurposes.includes("NSPrivacyCollectedDataTypePurposeDeveloperAdvertising"),
    developerAdvertisingTypes.has(entry.NSPrivacyCollectedDataType),
    `${entry.NSPrivacyCollectedDataType} 的开发者广告用途必须精确`
  );
}

console.log("check-ios-app-config.mjs: ok");
