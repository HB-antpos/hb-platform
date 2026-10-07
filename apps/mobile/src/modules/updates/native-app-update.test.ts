import assert from "node:assert/strict";
import { createHash } from "node:crypto";
import {
  APK_INTEGRITY_PROTOCOL,
  checkAndDownloadNativeAppUpdate,
  getBuildBoundNativeAppDownloadUrl,
  type NativeAppUpdateDependencies,
  type NativeApkInstallerPort,
} from "./native-app-update";

const APK_BYTES = new Uint8Array(Buffer.alloc(600_000, 0x41));
const WRONG_APK_BYTES = new Uint8Array(Buffer.alloc(APK_BYTES.byteLength, 0x42));
const APK_SHA256 = createHash("sha256").update(APK_BYTES).digest("hex");
const MAX_APK_SIZE_BYTES = 300 * 1024 * 1024;

const VALID_BUILD_PAYLOAD = {
  easBuildId: "build-17",
  appVersion: "1.0.3",
  appBuildVersion: "17",
  artifactUrl: "https://cos.hotbargain.top/mobile-app-builds/production/build-17.apk",
  artifactSha256: APK_SHA256,
  artifactSize: APK_BYTES.byteLength,
  buildProfile: "production",
} as const;

type TestHarness = {
  dependencies: NativeAppUpdateDependencies;
  files: Map<string, Uint8Array>;
  textFiles: Map<string, string>;
  downloaded: string[];
  deleted: string[];
  moved: string[];
  readChunks: { fileUri: string; position: number; length: number }[];
  requestedParams: (Record<string, unknown> | undefined)[];
  setDownloadBytes: (bytes: Uint8Array) => void;
};

function fileName(fileUri: string) {
  return fileUri.slice(fileUri.lastIndexOf("/") + 1);
}

function cloneBytes(bytes: Uint8Array) {
  return new Uint8Array(bytes);
}

function createHarness(options?: {
  payload?: unknown;
  currentBuild?: string;
  downloadBytes?: Uint8Array;
  nativeInstaller?: NativeApkInstallerPort | null;
  overrides?: Partial<NativeAppUpdateDependencies>;
}): TestHarness {
  const files = new Map<string, Uint8Array>();
  const textFiles = new Map<string, string>();
  const downloaded: string[] = [];
  const deleted: string[] = [];
  const moved: string[] = [];
  const readChunks: { fileUri: string; position: number; length: number }[] = [];
  const requestedParams: (Record<string, unknown> | undefined)[] = [];
  let downloadBytes = options?.downloadBytes ?? APK_BYTES;

  const dependencies: NativeAppUpdateDependencies = {
    platform: "android",
    apiClient: {
      get: async (_url, config) => {
        requestedParams.push(config?.params);
        return { data: options?.payload ?? VALID_BUILD_PAYLOAD };
      },
    },
    getCurrentBuildVersion: () => options?.currentBuild ?? "16",
    getCurrentPackageName: () => "com.hbweb.expo",
    getBuildProfile: () => "production",
    getDownloadDirectory: () => "file:///cache",
    getTrustedOrigins: () => ["https://hotbargain.vip", "https://cos.hotbargain.top"],
    getDownloadUrl: (build) => getBuildBoundNativeAppDownloadUrl(
      "https://hotbargain.vip/api",
      build,
    ),
    getFileInfo: async (fileUri) => {
      const bytes = files.get(fileUri);
      if (bytes) {
        return { exists: true, size: bytes.byteLength, isDirectory: false, modificationTime: 1 };
      }
      const text = textFiles.get(fileUri);
      return text == null
        ? { exists: false }
        : { exists: true, size: Buffer.byteLength(text), isDirectory: false, modificationTime: 1 };
    },
    downloadFile: async (url, targetUri) => {
      downloaded.push(`${url} -> ${targetUri}`);
      files.set(targetUri, cloneBytes(downloadBytes));
      return {
        uri: targetUri,
        status: 200,
        mimeType: "application/vnd.android.package-archive",
      };
    },
    deleteFile: async (fileUri) => {
      deleted.push(fileUri);
      files.delete(fileUri);
      textFiles.delete(fileUri);
    },
    moveFile: async (from, to) => {
      const bytes = files.get(from);
      if (!bytes) {
        throw new Error(`missing source: ${from}`);
      }
      moved.push(`${from} -> ${to}`);
      files.set(to, bytes);
      files.delete(from);
    },
    readFileChunk: async (fileUri, position, length) => {
      readChunks.push({ fileUri, position, length });
      const bytes = files.get(fileUri);
      if (!bytes) {
        throw new Error(`missing file: ${fileUri}`);
      }
      return bytes.slice(position, Math.min(position + length, bytes.byteLength));
    },
    readTextFile: async (fileUri) => {
      const text = textFiles.get(fileUri);
      if (text == null) {
        throw new Error(`missing text file: ${fileUri}`);
      }
      return text;
    },
    writeTextFile: async (fileUri, value) => {
      textFiles.set(fileUri, value);
    },
    readDirectory: async () => [
      ...new Set([
        ...Array.from(files.keys(), fileName),
        ...Array.from(textFiles.keys(), fileName),
      ]),
    ],
    nativeInstaller: options?.nativeInstaller ?? null,
    ...options?.overrides,
  };

  return {
    dependencies,
    files,
    textFiles,
    downloaded,
    deleted,
    moved,
    readChunks,
    requestedParams,
    setDownloadBytes: (bytes) => {
      downloadBytes = bytes;
    },
  };
}

