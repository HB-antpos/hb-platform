import { readFileSync } from 'node:fs'

import type { MobileAndroidLatestBuild } from '../../../types/appUpdatePolicy'

import { isAppUpdatePolicyVersionConflict } from './appUpdatePolicyLogic'
import {
  buildMobileAndroidNativePolicyConfirmation,
  buildMobileAndroidNativePolicyFormValue,
  buildMobileAndroidNativePolicyRequest,
  formatMobileAndroidBuildLabel,
  mobileAndroidNativePolicyErrorMessageKey,
  resolveMobileAndroidNativePolicySaveError,
  validateMobileAndroidNativePolicy,
  type MobileAndroidNativePolicyValidationError,
} from './mobileAndroidNativePolicyLogic'

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

const latestBuild: MobileAndroidLatestBuild = {
  easBuildId: 'build-63',
  appVersion: '1.0.10',
  appBuildVersion: 63,
  completedAt: '2026-10-03T05:06:00Z',
}

assertEqual(
  formatMobileAndroidBuildLabel(latestBuild),
  '1.0.10 (63)',
  '公开安卓包必须以「版本 (构建号)」展示',
)
assertEqual(formatMobileAndroidBuildLabel(null), '--', '无公开包时展示占位')

// 请求构建：启用时规范化，停用时清空最低构建号与说明。
assertDeepEqual(
  buildMobileAndroidNativePolicyRequest({
    enabled: true,
    minimumSupportedBuildNumber: 60,
    releaseMessage: '  请安装新版  ',
  }, 3),
  {
    expectedPolicyVersion: 3,
    enabled: true,
    minimumSupportedBuildNumber: 60,
    releaseMessage: '请安装新版',
  },
  '启用时 PUT 必须携带策略版本、最低构建号和去空白的说明',
)
assertDeepEqual(
  buildMobileAndroidNativePolicyRequest({
    enabled: true,
    minimumSupportedBuildNumber: 63,
    releaseMessage: '   ',
  }, 0),
  {
    expectedPolicyVersion: 0,
    enabled: true,
    minimumSupportedBuildNumber: 63,
    releaseMessage: null,
  },
  '空白说明必须提交为 null',
)
assertDeepEqual(
  buildMobileAndroidNativePolicyRequest({
    enabled: false,
    minimumSupportedBuildNumber: 60,
    releaseMessage: '旧说明',
  }, 5),
  {
    expectedPolicyVersion: 5,
    enabled: false,
    minimumSupportedBuildNumber: null,
    releaseMessage: null,
  },
  '停用时必须清空最低构建号和说明',
)

// 表单校验：与后端 PUT 错误码口径一致。
const validationCases: {
  name: string
  enabled: boolean
  minimum: number | null
  latest: MobileAndroidLatestBuild | null
  message?: string
  expected: MobileAndroidNativePolicyValidationError | null
}[] = [
  { name: '停用时不校验', enabled: false, minimum: null, latest: null, expected: null },
  { name: '无公开包不能启用', enabled: true, minimum: 10, latest: null, expected: 'NO_LATEST_BUILD' },
  { name: '启用时最低构建号必填', enabled: true, minimum: null, latest: latestBuild, expected: 'MINIMUM_BUILD_REQUIRED' },
  { name: '最低构建号不能为 0', enabled: true, minimum: 0, latest: latestBuild, expected: 'MINIMUM_BUILD_INVALID' },
  { name: '最低构建号不能为负数', enabled: true, minimum: -1, latest: latestBuild, expected: 'MINIMUM_BUILD_INVALID' },
  { name: '最低构建号必须是整数', enabled: true, minimum: 1.5, latest: latestBuild, expected: 'MINIMUM_BUILD_INVALID' },
  { name: '最低构建号可为 1', enabled: true, minimum: 1, latest: latestBuild, expected: null },
  { name: '最低构建号可等于公开包', enabled: true, minimum: 63, latest: latestBuild, expected: null },
  { name: '最低构建号不能高于公开包', enabled: true, minimum: 64, latest: latestBuild, expected: 'MINIMUM_BUILD_ABOVE_LATEST' },
  {
    name: '说明不能超过 1000 字',
    enabled: true,
    minimum: 60,
    latest: latestBuild,
    message: 'x'.repeat(1001),
    expected: 'RELEASE_MESSAGE_TOO_LONG',
  },
  {
    name: '说明恰好 1000 字可保存',
    enabled: true,
    minimum: 60,
    latest: latestBuild,
    message: 'x'.repeat(1000),
    expected: null,
  },
]
for (const testCase of validationCases) {
  assertEqual(
    validateMobileAndroidNativePolicy({
      enabled: testCase.enabled,
      minimumSupportedBuildNumber: testCase.minimum,
      releaseMessage: testCase.message,
    }, testCase.latest),
    testCase.expected,
    `安卓原生策略校验：${testCase.name}`,
  )
}

