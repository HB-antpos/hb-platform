import { readFileSync } from 'node:fs'
import {
  buildAppStoreReleaseRegistrationSummary,
  buildNativePolicyConfirmationSummary,
  buildNativeUpdatePolicyRequest,
  buildOtaPolicyConfirmationSummary,
  buildOtaRolloutRequest,
  isAppUpdatePolicyVersionConflict,
  isValidMobileIosBuildNumber,
  isValidPosIpadBuildNumber,
  resolveNativeReleaseStatus,
  resolveOtaReleaseStatus,
  validateMinimumSupportedBuildNumber,
  validateTargetStores,
} from './appUpdatePolicyLogic'

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

assertDeepEqual(
  buildAppStoreReleaseRegistrationSummary({
    appStoreId: ' 123456789 ',
    buildNumber: ' 200 ',
    storefront: ' AU ',
  }),
  {
    appStoreId: '123456789',
    buildNumber: '200',
    storefront: 'au',
  },
  'App Store 登记二次确认必须展示与最终 POST 完全一致的规范化事实',
)

assertDeepEqual(
  buildNativeUpdatePolicyRequest({
    enabled: true,
    releaseId: ' release-1 ',
    minimumSupportedVersion: ' 1.1.0 ',
    minimumSupportedBuildNumber: 28,
    releaseMessage: ' 请升级 ',
    targetScope: 'stores',
    targetStoreGuids: [' store-2 ', 'store-1', 'store-2'],
  }, true, 7),
  {
    expectedPolicyVersion: 7,
    enabled: true,
    releaseId: 'release-1',
    minimumSupportedVersion: '1.1.0',
    releaseMessage: '请升级',
    minimumSupportedBuildNumber: 28,
    targetScope: 'stores',
    targetStoreGuids: ['store-2', 'store-1'],
  },
  'iPad 原生策略应清理字段并保留唯一分店 GUID',
)

assertDeepEqual(
  buildNativeUpdatePolicyRequest({
    enabled: true,
    releaseId: 'release-mobile',
    minimumSupportedVersion: '1.0.0',
    minimumSupportedBuildNumber: 31,
    releaseMessage: '',
    targetScope: 'stores',
    targetStoreGuids: ['store-1'],
  }, false, 5),
  {
    expectedPolicyVersion: 5,
    enabled: true,
    releaseId: 'release-mobile',
    minimumSupportedVersion: '1.0.0',
    releaseMessage: null,
    minimumSupportedBuildNumber: 31,
  },
  'Mobile 策略必须保持全局，不得泄漏 iPad 分店字段',
)

assertDeepEqual(
  buildNativeUpdatePolicyRequest({
    enabled: true,
    releaseId: 'release-mobile',
    minimumSupportedVersion: ' ',
  }, false, 0),
  {
    expectedPolicyVersion: 0,
    enabled: true,
    releaseId: 'release-mobile',
    minimumSupportedVersion: null,
    releaseMessage: null,
    minimumSupportedBuildNumber: null,
  },
  '原生策略最低版本留空时应仅提供可选更新，不得误启用强制门禁',
)
assertEqual(
  buildNativeUpdatePolicyRequest({
    enabled: true,
    releaseId: 'release-ipad',
    minimumSupportedVersion: ' ',
    minimumSupportedBuildNumber: 28,
    targetScope: 'all',
  }, true, 4).minimumSupportedBuildNumber,
  null,
  'iPad 最低构建号不得脱离最低支持版本写入请求',
)

assertDeepEqual(
  buildNativeUpdatePolicyRequest({
    enabled: false,
    releaseId: 'release-1',
    minimumSupportedVersion: '1.1.0',
    releaseMessage: 'old',
    targetScope: 'stores',
    targetStoreGuids: ['store-1'],
  }, true, 9),
  {
    expectedPolicyVersion: 9,
    enabled: false,
    releaseId: null,
    minimumSupportedVersion: null,
    minimumSupportedBuildNumber: null,
    releaseMessage: null,
    targetScope: 'all',
    targetStoreGuids: [],
  },
  '停用原生策略时必须清空发布和目标，避免误激活',
)

