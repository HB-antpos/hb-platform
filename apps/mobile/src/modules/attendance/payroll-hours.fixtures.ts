// 工时统计的测试夹具（只给 payroll-hours.test.ts 用，不接界面）：数字对齐设计图
// docs/design/attendance-payroll-20261021/weekly-mobile.png，作为规格验收样例的回归断言。
// 1 周 = 第 42 周（2026-10-12 ~ 10-18），2 周 = 10-05 ~ 10-18（10-05 King's Birthday 公假）。
import {
  addDays,
  isValidDate,
  isoWeekNumber,
  isoWeekday,
  mondayOf,
  periodDates,
  periodEndOf,
  roundHours,
  summarizeEmployeeDays,
  sumReportTotals,
} from "./payroll-hours";
import type {
  PayrollHoursAlert,
  PayrollHoursDay,
  PayrollHoursEmployee,
  PayrollHoursQuery,
  PayrollHoursReport,
  PayrollTierHours,
} from "./payroll-hours-types";

/** 双周起始日（样例）：与规格示例 2026-10-05 一致。 */
const SAMPLE_FORTNIGHT_ANCHOR = "2026-10-05";
/** 不传 periodStart 时的「当前期」：1 周 = 第 42 周，2 周 = 10-05 起的双周。 */
const SAMPLE_CURRENT_WEEK = "2026-10-12";
const SAMPLE_HOLIDAY = { date: "2026-10-05", name: "King's Birthday" };

type Segment = [start: string, end: string, auto?: boolean];

interface SampleDay {
  hours?: number;
  segments?: Segment[];
  unavailable?: boolean;
  leave?: { type: string; hours: number; cert?: boolean };
  pending?: string[];
  cashOut?: boolean;
  holidayPay?: { state: string; hours?: number; count: number };
  otherStore?: { storeCode: string; storeName: string; hours: number };
}

interface SampleEmployee {
  userGuid: string;
  name: string;
  employmentType: string;
  ageYears?: number;
  birthday?: string;
  hasOtherStores?: boolean;
  /** 第 41、42 周，周一 → 周日；其余周期沿用第 42 周的排班样子（去掉待审、假期、生日等一次性事项）。 */
  week41: SampleDay[];
  week42: SampleDay[];
}

const REST: SampleDay = {};

