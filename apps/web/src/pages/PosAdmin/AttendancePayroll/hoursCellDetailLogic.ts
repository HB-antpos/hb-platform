// 工时统计「单元格气泡」与工资表「逐日明细气泡」用的纯函数：不碰界面、不读接口，便于单测。
// 数据来源都是工时统计接口（AttendanceHoursDayDto.shifts / segments / byTier / leave …），分钟数与工资计算同一份结果。

import type {
  AttendanceHoursDayDto,
  AttendanceHoursEmployeeDto,
  AttendanceHoursSegmentDto,
  AttendanceHoursShiftDto,
  AttendanceTierHours,
} from '../../../types/attendancePayroll'
import { round2 } from './payrollLogic'

export type TierKey = keyof AttendanceTierHours

/** 档位优先级：同一天只会落在一个档，按这个顺序取第一个有工时的。 */
export const TIER_KEYS: readonly TierKey[] = ['weekday', 'saturday', 'sunday', 'publicHoliday']

/** 这一天计薪工时落在哪个档；没有计薪工时返回 null。 */
export function dayTier(day: Pick<AttendanceHoursDayDto, 'byTier'>): TierKey | null {
  return TIER_KEYS.find((key) => (day.byTier?.[key] ?? 0) > 0) ?? null
}

/** 打卡来源归类：气泡里用不同文案显示（PosQr 收银机扫码 / 店长修改 / 补录 / 系统自动下班 / App）。 */
export type PunchSourceKind = 'pos' | 'adjusted' | 'manual' | 'autoOut' | 'app' | 'other'

export function punchSourceKind(source?: string | null): PunchSourceKind | null {
  if (!source) return null
  switch (source) {
    case 'PosQr': return 'pos'
    case 'ManualAdjustment': return 'adjusted'
    case 'ManualEntry': return 'manual'
    case 'SystemAutoClockOut': return 'autoOut'
    case 'App': return 'app'
    default: return 'other'
  }
}

/** HH:mm → 当天第几分钟；「24:00」= 1440；格式不对返回 null。 */
export function clockToMinutes(value?: string | null): number | null {
  if (!value) return null
  const match = /^(\d{1,2}):(\d{2})$/.exec(value)
  if (!match) return null
  const hours = Number(match[1])
  const minutes = Number(match[2])
  if (minutes > 59 || hours > 24 || (hours === 24 && minutes > 0)) return null
  return hours * 60 + minutes
}

/** 打卡与排班的偏差（分钟，只给非零的）：晚到 / 早到 / 早走 / 晚走。 */
export interface ShiftDeviation {
  lateIn: number
  earlyIn: number
  earlyOut: number
  lateOut: number
}

/** 把时刻（0..1440）按整天平移，取离目标最近的那个：夜班跨午夜时，06:05 的下班要当成次日 30:05 去对比 06:00 的排班结束。 */
function nearestToTarget(value: number, target: number): number {
  let best = value
  for (const shift of [-1440, 1440]) {
    if (Math.abs(value + shift - target) < Math.abs(best - target)) best = value + shift
  }
  return best
}

/**
 * 第一次上班打卡对比排班开始、最后一次下班打卡对比排班结束；没有闭合的班段 / 时间不合法返回 null。
 * 排班结束 ≤ 开始按跨午夜处理；打卡时刻都平移到离对应排班时刻最近的那一天再比较。
 */
