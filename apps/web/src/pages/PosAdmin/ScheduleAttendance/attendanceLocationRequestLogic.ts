// 「要求员工上报位置」的纯逻辑：权限判断、前端预判、状态展示、错误码文案、请求列表整理。
// 文案键必须以带引号的字面量出现：页面消息契约测试按这个形式静态扫描，才能发现缺失键。
import type {
  AttendanceLocationRequestDto,
  AttendancePunchDto,
} from '../../../types/scheduleAttendance'
import { P } from '../../../types/permissions'
import { normalizeAttendanceUtcText } from './attendanceLocationTrajectoryLogic'

type Translate = (key: string, options?: Record<string, unknown>) => string

// ───────────────────────── 权限 ─────────────────────────

export interface LocationRequestAccess {
  isAdmin: boolean
  hasPermission: (permission: string) => boolean
}

/**
 * 是否显示「请求位置」入口：管理员放行，其余只认独立权限码 Attendance.Punch.RequestLocation
 * （默认不授予任何角色，也不随查看打卡 / 排班权限连带）。管辖分店范围由服务端最终校验。
 */
export function canRequestAttendanceLocation(access: LocationRequestAccess): boolean {
  return access.isAdmin || access.hasPermission(P.Attendance.PunchRequestLocation)
}

// ───────────────────────── 前端预判（仅用于置灰按钮，最终以服务端错误码为准） ─────────────────────────

export type LocationRequestBlockReason = 'SELF' | 'OUT_OF_SCOPE' | 'NO_OPEN_SEGMENT'

export type LocationRequestEligibility =
  | { canRequest: true }
  | { canRequest: false; reason: LocationRequestBlockReason }

export const LOCATION_REQUEST_BLOCK_MESSAGE_KEYS: Readonly<Record<LocationRequestBlockReason, string>> = {
  SELF: 'posAdmin.scheduleAttendance.locationRequest.blocked.self',
  OUT_OF_SCOPE: 'posAdmin.scheduleAttendance.locationRequest.blocked.outOfScope',
  NO_OPEN_SEGMENT: 'posAdmin.scheduleAttendance.locationRequest.blocked.noOpenSegment',
}

function sameGuid(left?: string | null, right?: string | null): boolean {
  const leftKey = left?.trim().toLocaleLowerCase('en-US')
  const rightKey = right?.trim().toLocaleLowerCase('en-US')
  return Boolean(leftKey) && leftKey === rightKey
}

/**
 * 员工当前是否有进行中的班段：优先用后端给每条打卡标注的班段状态（Open 即上班未下班）；
 * 旧后端没有标注时退回按所选班段的上下班配对结果。这只是预判：员工是否真的在班由服务端（ACTIVE_SHIFT_REQUIRED）裁定。
 */
export function hasOpenAttendanceSegment(
  punches: ReadonlyArray<Pick<AttendancePunchDto, 'segmentStatus'>>,
  selectedSegmentIsOpen: boolean,
): boolean {
  const reported = punches.filter((punch) => Boolean(punch.segmentStatus))
  if (reported.length === 0) return selectedSegmentIsOpen
  return reported.some((punch) => punch.segmentStatus === 'Open')
}

/**
 * 只拦「几乎一定会被服务端拒绝」的三种情况：请求自己、不在管辖分店、员工没有进行中的班段。
 * 其余（已有待响应请求、10 分钟限频、员工实际不在班等）一律放行，点击后由服务端错误码给出准确原因。
 */
export function resolveLocationRequestEligibility(input: {
  targetUserGuid: string
  currentUserGuid?: string
  isManagedStore: boolean
  hasOpenSegment: boolean
}): LocationRequestEligibility {
  if (sameGuid(input.targetUserGuid, input.currentUserGuid)) return { canRequest: false, reason: 'SELF' }
  if (!input.isManagedStore) return { canRequest: false, reason: 'OUT_OF_SCOPE' }
  if (!input.hasOpenSegment) return { canRequest: false, reason: 'NO_OPEN_SEGMENT' }
  return { canRequest: true }
}

// ───────────────────────── 请求状态展示 ─────────────────────────

export type LocationRequestStatusKind =
  | 'Pending'
  | 'Responded'
  | 'Expired'
  | 'Cancelled'
  | 'ClockedOut'
  | 'Unknown'

