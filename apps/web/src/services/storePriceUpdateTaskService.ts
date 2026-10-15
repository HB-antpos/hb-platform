import type { ApiResponse } from '../types/api'
import request, { unwrapApiData } from '../utils/request'
import type { PriceNotificationPreview } from '../utils/priceNotification'

const API_BASE = '/api/react/v1/store-price-update-tasks'

export type StorePriceUpdateTaskKind = 'PriceUpdate' | 'LabelOnly'
export type StorePriceUpdateTaskStatus = 'Pending' | 'Completed' | 'Cancelled'
export type StorePriceUpdateCompletionMode = 'Printed' | 'MarkedReplaced' | 'KeptStorePrice' | 'PriceAligned'
export type StorePriceUpdateStoreState = 'Completed' | 'LabelOnly' | 'PriceUpdate' | 'Skipped'
export type SuggestedDiscountSource = 'WarehouseProducts' | 'MobileWarehouse' | 'BatchUpdate'

export interface StorePriceUpdateTask {
  id: number
  storeCode: string
  storeName?: string | null
  productCode: string
  productName?: string | null
  itemNumber?: string | null
  barcode?: string | null
  productImage?: string | null
  status: StorePriceUpdateTaskStatus
  kind: StorePriceUpdateTaskKind
  changedFields?: string[]
  shelfRetailPrice?: number | null
  shelfDiscountRate?: number | null
  storeRetailPrice?: number | null
  storeDiscountRate?: number | null
  targetRetailPrice?: number | null
  targetDiscountRate?: number | null
  initiatorName: string
  initiatorSource: string
  initiatorReference?: string | null
  initiatedAtUtc: string
  changeCount: number
  priceAppliedBy?: string | null
  priceAppliedAtUtc?: string | null
  completionMode?: StorePriceUpdateCompletionMode | null
  completedBy?: string | null
  completedAtUtc?: string | null
  labelPrintCount: number
  /** Reverted / NoLongerApplicable（系统自动取消）/ ManualCancel（Web 手动撤销）；仅已取消任务有值。 */
  cancelReason?: string | null
  cancelledBy?: string | null
  cancelledAtUtc?: string | null
  cancelNote?: string | null
  hqSyncOperationId?: string | null
  hqSyncStatus?: string | null
}

export interface StorePriceUpdateTaskPage {
  items: StorePriceUpdateTask[]
  total: number
  page: number
  pageSize: number
  pendingCount: number
  pendingPriceUpdateCount: number
  pendingLabelOnlyCount: number
  completedCount: number
  hqSyncEnabled: boolean
}

export interface StorePriceUpdateTaskSummary {
  pendingCount: number
  pendingPriceUpdateCount: number
  pendingLabelOnlyCount: number
  completedCount: number
  completionRate: number
  overdueCount: number
  overdueStoreCount: number
  overdueDays: number
  hqSyncFailedCount: number
  hqSyncEnabled: boolean
}

export interface StorePriceUpdateTaskStoreRow {
  storeCode: string
  storeName?: string | null
  pendingCount: number
  pendingPriceUpdateCount: number
  pendingLabelOnlyCount: number
  completedCount: number
  completionRate: number
  oldestPendingAtUtc?: string | null
  lastCompletedBy?: string | null
  lastCompletedAtUtc?: string | null
}

export interface StorePriceUpdateTaskProductStore {
  storeCode: string
  storeName?: string | null
  state: StorePriceUpdateStoreState
  shelfRetailPrice?: number | null
  storeRetailPrice?: number | null
  storeDiscountRate?: number | null
  completionMode?: StorePriceUpdateCompletionMode | null
  completedBy?: string | null
  completedAtUtc?: string | null
  initiatedAtUtc?: string | null
  hqSyncStatus?: string | null
}

export interface StorePriceUpdateTaskProductRow {
  productCode: string
  productName?: string | null
  itemNumber?: string | null
  productImage?: string | null
  targetRetailPrice?: number | null
  targetDiscountRate?: number | null
  initiatorName: string
  initiatorSource: string
  initiatorReference?: string | null
  initiatedAtUtc: string
  changeCount: number
  storeCount: number
  completedStoreCount: number
  stores: StorePriceUpdateTaskProductStore[]
}

export interface StorePriceUpdateTaskProductPage {
  items: StorePriceUpdateTaskProductRow[]
  total: number
  page: number
  pageSize: number
}

export interface SuggestedDiscountItem {
  productCode: string
  suggestedDiscountRate: number | null
}

interface ReadOptions {
  signal?: AbortSignal
}

type Query = Record<string, unknown>

export async function getStorePriceUpdateTaskSummary(params: Query, options: ReadOptions = {}) {
  const response = await request.get<ApiResponse<StorePriceUpdateTaskSummary>>(`${API_BASE}/summary`, { params, signal: options.signal })
  return unwrapApiData(response)
}

