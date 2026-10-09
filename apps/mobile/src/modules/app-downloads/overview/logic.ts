import type {
  ReleaseCenterTarget,
  ReleaseLaneStatus,
  WpfChannel,
} from "../release-center-nav";
import { worstLaneStatus } from "../release-center-nav";
import type {
  HandheldPolicy,
  IpadOtaRollout,
  NativePolicy,
  NativeRelease,
  OtaPolicy,
  PolicyLane,
} from "../types";
import {
  getPolicySummary,
  getWpfNewerActiveVersions,
  getWpfPolicyMode,
} from "../../wpf-versions/logic";
import type { WpfRelease } from "../../wpf-versions/types";
import type zhCopy from "@/locales/zh/releaseOverview.json";

// ---------------------------------------------------------------------------
// 投放总览（纯函数）：线路目录、各接口数据归一、待处理与最近变更。
// 口径对齐 Web apps/web/src/pages/System/AppDownloads/releaseCenterLogic.ts。
// ---------------------------------------------------------------------------

export type ReleaseOverviewCopy = typeof zhCopy;

export type OverviewTerminal = "mobile" | "ipad" | "handheld" | "wpf";

export const OVERVIEW_TERMINALS: readonly OverviewTerminal[] = [
  "mobile",
  "ipad",
  "handheld",
  "wpf",
];

export type ReleaseLaneKey =
  | "mobile-ios-native"
  | "mobile-android-native"
  | "mobile-ios-ota"
  | "mobile-android-ota"
  | "ipad-ios-native"
  | "ipad-ios-ota"
  | "handheld-android-native"
  | "handheld-ios-native"
  | "handheld-android-ota"
  | "handheld-ios-ota"
  | "wpf-production"
  | "wpf-preview";

export interface ReleaseLaneDefinition {
  key: ReleaseLaneKey;
  terminal: OverviewTerminal;
  /** 从总览点进该线路时的落点。 */
  target: ReleaseCenterTarget;
}

/** 发布线路目录，顺序即总览中的展示顺序（终端内顺序同 Web 工作区页签）。 */
export const RELEASE_LANES: readonly ReleaseLaneDefinition[] = [
  {
    key: "mobile-ios-native",
    terminal: "mobile",
    target: { terminal: "mobile", channel: "native", platform: "ios" },
  },
  {
    key: "mobile-android-native",
    terminal: "mobile",
    target: { terminal: "mobile", channel: "native", platform: "android" },
  },
  {
    key: "mobile-ios-ota",
    terminal: "mobile",
    target: { terminal: "mobile", channel: "ota", platform: "ios" },
  },
  {
    key: "mobile-android-ota",
    terminal: "mobile",
    target: { terminal: "mobile", channel: "ota", platform: "android" },
  },
  {
    key: "ipad-ios-native",
    terminal: "ipad",
    target: { terminal: "ipad", channel: "native", platform: "ios" },
  },
  {
    key: "ipad-ios-ota",
    terminal: "ipad",
    target: { terminal: "ipad", channel: "ota", platform: "ios" },
  },
  {
    key: "handheld-android-native",
    terminal: "handheld",
    target: { terminal: "handheld", channel: "native", platform: "android" },
  },
  {
    key: "handheld-ios-native",
    terminal: "handheld",
    target: { terminal: "handheld", channel: "native", platform: "ios" },
  },
  {
    key: "handheld-android-ota",
    terminal: "handheld",
    target: { terminal: "handheld", channel: "ota", platform: "android" },
  },
  {
    key: "handheld-ios-ota",
    terminal: "handheld",
    target: { terminal: "handheld", channel: "ota", platform: "ios" },
  },
  {
    key: "wpf-production",
    terminal: "wpf",
    target: { terminal: "wpf", wpfChannel: "production" },
  },
  {
    key: "wpf-preview",
    terminal: "wpf",
    target: { terminal: "wpf", wpfChannel: "preview" },
  },
];

export function getReleaseLaneDefinition(key: ReleaseLaneKey) {
  return RELEASE_LANES.find((lane) => lane.key === key)!;
}

/** 更新方式：optional 只提醒；minimum 低于最低版本强制；force 低于目标全部强制。 */
export type ReleaseLaneMode = "optional" | "minimum" | "force";

