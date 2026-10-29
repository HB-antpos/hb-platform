import assert from "node:assert/strict";
import { buildPayrollHoursFixture } from "./payroll-hours.fixtures";
import {
  emptyPayrollDay,
  employeeInitials,
  fillPeriodDays,
  formatHours,
  formatShortDate,
  groupDaysByWeek,
  isoWeekNumber,
  mondayOf,
  normalizePayrollHoursReport,
  pendingLabelKey,
  periodStartForWeeksSwitch,
  periodWeekNumbers,
  resolveDayDisplay,
  shiftPeriodStart,
  tierSummaryParts,
  weekendHoursOf,
} from "./payroll-hours";
import type { PayrollHoursDay } from "./payroll-hours-types";

const day = (date: string, patch: Partial<PayrollHoursDay> = {}): PayrollHoursDay => ({ ...emptyPayrollDay(date), ...patch });

// ── 小时格式：最多 2 位小数、去尾零 ──
assert.equal(formatHours(7.6), "7.6");
assert.equal(formatHours(8), "8");
assert.equal(formatHours(7.983333), "7.98");
assert.equal(formatHours(0.1 + 0.2), "0.3");
assert.equal(formatHours(39.1), "39.1");
assert.equal(formatHours(-0.001), "0");
assert.equal(formatHours(Number.NaN), "0");

// ── ISO 周序号与周起点 ──
assert.equal(isoWeekNumber("2026-10-12"), 42);
assert.equal(isoWeekNumber("2026-10-18"), 42, "周日仍属本周");
assert.equal(isoWeekNumber("2026-10-05"), 41);
assert.equal(isoWeekNumber("2026-01-01"), 1, "2026-01-01 是周四，属第 1 周");
assert.equal(isoWeekNumber("2021-01-03"), 53, "2021-01-03 属 2020 年第 53 周");
assert.equal(isoWeekNumber("bad"), 0);
assert.equal(mondayOf("2026-10-18"), "2026-10-12");
assert.equal(mondayOf("2026-10-12"), "2026-10-12");
assert.deepEqual(periodWeekNumbers("2026-10-05", 2), { first: 41, last: 42 });
assert.deepEqual(periodWeekNumbers("2026-12-28", 2), { first: 53, last: 1 }, "跨年双周");

// ── 翻页与周数切换 ──
assert.equal(shiftPeriodStart("2026-10-12", 1, -1), "2026-10-05");
assert.equal(shiftPeriodStart("2026-10-05", 2, 1), "2026-10-19");
assert.equal(shiftPeriodStart("2026-11-02", 1, -1), "2026-10-26", "跨月");
assert.equal(periodStartForWeeksSwitch({ periodStart: "2026-10-12", fromWeeks: 1, toWeeks: 2, today: "2026-10-26" }), "2026-10-12", "1→2 交给后端对齐双周");
assert.equal(periodStartForWeeksSwitch({ periodStart: "2026-10-05", fromWeeks: 2, toWeeks: 1, today: "2026-10-26" }), "2026-10-12", "2→1 默认看第二周");
assert.equal(periodStartForWeeksSwitch({ periodStart: "2026-10-19", fromWeeks: 2, toWeeks: 1, today: "2026-10-22" }), "2026-10-19", "第二周还没开始就看第一周");

// ── 短日期：中文月-日，英文按澳洲习惯日/月 ──
assert.equal(formatShortDate("2026-10-05", "zh"), "10-05");
assert.equal(formatShortDate("2026-10-05", "en"), "5/10");

// ── 补齐与按周分组 ──
const sparse = [day("2026-10-07", { paidHours: 7.98 }), day("2026-10-14", { paidHours: 8 }), day("2026-10-30", { paidHours: 9 })];
const filled = fillPeriodDays(sparse, "2026-10-05", 2);
assert.equal(filled.length, 14);
assert.equal(filled[0].date, "2026-10-05");
assert.equal(filled[13].date, "2026-10-18");
const groups = groupDaysByWeek(sparse, "2026-10-05", 2);
assert.deepEqual(groups.map((group) => [group.weekNumber, group.start, group.end, group.totalHours]), [
  [41, "2026-10-05", "2026-10-11", 7.98],
  [42, "2026-10-12", "2026-10-18", 8],
]);