export async function getStorePriceUpdateTasksByStore(params: Query, options: ReadOptions = {}) {
  const response = await request.get<ApiResponse<StorePriceUpdateTaskStoreRow[]>>(`${API_BASE}/by-store`, { params, signal: options.signal })
  const rows = unwrapApiData(response)
  return Array.isArray(rows) ? rows : []
}

export async function getStorePriceUpdateTasksByProduct(params: Query, options: ReadOptions = {}): Promise<StorePriceUpdateTaskProductPage> {
  const response = await request.get<ApiResponse<StorePriceUpdateTaskProductPage>>(`${API_BASE}/by-product`, { params, signal: options.signal })
  const page = unwrapApiData(response)
  return {
    items: Array.isArray(page?.items) ? page.items.map((item) => ({ ...item, stores: Array.isArray(item.stores) ? item.stores : [] })) : [],
    total: Number(page?.total ?? 0),
    page: Number(page?.page ?? 1),
    pageSize: Number(page?.pageSize ?? 30),
  }
}

export async function getStorePriceUpdateTasks(params: Query, options: ReadOptions = {}): Promise<StorePriceUpdateTaskPage> {
  const response = await request.get<ApiResponse<StorePriceUpdateTaskPage>>(`${API_BASE}/tasks`, { params, signal: options.signal })
  const page = unwrapApiData(response)
  return {
    ...page,
    items: Array.isArray(page?.items) ? page.items : [],
    total: Number(page?.total ?? 0),
    page: Number(page?.page ?? 1),
    pageSize: Number(page?.pageSize ?? 30),
    hqSyncEnabled: page?.hqSyncEnabled === true,
  }
}

/** 撤销入参二选一：任务 ID（至多 200 个）或商品编码（可用 storeCodes 限定分店）。 */
export type CancelStorePriceUpdateTasksPayload =
  | { taskIds: number[]; note?: string }
  | { productCode: string; storeCodes?: string; note?: string }

export interface CancelStorePriceUpdateTasksResult {
  items: { taskId: number; success: boolean; code: string; message?: string | null }[]
  cancelledCount: number
  skippedCount: number
}

/** 手动撤销未完成任务：只改任务状态，不改任何价格。 */
export async function cancelStorePriceUpdateTasks(payload: CancelStorePriceUpdateTasksPayload): Promise<CancelStorePriceUpdateTasksResult> {
  const response = await request.post<ApiResponse<CancelStorePriceUpdateTasksResult>>(`${API_BASE}/cancel`, payload)
  const data = unwrapApiData(response)
  return {
    items: Array.isArray(data?.items) ? data.items : [],
    cancelledCount: Number(data?.cancelledCount ?? 0),
    skippedCount: Number(data?.skippedCount ?? 0),
  }
}

export async function previewPriceNotification(
  params: { productCode: string; retailPrice?: number; suggestedDiscountSpecified: boolean; suggestedDiscountRate?: number | null },
  options: ReadOptions = {},
): Promise<PriceNotificationPreview> {
  const response = await request.get<ApiResponse<PriceNotificationPreview>>(`${API_BASE}/preview`, {
    params: {
      productCode: params.productCode,
      retailPrice: params.retailPrice,
      suggestedDiscountSpecified: params.suggestedDiscountSpecified,
      // 「指定为未设置」时不传 rate：buildQueryString 会丢弃 null，后端按 null 处理。
      suggestedDiscountRate: params.suggestedDiscountSpecified ? params.suggestedDiscountRate : undefined,
    },
    signal: options.signal,
  })
  const data = unwrapApiData(response)
  return {
    affectedStores: Number(data?.affectedStores ?? 0),
    skippedSpecialStores: Number(data?.skippedSpecialStores ?? 0),
  }
}

export async function lookupSuggestedDiscounts(productCodes: string[], options: ReadOptions = {}): Promise<SuggestedDiscountItem[]> {
  const response = await request.post<ApiResponse<SuggestedDiscountItem[]>>(
    `${API_BASE}/suggested-discounts/lookup`,
    { productCodes },
    { signal: options.signal },
  )
  const items = unwrapApiData(response)
  return Array.isArray(items) ? items : []
}

export async function setSuggestedDiscounts(
  payload: { productCodes: string[]; suggestedDiscountRate: number | null; source: SuggestedDiscountSource },
  options: { onResponse?: (response: Response) => void } = {},
): Promise<{ changedCount: number }> {
  const response = await request.put<ApiResponse<{ changedCount: number }>>(
    `${API_BASE}/suggested-discounts`,
    payload,
    { onResponse: options.onResponse },
  )
  const data = unwrapApiData(response)
  return { changedCount: Number(data?.changedCount ?? 0) }
}
