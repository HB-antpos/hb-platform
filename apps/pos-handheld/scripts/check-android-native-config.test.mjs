import assert from "node:assert/strict";
import { execFile } from "node:child_process";
import { readFile } from "node:fs/promises";
import { createRequire } from "node:module";
import path from "node:path";
import test from "node:test";
import { fileURLToPath } from "node:url";
import { promisify } from "node:util";

const appRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const expoCli = createRequire(import.meta.url).resolve("expo/bin/cli");
const execFileAsync = promisify(execFile);

async function read(relativePath) {
  return readFile(path.join(appRoot, relativePath), "utf8");
}

async function readJson(relativePath) {
  return JSON.parse(await read(relativePath));
}

function createCleanExpoEnvironment() {
  return Object.fromEntries(
    Object.entries(process.env).filter(
      ([name]) => !/^(?:EAS_|EXPO_PUBLIC_HBPOS_)/u.test(name),
    ),
  );
}

let androidModResultsPromise;

async function resolveAndroidModResults() {
  // 在内存中执行 config plugin，避免依赖或写入被忽略的原生工程目录。
  androidModResultsPromise ??= execFileAsync(
    expoCli,
    ["config", "--type", "introspect", "--json"],
    {
      cwd: appRoot,
      env: createCleanExpoEnvironment(),
      maxBuffer: 10 * 1024 * 1024,
    },
  ).then(({ stdout }) => JSON.parse(stdout)?._internal?.modResults?.android);

  const modResults = await androidModResultsPromise;
  assert.ok(modResults, "Expo introspection 未返回 Android modResults");
  return modResults;
}

async function resolveAndroidManifest() {
  const manifest = (await resolveAndroidModResults())?.manifest?.manifest;

  assert.ok(manifest, "Expo introspection 未返回 Android manifest");
  return manifest;
}

async function resolveAndroidStyles() {
  const styles = (await resolveAndroidModResults())?.styles;

  assert.ok(styles, "Expo introspection 未返回 Android styles");
  return styles;
}

test("Expo introspection 生成的 AndroidManifest 全局禁用系统与云备份", async () => {
  const [manifest, appConfig] = await Promise.all([
    resolveAndroidManifest(),
    read("app.config.ts"),
  ]);
  const [application] = manifest.application ?? [];

  assert.ok(application, "Expo introspection 未返回 application 节点");
  assert.equal(application.$?.["android:allowBackup"], "false");
  assert.doesNotMatch(appConfig, /configureAndroidBackup/);
});

test("Android 30 生成配置满足通知、相机与启动样式 lint 契约", async () => {
  const [manifest, styles] = await Promise.all([
    resolveAndroidManifest(),
    resolveAndroidStyles(),
  ]);
  const permissions = manifest["uses-permission"] ?? [];
  const cameraFeatures = (manifest["uses-feature"] ?? []).filter(
    (entry) => entry?.$?.["android:name"] === "android.hardware.camera",
  );
  const splashStyle = (styles.resources?.style ?? []).find(
    (entry) => entry?.$?.name === "Theme.App.SplashScreen",
  );
  const splashBehavior = (splashStyle?.item ?? []).find(
    (entry) =>
      entry?.$?.name === "android:windowSplashScreenBehavior",
  );

  assert.deepEqual(
    {
      postNotificationsPermissionCount: permissions.filter(
        (entry) =>
          entry?.$?.["android:name"] ===
          "android.permission.POST_NOTIFICATIONS",
      ).length,
      cameraFeatureCount: cameraFeatures.length,
      cameraRequired: cameraFeatures[0]?.$?.["android:required"],
      splashTargetApi: splashBehavior?.$?.["tools:targetApi"],
    },
    {
      postNotificationsPermissionCount: 1,
      cameraFeatureCount: 1,
      cameraRequired: "false",
      splashTargetApi: "33",
    },
  );
});

