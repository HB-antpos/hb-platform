import type { ApiResponse } from '../types/api'
import type {
  AppDeviceOnlineState,
  AppDeviceStatus,
  AppDeviceStatusPagedResult,
  AppDeviceStatusSummary,
  AppVersionDistribution,
  DeviceRegistrationDetail,
  DeviceRegistrationItem,
  DeviceRegistrationPagedResult,
  EmergencyLoginGrantCreateResponse,
  EmergencyLoginGrantSummary,
  StoreOption,
  UpdateDeviceRegistrationApiPayload,
  UpdateDeviceRegistrationPayload,
} from '../types/deviceRegistration'
import request, { unwrapApiData } from '../utils/request'

const DEVICE_API_BASE = '/api'
const REACT_API_BASE = '/api/react/v1/device-registration'
const EMERGENCY_LOGIN_GRANT_API_BASE = '/api/react/v1/emergency-login-grants'
export const DEVICE_RUNTIME_ONLINE_STALE_MS = 45_000
const APP_DEVICE_API_BASE = '/api/mobile/app-device-status'

function getString(raw: Record<string, unknown>, ...keys: string[]) {
  for (const key of keys) {
    const value = raw[key]
    if (typeof value === 'string') {
      return value
    }
  }
  return undefined
}

function getNullableString(raw: Record<string, unknown>, ...keys: string[]) {
  for (const key of keys) {
    const value = raw[key]
    if (typeof value === 'string') {
      return value
    }
    if (value === null) {
      return null
    }
  }
  return null
}

function getBoolean(raw: Record<string, unknown>, ...keys: string[]) {
  for (const key of keys) {
    const value = raw[key]
    if (typeof value === 'boolean') {
      return value
    }
  }
  return false
}

export function isDeviceRuntimeOnline(
  item: Pick<DeviceRegistrationItem, 'isOnline' | 'lastHeartbeatAt'>,
  now = Date.now()
) {
  if (!item.isOnline || !item.lastHeartbeatAt) {
    return false
  }

  const heartbeatTime = Date.parse(item.lastHeartbeatAt)
  return !Number.isNaN(heartbeatTime) && now - heartbeatTime <= DEVICE_RUNTIME_ONLINE_STALE_MS
}

function pick(raw: Record<string, unknown>, ...keys: string[]) {
  for (const key of keys) {
    if (raw[key] !== undefined && raw[key] !== null) {
      return raw[key]
    }
  }
  return undefined
}

function asRecord(value: unknown): Record<string, unknown> | null {
  return value && typeof value === 'object' && !Array.isArray(value)
    ? (value as Record<string, unknown>)
    : null
}

function asString(value: unknown) {
  if (typeof value === 'string') {
    const trimmed = value.trim()
    return trimmed || undefined
  }
  if (typeof value === 'number' && Number.isFinite(value)) {
    return String(value)
  }
  return undefined
}

function asNumber(value: unknown, fallback: number) {
  const numericValue = typeof value === 'string' && value.trim() ? Number(value) : value
  return typeof numericValue === 'number' && Number.isFinite(numericValue) ? numericValue : fallback
}

function asOptionalNumber(value: unknown) {
  const numericValue = typeof value === 'string' && value.trim() ? Number(value) : value
  return typeof numericValue === 'number' && Number.isFinite(numericValue) ? numericValue : undefined
}

function asBoolean(value: unknown, fallback = false) {
  if (typeof value === 'boolean') {
    return value
  }
  if (typeof value === 'string') {
    const normalized = value.trim().toLowerCase()
    if (['true', '1', 'yes'].includes(normalized)) {
      return true
    }
    if (['false', '0', 'no'].includes(normalized)) {
      return false
    }
  }
  return fallback
}

export function normalizeRegisteredDeviceSystem(value: unknown) {
  if (typeof value !== 'string') {
    return 'Windows'
  }
  const normalized = value.trim()
  return normalized || 'Windows'
}

