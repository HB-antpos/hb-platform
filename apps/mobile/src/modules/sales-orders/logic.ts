import {
  formatInsightDate,
  parseInsightDate,
  shiftInsightDate,
} from "@/modules/warehouse-product-insights/logic";
import type {
  SalesOrderFilters,
  SalesOrderLine,
  SalesOrderListItem,
  SalesOrderListPage,
  SalesOrderQueryBody,
  SalesOrderRange,
  SalesOrderRangePreset,
  SalesOrderRangeValidation,
  SalesOrderRefundTier,
  SalesOrderRefundTone,
  SalesOrderSortDirection,
  SalesOrderTypeFilter,
} from "./types";

/** 区间上限与后端 PosmSalesOrderMobileRules.MaxRangeDays 一致，含首尾计算。 */
export const SALES_ORDER_MAX_RANGE_DAYS = 30;
export const SALES_ORDER_PAGE_SIZE = 20;
export const SALES_ORDER_RANGE_PRESETS: SalesOrderRangePreset[] = [1, 7, 30];
export const SALES_ORDER_TYPE_FILTERS: SalesOrderTypeFilter[] = [-1, 0, 1, 2, 3, 4];
export const SALES_ORDER_VIEW_PERMISSION = "SalesOrders.View";
/** 订单类型里「已退款」那一项；移动端把它定义为「含退款商品的订单」，再分档，不再只看状态。 */
export const SALES_ORDER_REFUND_TYPE: SalesOrderTypeFilter = 3;
export const SALES_ORDER_REFUND_TIERS: SalesOrderRefundTier[] = ["all", "partial", "full"];

const DAY_MS = 86_400_000;

const STATUS_KEYS: Record<number, string> = {
  0: "pending",
  1: "paid",
  2: "cancelled",
  3: "refunded",
  4: "installment",
};

/** 订单状态 / 订单类型共用一套文案键；未知状态回退 unknown 而不是崩溃。 */
export function resolveSalesOrderStatusKey(status: number | null | undefined) {
  return status != null && STATUS_KEYS[status] ? STATUS_KEYS[status] : "unknown";
}

/**
 * 设备本地日期（YYYY-MM-DD）。
 * 订单时间在库里是门店本地墙钟时间，后端按墙钟比较日期边界，所以"今天"直接取设备当地日期即可。
 */
export function formatLocalDate(date: Date): string {
  const year = date.getFullYear();
  const month = `${date.getMonth() + 1}`.padStart(2, "0");
  const day = `${date.getDate()}`.padStart(2, "0");
  return `${year}-${month}-${day}`;
}

/** 默认区间：今天。 */
export function buildDefaultSalesOrderRange(today: string): SalesOrderRange {
  return { startDate: today, endDate: today };
}

export function buildDefaultSalesOrderFilters(today: string): SalesOrderFilters {
  return {
    range: buildDefaultSalesOrderRange(today),
    branchCodes: [],
    orderType: -1,
    refundTier: "all",
    sortDirection: "desc",
  };
}

/** 天数含首尾：同一天为 1 天。 */
export function countSalesOrderRangeDays(range: SalesOrderRange): number {
  const start = parseInsightDate(range.startDate);
  const end = parseInsightDate(range.endDate);
  if (start == null || end == null) return 0;
  return Math.round((end - start) / DAY_MS) + 1;
}

export function validateSalesOrderRange(
  range: SalesOrderRange,
): SalesOrderRangeValidation {
  const start = parseInsightDate(range.startDate);
  const end = parseInsightDate(range.endDate);
  if (start == null || end == null) {
    return { ok: false, dayCount: 0, reason: "format" };
  }
  if (start > end) {
    return { ok: false, dayCount: 0, reason: "order" };
  }
  const dayCount = Math.round((end - start) / DAY_MS) + 1;
  if (dayCount > SALES_ORDER_MAX_RANGE_DAYS) {
    return {
      ok: false,
      dayCount,
      reason: "tooLong",
      // 超限时以结束日为锚点收敛，保留用户真正关心的近期订单。
      clampedStartDate: formatInsightDate(
        end - (SALES_ORDER_MAX_RANGE_DAYS - 1) * DAY_MS,
      ),
    };
  }
  return { ok: true, dayCount };
}

/** 预设区间按含首尾计算：近 7 天 = 结束日往前 6 天；1 天即结束日当天。 */
export function buildSalesOrderPresetRange(
  endDate: string,
  days: number,
): SalesOrderRange {
  return { startDate: shiftInsightDate(endDate, -(days - 1)), endDate };
}

export function matchSalesOrderRangePreset(
  range: SalesOrderRange,
): SalesOrderRangePreset | null {
  const dayCount = countSalesOrderRangeDays(range);
  return SALES_ORDER_RANGE_PRESETS.find((preset) => preset === dayCount) ?? null;
}

export function canViewSalesOrders(
  isAuthenticated: boolean,
  hasPermission: (permission: string) => boolean,
  isReview: boolean,
) {
  return isAuthenticated && !isReview && hasPermission(SALES_ORDER_VIEW_PERMISSION);
}

