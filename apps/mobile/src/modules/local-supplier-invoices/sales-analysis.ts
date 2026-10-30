import type { InvoiceSalesAnalysisRow } from "./types";

/** 排序方式：默认保持后端返回顺序（即进货单明细顺序）；其余按进货后销量排。 */
export type SalesAnalysisSort = "default" | "sales30Desc" | "sales90Desc" | "sales90Asc";

export const SALES_ANALYSIS_SORTS: SalesAnalysisSort[] = [
  "default",
  "sales30Desc",
  "sales90Desc",
  "sales90Asc",
];

export interface SalesAnalysisTotals {
  rowCount: number;
  quantity: number;
  amount: number;
  salesQty30: number;
  salesQty60: number;
  salesQty90: number;
  /** 进货后 90 天销量为 0 的商品数：店长最关心的「进了没卖动」。 */
  zeroSales90Count: number;
}

/** 搜索：货号 / 条码 / 商品编码 / 商品名，忽略大小写与首尾空白；空白关键字返回原数组。 */
export function filterSalesAnalysisRows(
  rows: InvoiceSalesAnalysisRow[],
  keyword: string
): InvoiceSalesAnalysisRow[] {
  const needle = keyword.trim().toLocaleLowerCase();
  if (!needle) return rows;
  return rows.filter((row) =>
    [row.itemNumber, row.barcode, row.productCode, row.productName].some((field) =>
      field.toLocaleLowerCase().includes(needle)
    )
  );
}

/**
 * 排序（返回新数组，不改入参）。销量相同的行保持原有相对顺序（Array.prototype.sort 在现代引擎里是稳定排序），
 * 这样「最低销量」时零销量的行仍按明细顺序排列，便于对着单据逐行核对。
 */
export function sortSalesAnalysisRows(
  rows: InvoiceSalesAnalysisRow[],
  sort: SalesAnalysisSort
): InvoiceSalesAnalysisRow[] {
  if (sort === "default") return rows;
  const sorted = [...rows];
  switch (sort) {
    case "sales30Desc":
      return sorted.sort((a, b) => b.salesQty30 - a.salesQty30);
    case "sales90Desc":
      return sorted.sort((a, b) => b.salesQty90 - a.salesQty90);
    case "sales90Asc":
      return sorted.sort((a, b) => a.salesQty90 - b.salesQty90);
  }
}

/** 当前可见行的合计（随搜索变化，和明细页「筛选后合计」同习惯）。 */
export function getSalesAnalysisTotals(rows: InvoiceSalesAnalysisRow[]): SalesAnalysisTotals {
  return rows.reduce<SalesAnalysisTotals>(
    (sum, row) => ({
      rowCount: sum.rowCount + 1,
      quantity: sum.quantity + (row.quantity ?? 0),
      amount: sum.amount + (row.amount ?? 0),
      salesQty30: sum.salesQty30 + row.salesQty30,
      salesQty60: sum.salesQty60 + row.salesQty60,
      salesQty90: sum.salesQty90 + row.salesQty90,
      zeroSales90Count: sum.zeroSales90Count + (row.salesQty90 <= 0 ? 1 : 0),
    }),
    {
      rowCount: 0,
      quantity: 0,
      amount: 0,
      salesQty30: 0,
      salesQty60: 0,
      salesQty90: 0,
      zeroSales90Count: 0,
    }
  );
}

/**
 * 进货后 N 天销量的「售罄比」：销量 / 本次进货数量，进货数量缺失或为 0 时返回 null（不显示，避免除零的 Infinity）。
 * 结果夹在 [0, 1] 之外也如实返回（旧库存一起卖时销量可能大于进货数），由展示层决定是否截断。
 */
export function getSellThroughRatio(sales: number, purchasedQuantity: number | null): number | null {
  if (purchasedQuantity == null || !(purchasedQuantity > 0)) return null;
  return sales / purchasedQuantity;
}
