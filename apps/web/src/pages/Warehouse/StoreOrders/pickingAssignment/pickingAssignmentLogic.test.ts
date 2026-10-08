import {
  PICKER_SEGMENT_COLORS,
  adjustSegmentCount,
  assignSegmentPicker,
  assigneeStatus,
  formatUtcShort,
  buildSlipRows,
  otherSegmentLabels,
  evenSegmentCounts,
  paginateSlipRowsByHeight,
  resolveSlipsSearch,
  slipFirstPageBodyMm,
  SLIP_FIRST_PAGE_BODY_MM,
  SLIP_NEXT_PAGE_BODY_MM,
  parseUtcMs,
  resizeSegmentPickers,
  segmentColor,
  segmentCountsValid,
  shouldShowPickingAssignmentSection,
  slipTurnMarkers,
} from './pickingAssignmentLogic'

function assertDeepEqual(actual: unknown, expected: unknown, label: string) {
  const actualText = JSON.stringify(actual)
  const expectedText = JSON.stringify(expected)
  if (actualText !== expectedText) {
    throw new Error(`${label}。Expected: ${expectedText}, received: ${actualText}`)
  }
}

function assertEqual<T>(actual: T, expected: T, label: string) {
  if (actual !== expected) {
    throw new Error(`${label}。Expected: ${String(expected)}, received: ${String(actual)}`)
  }
}

// 平均分：余数从前往后，每人多一个；与后端 SplitEvenly 同口径。
assertDeepEqual(evenSegmentCounts(32, 3), [11, 11, 10], '32 个品种分 3 人')
assertDeepEqual(evenSegmentCounts(2, 3), [1, 1, 0], '品种比人少时后面的人为 0')
assertDeepEqual(evenSegmentCounts(5, 0), [], '没有员工时为空')

// 加减只移动相邻边界，合计不变。
assertDeepEqual(adjustSegmentCount([11, 11, 10], 0, 1), [12, 10, 10], '第 1 段加一个从第 2 段挪')
assertDeepEqual(adjustSegmentCount([11, 11, 10], 2, 1), [11, 10, 11], '最后一段加一个从前一段挪')
assertDeepEqual(adjustSegmentCount([11, 11, 10], 1, -1), [11, 10, 11], '中间段减一个还给后一段')
assertDeepEqual(adjustSegmentCount([11, 11, 10], 2, -1), [11, 12, 9], '最后一段减一个还给前一段')
const exhausted = [5, 0] as const
assertEqual(adjustSegmentCount(exhausted, 0, 1), exhausted, '相邻段已为 0 时加不动，返回原数组')
const empty = [0, 5] as const
assertEqual(adjustSegmentCount(empty, 0, -1), empty, '本段为 0 时减不动')
const single = [7] as const
assertEqual(adjustSegmentCount(single, 0, 1), single, '只有一段时不能调整')

assertEqual(segmentCountsValid([11, 11, 10], 3, 32), true, '合计等于品种数')
assertEqual(segmentCountsValid([11, 11, 9], 3, 32), false, '合计不等于品种数')
assertEqual(segmentCountsValid([11, 21], 3, 32), false, '段数与员工数不一致')
assertEqual(segmentCountsValid([33, -1], 2, 32), false, '不能为负')

// 份数与各段员工：员工可留空（扫码领取），同一人不能负责两段。
assertDeepEqual(resizeSegmentPickers(['u-chen', null], 3), ['u-chen', null, null], '加份数时新段待领取')
assertDeepEqual(resizeSegmentPickers(['u-chen', 'u-li', null], 1), ['u-chen'], '减份数时截掉末尾')
assertDeepEqual(resizeSegmentPickers([], 0), [null], '至少 1 份')
assertEqual(resizeSegmentPickers([], 20).length, 10, '最多 10 份')
assertDeepEqual(assignSegmentPicker(['u-chen', null, null], 2, 'u-chen'), [null, null, 'u-chen'], '指给新段时从原段撤下')
assertDeepEqual(assignSegmentPicker(['u-chen', 'u-li'], 1, null), ['u-chen', null], '清空为待领取')

assertEqual(segmentColor(1), PICKER_SEGMENT_COLORS[0], '第 1 段蓝色')
assertEqual(segmentColor(PICKER_SEGMENT_COLORS.length + 2), PICKER_SEGMENT_COLORS[1], '超过色板长度时循环')

