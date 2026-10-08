import type { ApiResponse } from '../types/api'
import type {
  SeasonalCardBatch,
  SeasonalCardBatchLine,
  SeasonalCardPriceOption,
  SeasonalCardStatsDetailQuery,
  SeasonalCardStatsPriceQuantity,
  SeasonalCardStatsQuery,
  SeasonalCardStatsStoreDetail,
  SeasonalCardStatsStoreRef,
  SeasonalCardStatsStoreRow,
  SeasonalCardStatsSummary,
  SeasonalCardStatsSupplierRef,
  SeasonalCardStatsSupplierTotal,
  SeasonalCardType,
} from '../types/seasonalCardStats'
import request, { unwrapApiData } from '../utils/request'

// 节日贺卡分店填报统计（后台只读）：汇总 + 单店明细。
// 返回 ApiResponse 信封，业务错误也是 HTTP 200 + success=false（unwrapApiData 会转成 RequestError）；
// 权限 SeasonalCards.Remaining.ViewAllStores（管理员隐含）。
export const SEASONAL_CARD_STATS_API_BASE = '/api/react/v1/seasonal-card-remaining/admin'

/** 价格类型固定 4 档；接口缺项时按 0 补齐，表格列不会错位。 */
const PRICE_OPTIONS: readonly SeasonalCardPriceOption[] = [1, 2, 3, 4]
const DEFAULT_PRICE_LABELS: Record<SeasonalCardPriceOption, string> = { 1: '$1', 2: '$2', 3: '$3', 4: 'Other' }

function toNumber(value: unknown, fallback = 0): number {
  if (value === null || value === undefined || value === '') return fallback
  const parsed = typeof value === 'number' ? value : Number(value)
  return Number.isFinite(parsed) ? parsed : fallback
}

function toNumberOrNull(value: unknown): number | null {
  if (value === null || value === undefined || value === '') return null
  const parsed = typeof value === 'number' ? value : Number(value)
  return Number.isFinite(parsed) ? parsed : null
}

function toText(value: unknown): string {
  return typeof value === 'string' ? value : value === null || value === undefined ? '' : String(value)
}

function toTextOrNull(value: unknown): string | null {
  return typeof value === 'string' && value.trim() ? value : null
}

function isPriceOption(value: unknown): value is SeasonalCardPriceOption {
  return value === 1 || value === 2 || value === 3 || value === 4
}

function toCardType(value: unknown, fallback: SeasonalCardType): SeasonalCardType {
  const parsed = toNumber(value, fallback)
  return parsed === 1 || parsed === 2 || parsed === 3 || parsed === 4 || parsed === 5 ? parsed : fallback
}

type Raw<T> = Partial<Record<keyof T, unknown>> | null | undefined

function asArray(value: unknown): unknown[] {
  return Array.isArray(value) ? value : []
}

/** 按价格类型补齐 4 档并按 $1/$2/$3/其他 排序；未知价格类型丢弃。 */
export function normalizePriceQuantities(raw: unknown): SeasonalCardStatsPriceQuantity[] {
  const byOption = new Map<SeasonalCardPriceOption, SeasonalCardStatsPriceQuantity>()
  for (const item of asArray(raw) as Raw<SeasonalCardStatsPriceQuantity>[]) {
    const option = toNumber(item?.priceOption, 0)
    if (!isPriceOption(option)) continue
    byOption.set(option, {
      priceOption: option,
      priceLabel: toText(item?.priceLabel) || DEFAULT_PRICE_LABELS[option],
      quantity: toNumber(item?.quantity),
      amount: toNumber(item?.amount),
    })
  }
  return PRICE_OPTIONS.map(
    (option) => byOption.get(option) ?? { priceOption: option, priceLabel: DEFAULT_PRICE_LABELS[option], quantity: 0, amount: 0 },
  )
}

function normalizeStoreRef(raw: Raw<SeasonalCardStatsStoreRef>): SeasonalCardStatsStoreRef {
  return { storeCode: toText(raw?.storeCode), storeName: toText(raw?.storeName) }
}

function normalizeSupplierRef(raw: Raw<SeasonalCardStatsSupplierRef>): SeasonalCardStatsSupplierRef {
  return { localSupplierCode: toTextOrNull(raw?.localSupplierCode), supplierName: toText(raw?.supplierName) }
}

