import { completeSum, margin, normalizeKeyword, quickDateSelection, validPeriod, type DateSelection } from '../ReportWorkbench/logic'
import type { SalesDetailQuery, SalesDetailRow, SupplierKind } from './reportService'

// 带图工作簿在浏览器中生成，限制单次行数以控制图片请求和 ExcelJS 内存占用。
export const MAX_PRODUCT_IMAGE_EXPORT_ROWS = 500
// 分类接口按供应商最多接受 100 个编码；分类多选也沿用后端可承载的 500 项上限。
export const MAX_SUPPLIER_SELECTIONS = 100
export const MAX_CATEGORY_SELECTIONS = 500

export function exceedsSalesDetailSelectionLimit(values: readonly string[], limit: number): boolean {
  return values.length > limit
}

/** 记住供应商下拉选项来自哪个页签（澳洲 / 国内），避免切页签时把另一个页签的选项带过来。 */
export interface RememberedSupplierOptions<T> { kind: SupplierKind; options: T[] }
/**
 * 选择供应商会改变整页查询键，useReportQuery 在重新加载期间不返回数据（loaded 为 undefined）。
 * 此时若直接用空数组：多选下拉被清空成「没有供应商」，已选标签退化成裸编码，数据回来后又弹回名称，表现为闪烁。
 * 因此加载期间沿用同一页签上一次加载成功的选项；加载完成后一律以最新数据为准。
 */
export function resolveSupplierOptions<T>(kind: SupplierKind, loaded: T[] | undefined,
  remembered: RememberedSupplierOptions<T> | undefined): RememberedSupplierOptions<T> {
  if (loaded) return { kind, options: loaded }
  return remembered?.kind === kind ? remembered : { kind, options: [] }
}

export interface DetailSelection { supplier?: string; supplierCodes: string[]; supplierCategoryGuids: string[]; warehouseCategoryGuids: string[]; branch?: string; product?: string; keyword: string; page: number; pageSize: number }
export const emptySelection: DetailSelection = { supplierCodes: [], supplierCategoryGuids: [], warehouseCategoryGuids: [], keyword: '', page: 1, pageSize: 20 }

export function initialDetailState(search: string): { dates: DateSelection; kind: SupplierKind; selection: DetailSelection } {
  const params = new URLSearchParams(search)
  let dates = quickDateSelection('today')
  const start = params.get('startDate'), end = params.get('endDate')
  if (start && end && validPeriod(start, end)) dates = { ...dates, startDate: start, endDate: end, quick: 'custom' }
  if (['false', '0'].includes(params.get('compare') ?? '')) dates.compare = false
  if (params.get('compareMode') === 'ByDate') dates.compareMode = 'ByDate'
  const supplierCodes = params.getAll('supplier').map(value => value.trim()).filter(Boolean)
  return { dates, kind: params.get('kind') === 'china' ? 'china' : 'australia',
    selection: { ...emptySelection, branch: params.get('branch')?.trim() || undefined,
      supplier: supplierCodes[0], supplierCodes,
      supplierCategoryGuids: params.getAll('supplierCategory').map(value => value.trim()).filter(Boolean),
      warehouseCategoryGuids: params.getAll('warehouseCategory').map(value => value.trim()).filter(Boolean) } }
}

/** 「澳洲供应商分类」页签的初始状态：kind=category 时启用，并从 URL 读取供应商与分店。 */
export function initialCategoryState(search: string): { active: boolean; supplierCodes: string[]; branch?: string } {
  const params = new URLSearchParams(search)
  const active = params.get('kind') === 'category'
  return { active, supplierCodes: active ? [...new Set(params.getAll('supplier').map(value => value.trim()).filter(Boolean))] : [],
    branch: active ? params.get('branch')?.trim() || undefined : undefined }
}

export function selectDimension(state: DetailSelection, dimension: 'supplier' | 'branch' | 'product', code: string): DetailSelection {
  if (dimension === 'supplier') {
    const supplierCodes = state.supplierCodes.includes(code) ? state.supplierCodes.filter(value => value !== code) : [...state.supplierCodes, code]
    return { ...state, supplier: supplierCodes[0], supplierCodes, supplierCategoryGuids: [], page: 1 }
  }
  return { ...state, [dimension]: state[dimension] === code ? undefined : code, page: dimension === 'product' ? state.page : 1 }
}

/** 商品反查只固定商品条件，清除分店和关键字，保留当前日期、类别、供应商与账号分店范围。 */
export function productBranchDrawerQuery(query: SalesDetailQuery, productCode: string): SalesDetailQuery {
  // 抽屉只读分店栏，商品排序与它无关；去掉后换排序不会让已打开的抽屉重新请求。
  return { ...query, selectedProductCode: productCode, selectedBranchCode: undefined, search: undefined, pageIndex: 1,
    productSortField: undefined, productSortOrder: undefined }
}

/** 商品明细可排序的列：营业额、数量、均价。服务端全量排序后分页，前端只防御性重排当前页。 */
export type ProductSortKey = 'revenue' | 'quantity' | 'averageUnitPrice'
export interface ProductSort { key: ProductSortKey; ascending: boolean }
/** 默认数量降序，与改版前的固定顺序一致。 */
export const DEFAULT_PRODUCT_SORT: ProductSort = { key: 'quantity', ascending: false }
const PRODUCT_SORT_FIELD: Record<ProductSortKey, NonNullable<SalesDetailQuery['productSortField']>> = {
  revenue: 'Revenue', quantity: 'Quantity', averageUnitPrice: 'AverageUnitPrice',
}