assertDeepEqual(
  buildOtaRolloutRequest({
    enabled: false,
    releaseId: 'ota-1',
    forceUpdate: true,
    targetScope: 'stores',
    targetStoreGuids: ['store-1'],
    releaseMessage: 'old',
  }, 11),
  {
    expectedPolicyVersion: 11,
    enabled: false,
    releaseId: null,
    forceUpdate: false,
    targetScope: 'all',
    targetStoreGuids: [],
    releaseMessage: null,
  },
  '停用 rollout 时必须清空强制和定向字段',
)

assertDeepEqual(
  buildNativePolicyConfirmationSummary({
    enabled: true,
    releaseId: ' release-1 ',
    minimumSupportedVersion: ' 1.1.0 ',
    minimumSupportedBuildNumber: 28,
    targetScope: 'stores',
    targetStoreGuids: [' store-2 ', 'store-1', 'store-2'],
  }, true),
  {
    releaseId: 'release-1',
    targetScope: 'stores',
    targetStoreGuids: ['store-2', 'store-1'],
    updateMode: 'required',
    minimumSupportedVersion: '1.1.0',
    minimumSupportedBuildNumber: 28,
  },
  'iPad 原生二次确认必须展示规范化后的发布、分店范围、最低支持版本和构建号',
)

assertDeepEqual(
  buildNativePolicyConfirmationSummary({
    enabled: true,
    releaseId: 'release-mobile',
    minimumSupportedVersion: ' 1.0.2 ',
    minimumSupportedBuildNumber: 31,
    targetScope: 'stores',
    targetStoreGuids: ['store-1'],
  }, false),
  {
    releaseId: 'release-mobile',
    targetScope: 'all',
    targetStoreGuids: [],
    updateMode: 'required',
    minimumSupportedVersion: '1.0.2',
    minimumSupportedBuildNumber: 31,
  },
  'Mobile 原生二次确认必须固定为全局范围，并展示最低支持版本和构建号',
)

assertDeepEqual(
  buildOtaPolicyConfirmationSummary({
    enabled: true,
    releaseId: ' ota-1 ',
    forceUpdate: true,
    targetScope: 'stores',
    targetStoreGuids: [' store-1 ', 'store-1'],
  }),
  {
    releaseId: 'ota-1',
    targetScope: 'stores',
    targetStoreGuids: ['store-1'],
    updateMode: 'required',
    minimumSupportedVersion: null,
    minimumSupportedBuildNumber: null,
  },
  'OTA 二次确认必须展示已登记发布、投放范围和强制状态',
)

