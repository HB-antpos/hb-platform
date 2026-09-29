import type {
  ChinaSupplierBranchTotalRow,
  ProductReportBranchRevenue,
  ProductReportCostStatus,
  ProductReportProductRow,
} from "@/modules/product-report/api";

/** 中国供应商页签「分店中国货占比」一行：分子来自商品日统计，分母来自分店营业额。 */
export interface ChinaBranchShareRow {
  branchCode: string;
  branchName: string;
  branchRevenue: number;
  compareBranchRevenue: number;
  chinaRevenue: number;
  compareChinaRevenue: number;
  /** 0-1 比率；分店营业额为 0 时无法计算，返回 null。 */
  share: number | null;
  compareShare: number | null;
  /** 本期占比 − 同期占比，单位百分点；任一期缺占比时为 null。 */
  shareDeltaPoints: number | null;
}

export type ChinaGoodsBranchSales = Pick<ChinaSupplierBranchTotalRow, "branchCode" | "branchName" | "revenue" | "compareRevenue">;

export type ChinaGoodsBranchTotals = Pick<
  ChinaSupplierBranchTotalRow,
  "revenue" | "compareRevenue" | "grossProfit" | "compareGrossProfit" | "costStatus" | "compareCostStatus"
>;

export type ChinaBranchShareSortField = "amount" | "share";
export interface ChinaBranchShareSort {
  field: ChinaBranchShareSortField;
  order: "asc" | "desc";
}

/** 分店区块默认按中国货金额降序（用户 2026-09-22 确认）。 */
export const DEFAULT_CHINA_BRANCH_SHARE_SORT: ChinaBranchShareSort = { field: "amount", order: "desc" };

/** 折叠状态下展示的分店行数；更多分店通过「展开全部」查看。 */
export const CHINA_BRANCH_COLLAPSED_ROW_COUNT = 8;

function ratio(numerator: number, denominator: number) {
  if (!Number.isFinite(numerator) || !Number.isFinite(denominator) || denominator <= 0) return null;
  return numerator / denominator;
}

/**
 * 以分店营业额为主表左连接中国货销售额：没有中国货销售的分店也要显示 0%，
 * 反过来只出现在中国货结果里的分店（营业额缺失）占比为 null，不能按 0 营业额算成无穷大。
 */
export function buildChinaBranchShareRows(
  branches: readonly ProductReportBranchRevenue[],
  chinaSales: readonly ChinaGoodsBranchSales[],
): ChinaBranchShareRow[] {
  const chinaByBranch = new Map(chinaSales.map((row) => [row.branchCode, row]));
  const rows: ChinaBranchShareRow[] = branches.map((branch) => {
    const china = chinaByBranch.get(branch.branchCode);
    chinaByBranch.delete(branch.branchCode);
    return createShareRow(
      branch.branchCode,
      branch.branchName || china?.branchName || branch.branchCode,
      branch.revenue,
      branch.compareRevenue,
      china?.revenue ?? 0,
      china?.compareRevenue ?? 0,
    );
  });
  chinaByBranch.forEach((china) => {
    rows.push(createShareRow(china.branchCode, china.branchName, 0, 0, china.revenue, china.compareRevenue));
  });
  return rows;
}

function createShareRow(
  branchCode: string,
  branchName: string,
  branchRevenue: number,
  compareBranchRevenue: number,
  chinaRevenue: number,
  compareChinaRevenue: number,
): ChinaBranchShareRow {
  const share = ratio(chinaRevenue, branchRevenue);
  const compareShare = ratio(compareChinaRevenue, compareBranchRevenue);
  return {
    branchCode,
    branchName,
    branchRevenue,
    compareBranchRevenue,
    chinaRevenue,
    compareChinaRevenue,
    share,
    compareShare,
    shareDeltaPoints: share === null || compareShare === null ? null : (share - compareShare) * 100,
  };
}

