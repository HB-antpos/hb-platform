// 导出排班图片的数据模型（纯函数，便于单测）。
// 刻意只接收 / 输出「发给员工看的」那几样：名字、起止时间、员工颜色、午饭标识、（可选）计薪工时、草稿标记。
// 管理者专用的信息（出生年龄、打卡、锁定、冲突提示、备注……）在类型里根本没有对应字段：
// 不是靠样式隐藏，也不会被「检查元素」翻出来，所以不可能出现在导出图里。
//
// 内容范围：默认只导出已发布（Active）的班次；草稿由用户在弹窗里勾选才带上（并标「草稿」）；已取消的永远不导出。

import { getMealMark } from './mealMarkLogic'
import type { MealMark } from './mealMarkLogic'
import { getMealSummary, normalizeHm, toMealChoice } from './scheduleCellEditorLogic'

export interface ExportOptions {
  /** 在卡片上显示有无午饭标识。 */
  showMeal: boolean
  /** 在卡片上显示计薪工时（扣除用餐后）。默认关：工时与计薪相关，不适合默认发到群里。 */
  showHours: boolean
  /** 连草稿班次一起导出（卡片虚线边框并标「草稿」）。 */
  includeDrafts: boolean
}

export const DEFAULT_EXPORT_OPTIONS: ExportOptions = { showMeal: true, showHours: false, includeDrafts: false }

/** 导出只读取班次的这几个字段（AttendanceScheduleDto 可直接传入，多余字段一律不读）。 */
export interface ExportSourceSchedule {
  scheduleGuid: string
  userGuid: string
  /** YYYY-MM-DD，或带时间的 ISO 串（只取前 10 位）。 */
  workDate: string
  startTime: string
  endTime: string
  /** Active＝已发布、Draft＝草稿、Cancelled＝已取消。 */
  status: string
  mealBreakCount?: number | null
}

export interface ExportColor {
  bg: string
  bar: string
  ink: string
}

export interface ExportCard {
  key: string
  userGuid: string
  /** 显示名：由调用方给（跟随看板的「昵称 / 全名」开关）。 */
  name: string
  startTime: string
  endTime: string
  color: ExportColor
  draft: boolean
  /** 午饭标识；关闭「午饭标识」时为 null。 */
  meal: MealMark | null
  /** 扣除用餐后的计薪时长（小时）；关闭「工时」时为 null。 */
  paidHours: number | null
}

export interface ExportDay {
  dateKey: string
  weekdayLabel: string
  /** MM-DD */
  dateLabel: string
  weekend: boolean
  /** 当天有班次的人数（同一人多班只算 1 人）。 */
  headcount: number
  cards: ExportCard[]
}

/** 图片里出现的文字（已按当前语言格式化好，模型 / 排版不碰 i18n）。 */
export interface ExportTexts {
  title: string
  subtitle: string
  onDuty: (count: number) => string
  total: (count: number) => string
  generatedAt: string
  draft: string
  mealLegendMeal: string
  mealLegendNone: string
}

export interface ExportModel {
  texts: ExportTexts
  days: ExportDay[]
  /** 本周出现的人数。 */
  peopleCount: number
  /** 本周草稿班次数（不论是否导出，弹窗用它决定要不要给「草稿班次」选项）。 */
  draftCount: number
  options: ExportOptions
}

export interface BuildExportInput {
  schedules: readonly ExportSourceSchedule[]
  /** 本周 7 天，YYYY-MM-DD，周一在前。 */
  weekDateKeys: readonly string[]
  weekdayLabels: readonly string[]
  nameOf: (userGuid: string) => string
  colorOf: (userGuid: string) => ExportColor
  options: ExportOptions
  texts: ExportTexts
}

const collator = new Intl.Collator(undefined, { sensitivity: 'base', numeric: true })

export function buildScheduleExportModel(input: BuildExportInput): ExportModel {
  const { options } = input
  const cardsByDate = new Map<string, ExportCard[]>(input.weekDateKeys.map((dateKey) => [dateKey, []]))
  let draftCount = 0

  input.schedules.forEach((schedule) => {
    const list = cardsByDate.get(schedule.workDate.slice(0, 10))
    if (!list) return // 不在本周
    const draft = schedule.status === 'Draft'
    if (draft) draftCount += 1
    // 已取消的永远不导出；草稿只有勾选才导出
    if (schedule.status !== 'Active' && !(draft && options.includeDrafts)) return

    const startTime = normalizeHm(schedule.startTime)
    const endTime = normalizeHm(schedule.endTime)
    const summary = options.showHours ? getMealSummary(startTime, endTime, toMealChoice(schedule.mealBreakCount)) : undefined
    list.push({
      key: schedule.scheduleGuid,
      userGuid: schedule.userGuid,
      name: input.nameOf(schedule.userGuid),
      startTime,
      endTime,
      color: input.colorOf(schedule.userGuid),
      draft,
      // 取消的班次已被上面排除，这里按「有效」判定即可
      meal: options.showMeal ? getMealMark({ startTime, endTime, mealBreakCount: schedule.mealBreakCount, status: 'Active' }) : null,
      paidHours: summary ? summary.paidHours : null,
    })
  })

  const everyone = new Set<string>()
  const days = input.weekDateKeys.map<ExportDay>((dateKey, index) => {
    const cards = (cardsByDate.get(dateKey) ?? []).sort((left, right) => (
      left.startTime.localeCompare(right.startTime)
      || collator.compare(left.name, right.name)
      || left.userGuid.localeCompare(right.userGuid)
    ))
    cards.forEach((card) => everyone.add(card.userGuid))
    return {
      dateKey,
      weekdayLabel: input.weekdayLabels[index] ?? '',
      dateLabel: dateKey.slice(5),
      weekend: index >= 5,
      headcount: new Set(cards.map((card) => card.userGuid)).size,
      cards,
    }
  })

  return { texts: input.texts, days, peopleCount: everyone.size, draftCount, options }
}

/** 文件名里不能出现的字符一律去掉（店名可能带 / 等）。 */
export function exportFileName(storeName: string, fromDateKey: string, toDateKey: string): string {
  const safeStore = storeName.replace(/[\\/:*?"<>|]/g, '').trim() || 'store'
  return `排班_${safeStore}_${fromDateKey}_${toDateKey.slice(5)}.png`
}
