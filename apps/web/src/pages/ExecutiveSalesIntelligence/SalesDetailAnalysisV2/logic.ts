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

export function selectDimension(state: DetailSelection, dimension: 'supplier' | 'branch' | 'product', code: string): DetailSelection {
  if (dimension === 'supplier') {
    const supplierCodes = state.supplierCodes.includes(code) ? state.supplierCodes.filter(value => value !== code) : [...state.supplierCodes, code]
    return { ...state, supplier: supplierCodes[0], supplierCodes, supplierCategoryGuids: [], page: 1 }
  }
  return { ...state, [dimension]: state[dimension] === code ? undefined : code, page: dimension === 'product' ? state.page : 1 }
}

/** 商品反查只固定商品条件，清除分店和关键字，保留当前日期、类别、供应商与账号分店范围。 */
export function productBranchDrawerQuery(query: SalesDetailQuery, productCode: string): SalesDetailQuery {
  return { ...query, selectedProductCode: productCode, selectedBranchCode: undefined, search: undefined, pageIndex: 1 }
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