function normalizeItem(raw: Record<string, unknown>): DeviceRegistrationItem {
  return {
    id: Number(raw.id ?? raw.Id ?? 0),
    hardwareId: String(raw.hardwareId ?? raw.HardwareId ?? ''),
    systemDeviceNumber: String(raw.systemDeviceNumber ?? raw.SystemDeviceNumber ?? ''),
    storeCode:
      typeof raw.storeCode === 'string'
        ? raw.storeCode
        : typeof raw.StoreCode === 'string'
          ? raw.StoreCode
          : null,
    storeName: getNullableString(raw, 'storeName', 'StoreName'),
    deviceType: String(raw.deviceType ?? raw.DeviceType ?? ''),
    deviceSystem: normalizeRegisteredDeviceSystem(raw.deviceSystem ?? raw.DeviceSystem),
    status: Number(raw.status ?? raw.Status ?? -1),
    statusDescription: String(raw.statusDescription ?? raw.StatusDescription ?? ''),
    allowTransactions: asBoolean(
      pick(raw, 'allowTransactions', 'AllowTransactions', '是否允许交易'),
      true
    ),
    remark: getNullableString(raw, 'remark', 'remarks', 'Remark', 'Remarks'),
    createdAt:
      typeof raw.createdAt === 'string'
        ? raw.createdAt
        : typeof raw.CreatedAt === 'string'
          ? raw.CreatedAt
          : undefined,
    lastModified:
      typeof raw.lastModified === 'string'
        ? raw.lastModified
        : typeof raw.LastModified === 'string'
          ? raw.LastModified
          : null,
    createdBy:
      typeof raw.createdBy === 'string'
        ? raw.createdBy
        : typeof raw.CreatedBy === 'string'
          ? raw.CreatedBy
          : null,
    lastModifiedBy:
      typeof raw.lastModifiedBy === 'string'
        ? raw.lastModifiedBy
        : typeof raw.LastModifiedBy === 'string'
          ? raw.LastModifiedBy
          : null,
    isOnline: getBoolean(raw, 'isOnline', 'IsOnline', '是否在线'),
    lastHeartbeatAt: getNullableString(raw, 'lastHeartbeatAt', 'LastHeartbeatAt', '最后心跳时间'),
    currentCashierId: getNullableString(raw, 'currentCashierId', 'CurrentCashierId', '当前收银员ID'),
    currentCashierName: getNullableString(
      raw,
      'currentCashierName',
      'CurrentCashierName',
      '当前收银员姓名'
    ),
    cashierLoginAt: getNullableString(raw, 'cashierLoginAt', 'CashierLoginAt', '收银员登录时间'),
  }
}

function normalizeAppDeviceStatus(raw: unknown): AppDeviceStatus | null {
  const record = asRecord(raw)
  if (!record) {
    return null
  }

  const hardwareId = asString(pick(record, 'hardwareId', 'HardwareId'))
  const id = asString(pick(record, 'id', 'Id')) ?? hardwareId
  if (!id || !hardwareId) {
    return null
  }

  return {
    id,
    hardwareId,
    systemDeviceNumber: asString(pick(record, 'systemDeviceNumber', 'SystemDeviceNumber')),
    deviceSystem: asString(pick(record, 'deviceSystem', 'DeviceSystem')),
    platform: asString(pick(record, 'platform', 'Platform')),
    storeCode: asString(pick(record, 'storeCode', 'StoreCode')),
    appVersion: asString(pick(record, 'appVersion', 'AppVersion')),
    appBuildVersion: asString(pick(record, 'appBuildVersion', 'AppBuildVersion')),
    runtimeVersion: asString(pick(record, 'runtimeVersion', 'RuntimeVersion')),
    channel: asString(pick(record, 'channel', 'Channel')),
    updateId: asString(pick(record, 'updateId', 'UpdateId')),
    updateSource: asString(pick(record, 'updateSource', 'UpdateSource')),
    lastSeenAtUtc: asString(pick(record, 'lastSeenAtUtc', 'LastSeenAtUtc', 'lastSeenAt', 'LastSeenAt')),
    isOnline: asBoolean(pick(record, 'isOnline', 'IsOnline')),
    lastAuthMode: asString(pick(record, 'lastAuthMode', 'LastAuthMode')),
    lastSeenUserGuid: asString(pick(record, 'lastSeenUserGuid', 'LastSeenUserGuid')),
    lastSeenUsername: asString(pick(record, 'lastSeenUsername', 'LastSeenUsername')),
    lastSeenUserFullName: asString(pick(record, 'lastSeenUserFullName', 'LastSeenUserFullName')),
    registeredDeviceId: asOptionalNumber(pick(record, 'registeredDeviceId', 'RegisteredDeviceId')),
  }
}

