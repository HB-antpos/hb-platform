import type { LegacyEmployeeLogEmployeeSummary } from '../../../types/legacyEmployeeLog'
import type {
  OperationAuditDeviceSystem,
  OperationAuditEmployeeSummaryRow,
  OperationAuditQueryParams,
  OperationAuditReviewStatus,
  OperationAuditRiskLens,
  OperationAuditSortField,
  OperationAuditSortOrder,
} from '../../../types/operationAudit'

export type OperationAuditTableSortOrder = 'ascend' | 'descend'

export interface OperationAuditQueryState {
  startUtc: string
  endUtc: string
  storeCode: string
  /** 分店多选；与 storeCode 合并后去重。 */
  storeCodes?: string[]
  cashierKeyword: string
  cashierId?: string
  deviceCode: string
  deviceSystem: string
  operationType: string
  operationTypes?: string[]
  outcome: string
  productKeyword: string
  orderGuid: string
  keyword: string
  page: number
  pageSize: number
  sortBy: OperationAuditSortField
  sortOrder: OperationAuditSortOrder
  riskLens?: OperationAuditRiskLens
  ruleCodes?: string[]
  reviewStatus?: OperationAuditReviewStatus
}

export type { OperationAuditQueryParams }

export const OPERATION_AUDIT_SORT_FIELDS = [
  'occurredAtUtc',
  'storeCode',
  'operationType',
  'amountDelta',
  'deviceCode',
  'outcome',
] as const satisfies readonly OperationAuditSortField[]

export const OPERATION_AUDIT_DEVICE_SYSTEM_OPTIONS = ['Windows', 'iPadOS', 'Unknown'] as const

export const DEFAULT_OPERATION_AUDIT_SORT = {
  sortBy: 'occurredAtUtc',
  sortOrder: 'descend',
} as const satisfies {
  sortBy: OperationAuditSortField
  sortOrder: OperationAuditTableSortOrder
}

export interface OperationAuditTableState {
  page: number
  pageSize: number
  sortBy: OperationAuditSortField
  sortOrder: OperationAuditTableSortOrder
}

export function createLatestOperationAuditRequestGuard() {
  let latestRequestId = 0
  return {
    begin() {
      latestRequestId += 1
      return latestRequestId
    },
    isLatest(requestId: number) {
      return requestId === latestRequestId
    },
  }
}

export function isOperationAuditSortField(value: unknown): value is OperationAuditSortField {
  return (
    typeof value === 'string'
    && OPERATION_AUDIT_SORT_FIELDS.includes(value as OperationAuditSortField)
  )
}

export function resolveOperationAuditTableChange(
  current: OperationAuditTableState,
  change: {
    action: 'paginate' | 'sort'
    page: number
    pageSize: number
    sortBy?: unknown
    sortOrder?: unknown
  },
): OperationAuditTableState {
  if (change.action === 'sort') {
    return {
      page: 1,
      pageSize: change.pageSize,
      sortBy: isOperationAuditSortField(change.sortBy) ? change.sortBy : current.sortBy,
      sortOrder:
        change.sortOrder === 'ascend' || change.sortOrder === 'descend'
          ? change.sortOrder
          : current.sortOrder,
    }
  }

  return {
    page: change.page,
    pageSize: change.pageSize,
    sortBy: current.sortBy,
    sortOrder: current.sortOrder,
  }
}

