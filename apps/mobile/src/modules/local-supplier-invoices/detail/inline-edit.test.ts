import {
  INVOICE_DETAIL_EDITABLE_FIELDS,
  applyInlineEdit,
  applyInlineEditToDetails,
  buildInvoiceDetailSaveItems,
  isInvoiceDetailEditableField,
  parseNonNegativeNumber,
  recalculateInvoiceDetailAmount,
  type InvoiceDetailInlineEditableField,
} from "./inline-edit";
import { INVOICE_DETAIL_SAVED_FIELDS, buildInvoiceDetailSnapshotIndex } from "./dirty-state";
import { assertDeepEqual, assertEqual, makeItem } from "./test-helpers";

function edit(item: ReturnType<typeof makeItem>, field: InvoiceDetailInlineEditableField, raw: unknown) {
  const result = applyInlineEdit(item, field, raw);
  if (!result.ok) throw new Error(`编辑 ${field}=${String(raw)} 应成功，实际 ${result.error}`);
  return result.item;
}

const base = makeItem({
  detailGuid: "detail-1",
  itemNumber: "72750",
  barcode: "9311192727509",
  productName: "Clean Angel Mop",
  quantity: 12,
  lastPurchasePrice: 1.94,
  purchasePrice: 0.44,
  retailPrice: 2.5,
  amount: 5.28,
  autoPricing: true,
  pricingFloatRate: 2.5,
  newAutoRetailPrice: 1.5,
  isSpecialProduct: false,
  discountRate: 0,
  activityType: 2,
});

// ---- 可编辑字段集合与未保存比较字段一致 ----
assertDeepEqual([...INVOICE_DETAIL_EDITABLE_FIELDS], [...INVOICE_DETAIL_SAVED_FIELDS], "可编辑字段 = 未保存比较字段");
assertEqual(isInvoiceDetailEditableField("quantity"), true, "quantity 可编辑");
assertEqual(isInvoiceDetailEditableField("amount"), false, "amount 不可直接编辑");
assertEqual(isInvoiceDetailEditableField("activityType"), false, "操作类型不走行内编辑");

// ---- 金额重算（搬自 Web inlineEdit.test.ts） ----
const afterQuantity = edit(base, "quantity", 20);
assertEqual(afterQuantity.quantity, 20, "应更新数量");
assertEqual(afterQuantity.amount, 8.8, "数量变化后按当前进货价重算金额");
const afterPrice = edit(afterQuantity, "purchasePrice", 0.5);
assertEqual(afterPrice.purchasePrice, 0.5, "应更新进货价");
assertEqual(afterPrice.amount, 10, "进货价变化后按当前数量重算金额");
assertEqual(base.quantity, 12, "原对象不被修改");
assertEqual(edit(base, "purchasePrice", 0).amount, 0, "进货价 0 是有效值，金额为 0");
assertEqual(edit(makeItem({ quantity: 3, purchasePrice: 1.005 }), "quantity", 3).amount, Math.round(3 * 1.005 * 100) / 100, "金额四舍五入到分");
assertEqual(edit(makeItem({ quantity: 3, purchasePrice: 0.1 }), "quantity", 3).amount, 0.3, "0.1 × 3 的浮点误差被吸收");
assertEqual(edit(base, "retailPrice", 9.99).amount, 5.28, "改零售价不重算金额");
assertEqual(recalculateInvoiceDetailAmount(null, 1), null, "数量为空金额为空");
assertEqual(recalculateInvoiceDetailAmount(2, undefined), null, "进货价为空金额为空");
assertEqual(recalculateInvoiceDetailAmount(2, 1.25), 2.5, "正常重算");

// ---- 数值校验：非法返回错误码而非抛 ----
const invalidInputs: unknown[] = [-1, "-1", "-0.01", Number.NaN, Number.POSITIVE_INFINITY, "", "   ", "abc", "1e3", "0x10", "1,5", "1.2.3", null, undefined, {}, true];
for (const input of invalidInputs) {
  const result = applyInlineEdit(base, "purchasePrice", input);
  assertEqual(result.ok, false, `非法数值 ${JSON.stringify(input)} 应被拒绝`);
  if (!result.ok) assertEqual(result.error, "invalidNumber", "错误码为 invalidNumber");
}
for (const field of ["quantity", "retailPrice", "pricingFloatRate", "newAutoRetailPrice", "discountRate"] as const) {
  assertEqual(applyInlineEdit(base, field, -1).ok, false, `${field} 不接受负数`);
}
assertEqual(parseNonNegativeNumber("12.50"), 12.5, "字符串数值");
assertEqual(parseNonNegativeNumber(" 7 "), 7, "字符串首尾空格被忽略");
assertEqual(parseNonNegativeNumber(".5"), 0.5, "省略整数位");
assertEqual(parseNonNegativeNumber("5."), 5, "省略小数位");
assertEqual(parseNonNegativeNumber("0"), 0, "0 是有效值");
assertEqual(parseNonNegativeNumber(0), 0, "数字 0 是有效值");
assertEqual(parseNonNegativeNumber(3), 3, "数字直接通过");
assertEqual(applyInlineEdit(base, "quantity", "abc").ok, false, "失败不改变入参");
assertEqual(base.quantity, 12, "失败后原对象保持不变");

