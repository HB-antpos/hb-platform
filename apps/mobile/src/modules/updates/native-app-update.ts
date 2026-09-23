import { sha256 } from "js-sha256";

export const APK_INTEGRITY_PROTOCOL = "sha256-v1";

const MAX_CACHED_APK_FILES = 3;
const APK_HASH_CHUNK_BYTES = 256 * 1024;
const MAX_APK_SIZE_BYTES = 300 * 1024 * 1024;
const APK_MARKER_SCHEMA_VERSION = 1;
const APP_APK_FILE_NAME_PATTERN = /^hb-[^/]+\.apk$/i;
const APP_APK_PART_FILE_NAME_PATTERN = /^hb-[^/]+\.apk\.part$/i;
const APP_APK_MARKER_FILE_NAME_PATTERN = /^hb-[^/]+\.apk\.verified\.json$/i;
const SHA256_HEX_PATTERN = /^[a-f0-9]{64}$/i;

export type NativeAppBuildInfo = {
  easBuildId: string;
  appVersion: string;
  appBuildVersion: string;
  artifactUrl: string;
  artifactSha256: string;
  artifactSize: number;
  buildProfile: string | null;
};

export type NativeAppUpdateCheckResult =
  | { status: "unsupported-platform" }
  | { status: "not-available" }
  | {
      status: "downloaded";
      build: NativeAppBuildInfo;
      fileUri: string;
      verification: "js" | "native";
    };

export type NativeAppUpdatePlatform = "android" | "ios" | "web" | string;
export type NativeAppUpdatePhase = "checking" | "downloading" | "verifying";

export type NativeAppUpdateApiClient = {
  get: (
    url: string,
    config?: {
      params?: Record<string, unknown>;
      headers?: Record<string, string>;
    }
  ) => Promise<{ data: unknown }>;
};

type NativeAppFileInfo = {
  exists: boolean;
  size?: number;
  isDirectory?: boolean;
  modificationTime?: number;
};

export type NativeApkDownloadRequest = {
  url: string;
  destinationFileUri: string;
  expectedSizeBytes: number;
  expectedSha256Hex: string;
  maximumSizeBytes: number;
  trustedOrigins: string[];
};

export type NativeApkVerificationRequest = {
  fileUri: string;
  expectedSizeBytes: number;
  expectedSha256Hex: string;
  expectedPackageName: string;
  expectedVersionCode: number;
  expectedVersionName: string;
};

export type NativeApkInstallerPort = {
  downloadApk: (request: NativeApkDownloadRequest) => Promise<{
    fileUri: string;
    sizeBytes: number;
    sha256Hex: string;
  }>;
  verifyApk: (request: NativeApkVerificationRequest) => Promise<{
    verified: boolean;
    packageName: string;
    versionCode: number;
  }>;
  removeDownloadedApk: (fileUri: string) => Promise<void>;
};

export type NativeAppUpdateDependencies = {
  apiClient: NativeAppUpdateApiClient;
  downloadFile: (url: string, fileUri: string) => Promise<{
    uri: string;
    status?: number;
    mimeType?: string | null;
  }>;
  deleteFile: (fileUri: string) => Promise<void>;
  moveFile: (from: string, to: string) => Promise<void>;
  getFileInfo: (fileUri: string) => Promise<NativeAppFileInfo>;
  readFileChunk: (fileUri: string, position: number, length: number) => Promise<Uint8Array>;
  readTextFile: (fileUri: string) => Promise<string>;
  writeTextFile: (fileUri: string, value: string) => Promise<void>;
  getCurrentBuildVersion: () => string | null;
  getCurrentPackageName: () => string | null;
  getBuildProfile: () => string | null;
  getDownloadDirectory: () => string | null;
  getTrustedOrigins: (build: NativeAppBuildInfo) => string[];
  getDownloadUrl: (build: NativeAppBuildInfo) => string | null;
  readDirectory?: (directory: string) => Promise<string[]>;
  nativeInstaller?: NativeApkInstallerPort | null;
  onPhase?: (phase: NativeAppUpdatePhase) => void;
  platform: NativeAppUpdatePlatform;
};