export function normalizeStoreRow(raw: Raw<SeasonalCardStatsStoreRow>): SeasonalCardStatsStoreRow {
  const prices = normalizePriceQuantities(raw?.prices)
  return {
    storeCode: toText(raw?.storeCode),
    storeName: toText(raw?.storeName),
    isFilled: raw?.isFilled === true,
    prices,
    totalQuantity: toNumber(raw?.totalQuantity, prices.reduce((sum, item) => sum + item.quantity, 0)),
    totalAmount: toNumber(raw?.totalAmount, prices.reduce((sum, item) => sum + item.amount, 0)),
    suppliers: (asArray(raw?.suppliers) as Raw<SeasonalCardStatsSupplierRef>[]).map(normalizeSupplierRef),
    lastSubmittedAt: toTextOrNull(raw?.lastSubmittedAt),
    lastSubmittedByName: toTextOrNull(raw?.lastSubmittedByName),
  }
}

function normalizeSupplierTotal(raw: Raw<SeasonalCardStatsSupplierTotal>): SeasonalCardStatsSupplierTotal {
  return {
    localSupplierCode: toTextOrNull(raw?.localSupplierCode),
    supplierName: toText(raw?.supplierName),
    storeCount: toNumber(raw?.storeCount),
    quantity: toNumber(raw?.quantity),
    amount: toNumber(raw?.amount),
  }
}

/** 逐字段兜底：后端缺字段或给 null 时界面不崩；分店数类指标缺失时由分店行推算。 */
export function normalizeSeasonalCardStatsSummary(
  raw: Raw<SeasonalCardStatsSummary>,
  query?: Pick<SeasonalCardStatsQuery, 'seasonYear' | 'cardType'>,
): SeasonalCardStatsSummary {
  const stores = (asArray(raw?.stores) as Raw<SeasonalCardStatsStoreRow>[]).map(normalizeStoreRow)
  const filled = stores.filter((store) => store.isFilled).length
  const storeCount = toNumber(raw?.storeCount, stores.length)
  const filledStoreCount = toNumber(raw?.filledStoreCount, filled)
  return {
    seasonYear: toNumber(raw?.seasonYear, query?.seasonYear ?? 0),
    cardType: toCardType(raw?.cardType, query?.cardType ?? 1),
    cardTypeName: toText(raw?.cardTypeName),
    storeCount,
    filledStoreCount,
    unfilledStoreCount: toNumber(raw?.unfilledStoreCount, storeCount - filledStoreCount),
    totalQuantity: toNumber(raw?.totalQuantity),
    totalAmount: toNumber(raw?.totalAmount),
    stores,
    priceTotals: normalizePriceQuantities(raw?.priceTotals),
    supplierTotals: (asArray(raw?.supplierTotals) as Raw<SeasonalCardStatsSupplierTotal>[]).map(normalizeSupplierTotal),
    unfilledStores: (asArray(raw?.unfilledStores) as Raw<SeasonalCardStatsStoreRef>[]).map(normalizeStoreRef),
    excludedStores: (asArray(raw?.excludedStores) as Raw<SeasonalCardStatsStoreRef>[]).map(normalizeStoreRef),
  }
}

function normalizeBatchLine(raw: Raw<SeasonalCardBatchLine>): SeasonalCardBatchLine | null {
  const option = toNumber(raw?.priceOption, 0)
  if (!isPriceOption(option)) return null
  return {
    submissionGuid: toText(raw?.submissionGuid),
    catalogGuid: toText(raw?.catalogGuid),
    priceOption: option,
    priceLabel: toText(raw?.priceLabel) || DEFAULT_PRICE_LABELS[option],
    unitPrice: toNumber(raw?.unitPrice),
    remainingQuantity: toNumber(raw?.remainingQuantity),
  }
}

