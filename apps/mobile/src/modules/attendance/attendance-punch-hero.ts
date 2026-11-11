// 员工「打卡」页主卡片的纯计算：实时已工作时长、离上班还有多久、班次进度、下一个班。
// 时间一律按手机本地 wall time（与打卡页其余显示一致：resolveAttendancePunchDisplayTime 给的就是本地时间）。
import { resolveAttendancePunchDisplayTime } from "./attendance-device-time";
import type { AttendancePunch, AttendanceSchedule, AttendanceWeek } from "./types";

interface HeroSegment {
  clockIn?: AttendancePunch;
  clockOut?: AttendancePunch;
}

/** 本地 wall time 字符串（YYYY-MM-DDTHH:mm[:ss]）→ 毫秒；解析不了返回 undefined。 */
export function localMillis(value?: string) {
  const match = value ? /^(\d{4})-(\d{2})-(\d{2})[T ](\d{2}):(\d{2})(?::(\d{2}))?/.exec(value) : null;
  if (!match) return undefined;
  const [, year, month, day, hour, minute, second = "0"] = match;
  return new Date(Number(year), Number(month) - 1, Number(day), Number(hour), Number(minute), Number(second)).getTime();
}

/**
 * 实时已工作分钟：已闭合的段按上下班时间算，还开着的段算到现在。
 * 系统自动下班（午夜补卡）的段不计。
 */
export function liveWorkedMinutes(segments: HeroSegment[], nowMs: number) {
  let total = 0;
  for (const segment of segments) {
    const start = localMillis(resolveAttendancePunchDisplayTime(segment.clockIn));
    if (start === undefined) continue;
    if (segment.clockOut?.isSystemAutoClockOut) continue;
    const end = segment.clockOut ? localMillis(resolveAttendancePunchDisplayTime(segment.clockOut)) : nowMs;
    if (end === undefined || end <= start) continue;
    total += Math.floor((end - start) / 60_000);
  }
  return total;
}

/** 排班起止（毫秒）；结束早于开始视为跨午夜。 */
export function shiftWindow(workDate: string, startTime?: string, endTime?: string) {
  const day = workDate.slice(0, 10);
  const start = localMillis(`${day}T${(startTime ?? "").slice(0, 5)}`);
  let end = localMillis(`${day}T${(endTime ?? "").slice(0, 5)}`);
  if (start === undefined || end === undefined) return undefined;
  if (end <= start) end += 86_400_000;
  return { start, end };
}

/** 进度条：0～1；还剩多少分钟（已过结束时间为 0）。 */
export function shiftProgress(window: { start: number; end: number }, nowMs: number) {
  const ratio = Math.min(1, Math.max(0, (nowMs - window.start) / (window.end - window.start)));
  return { ratio, remainingMinutes: Math.max(0, Math.ceil((window.end - nowMs) / 60_000)) };
}

/** 上班前提示：离排班开始还有几分钟（正数）或已经过了几分钟（负数）。 */
export function minutesToShiftStart(window: { start: number }, nowMs: number) {
  const diff = window.start - nowMs;
  return diff >= 0 ? Math.ceil(diff / 60_000) : -Math.floor(-diff / 60_000);
}

/** 当天最后一次上班卡（工作中的卡片右上角写「10:02 上班 · 扫收银台 POS-02」）。 */
export function lastClockIn(segments: HeroSegment[]) {
  return [...segments].reverse().find((segment) => segment.clockIn)?.clockIn;
}

/** 指定日期之后本周里的下一个班（不含请假、已取消）。 */
export function nextShiftAfter(week: AttendanceWeek | undefined, afterDate: string): AttendanceSchedule | undefined {
  if (!week) return undefined;
  return week.days
    .filter((day) => day.workDate.slice(0, 10) > afterDate)
    .sort((left, right) => left.workDate.localeCompare(right.workDate))
    .flatMap((day) => day.schedules
      .filter((item) => !item.leaveType && item.status.toLowerCase() !== "cancelled")
      .sort((left, right) => left.startTime.localeCompare(right.startTime)))[0];
}

/** 周日期条每天的小字：有班写「10–18」，请假写假，否则「休」。 */
export function weekDayShiftLabel(schedules: AttendanceSchedule[]) {
  const active = schedules.filter((item) => item.status.toLowerCase() !== "cancelled");
  if (active.some((item) => item.leaveType)) return { kind: "leave" as const };
  const first = [...active].sort((left, right) => left.startTime.localeCompare(right.startTime))[0];
  if (!first) return { kind: "rest" as const };
  const short = (value: string) => {
    const [hour, minute] = value.slice(0, 5).split(":");
    return minute === "00" ? String(Number(hour)) : `${Number(hour)}:${minute}`;
  };
  return { kind: "shift" as const, text: `${short(first.startTime)}–${short(first.endTime)}`, more: active.length - 1 };
}
