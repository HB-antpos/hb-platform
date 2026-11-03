import type { LocalSupplierInvoiceItem } from "@/modules/local-supplier-invoices/types";
import type { InvoiceDetailInlineEditableField } from "./inline-edit";

/**
 * 定价弹窗的纯逻辑（与 Web InvoiceEdit/pricingEditorChanges.ts、utils/discountRate.ts、
 * index.tsx renderPricingSummary 对齐）。折扣率落库口径为小数（0.1 = 10%），界面输入 / 展示为百分数。
 */

// ---------------------------------------------------------------------------
// 折扣率换算
// ---------------------------------------------------------------------------

/** 兼容历史脏数据：大于 1 的值视为已经是百分数（与 Web normalizeDiscountRate 一致）。 */
export function normalizeDiscountRate(value: number | null | undefined): number | null | undefined {
  if (value == null) return value;
  if (value > 1) return value / 100;
  return value;
}

/** 界面输入的百分数 → 落库的小数（12.5 → 0.125）。保留 6 位小数，避免 33.3 / 100 = 0.33299999999999996 这类浮点尾巴。 */
export function discountRateToDecimal(percentValue: number): number {
  return Math.round((percentValue / 100) * 1e6) / 1e6;
}

/** 落库的小数 → 界面百分数（0.1 → 10）；空值原样返回。保留 4 位小数以便直接回填输入框。 */
export function discountRateToPercent(decimalValue: number | null | undefined): number | null | undefined {
  if (decimalValue == null) return decimalValue;
  const normalized = normalizeDiscountRate(decimalValue) as number;
  return Math.round(normalized * 100 * 1e4) / 1e4;
}

/** 展示用折扣率：空值 "-"，否则 1 位小数的百分数（0.125 → "12.5%"；>1 视为已是百分数）。 */
export function formatDiscountRate(value: number | null | undefined): string {
  if (value == null) return "-";
  return `${(discountRateToPercent(value) as number).toFixed(1)}%`;
}

/** 浮动倍率展示：空值 "--"，否则 2 位小数。 */
export function formatPricingFloatRate(value: number | null | undefined): string {
  if (value === undefined || value === null) return "--";
  return value.toFixed(2);
}

// ---------------------------------------------------------------------------
// 编辑改动
// ---------------------------------------------------------------------------

export interface PricingEditorChange {
  field: InvoiceDetailInlineEditableField;
  /** discountRate 的 value 是百分数，交给 applyInlineEdit 换算成小数。 */
  value: unknown;
}

export interface PricingEditorDraft {
  autoPricing: boolean;
  /** 留空 = null = 不改。 */
  pricingFloatRate: number | null;
  /** 留空 = null = 不改。 */
  newAutoRetailPrice: number | null;
  isSpecialProduct: boolean;
  /** 折扣率百分数，留空 = null = 不改。 */
  discountPercent: number | null;
}

type PricingSource = Pick<
  LocalSupplierInvoiceItem,
  "autoPricing" | "pricingFloatRate" | "newAutoRetailPrice" | "isSpecialProduct" | "discountRate"
>;

/** 用明细当前值生成弹窗初始草稿。 */
export function buildPricingEditorDraft(detail: PricingSource): PricingEditorDraft {
  return {
    autoPricing: Boolean(detail.autoPricing),
    pricingFloatRate: detail.pricingFloatRate ?? null,
    newAutoRetailPrice: detail.newAutoRetailPrice ?? null,
    isSpecialProduct: Boolean(detail.isSpecialProduct),
    discountPercent: discountRateToPercent(detail.discountRate) ?? null,
  };
}

function isSameNumber(left: number | null | undefined, right: number | null | undefined): boolean {
  // null 与 undefined 都表示「没有值」，视为相同。
  if (left == null || right == null) return (left ?? null) === (right ?? null);
  return Math.abs(left - right) < 0.0001;
}

/**
 * 只返回真正改动过的字段；数字留空表示「不改」，不能把空值兜底成 0 落库。
 * 折扣率按百分数比较（0.1 与 10% 视为未改动）。
 */
export function buildPricingEditorChanges(detail: PricingSource, draft: PricingEditorDraft): PricingEditorChange[] {
  const changes: PricingEditorChange[] = [];
  if (draft.autoPricing !== Boolean(detail.autoPricing)) {
    changes.push({ field: "autoPricing", value: draft.autoPricing });
  }
  if (draft.pricingFloatRate != null && !isSameNumber(draft.pricingFloatRate, detail.pricingFloatRate)) {
    changes.push({ field: "pricingFloatRate", value: draft.pricingFloatRate });
  }
  if (draft.newAutoRetailPrice != null && !isSameNumber(draft.newAutoRetailPrice, detail.newAutoRetailPrice)) {
    changes.push({ field: "newAutoRetailPrice", value: draft.newAutoRetailPrice });
  }
  if (draft.isSpecialProduct !== Boolean(detail.isSpecialProduct)) {
    changes.push({ field: "isSpecialProduct", value: draft.isSpecialProduct });
  }
  if (
    draft.discountPercent != null &&
    !isSameNumber(draft.discountPercent, discountRateToPercent(detail.discountRate))
  ) {
    changes.push({ field: "discountRate", value: draft.discountPercent });
  }
  return changes;
}

// ---------------------------------------------------------------------------
// 定价摘要（结构化，UI 自行本地化）
// ---------------------------------------------------------------------------

