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
