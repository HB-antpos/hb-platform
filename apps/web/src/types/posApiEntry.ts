/** 收银端访问 POS API 的三条入口：EdgeOne（pos.hotbargain.vip）/ AWS 悉尼（pos-syd.hotbargain.vip）/ 直连（hotbargain.vip）。 */
export type PosApiEntryMode = 'edge' | 'aws' | 'direct'

/** 某分店设备最近一段时间实际走的入口分布（由服务器按请求来源识别，收银端无需上报）。 */
export interface PosApiEntryDeviceStats {
  total: number
  online: number
  byEntry: {
    edge: number
    aws: number
    direct: number
    /** 没有记录到入口（从未请求 / 老版本未带标记） */
    unknown: number
  }
  /** 实际入口与首选不一致（回退中）的设备数 */
  fallbackCount: number
  /** 客户端版本过低、不认后台设置的设备数 */
  unsupportedCount: number
}

export interface PosApiEntryStoreItem {
  storeGuid: string
  storeCode: string
  storeName: string
  /** 后台显式设置的首选；null = 从未设置，按 EdgeOne 默认 */
  mode: PosApiEntryMode | null
  /** 实际生效的首选（mode 为 null 时为 edge） */
  effectiveMode: PosApiEntryMode
  updatedAt: string | null
  updatedBy: string | null
  devices: PosApiEntryDeviceStats
}

export interface PosApiEntryUpdateResult {
  updated: number
  skipped: number
}

// ───────────────────────── 线路用量统计（GET /api/react/v1/pos-api-entry/usage） ─────────────────────────

/** 用量统计的粒度：日 / 周（周一起算）/ 月（自然月），周和月由后端对日表汇总。 */
export type PosApiEntryUsageGranularity = 'day' | 'week' | 'month'

/** 用量统计里的线路：三条线路 + 识别不出的 unknown（没带 X-HB-Entry，例如绕过 nginx 的直连）。 */
export type PosApiEntryUsageEntry = PosApiEntryMode | 'unknown'

export interface PosApiEntryUsageCounts {
  requests: number
  /** 4xx 请求数（requests 的子集） */
  status4xx: number
  /** 5xx 请求数（requests 的子集） */
  status5xx: number
}

export interface PosApiEntryUsageEntryItem extends PosApiEntryUsageCounts {
  entry: PosApiEntryUsageEntry
  /** 该线路请求数 / 范围内总请求数，0~1；总数为 0 时为 0 */
  share: number
}

export interface PosApiEntryUsageBucket {
  /** 桶起始日 yyyy-MM-dd（日=当天，周=周一，月=1 号），也是桶的唯一键 */
  bucketStart: string
  /** 桶结束日（含） */
  bucketEnd: string
  /** 首尾不完整的周 / 月：只汇总了查询范围内的日期 */
  partial: boolean
  total: PosApiEntryUsageCounts
  /** 固定四项：edge / aws / direct / unknown */
  entries: PosApiEntryUsageEntryItem[]
}

export interface PosApiEntryUsageStore {
  /** 空串＝未归属分店（未鉴权请求，如健康检查） */
  storeCode: string
  storeName: string
  total: PosApiEntryUsageCounts
  entries: PosApiEntryUsageEntryItem[]
}

export interface PosApiEntryUsageResult {
  startDate: string
  endDate: string
  granularity: PosApiEntryUsageGranularity
  timeZone: string
  storeCode: string
  total: PosApiEntryUsageCounts
  entries: PosApiEntryUsageEntryItem[]
  buckets: PosApiEntryUsageBucket[]
  stores: PosApiEntryUsageStore[]
}

export interface PosApiEntryUsageQuery {
  /** yyyy-MM-dd（悉尼日期，含） */
  startDate: string
  /** yyyy-MM-dd（悉尼日期，含） */
  endDate: string
  granularity: PosApiEntryUsageGranularity
  /** 不传＝全部可见分店 */
  storeCode?: string
}

// ───────────────────────── 主 API（Web 后台 + 员工 App）线路用量 ─────────────────────────

/** 主 API 请求的客户端类别（后端按 User-Agent 粗分）：web＝浏览器（Web 后台），app＝员工 App，other＝脚本 / 监控 / 其余。 */
export type MainApiClientKind = 'web' | 'app' | 'other'

/** 某一客户端类别在范围内的合计；share＝占范围内总请求数（0~1），entries 里的 share 以该类别自身请求数为分母。 */
export interface MainApiEntryUsageClientKindItem {
  clientKind: MainApiClientKind
  total: PosApiEntryUsageCounts
  share: number
  entries: PosApiEntryUsageEntryItem[]
}

export interface MainApiEntryUsageResult {
  startDate: string
  endDate: string
  granularity: PosApiEntryUsageGranularity
  timeZone: string
  /** 生效的客户端类别筛选；空串＝全部 */
  clientKind: string
  total: PosApiEntryUsageCounts
  entries: PosApiEntryUsageEntryItem[]
  buckets: PosApiEntryUsageBucket[]
  /** 固定三项：web / app / other */
  clientKinds: MainApiEntryUsageClientKindItem[]
}

export interface MainApiEntryUsageQuery {
  /** yyyy-MM-dd（悉尼日期，含） */
  startDate: string
  /** yyyy-MM-dd（悉尼日期，含） */
  endDate: string
  granularity: PosApiEntryUsageGranularity
  /** 不传＝全部类别 */
  clientKind?: MainApiClientKind
}
