import type {
  CompactSalesBoardSortField,
  CompactSalesBoardSortOrder,
  DateRange,
} from '../../../services/salesDashboardService'

export interface BoardSelection {
  code: string
  label: string
  detail?: string
  /** 仅商品选中项使用：该商品所属的国内供应商 */
  supplierCode?: string
}

export interface ProductSort {
  field: CompactSalesBoardSortField
  order: CompactSalesBoardSortOrder
}

export const defaultProductSort: ProductSort = { field: 'amount', order: 'desc' }

export interface BoardFilterState {
  dateRange: DateRange
  scopeKey: string
  branch: BoardSelection | null
  supplier: BoardSelection | null
  product: BoardSelection | null
  keyword: string
  productSort: ProductSort
  pageIndex: number
  pageSize: number
}

export interface PanelKeys {
  stores: string
  suppliers: string
  products: string
  summary: string
}

/**
 * 每栏只依赖「其他栏」的选中项：据此判断一次请求期间哪些栏的数据已过期，
 * 只给这些栏显示加载条，其余栏保持可操作、不闪烁。
 */
export function buildPanelKeys(state: BoardFilterState): PanelKeys {
  const date = `${state.dateRange.startDate}~${state.dateRange.endDate}`
  const branch = state.branch?.code ?? ''
  const supplier = state.supplier?.code ?? ''
  const product = state.product?.code ?? ''
  return {
    stores: JSON.stringify([date, state.scopeKey, supplier, product]),
    suppliers: JSON.stringify([date, state.scopeKey, branch, product]),
    products: JSON.stringify([
      date,
      state.scopeKey,
      branch,
      supplier,
      state.keyword.trim(),
      state.productSort.field,
      state.productSort.order,
      state.pageIndex,
      state.pageSize,
    ]),
    summary: JSON.stringify([date, state.scopeKey, branch, supplier, product]),
  }
}

/** 客户端短时缓存的键：包含全部请求参数，确保只复用完全相同的查询。 */
export function buildRequestKey(state: BoardFilterState): string {
  const keys = buildPanelKeys(state)
  return JSON.stringify([keys.summary, keys.products])
}

/**
 * 商品只属于一个国内供应商：改选成别的供应商时解除商品选中，避免隐藏条件让结果变空。
 */
export function resolveSupplierToggle(
  current: { supplier: BoardSelection | null; product: BoardSelection | null },
  next: BoardSelection,
): { supplier: BoardSelection | null; product: BoardSelection | null } {
  const supplier = current.supplier?.code === next.code ? null : next
  const product = supplier && current.product?.supplierCode && current.product.supplierCode !== supplier.code
    ? null
    : current.product
  return { supplier, product }
}

export function toggleSelection(current: BoardSelection | null, next: BoardSelection): BoardSelection | null {
  return current?.code === next.code ? null : next
}

/**
 * antd 受控排序回调 → 服务端排序参数。order 为空表示第三次点击，恢复默认的金额降序。
 */
export function resolveProductSort(columnKey: unknown, order: 'ascend' | 'descend' | null | undefined): ProductSort {
  if (!order) return defaultProductSort
  const field = columnKey === 'quantity' || columnKey === 'unitPrice' || columnKey === 'itemNumber' || columnKey === 'amount'
    ? columnKey
    : defaultProductSort.field
  return { field, order: order === 'ascend' ? 'asc' : 'desc' }
}

export function toAntdSortOrder(sort: ProductSort, field: CompactSalesBoardSortField): 'ascend' | 'descend' | null {
  if (sort.field !== field) return null
  return sort.order === 'asc' ? 'ascend' : 'descend'
}

export function describeProductSort(sort: ProductSort): string {
  const label = { amount: '金额', quantity: '数量', unitPrice: '单价', itemNumber: '货号' }[sort.field]
  return `按${label}${sort.order === 'asc' ? '升序' : '降序'}`
}

export function shareOf(value: number, total: number): number {
  return total > 0 && Number.isFinite(value) ? Math.max(0, value / total) : 0
}

export function formatShare(value: number, total: number): string {
  return total > 0 ? `${(shareOf(value, total) * 100).toFixed(1)}%` : '-'
}

/** 供应商栏的本地搜索：名称或代码包含关键词即可，不区分大小写。 */
export function matchesSupplierSearch(record: { supplierCode: string; supplierName: string }, search: string): boolean {
  const keyword = search.trim().toLowerCase()
  if (!keyword) return true
  return `${record.supplierName} ${record.supplierCode}`.toLowerCase().includes(keyword)
}

export function pageRange(pageIndex: number, pageSize: number, total: number): [number, number] {
  if (total <= 0) return [0, 0]
  const start = (pageIndex - 1) * pageSize + 1
  return [Math.min(start, total), Math.min(pageIndex * pageSize, total)]
}

/** Esc 清除筛选时不能抢走输入框、日期面板等控件自己的 Esc 行为。 */
export function shouldHandleEscape(target: EventTarget | null): boolean {
  if (!target || typeof (target as { closest?: unknown }).closest !== 'function') return true
  const element = target as Element
  return !element.closest('input, textarea, select, [contenteditable="true"], .ant-picker-dropdown, .ant-select-dropdown, .ant-modal')
}

/**
 * 导出表头的筛选说明，顺序与三栏一致（供应商 → 分店），再附搜索词。
 * 商品栏不被自身的选中商品收窄，所以选中的商品不写进说明。
 */
export function describeExportFilters(state: Pick<BoardFilterState, 'supplier' | 'branch' | 'keyword'>): string {
  const labelOf = (selection: BoardSelection) => selection.label && selection.label !== selection.code
    ? `${selection.label}（${selection.code}）`
    : selection.code
  const parts: string[] = []
  if (state.supplier) parts.push(`国内供应商 ${labelOf(state.supplier)}`)
  if (state.branch) parts.push(`分店 ${labelOf(state.branch)}`)
  const keyword = state.keyword.trim()
  if (keyword) parts.push(`搜索「${keyword}」`)
  return parts.length > 0 ? parts.join(' · ') : '全部国内供应商 · 全部分店'
}

type FlagStorage = Pick<Storage, 'getItem' | 'setItem'>

function defaultFlagStorage(): FlagStorage | null {
  return typeof window === 'undefined' ? null : window.localStorage
}

/** 每人记住的界面偏好（如底部统计展开）；隐私模式、禁用存储时读写会抛错，一律按默认值处理。 */
export function readStoredFlag(key: string, storage: () => FlagStorage | null = defaultFlagStorage): boolean {
  try {
    return storage()?.getItem(key) === '1'
  } catch {
    return false
  }
}

export function writeStoredFlag(key: string, value: boolean, storage: () => FlagStorage | null = defaultFlagStorage): void {
  try {
    storage()?.setItem(key, value ? '1' : '0')
  } catch {
    // 存储不可用时只是不记住偏好，不影响看板使用。
  }
}
