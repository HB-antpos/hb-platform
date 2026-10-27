import type { ApiResponse } from '../types/api'
import type {
  MyobConnection,
  MyobConnectionStatus,
  MyobStoreMappingChange,
  MyobStoreMappingItem,
  MyobStoreMappings,
} from '../types/myob'
import request, { unwrapApiData } from '../utils/request'

// 路由与后端 MYOB 控制器一致；全部接口需登录 + 权限 Myob.Connection.Manage。
const MYOB_BASE = '/api/react/v1/myob'

function text(value: unknown) {
  return typeof value === 'string' ? value : ''
}

function nullableText(value: unknown) {
  return typeof value === 'string' && value.trim() ? value : null
}

function record(value: unknown): Record<string, unknown> {
  return value && typeof value === 'object' ? (value as Record<string, unknown>) : {}
}

/** 状态值只认三种；未知值按「需重新授权」处理（偏保守：不会把未知状态当成可用连接）。 */
export function normalizeMyobStatus(value: unknown): MyobConnectionStatus {
  if (value === 'Active' || value === 'NeedsReauth' || value === 'Disconnected') {
    return value
  }
  return 'NeedsReauth'
}

export function normalizeMyobConnection(raw: unknown): MyobConnection | null {
  const item = record(raw)
  const connectionGuid = text(item.connectionGuid)
  if (!connectionGuid) {
    return null
  }
  return {
    connectionGuid,
    businessId: text(item.businessId),
    businessName: text(item.businessName),
    abn: nullableText(item.abn),
    status: normalizeMyobStatus(item.status),
    authorizedByUserGuid: nullableText(item.authorizedByUserGuid),
    authorizedByName: nullableText(item.authorizedByName),
    authorizedAt: nullableText(item.authorizedAt),
    lastRefreshedAt: nullableText(item.lastRefreshedAt),
    lastError: nullableText(item.lastError),
    storeCodes: Array.isArray(item.storeCodes)
      ? item.storeCodes.filter((code): code is string => typeof code === 'string' && code !== '')
      : [],
  }
}

export function normalizeMyobConnections(payload: unknown): MyobConnection[] {
  if (!Array.isArray(payload)) {
    return []
  }
  return payload.map(normalizeMyobConnection).filter((item): item is MyobConnection => item !== null)
}

function normalizeStoreItem(raw: unknown): MyobStoreMappingItem | null {
  const item = record(raw)
  const storeCode = text(item.storeCode)
  if (!storeCode) {
    return null
  }
  return {
    storeCode,
    storeName: text(item.storeName),
    storeAbn: nullableText(item.storeAbn),
    // 后端没给 isActive 时按启用处理，避免整张表被默认隐藏
    isActive: item.isActive !== false,
    connectionGuid: nullableText(item.connectionGuid),
    suggestedConnectionGuid: nullableText(item.suggestedConnectionGuid),
    mappedAt: nullableText(item.mappedAt),
  }
}

export function normalizeMyobStoreMappings(payload: unknown): MyobStoreMappings {
  const data = record(payload)
  return {
    stores: Array.isArray(data.stores)
      ? data.stores.map(normalizeStoreItem).filter((item): item is MyobStoreMappingItem => item !== null)
      : [],
    connections: normalizeMyobConnections(data.connections),
  }
}

/** 单条连接写接口（改 ABN / 验证 / 断开）统一返回更新后的连接；返回空时抛错，避免页面拿到 null 渲染崩溃。 */
function requireConnection(payload: unknown): MyobConnection {
  const connection = normalizeMyobConnection(payload)
  if (!connection) {
    throw new Error('MYOB response missing connection')
  }
  return connection
}

export async function getMyobConnections(): Promise<MyobConnection[]> {
  const response = await request.get<ApiResponse<unknown>>(`${MYOB_BASE}/connections`)
  return normalizeMyobConnections(unwrapApiData(response))
}

/** 取 MYOB 授权页地址；前端拿到后整页跳转（window.location.href）。 */
export async function createMyobAuthorizeUrl(): Promise<string> {
  const response = await request.post<ApiResponse<unknown>>(`${MYOB_BASE}/connections/authorize-url`)
  const url = text(record(unwrapApiData(response)).url)
  if (!url) {
    throw new Error('MYOB authorize url is empty')
  }
  return url
}

/** 更新 ABN；空串清除。调用方应先用 normalizeAbnInput 去空格。 */
export async function updateMyobConnectionAbn(connectionGuid: string, abn: string): Promise<MyobConnection> {
  const response = await request.put<ApiResponse<unknown>>(
    `${MYOB_BASE}/connections/${encodeURIComponent(connectionGuid)}/abn`,
    { abn },
  )
  return requireConnection(unwrapApiData(response))
}

/** 验证 token，并从 MYOB 刷新公司名与 ABN。 */
export async function verifyMyobConnection(connectionGuid: string): Promise<MyobConnection> {
  const response = await request.post<ApiResponse<unknown>>(
    `${MYOB_BASE}/connections/${encodeURIComponent(connectionGuid)}/verify`,
  )
  return requireConnection(unwrapApiData(response))
}

/** 断开：清除 token，并移除该连接下所有分店映射。 */
export async function disconnectMyobConnection(connectionGuid: string): Promise<MyobConnection> {
  const response = await request.post<ApiResponse<unknown>>(
    `${MYOB_BASE}/connections/${encodeURIComponent(connectionGuid)}/disconnect`,
  )
  return requireConnection(unwrapApiData(response))
}

export async function getMyobStoreMappings(): Promise<MyobStoreMappings> {
  const response = await request.get<ApiResponse<unknown>>(`${MYOB_BASE}/store-mappings`)
  return normalizeMyobStoreMappings(unwrapApiData(response))
}

/** 只提交变化的行；返回保存后的完整映射表。 */
export async function updateMyobStoreMappings(items: MyobStoreMappingChange[]): Promise<MyobStoreMappings> {
  const response = await request.put<ApiResponse<unknown>>(`${MYOB_BASE}/store-mappings`, { items })
  return normalizeMyobStoreMappings(unwrapApiData(response))
}
