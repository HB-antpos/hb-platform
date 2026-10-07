import type {
  DownloadApkRequest as NativeDownloadApkRequest,
  DownloadedApkResult as NativeDownloadedApkResult,
  InstallPermissionStatus,
} from "../../../modules/hb-app-installer/src/HBAppInstaller.types";

import {
  AndroidNativeUpdateAdapter,
  type AndroidApkDownloadPort,
  type AndroidApkDownloadRequest,
  type AndroidAppInstallerPort,
  type AndroidNativeUpdatePort,
  type DownloadedAndroidApk,
  type InstallVerifiedApkRequest,
  type InstallVerifiedApkResult,
  type VerifyDownloadedApkRequest,
} from "./android-native-update-adapter";
import { createApkDownloadProgressForwarder } from "./apk-download-progress";

export type HbAppInstallerNativeContract =
  (typeof import("../../../modules/hb-app-installer/src/HBAppInstallerModule"))["default"];

type NativeModuleLoader = () => Promise<HbAppInstallerNativeContract>;

/** 动态加载 Android-only module，iOS 启动不会触发 requireNativeModule。 */
export class ExpoHbAppInstallerBridge implements AndroidAppInstallerPort {
  private nativeModule: Promise<HbAppInstallerNativeContract> | null = null;

  public constructor(
    private readonly loadNativeModule: NativeModuleLoader = async () =>
      (await import(
        "../../../modules/hb-app-installer/src/HBAppInstallerModule"
      )).default,
  ) {}

  public async getInstallPermissionStatus(): Promise<InstallPermissionStatus> {
    return (await this.load()).getInstallPermissionStatus();
  }

  public async openInstallPermissionSettings(): Promise<void> {
    await (await this.load()).openInstallPermissionSettings();
  }

  public async getDownloadDirectory(): Promise<string> {
    return (await this.load()).getDownloadDirectory();
  }

  public async downloadApk(
    request: NativeDownloadApkRequest,
  ): Promise<NativeDownloadedApkResult> {
    return (await this.load()).downloadApk(request);
  }

  public async removeDownloadedApk(fileUri: string): Promise<void> {
    await (await this.load()).removeDownloadedApk(fileUri);
  }

  /**
   * 订阅本次下载目标的原生进度。旧原生包没有该事件时订阅照常成功但永远不触发，
   * 界面保持不确定状态；订阅本身失败也只退回不确定状态，不影响下载。
   */
  public async subscribeDownloadProgress(
    destinationFileUri: string,
    listener: (bytesWritten: number) => void,
  ): Promise<() => void> {
    const nativeModule = await this.load();
    if (typeof nativeModule.addListener !== "function") return noop;
    try {
      const subscription = nativeModule.addListener("onDownloadProgress", (event) => {
        if (
          event?.destinationFileUri === destinationFileUri &&
          typeof event.bytesWritten === "number"
        ) {
          listener(event.bytesWritten);
        }
      });
      return () => {
        try {
          subscription?.remove();
        } catch {
          // 退订失败不影响下载结果。
        }
      };
    } catch {
      return noop;
    }
  }

  public async installVerifiedApk(
    request: InstallVerifiedApkRequest,
  ): Promise<InstallVerifiedApkResult> {
    return (await this.load()).installVerifiedApk(request);
  }

  public async verifyDownloadedApk(request: VerifyDownloadedApkRequest): Promise<void> {
    await (await this.load()).verifyDownloadedApk(request);
  }

  private load(): Promise<HbAppInstallerNativeContract> {
    this.nativeModule ??= this.loadNativeModule();
    return this.nativeModule;
  }
}

type ExpoAndroidApkDownloaderDependencies = Readonly<{
  downloadApk(
    request: NativeDownloadApkRequest,
  ): Promise<NativeDownloadedApkResult>;
  removeDownloadedApk(fileUri: string): Promise<void>;
  subscribeDownloadProgress?(
    destinationFileUri: string,
    listener: (bytesWritten: number) => void,
  ): Promise<() => void>;
}>;

