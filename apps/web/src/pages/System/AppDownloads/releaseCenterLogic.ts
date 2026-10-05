import type {
  AppUpdateTargetScope,
  IosAppStoreRelease,
  MobileAndroidNativeUpdatePolicy,
  NativeUpdatePolicy,
  PosIpadOtaRollout,
} from '../../../types/appUpdatePolicy'
import type { MobileOtaPolicy } from '../../../types/mobileOtaPolicy'
import type { PosHandheldUpdatePolicy } from '../../../types/posHandheldUpdatePolicy'
import type { WpfAppRelease, WpfUpdateTargetScope } from '../../../types/wpfVersion'
import { compareWpfVersion, getWpfPolicySummary } from '../WpfVersions/logic'

// ---------------------------------------------------------------------------
// 视图与轨道：版本发布中心用 URL 参数 view / lane 记录当前位置，书签与标签页切换都能还原。
// ---------------------------------------------------------------------------

export const RELEASE_TERMINALS = ['mobile', 'ipad', 'handheld', 'wpf'] as const
export type ReleaseTerminal = (typeof RELEASE_TERMINALS)[number]
export type ReleaseCenterView = 'overview' | ReleaseTerminal | 'tools'

/** 每类终端在工作区里的轨道页签，顺序即展示顺序，第一个为默认。 */
export const RELEASE_TERMINAL_LANES = {
  mobile: ['ios-native', 'android-native', 'ota'],
  ipad: ['ios-native', 'ota'],
  handheld: ['policy', 'apk'],
  wpf: ['installer'],
} as const satisfies Record<ReleaseTerminal, readonly string[]>

export type ReleaseTerminalLane<T extends ReleaseTerminal = ReleaseTerminal> =
  (typeof RELEASE_TERMINAL_LANES)[T][number]

export interface ReleaseCenterLocation {
  view: ReleaseCenterView
  lane: string | null
}

export function isReleaseTerminal(value: unknown): value is ReleaseTerminal {
  return typeof value === 'string' && (RELEASE_TERMINALS as readonly string[]).includes(value)
}

export function resolveReleaseCenterLocation(
  search: URLSearchParams,
  options: { canViewTools: boolean },
): ReleaseCenterLocation {
  const rawView = search.get('view')?.trim().toLowerCase() ?? ''
  if (rawView === 'tools') {
    // 无工具权限时退回总览，不能靠手改 URL 进入凭据页。
    return options.canViewTools ? { view: 'tools', lane: null } : { view: 'overview', lane: null }
  }
  if (!isReleaseTerminal(rawView)) {
    return { view: 'overview', lane: null }
  }

  const lanes: readonly string[] = RELEASE_TERMINAL_LANES[rawView]
  const rawLane = search.get('lane')?.trim().toLowerCase() ?? ''
  return { view: rawView, lane: lanes.includes(rawLane) ? rawLane : lanes[0] }
}

/** 生成新的查询串：只保留 view / lane，其它参数（如旧链接带的筛选）一并清掉。 */
export function buildReleaseCenterSearch(view: ReleaseCenterView, lane?: string | null) {
  const params = new URLSearchParams()
  if (view !== 'overview') {
    params.set('view', view)
  }
  if (isReleaseTerminal(view)) {
    const lanes: readonly string[] = RELEASE_TERMINAL_LANES[view]
    const nextLane = lane && lanes.includes(lane) ? lane : lanes[0]
    if (nextLane !== lanes[0]) {
      params.set('lane', nextLane)
    }
  }
  const text = params.toString()
  return text ? `?${text}` : ''
}

// ---------------------------------------------------------------------------
// 总览矩阵：11 条轨道的当前生效策略，全部来自已有的只读接口。
// ---------------------------------------------------------------------------

export const RELEASE_MATRIX_COLUMNS = [
  'ios-native',
  'android-native',
  'windows',
  'ios-ota',
  'android-ota',
] as const
export type ReleaseMatrixColumn = (typeof RELEASE_MATRIX_COLUMNS)[number]

export type ReleaseLaneKey =
  | 'mobile-ios-native'
  | 'mobile-android-native'
  | 'mobile-ios-ota'
  | 'mobile-android-ota'
  | 'ipad-ios-native'
  | 'ipad-ios-ota'
  | 'handheld-ios-native'
  | 'handheld-android-native'
  | 'handheld-ios-ota'
  | 'handheld-android-ota'
  | 'wpf-windows'

