import { parseNonNegativeNumber } from "./inline-edit";

/**
 * 批量编辑（二期 B）：对勾选行统一改进货价 / 零售价 / 自动定价 / 特殊商品 / 折扣，
 * 字段与 Web handleBatchEdit 的 editFields 一致（接口 POST {guid}/details/batch-update）。
 * 每项有独立的「要改」开关；没开的字段不传值，后端不动它。
 */

export interface BatchEditDraft {
  updatePurchasePrice: boolean;
  purchasePrice: string;
  updateRetailPrice: boolean;
  retailPrice: string;
  updateIsAutoPricing: boolean;
  isAutoPricing: boolean;
  updateIsSpecialProduct: boolean;
  isSpecialProduct: boolean;
  updateDiscountRate: boolean;
  /** 百分数文本（10 = 10%），提交时换成小数。 */
  discountPercent: string;
}

export interface BatchEditFields {
  updatePurchasePrice: boolean;
  purchasePrice?: number;
  updateRetailPrice: boolean;
  retailPrice?: number;
  updateIsAutoPricing: boolean;
  isAutoPricing?: boolean;
  updateIsSpecialProduct: boolean;
  isSpecialProduct?: boolean;
  updateDiscountRate: boolean;
  /** 小数口径（0.1 = 10%），与明细 discountRate 一致。 */
  discountRate?: number;
  updateAction: false;
}

export type BatchEditError = "noField" | "invalidPurchasePrice" | "invalidRetailPrice" | "invalidDiscount";

export function createEmptyBatchEditDraft(): BatchEditDraft {
  return {
    updatePurchasePrice: false,
    purchasePrice: "",
    updateRetailPrice: false,
    retailPrice: "",
    updateIsAutoPricing: false,
    isAutoPricing: true,
    updateIsSpecialProduct: false,
    isSpecialProduct: false,
    updateDiscountRate: false,
    discountPercent: "",
  };
}

/** 草稿 → 请求字段；至少开一项、开了的数值必须是合法非负数，折扣 0~100%。 */
export function buildBatchEditFields(draft: BatchEditDraft): { ok: true; fields: BatchEditFields } | { ok: false; error: BatchEditError } {
  if (!draft.updatePurchasePrice && !draft.updateRetailPrice && !draft.updateIsAutoPricing
    && !draft.updateIsSpecialProduct && !draft.updateDiscountRate) {
    return { ok: false, error: "noField" };
  }
  const fields: BatchEditFields = {
    updatePurchasePrice: draft.updatePurchasePrice,
    updateRetailPrice: draft.updateRetailPrice,
    updateIsAutoPricing: draft.updateIsAutoPricing,
    updateIsSpecialProduct: draft.updateIsSpecialProduct,
    updateDiscountRate: draft.updateDiscountRate,
    updateAction: false,
  };
  if (draft.updatePurchasePrice) {
    const value = parseNonNegativeNumber(draft.purchasePrice);
    if (value === null) return { ok: false, error: "invalidPurchasePrice" };
    fields.purchasePrice = value;
  }
  if (draft.updateRetailPrice) {
    const value = parseNonNegativeNumber(draft.retailPrice);
    if (value === null) return { ok: false, error: "invalidRetailPrice" };
    fields.retailPrice = value;
  }
  if (draft.updateIsAutoPricing) fields.isAutoPricing = draft.isAutoPricing;
  if (draft.updateIsSpecialProduct) fields.isSpecialProduct = draft.isSpecialProduct;
  if (draft.updateDiscountRate) {
    const percent = parseNonNegativeNumber(draft.discountPercent);
    if (percent === null || percent > 100) return { ok: false, error: "invalidDiscount" };
    // 四舍五入到 4 位小数，避免 12.3% → 0.12300000000000001 这类浮点尾巴进库。
    fields.discountRate = Math.round(percent * 100) / 10000;
  }
  return { ok: true, fields };
}
