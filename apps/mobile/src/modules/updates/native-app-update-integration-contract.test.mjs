import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

const root = new URL("../../../", import.meta.url);
const [hookSource, coreSource, bridgeSource, configSource, layoutSource] = await Promise.all([
  readFile(new URL("src/modules/updates/use-automatic-native-app-update.ts", root), "utf8"),
  readFile(new URL("src/modules/updates/native-app-update.ts", root), "utf8"),
  readFile(new URL("modules/hb-app-installer/src/HBAppInstallerModule.ts", root), "utf8"),
  readFile(new URL("app.config.ts", root), "utf8"),
  readFile(new URL("app/_layout.tsx", root), "utf8"),
]);

test("Runtime 1.0.3 build 16 keeps the verified JS compatibility path", () => {
  assert.match(bridgeSource, /requireOptionalNativeModule/);
  assert.match(hookSource, /nativeInstaller\s*\? await nativeInstaller\.getDownloadDirectory\(\)/);
  assert.match(hookSource, /readAsStringAsync\(fileUri,[\s\S]*EncodingType\.Base64/);
  assert.match(hookSource, /toByteArray\(value\)/);
  assert.match(hookSource, /FileSystem\.moveAsync\(\{ from, to \}\)/);
  assert.match(coreSource, /function partFileUri/);
  assert.match(coreSource, /\$\{fileUri\}\.part/);
  assert.match(coreSource, /sha256\.create\(\)/);
  assert.match(coreSource, /\.verified\.json/);
});

test("build 17 uses native verification again when the user confirms install", () => {
  assert.match(hookSource, /result\.verification === "native" \? nativeInstaller : null/);
  assert.match(hookSource, /nativeInstaller\.installVerifiedApk\(\{/);
  for (const field of [
    "fileUri",
    "expectedSizeBytes",
    "expectedSha256Hex",
    "expectedPackageName",
    "expectedVersionCode",
    "expectedVersionName",
  ]) {
    assert.match(hookSource, new RegExp(`\\b${field}\\b`));
  }
  assert.match(coreSource, /installer\.verifyApk\(request\)/);
});

test("automatic update remains one operation and one prompt per build per process", () => {
  assert.match(hookSource, /if \(!options\.enabled \|\| inFlightRef\.current\)/);
  assert.match(hookSource, /inFlightRef\.current = true/);
  assert.match(hookSource, /inFlightRef\.current = false/);
  assert.match(hookSource, /promptedBuildIdRef\.current === result\.build\.easBuildId/);
  assert.match(hookSource, /promptedBuildIdRef\.current = result\.build\.easBuildId/);
});

test("APK status stays in the root layout alongside the router, behind the existing OTA gate", () => {
  assert.match(layoutSource, /const nativeAppUpdate = useAutomaticNativeAppUpdate/);
  assert.match(layoutSource, /<MobileOtaUpdateBoundary[\s\S]*<NativeAppUpdateStatus[\s\S]*<Stack/);
  assert.match(layoutSource, /phase=\{nativeAppUpdate\.phase\}/);
  assert.match(layoutSource, /onRetry=\{nativeAppUpdate\.retry\}/);
  assert.match(layoutSource, /onDismiss=\{nativeAppUpdate\.dismiss\}/);
});

test("native downloader trusts only configured HTTPS API and COS origins", () => {
  assert.match(hookSource, /url\.protocol === "https:"/);
  assert.doesNotMatch(hookSource, /build\.artifactUrl/);
  assert.match(configSource, /nativeAppInstallerTrustedOrigins/);
  assert.match(configSource, /https:\/\/hotbargain\.vip/);
  assert.match(configSource, /cos\.ap-singapore\.myqcloud\.com/);
});

test("automatic update has no mutable latest or EAS fallback path", () => {
  assert.doesNotMatch(hookSource, /checkLegacyNativeAppUpdate/);
  assert.doesNotMatch(hookSource, /getStableNativeAppDownloadUrl/);
  assert.doesNotMatch(coreSource, /android-latest\/download/);
  assert.doesNotMatch(coreSource, /getFallbackArtifactUrl/);
});
