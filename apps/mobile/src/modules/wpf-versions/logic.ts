import type {
  WpfPolicySummary,
  WpfRelease,
  WpfReleasePolicyRequest,
  WpfTargetScope,
} from "./types";

export const WPF_RELEASE_CHANNELS = ["production", "preview"] as const;

export function normalizeTargetScope(
  value: string | null | undefined,
): WpfTargetScope {
  const normalized = value?.trim().toLowerCase();
  return normalized === "stores" || normalized === "devices"
    ? normalized
    : "all";
}

export function normalizeVersion(value: string) {
  return value.trim().replace(/^v/i, "");
}

function parseVersion(value: string) {
  const match = normalizeVersion(value).match(
    /^(\d+)\.(\d+)\.(\d+)(?:\.(\d+))?$/,
  );
  return match ? match.slice(1).map((part) => Number(part ?? 0)) : null;
}

export function compareVersions(left: string, right: string) {
  const a = parseVersion(left);
  const b = parseVersion(right);
  if (!a || !b) return null;
  for (let index = 0; index < Math.max(a.length, b.length); index += 1) {
    const difference = (a[index] ?? 0) - (b[index] ?? 0);
    if (difference !== 0) return difference;
  }
  return 0;
}

export function getPolicyValidationError(
  input: Pick<
    WpfReleasePolicyRequest,
    | "targetVersion"
    | "minimumSupportedVersion"
    | "targetScope"
    | "targetStoreGuids"
    | "targetDeviceRegistrationIds"
  > & { activeVersions?: string[] },
) {
  const targetVersion = normalizeVersion(input.targetVersion);
  const minimumVersion = normalizeVersion(input.minimumSupportedVersion);
  if (!targetVersion || !minimumVersion) return "requiredVersions" as const;
  if (!parseVersion(targetVersion) || !parseVersion(minimumVersion))
    return "invalidVersion" as const;
  if (input.activeVersions && !input.activeVersions.includes(targetVersion))
    return "targetVersionUnavailable" as const;
  if (input.activeVersions && !input.activeVersions.includes(minimumVersion))
    return "minimumVersionUnavailable" as const;
  const range = compareVersions(minimumVersion, targetVersion);
  if (range !== null && range > 0) return "minimumAboveTarget" as const;
  const targetScope = normalizeTargetScope(input.targetScope);
  if (
    targetScope === "stores" &&
    input.targetStoreGuids.filter((value) => value.trim()).length === 0
  )
    return "storesRequired" as const;
  if (
    targetScope === "devices" &&
    input.targetDeviceRegistrationIds.filter(
      (value) => Number.isInteger(value) && value > 0,
    ).length === 0
  )
    return "devicesRequired" as const;
  return null;
}

export function canSavePolicy(
  input: Pick<
    WpfReleasePolicyRequest,
    | "targetVersion"
    | "minimumSupportedVersion"
    | "targetScope"
    | "targetStoreGuids"
    | "targetDeviceRegistrationIds"
  > & { activeVersions?: string[] },
) {
  return getPolicyValidationError(input) === null;
}

export function policySummaryMatchesRequest(
  request: WpfReleasePolicyRequest,
  summary: WpfPolicySummary | null,
) {
  if (!summary) return false;
  const requestStores =
    request.targetScope === "stores"
      ? [...new Set(request.targetStoreGuids)].sort()
      : [];
  const summaryStores =
    summary.targetScope === "stores"
      ? [...new Set(summary.targetStoreGuids)].sort()
      : [];
  const requestDevices =
    request.targetScope === "devices"
      ? [...new Set(request.targetDeviceRegistrationIds)].sort((a, b) => a - b)
      : [];
  const summaryDevices =
    summary.targetScope === "devices"
      ? [...new Set(summary.targetDeviceRegistrationIds)].sort((a, b) => a - b)
      : [];
  return (
    request.channel.trim().toLowerCase() ===
      summary.channel.trim().toLowerCase() &&
    normalizeVersion(request.targetVersion) ===
      normalizeVersion(summary.targetVersion) &&
    normalizeVersion(request.minimumSupportedVersion) ===
      normalizeVersion(summary.minimumSupportedVersion) &&
    Boolean(request.forceUpdate) === Boolean(summary.forceUpdate) &&
    normalizeTargetScope(request.targetScope) ===
      normalizeTargetScope(summary.targetScope) &&
    JSON.stringify(requestStores) === JSON.stringify(summaryStores) &&
    JSON.stringify(requestDevices) === JSON.stringify(summaryDevices)
  );
}

export function inferRollback(
  targetVersion: string,
  currentTargetVersion: string | null | undefined,
) {
  if (!currentTargetVersion) return false;
  const comparison = compareVersions(
    normalizeVersion(targetVersion),
    normalizeVersion(currentTargetVersion),
  );
  return comparison !== null && comparison < 0;
}

