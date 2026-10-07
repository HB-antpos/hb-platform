import type { WarehouseProductsTableQuery } from '../../../services/warehouseProductService';
import {
  parseComparableFilterTokens,
  parseTextFilterTokens,
  setFilterValues,
  type TextFilterMode,
  type WarehouseProductColumnFilters,
} from './columnFilters';

/**
 * 顶部筛选栏会把这些条件同步镜像写入 columnFilters（与列头筛选共用一份 state），
 * 已生效筛选条里它们只显示一次，并以「顶部筛选栏」身份展示。
 * 状态由状态页签控制；澳洲供应商在「更多筛选」里设置，也可在澳洲供应商列头设置。
 */
export const TOOLBAR_MIRRORED_FILTER_KEYS = ['domesticSupplierCode', 'isActive', 'productType', 'localSupplierCode'] as const;
export type ToolbarMirroredFilterKey = (typeof TOOLBAR_MIRRORED_FILTER_KEYS)[number];

export const ACTIVE_FILTER_SEARCH_KEY = 'searchText';
export const ACTIVE_FILTER_CATEGORY_KEY = 'category';
const COLUMN_CHIP_PREFIX = 'column:';

export type ActiveFilterColumnKind = 'text' | 'comparable' | 'enum';

export interface ActiveFilterColumnMeta {
  label: string;
  kind: ActiveFilterColumnKind;
  /** enum 列的可选项，用于把原始值（供应商代码、true/false、类型编号）转成可读文字。 */
  options?: Array<{ value: string; text: string }>;
}

export type ActiveFilterQuery = Pick<
  WarehouseProductsTableQuery,
  'searchText' | 'supplierCode' | 'productType' | 'isActive' | 'categoryGuid' | 'uncategorizedOnly' | 'filters'
>;

export interface ActiveFilterChip {
  key: string;
  label: string;
  value: string;
  source: 'toolbar' | 'column';
}

export interface BuildActiveFilterChipsInput {
  /** 最近一次实际发出的列表查询；为 null 表示尚未查询过。 */
  query: ActiveFilterQuery | null;
  labels: {
    searchText: string;
    category: string;
    /** 「只看未分类」生效时，分类标签显示的值。 */
    uncategorized: string;
  };
  /** 列头筛选键 → 列标题与取值格式，顺序即标签展示顺序；需包含镜像键的元数据。 */
  columns: Record<string, ActiveFilterColumnMeta>;
  /** 已解析的分类名称（或路径）；缺省时回退为分类 GUID。 */
  categoryLabel?: string;
  textModeLabels: Record<TextFilterMode, string>;
}

function isMirroredKey(key: string): key is ToolbarMirroredFilterKey {
  return (TOOLBAR_MIRRORED_FILTER_KEYS as readonly string[]).includes(key);
}

function formatEnumValues(values: string[], meta?: ActiveFilterColumnMeta) {
  return values
    .map((value) => meta?.options?.find((option) => option.value === value)?.text ?? value)
    .join(' / ');
}

/** 把一列的筛选令牌转成「包含 xx」「≥ 5」「1 ~ 9」这类摘要；无有效值时返回空串。 */
export function formatColumnFilterValue(
  values: string[] | undefined,
  meta: ActiveFilterColumnMeta | undefined,
  textModeLabels: Record<TextFilterMode, string>,
) {
  const normalizedValues = values?.map((value) => value.trim()).filter(Boolean) ?? [];
  if (!normalizedValues.length) {
    return '';
  }
  const kind = meta?.kind ?? 'enum';
  if (kind === 'text') {
    const parsed = parseTextFilterTokens(normalizedValues);
    return parsed.value ? `${textModeLabels[parsed.mode]} ${parsed.value}` : '';
  }
  if (kind === 'comparable') {
    const parsed = parseComparableFilterTokens(normalizedValues);
    if (parsed.mode === 'range') {
      return `${parsed.min} ~ ${parsed.max}`;
    }
    if (!parsed.value) {
      return '';
    }
    const symbol = parsed.mode === 'gte' ? '≥' : parsed.mode === 'lte' ? '≤' : '=';
    return `${symbol} ${parsed.value}`;
  }
  return formatEnumValues(normalizedValues, meta);
}