// ── 某天显示状态 ──
assert.equal(resolveDayDisplay(day("2026-10-14")).kind, "rest");
assert.equal(resolveDayDisplay(day("2026-10-14", { isScheduled: true })).kind, "scheduled");
assert.equal(resolveDayDisplay(day("2026-10-17", { isUnavailable: true })).kind, "unavailable");
assert.equal(resolveDayDisplay(day("2026-10-06", { leave: { type: "PaidSick", hours: 8, hasCertificate: true } })).kind, "sickLeave");
assert.equal(resolveDayDisplay(day("2026-10-06", { leave: { type: "PaidAnnual", hours: 8, hasCertificate: false }, isUnavailable: true })).kind, "annualLeave", "年假休息优先于不能上班");
const holiday = resolveDayDisplay(day("2026-10-05", { publicHolidayPay: { state: "Confirmed", hours: 7.6 } }));
assert.equal(holiday.kind, "publicHoliday");
assert.equal(holiday.hours, 7.6);
assert.equal(holiday.isPending, false);
const holidayPending = resolveDayDisplay(day("2026-10-05", { publicHolidayPay: { state: "Pending", count: 8 } }));
assert.equal(holidayPending.hours, 0);
assert.equal(holidayPending.isPending, true, "带薪公假待确认算待审");
assert.equal(resolveDayDisplay(day("2026-10-05", { publicHolidayPay: { state: "NotApplicable" } })).kind, "rest");
assert.equal(resolveDayDisplay(day("2026-10-18", { pending: [{ type: "PunchAdjustment" }] })).kind, "pending");
const sunday = resolveDayDisplay(day("2026-10-18", { paidHours: 3.5, pending: [{ type: "PunchAdjustment" }] }));
assert.deepEqual([sunday.kind, sunday.hours, sunday.isPending, sunday.isWeekend], ["worked", 3.5, true, true]);
const cashOut = resolveDayDisplay(day("2026-10-12", { paidHours: 5.5, approvedMarks: ["AnnualCashOut"] }));
assert.deepEqual([cashOut.kind, cashOut.isCashOut, cashOut.isWeekend], ["worked", true, false], "年假发放照常上班，只打角标");

// ── 分档小计：只列非零 ──
assert.deepEqual(tierSummaryParts({ weekday: 31.72, saturday: 0, sunday: 7.38, publicHoliday: 0 }, 0), [
  { key: "weekday", hours: 31.72 },
  { key: "sunday", hours: 7.38 },
]);
assert.deepEqual(tierSummaryParts({ weekday: 14, saturday: 5.5, sunday: 0, publicHoliday: 0 }, 24).map((part) => part.key), ["weekday", "saturday", "leave"]);
assert.deepEqual(tierSummaryParts({ weekday: 0, saturday: 0, sunday: 0, publicHoliday: 0 }, 0), []);

// ── 待审类型 → 文案 key ──
assert.equal(pendingLabelKey("PunchAdjustment"), "punchAdjustment");
assert.equal(pendingLabelKey("Late"), "late");
assert.equal(pendingLabelKey("Whatever"), "other");

// ── 头像首字母 ──
assert.equal(employeeInitials("[Temp] Sarah Kim"), "SK");
assert.equal(employeeInitials("Test Person Name"), "TN");
assert.equal(employeeInitials("测试员"), "测");
assert.equal(employeeInitials("  "), "?");