// 分单提示行：只在首行有货位时提示起始排，换排处不再插折返行；无货位/编码不规范的行只提示一次。
assertDeepEqual(
  slipTurnMarkers([
    { zone: 'A', rowLabel: '01' },
    { zone: 'A', rowLabel: '01' },
    { zone: 'A', rowLabel: '02' },
    { zone: null, rowLabel: null },
    { zone: null, rowLabel: null },
  ]),
  [
    { kind: 'start', zone: 'A', rowLabel: '01' },
    null,
    null,
    { kind: 'unlocated' },
    null,
  ],
  '只留起始排提示，不再有折返行',
)
assertDeepEqual(slipTurnMarkers([{ zone: null, rowLabel: null }, { zone: 'A', rowLabel: '01' }]), [{ kind: 'unlocated' }, null], '首行无货位时不提示起始排')

// 每人进度状态。
const now = Date.parse('2026-10-01T02:00:00Z')
assertDeepEqual(assigneeStatus({ lineCount: 11, completedLineCount: 10, stockoutLineCount: 1 }, now), { kind: 'done', stockoutLineCount: 1 }, '拣齐加没货覆盖整段')
assertDeepEqual(
  assigneeStatus({ lineCount: 11, completedLineCount: 7, lastActiveAtUtc: '2026-10-01T01:57:30Z' }, now),
  { kind: 'picking', minutesAgo: 2 },
  '拣货中，按整分钟向下取',
)
assertDeepEqual(assigneeStatus({ lineCount: 10, completedLineCount: 0 }, now), { kind: 'idle' }, '没有操作过为未开始')
assertDeepEqual(
  assigneeStatus({ lineCount: 4, completedLineCount: 1, pickedPieces: 28 }, now),
  { kind: 'helped' },
  '负责人没动过但已有人帮拣，不显示未开始',
)
assertDeepEqual(assigneeStatus({ lineCount: 0 }, now), { kind: 'idle' }, '空段不算已拣完')
assertDeepEqual(
  assigneeStatus({ lineCount: 11, completedLineCount: 7, lastActiveAtUtc: '2026-10-01T01:57:30.123' }, now),
  { kind: 'picking', minutesAgo: 2 },
  '后端不带 Z 的时间按 UTC 算，不能被当成本地时间',
)

// UTC 字段解析与本地显示。
assertDeepEqual(parseUtcMs('2026-10-01T02:04:56.263'), Date.parse('2026-10-01T02:04:56.263Z'), '缺时区标记按 UTC')
assertDeepEqual(parseUtcMs('2026-10-01T12:04:56+10:00'), Date.parse('2026-10-01T02:04:56Z'), '带偏移的保持原意')
assertDeepEqual(Number.isNaN(parseUtcMs(null)), true, '空值为 NaN')
{
  const local = new Date(Date.parse('2026-10-01T02:04:56Z'))
  const pad = (part: number) => String(part).padStart(2, '0')
  const expected = `${pad(local.getMonth() + 1)}-${pad(local.getDate())} ${pad(local.getHours())}:${pad(local.getMinutes())}`
  assertDeepEqual(formatUtcShort('2026-10-01T02:04:56.263'), expected, 'UTC 时间按本地时区显示')
}
assertDeepEqual(formatUtcShort(undefined), '—', '空值显示破折号')

