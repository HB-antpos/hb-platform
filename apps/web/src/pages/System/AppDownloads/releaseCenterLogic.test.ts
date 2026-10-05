import assert from 'node:assert/strict'
import type {
  IosAppStoreRelease,
  NativeUpdatePolicy,
  PosIpadOtaRollout,
} from '../../../types/appUpdatePolicy'
import type { MobileOtaPolicy } from '../../../types/mobileOtaPolicy'
import type { PosHandheldUpdatePolicy } from '../../../types/posHandheldUpdatePolicy'
import type { WpfAppRelease } from '../../../types/wpfVersion'
import {
  RELEASE_LANE_DEFINITIONS,
  RELEASE_MATRIX_COLUMNS,
  RELEASE_TERMINAL_LANES,
  buildRecentLaneChanges,
  buildReleaseCenterSearch,
  resolveReleaseCenterLocation,
  summarizeHandheldLane,
  summarizeIpadOtaLane,
  summarizeMobileAndroidNativeLane,
  summarizeMobileOtaLane,
  summarizeNativeLane,
  summarizeWpfLane,
  toReleaseTimestamp,
} from './releaseCenterLogic'

const search = (text: string) => new URLSearchParams(text)

// ---- 位置解析 ----
assert.deepEqual(resolveReleaseCenterLocation(search(''), { canViewTools: true }), { view: 'overview', lane: null })
assert.deepEqual(resolveReleaseCenterLocation(search('view=unknown'), { canViewTools: true }), { view: 'overview', lane: null })
assert.deepEqual(
  resolveReleaseCenterLocation(search('view=mobile'), { canViewTools: false }),
  { view: 'mobile', lane: 'ios-native' },
  '终端未带 lane 时落到第一条轨道',
)
assert.deepEqual(
  resolveReleaseCenterLocation(search('view=MOBILE&lane=OTA'), { canViewTools: false }),
  { view: 'mobile', lane: 'ota' },
  '大小写不敏感',
)
assert.deepEqual(
  resolveReleaseCenterLocation(search('view=ipad&lane=apk'), { canViewTools: false }),
  { view: 'ipad', lane: 'ios-native' },
  '不属于该终端的轨道回退默认',
)
assert.deepEqual(resolveReleaseCenterLocation(search('view=tools'), { canViewTools: true }), { view: 'tools', lane: null })
assert.deepEqual(
  resolveReleaseCenterLocation(search('view=tools'), { canViewTools: false }),
  { view: 'overview', lane: null },
  '无工具权限不能经 URL 进入组件与凭据',
)

assert.equal(buildReleaseCenterSearch('overview'), '')
assert.equal(buildReleaseCenterSearch('mobile', 'ios-native'), '?view=mobile', '默认轨道不写进 URL')
assert.equal(buildReleaseCenterSearch('mobile', 'ota'), '?view=mobile&lane=ota')
assert.equal(buildReleaseCenterSearch('wpf', 'whatever'), '?view=wpf')
assert.equal(buildReleaseCenterSearch('tools', 'ota'), '?view=tools')

// 矩阵定义：每条轨道都能落到所属终端的有效页签，且同一终端同一列不重复。
const cellKeys = new Set<string>()
for (const definition of RELEASE_LANE_DEFINITIONS) {
  const lanes: readonly string[] = RELEASE_TERMINAL_LANES[definition.terminal]
  assert.ok(lanes.includes(definition.lane), `${definition.key} 指向无效页签`)
  assert.ok((RELEASE_MATRIX_COLUMNS as readonly string[]).includes(definition.column))
  const cell = `${definition.terminal}:${definition.column}`
  assert.ok(!cellKeys.has(cell), `${cell} 重复`)
  cellKeys.add(cell)
}
assert.equal(RELEASE_LANE_DEFINITIONS.length, 11)

// ---- 时间 ----
assert.equal(toReleaseTimestamp('2026-10-05 07:19:00'), Date.parse('2026-10-05T07:19:00Z'), '不带时区按 UTC')
assert.equal(toReleaseTimestamp('2026-10-05T07:19:00+10:00'), Date.parse('2026-10-05T07:19:00+10:00'))
assert.ok(Number.isNaN(toReleaseTimestamp(null)))

