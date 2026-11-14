import type { ApiResponse } from '../types/api'
import type {
  PosSettingChange,
  PosSettingChangeQuery,
  PosSettingDefinition,
  PosSettingDeviceDetail,
  PosSettingDeviceStatus,
  PosSettingEffectiveValue,
  PosSettingLayer,
  PosSettingsCatalog,
  PosSettingStoreDetail,
  PosSettingStoreSummary,
  SavePosSettingsRequest,
} from '../types/posSettings'
import request, { unwrapApiData } from '../utils/request'

/**
 * 收银端设置下发接口（基础路径 api/react/v1/pos-settings）。响应统一包在 ApiResponse<T> 里：
 * - HTTP 非 2xx（含 403：非管理员改全局层）由 request 抛 RequestError，payload 里带可直接展示的 message；
 * - HTTP 200 但 success=false 由 unwrapApiData 抛 RequestError。
 * 这里只做信封解包与缺字段兜底（字典缺失给空对象、数组缺失给空数组），不改写任何设置值：
 * 值字典里没出现的键就是「没设置」，绝不补成 false。
 */

export const POS_SETTINGS_API_BASE = '/api/react/v1/pos-settings'

function url(path: string) {
  return `${POS_SETTINGS_API_BASE}/${path}`
}

function arrayOf<T>(value: T[] | null | undefined): T[] {
  return Array.isArray(value) ? value : []
}

function recordOf<T>(value: Record<string, T> | null | undefined): Record<string, T> {
  return value && typeof value === 'object' ? value : {}
}

type Envelope<T> = ApiResponse<T> | T

function normalizeDefinition(raw: PosSettingDefinition): PosSettingDefinition {
  return {
    key: raw.key,
    clientKinds: arrayOf(raw.clientKinds),
    // 缺省值缺失按 null（沿用本机值），不能当成 false。
    defaultWhenUnset: typeof raw.defaultWhenUnset === 'boolean' ? raw.defaultWhenUnset : null,
    isNew: raw.isNew === true,
  }
}

function normalizeLayer(raw: PosSettingLayer): PosSettingLayer {
  return {
    scope: raw?.scope ?? '',
    scopeKey: raw?.scopeKey ?? '',
    values: recordOf(raw?.values),
    updatedAtUtc: raw?.updatedAtUtc ?? null,
    updatedByName: raw?.updatedByName ?? null,
  }
}

function normalizeEffective(raw: Record<string, PosSettingEffectiveValue> | null | undefined) {
  return Object.fromEntries(
    Object.entries(recordOf(raw)).map(([key, item]) => [
      key,
      { value: typeof item?.value === 'boolean' ? item.value : null, source: item?.source ?? 'none' },
    ]),
  )
}

function normalizeDeviceStatus(raw: PosSettingDeviceStatus): PosSettingDeviceStatus {
  return {
    ...raw,
    clientKind: raw?.clientKind ?? 'other',
    appVersion: raw?.appVersion ?? null,
    status: raw?.status ?? 'never',
    appliedAtUtc: raw?.appliedAtUtc ?? null,
    hasDeviceOverride: raw?.hasDeviceOverride === true,
    offlineOverrideCount: raw?.offlineOverrideCount ?? 0,
    lastOfflineOverrideAtUtc: raw?.lastOfflineOverrideAtUtc ?? null,
  }
}

function normalizeStoreSummary(raw: PosSettingStoreSummary): PosSettingStoreSummary {
  return {
    ...raw,
    overrides: recordOf(raw?.overrides),
    deviceOverrideCount: raw?.deviceOverrideCount ?? 0,
    deviceTotal: raw?.deviceTotal ?? 0,
    deviceApplied: raw?.deviceApplied ?? 0,
  }
}

function normalizeStoreDetail(raw: PosSettingStoreDetail): PosSettingStoreDetail {
  return {
    ...raw,
    overrides: recordOf(raw?.overrides),
    effective: normalizeEffective(raw?.effective),
    devices: arrayOf(raw?.devices).map(normalizeDeviceStatus),
    updatedAtUtc: raw?.updatedAtUtc ?? null,
    updatedByName: raw?.updatedByName ?? null,
  }
}

