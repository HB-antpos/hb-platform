export type DownloadApkRequest = Readonly<{
  url: string;
  destinationFileUri: string;
  expectedSizeBytes: number;
  trustedOrigins: readonly string[];
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