export type ReleaseLaneAttention =
  | { kind: "pending-release"; count: number; latest: string }
  | { kind: "binding-invalid"; reason: string | null }
  | { kind: "partial-rollout"; scope: string };

export interface ReleaseLaneSummary {
  key: ReleaseLaneKey;
  terminal: OverviewTerminal;
  label: string;
  /** 「终端 · 线路」全名，用于待处理和最近变更。 */
  fullLabel: string;
  /** loading 期间 status 暂记 inactive，不参与终端汇总、待处理与最近变更。 */
  loading: boolean;
  status: ReleaseLaneStatus;
  target: string | null;
  /** 第二行补充：OTA ID / 手持原生 runtime / 「最低 X」，多项用 · 连接。 */
  detail: string | null;
  minimum: string | null;
  mode: ReleaseLaneMode | null;
  scope: string | null;
  updatedAt: string | null;
  updatedBy: string | null;
  attentions: ReleaseLaneAttention[];
  navTarget: ReleaseCenterTarget;
}

// ---------------------------------------------------------------------------
// 原始数据：员工端 OTA / iPad OTA / 手持策略的现有归一函数不带 updatedAt / updatedBy，
// 总览需要它们排「最近策略变更」，所以由取数层从原始响应里补读后合并进来。
// ---------------------------------------------------------------------------

export interface LaneAudit {
  updatedAt: string | null;
  updatedBy: string | null;
}

type RawRecord = Record<string, unknown>;
const asRecord = (value: unknown): RawRecord =>
  value && typeof value === "object" && !Array.isArray(value)
    ? (value as RawRecord)
    : {};
const nullableText = (value: unknown) =>
  typeof value === "string" || typeof value === "number"
    ? String(value).trim() || null
    : null;

export function readLaneAudit(raw: unknown): LaneAudit {
  const record = asRecord(raw);
  return {
    updatedAt: nullableText(record.updatedAt),
    updatedBy: nullableText(record.updatedBy),
  };
}

/** 手持策略是否已由后台接管：与 Web 一致，显式 managed 或已有策略行（id 非空）。 */
export function readHandheldManaged(raw: unknown) {
  const record = asRecord(raw);
  return (
    record.managed === true ||
    record.managed === "true" ||
    (record.id !== null && record.id !== undefined)
  );
}

export interface MobileAndroidNativePolicy extends LaneAudit {
  enabled: boolean;
  minimumSupportedBuildNumber: number | null;
  latestBuild: { appVersion: string; appBuildVersion: number } | null;
}

/** 员工端安卓原生策略（/app-update-policies/mobile-android），现有移动端 API 未覆盖。 */
export function normalizeMobileAndroidNativePolicy(
  raw: unknown,
): MobileAndroidNativePolicy {
  const record = asRecord(raw);
  const minimum = Number(record.minimumSupportedBuildNumber);
  const latest = asRecord(record.latestBuild);
  const buildVersion = Number(latest.appBuildVersion);
  return {
    enabled: record.enabled === true || record.enabled === "true",
    minimumSupportedBuildNumber:
      record.minimumSupportedBuildNumber == null ||
      !Number.isInteger(minimum)
        ? null
        : minimum,
    // versionCode 必须是正整数，否则视为没有可用公开包（同 Web）。
    latestBuild:
      Number.isInteger(buildVersion) && buildVersion >= 1
        ? {
            appVersion: nullableText(latest.appVersion) ?? "",
            appBuildVersion: buildVersion,
          }
        : null,
    ...readLaneAudit(raw),
  };
}

export type AuditedOtaPolicy = OtaPolicy & LaneAudit;
export type AuditedIpadOtaRollout = IpadOtaRollout & LaneAudit;
export type AuditedHandheldPolicy = HandheldPolicy &
  LaneAudit & { managed: boolean };

/** 每个数据源的读取状态；undefined 等同 loading。 */
export type LaneSource<T> =
  | { state: "loading" }
  | { state: "error" }
  | { state: "ready"; value: T };

