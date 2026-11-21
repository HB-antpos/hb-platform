import assert from "node:assert/strict";
import { execFileSync } from "node:child_process";
import { readFileSync, readdirSync } from "node:fs";
import { fileURLToPath } from "node:url";

const appRoot = new URL("../", import.meta.url);
const workspaceRoot = new URL("../../", appRoot);
const packageJson = JSON.parse(readFileSync(new URL("package.json", appRoot), "utf8"));
const packageLock = JSON.parse(
  readFileSync(new URL("package-lock.json", workspaceRoot), "utf8"),
);
const appLockEntry = packageLock.packages["apps/pos-ipad"];
const appConfigSource = readFileSync(new URL("app.config.ts", appRoot), "utf8");
const easConfig = JSON.parse(readFileSync(new URL("eas.json", appRoot), "utf8"));
const appProvidersSource = readFileSync(
  new URL("src/app-providers.tsx", appRoot),
  "utf8",
);
const appEntrySource = readFileSync(new URL("index.js", appRoot), "utf8");
const routeFiles = readdirSync(new URL("app/", appRoot), {
  recursive: true,
  withFileTypes: true,
})
  .filter((entry) => entry.isFile())
  .map((entry) => entry.name);

assert.equal(packageJson.name, "@hb/pos-ipad");
assert.equal(packageJson.version, "0.2.0");
assert.equal(appLockEntry.name, "@hb/pos-ipad");
assert.equal(appLockEntry.version, "0.2.0");
assert.equal(packageJson.main, "index.js");
assert.equal(
  appEntrySource.trim(),
  [
    'import "./src/core/performance/business-startup-origin";',
    'import "./src/core/performance/business-startup-clock";',
    'import "./src/core/peripherals/customer-display/native/external-display-native-module";',
    'import "./src/core/observability/sentry";',
    'import "expo-router/entry";',
  ].join("\n"),
  "自定义入口必须先捕获无依赖性能原点，再注册计时器、客显与 Sentry，最后交给 Expo Router。",
);
assert.equal(packageJson.private, true);
assert.equal(packageJson.scripts.android, undefined);
assert.match(
  packageJson.scripts["test:sync-history"],
  /src\/features\/sync-history\/\*\.rntl\.test\.tsx/,
  "默认同步历史测试必须包含屏幕 RNTL 用例。",
);
assert.match(appConfigSource, /com\.hbweb\.posipad/);
assert.match(appConfigSource, /supportedInterfaceOrientations/);
assert.match(appConfigSource, /UIRequiresFullScreen/);
assert.match(
  appConfigSource,
  /isTabletOnly:\s*true/,
  "HB POS 必须生成仅支持 iPad 的原生二进制，不能要求 iPhone App Store 截图。",
);
assert.match(appConfigSource, /useSQLCipher:\s*true/);
assert.match(
  appConfigSource,
  /"expo-audio"[\s\S]*microphonePermission:\s*false[\s\S]*recordAudioAndroid:\s*false/u,
  "仅播放的 POS 音效必须显式禁用麦克风与 Android 录音权限。",
);
assert.match(
  appConfigSource,
  /"expo-camera"[\s\S]*microphonePermission:\s*false[\s\S]*recordAudioAndroid:\s*false/u,
  "仅扫码的相机插件必须显式禁用麦克风与 Android 录音权限。",
);
assert.match(appConfigSource, /\.\/plugins\/with-hb-printer/);
assert.match(appConfigSource, /\.\/plugins\/with-hb-external-display/);
assert.match(appProvidersSource, /PeripheralStatusBridge/);
assert.equal(
  routeFiles.some((name) => /\.(?:test|spec)\.[cm]?[jt]sx?$/.test(name)),
  false,
  "Expo Router app/ 目录不得包含测试文件，避免测试依赖进入生产 bundle。",
);
assert.equal(easConfig.build.development.developmentClient, true);
assert.equal(easConfig.build.preview.distribution, "internal");
assert.equal(easConfig.build.production.distribution, "store");
assert.equal(
  easConfig.cli.appVersionSource,
  "remote",
  "动态 app.config.ts 必须使用 EAS 远程 build number，才能安全自动递增生产构建。",
);
assert.equal(
  easConfig.build.production.autoIncrement,
  true,
  "生产 Store 构建必须自动递增远程 build number。",
);
assert.equal(
  easConfig.build.production.env.EXPO_PUBLIC_HBPOS_EAS_PROJECT_ID,
  "b6491153-83a0-4fe0-9016-49e61e69ed97",
  "Production 远端构建必须能解析固定的 EAS projectId。",
);
assert.equal(
  easConfig.build.production.env.EXPO_PUBLIC_HBPOS_UPDATES_URL,
  "https://u.expo.dev/b6491153-83a0-4fe0-9016-49e61e69ed97",
  "Production 远端构建必须固定到同一 EAS Updates URL。",
);
assert.equal(
  easConfig.submit.production.ios.ascAppId,
  "6802176079",
  "HB POS 生产提交必须固定到 com.hbweb.posipad 对应的 App Store Connect 记录。",
);