const EMPLOYEES: SampleEmployee[] = [
  {
    userGuid: "sample-hours-sarah",
    name: "Sarah Kim",
    employmentType: "FullTime",
    week41: [
      { holidayPay: { state: "Confirmed", hours: 8, count: 10 } },
      { hours: 8, segments: [["08:58", "13:00"], ["13:30", "17:31"]] },
      { hours: 8, segments: [["08:57", "13:00"], ["13:30", "17:30"]] },
      { hours: 8, segments: [["08:59", "13:00"], ["13:30", "17:32"]] },
      { hours: 8, segments: [["08:58", "13:00"], ["13:30", "17:30"]] },
      REST,
      REST,
    ],
    week42: [
      { hours: 7.98, segments: [["08:58", "13:00"], ["13:30", "17:29"]] },
      { hours: 7.74, segments: [["09:00", "13:00"], ["13:31", "17:15"]] },
      REST,
      { hours: 8, segments: [["08:56", "13:00"], ["13:30", "17:30"]] },
      { hours: 8, segments: [["08:58", "13:00"], ["13:30", "17:31"]] },
      REST,
      { hours: 7.38, segments: [["09:00", "16:53"]] },
    ],
  },
  {
    userGuid: "sample-hours-emma",
    name: "Emma Walsh",
    employmentType: "PartTime",
    week41: [
      { holidayPay: { state: "Pending", count: 8 } },
      { hours: 5.5, segments: [["09:00", "14:30"]] },
      { hours: 5.5, segments: [["09:01", "14:31"]] },
      REST,
      { hours: 4, segments: [["17:00", "21:00"]] },
      { hours: 6, segments: [["10:00", "16:00"]] },
      REST,
    ],
    week42: [
      { hours: 5.5, segments: [["09:00", "14:30"]] },
      { unavailable: true },
      { hours: 5.5, segments: [["08:59", "14:30"]] },
      REST,
      { hours: 4, segments: [["17:00", "21:01"]] },
      { hours: 7.48, segments: [["09:02", "13:00"], ["13:30", "17:00"]] },
      REST,
    ],
  },
  {
    userGuid: "sample-hours-jack",
    name: "Jack Nguyen",
    employmentType: "FullTime",
    week41: [
      { holidayPay: { state: "Confirmed", hours: 7.6, count: 8 } },
      { leave: { type: "PaidSick", hours: 8, cert: true } },
      { hours: 7.98, segments: [["08:56", "13:00"], ["13:31", "17:30"]] },
      REST,
      { hours: 7.5, segments: [["12:59", "17:00"], ["17:30", "21:01"]] },
      { hours: 8, segments: [["09:00", "13:00"], ["13:30", "17:32"]] },
      REST,
    ],
    week42: [
      REST,
      { hours: 8, segments: [["08:55", "13:00"], ["13:30", "17:31"]] },
      REST,
      { hours: 7.8, segments: [["09:12", "13:00"], ["13:30", "17:30"]], pending: ["Late"] },
      { hours: 7.5, segments: [["12:58", "21:02"]] },
      { unavailable: true },
      { hours: 3.5, segments: [["08:57", "12:30"], ["13:02", "00:00", true]], pending: ["PunchAdjustment"] },
    ],
  },
  {
    userGuid: "sample-hours-olivia",
    name: "Olivia Chen",
    employmentType: "PartTime",
    week41: [
      REST,
      { hours: 5.5, segments: [["09:00", "14:30"]] },
      REST,
      { hours: 6, segments: [["12:00", "18:00"]] },
      REST,
      { hours: 5.5, segments: [["10:00", "15:30"]] },
      REST,
    ],
    week42: [
      { hours: 5.5, segments: [["09:00", "14:30"]], cashOut: true },
      REST,
      // 无薪「其他请假」里的病假：红格但不计入带薪假合计。
      { leave: { type: "SickLeave", hours: 6 } },
      REST,
      { hours: 8.5, segments: [["12:30", "21:00"]], pending: ["Overtime"], cashOut: true },
      { hours: 5.5, segments: [["10:00", "15:30"]], cashOut: true },
      REST,
    ],
  },
  {
    userGuid: "sample-hours-liam",
    name: "Liam Patel",
    employmentType: "Casual",
    hasOtherStores: true,
    week41: [
      { hours: 6, segments: [["10:00", "16:00"]] },
      REST,
      { hours: 6, segments: [["09:00", "15:00"]] },
      { hours: 6, segments: [["15:00", "21:00"]] },
      REST,
      { hours: 7, segments: [["09:00", "16:00"]] },
      REST,
    ],
    week42: [
      { hours: 5.98, segments: [["15:00", "20:59"]] },
      { hours: 6, segments: [["09:00", "15:30"]], pending: ["MealBreak"] },
      REST,
      { hours: 6, segments: [["15:00", "21:00"]] },
      { otherStore: { storeCode: "SAMPLE-GC", storeName: "Garden City", hours: 5 } },
      { hours: 7, segments: [["09:00", "16:00"]] },
      { hours: 7, segments: [["10:00", "17:00"]] },
    ],
  },
  {
    userGuid: "sample-hours-mia",
    name: "Mia Robinson",
    employmentType: "Casual",
    ageYears: 16,
    birthday: "2009-10-16",
    week41: [
      REST,
      { hours: 4, segments: [["16:00", "20:00"]] },
      REST,
      { hours: 4, segments: [["16:00", "20:00"]] },
      REST,
      { hours: 6, segments: [["09:00", "15:00"]] },
      { hours: 6, segments: [["10:00", "16:00"]] },
    ],
    week42: [
      REST,
      { hours: 4, segments: [["16:00", "20:00"]] },
      { hours: 4, segments: [["16:00", "20:00"]], pending: ["MissingClockOut"] },
      { hours: 5.49, segments: [["15:30", "20:59"]] },
      { hours: 4, segments: [["16:00", "20:00"]] },
      { hours: 6, segments: [["09:00", "15:00"]] },
      { hours: 6, segments: [["10:00", "16:00"]] },
    ],
  },
];

/** 样例的「当前期」与对齐：1 周对齐到周一；2 周对齐到双周起始日。 */
function resolveSamplePeriodStart(query: PayrollHoursQuery): string {
  const requested = isValidDate(query.periodStart) ? mondayOf(query.periodStart) : mondayOf(SAMPLE_CURRENT_WEEK);
  if (query.weeks !== 2) return requested;
  const offsetDays = Math.round((Date.parse(`${requested}T00:00:00Z`) - Date.parse(`${SAMPLE_FORTNIGHT_ANCHOR}T00:00:00Z`)) / 86400000);
  const fortnightIndex = Math.floor(offsetDays / 14);
  return addDays(SAMPLE_FORTNIGHT_ANCHOR, fortnightIndex * 14);
}

function tierFor(date: string, hours: number, isPublicHoliday: boolean): PayrollTierHours {
  const weekday = isoWeekday(date);
  return {
    weekday: !isPublicHoliday && weekday <= 5 ? hours : 0,
    saturday: !isPublicHoliday && weekday === 6 ? hours : 0,
    sunday: !isPublicHoliday && weekday === 7 ? hours : 0,
    publicHoliday: isPublicHoliday ? hours : 0,
  };
}

function sampleDayFor(employee: SampleEmployee, date: string): SampleDay {
  const index = isoWeekday(date) - 1;
  if (date >= "2026-10-05" && date <= "2026-10-11") return employee.week41[index] ?? REST;
  if (date >= "2026-10-12" && date <= "2026-10-18") return employee.week42[index] ?? REST;
  // 设计图以外的周期：沿用第 42 周的排班与工时，一次性事项（待审、假期、发放、其他分店）不带过去。
  const pattern = employee.week42[index] ?? REST;
  return { hours: pattern.hours, segments: pattern.segments, unavailable: pattern.unavailable };
}