export const OPERATION_TYPE_KEYS: Record<string, string> = {
  CASHIER_LOGIN: 'operationLogs.operations.cashierLogin',
  CASHIER_LOGOUT: 'operationLogs.operations.cashierLogout',
  CART_ITEM_ADD: 'operationLogs.operations.cartItemAdd',
  CART_ITEM_REMOVE: 'operationLogs.operations.cartItemRemove',
  CART_ITEM_QUANTITY_CHANGE: 'operationLogs.operations.cartItemQuantityChange',
  CART_ITEM_PRICE_CHANGE: 'operationLogs.operations.cartItemPriceChange',
  CART_LINE_DISCOUNT_CHANGE: 'operationLogs.operations.cartLineDiscountChange',
  CART_ORDER_DISCOUNT_CHANGE: 'operationLogs.operations.cartOrderDiscountChange',
  CART_CLEAR: 'operationLogs.operations.cartClear',
  ORDER_HOLD: 'operationLogs.operations.orderHold',
  ORDER_RECALL: 'operationLogs.operations.orderRecall',
  ORDER_CANCEL: 'operationLogs.operations.orderCancel',
  CASH_DRAWER_OPEN: 'operationLogs.operations.cashDrawerOpen',
  PAYMENT_TENDER_ADD: 'operationLogs.operations.paymentTenderAdd',
  PAYMENT_TENDER_REMOVE: 'operationLogs.operations.paymentTenderRemove',
  PAYMENT_CANCEL: 'operationLogs.operations.paymentCancel',
  SALE_COMPLETE: 'operationLogs.operations.saleComplete',
  RETURN_REFUND_COMPLETE: 'operationLogs.operations.returnRefundComplete',
  SALE_VOID: 'operationLogs.operations.saleVoid',
  RECEIPT_REPRINT: 'operationLogs.operations.receiptReprint',
  INSTALLMENT_REPAYMENT_COMPLETE: 'operationLogs.operations.installmentRepaymentComplete',
  INSTALLMENT_REPAYMENT_CANCEL: 'operationLogs.operations.installmentRepaymentCancel',
  DAILY_CLOSE_SAVE: 'operationLogs.operations.dailyCloseSave',
  DAILY_CLOSE_REPRINT: 'operationLogs.operations.dailyCloseReprint',
  LINKLY_SETTLEMENT: 'operationLogs.operations.linklySettlement',
  LINKLY_SETTLEMENT_REPRINT: 'operationLogs.operations.linklySettlementReprint',
  CARD_PAYMENT_SUPERVISOR_RESOLUTION: 'operationLogs.operations.cardPaymentSupervisorResolution',
  PERMISSION_OVERRIDE: 'operationLogs.operations.permissionOverride',
  INSTALLMENT_PICKUP_CONFIRM: 'operationLogs.operations.installmentPickupConfirm',
  INSTALLMENT_LINES_AMEND: 'operationLogs.operations.installmentLinesAmend',
  CATALOG_RESET: 'operationLogs.operations.catalogReset',
  TEST_SALES_DATA_RESET: 'operationLogs.operations.testSalesDataReset',
  DEVICE_REREGISTER: 'operationLogs.operations.deviceReregister',
  API_SERVER_CHANGE: 'operationLogs.operations.apiServerChange',
  REMOTE_MAINTENANCE_INSTALL: 'operationLogs.operations.remoteMaintenanceInstall',
}

export function normalizeOperationAuditPage<T>(payload: {
  items?: T[]
  total?: number
  totalCount?: number
  pageNumber?: number
  page?: number
  pageIndex?: number
  pageSize?: number
}) {
  return {
    items: payload.items ?? [],
    total: payload.total ?? payload.totalCount ?? 0,
    pageNumber: payload.pageNumber ?? payload.page ?? payload.pageIndex ?? 1,
    pageSize: payload.pageSize ?? 20,
  }
}

export function buildOperationAuditQuery(_state: OperationAuditQueryState): OperationAuditQueryParams {
  const trim = (value: string | undefined) => value?.trim() || undefined
  const deviceSystem = trim(_state.deviceSystem)
  const unique = (values: readonly (string | undefined)[] | undefined) =>
    [...new Set((values ?? []).map((value) => value?.trim()).filter((value): value is string => Boolean(value)))]
  const storeCodes = unique(_state.storeCodes)
  const operationTypes = unique(_state.operationTypes)
  const lens = _state.riskLens ?? 'all'
  return {
    fromUtc: _state.startUtc,
    toUtc: _state.endUtc,
    storeCode: trim(_state.storeCode),
    // 多选分店与操作类型按重复键展开；空数组不传，保持与旧后端兼容。
    ...(storeCodes.length ? { storeCodes } : {}),
    cashierKeyword: trim(_state.cashierKeyword),
    ...(trim(_state.cashierId) ? { cashierId: trim(_state.cashierId) } : {}),
    deviceCode: trim(_state.deviceCode),
    deviceSystem: OPERATION_AUDIT_DEVICE_SYSTEM_OPTIONS.includes(
      deviceSystem as OperationAuditDeviceSystem,
    ) ? deviceSystem as OperationAuditDeviceSystem : undefined,
    operationType: trim(_state.operationType),
    ...(operationTypes.length ? { operationTypes } : {}),
    outcome: trim(_state.outcome),
    productKeyword: trim(_state.productKeyword),
    orderGuid: trim(_state.orderGuid),
    keyword: trim(_state.keyword),
    pageNumber: _state.page,
    pageSize: _state.pageSize,
    sortBy: _state.sortBy,
    sortOrder: _state.sortOrder,
    // 默认入口不传；规则与核查状态只在「异常」入口下有意义。
    ...(lens !== 'all' ? { riskLens: lens } : {}),
    ...(lens === 'abnormal' && _state.ruleCodes?.length ? { ruleCodes: [..._state.ruleCodes] } : {}),
    ...(lens === 'abnormal' && _state.reviewStatus && _state.reviewStatus !== 'all' ? { reviewStatus: _state.reviewStatus } : {}),
  }
}

