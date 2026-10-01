import { readFileSync } from 'node:fs'
import {
  buildMobileOtaPolicyRequest,
  formatMobileOtaReleaseLabel,
  isMobileOtaReleaseCompatibleWithLane,
  parseMobileOtaRevisionSnapshot,
  resolveMobileOtaReleaseActivation,
} from './mobileOtaPolicyLogic'

function assertEqual<T>(actual: T, expected: T, message: string) {
  if (actual !== expected) {
    throw new Error(`${message}: expected ${String(expected)}, got ${String(actual)}`)
  }
}

function assertDeepEqual(actual: unknown, expected: unknown, message: string) {
  const actualJson = JSON.stringify(actual)
  const expectedJson = JSON.stringify(expected)
  if (actualJson !== expectedJson) {
    throw new Error(`${message}: expected ${expectedJson}, got ${actualJson}`)
  }
}

const productionAndroidRelease = {
  id: 'release-android-1',
  releaseBatchId: 'batch-1',
  appKey: 'mobile' as const,
  environment: 'production' as const,
  clientChannel: 'production',
  releaseChannel: 'mobile-production-android-release-20260827-120000',
  easBranch: 'mobile-production-android-release-20260827-120000',
  projectName: 'hbgroup',
  platform: 'android' as const,
  runtimeVersion: '1.0.2',
  updateGroupId: '11111111-1111-1111-1111-111111111111',
  updateId: '22222222-2222-2222-2222-222222222222',
  message: 'Android 可选更新',
  gitCommitHash: 'abcdef12',
  dashboardUrl: 'https://expo.dev/accounts/hb/projects/hbgroup/updates/release-1',
  publishedAtUtc: '2026-08-27T02:00:00Z',
  isRollback: false,
  rollbackOfReleaseId: null,
  factFingerprint: 'f'.repeat(64),
  legacy: false,
  registrationSource: 'mobile-release-script',
  createdAt: '2026-08-27T02:01:00Z',
  createdBy: 'release-bot',
}

assertDeepEqual(
  buildMobileOtaPolicyRequest({
    enabled: false,
    required: true,
    targetReleaseId: 'stale-release',
    releaseMessage: '旧说明',
  }, 7),
  {
    expectedPolicyVersion: 7,
    enabled: false,
    required: false,
    targetReleaseId: null,
    releaseMessage: null,
  },
  '停用 Mobile OTA lane 时必须清空目标、required 和说明',
)

assertDeepEqual(
  buildMobileOtaPolicyRequest({
    enabled: true,
    required: true,
    targetReleaseId: ' release-android-1 ',
    releaseMessage: ' 必须更新 ',
  }, 8),
  {
    expectedPolicyVersion: 8,
    enabled: true,
    required: true,
    targetReleaseId: 'release-android-1',
    releaseMessage: '必须更新',
  },
  '启用 Mobile OTA lane 时必须保留精确发布和 required 模式',
)

assertEqual(
  isMobileOtaReleaseCompatibleWithLane(
    productionAndroidRelease,
    'production',
    'android',
  ),
  true,
  '发布事实必须与 app/environment/platform lane 完全一致',
)
assertEqual(
  isMobileOtaReleaseCompatibleWithLane(
    { ...productionAndroidRelease, environment: 'preview' },
    'production',
    'android',
  ),
  false,
  'preview 发布不得进入 production 候选',
)
assertEqual(
  isMobileOtaReleaseCompatibleWithLane(
    { ...productionAndroidRelease, platform: 'ios' },
    'production',
    'android',
  ),
  false,
  'iOS 发布不得进入 Android 候选',
)

assertEqual(
  formatMobileOtaReleaseLabel(productionAndroidRelease),
  '1.0.2 · mobile-production-android-release-20260827-120000 · 22222222',
  '候选标签必须同时展示 Runtime、release channel 与短 Update ID',
)

assertDeepEqual(
  parseMobileOtaRevisionSnapshot('{"enabled":true,"required":false}'),
  { enabled: true, required: false },
  'revision 必须解析完整快照供审计展示',
)
assertDeepEqual(
  parseMobileOtaRevisionSnapshot('not-json'),
  {},
  '损坏 revision 快照不得打断管理页渲染',
)