// ---- 原生（App Store 登记）----
function release(id: string, version: string, buildNumber: string, createdAt: string): IosAppStoreRelease {
  return {
    id, app: 'pos-ipad', appStoreId: '1', bundleIdentifier: 'x', version, buildNumber,
    storefront: 'au', appStoreUrl: '', appleVerifiedAtUtc: createdAt, createdAt, createdBy: 'admin',
  }
}
const nativePolicy: NativeUpdatePolicy = {
  id: 'p', enabled: true, policyVersion: 8, releaseId: 'r2', latestVersion: '1.1.2',
  minimumSupportedVersion: '1.1.0', minimumSupportedBuildNumber: 27, appStoreUrl: null,
  releaseMessage: null, targetScope: 'stores', targetStoreGuids: ['a', 'b'],
  updatedAt: '2026-10-02T21:08:00Z', updatedBy: 'admin',
}
const releases = [
  release('r3', '1.1.3', '33', '2026-10-04T16:20:00Z'),
  release('r2', '1.1.2', '31', '2026-10-02T20:51:00Z'),
  release('r1', '1.1.0', '27', '2026-09-20T11:30:00Z'),
]
const native = summarizeNativeLane('ipad-ios-native', nativePolicy, releases)
assert.equal(native.status, 'active')
assert.equal(native.target, '1.1.2 (31)')
assert.equal(native.minimum, '1.1.0 (27)')
assert.equal(native.mode, 'minimum')
assert.deepEqual(native.scope, { kind: 'stores', count: 2 })
assert.deepEqual(native.attentions, [{ kind: 'pending-release', count: 1, latest: '1.1.3 (33)' }])

const nativeDisabled = summarizeNativeLane('ipad-ios-native', { ...nativePolicy, enabled: false }, releases)
assert.equal(nativeDisabled.status, 'disabled')
assert.equal(nativeDisabled.mode, null)
assert.deepEqual(nativeDisabled.attentions, [], '未启用的策略不提示新版本')

const nativeOptional = summarizeNativeLane(
  'mobile-ios-native',
  { ...nativePolicy, releaseId: 'r3', minimumSupportedVersion: null, minimumSupportedBuildNumber: null, targetScope: 'all', targetStoreGuids: [] },
  releases,
)
assert.equal(nativeOptional.mode, 'optional')
assert.deepEqual(nativeOptional.scope, { kind: 'all', count: 0 })
assert.deepEqual(nativeOptional.attentions, [], '目标已是最新登记时无待处理')

// ---- Mobile 安卓原生 ----
const android = summarizeMobileAndroidNativeLane({
  enabled: true, minimumSupportedBuildNumber: 60, releaseMessage: null, policyVersion: 6,
  updatedAt: '2026-10-03T21:40:00Z', updatedBy: 'admin',
  latestBuild: { easBuildId: 'e', appVersion: '1.0.10', appBuildVersion: 63, completedAt: null },
})
assert.equal(android.target, '1.0.10 (63)')
assert.equal(android.minimum, 'build 60')
assert.equal(android.mode, 'minimum')

// ---- OTA ----
const mobileOta: MobileOtaPolicy = {
  id: 'm', environment: 'production', platform: 'ios', enabled: true, required: true, policyVersion: 19,
  targetReleaseId: 'x', targetRuntimeVersion: '1.0.7', releaseMessage: null,
  targetRelease: null, additionalTargets: [], updatedAt: null, updatedBy: null,
}
const ota = summarizeMobileOtaLane('mobile-ios-ota', mobileOta)
assert.equal(ota.target, 'runtime 1.0.7')
assert.equal(ota.mode, 'required')
assert.equal(ota.detail, null)

const ipadOta: PosIpadOtaRollout = {
  id: 'o', enabled: true, policyVersion: 11, releaseId: 'x', forceUpdate: false,
  targetScope: 'stores', targetStoreGuids: ['s1', 's2', 's3'], releaseMessage: null,
  release: {
    id: 'x', environment: 'production', updateGroupId: '3aa0f1d2-6c41-4b0e-9a8d-000000000000', iosUpdateId: 'u',
    channel: 'production', runtimeVersion: '1.1.2', gitCommitHash: null, dashboardUrl: null,
    publishedAtUtc: '', isRollback: false, rollbackOfReleaseId: null, createdAt: '', createdBy: null,
  },
  updatedAt: '2026-10-04T09:15:00Z', updatedBy: 'admin',
}
const ipad = summarizeIpadOtaLane(ipadOta)
assert.equal(ipad.target, 'runtime 1.1.2')
assert.equal(ipad.detail, '3aa0f1d2…')
assert.equal(ipad.mode, 'optional')
assert.deepEqual(ipad.scope, { kind: 'stores', count: 3 })

// ---- 手持 ----
function handheld(overrides: Partial<PosHandheldUpdatePolicy>): PosHandheldUpdatePolicy {
  return {
    id: 'h', lane: 'android-native', managed: true, enabled: true, required: false, policyVersion: 5,
    candidateId: 'c', candidateValid: true, blockedReason: null,
    candidate: {
      id: 'c', lane: 'android-native', platform: 'android', kind: 'native', version: '1.0.9', buildNumber: '9',
      runtimeVersion: '0.1.1', channel: null, updateId: 'C0E65419ABCDEF', updateGroupId: null,
      downloadUrl: null, appStoreUrl: null, artifactSha256: null, createdAt: '', createdBy: null,
      activatable: true, blockedReason: null,
    },
    minimumSupportedVersion: '1.0.8', minimumSupportedBuildNumber: 8, releaseMessage: null,
    updatedAt: '2026-10-03T09:12:00Z', updatedBy: 'admin',
    ...overrides,
  }
}
const handheldNative = summarizeHandheldLane(handheld({}))
assert.equal(handheldNative.key, 'handheld-android-native')
assert.equal(handheldNative.target, '1.0.9 (9)')
assert.equal(handheldNative.detail, 'runtime 0.1.1')
assert.equal(handheldNative.minimum, '1.0.8 (8)')
assert.equal(handheldNative.mode, 'minimum')

