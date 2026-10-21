import { matchPath } from 'react-router-dom'
import type {
  LinklyAmountParseStatus,
  LinklySettlementAmountSummary,
  LinklySettlementDeviceOption,
  LinklySettlementFilters,
  LinklySettlementListQuery,
  LinklySettlementStatus,
  LinklyProviderSubmissionState,
} from '../../../types/linklySettlement'

export const DEFAULT_LINKLY_SETTLEMENT_PAGE_SIZE = 20
export const DEFAULT_LINKLY_SETTLEMENT_SORT = {
  sortBy: 'requestedAtUtc',
  sortOrder: 'desc',
} as const

export type SettlementTagColor = 'success' | 'warning' | 'error' | 'processing' | 'default'

export function formatLocalCalendarDate(date = new Date()): string {
  const year = date.getFullYear()
  const month = String(date.getMonth() + 1).padStart(2, '0')
  const day = String(date.getDate()).padStart(2, '0')
  return `${year}-${month}-${day}`
}

export function getDefaultLinklySettlementDateRange(date = new Date()): [string, string] {
  const today = formatLocalCalendarDate(date)
  return [today, today]
}

function trimOrUndefined(value?: string) {
  return value?.trim() || undefined
}

export function buildLinklySettlementQuery(
  filters: LinklySettlementFilters,
  pageNumber: number,
  pageSize: number,
): LinklySettlementListQuery {
  return {
    businessDateFrom: filters.businessDateFrom,
    businessDateTo: filters.businessDateTo,
    storeCode: trimOrUndefined(filters.storeCode),
    deviceCode: trimOrUndefined(filters.deviceCode),
    connectionMode: filters.connectionMode,
    environment: filters.environment,
    status: filters.status,
    providerSubmissionState: filters.providerSubmissionState,
    keyword: trimOrUndefined(filters.keyword),
    sortBy: filters.sortBy,
    sortOrder: filters.sortOrder,
    pageNumber,
    pageSize,
  }
}

export function normalizeLinklySettlementPage<T>(payload: {
  items?: T[]
  total?: number
  totalCount?: number
  page?: number
  pageIndex?: number
  pageNumber?: number
  pageSize?: number
}) {
  return {
    items: payload.items ?? [],
    total: payload.total ?? payload.totalCount ?? 0,
    pageNumber: payload.pageNumber ?? payload.page ?? payload.pageIndex ?? 1,
    pageSize: payload.pageSize ?? DEFAULT_LINKLY_SETTLEMENT_PAGE_SIZE,
  }
}

export function formatAmountMinor(
  amountMinor: number | null | undefined,
  parseStatus: LinklyAmountParseStatus,
): string {
  if (parseStatus !== 'Parsed' || amountMinor === null || amountMinor === undefined || !Number.isFinite(amountMinor)) {
    return '--'
  }

  return new Intl.NumberFormat('en-AU', {
    style: 'currency',
    currency: 'AUD',
    minimumFractionDigits: 2,
    maximumFractionDigits: 2,
  }).format(amountMinor / 100)
}

export function getAmountMinor(
  summary: LinklySettlementAmountSummary | null | undefined,
  field: keyof Pick<
    LinklySettlementAmountSummary,
    'purchaseAmountMinor' | 'refundAmountMinor' | 'cashOutAmountMinor' | 'totalAmountMinor'
  >,
) {
  return summary?.[field]
}

export function getSettlementStatusColor(status: LinklySettlementStatus): SettlementTagColor {
  switch (status) {
    case 'Succeeded': return 'success'
    case 'Pending': return 'processing'
    case 'Failed': return 'error'
    case 'Unknown': return 'warning'
    default: return 'default'
  }
}

export function getProviderSubmissionColor(
  state?: LinklyProviderSubmissionState | null,
): SettlementTagColor {
  switch (state) {
    case 'Submitted': return 'success'
    case 'NotSubmitted': return 'error'
    case 'Unknown': return 'warning'
    default: return 'default'
  }
}

export function getAmountParseStatusColor(status: LinklyAmountParseStatus): SettlementTagColor {
  switch (status) {
    case 'Parsed': return 'success'
    case 'Missing': return 'default'
    case 'Unsupported': return 'warning'
    case 'Invalid': return 'error'
    default: return 'default'
  }
}

function parseCalendarDate(value: string) {
  const match = /^(\d{4})-(\d{2})-(\d{2})$/.exec(value)
  if (!match) return null
  const utc = Date.UTC(Number(match[1]), Number(match[2]) - 1, Number(match[3]))
  const date = new Date(utc)
  if (
    date.getUTCFullYear() !== Number(match[1])
    || date.getUTCMonth() !== Number(match[2]) - 1
    || date.getUTCDate() !== Number(match[3])
  ) return null
  return utc
}

export function getInclusiveCalendarDayCount(from: string, to: string): number | null {
  const fromUtc = parseCalendarDate(from)
  const toUtc = parseCalendarDate(to)
  if (fromUtc === null || toUtc === null || toUtc < fromUtc) return null
  return Math.floor((toUtc - fromUtc) / 86_400_000) + 1
}

export function canExportLinklySettlementRange(from: string, to: string) {
  const days = getInclusiveCalendarDayCount(from, to)
  return days !== null && days <= 31
}

export function getValidLinklySettlementRouteId(value: string | undefined): string | null {
  // 路由 ID 必须保持原始十进制文本，避免 BIGINT 经 Number 转换后丢失精度。
  return value && /^0*[1-9]\d*$/.test(value) ? value : null
}

export function getLinklySettlementRouteIdFromPathname(pathname: string): string | null {
  // AdminLayout 手动解析并渲染路由元素，不会向页面注入 useParams 上下文。
  const match = matchPath('/pos-admin/linkly-settlements/:id', pathname)
  return getValidLinklySettlementRouteId(match?.params.id)
}

export interface LatestAbortableRequest {
  requestId: number
  signal: AbortSignal
}

export function createLatestAbortableRequestGuard() {
  let latestRequestId = 0
  let controller: AbortController | null = null

  return {
    begin(): LatestAbortableRequest {
      controller?.abort()
      controller = new AbortController()
      latestRequestId += 1
      return { requestId: latestRequestId, signal: controller.signal }
    },
    isLatest(requestId: number) {
      return requestId === latestRequestId
    },
    abort() {
      controller?.abort()
      controller = null
      latestRequestId += 1
    },
  }
}

/**
 * 设备下拉候选：已选门店时只列该门店的设备，未选门店时列全部（去重、排序）。
 * 选项文案带上门店，方便未选门店时区分同名设备。
 */
export function buildLinklyDeviceSelectOptions(
  devices: LinklySettlementDeviceOption[],
  storeCode?: string,
) {
  const seen = new Set<string>()
  return devices
    .filter((item) => !storeCode || item.storeCode === storeCode)
    .filter((item) => {
      if (!item.deviceCode || seen.has(item.deviceCode)) return false
      seen.add(item.deviceCode)
      return true
    })
    .sort((left, right) => left.deviceCode.localeCompare(right.deviceCode))
    .map((item) => ({
      value: item.deviceCode,
      label: storeCode ? item.deviceCode : `${item.deviceCode} · ${item.storeCode}`,
    }))
}
