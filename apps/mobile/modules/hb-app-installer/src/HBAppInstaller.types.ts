/** Native Android APK downloader's integrity-bound input. */
export type DownloadApkRequest = Readonly<{
  url: string;
  destinationFileUri: string;
  expectedSizeBytes: number;
  expectedSha256Hex: string;
  trustedOrigins: readonly string[];
}>;

/** 原生下载进度事件；destinationFileUri 原样回传 JS 发起下载时的目标 URI。 */
export type ApkDownloadProgressEvent = Readonly<{
  destinationFileUri: string;
  bytesWritten: number;
  totalBytes: number;
}>;

export type DownloadedApkResult = Readonly<{
  fileUri: string;
  sizeBytes: number;
  sha256Hex: string;
  finalUrl: string;
}>;

export type InstallPermissionStatus = "granted" | "denied";

export type InstallVerifiedApkRequest = Readonly<{
  fileUri: string;
  expectedSizeBytes: number;
  expectedSha256Hex: string;
  expectedPackageName: string;
  expectedVersionCode: number;
  expectedVersionName: string;
}>;

export type InstallVerifiedApkResult = Readonly<{
  launched: true;
  packageName: string;
  versionCode: number;
}>;
