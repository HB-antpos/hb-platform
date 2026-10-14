import type { CreateProductWithPricesRequest } from "@/modules/product-maintenance/types";

export interface CreateProductFormValues {
  localSupplierCode: string;
  itemNumber: string;
  barcode: string;
  productName: string;
  purchasePrice: string;
  retailPrice: string;
  isSpecialProduct: boolean;
  isAutoPricing: boolean;
}

/**
 * 弹层里可以被标红、滚动定位的字段，顺序与弹层从上到下的排列一致。
 * 「必填」类错误取这个顺序里第一个为空的字段，用户看到的永远是最靠上的问题。
 */
export type CreateProductErrorField =
  | "supplier"
  | "productName"
  | "itemNumber"
  | "barcode"
  | "purchasePrice"
  | "retailPrice";

export type CreateProductValidationResult =
  | {
      ok: true;
      payload: CreateProductWithPricesRequest;
    }
  | {
      ok: false;
      reason: "required" | "supplierRestricted" | "priceInvalid" | "itemNumberTooShort" | "barcodeTooShort" | "retailPriceTooLow";
      /** 出错的输入框；弹层据此标红并滚动到该处。 */
      field: CreateProductErrorField;
    };

/** 弹层内展示的一条错误；每次失败都生成新对象，弹层靠对象变化触发重新定位。 */
export interface CreateProductDialogError {
  message: string;
  field: CreateProductErrorField | null;
}

/**
 * 服务端对创建请求给出的、能明确指向某个输入框的业务冲突。
 * 其余失败（网络、5xx、条码被占用等没有错误码的 409）仍走通用文案，不去猜测归属哪个输入框。
 */
export function resolveCreateProductServerError(
  error: unknown
): { messageKey: string; field: CreateProductErrorField } | null {
  if (typeof error !== "object" || error === null) {
    return null;
  }
  const record = error as {
    code?: unknown;
    response?: { data?: { errorCode?: unknown; ErrorCode?: unknown } };
  };
  // HTTP 4xx 的响应体里带 errorCode（axios 错误）；success:false 信封解包后的错误则把它放在 error.code。
  const body = record.response?.data;
  const rawCode = body?.errorCode ?? body?.ErrorCode ?? record.code;
  const code = typeof rawCode === "string" ? rawCode.trim() : "";

  if (code === "ITEM_NUMBER_EXISTS") {
    return { messageKey: "createProduct.messages.itemNumberExists", field: "itemNumber" };
  }
  return null;
}

function parsePrice(value: string): number | null {
  const trimmed = value.trim();
  if (!trimmed) {
    return null;
  }

  const parsed = Number(trimmed);
  return Number.isFinite(parsed) ? parsed : null;
}

export function validateCreateProductForm(
  values: CreateProductFormValues
): CreateProductValidationResult {
  const localSupplierCode = values.localSupplierCode.trim();
  if (localSupplierCode === "200") {
    return { ok: false, reason: "supplierRestricted", field: "supplier" };
  }
  const itemNumber = values.itemNumber.trim();
  const barcode = values.barcode.trim();
  const productName = values.productName.trim();
  const purchasePriceText = values.purchasePrice.trim();
  const retailPriceText = values.retailPrice.trim();
  const purchasePrice = parsePrice(values.purchasePrice);
  const retailPrice = parsePrice(values.retailPrice);

  // 按弹层从上到下的顺序找第一个为空的字段，保证定位到最靠上的问题。
  const firstMissing = (
    [
      ["supplier", localSupplierCode],
      ["productName", productName],
      ["itemNumber", itemNumber],
      ["barcode", barcode],
      ["purchasePrice", purchasePriceText],
      ["retailPrice", retailPriceText],
    ] as const
  ).find(([, value]) => !value);
  if (firstMissing) {
    return { ok: false, reason: "required", field: firstMissing[0] };
  }

  if (itemNumber.length < 5) {
    return { ok: false, reason: "itemNumberTooShort", field: "itemNumber" };
  }

  if (barcode.length < 7) {
    return { ok: false, reason: "barcodeTooShort", field: "barcode" };
  }

  if (
    purchasePrice == null
    || retailPrice == null
    || purchasePrice < 0
    || retailPrice < 0
  ) {
    // 进货价有问题就指进货价，否则一定是零售价有问题。
    const purchaseInvalid = purchasePrice == null || purchasePrice < 0;
    return {
      ok: false,
      reason: "priceInvalid",
      field: purchaseInvalid ? "purchasePrice" : "retailPrice",
    };
  }

  if (!values.isAutoPricing && retailPrice < purchasePrice * 2) {
    return { ok: false, reason: "retailPriceTooLow", field: "retailPrice" };
  }

  return {
    ok: true,
    payload: {
      localSupplierCode,
      itemNumber,
      barcode,
      productName,
      purchasePrice,
      retailPrice,
      isSpecialProduct: values.isSpecialProduct,
      isAutoPricing: values.isAutoPricing,
    },
  };
}
