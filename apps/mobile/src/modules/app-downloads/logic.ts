import type {
  AppDownloadAppKey,
  AppDownloadEnvironment,
  AppDownloadsSection,
  HandheldCandidate,
  HandheldPolicy,
  NativePolicy,
  NativePolicyForm,
  NativeRelease,
  OtaPolicyForm,
  TargetScope,
} from "./types";

export type AppDownloadsApp = "mobile" | "ipad" | "handheld";
export type AppDownloadsChannel = "native" | "ota";

/** 两级导航（应用 × 渠道）映射回原有数据分区；手持两个渠道共用同一份策略数据，界面按通道过滤。 */
export function resolveAppDownloadsSection(
  app: AppDownloadsApp,
  channel: AppDownloadsChannel,
): AppDownloadsSection {
  if (app === "handheld") return "pos-handheld";
  if (app === "ipad") return channel === "native" ? "ipad-native" : "ipad-ota";
  return channel === "native" ? "mobile-native" : "mobile-ota";
}

/** 本机或内网地址：其他设备扫码访问不到，不能拿来拼分享链接。 */
function isPrivateHost(hostname: string) {
  const host = hostname.toLowerCase();
  if (host === "localhost" || host.endsWith(".local")) return true;
  const parts = host.split(".").map(Number);
  if (parts.length !== 4 || parts.some((n) => !Number.isInteger(n)))
    return false;
  const [a, b] = parts;
  return (
    a === 127 ||
    a === 10 ||
    (a === 192 && b === 168) ||
    (a === 172 && b >= 16 && b <= 31)
  );
}

/**
 * Android 稳定下载入口：后端匿名接口每次访问都按同一口径重定向到最新构建，
 * 分享出去的链接和二维码不会随某次构建的地址过期。
 * 连接本机或内网后端调试时返回 null，由调用方回退到构建产物地址。
 */
