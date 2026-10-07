export type DownloadApkRequest = Readonly<{
  url: string;
  destinationFileUri: string;
  expectedSizeBytes: number;
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
  finalUrl: string;
}>;

/** Android 当前包是否被系统允许发起 APK 安装。 */
export type InstallPermissionStatus = "granted" | "denied";

export type InstallVerifiedApkRequest = Readonly<{
  fileUri: string;
  expectedSha256Hex: string;
  expectedPackageName: string;
  expectedVersionCode: number;
  expectedVersionName: string;
  expectedSigningCertificateSha256: string;
}>;

export type InstallVerifiedApkResult = Readonly<{
  launched: true;
  packageName: string;
  versionCode: number;
}>;

export type VerifyDownloadedApkRequest = InstallVerifiedApkRequest;
