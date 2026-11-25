import type { ApiResponse } from '../types/api'
import type {
  DeviceActivationCodeCreatePayload,
  DeviceActivationCodeCreateResponse,
  DeviceActivationCodePagedResult,
  DeviceActivationCodeSummary,
  DeviceActivationManageableStore,
  DeviceActivationStatus,
  DeviceActivationSystem,
  MobileDeviceActivationCodeCreatePayload,
  MobileDeviceActivationManageableAccount,
} from '../types/deviceActivationCode'
import request, { unwrapApiData } from '../utils/request'

const API_BASE = '/api/react/v1/device-activation-codes'
const MOBILE_API_BASE = '/api/react/v1/mobile-device-activation-codes'

function asRecord(value: unknown): Record<string, unknown> | null {
  return value && typeof value === 'object' && !Array.isArray(value)
    ? value as Record<string, unknown>
    : null
}

function pick(raw: Record<string, unknown>, ...keys: string[]) {
  for (const key of keys) {
    if (Object.prototype.hasOwnProperty.call(raw, key)) {
      return raw[key]
    }
  }
  return undefined
}

function asString(value: unknown): string | undefined {
  return typeof value === 'string' && value.trim() ? value.trim() : undefined
}

function asNullableString(value: unknown): string | null {
  return value === null ? null : asString(value) ?? null
}

function asNumber(value: unknown, fallback: number) {
  const numeric = typeof value === 'number' ? value : Number(value)
  return Number.isFinite(numeric) ? numeric : fallback
}

function normalizeSystem(value: unknown): DeviceActivationSystem | null {
  const system = asString(value)
  return system === 'Windows' ||
    system === 'iPadOS' ||
    system === 'AndroidTablet' ||
    system === 'Android' ||
    system === 'iOS'
    ? system
    : null
}

function normalizeStatus(value: unknown): DeviceActivationStatus {
  const status = asString(value)?.toLowerCase()
  if (status === 'expired') return 'Expired'
  if (status === 'revoked') return 'Revoked'
  if (status === 'consumed') return 'Consumed'
  return 'Available'
}

export function normalizeDeviceActivationCodeSummary(
  value: unknown,
): DeviceActivationCodeSummary | null {
  const raw = asRecord(value)
  if (!raw) return null

  const grantId = asString(pick(raw, 'grantId', 'GrantId'))
  const storeCode = asString(pick(raw, 'storeCode', 'StoreCode'))
  const deviceSystem = normalizeSystem(pick(raw, 'deviceSystem', 'DeviceSystem'))
  if (!grantId || !storeCode || !deviceSystem) return null

  const consumptionKindValue = asString(pick(raw, 'consumptionKind', 'ConsumptionKind'))
  const consumptionKind = consumptionKindValue === 'Initial' || consumptionKindValue === 'Rebind'
    ? consumptionKindValue
    : null

  return {
    grantId,
    storeCode,
    storeName: asNullableString(pick(raw, 'storeName', 'StoreName')),
    deviceSystem,
    status: normalizeStatus(pick(raw, 'status', 'Status')),
    createdAtUtc: asString(pick(raw, 'createdAtUtc', 'CreatedAtUtc')) ?? '',
    createdBy: asString(pick(raw, 'createdBy', 'CreatedBy')) ?? '',
    reason: asString(pick(raw, 'reason', 'Reason', 'createReason', 'CreateReason')) ?? '',
    expiresAtUtc: asString(pick(raw, 'expiresAtUtc', 'ExpiresAtUtc')) ?? '',
    revokedAtUtc: asNullableString(pick(raw, 'revokedAtUtc', 'RevokedAtUtc')),
    revokedBy: asNullableString(pick(raw, 'revokedBy', 'RevokedBy')),
    revokeReason: asNullableString(pick(raw, 'revokeReason', 'RevokeReason')),
    consumedAtUtc: asNullableString(pick(raw, 'consumedAtUtc', 'ConsumedAtUtc')),
    consumedHardwareId: asNullableString(pick(raw, 'consumedHardwareId', 'ConsumedHardwareId')),
    consumedDeviceCode: asNullableString(pick(raw, 'consumedDeviceCode', 'ConsumedDeviceCode')),
    consumptionKind,
    previousStoreCode: asNullableString(pick(raw, 'previousStoreCode', 'PreviousStoreCode')),
    previousDeviceCode: asNullableString(pick(raw, 'previousDeviceCode', 'PreviousDeviceCode')),
    targetUserGuid: asNullableString(pick(raw, 'targetUserGuid', 'TargetUserGuid')),
    targetUsername: asNullableString(pick(raw, 'targetUsername', 'TargetUsername')),
    targetFullName: asNullableString(pick(raw, 'targetFullName', 'TargetFullName')),
  }
}

export function normalizeDeviceActivationCodeListResponse(
  response: unknown,
): DeviceActivationCodePagedResult {
  const envelope = asRecord(response)
  const data = envelope && Object.prototype.hasOwnProperty.call(envelope, 'data')
    ? pick(envelope, 'data', 'Data')
    : response
  const raw = asRecord(data)
  const rawItems = Array.isArray(data)
    ? data
    : pick(raw ?? {}, 'items', 'Items', 'grants', 'Grants')
  const items = Array.isArray(rawItems)
    ? rawItems
        .map(normalizeDeviceActivationCodeSummary)
        .filter((item): item is DeviceActivationCodeSummary => Boolean(item))
    : []

  return {
    items,
    total: asNumber(pick(raw ?? {}, 'total', 'Total'), items.length),
    page: asNumber(pick(raw ?? {}, 'page', 'Page'), 1),
    pageSize: asNumber(pick(raw ?? {}, 'pageSize', 'PageSize'), Math.max(items.length, 20)),
    totalPages: asNumber(pick(raw ?? {}, 'totalPages', 'TotalPages'), 1),
  }
}

