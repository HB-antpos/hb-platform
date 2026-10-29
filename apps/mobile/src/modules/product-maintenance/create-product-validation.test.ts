import {
  resolveCreateProductServerError,
  validateCreateProductForm,
  type CreateProductErrorField,
  type CreateProductFormValues,
} from "./create-product-validation";

function assertEqual(actual: unknown, expected: unknown, label: string) {
  if (actual !== expected) {
    throw new Error(`${label}: expected ${String(expected)}, got ${String(actual)}`);
  }
}

function assertDeepEqual(actual: unknown, expected: unknown, label: string) {
  const actualText = JSON.stringify(actual);
  const expectedText = JSON.stringify(expected);
  if (actualText !== expectedText) {
    throw new Error(`${label}: expected ${expectedText}, got ${actualText}`);
  }
}

function makeValues(overrides: Partial<CreateProductFormValues> = {}): CreateProductFormValues {
  return {
    localSupplierCode: " SUP-01 ",
    itemNumber: " ITEM-01 ",
    barcode: " BAR-001 ",
    productName: " Green Tea ",
    purchasePrice: "3.50",
    retailPrice: "7",
    isSpecialProduct: false,
    isAutoPricing: false,
    ...overrides,
  };
}

function assertInvalid(
  overrides: Partial<CreateProductFormValues>,
  reason: Exclude<ReturnType<typeof validateCreateProductForm>, { ok: true }>["reason"],
  field: CreateProductErrorField,
  label: string
) {
  const result = validateCreateProductForm(makeValues(overrides));

  assertEqual(result.ok, false, `${label} fails`);
  if (!result.ok) {
    assertEqual(result.reason, reason, `${label} reports ${reason}`);
    // 弹层靠 field 标红并滚动定位出错的输入框，必须指向真正出问题的那一个。
    assertEqual(result.field, field, `${label} points at ${field}`);
  }
}

const valid = validateCreateProductForm(makeValues());

assertEqual(valid.ok, true, "valid form passes");
if (valid.ok) {
  assertDeepEqual(
    valid.payload,
    {
      localSupplierCode: "SUP-01",
      itemNumber: "ITEM-01",
      barcode: "BAR-001",
      productName: "Green Tea",
      purchasePrice: 3.5,
      retailPrice: 7,
      isSpecialProduct: false,
      isAutoPricing: false,
    },
    "valid form returns trimmed backend payload"
  );
}

assertInvalid({ localSupplierCode: "" }, "required", "supplier", "missing supplier");
assertInvalid({ localSupplierCode: " 200 " }, "supplierRestricted", "supplier", "Hotbargain supplier cannot be self-created");
assertInvalid({ itemNumber: "" }, "required", "itemNumber", "missing item number");
assertInvalid({ barcode: "" }, "required", "barcode", "missing barcode");
assertInvalid({ purchasePrice: "" }, "required", "purchasePrice", "missing purchase price");
assertInvalid({ retailPrice: "" }, "required", "retailPrice", "missing retail price");
assertInvalid({ productName: "  " }, "required", "productName", "blank product name");
assertInvalid({ itemNumber: "ABCD" }, "itemNumberTooShort", "itemNumber", "short item number");
// 2026-10-14 门店反馈的原值：货号 6223 只有 4 位，被拦下却因提示被弹层盖住而「没反应」。
assertInvalid({ itemNumber: "6223" }, "itemNumberTooShort", "itemNumber", "four-digit item number from the field report");
assertInvalid({ barcode: "123456" }, "barcodeTooShort", "barcode", "short barcode");
assertInvalid({ purchasePrice: "-1" }, "priceInvalid", "purchasePrice", "negative purchase price");
assertInvalid({ purchasePrice: "abc" }, "priceInvalid", "purchasePrice", "non-numeric purchase price");
assertInvalid({ retailPrice: "-2" }, "priceInvalid", "retailPrice", "negative retail price");
assertInvalid({ retailPrice: "6.99" }, "retailPriceTooLow", "retailPrice", "manual retail below double purchase");

// 多项同时为空时，定位到弹层里最靠上的那一个（供应商 → 名称 → 货号 → 条码 → 进货价 → 零售价）。
assertInvalid(
  { retailPrice: "", barcode: "", productName: "" },
  "required",
  "productName",
  "several blanks resolve to the top-most field"
);
assertInvalid(
  { retailPrice: "", localSupplierCode: "" },
  "required",
  "supplier",
  "supplier outranks every other blank"
);
// 货号太短与条码太短同时存在时先报货号（与校验顺序一致），用户改完再看到条码。
assertInvalid(
  { itemNumber: "62", barcode: "12" },
  "itemNumberTooShort",
  "itemNumber",
  "item number is reported before barcode"
);

const autoLowRetail = validateCreateProductForm(
  makeValues({
    retailPrice: "1",
    isAutoPricing: true,
  })
);

assertEqual(autoLowRetail.ok, true, "auto pricing allows retail below double purchase");