const introspectedConfig = JSON.parse(
  execFileSync("npx", ["expo", "config", "--type", "introspect", "--json"], {
    cwd: fileURLToPath(appRoot),
    encoding: "utf8",
  }),
);
assert.equal(introspectedConfig.version, "0.2.0");
assert.equal(
  introspectedConfig.ios?.isTabletOnly,
  true,
  "Expo 最终配置必须把 TARGETED_DEVICE_FAMILY 收窄为 iPad。",
);
assert.deepEqual(introspectedConfig.runtimeVersion, {
  policy: "appVersion",
});
const audioPlugin = introspectedConfig.plugins?.find(
  (plugin) => Array.isArray(plugin) && plugin[0] === "expo-audio",
);
const cameraPlugin = introspectedConfig.plugins?.find(
  (plugin) => Array.isArray(plugin) && plugin[0] === "expo-camera",
);
assert.ok(Array.isArray(audioPlugin), "必须保留 expo-audio 插件。");
assert.ok(Array.isArray(cameraPlugin), "必须保留 expo-camera 插件。");
assert.equal(audioPlugin[1]?.microphonePermission, false);
assert.equal(audioPlugin[1]?.recordAudioAndroid, false);
assert.equal(cameraPlugin[1]?.microphonePermission, false);
assert.equal(cameraPlugin[1]?.recordAudioAndroid, false);

const finalInfoPlist = introspectedConfig.ios?.infoPlist ?? {};
const backgroundModes = Array.isArray(finalInfoPlist.UIBackgroundModes)
  ? finalInfoPlist.UIBackgroundModes
  : [finalInfoPlist.UIBackgroundModes].filter(Boolean);
const androidPermissions = Array.isArray(introspectedConfig.android?.permissions)
  ? introspectedConfig.android.permissions
  : [];
assert.equal(
  Object.hasOwn(finalInfoPlist, "NSMicrophoneUsageDescription"),
  false,
  "最终 iOS 配置不得包含麦克风用途说明。",
);
assert.equal(
  backgroundModes.includes("audio"),
  false,
  "最终 iOS 配置不得启用后台音频模式。",
);
assert.equal(
  androidPermissions.includes("android.permission.RECORD_AUDIO"),
  false,
  "最终 Android 配置不得申请录音权限。",
);

// BLE 扫码枪接入后仍只在前台用蓝牙：不得出现后台蓝牙模式；三处写入的蓝牙用途说明必须是同一句。
assert.equal(
  backgroundModes.includes("bluetooth-central"),
  false,
  "最终 iOS 配置不得启用后台蓝牙（bluetooth-central）。",
);
assert.equal(
  backgroundModes.includes("bluetooth-peripheral"),
  false,
  "最终 iOS 配置不得启用蓝牙外设后台模式。",
);
assert.equal(
  finalInfoPlist.NSBluetoothAlwaysUsageDescription,
  "HB POS 使用蓝牙连接门店小票打印机、钱箱和蓝牙扫码枪。",
  "蓝牙用途说明必须覆盖小票打印机、钱箱和蓝牙扫码枪。",
);

console.log("pos-ipad project contract: ok");