const handheldOta = summarizeHandheldLane(handheld({ lane: 'android-ota', required: true }))
assert.equal(handheldOta.key, 'handheld-android-ota')
assert.equal(handheldOta.target, 'runtime 0.1.1')
assert.equal(handheldOta.detail, 'C0E65419…')
assert.equal(handheldOta.minimum, null, 'OTA 不展示最低版本')
assert.equal(handheldOta.mode, 'required')

assert.equal(summarizeHandheldLane(handheld({ managed: false })).status, 'disabled', '后台未接管视为未启用')
assert.deepEqual(
  summarizeHandheldLane(handheld({ candidateValid: false, blockedReason: 'fingerprint' })).attentions,
  [{ kind: 'binding-invalid', reason: 'fingerprint' }],
)
assert.deepEqual(
  summarizeHandheldLane(handheld({ enabled: false, candidateValid: false })).attentions,
  [],
  '停用的轨道不提示绑定失效',
)

// ---- WPF ----
function wpf(version: string, overrides: Partial<WpfAppRelease> = {}): WpfAppRelease {
  return {
    id: version, version, channel: 'production', fileName: `Hbpos.Client.Wpf-${version}.exe`, fileSize: 1,
    sha256: null, installerType: 'exe', installerArguments: null, downloadUrl: null, objectKey: null,
    releaseNotes: null, isActive: true, isCurrent: false, isRollback: false, forceUpdate: false,
    minimumSupportedVersion: null, targetVersion: null, targetScope: 'all', targetStoreGuids: [],
    targetDeviceRegistrationIds: [], targetStoreSummaries: [], targetDeviceSummaries: [],
    policyUpdatedAt: null, policyUpdatedBy: null, createdAt: null, updatedAt: null,
    ...overrides,
  }
}
const policyFields = {
  targetVersion: '1.0.45', minimumSupportedVersion: '1.0.44', targetScope: 'stores' as const,
  targetStoreGuids: ['g1'], policyUpdatedAt: '2026-10-05T07:19:00Z', policyUpdatedBy: 'admin',
}
const wpfSummary = summarizeWpfLane([
  wpf('1.0.46'),
  wpf('1.0.45', { isCurrent: true, ...policyFields }),
  wpf('1.0.44', policyFields),
  wpf('1.0.47', { isActive: false }),
])
assert.equal(wpfSummary.status, 'active')
assert.equal(wpfSummary.target, '1.0.45')
assert.equal(wpfSummary.minimum, '1.0.44')
assert.equal(wpfSummary.mode, 'minimum')
assert.deepEqual(wpfSummary.scope, { kind: 'stores', count: 1 })
assert.deepEqual(wpfSummary.attentions, [
  { kind: 'partial-rollout', scope: { kind: 'stores', count: 1 } },
  { kind: 'pending-release', count: 1, latest: '1.0.46' },
], '已禁用的更高版本不算待投放')

const wpfForced = summarizeWpfLane([wpf('1.0.45', { isCurrent: true, targetVersion: '1.0.45', minimumSupportedVersion: '1.0.44', forceUpdate: true })])
assert.equal(wpfForced.mode, 'required')
assert.equal(wpfForced.minimum, null, '强制更新时最低版本不再影响结果，不重复展示')
assert.deepEqual(wpfForced.scope, { kind: 'all', count: 0 })

const wpfMinimumEqualsTarget = summarizeWpfLane([wpf('1.0.45', { isCurrent: true, targetVersion: '1.0.45', minimumSupportedVersion: '1.0.45' })])
assert.equal(wpfMinimumEqualsTarget.mode, 'required', '最低版本等于目标：低于目标的机器全部强制（后端 current < minimum）')
assert.equal(wpfMinimumEqualsTarget.minimum, null)

const wpfNoMinimum = summarizeWpfLane([wpf('1.0.45', { isCurrent: true, targetVersion: '1.0.45', minimumSupportedVersion: null })])
assert.equal(wpfNoMinimum.mode, 'optional', '未存最低版本时后端不按最低版本强制，不能用摘要的回退值判断')
assert.equal(wpfNoMinimum.minimum, null)

assert.equal(summarizeWpfLane([]).status, 'disabled')

// ---- 最近变更 ----
const recent = buildRecentLaneChanges([handheldNative, wpfSummary, ota, native], 2)
assert.deepEqual(recent.map((item) => item.key), ['wpf-windows', 'handheld-android-native'], '按时间倒序且跳过无时间的轨道')

console.log('releaseCenterLogic.test: ok')
