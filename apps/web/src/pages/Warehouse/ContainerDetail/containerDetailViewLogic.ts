/**
 * 货柜明细重设计（概况卡、提交前检查、保存状态条、列视图、紧凑筛选、已生效筛选条）用到的纯函数。
 *
 * 职责边界：
 * - 只做展示层的派生计算：列视图可见列、标签分组切换、行问题标签、概况事实格、草稿分类计数、筛选条摘要
 * - 不发请求、不碰保存/草稿/并发冲突的任何语义；筛选口径完全复用 containerDetailLogic 里的既有判断
 */
import type { Dayjs } from 'dayjs'
import type { ContainerDetail, ContainerMain } from '../../../types/container'
import {
  describeEtaHint,
  getIsoWeekNumber,
  getLoadRatePercent,
  parseContainerDate,
  type EtaHint,
} from '../Containers/containersLogic'
import type { WarehouseCategoryLookup } from '../Products/categoryPath'
import { CONTAINER_DETAIL_AUTO_SAVE_FIELDS, type ContainerDetailAutoSavePatchMap } from './containerDetailAutoSaveQueue'
import {
  deriveContainerFreightInput,
  getContainerDetailMatchType,
  getSubmittedContainerDetailFields,
  matchesContainerDetailTagFilter,
  normalizeContainerFreightInput,
  type ContainerDetailColumnFilters,
  type ContainerDetailLoadMode,
  type ContainerDetailNumberRangeFilter,
  type ContainerDetailTableColumnKey,
  type ContainerDetailTagFilter,
  type ContainerDetailTagStats,
  type PendingContainerDetailPatchMap,
} from './containerDetailLogic'

// ---------------------------------------------------------------------------
// 列视图：只控制列可见性的预设，列顺序与列宽仍沿用原有拖拽/调宽设置
// ---------------------------------------------------------------------------

export type ContainerDetailColumnView = 'cost' | 'pricing' | 'all'

export const CONTAINER_DETAIL_COLUMN_VIEWS: readonly ContainerDetailColumnView[] = ['cost', 'pricing', 'all']
// 默认打开「全部列」。存储 key 升到 v2：旧 v1 里多半记着原默认的「成本核算」，不升级的话老用户看不到新默认。
export const DEFAULT_CONTAINER_DETAIL_COLUMN_VIEW: ContainerDetailColumnView = 'all'
export const CONTAINER_DETAIL_COLUMN_VIEW_STORAGE_KEY = 'hbweb_rv.containerDetail.columnView.v2'

/**
 * 业务列的默认顺序（「全部列」视图看到的就是这个顺序，「重置列」也恢复到这里）：
 * 编号、图片、货号、英文名称、中文名称、分类、国内价格、仓库状态、运输成本、调整浮率、
 * 实时进货价、进口价格、实时零售价、零售价，其余列按原有相对顺序排在后面。
 * 合成列 product 固定在最前（左固定列），issues 放最后；它们只在「成本核算 / 上架定价」出现。
 */
export const CONTAINER_DETAIL_DEFAULT_COLUMN_ORDER: readonly ContainerDetailTableColumnKey[] = [
  'product',
  'index',
  'image',
  'itemNumber',
  'englishName',
  'productName',
  'categoryName',
  'domesticPrice',
  'warehouseStatus',
  'transportCost',
  'floatRate',
  'warehouseImportPrice',
  'importPrice',
  'lastOEMPrice',
  'oemPrice',
  // 以下为「其他列」
  'containerPieces',
  'packingQuantity',
  'containerQuantity',
  'unitVolume',
  'unitTransportCost',
  'middlePackQuantity',
  'newProduct',
  'productType',
  'matchType',
  'barcode',
  'remark',
  'issues',
]

/** 按默认列顺序排列列 key；不在默认清单里的列（将来新增）保持原相对顺序，排在已知列之后。 */
export function sortContainerDetailColumnKeysByDefaultOrder(
  keys: readonly ContainerDetailTableColumnKey[],
): ContainerDetailTableColumnKey[] {
  const rank = new Map(CONTAINER_DETAIL_DEFAULT_COLUMN_ORDER.map((key, index) => [key, index]))
  return keys
    .map((key, index) => ({ key, index, rank: rank.get(key) ?? Number.MAX_SAFE_INTEGER }))
    .sort((a, b) => a.rank - b.rank || a.index - b.index)
    .map(({ key }) => key)
}