export interface ReleaseOverviewSources {
  mobileIosNative?: LaneSource<{
    policy: NativePolicy;
    releases: NativeRelease[];
  }>;
  mobileAndroidNative?: LaneSource<MobileAndroidNativePolicy>;
  mobileIosOta?: LaneSource<AuditedOtaPolicy>;
  mobileAndroidOta?: LaneSource<AuditedOtaPolicy>;
  ipadNative?: LaneSource<{ policy: NativePolicy; releases: NativeRelease[] }>;
  ipadOta?: LaneSource<AuditedIpadOtaRollout>;
  /** 手持 4 条线路共用一个接口，失败时 4 条一起标 error。 */
  handheld?: LaneSource<AuditedHandheldPolicy[]>;
  /** WPF 通道的全量版本（含停用），用于策略摘要。 */
  wpfProduction?: LaneSource<WpfRelease[]>;
  wpfPreview?: LaneSource<WpfRelease[]>;
}

// ---------------------------------------------------------------------------
// 格式化
// ---------------------------------------------------------------------------

/** {name} 占位符替换（本模块直接读 JSON，不经过 i18next）。 */
export function fill(
  template: string,
  values: Record<string, string | number>,
) {
  return template.replace(/\{(\w+)\}/g, (match, key: string) =>
    key in values ? String(values[key]) : match,
  );
}

/** OTA 的 Group/Update ID 超过 12 位只显示前 8 位。 */
export function shortIdentity(value?: string | null) {
  const text = value?.trim();
  if (!text) return null;
  return text.length > 12 ? `${text.slice(0, 8)}…` : text;
}

export function formatVersionBuild(
  version?: string | null,
  build?: string | number | null,
) {
  const versionText = version?.trim();
  const buildText =
    build === null || build === undefined ? "" : String(build).trim();
  if (versionText && buildText) return `${versionText} (${buildText})`;
  if (versionText) return versionText;
  return buildText ? `build ${buildText}` : null;
}

const timezoneSuffixPattern = /(Z|[+-]\d{2}:?\d{2})$/i;

/** 后端时间多为 UTC 且可能不带时区后缀：无后缀按 UTC 解析；失败返回 NaN。 */
export function toReleaseTimestamp(value?: string | null) {
  const text = value?.trim();
  if (!text) return Number.NaN;
  const normalized = timezoneSuffixPattern.test(text)
    ? text
    : `${text.replace(" ", "T")}Z`;
  return Date.parse(normalized);
}

/** 本地时区 MM-DD HH:mm（跨年时带年份）；无法解析时原样返回，空值显示破折号。 */
export function formatReleaseTime(
  value: string | null | undefined,
  now: Date = new Date(),
) {
  if (!value) return "—";
  const timestamp = toReleaseTimestamp(value);
  if (!Number.isFinite(timestamp)) return value;
  const date = new Date(timestamp);
  const pad = (n: number) => String(n).padStart(2, "0");
  const day = `${pad(date.getMonth() + 1)}-${pad(date.getDate())}`;
  const time = `${pad(date.getHours())}:${pad(date.getMinutes())}`;
  return date.getFullYear() === now.getFullYear()
    ? `${day} ${time}`
    : `${date.getFullYear()}-${day} ${time}`;
}

type ScopeKind = "all" | "stores" | "devices";

export function formatScope(
  kind: ScopeKind,
  count: number,
  copy: ReleaseOverviewCopy,
) {
  return kind === "all"
    ? copy.scope.all
    : fill(copy.scope[kind], { count });
}

function joinDetail(
  detail: string | null,
  minimum: string | null,
  copy: ReleaseOverviewCopy,
) {
  const parts = [
    detail,
    minimum ? fill(copy.minimum, { value: minimum }) : null,
  ].filter((part): part is string => Boolean(part));
  return parts.length ? parts.join(" · ") : null;
}

// ---------------------------------------------------------------------------
// 各线路归一
// ---------------------------------------------------------------------------

type LaneCore = Omit<
  ReleaseLaneSummary,
  | "key"
  | "terminal"
  | "label"
  | "fullLabel"
  | "loading"
  | "status"
  | "navTarget"
  | "detail"
> & { enabled: boolean; detail: string | null };

