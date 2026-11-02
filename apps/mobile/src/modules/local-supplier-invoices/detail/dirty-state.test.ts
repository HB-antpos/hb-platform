import {
  INVOICE_DETAIL_SAVED_FIELDS,
  buildInvoiceDetailSnapshotIndex,
  countEditedInvoiceDetailRows,
  getEditedInvoiceDetailFields,
  hasUnsavedDetailChanges,
  isInvoiceDetailFieldEdited,
  isInvoiceDetailRowEdited,
} from "./dirty-state";
import { applyInlineEdit } from "./inline-edit";
import { assertDeepEqual, assertEqual, makeItem } from "./test-helpers";

// 口径搬自 Web workbenchLogic.test.ts 的「明细未保存修改」。
const snapshot = [
  makeItem({ detailGuid: "a", quantity: 10, purchasePrice: 1.2, retailPrice: 2.5, productName: "Card", discountRate: 0.1 }),
  makeItem({ detailGuid: "b", quantity: 5, purchasePrice: 3, retailPrice: null, productName: "", activityType: 0 }),
];
const index = buildInvoiceDetailSnapshotIndex(snapshot);

function edit(item: (typeof snapshot)[number], field: Parameters<typeof applyInlineEdit>[1], value: unknown) {
  const result = applyInlineEdit(item, field, value);
  if (!result.ok) throw new Error("编辑应成功");
  return result.item;
}

assertEqual(INVOICE_DETAIL_SAVED_FIELDS.includes("quantity"), true, "数量参与比较");
assertEqual(INVOICE_DETAIL_SAVED_FIELDS.includes("amount" as never), false, "金额不单独参与比较");
assertEqual(INVOICE_DETAIL_SAVED_FIELDS.includes("activityType" as never), false, "操作类型不参与比较");
assertEqual(index.size, 2, "快照索引按 detailGuid");
assertEqual(countEditedInvoiceDetailRows(snapshot, index), 0, "与快照一致时没有未保存修改");
assertEqual(hasUnsavedDetailChanges(snapshot, index), false, "与快照一致时 hasUnsaved 为 false");

let current = [edit(snapshot[0], "quantity", 12), snapshot[1]];
current = [edit(current[0], "retailPrice", 2.99), current[1]];
assertDeepEqual(
  getEditedInvoiceDetailFields(current[0], index),
  ["quantity", "retailPrice"],
  "列出被修改字段；金额随数量重算但不单独算作修改"
);
assertEqual(isInvoiceDetailFieldEdited(current[0], index, "quantity"), true, "数量已改");
assertEqual(isInvoiceDetailFieldEdited(current[0], index, "purchasePrice"), false, "进货价未改");
assertEqual(isInvoiceDetailRowEdited(current[0], index), true, "行已改");
assertEqual(isInvoiceDetailRowEdited(current[1], index), false, "行未改");
assertEqual(countEditedInvoiceDetailRows(current, index), 1, "同一行改多个字段只算一行");
assertEqual(hasUnsavedDetailChanges(current, index), true, "存在未保存修改");

// 操作类型立即落库，不算未保存
const actionOnly = [snapshot[0], { ...snapshot[1], activityType: 1 }];
assertEqual(countEditedInvoiceDetailRows(actionOnly, index), 0, "操作类型不算未保存");

// 空字符串 / null / undefined 统一
assertEqual(
  countEditedInvoiceDetailRows([snapshot[0], { ...snapshot[1], productName: null as unknown as string }], index),
  0,
  "空串与 null 落库语义相同，不算修改"
);
assertEqual(
  countEditedInvoiceDetailRows([snapshot[0], { ...snapshot[1], retailPrice: undefined as unknown as null }], index),
  0,
  "null 与 undefined 一致"
);

// 数字按四位小数比较
assertEqual(
  countEditedInvoiceDetailRows([{ ...snapshot[0], purchasePrice: 1.2 + 0.00001 }, snapshot[1]], index),
  0,
  "小于 0.0001 的浮点误差不算修改"
);
assertEqual(
  countEditedInvoiceDetailRows([{ ...snapshot[0], purchasePrice: 1.2001 }, snapshot[1]], index),
  1,
  "达到 0.0001 的差异算修改"
);

// 空 → 有值、false → true 都算修改
assertEqual(isInvoiceDetailFieldEdited(edit(snapshot[1], "retailPrice", 0), index, "retailPrice"), true, "空值改成 0 算修改");
assertEqual(isInvoiceDetailFieldEdited(edit(snapshot[1], "isSpecialProduct", true), index, "isSpecialProduct"), true, "布尔变化算修改");
assertEqual(isInvoiceDetailFieldEdited(edit(snapshot[0], "discountRate", 10), index, "discountRate"), false, "折扣率 10% 等于 0.1，未改");
assertEqual(isInvoiceDetailFieldEdited(edit(snapshot[0], "discountRate", 20), index, "discountRate"), true, "折扣率改为 20% 算修改");
assertEqual(isInvoiceDetailFieldEdited(edit(snapshot[0], "itemNumber", " 72750 "), index, "itemNumber"), false, "货号 trim 后未变不算修改");

// 改回原值后不再算未保存
let reverted = edit(current[0], "quantity", 10);
reverted = edit(reverted, "retailPrice", 2.5);
assertDeepEqual(getEditedInvoiceDetailFields(reverted, index), [], "改回原值后不再算未保存");

// 快照里没有的行不当作用户修改
assertEqual(
  countEditedInvoiceDetailRows([makeItem({ detailGuid: "stranger", quantity: 99 })], index),
  0,
  "快照外的行不算修改"
);