assertEqual(validateTargetStores('all', []), true, '全部分店目标无需选择 GUID')
assertEqual(validateTargetStores('stores', []), false, '指定分店至少选择一项')
assertEqual(validateTargetStores('stores', ['store-1']), true, '指定分店有 GUID 时可保存')
assertEqual(isValidPosIpadBuildNumber('0'), true, 'iPad 构建号允许 Int32 最小值 0')
assertEqual(
  isValidPosIpadBuildNumber('2147483647'),
  true,
  'iPad 构建号允许 Int32 最大值',
)
assertEqual(isValidPosIpadBuildNumber('-1'), false, 'iPad 构建号不允许负数')
assertEqual(isValidPosIpadBuildNumber('1.5'), false, 'iPad 构建号不允许小数')
assertEqual(
  isValidPosIpadBuildNumber('2147483648'),
  false,
  'iPad 构建号不得超过 Int32 最大值',
)
assertEqual(isValidPosIpadBuildNumber('build-28'), false, 'iPad 构建号只允许整数')
assertEqual(isValidMobileIosBuildNumber('0'), true, 'Mobile iOS 构建号允许 Int32 最小值 0')
assertEqual(
  isValidMobileIosBuildNumber('2147483647'),
  true,
  'Mobile iOS 构建号允许 Int32 最大值',
)
assertEqual(isValidMobileIosBuildNumber('-1'), false, 'Mobile iOS 构建号不允许负数')
assertEqual(isValidMobileIosBuildNumber('1.5'), false, 'Mobile iOS 构建号不允许小数')
assertEqual(
  isValidMobileIosBuildNumber('2147483648'),
  false,
  'Mobile iOS 构建号不得超过 Int32 最大值',
)
assertEqual(
  validateMinimumSupportedBuildNumber('1.2.0', 28),
  true,
  '填写最低支持版本后才可设置 iPad 最低构建号',
)
assertEqual(
  validateMinimumSupportedBuildNumber(' ', 28),
  false,
  'iPad 最低构建号不能脱离最低支持版本单独生效',
)
assertEqual(
  validateMinimumSupportedBuildNumber('1.2.0', 2_147_483_648),
  false,
  'iPad 最低构建号表单不得接受超过 Int32 的数值',
)
assertEqual(
  validateMinimumSupportedBuildNumber(null, null),
  true,
  '未设置最低构建号时无需最低支持版本',
)

assertEqual(
  isAppUpdatePolicyVersionConflict({
    status: 409,
    payload: { errorCode: 'APP_UPDATE_POLICY_VERSION_CONFLICT' },
  }),
  true,
  '策略版本冲突必须按 HTTP 409 和冻结错误码识别',
)
assertEqual(
  isAppUpdatePolicyVersionConflict({
    status: 409,
    payload: { errorCode: 'APP_UPDATE_POLICY_VERSION_REQUIRED' },
  }),
  true,
  '遗漏 expectedPolicyVersion 的 409 也必须重新加载权威状态',
)
assertEqual(
  isAppUpdatePolicyVersionConflict({
    status: 500,
    payload: { errorCode: 'APP_UPDATE_POLICY_VERSION_CONFLICT' },
  }),
  false,
  '非 409 响应不得误报并发冲突',
)

assertEqual(
  resolveNativeReleaseStatus('release-1', 'release-1', true),
  'active',
  '当前启用策略引用的 App Store 发布应标记已激活',
)
assertEqual(
  resolveNativeReleaseStatus('release-2', 'release-1', true),
  'verified',
  '未激活的 App Store 发布仍应标记 Apple 已验证',
)
assertEqual(
  resolveOtaReleaseStatus('ota-1', 'ota-1', true),
  'active',
  '当前 rollout 发布应标记已激活',
)
assertEqual(
  resolveOtaReleaseStatus('ota-2', 'ota-1', true),
  'registered',
  '未投放 OTA 只标记已登记',
)

const panelSource = readFileSync(
  'src/pages/System/AppDownloads/AppUpdatePolicyPanel.tsx',
  'utf8',
)
const requestLogicSource = readFileSync(
  'src/pages/System/AppDownloads/appUpdatePolicyRequestLogic.ts',
  'utf8',
)
const zhLocale = JSON.parse(readFileSync('src/i18n/locales/zh.json', 'utf8'))
const enLocale = JSON.parse(readFileSync('src/i18n/locales/en.json', 'utf8'))

for (const locale of [zhLocale, enLocale]) {
  const copy = locale.system.appDownloads.updatePolicy
  assertEqual(
    typeof copy.activateNativeConfirmDescription,
    'string',
    '原生更新必须使用独立的 Apple Lookup 二次确认文案',
  )
  assertEqual(
    typeof copy.activateOtaConfirmDescription,
    'string',
    'OTA 必须使用独立的 EAS rollout 二次确认文案',
  )
  assertEqual(
    copy.activateOtaConfirmDescription.includes('Apple Lookup'),
    false,
    'OTA 确认不得宣称已经 Apple Lookup 验证',
  )
  assertEqual(
    copy.registrationNotActivation.includes('仅用于审计')
      || copy.registrationNotActivation.includes('audit only'),
    false,
    'Mobile/iPad build-aware 策略不得把构建号描述成仅用于审计',
  )
  assertEqual(
    copy.registrationNotActivation.includes('Apple Lookup'),
    true,
    '登记说明必须明确 Apple Lookup 的构建号验证边界',
  )
  assertEqual(
    copy.registrationNotActivation.includes('App Store Connect'),
    true,
    '登记说明必须要求管理员在 App Store Connect 人工确认构建号',
  )
}

