import type { CurrentUser } from '../../../types/auth'
import { P } from '../../../types/permissions'
import type { AttendanceLocationRequestDto } from '../../../types/scheduleAttendance'
import { RequestError } from '../../../utils/request'
import { buildAccess } from '../../../utils/access'
import { unwrapAttendanceApiData } from '../../../services/scheduleAttendanceService'
import {
  LOCATION_REQUEST_ERROR_MESSAGE_KEYS,
  canRequestAttendanceLocation,
  describeLocationRequestError,
  extractLocationRequestError,
  hasLocationRequestCoordinates,
  hasOpenAttendanceSegment,
  prepareLocationRequestList,
  resolveLocationRequestEligibility,
  resolveLocationRequestStatusView,
  resolveSampleEventTypeLabelKey,
} from './attendanceLocationRequestLogic'

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

function createUser(permissions: string[], roleNames: string[] = []): CurrentUser {
  return {
    userGUID: 'manager-1',
    username: 'manager',
    email: 'manager@example.com',
    permissions,
    roleNames,
    storeNames: [],
  }
}

// ───────── 权限：只认独立权限码，管理员放行，查看 / 补卡权限不连带 ─────────
assertEqual(P.Attendance.PunchRequestLocation, 'Attendance.Punch.RequestLocation', '权限码必须与后端一致')
assertEqual(
  canRequestAttendanceLocation(buildAccess(createUser([P.Attendance.PunchRequestLocation], ['StoreManager']))),
  true,
  '持有 RequestLocation 的店长应显示请求入口',
)
assertEqual(canRequestAttendanceLocation(buildAccess(createUser([], ['Admin']))), true, '管理员应显示请求入口')
assertEqual(
  canRequestAttendanceLocation(buildAccess(createUser([
    P.Attendance.PunchViewManagedStore,
    P.Attendance.PunchAdjustManagedStore,
    P.Attendance.AdminView,
  ], ['StoreManager']))),
  false,
  '查看 / 补卡 / 考勤管理权限不得连带获得请求位置',
)
assertEqual(canRequestAttendanceLocation(buildAccess(createUser([]))), false, '没有任何权限不得显示')

// ───────── 前端预判：只拦必然被拒的三种情况，顺序固定 ─────────
const okInput = { targetUserGuid: 'user-1', currentUserGuid: 'manager-1', isManagedStore: true, hasOpenSegment: true }
assertDeepEqual(resolveLocationRequestEligibility(okInput), { canRequest: true }, '管辖分店内、有进行中班段的他人应放行')
assertDeepEqual(
  resolveLocationRequestEligibility({ ...okInput, targetUserGuid: 'MANAGER-1 ' }),
  { canRequest: false, reason: 'SELF' },
  '请求自己要忽略大小写与空白',
)
assertDeepEqual(
  resolveLocationRequestEligibility({ ...okInput, isManagedStore: false }),
  { canRequest: false, reason: 'OUT_OF_SCOPE' },
  '非管辖分店应置灰',
)
assertDeepEqual(
  resolveLocationRequestEligibility({ ...okInput, hasOpenSegment: false }),
  { canRequest: false, reason: 'NO_OPEN_SEGMENT' },
  '没有进行中班段应置灰',
)
assertDeepEqual(
  resolveLocationRequestEligibility({ targetUserGuid: 'manager-1', currentUserGuid: 'manager-1', isManagedStore: false, hasOpenSegment: false }),
  { canRequest: false, reason: 'SELF' },
  '多个原因同时成立时先报请求自己',
)
// 当前用户未知（未登录态）不能把空 GUID 当成「请求自己」
assertDeepEqual(
  resolveLocationRequestEligibility({ ...okInput, currentUserGuid: undefined }),
  { canRequest: true },
  '当前用户未知时不得误判为请求自己',
)

// 员工中途休息后又上班：所选班段已结束，但当天另有进行中的班段，仍应放行。
assertEqual(
  hasOpenAttendanceSegment([{ segmentStatus: 'Completed' }, { segmentStatus: 'Open' }], false),
  true,
  '当天任一班段进行中都算在班',
)
assertEqual(
  hasOpenAttendanceSegment([{ segmentStatus: 'Completed' }, { segmentStatus: 'MissingClockOut' }], true),
  false,
  '后端已标注班段状态时以后端为准，不再看所选班段的配对结果',
)
assertEqual(hasOpenAttendanceSegment([{}, {}], true), true, '旧后端没有班段状态时退回所选班段配对结果（进行中）')
assertEqual(hasOpenAttendanceSegment([], false), false, '没有任何打卡时退回所选班段配对结果（已结束）')