/** 状态：未启用 → inactive；启用且有待处理事项 → pending；否则 active。 */
function finalize(
  key: ReleaseLaneKey,
  core: LaneCore,
  copy: ReleaseOverviewCopy,
): ReleaseLaneSummary {
  const { enabled, detail, ...rest } = core;
  return {
    ...laneShell(key, copy),
    ...rest,
    loading: false,
    status: !enabled
      ? "inactive"
      : rest.attentions.length
        ? "pending"
        : "active",
    // 未启用时客户端不会收到提示，补充信息、方式与范围都不展示（同 Web）。
    detail: enabled ? joinDetail(detail, rest.minimum, copy) : null,
    mode: enabled ? rest.mode : null,
    scope: enabled ? rest.scope : null,
  };
}

function laneShell(key: ReleaseLaneKey, copy: ReleaseOverviewCopy) {
  const definition = getReleaseLaneDefinition(key);
  const label = copy.lanes[key];
  return {
    key,
    terminal: definition.terminal,
    label,
    fullLabel: `${copy.terminals[definition.terminal]} · ${label}`,
    navTarget: definition.target,
  };
}

function placeholderLane(
  key: ReleaseLaneKey,
  copy: ReleaseOverviewCopy,
  state: "loading" | "error",
): ReleaseLaneSummary {
  return {
    ...laneShell(key, copy),
    loading: state === "loading",
    status: state === "error" ? "error" : "inactive",
    target: null,
    detail: null,
    minimum: null,
    mode: null,
    scope: null,
    updatedAt: null,
    updatedBy: null,
    attentions: [],
  };
}

/** 员工端 iOS / iPad 原生：策略指向 App Store 登记的 releaseId。 */
export function summarizeNativeLane(
  key: "mobile-ios-native" | "ipad-ios-native",
  policy: NativePolicy,
  releases: readonly NativeRelease[],
  copy: ReleaseOverviewCopy,
): ReleaseLaneSummary {
  const activeRelease =
    releases.find((release) => release.id === policy.releaseId) ?? null;
  const minimum = policy.minimumSupportedVersion
    ? formatVersionBuild(
        policy.minimumSupportedVersion,
        policy.minimumSupportedBuildNumber,
      )
    : null;
  const attentions: ReleaseLaneAttention[] = [];
  if (policy.enabled && activeRelease) {
    // 比当前目标登记得更晚、还没投放的版本。
    const activeTime = toReleaseTimestamp(activeRelease.createdAt);
    const newer = releases
      .filter((release) => release.id !== activeRelease.id)
      .filter((release) => toReleaseTimestamp(release.createdAt) > activeTime)
      .sort(
        (left, right) =>
          toReleaseTimestamp(right.createdAt) -
          toReleaseTimestamp(left.createdAt),
      );
    if (newer.length)
      attentions.push({
        kind: "pending-release",
        count: newer.length,
        latest:
          formatVersionBuild(newer[0].version, newer[0].buildNumber) ??
          newer[0].version,
      });
  }
  // 员工端 iOS 没有分店定向，后端恒为 all；iPad 才可能是部分分店。
  const scope = formatScope(
    policy.targetScope,
    policy.targetStoreGuids.length,
    copy,
  );
  return finalize(
    key,
    {
      enabled: policy.enabled,
      target: activeRelease
        ? formatVersionBuild(activeRelease.version, activeRelease.buildNumber)
        : policy.latestVersion,
      detail: null,
      minimum,
      mode: minimum ? "minimum" : "optional",
      scope,
      updatedAt: policy.updatedAt,
      updatedBy: policy.updatedBy,
      attentions,
    },
    copy,
  );
}

/** 员工端安卓原生：目标固定为当前公开的 production 包，只设最低支持构建号。 */
export function summarizeMobileAndroidNativeLane(
  policy: MobileAndroidNativePolicy,
  copy: ReleaseOverviewCopy,
): ReleaseLaneSummary {
  const latest = policy.latestBuild;
  const minimum =
    policy.minimumSupportedBuildNumber !== null
      ? `build ${policy.minimumSupportedBuildNumber}`
      : null;
  return finalize(
    "mobile-android-native",
    {
      enabled: policy.enabled,
      target: latest
        ? formatVersionBuild(latest.appVersion, latest.appBuildVersion)
        : null,
      detail: null,
      minimum,
      mode: minimum ? "minimum" : "optional",
      scope: copy.scope.all,
      updatedAt: policy.updatedAt,
      updatedBy: policy.updatedBy,
      attentions: [],
    },
    copy,
  );
}