export function buildStableAndroidDownloadUrl(
  apiBaseUrl: string | null | undefined,
  appKey: AppDownloadAppKey,
  profile: AppDownloadEnvironment,
) {
  const base = apiBaseUrl?.trim().replace(/\/+$/, "");
  if (!base || !/^https?:\/\//i.test(base)) return null;
  try {
    if (isPrivateHost(new URL(base).hostname)) return null;
  } catch {
    return null;
  }
  const path =
    appKey === "pos-handheld"
      ? "/mobile-app-builds/pos-handheld/android-latest/download"
      : "/mobile-app-builds/android-latest/download";
  return `${base}${path}?profile=${encodeURIComponent(profile)}`;
}

/** 手持 iOS 原生下载：优先已启用策略绑定的候选，否则取最新的带商店链接候选。 */
export function pickHandheldIosDownload(
  policies: Pick<
    HandheldPolicy,
    "lane" | "enabled" | "candidateId" | "candidate"
  >[],
  candidates: HandheldCandidate[],
): { candidate: HandheldCandidate; active: boolean } | null {
  const withUrl = candidates.filter(
    (item) => item.lane === "ios-native" && item.appStoreUrl,
  );
  const policy = policies.find((item) => item.lane === "ios-native");
  if (policy?.enabled && policy.candidateId) {
    const bound =
      withUrl.find((item) => item.id === policy.candidateId) ??
      (policy.candidate?.id === policy.candidateId &&
      policy.candidate.appStoreUrl
        ? policy.candidate
        : undefined);
    if (bound) return { candidate: bound, active: true };
  }
  const latest = [...withUrl].sort((a, b) =>
    b.createdAt.localeCompare(a.createdAt),
  )[0];
  return latest ? { candidate: latest, active: false } : null;
}

export function nativePolicyFormFrom(
  policy: Pick<
    NativePolicy,
    | "enabled"
    | "releaseId"
    | "minimumSupportedVersion"
    | "minimumSupportedBuildNumber"
    | "releaseMessage"
    | "targetScope"
    | "targetStoreGuids"
  >,
): NativePolicyForm {
  return {
    enabled: policy.enabled,
    releaseId: policy.releaseId ?? "",
    minimumSupportedVersion: policy.minimumSupportedVersion ?? "",
    minimumSupportedBuildNumber:
      policy.minimumSupportedBuildNumber == null
        ? ""
        : String(policy.minimumSupportedBuildNumber),
    releaseMessage: policy.releaseMessage ?? "",
    targetScope: policy.targetScope,
    targetStoreGuids: policy.targetStoreGuids,
  };
}

export type PolicyField =
  | "enabled"
  | "required"
  | "release"
  | "minimumVersion"
  | "minimumBuild"
  | "message"
  | "scope";

function sameStores(a: string[] | undefined, b: string[] | undefined) {
  const left = [...(a ?? [])].sort();
  const right = [...(b ?? [])].sort();
  return (
    left.length === right.length &&
    left.every((value, index) => value === right[index])
  );
}

function scopeChanged(
  base: { targetScope?: TargetScope; targetStoreGuids?: string[] },
  next: { targetScope?: TargetScope; targetStoreGuids?: string[] },
) {
  const baseScope = base.targetScope ?? "all";
  const nextScope = next.targetScope ?? "all";
  if (baseScope !== nextScope) return true;
  return (
    nextScope === "stores" &&
    !sameStores(base.targetStoreGuids, next.targetStoreGuids)
  );
}

/** 底部保存栏的「N 项修改」：只比较管理员可编辑的字段，文本按去空白后比较。 */
export function diffNativePolicyForm(
  base: NativePolicyForm,
  next: NativePolicyForm,
  targeted = false,
): PolicyField[] {
  const changed: PolicyField[] = [];
  if (base.enabled !== next.enabled) changed.push("enabled");
  if (Boolean(base.required) !== Boolean(next.required))
    changed.push("required");
  if (base.releaseId.trim() !== next.releaseId.trim()) changed.push("release");
  if (
    base.minimumSupportedVersion.trim() !== next.minimumSupportedVersion.trim()
  )
    changed.push("minimumVersion");
  if (
    base.minimumSupportedBuildNumber.trim() !==
    next.minimumSupportedBuildNumber.trim()
  )
    changed.push("minimumBuild");
  if (base.releaseMessage.trim() !== next.releaseMessage.trim())
    changed.push("message");
  if (targeted && scopeChanged(base, next)) changed.push("scope");
  return changed;
}

export function diffOtaPolicyForm(
  base: OtaPolicyForm,
  next: OtaPolicyForm,
  targeted = false,
): PolicyField[] {
  const changed: PolicyField[] = [];
  if (base.enabled !== next.enabled) changed.push("enabled");
  if (base.required !== next.required) changed.push("required");
  if (base.targetReleaseId.trim() !== next.targetReleaseId.trim())
    changed.push("release");
  if (base.releaseMessage.trim() !== next.releaseMessage.trim())
    changed.push("message");
  if (targeted && scopeChanged(base, next)) changed.push("scope");
  return changed;
}

export interface ConfirmationLabels {
  enabled: string;
  disabled: string;
  release: string;
  noRelease: string;
  required: string;
  optional: string;
  allDevices: string;
  minimumVersion: string;
  minimumBuild: string;
  notes: string;
  scope: string;
  /** 标签与值之间的冒号，中文「：」英文「: 」；缺省按中文。 */
  colon?: string;
}

const INT32_MAX = 2_147_483_647;

export function normalizeText(value: string | null | undefined) {
  return value?.trim() || null;
}

/** 保留当前策略绑定但已退出候选分页的版本，避免保存时误报 releaseRequired。 */
export function mergeHandheldPolicyCandidates(
  candidates: HandheldCandidate[],
  policies: Pick<HandheldPolicy, "lane" | "candidateId" | "candidate">[],
) {
  const merged = new Map(
    candidates.map((candidate) => [
      `${candidate.lane}:${candidate.id}`,
      candidate,
    ]),
  );
  for (const policy of policies) {
    const candidate = policy.candidate;
    if (
      candidate &&
      candidate.id === policy.candidateId &&
      candidate.lane === policy.lane
    ) {
      const key = `${candidate.lane}:${candidate.id}`;
      if (!merged.has(key)) merged.set(key, candidate);
    }
  }
  return [...merged.values()];
}

/**
 * iOS/iPad 原生包只经 App Store 分发，安装包表只收 Android APK；
 * 下载入口优先取已启用策略指向的版本，否则取最新登记版本（接口已按 Apple 核验时间倒序）。
 */
export function pickNativeDownloadRelease(
  releases: NativeRelease[],
  policy: Pick<NativePolicy, "enabled" | "releaseId">,
): { release: NativeRelease; active: boolean } | null {
  const active =
    policy.enabled && policy.releaseId
      ? releases.find((release) => release.id === policy.releaseId)
      : undefined;
  if (active) return { release: active, active: true };
  return releases[0] ? { release: releases[0], active: false } : null;
}

export function parseBuildNumber(value: string, required = false) {
  const normalized = value.trim();
  if (!normalized && !required) return null;
  if (!/^\d+$/.test(normalized)) return null;
  const parsed = Number(normalized);
  return Number.isInteger(parsed) && parsed >= 0 && parsed <= INT32_MAX
    ? parsed
    : null;
}

export function parseHandheldBuildNumber(value: string) {
  const normalized = value.trim();
  if (!/^[1-9]\d*$/.test(normalized)) return null;
  const parsed = Number(normalized);
  return Number.isInteger(parsed) && parsed > 0 && parsed <= INT32_MAX
    ? parsed
    : null;
}

function parseHandheldRegistrationBuildNumber(value: string) {
  const normalized = value.trim();
  if (!/^[1-9]\d*$/.test(normalized)) return null;
  const parsed = Number(normalized);
  return Number.isSafeInteger(parsed) && parsed > 0 ? parsed : null;
}

export type PolicyValidationError =
  | "releaseRequired"
  | "minimumVersionRequired"
  | "minimumBuildInvalid"
  | "storesRequired"
  | "candidateBlocked";

export type RegistrationValidationError =
  | "appStoreIdInvalid"
  | "buildNumberInvalid"
  | "storefrontInvalid";

export function validateRegistration(
  app: "mobile-ios" | "pos-ipad" | "pos-handheld",
  value: { appStoreId: string; buildNumber: string; storefront: string },
): RegistrationValidationError | null {
  if (!/^\d{6,20}$/.test(value.appStoreId.trim())) return "appStoreIdInvalid";
  const build =
    app === "pos-handheld"
      ? parseHandheldRegistrationBuildNumber(value.buildNumber)
      : parseBuildNumber(value.buildNumber, true);
  if (build === null) return "buildNumberInvalid";
  if (!/^[A-Za-z]{2}$/.test(value.storefront.trim()))
    return "storefrontInvalid";
  return null;
}

export function validateNativePolicy(
  form: NativePolicyForm,
  targeted = false,
): PolicyValidationError | null {
  if (!form.enabled) return null;
  if (!form.releaseId.trim()) return "releaseRequired";
  if (
    form.minimumSupportedBuildNumber.trim() &&
    !form.minimumSupportedVersion.trim()
  )
    return "minimumVersionRequired";
  if (
    form.minimumSupportedBuildNumber.trim() &&
    parseBuildNumber(form.minimumSupportedBuildNumber) === null
  )
    return "minimumBuildInvalid";
  if (
    targeted &&
    form.targetScope === "stores" &&
    form.targetStoreGuids.length === 0
  )
    return "storesRequired";
  return null;
}

export function validateOtaPolicy(
  form: OtaPolicyForm,
): PolicyValidationError | null {
  if (!form.enabled) return null;
  if (!form.targetReleaseId.trim()) return "releaseRequired";
  return null;
}

export function validateTargetScope(
  targetScope: TargetScope | undefined,
  targetStoreGuids: string[] | undefined,
): PolicyValidationError | null {
  return targetScope === "stores" && (targetStoreGuids?.length ?? 0) === 0
    ? "storesRequired"
    : null;
}

export function validateHandheldCandidate(
  candidate: {
    id: string;
    activatable: boolean;
    blockedReason: string | null;
  } | null,
  candidateValid: boolean,
  policyBlockedReason: string | null = null,
): PolicyValidationError | null {
  if (!candidate || !candidate.id.trim()) return "releaseRequired";
  const blockedReason = policyBlockedReason ?? candidate.blockedReason;
  if (
    !candidateValid &&
    blockedReason !== "POS_HANDHELD_UPDATE_CANDIDATE_FINGERPRINT_MISMATCH"
  )
    return "candidateBlocked";
  if (!candidate.activatable) return "candidateBlocked";
  return null;
}

export function validateHandheldPolicy(
  form: NativePolicyForm,
  policyLane: string,
  candidate: {
    id: string;
    activatable: boolean;
    blockedReason: string | null;
  } | null,
  candidateValid: boolean,
  policyBlockedReason: string | null = null,
): PolicyValidationError | null {
  if (!form.enabled) return null;
  const nativeError = validateNativePolicy(form);
  if (nativeError) return nativeError;
  const candidateError = validateHandheldCandidate(
    candidate,
    candidateValid,
    policyBlockedReason,
  );
  if (candidateError) return candidateError;
  if (
    policyLane.endsWith("native") &&
    form.minimumSupportedBuildNumber.trim() &&
    parseHandheldBuildNumber(form.minimumSupportedBuildNumber) === null
  )
    return "minimumBuildInvalid";
  return null;
}

export function buildNativePolicyPayload(
  form: NativePolicyForm,
  expectedPolicyVersion: number,
  targeted = false,
) {
  if (!form.enabled) {
    return {
      expectedPolicyVersion,
      enabled: false,
      releaseId: null,
      minimumSupportedVersion: null,
      minimumSupportedBuildNumber: null,
      releaseMessage: null,
      ...(targeted
        ? { targetScope: "all" as const, targetStoreGuids: [] }
        : {}),
    };
  }
  const targetScope: TargetScope =
    targeted && form.targetScope === "stores" ? "stores" : "all";
  return {
    expectedPolicyVersion,
    enabled: true,
    releaseId: normalizeText(form.releaseId),
    minimumSupportedVersion: normalizeText(form.minimumSupportedVersion),
    minimumSupportedBuildNumber: parseBuildNumber(
      form.minimumSupportedBuildNumber,
    ),
    releaseMessage: normalizeText(form.releaseMessage),
    ...(targeted
      ? {
          targetScope,
          targetStoreGuids:
            targetScope === "stores" ? form.targetStoreGuids : [],
        }
      : {}),
  };
}

export function buildOtaPolicyPayload(
  form: OtaPolicyForm,
  expectedPolicyVersion: number,
) {
  return {
    expectedPolicyVersion,
    enabled: form.enabled,
    required: form.enabled && form.required,
    targetReleaseId: form.enabled ? normalizeText(form.targetReleaseId) : null,
    releaseMessage: form.enabled ? normalizeText(form.releaseMessage) : null,
  };
}

export function isPolicyConflict(error: unknown) {
  if (!error || typeof error !== "object") return false;
  const raw = error as {
    response?: { status?: number; data?: unknown };
    status?: number;
    code?: string;
    errorCode?: string;
  };
  const data = raw.response?.data as
    | { code?: unknown; errorCode?: unknown }
    | undefined;
  const code = String(
    raw.code ?? raw.errorCode ?? data?.code ?? data?.errorCode ?? "",
  );
  return (
    raw.response?.status === 409 ||
    raw.status === 409 ||
    code.includes("VERSION_CONFLICT")
  );
}

export function buildConfirmationSummary(
  form: NativePolicyForm | OtaPolicyForm,
  details: { releaseLabel: string; targetLabel?: string },
  labels: ConfirmationLabels,
) {
  const colon = labels.colon ?? "：";
  if ("targetReleaseId" in form) {
    return [
      `${labels.enabled}${colon}${form.enabled ? labels.enabled : labels.disabled}`,
      `${labels.release}${colon}${form.enabled ? details.releaseLabel : labels.noRelease}`,
      `${labels.required}${colon}${form.enabled && form.required ? labels.required : labels.optional}`,
      `${labels.notes}${colon}${form.releaseMessage.trim() || labels.noRelease}`,
    ];
  }
  return [
    `${labels.enabled}${colon}${form.enabled ? labels.enabled : labels.disabled}`,
    `${labels.release}${colon}${form.enabled ? details.releaseLabel : labels.noRelease}`,
    `${labels.scope}${colon}${details.targetLabel ?? labels.allDevices}`,
    `${labels.minimumVersion}${colon}${form.minimumSupportedVersion.trim() || labels.noRelease}`,
    `${labels.minimumBuild}${colon}${form.minimumSupportedBuildNumber.trim() || labels.noRelease}`,
    `${labels.notes}${colon}${form.releaseMessage.trim() || labels.noRelease}`,
  ];
}