// 分单明细行与分页。
const slipRows = buildSlipRows([
  { zone: 'A', rowLabel: '01', id: 'a' },
  { zone: 'A', rowLabel: '02', id: 'b' },
  { zone: null, rowLabel: null, id: 'c' },
])
assertDeepEqual(
  slipRows.map((row) => (row.kind === 'line' ? `${row.index}:${row.line.id}` : row.kind)),
  ['start', '1:a', '2:b', 'unlocated', '3:c'],
  '起始排与无货位提示行插在对应商品前，折返行不再出现',
)
const isMarker = (row: string) => row.startsWith('#')
const rowMm = (row: string) => (isMarker(row) ? 6 : 8)
assertDeepEqual(paginateSlipRowsByHeight(['#A', '1', '2', '3', '4'], 6 + 16, 24, rowMm, isMarker), [['#A', '1', '2'], ['3', '4']], '首页 22mm：提示行 6 + 两个商品行 16')
assertDeepEqual(paginateSlipRowsByHeight(['#A', '1', '2', '#B', '3', '4'], 6 + 16 + 6, 100, rowMm, isMarker), [['#A', '1', '2'], ['#B', '3', '4']], '提示行不落在页尾')
assertDeepEqual(paginateSlipRowsByHeight(['1', '2', '3', '4', '5'], 16, 16, rowMm, isMarker), [['1', '2'], ['3', '4'], ['5']], '按高度整行分页，不让一行跨页')
assertDeepEqual(paginateSlipRowsByHeight(['1', '2', '3'], 15.9, 100, rowMm, isMarker), [['1'], ['2', '3']], '差一点放不下的行整行挪到下一页')
assertDeepEqual(paginateSlipRowsByHeight([], 20, 30, rowMm, isMarker), [[]], '空段也有一页')
assertDeepEqual(paginateSlipRowsByHeight(['1', '2'], 3, 3, rowMm, isMarker), [['1'], ['2']], '单行比整页还高也独占一页，不死循环')
// 每页行数不超过版面预算：200 个商品行逐页累计高度都不超过各自页的可用高度。
const many = Array.from({ length: 200 }, (_, index) => String(index + 1))
const manyPages = paginateSlipRowsByHeight(many, SLIP_FIRST_PAGE_BODY_MM, SLIP_NEXT_PAGE_BODY_MM, rowMm, isMarker)
manyPages.forEach((page, index) => {
  const used = page.reduce((sum, row) => sum + rowMm(row), 0)
  assertEqual(used <= (index === 0 ? SLIP_FIRST_PAGE_BODY_MM : SLIP_NEXT_PAGE_BODY_MM), true, `第 ${index + 1} 页高度 ${used}mm 在预算内`)
})
assertEqual(manyPages.flat().length, 200, '分页后一行不丢')
// “其他段”文字超过一行时首页预算每多一行少 5mm，实测溢出的收紧量也要扣掉。
assertEqual(slipFirstPageBodyMm('x'.repeat(60)), SLIP_FIRST_PAGE_BODY_MM, '一行以内不扣')
assertEqual(slipFirstPageBodyMm('x'.repeat(61)), SLIP_FIRST_PAGE_BODY_MM - 5, '两行扣 5mm')
assertEqual(slipFirstPageBodyMm('x'.repeat(130), 8), SLIP_FIRST_PAGE_BODY_MM - 10 - 8, '三行再加收紧量')

// 分单页地址里没有订单时（页签只记路径）改用上次的查询串；地址有订单就以地址为准。
assertEqual(resolveSlipsSearch('?orders=a,b&segment=2', '?orders=z'), '?orders=a,b&segment=2', '地址有订单以地址为准')
assertEqual(resolveSlipsSearch('', '?orders=z&segment=1'), '?orders=z&segment=1', '地址为空时恢复上次')
assertEqual(resolveSlipsSearch('?segment=3', '?orders=z'), '?orders=z', '地址只剩无关参数也恢复')
assertEqual(resolveSlipsSearch('', null), null, '都没有时为空')
assertEqual(resolveSlipsSearch('', '?foo=1'), null, '上次记的也没有订单时不恢复')
assertEqual(resolveSlipsSearch('?orders=,', '?orders=z'), '?orders=z', '空订单号按没有处理')

// 拣货分配卡片显隐：只读订单没有任何分配时隐藏；能派单的订单即使没分配也要显示（唯一的派单入口）。
assertEqual(shouldShowPickingAssignmentSection({ assigneeCount: 0, assignable: false }), false, '只读且没有分配时隐藏')
assertEqual(shouldShowPickingAssignmentSection({ assigneeCount: 0, assignable: true }), true, '能派单但还没分配时保留入口')
assertEqual(shouldShowPickingAssignmentSection({ assigneeCount: 3, assignable: false }), true, '只读但有分配时仍展示记录与重新打印')
assertEqual(shouldShowPickingAssignmentSection({ assigneeCount: 3, assignable: true }), true, '能派单且有分配时展示')

// 分单“同单其他段”：待领取按界面语言渲染；旧后端没有结构化字段时回退原占位串。
const unclaimed = (no: number) => `Segment ${no} unclaimed`
assertDeepEqual(
  otherSegmentLabels({ otherPickerNames: ['第1段待领取', 'Li Na'], otherSegments: [{ segmentNo: 1, pickerName: null }, { segmentNo: 3, pickerName: ' Li Na ' }] }, unclaimed),
  ['Segment 1 unclaimed', 'Li Na'],
  '待领取用本地化文案、有姓名直接写姓名',
)
assertDeepEqual(otherSegmentLabels({ otherPickerNames: ['第2段待领取'] }, unclaimed), ['第2段待领取'], '旧后端无结构化字段时原样回退')
assertDeepEqual(otherSegmentLabels({ otherPickerNames: [], otherSegments: [] }, unclaimed), [], '只有一段时为空')

console.log('picking assignment logic tests passed')
