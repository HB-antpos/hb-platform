export type AppUpdateInfoValueKey =
  | "updates.unknown"
  | "updates.noBuildVersion"
  | "updates.noChannel"
  | "updates.noUpdateId"
  | "updates.sourceEmbedded"
  | "updates.sourceOta"
  | "updates.sourceUnknown";

export type AppUpdateInfoRow = {
  key: "version" | "build" | "runtime" | "channel" | "source" | "updateId";
  labelKey: string;
  value?: string;
  valueKey?: AppUpdateInfoValueKey;
};

export type AppUpdateInfo = {
  appVersion: string | null;
  appBuildVersion: string | null;
  runtimeVersion: string | null;
  channel: string | null;
  updateId: string | null;
  isEmbeddedLaunch: boolean;
};

export type AppUpdateCheckResult =
  | { status: "development-disabled" }
  | { status: "configuration-disabled" }
  | { status: "not-available" }
  | { status: "cancelled" }
  | { status: "downloaded" };

export type AppUpdateCheckAvailability =
  | "available"
  | "development-disabled"
  | "configuration-disabled";

export type AppUpdateRunGuard = {
  isCurrent: () => boolean;
};

export type AppUpdateCheckOperations = {
  availability: AppUpdateCheckAvailability;
  checkForUpdate: () => Promise<{ isAvailable: boolean }>;
  fetchUpdate: () => Promise<unknown>;
};

const ALWAYS_CURRENT_UPDATE_RUN: AppUpdateRunGuard = {
  isCurrent: () => true,
};

export async function runAppUpdateCheck(
  operations: AppUpdateCheckOperations,
  guard: AppUpdateRunGuard = ALWAYS_CURRENT_UPDATE_RUN,
): Promise<AppUpdateCheckResult> {
  if (operations.availability === "development-disabled") {
    return { status: "development-disabled" };
  }
  if (operations.availability === "configuration-disabled") {
    return { status: "configuration-disabled" };
  }
  if (!guard.isCurrent()) {
    return { status: "cancelled" };
  }

  const update = await operations.checkForUpdate();
  // expo-updates 本身不能取消 Promise；每个 await 返回后先核验代次，避免继续下载。
  if (!guard.isCurrent()) {
    return { status: "cancelled" };
  }
  if (!update.isAvailable) {
    return { status: "not-available" };
  }

  await operations.fetchUpdate();
  if (!guard.isCurrent()) {
    return { status: "cancelled" };
  }
  return { status: "downloaded" };
}

export function resolveAppUpdateCheckAvailability(options: {
  isDev: boolean;
  isEnabled: boolean;
}): AppUpdateCheckAvailability {
  if (options.isDev) {
    return "development-disabled";
  }

  if (!options.isEnabled) {
    return "configuration-disabled";
  }

  return "available";
}

function buildValueRow(
  key: AppUpdateInfoRow["key"],
  labelKey: string,
  value: string | null,
  fallbackKey: AppUpdateInfoValueKey
): AppUpdateInfoRow {
  if (value) {
    return { key, labelKey, value };
  }

  return { key, labelKey, valueKey: fallbackKey };
}

export function resolveAppUpdateSourceKey(
  info: Pick<AppUpdateInfo, "updateId" | "isEmbeddedLaunch">
): AppUpdateInfoValueKey {
  if (info.updateId) {
    return "updates.sourceOta";
  }

  if (info.isEmbeddedLaunch) {
    return "updates.sourceEmbedded";
  }

  return "updates.sourceUnknown";
}

export function formatAppPackageVersion(
  info: Pick<AppUpdateInfo, "appVersion" | "appBuildVersion">,
  fallback: string
) {
  if (info.appVersion && info.appBuildVersion) {
    return `${info.appVersion} (${info.appBuildVersion})`;
  }

  return info.appVersion || info.appBuildVersion || fallback;
}

export type AppUpdateChannelKind = "production" | "preview" | "development" | "custom" | "none";

// 受控 OTA 会把渠道覆盖成 mobile-<环境>-<平台>-release-<时间>-<id>，安装包内置渠道则是 production / preview 等裸名。
const RELEASE_CHANNEL_PATTERN = /^mobile-(production|preview|development)-/;

/** 把 Updates.channel 归成正式 / 预览 / 开发，用于登录页等处的简短标签；原文另行展示。 */
export function resolveAppUpdateChannelKind(channel: string | null | undefined): AppUpdateChannelKind {
  const value = channel?.trim().toLowerCase();
  if (!value) return "none";
  const environment = RELEASE_CHANNEL_PATTERN.exec(value)?.[1] ?? value;
  if (environment === "production" || environment === "preview" || environment === "development") {
    return environment;
  }
  return "custom";
}

export function buildAppUpdateInfoRows(info: AppUpdateInfo): AppUpdateInfoRow[] {
  return [
    buildValueRow("version", "updates.version", info.appVersion, "updates.unknown"),
    buildValueRow("build", "updates.buildVersion", info.appBuildVersion, "updates.noBuildVersion"),
    buildValueRow("runtime", "updates.runtime", info.runtimeVersion, "updates.unknown"),
    buildValueRow("channel", "updates.channel", info.channel, "updates.noChannel"),
    {
      key: "source",
      labelKey: "updates.source",
      valueKey: resolveAppUpdateSourceKey(info),
    },
    buildValueRow("updateId", "updates.updateId", info.updateId, "updates.noUpdateId"),
  ];
}