function buildDay(employee: SampleEmployee, date: string, mergeStores: boolean): PayrollHoursDay {
  const spec = sampleDayFor(employee, date);
  const isPublicHoliday = date === SAMPLE_HOLIDAY.date;
  // 合并查看：把其他分店的工时并进当天（只影响显示，不改数据）。
  const mergedHours = mergeStores && spec.otherStore ? spec.otherStore.hours : 0;
  const paidHours = roundHours((spec.hours ?? 0) + mergedHours);
  const birthdayMonthDay = employee.birthday?.slice(5);
  return {
    date,
    paidHours,
    byTier: tierFor(date, paidHours, isPublicHoliday),
    leave: spec.leave
      ? { type: spec.leave.type, hours: spec.leave.hours, hasCertificate: Boolean(spec.leave.cert) }
      : spec.cashOut
        ? { type: "AnnualCashOut", hours: 8, hasCertificate: false }
        : undefined,
    publicHolidayPay: spec.holidayPay,
    pending: (spec.pending ?? []).map((type, index) => ({ type, sourceGuid: `sample-pending-${employee.userGuid}-${date}-${index}` })),
    approvedMarks: spec.cashOut ? ["AnnualCashOut"] : [],
    otherStores: spec.otherStore ? [spec.otherStore] : [],
    isBirthday: Boolean(birthdayMonthDay && date.slice(5) === birthdayMonthDay),
    isPublicHoliday,
    holidayName: isPublicHoliday ? SAMPLE_HOLIDAY.name : undefined,
    isUnavailable: Boolean(spec.unavailable),
    isScheduled: Boolean(spec.hours || spec.leave || spec.pending?.length),
    segments: spec.segments?.map(([start, end, auto]) => ({ start, end, isAutoClockOut: Boolean(auto) })),
  };
}

function buildEmployee(employee: SampleEmployee, dates: string[], mergeStores: boolean): PayrollHoursEmployee {
  const days = dates.map((date) => buildDay(employee, date, mergeStores));
  const birthdayInPeriod = days.find((day) => day.isBirthday);
  // 周岁按期末算：期内过生日的人在期末已经长一岁。
  const ageYears = employee.ageYears !== undefined && birthdayInPeriod ? employee.ageYears + 1 : employee.ageYears;
  return {
    userGuid: employee.userGuid,
    name: employee.name,
    employmentType: employee.employmentType,
    ageYears,
    birthday: employee.birthday,
    segmentLimit: 4,
    hasOtherStores: Boolean(employee.hasOtherStores && days.some((day) => day.otherStores.length > 0)),
    ...summarizeEmployeeDays(days),
    days,
  };
}

function buildAlerts(employees: PayrollHoursEmployee[], mergeStores: boolean): PayrollHoursAlert[] {
  const alerts: PayrollHoursAlert[] = [];
  for (const employee of employees) {
    const birthday = employee.days.find((day) => day.isBirthday);
    if (birthday) {
      const isMinor = employee.ageYears !== undefined && employee.ageYears < 18;
      alerts.push({
        type: isMinor ? "MinorBirthday" : "Birthday",
        userGuid: employee.userGuid,
        employeeName: employee.name,
        date: birthday.date,
        ageYears: employee.ageYears,
      });
    }
  }
  const pendingCount = employees.reduce(
    (sum, employee) => sum + employee.days.reduce((daySum, day) => daySum + day.pending.length, 0),
    0,
  );
  if (pendingCount > 0) alerts.push({ type: "Pending", count: pendingCount });
  const holidayPending = employees.reduce(
    (sum, employee) => sum + employee.days.filter((day) => day.publicHolidayPay?.state === "Pending").length,
    0,
  );
  if (holidayPending > 0) alerts.push({ type: "PublicHolidayPay", count: holidayPending });
  if (!mergeStores) {
    for (const employee of employees.filter((item) => item.hasOtherStores)) {
      alerts.push({ type: "OtherStores", userGuid: employee.userGuid, employeeName: employee.name });
    }
  }
  return alerts;
}

export function buildPayrollHoursFixture(query: PayrollHoursQuery): PayrollHoursReport {
  const weeks: 1 | 2 = query.weeks === 2 ? 2 : 1;
  const periodStart = resolveSamplePeriodStart({ ...query, weeks });
  const dates = periodDates(periodStart, weeks);
  const mergeStores = Boolean(query.mergeStores);
  const employees = EMPLOYEES.map((employee) => buildEmployee(employee, dates, mergeStores));
  return {
    storeCode: query.storeCode,
    storeName: "Chermside",
    periodStart,
    periodEnd: periodEndOf(periodStart, weeks),
    weeks,
    weekNumber: isoWeekNumber(periodStart),
    periodStatus: "Open",
    totals: sumReportTotals(employees),
    alerts: buildAlerts(employees, mergeStores),
    employees,
  };
}