/** 镜像键优先取列头 filters（可能多选），为空时回退到顶层单值字段。 */
function resolveMirroredValues(key: ToolbarMirroredFilterKey, query: ActiveFilterQuery) {
  const fromFilters = query.filters?.[key]?.map((value) => value.trim()).filter(Boolean) ?? [];
  if (fromFilters.length) {
    return fromFilters;
  }
  // 澳洲供应商没有顶层查询字段，只存在于 filters。
  const topLevel = key === 'domesticSupplierCode'
    ? query.supplierCode
    : key === 'productType'
      ? query.productType
      : key === 'isActive'
        ? query.isActive
        : undefined;
  return topLevel === undefined || topLevel === null || String(topLevel).trim() === '' ? [] : [String(topLevel)];
}

/**
 * 条件 → 已生效标签：只依据「实际发出的查询」生成，保证标签与表格数据一致。
 * 顺序：关键词、国内供应商、分类、状态、商品类型、澳洲供应商，其后是其余列头筛选。
 */
export function buildActiveFilterChips(input: BuildActiveFilterChipsInput): ActiveFilterChip[] {
  const { query, labels, columns, categoryLabel, textModeLabels } = input;
  if (!query) {
    return [];
  }
  const chips: ActiveFilterChip[] = [];
  const keyword = query.searchText?.trim();
  if (keyword) {
    chips.push({ key: ACTIVE_FILTER_SEARCH_KEY, label: labels.searchText, value: keyword, source: 'toolbar' });
  }

  const pushMirrored = (key: ToolbarMirroredFilterKey) => {
    const meta = columns[key];
    const value = formatColumnFilterValue(resolveMirroredValues(key, query), meta, textModeLabels);
    if (value) {
      chips.push({ key, label: meta?.label ?? key, value, source: 'toolbar' });
    }
  };

  pushMirrored('domesticSupplierCode');
  if (query.uncategorizedOnly) {
    chips.push({ key: ACTIVE_FILTER_CATEGORY_KEY, label: labels.category, value: labels.uncategorized, source: 'toolbar' });
  }
  else if (query.categoryGuid) {
    chips.push({
      key: ACTIVE_FILTER_CATEGORY_KEY,
      label: labels.category,
      value: categoryLabel || query.categoryGuid,
      source: 'toolbar',
    });
  }
  pushMirrored('isActive');
  pushMirrored('productType');
  pushMirrored('localSupplierCode');

  const filters = query.filters ?? {};
  // 先按列定义顺序，再补上未登记列元数据的键，避免有条件生效却不显示。
  const orderedKeys = [
    ...Object.keys(columns).filter((key) => key in filters),
    ...Object.keys(filters).filter((key) => !(key in columns)),
  ];
  for (const key of orderedKeys) {
    if (isMirroredKey(key)) {
      continue;
    }
    const meta = columns[key];
    const value = formatColumnFilterValue(filters[key], meta, textModeLabels);
    if (value) {
      chips.push({ key: `${COLUMN_CHIP_PREFIX}${key}`, label: meta?.label ?? key, value, source: 'column' });
    }
  }
  return chips;
}

/**
 * 移除单个标签后要发出的查询覆盖参数（总是回到第 1 页）。
 * 调用方同时要把对应的界面 state 清掉，并用这里的 filters 作为新的 columnFilters。
 */
export function buildActiveFilterRemovalOverrides(
  chipKey: string,
  filters: WarehouseProductColumnFilters,
): Partial<WarehouseProductsTableQuery> & { filters: WarehouseProductColumnFilters } {
  if (chipKey === ACTIVE_FILTER_SEARCH_KEY) {
    return { page: 1, searchText: '', filters };
  }
  if (chipKey === ACTIVE_FILTER_CATEGORY_KEY) {
    return { page: 1, categoryGuid: undefined, uncategorizedOnly: false, filters };
  }
  if (chipKey === 'domesticSupplierCode') {
    return { page: 1, supplierCode: undefined, filters: setFilterValues(filters, chipKey, undefined) };
  }
  if (chipKey === 'productType') {
    return { page: 1, productType: undefined, filters: setFilterValues(filters, chipKey, undefined) };
  }
  if (chipKey === 'isActive') {
    return { page: 1, isActive: undefined, filters: setFilterValues(filters, chipKey, undefined) };
  }
  if (chipKey === 'localSupplierCode') {
    return { page: 1, filters: setFilterValues(filters, chipKey, undefined) };
  }
  if (chipKey.startsWith(COLUMN_CHIP_PREFIX)) {
    return { page: 1, filters: setFilterValues(filters, chipKey.slice(COLUMN_CHIP_PREFIX.length), undefined) };
  }
  return { page: 1, filters };
}