export class ExpoAndroidApkDownloader implements AndroidApkDownloadPort {
  public constructor(
    private readonly dependencies: ExpoAndroidApkDownloaderDependencies =
      defaultDownloaderDependencies(),
  ) {}

  public async download(
    input: AndroidApkDownloadRequest,
  ): Promise<DownloadedAndroidApk> {
    validateDownloadBounds(input);
    // 先订阅再发起下载，避免漏掉原生开始写盘时的 0% 首帧。
    const stopProgress = await this.subscribeProgress(input);
    let downloaded: NativeDownloadedApkResult;
    try {
      downloaded = await this.dependencies.downloadApk(
        Object.freeze({
          url: input.url,
          destinationFileUri: input.destinationFileUri,
          expectedSizeBytes: input.expectedSizeBytes,
          trustedOrigins: Object.freeze([...input.trustedOrigins]),
        }),
      );
    } finally {
      stopProgress();
    }
    if (downloaded.fileUri !== input.destinationFileUri) {
      throw new Error("Android APK native download escaped its destination.");
    }
    if (
      !Number.isSafeInteger(downloaded.sizeBytes) ||
      downloaded.sizeBytes !== input.expectedSizeBytes ||
      downloaded.sizeBytes > input.maximumSizeBytes
    ) {
      throw new Error("Android APK native download size does not match metadata.");
    }
    validateFinalUrl(downloaded.finalUrl, input.trustedOrigins);
    return Object.freeze({ ...downloaded });
  }

  public remove(fileUri: string): Promise<void> {
    return this.dependencies.removeDownloadedApk(fileUri);
  }

  private async subscribeProgress(
    input: AndroidApkDownloadRequest,
  ): Promise<() => void> {
    const { onProgress } = input;
    if (!onProgress || !this.dependencies.subscribeDownloadProgress) return noop;
    try {
      return await this.dependencies.subscribeDownloadProgress(
        input.destinationFileUri,
        // 总大小以服务端已验证元数据为准，不信任事件里的 totalBytes。
        createApkDownloadProgressForwarder(input.expectedSizeBytes, onProgress),
      );
    } catch {
      return noop;
    }
  }
}

function noop(): void {
  // 无进度事件时的空退订。
}

export function createExpoAndroidNativeUpdatePort(input: Readonly<{
  platform: unknown;
  trustedDownloadOrigins: readonly string[];
  installedPackageName: string | null;
  installedVersionCode: number | null;
}>): AndroidNativeUpdatePort {
  const nativeBridge = new ExpoHbAppInstallerBridge();
  return new AndroidNativeUpdateAdapter({
    ...input,
    downloader: new ExpoAndroidApkDownloader(nativeBridge),
    installer: nativeBridge,
  });
}

function validateDownloadBounds(input: AndroidApkDownloadRequest): void {
  if (
    !Number.isSafeInteger(input.expectedSizeBytes) ||
    !Number.isSafeInteger(input.maximumSizeBytes) ||
    input.expectedSizeBytes <= 0 ||
    input.maximumSizeBytes <= 0 ||
    input.expectedSizeBytes > input.maximumSizeBytes ||
    input.trustedOrigins.length === 0
  ) {
    throw new Error("Android APK expected size is invalid.");
  }
}

function defaultDownloaderDependencies(): ExpoAndroidApkDownloaderDependencies {
  return new ExpoHbAppInstallerBridge();
}

function validateFinalUrl(
  value: string,
  trustedOrigins: readonly string[],
): void {
  let parsed: URL;
  try {
    parsed = new URL(value);
  } catch {
    throw new Error("Android APK native final URL is invalid.");
  }
  if (
    parsed.protocol !== "https:" ||
    parsed.username ||
    parsed.password ||
    parsed.hash ||
    !trustedOrigins.includes(parsed.origin)
  ) {
    throw new Error("Android APK native final URL is not trusted.");
  }
}