/** 员工端 OTA（只看 production 环境，同 Web）。 */
export function summarizeMobileOtaLane(
  key: "mobile-ios-ota" | "mobile-android-ota",
  policy: AuditedOtaPolicy,
  copy: ReleaseOverviewCopy,
): ReleaseLaneSummary {
  const runtime =
    policy.targetRelease?.runtimeVersion || policy.targetRuntimeVersion;
  return finalize(
    key,
    {
      enabled: policy.enabled,
      target: runtime ? `runtime ${runtime}` : null,
      detail: shortIdentity(policy.targetRelease?.updateGroupId),
      minimum: null,
      mode: policy.required ? "force" : "optional",
      scope: copy.scope.all,
      updatedAt: policy.updatedAt,
      updatedBy: policy.updatedBy,
      attentions: [],
    },
    copy,
  );
}

export function summarizeIpadOtaLane(
  rollout: AuditedIpadOtaRollout,
  copy: ReleaseOverviewCopy,
): ReleaseLaneSummary {
  const release = rollout.release;
  return finalize(
    "ipad-ios-ota",
    {
      enabled: rollout.enabled,
      target: release?.runtimeVersion
        ? `runtime ${release.runtimeVersion}`
        : null,
      detail: shortIdentity(release?.updateGroupId),
      minimum: null,
      mode: rollout.forceUpdate ? "force" : "optional",
      scope: formatScope(
        rollout.targetScope,
        rollout.targetStoreGuids.length,
        copy,
      ),
      updatedAt: rollout.updatedAt,
      updatedBy: rollout.updatedBy,
      attentions: [],
    },
    copy,
  );
}

const HANDHELD_LANE_KEYS = {
  "android-native": "handheld-android-native",
  "ios-native": "handheld-ios-native",
  "android-ota": "handheld-android-ota",
  "ios-ota": "handheld-ios-ota",
} as const satisfies Record<PolicyLane, ReleaseLaneKey>;

export function summarizeHandheldLane(
  policy: AuditedHandheldPolicy,
  copy: ReleaseOverviewCopy,
): ReleaseLaneSummary {
  const isNative = policy.lane.endsWith("-native");
  const candidate = policy.candidate;
  const enabled = policy.managed && policy.enabled;
  const minimum =
    isNative && policy.minimumSupportedVersion
      ? formatVersionBuild(
          policy.minimumSupportedVersion,
          policy.minimumSupportedBuildNumber,
        )
      : null;
  const attentions: ReleaseLaneAttention[] = [];
  if (enabled && !policy.candidateValid) {
    // 源发布事实变化或不再是 channel 头部时，设备会拒绝这条绑定。
    attentions.push({ kind: "binding-invalid", reason: policy.blockedReason });
  }
  return finalize(
    HANDHELD_LANE_KEYS[policy.lane],
    {
      enabled,
      target: candidate
        ? isNative
          ? formatVersionBuild(candidate.version, candidate.buildNumber)
          : candidate.runtimeVersion
            ? `runtime ${candidate.runtimeVersion}`
            : null
        : null,
      detail: candidate
        ? isNative
          ? candidate.runtimeVersion
            ? `runtime ${candidate.runtimeVersion}`
            : null
          : shortIdentity(candidate.updateId)
        : null,
      minimum,
      mode: policy.required ? "force" : minimum ? "minimum" : "optional",
      scope: copy.scope.all,
      updatedAt: policy.updatedAt,
      updatedBy: policy.updatedBy,
      attentions,
    },
    copy,
  );
}

const WPF_MODE: Record<
  ReturnType<typeof getWpfPolicyMode>["mode"],
  ReleaseLaneMode
> = { required: "force", minimum: "minimum", optional: "optional" };

