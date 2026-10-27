// MYOB 对接（阶段 0：连接）接口类型。契约以后端 services/backend/BlazorApp.Api/Services/Myob/MyobDtos.cs 为准，
// 全部接口走 /api/react/v1/myob，返回 ApiResponse<T>，权限 Myob.Connection.Manage。

/** 连接状态：Active 可用；NeedsReauth token 失效需重新授权；Disconnected 已手动断开。 */
export type MyobConnectionStatus = 'Active' | 'NeedsReauth' | 'Disconnected'

export interface MyobConnection {
  connectionGuid: string
  /** MYOB 公司文件 ID（API 路径里的 cf_uri）。 */
  businessId: string
  businessName: string
  /** 只含数字（11 位）；未知为 null。 */
  abn: string | null
  status: MyobConnectionStatus
  authorizedByUserGuid: string | null
  authorizedByName: string | null
  /** 后端 UTC 时间字符串（可能不带 Z）。 */
  authorizedAt: string | null
  lastRefreshedAt: string | null
  lastError: string | null
  /** 映射到本连接的分店编号。 */
  storeCodes: string[]
}

export interface MyobStoreMappingItem {
  storeCode: string
  storeName: string
  storeAbn: string | null
  isActive: boolean
  /** 当前映射的连接；未映射为 null。 */
  connectionGuid: string | null
  /** 未映射时按 ABN 一致建议的 Active 连接；已映射时恒为 null。 */
  suggestedConnectionGuid: string | null
  mappedAt: string | null
}

export interface MyobStoreMappings {
  stores: MyobStoreMappingItem[]
  /** 可选作映射目标的连接（仅 Active）。 */
  connections: MyobConnection[]
}

/** PUT /store-mappings 的一行；connectionGuid 为 null 表示移除该分店映射。 */
export interface MyobStoreMappingChange {
  storeCode: string
  connectionGuid: string | null
}
