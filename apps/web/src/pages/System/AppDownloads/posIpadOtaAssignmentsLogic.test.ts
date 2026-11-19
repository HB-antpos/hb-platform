import type {
  PosIpadOtaAssignment,
  PosIpadOtaRelease,
} from '../../../types/appUpdatePolicy'
import {
  buildCreatePosIpadOtaAssignmentRequest,
  buildPosIpadOtaAssignmentFormValue,
  buildToggleEnabledRequest,
  buildUpdatePosIpadOtaAssignmentRequest,
  classifyPosIpadOtaAssignmentError,
  filterAssignableReleases,
  isExpiresAtValid,
  parseUtcTimestamp,
  readPosIpadOtaAssignmentErrorCode,
  shouldReloadAfterAssignmentError,
  sortPosIpadOtaAssignments,
} from './posIpadOtaAssignmentsLogic'

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

function release(id: string, runtimeVersion: string, publishedAtUtc: string): PosIpadOtaRelease {
  return {
    id,
    environment: 'production',
    updateGroupId: '',
    iosUpdateId: id,
    channel: 'pos-ipad-production',
    runtimeVersion,
    gitCommitHash: null,
    dashboardUrl: null,
    publishedAtUtc,
    isRollback: false,
    rollbackOfReleaseId: null,
    createdAt: publishedAtUtc,
    createdBy: null,
  }
}

function assignment(overrides: Partial<PosIpadOtaAssignment>): PosIpadOtaAssignment {
  return {
    id: 'a1',
    targetType: 'store',
    storeGuid: 'store-1',
    storeCode: '001',
    storeName: 'Brisbane',
    deviceRegistrationId: null,
    deviceNumber: null,
    deviceRemarks: null,
    deviceAppVersion: null,
    enabled: true,
    expiresAtUtc: null,
    isExpired: false,
    policyVersion: 5,
    releaseId: 'ota-1',
    release: null,
    forceUpdate: true,
    releaseMessage: '升级',
    note: '试点',
    updatedAt: null,
    updatedBy: null,
    ...overrides,
  }
}

// ---- 时间解析：缺时区补 Z（后端 UTC 语义），带时区原样 ----
assertEqual(
  parseUtcTimestamp('2026-12-01T00:00:00')?.toISOString(),
  '2026-12-01T00:00:00.000Z',
  '缺时区的时间按 UTC 解析',
)
assertEqual(
  parseUtcTimestamp('2026-12-01 10:00:00+10:00')?.toISOString(),
  '2026-12-01T00:00:00.000Z',
  '带时区的时间保持原语义',
)
assertEqual(parseUtcTimestamp(null), null, '空值返回 null')
assertEqual(parseUtcTimestamp('not-a-date'), null, '无法解析返回 null')

// ---- 创建请求：只带与类型匹配的目标 ----
const expires = { toISOString: () => '2026-12-01T00:00:00.000Z' }
assertDeepEqual(
  buildCreatePosIpadOtaAssignmentRequest({
    targetType: 'device',
    storeGuid: 'store-filter',
    deviceRegistrationId: 42,
    releaseId: 'ota-1',
    forceUpdate: true,
    releaseMessage: '  请升级  ',
    note: '   ',
    expiresAt: expires,
    enabled: true,
  }),
  {
    targetType: 'device',
    storeGuid: null,
    deviceRegistrationId: 42,
    releaseId: 'ota-1',
    forceUpdate: true,
    releaseMessage: '请升级',
    note: null,
    expiresAtUtc: '2026-12-01T00:00:00.000Z',
    enabled: true,
  },
  '设备覆盖不得把筛选用的分店当成目标提交，空白备注转 null',
)
assertDeepEqual(
  buildCreatePosIpadOtaAssignmentRequest({
    targetType: 'store',
    storeGuid: ' store-1 ',
    deviceRegistrationId: 42,
    releaseId: 'ota-1',
  }),
  {
    targetType: 'store',
    storeGuid: 'store-1',
    deviceRegistrationId: null,
    releaseId: 'ota-1',
    forceUpdate: false,
    releaseMessage: null,
    note: null,
    expiresAtUtc: null,
    enabled: true,
  },
  '分店覆盖不得带设备注册 ID，缺省启用、非强制、不过期',
)

// ---- 修改请求：带 expectedPolicyVersion，不含目标字段 ----
const updateRequest = buildUpdatePosIpadOtaAssignmentRequest(
  { targetType: 'store', storeGuid: 'x', releaseId: 'ota-2', forceUpdate: false, enabled: false },
  9,
)
assertDeepEqual(
  updateRequest,
  {
    expectedPolicyVersion: 9,
    releaseId: 'ota-2',
    forceUpdate: false,
    releaseMessage: null,
    note: null,
    expiresAtUtc: null,
    enabled: false,
  },
  '修改请求必须带 expectedPolicyVersion 且不含目标字段',
)
assertEqual('targetType' in updateRequest, false, '目标不可变，修改请求不得带 targetType')

// ---- 一键启停：沿用原行字段，只翻转 enabled ----
assertDeepEqual(
  buildToggleEnabledRequest(
    assignment({ expiresAtUtc: '2026-12-01T00:00:00', policyVersion: 7 }),
    false,
  ),
  {
    expectedPolicyVersion: 7,
    releaseId: 'ota-1',
    forceUpdate: true,
    releaseMessage: '升级',
    note: '试点',
    expiresAtUtc: '2026-12-01T00:00:00.000Z',
    enabled: false,
  },
  '启停必须沿用原到期时间（缺时区按 UTC）与全部投放字段',
)