/** WPF 某通道：策略随版本行返回，复用 wpf-versions/logic 的共享口径。 */
export function summarizeWpfLane(
  channel: WpfChannel,
  releases: readonly WpfRelease[],
  copy: ReleaseOverviewCopy,
): ReleaseLaneSummary {
  const key: ReleaseLaneKey =
    channel === "preview" ? "wpf-preview" : "wpf-production";
  const summary = getPolicySummary([...releases]);
  if (!summary) {
    return finalize(
      key,
      {
        enabled: false,
        target: null,
        detail: null,
        minimum: null,
        mode: null,
        scope: null,
        updatedAt: null,
        updatedBy: null,
        attentions: [],
      },
      copy,
    );
  }
  const scope = formatScope(
    summary.targetScope,
    summary.targetScope === "devices"
      ? summary.targetDeviceRegistrationIds.length
      : summary.targetStoreGuids.length,
    copy,
  );
  const { mode, minimum } = getWpfPolicyMode(releases, summary);
  const attentions: ReleaseLaneAttention[] = [];
  if (summary.targetScope !== "all")
    attentions.push({ kind: "partial-rollout", scope });
  const newer = getWpfNewerActiveVersions(releases, summary.targetVersion);
  if (newer.length)
    attentions.push({
      kind: "pending-release",
      count: newer.length,
      latest: newer[0],
    });
  return finalize(
    key,
    {
      enabled: true,
      target: summary.targetVersion,
      detail: null,
      minimum,
      mode: WPF_MODE[mode],
      scope,
      updatedAt: summary.policyUpdatedAt,
      updatedBy: summary.policyUpdatedBy,
      attentions,
    },
    copy,
  );
}

// ---------------------------------------------------------------------------
// 汇总
// ---------------------------------------------------------------------------

function fromSource<T>(
  keys: readonly ReleaseLaneKey[],
  source: LaneSource<T> | undefined,
  copy: ReleaseOverviewCopy,
  summarize: (value: T) => ReleaseLaneSummary[],
): ReleaseLaneSummary[] {
  if (!source || source.state === "loading")
    return keys.map((key) => placeholderLane(key, copy, "loading"));
  if (source.state === "error")
    return keys.map((key) => placeholderLane(key, copy, "error"));
  let summaries: ReleaseLaneSummary[];
  try {
    summaries = summarize(source.value);
  } catch {
    // 归一出错只影响这个数据源的线路，不拖垮整页。
    summaries = [];
  }
  // 接口没返回某条线路（如手持缺 lane）时按读取失败处理，同 Web。
  return keys.map(
    (key) =>
      summaries.find((summary) => summary.key === key) ??
      placeholderLane(key, copy, "error"),
  );
}

/** 把各数据源归一成按 RELEASE_LANES 排序的线路列表；单个数据源失败只影响自己的线路。 */
export function buildReleaseLanes(
  sources: ReleaseOverviewSources,
  copy: ReleaseOverviewCopy,
): ReleaseLaneSummary[] {
  const lanes = [
    ...fromSource(
      ["mobile-ios-native"],
      sources.mobileIosNative,
      copy,
      ({ policy, releases }) => [
        summarizeNativeLane("mobile-ios-native", policy, releases, copy),
      ],
    ),
    ...fromSource(
      ["mobile-android-native"],
      sources.mobileAndroidNative,
      copy,
      (policy) => [summarizeMobileAndroidNativeLane(policy, copy)],
    ),
    ...fromSource(["mobile-ios-ota"], sources.mobileIosOta, copy, (policy) => [
      summarizeMobileOtaLane("mobile-ios-ota", policy, copy),
    ]),
    ...fromSource(
      ["mobile-android-ota"],
      sources.mobileAndroidOta,
      copy,
      (policy) => [summarizeMobileOtaLane("mobile-android-ota", policy, copy)],
    ),
    ...fromSource(
      ["ipad-ios-native"],
      sources.ipadNative,
      copy,
      ({ policy, releases }) => [
        summarizeNativeLane("ipad-ios-native", policy, releases, copy),
      ],
    ),
    ...fromSource(["ipad-ios-ota"], sources.ipadOta, copy, (rollout) => [
      summarizeIpadOtaLane(rollout, copy),
    ]),
    ...fromSource(
      Object.values(HANDHELD_LANE_KEYS),
      sources.handheld,
      copy,
      (policies) => policies.map((policy) => summarizeHandheldLane(policy, copy)),
    ),
    ...fromSource(["wpf-production"], sources.wpfProduction, copy, (rows) => [
      summarizeWpfLane("production", rows, copy),
    ]),
    ...fromSource(["wpf-preview"], sources.wpfPreview, copy, (rows) => [
      summarizeWpfLane("preview", rows, copy),
    ]),
  ];
  const order = new Map(RELEASE_LANES.map((lane, index) => [lane.key, index]));
  return lanes.sort(
    (left, right) => (order.get(left.key) ?? 0) - (order.get(right.key) ?? 0),
  );
}

