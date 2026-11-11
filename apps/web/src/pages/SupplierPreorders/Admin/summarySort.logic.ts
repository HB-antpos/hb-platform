// 「订货汇总」商品排序的纯逻辑：下拉选项、点分店列头的三态循环、写进接口 query 的参数、容错校验。
// 不依赖 React / antd，便于 node 直接跑单测。
import type { SupplierPreorderMatrixQuery, SupplierPreorderMatrixSort } from '../../../types/supplierPreorder'

/** 排序状态：storeCartons* 时 storeGuid 必填（按这家店的箱数排），其余排序忽略 storeGuid */
export interface SortState {
  sort: SupplierPreorderMatrixSort
  storeGuid?: string
}

export const DEFAULT_SORT: SortState = { sort: 'seq' }

/** 下拉里能直接选的排序（顺序 = 下拉里的顺序）；分店箱数排序只能点列头触发 */
export const SORT_OPTION_VALUES = ['seq', 'totalCartonsDesc', 'lastYearSalesDesc', 'sellThroughDesc', 'ratioAsc', 'priceAsc', 'priceDesc', 'codeAsc'] as const

const ALL_SORT_VALUES: readonly SupplierPreorderMatrixSort[] = [...SORT_OPTION_VALUES, 'storeCartonsDesc', 'storeCartonsAsc']

export function isSortValue(value: unknown): value is SupplierPreorderMatrixSort {
  return typeof value === 'string' && (ALL_SORT_VALUES as readonly string[]).includes(value)
}

export function isStoreSort(state: SortState): boolean {
  return state.sort === 'storeCartonsDesc' || state.sort === 'storeCartonsAsc'
}

/** 点某家分店的列头：该店 desc → asc → 回默认序号；点别的店（或当前不是店排序）从 desc 开始 */
export function nextStoreSort(current: SortState, storeGuid: string): SortState {
  if (current.storeGuid === storeGuid && current.sort === 'storeCartonsDesc') return { sort: 'storeCartonsAsc', storeGuid }
  if (current.storeGuid === storeGuid && current.sort === 'storeCartonsAsc') return DEFAULT_SORT
  return { sort: 'storeCartonsDesc', storeGuid }
}

/** 这家店的列头当前是不是排序依据：返回方向，不是则 null */
export function storeSortDirection(current: SortState, storeGuid: string): 'desc' | 'asc' | null {
  if (current.storeGuid !== storeGuid) return null
  if (current.sort === 'storeCartonsDesc') return 'desc'
  if (current.sort === 'storeCartonsAsc') return 'asc'
  return null
}

/** 写进矩阵接口的排序参数：默认序号不传（后端不传 = seq），分店排序同时带 sortStoreGuid */
export function toSortQuery(state: SortState): Pick<SupplierPreorderMatrixQuery, 'sort' | 'sortStoreGuid'> {
  if (isStoreSort(state)) return state.storeGuid ? { sort: state.sort, sortStoreGuid: state.storeGuid } : {}
  return state.sort === 'seq' ? {} : { sort: state.sort }
}

/**
 * 校验排序状态：值不认识 / 分店排序缺店或店已不在订单表里 → 回默认序号。
 * validStoreGuids 为空（矩阵还没加载）时不校验店，避免首次加载就把记住的店排序丢掉。
 */
export function normalizeSort(state: SortState, validStoreGuids?: ReadonlySet<string>): SortState {
  if (!isSortValue(state.sort)) return DEFAULT_SORT
  // 非分店排序不该带 storeGuid，带了就去掉（避免旧值残留影响列头的排序箭头）
  if (!isStoreSort(state)) return state.storeGuid === undefined ? state : { sort: state.sort }
  if (!state.storeGuid) return DEFAULT_SORT
  if (validStoreGuids && validStoreGuids.size > 0 && !validStoreGuids.has(state.storeGuid)) return DEFAULT_SORT
  return state
}