/** 只在「成本核算 / 上架定价」出现的合成列；「全部列」保持原 26 列不变。 */
export const CONTAINER_DETAIL_VIEW_ONLY_COLUMN_KEYS: readonly ContainerDetailTableColumnKey[] = ['product', 'issues']

const CONTAINER_DETAIL_VIEW_COLUMN_KEYS: Record<Exclude<ContainerDetailColumnView, 'all'>, readonly ContainerDetailTableColumnKey[]> = {
  cost: [
    'product',
    'containerPieces',
    'packingQuantity',
    'containerQuantity',
    'unitVolume',
    'domesticPrice',
    'unitTransportCost',
    'floatRate',
    'importPrice',
    'issues',
  ],
  // 未拖拽过列时按这里的顺序展示，与调整默认列顺序之前该视图的显示顺序一致。
  pricing: [
    'product',
    'englishName',
    'categoryName',
    'warehouseImportPrice',
    'importPrice',
    'oemPrice',
    'matchType',
    'warehouseStatus',
  ],
}

/**
 * 「成本核算 / 上架定价」两个视图的默认列宽。沿用「全部列」的列宽时合计约 1230px，1440 宽屏会把最右的「问题」列挤出视野；
 * 这里按视图重新分配，合计（不含 56px 勾选列）不超过 1064px，给表体纵向滚动条留出位置。用户手动拖过的列宽仍优先生效，「全部列」保持原列宽。
 */
export const CONTAINER_DETAIL_VIEW_DEFAULT_COLUMN_WIDTHS: Record<
  Exclude<ContainerDetailColumnView, 'all'>,
  Partial<Record<ContainerDetailTableColumnKey, number>>
> = {
  cost: {
    product: 248,
    containerPieces: 68,
    packingQuantity: 84,
    containerQuantity: 80,
    unitVolume: 88,
    domesticPrice: 88,
    unitTransportCost: 88,
    floatRate: 80,
    importPrice: 112,
    issues: 124,
  },
  pricing: {
    product: 248,
    englishName: 164,
    categoryName: 140,
    importPrice: 104,
    oemPrice: 104,
    warehouseImportPrice: 96,
    matchType: 124,
    warehouseStatus: 84,
  },
}

export function getContainerDetailViewDefaultColumnWidth(
  view: ContainerDetailColumnView,
  key: ContainerDetailTableColumnKey,
): number | undefined {
  return view === 'all' ? undefined : CONTAINER_DETAIL_VIEW_DEFAULT_COLUMN_WIDTHS[view][key]
}

export function normalizeContainerDetailColumnView(value: unknown): ContainerDetailColumnView {
  return CONTAINER_DETAIL_COLUMN_VIEWS.includes(value as ContainerDetailColumnView)
    ? value as ContainerDetailColumnView
    : DEFAULT_CONTAINER_DETAIL_COLUMN_VIEW
}

/**
 * 按视图从「当前列顺序」里挑出可见列。
 * - 全部列：原列顺序原样保留，只去掉视图专用的合成列
 * - 其他视图：本视图的列被拖拽调过相对顺序时按当前列顺序过滤；没调过时按视图自身的列清单顺序
 *   （默认列顺序是按「全部列」排的，成本核算若跟着它走，装柜件数等会被挤到进口价之后）。
 *   只比较本视图的列：其他列（含将来追加到末尾的新列）怎么排都不影响本视图。
 *   最后都把固定在左侧的列挪到最前（AntD 要求左固定列必须在最左边）
 */
export function resolveContainerDetailViewColumnKeys(
  view: ContainerDetailColumnView,
  orderedKeys: readonly ContainerDetailTableColumnKey[],
  fixedLeftKeys: ReadonlySet<ContainerDetailTableColumnKey> = new Set(),
): ContainerDetailTableColumnKey[] {
  if (view === 'all') {
    return orderedKeys.filter((key) => !CONTAINER_DETAIL_VIEW_ONLY_COLUMN_KEYS.includes(key))
  }
  const viewKeys = CONTAINER_DETAIL_VIEW_COLUMN_KEYS[view]
  const visible = new Set(viewKeys)
  const orderedViewKeys = orderedKeys.filter((key) => visible.has(key))
  const defaultViewKeys = sortContainerDetailColumnKeysByDefaultOrder(orderedViewKeys)
  const isReordered = orderedViewKeys.some((key, index) => key !== defaultViewKeys[index])
  const available = new Set(orderedViewKeys)
  const keys = isReordered ? orderedViewKeys : viewKeys.filter((key) => available.has(key))
  return [
    ...keys.filter((key) => fixedLeftKeys.has(key)),
    ...keys.filter((key) => !fixedLeftKeys.has(key)),
  ]
}

