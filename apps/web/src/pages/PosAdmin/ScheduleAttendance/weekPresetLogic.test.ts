import assert from 'node:assert/strict'

import { activeWeekPreset, isDateInRange, mondayOf, weekPresetRange } from './weekPresetLogic'

// 周一到周日：2026-11-19 是周四
assert.equal(mondayOf('2026-11-19'), '2026-11-16')
assert.equal(mondayOf('2026-11-16'), '2026-11-16', '周一就是自己')
assert.equal(mondayOf('2026-11-22'), '2026-11-16', '周日退回本周一，不是下周一')
assert.equal(mondayOf('2027-01-01'), '2026-12-28', '跨年')
assert.equal(mondayOf('nope'), null)
assert.equal(mondayOf('2026-02-30'), null, '不存在的日期')

assert.deepEqual(weekPresetRange('current', '2026-11-19'), ['2026-11-16', '2026-11-22'])
assert.deepEqual(weekPresetRange('previous', '2026-11-19'), ['2026-11-09', '2026-11-15'])
assert.deepEqual(weekPresetRange('next', '2026-11-22'), ['2026-11-23', '2026-11-29'])
assert.deepEqual(weekPresetRange('next', '2026-12-30'), ['2027-01-04', '2027-01-10'], '跨年')
assert.equal(weekPresetRange('current', ''), null)

assert.equal(activeWeekPreset('2026-11-09', '2026-11-15', '2026-11-19'), 'previous')
assert.equal(activeWeekPreset('2026-11-16', '2026-11-22', '2026-11-19'), 'current')
assert.equal(activeWeekPreset('2026-11-16', '2026-11-21', '2026-11-19'), undefined, '自定义范围不高亮')
assert.equal(activeWeekPreset(undefined, '2026-11-22', '2026-11-19'), undefined)

assert.equal(isDateInRange('2026-11-16', '2026-11-16', '2026-11-22'), true, '含首日')
assert.equal(isDateInRange('2026-11-22T00:00:00', '2026-11-16', '2026-11-22'), true, '带时间也按日期比')
assert.equal(isDateInRange('2026-11-23', '2026-11-16', '2026-11-22'), false)
assert.equal(isDateInRange(undefined, '2026-11-16', '2026-11-22'), false)

console.log('weekPresetLogic.test: ok')
