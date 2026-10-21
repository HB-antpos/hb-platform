import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

const root = new URL("../../../", import.meta.url);
const [otaHook, nativeHook, iosHook, layout, settings, zh, en] = await Promise.all([
  readFile(new URL("src/modules/updates/use-mobile-ota-update.ts", root), "utf8"),
  readFile(new URL("src/modules/updates/use-automatic-native-app-update.ts", root), "utf8"),
  readFile(new URL("src/modules/updates/use-ios-native-app-update.ts", root), "utf8"),
  readFile(new URL("app/_layout.tsx", root), "utf8"),
  readFile(new URL("app/(shell)/settings.tsx", root), "utf8"),
  readFile(new URL("src/locales/zh/screens/settings.json", root), "utf8"),
  readFile(new URL("src/locales/en/screens/settings.json", root), "utf8"),
]);

test("根布局把自动更新开关接到三条更新通道，并先读盘再放行", () => {
  assert.match(layout, /hydrateAutoUpdatePreference\(\),/, "启动就绪任务必须读盘");
  assert.match(layout, /useAutoUpdatePreferenceStore\(isAutoUpdateAllowed\)/);
  for (const hook of ["useIosNativeAppUpdate", "useAutomaticNativeAppUpdate", "useMobileOtaUpdate"]) {
    const call = layout.slice(layout.indexOf(`${hook}({`));
    assert.match(call.slice(0, 400), /autoUpdate: autoUpdateAllowed/, `${hook} 必须收到开关`);
  }
  // 开关只管「可选更新」：不得并入 enabled，否则会连强制更新一起关掉。
  assert.doesNotMatch(layout, /automaticUpdatesEnabled\s*=[^;]*autoUpdate/s);
});

test("OTA：关闭时只放行强制更新和手动检查，可选更新不下载不提醒", () => {
  assert.match(
    otaHook,
    /!manualCheckRef\.current\s*&& !autoUpdateRef\.current\s*&& decision\.state !== "required"/,
  );
  assert.match(otaHook, /if \(!force && !autoUpdateRef\.current && decision\.state !== "required"\) return;/);
  assert.match(otaHook, /promptRestart\(current\.decision, true\)/, "手动检查必须强制提醒");
});

test("Android APK：关闭时在强制判定之后、可选下载之前返回", () => {
  const guard = nativeHook.indexOf("!requiredRef.current && options.autoUpdate === false");
  assert.ok(guard > nativeHook.indexOf("await refreshRequiredDecision("), "必须先刷新强制判定");
  assert.ok(guard < nativeHook.indexOf("backgroundDownloadGate.isOpen()"), "必须早于可选下载闸门");
  assert.match(nativeHook, /optionsRef\.current\.autoUpdate === false\) \{\s*return false;/, "可选安装提醒也要挡住");
});

test("iOS：关闭时可选 App Store 提醒保持排队不弹，强制拦截页不受影响", () => {
  assert.match(iosHook, /\|\| !autoUpdateRef\.current/);
  assert.match(iosHook, /autoUpdate\?: boolean/);
});

test("设置页提供自动更新开关，并在启动读盘", () => {
  assert.match(settings, /testID="settings-auto-update-switch"/);
  assert.match(settings, /useAutoUpdatePreferenceStore\(\(state\) => state\.setEnabled\)/);
  assert.match(settings, /hydrateAutoUpdatePreference\(\)/);
  for (const [name, source] of [["zh", zh], ["en", en]]) {
    const updates = JSON.parse(source).updates;
    for (const key of ["autoUpdate", "autoUpdateOnHint", "autoUpdateOffHint"]) {
      assert.ok(updates[key], `${name} 缺少 updates.${key}`);
    }
  }
});
