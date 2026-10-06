// 前缀管理列表页的纯逻辑：查询参数合并、状态筛选互转、排序键白名单、时间文本。
import type { PrefixCodeListParams } from '../../../types/productPrefixCode'

export type PrefixListQuery = PrefixCodeListParams & {
  page: number
  pageSize: number
}

/** 状态分段：全部 / 启用 / 停用（统一用「停用」，旧页有「禁用」）。 */
export type PrefixStatusFilter = 'all' | 'active' | 'inactive'

export function statusFilterToIsActive(value: PrefixStatusFilter): boolean | undefined {
  return value === 'all' ? undefined : value === 'active'
}

/**
 * 后端 GetAllPrefixesAsync 认的排序键（忽略大小写）：prefixName / supplierName / supplierCode / isActive / sortOrder / createdAt。
 * 页面只给前缀、供应商、排序三列开排序；「更新时间」后端不支持，不给箭头，避免点了没反应。
 */
export const PREFIX_SORT_FIELDS = ['prefixName', 'supplierName', 'sortOrder'] as const
export type PrefixSortField = (typeof PREFIX_SORT_FIELDS)[number]

export type PrefixTableSortOrder = 'ascend' | 'descend' | null | undefined

/** 把 antd 表头排序结果换成后端参数；不在白名单内的列一律视为「不排序」。 */
export function resolvePrefixSort(
  field: unknown,
  order: PrefixTableSortOrder,
): Pick<PrefixListQuery, 'sortField' | 'sortDirection'> {
  const matched = PREFIX_SORT_FIELDS.find((item) => item === field)
  if (!matched || !order) {
    return { sortField: undefined, sortDirection: undefined }
  }
  return { sortField: matched, sortDirection: order === 'ascend' ? 'asc' : 'desc' }
}

/** 表头需要的 sortOrder：当前排序列显示箭头，其余列清空。 */
export function toTableSortOrder(
  query: Pick<PrefixListQuery, 'sortField' | 'sortDirection'>,
  field: PrefixSortField,
): 'ascend' | 'descend' | null {
  if (query.sortField !== field) {
    return null
  }
  return query.sortDirection === 'asc' ? 'ascend' : 'descend'
}

/**
 * 合并查询参数：overrides 里显式写 undefined 表示「清除该筛选」。
 * 必须用对象展开合并，不能用默认参数 `load(x = state)`，否则显式 undefined 会被默认值吞掉，「清除筛选」就失效了。
 */
export function mergePrefixListQuery(base: PrefixListQuery, overrides: Partial<PrefixListQuery> = {}): PrefixListQuery {
  return { ...base, ...overrides }
}

/** 是否有任何筛选条件生效（排序和分页不算），用于区分「没有数据」和「没有符合条件的数据」。 */
export function hasActivePrefixFilters(query: Pick<PrefixListQuery, 'search' | 'supplierCode' | 'isActive'>): boolean {
  return Boolean(query.search) || Boolean(query.supplierCode) || query.isActive !== undefined
}

/** 删除一行后，若当前页已空且不在第一页，应回退一页，避免停在空白页。 */
export function pageAfterRemoval(currentPage: number, rowCountOnPage: number): number {
  return rowCountOnPage <= 1 && currentPage > 1 ? currentPage - 1 : currentPage
}

/**
 * 更新时间文本：后端返回不带时区的本地时间文本，只做文本层面整理，不经时区换算。
 * 当年显示 `MM-DD HH:mm`（设计稿写法），跨年显示完整日期，避免去年和今年的同月同日混淆。
 */
export function formatPrefixTimestamp(value?: string, currentYear: number = new Date().getFullYear()): string {
  const match = value?.match(/^(\d{4})-(\d{2})-(\d{2})[T ](\d{2}):(\d{2})/)
  if (!match) {
    return value || '--'
  }
  const [, year, month, day, hour, minute] = match
  return Number(year) === currentYear ? `${month}-${day} ${hour}:${minute}` : `${year}-${month}-${day} ${hour}:${minute}`
}

/** 展开行里的国内价。 */
export function formatPrefixProductPrice(value?: number): string {
  return typeof value === 'number' && Number.isFinite(value) ? `¥ ${value.toFixed(2)}` : '--'
}
