import type { LocationItem } from '../../../types/location'
import {
  buildComparableFilterTokens,
  buildTextFilterTokens,
  setFilterValues,
  type TextFilterMode,
  type WarehouseLocationColumnFilters,
} from './columnFilters'

/** 搜索框可选的检索字段：都对应列表接口 filters 里已有的文本条件。 */
export type LocationSearchField = 'locationCode' | 'locationBarcode' | 'productItemNumber' | 'productBarcode' | 'productName'

export const LOCATION_SEARCH_FIELDS: LocationSearchField[] = [
  'locationCode',
  'locationBarcode',
  'productItemNumber',
  'productBarcode',
  'productName',
]

/** 货位类型：后端约定 1 = 拣货位、2 = 存储位。 */
export const LOCATION_TYPE_PICKING = 1
export const LOCATION_TYPE_STORAGE = 2

export interface LocationListFilters {
  searchField: LocationSearchField
  /** 原列头文本筛选支持的匹配方式（包含/等于/开头是/结尾是），收进「更多筛选」。 */
  searchMode: TextFilterMode
  keyword: string
  locationType?: number
  usage?: boolean
  status?: number
  updatedBy?: string
  updatedAtFrom?: string
  updatedAtTo?: string
  /** 区/排快捷筛选：来自货位分布卡片与货架图。 */
  zone?: string
  zoneRow?: string
}

export const EMPTY_LOCATION_FILTERS: LocationListFilters = {
  searchField: 'locationCode',
  searchMode: 'contains',
  keyword: '',
}

export function buildLocationCodePrefix(zone?: string, zoneRow?: string) {
  if (!zone) return undefined
  return zoneRow ? `${zone}-${zoneRow}-` : `${zone}-`
}

/**
 * 把工具栏状态转换成列表接口的 filters（沿用列头筛选的 token 约定）。
 * 区/排快捷筛选用货位代码「开头是」，与搜索框里的货位代码条件同时生效：
 * 服务端对同一字段的多个条件逐个叠加（取交集）。
 */
export function buildLocationListColumnFilters(filters: LocationListFilters): WarehouseLocationColumnFilters {
  const keyword = filters.keyword.trim()
  const prefix = buildLocationCodePrefix(filters.zone, filters.zoneRow)
  let next: WarehouseLocationColumnFilters = {}

  next = setFilterValues(next, 'locationCode', [
    ...(prefix ? buildTextFilterTokens('starts', prefix) : []),
    ...(filters.searchField === 'locationCode' ? buildTextFilterTokens(filters.searchMode, keyword) : []),
  ])
  if (filters.searchField !== 'locationCode') {
    next = setFilterValues(next, filters.searchField, buildTextFilterTokens(filters.searchMode, keyword))
  }
  next = setFilterValues(next, 'locationType', filters.locationType === undefined ? undefined : [filters.locationType])
  next = setFilterValues(next, 'usage', filters.usage === undefined ? undefined : [filters.usage])
  next = setFilterValues(next, 'status', filters.status === undefined ? undefined : [filters.status])
  next = setFilterValues(next, 'updatedBy', buildTextFilterTokens('contains', filters.updatedBy))
  next = setFilterValues(
    next,
    'updatedAt',
    buildComparableFilterTokens('range', { min: filters.updatedAtFrom, max: filters.updatedAtTo }),
  )
  return next
}

/** 「更多筛选」里生效的条件数，用于按钮角标。 */
export function countMoreLocationFilters(filters: LocationListFilters) {
  let count = 0
  if (filters.status !== undefined) count += 1
  if (filters.updatedBy?.trim()) count += 1
  if (filters.updatedAtFrom || filters.updatedAtTo) count += 1
  if (filters.searchMode !== 'contains') count += 1
  return count
}

export interface LocationCodeParts {
  zone: string
  rowText: string
  row: number
  columnText: string
  column: number
  levelText: string
  level: number
}

// 货位编码约定「区-排-列-层」，如 A-03-12-02；区可以是字母或数字，其余三段是数字。
const LOCATION_CODE_PATTERN = /^([A-Za-z0-9]+)-(\d+)-(\d+)-(\d+)$/

