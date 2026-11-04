import type { ApiResponse } from '../types/api'
import request from '../utils/request'

/**
 * 节日销售报告（万圣节）：KFC / Brazco 节日商品的销售、进货、卖断与滞销。
 *
 * 数值字段缺失或不是有限数值时按 0 处理（报表合计）；售罄率、卖断天数这类「没有就是没有」的字段保持 null，
 * 页面显示「—」，不能当 0%。
 */
export type HolidayCategoryKey = 'Costume' | 'Decoration' | 'MakeUp' | 'Accessory' | 'Party' | 'MaskWig' | 'Animated'

export const HOLIDAY_CATEGORY_KEYS: readonly HolidayCategoryKey[] = [
  'Costume',
  'Decoration',
  'MakeUp',
  'Accessory',
  'Party',
  'MaskWig',
  'Animated',
]

export type HolidaySupplierFilter = 'all' | '257' | '243'

export interface HolidayTotals {
  amount: number
  quantity: number
  skuCount: number
  purchaseQuantity: number
  purchaseCost: number
  sellThrough: number | null
  /** 店次：卖断的「分店 × 商品」数。 */
  earlySoldOutCount: number
  /** 款数：不重复的商品数。 */
  earlySoldOutProductCount: number
  estimatedLostAmount: number
  leftoverQuantity: number
  leftoverCost: number
  holidayDayAmount: number
  lastWeekAmount: number
  afterHolidayAmount: number
}

export interface HolidaySupplier {
  supplierCode: string
  supplierName: string
  amount: number
  quantity: number
  purchaseQuantity: number
}

export interface HolidayDaily {
  /** yyyy-MM-dd */
  date: string
  amount: number
}

export interface HolidayCategory {
  key: HolidayCategoryKey
  amount: number
  quantity: number
  skuCount: number
  purchaseQuantity: number
  sellThrough: number | null
  estimatedLostAmount: number
  leftoverQuantity: number
  leftoverCost: number
}

export interface HolidayProduct {
  productCode: string
  itemNumber: string
  productName: string
  imageUrl: string | null
  supplierCode: string
  categoryKey: HolidayCategoryKey
  amount: number
  quantity: number
  purchaseQuantity: number
  sellThrough: number | null
  storeCount: number
  soldOutStoreCount: number
  daysEarly: number | null
  soldOutDate: string | null
  estimatedLostAmount: number
  leftoverQuantity: number
  leftoverCost: number
}

export interface HolidayStore {
  branchCode: string
  branchName: string
  amount: number
  quantity: number
  purchaseQuantity: number
  sellThrough: number | null
  earlySoldOutCount: number
  estimatedLostAmount: number
  leftoverCost: number
  afterHolidayAmount: number
}

export interface HolidayBenchmark {
  storeCount: number
  rank: number
  averageAmount: number
  sellThrough: number | null
  averageEarlySoldOutCount: number
  averageLostAmount: number
  averageLeftoverCost: number
  averageAfterHolidayAmount: number
  categorySellThrough: Partial<Record<HolidayCategoryKey, number | null>>
}

export interface HolidaySalesReport {
  year: number
  holidayDate: string
  salesFrom: string
  salesThrough: string
  productCount: number
  branchCode: string | null
  branchName: string | null
  totals: HolidayTotals
  suppliers: HolidaySupplier[]
  daily: HolidayDaily[]
  categories: HolidayCategory[]
  bestSellers: HolidayProduct[]
  earlySoldOut: HolidayProduct[]
  slowSellers: HolidayProduct[]
  overstock: HolidayProduct[]
  stores: HolidayStore[]
  storesWithoutSales: HolidayStore[]
  benchmark: HolidayBenchmark | null
}

