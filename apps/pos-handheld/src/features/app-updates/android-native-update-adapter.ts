import type {
  InstallVerifiedApkRequest as NativeInstallVerifiedApkRequest,
  InstallVerifiedApkResult as NativeInstallVerifiedApkResult,
  VerifyDownloadedApkRequest as NativeVerifyDownloadedApkRequest,
  InstallPermissionStatus as NativeInstallPermissionStatus,
} from "../../../modules/hb-app-installer/src/HBAppInstaller.types";

import {
  ANDROID_APK_MAX_SIZE_BYTES,
  normalizePosHandheldUpdatePolicy,
  type PosHandheldUpdatePolicy,
} from "@/core/contracts/app-updates";

export type AndroidApkDownloadRequest = Readonly<{
  url: string;
  destinationFileUri: string;
  expectedSizeBytes: number;
  maximumSizeBytes: number;
  trustedOrigins: readonly string[];
}>;

export type DownloadedAndroidApk = Readonly<{
  fileUri: string;
  sizeBytes: number;
  finalUrl: string;
}>;

export interface AndroidApkDownloadPort {
  download(input: AndroidApkDownloadRequest): Promise<DownloadedAndroidApk>;
  remove(fileUri: string): Promise<void>;
}

export type InstallVerifiedApkRequest = NativeInstallVerifiedApkRequest;
export type VerifyDownloadedApkRequest = NativeVerifyDownloadedApkRequest;
export type InstallVerifiedApkResult = NativeInstallVerifiedApkResult;
export type AndroidInstallPermissionStatus = NativeInstallPermissionStatus;

/** 直接复用 modules/hb-app-installer 的强类型身份合同。 */
export interface AndroidAppInstallerPort {
  getInstallPermissionStatus(): Promise<AndroidInstallPermissionStatus>;
  openInstallPermissionSettings(): Promise<void>;
  getDownloadDirectory(): Promise<string>;
  installVerifiedApk(
    request: InstallVerifiedApkRequest,
  ): Promise<InstallVerifiedApkResult>;
  verifyDownloadedApk(request: VerifyDownloadedApkRequest): Promise<void>;
}

export interface AndroidNativeUpdatePort {
  getInstallPermissionStatus(): Promise<AndroidInstallPermissionStatus>;
  openInstallPermissionSettings(): Promise<void>;
  prepare(decision: PosHandheldUpdatePolicy): Promise<void>;
  install(
    decision: PosHandheldUpdatePolicy,
  ): Promise<InstallVerifiedApkResult>;
}

export type AndroidNativeUpdateAdapterOptions = Readonly<{
  platform: unknown;
  trustedDownloadOrigins: readonly string[];
  installedPackageName: string | null;
  installedVersionCode: number | null;
  downloader: AndroidApkDownloadPort;
  installer: AndroidAppInstallerPort;
}>;

/**
 * 下载与 native 安装之间没有 fallback 或自动重试。任何失败都删除本次目标文件，
 * 下一次尝试只能由用户再次触发。
 */
export class AndroidNativeUpdateAdapter implements AndroidNativeUpdatePort {
  private readonly trustedOrigins: ReadonlySet<string>;
  private readonly trustedOriginValues: readonly string[];
  private prepared: PreparedAndroidApk | null = null;
  private preparing: Promise<void> | null = null;
  private preparingFingerprint: string | null = null;

  public constructor(
    private readonly options: AndroidNativeUpdateAdapterOptions,
  ) {
    this.trustedOriginValues = Object.freeze(
      options.trustedDownloadOrigins.map(requiredTrustedOrigin),
    );
    this.trustedOrigins = new Set(this.trustedOriginValues);
  }

  public async install(
    input: PosHandheldUpdatePolicy,
  ): Promise<InstallVerifiedApkResult> {
    const decision = validateAndroidDecision(input, this.options, this.trustedOrigins);
    await this.prepare(decision);
    if ((await this.getInstallPermissionStatus()) !== "granted") {
      throw new AndroidInstallPermissionRequiredError();
    }
    const prepared = this.prepared;
    if (!prepared || prepared.fingerprint !== androidDecisionFingerprint(decision)) {
      throw new Error("Android update preparation was lost.");
    }
    try {
      const result = await this.options.installer.installVerifiedApk(prepared.request);
      if (
        result.launched !== true ||
        result.packageName !== prepared.request.expectedPackageName ||
        result.versionCode !== prepared.request.expectedVersionCode
      ) {
        throw new Error("Android native installer returned a mismatched identity.");
      }
      return Object.freeze({ ...result });
    } catch (error) {
      await bestEffortRemove(this.options.downloader, prepared.request.fileUri);
      this.prepared = null;
      throw error;
    }
  }