// ───────── 状态展示 ─────────
function statusKind(status: string, reason?: string) {
  return resolveLocationRequestStatusView(status, reason).kind
}
assertEqual(statusKind('Pending'), 'Pending', '待响应')
assertEqual(statusKind('Responded'), 'Responded', '已响应')
assertEqual(statusKind('Expired'), 'Expired', '已过期')
assertEqual(statusKind('Cancelled'), 'Cancelled', '已取消')
assertEqual(statusKind('Cancelled', 'ClockedOut'), 'ClockedOut', 'Cancelled + ClockedOut 显示已下班已作废')
assertEqual(statusKind('cancelled', ' clockedout '), 'ClockedOut', '状态与原因按忽略大小写 / 空白比较')
assertEqual(statusKind('Cancelled', 'SomethingElse'), 'Cancelled', '未知取消原因仍是已取消')
assertEqual(statusKind('Pending', 'ClockedOut'), 'Pending', '只有 Cancelled 才看 statusReason')
assertEqual(statusKind('Whatever'), 'Unknown', '没见过的状态走兜底')
assertEqual(statusKind(''), 'Unknown', '空状态走兜底')
assertEqual(resolveLocationRequestStatusView('Pending').color, 'processing', '待响应用醒目色')
assertEqual(resolveLocationRequestStatusView('Responded').color, 'success', '已响应用成功色')
assertEqual(
  resolveLocationRequestStatusView('Cancelled', 'ClockedOut').messageKey,
  'posAdmin.scheduleAttendance.locationRequest.status.ClockedOut',
  '已下班作废使用单独文案键',
)

assertEqual(hasLocationRequestCoordinates({ locationLatitude: -33.86, locationLongitude: 151.2 }), true, '有效坐标')
assertEqual(hasLocationRequestCoordinates({ locationLatitude: 0, locationLongitude: 0 }), true, '0,0 是合法坐标')
assertEqual(hasLocationRequestCoordinates({ locationLatitude: undefined, locationLongitude: 151.2 }), false, '缺纬度')
assertEqual(hasLocationRequestCoordinates({ locationLatitude: 91, locationLongitude: 151.2 }), false, '纬度越界')
assertEqual(hasLocationRequestCoordinates({ locationLatitude: -33, locationLongitude: Number.NaN }), false, 'NaN 经度')

// ───────── 样本事件类型标签：只有 ServerRequest 打标签，其他保持原样 ─────────
assertEqual(
  resolveSampleEventTypeLabelKey('ServerRequest'),
  'posAdmin.scheduleAttendance.locationRequest.eventType.ServerRequest',
  'ServerRequest 样本应显示服务端请求标签',
)
assertEqual(resolveSampleEventTypeLabelKey(' serverrequest '), 'posAdmin.scheduleAttendance.locationRequest.eventType.ServerRequest', '忽略大小写与空白')
assertEqual(resolveSampleEventTypeLabelKey('ShiftSample'), null, 'ShiftSample 不加标签')
assertEqual(resolveSampleEventTypeLabelKey('ShiftHeartbeat'), null, 'ShiftHeartbeat 不加标签')
assertEqual(resolveSampleEventTypeLabelKey(undefined), null, '缺失事件类型不加标签')

// ───────── 请求列表整理：只留该员工 / 分店，按创建时间倒序，限量 ─────────
function requestItem(requestGuid: string, createdAtUtc: string, overrides: Partial<AttendanceLocationRequestDto> = {}): AttendanceLocationRequestDto {
  return {
    requestGuid,
    storeCode: 'S001',
    targetUserGuid: 'user-1',
    requestedByUserGuid: 'manager-1',
    status: 'Pending',
    createdAtUtc,
    expiresAtUtc: createdAtUtc,
    ...overrides,
  }
}

const unsorted = [
  requestItem('b-old', '2026-11-19T01:00:00'),
  requestItem('a-new', '2026-11-19T03:00:00'),
  requestItem('c-mid', '2026-11-19T02:00:00Z'),
  requestItem('other-user', '2026-11-19T04:00:00', { targetUserGuid: 'user-2' }),
  requestItem('other-store', '2026-11-19T05:00:00', { storeCode: 'S002' }),
  requestItem('bad-time', 'not-a-date'),
  requestItem('same-time-z', '2026-11-19T03:00:00Z'),
]
assertDeepEqual(
  prepareLocationRequestList(unsorted, { storeCode: 's001', targetUserGuid: 'USER-1' }).map((item) => item.requestGuid),
  ['a-new', 'same-time-z', 'c-mid', 'b-old', 'bad-time'],
  '应只留该员工该分店的记录，按创建时间倒序（无后缀 UTC 与带 Z 等价，同刻按 GUID 稳定排序，无法解析的排最后）',
)
assertDeepEqual(
  prepareLocationRequestList(unsorted, { storeCode: 'S001', targetUserGuid: 'user-1', limit: 2 }).map((item) => item.requestGuid),
  ['a-new', 'same-time-z'],
  'limit 应截取最新的 N 条',
)
assertDeepEqual(
  prepareLocationRequestList(unsorted, {}).length,
  unsorted.length,
  '不传过滤条件时不丢记录',
)
const original = unsorted.map((item) => item.requestGuid)
prepareLocationRequestList(unsorted, { limit: 1 })
assertDeepEqual(unsorted.map((item) => item.requestGuid), original, '整理不得原地改动入参')

