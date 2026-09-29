/**
 * 商品管理「已生效筛选条」的纯逻辑：把生效态的顶部筛选与列头筛选转换成可读标签。
 * 不依赖 React 与 i18n，文案由调用方通过 labels 传入，便于单元测试。
 */
import { HOT_BARGAIN_SUPPLIER_CODE } from '../../../types/localSupplierCategory'

export type StoreRecordCountMode = 'all' | 'hasRecords' | 'noRecords' | 'custom'

export type ToolbarFilterKey =
  | 'keyword'
  | 'supplierCode'
  | 'supplierCategory'
  | 'categoryGuid'
  | 'warehouseCategoryGuid'
  | 'isActive'
  | 'isSet'
  | 'storeRecordCount'

/** 顶部筛选栏的「生效态」，与 loadData 使用的条件一致。 */
export interface AppliedToolbarFilters {
  keyword?: string
  supplierCode?: string
  /** 非 200 供应商的分类；200 的分类由 warehouseCategoryGuid 表达，不重复出标签。 */
  supplierCategoryGuid?: string
  supplierCategoryUnassignedOnly?: boolean
  categoryGuid?: string
  warehouseCategoryGuid?: string
  isActive?: boolean
  isSet?: boolean
  storeRecordCountMode: StoreRecordCountMode
  storeRecordCountMin?: number
  storeRecordCountMax?: number
}

export interface ToolbarFilterLookups {
  supplierName: (code: string) => string | undefined
  categoryPath: (guid: string) => string[] | undefined
  warehouseCategoryPath: (guid: string) => string[] | undefined
  /** 已加载的供应商分类树里查名称路径；树未加载时返回 undefined 回退为 GUID。 */
  supplierCategoryPath?: (supplierCode: string, guid: string) => string[] | undefined
}

export interface ToolbarFilterChipLabels {
  keyword: string
  supplier: string
  supplierCategory: string
  supplierCategoryUnassigned: string
  category: string
  warehouseCategory: string
  status: string
  setType: string
  storeRecord: string
  active: string
  inactive: string
  setProduct: string
  normalProduct: string
  hasRecords: string
  noRecords: string
}

export interface FilterChip<K extends string = string> {
  key: K
  label: string
  value: string
}

export const CATEGORY_PATH_SEPARATOR = ' / '

/** 下拉里选「全部 / 有记录 / 无记录」时对应的后端范围，与 handleSearch 的折算保持一致。 */
export function getPresetStoreRecordCountRange(mode: Exclude<StoreRecordCountMode, 'custom'>): { min?: number; max?: number } {
  if (mode === 'hasRecords') return { min: 1, max: undefined }
  if (mode === 'noRecords') return { min: 0, max: 0 }
  return { min: undefined, max: undefined }
}

/** 数值区间摘要：两端都有显示「a – b」，只有一端显示「≥ a」或「≤ b」，都没有返回空串。 */
export function formatNumberRange(min?: number | string | null, max?: number | string | null): string {
  const minText = min === undefined || min === null ? '' : String(min).trim()
  const maxText = max === undefined || max === null ? '' : String(max).trim()
  if (minText && maxText) return minText === maxText ? `= ${minText}` : `${minText} – ${maxText}`
  if (minText) return `≥ ${minText}`
  if (maxText) return `≤ ${maxText}`
  return ''
}

function formatPath(path: string[] | undefined, fallback: string): string {
  return path && path.length ? path.join(CATEGORY_PATH_SEPARATOR) : fallback
}

/**
 * 顶部筛选 → 标签。只展示真正参与查询的条件：
 * 分店记录按生效态模式显示，自定义范围显示折算后的区间。
 */