test("printer and attendance Expo modules autolink Apple and Android without ReactPackage", async () => {
  const [printerConfig, attendanceConfig, printerSource, attendanceSource] =
    await Promise.all([
      readJson("modules/hb-printer/expo-module.config.json"),
      readJson("modules/hb-attendance-security/expo-module.config.json"),
      read(
        "modules/hb-printer/android/src/main/java/expo/modules/hbprinter/HbPrinterModule.kt",
      ),
      read(
        "modules/hb-attendance-security/android/src/main/java/expo/modules/hbattendancesecurity/HBAttendanceSecurityModule.kt",
      ),
    ]);

  assert.deepEqual(new Set(printerConfig.platforms), new Set(["apple", "android"]));
  assert.deepEqual(printerConfig.apple?.modules, ["HbPrinterModule"]);
  assert.deepEqual(printerConfig.android?.modules, [
    "expo.modules.hbprinter.HbPrinterModule",
  ]);
  assert.deepEqual(
    new Set(attendanceConfig.platforms),
    new Set(["apple", "android"]),
  );
  assert.deepEqual(attendanceConfig.apple?.modules, [
    "HBAttendanceSecurityModule",
  ]);
  assert.deepEqual(attendanceConfig.android?.modules, [
    "expo.modules.hbattendancesecurity.HBAttendanceSecurityModule",
  ]);

  const nativeSources = `${printerSource}\n${attendanceSource}`;
  assert.doesNotMatch(nativeSources, /ReactPackage|ReactContextBaseJavaModule/);
  assert.match(printerSource, /Name\("HbPrinter"\)/);
  assert.match(attendanceSource, /Name\("HBAttendanceSecurity"\)/);
});

test("Android printer keeps BLE and paired SPP as explicit opaque transports", async () => {
  const source = await read(
    "modules/hb-printer/android/src/main/java/expo/modules/hbprinter/HbPrinterModule.kt",
  );

  for (const functionName of [
    "getStatus",
    "scan",
    "connect",
    "disconnect",
    "write",
    "printText",
    "openCashDrawer",
  ]) {
    assert.match(source, new RegExp(`AsyncFunction\\(\"${functionName}\"\\)`));
  }
  assert.match(source, /"ble:"/);
  assert.match(source, /"spp:"/);
  assert.match(source, /BluetoothLeScanner|bluetoothLeScanner/);
  assert.match(source, /startDiscovery\(\)/);
  assert.match(source, /bondedDevices/);
  assert.match(source, /BluetoothDevice\.BOND_BONDED/);
  assert.match(source, /PRINTER_SPP_PAIRING_REQUIRED/);
  assert.match(source, /createRfcommSocketToServiceRecord/);
  assert.match(source, /BluetoothDevice\.TRANSPORT_LE/);
  // API 30 及以下扫描前必须检查定位总开关，否则 BLE 扫描静默返回空列表。
  assert.match(
    source,
    /private fun startScan[\s\S]*?requireBluetoothReady[\s\S]*?isScanLocationServiceReady\(\)[\s\S]*?PRINTER_BLUETOOTH_LOCATION_OFF[\s\S]*?scanPromise = promise/,
  );
  assert.match(
    source,
    /fun isScanLocationServiceReady\(\)[\s\S]*?VERSION_CODES\.S\) return true[\s\S]*?isLocationEnabled/,
  );
  assert.doesNotMatch(source, /createBond\s*\(/);
  assert.doesNotMatch(source, /createInsecureRfcommSocket/);
  assert.doesNotMatch(source, /ReactPackage|WritableMap|ReadableMap/);
  assert.doesNotMatch(source, /buildProductLabel|Bitmap|Canvas|ZXing/);
});