export function shiftDeviation(shift: Pick<AttendanceHoursShiftDto, 'scheduleStart' | 'scheduleEnd' | 'segments'>): ShiftDeviation | null {
  const closed = shift.segments.filter((segment) => segment.end && !segment.isAutoClockOut)
  if (closed.length === 0) return null
  const scheduleStart = clockToMinutes(shift.scheduleStart)
  let scheduleEnd = clockToMinutes(shift.scheduleEnd)
  const firstIn = clockToMinutes(closed[0].start)
  const lastOut = clockToMinutes(closed[closed.length - 1].end)
  if (scheduleStart === null || scheduleEnd === null || firstIn === null || lastOut === null) return null
  if (scheduleEnd <= scheduleStart) scheduleEnd += 1440
  const inDiff = nearestToTarget(firstIn, scheduleStart) - scheduleStart
  const outDiff = nearestToTarget(lastOut, scheduleEnd) - scheduleEnd
  return {
    lateIn: Math.max(0, inDiff),
    earlyIn: Math.max(0, -inDiff),
    earlyOut: Math.max(0, -outDiff),
    lateOut: Math.max(0, outDiff),
  }
}

export interface ShiftSegmentView {
  start: string
  /** 没有下班打卡为 null（还没下班或漏打）。 */
  end: string | null
  isAutoClockOut: boolean
  /** 本段时长（分钟）；没闭合 / 系统自动下班为 null。 */
  minutes: number | null
  inSource: PunchSourceKind | null
  outSource: PunchSourceKind | null
  /** 上班打卡的收银机设备码。 */
  deviceCode: string | null
}

export interface ShiftView {
  /** 「09:00–17:30」；旧接口没有排班窗口时为空。 */
  scheduleText: string
  /** 有排班、没有任何打卡段。 */
  missing: boolean
  segments: ShiftSegmentView[]
  deviation: ShiftDeviation | null
  workedMinutes: number
  mealDeductionMinutes: number
  mealAddBackMinutes: number
  overtimeMinutes: number
  paidMinutes: number
}

function segmentView(segment: AttendanceHoursSegmentDto): ShiftSegmentView {
  const start = clockToMinutes(segment.start)
  let end = segment.end ? clockToMinutes(segment.end) : null
  if (start !== null && end !== null && end <= start) end += 1440
  return {
    start: segment.start,
    end: segment.end ?? null,
    isAutoClockOut: segment.isAutoClockOut === true,
    minutes: segment.isAutoClockOut || start === null || end === null ? null : end - start,
    inSource: punchSourceKind(segment.inSource),
    outSource: punchSourceKind(segment.outSource),
    deviceCode: segment.deviceCode?.trim() || null,
  }
}

/** 一个排班的展示模型：排班窗口、各打卡段、偏差、用餐扣除。 */
export function buildShiftView(shift: AttendanceHoursShiftDto): ShiftView {
  const segments = shift.segments.map(segmentView)
  return {
    scheduleText: shift.scheduleStart && shift.scheduleEnd ? `${shift.scheduleStart}–${shift.scheduleEnd}` : '',
    missing: segments.length === 0,
    segments,
    deviation: shiftDeviation(shift),
    workedMinutes: shift.workedMinutes,
    mealDeductionMinutes: shift.mealDeductionMinutes,
    mealAddBackMinutes: shift.mealAddBackMinutes,
    overtimeMinutes: shift.overtimeMinutes,
    paidMinutes: shift.paidMinutes,
  }
}

export interface CellDetailModel {
  /** 有内容才显示气泡：没有排班、没有打卡、没有请假 / 公假 / 补录的休息格不弹。 */
  hasContent: boolean
  tier: TierKey | null
  paidHours: number | null
  shifts: ShiftView[]
}

/**
 * 单元格气泡模型。接口没带 shifts（旧版）但有打卡段时，用 segments 合成一个没有排班窗口的班，
 * 这样气泡至少能列出上班 / 下班时间。
 */
export function buildCellDetail(day: AttendanceHoursDayDto): CellDetailModel {
  let shifts: ShiftView[] = (day.shifts ?? []).map(buildShiftView)
  if (shifts.length === 0 && (day.segments?.length ?? 0) > 0) {
    shifts = [{
      scheduleText: '',
      missing: false,
      segments: (day.segments ?? []).map(segmentView),
      deviation: null,
      workedMinutes: 0,
      mealDeductionMinutes: 0,
      mealAddBackMinutes: 0,
      overtimeMinutes: 0,
      paidMinutes: Math.round((day.paidHours ?? 0) * 60),
    }]
  }
  const hasContent = shifts.length > 0
    || Boolean(day.leave)
    || Boolean(day.publicHolidayPay)
    || Boolean(day.manualEntry)
    || day.otherStores.length > 0
    || day.pending.length > 0
  return { hasContent, tier: dayTier(day), paidHours: day.paidHours, shifts }
}

