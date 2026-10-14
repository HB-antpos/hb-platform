// 推送相关的应用配置契约：app.json 插件与权限、app.config.ts 对 GOOGLE_SERVICES_JSON 的处理（有 / 无两种情况）、
// 提交在仓库里的 Android 原生工程（bare）里的 FCM 接线，以及凭据文件绝不入库。
import assert from "node:assert/strict";
import { execFileSync, spawnSync } from "node:child_process";
import { readFileSync } from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

const projectRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const read = (relative) => readFileSync(path.join(projectRoot, relative), "utf8");
const appJson = JSON.parse(read("app.json")).expo;

function resolveExpoConfig(env) {
  const cleanEnv = { ...process.env };
  delete cleanEnv.GOOGLE_SERVICES_JSON;
  const result = spawnSync(process.execPath, [path.join(projectRoot, "node_modules/expo/bin/cli"), "config", "--type", "public", "--json"], {
    cwd: projectRoot,
    env: { ...cleanEnv, ...env },
    encoding: "utf8",
  });
  assert.equal(result.status, 0, result.stderr || "expo config 生成失败");
  return JSON.parse(result.stdout);
}

// ───────── app.json：插件与权限 ─────────
{
  const plugin = appJson.plugins.find((entry) => (Array.isArray(entry) ? entry[0] : entry) === "expo-notifications");
  assert.ok(plugin, "app.json 必须登记 expo-notifications 插件（iOS 的 aps-environment 由它写入）");
  assert.ok(Array.isArray(plugin), "插件带配置（通知强调色）");
  assert.match(plugin[1].color, /^#[0-9A-Fa-f]{6}$/);
  assert.equal("icon" in plugin[1], false, "不配自定义小图标，使用默认");
  assert.ok(appJson.android.permissions.includes("android.permission.POST_NOTIFICATIONS"), "Android 13+ 通知需要运行时授权，须声明 POST_NOTIFICATIONS");
  assert.equal(appJson.android.permissions.includes("android.permission.RECORD_AUDIO"), false);
  assert.equal("googleServicesFile" in appJson.android, false, "凭据路径只来自环境变量，不写进 app.json");
  assert.equal(JSON.stringify(appJson).includes("google-services.json"), false, "app.json 里不出现凭据文件名");
}

// ───────── app.config.ts：有无 GOOGLE_SERVICES_JSON ─────────
{
  const without = resolveExpoConfig({});
  assert.equal(without.android.googleServicesFile, undefined, "没设置环境变量：不写 googleServicesFile（本地开发 / OTA / 没配置的构建不受影响）");
  assert.equal("googleServicesFile" in without.android, false);

  for (const blank of ["", "   "]) {
    const blankConfig = resolveExpoConfig({ GOOGLE_SERVICES_JSON: blank });
    assert.equal("googleServicesFile" in blankConfig.android, false, "空值等同没设置");
  }

  const filePath = "/tmp/eas-file-env/abc/google-services.json";
  const withFile = resolveExpoConfig({ GOOGLE_SERVICES_JSON: `  ${filePath}  ` });
  assert.equal(withFile.android.googleServicesFile, filePath, "有值：原样（去掉首尾空白）写入 android.googleServicesFile");

  // 除 googleServicesFile 外，两种情况的 android / ios / 插件配置完全一致
  const strip = (config) => {
    const copy = JSON.parse(JSON.stringify(config));
    delete copy.android.googleServicesFile;
    return copy;
  };
  assert.deepEqual(strip(withFile), strip(without));
  assert.equal(JSON.stringify(without).includes("google-services"), false);
}

// ───────── 提交在仓库里的 Android 原生工程（bare）：EAS 不会重新 prebuild，需要手工接线 ─────────
{
  const manifest = read("android/app/src/main/AndroidManifest.xml");
  assert.match(manifest, /<uses-permission android:name="android\.permission\.POST_NOTIFICATIONS"\s*\/>/);
  assert.match(manifest, /<uses-permission android:name="android\.permission\.RECORD_AUDIO" tools:node="remove"\s*\/>/, "既有的录音权限移除不能被动到");
  const colors = read("android/app/src/main/res/values/colors.xml");
  const color = /<color name="notification_icon_color">(#[0-9A-Fa-f]{6})<\/color>/.exec(colors)?.[1];
  assert.ok(color, "缺少 notification_icon_color");
  const pluginColor = appJson.plugins.find((entry) => Array.isArray(entry) && entry[0] === "expo-notifications")[1].color;
  assert.equal(color.toLowerCase(), pluginColor.toLowerCase(), "原生工程的通知强调色须与 app.json 插件配置一致");
  for (const name of ["com.google.firebase.messaging.default_notification_color", "expo.modules.notifications.default_notification_color"]) {
    assert.match(manifest, new RegExp(`<meta-data android:name="${name.replaceAll(".", "\\.")}" android:resource="@color/notification_icon_color"\\s*/>`));
  }

  const rootGradle = read("android/build.gradle");
  assert.match(rootGradle, /classpath\('com\.google\.gms:google-services:\d+\.\d+\.\d+'\)/);

  const appGradle = read("android/app/build.gradle");
  const applies = [...appGradle.matchAll(/apply plugin: 'com\.google\.gms\.google-services'/g)];
  assert.equal(applies.length, 1);
  const guard = appGradle.indexOf("if (file('google-services.json').exists()) {");
  assert.ok(guard > 0 && guard < applies[0].index, "Google Services 插件只能在 google-services.json 存在时应用，否则没配置凭据的构建会直接失败");
  assert.equal(appGradle.indexOf("}", guard) > applies[0].index, true);
}

// ───────── 构建钩子与凭据不入库 ─────────
{
  const pkg = JSON.parse(read("package.json"));
  assert.equal(pkg.scripts["eas-build-post-install"], "node scripts/prepare-android-google-services.mjs");
  assert.equal(pkg.scripts["eas-build-pre-install"], "node scripts/check-ios-review-build-config.mjs", "既有的 iOS 审核构建前置校验保持不变");
  assert.match(read(".gitignore"), /^google-services\.json$/m, "凭据文件必须被忽略");
  const tracked = execFileSync("git", ["ls-files", "--", "."], { cwd: projectRoot, encoding: "utf8" }).split("\n");
  const leaked = tracked.filter((file) => /google-services\.json$|GoogleService-Info\.plist$/.test(file));
  assert.deepEqual(leaked, [], "仓库里不得跟踪 google-services.json");
  const eas = JSON.parse(read("eas.json"));
  assert.equal(JSON.stringify(eas).includes("GOOGLE_SERVICES_JSON"), false, "凭据变量在 EAS 环境里配置，不写进 eas.json");
}

console.log("check-push-app-config.test.mjs: ok");