/** 点同一列切换升降序，换列从降序开始（先看最大的）。 */
export function toggleProductSort(current: ProductSort, key: ProductSortKey): ProductSort {
  return current.key === key ? { key, ascending: !current.ascending } : { key, ascending: false }
}

/** 默认排序不带参数：请求与查询键和改版前完全相同，旧缓存继续命中。 */
export function productSortParams(sort: ProductSort): Pick<SalesDetailQuery, 'productSortField' | 'productSortOrder'> {
  return sort.key === DEFAULT_PRODUCT_SORT.key && sort.ascending === DEFAULT_PRODUCT_SORT.ascending
    ? {} : { productSortField: PRODUCT_SORT_FIELD[sort.key], productSortOrder: sort.ascending ? 'asc' : 'desc' }
}

/**
 * 与服务端同一口径重排当前页（兼容缓存或旧接口返回的非确定顺序）：
 * 数量 / 营业额同值再比同期值，均价同值比营业额（高的在前），最后按商品代码；没有均价的商品升降序都排最后。
 */
export function sortProductRows(rows: readonly SalesDetailRow[], sort: ProductSort): SalesDetailRow[] {
  return [...rows].sort((left, right) => compareProductRows(left, right, sort))
}

function compareProductRows(left: SalesDetailRow, right: SalesDetailRow, sort: ProductSort): number {
  const direction = sort.ascending ? 1 : -1
  if (sort.key === 'averageUnitPrice') {
    const a = unitPrice(left.revenue, left.quantity), b = unitPrice(right.revenue, right.quantity)
    if (a == null || b == null) {
      if (a != null) return -1
      if (b != null) return 1
      return right.revenue - left.revenue || left.code.localeCompare(right.code)
    }
    return (a - b) * direction || right.revenue - left.revenue || left.code.localeCompare(right.code)
  }
  const compareKey = sort.key === 'revenue' ? 'compareRevenue' : 'compareQuantity'
  return (left[sort.key] - right[sort.key]) * direction
    || ((left[compareKey] ?? 0) - (right[compareKey] ?? 0)) * direction
    || left.code.localeCompare(right.code)
}

export function applyKeyword(state: DetailSelection, keyword: string): DetailSelection {
  const normalized = normalizeKeyword(keyword)
  return normalized === state.keyword ? state : { ...state, keyword: normalized, product: undefined, page: 1 }
}

/** 合计行只使用这些字段，服务端汇总行与前端本页汇总都满足该形状。 */
export type DetailTotals = Pick<SalesDetailRow, 'revenue' | 'compareRevenue' | 'quantity' | 'compareQuantity'
  | 'averageUnitPrice' | 'compareAverageUnitPrice' | 'grossProfit' | 'compareGrossProfit' | 'grossMarginRate' | 'compareGrossMarginRate'>

// 均价口径与页面说明一致：营业额 ÷ 商品数量，数量 ≤ 0 时不给均价。
function unitPrice(revenue: number | null, quantity: number | null) {
  return revenue == null || quantity == null || quantity <= 0 ? null : revenue / quantity
}

export function sumProductPage(rows: SalesDetailRow[]): DetailTotals {
  const revenue = rows.reduce((sum, row) => sum + row.revenue, 0)
  const compareRevenue = completeSum(rows.map(row => row.compareRevenue))
  const quantity = rows.reduce((sum, row) => sum + row.quantity, 0)
  const compareQuantity = completeSum(rows.map(row => row.compareQuantity))
  const grossProfit = completeSum(rows.map(row => row.grossProfit))
  const compareGrossProfit = completeSum(rows.map(row => row.compareGrossProfit))
  return { revenue, compareRevenue, quantity, compareQuantity,
    averageUnitPrice: unitPrice(revenue, quantity), compareAverageUnitPrice: unitPrice(compareRevenue, compareQuantity),
    grossProfit, compareGrossProfit, grossMarginRate: margin(grossProfit, revenue),
    compareGrossMarginRate: compareRevenue == null ? null : margin(compareGrossProfit, compareRevenue) }
}

/** 左栏（供应商 + 分店）占工作区的百分比；拖到两端时仍给商品明细留出主视图宽度。 */
export const RAIL_DEFAULT_WIDTH = 28
export function clampRailWidth(width: number): number {
  return Math.min(46, Math.max(20, width))
}

/** 商品表的同期展示方式：上下两行、仅本期单行、本期同期左右并排。 */
export type CompareView = 'stack' | 'current' | 'side'
export interface DetailViewPreference { compareView: CompareView; railCollapsed: boolean }
export const defaultDetailView: DetailViewPreference = { compareView: 'stack', railCollapsed: false }

/** 读取本机保存的展示偏好；存储被清空、损坏或来自旧版本时回到默认值。 */
export function parseDetailView(raw: string | null): DetailViewPreference {
  try {
    const value = raw ? JSON.parse(raw) as Partial<DetailViewPreference> : {}
    return {
      compareView: value.compareView === 'current' || value.compareView === 'side' ? value.compareView : 'stack',
      railCollapsed: value.railCollapsed === true,
    }
  } catch {
    return defaultDetailView
  }
}