type VerifiedApkMarker = {
  schemaVersion: 1;
  easBuildId: string;
  artifactSha256: string;
  artifactSize: number;
};

function asRecord(value: unknown): Record<string, unknown> | null {
  return value && typeof value === "object" && !Array.isArray(value)
    ? (value as Record<string, unknown>)
    : null;
}

function asString(value: unknown): string | null {
  if (typeof value === "string" && value.trim()) {
    return value.trim();
  }
  if (typeof value === "number" && Number.isFinite(value)) {
    return String(value);
  }
  return null;
}

function asArtifactSize(value: unknown): number | null {
  return Number.isSafeInteger(value) && (value as number) > 0 && (value as number) <= MAX_APK_SIZE_BYTES
    ? (value as number)
    : null;
}

function toBuildNumber(value: string | null): number | null {
  if (!value) {
    return null;
  }
  const parsed = Number(value);
  return Number.isSafeInteger(parsed) && parsed > 0 ? parsed : null;
}

function normalizeLatestBuild(payload: unknown): NativeAppBuildInfo | null {
  const root = asRecord(payload);
  if (!root) {
    return null;
  }

  const buildRoot = asRecord(root.data) ?? root;
  const artifactUrl = asString(buildRoot.artifactUrl);
  const easBuildId = asString(buildRoot.easBuildId);
  const appVersion = asString(buildRoot.appVersion);
  const appBuildVersion = asString(buildRoot.appBuildVersion);
  const artifactSha256 = asString(buildRoot.artifactSha256)?.toLowerCase() ?? null;
  const artifactSize = asArtifactSize(buildRoot.artifactSize);
  if (
    !artifactUrl
    || !easBuildId
    || !appVersion
    || toBuildNumber(appBuildVersion) == null
    || !artifactSha256
    || !SHA256_HEX_PATTERN.test(artifactSha256)
    || artifactSize == null
  ) {
    return null;
  }

  return {
    easBuildId,
    artifactUrl,
    artifactSha256,
    artifactSize,
    appVersion,
    appBuildVersion: appBuildVersion!,
    buildProfile: asString(buildRoot.buildProfile),
  };
}

async function fetchLatestBuild(dependencies: {
  apiClient: NativeAppUpdateApiClient;
  getBuildProfile: () => string | null;
}) {
  const response = await dependencies.apiClient.get("/mobile-app-builds/android-latest", {
    params: {
      profile: dependencies.getBuildProfile() || "production",
      integrity: APK_INTEGRITY_PROTOCOL,
    },
    headers: { "X-Skip-Center-Log": "1" },
  });
  return normalizeLatestBuild(response.data);
}

export function getBuildBoundNativeAppDownloadUrl(
  baseURL: string | undefined,
  build: NativeAppBuildInfo,
  fallbackProfile = "production"
) {
  if (!baseURL?.trim() || !build.easBuildId.trim()) {
    return null;
  }

  try {
    const base = baseURL.endsWith("/") ? baseURL : `${baseURL}/`;
    const profile = build.buildProfile?.trim() || fallbackProfile;
    const query = new URLSearchParams({ profile });
    return new URL(
      `mobile-app-builds/android/${encodeURIComponent(build.easBuildId)}/download?${query.toString()}`,
      base
    ).toString();
  } catch {
    return null;
  }
}

function isNewerBuild(build: NativeAppBuildInfo | null, currentBuild: number | null) {
  const latestBuild = toBuildNumber(build?.appBuildVersion ?? null);
  return Boolean(build && currentBuild != null && latestBuild != null && latestBuild > currentBuild);
}

function buildFileUri(directory: string, fileName: string) {
  return `${directory.replace(/\/?$/, "/")}${fileName}`;
}

function buildApkFileUri(directory: string, build: NativeAppBuildInfo) {
  const safeBuildId = build.easBuildId.replace(/[^a-zA-Z0-9._-]/g, "-");
  return buildFileUri(directory, `hb-${safeBuildId}.apk`);
}