assertDeepEqual(
  buildMobileAndroidNativePolicyFormValue({
    enabled: true,
    minimumSupportedBuildNumber: 60,
    releaseMessage: '请升级',
    policyVersion: 2,
    updatedAt: null,
    updatedBy: null,
    latestBuild,
  }),
  { enabled: true, minimumSupportedBuildNumber: 60, releaseMessage: '请升级' },
  '表单回填只取策略的三个可编辑字段',
)

assertDeepEqual(
  buildMobileAndroidNativePolicyConfirmation({
    enabled: true,
    minimumSupportedBuildNumber: 60,
    releaseMessage: ' 请升级 ',
  }, latestBuild),
  {
    enabled: true,
    minimumSupportedBuildNumber: 60,
    latestBuildLabel: '1.0.10 (63)',
    releaseMessage: '请升级',
  },
  '二次确认必须展示与 PUT 一致的规范化值',
)
assertDeepEqual(
  buildMobileAndroidNativePolicyConfirmation({
    enabled: false,
    minimumSupportedBuildNumber: 60,
    releaseMessage: '旧说明',
  }, latestBuild),
  {
    enabled: false,
    minimumSupportedBuildNumber: null,
    latestBuildLabel: '1.0.10 (63)',
    releaseMessage: null,
  },
  '停用确认不得展示将被清空的旧值',
)

// 保存失败：业务错误码识别；版本冲突复用现有判断。
assertEqual(
  resolveMobileAndroidNativePolicySaveError({
    status: 400,
    payload: { success: false, errorCode: 'MINIMUM_BUILD_ABOVE_LATEST' },
  }),
  'MINIMUM_BUILD_ABOVE_LATEST',
  '后端拒绝高于公开包的最低构建号时必须给出具体原因',
)
assertEqual(
  resolveMobileAndroidNativePolicySaveError({
    status: 400,
    payload: { data: { code: 'RELEASE_MESSAGE_TOO_LONG' } },
  }),
  'RELEASE_MESSAGE_TOO_LONG',
  '嵌套 data 中的错误码也必须识别',
)
assertEqual(
  resolveMobileAndroidNativePolicySaveError({
    status: 409,
    payload: { errorCode: 'APP_UPDATE_POLICY_VERSION_CONFLICT' },
  }),
  null,
  '版本冲突不属于表单校验错误',
)
assertEqual(resolveMobileAndroidNativePolicySaveError(new Error('x')), null, '未知错误走通用提示')
assertEqual(
  isAppUpdatePolicyVersionConflict({
    status: 409,
    payload: { errorCode: 'APP_UPDATE_POLICY_VERSION_CONFLICT' },
  }),
  true,
  '安卓原生策略 409 版本冲突必须被现有冲突判断识别',
)
assertEqual(
  isAppUpdatePolicyVersionConflict({
    status: 409,
    payload: { errorCode: 'APP_UPDATE_POLICY_VERSION_REQUIRED' },
  }),
  true,
  '缺少策略版本的 409 也必须走重载流程',
)

