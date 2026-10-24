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