export interface ReleaseLaneDefinition {
  key: ReleaseLaneKey
  terminal: ReleaseTerminal
  /** 点击后进入的工作区轨道页签。 */
  lane: string
  column: ReleaseMatrixColumn
}

export const RELEASE_LANE_DEFINITIONS: readonly ReleaseLaneDefinition[] = [
  { key: 'mobile-ios-native', terminal: 'mobile', lane: 'ios-native', column: 'ios-native' },
  { key: 'mobile-android-native', terminal: 'mobile', lane: 'android-native', column: 'android-native' },
  { key: 'mobile-ios-ota', terminal: 'mobile', lane: 'ota', column: 'ios-ota' },
  { key: 'mobile-android-ota', terminal: 'mobile', lane: 'ota', column: 'android-ota' },
  { key: 'ipad-ios-native', terminal: 'ipad', lane: 'ios-native', column: 'ios-native' },
  { key: 'ipad-ios-ota', terminal: 'ipad', lane: 'ota', column: 'ios-ota' },
  { key: 'handheld-ios-native', terminal: 'handheld', lane: 'policy', column: 'ios-native' },
  { key: 'handheld-android-native', terminal: 'handheld', lane: 'policy', column: 'android-native' },
  { key: 'handheld-ios-ota', terminal: 'handheld', lane: 'policy', column: 'ios-ota' },
  { key: 'handheld-android-ota', terminal: 'handheld', lane: 'policy', column: 'android-ota' },
  { key: 'wpf-windows', terminal: 'wpf', lane: 'installer', column: 'windows' },
]

/**
 * 更新方式：
 * - optional：只做可选提醒；
 * - minimum：低于最低支持版本的客户端必须更新，其余可选；
 * - required：低于目标的客户端全部必须更新。
 */
export type ReleaseLaneMode = 'optional' | 'minimum' | 'required'

export interface ReleaseLaneScope {
  kind: AppUpdateTargetScope | WpfUpdateTargetScope
  count: number
}

export type ReleaseLaneAttention =
  | { kind: 'pending-release'; count: number; latest: string }
  | { kind: 'binding-invalid'; reason: string | null }
  | { kind: 'partial-rollout'; scope: ReleaseLaneScope }

export interface ReleaseLaneSummary {
  key: ReleaseLaneKey
  /** disabled = 策略存在但未启用 / 后台未接管，客户端不会收到提示。 */
  status: 'active' | 'disabled'
  target: string | null
  /** 第二行补充信息：OTA 的 Group/Update ID、原生的 runtime 等。 */
  detail: string | null
  minimum: string | null
  mode: ReleaseLaneMode | null
  scope: ReleaseLaneScope | null
  updatedAt: string | null
  updatedBy: string | null
  attentions: ReleaseLaneAttention[]
}

function shortIdentity(value?: string | null) {
  const text = value?.trim()
  if (!text) {
    return null
  }
  return text.length > 12 ? `${text.slice(0, 8)}…` : text
}

function formatVersionBuild(version?: string | null, build?: string | number | null) {
  const versionText = version?.trim()
  const buildText = build === null || build === undefined ? '' : String(build).trim()
  if (versionText && buildText) {
    return `${versionText} (${buildText})`
  }
  if (versionText) {
    return versionText
  }
  return buildText ? `build ${buildText}` : null
}

const timezoneSuffixPattern = /(Z|[+-]\d{2}:?\d{2})$/i

/** 后端时间多为 UTC 且可能不带时区后缀；只用于排序比较，解析失败返回 NaN。 */
export function toReleaseTimestamp(value?: string | null) {
  const text = value?.trim()
  if (!text) {
    return Number.NaN
  }
  const normalized = timezoneSuffixPattern.test(text) ? text : `${text.replace(' ', 'T')}Z`
  return Date.parse(normalized)
}

function scopeOf(kind: AppUpdateTargetScope | WpfUpdateTargetScope, count: number): ReleaseLaneScope {
  return { kind, count: kind === 'all' ? 0 : count }
}