// 文案契约：中英文都必须定义页签、表单与确认文案。
const zhLocale = JSON.parse(readFileSync('src/i18n/locales/zh.json', 'utf8'))
const enLocale = JSON.parse(readFileSync('src/i18n/locales/en.json', 'utf8'))
const errorCodes: MobileAndroidNativePolicyValidationError[] = [
  'NO_LATEST_BUILD',
  'MINIMUM_BUILD_REQUIRED',
  'MINIMUM_BUILD_INVALID',
  'MINIMUM_BUILD_ABOVE_LATEST',
  'RELEASE_MESSAGE_TOO_LONG',
]
for (const locale of [zhLocale, enLocale]) {
  const copy = locale.system.appDownloads.updatePolicy
  assertEqual(typeof copy.tabs.mobileAndroidNative, 'string', '必须定义「Mobile 安卓原生」页签文案')
  for (const key of [
    'boundaryTitle',
    'boundaryDescription',
    'loadFailed',
    'latestBuildTitle',
    'latestBuild',
    'completedAt',
    'noLatestBuild',
    'minimumBuild',
    'minimumBuildHelp',
    'confirmTitle',
    'blockWarning',
    'disableConfirmDescription',
  ]) {
    assertEqual(typeof copy.mobileAndroid[key], 'string', `安卓原生策略必须定义多语言文案 ${key}`)
  }
  for (const code of errorCodes) {
    const key = mobileAndroidNativePolicyErrorMessageKey(code)
    const leaf = key.split('.').pop() as string
    assertEqual(typeof copy.mobileAndroid[leaf], 'string', `错误码 ${code} 必须有多语言文案`)
  }
  assertEqual(
    copy.mobileAndroid.blockWarning.includes('{{build}}'),
    true,
    '拦截提示必须写明具体的最低构建号',
  )
  assertEqual(
    copy.mobileAndroid.minimumBuildHelp.includes('{{max}}'),
    true,
    '最低构建号说明必须写明公开包构建号上限',
  )
}
assertEqual(
  zhLocale.system.appDownloads.updatePolicy.mobileAndroid.blockWarning,
  '构建号低于 {{build}} 的安卓设备会被整页拦截，必须安装最新安装包后才能继续使用。',
  '中文确认必须明确整页拦截与必须安装最新安装包',
)

// 页签接线：面板挂载独立组件，组件走 409 重载与二次确认。
const panelSource = readFileSync('src/pages/System/AppDownloads/AppUpdatePolicyPanel.tsx', 'utf8')
const tabSource = readFileSync('src/pages/System/AppDownloads/MobileAndroidNativePolicyTab.tsx', 'utf8')
assertEqual(
  panelSource.includes("key: 'mobile-android-native'")
    && panelSource.includes('<MobileAndroidNativePolicyTab'),
  true,
  '更新策略面板必须挂载「Mobile 安卓原生」页签',
)
assertEqual(
  panelSource.includes('setMobileAndroidRefreshVersion((version) => version + 1)'),
  true,
  '面板全局刷新必须同时刷新安卓原生页签',
)
assertEqual(
  tabSource.includes('savePolicyWithConflictReload(')
    && tabSource.includes('isAppUpdatePolicyVersionConflict'),
  true,
  '安卓原生策略保存必须走 409 冲突识别与重载',
)
assertEqual(
  tabSource.includes('Modal.confirm({') && tabSource.includes('onOk: () => savePolicy(value)'),
  true,
  '安卓原生策略只能在二次确认后保存',
)
assertEqual(
  tabSource.includes('disabled={!canManage}'),
  true,
  '无管理权限时安卓原生策略表单必须只读',
)

console.log('mobileAndroidNativePolicyLogic.test.ts: ok')
