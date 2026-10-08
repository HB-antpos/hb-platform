// 门店小票资料「下发」接口类型。字段名与路由严格对应契约（HBweb 后端 StoreReceiptProfilesController，路由前缀 api/stores/receipt-profile），不得自行改名。

/** 参与下发的 8 个小票字段（Store 当前值或某次下发快照）；voucherTerms / installmentTerms 为 null 表示未定制（收银端按内置默认文案打印）。 */
export interface StoreReceiptProfileFields {
  brandName: string | null
  storeName: string
  address: string | null
  phone: string | null
  abn: string | null
  returnPolicy: string | null
  voucherTerms: string | null
  installmentTerms: string | null
}

/** never=从未下发；synced=Store 当前值与最新快照一致；pending=有未下发的修改。 */
export type StoreReceiptProfileStatus = 'never' | 'synced' | 'pending'

export interface StoreReceiptProfileStatusItem {
  storeGuid: string
  storeCode: string
  storeName: string
  status: StoreReceiptProfileStatus
  /** 0=从未下发。 */
  latestVersion: number
  publishedAtUtc: string | null
  publishedBy: string | null
  /** Store 当前值，用于确认框新旧对比。 */
  current: StoreReceiptProfileFields
  /** 最新快照；从未下发为 null。 */
  latest: StoreReceiptProfileFields | null
  /** POSM 中该店 设备类型='POS' 且 设备状态=1 的台数。 */
  deviceTotal: number
  /** 其中已应用到最新版本的台数；latestVersion=0 时为 0。 */
  deviceApplied: number
}

export interface StoreReceiptProfileStatusRequest {
  storeGuids: string[]
}

export type StoreReceiptProfileClientKind = 'wpf' | 'handheld' | 'ipad' | 'other'

export interface StoreReceiptProfileDevice {
  deviceCode: string
  deviceSystem: string
  clientKind: StoreReceiptProfileClientKind
  deviceStatus: number
  isOnline: boolean
  lastHeartbeatAt: string | null
  appliedVersion: number | null
  appliedAtUtc: string | null
  upToDate: boolean
}

export interface StoreReceiptProfileDevicesResult {
  storeGuid: string
  storeCode: string
  latestVersion: number
  devices: StoreReceiptProfileDevice[]
}

export interface StoreReceiptProfilePublishRequest {
  storeGuids: string[]
}

export type StoreReceiptProfilePublishOutcome = 'published' | 'unchanged'

export interface StoreReceiptProfilePublishResultItem {
  storeGuid: string
  storeCode: string
  outcome: StoreReceiptProfilePublishOutcome
  version: number
}

export interface StoreReceiptProfilePublishResult {
  requestedCount: number
  publishedCount: number
  unchangedCount: number
  items: StoreReceiptProfilePublishResultItem[]
}

/** 整批失败（HTTP 400/409）时 ApiResponse.details 里的逐店条目。 */
export interface StoreReceiptProfilePublishErrorDetail {
  storeGuid?: string
  storeCode?: string
  errorCode?: string
  message?: string
}