/** 关键词去空白；空串按未提供处理，后端不再执行明细子查询。 */
export function normalizeSalesOrderKeyword(value: string): string | null {
  const keyword = value.trim();
  return keyword ? keyword : null;
}

/**
 * 订单含退款商品时按整单金额着色：金额口径与后端一致——取卡片显示的实付金额，实付为 0 / 空时回落订单总金额；
 * 小于 0 为整单退款（红），其余（含 0）为部分退款（橙）。不含退款商品返回 null。
 * 退款单（状态=已退款）一定含退款商品，即使旧后端没返回 hasRefundItems 也按状态认出来。
 */
export function resolveSalesOrderRefundTone(
  order: Pick<SalesOrderListItem, "status" | "hasRefundItems" | "actualAmount" | "totalAmount">,
): SalesOrderRefundTone | null {
  if (!order.hasRefundItems && order.status !== SALES_ORDER_REFUND_TYPE) return null;
  const amount =
    order.actualAmount != null && order.actualAmount !== 0 ? order.actualAmount : (order.totalAmount ?? 0);
  return amount < 0 ? "full" : "partial";
}

export function buildSalesOrderQueryBody(
  filters: SalesOrderFilters,
  keyword: string,
  pageNumber: number,
): SalesOrderQueryBody {
  const refundType = filters.orderType === SALES_ORDER_REFUND_TYPE;
  return {
    startDate: filters.range.startDate,
    endDate: filters.range.endDate,
    branchCodes: [...filters.branchCodes],
    // 「已退款」按含退款商品的订单筛（任意状态），所以订单类型发「全部」，范围交给 refundFilter。
    orderType: refundType ? -1 : filters.orderType,
    refundFilter: refundType ? (filters.refundTier === "all" ? "any" : filters.refundTier) : null,
    keyword: normalizeSalesOrderKeyword(keyword),
    sortDirection: filters.sortDirection,
    pageNumber,
    pageSize: SALES_ORDER_PAGE_SIZE,
  };
}

export function hasMoreSalesOrderPages(page: Pick<SalesOrderListPage, "pageNumber" | "pageSize" | "total">) {
  return page.pageNumber * page.pageSize < page.total;
}

/**
 * 追加下一页时按订单号去重。
 * 翻页期间可能有新订单上传导致偏移，宁可少显示一条也不能重复显示同一单。
 */
export function mergeSalesOrderPages(
  existing: SalesOrderListItem[],
  incoming: SalesOrderListItem[],
): SalesOrderListItem[] {
  const seen = new Set(existing.map((item) => item.orderGuid));
  const merged = [...existing];
  for (const item of incoming) {
    if (seen.has(item.orderGuid)) continue;
    seen.add(item.orderGuid);
    merged.push(item);
  }
  return merged;
}

/** 完整 GUID 在卡片上放不下，只展示尾 6 位便于口头核对；详情页仍显示全文。 */
export function formatSalesOrderGuidTail(orderGuid: string): string {
  const compact = orderGuid.replace(/-/g, "");
  return compact.length > 6 ? `…${compact.slice(-6).toUpperCase()}` : compact.toUpperCase();
}

export function toggleSortDirection(direction: SalesOrderSortDirection): SalesOrderSortDirection {
  return direction === "desc" ? "asc" : "desc";
}

/**
 * 分店多选：传入的 codes 为空表示不限；选择全部授权分店与不限等价，统一归一为空数组，
 * 避免同一语义产生两种请求体。
 */
export function normalizeSelectedBranchCodes(
  selected: Iterable<string>,
  available: Iterable<string>,
): string[] {
  const availableSet = new Set(available);
  const normalized = [...new Set(selected)].filter((code) => availableSet.has(code));
  return normalized.length === 0 || normalized.length === availableSet.size ? [] : normalized;
}

/** 判断当前筛选是否偏离默认值，用于筛选 chip 的高亮与"重置"按钮可用性。 */
export function countActiveSalesOrderFilters(
  filters: SalesOrderFilters,
  today: string,
): number {
  let count = 0;
  const defaults = buildDefaultSalesOrderFilters(today);
  if (
    filters.range.startDate !== defaults.range.startDate ||
    filters.range.endDate !== defaults.range.endDate
  ) {
    count++;
  }
  if (filters.branchCodes.length > 0) count++;
  if (filters.orderType !== -1) count++;
  return count;
}

/**
 * 详情接口的订单头不带 SKU 数（后端映射固定为 0），必须从明细行重新统计：
 * SKU 按去重货号计数，件数按数量求和；同一货号拆成多行不能算成多个 SKU。
 */
export function summarizeSalesOrderLines(lines: SalesOrderLine[]) {
  const codes = new Set<string>();
  let items = 0;
  for (const line of lines) {
    codes.add((line.productCode ?? "").trim() || `#${codes.size}`);
    items += line.quantity ?? 0;
  }
  return { skuCount: codes.size, itemCount: items };
}
