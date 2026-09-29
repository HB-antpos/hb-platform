export interface RevenueBranch {
  rank: number
  branchCode: string
  branchName: string
  revenue: number
  revenueLY: number
  orderCount: number
  orderCountLY: number
  aov: number
  aovLY: number
}

export interface RevenueHourly {
  hour: string
  revenue: number
  revenueLY: number
  percentage: number
  isPeak: boolean
  branchCode?: string
  branchName?: string
  /** 后端整页快照按分店逐小时返回单数；旧接口缺字段时按 0。 */
  orderCount?: number
  orderCountLY?: number
}

/** 多日区间最后一天（今天）与同期对应日的数据，用于把今天对齐到截止整点。 */
export interface RevenueLastDay {
  /** 后端 DateTime，形如 2026-09-24T00:00:00；取前 10 位作为日期。 */
  date: string
  compareDate?: string | null
  /** 今天各店全天日统计（revenue）与同期对应日全天（revenueLY）。 */
  branches: RevenueBranch[]
  /** 今天（revenue）与同期对应日（revenueLY）的分店×小时。 */
  hourly: RevenueHourly[]
}

export interface RevenueWeeklyNode {
  key: string
  level: 'week' | 'branch' | 'date'
  hierarchy: string
  revenue: number
  revenueLY: number
  orders: number
  ordersLY: number
  aov: number
  aovLY: number
  yoyChange?: number | null
  children?: RevenueWeeklyNode[] | null
}

export interface RevenueSummary {
  revenue: number
  revenueLY: number | null
  orders: number
  ordersLY: number | null
  aov: number
  aovLY: number | null
  decliningBranches: number
  newBranches: number
}

export interface RevenueHourlyRow {
  hour: string
  revenue: number
  revenueLY: number | null
  percentage: number
  isPeak: boolean
}

export type RevenueTrendTone = 'positive' | 'negative' | 'neutral'

export interface RevenueTrend {
  text: string
  tone: RevenueTrendTone
}