/** 单个商品在某家分店的进货 / 售出 / 卖断 / 剩余（榜单里点商品钻取）。 */
export interface HolidayProductStore {
  /** 合计行（HolidayProductStores.total）的编码为空串。 */
  branchCode: string
  branchName: string
  amount: number
  quantity: number
  purchaseQuantity: number
  /** 没有进货时为 null（不是 0%）。 */
  sellThrough: number | null
  /** 卖断日（不晚于节日当天）；没有卖断为 null。 */
  soldOutDate: string | null
  /** 0 = 节日当天才卖完；null = 没有卖断。 */
  daysEarly: number | null
  estimatedLostAmount: number
  leftoverQuantity: number
  leftoverCost: number
  firstSaleDate: string | null
}

export interface HolidayProductStores {
  year: number
  holidayDate: string
  productCode: string
  itemNumber: string
  productName: string
  imageUrl: string | null
  supplierCode: string
  categoryKey: HolidayCategoryKey
  total: HolidayProductStore
  storeCount: number
  soldOutStoreCount: number
  stores: HolidayProductStore[]
}

/** 大类明细（大类表里点大类钻取）：合计、各分店表现（合计视图）与完整商品清单。 */
export interface HolidayCategoryDetail {
  year: number
  holidayDate: string
  /** 单店视图时的分店；合计视图为 null，此时 stores 有值。 */
  branchCode: string | null
  branchName: string | null
  category: HolidayCategory
  stores: HolidayStore[]
  products: HolidayProduct[]
}

export interface HolidayCategoryDetailQuery {
  year: number
  categoryKey: HolidayCategoryKey
  branchCode?: string
  supplierCode?: HolidaySupplierFilter
}

export interface HolidayCategoryMapEntry {
  supplierCode: string
  /** 供应商网站分类名；商品没有归类时为 null。 */
  supplierCategory: string | null
  skuCount: number
  amount: number
}

export interface HolidayCategoryMapGroup {
  key: HolidayCategoryKey
  amount: number
  entries: HolidayCategoryMapEntry[]
}

export interface HolidayCategoryMap {
  year: number
  groups: HolidayCategoryMapGroup[]
}

export interface HolidaySalesReportQuery {
  year: number
  /** 只看这一家店；不传是授权范围内全部分店合计。 */
  branchCode?: string
  supplierCode?: HolidaySupplierFilter
  forceRefresh?: boolean
}

const datePattern = /^\d{4}-(0[1-9]|1[0-2])-(0[1-9]|[12]\d|3[01])$/

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value)
}

function readString(value: unknown): string | undefined {
  return typeof value === 'string' && value.trim() ? value.trim() : undefined
}

function readNumber(value: unknown): number {
  return typeof value === 'number' && Number.isFinite(value) ? value : 0
}

/** 只认有限数值；字符串、NaN、对象一律按 null，绝不悄悄当 0。 */
function readNullableNumber(value: unknown): number | null {
  return typeof value === 'number' && Number.isFinite(value) ? value : null
}

function readDate(value: unknown): string | null {
  const text = readString(value)
  return text && datePattern.test(text) ? text : null
}

function readCategoryKey(value: unknown): HolidayCategoryKey {
  const text = readString(value)
  return (HOLIDAY_CATEGORY_KEYS as readonly string[]).includes(text ?? '') ? (text as HolidayCategoryKey) : 'Accessory'
}

function list<T>(value: unknown, mapper: (raw: Record<string, unknown>) => T | null): T[] {
  if (!Array.isArray(value)) return []
  const result: T[] = []
  for (const item of value) {
    if (!isRecord(item)) continue
    const mapped = mapper(item)
    if (mapped) result.push(mapped)
  }
  return result
}

