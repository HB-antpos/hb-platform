import assert from 'node:assert/strict'

import {
  DEFAULT_SHIFT_PRESETS,
  FALLBACK_SHIFT,
  buildScheduleSavePayloads,
  buildShiftPresets,
  filterRowsByKeyword,
  getNextEmptyDateKey,
  getOccupiedDateKeys,
  getShiftHours,
  normalizeHm,
  pickDefaultShift,
} from './scheduleCellEditorLogic'

const shift = (startTime: string, endTime: string, status: 'Draft' | 'Active' | 'Cancelled' = 'Active') => ({ startTime, endTime, status })

// 时间统一收敛成 HH:mm（接口可能返回 HH:mm:ss）
assert.equal(normalizeHm('09:00:00'), '09:00')
assert.equal(normalizeHm('09:00'), '09:00')
assert.equal(normalizeHm(undefined), '')

// 没有历史班次：用内置四档，默认时段沿用抽屉原来的 09:00-17:00
assert.deepEqual(buildShiftPresets([]), DEFAULT_SHIFT_PRESETS)
assert.deepEqual(pickDefaultShift([]), FALLBACK_SHIFT)

// 本周最常用的时段排最前，且恰好等于内置档位时带名称，自定义时段不带名称
{
  const week = [
    shift('12:00:00', '20:00:00'), shift('12:00', '20:00'), shift('12:00', '20:00', 'Draft'),
    shift('07:00', '15:00'), shift('07:00', '15:00'),
    shift('08:30', '16:30'),
  ]
  const presets = buildShiftPresets(week)
  assert.equal(presets.length, 4)
  assert.deepEqual(presets[0], { start: '12:00', end: '20:00' })
  assert.deepEqual(presets[1], { start: '07:00', end: '15:00', nameKey: 'morning' })
  assert.deepEqual(presets[2], { start: '08:30', end: '16:30' })
  // 已出现过的内置档位不会再被补一遍
  assert.equal(presets.filter((preset) => preset.start === '07:00' && preset.end === '15:00').length, 1)
  assert.deepEqual(pickDefaultShift(week), { start: '12:00', end: '20:00' })
}

// 已取消的班次不参与常用统计，也不占用日期
{
  const week = [shift('06:00', '14:00', 'Cancelled'), shift('06:00', '14:00', 'Cancelled'), shift('10:00', '18:00')]
  assert.deepEqual(pickDefaultShift(week), { start: '10:00', end: '18:00' })
  assert.ok(!buildShiftPresets(week).some((preset) => preset.start === '06:00'))
  const occupied = getOccupiedDateKeys({
    '2026-10-26': [{ status: 'Cancelled' }],
    '2026-10-27': [{ status: 'Draft' }],
    '2026-10-28': [{ status: 'Cancelled' }, { status: 'Active' }],
  })
  assert.deepEqual(Array.from(occupied).sort(), ['2026-10-27', '2026-10-28'])
}

// 次数相同按开始时间升序，结果稳定
assert.deepEqual(
  buildShiftPresets([shift('15:00', '23:00'), shift('07:00', '15:00')]).slice(0, 2).map((preset) => preset.start),
  ['07:00', '15:00'],
)

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
