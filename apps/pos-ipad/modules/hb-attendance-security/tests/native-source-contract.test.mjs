import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import path from "node:path";
import test from "node:test";
import { fileURLToPath } from "node:url";

const moduleRoot = path.resolve(
  path.dirname(fileURLToPath(import.meta.url)),
  "..",
);

async function read(relativePath) {
  return readFile(path.join(moduleRoot, relativePath), "utf8");
}

test("Expo module keeps the Apple bridge while adding the Android bridge", async () => {
  const [moduleConfig, swiftModule] = await Promise.all([
    read("expo-module.config.json"),
    read("ios/HBAttendanceSecurityModule.swift"),
  ]);

  assert.match(moduleConfig, /"platforms":\s*\["apple",\s*"android"\]/);
  assert.match(moduleConfig, /"HBAttendanceSecurityModule"/);
  assert.match(
    moduleConfig,
    /"expo\.modules\.hbattendancesecurity\.HBAttendanceSecurityModule"/,
  );
  assert.match(swiftModule, /Name\("HBAttendanceSecurity"\)/);
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
      swiftModule,
      new RegExp(`AsyncFunction\\("${functionName}"\\)`),
    );
  }
  assert.match(
    swiftModule,
    /Function\("getSystemUptimeMilliseconds"\)/,
  );
  assert.match(
    swiftModule,
    /ProcessInfo\.processInfo\.systemUptime\s*\*\s*1_000/,
  );
  assert.doesNotMatch(
    swiftModule,
    /AsyncFunction\("getSystemUptimeMilliseconds"\)/,
  );
});

