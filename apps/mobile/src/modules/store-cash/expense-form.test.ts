import assert from "node:assert/strict";
import {
  appendExpensePhotos,
  buildCreateExpenseRequest,
  createInitialExpense,
  isPayeeApplicable,
  isReceiptRequired,
  patchExpensePhoto,
  removeExpensePhoto,
  validateExpenseDraft,
  type ExpenseDraft,
  type ExpenseLimits,
} from "./expense-form";
import { resolveEntryDateRange } from "./dates";
import { createPhotoDraft, type PhotoDraft } from "./photo-drafts";
import { CASH_EXPENSE_CATEGORIES } from "./constants";

const uploaded = (key: string, guid: string): PhotoDraft => ({
  ...createPhotoDraft(key, { uri: `file://${key}.jpg`, width: 10, height: 10, fileSize: 10 }),
  status: "uploaded",
  attachmentGuid: guid,
});

const limits = (patch: Partial<ExpenseLimits> = {}): ExpenseLimits => ({
  maxImages: 5,
  dateRange: resolveEntryDateRange({ storeToday: "2026-10-07", maxBackfillDays: 7, canViewAllStores: false }),
  ...patch,
});

const draft = (patch: Partial<ExpenseDraft> = {}): ExpenseDraft => ({
  expenseDate: "2026-10-07",
  category: "Other",
  amountText: "25.50",
  payeeName: "",
  note: "",
  photos: [],
  ...patch,
});

const codes = (value: ExpenseDraft, patch: Partial<ExpenseLimits> = {}) =>
  validateExpenseDraft(value, limits(patch)).map((issue) => issue.code);

// ───────── 类别集合：固定四个，顺序固定，T2 只叫 T2 ─────────
assert.deepEqual([...CASH_EXPENSE_CATEGORIES], ["Salary", "Purchase", "T2", "Other"]);

// ───────── 初始草稿：日期默认门店今天，不预选类别 ─────────
assert.deepEqual(createInitialExpense("2026-10-07"), {
  expenseDate: "2026-10-07",
  category: null,
  amountText: "",
  payeeName: "",
  note: "",
  photos: [],
});

// ───────── 类别规则 ─────────
assert.equal(isReceiptRequired("Purchase"), true, "购物必须带收据");
assert.equal(isReceiptRequired("Salary"), false);
assert.equal(isReceiptRequired("T2"), false);
assert.equal(isReceiptRequired("Other"), false, "其他类别图片可选");
assert.equal(isReceiptRequired(null), false);
assert.equal(isPayeeApplicable("Salary"), true, "工资可填收款人");
assert.equal(isPayeeApplicable("Purchase"), false);