export function getPolicySummary(
  releases: WpfRelease[],
): WpfPolicySummary | null {
  const carrier =
    releases.find((item) => item.targetVersion?.trim()) ??
    releases.find((item) => item.isCurrent);
  if (!carrier) return null;
  const targetVersion = carrier.targetVersion?.trim() || carrier.version.trim();
  if (!targetVersion) return null;
  const scope = normalizeTargetScope(carrier.targetScope);
  const targetStoreGuids = scope === "stores" ? carrier.targetStoreGuids : [];
  const targetDeviceRegistrationIds =
    scope === "devices" ? carrier.targetDeviceRegistrationIds : [];
  const currentRelease = releases.find(
    (item) => item.isCurrent || item.version.trim() === targetVersion,
  );
  return {
    channel: currentRelease?.channel ?? carrier.channel,
    targetVersion,
    minimumSupportedVersion:
      carrier.minimumSupportedVersion?.trim() || targetVersion,
    forceUpdate: Boolean(carrier.forceUpdate || currentRelease?.forceUpdate),
    targetScope: scope,
    targetStoreGuids,
    targetDeviceRegistrationIds,
    targetStoreSummaries:
      scope === "stores" ? carrier.targetStoreSummaries : [],
    targetDeviceSummaries:
      scope === "devices" ? carrier.targetDeviceSummaries : [],
    policyUpdatedAt: carrier.policyUpdatedAt,
    policyUpdatedBy: carrier.policyUpdatedBy,
  };
}

export function createLatestRequestGuard() {
  let latest = 0;
  return {
    next() {
      latest += 1;
      return latest;
    },
    invalidate() {
      latest += 1;
    },
    isCurrent(requestId: number) {
      return requestId === latest;
    },
  };
}

export function maskSha256(value: string | null) {
  if (!value) return "-";
  const trimmed = value.trim();
  return trimmed.length > 18
    ? `${trimmed.slice(0, 12)}…${trimmed.slice(-6)}`
    : trimmed;
}

export function formatFileSize(value: number | null) {
  if (!value || value <= 0) return "-";
  if (value >= 1024 * 1024 * 1024)
    return `${(value / 1024 / 1024 / 1024).toFixed(2)} GB`;
  if (value >= 1024 * 1024) return `${(value / 1024 / 1024).toFixed(2)} MB`;
  return `${(value / 1024).toFixed(1)} KB`;
}

export function getErrorMessage(error: unknown, fallback: string) {
  return error instanceof Error && error.message.trim()
    ? error.message
    : fallback;
}

/**
 * 更新方式（与 Web 版本发布中心 summarizeWpfLane、后端 WpfAppReleaseService 同口径）：
 * - required：开了强制更新，或最低支持版本不低于目标（低于目标的机器全部落在强制区间）；
 * - minimum：最低支持版本低于目标，低于最低版本的机器强制、其余可选；
 * - optional：只做可选提醒。
 * 摘要里的 minimumSupportedVersion 在未存时会回退成目标版本（仅供展示），所以这里读策略载体上的原始值。
 */
export type WpfPolicyMode = "optional" | "minimum" | "required";

export function getWpfPolicyMode(
  releases: readonly WpfRelease[],
  summary: Pick<WpfPolicySummary, "targetVersion" | "forceUpdate">,
): { mode: WpfPolicyMode; minimum: string | null } {
  const carrier =
    releases.find((item) => item.targetVersion?.trim()) ??
    releases.find((item) => item.isCurrent);
  const storedMinimum = carrier?.minimumSupportedVersion?.trim() || null;
  const minimumVsTarget = storedMinimum
    ? compareVersions(storedMinimum, summary.targetVersion)
    : null;
  const minimumBelowTarget = minimumVsTarget !== null && minimumVsTarget < 0;
  const minimumCoversTarget = minimumVsTarget !== null && minimumVsTarget >= 0;
  if (summary.forceUpdate || minimumCoversTarget)
    return { mode: "required", minimum: null };
  return minimumBelowTarget
    ? { mode: "minimum", minimum: storedMinimum }
    : { mode: "optional", minimum: null };
}

/** 比目标版本更新、且已启用的版本（新到旧）：有则说明「有新版本未投放」。 */
export function getWpfNewerActiveVersions(
  releases: readonly WpfRelease[],
  targetVersion: string,
): string[] {
  const versions = releases
    .filter((item) => item.isActive)
    .map((item) => normalizeVersion(item.version))
    .filter((version) => (compareVersions(version, targetVersion) ?? 0) > 0);
  return [...new Set(versions)].sort(
    (left, right) => compareVersions(right, left) ?? 0,
  );
}

export type WpfDecisionKind = "force" | "optional" | "latest" | "rollback";

export interface WpfDecisionSegment {
  kind: WpfDecisionKind;
  /** 区间边界版本，由界面拼成「低于 X」「= X」「高于 X」。 */
  bound: string;
  /** between：不低于最低版本、未到目标。 */
  variant?: "below" | "between";
}

/**
 * 设备决策预览（与 Web buildDecisionLadder 的 WPF 分支一致）：
 * 机器按自身版本落在哪一段就收到哪种提示；高于目标的机器会被提示回退，
 * 回退是否强制只取决于强制更新开关。范围外的机器保持现状，由界面另行说明。
 */
export function buildWpfDecisionLadder(
  mode: WpfPolicyMode,
  targetVersion: string,
  minimum: string | null,
): WpfDecisionSegment[] {
  const segments: WpfDecisionSegment[] = [];
  if (mode === "required") {
    segments.push({ kind: "force", bound: targetVersion, variant: "below" });
  } else if (mode === "minimum" && minimum) {
    segments.push({ kind: "force", bound: minimum, variant: "below" });
    segments.push({
      kind: "optional",
      bound: targetVersion,
      variant: "between",
    });
  } else {
    segments.push({ kind: "optional", bound: targetVersion, variant: "below" });
  }
  segments.push({ kind: "latest", bound: targetVersion });
  segments.push({ kind: "rollback", bound: targetVersion });
  return segments;
}