/** 自动定价支持的最低进货价，与后端 AutoPricingRules.MinPurchasePrice 一致。 */
export const AUTO_PRICING_MIN_PURCHASE_PRICE = 0.1;

/** 本店现价 ÷ 本次进货价低于它就标红：与自动定价 1.5～5 成率的下限一致（与 Web retailPriceDisplay 同口径）。 */
export const STORE_RETAIL_MIN_MARKUP = 1.5;

export interface RetailPriceDisplay {
  /** 零售价格子主值：有指定零售价用指定价，否则用本店现价；两者都没有为 null。 */
  value: number | null;
  source: "designated" | "store" | "none";
  /** 本店现价；没匹配商品或本店未建价格为 null。 */
  storeRetailPrice: number | null;
  /** 本店现价 ÷ 本次进货价，两位小数文本；进货价缺失或为 0 时为 null。 */
  storeMarkupText: string | null;
  storeMarkupLow: boolean;
}

/**
 * 零售价格子显示什么：retailPrice 是本行「指定零售价」（「更新到分店」优先写入它），storeRetailPrice 是本店现价（只读）。
 * 指定价为空或 0（执行时同样当作没填）时显示本店现价作参考，并给出与本次进货价的倍率。
 */
export function getRetailPriceDisplay(
  detail: Pick<LocalSupplierInvoiceItem, "retailPrice" | "storeRetailPrice" | "purchasePrice" | "productCode">
): RetailPriceDisplay {
  // 没匹配到商品谈不上本店价格；改选商品后到重新加载前，旧的本店现价也不该挂在新商品上。
  const storeRetailPrice = detail.productCode && typeof detail.storeRetailPrice === "number" && Number.isFinite(detail.storeRetailPrice)
    ? detail.storeRetailPrice
    : null;
  const designated = typeof detail.retailPrice === "number" && Number.isFinite(detail.retailPrice) && detail.retailPrice > 0
    ? detail.retailPrice
    : null;
  const markup = storeRetailPrice !== null && typeof detail.purchasePrice === "number" && detail.purchasePrice > 0
    ? storeRetailPrice / detail.purchasePrice
    : null;
  return {
    value: designated ?? storeRetailPrice,
    source: designated !== null ? "designated" : storeRetailPrice !== null ? "store" : "none",
    storeRetailPrice,
    storeMarkupText: markup === null ? null : markup.toFixed(2),
    storeMarkupLow: markup !== null && markup < STORE_RETAIL_MIN_MARKUP,
  };
}

export type PricingModeKind = "unset" | "auto" | "autoNoRate" | "manual";
/** 本店分店价格表的自动定价状态：hidden = 没匹配到商品，不显示；none = 本店未建；auto / manual = 本店自动 / 手动。 */
export type StorePricingStatus = "hidden" | "none" | "auto" | "manual";

export interface PricingSummary {
  /** unset（autoPricing 为空，显示 "--"）/ auto（自动 ×浮动倍率）/ autoNoRate（自动但无倍率）/ manual（手动）。 */
  mode: PricingModeKind;
  /** 浮动倍率原值与 2 位小数文本；仅 mode = auto 有值。 */
  floatRate: number | null;
  floatRateText: string | null;
  isSpecial: boolean;
  /** 折扣率：为空或 0 视为没有折扣（null）；否则给出百分数和 "12.5%" 文本。 */
  discount: { percent: number; text: string } | null;
  newAutoRetailPrice: number | null;
  /** 自动定价开着、进货价大于 0 但低于下限（0.10）：后端不会生成倍率和新价，执行新建商品也会被拦，行内要把原因说出来。 */
  belowMinimum: boolean;
  storeStatus: StorePricingStatus;
}

export function getPricingSummary(
  detail: PricingSource & Pick<LocalSupplierInvoiceItem, "productCode" | "storeIsAutoPricing" | "purchasePrice">
): PricingSummary {
  let mode: PricingModeKind;
  if (detail.autoPricing == null) mode = "unset";
  else if (detail.autoPricing) mode = detail.pricingFloatRate != null ? "auto" : "autoNoRate";
  else mode = "manual";

  const hasDiscount = detail.discountRate != null && detail.discountRate !== 0;

  let storeStatus: StorePricingStatus;
  // 没有匹配到商品时谈不上分店状态，不显示（与 Web renderStoreAutoPricing 一致）。
  if (!detail.productCode) storeStatus = "hidden";
  else if (detail.storeIsAutoPricing == null) storeStatus = "none";
  else storeStatus = detail.storeIsAutoPricing ? "auto" : "manual";

  const belowMinimum = Boolean(detail.autoPricing)
    && typeof detail.purchasePrice === "number"
    && detail.purchasePrice > 0
    && detail.purchasePrice < AUTO_PRICING_MIN_PURCHASE_PRICE;

  return {
    mode,
    floatRate: mode === "auto" ? (detail.pricingFloatRate as number) : null,
    floatRateText: mode === "auto" ? formatPricingFloatRate(detail.pricingFloatRate) : null,
    isSpecial: Boolean(detail.isSpecialProduct),
    discount: hasDiscount
      ? {
          percent: discountRateToPercent(detail.discountRate) as number,
          text: formatDiscountRate(detail.discountRate),
        }
      : null,
    newAutoRetailPrice: detail.newAutoRetailPrice ?? null,
    belowMinimum,
    storeStatus,
  };
}