test("AES-256 attendance keys stay in a non-synchronizable ThisDeviceOnly Keychain item", async () => {
  const source = await read("ios/HBAttendanceSecurityKeychain.swift");

  assert.match(source, /kSecClassGenericPassword/);
  assert.match(source, /kSecAttrAccessibleWhenUnlockedThisDeviceOnly/);
  assert.match(
    source,
    /kSecAttrSynchronizable as String:\s*kCFBooleanFalse/,
  );
  assert.match(source, /SecRandomCopyBytes\(kSecRandomDefault,\s*32/);
  assert.match(source, /SecItemAdd/);
  assert.match(source, /SecItemCopyMatching/);
  assert.match(source, /SecItemDelete/);
  assert.match(source, /resetBytes\(in:/);
  assert.doesNotMatch(source, /UserDefaults|NSUbiquitousKeyValueStore|iCloud/);
});

test("attendance token codec matches HBATE1 AES-GCM wire format and emits only a QR image", async () => {
  const [qrSource, tokenSource] = await Promise.all([
    read("ios/HBAttendanceQrCodec.swift"),
    read("ios/HBAttendanceTokenCodec.swift"),
  ]);
  const source = `${tokenSource}\n${qrSource}`;

  assert.match(source, /static let tokenPrefix = "HBATE1"/);
  assert.match(source, /AES\.GCM\.seal/);
  assert.match(source, /authenticating:\s*aad/);
  assert.match(
    source,
    /Data\("\\\(tokenPrefix\)\.\\\(input\.kid\)"\.utf8\)/,
  );
  assert.match(source, /toDotNetGuidBytes/);
  assert.match(source, /issuedAtEpochMs\.littleEndian/);
  assert.match(source, /CIFilter\.qrCodeGenerator\(\)/);
  assert.match(source, /data:image\/png;base64,/);
  assert.match(source, /resetBytes\(in:/);
  assert.doesNotMatch(source, /\["token":\s*token\]/);
});

test("emergency verifier supports both WPF formats, P-256 SPKI fingerprints, and raw ES256 signatures", async () => {
  const source = await read("ios/HBEmergencyLoginVerifier.swift");

  assert.match(source, /legacyPrefix = "HBPOSE1-"/);
  assert.match(source, /v2Prefix = "HBPOSE2-"/);
  assert.match(source, /token\.count == 158/);
  assert.match(
    source,
    /P256\.Signing\.PublicKey\([\s\S]*pemRepresentation:/,
  );
  assert.match(source, /publicKey\.derRepresentation/);
  assert.match(source, /SHA256\.hash/);
  assert.match(
    source,
    /ECDSASignature\([\s\S]*rawRepresentation:/,
  );
  assert.match(source, /Data\("HBPOSE2-"\.utf8\)/);
  assert.match(source, /constantTimeEquals/);
  assert.match(source, /EMERGENCY_TOKEN_KEY_UNKNOWN/);
  assert.match(source, /EMERGENCY_TOKEN_SIGNATURE_INVALID/);
  assert.match(source, /EMERGENCY_TOKEN_WRONG_STORE/);
  assert.match(source, /EMERGENCY_TOKEN_NOT_ACTIVE/);
  assert.match(source, /EMERGENCY_TOKEN_EXPIRED/);
});

test("native module declares stable non-secret error codes", async () => {
  const source = await read("ios/HBAttendanceSecurityError.swift");

  for (const code of [
    "ATTENDANCE_SECURITY_INVALID_ARGUMENT",
    "ATTENDANCE_KEY_NOT_FOUND",
    "ATTENDANCE_KEYCHAIN_FAILURE",
    "ATTENDANCE_KEY_GENERATION_FAILED",
    "ATTENDANCE_TOKEN_GENERATION_FAILED",
    "ATTENDANCE_QR_RENDER_FAILED",
  ]) {
    assert.match(source, new RegExp(`"${code}"`));
  }
  assert.doesNotMatch(
    source,
    /keyMaterial|publicKeyPem|rawValue:\s*token/,
  );
});

test("Android keeps the same A256 bridge and wraps export-once HBATE1 key material", async () => {
  const [moduleSource, storageSource, tokenSource, errorSource] =
    await Promise.all([
      read(
        "android/src/main/java/expo/modules/hbattendancesecurity/HBAttendanceSecurityModule.kt",
      ),
      read(
        "android/src/main/java/expo/modules/hbattendancesecurity/HBAttendanceKeystore.kt",
      ),
      read(
        "android/src/main/java/expo/modules/hbattendancesecurity/HBAttendanceTokenCodec.kt",
      ),
      read(
        "android/src/main/java/expo/modules/hbattendancesecurity/HBAttendanceSecurityException.kt",
      ),
    ]);

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
  assert.match(moduleSource, /Function\("getSystemUptimeMilliseconds"\)/);
  assert.doesNotMatch(moduleSource, /createEs256Identity|HBATE2/);

  assert.match(storageSource, /AndroidKeyStore/);
  assert.match(storageSource, /AES\/GCM\/NoPadding/);
  assert.match(storageSource, /KeyProperties\.PURPOSE_ENCRYPT/);
  assert.match(storageSource, /KeyProperties\.PURPOSE_DECRYPT/);
  assert.match(storageSource, /ATTENDANCE_KEY_SIZE\s*=\s*32/);
  assert.match(storageSource, /ByteArray\(ATTENDANCE_KEY_SIZE\)/);
  assert.match(storageSource, /SharedPreferences/);
  assert.match(storageSource, /consumed/i);
  assert.match(storageSource, /fill\(0\)/);
  assert.doesNotMatch(storageSource, /wrappingKey\.encoded/);

  assert.match(tokenSource, /TOKEN_PREFIX\s*=\s*"HBATE1"/);
  assert.doesNotMatch(tokenSource, /HBATE2/);
  assert.match(tokenSource, /AES\/GCM\/NoPadding/);
  assert.match(tokenSource, /NONCE_SIZE\s*=\s*12/);
  assert.match(tokenSource, /ByteArray\(NONCE_SIZE\)/);
  assert.match(tokenSource, /ByteOrder\.LITTLE_ENDIAN/);
  assert.match(tokenSource, /toDotNetGuidBytes/);
  assert.match(tokenSource, /MAX_TOKEN_LENGTH\s*=\s*600/);
  assert.match(tokenSource, /ErrorCorrectionLevel\.Q/);
  assert.match(storageSource, /com\.hbweb\.posipad\.attendance\.a256\.wrap\./);

  for (const code of [
    "ATTENDANCE_SECURITY_INVALID_ARGUMENT",
    "ATTENDANCE_KEY_NOT_FOUND",
    "ATTENDANCE_KEYCHAIN_FAILURE",
    "ATTENDANCE_KEY_GENERATION_FAILED",
    "ATTENDANCE_TOKEN_GENERATION_FAILED",
    "ATTENDANCE_QR_RENDER_FAILED",
  ]) {
    assert.match(errorSource, new RegExp(`"${code}"`));
  }
  assert.doesNotMatch(
    errorSource,
    /ATTENDANCE_KEYSTORE_FAILURE|ATTENDANCE_REGISTRATION_EXPORT_CONSUMED/,
  );
});
