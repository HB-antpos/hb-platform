import type { LocalSupplierInvoiceItem } from "@/modules/local-supplier-invoices/types";
import {
  buildInvoiceDetailSnapshotIndex,
  isInvoiceDetailRowEdited,
  type InvoiceDetailSnapshotIndex,
} from "./dirty-state";
import { discountRateToDecimal } from "./pricing-editor";

/**
 * 明细行内编辑（与 Web InvoiceEdit/inlineEdit.ts 对齐）：校验 + 规整、金额重算、保存请求体生成。
 */
export type InvoiceDetailInlineEditableField =
  | "itemNumber"
  | "barcode"
  | "productName"
  | "quantity"
  | "purchasePrice"
  | "retailPrice"
  | "pricingFloatRate"
  | "newAutoRetailPrice"
  | "autoPricing"
  | "isSpecialProduct"
  | "discountRate";

/** 可编辑字段集合（与 dirty-state 的 INVOICE_DETAIL_SAVED_FIELDS 内容一致，测试里会校验）。 */
export const INVOICE_DETAIL_EDITABLE_FIELDS: readonly InvoiceDetailInlineEditableField[] = [
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

const TEXT_FIELDS = new Set<InvoiceDetailInlineEditableField>(["itemNumber", "barcode", "productName"]);
const BOOLEAN_FIELDS = new Set<InvoiceDetailInlineEditableField>(["autoPricing", "isSpecialProduct"]);
const NUMERIC_FIELDS = new Set<InvoiceDetailInlineEditableField>([
  "quantity",
  "purchasePrice",
  "retailPrice",
  "pricingFloatRate",
  "newAutoRetailPrice",
  "discountRate",
]);

export function isInvoiceDetailEditableField(field: string): field is InvoiceDetailInlineEditableField {
  return (INVOICE_DETAIL_EDITABLE_FIELDS as readonly string[]).includes(field);
}

/**
 * 错误码（不抛异常，由 UI 本地化）：
 * - invalidNumber：不是有效的非负数值（负数、NaN、Infinity、空串、带字母 / 科学计数法等）
 * - unsupportedField：不是可编辑字段
 */
export type InlineEditErrorCode = "invalidNumber" | "unsupportedField";

export type InlineEditResult =
  | { ok: true; item: LocalSupplierInvoiceItem }
  | { ok: false; error: InlineEditErrorCode };

const PLAIN_NON_NEGATIVE_NUMBER = /^(\d+(\.\d*)?|\.\d+)$/;

/** 把输入解析为非负有限数；number 直接校验，string 先 trim 且只接受纯十进制（拒绝 "", "1e3", "0x10", "-1"）。 */
export function parseNonNegativeNumber(raw: unknown): number | null {
  if (typeof raw === "number") return Number.isFinite(raw) && raw >= 0 ? raw : null;
  if (typeof raw === "string") {
    const text = raw.trim();
    if (!PLAIN_NON_NEGATIVE_NUMBER.test(text)) return null;
    const parsed = Number(text);
    return Number.isFinite(parsed) ? parsed : null;
  }
  return null;
}

function roundMoney(value: number): number {
  return Math.round(value * 100) / 100;
}

/** 金额 = 数量 × 进货价，四舍五入到分；任一为空则金额为空（与 Web 一致）。 */
export function recalculateInvoiceDetailAmount(
  quantity: number | null | undefined,
  purchasePrice: number | null | undefined
): number | null {
  if (quantity == null || purchasePrice == null) return null;
  return roundMoney(quantity * purchasePrice);
}

/**
 * 对单行应用一次行内编辑。
 * - 文本字段：trim（允许清空）
 * - 数值字段：必须是非负有限数，否则返回 invalidNumber；折扣率传入百分数，内部换算为小数
 * - 布尔字段：autoPricing / isSpecialProduct
 * - quantity / purchasePrice 变更后重算 amount
 */
export function applyInlineEdit(
  item: LocalSupplierInvoiceItem,
  field: InvoiceDetailInlineEditableField,
  rawValue: unknown
): InlineEditResult {
  let value: string | number | boolean;
  if (TEXT_FIELDS.has(field)) {
    value = String(rawValue ?? "").trim();
  } else if (BOOLEAN_FIELDS.has(field)) {
    value = Boolean(rawValue);
  } else if (NUMERIC_FIELDS.has(field)) {
    const parsed = parseNonNegativeNumber(rawValue);
    if (parsed === null) return { ok: false, error: "invalidNumber" };
    value = field === "discountRate" ? discountRateToDecimal(parsed) : parsed;
  } else {
    return { ok: false, error: "unsupportedField" };
  }

  const next = { ...item, [field]: value } as LocalSupplierInvoiceItem;
  if (field === "quantity" || field === "purchasePrice") {
    next.amount = recalculateInvoiceDetailAmount(next.quantity, next.purchasePrice);
  }
  return { ok: true, item: next };
}

export type InlineEditDetailsResult =
  | { ok: true; details: LocalSupplierInvoiceItem[] }
  | { ok: false; error: InlineEditErrorCode };

/** 在明细列表里按 detailGuid 编辑一行；找不到行时原样返回（不报错）。 */
export function applyInlineEditToDetails(
  details: readonly LocalSupplierInvoiceItem[],
  detailGuid: string,
  field: InvoiceDetailInlineEditableField,
  rawValue: unknown
): InlineEditDetailsResult {
  const index = details.findIndex((detail) => detail.detailGuid === detailGuid);
  if (index < 0) return { ok: true, details: [...details] };
  const result = applyInlineEdit(details[index], field, rawValue);
  if (!result.ok) return result;
  const next = [...details];
  next[index] = result.item;
  return { ok: true, details: next };
}

/**
 * batch-upsert 请求体的一项，字段名（含大小写）与 Web InvoiceDetailUpsertItemDto 一致：主键为 `detailGUID`。
 * 除 detailGUID 外都是可选：缺省 = 本次不写该字段。
 */
export interface InvoiceDetailUpsertItem {
  detailGUID: string;
  itemNumber?: string;
  barcode?: string;
  additionalBarcodes?: string[];
  productName?: string;
  productCategoryGUID?: string;
  storeProductCode?: string;
  productCode?: string;
  quantity?: number;
  lastPurchasePrice?: number;
  purchasePrice?: number;
  retailPrice?: number;
  amount?: number;
  activityType?: number;
  discountRate?: number;
  autoPricing?: boolean;
  pricingFloatRate?: number;
  newAutoRetailPrice?: number;
  isSpecialProduct?: boolean;
}

function orUndefined<T>(value: T | null | undefined): T | undefined {
  return value ?? undefined;
}

/**
 * 保存明细的请求体：字段与 Web buildInvoiceDetailSaveItems 完全一致（主键 + 业务可改字段，
 * 不带图片、检测状态、上次进货价、操作类型等只读或独立落库的字段；操作类型走独立接口）。
 * null 一律转 undefined（JSON 序列化时省略），对应 Web 的 undefined 语义。
 *
 * 与 Web 的差异：Web 每次保存整页明细；手机端传入 snapshot（加载时的快照数组或其索引）时，
 * 只输出相对快照有未保存修改的行，避免只改一行却把整张单（可能几百行）都 upsert 一遍。
 * 不传 snapshot 则输出全部行。快照里没有的行不当作用户修改，会被跳过。
 */
export function buildInvoiceDetailSaveItems(
  details: readonly LocalSupplierInvoiceItem[],
  snapshot?: readonly LocalSupplierInvoiceItem[] | InvoiceDetailSnapshotIndex
): InvoiceDetailUpsertItem[] {
  let target = details;
  if (snapshot) {
    const index = snapshot instanceof Map ? snapshot : buildInvoiceDetailSnapshotIndex(snapshot as LocalSupplierInvoiceItem[]);
    target = details.filter((detail) => isInvoiceDetailRowEdited(detail, index));
  }
  return target.map((detail) => ({
    detailGUID: detail.detailGuid,
    itemNumber: orUndefined(detail.itemNumber),
    barcode: orUndefined(detail.barcode),
    additionalBarcodes: detail.additionalBarcodes ? [...detail.additionalBarcodes] : undefined,
    productName: orUndefined(detail.productName),
    quantity: orUndefined(detail.quantity),
    purchasePrice: orUndefined(detail.purchasePrice),
    retailPrice: orUndefined(detail.retailPrice),
    amount: orUndefined(detail.amount),
    autoPricing: orUndefined(detail.autoPricing),
    pricingFloatRate: orUndefined(detail.pricingFloatRate),
    newAutoRetailPrice: orUndefined(detail.newAutoRetailPrice),
    isSpecialProduct: orUndefined(detail.isSpecialProduct),
    discountRate: orUndefined(detail.discountRate),
  }));
}