function normalizeProduct(raw: Record<string, unknown>): HolidayProduct | null {
  const productCode = readString(raw.productCode)
  if (!productCode) return null
  return {
    productCode,
    itemNumber: readString(raw.itemNumber) ?? '',
    productName: readString(raw.productName) ?? productCode,
    imageUrl: readString(raw.imageUrl) ?? null,
    supplierCode: readString(raw.supplierCode) ?? '',
    categoryKey: readCategoryKey(raw.categoryKey),
    amount: readNumber(raw.amount),
    quantity: readNumber(raw.quantity),
    purchaseQuantity: readNumber(raw.purchaseQuantity),
    sellThrough: readNullableNumber(raw.sellThrough),
    storeCount: readNumber(raw.storeCount),
    soldOutStoreCount: readNumber(raw.soldOutStoreCount),
    daysEarly: readNullableNumber(raw.daysEarly),
    soldOutDate: readDate(raw.soldOutDate),
    estimatedLostAmount: readNumber(raw.estimatedLostAmount),
    leftoverQuantity: readNumber(raw.leftoverQuantity),
    leftoverCost: readNumber(raw.leftoverCost),
  }
}

function normalizeStore(raw: Record<string, unknown>): HolidayStore | null {
  const branchCode = readString(raw.branchCode)
  if (!branchCode) return null
  return {
    branchCode,
    branchName: readString(raw.branchName) ?? branchCode,
    amount: readNumber(raw.amount),
    quantity: readNumber(raw.quantity),
    purchaseQuantity: readNumber(raw.purchaseQuantity),
    sellThrough: readNullableNumber(raw.sellThrough),
    earlySoldOutCount: readNumber(raw.earlySoldOutCount),
    estimatedLostAmount: readNumber(raw.estimatedLostAmount),
    leftoverCost: readNumber(raw.leftoverCost),
    afterHolidayAmount: readNumber(raw.afterHolidayAmount),
  }
}

function normalizeCategory(c: Record<string, unknown>): HolidayCategory {
  return {
    key: readCategoryKey(c.key),
    amount: readNumber(c.amount),
    quantity: readNumber(c.quantity),
    skuCount: readNumber(c.skuCount),
    purchaseQuantity: readNumber(c.purchaseQuantity),
    sellThrough: readNullableNumber(c.sellThrough),
    estimatedLostAmount: readNumber(c.estimatedLostAmount),
    leftoverQuantity: readNumber(c.leftoverQuantity),
    leftoverCost: readNumber(c.leftoverCost),
  }
}

function normalizeTotals(raw: unknown): HolidayTotals {
  const r = isRecord(raw) ? raw : {}
  return {
    amount: readNumber(r.amount),
    quantity: readNumber(r.quantity),
    skuCount: readNumber(r.skuCount),
    purchaseQuantity: readNumber(r.purchaseQuantity),
    purchaseCost: readNumber(r.purchaseCost),
    sellThrough: readNullableNumber(r.sellThrough),
    earlySoldOutCount: readNumber(r.earlySoldOutCount),
    earlySoldOutProductCount: readNumber(r.earlySoldOutProductCount),
    estimatedLostAmount: readNumber(r.estimatedLostAmount),
    leftoverQuantity: readNumber(r.leftoverQuantity),
    leftoverCost: readNumber(r.leftoverCost),
    holidayDayAmount: readNumber(r.holidayDayAmount),
    lastWeekAmount: readNumber(r.lastWeekAmount),
    afterHolidayAmount: readNumber(r.afterHolidayAmount),
  }
}

function normalizeBenchmark(raw: unknown): HolidayBenchmark | null {
  if (!isRecord(raw)) return null
  const byCategory: Partial<Record<HolidayCategoryKey, number | null>> = {}
  if (isRecord(raw.categorySellThrough)) {
    for (const key of HOLIDAY_CATEGORY_KEYS) byCategory[key] = readNullableNumber(raw.categorySellThrough[key])
  }
  return {
    storeCount: readNumber(raw.storeCount),
    rank: readNumber(raw.rank),
    averageAmount: readNumber(raw.averageAmount),
    sellThrough: readNullableNumber(raw.sellThrough),
    averageEarlySoldOutCount: readNumber(raw.averageEarlySoldOutCount),
    averageLostAmount: readNumber(raw.averageLostAmount),
    averageLeftoverCost: readNumber(raw.averageLeftoverCost),
    averageAfterHolidayAmount: readNumber(raw.averageAfterHolidayAmount),
    categorySellThrough: byCategory,
  }
}