// ───────── 错误码 → 文案 ─────────
// 用「键 + 参数」代替真实翻译，便于断言选中了哪条文案
const fakeTranslate = (key: string, options?: Record<string, unknown>) => (
  options ? `${key}|${JSON.stringify(options)}` : key
)

assertDeepEqual(
  Object.keys(LOCATION_REQUEST_ERROR_MESSAGE_KEYS).sort(),
  [
    'ACTIVE_SHIFT_REQUIRED',
    'CANNOT_REQUEST_SELF',
    'FORBIDDEN_STORE',
    'LOCATION_REQUEST_PENDING',
    'LOCATION_REQUEST_RATE_LIMITED',
    'STORE_REQUIRED',
    'TARGET_USER_REQUIRED',
  ],
  '后端约定的 7 个创建错误码都必须有友好文案',
)

// 业务失败：HTTP 200 + success=false，经 unwrapAttendanceApiData 抛出的真实错误形态
function businessFailure(errorCode: string, message: string): unknown {
  try {
    unwrapAttendanceApiData({ success: false, errorCode, message })
  } catch (error) {
    return error
  }
  throw new Error('应当抛出业务错误')
}

const activeShiftError = businessFailure('ACTIVE_SHIFT_REQUIRED', '员工当前没有上班')
assertDeepEqual(
  extractLocationRequestError(activeShiftError),
  { code: 'ACTIVE_SHIFT_REQUIRED', message: '员工当前没有上班' },
  '应从 payload.errorCode 取错误码、剥掉 message 里的 CODE: 前缀',
)
assertEqual(
  describeLocationRequestError(activeShiftError, fakeTranslate),
  'posAdmin.scheduleAttendance.locationRequest.errors.ACTIVE_SHIFT_REQUIRED',
  '已知错误码用友好文案，不显示服务端原文',
)
for (const code of Object.keys(LOCATION_REQUEST_ERROR_MESSAGE_KEYS)) {
  if (code === 'LOCATION_REQUEST_RATE_LIMITED') continue
  assertEqual(
    describeLocationRequestError(businessFailure(code, 'server text'), fakeTranslate),
    LOCATION_REQUEST_ERROR_MESSAGE_KEYS[code],
    `${code} 应映射到各自的文案键`,
  )
}

// 限频：剩余时间只在服务端 message 里，友好文案后要接上
assertEqual(
  describeLocationRequestError(businessFailure('LOCATION_REQUEST_RATE_LIMITED', '请 7 分钟后再试'), fakeTranslate),
  'posAdmin.scheduleAttendance.locationRequest.errors.LOCATION_REQUEST_RATE_LIMITED '
    + 'posAdmin.scheduleAttendance.locationRequest.errors.serverDetail|{"detail":"请 7 分钟后再试"}',
  '限频要把服务端 message（含剩余时间）接在友好文案后面',
)
assertEqual(
  describeLocationRequestError(businessFailure('LOCATION_REQUEST_RATE_LIMITED', ''), fakeTranslate),
  'posAdmin.scheduleAttendance.locationRequest.errors.LOCATION_REQUEST_RATE_LIMITED',
  '限频没有服务端 message 时只显示友好文案',
)

// 未知错误码回退服务端 message；连 message 都没有才用通用失败文案
assertEqual(
  describeLocationRequestError(businessFailure('SOMETHING_NEW', '服务端新增的错误'), fakeTranslate),
  '服务端新增的错误',
  '未知错误码回退服务端 message',
)
assertEqual(
  describeLocationRequestError(new Error(''), fakeTranslate),
  'posAdmin.scheduleAttendance.locationRequest.createFailed',
  '没有任何信息时用通用失败文案',
)
assertEqual(
  describeLocationRequestError('boom', fakeTranslate),
  'posAdmin.scheduleAttendance.locationRequest.createFailed',
  '非 Error 对象也不能抛异常',
)
assertEqual(
  describeLocationRequestError(
    new RequestError('原型链键', 200, { success: false, errorCode: 'constructor', message: '原型链键' }),
    fakeTranslate,
  ),
  '原型链键',
  'Object 原型上的属性名不能被当成已知错误码',
)

// 非 2xx（如无权限 403）：RequestError.message 已是 payload.message，错误码在 payload.errorCode
const forbidden = new RequestError('当前分店不在你的管辖范围', 403, { success: false, errorCode: 'FORBIDDEN_STORE', message: '当前分店不在你的管辖范围' })
assertDeepEqual(
  extractLocationRequestError(forbidden),
  { code: 'FORBIDDEN_STORE', message: '当前分店不在你的管辖范围' },
  '非 2xx 错误也能取到错误码',
)
// 只有 `CODE: message` 前缀、payload 里没有错误码的老形态
assertDeepEqual(
  extractLocationRequestError(new RequestError('CANNOT_REQUEST_SELF: 不能请求自己', 200, undefined)),
  { code: 'CANNOT_REQUEST_SELF', message: '不能请求自己' },
  '没有 payload 时从 message 前缀取错误码',
)

console.log('attendanceLocationRequestLogic.test.ts: ok')
