import assert from "node:assert/strict";
import test from "node:test";

import {
  AndroidNativeUpdateAdapter,
  type AndroidApkDownloadPort,
  type AndroidAppInstallerPort,
} from "./android-native-update-adapter";

import type { PosHandheldUpdatePolicy } from "@/core/contracts/app-updates";

const SHA256 = "a".repeat(64);
const SIGNING_SHA256 = "b".repeat(64);
const PACKAGE_NAME = "com.hbweb.poshandheld";
const DOWNLOAD_URL =
  "https://updates.example.test/pos-handheld/builds/200.apk";
const DOWNLOAD_DIRECTORY =
  "file:///data/user/0/com.hbweb.poshandheld/cache/hb-app-updates/";

const androidDecision: PosHandheldUpdatePolicy = Object.freeze({
  enabled: true,
  state: "required",
  policyVersion: "android-policy-200",
  platform: "Android",
  required: true,
  latestVersion: "2.0.0",
  latestBuild: "200",
  minimumSupportedVersion: "1.5.0",
  distribution: "apk",
  downloadUrl: DOWNLOAD_URL,
  fileSize: 2_048,
  sha256: SHA256,
  packageName: PACKAGE_NAME,
  signingCertificateSha256: SIGNING_SHA256,
  bundleIdentifier: null,
  appStoreId: null,
  releaseMessage: "Security update",
});

const iosDecision: PosHandheldUpdatePolicy = Object.freeze({
  enabled: true,
  state: "optional",
  policyVersion: "ios-policy-300",
  platform: "iOS",
  required: false,
  latestVersion: "3.0.0",
  latestBuild: "300",
  minimumSupportedVersion: "2.0.0",
  distribution: "testflight",
  downloadUrl: "https://testflight.apple.com/join/AbCdEf12",
  fileSize: null,
  sha256: null,
  packageName: null,
  signingCertificateSha256: null,
  bundleIdentifier: PACKAGE_NAME,
  appStoreId: "1234567890",
  releaseMessage: null,
});

test("Android 成功路径只下载一次，并把后端六项安装身份完整传给 native", async () => {
  const harness = createHarness();

  assert.deepEqual(await harness.adapter.install(androidDecision), {
    launched: true,
    packageName: PACKAGE_NAME,
    versionCode: 200,
  });
  assert.deepEqual(harness.events, [
    "directory",
    "download",
    "verify",
    "permission",
    "install",
  ]);
  assert.equal(harness.downloads.length, 1);
  assert.equal(harness.downloads[0]?.url, DOWNLOAD_URL);
  assert.equal(harness.downloads[0]?.expectedSizeBytes, 2_048);
  assert.deepEqual(harness.downloads[0]?.trustedOrigins, [
    "https://updates.example.test",
  ]);
  assert.match(
    harness.downloads[0]?.destinationFileUri ?? "",
    /^file:\/\/\/data\/user\/0\/com\.hbweb\.poshandheld\/cache\/hb-app-updates\/hb-pos-handheld-200-[a-f0-9]{12}\.apk$/u,
  );
  assert.deepEqual(harness.installs, [
    [{
      fileUri: harness.downloads[0]?.destinationFileUri,
      expectedSha256Hex: SHA256,
      expectedPackageName: PACKAGE_NAME,
      expectedVersionCode: 200,
      expectedVersionName: "2.0.0",
      expectedSigningCertificateSha256: SIGNING_SHA256,
    }],
  ]);
  assert.deepEqual(harness.removals, []);
});

test("prepare 后复用已校验 APK；策略身份变化会重新下载且不查询安装权限", async () => {
  const harness = createHarness();

  await harness.adapter.prepare(androidDecision);
  await harness.adapter.prepare(androidDecision);
  assert.equal(harness.downloads.length, 1);
  assert.deepEqual(harness.events, ["directory", "download", "verify"]);

  await harness.adapter.prepare({
    ...androidDecision,
    policyVersion: "android-policy-201",
    latestBuild: "201",
    latestVersion: "2.0.1",
    sha256: "c".repeat(64),
  });
  assert.equal(harness.downloads.length, 2);
  assert.equal(harness.events.filter((event) => event === "permission").length, 0);
});

