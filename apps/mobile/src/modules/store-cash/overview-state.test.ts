import assert from "node:assert/strict";
import { orderExpenseCategoryTotals, resolveOverviewState, shouldHighlightDeposit } from "./overview-state";
import type { CashStoreSummary } from "./types";

const summary = (patch: Partial<CashStoreSummary> = {}): CashStoreSummary => ({
  storeCode: "S01",
  storeName: "Store 1",
  asOfDate: "2026-10-07",
  dailyCloseConnected: true,
  latestCloseDate: "2026-10-06",
  openingMissing: false,
  opening: null,
  poolBalance: 1200,
  inflowTotal: 3000,
  depositTotal: 1500,
  expenseTotal: 300,
  expenseByCategory: [],
  t2Restricted: false,
  uncoveredDayCount: 2,
  oldestUncoveredDate: "2026-10-05",
  uncoveredCash: 500,
  depositOverdue: false,
  suggestedDepositAmount: 1200,
  lastDepositDate: "2026-10-01",
  lastCount: null,
  missingCloseDates: [],
  ...patch,
});

const caps = { canCreateDeposit: true };

// ───────── 正常 ─────────
{
  const state = resolveOverviewState({ dailyCloseConnected: true }, summary(), caps);
  assert.equal(state.dailyCloseConnected, true);
  assert.equal(state.balanceAvailable, true);
  assert.equal(state.openingMissing, false);
  assert.equal(state.canRecordOpening, false);
  assert.equal(state.suggestedDepositAvailable, true);
  assert.equal(state.uncoveredDayCount, 2);
}

// ───────── 日结未接入：余额不可算，不能显示成 0 ─────────
{
  const state = resolveOverviewState(
    { dailyCloseConnected: false },
    summary({ dailyCloseConnected: false, poolBalance: null, inflowTotal: null, uncoveredCash: null, suggestedDepositAmount: null, uncoveredDayCount: 0 }),
    caps,
  );
  assert.equal(state.dailyCloseConnected, false);
  assert.equal(state.balanceAvailable, false);
  assert.equal(state.suggestedDepositAvailable, false);
  assert.equal(state.depositOverdue, false);
}
{
  // 上下文说未接入，即使分店总览残留了数值也按未接入处理（取保守口径）
  const state = resolveOverviewState({ dailyCloseConnected: false }, summary(), caps);
  assert.equal(state.dailyCloseConnected, false);
  assert.equal(state.balanceAvailable, false);
  assert.equal(state.depositOverdue, false);
  assert.equal(state.uncoveredDayCount, 0, "日结未接入时未覆盖天数不展示");
}
{
  // 已接入但余额为 null（没有期初）
  const state = resolveOverviewState({ dailyCloseConnected: true }, summary({ poolBalance: null, openingMissing: true }), caps);
  assert.equal(state.balanceAvailable, false);
  assert.equal(state.openingMissing, true);
  assert.equal(state.canRecordOpening, true, "缺期初且有存款能力时引导录入期初");
}
{
  const state = resolveOverviewState({ dailyCloseConnected: true }, summary({ openingMissing: true }), { canCreateDeposit: false });
  assert.equal(state.openingMissing, true);
  assert.equal(state.canRecordOpening, false, "没有能力时只提示不给入口");
}

// ───────── 建议存款额与逾期 ─────────
assert.equal(
  resolveOverviewState({ dailyCloseConnected: true }, summary({ suggestedDepositAmount: 0 }), caps).suggestedDepositAvailable,
  false,
);
assert.equal(
  resolveOverviewState({ dailyCloseConnected: true }, summary({ suggestedDepositAmount: null }), caps).suggestedDepositAvailable,
  false,
);
assert.equal(shouldHighlightDeposit(resolveOverviewState({ dailyCloseConnected: true }, summary(), caps)), true);
assert.equal(
  shouldHighlightDeposit(
    resolveOverviewState({ dailyCloseConnected: true }, summary({ suggestedDepositAmount: null, depositOverdue: true }), caps),
  ),
  true,
);
assert.equal(
  shouldHighlightDeposit(resolveOverviewState({ dailyCloseConnected: true }, summary({ suggestedDepositAmount: null }), caps)),
  false,
);
assert.equal(
  resolveOverviewState({ dailyCloseConnected: true }, summary({ missingCloseDates: ["2026-10-03"] }), caps).hasMissingCloseDates,
  true,
);

// ───────── 分类顺序：现金工资、现金购物、T2、其他；服务端返回什么就展示什么 ─────────
assert.deepEqual(
  orderExpenseCategoryTotals([
    { category: "Other", amount: 1 },
    { category: "T2", amount: 2 },
    { category: "Salary", amount: 3 },
    { category: "Purchase", amount: 4 },
  ]).map((item) => item.category),
  ["Salary", "Purchase", "T2", "Other"],
);
assert.deepEqual(
  orderExpenseCategoryTotals([{ category: "Mystery", amount: 1 }, { category: "Salary", amount: 2 }]).map((item) => item.category),
  ["Salary", "Mystery"],
  "未知类别排最后",
);
assert.deepEqual(
  orderExpenseCategoryTotals([{ category: "Salary", amount: 10 }]),
  [{ category: "Salary", amount: 10 }],
  "不补缺失的类别（T2 的可见范围由服务端决定）",
);

console.log("overview-state.test.ts: ok");
