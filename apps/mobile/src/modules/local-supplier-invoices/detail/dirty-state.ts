import type { LocalSupplierInvoiceItem } from "@/modules/local-supplier-invoices/types";
import type { InvoiceDetailInlineEditableField } from "./inline-edit";

/**
 * 明细「未保存修改」判定（与 Web InvoiceEdit/detailDirtyState.ts 对齐）。
 * 只比较可在页面上编辑、要靠「保存修改」落库的字段；
 * 操作类型（activityType）改了会立即落库，商品检测结果由后端写回，都不算未保存。
 */
export const INVOICE_DETAIL_SAVED_FIELDS: readonly InvoiceDetailInlineEditableField[] = [
  "itemNumber",
  "barcode",
  "productName",
  "quantity",
  "purchasePrice",
  "retailPrice",
  "pricingFloatRate",
  "newAutoRetailPrice",
  "autoPricing",
  "isSpecialProduct",
  "discountRate",
];

export type InvoiceDetailSnapshotIndex = ReadonlyMap<string, LocalSupplierInvoiceItem>;

/** 以 detailGuid 为键索引加载时的快照。 */
export function buildInvoiceDetailSnapshotIndex(
  snapshot: readonly LocalSupplierInvoiceItem[]
): InvoiceDetailSnapshotIndex {
  return new Map(snapshot.map((item) => [item.detailGuid, item]));
}

function normalizeComparableValue(value: unknown): unknown {
  // 空字符串、null、undefined 在落库语义上一致，比较前统一成 undefined。
  if (value === null || value === undefined || value === "") return undefined;
  // 数字按四位小数比较，吸收浮点误差。
  if (typeof value === "number") return Math.round(value * 10000) / 10000;
  return value;
}

export function isInvoiceDetailFieldEdited(
  detail: LocalSupplierInvoiceItem,
  snapshotIndex: InvoiceDetailSnapshotIndex,
  field: InvoiceDetailInlineEditableField
): boolean {
  const original = snapshotIndex.get(detail.detailGuid);
  // 快照里没有的行只会来自服务端刷新的竞态，这里不当作用户修改。
  if (!original) return false;
  return normalizeComparableValue(detail[field]) !== normalizeComparableValue(original[field]);
}

export function getEditedInvoiceDetailFields(
  detail: LocalSupplierInvoiceItem,
  snapshotIndex: InvoiceDetailSnapshotIndex
): InvoiceDetailInlineEditableField[] {
  return INVOICE_DETAIL_SAVED_FIELDS.filter((field) => isInvoiceDetailFieldEdited(detail, snapshotIndex, field));
}

export function isInvoiceDetailRowEdited(
  detail: LocalSupplierInvoiceItem,
  snapshotIndex: InvoiceDetailSnapshotIndex
): boolean {
  return INVOICE_DETAIL_SAVED_FIELDS.some((field) => isInvoiceDetailFieldEdited(detail, snapshotIndex, field));
}

/** 已改行数：同一行改多个字段只算一行。 */
export function countEditedInvoiceDetailRows(
  details: readonly LocalSupplierInvoiceItem[],
  snapshotIndex: InvoiceDetailSnapshotIndex
): number {
  return details.reduce((count, detail) => (isInvoiceDetailRowEdited(detail, snapshotIndex) ? count + 1 : count), 0);
}

export function hasUnsavedDetailChanges(
  details: readonly LocalSupplierInvoiceItem[],
  snapshotIndex: InvoiceDetailSnapshotIndex
): boolean {
  return details.some((detail) => isInvoiceDetailRowEdited(detail, snapshotIndex));
}
