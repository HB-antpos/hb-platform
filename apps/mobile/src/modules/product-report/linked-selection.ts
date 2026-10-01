import type { ProductReportTotalRevenue, SupplierReportKind } from "@/modules/product-report/api";

/**
 * 商品报告主报表的展示条件。三表联动时，同一份报告（账号、授权分店、页签、日期都相同）内
 * 只改了分店、供应商或明细的分页、排序、搜索，才允许沿用上一份完整结果做局部过渡。
 */
export interface ProductReportDisplayContext {
  /** 账号与收银启用分店白名单；任一变化都代表权限边界变化，不能沿用旧结果。 */
  scopeKey: string;
  kind: SupplierReportKind;
  startDate: string;
  endDate: string;
  compareStartDate: string;
  compareEndDate: string;
  compareMode: string;
  branchCode: string | null;
  supplierCode: string | null;
  productSearch: string;
  productPage: number;
  productSort: string;
}

/** 局部过渡期间，哪些区块显示的仍是旧条件的数据（变淡并标「更新中」）。 */
export interface LinkedSectionStaleness {
  branchTable: boolean;
  supplierTable: boolean;
  productTable: boolean;
}

export const FRESH_LINKED_SECTIONS: LinkedSectionStaleness = Object.freeze({
  branchTable: false,
  supplierTable: false,
  productTable: false,
});

/** 分店、供应商代码比较忽略大小写与首尾空白，与收银启用白名单的匹配口径一致。 */
export function isSameLinkedCode(left: string | null | undefined, right: string | null | undefined) {
  return (left ?? "").trim().toLocaleLowerCase() === (right ?? "").trim().toLocaleLowerCase();
}

/**
 * 从已展示的条件切换到新条件时，返回各区块是否过期；不是同一份报告时返回 null，由调用方走整页加载。
 * 交叉筛选的依赖关系：分店表只依赖供应商（仅中国页签有分店表），供应商表只依赖分店，
 * 商品明细依赖分店、供应商以及自身的分页、排序和搜索；明细不反向影响前两张表。
 */
export function getLinkedTransitionStaleness(
  displayed: ProductReportDisplayContext,
  requested: ProductReportDisplayContext,
): LinkedSectionStaleness | null {
  if (
    displayed.scopeKey !== requested.scopeKey
    || displayed.kind !== requested.kind
    || displayed.startDate !== requested.startDate
    || displayed.endDate !== requested.endDate
    || displayed.compareStartDate !== requested.compareStartDate
    || displayed.compareEndDate !== requested.compareEndDate
    || displayed.compareMode !== requested.compareMode
  ) return null;

  const branchChanged = !isSameLinkedCode(displayed.branchCode, requested.branchCode);
  const supplierChanged = !isSameLinkedCode(displayed.supplierCode, requested.supplierCode);
  return {
    branchTable: requested.kind === "china" && supplierChanged,
    supplierTable: branchChanged,
    productTable: branchChanged
      || supplierChanged
      || displayed.productSearch !== requested.productSearch
      || displayed.productPage !== requested.productPage
      || displayed.productSort !== requested.productSort,
  };
}

/** 按所选分店筛出分店行；未选分店时返回全部。 */
export function filterRowsByBranch<T extends { branchCode: string }>(
  rows: readonly T[],
  branchCode: string | null | undefined,
): T[] {
  if (!branchCode) return [...rows];
  return rows.filter((row) => isSameLinkedCode(row.branchCode, branchCode));
}

/**
 * 占比分母：未选分店时用接口给出的全部分店营业额；选了分店则从分店明细里切出该店，
 * 与之前「顶部单店筛选后重新请求」得到的数字一致，但切换分店不必再请求一次。
 */
export function getScopedRevenue(
  totalRevenue: Pick<ProductReportTotalRevenue, "revenue" | "compareRevenue" | "branches"> | undefined,
  branchCode: string | null | undefined,
): { revenue: number; compareRevenue: number } {
  if (!totalRevenue) return { revenue: 0, compareRevenue: 0 };
  if (!branchCode) return { revenue: totalRevenue.revenue, compareRevenue: totalRevenue.compareRevenue };
  return filterRowsByBranch(totalRevenue.branches, branchCode).reduce(
    (sum, branch) => ({
      revenue: sum.revenue + branch.revenue,
      compareRevenue: sum.compareRevenue + branch.compareRevenue,
    }),
    { revenue: 0, compareRevenue: 0 },
  );
}

/**
 * 折叠时只展示前 limit 行；已选行不在其中时附在末尾，保证选中的分店始终看得见。
 * appendedSelected 为 true 时，调用方在附加行前放一条分隔说明。
 */
export function getCollapsedRowsWithSelection<T>(
  rows: readonly T[],
  limit: number,
  isSelected: (row: T) => boolean,
): { rows: T[]; appendedSelected: boolean } {
  const head = rows.slice(0, Math.max(0, limit));
  if (head.some(isSelected)) return { rows: head, appendedSelected: false };
  const selected = rows.slice(head.length).find(isSelected);
  return selected ? { rows: [...head, selected], appendedSelected: true } : { rows: head, appendedSelected: false };
}

/** 返回第一条满足条件的行所在页（从 1 开始）；找不到时返回 null。 */
export function findRowPage<T>(rows: readonly T[], pageSize: number, predicate: (row: T) => boolean): number | null {
  if (pageSize <= 0) return null;
  const index = rows.findIndex(predicate);
  return index < 0 ? null : Math.floor(index / pageSize) + 1;
}