export function parseLocationCode(code?: string | null): LocationCodeParts | undefined {
  const match = LOCATION_CODE_PATTERN.exec(code?.trim() ?? '')
  if (!match) return undefined
  const [, zone, rowText, columnText, levelText] = match
  return {
    zone: zone.toUpperCase(),
    rowText,
    row: Number(rowText),
    columnText,
    column: Number(columnText),
    levelText,
    level: Number(levelText),
  }
}

/** 分布统计只需要编码、启用与是否有货，拉全量后立即压缩，避免长时间持有商品明细。 */
export interface LocationDistributionEntry {
  code: string
  enabled: boolean
  used: boolean
}

export function toDistributionEntry(item: LocationItem): LocationDistributionEntry {
  return {
    code: item.locationCode?.trim() ?? '',
    // 与列表「启用/停用」口径一致：只有 status === 1 算启用。
    enabled: item.status === 1,
    // 列表接口只返回未删除商品的有效绑定，与后端 IsUsed 口径一致。
    used: (item.products?.length ?? 0) > 0,
  }
}

export const OTHER_ZONE_KEY = '__other__'

export interface LocationZoneSummary {
  /** 区代码；编码不符合约定的归入 OTHER_ZONE_KEY。 */
  key: string
  total: number
  used: number
  disabled: number
}

const zoneCollator = new Intl.Collator('en', { numeric: true, sensitivity: 'base' })

function emptyZone(key: string): LocationZoneSummary {
  return { key, total: 0, used: 0, disabled: 0 }
}

function addToZone(summary: LocationZoneSummary, entry: LocationDistributionEntry) {
  summary.total += 1
  if (entry.used) summary.used += 1
  if (!entry.enabled) summary.disabled += 1
}

/** 每区货位数、已用数、停用数；「其他」放最后。 */
export function summarizeLocationZones(entries: readonly LocationDistributionEntry[]) {
  const all = emptyZone('all')
  const zoneMap = new Map<string, LocationZoneSummary>()
  for (const entry of entries) {
    addToZone(all, entry)
    const key = parseLocationCode(entry.code)?.zone ?? OTHER_ZONE_KEY
    const summary = zoneMap.get(key) ?? emptyZone(key)
    addToZone(summary, entry)
    zoneMap.set(key, summary)
  }

  const zones = Array.from(zoneMap.values()).sort((left, right) => {
    if (left.key === OTHER_ZONE_KEY) return 1
    if (right.key === OTHER_ZONE_KEY) return -1
    return zoneCollator.compare(left.key, right.key)
  })
  return { all, zones }
}

export function getUsageRatePercent(summary: Pick<LocationZoneSummary, 'total' | 'used'>) {
  return summary.total > 0 ? Math.round((summary.used / summary.total) * 100) : 0
}

export type RackCellState = 'full' | 'partial' | 'empty' | 'off'

export interface RackCell {
  columnText: string
  column: number
  levels: number
  enabledLevels: number
  usedLevels: number
  disabledLevels: number
  state: RackCellState
}

export interface RackRow {
  rowText: string
  row: number
  /** 与 columns 一一对应；该列没有任何货位时为 null（货架上的空缺）。 */
  cells: Array<RackCell | null>
}

export interface ZoneRack {
  zone: string
  columns: number[]
  rows: RackRow[]
}

/** 列号跨度超过这个值就只画实际出现的列，避免编码异常时画出几百格。 */
const MAX_CONTIGUOUS_RACK_COLUMNS = 60

/**
 * 一格 = 一列（含各层）：按启用的层判断——各层都有货为全满、部分层空为部分空、
 * 都没货为整列空；整列都停用才显示停用。
 */
export function getRackCellState(enabledLevels: number, usedLevels: number): RackCellState {
  if (enabledLevels === 0) return 'off'
  if (usedLevels >= enabledLevels) return 'full'
  if (usedLevels === 0) return 'empty'
  return 'partial'
}