export function normalizeAppDeviceStatusListResponse(payload: unknown): AppDeviceStatusPagedResult {
  const data = unwrapApiData(payload as ApiResponse<unknown> | unknown)
  const record = asRecord(data) ?? {}
  const itemsPayload = pick(record, 'items', 'Items', 'devices', 'Devices', 'data', 'Data')
  const total = asNumber(pick(record, 'total', 'Total', 'totalCount', 'TotalCount'), 0)
  const page = asNumber(pick(record, 'page', 'Page', 'pageIndex', 'PageIndex'), 1)
  const pageSize = asNumber(pick(record, 'pageSize', 'PageSize'), 20)

  return {
    devices: Array.isArray(itemsPayload)
      ? itemsPayload.map(normalizeAppDeviceStatus).filter((item): item is AppDeviceStatus => Boolean(item))
      : [],
    total,
    page,
    pageSize,
    totalPages: asNumber(
      pick(record, 'totalPages', 'TotalPages'),
      pageSize > 0 ? Math.ceil(total / pageSize) : 0
    ),
  }
}

export function normalizeAppDeviceStatusSummary(payload: unknown): AppDeviceStatusSummary {
  const data = unwrapApiData(payload as ApiResponse<unknown> | unknown)
  const record = asRecord(data) ?? {}
  return {
    total: asNumber(pick(record, 'total', 'Total'), 0),
    online: asNumber(pick(record, 'online', 'Online'), 0),
    offline: asNumber(pick(record, 'offline', 'Offline'), 0),
    android: asNumber(pick(record, 'android', 'Android'), 0),
    ios: asNumber(pick(record, 'ios', 'Ios', 'iOS', 'IOS'), 0),
    unknownSystem: asNumber(pick(record, 'unknownSystem', 'UnknownSystem'), 0),
  }
}

export function normalizeAppVersionDistribution(payload: unknown): AppVersionDistribution {
  const data = unwrapApiData(payload as ApiResponse<unknown> | unknown)
  const record = asRecord(data) ?? {}
  const itemsPayload = pick(record, 'items', 'Items')

  return {
    total: asNumber(pick(record, 'total', 'Total'), 0),
    items: Array.isArray(itemsPayload)
      ? itemsPayload.flatMap((raw) => {
          const item = asRecord(raw)
          if (!item) {
            return []
          }
          return [
            {
              deviceSystem: asString(pick(item, 'deviceSystem', 'DeviceSystem')),
              appVersion: asString(pick(item, 'appVersion', 'AppVersion')),
              appBuildVersion: asString(pick(item, 'appBuildVersion', 'AppBuildVersion')),
              total: asNumber(pick(item, 'total', 'Total'), 0),
              online: asNumber(pick(item, 'online', 'Online'), 0),
              ota: asNumber(pick(item, 'ota', 'Ota', 'OTA'), 0),
              embedded: asNumber(pick(item, 'embedded', 'Embedded'), 0),
            },
          ]
        })
      : [],
  }
}

