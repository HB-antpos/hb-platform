import type { AttendanceScheduleDto, SaveAttendanceSchedulePayload } from '../../../types/scheduleAttendance'

import { getScheduleAvailabilityConflict } from './availabilityLogic'
import type { DayAvailability } from './availabilityLogic'
import { normalizeHm } from './scheduleCellEditorLogic'

/** 周排班拖拽（移动 / 复制 / 托盘添加）的纯逻辑：落点判定、锁定判定、各操作的保存载荷。 */

export type DragKind = 'move' | 'copy' | 'preset'

/** 正在被拖的内容：已有班次（移动 / 复制）或托盘里的常用班次（新建）。 */
export interface DragSource {
  kind: DragKind
  /** 移动 / 复制时的原班次；托盘添加时为空。 */
  schedule?: AttendanceScheduleDto
  start: string
  end: string
}

export type DropVerdict =
  | { state: 'noop' }
  | { state: 'ok' }
  /** 能放但要提醒：员工当天上报了不能上班 / 超出可上班时段。 */
  | { state: 'warn'; reason: 'unavailable' | 'outsideAvailable' }
  /** 不能放：与目标格已有班次时间重叠（后端同样会拒绝）。 */
  | { state: 'bad'; reason: 'overlap' }

const toMinutes = (value: string) => {
  const [hour, minute] = normalizeHm(value).split(':').map(Number)
  return Number.isFinite(hour) && Number.isFinite(minute) ? hour * 60 + minute : undefined
}

/** 两个时段是否重叠（首尾相接不算重叠）；结束不晚于开始的脏数据不判重叠。 */
export function rangesOverlap(aStart: string, aEnd: string, bStart: string, bEnd: string): boolean {
  const a0 = toMinutes(aStart)
  const a1 = toMinutes(aEnd)
  const b0 = toMinutes(bStart)
  const b1 = toMinutes(bEnd)
  if (a0 === undefined || a1 === undefined || b0 === undefined || b1 === undefined) return false
  if (a1 <= a0 || b1 <= b0) return false
  return a0 < b1 && b0 < a1
}

interface DropTarget {
  userGuid: string
  dateKey: string
  /** 目标格里已有的班次（含已取消的，这里会忽略已取消）。 */
  schedules: Pick<AttendanceScheduleDto, 'scheduleGuid' | 'startTime' | 'endTime' | 'status'>[]
  availability?: DayAvailability
}

/**
 * 某个格子对当前拖拽内容的落点判定：
 * - 原地放回（移动时目标就是原格）→ noop；
 * - 与目标格里别的未取消班次时间重叠 → bad（移动时忽略它自己）；
 * - 与员工上报的不能上班 / 可上班时段冲突 → warn（只提醒，不阻止）；
 * - 否则 ok。
 */
export function getDropVerdict(source: DragSource, target: DropTarget, sourceCell?: { userGuid: string; dateKey: string }): DropVerdict {
  if (
    source.kind === 'move'
    && sourceCell
    && sourceCell.userGuid === target.userGuid
    && sourceCell.dateKey === target.dateKey
  ) {
    return { state: 'noop' }
  }

  const ignoreGuid = source.kind === 'move' ? source.schedule?.scheduleGuid : undefined
  const overlapped = target.schedules.some((item) => (
    item.status !== 'Cancelled'
    && item.scheduleGuid !== ignoreGuid
    && rangesOverlap(source.start, source.end, item.startTime, item.endTime)
  ))
  if (overlapped) return { state: 'bad', reason: 'overlap' }

  const conflict = getScheduleAvailabilityConflict(
    { startTime: source.start, endTime: source.end, status: 'Draft' },
    target.availability,
  )
  if (conflict) return { state: 'warn', reason: conflict }
  return { state: 'ok' }
}

/**
 * 班次是否被锁定（不能拖动，「×」仍可用）：
 * - 本周打卡里能查到这条班次的打卡记录 → 锁；
 * - 看不到打卡（没有打卡查看权限，无法确认）时，今天及以前的班次保守地锁住，避免把已经上过班的班次拖走。
 */
export function isScheduleLocked(input: {
  scheduleGuid: string
  workDateKey: string
  todayKey: string
  punchScheduleGuids: ReadonlySet<string>
  canViewPunches: boolean
}): boolean {
  if (input.punchScheduleGuids.has(input.scheduleGuid)) return true
  return !input.canViewPunches && input.workDateKey <= input.todayKey
}

/** 把本周打卡列表整理成「有打卡的排班 GUID 集合」。 */
export function collectPunchScheduleGuids(punches: { scheduleGuid?: string }[]): Set<string> {
  const guids = new Set<string>()
  punches.forEach((punch) => {
    if (punch.scheduleGuid) guids.add(punch.scheduleGuid)
  })
  return guids
}

const base = (schedule: AttendanceScheduleDto, userGuid: string, dateKey: string): SaveAttendanceSchedulePayload => ({
  storeCode: schedule.storeCode,
  userGuid,
  workDate: dateKey,
  startTime: normalizeHm(schedule.startTime),
  endTime: normalizeHm(schedule.endTime),
  remark: schedule.remark,
})

/** 同员工换日期：走「更新排班」，保持原状态；用餐字段不带，后端保持原值。 */
export function buildMoveUpdatePayload(schedule: AttendanceScheduleDto, dateKey: string): SaveAttendanceSchedulePayload {
  return { ...base(schedule, schedule.userGuid, dateKey), status: schedule.status }
}

/** 换员工：新建一条（保持原状态，备注和手动指定的用餐次数一并带过去），之后再删除原班次。 */
export function buildTransferCreatePayload(schedule: AttendanceScheduleDto, userGuid: string, dateKey: string): SaveAttendanceSchedulePayload {
  return {
    ...base(schedule, userGuid, dateKey),
    status: schedule.status,
    ...(typeof schedule.mealBreakCount === 'number' ? { mealBreakCount: schedule.mealBreakCount } : {}),
  }
}

/** ⌥ 复制：新建草稿，备注和手动指定的用餐次数一并带过去，原班次不动。 */
export function buildCopyPayload(schedule: AttendanceScheduleDto, userGuid: string, dateKey: string): SaveAttendanceSchedulePayload {
  return {
    ...base(schedule, userGuid, dateKey),
    status: 'Draft',
    ...(typeof schedule.mealBreakCount === 'number' ? { mealBreakCount: schedule.mealBreakCount } : {}),
  }
}

/** 托盘添加：新建草稿，用餐默认自动。 */
export function buildPresetPayload(storeCode: string, userGuid: string, dateKey: string, start: string, end: string): SaveAttendanceSchedulePayload {
  return { storeCode, userGuid, workDate: dateKey, startTime: start, endTime: end, status: 'Draft' }
}

/** 把「手动指定的用餐次数」转成班次块上的小标记文案键；自动（含 null）不显示标记。 */
export function getMealBadge(mealBreakCount?: number | null): { count: number } | undefined {
  return typeof mealBreakCount === 'number' ? { count: mealBreakCount } : undefined
}
