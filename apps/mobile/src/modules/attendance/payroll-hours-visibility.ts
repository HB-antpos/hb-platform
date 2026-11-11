// 工时统计列表「0 工时默认收起，可手动添加」的纯逻辑：判断谁算 0 工时、按本机记住的名单拆成显示 / 收起两组。
// 不碰 React 与存储，便于 tsx 单测；存储读写见 payroll-hours-pinned-storage.ts。
import type { PayrollHoursEmployee } from "./payroll-hours-types";

/**
 * 本期「什么都没有」才算 0 工时：没计薪、没带薪假、没有待审、没有已批准角标（年假发放）、
 * 也没有其他分店工时。有待审的人即使计薪为 0 也要显示，否则店长看不到「待审没算进去」的是谁。
 */
export function isZeroHoursEmployee(employee: PayrollHoursEmployee): boolean {
  if (employee.totalHours > 0 || employee.leaveHours > 0 || employee.pendingCount > 0) return false;
  return !employee.days.some(
    (day) =>
      day.paidHours > 0
      || (day.leave?.hours ?? 0) > 0
      || (day.publicHolidayPay?.hours ?? 0) > 0
      || day.pending.length > 0
      || day.approvedMarks.length > 0
      || day.otherStores.some((store) => store.hours > 0),
  );
}

/** 本期是否有排班：收起名单里有排班没打卡的人排前面（最值得店长盯）。 */
export function hasScheduledDay(employee: PayrollHoursEmployee): boolean {
  return employee.days.some((day) => day.isScheduled === true);
}

export interface HoursEmployeeVisibility {
  /** 列表里显示的人，保持后端原顺序。 */
  visible: PayrollHoursEmployee[];
  /** 其中 0 工时、因为在本机名单里才显示的人（卡片标「手动添加」）。 */
  pinnedGuids: Set<string>;
  /** 被收起的 0 工时员工：有排班的在前，其余保持原顺序。 */
  hidden: PayrollHoursEmployee[];
}

/** 按「0 工时 + 不在名单」拆分；名单里的人有了工时就是普通卡，不再算手动添加。 */
export function splitHoursEmployees(employees: PayrollHoursEmployee[], pinned: ReadonlySet<string>): HoursEmployeeVisibility {
  const visible: PayrollHoursEmployee[] = [];
  const hidden: PayrollHoursEmployee[] = [];
  const pinnedGuids = new Set<string>();
  for (const employee of employees) {
    if (!isZeroHoursEmployee(employee)) {
      visible.push(employee);
    } else if (pinned.has(employee.userGuid)) {
      visible.push(employee);
      pinnedGuids.add(employee.userGuid);
    } else {
      hidden.push(employee);
    }
  }
  // 稳定排序：有排班的排前面，同组内保持后端顺序。
  const ordered = hidden
    .map((employee, index) => ({ employee, index, scheduled: hasScheduledDay(employee) }))
    .sort((a, b) => Number(b.scheduled) - Number(a.scheduled) || a.index - b.index)
    .map((entry) => entry.employee);
  return { visible, pinnedGuids, hidden: ordered };
}

/** 本机名单的 AsyncStorage key：按门店分开记（换门店名单不串）。 */
export function pinnedHoursStorageKey(storeCode: string): string {
  return `attendance.hours.pinnedZeroEmployees.${storeCode.trim().toUpperCase()}`;
}

/** 读回的名单做容错：只认字符串数组，去空去重。 */
export function normalizePinnedGuids(value: unknown): string[] {
  if (!Array.isArray(value)) return [];
  const result: string[] = [];
  for (const item of value) {
    if (typeof item !== "string") continue;
    const guid = item.trim();
    if (guid && !result.includes(guid)) result.push(guid);
  }
  return result;
}