/** 信封 { success, data } 或裸对象都接受；success 为 false 时抛错而不是当成空数据。 */
function unwrapPayload(payload: unknown): Record<string, unknown> {
  let current: unknown = payload
  for (let depth = 0; depth < 3; depth += 1) {
    if (!isRecord(current)) break
    if (current.success === false || current.isSuccess === false) {
      throw new Error(readString(current.message) ?? 'Holiday sales report request failed')
    }
    if (!('data' in current)) break
    current = current.data
  }
  if (!isRecord(current)) throw new Error('Holiday sales report response is malformed')
  return current
}

export function normalizeHolidaySalesReport(payload: unknown): HolidaySalesReport {
  const r = unwrapPayload(payload)
  const year = readNumber(r.year)
  return {
    year,
    holidayDate: readDate(r.holidayDate) ?? `${year}-10-31`,
    salesFrom: readDate(r.salesFrom) ?? `${year}-08-01`,
    salesThrough: readDate(r.salesThrough) ?? `${year}-08-01`,
    productCount: readNumber(r.productCount),
    branchCode: readString(r.branchCode) ?? null,
    branchName: readString(r.branchName) ?? null,
    totals: normalizeTotals(r.totals),
    suppliers: list(r.suppliers, (s) => {
      const supplierCode = readString(s.supplierCode)
      return supplierCode
        ? {
            supplierCode,
            supplierName: readString(s.supplierName) ?? supplierCode,
            amount: readNumber(s.amount),
            quantity: readNumber(s.quantity),
            purchaseQuantity: readNumber(s.purchaseQuantity),
          }
        : null
    }),
    // 同一天出现多条时只保留第一条，避免曲线与合计重复计入。
    daily: Array.from(
      list(r.daily, (d) => {
        const date = readDate(d.date)
        return date ? { date, amount: readNumber(d.amount) } : null
      })
        .reduce((map, row) => (map.has(row.date) ? map : map.set(row.date, row)), new Map<string, HolidayDaily>())
        .values(),
    ).sort((a, b) => a.date.localeCompare(b.date)),
    categories: list(r.categories, normalizeCategory),
    bestSellers: list(r.bestSellers, normalizeProduct),
    earlySoldOut: list(r.earlySoldOut, normalizeProduct),
    slowSellers: list(r.slowSellers, normalizeProduct),
    overstock: list(r.overstock, normalizeProduct),
    stores: list(r.stores, normalizeStore),
    storesWithoutSales: list(r.storesWithoutSales, normalizeStore),
    benchmark: normalizeBenchmark(r.benchmark),
  }
}

function normalizeProductStore(raw: unknown): HolidayProductStore {
  const r = isRecord(raw) ? raw : {}
  return {
    branchCode: readString(r.branchCode) ?? '',
    branchName: readString(r.branchName) ?? readString(r.branchCode) ?? '',
    amount: readNumber(r.amount),
    quantity: readNumber(r.quantity),
    purchaseQuantity: readNumber(r.purchaseQuantity),
    sellThrough: readNullableNumber(r.sellThrough),
    soldOutDate: readDate(r.soldOutDate),
    daysEarly: readNullableNumber(r.daysEarly),
    estimatedLostAmount: readNumber(r.estimatedLostAmount),
    leftoverQuantity: readNumber(r.leftoverQuantity),
    leftoverCost: readNumber(r.leftoverCost),
    firstSaleDate: readDate(r.firstSaleDate),
  }
}