test("Android printer drops a ready SPP session when its ACL link or the adapter goes away", async () => {
  const source = await read(
    "modules/hb-printer/android/src/main/java/expo/modules/hbprinter/HbPrinterModule.kt",
  );

  // SPP socket.isConnected 只看本地状态；不听 ACL 断开，打印机重启后第一张小票会写进死连接。
  assert.match(source, /BluetoothDevice\.ACTION_ACL_DISCONNECTED/);
  assert.match(source, /BluetoothAdapter\.ACTION_STATE_CHANGED/);
  assert.match(
    source,
    /private fun startConnect[\s\S]*?ensureLinkStateReceiverRegistered\(\)[\s\S]*?connectPromise = promise/,
  );
  assert.match(
    source,
    /private fun handleSppLinkLost[\s\S]*?connectedTransport != TransportKind\.SPP[\s\S]*?token\.address != address[\s\S]*?finishPendingOperation\([\s\S]*?"unknown"[\s\S]*?clearConnectedTransport\(\)[\s\S]*?connectionState = "disconnected"/,
  );
  assert.match(
    source,
    /private fun handleBluetoothTurnedOff[\s\S]*?failConnect\([\s\S]*?clearConnectedTransport\(\)[\s\S]*?connectionState = "disconnected"/,
  );
  assert.match(source, /OnDestroy[\s\S]*?unregisterLinkStateReceiver\(\)/);
  assert.match(source, /registerReceiver\(linkStateReceiver, filter, Context\.RECEIVER_NOT_EXPORTED\)/);
});

test("printer operation ids are exclusive and uncertain writes never switch or replay transports", async () => {
  const source = await read(
    "modules/hb-printer/android/src/main/java/expo/modules/hbprinter/HbPrinterModule.kt",
  );

  assert.match(source, /PRINTER_OPERATION_ID_REQUIRED/);
  assert.match(source, /PRINTER_OPERATION_IN_PROGRESS/);
  assert.match(source, /PRINTER_OPERATION_ALREADY_USED/);
  assert.match(source, /operationTransportById/);
  assert.match(source, /connectedTransport/);
  assert.match(source, /state\s*=\s*"unknown"|resultPayload\([^)]*"unknown"/s);
  assert.match(source, /无法确认|unknown/);
  assert.doesNotMatch(source, /fallback|retryWrite|resend/i);
});

test("Android attendance wraps an export-once A256 key and emits only interoperable HBATE1 QR data", async () => {
  const [
    moduleSource,
    keystoreSource,
    tokenSource,
    verifierSource,
    exceptionSource,
  ] =
    await Promise.all([
      read(
        "modules/hb-attendance-security/android/src/main/java/expo/modules/hbattendancesecurity/HBAttendanceSecurityModule.kt",
      ),
      read(
        "modules/hb-attendance-security/android/src/main/java/expo/modules/hbattendancesecurity/HBAttendanceKeystore.kt",
      ),
      read(
        "modules/hb-attendance-security/android/src/main/java/expo/modules/hbattendancesecurity/HBAttendanceTokenCodec.kt",
      ),
      read(
        "modules/hb-attendance-security/android/src/main/java/expo/modules/hbattendancesecurity/HBEmergencyLoginVerifier.kt",
      ),
      read(
        "modules/hb-attendance-security/android/src/main/java/expo/modules/hbattendancesecurity/HBAttendanceSecurityException.kt",
      ),
    ]);
  const source = `${moduleSource}\n${keystoreSource}\n${tokenSource}\n${verifierSource}\n${exceptionSource}`;

  assert.match(keystoreSource, /AndroidKeyStore/);
  assert.match(keystoreSource, /KeyGenParameterSpec/);
  assert.match(keystoreSource, /KeyProperties\.PURPOSE_ENCRYPT/);
  assert.match(keystoreSource, /KeyProperties\.PURPOSE_DECRYPT/);
  assert.match(keystoreSource, /AES\/GCM\/NoPadding/);
  assert.match(keystoreSource, /SecureRandom/);
  assert.match(keystoreSource, /ATTENDANCE_KEY_SIZE\s*=\s*32/);
  assert.match(keystoreSource, /ByteArray\(ATTENDANCE_KEY_SIZE\)/);
  assert.match(keystoreSource, /encrypted|ciphertext/i);
  assert.match(keystoreSource, /nonce|iv/i);
  assert.match(keystoreSource, /SharedPreferences/);
  assert.match(keystoreSource, /consumed/i);
  assert.match(keystoreSource, /fill\(0\)/);
  assert.doesNotMatch(source, /wrappingKey\.encoded|PRIVATE KEY-----/);

  assert.match(moduleSource, /Function\("getSystemUptimeMilliseconds"\)/);
  assert.match(moduleSource, /SystemClock\.elapsedRealtime\(\)/);
  for (const functionName of [
    "createA256Identity",
    "hasA256Key",
    "readRegistrationKeyMaterial",
    "issueAttendanceQr",
    "destroyA256Key",
    "validateEs256P256PublicKey",
    "verifyEs256P256Token",
  ]) {
    assert.match(
      moduleSource,
      new RegExp(`AsyncFunction\\("${functionName}"\\)`),
    );
  }
  assert.doesNotMatch(
    moduleSource,
    /createEs256Identity|hasEs256Key|readRegistrationPublicKey|destroyEs256Key/,
  );

  assert.match(tokenSource, /HBATE1/);
  assert.doesNotMatch(tokenSource, /HBATE2/);
  assert.match(tokenSource, /AES\/GCM\/NoPadding/);
  assert.match(tokenSource, /NONCE_SIZE\s*=\s*12/);
  assert.match(tokenSource, /ByteArray\(NONCE_SIZE\)/);
  assert.match(tokenSource, /HBATE1\.\$\{input\.kid\}|TOKEN_PREFIX.*input\.kid/s);
  assert.match(tokenSource, /ByteOrder\.LITTLE_ENDIAN/);
  assert.match(tokenSource, /swapAt|toDotNetGuidBytes/);
  assert.match(tokenSource, /storeBytes\.size\.toByte\(\)/);
  assert.match(tokenSource, /deviceBytes\.size\.toByte\(\)/);
  assert.match(tokenSource, /MAX_TOKEN_LENGTH\s*=\s*600/);
  assert.match(tokenSource, /tokenId/);

  assert.match(verifierSource, /HBPOSE1-/);
  assert.match(verifierSource, /HBPOSE2-/);
  assert.match(verifierSource, /EMERGENCY_TOKEN_WRONG_STORE/);
  assert.match(verifierSource, /EMERGENCY_TOKEN_NOT_ACTIVE/);
  assert.match(verifierSource, /EMERGENCY_TOKEN_EXPIRED/);

  for (const code of [
    "ATTENDANCE_SECURITY_INVALID_ARGUMENT",
    "ATTENDANCE_KEY_NOT_FOUND",
    "ATTENDANCE_KEYCHAIN_FAILURE",
    "ATTENDANCE_KEY_GENERATION_FAILED",
    "ATTENDANCE_TOKEN_GENERATION_FAILED",
    "ATTENDANCE_QR_RENDER_FAILED",
  ]) {
    assert.match(exceptionSource, new RegExp(`"${code}"`));
  }
  assert.doesNotMatch(
    exceptionSource,
    /ATTENDANCE_KEYSTORE_FAILURE|ATTENDANCE_REGISTRATION_EXPORT_CONSUMED/,
  );
});

test("APK installer accepts only verified app-owned local APKs and delegates to FileProvider", async () => {
  const [config, source, manifest, paths] = await Promise.all([
    readJson("modules/hb-app-installer/expo-module.config.json"),
    read(
      "modules/hb-app-installer/android/src/main/java/expo/modules/hbappinstaller/HBAppInstallerModule.kt",
    ),
    read("modules/hb-app-installer/android/src/main/AndroidManifest.xml"),
    read("modules/hb-app-installer/android/src/main/res/xml/hb_app_installer_paths.xml"),
  ]);

  assert.deepEqual(config.platforms, ["android"]);
  assert.deepEqual(config.android?.modules, [
    "expo.modules.hbappinstaller.HBAppInstallerModule",
  ]);
  assert.match(source, /Name\("HBAppInstaller"\)/);
  assert.match(source, /AsyncFunction\("getDownloadDirectory"\)/);
  assert.match(source, /AsyncFunction\("installVerifiedApk"\)/);
  assert.match(source, /scheme\s*!=\s*"file"|scheme\s*==\s*"file"/);
  assert.match(source, /canonicalFile/);
  assert.match(source, /MessageDigest\.getInstance\("SHA-256"\)/);
  assert.match(source, /getPackageArchiveInfo/);
  assert.match(source, /archiveInfo\.packageName\s*!=\s*context\.packageName/);
  assert.match(source, /FileProvider\.getUriForFile/);
  assert.match(source, /Intent\.ACTION_VIEW/);
  assert.match(source, /FLAG_GRANT_READ_URI_PERMISSION/);
  assert.doesNotMatch(source, /https?:|ACTION_INSTALL_PACKAGE/);

  assert.match(manifest, /android\.permission\.REQUEST_INSTALL_PACKAGES/);
  assert.match(manifest, /androidx\.core\.content\.FileProvider/);
  assert.match(manifest, /android:exported="false"/);
  assert.match(manifest, /android:grantUriPermissions="true"/);
  assert.doesNotMatch(paths, /root-path|external-path/);
  assert.match(paths, /cache-path/);
  assert.match(paths, /files-path/);
});

test("permission plugin encodes the API 30 and API 31+ Bluetooth matrix only", async () => {
  const [plugin, installerManifest] = await Promise.all([
    read("plugins/with-hb-printer.js"),
    read("modules/hb-app-installer/android/src/main/AndroidManifest.xml"),
  ]);

  assert.match(plugin, /withAndroidManifest/);
  for (const permission of [
    "android.permission.BLUETOOTH",
    "android.permission.BLUETOOTH_ADMIN",
    "android.permission.ACCESS_FINE_LOCATION",
    "android.permission.BLUETOOTH_SCAN",
    "android.permission.BLUETOOTH_CONNECT",
  ]) {
    assert.match(plugin, new RegExp(permission.replaceAll(".", "\\.")));
  }
  assert.match(plugin, /android:maxSdkVersion/);
  assert.doesNotMatch(
    plugin,
    /BLUETOOTH_ADVERTISE|ACCESS_BACKGROUND_LOCATION|FOREGROUND_SERVICE|bluetooth-central/,
  );
  assert.doesNotMatch(plugin, /REQUEST_INSTALL_PACKAGES/);
  assert.match(installerManifest, /REQUEST_INSTALL_PACKAGES/);
});