function finalApkUri(buildId = "build-17") {
  return `file:///cache/hb-${buildId}.apk`;
}

function partApkUri(buildId = "build-17") {
  return `${finalApkUri(buildId)}.part`;
}

function markerUri(buildId = "build-17") {
  return `${finalApkUri(buildId)}.verified.json`;
}

function validMarker(overrides?: Record<string, unknown>) {
  return JSON.stringify({
    schemaVersion: 1,
    easBuildId: "build-17",
    artifactSha256: APK_SHA256,
    artifactSize: APK_BYTES.byteLength,
    ...overrides,
  });
}

async function run() {
  {
    const phases: string[] = [];
    const harness = createHarness({ overrides: { onPhase: (phase) => phases.push(phase) } });
    const downloadFile = harness.dependencies.downloadFile;
    harness.dependencies.downloadFile = async (...args) => {
      assert.equal(phases.at(-1), "downloading", "下载等待期间必须已通知界面，不能到完成才提示");
      return downloadFile(...args);
    };
    await checkAndDownloadNativeAppUpdate(harness.dependencies);
    assert.deepEqual(phases, ["checking", "downloading", "verifying"]);

    phases.length = 0;
    await checkAndDownloadNativeAppUpdate(harness.dependencies);
    assert.deepEqual(phases, ["checking", "verifying"], "命中缓存时不应误报正在下载");

    phases.length = 0;
    await checkAndDownloadNativeAppUpdate({ ...harness.dependencies, getCurrentBuildVersion: () => "17" });
    assert.deepEqual(phases, ["checking"], "无新版本不展示下载或校验");

    phases.length = 0;
    await checkAndDownloadNativeAppUpdate({ ...harness.dependencies, platform: "ios" });
    assert.deepEqual(phases, [], "iOS 不产生 APK 下载状态");
  }

  assert.equal(
    getBuildBoundNativeAppDownloadUrl("https://hotbargain.vip/api", VALID_BUILD_PAYLOAD),
    "https://hotbargain.vip/api/mobile-app-builds/android/build-17/download?profile=production",
  );

  {
    const harness = createHarness({
      payload: {
        ...VALID_BUILD_PAYLOAD,
        artifactSha256: undefined,
        artifactSize: undefined,
      },
    });
    const result = await checkAndDownloadNativeAppUpdate(harness.dependencies);
    assert.equal(result.status, "not-available", "缺少完整性元数据必须 fail closed");
    assert.equal(harness.downloaded.length, 0);
    assert.deepEqual(harness.requestedParams[0], {
      profile: "production",
      integrity: APK_INTEGRITY_PROTOCOL,
    });
  }

  for (const invalidPayload of [
    { artifactSha256: "abc" },
    { artifactSha256: "g".repeat(64) },
    { artifactSize: 0 },
    { artifactSize: -1 },
    { artifactSize: 1.5 },
    { artifactSize: MAX_APK_SIZE_BYTES + 1 },
    { appBuildVersion: "not-a-build" },
    { appVersion: null },
  ]) {
    const harness = createHarness({ payload: { ...VALID_BUILD_PAYLOAD, ...invalidPayload } });
    const result = await checkAndDownloadNativeAppUpdate(harness.dependencies);
    assert.equal(result.status, "not-available", `非法候选必须拒绝: ${JSON.stringify(invalidPayload)}`);
    assert.equal(harness.downloaded.length, 0);
  }

  {
    const harness = createHarness({ payload: { success: true, data: VALID_BUILD_PAYLOAD } });
    const result = await checkAndDownloadNativeAppUpdate(harness.dependencies);
    assert.equal(result.status, "downloaded");
    assert.equal(result.status === "downloaded" ? result.verification : null, "js");
    assert.deepEqual(harness.downloaded, [
      "https://hotbargain.vip/api/mobile-app-builds/android/build-17/download?profile=production -> file:///cache/hb-build-17.apk.part",
    ]);
    assert.deepEqual(harness.moved, [
      "file:///cache/hb-build-17.apk.part -> file:///cache/hb-build-17.apk",
    ]);
    assert.equal(harness.files.has(finalApkUri()), true);
    assert.equal(harness.files.has(partApkUri()), false);
    assert.equal(harness.textFiles.has(markerUri()), true);
    assert.ok(harness.readChunks.length >= 3, "600KB APK 应按 256KiB 分段读取");
    assert.ok(harness.readChunks.every((chunk) => chunk.length <= 256 * 1024));
  }

  {
    const harness = createHarness();
    harness.files.set(finalApkUri(), APK_BYTES.slice(0, 1024));
    const result = await checkAndDownloadNativeAppUpdate(harness.dependencies);
    assert.equal(result.status, "downloaded", "截断缓存应删除后重新下载");
    assert.ok(harness.deleted.includes(finalApkUri()));
    assert.equal(harness.downloaded.length, 1);
  }

  {
    const harness = createHarness();
    harness.files.set(finalApkUri(), cloneBytes(WRONG_APK_BYTES));
    const result = await checkAndDownloadNativeAppUpdate(harness.dependencies);
    assert.equal(result.status, "downloaded", "同大小错误内容也必须重新下载");
    assert.ok(harness.deleted.includes(finalApkUri()));
    assert.equal(harness.downloaded.length, 1);
  }

  {
    const harness = createHarness();
    harness.files.set(finalApkUri(), cloneBytes(APK_BYTES));
    harness.textFiles.set(markerUri(), validMarker());
    const result = await checkAndDownloadNativeAppUpdate(harness.dependencies);
    assert.equal(result.status, "downloaded");
    assert.equal(harness.downloaded.length, 0, "有效 marker 应避免重复下载");
    assert.equal(harness.readChunks.length, 0, "有效 marker 应避免每次启动重算 115MB 哈希");
  }

  {
    const harness = createHarness();
    harness.files.set(finalApkUri(), cloneBytes(APK_BYTES));
    const result = await checkAndDownloadNativeAppUpdate(harness.dependencies);
    assert.equal(result.status, "downloaded");
    assert.equal(harness.downloaded.length, 0, "marker 缺失但哈希正确时不应重下");
    assert.ok(harness.readChunks.length >= 3, "marker 缺失必须重新校验完整哈希");
    assert.equal(harness.textFiles.has(markerUri()), true);
  }

  {
    const harness = createHarness();
    harness.files.set(finalApkUri(), cloneBytes(APK_BYTES));
    harness.textFiles.set(markerUri(), validMarker({ artifactSha256: "b".repeat(64) }));
    const result = await checkAndDownloadNativeAppUpdate(harness.dependencies);
    assert.equal(result.status, "downloaded");
    assert.ok(harness.readChunks.length >= 3, "marker 失配必须重新哈希");
    assert.equal(JSON.parse(harness.textFiles.get(markerUri())!).artifactSha256, APK_SHA256);
  }

  {
    const harness = createHarness({ downloadBytes: APK_BYTES.slice(0, 1024) });
    await assert.rejects(
      () => checkAndDownloadNativeAppUpdate(harness.dependencies),
      /size|大小/i,
    );
    assert.equal(harness.files.has(partApkUri()), false);
    assert.equal(harness.files.has(finalApkUri()), false);
    assert.equal(harness.moved.length, 0);
  }

  {
    const harness = createHarness({ downloadBytes: WRONG_APK_BYTES });
    await assert.rejects(
      () => checkAndDownloadNativeAppUpdate(harness.dependencies),
      /SHA|哈希/i,
    );
    assert.equal(harness.files.has(partApkUri()), false);
    assert.equal(harness.files.has(finalApkUri()), false);
    assert.equal(harness.moved.length, 0);
  }

  {
    const harness = createHarness({
      overrides: {
        downloadFile: async (_url, targetUri) => {
          harness.files.set(targetUri, APK_BYTES.slice(0, 1024));
          throw new Error("network interrupted");
        },
      },
    });
    await assert.rejects(
      () => checkAndDownloadNativeAppUpdate(harness.dependencies),
      /network interrupted/,
    );
    assert.equal(harness.files.has(partApkUri()), false, "中断留下的 .part 必须清理");
  }

  {
    const harness = createHarness({
      overrides: {
        downloadFile: async (_url, targetUri) => {
          harness.files.set(targetUri, cloneBytes(APK_BYTES));
          return { uri: targetUri, status: 200, mimeType: "text/html" };
        },
      },
    });
    await assert.rejects(
      () => checkAndDownloadNativeAppUpdate(harness.dependencies),
      /文件类型异常/,
    );
    assert.equal(harness.files.has(partApkUri()), false);
  }

  {
    const harness = createHarness();
    harness.files.set(partApkUri("stale"), new Uint8Array([1]));
    harness.textFiles.set(markerUri("orphan"), "{}");
    for (const [buildId, modificationByte] of [["13", 13], ["14", 14], ["15", 15], ["16", 16]] as const) {
      harness.files.set(finalApkUri(buildId), new Uint8Array([modificationByte]));
    }
    harness.files.set("file:///cache/other.apk", new Uint8Array([1]));
    const result = await checkAndDownloadNativeAppUpdate({
      ...harness.dependencies,
      getCurrentBuildVersion: () => "17",
      getFileInfo: async (fileUri) => {
        const base = await harness.dependencies.getFileInfo(fileUri);
        const match = /hb-(\d+)\.apk$/.exec(fileUri);
        return match && base.exists ? { ...base, modificationTime: Number(match[1]) } : base;
      },
    });
    assert.equal(result.status, "not-available");
    assert.ok(harness.deleted.includes(partApkUri("stale")));
    assert.ok(harness.deleted.includes(markerUri("orphan")));
    assert.ok(harness.deleted.includes(finalApkUri("13")), "只保留最近三个 hb APK");
    assert.equal(harness.deleted.includes("file:///cache/other.apk"), false);
  }

  {
    const nativeCalls: string[] = [];
    const phases: string[] = [];
    const nativeInstaller: NativeApkInstallerPort = {
      downloadApk: async (request) => {
        assert.equal(phases.at(-1), "downloading", "原生下载开始前必须通知界面");
        nativeCalls.push(`download:${request.destinationFileUri}`);
        return {
          fileUri: request.destinationFileUri,
          sizeBytes: request.expectedSizeBytes,
          sha256Hex: request.expectedSha256Hex,
        };
      },
      verifyApk: async (request) => {
        assert.equal(phases.at(-1), "verifying", "下载后的身份校验必须显示校验状态");
        nativeCalls.push(`verify:${request.fileUri}:${request.expectedSizeBytes}`);
        return {
          verified: true,
          packageName: request.expectedPackageName,
          versionCode: request.expectedVersionCode,
        };
      },
      removeDownloadedApk: async (fileUri) => {
        nativeCalls.push(`remove:${fileUri}`);
      },
    };
    const harness = createHarness({ nativeInstaller, overrides: { onPhase: (phase) => phases.push(phase) } });
    const result = await checkAndDownloadNativeAppUpdate(harness.dependencies);
    assert.equal(result.status, "downloaded");
    assert.equal(result.status === "downloaded" ? result.verification : null, "native");
    assert.deepEqual(nativeCalls, [
      `remove:${finalApkUri()}`,
      `download:${finalApkUri()}`,
      `verify:${finalApkUri()}:${APK_BYTES.byteLength}`,
    ]);
    assert.equal(harness.downloaded.length, 0, "原生模块可用时不得走 JS downloader");
    assert.deepEqual(phases, ["checking", "downloading", "verifying"]);
    phases.length = 0;
    harness.files.set(finalApkUri(), APK_BYTES);
    await checkAndDownloadNativeAppUpdate(harness.dependencies);
    assert.deepEqual(phases, ["checking", "verifying"], "原生缓存校验不能误报下载");
  }

  for (const [share, expected] of [
    [0.5, 0.5],
    [null, undefined],
    [0, undefined],
    [1, undefined],
    [Number.NaN, undefined],
    [1.5, undefined],
  ] as [number | null, number | undefined][]) {
    // 后台下载把带宽比例交给原生限速；强制更新（null）或不合法的值不带该字段，保持全速。
    const requests: Parameters<NativeApkInstallerPort["downloadApk"]>[0][] = [];
    const nativeInstaller: NativeApkInstallerPort = {
      downloadApk: async (request) => {
        requests.push(request);
        return {
          fileUri: request.destinationFileUri,
          sizeBytes: request.expectedSizeBytes,
          sha256Hex: request.expectedSha256Hex,
        };
      },
      verifyApk: async (request) => ({
        verified: true,
        packageName: request.expectedPackageName,
        versionCode: request.expectedVersionCode,
      }),
      removeDownloadedApk: async () => undefined,
    };
    const harness = createHarness({ nativeInstaller, overrides: { getDownloadBandwidthShare: () => share } });
    const result = await checkAndDownloadNativeAppUpdate(harness.dependencies);
    assert.equal(result.status, "downloaded", `share=${share}`);
    assert.equal(requests.length, 1);
    assert.equal(requests[0].bandwidthShare, expected, `share=${share}`);
    assert.equal("bandwidthShare" in requests[0], expected !== undefined, `share=${share} 时是否带字段`);
  }

  for (const outcome of ["success", "failure"] as const) {
    // 新原生包：下载前订阅进度，只认本次目标 URI，下载结束（成功或失败）立即退订。
    const progress: unknown[] = [];
    const subscription = { listener: null as ((event: unknown) => void) | null, removed: 0 };
    const nativeInstaller: NativeApkInstallerPort = {
      addListener: (eventName, listener) => {
        assert.equal(eventName, "onDownloadProgress");
        subscription.listener = listener as (event: unknown) => void;
        return { remove: () => { subscription.removed += 1; } };
      },
      downloadApk: async (request) => {
        const emit = subscription.listener;
        assert.ok(emit, "原生下载开始前必须已订阅进度");
        const total = request.expectedSizeBytes;
        emit({ destinationFileUri: request.destinationFileUri, bytesWritten: 0, totalBytes: total });
        emit({ destinationFileUri: request.destinationFileUri, bytesWritten: total / 2, totalBytes: total });
        emit({ destinationFileUri: request.destinationFileUri, bytesWritten: total / 2 + 1, totalBytes: total });
        emit({ destinationFileUri: "file:///cache/hb-other.apk", bytesWritten: total, totalBytes: total });
        emit({ destinationFileUri: request.destinationFileUri, bytesWritten: "bad", totalBytes: total });
        if (outcome === "failure") {
          throw new Error("APP_DOWNLOAD_HTTP_ERROR");
        }
        emit({ destinationFileUri: request.destinationFileUri, bytesWritten: total, totalBytes: total });
        return {
          fileUri: request.destinationFileUri,
          sizeBytes: request.expectedSizeBytes,
          sha256Hex: request.expectedSha256Hex,
        };
      },
      verifyApk: async (request) => ({
        verified: true,
        packageName: request.expectedPackageName,
        versionCode: request.expectedVersionCode,
      }),
      removeDownloadedApk: async () => undefined,
    };
    const harness = createHarness({
      nativeInstaller,
      overrides: { onProgress: (value) => progress.push(value) },
    });
    if (outcome === "failure") {
      await assert.rejects(checkAndDownloadNativeAppUpdate(harness.dependencies), /APP_DOWNLOAD_HTTP_ERROR/);
    } else {
      assert.equal((await checkAndDownloadNativeAppUpdate(harness.dependencies)).status, "downloaded");
    }
    const total = APK_BYTES.byteLength;
    assert.deepEqual(progress, [
      { bytesWritten: 0, totalBytes: total },
      { bytesWritten: total / 2, totalBytes: total },
      ...(outcome === "success" ? [{ bytesWritten: total, totalBytes: total }] : []),
    ], "同一百分比、别的目标和非法值都不能转发");
    assert.equal(subscription.removed, 1, `${outcome} 后必须退订原生进度`);
  }

  {
    // 新原生包的进度事件带实测速率：低于 128KB/s 时转发「网络差」，界面据此提示换网络。
    const progress: unknown[] = [];
    let emit: ((event: unknown) => void) | null = null;
    const nativeInstaller: NativeApkInstallerPort = {
      addListener: (_eventName, listener) => {
        emit = listener as (event: unknown) => void;
        return { remove: () => undefined };
      },
      downloadApk: async (request) => {
        const total = request.expectedSizeBytes;
        emit?.({ destinationFileUri: request.destinationFileUri, bytesWritten: 0, totalBytes: total });
        emit?.({ destinationFileUri: request.destinationFileUri, bytesWritten: 1, totalBytes: total, bytesPerSecond: 60 * 1024 });
        emit?.({ destinationFileUri: request.destinationFileUri, bytesWritten: total, totalBytes: total, bytesPerSecond: 500 * 1024 });
        return {
          fileUri: request.destinationFileUri,
          sizeBytes: request.expectedSizeBytes,
          sha256Hex: request.expectedSha256Hex,
        };
      },
      verifyApk: async (request) => ({
        verified: true,
        packageName: request.expectedPackageName,
        versionCode: request.expectedVersionCode,
      }),
      removeDownloadedApk: async () => undefined,
    };
    const harness = createHarness({ nativeInstaller, overrides: { onProgress: (value) => progress.push(value) } });
    assert.equal((await checkAndDownloadNativeAppUpdate(harness.dependencies)).status, "downloaded");
    const total = APK_BYTES.byteLength;
    assert.deepEqual(progress, [
      { bytesWritten: 0, totalBytes: total },
      { bytesWritten: 1, totalBytes: total, slowNetwork: true },
      { bytesWritten: total, totalBytes: total },
    ]);
  }

  {
    // 旧原生包没有 addListener：照常下载，不产生任何伪造进度。
    const progress: unknown[] = [];
    const nativeInstaller: NativeApkInstallerPort = {
      downloadApk: async (request) => ({
        fileUri: request.destinationFileUri,
        sizeBytes: request.expectedSizeBytes,
        sha256Hex: request.expectedSha256Hex,
      }),
      verifyApk: async (request) => ({
        verified: true,
        packageName: request.expectedPackageName,
        versionCode: request.expectedVersionCode,
      }),
      removeDownloadedApk: async () => undefined,
    };
    const harness = createHarness({
      nativeInstaller,
      overrides: { onProgress: (value) => progress.push(value) },
    });
    assert.equal((await checkAndDownloadNativeAppUpdate(harness.dependencies)).status, "downloaded");
    assert.deepEqual(progress, []);
  }

  {
    // 兼容下载器（无原生模块）：字节回调同样按百分比节流后转发。
    const progress: unknown[] = [];
    const harness = createHarness({ overrides: { onProgress: (value) => progress.push(value) } });
    const baseDownload = harness.dependencies.downloadFile;
    harness.dependencies.downloadFile = async (url, targetUri, onBytesWritten) => {
      assert.ok(onBytesWritten, "有进度订阅者时必须传入字节回调");
      for (const written of [0, 1, 6_000, 6_001, 300_000, APK_BYTES.byteLength]) {
        onBytesWritten(written);
      }
      return baseDownload(url, targetUri);
    };
    const result = await checkAndDownloadNativeAppUpdate(harness.dependencies);
    assert.equal(result.status === "downloaded" ? result.verification : null, "js");
    const total = APK_BYTES.byteLength;
    assert.deepEqual(progress, [
      { bytesWritten: 0, totalBytes: total },
      { bytesWritten: 6_000, totalBytes: total },
      { bytesWritten: 300_000, totalBytes: total },
      { bytesWritten: total, totalBytes: total },
    ]);
  }

  {
    const harness = createHarness({
      overrides: { getDownloadUrl: () => null },
    });
    const result = await checkAndDownloadNativeAppUpdate(harness.dependencies);
    assert.equal(result.status, "not-available", "不能生成 build-bound URL 时不得回退 artifactUrl");
    assert.equal(harness.downloaded.length, 0);
  }

  {
    const harness = createHarness({ currentBuild: "17" });
    const result = await checkAndDownloadNativeAppUpdate(harness.dependencies);
    assert.equal(result.status, "not-available");
    assert.equal(harness.downloaded.length, 0);
  }

  console.log("native-app-update.test.ts: ok");
}

void run();