// ---- 表单初值 ----
assertEqual(buildPosIpadOtaAssignmentFormValue(null).enabled, true, '新增默认启用')
assertEqual(buildPosIpadOtaAssignmentFormValue(null).targetType, 'store', '新增默认按分店')
assertEqual(
  buildPosIpadOtaAssignmentFormValue(assignment({ note: '试点' })).note,
  '试点',
  '编辑回显备注',
)

// ---- 到期时间校验 ----
const now = Date.parse('2026-11-19T00:00:00.000Z')
assertEqual(isExpiresAtValid(true, null, now), true, '空到期时间合法')
assertEqual(isExpiresAtValid(true, expires, now), true, '启用 + 未来到期合法')
assertEqual(
  isExpiresAtValid(true, { toISOString: () => '2026-11-01T00:00:00.000Z' }, now),
  false,
  '启用 + 过去到期不合法',
)
assertEqual(
  isExpiresAtValid(false, { toISOString: () => '2026-11-01T00:00:00.000Z' }, now),
  true,
  '停用的覆盖允许保留过去的到期时间',
)

// ---- 可选发布：按基线 runtime 过滤并保留已选项，新发布在前 ----
const releases = [
  release('old-110', '1.1.0', '2026-10-01T00:00:00Z'),
  release('new-100', '1.0.0', '2026-11-10T00:00:00Z'),
  release('old-100', '1.0.0', '2026-10-01T00:00:00Z'),
]
assertDeepEqual(
  filterAssignableReleases(releases, '1.0.0').map((item) => item.id),
  ['new-100', 'old-100'],
  '只列 runtime 与基线一致的发布，按发布时间倒序',
)
assertEqual(
  filterAssignableReleases(releases, '1.0.0', 'old-110').some((item) => item.id === 'old-110'),
  true,
  '编辑时已选中的发布即使 runtime 不同也必须保留',
)
assertEqual(
  filterAssignableReleases(releases, null).length,
  3,
  '基线未指定发布时无法按 runtime 过滤，列出全部',
)

// ---- 排序：设备在前，再按分店编号 / 设备编号 ----
assertDeepEqual(
  sortPosIpadOtaAssignments([
    assignment({ id: 's2', storeCode: '002' }),
    assignment({ id: 'd2', targetType: 'device', storeCode: '002', deviceNumber: 'B' }),
    assignment({ id: 's1', storeCode: '001' }),
    assignment({ id: 'd1', targetType: 'device', storeCode: '002', deviceNumber: 'A' }),
  ]).map((item) => item.id),
  ['d1', 'd2', 's1', 's2'],
  '设备覆盖优先级最高，排在分店覆盖之前',
)

// ---- 错误分类 ----
const conflict = Object.assign(new Error('版本变化'), {
  status: 409,
  payload: { success: false, errorCode: 'APP_UPDATE_POLICY_VERSION_CONFLICT' },
})
assertEqual(classifyPosIpadOtaAssignmentError(conflict), 'version-conflict', '409 版本冲突')
const versionRequired = Object.assign(new Error('缺版本'), {
  status: 409,
  payload: { errorCode: 'APP_UPDATE_POLICY_VERSION_REQUIRED' },
})
assertEqual(classifyPosIpadOtaAssignmentError(versionRequired), 'version-conflict', '缺版本按冲突处理')
const targetExists = Object.assign(new Error('已存在'), {
  status: 409,
  payload: { success: false, errorCode: 'OTA_ASSIGNMENT_TARGET_EXISTS' },
})
assertEqual(classifyPosIpadOtaAssignmentError(targetExists), 'target-exists', '409 目标重复')
assertEqual(readPosIpadOtaAssignmentErrorCode(targetExists), 'OTA_ASSIGNMENT_TARGET_EXISTS', '读取响应体错误码')
// HTTP 200 的业务失败被 unwrapApiData 转成 「CODE: message」，没有可靠 payload 时从 message 前缀取码。
const businessFailure = Object.assign(new Error('OTA_ASSIGNMENT_EXPIRES_INVALID: 到期时间必须晚于当前时间'), {
  status: 200,
})
assertEqual(classifyPosIpadOtaAssignmentError(businessFailure), 'expires-invalid', '200 业务失败按 message 前缀分类')
assertEqual(
  classifyPosIpadOtaAssignmentError(new Error('请求失败 (500)')),
  'unknown',
  '无错误码一律 unknown',
)
assertEqual(classifyPosIpadOtaAssignmentError(null), 'unknown', '非对象异常 unknown')
// 即使状态码不是 409，只要响应体带版本错误码，也按冲突归类（以错误码为准）。
const non409 = Object.assign(new Error('x'), {
  status: 500,
  payload: { errorCode: 'APP_UPDATE_POLICY_VERSION_CONFLICT' },
})
assertEqual(
  classifyPosIpadOtaAssignmentError(non409),
  'version-conflict',
  '带版本错误码的响应按码归类为冲突',
)
assertEqual(shouldReloadAfterAssignmentError('version-conflict'), true, '冲突后必须重新拉取')
assertEqual(shouldReloadAfterAssignmentError('target-exists'), true, '目标重复后必须重新拉取')
assertEqual(shouldReloadAfterAssignmentError('not-found'), true, '已删除后必须重新拉取')
assertEqual(shouldReloadAfterAssignmentError('unknown'), false, '未知错误不自动刷新')

console.log('posIpadOtaAssignmentsLogic.test.ts: ok')