export function normalizeHolidayProductStores(payload: unknown): HolidayProductStores {
  const r = unwrapPayload(payload)
  const productCode = readString(r.productCode)
  if (!productCode) throw new Error('Holiday product stores response is malformed')
  const seen = new Set<string>()
  const stores: HolidayProductStore[] = []
  // 同一家店出现多行时只保留第一行，避免合计之外重复显示。
  for (const item of Array.isArray(r.stores) ? r.stores : []) {
    if (!isRecord(item) || !readString(item.branchCode)) continue
    const store = normalizeProductStore(item)
    if (seen.has(store.branchCode)) continue
    seen.add(store.branchCode)
    stores.push(store)
  }
  return {
    year: readNumber(r.year),
    holidayDate: readDate(r.holidayDate) ?? '',
    productCode,
    itemNumber: readString(r.itemNumber) ?? '',
    productName: readString(r.productName) ?? productCode,
    imageUrl: readString(r.imageUrl) ?? null,
    supplierCode: readString(r.supplierCode) ?? '',
    categoryKey: readCategoryKey(r.categoryKey),
    total: normalizeProductStore(r.total),
    storeCount: readNumber(r.storeCount),
    soldOutStoreCount: readNumber(r.soldOutStoreCount),
    stores,
  }
}

export function normalizeHolidayCategoryDetail(payload: unknown): HolidayCategoryDetail {
  const r = unwrapPayload(payload)
  if (!isRecord(r.category)) throw new Error('Holiday category detail response is malformed')
  return {
    year: readNumber(r.year),
    holidayDate: readDate(r.holidayDate) ?? '',
    branchCode: readString(r.branchCode) ?? null,
    branchName: readString(r.branchName) ?? null,
    category: normalizeCategory(r.category),
    stores: list(r.stores, normalizeStore),
    products: list(r.products, normalizeProduct),
  }
}

export function normalizeHolidayCategoryMap(payload: unknown): HolidayCategoryMap {
  const r = unwrapPayload(payload)
  return {
    year: readNumber(r.year),
    groups: list(r.groups, (g) => ({
      key: readCategoryKey(g.key),
      amount: readNumber(g.amount),
      entries: list(g.entries, (e) => ({
        supplierCode: readString(e.supplierCode) ?? '',
        supplierCategory: readString(e.supplierCategory) ?? null,
        skuCount: readNumber(e.skuCount),
        amount: readNumber(e.amount),
      })),
    })),
  }
}

const BASE = '/api/react/v1/dashboard/holiday-sales-report'

export async function getHolidaySalesReport(
  query: HolidaySalesReportQuery,
  signal?: AbortSignal,
): Promise<HolidaySalesReport> {
  const response = await request<ApiResponse<HolidaySalesReport> | HolidaySalesReport>(BASE, {
    method: 'GET',
    signal,
    params: {
      year: query.year,
      branchCode: query.branchCode,
      // 'all' 不传：服务端把没有供应商参数当作两家合计。
      supplierCode: query.supplierCode && query.supplierCode !== 'all' ? query.supplierCode : undefined,
      forceRefresh: query.forceRefresh ?? false,
    },
  })
  return normalizeHolidaySalesReport(response)
}

export async function getHolidayCategoryDetail(query: HolidayCategoryDetailQuery, signal?: AbortSignal): Promise<HolidayCategoryDetail> {
  const response = await request<ApiResponse<HolidayCategoryDetail> | HolidayCategoryDetail>(`${BASE}/category`, {
    method: 'GET',
    signal,
    params: {
      year: query.year,
      categoryKey: query.categoryKey,
      branchCode: query.branchCode,
      supplierCode: query.supplierCode && query.supplierCode !== 'all' ? query.supplierCode : undefined,
    },
  })
  return normalizeHolidayCategoryDetail(response)
}

export async function getHolidayProductStores(year: number, productCode: string, signal?: AbortSignal): Promise<HolidayProductStores> {
  const response = await request<ApiResponse<HolidayProductStores> | HolidayProductStores>(`${BASE}/product`, {
    method: 'GET',
    signal,
    params: { year, productCode },
  })
  return normalizeHolidayProductStores(response)
}

export async function getHolidayCategoryMap(year: number, signal?: AbortSignal): Promise<HolidayCategoryMap> {
  const response = await request<ApiResponse<HolidayCategoryMap> | HolidayCategoryMap>(`${BASE}/category-map`, {
    method: 'GET',
    signal,
    params: { year },
  })
  return normalizeHolidayCategoryMap(response)
}