function normalizeDeviceDetail(raw: PosSettingDeviceDetail): PosSettingDeviceDetail {
  return {
    ...raw,
    status: normalizeDeviceStatus(raw?.status),
    overrides: recordOf(raw?.overrides),
    effective: normalizeEffective(raw?.effective),
    reported: recordOf(raw?.reported),
  }
}

export async function getPosSettingsCatalog(signal?: AbortSignal): Promise<PosSettingsCatalog> {
  const payload = await request<Envelope<PosSettingsCatalog>>(url('catalog'), { method: 'GET', signal })
  const data = unwrapApiData(payload)
  return { definitions: arrayOf(data?.definitions).map(normalizeDefinition) }
}

export async function getPosSettingsGlobal(signal?: AbortSignal): Promise<PosSettingLayer> {
  const payload = await request<Envelope<PosSettingLayer>>(url('global'), { method: 'GET', signal })
  return normalizeLayer(unwrapApiData(payload))
}

export async function savePosSettingsGlobal(body: SavePosSettingsRequest): Promise<PosSettingLayer> {
  const payload = await request<Envelope<PosSettingLayer>>(url('global'), { method: 'PUT', data: body })
  return normalizeLayer(unwrapApiData(payload))
}

export async function listPosSettingStores(signal?: AbortSignal): Promise<PosSettingStoreSummary[]> {
  const payload = await request<Envelope<PosSettingStoreSummary[]>>(url('stores'), { method: 'GET', signal })
  return arrayOf(unwrapApiData(payload)).map(normalizeStoreSummary)
}

export async function getPosSettingStore(storeCode: string, signal?: AbortSignal): Promise<PosSettingStoreDetail> {
  const payload = await request<Envelope<PosSettingStoreDetail>>(
    url(`stores/${encodeURIComponent(storeCode)}`),
    { method: 'GET', signal },
  )
  return normalizeStoreDetail(unwrapApiData(payload))
}

export async function savePosSettingStore(storeCode: string, body: SavePosSettingsRequest): Promise<PosSettingStoreDetail> {
  const payload = await request<Envelope<PosSettingStoreDetail>>(
    url(`stores/${encodeURIComponent(storeCode)}`),
    { method: 'PUT', data: body },
  )
  return normalizeStoreDetail(unwrapApiData(payload))
}

export async function listPosSettingDevices(storeCode: string, signal?: AbortSignal): Promise<PosSettingDeviceStatus[]> {
  const payload = await request<Envelope<PosSettingDeviceStatus[]>>(url('devices'), {
    method: 'GET',
    signal,
    params: { storeCode },
  })
  return arrayOf(unwrapApiData(payload)).map(normalizeDeviceStatus)
}

export async function getPosSettingDevice(deviceCode: string, signal?: AbortSignal): Promise<PosSettingDeviceDetail> {
  const payload = await request<Envelope<PosSettingDeviceDetail>>(
    url(`devices/${encodeURIComponent(deviceCode)}`),
    { method: 'GET', signal },
  )
  return normalizeDeviceDetail(unwrapApiData(payload))
}

export async function savePosSettingDevice(deviceCode: string, body: SavePosSettingsRequest): Promise<PosSettingDeviceDetail> {
  const payload = await request<Envelope<PosSettingDeviceDetail>>(
    url(`devices/${encodeURIComponent(deviceCode)}`),
    { method: 'PUT', data: body },
  )
  return normalizeDeviceDetail(unwrapApiData(payload))
}

export async function listPosSettingChanges(query: PosSettingChangeQuery = {}, signal?: AbortSignal): Promise<PosSettingChange[]> {
  const payload = await request<Envelope<PosSettingChange[]>>(url('changes'), {
    method: 'GET',
    signal,
    params: { scope: query.scope, scopeKey: query.scopeKey, limit: query.limit ?? 50 },
  })
  return arrayOf(unwrapApiData(payload))
}
