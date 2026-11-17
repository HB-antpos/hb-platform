import assert from 'node:assert/strict'

import { MEAL_ICON_PATH, MEAL_SLASH_PATH } from './mealMarkLogic'
import { DEFAULT_EXPORT_OPTIONS, buildScheduleExportModel } from './scheduleExportLogic'
import type { ExportOptions, ExportSourceSchedule, ExportTexts } from './scheduleExportLogic'
import { EXPORT_LAYOUT, escapeXml, fitText, layoutScheduleExport, pickScale, renderScheduleExportSvg } from './scheduleExportImage'
import type { TextMeasure } from './scheduleExportImage'

// 假的文字测量：非 ASCII（中文）一个字一个字号宽，其余 0.58 个字号宽——够单测用，不依赖浏览器
const measure: TextMeasure = (text, fontSize) => Array.from(text).reduce((sum, char) => sum + (char.charCodeAt(0) > 255 ? fontSize : fontSize * 0.58), 0)

const WEEK = ['2026-11-16', '2026-11-17', '2026-11-18', '2026-11-19', '2026-11-20', '2026-11-21', '2026-11-22']
const LABELS = ['周一', '周二', '周三', '周四', '周五', '周六', '周日']
const COLOR = { bg: '#e8f0ff', bar: '#3b82f6', ink: '#1d4ed8' }
const texts: ExportTexts = {
  title: 'Charlestown 排班表',
  subtitle: '2026 第 47 周 · 11/16 – 11/22 · 已发布排班',
  onDuty: (count) => `在岗 ${count} 人`,
  total: (count) => `共 ${count} 人`,
  generatedAt: '生成于 2026-11-17 17:42',
  draft: '草稿',
  mealLegendMeal: '含午饭',
  mealLegendNone: '无午饭',
}

let seq = 0
const sched = (userGuid: string, date: string, start: string, end: string, extra: Partial<ExportSourceSchedule> = {}): ExportSourceSchedule => ({
  scheduleGuid: `s${(seq += 1)}`, userGuid, workDate: date, startTime: start, endTime: end, status: 'Active', ...extra,
})
const render = (schedules: ExportSourceSchedule[], options: Partial<ExportOptions> = {}, nameOf: (guid: string) => string = (guid) => guid) => {
  const model = buildScheduleExportModel({ schedules, weekDateKeys: WEEK, weekdayLabels: LABELS, nameOf, colorOf: () => COLOR, options: { ...DEFAULT_EXPORT_OPTIONS, ...options }, texts })
  return { model, ...renderScheduleExportSvg(model, measure) }
}

// ---------- 工具函数 ----------
assert.equal(escapeXml(`<a href="x">&'</a>`), '&lt;a href=&quot;x&quot;&gt;&amp;&apos;&lt;/a&gt;', '尖括号、引号、& 都要转义')
assert.equal(fitText('Cody', 100, 12, 600, measure), 'Cody', '放得下原样返回')
assert.equal(fitText('Imogen Hitchcock Wellington', 60, 12, 600, measure).endsWith('…'), true, '放不下截断并补省略号')
assert.equal(measure(fitText('Imogen Hitchcock Wellington', 60, 12, 600, measure), 12, 600) <= 60, true, '截断后不超宽')
assert.equal(fitText('Cody', 0, 12, 600, measure), '', '没有宽度就不画')
assert.equal(pickScale(1098, 700), 2, '常规尺寸 2 倍')
assert.ok(pickScale(6000, 3000) < 2 && pickScale(6000, 3000) >= 1, '超大画布降低倍数')
assert.equal(pickScale(20000, 3000), 1, '再大也不低于 1 倍')

// ---------- 排版尺寸 ----------
const L = EXPORT_LAYOUT
const week = [
  sched('a', '2026-11-16', '09:00', '17:30'), sched('b', '2026-11-16', '09:00', '17:30'), sched('c', '2026-11-16', '15:00', '17:30'),
  sched('a', '2026-11-17', '09:00', '17:30'),
]
const sample = render(week)
assert.equal(sample.width, L.padding * 2 + L.colWidth * 7, '宽度 = 两侧边距 + 7 列')
const layout = layoutScheduleExport(sample.model)
assert.equal(layout.bodyHeight, L.colPadTop + 3 * L.cardHeight + 2 * L.cardGap + L.colPadBottom, '列高随当天最多的卡片数增高（周一 3 张）')
assert.equal(sample.height, L.padding + L.titleHeight + L.headHeight + layout.bodyHeight + L.footerHeight + L.padding, '总高 = 标题 + 表头 + 列高 + 页脚 + 边距')
assert.equal(layoutScheduleExport(render([]).model).bodyHeight, L.minBodyHeight, '空周也有最小列高')

