// 日结记录（WPF / 手持 / iPad 上传的日结与现金盘点明细）只读接口的前端类型。
// 与后端 BlazorApp.Shared/DTOs/DailyCloseDtos.cs 一一对应，字段 camelCase。

/** 来源终端类型。 */
export type DailyCloseClientKind = 'Wpf' | 'Handheld' | 'Ipad'

/** 明细完整度：Full 客户端完整上传；CashOnly 回填且只有现金三项；TraceOnly 回填且没有任何金额。 */
export type DailyCloseDetailLevel = 'Full' | 'CashOnly' | 'TraceOnly'

/** 数据来源：收银端上传 / 操作日志回填。 */
export type DailyCloseDataSource = 'ClientUpload' | 'AuditBackfill'

/** 现金差额三态 + 无金额，由后端按 cashDifference 符号给出。 */
export type DailyCloseDifferenceKind = 'short' | 'over' | 'even' | 'none'

/** 列表状态页签（all 为不过滤）。 */
export type DailyCloseStatusFilter = 'all' | DailyCloseDifferenceKind

export type DailyCloseTenderMethod = 'Cash' | 'Card' | 'Voucher'

export type DailyCloseCashKind = 'Note' | 'Coin'

/** 列表查询参数（GET /api/react/v1/pos-daily-closes）。 */
export interface DailyCloseListQuery {
  businessDateFrom: string
  businessDateTo: string
  /** 逗号分隔的分店编号；不传表示账号可见的全部分店。 */
  storeCodes?: string
  deviceCode?: string
  clientKind?: DailyCloseClientKind
  keyword?: string
  status?: DailyCloseStatusFilter
  page: number
  pageSize: number
}

export interface DailyCloseListItem {
  dailyCloseGuid: string
  storeCode: string
  storeName: string | null
  storeTimeZoneId: string | null
  deviceCode: string
  clientKind: DailyCloseClientKind | string
  detailLevel: DailyCloseDetailLevel | string
  dataSource: DailyCloseDataSource | string
  /** yyyy-MM-dd */
  businessDate: string
  businessDateInferred: boolean
  cashierId: string
  cashierName: string
  savedAtUtc: string
  orderCount: number | null
  expectedCashAmount: number | null
  countedCashAmount: number | null
  cashDifference: number | null
  cardNetAmount: number | null
  differenceKind: DailyCloseDifferenceKind
  /** 同一分店、终端、营业日内第几次保存（从 1 起）。 */
  saveSequence: number
  /** 同一分店、终端、营业日内的保存总次数。 */
  saveCountInDay: number
}

/** 各状态页签的记录数：不受 status 参数影响，其余筛选条件生效。 */
export interface DailyCloseCounts {
  all: number
  short: number
  over: number
  even: number
  none: number
}

/** 当前筛选（含 status）下现金差额非空的记录的金额合计。 */
export interface DailyCloseTotals {
  expectedCash: number
  countedCash: number
  difference: number
}

export interface DailyCloseListResult {
  items: DailyCloseListItem[]
  total: number
  page: number
  pageSize: number
  counts: DailyCloseCounts
  totals: DailyCloseTotals
}

export interface DailyCloseTender {
  method: DailyCloseTenderMethod | string
  salesAmount: number
  refundAmount: number
  netAmount: number
}

export interface DailyCloseCashCount {
  /** 面额（分），10000 表示 $100。 */
  denominationCents: number
  quantity: number
  subtotalAmount: number
  kind: DailyCloseCashKind | string
}

export interface DailyCloseDetail extends DailyCloseListItem {
  periodFromUtc: string | null
  periodToUtc: string | null
  appVersion: string | null
  returnQuantity: number | null
  refundAmount: number | null
  /** 仅 Full 记录有值，非 Full 为空数组。 */
  tenders: DailyCloseTender[]
  /** 仅 Full 记录有值（按面额降序），非 Full 为空数组。 */
  cashCounts: DailyCloseCashCount[]
  noteSubtotal: number | null
  coinSubtotal: number | null
  receivedAtUtc: string
}
