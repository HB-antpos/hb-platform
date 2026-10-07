import type { ApiResponse } from '../types/api'
import type {
  DailyCloseCashCount,
  DailyCloseCounts,
  DailyCloseDetail,
  DailyCloseListItem,
  DailyCloseListQuery,
  DailyCloseListResult,
  DailyCloseTender,
  DailyCloseTotals,
} from '../types/dailyClose'
import request, { unwrapApiData } from '../utils/request'
import { classifyDifference, isDifferenceKind } from '../pages/PosAdmin/DailyCloses/logic'

// 日结记录只读接口：列表 + 详情。返回 ApiResponse 信封，字段 camelCase；权限 DailyCloseRecords.View。
const API_BASE = '/api/react/v1/pos-daily-closes'

function toNumberOrNull(value: unknown): number | null {
  if (value === null || value === undefined || value === '') return null
  const parsed = typeof value === 'number' ? value : Number(value)
  return Number.isFinite(parsed) ? parsed : null
}

function toNumber(value: unknown, fallback = 0): number {
  return toNumberOrNull(value) ?? fallback
}

function toStringOrNull(value: unknown): string | null {
  return typeof value === 'string' && value.trim() ? value : null
}

/** 营业日只取日期部分，兼容后端偶发带 T00:00:00 的序列化。 */
function normalizeBusinessDate(value: unknown): string {
  return typeof value === 'string' ? value.slice(0, 10) : ''
}

/** 逐字段兜底：后端缺字段或给 null 时界面不崩，差额状态缺失时按差额符号推算。 */
export function normalizeDailyCloseItem(raw: Partial<DailyCloseListItem> | null | undefined): DailyCloseListItem {
  const source = raw ?? {}
  const cashDifference = toNumberOrNull(source.cashDifference)
  return {
    dailyCloseGuid: String(source.dailyCloseGuid ?? ''),
    storeCode: String(source.storeCode ?? ''),
    storeName: toStringOrNull(source.storeName),
    storeTimeZoneId: toStringOrNull(source.storeTimeZoneId),
    deviceCode: String(source.deviceCode ?? ''),
    clientKind: String(source.clientKind ?? ''),
    detailLevel: String(source.detailLevel ?? 'Full'),
    dataSource: String(source.dataSource ?? 'ClientUpload'),
    businessDate: normalizeBusinessDate(source.businessDate),
    businessDateInferred: Boolean(source.businessDateInferred),
    cashierId: String(source.cashierId ?? ''),
    cashierName: String(source.cashierName ?? ''),
    savedAtUtc: String(source.savedAtUtc ?? ''),
    orderCount: toNumberOrNull(source.orderCount),
    expectedCashAmount: toNumberOrNull(source.expectedCashAmount),
    countedCashAmount: toNumberOrNull(source.countedCashAmount),
    cashDifference,
    cardNetAmount: toNumberOrNull(source.cardNetAmount),
    differenceKind: isDifferenceKind(source.differenceKind) ? source.differenceKind : classifyDifference(cashDifference),
    saveSequence: toNumber(source.saveSequence, 1),
    saveCountInDay: toNumber(source.saveCountInDay, 1),
  }
}

function normalizeCounts(raw: Partial<DailyCloseCounts> | null | undefined): DailyCloseCounts {
  const counts = {
    short: toNumber(raw?.short),
    over: toNumber(raw?.over),
    even: toNumber(raw?.even),
    none: toNumber(raw?.none),
  }
  return { ...counts, all: toNumber(raw?.all, counts.short + counts.over + counts.even + counts.none) }
}

function normalizeTotals(raw: Partial<DailyCloseTotals> | null | undefined): DailyCloseTotals {
  return {
    expectedCash: toNumber(raw?.expectedCash),
    countedCash: toNumber(raw?.countedCash),
    difference: toNumber(raw?.difference),
  }
}

export function normalizeDailyCloseList(raw: Partial<DailyCloseListResult> | null | undefined): DailyCloseListResult {
  const source = raw ?? {}
  return {
    items: (source.items ?? []).map(normalizeDailyCloseItem),
    total: toNumber(source.total),
    page: toNumber(source.page, 1),
    pageSize: toNumber(source.pageSize, 20),
    counts: normalizeCounts(source.counts),
    totals: normalizeTotals(source.totals),
  }
}

function normalizeTender(raw: Partial<DailyCloseTender>): DailyCloseTender {
  return {
    method: String(raw.method ?? ''),
    salesAmount: toNumber(raw.salesAmount),
    refundAmount: toNumber(raw.refundAmount),
    netAmount: toNumber(raw.netAmount),
  }
}

function normalizeCashCount(raw: Partial<DailyCloseCashCount>): DailyCloseCashCount {
  return {
    denominationCents: toNumber(raw.denominationCents),
    quantity: toNumber(raw.quantity),
    subtotalAmount: toNumber(raw.subtotalAmount),
    kind: String(raw.kind ?? ''),
  }
}

export function normalizeDailyCloseDetail(raw: Partial<DailyCloseDetail> | null | undefined): DailyCloseDetail {
  const source = raw ?? {}
  return {
    ...normalizeDailyCloseItem(source),
    periodFromUtc: toStringOrNull(source.periodFromUtc),
    periodToUtc: toStringOrNull(source.periodToUtc),
    appVersion: toStringOrNull(source.appVersion),
    returnQuantity: toNumberOrNull(source.returnQuantity),
    refundAmount: toNumberOrNull(source.refundAmount),
    tenders: (source.tenders ?? []).map(normalizeTender),
    cashCounts: (source.cashCounts ?? []).map(normalizeCashCount),
    noteSubtotal: toNumberOrNull(source.noteSubtotal),
    coinSubtotal: toNumberOrNull(source.coinSubtotal),
    receivedAtUtc: String(source.receivedAtUtc ?? ''),
  }
}

export async function getDailyCloses(query: DailyCloseListQuery, signal?: AbortSignal): Promise<DailyCloseListResult> {
  const response = await request.get<ApiResponse<DailyCloseListResult>>(API_BASE, {
    params: query as unknown as Record<string, unknown>,
    signal,
  })
  return normalizeDailyCloseList(unwrapApiData(response))
}

export async function getDailyCloseDetail(dailyCloseGuid: string, signal?: AbortSignal): Promise<DailyCloseDetail> {
  const response = await request.get<ApiResponse<DailyCloseDetail>>(
    `${API_BASE}/${encodeURIComponent(dailyCloseGuid)}`,
    { signal },
  )
  return normalizeDailyCloseDetail(unwrapApiData(response))
}
