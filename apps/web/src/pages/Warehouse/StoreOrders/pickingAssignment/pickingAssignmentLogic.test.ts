import {
  PICKER_SEGMENT_COLORS,
  adjustSegmentCount,
  assignSegmentPicker,
  assigneeStatus,
  formatUtcShort,
  buildSlipRows,
  evenSegmentCounts,
  paginateSlipRows,
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

// 分单换排提示：同排不重复提示，无货位/编码不规范的行只提示一次。
assertDeepEqual(
  slipTurnMarkers([
    { zone: 'A', rowLabel: '01' },
    { zone: 'A', rowLabel: '01' },
    { zone: 'A', rowLabel: '02' },
    { zone: null, rowLabel: null },
    { zone: null, rowLabel: null },
  ]),
  [
    { kind: 'row', zone: 'A', rowLabel: '01', first: true },
    null,
    { kind: 'row', zone: 'A', rowLabel: '02', first: false },
    { kind: 'unlocated' },
    null,
  ],
  '换排提示',
)

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
  ['turn', '1:a', 'turn', '2:b', 'unlocated', '3:c'],
  '换排与无货位提示行插在对应商品前',
)
const isMarker = (row: string) => row.startsWith('#')
assertDeepEqual(paginateSlipRows(['#A', '1', '2', '#B', '3', '4'], 4, 3, isMarker), [['#A', '1', '2'], ['#B', '3', '4']], '提示行不落在页尾')
assertDeepEqual(paginateSlipRows(['1', '2', '3', '4', '5'], 2, 2, isMarker), [['1', '2'], ['3', '4'], ['5']], '按容量分页')
assertDeepEqual(paginateSlipRows([], 20, 30, isMarker), [[]], '空段也有一页')

// 拣货分配卡片显隐：只读订单没有任何分配时隐藏；能派单的订单即使没分配也要显示（唯一的派单入口）。
assertEqual(shouldShowPickingAssignmentSection({ assigneeCount: 0, assignable: false }), false, '只读且没有分配时隐藏')
assertEqual(shouldShowPickingAssignmentSection({ assigneeCount: 0, assignable: true }), true, '能派单但还没分配时保留入口')
assertEqual(shouldShowPickingAssignmentSection({ assigneeCount: 3, assignable: false }), true, '只读但有分配时仍展示记录与重新打印')
assertEqual(shouldShowPickingAssignmentSection({ assigneeCount: 3, assignable: true }), true, '能派单且有分配时展示')

console.log('picking assignment logic tests passed')
