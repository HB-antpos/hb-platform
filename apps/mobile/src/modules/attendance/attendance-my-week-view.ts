// 员工「我的排班 / 我的申请」改版用到的纯计算：本周汇总、每天结果、下周可上班填写进度、申请进度。
import { schedulePaidMinutes, type MyWeekRow } from "./attendance-my-week";
import { toAttendanceUtcMillis } from "./attendance-device-time";
import type { AttendanceSchedule } from "./types";

function isCountedShift(schedule: AttendanceSchedule) {
  return !schedule.leaveType && schedule.status.toLowerCase() !== "cancelled";
}

/** 已完成班次的计薪分钟：后端算好的优先（含用餐扣除与加回），旧后端退回排班计薪工时。 */
function completedPaidMinutes(schedule: AttendanceSchedule) {
  return schedule.paidMinutes ?? schedulePaidMinutes(schedule);
}

export interface MyWeekSummary {
  /** 不含请假、已取消的班次数。 */
  shiftCount: number;
  /** 已完成的班次数与计薪分钟（实际）。 */
  doneCount: number;
  doneMinutes: number;
  /** 周一到周日每天的排班计薪分钟，汇总卡的小柱子用。 */
  dailyMinutes: number[];
}

/** 本周汇总卡：几个班、已上几班、每天的小时数。总小时与工作日 / 周末拆分仍走 computeScheduleHourStats。 */
export function summarizeMyWeek(rows: MyWeekRow[]): MyWeekSummary {
  let shiftCount = 0;
  let doneCount = 0;
  let doneMinutes = 0;
  const dailyMinutes = rows.map((row) => {
    const shifts = row.schedules.filter(isCountedShift);
    shiftCount += shifts.length;
    for (const schedule of shifts) {
      if (schedule.scheduleState?.toLowerCase() === "completed") {
        doneCount += 1;
        doneMinutes += completedPaidMinutes(schedule);
      }
    }
    return shifts.reduce((sum, schedule) => sum + schedulePaidMinutes(schedule), 0);
  });
  return { shiftCount, doneCount, doneMinutes, dailyMinutes };
}

export type MyShiftOutcome =
  | { kind: "leave"; leaveType: string }
  | { kind: "completed"; paidMinutes: number }
  | { kind: "missingClockOut" }
  | { kind: "absent" }
  | { kind: "working"; workedMinutes: number }
  | { kind: "onBreak"; workedMinutes: number }
  | { kind: "upcoming" }
  /** 旧后端没返回班次状态的过去日子：只能写「已完成」。 */
  | { kind: "pastUnknown" };

/**
 * 每个班的结果：后端 scheduleState（NotStarted / Working / OnBreak / MissingClockOut / Completed）+ 是否已过去。
 * 过去的日子还是 NotStarted 说明一次卡都没打，写「未打卡」。
 */
export function myShiftOutcome(schedule: AttendanceSchedule, isPast: boolean): MyShiftOutcome {
  if (schedule.leaveType) return { kind: "leave", leaveType: schedule.leaveType };
  const state = schedule.scheduleState?.toLowerCase();
  if (state === "completed") return { kind: "completed", paidMinutes: completedPaidMinutes(schedule) };
  if (state === "missingclockout") return { kind: "missingClockOut" };
  if (state === "working") return { kind: "working", workedMinutes: schedule.workedMinutes ?? 0 };
  if (state === "onbreak") return { kind: "onBreak", workedMinutes: schedule.workedMinutes ?? 0 };
  if (state === "notstarted") return isPast ? { kind: "absent" } : { kind: "upcoming" };
  return isPast ? { kind: "pastUnknown" } : { kind: "upcoming" };
}

export interface AvailabilityFillProgress {
  /** 还能填的日子（今天及以后、没排班）。 */
  total: number;
  /** 其中已经填了可上班或不能上班的。 */
  filled: number;
  /** 这一周一个班都还没发布。 */
  unpublished: boolean;
}

/** 下周「先报可上班时间」的进度：只数今天及以后、还没排班的日子。 */
export function availabilityFillProgress(rows: MyWeekRow[]): AvailabilityFillProgress {
  const open = rows.filter((row) => !row.isPast && row.state !== "scheduled");
  return {
    total: open.length,
    filled: open.filter((row) => row.state === "available" || row.state === "unavailable").length,
    unpublished: rows.every((row) => row.schedules.length === 0),
  };
}

/** 申请卡三步进度：1 已提交 → 2 店长审核 → 3 已处理（生效 / 驳回 / 撤回都算走完）。 */
export function requestProgressStep(status: string): 2 | 3 {
  return status.toLowerCase() === "pending" ? 2 : 3;
}

export type WaitedDuration = { unit: "minutes" | "hours" | "days"; count: number };

/** 「已等多久」：提交时间（UTC）到现在；不到 1 小时按分钟、不到 2 天按小时，否则按天。 */
export function waitedSince(submittedAt: string | undefined, nowMs: number): WaitedDuration | undefined {
  const millis = toAttendanceUtcMillis(submittedAt);
  if (millis === undefined) return undefined;
  const minutes = Math.max(0, Math.floor((nowMs - millis) / 60_000));
  if (minutes < 60) return { unit: "minutes", count: minutes };
  const hours = Math.floor(minutes / 60);
  if (hours < 48) return { unit: "hours", count: hours };
  return { unit: "days", count: Math.floor(hours / 24) };
}