/**
 * 在「成本核算 / 上架定价」里拖拽前，先把全局列顺序里本视图各列占的位置按当前显示顺序重新填一遍，
 * 再做移动；否则视图按自身清单顺序显示时，拖拽结果会和用户看到的顺序对不上。其他列的位置不动。
 */
export function alignContainerDetailColumnOrderToView(
  order: readonly ContainerDetailTableColumnKey[],
  displayedKeys: readonly ContainerDetailTableColumnKey[],
): ContainerDetailTableColumnKey[] {
  const present = new Set(order)
  const queue = displayedKeys.filter((key) => present.has(key))
  const displayed = new Set(queue)
  let cursor = 0
  return order.map((key) => (displayed.has(key) ? queue[cursor++] : key))
}

// ---------------------------------------------------------------------------
// 分类列：无论界面语言，表格里一律显示分类英文名
// ---------------------------------------------------------------------------

function getLastCategoryPathSegment(path: string | undefined) {
  return path?.split(' > ').pop()?.trim() || undefined
}

/**
 * 分类英文名：优先按分类 GUID 从分类树取，其次按名称（中英文名都能反查）取；
 * 分类树里没有英文名时 formatWarehouseCategoryNodeName 会回退中文名，分类树也查不到就原样显示后端返回的名称。
 */
export function resolveContainerDetailCategoryEnglishName(
  record: { categoryName?: string; warehouseCategoryGUID?: string },
  lookup: Pick<WarehouseCategoryLookup, 'displayByGuid' | 'displayByName'>,
): string | undefined {
  if (record.warehouseCategoryGUID) {
    const byGuid = getLastCategoryPathSegment(lookup.displayByGuid.get(record.warehouseCategoryGUID)?.en)
    if (byGuid) return byGuid
  }
  const name = record.categoryName?.trim()
  if (!name) return undefined
  // 同名分类可能挂在不同父级下，但末级名称一致，取第一条路径即可。
  return getLastCategoryPathSegment(lookup.displayByName.get(name)?.en?.[0]) || name
}

// ---------------------------------------------------------------------------
// 标签筛选分组：组内并集、组间交集的口径由 matchesContainerDetailSelectedTags 负责，这里只换组内选择
// ---------------------------------------------------------------------------

export const CONTAINER_DETAIL_NEW_STATE_TAGS = ['new', 'existing'] as const satisfies readonly ContainerDetailTagFilter[]
export const CONTAINER_DETAIL_PRODUCT_TYPE_TAGS = ['normal', 'set', 'multi', 'setChild'] as const satisfies readonly ContainerDetailTagFilter[]
export const CONTAINER_DETAIL_WAREHOUSE_STATUS_TAGS = ['active', 'inactive'] as const satisfies readonly ContainerDetailTagFilter[]
export const CONTAINER_DETAIL_CHECK_TAGS = ['noOemPrice', 'abnormalImport'] as const satisfies readonly ContainerDetailTagFilter[]
/** 涨跌：本次进口价格对比仓库实时进货价，口径同进口价格列的涨跌箭头（任一价格缺失或相等都不算涨跌）。 */
export const CONTAINER_DETAIL_PRICE_TREND_TAGS = ['priceUp', 'priceDown'] as const satisfies readonly ContainerDetailTagFilter[]

export type ContainerDetailTagGroup = readonly ContainerDetailTagFilter[]

export function getContainerDetailTagGroupSelection(
  selected: readonly ContainerDetailTagFilter[],
  group: ContainerDetailTagGroup,
): ContainerDetailTagFilter[] {
  return selected.filter((tag) => group.includes(tag))
}