/** Mobile iOS / iPad 原生：发布事实是 App Store 登记，策略指向其中一个 releaseId。 */
export function summarizeNativeLane(
  key: ReleaseLaneKey,
  policy: NativeUpdatePolicy,
  releases: readonly IosAppStoreRelease[],
): ReleaseLaneSummary {
  const activeRelease = releases.find((release) => release.id === policy.releaseId) ?? null
  const minimum = policy.minimumSupportedVersion
    ? formatVersionBuild(policy.minimumSupportedVersion, policy.minimumSupportedBuildNumber)
    : null
  const attentions: ReleaseLaneAttention[] = []

  if (policy.enabled && activeRelease) {
    // 比目标登记得更晚、还没投放的版本，提醒管理员决定是否激活。
    const activeTime = toReleaseTimestamp(activeRelease.createdAt)
    const newer = releases
      .filter((release) => release.id !== activeRelease.id)
      .filter((release) => toReleaseTimestamp(release.createdAt) > activeTime)
      .sort((left, right) => toReleaseTimestamp(right.createdAt) - toReleaseTimestamp(left.createdAt))
    if (newer.length) {
      attentions.push({
        kind: 'pending-release',
        count: newer.length,
        latest: formatVersionBuild(newer[0].version, newer[0].buildNumber) ?? newer[0].version,
      })
    }
  }

  return {
    key,
    status: policy.enabled ? 'active' : 'disabled',
    target: activeRelease
      ? formatVersionBuild(activeRelease.version, activeRelease.buildNumber)
      : policy.latestVersion,
    detail: null,
    minimum,
    mode: policy.enabled ? (minimum ? 'minimum' : 'optional') : null,
    scope: scopeOf(policy.targetScope, policy.targetStoreGuids.length),
    updatedAt: policy.updatedAt,
    updatedBy: policy.updatedBy,
    attentions,
  }
}

/** Mobile 安卓原生：没有发布事实，目标固定为当前公开的 production 包，只设最低支持构建号。 */
export function summarizeMobileAndroidNativeLane(
  policy: MobileAndroidNativeUpdatePolicy,
): ReleaseLaneSummary {
  const latest = policy.latestBuild
  const minimum = policy.minimumSupportedBuildNumber !== null
    ? `build ${policy.minimumSupportedBuildNumber}`
    : null
  return {
    key: 'mobile-android-native',
    status: policy.enabled ? 'active' : 'disabled',
    target: latest ? formatVersionBuild(latest.appVersion, latest.appBuildVersion) : null,
    detail: null,
    minimum,
    mode: policy.enabled ? (minimum ? 'minimum' : 'optional') : null,
    scope: scopeOf('all', 0),
    updatedAt: policy.updatedAt,
    updatedBy: policy.updatedBy,
    attentions: [],
  }
}

export function summarizeMobileOtaLane(
  key: 'mobile-ios-ota' | 'mobile-android-ota',
  policy: MobileOtaPolicy,
): ReleaseLaneSummary {
  const runtime = policy.targetRelease?.runtimeVersion ?? policy.targetRuntimeVersion
  return {
    key,
    status: policy.enabled ? 'active' : 'disabled',
    target: runtime ? `runtime ${runtime}` : null,
    detail: shortIdentity(policy.targetRelease?.updateGroupId),
    minimum: null,
    mode: policy.enabled ? (policy.required ? 'required' : 'optional') : null,
    scope: scopeOf('all', 0),
    updatedAt: policy.updatedAt,
    updatedBy: policy.updatedBy,
    attentions: [],
  }
}

export function summarizeIpadOtaLane(rollout: PosIpadOtaRollout): ReleaseLaneSummary {
  const release = rollout.release
  return {
    key: 'ipad-ios-ota',
    status: rollout.enabled ? 'active' : 'disabled',
    target: release?.runtimeVersion ? `runtime ${release.runtimeVersion}` : null,
    detail: shortIdentity(release?.updateGroupId),
    minimum: null,
    mode: rollout.enabled ? (rollout.forceUpdate ? 'required' : 'optional') : null,
    scope: scopeOf(rollout.targetScope, rollout.targetStoreGuids.length),
    updatedAt: rollout.updatedAt,
    updatedBy: rollout.updatedBy,
    attentions: [],
  }
}

const HANDHELD_LANE_KEYS = {
  'ios-native': 'handheld-ios-native',
  'android-native': 'handheld-android-native',
  'ios-ota': 'handheld-ios-ota',
  'android-ota': 'handheld-android-ota',
} as const satisfies Record<PosHandheldUpdatePolicy['lane'], ReleaseLaneKey>