assertEqual(
  zhLocale.system.appDownloads.updatePolicy.registrationNotActivation.includes(
    '可选、强制或无需更新',
  ),
  true,
  '中文登记说明必须解释构建号会参与原生更新资格判断',
)
assertEqual(
  enLocale.system.appDownloads.updatePolicy.registrationNotActivation.includes(
    'optional, required, or not needed',
  ),
  true,
  '英文登记说明必须解释构建号会参与原生更新资格判断',
)

assertEqual(
  panelSource.includes("kind === 'native'"),
  true,
  '原生与 OTA 激活确认必须按策略类型选择独立文案',
)
for (const key of ['confirmRelease', 'confirmScope', 'confirmUpdateMode']) {
  assertEqual(
    panelSource.includes(`updatePolicy.${key}`),
    true,
    `二次确认必须渲染 ${key} 摘要`,
  )
}
for (const key of ['confirmMinimumVersion', 'confirmMinimumBuild']) {
  assertEqual(
    panelSource.includes(`updatePolicy.${key}`),
    true,
    `原生二次确认必须明确渲染 ${key}`,
  )
}
assertEqual(
  panelSource.includes("app === 'mobile-ios' || app === 'pos-ipad'"),
  true,
  'Mobile 和 iPad 原生策略必须共同启用最低构建号编辑能力',
)
assertEqual(
  panelSource.includes('mobileBuildNotVerifiedWarning'),
  true,
  'Mobile 登记和策略确认必须警告 Apple Lookup 不验证构建号',
)
assertEqual(
  panelSource.includes('onFinish={handleRegisterRelease}'),
  true,
  'App Store 登记 Form 必须通过 onFinish 统一键盘提交',
)
assertEqual(
  panelSource.includes('onClick={() => registerForm.submit()}'),
  true,
  '登记 footer 按钮必须调用 Form.submit，与 Enter 共用提交流程',
)
const registerHandlerSource = panelSource.slice(
  panelSource.indexOf('function handleRegisterRelease'),
  panelSource.indexOf('function confirmPolicySave'),
)
assertEqual(
  registerHandlerSource.includes('Modal.confirm({'),
  true,
  'App Store 表单通过校验后必须先显示独立二次确认',
)
assertEqual(
  registerHandlerSource.includes('onOk:'),
  true,
  'App Store 不可变发布事实只能由二次确认的 onOk 发起登记',
)
for (const lane of [
  'loadMobileNativeLane',
  'loadIpadNativeLane',
  'loadIpadOtaLane',
  'loadStoreOptionsLane',
]) {
  assertEqual(
    panelSource.includes(lane),
    true,
    `更新策略必须保留独立加载通道 ${lane}`,
  )
}
assertEqual(
  panelSource.includes('await Promise.allSettled(loads)'),
  true,
  '刷新必须并发启动当前轨道的独立加载通道，单个通道失败不阻塞其它通道',
)
assertEqual(
  panelSource.includes("if (lane === 'ipad-native') {\n      loads.push(loadIpadNativeLane(), loadStoreOptionsLane())")
    && panelSource.includes("if (lane === 'ipad-ota') {\n      loads.push(loadIpadOtaLane(), loadStoreOptionsLane())"),
  true,
  'iPad 原生与 OTA 轨道都依赖分店选项，必须同时加载分店通道',
)
assertEqual(
  requestLogicSource.includes('new AbortController()'),
  true,
  '每个加载通道必须以 AbortController 取消旧请求',
)
assertEqual(
  panelSource.includes('savePolicyWithConflictReload('),
  true,
  '409 并发冲突必须走显式识别和权威状态重载',
)
const storeOptionsLaneSource = panelSource.slice(
  panelSource.indexOf('const loadStoreOptionsLane'),
  panelSource.indexOf('const refreshAll'),
)
assertEqual(
  storeOptionsLaneSource.includes('() => setStoreOptions([])'),
  true,
  'Store options 加载失败必须清除旧名称，让现有策略回退显示 GUID',
)
assertEqual(
  panelSource.includes('storeOptionsUsable'),
  true,
  'Store options 不可用时必须阻止指定分店策略保存',
)
assertEqual(
  panelSource.includes('minimumSupportedBuildNumber'),
  true,
  'iPad 原生策略表单和确认摘要必须接入最低支持构建号',
)
assertEqual(
  panelSource.includes('ipadBuildNotVerifiedWarning'),
  true,
  'iPad App Store 登记必须明确提示 Apple Lookup 不校验构建号',
)
const policyConfirmSource = panelSource.slice(
  panelSource.indexOf('function confirmPolicySave'),
  panelSource.indexOf('async function saveNativePolicy'),
)
assertEqual(
  policyConfirmSource.includes("nativeApp === 'pos-ipad'"),
  true,
  '只有 iPad 原生策略激活确认需要显示构建号人工核对警告',
)
assertEqual(
  policyConfirmSource.includes('ipadPolicyBuildConfirmDescription'),
  true,
  'iPad 原生策略激活前必须再次确认 Apple Lookup 未验证构建号',
)

