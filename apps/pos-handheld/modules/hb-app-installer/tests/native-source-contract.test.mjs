import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

const moduleRoot = new URL("../", import.meta.url);
const [
  typesSource,
  bridgeSource,
  nativeSource,
  downloaderSource,
  signerPolicySource,
] = await Promise.all([
  readFile(new URL("src/HBAppInstaller.types.ts", moduleRoot), "utf8"),
  readFile(new URL("src/HBAppInstallerModule.ts", moduleRoot), "utf8"),
  readFile(
    new URL(
      "android/src/main/java/expo/modules/hbappinstaller/HBAppInstallerModule.kt",
      moduleRoot,
    ),
    "utf8",
  ),
  readFile(
    new URL(
      "android/src/main/java/expo/modules/hbappinstaller/HBAppInstallerDownloader.kt",
      moduleRoot,
    ),
    "utf8",
  ),
  readFile(
    new URL(
      "android/src/main/java/expo/modules/hbappinstaller/HBAppInstallerSignerPolicy.kt",
      moduleRoot,
    ),
    "utf8",
  ),
]);

const requiredMetadataFields = [
  "fileUri",
  "expectedSha256Hex",
  "expectedPackageName",
  "expectedVersionCode",
  "expectedVersionName",
  "expectedSigningCertificateSha256",
];

