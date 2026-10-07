import assert from "node:assert/strict";
import { canViewStoreCash, resolveCashActionAccess } from "./access";
import { STORE_CASH_PERMISSIONS } from "./constants";
import {
  buildBalanceNewHref,
  buildDepositNewHref,
  buildExpenseNewHref,
  buildRecordDetailHref,
  firstParam,
  parseBalanceKind,
  parseRecordDetailKind,
} from "./routes";

// ───────── 入口可见性：已登录 + 非审核模式 + Cash.Overview.View ─────────
const has = (...codes: string[]) => (code: string) => codes.includes(code);
assert.equal(canViewStoreCash(true, has("Cash.Overview.View"), false), true);
assert.equal(canViewStoreCash(false, has("Cash.Overview.View"), false), false, "未登录不可见");
assert.equal(canViewStoreCash(true, has("Cash.Overview.View"), true), false, "iOS 审核离线会话不可见");
assert.equal(canViewStoreCash(true, has("Cash.Deposit.Create"), false), false, "只有存款权限没有查看权限不能进入模块");
assert.equal(canViewStoreCash(true, has(), false), false);
assert.deepEqual(STORE_CASH_PERMISSIONS, {
  overviewView: "Cash.Overview.View",
  depositCreate: "Cash.Deposit.Create",
  expenseCreate: "Cash.Expense.Create",
  void: "Cash.Void",
  allStoresView: "Cash.AllStores.View",
});

// ───────── 按钮显隐只看 capabilities ─────────
assert.deepEqual(
  resolveCashActionAccess({ canCreateDeposit: true, canCreateExpense: false, canViewAllStores: false, canVoid: false }),
  { canDeposit: true, canExpense: false, canManageBalance: true },
);
assert.deepEqual(
  resolveCashActionAccess({ canCreateDeposit: false, canCreateExpense: true, canViewAllStores: true, canVoid: true }),
  { canDeposit: false, canExpense: true, canManageBalance: false },
  "日结选择、期初、盘点与存款同属 Cash.Deposit.Create",
);

// ───────── 路径 ─────────
assert.equal(buildDepositNewHref("S 01"), "/store-cash/deposit-new?storeCode=S%2001");
assert.equal(buildExpenseNewHref("S01"), "/store-cash/expense-new?storeCode=S01");
assert.equal(buildBalanceNewHref("S01", "opening"), "/store-cash/balance-new?storeCode=S01&kind=opening");
assert.equal(
  buildRecordDetailHref("deposit", "a/b", "S01"),
  "/store-cash/record-detail?kind=deposit&guid=a%2Fb&storeCode=S01",
  "guid 必须编码",
);
assert.equal(
  buildRecordDetailHref("expense", "e1", "S01", { created: true }),
  "/store-cash/record-detail?kind=expense&guid=e1&storeCode=S01&created=1",
  "刚提交成功时带 created 标记",
);
assert.equal(firstParam("x"), "x");
assert.equal(firstParam(["x", "y"]), "x");
assert.equal(firstParam(undefined), "");
assert.equal(parseBalanceKind("opening"), "opening");
assert.equal(parseBalanceKind("count"), "count");
assert.equal(parseBalanceKind(undefined), "count", "未知值按盘点处理，不会误入期初");
assert.equal(parseRecordDetailKind("deposit"), "deposit");
assert.equal(parseRecordDetailKind(["expense"]), "expense");
assert.equal(parseRecordDetailKind("other"), null);

console.log("routes-access.test.ts: ok");
