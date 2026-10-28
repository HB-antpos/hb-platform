import assert from 'node:assert/strict'

import {
  DEFAULT_SHIFT,
  SHIFT_PRESETS,
  buildScheduleSavePayloads,
  defaultMealBreakCount,
  filterRowsByKeyword,
  getMealSummary,
  getNextEmptyDateKey,
  getOccupiedDateKeys,
  getShiftHours,
  normalizeHm,
  toMealChoice,
} from './scheduleCellEditorLogic'

// 时间统一收敛成 HH:mm（接口可能返回 HH:mm:ss）
assert.equal(normalizeHm('09:00:00'), '09:00')
assert.equal(normalizeHm('09:00'), '09:00')
assert.equal(normalizeHm(undefined), '')

// 固定常用班次：5 个，顺序即界面顺序；默认时段 09:00–17:30，不再按本周班次推导
assert.deepEqual(
  SHIFT_PRESETS.map((preset) => `${preset.start}-${preset.end}`),
  ['09:00-13:00', '16:00-17:30', '09:00-17:00', '09:00-21:00', '09:00-19:00'],
)
assert.deepEqual(DEFAULT_SHIFT, { start: '09:00', end: '17:30' })

// 已取消的班次不占用日期
{
  const occupied = getOccupiedDateKeys({
    '2026-10-26': [{ status: 'Cancelled' }],
    '2026-10-27': [{ status: 'Draft' }],
    '2026-10-28': [{ status: 'Cancelled' }, { status: 'Active' }],
  })
  assert.deepEqual(Array.from(occupied).sort(), ['2026-10-27', '2026-10-28'])
}

// 默认用餐次数：门槛都是「超过」，与后端 AttendanceMealBreakRules 一致
assert.equal(defaultMealBreakCount(240), 0) // 4 小时
assert.equal(defaultMealBreakCount(269), 0)
assert.equal(defaultMealBreakCount(270), 1) // 正好 4.5 小时起扣 1 次
assert.equal(defaultMealBreakCount(271), 1)
assert.equal(defaultMealBreakCount(480), 1) // 8 小时
assert.equal(defaultMealBreakCount(540), 1) // 正好 9 小时仍按 1 次
assert.equal(defaultMealBreakCount(541), 2)
assert.equal(defaultMealBreakCount(720), 2) // 12 小时

// 用餐算式（设计稿里的数字）：自动 / 手动 / 不扣
assert.deepEqual(getMealSummary('09:00', '17:30', 'auto'), { hours: 8.5, count: 1, isAuto: true, deductMinutes: 30, paidHours: 8 })
assert.deepEqual(getMealSummary('09:00', '17:00', 'auto'), { hours: 8, count: 1, isAuto: true, deductMinutes: 30, paidHours: 7.5 })
assert.deepEqual(getMealSummary('09:00', '13:00', 'auto'), { hours: 4, count: 0, isAuto: true, deductMinutes: 0, paidHours: 4 })
assert.deepEqual(getMealSummary('16:00', '17:30', 'auto'), { hours: 1.5, count: 0, isAuto: true, deductMinutes: 0, paidHours: 1.5 })
assert.deepEqual(getMealSummary('09:00', '21:00', 'auto'), { hours: 12, count: 2, isAuto: true, deductMinutes: 60, paidHours: 11 })
assert.deepEqual(getMealSummary('09:00', '19:00', 'auto'), { hours: 10, count: 2, isAuto: true, deductMinutes: 60, paidHours: 9 })
assert.deepEqual(getMealSummary('09:00', '17:30', 0), { hours: 8.5, count: 0, isAuto: false, deductMinutes: 0, paidHours: 8.5 })
assert.deepEqual(getMealSummary('09:00', '17:00', 2), { hours: 8, count: 2, isAuto: false, deductMinutes: 60, paidHours: 7 })
assert.equal(getMealSummary('17:00', '09:00', 'auto'), undefined) // 结束不晚于开始不下结论
assert.equal(getMealSummary('', '09:00', 1), undefined)

// 记录里的 mealBreakCount → 界面选择：只认 0–3，其余（含 null / undefined）都是自动
assert.equal(toMealChoice(undefined), 'auto')
assert.equal(toMealChoice(null), 'auto')
assert.equal(toMealChoice(0), 0)
assert.equal(toMealChoice(3), 3)
assert.equal(toMealChoice(4), 'auto')
assert.equal(toMealChoice(-1), 'auto')

