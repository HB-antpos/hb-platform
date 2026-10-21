export type LinklySettlementConnectionMode =
  | 'LocalIp'
  | 'CloudDirectSync'
  | 'CloudBackendAsync'

export type LinklySettlementEnvironment = 'Production' | 'Sandbox'
export type LinklySettlementStatus = 'Pending' | 'Succeeded' | 'Failed' | 'Unknown'
export type LinklyProviderSubmissionState = 'NotSubmitted' | 'Submitted' | 'Unknown'
export type LinklyAmountParseStatus = 'Parsed' | 'Missing' | 'Unsupported' | 'Invalid'
export type LinklySettlementSortOrder = 'asc' | 'desc'

export interface LinklySettlementAmountSummary {
  currencyCode?: string | null
  purchaseAmountMinor?: number | null
  purchaseCount?: number | null
  cashOutAmountMinor?: number | null
  cashOutCount?: number | null
  refundAmountMinor?: number | null
  refundCount?: number | null
  totalAmountMinor?: number | null
  totalCount?: number | null
}

export interface LinklySettlementCardTotal extends LinklySettlementAmountSummary {
  cardName: string
}

export interface LinklySettlementListItem {
  id: string
  settlementGuid: string
  storeCode: string
  deviceCode: string
  businessDate: string
  connectionMode: LinklySettlementConnectionMode
  environment: LinklySettlementEnvironment
  status: LinklySettlementStatus
  providerSubmissionState?: LinklyProviderSubmissionState | null
  requestedAtUtc: string
  completedAtUtc?: string | null
  responseCode?: string | null
  responseText?: string | null
  receiptCount: number
  printCount: number
  lastPrintError?: string | null
  receivedAtUtc: string
  updatedAtUtc: string
  amountParseStatus: LinklyAmountParseStatus
  amountSummary?: LinklySettlementAmountSummary | null
}

export interface LinklySettlementDetail extends LinklySettlementListItem {
  providerSessionId?: string | null
  cloudBackendSessionId?: string | null
  firstPrintedAtUtc?: string | null
  lastPrintedAtUtc?: string | null
  clientRevision: string
  cardTotals: LinklySettlementCardTotal[]
  receipts: string[]
}

export interface LinklySettlementFilters {
  businessDateFrom: string
  businessDateTo: string
  storeCode?: string
  deviceCode?: string
  connectionMode?: LinklySettlementConnectionMode
  environment?: LinklySettlementEnvironment
  status?: LinklySettlementStatus
  providerSubmissionState?: LinklyProviderSubmissionState
  keyword?: string
  sortBy: string
  sortOrder: LinklySettlementSortOrder
}

export interface LinklySettlementListQuery extends LinklySettlementFilters {
  pageNumber: number
  pageSize: number
}

export interface LinklySettlementExportResult {
  blob: Blob
  fileName: string
}

/** 筛选用「门店 + 设备」候选，来自已同步的结算记录。 */
export interface LinklySettlementDeviceOption {
  storeCode: string
  deviceCode: string
}
