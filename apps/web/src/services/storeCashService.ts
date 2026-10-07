import type { ApiResponse } from '../types/api'
import type {
  CashBalanceEntry,
  CashContext,
  CashDaily,
  CashDailyQuery,
  CashDepositDetail,
  CashDepositListItem,
  CashDepositListQuery,
  CashExpenseDetail,
  CashExpenseListItem,
  CashExpenseListQuery,
  CashExpenseReviewRequest,
  CashOverview,
  CashOverviewQuery,
  CashOverviewTotals,
  CashPaged,
} from '../types/storeCash'
import request, { unwrapApiData } from '../utils/request'

/**
 * 分店现金管理接口（基础路径 api/react/v1/cash）。响应统一包在 ApiResponse<T> 里：
 * - HTTP 非 2xx 由 request 抛 RequestError，payload 里带 errorCode 与可直接展示的中文 message；
 * - HTTP 200 但 success=false 由 unwrapApiData 抛 RequestError。
 * 这里只做信封解包与数组字段兜底（缺字段时给空数组，避免页面 map 报错），不改写任何金额：
 * 可空金额保持 null，绝不补 0。
 */

export const CASH_API_BASE = '/api/react/v1/cash'

/** 列表接口单页上限（服务端 MaxListLimit）。 */
export const CASH_LIST_MAX_LIMIT = 200

function cashUrl(path: string) {
  return `${CASH_API_BASE}/${path}`
}

function arrayOf<T>(value: T[] | null | undefined): T[] {
  return Array.isArray(value) ? value : []
}

function normalizeContext(raw: CashContext): CashContext {
  return {
    ...raw,
    stores: arrayOf(raw?.stores),
    capabilities: {
      canCreateDeposit: raw?.capabilities?.canCreateDeposit === true,
      canCreateExpense: raw?.capabilities?.canCreateExpense === true,
      canViewAllStores: raw?.capabilities?.canViewAllStores === true,
      canVoid: raw?.capabilities?.canVoid === true,
    },
    dailyCloseConnected: raw?.dailyCloseConnected === true,
  }
}

function normalizeTotals(raw: CashOverviewTotals | undefined): CashOverviewTotals {
  return {
    poolBalance: raw?.poolBalance ?? null,
    inflowCash: raw?.inflowCash ?? null,
    closeVariance: raw?.closeVariance ?? null,
    depositTotal: raw?.depositTotal ?? 0,
    depositCount: raw?.depositCount ?? 0,
    expenseTotal: raw?.expenseTotal ?? 0,
    expenseByCategory: arrayOf(raw?.expenseByCategory),
    uncoveredDayCount: raw?.uncoveredDayCount ?? 0,
    overdueStoreCount: raw?.overdueStoreCount ?? 0,
    flaggedExpenseCount: raw?.flaggedExpenseCount ?? 0,
  }
}

function normalizeOverview(raw: CashOverview): CashOverview {
  return {
    ...raw,
    dailyCloseConnected: raw?.dailyCloseConnected === true,
    t2Restricted: raw?.t2Restricted === true,
    rows: arrayOf(raw?.rows).map((row) => ({
      ...row,
      // 可空字段缺失时按 null（不可算），不能被当成 0。
      poolBalance: row.poolBalance ?? null,
      inflowCash: row.inflowCash ?? null,
      closeVariance: row.closeVariance ?? null,
      expenseByCategory: arrayOf(row.expenseByCategory),
    })),
    totals: normalizeTotals(raw?.totals),
  }
}

function normalizeDaily(raw: CashDaily): CashDaily {
  return {
    ...raw,
    dailyCloseConnected: raw?.dailyCloseConnected === true,
    rows: arrayOf(raw?.rows).map((row) => ({
      ...row,
      devices: arrayOf(row.devices).map((device) => ({ ...device, archives: arrayOf(device.archives) })),
    })),
  }
}

function normalizePaged<T>(raw: CashPaged<T>): CashPaged<T> {
  const items = arrayOf(raw?.items)
  return { items, total: typeof raw?.total === 'number' ? raw.total : items.length }
}

function normalizeDepositDetail(raw: CashDepositDetail): CashDepositDetail {
  return {
    ...raw,
    slips: arrayOf(raw?.slips).map((slip) => ({ ...slip, attachments: arrayOf(slip.attachments) })),
  }
}

function normalizeExpenseDetail(raw: CashExpenseDetail): CashExpenseDetail {
  return { ...raw, attachments: arrayOf(raw?.attachments) }
}

type Envelope<T> = ApiResponse<T> | T

