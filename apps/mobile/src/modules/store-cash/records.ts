// 记录列表（存款 / 支出 / 期初盘点）的筛选与分页纯逻辑。
import { CASH_LIST_PAGE_SIZE } from "./constants";
import { addDays, type DateRange } from "./dates";
import type { CashExpenseCategory } from "./types";

export type RecordsKind = "deposits" | "expenses" | "entries";
export type RecordsRangePreset = "all" | "last30" | "last90";

export interface RecordsFilter {
  range: RecordsRangePreset;
  includeVoided: boolean;
  /** 仅支出列表使用；"all" 表示不按类别筛。 */
  category: CashExpenseCategory | "all";
}

export const DEFAULT_RECORDS_FILTER: RecordsFilter = {
  range: "last30",
  includeVoided: false,
  category: "all",
};

/** 预设范围换算成 from/to（以门店今天为基准，含两端）；"all" 返回 null，表示不限日期。 */
export function resolveRecordsRange(preset: RecordsRangePreset, storeToday: string): DateRange | null {
  if (preset === "all") return null;
  const days = preset === "last30" ? 30 : 90;
  return { from: addDays(storeToday, -(days - 1)), to: storeToday };
}

/** 已加载条数还没到 total 时返回下一页 offset，否则 undefined（用作 getNextPageParam）。 */
export function getNextOffset(loadedCount: number, total: number): number | undefined {
  return loadedCount < total ? loadedCount : undefined;
}

/**
 * 按 offset 翻页期间若有新记录插入，下一页会出现与上一页重复的行；
 * 合并时按 key 去重，保留先出现的。
 */
export function mergeUniqueByKey<T>(pages: readonly (readonly T[])[], getKey: (item: T) => string): T[] {
  const seen = new Set<string>();
  const merged: T[] = [];
  for (const page of pages) {
    for (const item of page) {
      const key = getKey(item);
      if (seen.has(key)) continue;
      seen.add(key);
      merged.push(item);
    }
  }
  return merged;
}

export interface RecordsListParams {
  storeCode: string;
  from?: string;
  to?: string;
  includeVoided: boolean;
  category?: CashExpenseCategory;
  limit: number;
  offset: number;
}

/** 把筛选与分页状态翻译成接口查询参数；"all" 日期与类别不带参数。 */
export function buildRecordsListParams(
  storeCode: string,
  storeToday: string,
  filter: RecordsFilter,
  offset: number,
): RecordsListParams {
  const range = resolveRecordsRange(filter.range, storeToday);
  return {
    storeCode,
    from: range?.from,
    to: range?.to,
    includeVoided: filter.includeVoided,
    category: filter.category === "all" ? undefined : filter.category,
    limit: CASH_LIST_PAGE_SIZE,
    offset,
  };
}
