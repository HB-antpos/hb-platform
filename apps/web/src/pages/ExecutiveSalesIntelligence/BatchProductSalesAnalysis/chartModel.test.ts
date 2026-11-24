import type { BatchSalesDaily } from '../../../types/batchProductSalesAnalysis'

function assert(condition: unknown, message: string): asserts condition { if (!condition) throw new Error(message) }
function metrics(overrides: Partial<BatchSalesDaily['metrics']>): BatchSalesDaily['metrics'] {
  return { quantity: 0, regularQuantity: 0, discountQuantity: 0, unknownQuantity: 0, returnQuantity: 0, salesAmount: 0, discountStatus: 'complete', originalPriceMin: null, originalPriceMax: null, discountPriceMin: null, discountPriceMax: null, ...overrides }
}

const { buildDiscountDailyChartModel } = await import('./chartModel')
const chart = buildDiscountDailyChartModel([
  { date: '2026-09-01', metrics: metrics({ quantity: 8, salesAmount: 51.5, regularQuantity: 5, discountQuantity: 3 }) },
  { date: '2026-09-02', metrics: metrics({ quantity: -4, regularQuantity: -1, discountQuantity: -3 }) },
  { date: '2026-09-03', metrics: metrics({ quantity: 0, unknownQuantity: 0 }) },
  { date: '2026-09-04', metrics: { ...metrics({ quantity: 2, discountQuantity: 2 }), regularQuantity: null } as unknown as BatchSalesDaily['metrics'] },
])

assert(chart.points.length === 4, '每个业务日都应有一个数据点')
assert(chart.points[0].salesAmount === 51.5, '图表数据点必须保留每日销售额供悬浮提示显示')
assert(chart.points[0].segments[0].height > 0 && chart.points[0].segments[1].height > 0, '正价和折扣的正销量应分别堆叠')
assert(chart.points[0].segments[1].y < chart.points[0].segments[0].y, '正值后续分段应位于前一个正值之上')
assert(chart.points[1].segments[0].y === chart.zeroY, '负销量第一段应从零线开始')
assert(chart.points[1].segments[1].y > chart.zeroY, '负销量后续分段应在零线下继续堆叠')
assert(chart.minValue < 0 && chart.maxValue > 0, '正负销量同时存在时坐标轴必须覆盖零线两侧')
assert(chart.points[2].segments.every((segment) => segment.height === 0), '零量与缺失分类不应伪造可见柱')
assert(chart.points[3].regularQuantity === 0 && chart.points[3].segments[1].height > 0, '服务意外返回空分类时应按 0 处理且保留其余有效分类')
assert(buildDiscountDailyChartModel([]).points.length === 0, '空数据不应生成虚构日期或柱')

const acrossWeeks = buildDiscountDailyChartModel([
  { date: '2026-09-06', metrics: metrics({ quantity: 1, regularQuantity: 1 }) },
  { date: '2026-09-07', metrics: metrics({ quantity: 1, regularQuantity: 1 }) },
  { date: '2026-09-08', metrics: metrics({ quantity: 1, regularQuantity: 1 }) },
  { date: '2026-09-13', metrics: metrics({ quantity: 1, regularQuantity: 1 }) },
  { date: '2026-09-14', metrics: metrics({ quantity: 1, regularQuantity: 1 }) },
])
assert(acrossWeeks.weekDividers.length === 2, '跨自然周数据应在每个周边界生成分割线')
assert(acrossWeeks.weekDividers.map((divider) => divider.date).join(',') === '2026-09-07,2026-09-14', '完整日期数据应在周一的槽边界分割')
assert(acrossWeeks.weekDividers[0].x === acrossWeeks.plotLeft + (acrossWeeks.plotRight - acrossWeeks.plotLeft) / acrossWeeks.points.length, '周分割线应位于当前柱槽的左边界')