// ───────── 校验 ─────────
assert.deepEqual(codes(draft()), []);
assert.deepEqual(codes(draft({ category: null })), ["categoryMissing"]);
assert.deepEqual(validateExpenseDraft(draft({ amountText: "0" }), limits()), [{ code: "amount", issue: "notPositive" }], "金额必须 > 0");
assert.deepEqual(validateExpenseDraft(draft({ amountText: "" }), limits()), [{ code: "amount", issue: "empty" }]);
assert.deepEqual(validateExpenseDraft(draft({ amountText: "1.234" }), limits()), [{ code: "amount", issue: "tooManyDecimals" }]);
assert.deepEqual(codes(draft({ category: "Purchase" })), ["receiptRequired"], "购物没有收据不通过");
assert.deepEqual(codes(draft({ category: "Purchase", photos: [uploaded("a", "g")] })), []);
assert.deepEqual(codes(draft({ category: "T2" })), [], "T2 不强制图片");
assert.deepEqual(
  codes(draft({ photos: Array.from({ length: 6 }, (_, i) => uploaded(`p${i}`, `g${i}`)) })),
  ["photosTooMany"],
  "最多 maxImagesPerExpense 张",
);
assert.deepEqual(
  codes(draft({ photos: Array.from({ length: 5 }, (_, i) => uploaded(`p${i}`, `g${i}`)) })),
  [],
  "恰好等于上限允许",
);
assert.deepEqual(codes(draft({ expenseDate: "2026-10-08" })), ["dateOutOfRange"]);
assert.deepEqual(codes(draft({ expenseDate: "2026-09-29" })), ["dateOutOfRange"]);
assert.deepEqual(codes(draft({ expenseDate: "2026-09-30" })), []);
assert.deepEqual(codes(draft({ expenseDate: "xx" })), ["dateInvalid"]);
assert.deepEqual(
  codes(draft({ expenseDate: "2025-01-01" }), {
    dateRange: resolveEntryDateRange({ storeToday: "2026-10-07", maxBackfillDays: 7, canViewAllStores: true }),
  }),
  [],
  "有全部分店权限不限回溯",
);
assert.deepEqual(codes(draft({ category: "Salary", payeeName: "x".repeat(101) })), ["payeeTooLong"]);
assert.deepEqual(codes(draft({ category: "Other", payeeName: "x".repeat(101) })), [], "非工资类别不校验收款人");
assert.deepEqual(codes(draft({ note: "x".repeat(501) })), ["noteTooLong"]);

// ───────── 请求体 ─────────
assert.deepEqual(
  buildCreateExpenseRequest(
    draft({ category: "Salary", amountText: "300", payeeName: " 小王 ", note: " 周薪 ", photos: [uploaded("a", "att-1")] }),
    { clientRequestId: "req-1", storeCode: "S01" },
  ),
  {
    clientRequestId: "req-1",
    storeCode: "S01",
    expenseDate: "2026-10-07",
    category: "Salary",
    amount: 300,
    payeeName: "小王",
    note: "周薪",
    attachmentGuids: ["att-1"],
  },
);
assert.equal(
  buildCreateExpenseRequest(draft({ category: "Purchase", payeeName: "残留姓名", photos: [uploaded("a", "g")] }), {
    clientRequestId: "r",
    storeCode: "S01",
  })?.payeeName,
  undefined,
  "切换类别后残留的收款人不带出",
);
assert.deepEqual(
  buildCreateExpenseRequest(draft({ category: "Other" }), { clientRequestId: "r", storeCode: "S01" })?.attachmentGuids,
  [],
  "无图片时 attachmentGuids 为空数组",
);
assert.equal(
  buildCreateExpenseRequest(draft({ photos: [createPhotoDraft("a", { uri: "f", width: 1, height: 1, fileSize: 1 })] }), {
    clientRequestId: "r",
    storeCode: "S01",
  }),
  null,
  "照片未上传完成时不构造请求",
);
assert.equal(buildCreateExpenseRequest(draft({ category: null }), { clientRequestId: "r", storeCode: "S01" }), null);
assert.equal(buildCreateExpenseRequest(draft({ amountText: "abc" }), { clientRequestId: "r", storeCode: "S01" }), null);

// ───────── 照片增删改与上限 ─────────
{
  const photo = (key: string) => createPhotoDraft(key, { uri: `file://${key}`, width: 1, height: 1, fileSize: 1 });
  const start = draft({ photos: [photo("a"), photo("b")] });
  const appended = appendExpensePhotos(start, [photo("c"), photo("d"), photo("e"), photo("f")], 5);
  assert.deepEqual(appended.draft.photos.map((item) => item.key), ["a", "b", "c", "d", "e"], "最多 5 张");
  assert.equal(appended.rejected, 1);
  assert.equal(patchExpensePhoto(appended.draft, "c", { status: "failed", failureStage: "upload" }).photos[2].status, "failed");
  assert.deepEqual(removeExpensePhoto(appended.draft, "a").photos.map((item) => item.key), ["b", "c", "d", "e"]);
}

console.log("expense-form.test.ts: ok");
