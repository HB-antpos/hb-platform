/** 老系统（旧版收银 POSM.EmployeeLogs）操作日志；时间均为门店本地墙钟时间字符串，不带时区。 */
export interface LegacyEmployeeLogItem {
  id: string
  employeeId?: string | null
  employeeName?: string | null
  operation?: string | null
  operationDetail?: string | null
  operationTime: string
  deviceCode?: string | null
  storeCode?: string | null
  lastUploadTime: string
  /** 操作类型属于危险操作（直接影响收款或现金）。 */
  isDanger?: boolean
  /** 当前有效的异常规则命中。 */
  flags?: LegacyEmployeeLogFlag[]
  /** 核查结论；从未核查时为 null，撤销后为 revoked（视同待核查，提交时仍要带版本号）。 */
  review?: LegacyEmployeeLogReview | null
  /** 应收减少金额；扫描任务算不出时为 null。 */
  amountImpact?: number | null
}

export type LegacyRiskLens = 'all' | 'danger' | 'abnormal'
export type LegacyReviewStatus = 'all' | 'pending' | 'reviewed' | 'followUp'
export type LegacyRuleCode =
  | 'noSaleDrawer'
  | 'deleteAfterCheckout'
  | 'bigDiscount'
  | 'burstDelete'
  | 'repeatReprint'
  | 'offHours'

export interface LegacyEmployeeLogFlag {
  ruleCode: LegacyRuleCode | string
  /** 按规则约定键名的依据，时间为门店墙钟 HH:mm:ss。 */
  evidence: Record<string, string>
  detectedAtUtc: string
}

export interface LegacyEmployeeLogReview {
  result: 'normal' | 'followUp' | 'revoked'
  note?: string | null
  reviewedByName: string
  reviewedAtUtc: string
  version: number
}

export interface LegacyEmployeeLogRuleCount {
  ruleCode: string
  count: number
}

export interface LegacyEmployeeLogRiskSummary {
  dangerTotal: number
  abnormalTotal: number
  pendingReview: number
  abnormalEmployees: number
  abnormalByRule: LegacyEmployeeLogRuleCount[]
}

export interface LegacyEmployeeLogEmployeeSummary {
  employeeId?: string | null
  employeeName?: string | null
  storeCodes: string[]
  deviceCodes: string[]
  total: number
  dangerCount: number
  abnormalCount: number
  pendingReview: number
  abnormalByRule: LegacyEmployeeLogRuleCount[]
  amountImpact: number
}

export interface LegacyEmployeeLogEmployeeSummaryResult {
  employees: LegacyEmployeeLogEmployeeSummary[]
  total: number
  dangerTotal: number
}

export interface LegacyEmployeeLogEmployeeSummaryParams {
  storeCodes: string[]
  from: string
  to: string
  deviceCode?: string
}

export interface LegacyEmployeeLogReviewRequest {
  logId: string
  result: 'normal' | 'followUp' | 'revoked'
  note?: string
  expectedVersion?: number | null
}

export interface LegacyEmployeeLogOperationCount {
  operation?: string | null
  count: number
}

export interface LegacyEmployeeLogEmployeeOption {
  employeeId?: string | null
  employeeName?: string | null
  count: number
}

export interface LegacyEmployeeLogDeviceOption {
  deviceCode?: string | null
  count: number
}

export interface LegacyEmployeeLogListResult {
  items: LegacyEmployeeLogItem[]
  total: number
  pageNumber: number
  pageSize: number
  operationCounts: LegacyEmployeeLogOperationCount[]
  employees: LegacyEmployeeLogEmployeeOption[]
  devices: LegacyEmployeeLogDeviceOption[]
  /** 三个风险入口的计数（与 operationCounts 同口径）；旧后端没有该字段。 */
  riskSummary?: LegacyEmployeeLogRiskSummary
}

export interface LegacyEmployeeLogContext {
  target: LegacyEmployeeLogItem
  windowMinutes: number
  neighbors: LegacyEmployeeLogItem[]
  truncated: boolean
}

export interface LegacyEmployeeLogQueryParams {
  /** 至少一个；数组按重复键展开为 storeCodes=a&storeCodes=b */
  storeCodes: string[]
  /** 墙钟时间 YYYY-MM-DDTHH:mm:ss，半开区间 [from, to) */
  from: string
  to: string
  deviceCode?: string
  employeeIds?: string[]
  operations?: string[]
  keyword?: string
  pageNumber: number
  pageSize: number
  sortOrder: 'asc' | 'desc'
  riskLens?: LegacyRiskLens
  ruleCodes?: string[]
  reviewStatus?: LegacyReviewStatus
}