// ── 接口归一化：PascalCase、缺字段、totals 兜底 ──
const normalized = normalizePayrollHoursReport(
  {
    StoreCode: "S1",
    PeriodStart: "2026-10-12T00:00:00",
    Weeks: 1,
    Employees: [
      {
        UserGuid: "u-1",
        Name: "Test Person A",
        EmploymentType: "PartTime",
        Days: [
          { Date: "2026-10-13T00:00:00", PaidHours: 7.983, ByTier: { Weekday: 7.983 }, Pending: ["Late"] },
          { Date: "2026-10-18", PaidHours: 3.5, ByTier: { Sunday: 3.5 }, Leave: { Type: "PaidSick", Hours: 8 } },
          { Date: "not-a-date", PaidHours: 99 },
        ],
      },
      { Name: "缺 GUID 的行会被丢弃" },
      { userGuid: "u-2", name: "Test Person B", totalHours: 10, byTier: { weekday: 10 }, leaveHours: 0, pendingCount: 0, days: [] },
    ],
    Alerts: [{ Type: "Pending", Count: 1 }, { Message: "没有 type 的提醒丢弃" }],
  },
  { storeCode: "S1", weeks: 1 },
);
assert.equal(normalized.periodStart, "2026-10-12");
assert.equal(normalized.periodEnd, "2026-10-18");
assert.equal(normalized.weekNumber, 42);
assert.equal(normalized.employees.length, 2);
const personA = normalized.employees[0];
assert.equal(personA.days.length, 2, "非法日期的天被丢弃");
assert.equal(personA.totalHours, 11.48, "缺 totalHours 时按天合计");
assert.deepEqual(personA.byTier, { weekday: 7.98, saturday: 0, sunday: 3.5, publicHoliday: 0 });
assert.equal(personA.leaveHours, 8);
assert.equal(personA.pendingCount, 1);
assert.equal(personA.hasOtherStores, false);
assert.deepEqual(personA.days[0].pending.map((item) => item.type), ["Late"], "字符串形式的待审也接受");
assert.deepEqual(normalized.totals, { paidHours: 21.48, weekendHours: 3.5, publicHolidayHours: 0, leaveHours: 8, pendingCount: 1 });
assert.deepEqual(normalized.alerts.map((alert) => [alert.type, alert.count]), [["Pending", 1]]);

const empty = normalizePayrollHoursReport(null, { storeCode: "S2", periodStart: "2026-10-05", weeks: 2 });
assert.deepEqual([empty.storeCode, empty.periodStart, empty.periodEnd, empty.weeks, empty.employees.length], ["S2", "2026-10-05", "2026-10-18", 2, 0]);
assert.equal(normalizePayrollHoursReport({ weeks: 3 }, { storeCode: "S3", weeks: 1 }).weeks, 1, "周数只认 1 / 2");

// ── 夹具对齐设计图数字 ──
const week42 = buildPayrollHoursFixture({ storeCode: "SAMPLE", weeks: 1 });
assert.equal(week42.periodStart, "2026-10-12");
assert.deepEqual(
  [week42.totals.paidHours, week42.totals.weekendHours, week42.totals.leaveHours, week42.totals.pendingCount],
  [169.35, 49.86, 24, 5],
  "A 屏：计薪 169.35 / 周末 49.86 / 带薪假 24 / 5 项待审",
);
const sampleTotals = Object.fromEntries(week42.employees.map((employee) => [employee.userGuid, employee.totalHours]));
assert.equal(sampleTotals["sample-hours-sarah"], 39.1);
assert.equal(sampleTotals["sample-hours-emma"], 22.48);
assert.equal(sampleTotals["sample-hours-jack"], 26.8);
assert.equal(sampleTotals["sample-hours-olivia"], 19.5);
assert.ok(week42.alerts.some((alert) => alert.type === "MinorBirthday" && alert.ageYears === 17 && alert.date === "2026-10-16"));

