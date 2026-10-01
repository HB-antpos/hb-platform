import type {
  LegacyEmployeeLogFlag,
  LegacyEmployeeLogReview,
  LegacyEmployeeLogRiskSummary,
  LegacyReviewStatus,
  LegacyRiskLens,
  LegacyRuleCode,
} from './legacyEmployeeLog'

export type OperationAuditOutcome = 'Succeeded' | 'Denied' | 'Failed'
export type OperationAuditDeviceSystem = 'Windows' | 'iPadOS' | 'Unknown'
export type OperationAuditSortField =
  | 'occurredAtUtc'
  | 'storeCode'
  | 'operationType'
  | 'amountDelta'
  | 'deviceCode'
  | 'outcome'
export type OperationAuditSortOrder = 'asc' | 'desc'

/** 风险入口、核查状态与老收银共用同一套取值（后端规则编号也相同）。 */
export type OperationAuditRiskLens = LegacyRiskLens
export type OperationAuditReviewStatus = LegacyReviewStatus
/** 新收银比老收银多一条「紧急覆盖」规则。 */
export type OperationAuditRuleCode = LegacyRuleCode | 'emergencyOverride'

export interface OperationAuditQueryParams {
  fromUtc: string
  toUtc: string
  storeCode?: string
  /** 分店多选；数组按重复键展开为 storeCodes=a&storeCodes=b。 */
  storeCodes?: string[]
  cashierKeyword?: string
  /** 收银员编号精确筛选（从按员工汇总下钻）。 */
  cashierId?: string
  deviceCode?: string
  // Unknown 表示服务端历史记录尚未写入设备平台。
  deviceSystem?: OperationAuditDeviceSystem
  operationType?: string
  operationTypes?: string[]
  outcome?: string
  productKeyword?: string
  orderGuid?: string
  keyword?: string
  pageNumber: number
  pageSize: number
  sortBy?: OperationAuditSortField
  sortOrder?: OperationAuditSortOrder
  riskLens?: OperationAuditRiskLens
  ruleCodes?: string[]
  reviewStatus?: OperationAuditReviewStatus
}

export interface OperationAuditListItem {
  eventId: string
  schemaVersion: number
  occurredAtUtc: string
  receivedAtUtc: string
  operationType: string
  outcome: OperationAuditOutcome
  cashierId?: string
  userGuid?: string
  cashierName?: string
  isOfflineCached: boolean
  isEmergencyOverride: boolean
  storeCode: string
  deviceCode: string
  deviceSystem?: string | null
  appVersion?: string
  instanceId?: string
  orderGuid?: string
  receiptNumber?: string
  correlationId?: string
  traceId?: string
  paymentMethod?: string
  reasonCode?: string
  safeMessage?: string
  currencyCode: string
  paymentAmount?: number
  beforeGross?: number
  afterGross?: number
  beforeDiscount?: number
  afterDiscount?: number
  beforeActual?: number
  afterActual?: number
  amountDelta?: number
  productCount: number
  primaryProduct?: string
  /** 危险操作；手动开钱箱算，随收款自动开钱箱不算。 */
  isDanger?: boolean
  /** 当前有效的异常规则命中，依据键名与老收银一致。 */
  flags?: LegacyEmployeeLogFlag[]
  /** 核查结论；未核查时为 null。 */
  review?: LegacyEmployeeLogReview | null
  /** 应收减少金额（删除、改价、折扣、退款）。 */
  amountImpact?: number | null
}

/** 汇总计数：结果类计数忽略结果筛选；风险计数再忽略操作类型与风险入口。 */
export interface OperationAuditSummary extends LegacyEmployeeLogRiskSummary {
  total: number
  succeeded: number
  denied: number
  failed: number
  emergencyOverride: number
  offlineCached: number
}

export interface OperationAuditEmployeeSummaryRow {
  cashierId?: string | null
  cashierName?: string | null
  storeCodes: string[]
  deviceCodes: string[]
  total: number
  dangerCount: number
  abnormalCount: number
  pendingReview: number
  abnormalByRule: { ruleCode: string; count: number }[]
  amountImpact: number
}

export interface OperationAuditEmployeeSummaryResult {
  employees: OperationAuditEmployeeSummaryRow[]
  total: number
  dangerTotal: number
}

export interface OperationAuditContext {
  target: OperationAuditListItem
  windowMinutes: number
  neighbors: OperationAuditListItem[]
  truncated: boolean
}

export interface OperationAuditReviewRequest {
  eventId: string
  result: 'normal' | 'followUp' | 'revoked'
  note?: string
  expectedVersion?: number | null
}

export interface OperationAuditDetailItem {
  eventId: string
  lineIndex: number
  productCode?: string
  itemNumber?: string
  referenceCode?: string
  lookupCode?: string
  displayName?: string
  lineKind?: string
  beforeQuantity?: number
  afterQuantity?: number
  quantityDelta?: number
  beforeUnitPrice?: number
  afterUnitPrice?: number
  unitPriceDelta?: number
  beforeDiscountAmount?: number
  afterDiscountAmount?: number
  discountAmountDelta?: number
  beforeGrossAmount?: number
  afterGrossAmount?: number
  grossAmountDelta?: number
  beforeActualAmount?: number
  afterActualAmount?: number
  actualAmountDelta?: number
}

export interface OperationAuditDetail extends OperationAuditListItem {
  propertiesJson?: string
  items: OperationAuditDetailItem[]
}
