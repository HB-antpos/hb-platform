import type { WarehouseProductListItem, WarehouseProductsTableQuery } from '../../../services/warehouseProductService';
import type { SupplyPlan } from '../../../types/supplyNotice';

/**
 * 仓库商品管理列表的派生显示与状态页签计数规划。
 * 只用现有接口字段在前端计算，不引入新接口；页面只负责把文案和请求接上。
 */

export type WarehouseProductStatusTabKey = 'all' | 'active' | 'inactive';

export const WAREHOUSE_PRODUCT_STATUS_TAB_KEYS: readonly WarehouseProductStatusTabKey[] = ['all', 'active', 'inactive'];

/** 状态页签与列表查询的 isActive 一一对应：undefined = 全部。 */
export function resolveWarehouseProductStatusTab(isActive?: boolean | null): WarehouseProductStatusTabKey {
  if (isActive === true) {
    return 'active';
  }
  if (isActive === false) {
    return 'inactive';
  }
  return 'all';
}

export function warehouseProductStatusTabToIsActive(key: WarehouseProductStatusTabKey): boolean | undefined {
  if (key === 'active') {
    return true;
  }
  if (key === 'inactive') {
    return false;
  }
  return undefined;
}

/**
 * 某个状态页签的计数请求：沿用当前列表的其余筛选（关键词、分类、供应商、类型、列头条件），
 * 只替换状态并取第 1 页、每页 1 条，接口返回的 total 即该页签数量。
 * 列头 filters 里镜像的 isActive 必须一起去掉，否则「全部」页签会被当前状态再过滤一次。
 */
export function buildWarehouseProductStatusCountQuery(
  query: WarehouseProductsTableQuery,
  tab: WarehouseProductStatusTabKey,
): WarehouseProductsTableQuery {
  const { isActive: _currentIsActive, filters, ...rest } = query;
  const restFilters = Object.fromEntries(Object.entries(filters ?? {}).filter(([key]) => key !== 'isActive'));
  return {
    ...rest,
    page: 1,
    pageSize: 1,
    filters: Object.keys(restFilters).length ? restFilters : undefined,
    isActive: warehouseProductStatusTabToIsActive(tab),
  };
}

/** 计数缓存键：只由「状态以外」的筛选条件决定；翻页、改每页条数、排序、切换状态页签都不需要重新计数。 */
export function buildWarehouseProductStatusCountKey(query: WarehouseProductsTableQuery): string {
  const countQuery = buildWarehouseProductStatusCountQuery(query, 'all');
  const filters = Object.entries(countQuery.filters ?? {})
    .map(([key, values]) => [key, [...values].map((value) => value.trim()).filter(Boolean).sort()] as const)
    .filter(([, values]) => values.length > 0)
    .sort(([left], [right]) => left.localeCompare(right));
  return JSON.stringify({
    searchText: countQuery.searchText?.trim() ?? '',
    supplierCode: countQuery.supplierCode ?? '',
    productType: countQuery.productType ?? '',
    categoryGuid: countQuery.categoryGuid ?? '',
    uncategorizedOnly: countQuery.categoryGuid ? false : countQuery.uncategorizedOnly === true,
    filters,
  });
}

export interface WarehouseProductStatusCounts {
  key: string;
  counts: Partial<Record<WarehouseProductStatusTabKey, number>>;
}

export type WarehouseProductStatusCountPlan =
  | { skip: true }
  | {
    skip: false;
    key: string;
    /** 无需请求即可确定的计数（当前页签直接用列表 total）。 */
    knownCounts: Partial<Record<WarehouseProductStatusTabKey, number>>;
    /** 需要发 pageSize=1 计数请求的页签。 */
    tabsToFetch: WarehouseProductStatusTabKey[];
  };

/**
 * 规划状态页签计数：
 * - 条件未变、未标记过期、且当前页签数量与列表 total 一致时跳过，避免翻页、排序时重复计数；
 * - 当前页签的数量直接取列表 total，只为另外两个页签各发一个计数请求；
 * - 没有列表 total 时（例如行内上下架后单独刷新计数）三个页签都请求。
 */