export function summarizeHandheldLane(policy: PosHandheldUpdatePolicy): ReleaseLaneSummary {
  const isNative = policy.lane.endsWith('-native')
  const candidate = policy.candidate
  const enabled = policy.managed && policy.enabled
  const minimum = isNative && policy.minimumSupportedVersion
    ? formatVersionBuild(policy.minimumSupportedVersion, policy.minimumSupportedBuildNumber)
    : null
  const attentions: ReleaseLaneAttention[] = []
  if (enabled && !policy.candidateValid) {
    // 源发布事实变化或不再是 channel 头部时，设备会拒绝这条绑定。
    attentions.push({ kind: 'binding-invalid', reason: policy.blockedReason })
  }

  return {
    key: HANDHELD_LANE_KEYS[policy.lane],
    status: enabled ? 'active' : 'disabled',
    target: candidate
      ? isNative
        ? formatVersionBuild(candidate.version, candidate.buildNumber)
        : candidate.runtimeVersion
          ? `runtime ${candidate.runtimeVersion}`
          : null
      : null,
    detail: candidate
      ? isNative
        ? candidate.runtimeVersion ? `runtime ${candidate.runtimeVersion}` : null
        : shortIdentity(candidate.updateId)
      : null,
    minimum,
    mode: enabled
      ? policy.required ? 'required' : minimum ? 'minimum' : 'optional'
      : null,
    scope: scopeOf('all', 0),
    updatedAt: policy.updatedAt,
    updatedBy: policy.updatedBy,
    attentions,
  }
}

/** WPF：策略随版本行一起返回，复用版本页的摘要逻辑，避免两处口径不一致。 */
export function summarizeWpfLane(releases: readonly WpfAppRelease[]): ReleaseLaneSummary {
  const summary = getWpfPolicySummary([...releases])
  if (!summary) {
    return {
      key: 'wpf-windows',
      status: 'disabled',
      target: null,
      detail: null,
      minimum: null,
      mode: null,
      scope: null,
      updatedAt: null,
      updatedBy: null,
      attentions: [],
    }
  }

  const scope = scopeOf(
    summary.targetScope,
    summary.targetScope === 'devices'
      ? summary.targetDeviceRegistrationIds.length
      : summary.targetStoreGuids.length,
  )
  // 强更口径与后端 WpfAppReleaseService 一致：当前版本 < 最低支持版本即强制；未存最低版本则不按最低版本强制。
  // 摘要里的 minimumSupportedVersion 在未存时会回退成目标版本（仅供展示），所以这里读原始值。
  const policyCarrier = releases.find((release) => release.targetVersion?.trim())
    ?? releases.find((release) => release.isCurrent)
  const storedMinimum = policyCarrier?.minimumSupportedVersion?.trim() || null
  const minimumVsTarget = storedMinimum ? compareWpfVersion(storedMinimum, summary.targetVersion) : null
  const minimumBelowTarget = minimumVsTarget !== null && minimumVsTarget < 0
  // 最低版本等于目标时，低于目标的机器全部落在强制区间，等同强制更新。
  const minimumCoversTarget = minimumVsTarget !== null && minimumVsTarget >= 0
  const attentions: ReleaseLaneAttention[] = []
  if (scope.kind !== 'all') {
    attentions.push({ kind: 'partial-rollout', scope })
  }

  const newer = releases
    .filter((release) => release.isActive)
    .filter((release) => (compareWpfVersion(release.version, summary.targetVersion) ?? 0) > 0)
    .sort((left, right) => (compareWpfVersion(right.version, left.version) ?? 0))
  if (newer.length) {
    attentions.push({ kind: 'pending-release', count: newer.length, latest: newer[0].version })
  }

  return {
    key: 'wpf-windows',
    status: 'active',
    target: summary.targetVersion,
    detail: null,
    minimum: !summary.forceUpdate && minimumBelowTarget ? storedMinimum : null,
    mode: summary.forceUpdate || minimumCoversTarget
      ? 'required'
      : minimumBelowTarget ? 'minimum' : 'optional',
    scope,
    updatedAt: summary.policyUpdatedAt,
    updatedBy: summary.policyUpdatedBy,
    attentions,
  }
}

/** 跨轨道「最近策略变更」：按每条轨道的最后更新时间倒序。 */
export function buildRecentLaneChanges(
  summaries: readonly ReleaseLaneSummary[],
  limit = 8,
) {
  return summaries
    .filter((summary) => Number.isFinite(toReleaseTimestamp(summary.updatedAt)))
    .sort((left, right) => toReleaseTimestamp(right.updatedAt) - toReleaseTimestamp(left.updatedAt))
    .slice(0, limit)
}

export function getReleaseLaneDefinition(key: ReleaseLaneKey) {
  return RELEASE_LANE_DEFINITIONS.find((definition) => definition.key === key)!
}