export function buildZoneRack(entries: readonly LocationDistributionEntry[], zone: string): ZoneRack | undefined {
  const rowMap = new Map<string, { row: number; columns: Map<number, Omit<RackCell, 'state'>> }>()
  const columnNumbers = new Set<number>()

  for (const entry of entries) {
    const parts = parseLocationCode(entry.code)
    if (!parts || parts.zone !== zone) continue

    const rowGroup = rowMap.get(parts.rowText) ?? { row: parts.row, columns: new Map() }
    const cell = rowGroup.columns.get(parts.column) ?? {
      columnText: parts.columnText,
      column: parts.column,
      levels: 0,
      enabledLevels: 0,
      usedLevels: 0,
      disabledLevels: 0,
    }
    cell.levels += 1
    if (entry.enabled) {
      cell.enabledLevels += 1
      if (entry.used) cell.usedLevels += 1
    } else {
      cell.disabledLevels += 1
    }
    rowGroup.columns.set(parts.column, cell)
    rowMap.set(parts.rowText, rowGroup)
    columnNumbers.add(parts.column)
  }

  if (!rowMap.size) return undefined

  const sortedColumns = Array.from(columnNumbers).sort((left, right) => left - right)
  const minColumn = sortedColumns[0]
  const maxColumn = sortedColumns[sortedColumns.length - 1]
  const columns =
    maxColumn - minColumn + 1 <= MAX_CONTIGUOUS_RACK_COLUMNS
      ? Array.from({ length: maxColumn - minColumn + 1 }, (_, index) => minColumn + index)
      : sortedColumns

  const rows = Array.from(rowMap.entries())
    .sort(([leftText, left], [rightText, right]) => left.row - right.row || leftText.localeCompare(rightText))
    .map(([rowText, group]) => ({
      rowText,
      row: group.row,
      cells: columns.map((column) => {
        const cell = group.columns.get(column)
        return cell ? { ...cell, state: getRackCellState(cell.enabledLevels, cell.usedLevels) } : null
      }),
    }))

  return { zone, columns, rows }
}

export const DISTRIBUTION_PAGE_SIZE = 1000
export const DISTRIBUTION_MAX_PAGES = 10

export interface LocationDistributionResult {
  entries: LocationDistributionEntry[]
  total: number
  /** 货位数超过 pageSize × maxPages 时只统计了前一部分。 */
  truncated: boolean
}

/**
 * 分页读取全部货位做分布统计：每页 1000 个顺序读取（单次 IN 列表与传输量都可控），
 * 按 locationGuid 去重；isCancelled 为真时立即停止，避免收起或卸载后继续占用后端。
 */
export async function collectLocationDistribution(
  fetchPage: (pageNumber: number, pageSize: number) => Promise<{ items: LocationItem[]; total: number }>,
  options: {
    pageSize?: number
    maxPages?: number
    isCancelled?: () => boolean
    onProgress?: (loaded: number, total: number) => void
  } = {},
): Promise<LocationDistributionResult | undefined> {
  const pageSize = options.pageSize ?? DISTRIBUTION_PAGE_SIZE
  const maxPages = options.maxPages ?? DISTRIBUTION_MAX_PAGES
  const entryMap = new Map<string, LocationDistributionEntry>()

  const first = await fetchPage(1, pageSize)
  if (options.isCancelled?.()) return undefined
  first.items.forEach((item) => entryMap.set(item.locationGuid, toDistributionEntry(item)))
  const total = first.total
  options.onProgress?.(entryMap.size, total)

  const pagesNeeded = Math.ceil(total / pageSize)
  const lastPage = Math.min(pagesNeeded, maxPages)
  for (let pageNumber = 2; pageNumber <= lastPage; pageNumber += 1) {
    const result = await fetchPage(pageNumber, pageSize)
    if (options.isCancelled?.()) return undefined
    result.items.forEach((item) => entryMap.set(item.locationGuid, toDistributionEntry(item)))
    options.onProgress?.(entryMap.size, total)
    if (result.items.length < pageSize) break
  }

  return {
    entries: Array.from(entryMap.values()),
    total,
    truncated: pagesNeeded > maxPages,
  }
}
