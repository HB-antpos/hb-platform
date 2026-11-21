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

// ===== 班中后台定位已取消：只在扫码上班/下班那一刻取一次前台位置 =====
// 原因：后台定位耗电，且员工用私人手机、抵触被持续定位。原生层一旦重新声明后台定位能力，
// 新包就会再次出现「始终允许」授权入口、iOS 蓝色定位状态栏和 Android 前台服务常驻通知。
const infoPlist = config.ios?.infoPlist ?? {};
assert.match(
  infoPlist.NSLocationWhenInUseUsageDescription ?? "",
  /precise location/i,
  "必须保留「使用 App 期间」定位文案：扫码打卡仍需在前台取一次精确位置"
);
for (const key of ["NSLocationAlwaysAndWhenInUseUsageDescription", "NSLocationAlwaysUsageDescription"]) {
  assert.equal(infoPlist[key], undefined, `app.json 不得声明 ${key}（班中后台定位已取消）`);
}
const locationOptions = pluginMap.get("expo-location");
assert.notEqual(locationOptions?.isIosBackgroundLocationEnabled, true, "expo-location 不得开启 iOS 后台定位");
assert.notEqual(locationOptions?.isAndroidBackgroundLocationEnabled, true, "expo-location 不得开启 Android 后台定位");
// 注意：只“删掉”这两个文案选项不够——expo-location 插件在没有显式值时会把默认文案
// 「Allow $(PRODUCT_NAME) to access your location」写回 Info.plist，必须显式传 false 才会删除对应键。
// 删除 NSLocationAlwaysAndWhenInUseUsageDescription 还有一层好处：expo-location 的 iOS 原生代码会据此判断
// 是否具备「始终允许」配置，缺少该键时永远不会去请求 Always 授权。
assert.equal(locationOptions?.locationAlwaysAndWhenInUsePermission, false, "locationAlwaysAndWhenInUsePermission 必须显式为 false");
assert.equal(locationOptions?.locationAlwaysPermission, false, "locationAlwaysPermission 必须显式为 false");
const configuredAndroidPermissions = config.android?.permissions ?? [];
assert.equal(
  configuredAndroidPermissions.includes("android.permission.ACCESS_BACKGROUND_LOCATION"),
  false,
  "app.json 不得声明 ACCESS_BACKGROUND_LOCATION"
);
assert.equal(
  configuredAndroidPermissions.includes("android.permission.ACCESS_FINE_LOCATION"),
  true,
  "app.json 必须保留 ACCESS_FINE_LOCATION：扫码打卡要取精确位置"
);
// Android 原生工程已纳入版本管理，EAS 不会再对它 prebuild，Manifest 才是实际打包内容。
// 这里只拦截定位相关的两项：FOREGROUND_SERVICE 本身不拦，以免日后下载/播放等功能合理使用时被误伤。
for (const permission of ["ACCESS_BACKGROUND_LOCATION", "FOREGROUND_SERVICE_LOCATION"]) {
  assert.doesNotMatch(
    androidManifest,
    new RegExp(`android\\.permission\\.${permission}"`),
    `AndroidManifest.xml 不得声明 ${permission}（班中后台定位已取消）`
  );
}
assert.match(
  androidManifest,
  /android\.permission\.ACCESS_FINE_LOCATION"/,
  "AndroidManifest.xml 必须保留 ACCESS_FINE_LOCATION"
);

// 最终产物核对：`--type public` 只反映 app.json 原文，看不到各 config plugin 运行之后的结果。
// introspect 会真正执行 iOS/Android 的 mod，能发现「插件把默认文案或后台模式写回去」这类问题。
const introspectResult = spawnSync(
  process.execPath,
  [expoCli, "config", "--type", "introspect", "--json"],
  { cwd: process.cwd(), encoding: "utf8", env: process.env }
);
assert.equal(introspectResult.status, 0, introspectResult.stderr || "Expo introspect config 生成失败");
const introspected = JSON.parse(introspectResult.stdout);
const finalInfoPlist = introspected.ios?.infoPlist ?? {};
assert.equal(
  (finalInfoPlist.UIBackgroundModes ?? []).includes("location"),
  false,
  "最终 Info.plist 的 UIBackgroundModes 不得包含 location"
);
for (const key of ["NSLocationAlwaysAndWhenInUseUsageDescription", "NSLocationAlwaysUsageDescription"]) {
  assert.equal(finalInfoPlist[key], undefined, `最终 Info.plist 不得包含 ${key}（可能被 expo-location 默认文案写回）`);
}
assert.ok(
  typeof finalInfoPlist.NSLocationWhenInUseUsageDescription === "string" &&
    finalInfoPlist.NSLocationWhenInUseUsageDescription.length > 0,
  "最终 Info.plist 必须保留 NSLocationWhenInUseUsageDescription"
);
// ===== BLE 扫码枪（react-native-ble-plx）：只在前台连接，不开后台蓝牙 =====
// 打印机插件与 ble-plx 插件都会写 NSBluetoothAlwaysUsageDescription，最终文案必须同时覆盖打印机与扫码枪。
const blePlxOptions = pluginMap.get("react-native-ble-plx");
assert.ok(blePlxOptions, "必须配置 react-native-ble-plx 插件（BLE 扫码枪）");
assert.equal(blePlxOptions.isBackgroundEnabled, false, "ble-plx 不得开启后台蓝牙");
assert.deepEqual(blePlxOptions.modes, [], "ble-plx 不得声明蓝牙后台模式");
assert.match(finalInfoPlist.NSBluetoothAlwaysUsageDescription ?? "", /printers/i, "蓝牙文案必须覆盖打印机");
assert.match(finalInfoPlist.NSBluetoothAlwaysUsageDescription ?? "", /barcode scanners/i, "蓝牙文案必须覆盖扫码枪");
for (const mode of ["bluetooth-central", "bluetooth-peripheral"]) {
  assert.equal((finalInfoPlist.UIBackgroundModes ?? []).includes(mode), false, `最终 Info.plist 不得包含后台模式 ${mode}`);
}

const finalAndroidPermissions = introspected.android?.permissions ?? [];
assert.ok(finalAndroidPermissions.length > 0, "introspect 结果缺少 android.permissions，无法核对最终权限");
for (const permission of [
  "android.permission.ACCESS_BACKGROUND_LOCATION",
  "android.permission.FOREGROUND_SERVICE_LOCATION",
]) {
  assert.equal(finalAndroidPermissions.includes(permission), false, `最终 Android 权限不得包含 ${permission}`);
}

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
