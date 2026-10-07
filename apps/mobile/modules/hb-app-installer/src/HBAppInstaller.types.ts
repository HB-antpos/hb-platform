/** Native Android APK downloader's integrity-bound input. */
export type DownloadApkRequest = Readonly<{
  url: string;
  destinationFileUri: string;
  expectedSizeBytes: number;
  expectedSha256Hex: string;
  trustedOrigins: readonly string[];
  /** 后台下载最多占用的带宽比例 (0,1)，原生先探测再限速；不传即全速。旧原生包忽略。 */
  bandwidthShare?: number;
}>;

/** 原生下载进度事件；destinationFileUri 原样回传 JS 发起下载时的目标 URI。 */
export type ApkDownloadProgressEvent = Readonly<{
  destinationFileUri: string;
  bytesWritten: number;
  totalBytes: number;
  /** 链路实测速率（已扣除限速补睡）；下载前 10 秒或旧原生包不带。 */
  bytesPerSecond?: number;
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