const skippedMonday = buildDiscountDailyChartModel([
  { date: '2026-09-06', metrics: metrics({ quantity: 1, regularQuantity: 1 }) },
  { date: '2026-09-08', metrics: metrics({ quantity: 1, regularQuantity: 1 }) },
])
assert(skippedMonday.weekDividers.length === 1 && skippedMonday.weekDividers[0].date === '2026-09-08', '缺少周一记录时，跨周的下一条记录仍应生成分割线')

const missingSameWeekDay = buildDiscountDailyChartModel([
  { date: '2026-09-08', metrics: metrics({ quantity: 1, regularQuantity: 1 }) },
  { date: '2026-09-10', metrics: metrics({ quantity: 1, regularQuantity: 1 }) },
])
assert(missingSameWeekDay.weekDividers.length === 0, '同一自然周内缺日不得生成分割线')

const mondayFirst = buildDiscountDailyChartModel([
  { date: '2026-09-07', metrics: metrics({ quantity: 1, regularQuantity: 1 }) },
  { date: '2026-09-08', metrics: metrics({ quantity: 1, regularQuantity: 1 }) },
])
assert(mondayFirst.weekDividers.length === 0, '首日为周一时不得重复绘制绘图区左边界')

const annual = buildDiscountDailyChartModel(Array.from({ length: 366 }, (_, index) => ({
  date: new Date(Date.UTC(2024, 0, index + 1)).toISOString().slice(0, 10),
  metrics: metrics({ quantity: 1, regularQuantity: 1 }),
})))
assert(annual.points.every((point, index) => index === 0
  || annual.points[index - 1].segments[0].x + annual.points[index - 1].segments[0].width <= point.segments[0].x),
'全年每日柱应保持独立，不能因最小柱宽互相覆盖')
assert(annual.weekDividers.length === 52, '全年数据应为每个非首日周一生成分割线')
assert(annual.weekDividers.every((divider, index) => divider.x > annual.plotLeft && divider.x < annual.plotRight
  && (index === 0 || annual.weekDividers[index - 1].x < divider.x)), '全年周分割线必须位于绘图区内且严格递增')

const pending = buildDiscountDailyChartModel([
  { date: '2026-09-01', metrics: metrics({ quantity: 8, unknownQuantity: 8, discountStatus: 'pending' }) },
  { date: '2026-09-02', metrics: metrics({ quantity: -2, unknownQuantity: -2, discountStatus: 'pending' }) },
])
assert(pending.points.every((point) => point.pending && point.segments.length === 1 && point.segments[0].kind === 'total'), '待统计只能画总销量，不能伪装正价或未知分类')
assert(pending.points[1].segments[0].y === pending.zeroY && pending.points[1].segments[0].height > 0, '待统计负净销量仍在零线下显示')

const unavailable = buildDiscountDailyChartModel([
  { date: '2026-09-01', metrics: metrics({ quantity: 8, salesAmount: 40, unknownQuantity: 8, discountStatus: 'unknown' }) },
], 720, 248, true)
assert(unavailable.points[0].pending && !unavailable.points[0].discountPending && unavailable.points[0].segments.length === 1,
  '折扣拆分终态不可用时只画可靠的总销量，且不得伪装成仍在等待')

const partial = buildDiscountDailyChartModel([
  { date: '2026-09-01', metrics: metrics({ quantity: 8, regularQuantity: 4, discountQuantity: 3, unknownQuantity: 1, discountStatus: 'partial' }) },
])
assert(!partial.points[0].pending && partial.points[0].segments.length === 3,
  '部分分类已有证据时必须保留正价、折扣和未知分段')

const freshUnknown = buildDiscountDailyChartModel([
  { date: '2026-09-01', metrics: metrics({ quantity: 8, unknownQuantity: 8, discountStatus: 'unknown' }) },
])
assert(!freshUnknown.points[0].pending && freshUnknown.points[0].segments[2].height > 0,
  'Fresh 快照中真实存在的未知价格成交必须保留灰色未知分类柱')

