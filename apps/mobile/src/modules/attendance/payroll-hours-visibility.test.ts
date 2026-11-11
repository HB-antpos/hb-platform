import assert from "node:assert/strict";
import type { PayrollHoursDay, PayrollHoursEmployee } from "./payroll-hours-types";
import {
  hasScheduledDay,
  isZeroHoursEmployee,
  normalizePinnedGuids,
  pinnedHoursStorageKey,
  splitHoursEmployees,
} from "./payroll-hours-visibility";

const tiers = { weekday: 0, saturday: 0, sunday: 0, publicHoliday: 0 };

function day(date: string, patch: Partial<PayrollHoursDay> = {}): PayrollHoursDay {
  return { date, paidHours: 0, byTier: tiers, pending: [], approvedMarks: [], otherStores: [], isBirthday: false, ...patch };
}

function employee(userGuid: string, patch: Partial<PayrollHoursEmployee> = {}): PayrollHoursEmployee {
  return {
    userGuid,
    name: userGuid,
    hasOtherStores: false,
    totalHours: 0,
    byTier: tiers,
    leaveHours: 0,
    pendingCount: 0,
    days: [day("2026-11-09")],
    ...patch,
  };
}

// 什么都没有才算 0 工时。
assert.equal(isZeroHoursEmployee(employee("a")), true);
assert.equal(isZeroHoursEmployee(employee("a", { totalHours: 6.25 })), false);
assert.equal(isZeroHoursEmployee(employee("a", { leaveHours: 7.6 })), false);
// 有待审（计薪还是 0）也要显示，否则看不到「待审没算进去」的是谁。
assert.equal(isZeroHoursEmployee(employee("a", { pendingCount: 1 })), false);
assert.equal(isZeroHoursEmployee(employee("a", { days: [day("2026-11-09", { pending: [{ type: "Punch" }] })] })), false);
// 年假发放角标、其他分店工时、带薪公假也算有内容。
assert.equal(isZeroHoursEmployee(employee("a", { days: [day("2026-11-09", { approvedMarks: ["AnnualCashOut"] })] })), false);
assert.equal(isZeroHoursEmployee(employee("a", { days: [day("2026-11-09", { otherStores: [{ storeCode: "X", hours: 4 }] })] })), false);
assert.equal(isZeroHoursEmployee(employee("a", { days: [day("2026-11-09", { publicHolidayPay: { state: "Confirmed", hours: 7.6 } })] })), false);
// 只是有排班、还没打卡，仍算 0 工时（默认收起）。
assert.equal(isZeroHoursEmployee(employee("a", { days: [day("2026-11-15", { isScheduled: true })] })), true);

assert.equal(hasScheduledDay(employee("a", { days: [day("2026-11-15", { isScheduled: true })] })), true);
assert.equal(hasScheduledDay(employee("a")), false);

// 拆分：有工时的照常显示；名单里的 0 工时员工显示并标为手动添加；其余收起，有排班的排前。
{
  const worked = employee("worked", { totalHours: 12.25 });
  const zeroA = employee("zeroA");
  const zeroScheduled = employee("zeroScheduled", { days: [day("2026-11-15", { isScheduled: true })] });
  const zeroPinned = employee("zeroPinned");
  const zeroB = employee("zeroB");
  const result = splitHoursEmployees([worked, zeroA, zeroScheduled, zeroPinned, zeroB], new Set(["zeroPinned"]));
  assert.deepEqual(result.visible.map((item) => item.userGuid), ["worked", "zeroPinned"]);
  assert.deepEqual([...result.pinnedGuids], ["zeroPinned"]);
  assert.deepEqual(result.hidden.map((item) => item.userGuid), ["zeroScheduled", "zeroA", "zeroB"]);
}

// 名单里的人有了工时就是普通卡，不再标手动添加。
{
  const result = splitHoursEmployees([employee("p", { totalHours: 3 })], new Set(["p"]));
  assert.deepEqual(result.visible.map((item) => item.userGuid), ["p"]);
  assert.equal(result.pinnedGuids.size, 0);
  assert.equal(result.hidden.length, 0);
}

// 名单按门店分 key；读回的值容错。
assert.equal(pinnedHoursStorageKey(" rob01 "), "attendance.hours.pinnedZeroEmployees.ROB01");
assert.deepEqual(normalizePinnedGuids(["a", " a ", "", 3, null, "b"]), ["a", "b"]);
assert.deepEqual(normalizePinnedGuids({ a: 1 }), []);
assert.deepEqual(normalizePinnedGuids(null), []);

console.log("payroll-hours-visibility tests passed");