// 保存后跳到右侧下一个空白格：跳过有班次的日期和本次刚一并写入的日期
{
  const week = ['2026-10-26', '2026-10-27', '2026-10-28', '2026-10-29', '2026-10-30', '2026-10-31', '2026-11-01']
  const occupied = new Set(['2026-10-27', '2026-10-28'])
  assert.equal(getNextEmptyDateKey('2026-10-26', week, occupied), '2026-10-29')
  assert.equal(getNextEmptyDateKey('2026-10-26', week, occupied, ['2026-10-29']), '2026-10-30')
  assert.equal(getNextEmptyDateKey('2026-11-01', week, occupied), undefined)
  assert.equal(getNextEmptyDateKey('2026-12-01', week, occupied), undefined)
}

// 保存载荷：日期去重排序、备注去空白、GUID 去首尾空格
{
  const payloads = buildScheduleSavePayloads({
    storeCode: '1013',
    userGuid: ' u-1 ',
    dateKeys: ['2026-10-29', '2026-10-28', '2026-10-29'],
    start: '11:00',
    end: '19:00',
    remark: '  补缺口  ',
    status: 'Draft',
  })
  assert.deepEqual(payloads.map((payload) => payload.workDate), ['2026-10-28', '2026-10-29'])
  assert.ok(payloads.every((payload) => payload.userGuid === 'u-1' && payload.status === 'Draft' && payload.remark === '补缺口'))
  assert.equal(buildScheduleSavePayloads({ storeCode: '1013', userGuid: 'u', dateKeys: ['2026-10-28'], start: '09:00', end: '17:00', remark: '   ', status: 'Active' })[0].remark, undefined)
}

// 用餐字段：新建选数字带 mealBreakCount；选自动新建时不带；更新时选自动要显式带恢复标记
{
  const base = { storeCode: '1013', userGuid: 'u', dateKeys: ['2026-10-28'], start: '09:00', end: '17:30', status: 'Draft' as const }
  const manual = buildScheduleSavePayloads({ ...base, mealChoice: 2 })[0]
  assert.equal(manual.mealBreakCount, 2)
  assert.equal('resetMealBreakCount' in manual, false)
  const zero = buildScheduleSavePayloads({ ...base, mealChoice: 0 })[0]
  assert.equal(zero.mealBreakCount, 0)
  const autoCreate = buildScheduleSavePayloads({ ...base, mealChoice: 'auto' })[0]
  assert.equal('mealBreakCount' in autoCreate, false)
  assert.equal('resetMealBreakCount' in autoCreate, false)
  const autoUpdate = buildScheduleSavePayloads({ ...base, mealChoice: 'auto', resetAutoMeal: true })[0]
  assert.equal(autoUpdate.resetMealBreakCount, true)
  assert.equal('mealBreakCount' in autoUpdate, false)
  // 不传 mealChoice：什么用餐字段都不带（后端保持原值）
  const none = buildScheduleSavePayloads(base)[0]
  assert.equal('mealBreakCount' in none || 'resetMealBreakCount' in none, false)
}

// 姓名本地搜索：按姓名或 GUID 子串，忽略大小写；空关键字不过滤
{
  const rows = [
    { userGuid: 'a-1', userName: 'Sarah Chen' },
    { userGuid: 'b-2', userName: '陈静' },
    { userGuid: 'c-3', userName: undefined },
  ]
  assert.equal(filterRowsByKeyword(rows, '').length, 3)
  assert.equal(filterRowsByKeyword(rows, undefined).length, 3)
  assert.deepEqual(filterRowsByKeyword(rows, ' SARAH ').map((row) => row.userGuid), ['a-1'])
  assert.deepEqual(filterRowsByKeyword(rows, '陈').map((row) => row.userGuid), ['b-2'])
  assert.deepEqual(filterRowsByKeyword(rows, 'C-3').map((row) => row.userGuid), ['c-3'])
}

// 时长：结束不晚于开始不下结论
assert.equal(getShiftHours('09:00', '17:00'), 8)
assert.equal(getShiftHours('09:00', '13:30'), 4.5)
assert.equal(getShiftHours('17:00', '09:00'), undefined)
assert.equal(getShiftHours('', '09:00'), undefined)

console.log('scheduleCellEditorLogic tests passed')
