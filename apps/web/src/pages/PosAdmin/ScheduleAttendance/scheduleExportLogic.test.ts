import assert from 'node:assert/strict'

import { DEFAULT_EXPORT_OPTIONS, buildScheduleExportModel, exportFileName } from './scheduleExportLogic'
import type { BuildExportInput, ExportOptions, ExportSourceSchedule, ExportTexts } from './scheduleExportLogic'

const WEEK = ['2026-11-16', '2026-11-17', '2026-11-18', '2026-11-19', '2026-11-20', '2026-11-21', '2026-11-22']
const LABELS = ['周一', '周二', '周三', '周四', '周五', '周六', '周日']
const NAMES: Record<string, string> = { a: 'Cody', b: 'Hunter', c: 'Rylie', d: 'Zhan Chen' }
const COLOR = { bg: '#e8f0ff', bar: '#3b82f6', ink: '#1d4ed8' }
const texts: ExportTexts = {
  title: 'Charlestown 排班表',
  subtitle: '2026 第 47 周 · 已发布排班',
  onDuty: (count) => `在岗 ${count} 人`,
  total: (count) => `共 ${count} 人`,
  generatedAt: '生成于 2026-11-17 17:42',
  draft: '草稿',
  mealLegendMeal: '含午饭',
  mealLegendNone: '无午饭',
}

let seq = 0
const sched = (userGuid: string, date: string, start: string, end: string, extra: Partial<ExportSourceSchedule> = {}): ExportSourceSchedule => ({
  scheduleGuid: `s${(seq += 1)}`,
  userGuid,
  workDate: date,
  startTime: start,
  endTime: end,
  status: 'Active',
  ...extra,
})

const build = (schedules: ExportSourceSchedule[], options: Partial<ExportOptions> = {}, nameOf = (guid: string) => NAMES[guid] ?? guid) => buildScheduleExportModel({
  schedules,
  weekDateKeys: WEEK,
  weekdayLabels: LABELS,
  nameOf,
  colorOf: () => COLOR,
  options: { ...DEFAULT_EXPORT_OPTIONS, ...options },
  texts,
} satisfies BuildExportInput)

assert.deepEqual(DEFAULT_EXPORT_OPTIONS, { showMeal: true, showHours: false, includeDrafts: false }, '默认：午饭标识开、工时关、不含草稿')

const data = [
  sched('b', '2026-11-16', '09:00', '17:30'),
  sched('a', '2026-11-16', '09:00', '17:30'),
  sched('c', '2026-11-16', '15:00', '17:30'),
  sched('a', '2026-11-16', '18:00', '21:00'), // 同一人当天第二班：人数只算 1 个
  sched('d', '2026-11-17', '09:00', '19:00', { mealBreakCount: 0 }), // 长班次手动不扣
  sched('a', '2026-11-18', '09:00', '17:30', { status: 'Cancelled' }), // 已取消：永远不导出
  sched('b', '2026-11-18', '10:00', '16:00', { status: 'Draft' }), // 草稿：默认不导出
  sched('a', '2026-11-30', '09:00', '17:30'), // 不在本周
  sched('c', '2026-11-21T00:00:00', '09:00', '17:00'), // 带时间的日期也能识别
]

// ---------- 范围：只导已发布；草稿按选项；取消永不导出；本周之外忽略 ----------
const model = build(data)
assert.deepEqual(model.days.map((day) => day.cards.length), [4, 1, 0, 0, 0, 1, 0], '周一 4 张、周二 1 张、周三只有取消 + 草稿 → 0、周六 1 张')
assert.equal(model.draftCount, 1, '草稿数照常统计（弹窗据此决定是否出现「草稿班次」选项）')
assert.deepEqual(model.days.map((day) => day.headcount), [3, 1, 0, 0, 0, 1, 0], '周一 3 人（a 两个班只算 1 人）')
assert.equal(model.peopleCount, 4, '本周出现 a / b / c / d 共 4 人')
assert.equal(model.days[0].weekdayLabel, '周一')
assert.equal(model.days[0].dateLabel, '11-16')
assert.deepEqual(model.days.map((day) => day.weekend), [false, false, false, false, false, true, true], '周六周日是周末')

const withDrafts = build(data, { includeDrafts: true })
assert.equal(withDrafts.days[2].cards.length, 1, '勾选草稿后周三出现草稿班次')
assert.equal(withDrafts.days[2].cards[0].draft, true, '草稿卡片带 draft 标志')
assert.equal(withDrafts.days[0].cards.every((card) => !card.draft), true, '已发布的不是草稿')
assert.equal(withDrafts.days[2].cards.some((card) => card.key === data[5].scheduleGuid), false, '已取消的班次即使勾选草稿也不导出')

// ---------- 排序：开始时间 → 显示名 ----------
assert.deepEqual(model.days[0].cards.map((card) => `${card.startTime} ${card.name}`), ['09:00 Cody', '09:00 Hunter', '15:00 Rylie', '18:00 Cody'], '同一天按开始时间、再按名字排')

// ---------- 午饭标识 ----------
assert.equal(model.days[0].cards[0].meal?.kind, 'meal', '09:00–17:30 自动含午饭')
assert.equal(model.days[0].cards[2].meal?.kind, 'hidden', '15:00–17:30 只有 2.5 小时，不显示')
assert.equal(model.days[1].cards[0].meal?.kind, 'none', '长班次手动不扣：无午饭')
assert.equal(model.days[1].cards[0].meal?.manual, true)
const noMeal = build(data, { showMeal: false })
assert.equal(noMeal.days[0].cards.every((card) => card.meal === null), true, '关闭午饭标识后不带标识')

// ---------- 工时：默认不带；勾选后是扣餐后的计薪时长 ----------
assert.equal(model.days[0].cards[0].paidHours, null, '默认不带工时')
const withHours = build(data, { showHours: true })
assert.equal(withHours.days[0].cards[0].paidHours, 8, '8.5 小时扣 30 分钟 = 8')
assert.equal(withHours.days[1].cards[0].paidHours, 10, '手动不扣的 10 小时班次：计薪 10')

// ---------- 名字：跟随调用方（昵称 / 全名开关） ----------
assert.equal(build(data, {}, () => 'X').days[0].cards[0].name, 'X')
assert.equal(model.days[1].cards[0].name, 'Zhan Chen')

// ---------- 年龄 / 管理者信息：不管源数据带了什么，模型和输出里都不会有 ----------
const dirty = data.map((item) => ({ ...item, age: 19, employeeAge: 19, remark: '私下备注', isPublishedWeekLocked: true, conflict: 'unavailable', punchGuid: 'p1' })) as ExportSourceSchedule[]
const serialized = JSON.stringify(build(dirty, { showHours: true, includeDrafts: true }))
for (const forbidden of ['"age"', 'employeeAge', '私下备注', 'isPublishedWeekLocked', 'conflict', 'punchGuid', 'remark']) {
  assert.equal(serialized.includes(forbidden), false, `导出模型里不应出现 ${forbidden}`)
}

// ---------- 文件名 ----------
assert.equal(exportFileName('Charlestown', '2026-11-16', '2026-11-22'), '排班_Charlestown_2026-11-16_11-22.png')
assert.equal(exportFileName('A/B:C*?', '2026-11-16', '2026-11-22'), '排班_ABC_2026-11-16_11-22.png', '店名里的非法字符要去掉')
assert.equal(exportFileName('///', '2026-11-16', '2026-11-22'), '排班_store_2026-11-16_11-22.png', '全是非法字符时用兜底名')

console.log('scheduleExportLogic ok')