// 未知字段返回错误码
const unknownField = applyInlineEdit(base, "amount" as InvoiceDetailInlineEditableField, 1);
assertEqual(unknownField.ok, false, "不可编辑字段被拒绝");
if (!unknownField.ok) assertEqual(unknownField.error, "unsupportedField", "错误码为 unsupportedField");

// ---- 文本 / 布尔 / 折扣率 ----
assertEqual(edit(base, "itemNumber", "  A-1  ").itemNumber, "A-1", "文本字段 trim");
assertEqual(edit(base, "productName", "   ").productName, "", "文本允许清空");
assertEqual(edit(base, "barcode", null).barcode, "", "文本 null 规整为空串");
assertEqual(edit(base, "autoPricing", false).autoPricing, false, "布尔 false 是有效值");
assertEqual(edit(base, "isSpecialProduct", true).isSpecialProduct, true, "布尔 true");
assertEqual(edit(base, "discountRate", 12.5).discountRate, 0.125, "折扣率百分数 12.5 → 小数 0.125");
assertEqual(edit(base, "discountRate", "33.3").discountRate, 0.333, "折扣率换算不留浮点尾巴");
assertEqual(edit(base, "discountRate", 0).discountRate, 0, "折扣率 0 是有效值");
assertEqual(edit(base, "pricingFloatRate", "2.75").pricingFloatRate, 2.75, "浮动倍率支持字符串");

// ---- 按 detailGuid 编辑列表 ----
const list = [base, makeItem({ detailGuid: "detail-2", quantity: 5, purchasePrice: 1, amount: 5 })];
const listResult = applyInlineEditToDetails(list, "detail-2", "quantity", "10");
if (!listResult.ok) throw new Error("列表编辑应成功");
assertEqual(listResult.details[1].amount, 10, "只改目标行并重算金额");
assertEqual(listResult.details[0], base, "其他行引用不变");
assertEqual(applyInlineEditToDetails(list, "detail-2", "quantity", "x").ok, false, "列表编辑同样返回错误码");
const missing = applyInlineEditToDetails(list, "nope", "quantity", 1);
assertEqual(missing.ok && missing.details.length, 2, "找不到行时原样返回");

// ---- 保存请求体：字段与 Web buildInvoiceDetailSaveItems 逐字段一致（搬自 Web inlineEdit.test.ts） ----
assertDeepEqual(
  buildInvoiceDetailSaveItems([base]),
  [
    {
      detailGUID: "detail-1",
      itemNumber: "72750",
      barcode: "9311192727509",
      additionalBarcodes: [],
      productName: "Clean Angel Mop",
      quantity: 12,
      purchasePrice: 0.44,
      retailPrice: 2.5,
      amount: 5.28,
      autoPricing: true,
      pricingFloatRate: 2.5,
      newAutoRetailPrice: 1.5,
      isSpecialProduct: false,
      discountRate: 0,
    },
  ],
  "payload 只含主键与业务可改字段，不带图片、状态、上次进货价、操作类型"
);
const payloadKeys = Object.keys(buildInvoiceDetailSaveItems([makeItem({ additionalBarcodes: ["1", "2"] })])[0]);
assertDeepEqual(
  payloadKeys,
  ["detailGUID", "itemNumber", "barcode", "additionalBarcodes", "productName", "quantity", "purchasePrice", "retailPrice", "amount", "autoPricing", "pricingFloatRate", "newAutoRetailPrice", "isSpecialProduct", "discountRate"],
  "字段名与顺序同 Web（主键是大写 GUID）"
);
const nullable = JSON.parse(
  JSON.stringify(buildInvoiceDetailSaveItems([makeItem({ detailGuid: "n", discountRate: null, autoPricing: null, pricingFloatRate: null, newAutoRetailPrice: null, retailPrice: null, amount: null })]))
)[0];
assertEqual("discountRate" in nullable, false, "null 转 undefined，JSON 中省略");
assertEqual("autoPricing" in nullable, false, "autoPricing null 省略");
assertEqual("amount" in nullable, false, "amount null 省略");
assertEqual(nullable.isSpecialProduct, false, "false 布尔保留");

// 传快照时只输出有未保存修改的行
const snapshot = [base, makeItem({ detailGuid: "detail-2" }), makeItem({ detailGuid: "detail-3" })];
const edited = [
  edit(base, "quantity", 20),
  snapshot[1],
  { ...snapshot[2], activityType: 1 }, // 操作类型立即落库，不算未保存
];
assertDeepEqual(
  buildInvoiceDetailSaveItems(edited, snapshot).map((item) => item.detailGUID),
  ["detail-1"],
  "只保存有未保存修改的行"
);
assertEqual(buildInvoiceDetailSaveItems(edited, snapshot)[0].amount, 8.8, "保存的是重算后的金额");
assertDeepEqual(
  buildInvoiceDetailSaveItems(edited, buildInvoiceDetailSnapshotIndex(snapshot)).map((item) => item.detailGUID),
  ["detail-1"],
  "也可以直接传快照索引"
);
assertEqual(buildInvoiceDetailSaveItems(edited).length, 3, "不传快照则输出全部行");
assertEqual(
  buildInvoiceDetailSaveItems([makeItem({ detailGuid: "unknown-row", quantity: 99 })], snapshot).length,
  0,
  "快照里没有的行不当作用户修改"
);
assertEqual(buildInvoiceDetailSaveItems(snapshot, snapshot).length, 0, "无修改时为空数组");