const parentPanelSource = readFileSync(
  'src/pages/System/AppDownloads/AppUpdatePolicyPanel.tsx',
  'utf8',
)
const mobileOtaPanelSource = readFileSync(
  'src/pages/System/AppDownloads/MobileOtaPolicyTab.tsx',
  'utf8',
)
const orderedTabKeys = [
  "key: 'mobile-native'",
  "key: 'mobile-ota'",
  "key: 'ipad-native'",
  "key: 'ipad-ota'",
  "key: 'pos-handheld'",
]
let previousTabIndex = -1
for (const tabKey of orderedTabKeys) {
  const tabIndex = parentPanelSource.indexOf(tabKey)
  assertEqual(tabIndex > previousTabIndex, true, `顶级页签顺序必须包含 ${tabKey}`)
  previousTabIndex = tabIndex
}
for (const environment of ['production', 'preview']) {
  assertEqual(
    mobileOtaPanelSource.includes(`value: '${environment}'`),
    true,
    `Mobile OTA 必须提供 ${environment} 环境`,
  )
}
for (const platform of ['android', 'ios']) {
  assertEqual(
    mobileOtaPanelSource.includes(`key: '${platform}'`),
    true,
    `Mobile OTA 必须提供 ${platform} 独立 lane`,
  )
}
for (const invariant of [
  'savePolicyWithConflictReload(',
  'requiredWarning',
  'compatibilityBoundary',
  'revision.snapshotJson',
  'legacyHistoryWarning',
]) {
  assertEqual(
    mobileOtaPanelSource.includes(invariant),
    true,
    `Mobile OTA 管理页必须保留安全合同 ${invariant}`,
  )
}
assertEqual(
  /EAS_TOKEN|EXPO_TOKEN|eas\s+update/i.test(mobileOtaPanelSource),
  false,
  'Mobile OTA 浏览器组件不得包含 EAS 凭据或直接发布命令',
)

// 主目标与按 Runtime 的附加目标都在投放；只比对主目标会把附加目标误显示为「已登记」。
const activePolicy = {
  enabled: true,
  targetReleaseId: 'release-105',
  additionalTargets: [
    { targetReleaseId: 'release-106', targetRuntimeVersion: '1.0.6' },
    { targetReleaseId: 'release-107', targetRuntimeVersion: '1.0.7' },
  ],
}
assertDeepEqual(
  resolveMobileOtaReleaseActivation('release-105', activePolicy),
  { kind: 'primary' },
  '主目标必须识别为已激活',
)
assertDeepEqual(
  resolveMobileOtaReleaseActivation('release-107', activePolicy),
  { kind: 'additional', runtimeVersion: '1.0.7' },
  '附加目标必须识别为已激活并带上所属 Runtime',
)
assertEqual(
  resolveMobileOtaReleaseActivation('release-old', activePolicy),
  null,
  '未被策略引用的发布只算已登记',
)
assertEqual(
  resolveMobileOtaReleaseActivation('release-107', { ...activePolicy, enabled: false }),
  null,
  '策略停用时主目标与附加目标都不再投放',
)
assertEqual(resolveMobileOtaReleaseActivation('release-105', null), null, '策略未加载时不标记激活')

for (const localePath of ['src/i18n/locales/zh.json', 'src/i18n/locales/en.json']) {
  const locale = JSON.parse(readFileSync(localePath, 'utf8'))
  assertEqual(
    typeof locale.system.appDownloads.updatePolicy.tabs.mobileOta,
    'string',
    '中英文必须提供 Mobile OTA 顶级页签文案',
  )
  assertEqual(
    typeof locale.system.appDownloads.updatePolicy.mobileOta.compatibilityBoundary,
    'string',
    '中英文必须明确 required 的 Runtime/bootstrap 覆盖边界',
  )
  for (const key of ['activeAdditional', 'additionalTargets', 'additionalTargetsKept', 'additionalTargetsNone']) {
    assertEqual(
      typeof locale.system.appDownloads.updatePolicy.mobileOta[key],
      'string',
      `中英文必须提供附加目标文案 ${key}`,
    )
  }
}

console.log('mobileOtaPolicyLogic.test.ts: ok')