test("已有有效 APK 时冷启动 prepare 不联网", async () => {
  const harness = createHarness({ existingPreparedFile: true });
  await harness.adapter.prepare(androidDecision);
  assert.equal(harness.downloads.length, 0);
  assert.deepEqual(harness.events, ["directory", "verify"]);
});

test("不同策略并发 prepare 串行下载并各自保留最终目标", async () => {
  let release!: () => void;
  const gate = new Promise<void>((resolve) => {
    release = resolve;
  });
  const harness = createHarness({ downloadGate: gate });
  const secondDecision = { ...androidDecision, latestBuild: "201", latestVersion: "2.0.1", sha256: "c".repeat(64) };
  const first = harness.adapter.prepare(androidDecision);
  while (harness.downloads.length === 0) await Promise.resolve();
  const second = harness.adapter.prepare(secondDecision);
  assert.equal(harness.downloads.length, 1);
  release();
  await Promise.all([first, second]);
  assert.equal(harness.downloads.length, 2);
});

test("prepare 失败后允许显式重试同一策略", async () => {
  const harness = createHarness({ downloadErrors: [new Error("network failed")] });
  await assert.rejects(() => harness.adapter.prepare(androidDecision), /network failed/u);
  await harness.adapter.prepare(androidDecision);
  assert.equal(harness.downloads.length, 2);
});

test("下载大小不符时 fail closed、删除临时文件且不调用 installer", async () => {
  const harness = createHarness({ downloadedSizeBytes: 2_047 });
  await assert.rejects(
    () => harness.adapter.install(androidDecision),
    /size/i,
  );
  assert.equal(harness.downloads.length, 1);
  assert.equal(harness.installs.length, 0);
  assert.deepEqual(harness.removals, [
    harness.downloads[0]?.destinationFileUri,
  ]);
});

test("未知来源安装未授权时仍可完成后台下载，点击安装时才拒绝", async () => {
  const harness = createHarness({ installPermissionGranted: false });

  await assert.rejects(
    () => harness.adapter.install(androidDecision),
    (error: unknown) =>
      error instanceof Error &&
      (error as Error & { code?: string }).code ===
        "APP_INSTALL_PERMISSION_REQUIRED",
  );

  assert.deepEqual(harness.events, ["directory", "download", "verify", "permission"]);
  assert.equal(harness.downloads.length, 1);
  assert.equal(harness.installs.length, 0);
  assert.deepEqual(harness.removals, []);
});

test("package 身份不符或 build 不是升级时在下载前拒绝", async (t) => {
  await t.test("package mismatch", async () => {
    const harness = createHarness();
    await assert.rejects(
      () =>
        harness.adapter.install({
          ...androidDecision,
          packageName: "com.attacker.fakepos",
        }),
      /package/i,
    );
    assert.deepEqual(harness.events, []);
  });

  await t.test("old version", async () => {
    const harness = createHarness({ installedVersionCode: 200 });
    await assert.rejects(
      () => harness.adapter.install(androidDecision),
      /newer|version|build/i,
    );
    assert.deepEqual(harness.events, []);
  });
});

test("native 流式拒绝 APK hash、package 或 signature 篡改时删除文件且不自动重放", async (t) => {
  for (const installerError of [
    new Error("APP_INSTALL_SHA256_MISMATCH"),
    new Error("APP_INSTALL_PACKAGE_MISMATCH"),
    new Error("APP_INSTALL_VERSION_NAME_MISMATCH"),
    new Error("APP_INSTALL_SIGNER_MISMATCH"),
  ]) {
    await t.test(installerError.message, async () => {
      const harness = createHarness({ installerError });
      await assert.rejects(
        () => harness.adapter.install(androidDecision),
        new RegExp(installerError.message, "u"),
      );
      assert.equal(harness.downloads.length, 1);
      assert.equal(harness.installs.length, 1);
      assert.equal(harness.removals.length, 1);
      assert.deepEqual(harness.events, [
        "directory",
        "download",
        "verify",
        "permission",
        "install",
        "remove",
      ]);
    });
  }
});

