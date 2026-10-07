import { requireOptionalNativeModule } from "expo-modules-core";

import type {
  ApkDownloadProgressEvent,
  DownloadApkRequest,
  DownloadedApkResult,
  InstallPermissionStatus,
  InstallVerifiedApkRequest,
  InstallVerifiedApkResult,
} from "./HBAppInstaller.types";

export type HBAppInstallerNativeModule = {
  /** 新原生包才会发出 onDownloadProgress；旧包或测试替身可能收不到事件。 */
  addListener?(
    eventName: "onDownloadProgress",
    listener: (event: ApkDownloadProgressEvent) => void,
  ): { remove(): void };
  getInstallPermissionStatus(): Promise<InstallPermissionStatus>;
  openInstallPermissionSettings(): Promise<void>;
  getDownloadDirectory(): Promise<string>;
  downloadApk(request: DownloadApkRequest): Promise<DownloadedApkResult>;
  verifyApk(request: InstallVerifiedApkRequest): Promise<{
    verified: true;
    packageName: string;
    versionCode: number;
  }>;
  removeDownloadedApk(fileUri: string): Promise<void>;
  installVerifiedApk(request: InstallVerifiedApkRequest): Promise<InstallVerifiedApkResult>;
};

/**
 * 新包可选发现原生安装器；旧原生包没有该模块时，JS 必须安全降级到兼容下载器。
 */
export default requireOptionalNativeModule<HBAppInstallerNativeModule>("HBAppInstaller");