  public async prepare(input: PosHandheldUpdatePolicy): Promise<void> {
    const decision = validateAndroidDecision(input, this.options, this.trustedOrigins);
    const fingerprint = androidDecisionFingerprint(decision);
    if (this.prepared?.fingerprint === fingerprint) return;
    if (this.preparing) {
      const waitingForFingerprint = this.preparingFingerprint;
      try {
        await this.preparing;
      } catch (error) {
        if (waitingForFingerprint === fingerprint) throw error;
      }
      if (this.prepared?.fingerprint === fingerprint) return;
      return this.prepare(input);
    }
    const operation = this.prepareFresh(decision, fingerprint);
    this.preparing = operation;
    this.preparingFingerprint = fingerprint;
    try {
      await operation;
    } finally {
      if (this.preparing === operation) {
        this.preparing = null;
        this.preparingFingerprint = null;
      }
    }
  }

  private async prepareFresh(
    decision: PosHandheldUpdatePolicy,
    fingerprint: string,
  ): Promise<void> {
    const downloadUrl = requiredTrustedDownloadUrl(decision.downloadUrl, this.trustedOrigins);
    const directoryUri = await this.options.installer.getDownloadDirectory();
    const expectedVersionCode = Number(decision.latestBuild);
    const destinationFileUri = appOwnedApkDestination(directoryUri, expectedVersionCode, decision.sha256!);
    const request = Object.freeze({
      fileUri: destinationFileUri,
      expectedSha256Hex: decision.sha256!,
      expectedPackageName: decision.packageName!,
      expectedVersionCode,
      expectedVersionName: decision.latestVersion!,
      expectedSigningCertificateSha256: decision.signingCertificateSha256!,
    });
    this.prepared = null;
    try {
      await this.options.installer.verifyDownloadedApk(request);
      this.prepared = Object.freeze({ fingerprint, request });
      return;
    } catch {
      // 目标文件不存在或身份已失效时才重新下载；下载器会原子覆盖同名目标。
    }
    try {
      const downloaded = await this.options.downloader.download({
        url: downloadUrl,
        destinationFileUri,
        expectedSizeBytes: decision.fileSize!,
        maximumSizeBytes: ANDROID_APK_MAX_SIZE_BYTES,
        trustedOrigins: this.trustedOriginValues,
      });
      validateDownloadedArtifact(downloaded, destinationFileUri, decision.fileSize!, this.trustedOrigins);
      await this.options.installer.verifyDownloadedApk(request);
      this.prepared = Object.freeze({ fingerprint, request });
    } catch (error) {
      await bestEffortRemove(this.options.downloader, destinationFileUri);
      throw error;
    }
  }

  public getInstallPermissionStatus(): Promise<AndroidInstallPermissionStatus> {
    this.requireAndroidPlatform();
    return this.options.installer.getInstallPermissionStatus();
  }

  public async openInstallPermissionSettings(): Promise<void> {
    this.requireAndroidPlatform();
    await this.options.installer.openInstallPermissionSettings();
  }

  private requireAndroidPlatform(): void {
    if (this.options.platform !== "Android") {
      throw new Error("Android native update requires the Android platform.");
    }
  }
}

type PreparedAndroidApk = Readonly<{
  fingerprint: string;
  request: InstallVerifiedApkRequest;
}>;

function validateAndroidDecision(
  input: PosHandheldUpdatePolicy,
  options: AndroidNativeUpdateAdapterOptions,
  trustedOrigins: ReadonlySet<string>,
): PosHandheldUpdatePolicy {
  if (options.platform !== "Android") {
    throw new Error("Android native update requires the Android platform.");
  }
  const decision = normalizePosHandheldUpdatePolicy(input);
  if (decision.platform !== "Android" || decision.distribution !== "apk") {
    throw new Error("Android native update requires an Android APK decision.");
  }
  const packageName = requiredInstalledPackageName(options.installedPackageName);
  if (decision.packageName !== packageName) {
    throw new Error("Android update package identity does not match this app.");
  }
  const installedVersionCode = requiredInstalledVersionCode(options.installedVersionCode);
  const expectedVersionCode = Number(decision.latestBuild);
  if (!Number.isSafeInteger(expectedVersionCode) || expectedVersionCode <= installedVersionCode) {
    throw new Error("Android update build must be newer than the installed version.");
  }
  requiredTrustedDownloadUrl(decision.downloadUrl, trustedOrigins);
  if (
    decision.fileSize === null ||
    decision.sha256 === null ||
    decision.latestVersion === null ||
    decision.packageName === null ||
    decision.signingCertificateSha256 === null
  ) {
    throw new Error("Android update identity metadata is incomplete.");
  }
  return decision;
}