function buildAppDeviceStatusParams(params?: {
  page?: number
  pageSize?: number
  storeCode?: string
  deviceSystem?: string
  onlineState?: AppDeviceOnlineState
  keyword?: string
  appVersion?: string
  appBuildVersion?: string
}) {
  return {
    page: params?.page,
    pageSize: params?.pageSize,
    storeCode: params?.storeCode,
    deviceSystem: params?.deviceSystem,
    onlineState: params?.onlineState && params.onlineState !== 'all' ? params.onlineState : undefined,
    keyword: params?.keyword?.trim() || undefined,
    appVersion: params?.appVersion || undefined,
    appBuildVersion: params?.appBuildVersion || undefined,
  }
}

export function normalizeDeviceRegistrationDetail(
  raw: Record<string, unknown>
): DeviceRegistrationDetail {
  return {
    id: Number(raw.id ?? raw.ID ?? raw.Id ?? 0),
    hardwareId: String(raw.hardwareId ?? raw.设备硬件识别码 ?? raw.HardwareId ?? ''),
    systemDeviceNumber: String(
      raw.systemDeviceNumber ?? raw.系统设备编号 ?? raw.SystemDeviceNumber ?? ''
    ),
    storeCode: getNullableString(raw, 'storeCode', '分店代码', 'StoreCode'),
    storeName: getNullableString(raw, 'storeName', '分店名称', 'StoreName'),
    deviceType: String(raw.deviceType ?? raw.设备类型 ?? raw.DeviceType ?? ''),
    deviceSystem: normalizeRegisteredDeviceSystem(
      raw.deviceSystem ?? raw.设备系统 ?? raw.DeviceSystem
    ),
    status: Number(raw.status ?? raw.设备状态 ?? raw.Status ?? -1),
    statusDescription: String(
      raw.statusDescription ?? raw.设备状态描述 ?? raw.StatusDescription ?? ''
    ),
    allowTransactions: asBoolean(
      pick(raw, 'allowTransactions', 'AllowTransactions', '是否允许交易'),
      true
    ),
    remark: getNullableString(raw, 'remark', 'remarks', '备注', 'Remark', 'Remarks'),
    createdAt: getString(raw, 'createdAt', '创建时间', 'CreatedAt'),
    lastModified: getNullableString(raw, 'lastModified', '最后修改时间', 'LastModified'),
    createdBy: getNullableString(raw, 'createdBy', '创建人', 'CreatedBy'),
    lastModifiedBy: getNullableString(raw, 'lastModifiedBy', '最后修改人', 'LastModifiedBy'),
    isOnline: getBoolean(raw, 'isOnline', '是否在线', 'IsOnline'),
    lastHeartbeatAt: getNullableString(raw, 'lastHeartbeatAt', '最后心跳时间', 'LastHeartbeatAt'),
    currentCashierId: getNullableString(raw, 'currentCashierId', '当前收银员ID', 'CurrentCashierId'),
    currentCashierName: getNullableString(
      raw,
      'currentCashierName',
      '当前收银员姓名',
      'CurrentCashierName'
    ),
    cashierLoginAt: getNullableString(raw, 'cashierLoginAt', '收银员登录时间', 'CashierLoginAt'),
  }
}

export function buildUpdateDeviceRegistrationPayload(
  payload: UpdateDeviceRegistrationPayload
): UpdateDeviceRegistrationApiPayload {
  return {
    设备类型: payload.deviceType,
    设备系统: payload.deviceSystem,
    是否允许交易: payload.allowTransactions,
    备注: payload.remark ?? '',
  }
}