test("installVerifiedApk uses one strongly typed metadata object", () => {
  assert.match(typesSource, /export type InstallVerifiedApkRequest/);
  for (const field of requiredMetadataFields) {
    assert.match(typesSource, new RegExp(`\\b${field}\\b`));
    assert.match(nativeSource, new RegExp(`@Field var ${field}:`));
  }
  assert.match(
    bridgeSource,
    /installVerifiedApk\(\s*request: InstallVerifiedApkRequest,?\s*\)/,
  );
  assert.match(
    nativeSource,
    /AsyncFunction\("installVerifiedApk"\) \{ request: InstallVerifiedApkRequestRecord ->/,
  );
  assert.match(nativeSource, /AsyncFunction\("verifyDownloadedApk"\)/);
});

test("unknown-app-source permission has an explicit query and current-package settings contract", () => {
  assert.match(typesSource, /export type InstallPermissionStatus/);
  assert.match(typesSource, /granted/);
  assert.match(typesSource, /denied/);
  assert.match(bridgeSource, /getInstallPermissionStatus\(\): Promise<InstallPermissionStatus>/);
  assert.match(bridgeSource, /openInstallPermissionSettings\(\): Promise<void>/);
  assert.match(nativeSource, /AsyncFunction\("getInstallPermissionStatus"\)/);
  assert.match(nativeSource, /canRequestPackageInstalls\(\)/);
  assert.match(nativeSource, /AsyncFunction\("openInstallPermissionSettings"\)/);
  assert.match(nativeSource, /Settings\.ACTION_MANAGE_UNKNOWN_APP_SOURCES/);
  assert.match(nativeSource, /Uri\.parse\("package:\$\{context\.packageName\}"\)/);
  assert.match(nativeSource, /APP_INSTALL_PERMISSION_SETTINGS_UNAVAILABLE/);
});

test("all native file and install boundaries share the permission guard before side effects", () => {
  const getDirectoryBoundary = sourceBetween(
    nativeSource,
    'AsyncFunction("getDownloadDirectory")',
    'AsyncFunction("downloadApk")',
  );
  const downloadBoundary = sourceBetween(
    nativeSource,
    'AsyncFunction("downloadApk")',
    'AsyncFunction("removeDownloadedApk")',
  );
  const installBoundary = sourceBetween(
    nativeSource,
    'AsyncFunction("installVerifiedApk")',
    "private fun requireContext",
  );

  assert.match(
    nativeSource,
    /private fun requireInstallPermission\(context: Context\)/,
  );
  assert.doesNotMatch(getDirectoryBoundary, /requireInstallPermission/);
  assert.doesNotMatch(downloadBoundary, /requireInstallPermission/);
  assertAppearsBefore(
    installBoundary,
    "validateDownloadedApk(context, metadata)",
    "requireInstallPermission(context)",
  );
  assertAppearsBefore(
    installBoundary,
    "requireInstallPermission(context)",
    "FileProvider.getUriForFile(",
  );
  assert.doesNotMatch(
    installBoundary,
    /if \(!context\.packageManager\.canRequestPackageInstalls\(\)\)/,
  );
  assert.match(
    nativeSource,
    /private fun requireInstallPermission\(context: Context\)[\s\S]*?APP_INSTALL_PERMISSION_REQUIRED/,
  );
});

test("native validation consumes every server metadata field before launch", () => {
  assert.match(nativeSource, /validateSha256\(apk, metadata\.expectedSha256Hex\)/);
  assert.match(nativeSource, /metadata\.expectedPackageName != context\.packageName/);
  assert.match(nativeSource, /archiveInfo\.packageName != metadata\.expectedPackageName/);
  assert.match(nativeSource, /archiveInfo\.longVersionCode != metadata\.expectedVersionCode/);
  assert.match(nativeSource, /archiveInfo\.versionName != metadata\.expectedVersionName/);
  assert.match(nativeSource, /expectedSigningCertificateSha256 = metadata\.expectedSigningCertificateSha256/);
});

test("APK bytes are hashed as a stream and only app-owned file URIs reach FileProvider", () => {
  assert.match(nativeSource, /file\.inputStream\(\)\.buffered\(\)\.use/);
  assert.match(nativeSource, /while \(true\)/);
  assert.match(nativeSource, /uri\.scheme != "file"/);
  assert.match(nativeSource, /val parent = file\.parentFile/);
  assert.match(nativeSource, /parent !in allowedParents/);
  assert.match(nativeSource, /FileProvider\.getUriForFile/);
});

test("标准 file:/// URI 的空 authority 可通过，远端 file host 仍被拒绝", () => {
  assert.equal(
    nativeSource.match(/!uri\.authority\.isNullOrEmpty\(\)/gu)?.length,
    2,
  );
});

test("APK download is native, streaming, redirect-visible, and does not use Expo downloader", () => {
  assert.match(typesSource, /export type DownloadApkRequest/);
  assert.match(typesSource, /trustedOrigins/);
  assert.match(typesSource, /export type DownloadedApkResult/);
  assert.match(bridgeSource, /downloadApk\(/);
  assert.match(nativeSource, /AsyncFunction\("downloadApk"\)/);
  assert.match(downloaderSource, /instanceFollowRedirects = false/);
  assert.match(downloaderSource, /input\.read\(buffer\)/);
  assert.match(downloaderSource, /nextTotal > expectedSizeBytes/);
  assert.match(downloaderSource, /\.part/);
  assert.match(downloaderSource, /renameTo\(destination\)/);
  assert.match(downloaderSource, /connectTimeout = connectTimeoutMillis/);
  assert.match(downloaderSource, /readTimeout = readTimeoutMillis/);
});

test("signer policy distinguishes rotation history from exact multi-signer sets", () => {
  assert.match(signerPolicySource, /normalizeSigningCertificateSha256/);
  assert.match(signerPolicySource, /archive\.signingCertificateHistory/);
  assert.match(signerPolicySource, /installed\.currentSignerDigests != archive\.currentSignerDigests/);
  assert.match(signerPolicySource, /expected !in archive\.currentSignerDigests/);
  assert.match(nativeSource, /signingInfo\.apkContentsSigners/);
  assert.match(nativeSource, /signingInfo\.signingCertificateHistory/);
});

function sourceBetween(source, startMarker, endMarker) {
  const start = source.indexOf(startMarker);
  const end = source.indexOf(endMarker, start + startMarker.length);
  assert.notEqual(start, -1, `missing source marker: ${startMarker}`);
  assert.notEqual(end, -1, `missing source marker: ${endMarker}`);
  return source.slice(start, end);
}

function assertAppearsBefore(source, first, second) {
  const firstIndex = source.indexOf(first);
  const secondIndex = source.indexOf(second);
  assert.notEqual(firstIndex, -1, `missing ordered source: ${first}`);
  assert.notEqual(secondIndex, -1, `missing ordered source: ${second}`);
  assert.ok(firstIndex < secondIndex, `${first} must appear before ${second}`);
}
