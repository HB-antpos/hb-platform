import assert from "node:assert/strict";
import {
  buildCreateCountRequest,
  buildSetOpeningRequest,
  canRecordOpening,
  createInitialBalance,
  describeCountOutcome,
  resolveBalanceDateRange,
  validateBalanceDraft,
} from "./balance-form";
import { resolveEntryDateRange } from "./dates";

const entryRange = resolveEntryDateRange({ storeToday: "2026-10-07", maxBackfillDays: 7, canViewAllStores: false });
const openingRange = resolveBalanceDateRange("opening", { storeToday: "2026-10-07", entryRange });
const countRange = resolveBalanceDateRange("count", { storeToday: "2026-10-07", entryRange });

// 期初：日期只要不晚于门店今天；盘点沿用补录窗口
assert.deepEqual(openingRange, { min: null, max: "2026-10-07" });
assert.deepEqual(countRange, entryRange);

// ───────── 初始草稿 ─────────
assert.deepEqual(createInitialBalance("opening", "2026-10-07"), {
  kind: "opening",
  entryDate: "2026-10-07",
  amountText: "",
  note: "",
});

// ───────── 只有缺期初且有存款能力才可录入期初 ─────────
assert.equal(canRecordOpening(true, true), true);
assert.equal(canRecordOpening(false, true), false, "已有期初不能再录");
assert.equal(canRecordOpening(true, false), false, "没有 Cash.Deposit.Create 不能录");

// ───────── 校验 ─────────
const codes = (kind: "opening" | "count", patch: Partial<ReturnType<typeof createInitialBalance>>) =>
  validateBalanceDraft(
    { ...createInitialBalance(kind, "2026-10-07"), amountText: "100", ...patch },
    kind === "opening" ? openingRange : countRange,
  ).map((issue) => issue.code);

assert.deepEqual(codes("opening", {}), []);
assert.deepEqual(codes("opening", { amountText: "0" }), [], "期初允许 0");
assert.deepEqual(codes("count", { amountText: "0" }), [], "盘点允许 0");
assert.deepEqual(codes("opening", { amountText: "" }), ["amount"]);
assert.deepEqual(codes("opening", { amountText: "-1" }), ["amount"], "金额不能为负");
assert.deepEqual(codes("opening", { entryDate: "2026-10-08" }), ["dateOutOfRange"], "期初日期不能晚于门店今天");
assert.deepEqual(codes("opening", { entryDate: "2025-01-01" }), [], "期初可以是很早的日期");
assert.deepEqual(codes("count", { entryDate: "2025-01-01" }), ["dateOutOfRange"], "盘点不能超出回溯窗口");
assert.deepEqual(codes("opening", { entryDate: "bad" }), ["dateInvalid"]);
assert.deepEqual(codes("count", { note: "x".repeat(501) }), ["noteTooLong"]);

// ───────── 请求体 ─────────
assert.deepEqual(
  buildSetOpeningRequest(
    { kind: "opening", entryDate: "2026-10-01", amountText: "1500.5", note: " 开账 " },
    { clientRequestId: "r1", storeCode: "S01" },
  ),
  { clientRequestId: "r1", storeCode: "S01", entryDate: "2026-10-01", amount: 1500.5, note: "开账" },
);
assert.deepEqual(
  buildCreateCountRequest(
    { kind: "count", entryDate: "2026-10-07", amountText: "0", note: "" },
    { clientRequestId: "r2", storeCode: "S01" },
  ),
  { clientRequestId: "r2", storeCode: "S01", entryDate: "2026-10-07", amount: 0, note: undefined },
);
assert.equal(
  buildSetOpeningRequest(
    { kind: "count", entryDate: "2026-10-07", amountText: "1", note: "" },
    { clientRequestId: "r", storeCode: "S01" },
  ),
  null,
  "盘点草稿不能当期初提交",
);
assert.equal(
  buildCreateCountRequest(
    { kind: "count", entryDate: "2026-10-07", amountText: "abc", note: "" },
    { clientRequestId: "r", storeCode: "S01" },
  ),
  null,
);

// ───────── 盘点结果：difference 可能为 null ─────────
assert.deepEqual(describeCountOutcome({ difference: null }), { kind: "unknown" });
assert.deepEqual(describeCountOutcome({ difference: 0 }), { kind: "balanced", difference: 0 });
assert.deepEqual(describeCountOutcome({ difference: 5.25 }), { kind: "over", difference: 5.25 });
assert.deepEqual(describeCountOutcome({ difference: -3 }), { kind: "short", difference: -3 });
assert.deepEqual(describeCountOutcome({ difference: 0.004 }), { kind: "balanced", difference: 0 }, "不足一分视为平");

console.log("balance-form.test.ts: ok");