export async function getDeviceActivationCodes(params: {
  page: number
  pageSize: number
  storeCode?: string
  deviceSystem?: DeviceActivationSystem
  status?: DeviceActivationStatus
}): Promise<DeviceActivationCodePagedResult> {
  const response = await request.get<ApiResponse<unknown>>(API_BASE, { params })
  return normalizeDeviceActivationCodeListResponse(unwrapApiData(response))
}

export async function getMobileDeviceActivationCodes(params: {
  page: number
  pageSize: number
  storeCode?: string
  deviceSystem?: DeviceActivationSystem
  status?: DeviceActivationStatus
}): Promise<DeviceActivationCodePagedResult> {
  const response = await request.get<ApiResponse<unknown>>(MOBILE_API_BASE, { params })
  return normalizeDeviceActivationCodeListResponse(unwrapApiData(response))
}

export async function getDeviceActivationManageableStores(): Promise<DeviceActivationManageableStore[]> {
  const response = await request.get<ApiResponse<unknown>>(`${API_BASE}/manageable-stores`)
  const data = unwrapApiData(response)
  if (!Array.isArray(data)) return []

  return data.flatMap((value) => {
    const raw = asRecord(value)
    const storeCode = raw ? asString(pick(raw, 'storeCode', 'StoreCode')) : undefined
    const storeName = raw ? asString(pick(raw, 'storeName', 'StoreName')) : undefined
    return storeCode && storeName ? [{ storeCode, storeName }] : []
  })
}

export async function getMobileDeviceActivationManageableStores(): Promise<DeviceActivationManageableStore[]> {
  const response = await request.get<ApiResponse<unknown>>(`${MOBILE_API_BASE}/manageable-stores`)
  const data = unwrapApiData(response)
  if (!Array.isArray(data)) return []

  return data.flatMap((value) => {
    const raw = asRecord(value)
    const storeCode = raw ? asString(pick(raw, 'storeCode', 'StoreCode')) : undefined
    const storeName = raw ? asString(pick(raw, 'storeName', 'StoreName')) : undefined
    return storeCode && storeName ? [{ storeCode, storeName }] : []
  })
}

export async function getMobileDeviceActivationManageableAccounts(
  storeCode: string,
): Promise<MobileDeviceActivationManageableAccount[]> {
  const response = await request.get<ApiResponse<unknown>>(`${MOBILE_API_BASE}/manageable-accounts`, {
    params: { storeCode },
  })
  const data = unwrapApiData(response)
  if (!Array.isArray(data)) return []

  return data.flatMap((value) => {
    const raw = asRecord(value)
    if (!raw) return []
    const userGuid = asString(pick(raw, 'userGuid', 'UserGuid'))
    const username = asString(pick(raw, 'username', 'Username'))
    if (!userGuid || !username) return []
    return [{
      userGuid,
      username,
      fullName: asNullableString(pick(raw, 'fullName', 'FullName')),
    }]
  })
}

export async function createDeviceActivationCode(
  payload: DeviceActivationCodeCreatePayload,
): Promise<DeviceActivationCodeCreateResponse> {
  const response = await request.post<ApiResponse<unknown>>(API_BASE, {
    ...payload,
    reason: payload.reason.trim(),
  })
  const data = asRecord(unwrapApiData(response))
  const grant = normalizeDeviceActivationCodeSummary(
    data ? pick(data, 'grant', 'Grant') ?? data : null,
  )
  const activationCode = data
    ? asString(pick(data, 'activationCode', 'ActivationCode'))
    : undefined
  if (!grant || !activationCode) {
    throw new Error('设备开通码创建响应无效')
  }
  return { grant, activationCode }
}

export async function createMobileDeviceActivationCode(
  payload: MobileDeviceActivationCodeCreatePayload,
): Promise<DeviceActivationCodeCreateResponse> {
  const response = await request.post<ApiResponse<unknown>>(MOBILE_API_BASE, {
    ...payload,
    reason: payload.reason.trim(),
  })
  const data = asRecord(unwrapApiData(response))
  const grant = normalizeDeviceActivationCodeSummary(
    data ? pick(data, 'grant', 'Grant') ?? data : null,
  )
  const activationCode = data
    ? asString(pick(data, 'activationCode', 'ActivationCode'))
    : undefined
  if (!grant || !activationCode || !grant.targetUserGuid || !grant.targetUsername) {
    throw new Error('移动端设备开通码创建响应无效')
  }
  return { grant, activationCode }
}

export async function revokeDeviceActivationCode(
  grantId: string,
  reason: string,
): Promise<DeviceActivationCodeSummary> {
  const response = await request.post<ApiResponse<unknown>>(
    `${API_BASE}/${encodeURIComponent(grantId)}/revoke`,
    { reason: reason.trim() },
  )
  const grant = normalizeDeviceActivationCodeSummary(unwrapApiData(response))
  if (!grant) {
    throw new Error('设备开通码撤销响应无效')
  }
  return grant
}
export async function revokeMobileDeviceActivationCode(
  grantId: string,
  reason: string,
): Promise<DeviceActivationCodeSummary> {
  const response = await request.post<ApiResponse<unknown>>(
    `${MOBILE_API_BASE}/${encodeURIComponent(grantId)}/revoke`,
    { reason: reason.trim() },
  )
  const grant = normalizeDeviceActivationCodeSummary(unwrapApiData(response))
  if (!grant || !grant.targetUserGuid || !grant.targetUsername) {
    throw new Error('移动端设备开通码撤销响应无效')
  }
  return grant
}
