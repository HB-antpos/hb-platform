import assert from 'node:assert/strict'

import type { SupplierPreorderItemHistory } from '../../../types/supplierPreorder'

import {
  aggregateBatches,
  dateAtOffset,
  dayOffset,
  estimateTextWidth,
  expandDaily,
  findPeak,
  formatDateSpan,
  formatDayRange,
  formatMonthDay,
  formatPercent,
  groupKeyDateMarks,
  historyCellState,
  keyDateMarks,
  layoutLabelRows,
  monthStartTicks,
  niceScale,
  rangesToIndexSpans,
  refDisplayCode,
  resolveSeriesLength,
  sellThroughTone,
  seriesX,
  sparklinePaths,
  toDayNumber,
  windowDayCount,
} from './orderingHistory.logic'

// ---------- 业务日期：只按日期算，不做时区换算 ----------
assert.equal(toDayNumber(null), null)
assert.equal(toDayNumber('bad'), null)
assert.equal(toDayNumber('1970-01-02'), 1)
assert.equal(dayOffset('2025-09-01T00:00:00', '2025-09-01'), 0)
assert.equal(dayOffset('2025-09-01T00:00:00', '2025-12-25T00:00:00'), 115)
assert.equal(dayOffset('2025-09-01', '2026-01-01'), 122, '跨年')
assert.equal(dayOffset(null, '2025-09-01'), null)
assert.equal(windowDayCount('2025-09-01T00:00:00', '2026-01-18T00:00:00'), 140)
assert.equal(windowDayCount('2025-09-02', '2025-09-01'), null, '结束早于开始')
assert.equal(windowDayCount(undefined, '2025-09-01'), null)
assert.equal(dateAtOffset('2025-09-01T00:00:00', 0), '2025-09-01')
assert.equal(dateAtOffset('2025-09-01', 121), '2025-12-31')
assert.equal(dateAtOffset('2025-09-01', 122), '2026-01-01')
assert.equal(dateAtOffset(null, 3), '')

// ---------- 日期格式 ----------
assert.equal(formatMonthDay('2025-09-21T00:00:00'), '09-21')
assert.equal(formatMonthDay(null), '—')
assert.equal(formatMonthDay('x'), '—')
assert.equal(formatDateSpan('2025-09-21', '2025-11-14'), '09-21 → 11-14')
assert.equal(formatDateSpan(null, null), '—')
assert.equal(formatDateSpan('2025-09-21', null), '09-21 → —')
assert.equal(formatDayRange('2025-12-25', '2025-12-25'), '12-25')
assert.equal(formatDayRange('2025-12-24', '2025-12-26'), '12-24~26')
assert.equal(formatDayRange('2025-11-30', '2025-12-01'), '11-30~12-01')

// ---------- 百分比与售罄率颜色档 ----------
assert.equal(formatPercent(0.544), '54%')
assert.equal(formatPercent(0.07), '7%')
assert.equal(formatPercent(1), '100%')
assert.equal(formatPercent(null), '—')
assert.equal(formatPercent(Number.NaN), '—')
assert.equal(sellThroughTone(0.93), 'good')
assert.equal(sellThroughTone(0.9), 'good', '90% 算绿色')
assert.equal(sellThroughTone(0.6), 'normal', '60% 不算偏低')
assert.equal(sellThroughTone(0.599), 'low')
assert.equal(sellThroughTone(null), 'none')

// ---------- 刻度：整数步长 ----------
assert.deepEqual(niceScale(0), { max: 1, ticks: [0, 1] })
assert.deepEqual(niceScale(15, 4), { max: 15, ticks: [0, 5, 10, 15] })
assert.deepEqual(niceScale(16, 4), { max: 20, ticks: [0, 5, 10, 15, 20] })
assert.deepEqual(niceScale(3, 4), { max: 3, ticks: [0, 1, 2, 3] }, '小数值不出现小数刻度')
assert.deepEqual(niceScale(240, 3), { max: 300, ticks: [0, 100, 200, 300] })
assert.equal(niceScale(-5).max, 1)

// ---------- 稀疏日销 → 稠密数组 ----------
assert.equal(resolveSeriesLength(140, [[3, 1, 0]]), 140)
assert.equal(resolveSeriesLength(null, [[3, 1, 0], [9, 2, 0]]), 10, '没有窗口时按最大下标')
assert.equal(resolveSeriesLength(null, []), 0)
{
  const { sales, discount } = expandDaily(
    [
      [0, 2, 0],
      [3, 5, 1],
      [3, 1, 1],
      [-1, 9, 9],
      [10, 9, 9],
    ],
    5,
  )
  assert.deepEqual(sales, [2, 0, 0, 6, 0], '同一天累加，越界丢弃')
  assert.deepEqual(discount, [0, 0, 0, 2, 0])
}
assert.deepEqual(expandDaily([], 0), { sales: [], discount: [] })

// ---------- 峰值 ----------
assert.deepEqual(findPeak([0, 3, 15, 15, 2]), { index: 2, value: 15 }, '取最早的最大值')
assert.equal(findPeak([0, 0]), null)
assert.equal(findPeak([]), null)

// ---------- 折扣区间 → 下标 ----------
assert.deepEqual(
  rangesToIndexSpans(
    [
      { from: '2025-12-27T00:00:00', to: '2026-01-20T00:00:00' },
      { from: '2025-08-20', to: '2025-09-02' },
      { from: '2025-07-01', to: '2025-07-05' },
      { from: '2025-10-05', to: '2025-10-01' },
    ],
    '2025-09-01',
    140,
  ),
  [
    { start: 0, end: 1 },
    { start: 117, end: 139 },
  ],
  '裁剪到窗口内，窗口外与倒置的丢弃，按开始排序',
)