export function buildToolbarFilterChips(
  filters: AppliedToolbarFilters,
  lookups: ToolbarFilterLookups,
  labels: ToolbarFilterChipLabels,
): FilterChip<ToolbarFilterKey>[] {
  const chips: FilterChip<ToolbarFilterKey>[] = []
  const keyword = filters.keyword?.trim()
  if (keyword) {
    chips.push({ key: 'keyword', label: labels.keyword, value: keyword })
  }
  if (filters.supplierCode) {
    chips.push({
      key: 'supplierCode',
      label: labels.supplier,
      value: lookups.supplierName(filters.supplierCode) || filters.supplierCode,
    })
  }
  if (filters.supplierCategoryUnassignedOnly) {
    chips.push({ key: 'supplierCategory', label: labels.supplierCategory, value: labels.supplierCategoryUnassigned })
  } else if (filters.supplierCategoryGuid && filters.supplierCode !== HOT_BARGAIN_SUPPLIER_CODE) {
    chips.push({
      key: 'supplierCategory',
      label: labels.supplierCategory,
      value: formatPath(
        lookups.supplierCategoryPath?.(filters.supplierCode ?? '', filters.supplierCategoryGuid),
        filters.supplierCategoryGuid,
      ),
    })
  }
  if (filters.categoryGuid) {
    chips.push({
      key: 'categoryGuid',
      label: labels.category,
      value: formatPath(lookups.categoryPath(filters.categoryGuid), filters.categoryGuid),
    })
  }
  if (filters.warehouseCategoryGuid) {
    chips.push({
      key: 'warehouseCategoryGuid',
      label: labels.warehouseCategory,
      value: formatPath(lookups.warehouseCategoryPath(filters.warehouseCategoryGuid), filters.warehouseCategoryGuid),
    })
  }
  if (filters.isActive !== undefined) {
    chips.push({ key: 'isActive', label: labels.status, value: filters.isActive ? labels.active : labels.inactive })
  }
  if (filters.isSet !== undefined) {
    chips.push({ key: 'isSet', label: labels.setType, value: filters.isSet ? labels.setProduct : labels.normalProduct })
  }
  if (filters.storeRecordCountMode === 'hasRecords') {
    chips.push({ key: 'storeRecordCount', label: labels.storeRecord, value: labels.hasRecords })
  } else if (filters.storeRecordCountMode === 'noRecords') {
    chips.push({ key: 'storeRecordCount', label: labels.storeRecord, value: labels.noRecords })
  } else if (filters.storeRecordCountMode === 'custom') {
    const range = formatNumberRange(filters.storeRecordCountMin, filters.storeRecordCountMax)
    if (range) {
      chips.push({ key: 'storeRecordCount', label: labels.storeRecord, value: range })
    }
  }
  return chips
}

export interface ColumnFilterMeta {
  /** 列标题，作为标签名。 */
  label: string
  /** 列筛选类型：文本列按「匹配方式 + 值」显示，数值/日期列按比较符显示。 */
  kind: 'text' | 'number' | 'date' | 'enum'
  /** 枚举列的选项，用于把值翻译成可读文本。 */
  options?: Array<{ text: string; value: string }>
}

export interface ColumnFilterSummaryLabels {
  /** 文本匹配方式：contains / equals / startsWith / endsWith。 */
  textOperators: Record<string, string>
  /** 多个枚举值之间的分隔符。 */
  listSeparator: string
}

function parseToken(value: string): Record<string, unknown> | null {
  if (!value.startsWith('{')) return null
  try {
    const parsed = JSON.parse(value)
    return typeof parsed === 'object' && parsed !== null ? parsed as Record<string, unknown> : null
  } catch {
    return null
  }
}

function tokenText(value: unknown): string {
  return value === undefined || value === null ? '' : String(value).trim()
}

/**
 * 列头筛选的单个值 → 摘要：
 * 文本 token 显示「包含 abc」，数值/日期 token 显示「= 5」「1 – 9」「≥ 2026-01-01」，
 * 普通枚举值按列选项翻译成文本，找不到时原样显示。
 */
export function summarizeColumnFilterValue(
  raw: string,
  meta: ColumnFilterMeta | undefined,
  labels: ColumnFilterSummaryLabels,
): string {
  const token = parseToken(raw)
  const operator = token && typeof token.operator === 'string' ? token.operator : ''
  if (!token || !operator) {
    return meta?.options?.find((option) => option.value === raw)?.text ?? raw
  }
  if (operator === 'between') {
    // 数值区间用 min/max，日期区间用 start/end，二者共用区间摘要。
    const isDate = 'start' in token || 'end' in token
    return isDate
      ? formatNumberRange(tokenText(token.start), tokenText(token.end))
      : formatNumberRange(tokenText(token.min), tokenText(token.max))
  }
  const value = tokenText(token.value)
  // 文本、数值、日期 token 都可能是 equals，只能靠列类型区分显示方式。
  if (meta?.kind === 'text') {
    return `${labels.textOperators[operator] ?? operator} ${value}`
  }
  if (operator === 'gte') return `≥ ${value}`
  if (operator === 'lte') return `≤ ${value}`
  if (operator === 'equals') return `= ${value}`
  return value
}

/** 列头筛选 → 标签。标签键加 `column:` 前缀，避免与顶部筛选键冲突。 */
export function buildColumnFilterChips(
  columnFilters: Record<string, string[] | undefined>,
  columnMeta: Record<string, ColumnFilterMeta>,
  labels: ColumnFilterSummaryLabels,
): Array<FilterChip & { filterKey: string }> {
  return Object.entries(columnFilters).flatMap(([filterKey, values]) => {
    const summaries = (values ?? [])
      .map((value) => summarizeColumnFilterValue(value, columnMeta[filterKey], labels))
      .filter(Boolean)
    if (!summaries.length) return []
    return [{
      key: `column:${filterKey}`,
      filterKey,
      label: columnMeta[filterKey]?.label ?? filterKey,
      value: summaries.join(labels.listSeparator),
    }]
  })
}