const fortnight = buildPayrollHoursFixture({ storeCode: "SAMPLE", periodStart: "2026-10-12", weeks: 2 });
assert.equal(fortnight.periodStart, "2026-10-05", "2 周对齐双周起始日");
const jack = fortnight.employees.find((employee) => employee.userGuid === "sample-hours-jack");
assert.ok(jack);
assert.deepEqual(
  [jack.totalHours, weekendHoursOf(jack.byTier), jack.byTier.publicHoliday, jack.leaveHours, jack.pendingCount],
  [50.28, 11.5, 0, 15.6, 2],
  "B 屏：计薪 50.28 / 周末 11.5 / 公 0 / 带薪假 15.6 / 待审 2",
);
assert.deepEqual(groupDaysByWeek(jack.days, fortnight.periodStart, 2).map((group) => group.totalHours), [23.48, 26.8]);

const merged = buildPayrollHoursFixture({ storeCode: "SAMPLE", weeks: 1, mergeStores: true });
assert.equal(merged.totals.paidHours, 174.35, "合并查看把其他分店工时并进来");
assert.ok(!merged.alerts.some((alert) => alert.type === "OtherStores"));

// ── 卡 B 后端实际形状（AttendancePayrollDtos.cs）：totals 对象、isManager、hasSchedule、字符串 pending、MultiStore / Birthday 提醒 ──
const backend = normalizePayrollHoursReport({
  storeCode: "S9",
  periodStart: "2026-10-12T00:00:00",
  periodEnd: "2026-10-18T00:00:00",
  weeks: 1,
  weekNumber: 42,
  pendingCount: 3,
  totals: { weekday: 30, saturday: 4, sunday: 2.5, publicHoliday: 0, leave: 8, total: 44.5 },
  employees: [{
    userGuid: "u1",
    userName: "Test Person",
    employmentType: "PartTime",
    isManager: true,
    pendingCount: 1,
    totals: { weekday: 30, saturday: 4, sunday: 2.5, publicHoliday: 0, leave: 8, total: 44.5 },
    days: [{ date: "2026-10-13T00:00:00", paidHours: 8, byTier: { weekday: 8 }, hasSchedule: true, pending: ["PunchAdjustment"], approvedMarks: [], otherStores: [], segments: [{ start: "13:02", end: "24:00", isAutoClockOut: true }] }],
  }],
  alerts: [
    { type: "MultiStore", otherStoreItems: [{ userGuid: "u1", userName: "Test Person", storeCode: "X", hours: 2 }, { userGuid: "u1", userName: "Test Person", storeCode: "Y", hours: 1 }] },
    { type: "Birthday", userGuid: "u1", userName: "Test Person", date: "2026-10-16T00:00:00", ageYears: 17, isMinor: true },
    { type: "PublicHolidayPay", userGuid: "u1" },
    { type: "PublicHolidayPay", userGuid: "u2" },
  ],
}, { storeCode: "S9", weeks: 1 });
assert.deepEqual(backend.totals, { paidHours: 44.5, weekendHours: 6.5, publicHolidayHours: 0, leaveHours: 8, pendingCount: 3 });
assert.equal(backend.employees[0].name, "Test Person");
assert.equal(backend.employees[0].totalHours, 44.5);
assert.equal(backend.employees[0].leaveHours, 8);
assert.equal(backend.employees[0].byTier.sunday, 2.5);
assert.equal(backend.employees[0].isStoreManager, true);
assert.equal(backend.employees[0].days[0].isScheduled, true);
assert.deepEqual(backend.employees[0].days[0].pending, [{ type: "PunchAdjustment" }]);
assert.equal(backend.employees[0].days[0].segments?.[0].end, "00:00", "自动下班 24:00 显示为 00:00");
assert.deepEqual(backend.alerts.map((alert) => alert.type), ["OtherStores", "MinorBirthday", "PublicHolidayPay"]);
assert.equal(backend.alerts[0].employeeName, "Test Person");
assert.equal(backend.alerts[2].count, 2);

console.log("payroll-hours.test.ts: ok");
