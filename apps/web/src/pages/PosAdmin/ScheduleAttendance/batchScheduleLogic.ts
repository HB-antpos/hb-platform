// 批量排班（员工 × 日期 × 统一班次）的纯逻辑：生成预览计划、汇总、转成保存载荷、并发提交。
import type { SaveAttendanceSchedulePayload } from '../../../types/scheduleAttendance'

import { getScheduleAvailabilityConflict } from './availabilityLogic'
import type { DayAvailability } from './availabilityLogic'
import { buildScheduleSavePayloads } from './scheduleCellEditorLogic'
import type { MealChoice } from './scheduleCellEditorLogic'
import { rangesOverlap } from './scheduleDragLogic'

/** 预览里一个格子（某员工 × 某天）的判定。 */
export type BatchCellState =
  /** 可以直接新建。 */
  | 'new'
  /** 新建，但超出员工上报的可上班时间：照排，格子带 ⚠。 */
  | 'warn'
  /** 当天已有时间重叠的班次：跳过，不能改成排。 */
  | 'skip'
  /** 员工当天标记了不能上班：默认不排，可手动改成排。 */
  | 'unavailable'

export interface BatchCell {
  userGuid: string
  dateKey: string
  state: BatchCellState
  /** 已有班次的时间（state=skip 时展示）。 */
  existingRange?: string
  /** 最终是否创建：由判定给默认值，用户可点格子翻转（skip 不能翻转）。 */
  include: boolean
}

export interface ExistingShift {
  startTime: string
  endTime: string
  status?: string
}

export interface BatchPlanInput {
  userGuids: readonly string[]
  dateKeys: readonly string[]
  start: string
  end: string
  existingOf: (userGuid: string, dateKey: string) => readonly ExistingShift[]
  availabilityOf: (userGuid: string, dateKey: string) => DayAvailability | undefined
}

const hm = (value: string) => value.slice(0, 5)

/** 按「员工 × 日期」生成预览格子；日期去重并从早到晚，员工保持传入顺序。 */
export function buildBatchPlan(input: BatchPlanInput): BatchCell[] {
  const dateKeys = Array.from(new Set(input.dateKeys)).sort()
  const cells: BatchCell[] = []
  input.userGuids.forEach((userGuid) => {
    dateKeys.forEach((dateKey) => {
      const clash = input.existingOf(userGuid, dateKey).find((item) => (
        item.status !== 'Cancelled' && rangesOverlap(input.start, input.end, item.startTime, item.endTime)
      ))
      if (clash) {
        cells.push({ userGuid, dateKey, state: 'skip', existingRange: `${hm(clash.startTime)}–${hm(clash.endTime)}`, include: false })
        return
      }
      const conflict = getScheduleAvailabilityConflict(
        { startTime: input.start, endTime: input.end, status: 'Draft' },
        input.availabilityOf(userGuid, dateKey),
      )
      if (conflict === 'unavailable') cells.push({ userGuid, dateKey, state: 'unavailable', include: false })
      else if (conflict) cells.push({ userGuid, dateKey, state: 'warn', include: true })
      else cells.push({ userGuid, dateKey, state: 'new', include: true })
    })
  })
  return cells
}

export const batchCellKey = (cell: Pick<BatchCell, 'userGuid' | 'dateKey'>) => `${cell.userGuid}|${cell.dateKey}`

/** 套用用户手动点格子的结果（overrides: 格子键 → 是否创建）；skip 的格子永远不创建。 */
export function applyBatchOverrides(cells: readonly BatchCell[], overrides: ReadonlyMap<string, boolean>): BatchCell[] {
  return cells.map((cell) => {
    if (cell.state === 'skip') return cell
    const override = overrides.get(batchCellKey(cell))
    return override === undefined ? cell : { ...cell, include: override }
  })
}

export interface BatchPlanSummary {
  /** 将要创建的班次数。 */
  create: number
  /** 因已有重叠班次而跳过。 */
  skipped: number
  /** 没跳过、但最终不创建（员工标记不能上班、或用户手动取消）。 */
  excluded: number
  /** 创建的班次里带「超出可上班时间」提醒的数量。 */
  warnings: number
}

export function summarizePlan(cells: readonly BatchCell[]): BatchPlanSummary {
  return {
    create: cells.filter((cell) => cell.include).length,
    skipped: cells.filter((cell) => cell.state === 'skip').length,
    excluded: cells.filter((cell) => cell.state !== 'skip' && !cell.include).length,
    warnings: cells.filter((cell) => cell.include && cell.state === 'warn').length,
  }
}

export interface BatchPayloadOptions {
  storeCode: string
  start: string
  end: string
  remark?: string
  mealChoice?: MealChoice
}

/** 把要创建的格子转成保存载荷（一律草稿）；沿用单格编辑浮层的同一个构造函数，备注去空白、用餐字段口径一致。 */
export function buildBatchPayloads(cells: readonly BatchCell[], options: BatchPayloadOptions): SaveAttendanceSchedulePayload[] {
  const byUser = new Map<string, string[]>()
  cells.filter((cell) => cell.include).forEach((cell) => {
    byUser.set(cell.userGuid, [...(byUser.get(cell.userGuid) ?? []), cell.dateKey])
  })
  return Array.from(byUser.entries()).flatMap(([userGuid, dateKeys]) => buildScheduleSavePayloads({
    storeCode: options.storeCode,
    userGuid,
    dateKeys,
    start: options.start,
    end: options.end,
    remark: options.remark,
    status: 'Draft',
    mealChoice: options.mealChoice,
  }))
}

export interface BatchResultItem {
  userGuid: string
  dateKey: string
  ok: boolean
  error?: string
}

export interface BatchResultSummary {
  created: number
  failed: number
}

export function summarizeResults(items: readonly BatchResultItem[]): BatchResultSummary {
  return { created: items.filter((item) => item.ok).length, failed: items.filter((item) => !item.ok).length }
}

/**
 * 限并发执行：最多同时跑 limit 个任务；单个任务抛错不会中断其它任务（错误由调用方自己在 worker 里记录）。
 * 每个任务结束后回调 onSettled（用于刷新进度）。
 */
export async function runWithConcurrency<T>(
  items: readonly T[],
  limit: number,
  worker: (item: T, index: number) => Promise<void>,
  onSettled?: (item: T, index: number) => void,
): Promise<void> {
  let next = 0
  const size = Math.max(1, Math.min(limit, items.length))
  const lane = async () => {
    while (next < items.length) {
      const index = next
      next += 1
      try {
        await worker(items[index], index)
      } catch {
        // worker 应自己处理错误；这里兜底，避免一个任务异常让整条通道退出
      }
      onSettled?.(items[index], index)
    }
  }
  await Promise.all(Array.from({ length: size }, lane))
}