// ---------- SVG 内容与安全 ----------
assert.ok(sample.svg.startsWith('<svg'), '输出完整的 SVG 文档')
assert.ok(sample.svg.includes(`width="${sample.width}" height="${sample.height}"`), '自带 width / height，作为图片加载有固有尺寸')
assert.ok(sample.svg.includes('Charlestown 排班表') && sample.svg.includes('周一 11-16') && sample.svg.includes('在岗 3 人') && sample.svg.includes('在岗 1 人'), '标题、表头、每天在岗人数（周一 3 人、周二 1 人）')
for (const forbidden of ['foreignObject', '<image', 'href=', 'xlink', '<script']) {
  assert.equal(sample.svg.includes(forbidden), false, `SVG 里不能有 ${forbidden}（会污染画布 / 引外部资源）`)
}
assert.equal(render(week).svg, sample.svg, '同样的模型出同样的 SVG（确定性）')
const evil = render([sched('x', '2026-11-16', '09:00', '17:30')], {}, () => '<script>alert("x")</script>&')
assert.equal(evil.svg.includes('<script>'), false, '用户数据里的标签不能原样进 SVG')
assert.ok(evil.svg.includes('&lt;script&gt;') || evil.svg.includes('&amp;'), '被转义后保留')
assert.ok(render(week, {}, () => 'Imogen Hitchcock Wellington').svg.includes('…'), '名字太长会截断')

// ---------- 周末浅底、0 人标红 ----------
assert.equal((sample.svg.match(/fill="#f9fafb"/g) ?? []).length, 2, '周六周日两列浅底')
assert.ok(sample.svg.includes('fill="#b42318"'), '没人上班的那天「在岗 0 人」用警示色')

// ---------- 午饭标识与图例 ----------
const meal = render([sched('a', '2026-11-16', '09:00', '17:30'), sched('b', '2026-11-16', '09:00', '19:00', { mealBreakCount: 0 })])
assert.ok(meal.svg.includes(MEAL_ICON_PATH), '含午饭画叉勺')
assert.ok(meal.svg.includes(MEAL_SLASH_PATH), '长班次无午饭叠斜杠')
assert.ok(meal.svg.includes('含午饭') && meal.svg.includes('无午饭'), '页脚图例：图里出现的两种都列出')
const onlyMeal = render([sched('a', '2026-11-16', '09:00', '17:30')])
assert.equal(onlyMeal.svg.includes(MEAL_SLASH_PATH), false, '没有「无午饭」时不画斜杠')
assert.equal(onlyMeal.svg.includes('无午饭'), false, '没出现过的状态不进图例')
const noMeal = render([sched('a', '2026-11-16', '09:00', '17:30')], { showMeal: false })
assert.equal(noMeal.svg.includes(MEAL_ICON_PATH), false, '关闭午饭标识：不画图标')
assert.equal(noMeal.svg.includes('含午饭'), false, '关闭午饭标识：也没有图例')
assert.equal(render([sched('a', '2026-11-16', '15:00', '17:30')]).svg.includes(MEAL_ICON_PATH), false, '短班次不显示午饭标识')
assert.ok(render([sched('a', '2026-11-16', '09:00', '19:30')]).svg.includes('×2'), '超过 9 小时含 2 次，标 ×2')

// ---------- 工时、草稿 ----------
assert.equal(render([sched('a', '2026-11-16', '09:00', '17:30')]).svg.includes('>8h<'), false, '默认不带工时')
assert.ok(render([sched('a', '2026-11-16', '09:00', '17:30')], { showHours: true }).svg.includes('>8h<'), '勾选工时：扣餐后的计薪 8h')
const drafty = render([sched('a', '2026-11-16', '09:00', '17:30', { status: 'Draft' })], { includeDrafts: true })
assert.ok(drafty.svg.includes('stroke-dasharray="4 3"') && drafty.svg.includes('草稿'), '草稿卡片虚线边框并标「草稿」')
assert.equal(render([sched('a', '2026-11-16', '09:00', '17:30', { status: 'Draft' })]).svg.includes('草稿'), false, '默认不导出草稿')

// ---------- 年龄 / 管理者信息：源数据带了也画不出来 ----------
const dirty = week.map((item) => ({ ...item, age: 'AGE-MARK-731', employeeAge: 'AGE-MARK-731', remark: 'REMARK-MARK', isPublishedWeekLocked: true })) as ExportSourceSchedule[]
const dirtySvg = render(dirty, { showHours: true, includeDrafts: true }).svg
for (const forbidden of ['AGE-MARK', 'REMARK-MARK', 'lock']) {
  assert.equal(dirtySvg.includes(forbidden), false, `导出图里不应出现 ${forbidden}`)
}

console.log('scheduleExportImage ok')
