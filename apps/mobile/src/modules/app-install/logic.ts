import { unwrapApiEnvelope } from "@/shared/api/api-envelope";
import { buildStableAndroidDownloadUrl } from "@/modules/app-downloads/logic";

export type AppInstallPlatform = "ios" | "android";

export interface AppInstallIos {
  version: string;
  buildNumber: string;
  appStoreUrl: string;
  verifiedAt: string | null;
}

export interface AppInstallAndroid {
  easBuildId: string;
  appVersion: string | null;
  appBuildVersion: string | null;
  downloadUrl: string;
  artifactSize: number | null;
  completedAt: string | null;
}

export interface AppInstallLinks {
  ios: AppInstallIos | null;
  android: AppInstallAndroid | null;
}

type RecordValue = Record<string, unknown>;
const record = (value: unknown): RecordValue | null =>
  value && typeof value === "object" && !Array.isArray(value)
    ? (value as RecordValue)
    : null;
const text = (value: unknown) =>
  typeof value === "string" || typeof value === "number"
    ? String(value).trim() || null
    : null;
/** 二维码只接受 http(s) 地址，避免把异常数据编码成无法打开的内容。 */
const httpUrl = (value: unknown) => {
  const url = text(value);
  return url && /^https?:\/\//i.test(url) ? url : null;
};

export function parseAppInstallLinks(payload: unknown): AppInstallLinks {
  const root = record(unwrapApiEnvelope(payload)) ?? {};
  const ios = record(root.ios);
  const android = record(root.android);
  const appStoreUrl = httpUrl(ios?.appStoreUrl);
  const downloadUrl = httpUrl(android?.downloadUrl);
  const size = Number(android?.artifactSize);
  return {
    ios:
      ios && appStoreUrl
        ? {
            version: text(ios.version) ?? "—",
            buildNumber: text(ios.buildNumber) ?? "",
            appStoreUrl,
            verifiedAt: text(ios.appleVerifiedAtUtc),
          }
        : null,
    android:
      android && downloadUrl
        ? {
            easBuildId: text(android.easBuildId) ?? "",
            appVersion: text(android.appVersion),
            appBuildVersion: text(android.appBuildVersion),
            downloadUrl,
            artifactSize:
              android.artifactSize != null && Number.isFinite(size) && size > 0
                ? size
                : null,
            completedAt: text(android.completedAt),
          }
        : null,
  };
}

/**
 * 安卓二维码地址：优先后端匿名稳定入口（每次扫码都跳到最新正式包，截图转发也不会过期），
 * 连接本机/内网调试后端时稳定入口别的手机访问不到，退回本次构建的直接下载地址。
 */
export function resolveAndroidInstallUrl(
  android: AppInstallAndroid,
  apiBaseUrl: string | null | undefined,
) {
  return (
    buildStableAndroidDownloadUrl(apiBaseUrl, "mobile", "production") ??
    android.downloadUrl
  );
}

/** 默认展示当前设备所在平台，其他系统（如 web）落到 iOS。 */
export function defaultInstallPlatform(os: string): AppInstallPlatform {
  return os === "android" ? "android" : "ios";
}

export function formatArtifactSize(bytes: number | null) {
  if (!bytes) return null;
  return `${(bytes / 1024 / 1024).toFixed(1)} MB`;
}