export interface LocationRequestStatusView {
  kind: LocationRequestStatusKind
  messageKey: string
  /** antd Tag 颜色：只有「待响应」用醒目色，已响应用成功色，其余（过期 / 作废）一律灰。 */
  color: 'processing' | 'success' | 'default'
}

const STATUS_VIEWS: Readonly<Record<LocationRequestStatusKind, LocationRequestStatusView>> = {
  Pending: { kind: 'Pending', messageKey: 'posAdmin.scheduleAttendance.locationRequest.status.Pending', color: 'processing' },
  Responded: { kind: 'Responded', messageKey: 'posAdmin.scheduleAttendance.locationRequest.status.Responded', color: 'success' },
  Expired: { kind: 'Expired', messageKey: 'posAdmin.scheduleAttendance.locationRequest.status.Expired', color: 'default' },
  Cancelled: { kind: 'Cancelled', messageKey: 'posAdmin.scheduleAttendance.locationRequest.status.Cancelled', color: 'default' },
  ClockedOut: { kind: 'ClockedOut', messageKey: 'posAdmin.scheduleAttendance.locationRequest.status.ClockedOut', color: 'default' },
  Unknown: { kind: 'Unknown', messageKey: 'posAdmin.scheduleAttendance.locationRequest.status.Unknown', color: 'default' },
}

/**
 * 服务端状态 → 展示：Cancelled 且原因是 ClockedOut（员工已下班）单独显示「已下班，已作废」；
 * Expired 以服务端推导为准，前端不再按本机时钟二次推导（避免电脑时钟偏差造成与服务端不一致）。
 * 没见过的状态落到 Unknown，界面显示原始状态文本而不是空白。
 */
export function resolveLocationRequestStatusView(status?: string, statusReason?: string): LocationRequestStatusView {
  switch (status?.trim().toLowerCase()) {
    case 'pending':
      return STATUS_VIEWS.Pending
    case 'responded':
      return STATUS_VIEWS.Responded
    case 'expired':
      return STATUS_VIEWS.Expired
    case 'cancelled':
      return statusReason?.trim().toLowerCase() === 'clockedout' ? STATUS_VIEWS.ClockedOut : STATUS_VIEWS.Cancelled
    default:
      return STATUS_VIEWS.Unknown
  }
}

/** 已响应请求是否带有可在地图上打开的有效坐标。 */
export function hasLocationRequestCoordinates(request: Pick<AttendanceLocationRequestDto, 'locationLatitude' | 'locationLongitude'>): boolean {
  const { locationLatitude: latitude, locationLongitude: longitude } = request
  return typeof latitude === 'number'
    && Number.isFinite(latitude)
    && latitude >= -90
    && latitude <= 90
    && typeof longitude === 'number'
    && Number.isFinite(longitude)
    && longitude >= -180
    && longitude <= 180
}

// ───────────────────────── 班中定位样本的事件类型标签 ─────────────────────────

/**
 * 样本事件类型 → 标签文案键。只有「服务端请求」（员工响应管理员请求时写入，EventType = ServerRequest）
 * 需要在时间轴上特别标出；其余事件类型（ShiftSample / ShiftHeartbeat 等）保持原样不加标签。
 */
export function resolveSampleEventTypeLabelKey(eventType?: string): string | null {
  return eventType?.trim().toLowerCase() === 'serverrequest'
    ? 'posAdmin.scheduleAttendance.locationRequest.eventType.ServerRequest'
    : null
}

// ───────────────────────── 请求列表整理 ─────────────────────────

function createdTimestamp(request: Pick<AttendanceLocationRequestDto, 'createdAtUtc'>): number {
  const normalized = normalizeAttendanceUtcText(request.createdAtUtc)
  const timestamp = normalized ? Date.parse(normalized) : Number.NaN
  return Number.isFinite(timestamp) ? timestamp : Number.NEGATIVE_INFINITY
}

/**
 * 整理某员工的请求列表：只留该员工（及所选分店）的记录，按创建时间倒序（时间无法解析的排最后，
 * 同刻按请求 GUID 稳定排序），最多保留 limit 条。服务端本就按管辖范围过滤并倒序，这里是防御式二次整理。
 */