/** 用 nextValues 整体替换某一组的已选标签，其他组保持原顺序不动。 */
export function replaceContainerDetailTagGroup(
  selected: readonly ContainerDetailTagFilter[],
  group: ContainerDetailTagGroup,
  nextValues: readonly ContainerDetailTagFilter[],
): ContainerDetailTagFilter[] {
  const others = selected.filter((tag) => tag !== 'all' && !group.includes(tag))
  const next = group.filter((tag) => nextValues.includes(tag))
  return [...others, ...next]
}

/**
 * 分段控件的当前值：组内恰好选了一个时就是它；没选或全选（并集等于不过滤）都视为「全部」。
 */
export function resolveContainerDetailTagSegmentValue(
  selected: readonly ContainerDetailTagFilter[],
  group: ContainerDetailTagGroup,
): ContainerDetailTagFilter | 'all' {
  const inGroup = getContainerDetailTagGroupSelection(selected, group)
  return inGroup.length === 1 ? inGroup[0] : 'all'
}

// ---------------------------------------------------------------------------
// 「匹配待确认」= 列头「匹配方式」筛选里的候选需确认（supplierItem），全量与分页两种模式都已支持
// ---------------------------------------------------------------------------

export function isContainerDetailMatchPendingFilterActive(filters: ContainerDetailColumnFilters) {
  return Boolean(filters.matchTypes?.includes('supplierItem'))
}

export function toggleContainerDetailMatchPendingFilter(filters: ContainerDetailColumnFilters): ContainerDetailColumnFilters {
  if (isContainerDetailMatchPendingFilterActive(filters)) {
    const rest = (filters.matchTypes ?? []).filter((value) => value !== 'supplierItem')
    const next = { ...filters }
    if (rest.length) {
      next.matchTypes = rest
    } else {
      delete next.matchTypes
    }
    return next
  }
  // 点选即只看待确认的候选，不与列头里已有的其他匹配方式并在一起，避免列表看起来没变化。
  return { ...filters, matchTypes: ['supplierItem'] }
}

// ---------------------------------------------------------------------------
// 行问题标签：缺零售价 / 进口价缺失 / 匹配待确认，口径与标签统计一致
// ---------------------------------------------------------------------------

export type ContainerDetailRowIssue = 'missingRetailPrice' | 'missingImportPrice' | 'matchPending'

export function getContainerDetailRowIssues(row: ContainerDetail): ContainerDetailRowIssue[] {
  const issues: ContainerDetailRowIssue[] = []
  if (matchesContainerDetailTagFilter(row, 'noOemPrice')) issues.push('missingRetailPrice')
  // 原「进口价异常」标签只判断进口价为空或 ≤ 0，这里沿用同一口径，只是名称改为「进口价缺失」。
  if (matchesContainerDetailTagFilter(row, 'abnormalImport')) issues.push('missingImportPrice')
  if (getContainerDetailMatchType(row) === 'supplierItem') issues.push('matchPending')
  return issues
}

// ---------------------------------------------------------------------------
// 概况卡：整柜口径的商品构成统计
// ---------------------------------------------------------------------------

export function hasContainerDetailColumnFilterValues(filters: ContainerDetailColumnFilters) {
  return Object.values(filters).some((value) => {
    if (Array.isArray(value)) return value.length > 0
    if (value && typeof value === 'object') {
      const range = value as ContainerDetailNumberRangeFilter
      return range.min != null || range.max != null
    }
    return typeof value === 'string' ? Boolean(value.trim()) : value != null
  })
}

export interface ContainerDetailOverviewStatsInput {
  loadMode: ContainerDetailLoadMode
  /** 全量模式下按全部已加载行（未经任何筛选）算出的统计。 */
  allRowsStats: ContainerDetailTagStats
  remoteStats: ContainerDetailTagStats | null
  /** 远程统计是否对应当前筛选范围（统计 key 已与当前查询一致）。 */
  remoteStatsIsCurrent: boolean
  hasColumnFilters: boolean
  cachedStats: ContainerDetailTagStats | null
}

/**
 * 概况卡展示整柜构成，不能随列头筛选变化：
 * - 全量模式直接用全部行本地统计
 * - 分页模式只有「无列头筛选且统计已对应当前查询」时，服务端统计才等于整柜口径（统计本身不含标签筛选）
 * - 其他时刻沿用上一次拿到的整柜统计；从未拿到就返回 null（界面显示 --）
 * 返回 cacheable=true 时调用方应把 stats 记为该货柜的整柜统计缓存。
 */