function compareNullable(left: number | null, right: number | null, direction: 1 | -1) {
  // 无法计算的占比无论升降序都排在最后，避免空值在升序时顶到最前。
  if (left === null || right === null) {
    if (left === right) return 0;
    return left === null ? 1 : -1;
  }
  if (left === right) return 0;
  return (left < right ? -1 : 1) * direction;
}

/** 返回排序后的新数组；同值时按同期值、再按分店代码兜底，保证顺序稳定。 */
export function sortChinaBranchShareRows(
  rows: readonly ChinaBranchShareRow[],
  sort: ChinaBranchShareSort,
): ChinaBranchShareRow[] {
  const direction = sort.order === "asc" ? 1 : -1;
  const read = (row: ChinaBranchShareRow): [number | null, number | null] => sort.field === "amount"
    ? [row.chinaRevenue, row.compareChinaRevenue]
    : [row.share, row.compareShare];
  return [...rows].sort((left, right) => {
    const [leftCurrent, leftCompare] = read(left);
    const [rightCurrent, rightCompare] = read(right);
    return compareNullable(leftCurrent, rightCurrent, direction)
      || compareNullable(leftCompare, rightCompare, direction)
      || (left.branchCode < right.branchCode ? -1 : left.branchCode > right.branchCode ? 1 : 0);
  });
}

/** 点表头：换列默认降序，同列在降序和升序之间切换，与主表排序交互一致。 */
export function toggleChinaBranchShareSort(
  current: ChinaBranchShareSort,
  field: ChinaBranchShareSortField,
): ChinaBranchShareSort {
  if (current.field !== field) return { field, order: "desc" };
  return { field, order: current.order === "desc" ? "asc" : "desc" };
}

/**
 * 占比条刻度上限：取本期与同期占比的最大值，向上取整到 1/stepsPerUnit 的倍数（最少一格，最多 100%），
 * 让条形长度可比、又不会因为占比普遍偏低而全部挤在左侧。
 * 默认 10 即按 10% 取整；选中单个供应商后占比通常只有几个百分点，传 100 按 1% 取整。
 */
export function getChinaBranchShareScaleMax(rows: readonly ChinaBranchShareRow[], stepsPerUnit = 10) {
  const maxShare = rows.reduce((max, row) => Math.max(max, row.share ?? 0, row.compareShare ?? 0), 0);
  const steps = Math.ceil(Math.max(0, maxShare) * stepsPerUnit - 1e-9);
  return Math.min(1, Math.max(1, steps) / stepsPerUnit);
}

/**
 * 选中供应商后，分店表的分子来自供应商分店分解接口，它按「分店×供应商」逐行返回；
 * 按分店合并成与分店中国货合计同形的行，就能复用同一套占比计算。
 */
export function mergeSupplierBranchSales(rows: readonly ChinaGoodsBranchSales[]): ChinaGoodsBranchSales[] {
  const byBranch = new Map<string, ChinaGoodsBranchSales>();
  rows.forEach((row) => {
    const existing = byBranch.get(row.branchCode);
    byBranch.set(row.branchCode, existing
      ? {
          ...existing,
          branchName: existing.branchName || row.branchName,
          revenue: existing.revenue + row.revenue,
          compareRevenue: existing.compareRevenue + row.compareRevenue,
        }
      : { branchCode: row.branchCode, branchName: row.branchName, revenue: row.revenue, compareRevenue: row.compareRevenue });
  });
  return [...byBranch.values()];
}

export function mergeReportCostStatuses(statuses: readonly ProductReportCostStatus[]): ProductReportCostStatus {
  if (statuses.some((status) => status === "Missing")) return "Missing";
  if (statuses.some((status) => status === "Complete")) return "Complete";
  return "NoActivity";
}

export interface ChinaGoodsSummary {
  revenue: number;
  compareRevenue: number;
  share: number | null;
  compareShare: number | null;
  grossMarginRate: number | null;
  compareGrossMarginRate: number | null;
  costStatus: ProductReportCostStatus;
  compareCostStatus: ProductReportCostStatus;
}

