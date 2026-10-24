import type { ExpoPosUpdateIdentity } from "./expo-pos-runtime";

export type NativeAppVersion = Readonly<{
  appVersion: string;
  buildNumber: string;
}>;

export const UNKNOWN_NATIVE_APP_VERSION: NativeAppVersion = Object.freeze({
  appVersion: "unknown",
  buildNumber: "unknown",
});

export type NativeAppVersionSources = Readonly<{
  applicationVersion?: string | null | undefined;
  constantsVersion?: string | null | undefined;
  configVersion?: string | null | undefined;
  applicationBuild?: string | null | undefined;
  constantsBuild?: string | null | undefined;
}>;

/**
 * 解析原生壳的版本号与构建号（即 TestFlight 显示的 0.2.0 (11)）。
 * build 号由 EAS 远端自增，配置里的 ios.buildNumber 不可信，必须优先用原生值。
 * 只接收已读出的值、不直接引用原生模块，路由层与测试都不必加载 expo-application。
 */
export function resolveNativeAppVersion(
  sources: NativeAppVersionSources,
): NativeAppVersion {
  return Object.freeze({
    appVersion:
      nonEmpty(sources.applicationVersion) ??
      nonEmpty(sources.constantsVersion) ??
      nonEmpty(sources.configVersion) ??
      UNKNOWN_NATIVE_APP_VERSION.appVersion,
    buildNumber:
      nonEmpty(sources.applicationBuild) ??
      nonEmpty(sources.constantsBuild) ??
      UNKNOWN_NATIVE_APP_VERSION.buildNumber,
  });
}

/** 运行内嵌包时显示 embedded，运行 OTA 时显示更新 ID 前 8 位，便于和发布记录对照。 */
export function otaVersionLabel(
  identity: ExpoPosUpdateIdentity | null | undefined,
): string {
  const updateId = identity?.updateId?.trim().toLowerCase();
  return identity?.isEmbeddedLaunch === false && updateId
    ? updateId.slice(0, 8)
    : "embedded";
}

/** 收银页头部的版本行；运行 OTA 时附上更新 ID，避免只看 build 号分不清 JS 版本。 */
export function formatAppVersionLabel(
  version: NativeAppVersion,
  otaVersion: string,
): string {
  const base = `Version ${version.appVersion} · Build ${version.buildNumber}`;
  return otaVersion === "embedded" ? base : `${base} · OTA ${otaVersion}`;
}

function nonEmpty(value: string | null | undefined): string | null {
  const trimmed = value?.trim();
  return trimmed ? trimmed : null;
}
