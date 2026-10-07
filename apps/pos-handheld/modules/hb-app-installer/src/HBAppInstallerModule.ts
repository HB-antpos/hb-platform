import { requireNativeModule } from "expo";

import type {
  ApkDownloadProgressEvent,
  DownloadApkRequest,
  DownloadedApkResult,
  InstallPermissionStatus,
  InstallVerifiedApkRequest,
  InstallVerifiedApkResult,
  VerifyDownloadedApkRequest,
} from "./HBAppInstaller.types";

type HBAppInstallerNativeModule = {
  /** 新原生包才会发出 onDownloadProgress；旧包或测试替身可能收不到事件。 */
  addListener?(
    eventName: "onDownloadProgress",
    listener: (event: ApkDownloadProgressEvent) => void,
  ): { remove(): void };
  getInstallPermissionStatus(): Promise<InstallPermissionStatus>;
  openInstallPermissionSettings(): Promise<void>;
  getDownloadDirectory(): Promise<string>;
  downloadApk(request: DownloadApkRequest): Promise<DownloadedApkResult>;
  removeDownloadedApk(fileUri: string): Promise<void>;
  installVerifiedApk(
    request: InstallVerifiedApkRequest,
  ): Promise<InstallVerifiedApkResult>;
  verifyDownloadedApk(request: VerifyDownloadedApkRequest): Promise<void>;
};

export default requireNativeModule<HBAppInstallerNativeModule>(
  "HBAppInstaller",
);