/**
 * 顶部汇总取自分店中国货合计，而不是供应商排行求和：排行的同期只含本期上榜的供应商，
 * 分店合计的同期覆盖同期期间卖过货的全部中国供应商，才是真正的「同期中国货营业额」。
 * 占比分母与供应商表「占总营业」相同，都是商品报告总营业额。
 */
export function summarizeChinaGoods(
  branchTotals: readonly ChinaGoodsBranchTotals[],
  totalRevenue: number,
  compareTotalRevenue: number,
): ChinaGoodsSummary {
  const revenue = branchTotals.reduce((sum, row) => sum + row.revenue, 0);
  const compareRevenue = branchTotals.reduce((sum, row) => sum + row.compareRevenue, 0);
  const costStatus = mergeReportCostStatuses(branchTotals.map((row) => row.costStatus));
  const compareCostStatus = mergeReportCostStatuses(branchTotals.map((row) => row.compareCostStatus));
  // 任一分店缺成本时整体毛利率不可信，交给展示层显示「成本待补全」。
  const grossProfit = branchTotals.reduce((sum, row) => sum + (row.grossProfit ?? 0), 0);
  const compareGrossProfit = branchTotals.reduce((sum, row) => sum + (row.compareGrossProfit ?? 0), 0);
  return {
    revenue,
    compareRevenue,
    share: ratio(revenue, totalRevenue),
    compareShare: ratio(compareRevenue, compareTotalRevenue),
    grossMarginRate: costStatus === "Missing" ? null : ratio(grossProfit, revenue),
    compareGrossMarginRate: compareCostStatus === "Missing" ? null : ratio(compareGrossProfit, compareRevenue),
    costStatus,
    compareCostStatus,
  };
}

export interface ProductPageTotals {
  quantity: number;
  compareQuantity: number;
  salesAmount: number;
  compareSalesAmount: number;
  averageUnitPrice: number | null;
  compareAverageUnitPrice: number | null;
  grossProfit: number | null;
  compareGrossProfit: number | null;
  grossMarginRate: number | null;
  compareGrossMarginRate: number | null;
  costStatus: ProductReportCostStatus;
  compareCostStatus: ProductReportCostStatus;
}

/** 商品明细表头下固定的「本页合计」行；均价按本页金额 ÷ 本页数量，不对单品均价取平均。 */
export function summarizeProductPage(rows: readonly ProductReportProductRow[]): ProductPageTotals {
  const quantity = rows.reduce((sum, row) => sum + row.quantity, 0);
  const compareQuantity = rows.reduce((sum, row) => sum + row.compareQuantity, 0);
  const salesAmount = rows.reduce((sum, row) => sum + row.salesAmount, 0);
  const compareSalesAmount = rows.reduce((sum, row) => sum + row.compareSalesAmount, 0);
  const costStatus = mergeReportCostStatuses(rows.map((row) => row.costStatus));
  const compareCostStatus = mergeReportCostStatuses(rows.map((row) => row.compareCostStatus));
  const grossProfit = costStatus === "Missing"
    ? null
    : rows.reduce((sum, row) => sum + (row.grossProfit ?? 0), 0);
  const compareGrossProfit = compareCostStatus === "Missing"
    ? null
    : rows.reduce((sum, row) => sum + (row.compareGrossProfit ?? 0), 0);
  return {
    quantity,
    compareQuantity,
    salesAmount,
    compareSalesAmount,
    averageUnitPrice: ratio(salesAmount, quantity),
    compareAverageUnitPrice: ratio(compareSalesAmount, compareQuantity),
    grossProfit,
    compareGrossProfit,
    grossMarginRate: grossProfit === null ? null : ratio(grossProfit, salesAmount),
    compareGrossMarginRate: compareGrossProfit === null ? null : ratio(compareGrossProfit, compareSalesAmount),
    costStatus,
    compareCostStatus,
  };
}