export function planWarehouseProductStatusCounts(input: {
  query: WarehouseProductsTableQuery;
  currentTotal?: number;
  cached: WarehouseProductStatusCounts | null;
  stale: boolean;
}): WarehouseProductStatusCountPlan {
  const { query, currentTotal, cached, stale } = input;
  const key = buildWarehouseProductStatusCountKey(query);
  const currentTab = resolveWarehouseProductStatusTab(query.isActive);
  const hasTotal = typeof currentTotal === 'number';
  if (
    hasTotal &&
    !stale &&
    cached?.key === key &&
    cached.counts[currentTab] === currentTotal &&
    WAREHOUSE_PRODUCT_STATUS_TAB_KEYS.every((tab) => typeof cached.counts[tab] === 'number')
  ) {
    return { skip: true };
  }
  return {
    skip: false,
    key,
    knownCounts: hasTotal ? { [currentTab]: currentTotal } : {},
    tabsToFetch: WAREHOUSE_PRODUCT_STATUS_TAB_KEYS.filter((tab) => !hasTotal || tab !== currentTab),
  };
}

/** 零售价为空或为 0 时视为缺失：表格显示琥珀色「—」，提示需要补价。 */
export function isWarehouseProductRetailPriceMissing(value: number | null | undefined): boolean {
  return value === undefined || value === null || !Number.isFinite(value) || value === 0;
}

/**
 * 商品单元格第二行：条码 · 分类末级 · 中包 N，缺失的部分直接省略。
 * 中包数来源与「中包数量」列一致，都是 WarehouseProduct.MinOrderQuantity 归一后的 minOrderQuantity。
 */
export function buildWarehouseProductMetaParts(
  record: Pick<WarehouseProductListItem, 'barcode' | 'categoryName' | 'minOrderQuantity'>,
  formatMiddlePack: (count: number) => string,
): string[] {
  const parts: string[] = [];
  const barcode = record.barcode?.trim();
  if (barcode) {
    parts.push(barcode);
  }
  const categoryName = record.categoryName?.trim();
  if (categoryName) {
    parts.push(categoryName);
  }
  if (typeof record.minOrderQuantity === 'number' && Number.isFinite(record.minOrderQuantity)) {
    parts.push(formatMiddlePack(record.minOrderQuantity));
  }
  return parts;
}

export interface SupplyNoticeSummarySource {
  supplyPlan: SupplyPlan;
  isOverdue: boolean;
  watchingStoreCount: number;
}

export interface SupplyNoticeSummaryLabels {
  /** 下架但尚未登记说明时显示的文案。 */
  none: string;
  plan: (plan: SupplyPlan) => string;
  /** 预计恢复时间的可读文案；只在未逾期、且不是「不再供应」时调用。 */
  expected: () => string;
  overdue: string;
  watchers: (count: number) => string;
}

export interface SupplyNoticeSummary {
  text: string;
  tone: 'none' | 'normal' | 'overdue';
}

/**
 * 下架行状态单元格第二行的供货计划摘要：计划 · 预计恢复（逾期时改为「已逾期」）· N 家门店关注。
 * 「不再供应」没有恢复时间；逾期用红色强调，提醒仓库更新预计时间。
 */
export function buildSupplyNoticeSummary(
  notice: SupplyNoticeSummarySource | null | undefined,
  labels: SupplyNoticeSummaryLabels,
): SupplyNoticeSummary {
  if (!notice) {
    return { text: labels.none, tone: 'none' };
  }
  const parts = [labels.plan(notice.supplyPlan)];
  const isDiscontinued = notice.supplyPlan === 'Discontinued';
  if (notice.isOverdue && !isDiscontinued) {
    parts.push(labels.overdue);
  }
  else if (!isDiscontinued) {
    const expected = labels.expected().trim();
    if (expected) {
      parts.push(expected);
    }
  }
  if (notice.watchingStoreCount > 0) {
    parts.push(labels.watchers(notice.watchingStoreCount));
  }
  return { text: parts.join(' · '), tone: notice.isOverdue && !isDiscontinued ? 'overdue' : 'normal' };
}

/** 计数统一用千分位，副标题、分页合计与状态页签保持同一格式。 */
export function formatWarehouseProductCount(value: number): string {
  return value.toLocaleString('en-US');
}