export async function getDeviceRegistrations(params?: {
  page?: number
  pageSize?: number
  storeCode?: string
  deviceType?: string
  deviceSystem?: string
}): Promise<DeviceRegistrationPagedResult> {
  // 列表分页和状态动作仍由旧设备注册控制器承载；详情编辑走 React 控制器。
  const response = await request.get<
    ApiResponse<{
      devices?: Record<string, unknown>[]
      pagination?: {
        page?: number
        pageSize?: number
        total?: number
        totalPages?: number
      }
    }>
  >(`${DEVICE_API_BASE}/paged`, {
    params: {
      page: params?.page ?? 1,
      pageSize: params?.pageSize ?? 50,
      storeCode: params?.storeCode,
      deviceType: params?.deviceType,
      deviceSystem: params?.deviceSystem,
    },
  })

  const data = response.data ?? {}
  const pagination = data.pagination ?? {}

  return {
    devices: Array.isArray(data.devices) ? data.devices.map(normalizeItem) : [],
    total: Number(pagination.total ?? 0),
    page: Number(pagination.page ?? params?.page ?? 1),
    pageSize: Number(pagination.pageSize ?? params?.pageSize ?? 50),
    totalPages: Number(pagination.totalPages ?? 1),
  }
}

export async function getAppDeviceStatuses(params?: {
  page?: number
  pageSize?: number
  storeCode?: string
  deviceSystem?: string
  onlineState?: AppDeviceOnlineState
  keyword?: string
  appVersion?: string
  appBuildVersion?: string
}): Promise<AppDeviceStatusPagedResult> {
  const response = await request.get<ApiResponse<unknown>>(`${APP_DEVICE_API_BASE}/paged`, {
    params: buildAppDeviceStatusParams(params),
  })
  return normalizeAppDeviceStatusListResponse(response)
}

export async function getAppDeviceStatusSummary(params?: {
  storeCode?: string
  deviceSystem?: string
  keyword?: string
  appVersion?: string
  appBuildVersion?: string
}): Promise<AppDeviceStatusSummary> {
  const response = await request.get<ApiResponse<unknown>>(`${APP_DEVICE_API_BASE}/summary`, {
    params: {
      storeCode: params?.storeCode,
      deviceSystem: params?.deviceSystem,
      keyword: params?.keyword?.trim() || undefined,
      appVersion: params?.appVersion || undefined,
      appBuildVersion: params?.appBuildVersion || undefined,
    },
  })
  return normalizeAppDeviceStatusSummary(response)
}

/** 按版本统计设备数；只受分店、系统、关键词约束，不带版本精确筛选。 */
export async function getAppVersionDistribution(params?: {
  storeCode?: string
  deviceSystem?: string
  keyword?: string
}): Promise<AppVersionDistribution> {
  const response = await request.get<ApiResponse<unknown>>(`${APP_DEVICE_API_BASE}/version-distribution`, {
    params: {
      storeCode: params?.storeCode,
      deviceSystem: params?.deviceSystem,
      keyword: params?.keyword?.trim() || undefined,
    },
  })
  return normalizeAppVersionDistribution(response)
}

export async function activateDevice(id: number) {
  return request.post<ApiResponse<object>>(`${DEVICE_API_BASE}/${id}/activate`, {})
}

export async function disableDevice(id: number) {
  return request.post<ApiResponse<object>>(`${DEVICE_API_BASE}/${id}/disable`, {})
}

export async function lockDevice(id: number) {
  return request.post<ApiResponse<object>>(`${DEVICE_API_BASE}/${id}/lock`, {})
}

export async function getDeviceRegistrationDetail(id: number): Promise<DeviceRegistrationDetail> {
  const response = await request.get<ApiResponse<Record<string, unknown>>>(`${REACT_API_BASE}/${id}`)
  return normalizeDeviceRegistrationDetail(unwrapApiData(response) ?? {})
}

export async function updateDeviceRegistration(
  id: number,
  payload: UpdateDeviceRegistrationPayload
): Promise<DeviceRegistrationDetail> {
  const response = await request.put<ApiResponse<Record<string, unknown>>>(
    `${REACT_API_BASE}/${id}`,
    buildUpdateDeviceRegistrationPayload(payload)
  )
  return normalizeDeviceRegistrationDetail(unwrapApiData(response) ?? {})
}