export function resolveContainerDetailOverviewStats({
  loadMode,
  allRowsStats,
  remoteStats,
  remoteStatsIsCurrent,
  hasColumnFilters,
  cachedStats,
}: ContainerDetailOverviewStatsInput): { stats: ContainerDetailTagStats | null; cacheable: boolean } {
  if (loadMode === 'full') return { stats: allRowsStats, cacheable: true }
  if (loadMode === 'paged' && remoteStats && remoteStatsIsCurrent && !hasColumnFilters) {
    return { stats: remoteStats, cacheable: true }
  }
  return { stats: cachedStats, cacheable: false }
}

/** 预计到岸提示，口径与货柜列表一致（只有未完成且未到货的货柜才提示逾期/今天/还有几天）。 */
export type ContainerDetailEtaHint = EtaHint

export interface ContainerDetailOverviewFacts {
  loadingDate?: string
  loadingWeek?: number
  etaDate?: string
  etaHint: EtaHint
  actualArrivalDate?: string
  /** 按 68 m³ 标准柜折算的运费报价（= 运费 × 68 ÷ 总体积），总体积无效时为空。 */
  standard68Freight?: number
  loadRatePercent?: number
}

export function buildContainerDetailOverviewFacts(
  container: Pick<ContainerMain, '状态' | '装柜日期' | '预计到岸日期' | '实际到货日期' | '运费' | '总体积'> | null | undefined,
  today: Dayjs,
): ContainerDetailOverviewFacts {
  if (!container) return { etaHint: { kind: 'none' } }
  const loading = parseContainerDate(container.装柜日期)
  const eta = parseContainerDate(container.预计到岸日期)
  const actualArrival = parseContainerDate(container.实际到货日期)
  return {
    loadingDate: loading?.format('YYYY-MM-DD'),
    loadingWeek: loading ? getIsoWeekNumber(loading) : undefined,
    etaDate: eta?.format('YYYY-MM-DD'),
    etaHint: describeEtaHint(container, today),
    actualArrivalDate: actualArrival?.format('YYYY-MM-DD'),
    standard68Freight: normalizeContainerFreightInput(
      deriveContainerFreightInput(container.运费, container.总体积, 'standard68'),
      'standard68',
    ),
    loadRatePercent: getLoadRatePercent(container.总体积),
  }
}

// ---------------------------------------------------------------------------
// 保存状态条：把「需点保存明细」的本机草稿和「失焦自动保存」区分开
// ---------------------------------------------------------------------------

export interface ContainerDetailManualDraftSummary {
  total: number
  importPrice: number
  retailPrice: number
  englishName: number
  other: number
}

const CONTAINER_DETAIL_AUTO_SAVE_FIELD_SET = new Set<string>(CONTAINER_DETAIL_AUTO_SAVE_FIELDS)

/**
 * 自动保存字段也会先写进同一份本机草稿；它们还在自动保存队列里（排队、发送中或失败待重试）时，
 * 由「自动保存」状态展示，不算进「N 项待保存」。其余字段（进口价、零售价、英文名称，以及恢复出来、
 * 不在队列里的旧字段）都只能靠「保存明细」落库。
 */
export function summarizeContainerDetailManualDraft(
  pendingPatches: PendingContainerDetailPatchMap,
  autoSaveUnsettledPatches: ContainerDetailAutoSavePatchMap,
): ContainerDetailManualDraftSummary {
  const summary: ContainerDetailManualDraftSummary = { total: 0, importPrice: 0, retailPrice: 0, englishName: 0, other: 0 }
  Object.values(pendingPatches).forEach((patch) => {
    const unsettled = autoSaveUnsettledPatches[patch.hguid] ?? {}
    getSubmittedContainerDetailFields(patch).forEach((field) => {
      if (CONTAINER_DETAIL_AUTO_SAVE_FIELD_SET.has(field) && field in unsettled) return
      summary.total += 1
      if (field === '进口价格') summary.importPrice += 1
      else if (field === '贴牌价格') summary.retailPrice += 1
      else if (field === '英文名称') summary.englishName += 1
      else summary.other += 1
    })
  })
  return summary
}