export function prepareLocationRequestList(
  items: ReadonlyArray<AttendanceLocationRequestDto>,
  options: { storeCode?: string; targetUserGuid?: string; limit?: number } = {},
): AttendanceLocationRequestDto[] {
  const storeKey = options.storeCode?.trim().toLocaleLowerCase('en-US')
  const filtered = items.filter((item) => (
    (!options.targetUserGuid || sameGuid(item.targetUserGuid, options.targetUserGuid))
    && (!storeKey || item.storeCode.trim().toLocaleLowerCase('en-US') === storeKey)
  ))
  const sorted = [...filtered].sort((left, right) => {
    const leftTime = createdTimestamp(left)
    const rightTime = createdTimestamp(right)
    if (leftTime !== rightTime) return rightTime > leftTime ? 1 : -1
    return left.requestGuid.localeCompare(right.requestGuid, 'en')
  })
  const limit = options.limit
  return typeof limit === 'number' && Number.isInteger(limit) && limit >= 0 ? sorted.slice(0, limit) : sorted
}

// ───────────────────────── 创建请求失败的文案 ─────────────────────────

// 服务端业务错误码 → 友好文案键；未知码回退服务端 message。
export const LOCATION_REQUEST_ERROR_MESSAGE_KEYS: Readonly<Record<string, string>> = {
  STORE_REQUIRED: 'posAdmin.scheduleAttendance.locationRequest.errors.STORE_REQUIRED',
  TARGET_USER_REQUIRED: 'posAdmin.scheduleAttendance.locationRequest.errors.TARGET_USER_REQUIRED',
  FORBIDDEN_STORE: 'posAdmin.scheduleAttendance.locationRequest.errors.FORBIDDEN_STORE',
  CANNOT_REQUEST_SELF: 'posAdmin.scheduleAttendance.locationRequest.errors.CANNOT_REQUEST_SELF',
  ACTIVE_SHIFT_REQUIRED: 'posAdmin.scheduleAttendance.locationRequest.errors.ACTIVE_SHIFT_REQUIRED',
  LOCATION_REQUEST_PENDING: 'posAdmin.scheduleAttendance.locationRequest.errors.LOCATION_REQUEST_PENDING',
  LOCATION_REQUEST_RATE_LIMITED: 'posAdmin.scheduleAttendance.locationRequest.errors.LOCATION_REQUEST_RATE_LIMITED',
}

// 限频错误的剩余时间只在服务端 message 里，友好文案后面要把它接上。
const ERROR_CODES_WITH_SERVER_DETAIL: ReadonlySet<string> = new Set(['LOCATION_REQUEST_RATE_LIMITED'])

const ERROR_CODE_PREFIX = /^([A-Z][A-Z0-9_]*):\s*/

/**
 * 取服务端业务错误：
 * - HTTP 200 + success=false：unwrapAttendanceApiData 把 message 拼成 `CODE: message`，错误码同时在 payload.errorCode / code；
 * - 非 2xx：RequestError.message 已是 payload.message，错误码在 payload.code / errorCode。
 */
export function extractLocationRequestError(error: unknown): { code: string | null; message: string } {
  const payload = error && typeof error === 'object' && 'payload' in error
    ? (error as { payload?: unknown }).payload
    : undefined
  const record = payload && typeof payload === 'object' ? payload as Record<string, unknown> : undefined
  const payloadCode = record
    ? [record.errorCode, record.code, record.ErrorCode, record.Code]
      .find((value): value is string => typeof value === 'string' && value !== '')
    : undefined
  const rawMessage = error instanceof Error ? error.message.trim() : ''
  const prefixed = ERROR_CODE_PREFIX.exec(rawMessage)
  const message = (prefixed ? rawMessage.slice(prefixed[0].length) : rawMessage).trim()
  return { code: payloadCode ?? prefixed?.[1] ?? null, message }
}

/** 创建请求失败 → 要展示的文案：已知错误码用中英文友好文案；未知码回退服务端 message；都没有则用通用失败文案。 */
export function describeLocationRequestError(error: unknown, translate: Translate): string {
  const { code, message } = extractLocationRequestError(error)
  const knownKey = code && Object.prototype.hasOwnProperty.call(LOCATION_REQUEST_ERROR_MESSAGE_KEYS, code)
    ? LOCATION_REQUEST_ERROR_MESSAGE_KEYS[code]
    : null
  if (!knownKey) return message || translate('posAdmin.scheduleAttendance.locationRequest.createFailed')

  const friendly = translate(knownKey)
  if (code && ERROR_CODES_WITH_SERVER_DETAIL.has(code) && message && message !== friendly) {
    return `${friendly} ${translate('posAdmin.scheduleAttendance.locationRequest.errors.serverDetail', { detail: message })}`
  }
  return friendly
}