// —— 服务端业务冲突 → 弹层内的字段定位 ——
// 后端对重复货号返回 409 + errorCode；axios 错误把它放在 response.data，信封解包后的错误放在 error.code。
const itemConflict = { messageKey: "createProduct.messages.itemNumberExists", field: "itemNumber" };

assertDeepEqual(
  resolveCreateProductServerError({
    code: "ERR_BAD_REQUEST",
    response: { status: 409, data: { success: false, message: "该供应商下货号已存在，请更换货号", errorCode: "ITEM_NUMBER_EXISTS" } },
  }),
  itemConflict,
  "axios 409 body errorCode points at the item number"
);
assertDeepEqual(
  resolveCreateProductServerError({ response: { data: { ErrorCode: " ITEM_NUMBER_EXISTS " } } }),
  itemConflict,
  "PascalCase error code is accepted and trimmed"
);
assertDeepEqual(
  resolveCreateProductServerError(Object.assign(new Error("exists"), { code: "ITEM_NUMBER_EXISTS" })),
  itemConflict,
  "unwrapped business error carries the code on error.code"
);
// 没有错误码的 409（例如条码被占用）、网络错误、5xx 都不猜测归属，由通用文案兜底。
assertEqual(
  resolveCreateProductServerError({ response: { status: 409, data: { success: false, message: "条码已被使用，请重新生成条码" } } }),
  null,
  "409 without an error code stays generic"
);
assertEqual(resolveCreateProductServerError({ code: "ERR_NETWORK" }), null, "network error stays generic");
assertEqual(resolveCreateProductServerError({ response: { status: 500, data: { message: "创建失败" } } }), null, "5xx stays generic");
assertEqual(resolveCreateProductServerError(null), null, "null is not an error object");
assertEqual(resolveCreateProductServerError("ITEM_NUMBER_EXISTS"), null, "a bare string is not an error object");

// —— 条码已被已有商品占用（BARCODE_EXISTS）→ 标红条码并点名已有商品 ——
const barcodeConflictError = (existingProducts: unknown[]) => ({
  code: "ERR_BAD_REQUEST",
  response: {
    status: 409,
    data: {
      success: false,
      message: "条码 9310000000017 已属于商品 货号 62800 · Soft toy（供应商 250），请选用已有商品，不能重复新建",
      errorCode: "BARCODE_EXISTS",
      existingProducts,
    },
  },
});

assertDeepEqual(
  resolveCreateProductServerError(
    barcodeConflictError([
      { productCode: "p1", itemNumber: "62800", productName: "Soft toy", localSupplierCode: "250", source: "MainBarcode" },
    ])
  ),
  {
    messageKey: "createProduct.messages.barcodeExists",
    field: "barcode",
    params: { product: "62800 · Soft toy", supplier: "250", count: 1 },
  },
  "single existing product is named in the barcode conflict"
);
assertDeepEqual(
  resolveCreateProductServerError(
    barcodeConflictError([
      { productCode: "p1", itemNumber: "62800", productName: "Soft toy", localSupplierCode: "250" },
      { productCode: "p2", itemNumber: "62801", productName: "Soft toy B", localSupplierCode: "248" },
    ])
  ),
  {
    messageKey: "createProduct.messages.barcodeExistsMore",
    field: "barcode",
    params: { product: "62800 · Soft toy", supplier: "250", count: 2 },
  },
  "several existing products use the plural message and carry the total"
);
// 名称/货号缺失时退回商品编码，供应商缺失显示 --，不能出现 undefined。
assertDeepEqual(
  resolveCreateProductServerError(barcodeConflictError([{ productCode: "p9" }])),
  {
    messageKey: "createProduct.messages.barcodeExists",
    field: "barcode",
    params: { product: "p9", supplier: "--", count: 1 },
  },
  "missing item number and name fall back to the product code"
);
// 清单缺失（旧后端 / 异常响应）也要识别为条码冲突，只是不点名。
assertDeepEqual(
  resolveCreateProductServerError(barcodeConflictError([])),
  { messageKey: "createProduct.messages.barcodeExistsUnknown", field: "barcode" },
  "an empty list still reports the barcode conflict"
);
assertDeepEqual(
  resolveCreateProductServerError({ response: { status: 409, data: { errorCode: "BARCODE_EXISTS" } } }),
  { messageKey: "createProduct.messages.barcodeExistsUnknown", field: "barcode" },
  "a missing list still reports the barcode conflict"
);
assertDeepEqual(
  resolveCreateProductServerError({
    response: { data: { ErrorCode: " BARCODE_EXISTS ", ExistingProducts: [{ itemNumber: "1", productName: "N", localSupplierCode: "S" }] } },
  }),
  {
    messageKey: "createProduct.messages.barcodeExists",
    field: "barcode",
    params: { product: "1 · N", supplier: "S", count: 1 },
  },
  "PascalCase error code and list are accepted"
);
// 货号冲突不受影响：仍然不带 params。
assertDeepEqual(
  resolveCreateProductServerError({ response: { data: { errorCode: "ITEM_NUMBER_EXISTS", existingProducts: [{ productCode: "p1" }] } } }),
  itemConflict,
  "item number conflict keeps its own message and field"
);