export async function getStoreOptions(): Promise<StoreOption[]> {
  const response = await request.get<ApiResponse<StoreOption[]> | StoreOption[]>(
    '/api/Stores/all-by-name'
  )
  const payload = Array.isArray(response) ? response : response.data
  return Array.isArray(payload) ? payload : []
}

function normalizeEmergencyLoginGrant(value: unknown): EmergencyLoginGrantSummary | null {
  const raw = asRecord(value)
  if (!raw) {
    return null
  }

  const grantId = asString(pick(raw, 'grantId', 'GrantId'))
  const storeCode = asString(pick(raw, 'storeCode', 'StoreCode'))
  if (!grantId || !storeCode) {
    return null
  }

  const rawStatus = asString(pick(raw, 'status', 'Status'))?.toLowerCase()
  const status: EmergencyLoginGrantSummary['status'] =
    rawStatus === 'active' ? 'Active' : rawStatus === 'revoked' ? 'Revoked' : 'Expired'

  return {
    grantId,
    storeCode,
    businessDate: asString(pick(raw, 'businessDate', 'BusinessDate')) ?? '',
    keyId: asString(pick(raw, 'keyId', 'KeyId')) ?? '',
    permissionProfile: 'AllPosTerminal',
    issuedBy: asString(pick(raw, 'issuedBy', 'IssuedBy')) ?? '',
    reason: asString(pick(raw, 'reason', 'Reason', 'issuedReason', 'IssuedReason')) ?? '',
    issuedAtUtc: asString(pick(raw, 'issuedAtUtc', 'IssuedAtUtc')) ?? '',
    expiresAtUtc: asString(pick(raw, 'expiresAtUtc', 'ExpiresAtUtc')) ?? '',
    revokedBy: asString(pick(raw, 'revokedBy', 'RevokedBy')) ?? null,
    revokedAtUtc: asString(pick(raw, 'revokedAtUtc', 'RevokedAtUtc')) ?? null,
    revokeReason: asString(
      pick(raw, 'revokeReason', 'RevokeReason', 'revokedReason', 'RevokedReason')
    ) ?? null,
    status,
  }
}

export async function getEmergencyLoginGrant(
  storeCode: string
): Promise<EmergencyLoginGrantSummary | null> {
  const response = await request.get<ApiResponse<unknown>>(EMERGENCY_LOGIN_GRANT_API_BASE, {
    params: { storeCode },
  })
  const data = unwrapApiData(response)
  if (Array.isArray(data)) {
    const grants = data
      .map(normalizeEmergencyLoginGrant)
      .filter((grant): grant is EmergencyLoginGrantSummary => Boolean(grant))
    return grants.find((grant) => grant.status === 'Active') ?? grants[0] ?? null
  }
  return normalizeEmergencyLoginGrant(data)
}

export async function createEmergencyLoginGrant(
  storeCode: string,
  reason: string
): Promise<EmergencyLoginGrantCreateResponse> {
  const response = await request.post<ApiResponse<unknown>>(EMERGENCY_LOGIN_GRANT_API_BASE, {
    storeCode,
    reason: reason.trim(),
  })
  const data = asRecord(unwrapApiData(response))
  const grant = normalizeEmergencyLoginGrant(data ? pick(data, 'grant', 'Grant') : null)
  const token = data ? asString(pick(data, 'token', 'Token')) : undefined
  if (!grant || !token) {
    throw new Error('紧急登录授权响应无效')
  }
  return { grant, token }
}

export async function revokeEmergencyLoginGrant(
  grantId: string,
  reason: string
): Promise<EmergencyLoginGrantSummary> {
  const response = await request.post<ApiResponse<unknown>>(
    `${EMERGENCY_LOGIN_GRANT_API_BASE}/${encodeURIComponent(grantId)}/revoke`,
    { reason: reason.trim() }
  )
  const grant = normalizeEmergencyLoginGrant(unwrapApiData(response))
  if (!grant) {
    throw new Error('紧急登录授权响应无效')
  }
  return grant
}