test("非可信 origin、iOS 和未知平台均在 native/download 前 fail closed", async (t) => {
  await t.test("untrusted HTTPS origin", async () => {
    const harness = createHarness();
    await assert.rejects(
      () =>
        harness.adapter.install({
          ...androidDecision,
          downloadUrl: "https://attacker.example/fake.apk",
        }),
      /trusted origin/i,
    );
    assert.deepEqual(harness.events, []);
  });

  for (const [name, platform, decision] of [
    ["iOS", "iOS", iosDecision],
    ["unknown", "web", androidDecision],
  ] as const) {
    await t.test(name, async () => {
      const harness = createHarness({ platform });
      await assert.rejects(
        () => harness.adapter.install(decision),
        /Android platform/i,
      );
      assert.deepEqual(harness.events, []);
    });
  }
});

test("下载异常只尝试一次并清理已分配的 app-owned 目标", async () => {
  const harness = createHarness({ downloadError: new Error("network failed") });
  await assert.rejects(
    () => harness.adapter.install(androidDecision),
    /network failed/i,
  );
  assert.equal(harness.downloads.length, 1);
  assert.equal(harness.installs.length, 0);
  assert.deepEqual(harness.events, [
    "directory",
    "download",
    "remove",
  ]);
  assert.deepEqual(harness.removals, [
    harness.downloads[0]?.destinationFileUri,
  ]);
});

test("native 返回未受信最终 URL 时不安装并清理目标", async () => {
  const harness = createHarness({
    downloadedFinalUrl: "https://attacker.example/fake.apk",
  });
  await assert.rejects(
    () => harness.adapter.install(androidDecision),
    /trusted origin/i,
  );
  assert.equal(harness.installs.length, 0);
  assert.deepEqual(harness.events, [
    "directory",
    "download",
    "remove",
  ]);
});

type DownloadInput = Parameters<AndroidApkDownloadPort["download"]>[0];
type InstallCall = Parameters<AndroidAppInstallerPort["installVerifiedApk"]>;

function createHarness(
  overrides: Readonly<{
    platform?: unknown;
    installedVersionCode?: number;
    downloadedSizeBytes?: number;
    downloadedFinalUrl?: string;
    downloadError?: Error;
    downloadErrors?: Error[];
    downloadGate?: Promise<void>;
    existingPreparedFile?: boolean;
    installerError?: Error;
    installPermissionGranted?: boolean;
  }> = {},
) {
  const events: string[] = [];
  const downloads: DownloadInput[] = [];
  const removals: string[] = [];
  const installs: InstallCall[] = [];
  const availableFiles = new Set<string>();
  const downloader: AndroidApkDownloadPort = {
    async download(input) {
      events.push("download");
      downloads.push(input);
      if (overrides.downloadError) throw overrides.downloadError;
      if (overrides.downloadErrors?.length) {
        const error = overrides.downloadErrors.shift();
        if (error) throw error;
      }
      if (overrides.downloadGate) await overrides.downloadGate;
      availableFiles.add(input.destinationFileUri);
      return {
        fileUri: input.destinationFileUri,
        sizeBytes: overrides.downloadedSizeBytes ?? input.expectedSizeBytes,
        finalUrl: overrides.downloadedFinalUrl ?? input.url,
      };
    },
    async remove(fileUri) {
      events.push("remove");
      removals.push(fileUri);
    },
  };
  const installer: AndroidAppInstallerPort = {
    async getInstallPermissionStatus() {
      events.push("permission");
      return overrides.installPermissionGranted === false
        ? "denied"
        : "granted";
    },
    async openInstallPermissionSettings() {
      events.push("open-permission-settings");
    },
    async getDownloadDirectory() {
      events.push("directory");
      return DOWNLOAD_DIRECTORY;
    },
    async installVerifiedApk(input) {
      events.push("install");
      installs.push([input]);
      if (overrides.installerError) throw overrides.installerError;
      return {
        launched: true,
        packageName: PACKAGE_NAME,
        versionCode: 200,
      };
    },
    async verifyDownloadedApk(input) {
      if (!overrides.existingPreparedFile && !availableFiles.has(input.fileUri)) {
        throw new Error("APK file is not prepared.");
      }
      events.push("verify");
    },
  };
  return {
    adapter: new AndroidNativeUpdateAdapter({
      platform: overrides.platform ?? "Android",
      trustedDownloadOrigins: ["https://updates.example.test"],
      installedPackageName: PACKAGE_NAME,
      installedVersionCode: overrides.installedVersionCode ?? 100,
      downloader,
      installer,
    }),
    downloads,
    events,
    installs,
    removals,
  };
}
