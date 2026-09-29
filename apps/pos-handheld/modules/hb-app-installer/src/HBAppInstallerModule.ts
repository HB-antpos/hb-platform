import { requireNativeModule } from "expo";

import type {
  DownloadApkRequest,
  DownloadedApkResult,
  InstallPermissionStatus,
  InstallVerifiedApkRequest,
  InstallVerifiedApkResult,
  VerifyDownloadedApkRequest,
} from "./HBAppInstaller.types";

type HBAppInstallerNativeModule = {
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