function markerFileUri(fileUri: string) {
  return `${fileUri}.verified.json`;
}

function partFileUri(fileUri: string) {
  return `${fileUri}.part`;
}

function markerFor(build: NativeAppBuildInfo): VerifiedApkMarker {
  return {
    schemaVersion: APK_MARKER_SCHEMA_VERSION,
    easBuildId: build.easBuildId,
    artifactSha256: build.artifactSha256,
    artifactSize: build.artifactSize,
  };
}

function markerMatches(value: unknown, build: NativeAppBuildInfo) {
  const marker = asRecord(value);
  return marker?.schemaVersion === APK_MARKER_SCHEMA_VERSION
    && marker.easBuildId === build.easBuildId
    && marker.artifactSha256 === build.artifactSha256
    && marker.artifactSize === build.artifactSize;
}

async function readValidMarker(
  dependencies: NativeAppUpdateDependencies,
  fileUri: string,
  build: NativeAppBuildInfo,
) {
  try {
    return markerMatches(JSON.parse(await dependencies.readTextFile(markerFileUri(fileUri))), build);
  } catch {
    return false;
  }
}

async function writeMarkerBestEffort(
  dependencies: NativeAppUpdateDependencies,
  fileUri: string,
  build: NativeAppBuildInfo,
) {
  try {
    await dependencies.writeTextFile(markerFileUri(fileUri), JSON.stringify(markerFor(build)));
  } catch {
    // APK 已在本轮完成精确校验；marker 写失败时下次启动重新计算哈希即可。
  }
}

async function deleteBestEffort(dependencies: NativeAppUpdateDependencies, fileUri: string) {
  try {
    await dependencies.deleteFile(fileUri);
  } catch {
    // 清理失败留给下次启动，不得把未验证文件交给安装器。
  }
}

async function sha256File(
  dependencies: NativeAppUpdateDependencies,
  fileUri: string,
  expectedSize: number,
) {
  const digest = sha256.create();
  for (let position = 0; position < expectedSize; position += APK_HASH_CHUNK_BYTES) {
    const length = Math.min(APK_HASH_CHUNK_BYTES, expectedSize - position);
    const chunk = await dependencies.readFileChunk(fileUri, position, length);
    if (chunk.byteLength !== length) {
      throw new Error(`APK size mismatch while hashing at ${position}`);
    }
    digest.update(chunk);
  }
  return digest.hex().toLowerCase();
}

async function hasExpectedFileSize(
  dependencies: NativeAppUpdateDependencies,
  fileUri: string,
  expectedSize: number,
) {
  const info = await dependencies.getFileInfo(fileUri);
  return info.exists && !info.isDirectory && info.size === expectedSize;
}

async function verifyJsCachedApk(
  dependencies: NativeAppUpdateDependencies,
  fileUri: string,
  build: NativeAppBuildInfo,
) {
  if (!(await hasExpectedFileSize(dependencies, fileUri, build.artifactSize))) {
    return false;
  }
  dependencies.onPhase?.("verifying");
  if (await readValidMarker(dependencies, fileUri, build)) {
    return true;
  }
  if ((await sha256File(dependencies, fileUri, build.artifactSize)) !== build.artifactSha256) {
    return false;
  }
  await writeMarkerBestEffort(dependencies, fileUri, build);
  return true;
}

function isRejectedApkMimeType(mimeType: string | null | undefined) {
  const normalized = mimeType?.split(";")[0]?.trim().toLowerCase();
  if (!normalized) {
    return false;
  }
  return normalized.startsWith("text/")
    || normalized === "application/json"
    || normalized === "application/xml"
    || normalized === "application/xhtml+xml"
    || normalized.endsWith("+json")
    || normalized.endsWith("+xml");
}

