import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { join } from "node:path";
import test from "node:test";

import {
  ExpoAndroidApkDownloader,
  ExpoHbAppInstallerBridge,
  type HbAppInstallerNativeContract,
} from "./expo-android-native-update-port";

const FILE_URI =
  "file:///data/user/0/com.hbweb.poshandheld/cache/hb-app-updates/hb-pos-handheld-2.apk";

test("Expo bridge 受真实 HBAppInstaller 六项身份对象签名约束", async () => {
  const calls: unknown[] = [];
  const nativeModule = {
    async getInstallPermissionStatus() {
      return "granted" as const;
    },
    async openInstallPermissionSettings() {
      calls.push("open-install-permission-settings");
    },
    async getDownloadDirectory() {
      return "file:///data/user/0/com.hbweb.poshandheld/cache/hb-app-updates/";
    },
    async downloadApk(request) {
      calls.push(request);
      return {
        fileUri: request.destinationFileUri,
        sizeBytes: request.expectedSizeBytes,
        finalUrl: request.url,
      };
    },
    async removeDownloadedApk(fileUri) {
      calls.push(fileUri);
    },
    async installVerifiedApk(request) {
      calls.push(request);
      return {
        launched: true as const,
        packageName: "com.hbweb.poshandheld",
        versionCode: 200,
      };
    },
    async verifyDownloadedApk(request) {
      calls.push(request);
    },
  } satisfies HbAppInstallerNativeContract;
  const bridge = new ExpoHbAppInstallerBridge(async () => nativeModule);
  const request = {
    fileUri: FILE_URI,
    expectedSha256Hex: "a".repeat(64),
    expectedPackageName: "com.hbweb.poshandheld",
    expectedVersionCode: 200,
    expectedVersionName: "2.0.0",
    expectedSigningCertificateSha256: "b".repeat(64),
  };

  assert.equal(await bridge.getInstallPermissionStatus(), "granted");
  await bridge.openInstallPermissionSettings();
  assert.match(await bridge.getDownloadDirectory(), /hb-app-updates/u);
  assert.deepEqual(
    await bridge.downloadApk({
      url: "https://updates.example.test/build.apk",
      destinationFileUri: FILE_URI,
      expectedSizeBytes: 4,
      trustedOrigins: ["https://updates.example.test"],
    }),
    {
      fileUri: FILE_URI,
      sizeBytes: 4,
      finalUrl: "https://updates.example.test/build.apk",
    },
  );
  await bridge.removeDownloadedApk(FILE_URI);
  assert.deepEqual(
    await bridge.installVerifiedApk(request),
    {
      launched: true,
      packageName: "com.hbweb.poshandheld",
      versionCode: 200,
    },
  );
  assert.deepEqual(calls, [
    "open-install-permission-settings",
    {
      url: "https://updates.example.test/build.apk",
      destinationFileUri: FILE_URI,
      expectedSizeBytes: 4,
      trustedOrigins: ["https://updates.example.test"],
    },
    FILE_URI,
    request,
  ]);
});

test("Expo APK downloader 只调用一次受信 native 流式下载并透传签名 origins", async () => {
  const downloads: unknown[][] = [];
  const downloader = new ExpoAndroidApkDownloader({
    async downloadApk(request) {
      downloads.push([request]);
      return {
        fileUri: request.destinationFileUri,
        sizeBytes: 4,
        finalUrl: request.url,
      };
    },
    async removeDownloadedApk() {},
  });

  assert.deepEqual(
    await downloader.download({
      url: "https://updates.example.test/build.apk",
      destinationFileUri: FILE_URI,
      expectedSizeBytes: 4,
      maximumSizeBytes: 10,
      trustedOrigins: [
        "https://updates.example.test",
        "https://cdn.example.test",
      ],
    }),
    {
      fileUri: FILE_URI,
      sizeBytes: 4,
      finalUrl: "https://updates.example.test/build.apk",
    },
  );
  assert.deepEqual(downloads, [
    [
      {
        url: "https://updates.example.test/build.apk",
        destinationFileUri: FILE_URI,
        expectedSizeBytes: 4,
        trustedOrigins: [
          "https://updates.example.test",
          "https://cdn.example.test",
        ],
      },
    ],
  ]);
});

test("原生落盘后的 URI/size 不符时拒绝；下载层自身不重放", async (t) => {
  for (const [name, result] of [
    ["size mismatch", { fileUri: FILE_URI, sizeBytes: 5 }],
    [
      "destination mismatch",
      {
        fileUri:
          "file:///data/user/0/com.hbweb.poshandheld/cache/escaped.apk",
        sizeBytes: 4,
      },
    ],
  ] as const) {
    await t.test(name, async () => {
      let downloads = 0;
      const downloader = new ExpoAndroidApkDownloader({
        async downloadApk() {
          downloads += 1;
          return {
            ...result,
            finalUrl: "https://updates.example.test/build.apk",
          };
        },
        async removeDownloadedApk() {},
      });
      await assert.rejects(
        () =>
          downloader.download({
            url: "https://updates.example.test/build.apk",
            destinationFileUri: FILE_URI,
            expectedSizeBytes: 4,
            maximumSizeBytes: 10,
            trustedOrigins: ["https://updates.example.test"],
          }),
        /size|destination/i,
      );
      assert.equal(downloads, 1);
    });
  }
});

test("原生返回的最终 redirect URL 必须仍在签名 trusted origins 内", async () => {
  const downloader = new ExpoAndroidApkDownloader({
    async downloadApk(request) {
      return {
        fileUri: request.destinationFileUri,
        sizeBytes: request.expectedSizeBytes,
        finalUrl: "https://attacker.example/fake.apk",
      };
    },
    async removeDownloadedApk() {},
  });

  await assert.rejects(
    () =>
      downloader.download({
        url: "https://updates.example.test/build.apk",
        destinationFileUri: FILE_URI,
        expectedSizeBytes: 4,
        maximumSizeBytes: 10,
        trustedOrigins: ["https://updates.example.test"],
      }),
    /final URL|trusted/i,
  );
});

test("生产 downloader 不再调用 Expo 下载器，真实下载与 redirect 校验均进入 native", () => {
  const source = readFileSync(
    join(
      process.cwd(),
      "src/features/app-updates/expo-android-native-update-port.ts",
    ),
    "utf8",
  );
  assert.doesNotMatch(source, /\.arrayBuffer\s*\(/u);
  assert.doesNotMatch(source, /\.bytes(?:Sync)?\s*\(/u);
  assert.doesNotMatch(source, /downloadFileAsync/u);
  assert.doesNotMatch(source, /expo-file-system/u);
  assert.match(source, /\.downloadApk\(/u);
  assert.match(source, /trustedOrigins/u);
});

test("生产组合根只从签名构建 extra 注入 APK origins，且不保留 URL opener", () => {
  const source = readFileSync(
    join(process.cwd(), "src/core/runtime/expo-pos-runtime.ts"),
    "utf8",
  );
  assert.match(source, /createExpoAndroidNativeUpdatePort/u);
  assert.match(
    source,
    /trustedDownloadOrigins:\s*publicExtra\?\.hbpos\?\.trustedApkOrigins\s*\?\?\s*\[\]/u,
  );
  assert.doesNotMatch(source, /androidApk\s*:/u);
  assert.doesNotMatch(source, /Linking\.openURL\([^)]*downloadUrl/u);
});