const partialCoverage = buildDiscountDailyChartModel([
  { date: '2026-09-01', metrics: metrics({ quantity: 2, regularQuantity: 2 }) },
  { date: '2026-09-02', metrics: null },
  { date: '2026-09-03', metrics: metrics({ quantity: 0 }) },
])
assert(partialCoverage.points[1].unavailable && partialCoverage.points[1].segments.length === 0, '未完成日期必须保留日期轴断点，不能补零或画柱')
assert(partialCoverage.points[1].hitWidth > 0, '未完成日期即使没有柱形也必须保留可悬停和键盘聚焦的日期槽')
assert(!partialCoverage.points[2].unavailable && partialCoverage.points[2].segments.every((segment) => segment.height === 0), '已完成日期才可以作为真实零值')

// ---------- 柱顶数值标注 ----------
const { buildValueLabels, placeChartTooltip } = await import('./chartModel')
type Box = { x1: number; x2: number; y1: number; y2: number }
const boxesHit = (a: Box, b: Box) => a.x1 < b.x2 && b.x1 < a.x2 && a.y1 < b.y2 && b.y1 < a.y2
function dailySeries(values: (number | null)[]) {
  return values.map((quantity, index) => ({
    date: new Date(Date.UTC(2026, 3, 1 + index)).toISOString().slice(0, 10),
    metrics: quantity === null ? null : metrics({ quantity, regularQuantity: quantity }),
  }))
}
function barBox(point: ReturnType<typeof buildDiscountDailyChartModel>['points'][number]): Box | null {
  const visible = point.segments.filter((segment) => segment.height > 0)
  if (!visible.length) return null
  return { x1: visible[0].x, x2: visible[0].x + visible[0].width, y1: Math.min(...visible.map((s) => s.y)), y2: Math.max(...visible.map((s) => s.y + s.height)) }
}
// 与线上截图同形态：41 天，前段零星小值，末段 230 / 170 / 362 高峰
const greenhills = [6, 3, 0, 2, 0, 0, 0, 6, 10, 22, 13, 4, 2, 3, 2, 6, 7, 20, 25, 16, 8, 5, 8, 21, 26, 0, 35, 31, 18, 22, 40, 58, 52, 60, 85, 79, 230, 170, 362, 38, 0]
for (const width of [260, 500, 776, 1100]) {
  const model = buildDiscountDailyChartModel(dailySeries(greenhills), width, 186)
  const labels = buildValueLabels(model)
  assert(labels.some((label) => label.peak && model.points[label.index].quantity === 362), `宽 ${width}px 时峰值必须标注并加粗`)
  assert(labels.every((label, index) => labels.every((other, otherIndex) => otherIndex === index || !boxesHit(label.box, other.box))), `宽 ${width}px 时数值标签不得互相重叠`)
  assert(labels.every((label) => model.points.every((point, pointIndex) => {
    const bar = barBox(point)
    return pointIndex === label.index || !bar || !boxesHit(label.box, bar)
  })), `宽 ${width}px 时数值标签不得压住其他日期的柱子`)
  assert(labels.every((label) => label.box.x1 >= model.plotLeft - 1 && label.box.x2 <= model.plotRight + 1), `宽 ${width}px 时数值标签不得超出绘图区左右边界`)
  assert(labels.every((label) => model.points[label.index].quantity !== 0), '净销量为 0 的日期不标数值')
}
const denseGreenhills = buildDiscountDailyChartModel(dailySeries(greenhills), 776, 186)
const denseGreenhillsLabels = buildValueLabels(denseGreenhills)
assert(denseGreenhills.slotWidth < 24 && denseGreenhillsLabels.every((label) => Math.abs(denseGreenhills.points[label.index].quantity) >= 362 * 0.05),
  '柱子较密时不标低于峰值 5% 的小值，避免底部一排数字')
