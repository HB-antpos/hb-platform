import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

const root = new URL("../../../", import.meta.url);
const [otaHookSource, nativeHookSource, layoutSource, installerTypesSource, moduleSource] = await Promise.all([
  readFile(new URL("src/modules/updates/use-mobile-ota-update.ts", root), "utf8"),
  readFile(new URL("src/modules/updates/use-automatic-native-app-update.ts", root), "utf8"),
  readFile(new URL("app/_layout.tsx", root), "utf8"),
  readFile(new URL("modules/hb-app-installer/src/HBAppInstaller.types.ts", root), "utf8"),
  readFile(
    new URL("modules/hb-app-installer/android/src/main/java/expo/modules/hbappinstaller/HBAppInstallerModule.kt", root),
    "utf8",
  ),
]);

test("只有可选 OTA 受登录后空闲闸门约束，强制 OTA 与手动检查立即下载", () => {
  const gateCheck = otaHookSource.match(
    /if \(\s*decision\.state === "optional"\s*&& !manualCheckRef\.current\s*&& !backgroundDownloadGate\.isOpen\(\)\s*\) \{\s*deferredByIdleGateRef\.current = true;\s*return;\s*\}/,
  );
  assert.ok(gateCheck, "闸门判定必须同时限定 optional 与非手动检查");
  assert.ok(
    otaHookSource.indexOf(gateCheck[0]) < otaHookSource.indexOf("await downloadDecision(decision, isCurrent);"),
    "闸门判定必须在自动下载之前",
  );
  // 手动检查标记必须在 finally 中复位，否则一次失败的手动检查会让后续自动检查永远绕过闸门。
  assert.match(
    otaHookSource,
    /manualCheckRef\.current = true;\s*try \{\s*await runCheckRef\.current\(\);\s*\} finally \{\s*manualCheckRef\.current = false;\s*\}/,
  );
});

test("两个更新通道都订阅闸门，打开时重试被挡下的下载并在卸载时退订", () => {
  for (const [name, source] of [["OTA", otaHookSource], ["原生安装包", nativeHookSource]]) {
    assert.match(source, /backgroundDownloadGate\.subscribe\(/, `${name} 通道要订阅闸门`);
    assert.match(source, /deferredByIdleGateRef\.current = true;/, `${name} 通道要记下被挡下`);
  }
  assert.match(otaHookSource, /unsubscribeIdleGate\(\);/);
});

test("根布局先把闸门接到登录状态，再注册各更新通道", () => {
  const bindIndex = layoutSource.indexOf("backgroundDownloadGate.bind(useAuthStore.getState().isAuthenticated)");
  assert.ok(bindIndex > 0, "根布局要用当前登录状态接上闸门");
  assert.match(layoutSource, /backgroundDownloadGate\.setAuthenticated\(current\.isAuthenticated\)/);
  assert.match(layoutSource, /backgroundDownloadGate\.unbind\(\);/);
  // effect 按注册顺序执行：闸门必须先于两个更新通道的首次检查接上，否则首检会按「未接上 = 打开」放行。
  assert.ok(bindIndex < layoutSource.indexOf("useAutomaticNativeAppUpdate({"));
  assert.ok(bindIndex < layoutSource.indexOf("useMobileOtaUpdate({"));
});

test("带宽比例是可选字段，旧原生包忽略它不影响下载", () => {
  assert.match(installerTypesSource, /bandwidthShare\?: number;/);
  assert.match(moduleSource, /@Field\s+var bandwidthShare: Double\? = null/);
});