async function downloadJsVerifiedApk(
  dependencies: NativeAppUpdateDependencies,
  build: NativeAppBuildInfo,
  downloadUrl: string,
  finalFileUri: string,
) {
  const temporaryFileUri = partFileUri(finalFileUri);
  await deleteBestEffort(dependencies, temporaryFileUri);
  await deleteBestEffort(dependencies, markerFileUri(finalFileUri));
  try {
    dependencies.onPhase?.("downloading");
    const download = await dependencies.downloadFile(downloadUrl, temporaryFileUri);
    dependencies.onPhase?.("verifying");
    if (download.status != null && (download.status < 200 || download.status >= 300)) {
      throw new Error(`APK 下载失败，HTTP 状态码: ${download.status}`);
    }
    if (isRejectedApkMimeType(download.mimeType)) {
      throw new Error(`APK 下载失败，文件类型异常: ${download.mimeType}`);
    }
    if (!(await hasExpectedFileSize(dependencies, temporaryFileUri, build.artifactSize))) {
      throw new Error("APK size mismatch after download");
    }
    const actualSha256 = await sha256File(dependencies, temporaryFileUri, build.artifactSize);
    if (actualSha256 !== build.artifactSha256) {
      throw new Error("APK SHA-256 哈希不匹配");
    }
    await dependencies.moveFile(temporaryFileUri, finalFileUri);
    await writeMarkerBestEffort(dependencies, finalFileUri, build);
    return finalFileUri;
  } catch (error) {
    await deleteBestEffort(dependencies, temporaryFileUri);
    await deleteBestEffort(dependencies, finalFileUri);
    await deleteBestEffort(dependencies, markerFileUri(finalFileUri));
    throw error;
  }
}

function verificationRequest(
  build: NativeAppBuildInfo,
  fileUri: string,
  packageName: string,
): NativeApkVerificationRequest {
  return {
    fileUri,
    expectedSizeBytes: build.artifactSize,
    expectedSha256Hex: build.artifactSha256,
    expectedPackageName: packageName,
    expectedVersionCode: toBuildNumber(build.appBuildVersion)!,
    expectedVersionName: build.appVersion,
  };
}

async function verifyNativeResult(
  installer: NativeApkInstallerPort,
  request: NativeApkVerificationRequest,
) {
  const result = await installer.verifyApk(request);
  return result.verified === true
    && result.packageName === request.expectedPackageName
    && result.versionCode === request.expectedVersionCode;
}

async function prepareNativeVerifiedApk(
  dependencies: NativeAppUpdateDependencies,
  build: NativeAppBuildInfo,
  downloadUrl: string,
  fileUri: string,
) {
  const installer = dependencies.nativeInstaller!;
  const packageName = dependencies.getCurrentPackageName()?.trim();
  if (!packageName) {
    return null;
  }
  const request = verificationRequest(build, fileUri, packageName);
  if (await hasExpectedFileSize(dependencies, fileUri, build.artifactSize)) {
    try {
      dependencies.onPhase?.("verifying");
      if (await verifyNativeResult(installer, request)) {
        return fileUri;
      }
    } catch {
      // 缓存身份校验失败后在本轮只重新下载一次。
    }
  }

  await installer.removeDownloadedApk(fileUri).catch(() => undefined);
  try {
    // 旧原生包没有进度事件，只上报实际阶段，不能伪造百分比。
    dependencies.onPhase?.("downloading");
    const downloaded = await installer.downloadApk({
      url: downloadUrl,
      destinationFileUri: fileUri,
      expectedSizeBytes: build.artifactSize,
      expectedSha256Hex: build.artifactSha256,
      maximumSizeBytes: MAX_APK_SIZE_BYTES,
      trustedOrigins: dependencies.getTrustedOrigins(build),
    });
    dependencies.onPhase?.("verifying");
    if (
      downloaded.fileUri !== fileUri
      || downloaded.sizeBytes !== build.artifactSize
      || downloaded.sha256Hex.toLowerCase() !== build.artifactSha256
      || !(await verifyNativeResult(installer, request))
    ) {
      throw new Error("APK native identity verification failed");
    }
    return fileUri;
  } catch (error) {
    await installer.removeDownloadedApk(fileUri).catch(() => undefined);
    throw error;
  }
}