export function formatSignedMoney(_value: number | null | undefined, _currencyCode: string): string {
  if (_value === null || _value === undefined || Number.isNaN(_value)) return '-'
  const symbol = _currencyCode.toUpperCase() === 'AUD' ? '$' : `${_currencyCode.toUpperCase()} `
  const sign = _value > 0 ? '+' : _value < 0 ? '-' : ''
  return `${sign}${symbol}${Math.abs(_value).toFixed(2)}`
}

export function formatMoney(_value: number | null | undefined, _currencyCode: string): string {
  if (_value === null || _value === undefined || Number.isNaN(_value)) return '-'
  const symbol = _currencyCode.toUpperCase() === 'AUD' ? '$' : `${_currencyCode.toUpperCase()} `
  const sign = _value < 0 ? '-' : ''
  return `${sign}${symbol}${Math.abs(_value).toFixed(2)}`
}

type ProductSummarySource = {
  productCount: number
  primaryProductName?: string | null
  primaryProduct?: string | null
  primaryItemNumber?: string | null
} | null | undefined

/** 主商品名 + 剩余数量（不含货号），供图文两行的商品摘要使用；没有商品返回 '-'。 */
export function summarizeProductName(_summary: ProductSummarySource, _fallback = 'Product'): string {
  if (!_summary || _summary.productCount <= 0) return '-'
  const name = _summary.primaryProductName?.trim() || _summary.primaryProduct?.trim() || _fallback
  const remaining = Math.max(0, _summary.productCount - 1)
  return remaining > 0 ? `${name} +${remaining}` : name
}

/** 主商品的货号；与商品名相同（收银端拿货号当名称）时不重复显示。 */
export function primaryItemNumberOf(_summary: ProductSummarySource): string | undefined {
  const itemNumber = _summary?.primaryItemNumber?.trim()
  if (!_summary || _summary.productCount <= 0 || !itemNumber) return undefined
  const name = _summary.primaryProductName?.trim() || _summary.primaryProduct?.trim()
  return itemNumber === name ? undefined : itemNumber
}

/** 单行文字摘要（时间线等）：货号紧跟主商品名，“+N”仍放最后，避免误读成货号属于其余商品。 */
export function summarizeProducts(_summary: ProductSummarySource, _fallback = 'Product'): string {
  if (!_summary || _summary.productCount <= 0) return '-'
  const name = _summary.primaryProductName?.trim() || _summary.primaryProduct?.trim() || _fallback
  const itemNumber = primaryItemNumberOf(_summary)
  const primary = itemNumber ? `${name} (${itemNumber})` : name
  const remaining = Math.max(0, _summary.productCount - 1)
  return remaining > 0 ? `${primary} +${remaining}` : primary
}

export function buildSystemLogLink(_input: {
  deviceCode?: string | null
  deviceSystem?: string | null
  traceId?: string | null
  occurredAtUtc: string
}): string | undefined {
  const occurredAt = new Date(_input.occurredAtUtc)
  const projectCode = _input.deviceSystem === 'Windows'
    ? 'hbpos_win'
    : _input.deviceSystem === 'iPadOS'
      ? 'hbpos_ipad'
      : undefined
  const traceId = _input.traceId?.trim()
  // 历史记录没有可信平台和 Trace 时，不能猜测 WPF 项目并跳转到错误日志范围。
  if (!projectCode && !traceId) return undefined
  const params = new URLSearchParams()
  if (projectCode) params.set('projectCode', projectCode)
  if (projectCode && _input.deviceCode?.trim()) params.set('deviceCode', _input.deviceCode.trim())
  if (traceId) params.set('traceId', traceId)
  if (!Number.isNaN(occurredAt.valueOf())) {
    params.set('fromUtc', new Date(occurredAt.valueOf() - 5 * 60 * 1000).toISOString())
    params.set('toUtc', new Date(occurredAt.valueOf() + 5 * 60 * 1000).toISOString())
  }
  return `/system/center-logs?${params.toString()}`
}

/** 新收银按收银员汇总的一行转成老收银员工汇总的形状，复用同一张汇总表。 */
export function toLegacyEmployeeSummary(row: OperationAuditEmployeeSummaryRow): LegacyEmployeeLogEmployeeSummary {
  return {
    employeeId: row.cashierId ?? null,
    employeeName: row.cashierName ?? null,
    storeCodes: row.storeCodes,
    deviceCodes: row.deviceCodes,
    total: row.total,
    dangerCount: row.dangerCount,
    abnormalCount: row.abnormalCount,
    pendingReview: row.pendingReview,
    abnormalByRule: row.abnormalByRule,
    amountImpact: row.amountImpact,
  }
}