// ---------- 进货批次按天汇总 ----------
{
  const result = aggregateBatches(
    [
      { date: '2025-09-02', source: 'warehouse', quantity: 240 },
      { date: '2025-09-02', source: 'supplier', quantity: 48 },
      { date: '2025-09-04', source: 'warehouse', quantity: 96 },
      { date: '2026-03-01', source: 'warehouse', quantity: 1 },
    ],
    '2025-09-01',
    4,
  )
  assert.deepEqual(result.warehouse, [0, 240, 0, 96])
  assert.deepEqual(result.supplier, [0, 48, 0, 0])
}

// ---------- 月份刻度 ----------
assert.deepEqual(monthStartTicks('2025-09-20', 40), [
  { index: 0, label: '2025-09' },
  { index: 11, label: '2025-10' },
])
assert.deepEqual(monthStartTicks(null, 10), [])

// ---------- 节日关键日期：窗口内下标 + 相邻合并 ----------
{
  const marks = keyDateMarks(
    [
      { date: '2025-12-25T00:00:00', key: 'Christmas' },
      { date: '2025-12-24T00:00:00', key: 'ChristmasEve' },
      { date: '2025-12-26T00:00:00', key: 'BoxingDay' },
      { date: '2025-10-31T00:00:00', key: 'Halloween' },
      { date: '2025-11-28T00:00:00', key: 'BlackFriday' },
      { date: '2025-11-28T00:00:00', key: 'BlackFriday' },
      { date: '2026-04-05T00:00:00', key: 'Easter' },
    ],
    '2025-09-01',
    140,
  )
  assert.deepEqual(
    marks.map((mark) => mark.key),
    ['Halloween', 'BlackFriday', 'ChristmasEve', 'Christmas', 'BoxingDay'],
    '按日期排序、去重、窗口外丢弃',
  )
  const groups = groupKeyDateMarks(marks)
  assert.equal(groups.length, 3)
  assert.deepEqual(groups[2], {
    start: 114,
    end: 116,
    keys: ['ChristmasEve', 'Christmas', 'BoxingDay'],
    firstDate: '2025-12-24',
    lastDate: '2025-12-26',
  })
  assert.equal(formatDayRange(groups[2].firstDate, groups[2].lastDate), '12-24~26')
  assert.equal(groupKeyDateMarks(marks, 0).length, 5, '间隔 0 时不合并')
}

// ---------- 标签分层：重叠就换行，限制在绘图区内 ----------
assert.equal(estimateTextWidth('ab', 10), 12)
assert.equal(estimateTextWidth('圣诞', 10), 20)
{
  const { slots, rowCount } = layoutLabelRows(
    [
      { center: 100, width: 60 },
      { center: 130, width: 60 },
      { center: 300, width: 40 },
      { center: 5, width: 40 },
    ],
    0,
    400,
    6,
  )
  assert.equal(rowCount, 2)
  assert.deepEqual(slots[3], { left: 0, row: 0 }, '贴左边界')
  assert.deepEqual(slots[0], { left: 70, row: 0 })
  assert.deepEqual(slots[1], { left: 100, row: 1 }, '与上一个重叠，放到第二层')
  assert.deepEqual(slots[2], { left: 280, row: 0 }, '后面放得下就回到第一层')
}
{
  const { slots } = layoutLabelRows([{ center: 395, width: 50 }], 0, 400)
  assert.equal(slots[0].left, 350, '贴右边界')
}

// ---------- 迷你曲线路径 ----------
assert.equal(seriesX(0, 1, 10, 100), 60, '只有一个点时居中')
assert.equal(seriesX(4, 5, 0, 100), 100)
assert.deepEqual(sparklinePaths([], 120, 40), { line: '', area: '' })
{
  const { line, area } = sparklinePaths([0, 10, 5], 100, 40, { top: 0 })
  assert.equal(line, 'M0.0,40.0 L50.0,0.0 L100.0,20.0')
  assert.equal(area, 'M0.0,40.0 L50.0,0.0 L100.0,20.0 L100.0,40 L0.0,40 Z')
}
assert.equal(sparklinePaths([0, 0], 100, 40).line, 'M0.0,40.0 L100.0,40.0', '全 0 时贴底')

// ---------- 格子状态 ----------
const own: SupplierPreorderItemHistory = {
  source: 'Own',
  productCode: 'P1',
  hasStoreData: true,
  purchaseBatches: 2,
  purchaseQty: 336,
  salesQty: 180,
  sellThrough: 0.54,
  discountStatus: 'complete',
  hasDiscount: true,
  discountShare: 0.15,
  discountRanges: [],
  daily: [],
}
assert.equal(historyCellState(own, 'Ready'), 'data')
assert.equal(historyCellState(own, 'Running'), 'data', '夜间刷新时旧数据照常显示')
assert.equal(historyCellState({ ...own, hasStoreData: false }, 'Ready'), 'noStore')
assert.equal(historyCellState(null, 'Queued'), 'pending')
assert.equal(historyCellState(null, 'Running'), 'pending')
assert.equal(historyCellState(null, 'None'), 'pending')
assert.equal(historyCellState(null, 'Failed'), 'failed')
assert.equal(historyCellState(null, 'Ready'), 'none')
assert.equal(historyCellState(undefined, 'NoWindow'), 'none')

// ---------- 参考商品编号 ----------
assert.equal(refDisplayCode({ source: 'Auto', itemNumber: ' XD0001342 ', productCode: 'P9' }), 'XD0001342')
assert.equal(refDisplayCode({ source: 'Auto', itemNumber: '', productCode: 'P9' }), 'P9')
assert.equal(refDisplayCode(null, 'P1'), 'P1')
assert.equal(refDisplayCode(null), '')