// ---------------------------------------------------------------------------
// 已生效筛选条：把列头筛选与排序整理成可逐个移除的条目
// ---------------------------------------------------------------------------

export type ContainerDetailColumnFilterKey = keyof ContainerDetailColumnFilters

export type ContainerDetailColumnFilterDescriptor =
  | { key: ContainerDetailColumnFilterKey; kind: 'text'; value: string }
  | { key: ContainerDetailColumnFilterKey; kind: 'range'; min?: number; max?: number }
  | { key: ContainerDetailColumnFilterKey; kind: 'enum'; values: string[] }

/** 条目顺序跟表格默认列顺序（CONTAINER_DETAIL_DEFAULT_COLUMN_ORDER）一致，便于对照列头。 */
export const CONTAINER_DETAIL_COLUMN_FILTER_ORDER: readonly ContainerDetailColumnFilterKey[] = [
  'itemNumber',
  'englishName',
  'productName',
  'domesticPrice',
  'warehouseStatus',
  'transportCost',
  'floatRate',
  'warehouseImportPrice',
  'importPrice',
  'lastOEMPrice',
  'oemPrice',
  'containerPieces',
  'packingQuantity',
  'containerQuantity',
  'unitVolume',
  'unitTransportCost',
  'middlePackQuantity',
  'newProductStates',
  'productTypes',
  'matchTypes',
  'barcode',
  'remark',
]

export function describeContainerDetailColumnFilters(filters: ContainerDetailColumnFilters): ContainerDetailColumnFilterDescriptor[] {
  return CONTAINER_DETAIL_COLUMN_FILTER_ORDER.flatMap((key): ContainerDetailColumnFilterDescriptor[] => {
    const value = filters[key]
    if (value == null) return []
    if (typeof value === 'string') {
      return value.trim() ? [{ key, kind: 'text', value: value.trim() }] : []
    }
    if (Array.isArray(value)) {
      return value.length ? [{ key, kind: 'enum', values: [...value] }] : []
    }
    const range = value as ContainerDetailNumberRangeFilter
    return range.min != null || range.max != null ? [{ key, kind: 'range', min: range.min, max: range.max }] : []
  })
}

export function removeContainerDetailColumnFilter(
  filters: ContainerDetailColumnFilters,
  key: ContainerDetailColumnFilterKey,
): ContainerDetailColumnFilters {
  const next = { ...filters }
  delete next[key]
  return next
}

// ---------------------------------------------------------------------------
// 工具栏搜索：后端只支持按单列文字筛选（各列之间是交集），所以搜索框带字段选择，直接写入对应列头筛选
// ---------------------------------------------------------------------------

export type ContainerDetailSearchField = 'itemNumber' | 'productName' | 'barcode' | 'englishName'

export const CONTAINER_DETAIL_SEARCH_FIELDS: readonly ContainerDetailSearchField[] = ['itemNumber', 'productName', 'barcode', 'englishName']

export function applyContainerDetailSearchText(
  filters: ContainerDetailColumnFilters,
  field: ContainerDetailSearchField,
  text: string,
): ContainerDetailColumnFilters {
  const current = filters[field] ?? ''
  if (!text.trim()) {
    return current ? removeContainerDetailColumnFilter(filters, field) : filters
  }
  return current === text ? filters : { ...filters, [field]: text }
}

/**
 * 切换搜索字段：目标字段没有条件时把当前关键字挪过去（用户多半是想换个字段搜同一个词）；
 * 目标字段已有条件则保持两边原样，只把搜索框换成显示目标字段的值。
 */
export function switchContainerDetailSearchField(
  filters: ContainerDetailColumnFilters,
  from: ContainerDetailSearchField,
  to: ContainerDetailSearchField,
): { filters: ContainerDetailColumnFilters; text: string } {
  if (from === to) return { filters, text: filters[to] ?? '' }
  const fromText = filters[from] ?? ''
  const toText = filters[to] ?? ''
  if (fromText.trim() && !toText.trim()) {
    const moved = applyContainerDetailSearchText(removeContainerDetailColumnFilter(filters, from), to, fromText)
    return { filters: moved, text: fromText }
  }
  return { filters, text: toText }
}