async function cleanupDownloadedApkFiles(
  dependencies: NativeAppUpdateDependencies,
  downloadDirectory: string,
  protectedFileUri?: string,
) {
  if (!dependencies.readDirectory) {
    return;
  }
  let fileNames: string[];
  try {
    fileNames = await dependencies.readDirectory(downloadDirectory);
  } catch {
    return;
  }

  const apkFiles: { fileName: string; fileUri: string; modificationTime: number }[] = [];
  const apkFileNames = new Set(fileNames.filter((name) => APP_APK_FILE_NAME_PATTERN.test(name)));
  for (const fileName of fileNames) {
    const fileUri = buildFileUri(downloadDirectory, fileName);
    if (APP_APK_PART_FILE_NAME_PATTERN.test(fileName)) {
      await deleteBestEffort(dependencies, fileUri);
      continue;
    }
    if (APP_APK_MARKER_FILE_NAME_PATTERN.test(fileName)) {
      const apkName = fileName.slice(0, -".verified.json".length);
      if (!apkFileNames.has(apkName)) {
        await deleteBestEffort(dependencies, fileUri);
      }
      continue;
    }
    if (!APP_APK_FILE_NAME_PATTERN.test(fileName)) {
      continue;
    }
    try {
      const info = await dependencies.getFileInfo(fileUri);
      if (info.exists && !info.isDirectory) {
        apkFiles.push({ fileName, fileUri, modificationTime: info.modificationTime ?? 0 });
      }
    } catch {
      // 单文件异常不阻断当前已验证 APK。
    }
  }

  apkFiles.sort((left, right) => {
    if (right.modificationTime !== left.modificationTime) {
      return right.modificationTime - left.modificationTime;
    }
    return right.fileName.localeCompare(left.fileName);
  });
  const keepFileUris = new Set<string>();
  if (protectedFileUri) {
    keepFileUris.add(protectedFileUri);
  }
  for (const apkFile of apkFiles) {
    if (keepFileUris.size >= MAX_CACHED_APK_FILES) {
      break;
    }
    keepFileUris.add(apkFile.fileUri);
  }
  for (const apkFile of apkFiles) {
    if (!keepFileUris.has(apkFile.fileUri)) {
      await deleteBestEffort(dependencies, apkFile.fileUri);
      await deleteBestEffort(dependencies, markerFileUri(apkFile.fileUri));
    }
  }
}

export async function checkAndDownloadNativeAppUpdate(
  dependencies: NativeAppUpdateDependencies
): Promise<NativeAppUpdateCheckResult> {
  if (dependencies.platform !== "android") {
    return { status: "unsupported-platform" };
  }
  const currentBuild = toBuildNumber(dependencies.getCurrentBuildVersion());
  const downloadDirectory = dependencies.getDownloadDirectory();
  if (currentBuild == null || !downloadDirectory) {
    return { status: "not-available" };
  }

  dependencies.onPhase?.("checking");
  const build = await fetchLatestBuild(dependencies);
  if (!isNewerBuild(build, currentBuild)) {
    await cleanupDownloadedApkFiles(dependencies, downloadDirectory);
    return { status: "not-available" };
  }
  const downloadUrl = dependencies.getDownloadUrl(build!);
  if (!downloadUrl) {
    return { status: "not-available" };
  }
  const fileUri = buildApkFileUri(downloadDirectory, build!);

  if (dependencies.nativeInstaller) {
    const prepared = await prepareNativeVerifiedApk(dependencies, build!, downloadUrl, fileUri);
    if (!prepared) {
      return { status: "not-available" };
    }
    await cleanupDownloadedApkFiles(dependencies, downloadDirectory, prepared);
    return { status: "downloaded", build: build!, fileUri: prepared, verification: "native" };
  }

  if (!(await verifyJsCachedApk(dependencies, fileUri, build!))) {
    await deleteBestEffort(dependencies, fileUri);
    await deleteBestEffort(dependencies, markerFileUri(fileUri));
    await downloadJsVerifiedApk(dependencies, build!, downloadUrl, fileUri);
  }
  await cleanupDownloadedApkFiles(dependencies, downloadDirectory, fileUri);
  return { status: "downloaded", build: build!, fileUri, verification: "js" };
}