function androidDecisionFingerprint(decision: PosHandheldUpdatePolicy): string {
  return [
    decision.policyVersion,
    decision.latestVersion,
    decision.latestBuild,
    decision.downloadUrl,
    decision.fileSize,
    decision.sha256,
    decision.packageName,
    decision.signingCertificateSha256,
  ].join("\u001f");
}

/** 可恢复的用户授权缺失；UI 必须呈现设置入口，不能降级为普通下载失败。 */
export class AndroidInstallPermissionRequiredError extends Error {
  public readonly code = "APP_INSTALL_PERMISSION_REQUIRED";

  public constructor() {
    super("Android unknown-app-source installation permission is required.");
    this.name = "AndroidInstallPermissionRequiredError";
  }
}

export function isAndroidInstallPermissionRequiredError(
  value: unknown,
): value is Error & Readonly<{ code: "APP_INSTALL_PERMISSION_REQUIRED" }> {
  return (
    value instanceof Error &&
    (value as Error & { code?: unknown }).code ===
      "APP_INSTALL_PERMISSION_REQUIRED"
  );
}

function validateDownloadedArtifact(
  downloaded: DownloadedAndroidApk,
  destinationFileUri: string,
  expectedSizeBytes: number,
  trustedOrigins: ReadonlySet<string>,
): void {
  if (downloaded.fileUri !== destinationFileUri) {
    throw new Error("Android APK download escaped the app-owned destination.");
  }
  if (
    !Number.isSafeInteger(downloaded.sizeBytes) ||
    downloaded.sizeBytes !== expectedSizeBytes ||
    downloaded.sizeBytes > ANDROID_APK_MAX_SIZE_BYTES
  ) {
    throw new Error("Android APK size does not match backend metadata.");
  }
  requiredTrustedDownloadUrl(downloaded.finalUrl, trustedOrigins);
}

function appOwnedApkDestination(
  directoryUri: string,
  versionCode: number,
  sha256Hex: string,
): string {
  let parsed: URL;
  try {
    parsed = new URL(directoryUri);
  } catch {
    throw new Error("Android update directory URI is invalid.");
  }
  if (
    parsed.protocol !== "file:" ||
    parsed.host ||
    parsed.username ||
    parsed.password ||
    parsed.search ||
    parsed.hash
  ) {
    throw new Error("Android update directory is not app-owned.");
  }
  const decodedPath = decodeURIComponent(parsed.pathname).replace(/\/+$/u, "");
  if (!/(?:\/cache|\/files)\/hb-app-updates$/u.test(decodedPath)) {
    throw new Error("Android update directory is not app-owned.");
  }
  parsed.pathname = `${decodedPath}/`;
  const fileName = `hb-pos-handheld-${versionCode}-${sha256Hex.slice(0, 12)}.apk`;
  return new URL(fileName, parsed).toString();
}

function requiredTrustedDownloadUrl(
  value: string | null,
  trustedOrigins: ReadonlySet<string>,
): string {
  if (value === null) {
    throw new Error("Android APK trusted origin is missing.");
  }
  let parsed: URL;
  try {
    parsed = new URL(value);
  } catch {
    throw new Error("Android APK trusted origin is invalid.");
  }
  if (
    parsed.protocol !== "https:" ||
    parsed.username ||
    parsed.password ||
    parsed.hash ||
    !trustedOrigins.has(parsed.origin)
  ) {
    throw new Error("Android APK URL is not from a trusted origin.");
  }
  return parsed.toString();
}

function requiredTrustedOrigin(value: string): string {
  let parsed: URL;
  try {
    parsed = new URL(value);
  } catch {
    throw new TypeError("Android APK trusted origin is invalid.");
  }
  if (
    parsed.protocol !== "https:" ||
    parsed.username ||
    parsed.password ||
    parsed.pathname !== "/" ||
    parsed.search ||
    parsed.hash
  ) {
    throw new TypeError("Android APK trusted origin is invalid.");
  }
  return parsed.origin;
}

function requiredInstalledPackageName(value: string | null): string {
  const normalized = value?.trim() ?? "";
  if (!/^[A-Za-z][A-Za-z0-9_]*(?:\.[A-Za-z][A-Za-z0-9_]*)+$/u.test(normalized)) {
    throw new Error("Android installed package identity is unavailable.");
  }
  return normalized;
}

function requiredInstalledVersionCode(value: number | null): number {
  if (!Number.isSafeInteger(value) || value === null || value <= 0) {
    throw new Error("Android installed version code is unavailable.");
  }
  return value;
}

async function bestEffortRemove(
  downloader: AndroidApkDownloadPort,
  fileUri: string,
): Promise<void> {
  try {
    await downloader.remove(fileUri);
  } catch {
    // 原始验证/安装错误优先；清理将在下一次同目标下载前再次覆盖。
  }
}