export async function getCashContext(signal?: AbortSignal): Promise<CashContext> {
  const payload = await request<Envelope<CashContext>>(cashUrl('context'), { method: 'GET', signal })
  return normalizeContext(unwrapApiData(payload))
}

export async function getCashOverview(query: CashOverviewQuery, signal?: AbortSignal): Promise<CashOverview> {
  const payload = await request<Envelope<CashOverview>>(cashUrl('overview'), {
    method: 'GET',
    signal,
    // storeCodes 按重复参数传（storeCodes=S001&storeCodes=S002）；空数组会被 request 丢弃 = 全部可见分店。
    params: { from: query.from, to: query.to, storeCodes: query.storeCodes },
  })
  return normalizeOverview(unwrapApiData(payload))
}

export async function getCashDaily(query: CashDailyQuery, signal?: AbortSignal): Promise<CashDaily> {
  const payload = await request<Envelope<CashDaily>>(cashUrl('daily'), {
    method: 'GET',
    signal,
    params: { storeCode: query.storeCode, from: query.from, to: query.to },
  })
  return normalizeDaily(unwrapApiData(payload))
}

export async function listCashDeposits(
  query: CashDepositListQuery,
  signal?: AbortSignal,
): Promise<CashPaged<CashDepositListItem>> {
  const payload = await request<Envelope<CashPaged<CashDepositListItem>>>(cashUrl('deposits'), {
    method: 'GET',
    signal,
    params: {
      storeCode: query.storeCode,
      from: query.from,
      to: query.to,
      includeVoided: query.includeVoided ?? false,
      limit: query.limit,
      offset: query.offset,
    },
  })
  return normalizePaged(unwrapApiData(payload))
}

export async function getCashDeposit(depositGuid: string, signal?: AbortSignal): Promise<CashDepositDetail> {
  const payload = await request<Envelope<CashDepositDetail>>(
    cashUrl(`deposits/${encodeURIComponent(depositGuid)}`),
    { method: 'GET', signal },
  )
  return normalizeDepositDetail(unwrapApiData(payload))
}

export async function voidCashDeposit(depositGuid: string, reason: string): Promise<CashDepositDetail> {
  const payload = await request<Envelope<CashDepositDetail>>(
    cashUrl(`deposits/${encodeURIComponent(depositGuid)}/void`),
    { method: 'POST', data: { reason } },
  )
  return normalizeDepositDetail(unwrapApiData(payload))
}

export async function listCashExpenses(
  query: CashExpenseListQuery,
  signal?: AbortSignal,
): Promise<CashPaged<CashExpenseListItem>> {
  const payload = await request<Envelope<CashPaged<CashExpenseListItem>>>(cashUrl('expenses'), {
    method: 'GET',
    signal,
    params: {
      storeCode: query.storeCode,
      from: query.from,
      to: query.to,
      category: query.category,
      reviewStatus: query.reviewStatus,
      includeVoided: query.includeVoided ?? false,
      limit: query.limit,
      offset: query.offset,
    },
  })
  return normalizePaged(unwrapApiData(payload))
}

export async function getCashExpense(expenseGuid: string, signal?: AbortSignal): Promise<CashExpenseDetail> {
  const payload = await request<Envelope<CashExpenseDetail>>(
    cashUrl(`expenses/${encodeURIComponent(expenseGuid)}`),
    { method: 'GET', signal },
  )
  return normalizeExpenseDetail(unwrapApiData(payload))
}

export async function voidCashExpense(expenseGuid: string, reason: string): Promise<CashExpenseDetail> {
  const payload = await request<Envelope<CashExpenseDetail>>(
    cashUrl(`expenses/${encodeURIComponent(expenseGuid)}/void`),
    { method: 'POST', data: { reason } },
  )
  return normalizeExpenseDetail(unwrapApiData(payload))
}

export async function reviewCashExpense(
  expenseGuid: string,
  body: CashExpenseReviewRequest,
): Promise<CashExpenseDetail> {
  const payload = await request<Envelope<CashExpenseDetail>>(
    cashUrl(`expenses/${encodeURIComponent(expenseGuid)}/review`),
    { method: 'POST', data: { reviewStatus: body.reviewStatus, note: body.note } },
  )
  return normalizeExpenseDetail(unwrapApiData(payload))
}

export async function listCashEntries(
  storeCode: string,
  includeVoided = false,
  signal?: AbortSignal,
): Promise<CashBalanceEntry[]> {
  const payload = await request<Envelope<CashBalanceEntry[]>>(cashUrl('entries'), {
    method: 'GET',
    signal,
    params: { storeCode, includeVoided },
  })
  return arrayOf(unwrapApiData(payload))
}
