import type {
  AppDeviceStatus,
  AppVersionDistribution,
  AppVersionDistributionItem,
  DeviceManagementDevice,
  DeviceVersionSummary,
} from "@/modules/device-management/types";

/** 版本徽标的色调，对应 HB_COLORS 的 success / warning / brand / 中性色。 */
export type VersionTone = "ok" | "warn" | "info" | "muted";

export interface VersionBadge {
  tone: VersionTone;
  /** deviceManagement 命名空间下的文案键。 */
  labelKey: string;
  labelParams?: Record<string, string>;
  /** 徽标下方的一行提示，可为空。 */
  hintKey?: string;
  hintParams?: Record<string, string>;
}

// 与后端 DeviceVersionStateResolver 同一规则：只取开头的 major.minor.patch[.build]，忽略「+提交号」「-beta」后缀。
const VERSION_PREFIX = /^\s*[vV]?(\d+)\.(\d+)\.(\d+)(?:\.(\d+))?/;

function versionParts(raw?: string | null): number[] | null {
  if (!raw) return null;
  const match = VERSION_PREFIX.exec(raw);
  if (!match) return null;
  return match.slice(1).filter((part) => part !== undefined).map(Number);
}

/** 规范化为 1.0.96（有第四段时为 1.0.96.3）；无法识别返回 null。 */
export function normalizeVersion(raw?: string | null): string | null {
  const parts = versionParts(raw);
  return parts ? parts.join(".") : null;
}

/** 按数值比较两个版本，缺少第四段视为 0；任一无法识别时返回 null。 */
export function compareVersions(left?: string | null, right?: string | null): number | null {
  const leftParts = versionParts(left);
  const rightParts = versionParts(right);
  if (!leftParts || !rightParts) return null;
  for (let index = 0; index < Math.max(leftParts.length, rightParts.length); index += 1) {
    const diff = (leftParts[index] ?? 0) - (rightParts[index] ?? 0);
    if (diff !== 0) return diff > 0 ? 1 : -1;
  }
  return 0;
}

/** 设备注册（Windows POS）卡片的版本徽标；没有版本状态（非 Windows POS 或没有策略）时返回 null。 */
export function getRegisteredVersionBadge(device: Pick<DeviceManagementDevice, "versionState" | "targetAppVersion">): VersionBadge | null {
  const target = device.targetAppVersion ?? "";
  switch (device.versionState) {
    case "latest":
      return { tone: "ok", labelKey: "versionState.latest" };
    case "behind":
      return { tone: "warn", labelKey: "versionState.behind", labelParams: { target }, hintKey: "versionState.behindHint" };
    case "ahead":
      return { tone: "info", labelKey: "versionState.ahead", hintKey: "versionState.targetHint", hintParams: { target } };
    case "notTargeted":
      return { tone: "muted", labelKey: "versionState.notTargeted", hintKey: "versionState.targetHint", hintParams: { target } };
    case "unreported":
      return { tone: "muted", labelKey: "versionState.unreported", hintKey: "versionState.unreportedHint" };
    default:
      return null;
  }
}

export function formatAppVersion(version?: string | null, build?: string | null): string | null {
  if (version && build) return `${version} (${build})`;
  return version || build || null;
}

/** App 使用情况卡片的版本徽标。 */
export function getAppVersionBadge(device: Pick<AppDeviceStatus, "versionState" | "latestAppVersion" | "latestAppBuildVersion">): VersionBadge | null {
  const latest = formatAppVersion(device.latestAppVersion, device.latestAppBuildVersion) ?? "";
  switch (device.versionState) {
    case "latest":
      return { tone: "ok", labelKey: "versionState.appLatest" };
    case "behind":
      return { tone: "warn", labelKey: "versionState.appBehind", hintKey: "versionState.appLatestHint", hintParams: { latest } };
    case "unreported":
      return { tone: "muted", labelKey: "versionState.unreported", hintKey: "versionState.unreportedHint" };
    default:
      return null;
  }
}

export type SegmentTone = "ok" | "warn" | "info" | "muted";

export interface VersionSegment {
  /** 版本号；null 表示未上报版本。 */
  appVersion: string | null;
  total: number;
  /** 与策略目标版本的关系：等于目标 / 低于目标 / 高于目标 / 未上报或没有策略。 */
  tone: SegmentTone;
}

/** 设备注册版本分布条：每个版本一段，按相对目标版本的关系着色。 */
export function buildRegisteredVersionSegments(summary?: DeviceVersionSummary | null): VersionSegment[] {
  if (!summary) return [];
  return summary.items.map((item) => {
    const comparison = item.appVersion && summary.targetVersion ? compareVersions(item.appVersion, summary.targetVersion) : null;
    const tone: SegmentTone = comparison === null ? "muted" : comparison === 0 ? "ok" : comparison < 0 ? "warn" : "info";
    return { appVersion: item.appVersion, total: item.total, tone };
  });
}

