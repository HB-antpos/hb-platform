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

/** 服务端 409 里的 existingProducts 单项（后端 ProductDuplicateGuard.ToResponsePayload）。 */
interface ExistingProductHit {
  productCode?: unknown;
  itemNumber?: unknown;
  productName?: unknown;
  localSupplierCode?: unknown;
}

/** 服务端冲突在弹层里的展示：文案 key + 插值参数 + 要标红的输入框。 */
export interface CreateProductServerConflict {
  messageKey: string;
  field: CreateProductErrorField;
  /** 文案插值参数；只有需要点名已有商品的冲突（条码已存在）才带。 */
  params?: Record<string, string | number>;
}

function readText(value: unknown): string {
  return typeof value === "string" ? value.trim() : "";
}

/**
 * 条码被已有商品占用时的文案参数：点名第一个已有商品（货号 · 名称）和它的供应商，
 * 多个时再带上总数，让手持上的用户一眼知道该去选用哪件商品。
 */
function buildBarcodeExistsConflict(existingProducts: unknown): CreateProductServerConflict {
  const hits = (Array.isArray(existingProducts) ? existingProducts : []).filter(
    (item): item is ExistingProductHit => typeof item === "object" && item !== null
  );
  if (hits.length === 0) {
    // 旧后端或响应里没带清单：仍然明确告诉用户「条码已有商品」，只是不点名。
    return { messageKey: "createProduct.messages.barcodeExistsUnknown", field: "barcode" };
  }
  const first = hits[0];
  const itemNumber = readText(first.itemNumber);
  const productName = readText(first.productName);
  const params = {
    product: [itemNumber, productName].filter(Boolean).join(" · ") || readText(first.productCode) || "--",
    supplier: readText(first.localSupplierCode) || "--",
    count: hits.length,
  };
  return {
    messageKey: hits.length > 1
      ? "createProduct.messages.barcodeExistsMore"
      : "createProduct.messages.barcodeExists",
    field: "barcode",
    params,
  };
}

/**
 * 服务端对创建请求给出的、能明确指向某个输入框的业务冲突。
 * 其余失败（网络、5xx、没有错误码的 409 等）仍走通用文案，不去猜测归属哪个输入框。
 * - ITEM_NUMBER_EXISTS：同供应商下货号已存在 → 标红货号。
 * - BARCODE_EXISTS：条码已被任何供应商的存活商品占用 → 标红条码，提示选用已有商品（不提供「仍然创建」）。
 */
export function resolveCreateProductServerError(
  error: unknown
): CreateProductServerConflict | null {
  if (typeof error !== "object" || error === null) {
    return null;
  }
  const record = error as {
    code?: unknown;
    response?: { data?: { errorCode?: unknown; ErrorCode?: unknown; existingProducts?: unknown; ExistingProducts?: unknown } };
  };
  // HTTP 4xx 的响应体里带 errorCode（axios 错误）；success:false 信封解包后的错误则把它放在 error.code。
  const body = record.response?.data;
  const rawCode = body?.errorCode ?? body?.ErrorCode ?? record.code;
  const code = typeof rawCode === "string" ? rawCode.trim() : "";

  if (code === "ITEM_NUMBER_EXISTS") {
    return { messageKey: "createProduct.messages.itemNumberExists", field: "itemNumber" };
  }
  if (code === "BARCODE_EXISTS") {
    return buildBarcodeExistsConflict(body?.existingProducts ?? body?.ExistingProducts);
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
