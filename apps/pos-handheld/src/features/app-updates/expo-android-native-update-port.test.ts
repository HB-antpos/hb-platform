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

test("下载前先订阅原生进度，只转发本目标事件，结束后无论成败都退订", async (t) => {
  for (const outcome of ["success", "failure"] as const) {
    await t.test(outcome, async () => {
      const order: string[] = [];
      const progress: unknown[] = [];
      let emit: ((bytesWritten: number) => void) | null = null;
      let subscribedUri: string | null = null;
      const downloader = new ExpoAndroidApkDownloader({
        async subscribeDownloadProgress(destinationFileUri, listener) {
          order.push("subscribe");
          subscribedUri = destinationFileUri;
          emit = listener;
          return () => {
            order.push("unsubscribe");
            emit = null;
          };
        },
        async downloadApk(request) {
          order.push("download");
          emit?.(0);
          emit?.(1);
          emit?.(1);
          emit?.(4);
          if (outcome === "failure") throw new Error("network failed");
          return {
            fileUri: request.destinationFileUri,
            sizeBytes: 4,
            finalUrl: request.url,
          };
        },
        async removeDownloadedApk() {},
      });

      const run = downloader.download({
        url: "https://updates.example.test/build.apk",
        destinationFileUri: FILE_URI,
        expectedSizeBytes: 4,
        maximumSizeBytes: 10,
        trustedOrigins: ["https://updates.example.test"],
        onProgress: (value) => progress.push(value),
      });
      if (outcome === "failure") {
        await assert.rejects(run, /network failed/u);
      } else {
        await run;
      }

      assert.equal(subscribedUri, FILE_URI);
      assert.deepEqual(order, ["subscribe", "download", "unsubscribe"]);
      // 重复的 25% 被百分比节流掉；总大小取已验证的 expectedSizeBytes。
      assert.deepEqual(progress, [
        { bytesWritten: 0, totalBytes: 4 },
        { bytesWritten: 1, totalBytes: 4 },
        { bytesWritten: 4, totalBytes: 4 },
      ]);
    });
  }
});

test("没有进度回调或订阅失败时照常下载", async () => {
  let downloads = 0;
  const downloader = new ExpoAndroidApkDownloader({
    async subscribeDownloadProgress() {
      throw new Error("native module unavailable");
    },
    async downloadApk(request) {
      downloads += 1;
      return {
        fileUri: request.destinationFileUri,
        sizeBytes: 4,
        finalUrl: request.url,
      };
    },
    async removeDownloadedApk() {},
  });
  const input = {
    url: "https://updates.example.test/build.apk",
    destinationFileUri: FILE_URI,
    expectedSizeBytes: 4,
    maximumSizeBytes: 10,
    trustedOrigins: ["https://updates.example.test"],
  };

  await downloader.download(input);
  await downloader.download({ ...input, onProgress: () => undefined });
  assert.equal(downloads, 2);
});

test("Expo bridge 进度订阅按目标 URI 过滤；旧原生包没有 addListener 时返回空退订", async () => {
  const received: number[] = [];
  // 回调里赋值的 let 会被外层控制流收窄为 null，用对象持有监听器引用。
  const native: { listener: ((event: unknown) => void) | null } = { listener: null };
  let removed = 0;
  const withEvents = new ExpoHbAppInstallerBridge(async () => ({
    addListener(_eventName: "onDownloadProgress", listener: (event: unknown) => void) {
      native.listener = listener;
      return { remove: () => { removed += 1; } };
    },
  }) as unknown as HbAppInstallerNativeContract);

  const stop = await withEvents.subscribeDownloadProgress(FILE_URI, (bytes) => received.push(bytes));
  native.listener?.({ destinationFileUri: FILE_URI, bytesWritten: 2, totalBytes: 4 });
  native.listener?.({ destinationFileUri: `${FILE_URI}.other`, bytesWritten: 3, totalBytes: 4 });
  native.listener?.({ destinationFileUri: FILE_URI, bytesWritten: "4", totalBytes: 4 });
  stop();
  assert.deepEqual(received, [2]);
  assert.equal(removed, 1);

  const legacy = new ExpoHbAppInstallerBridge(
    async () => ({}) as unknown as HbAppInstallerNativeContract,
  );
  const stopLegacy = await legacy.subscribeDownloadProgress(FILE_URI, () => undefined);
  assert.doesNotThrow(stopLegacy);
});