export interface AppVersionOption {
  /** 系统 + 版本 + 构建号的稳定键，用来比较选中项。 */
  key: string;
  deviceSystem?: string;
  appVersion?: string;
  appBuildVersion?: string;
  total: number;
  online: number;
  /** 该系统在用的最高版本（后端分布按新到旧排序，每个系统的第一个有版本的行）。 */
  isLatest: boolean;
}

export interface AppVersionGroup {
  deviceSystem: string;
  options: AppVersionOption[];
}

export function appVersionKey(item: Pick<AppVersionDistributionItem, "deviceSystem" | "appVersion" | "appBuildVersion">) {
  return `${item.deviceSystem ?? ""}|${item.appVersion ?? ""}|${item.appBuildVersion ?? ""}`;
}

const SYSTEM_ORDER = ["iOS", "Android"];

/** 把版本分布按系统分组：iOS、Android 在前，其他系统按名称，没有系统的垫底；没有版本号的行不进选项（它们属于「未上报」）。 */
export function groupAppVersionDistribution(distribution?: AppVersionDistribution | null): AppVersionGroup[] {
  if (!distribution) return [];
  const groups = new Map<string, AppVersionOption[]>();
  for (const item of distribution.items) {
    if (!item.appVersion) continue;
    const system = item.deviceSystem ?? "";
    const options = groups.get(system) ?? [];
    options.push({
      key: appVersionKey(item),
      deviceSystem: item.deviceSystem,
      appVersion: item.appVersion,
      appBuildVersion: item.appBuildVersion,
      total: item.total,
      online: item.online,
      isLatest: options.length === 0,
    });
    groups.set(system, options);
  }
  const rank = (system: string) => {
    if (!system) return 99;
    const index = SYSTEM_ORDER.indexOf(system);
    return index === -1 ? 10 : index;
  };
  return Array.from(groups.entries())
    .sort(([left], [right]) => rank(left) - rank(right) || left.localeCompare(right))
    .map(([deviceSystem, options]) => ({ deviceSystem, options }));
}

export interface AppVersionStateCounts {
  latest: number;
  behind: number;
  unreported: number;
}

/**
 * 状态预设（最新 / 落后 / 未上报）的设备数，由版本分布推出：每个系统分布里的第一个有版本的行是最新，
 * 其余落后，没有版本号的行归未上报。传入 system 时只统计该系统，口径与列表的系统筛选一致。
 */
export function countAppVersionStates(distribution?: AppVersionDistribution | null, system?: string | null): AppVersionStateCounts {
  const counts: AppVersionStateCounts = { latest: 0, behind: 0, unreported: 0 };
  if (!distribution) return counts;
  const matchesSystem = (item: AppVersionDistributionItem) =>
    !system || (item.deviceSystem ?? "").toLowerCase() === system.toLowerCase();
  for (const group of groupAppVersionDistribution(distribution)) {
    if (system && group.deviceSystem.toLowerCase() !== system.toLowerCase()) continue;
    for (const option of group.options) {
      if (option.isLatest) counts.latest += option.total;
      else counts.behind += option.total;
    }
  }
  for (const item of distribution.items) {
    if (!item.appVersion && matchesSystem(item)) counts.unreported += item.total;
  }
  return counts;
}

export type RelativeAge =
  | { unit: "justNow" }
  | { unit: "minutes" | "hours" | "days"; count: number };

/** 距现在多久：1 分钟内「刚刚」，之后按分钟 / 小时 / 天；时间无法解析或在未来返回 null。 */
export function getRelativeAge(iso: string | null | undefined, now: Date = new Date()): RelativeAge | null {
  if (!iso) return null;
  const time = new Date(iso).getTime();
  if (Number.isNaN(time)) return null;
  const minutes = Math.floor((now.getTime() - time) / 60000);
  if (minutes < 0) return null;
  if (minutes < 1) return { unit: "justNow" };
  if (minutes < 60) return { unit: "minutes", count: minutes };
  const hours = Math.floor(minutes / 60);
  if (hours < 24) return { unit: "hours", count: hours };
  return { unit: "days", count: Math.floor(hours / 24) };
}

/**
 * 后端把「库里是 UTC 但不带时区」的时间当作无后缀字符串返回时，JS 会按本地时区解析而偏移。
 * 这里把没有时区后缀的 ISO 串补上 Z，保证和后端口径（UTC）一致。
 */
export function parseUtcTimestamp(raw?: string | null): string | null {
  if (!raw) return null;
  return /(?:Z|[+-]\d{2}:?\d{2})$/i.test(raw) ? raw : `${raw}Z`;
}