assert(denseGreenhillsLabels.find((label) => denseGreenhills.points[label.index].quantity === 230)?.text === '230'
  && denseGreenhillsLabels.some((label) => denseGreenhills.points[label.index].quantity === 170), '相邻的 230 / 170 / 362 高峰都应标出')

const sparse = buildDiscountDailyChartModel(dailySeries(greenhills.slice(27)), 500, 186)
assert(sparse.slotWidth >= 24 && buildValueLabels(sparse).length === sparse.points.filter((point) => point.quantity !== 0).length,
  '柱槽宽松（约 30 天以内）时所有非零日期都应标注')

const thousands = [30, 35, 10, 15, 8, 5, 20, 25, 60, 110, 140, 120, 95, 100, 120, 150, 170, 140, 130, 160, 165, 175, 230, 260, 210, 30, 270, 300, 310, 380, 450, 520, 560, 600, 760, 1000, 1850, 2150, 3300, 700, 30]
const denseThousands = buildDiscountDailyChartModel(dailySeries(thousands), 776, 186)
assert(buildValueLabels(denseThousands).find((label) => label.index === 38)?.text === '3.3k', '两侧邻柱间放不下完整四位数时改用 k 缩写')
const wideThousands = buildDiscountDailyChartModel(dailySeries([600, 3300, 700]), 720, 186)
assert(buildValueLabels(wideThousands).find((label) => label.index === 1)?.text === '3,300', '放得下时保留千分位完整数字')

const withReturns = buildDiscountDailyChartModel(dailySeries([8, -4, 6]), 720, 186)
const returnLabel = buildValueLabels(withReturns).find((label) => label.index === 1)
assert(returnLabel?.text === '-4' && returnLabel.y > withReturns.zeroY, '退货（负净销量）标在零线下方的柱底')
assert(!buildValueLabels(buildDiscountDailyChartModel(dailySeries([5, null, 7]), 720, 186)).some((label) => label.index === 1), '未统计日期不得标数值')
assert(buildValueLabels(denseGreenhills, { avoid: { x1: 0, x2: 9999, y1: 0, y2: 9999 } }).length === 0, '与避让区（悬浮提示框）相交的标签必须隐藏')
assert(buildValueLabels(buildDiscountDailyChartModel(dailySeries([0, 0]), 720, 186)).length === 0, '全为 0 时不标任何数值')

// ---------- 悬浮提示框定位 ----------
for (const [index, point] of denseGreenhills.points.entries()) {
  const box = placeChartTooltip(denseGreenhills, index, { width: 176, height: 166 })
  assert(box.x1 >= 0 && box.x2 <= denseGreenhills.width, `第 ${index} 天提示框不得超出图表`)
  assert(!(box.x1 < point.hitX + point.hitWidth && point.hitX < box.x2), `第 ${index} 天提示框不得盖住当前日期槽（光标所在列），否则会抢走鼠标造成闪烁`)
}
assert(placeChartTooltip(denseGreenhills, 0, { width: 176, height: 150 }).y2 <= denseGreenhills.plotBottom + 5, '完整提示框须在 x 轴日期胶囊上方结束，不遮挡悬停日期')
assert(placeChartTooltip(denseGreenhills, 0, { width: 176, height: 166 }).side === 'right', '左侧日期的提示框放在右边')
assert(placeChartTooltip(denseGreenhills, 38, { width: 176, height: 166 }).side === 'left', '右缘高峰日期的提示框翻到左边，不再夹回压住当前列')
const narrow = buildDiscountDailyChartModel(dailySeries(greenhills), 260, 186)
assert(narrow.points.every((_, index) => {
  const box = placeChartTooltip(narrow, index, { width: 176, height: 166 })
  return box.x1 >= 0 && box.x2 <= narrow.width
}), '极窄图两侧都放不下时贴边显示，但不得超出图表')