export function normalizeBatch(raw: Raw<SeasonalCardBatch>): SeasonalCardBatch {
  const lines = (asArray(raw?.lines) as Raw<SeasonalCardBatchLine>[])
    .map(normalizeBatchLine)
    .filter((line): line is SeasonalCardBatchLine => line !== null)
    .sort((left, right) => left.priceOption - right.priceOption)
  return {
    batchGuid: toTextOrNull(raw?.batchGuid),
    storeCode: toText(raw?.storeCode),
    storeName: toTextOrNull(raw?.storeName),
    seasonYear: toNumber(raw?.seasonYear),
    cardType: toCardType(raw?.cardType, 1),
    cardTypeName: toText(raw?.cardTypeName),
    localSupplierCode: toTextOrNull(raw?.localSupplierCode),
    supplierName: toTextOrNull(raw?.supplierName),
    remark: toTextOrNull(raw?.remark),
    submittedByName: toText(raw?.submittedByName),
    submittedAt: toText(raw?.submittedAt),
    totalQuantity: toNumber(raw?.totalQuantity, lines.reduce((sum, line) => sum + line.remainingQuantity, 0)),
    totalAmount: toNumber(raw?.totalAmount, lines.reduce((sum, line) => sum + line.remainingQuantity * line.unitPrice, 0)),
    isCurrent: raw?.isCurrent === true,
    lines,
  }
}

/** 提交时间倒序（ISO 字符串可直接比较；缺 Z 的也同为 UTC 口径）。 */
function sortBySubmittedAtDesc(batches: SeasonalCardBatch[]): SeasonalCardBatch[] {
  return [...batches].sort((left, right) => (left.submittedAt < right.submittedAt ? 1 : left.submittedAt > right.submittedAt ? -1 : 0))
}

export function normalizeSeasonalCardStatsStoreDetail(raw: Raw<SeasonalCardStatsStoreDetail>): SeasonalCardStatsStoreDetail {
  return {
    storeCode: toText(raw?.storeCode),
    storeName: toText(raw?.storeName),
    seasonYear: toNumber(raw?.seasonYear),
    cardType: toCardType(raw?.cardType, 1),
    cardTypeName: toText(raw?.cardTypeName),
    isFilled: raw?.isFilled === true,
    totalQuantity: toNumber(raw?.totalQuantity),
    totalAmount: toNumber(raw?.totalAmount),
    currentBatches: (asArray(raw?.currentBatches) as Raw<SeasonalCardBatch>[]).map(normalizeBatch),
    history: sortBySubmittedAtDesc((asArray(raw?.history) as Raw<SeasonalCardBatch>[]).map(normalizeBatch)),
    previousYearTotalQuantity: toNumberOrNull(raw?.previousYearTotalQuantity),
  }
}

/** 查询参数：空供应商 / 价格不传；分店去重去空后按数组传（序列化为 storeCodes=a&storeCodes=b）。 */
export function buildSummaryParams(query: SeasonalCardStatsQuery): Record<string, unknown> {
  const storeCodes = Array.from(new Set((query.storeCodes ?? []).map((code) => code.trim()).filter(Boolean)))
  const supplier = query.localSupplierCode?.trim()
  return {
    seasonYear: query.seasonYear,
    cardType: query.cardType,
    localSupplierCode: supplier || undefined,
    priceOption: query.priceOption,
    storeCodes: storeCodes.length ? storeCodes : undefined,
  }
}

export async function getSeasonalCardStatsSummary(
  query: SeasonalCardStatsQuery,
  signal?: AbortSignal,
): Promise<SeasonalCardStatsSummary> {
  const response = await request.get<ApiResponse<SeasonalCardStatsSummary>>(`${SEASONAL_CARD_STATS_API_BASE}/summary`, {
    params: buildSummaryParams(query),
    signal,
  })
  return normalizeSeasonalCardStatsSummary(unwrapApiData(response) as Raw<SeasonalCardStatsSummary>, query)
}

export async function getSeasonalCardStatsStoreDetail(
  storeCode: string,
  query: SeasonalCardStatsDetailQuery,
  signal?: AbortSignal,
): Promise<SeasonalCardStatsStoreDetail> {
  const response = await request.get<ApiResponse<SeasonalCardStatsStoreDetail>>(
    `${SEASONAL_CARD_STATS_API_BASE}/stores/${encodeURIComponent(storeCode)}`,
    { params: { seasonYear: query.seasonYear, cardType: query.cardType }, signal },
  )
  return normalizeSeasonalCardStatsStoreDetail(unwrapApiData(response) as Raw<SeasonalCardStatsStoreDetail>)
}