/** 分钟 → 「30 分钟」「1 小时 5 分钟」；0 返回空串。 */
export function minutesLabel(minutes: number, units: { hour: string; minute: string }): string {
  if (minutes <= 0) return ''
  const hours = Math.floor(minutes / 60)
  const rest = minutes % 60
  if (hours === 0) return `${rest} ${units.minute}`
  return rest === 0 ? `${hours} ${units.hour}` : `${hours} ${units.hour} ${rest} ${units.minute}`
}

// ---------- 工资表：某个数字对应哪几天 ----------

export type BreakdownKey = TierKey | 'leave' | 'total'

export interface BreakdownItem {
  date: string
  /** 这一天计入该数字的小时数。 */
  hours: number
  /** 这一天的档位（leave 项为 'leave'）。 */
  kind: TierKey | 'leave'
  /** 当天打卡时间段（「09:00–17:30」）；请假没有。 */
  ranges: string[]
  /** kind 为 leave 时的请假类型：PaidSick / PaidAnnual / AnnualCashOut / PublicHolidayPay。 */
  leaveType?: string
}

export interface Breakdown {
  items: BreakdownItem[]
  total: number
}

function dayRanges(day: AttendanceHoursDayDto): string[] {
  return (day.segments ?? []).map((segment) => (segment.end ? `${segment.start}–${segment.end}` : `${segment.start}–?`))
}

/**
 * 工资表某个数字（平日 / 周六 / 周日 / 公假 / 带薪假 / 合计）对应哪几天、各多少小时。
 * 档位按每天的 byTier；带薪假按每天的请假申请工时 + 已确认的带薪公假；合计 = 两者之和，按日期排序。
 * 年假的最终发放由财务在工资表核定（可能与申请工时不同），所以调用方要拿 total 和表里的数字比，不一致时提示「以表中数字为准」。
 */
export function buildBreakdown(employee: AttendanceHoursEmployeeDto, key: BreakdownKey): Breakdown {
  const items: BreakdownItem[] = []
  for (const day of employee.days) {
    if (key !== 'leave') {
      for (const tier of TIER_KEYS) {
        if ((key === 'total' || key === tier) && (day.byTier?.[tier] ?? 0) > 0) {
          items.push({ date: day.date, hours: round2(day.byTier[tier]), kind: tier, ranges: dayRanges(day) })
        }
      }
    }
    if (key === 'leave' || key === 'total') {
      // 年假发放照常上班、另计工时，与打卡工时并存：这里按请假申请工时列出
      if (day.leave && day.leave.hours > 0) {
        items.push({ date: day.date, hours: round2(day.leave.hours), kind: 'leave', ranges: [], leaveType: day.leave.type })
      }
      if (day.publicHolidayPay?.state === 'Confirmed' && (day.publicHolidayPay.hours ?? 0) > 0) {
        items.push({ date: day.date, hours: round2(day.publicHolidayPay.hours ?? 0), kind: 'leave', ranges: [], leaveType: 'PublicHolidayPay' })
      }
    }
  }
  items.sort((a, b) => a.date.localeCompare(b.date))
  return { items, total: round2(items.reduce((sum, item) => sum + item.hours, 0)) }
}

/** 气泡合计与表里数字对不上（年假核定与申请不同 / 其他店合并等）：返回 true 时界面加一行「以表中数字为准」。 */
export function breakdownMismatch(breakdown: Breakdown, tableValue: number): boolean {
  return Math.abs(round2(breakdown.total) - round2(tableValue)) > 0.005
}