const nativeReleaseActionSource = panelSource.slice(
  panelSource.indexOf('function nativeReleaseColumns'),
  panelSource.indexOf('const otaReleaseColumns'),
)
assertEqual(
  nativeReleaseActionSource.includes("app === 'mobile-ios' ?"),
  true,
  '只有 Mobile iOS 发布行需要提供 App Store 安装二维码入口',
)
assertEqual(
  nativeReleaseActionSource.includes('icon={<QrcodeOutlined />}')
    && nativeReleaseActionSource.includes('onClick={() => setQrRelease(release)}')
    && nativeReleaseActionSource.includes('disabled={!url}'),
  true,
  'Mobile iOS 二维码入口必须依赖当前行已经过白名单校验的 App Store URL',
)
assertEqual(
  panelSource.includes('open={Boolean(qrRelease)}')
    && panelSource.includes('onCancel={() => setQrRelease(null)}')
    && panelSource.includes('<QRCodeSVG')
    && panelSource.includes('value={qrReleaseUrl}')
    && panelSource.includes('marginSize={4}')
    && panelSource.includes('level="M"')
    && panelSource.includes('copyable={{ text: qrReleaseUrl }}'),
  true,
  'App Store 安装二维码弹窗必须展示带四模块静区的二维码、版本信息和同一条可复制安全链接',
)

assertEqual(
  zhLocale.system.appDownloads.updatePolicy.viewInstallQrCode,
  '查看安装二维码',
  '中文二维码操作必须明确用于安装',
)
assertEqual(
  enLocale.system.appDownloads.updatePolicy.viewInstallQrCode,
  'View Install QR Code',
  '英文二维码操作必须明确用于安装',
)
for (const locale of [zhLocale, enLocale]) {
  const copy = locale.system.appDownloads.updatePolicy
  for (const key of ['appStoreQrCodeTitle', 'appStoreQrCodeHint', 'appStoreQrCodeAccessibleTitle']) {
    assertEqual(
      typeof copy[key],
      'string',
      `App Store 安装二维码必须定义多语言文案 ${key}`,
    )
  }
}

console.log('appUpdatePolicyLogic.test.ts: ok')