export interface ReleaseAttentionItem {
  id: string;
  laneKey: ReleaseLaneKey;
  laneLabel: string;
  kind: ReleaseLaneAttention["kind"];
  title: string;
  description: string;
  navTarget: ReleaseCenterTarget;
}

/** 「待处理」：已启用线路上需要管理员决策的事项，按线路顺序展开。 */
export function buildAttentionItems(
  lanes: readonly ReleaseLaneSummary[],
  copy: ReleaseOverviewCopy,
): ReleaseAttentionItem[] {
  return lanes
    .filter((lane) => !lane.loading)
    .flatMap((lane) =>
      lane.attentions.map((attention, index) => {
        const text =
          attention.kind === "pending-release"
            ? {
                title: fill(copy.attention.pendingRelease, {
                  count: attention.count,
                  latest: attention.latest,
                }),
                description: copy.attention.pendingReleaseDesc,
              }
            : attention.kind === "binding-invalid"
              ? {
                  title: copy.attention.bindingInvalid,
                  description:
                    attention.reason || copy.attention.bindingInvalidDesc,
                }
              : {
                  title: fill(copy.attention.partialRollout, {
                    scope: attention.scope,
                  }),
                  description: copy.attention.partialRolloutDesc,
                };
        return {
          id: `${lane.key}-${attention.kind}-${index}`,
          laneKey: lane.key,
          laneLabel: lane.fullLabel,
          kind: attention.kind,
          ...text,
          navTarget: lane.navTarget,
        };
      }),
    );
}

/** 「最近策略变更」：各线路最后一次策略保存，按时间倒序取前 limit 条。 */
export function buildRecentChanges(
  lanes: readonly ReleaseLaneSummary[],
  limit = 8,
): ReleaseLaneSummary[] {
  return lanes
    .filter(
      (lane) =>
        !lane.loading &&
        lane.status !== "error" &&
        Number.isFinite(toReleaseTimestamp(lane.updatedAt)),
    )
    .sort(
      (left, right) =>
        toReleaseTimestamp(right.updatedAt) -
        toReleaseTimestamp(left.updatedAt),
    )
    .slice(0, limit);
}

/** 最近变更一行的「目标 · 方式 · 范围」；未启用直接显示「未启用」。 */
export function describeLaneChange(
  lane: ReleaseLaneSummary,
  copy: ReleaseOverviewCopy,
) {
  if (lane.status === "inactive") return copy.notEnabled;
  return (
    [
      lane.target ?? copy.noTarget,
      lane.mode ? copy.modes[lane.mode] : null,
      lane.scope,
    ]
      .filter(Boolean)
      .join(" · ") || copy.noTarget
  );
}

export type TerminalStatusMap = Record<
  OverviewTerminal,
  ReleaseLaneStatus | null
>;

/** 每个终端取最需要关注的线路状态；仍在读取的线路不参与，全部读取中为 null。 */
export function buildTerminalStatus(
  lanes: readonly ReleaseLaneSummary[],
): TerminalStatusMap {
  const result = {} as TerminalStatusMap;
  for (const terminal of OVERVIEW_TERMINALS) {
    result[terminal] = worstLaneStatus(
      lanes
        .filter((lane) => lane.terminal === terminal && !lane.loading)
        .map((lane) => lane.status),
    );
  }
  return result;
}

export interface ReleaseOverviewModel {
  lanes: ReleaseLaneSummary[];
  attention: ReleaseAttentionItem[];
  recent: ReleaseLaneSummary[];
  terminalStatus: TerminalStatusMap;
}

export function buildReleaseOverview(
  sources: ReleaseOverviewSources,
  copy: ReleaseOverviewCopy,
  recentLimit = 8,
): ReleaseOverviewModel {
  const lanes = buildReleaseLanes(sources, copy);
  return {
    lanes,
    attention: buildAttentionItems(lanes, copy),
    recent: buildRecentChanges(lanes, recentLimit),
    terminalStatus: buildTerminalStatus(lanes),
  };
}
