import type { LocalSupplierInvoiceItem } from "@/modules/local-supplier-invoices/types";

/**
 * 明细行状态的底层分类（商品状态 / 条码状态 / 操作类型），与 Web statusFilters.ts、progressBuckets.ts 对齐。
 * 单独成文件是为了让 progress-buckets 与 status-filters 都能依赖它而互相不循环引用。
 *
 * 手机端明细数值字段按 `number | null` 处理，null / undefined 同义（对应 Web 的 undefined）。
 */

/** 行操作类型 DetailAction（与 Web types/localSupplierInvoice.ts 的枚举数值一致）。 */
export const DetailAction = {
  None: 0,
  CreateProduct: 1,
  UpdatePurchasePrice: 2,
  WaitForOperation: 3,
  UpdateItemNumber: 4,
  AddMultiCode: 5,
} as const;

/** 后端批量执行成功后写回的「已执行」操作类型（不在 DetailAction 枚举内）。 */
export const EXECUTED_DETAIL_ACTION = 99;

export type ProductStatusFilter = "notDetected" | "exists" | "notExists" | "createdHere";
export type BarcodeStatusFilter = "notDetected" | "normal" | "noMatch" | "multiMatch";

export const ACTION_TYPE_FILTERS = [
  DetailAction.None,
  DetailAction.CreateProduct,
  DetailAction.UpdatePurchasePrice,
  DetailAction.WaitForOperation,
  DetailAction.UpdateItemNumber,
  DetailAction.AddMultiCode,
] as const;

export type ActionTypeFilter = (typeof ACTION_TYPE_FILTERS)[number];

/** 行内改过、尚未回读的操作类型覆盖，key 为 detailGuid。 */
export type RowActions = Record<string, number>;

export type DetailStatusSource = Pick<
  LocalSupplierInvoiceItem,
  "detailGuid" | "isCreatedByThisInvoice" | "existingProductCount" | "barcodeStatus" | "barcodeMatchCount" | "activityType"
>;

/** 当前生效的操作类型：行内覆盖 > 明细 activityType > 无。 */
export function resolveDetailAction(
  detail: Pick<LocalSupplierInvoiceItem, "detailGuid" | "activityType">,
  rowActions: RowActions = {}
): number {
  return rowActions[detail.detailGuid] ?? detail.activityType ?? DetailAction.None;
}

export function getProductStatusFilter(detail: DetailStatusSource): ProductStatusFilter {
  // 本单新品优先：再次商品检测会把 existingProductCount 改成 1，不能据此判断是不是本单建的（与 Web 一致）。
  if (detail.isCreatedByThisInvoice) return "createdHere";
  const count = detail.existingProductCount;
  // existingProductCount 为空 = 还没做过商品检测。
  if (count === undefined || count === null) return "notDetected";
  if (count > 0) return "exists";
  return "notExists";
}

export function getBarcodeStatusFilter(detail: DetailStatusSource): BarcodeStatusFilter {
  const status = detail.barcodeStatus;
  const count = detail.barcodeMatchCount ?? 0;
  if (status === undefined || status === null || status === 0) return "notDetected";
  if (status === 1) return "normal";
  // 其余状态按匹配数区分：0 个 = 无匹配，≥1 个 = 多匹配（与 Web 一致）。
  if (count === 0) return "noMatch";
  return "multiMatch";
}

/** 条码异常 = 无匹配 + 多匹配。 */
export function isBarcodeStatusAbnormal(detail: DetailStatusSource): boolean {
  const status = getBarcodeStatusFilter(detail);
  return status === "noMatch" || status === "multiMatch";
}

/**
 * 操作类型筛选值。注意：99「已执行」不在筛选枚举里，会落入「无」（与 Web getActionTypeFilter 一致），
 * 「已执行」请用处理进度分段（progress-buckets）筛选。
 */
export function getActionTypeFilter(detail: DetailStatusSource, rowActions: RowActions = {}): ActionTypeFilter {
  const action = resolveDetailAction(detail, rowActions);
  return (ACTION_TYPE_FILTERS as readonly number[]).includes(action)
    ? (action as ActionTypeFilter)
    : DetailAction.None;
}
