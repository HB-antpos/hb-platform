import assert from 'node:assert/strict'

import { LONG_SHIFT_MINUTES, MEAL_ICON_PATH, MEAL_SLASH_PATH, getMealMark } from './mealMarkLogic'

// 以 09:00 起、按分钟数算结束时间
const end = (minutes: number) => {
  const total = 9 * 60 + minutes
  return `${String(Math.floor(total / 60)).padStart(2, '0')}:${String(total % 60).padStart(2, '0')}`
}
const shift = (minutes: number, extra: { mealBreakCount?: number | null; status?: string } = {}) => (
  getMealMark({ startTime: '09:00', endTime: end(minutes), ...extra })
)

assert.equal(LONG_SHIFT_MINUTES, 270, '满 4.5 小时（270 分钟）起才有午饭')

// ---------- 自动：4.5 小时（含）1 次、超过 9 小时 2 次 ----------
assert.equal(shift(269).kind, 'hidden', '269 分钟不到 4.5 小时：不显示')
assert.deepEqual(shift(270), { kind: 'meal', count: 1, manual: false, deductMinutes: 30, durationMinutes: 270 }, '正好 4.5 小时：1 次')
assert.deepEqual(shift(510), { kind: 'meal', count: 1, manual: false, deductMinutes: 30, durationMinutes: 510 }, '8.5 小时：1 次，扣 30 分钟')
assert.equal(shift(540).count, 1, '正好 9 小时仍是 1 次')
assert.deepEqual(shift(541), { kind: 'meal', count: 2, manual: false, deductMinutes: 60, durationMinutes: 541 }, '超过 9 小时：2 次，扣 60 分钟')

// ---------- 手动指定 ----------
assert.deepEqual(shift(510, { mealBreakCount: 2 }), { kind: 'meal', count: 2, manual: true, deductMinutes: 60, durationMinutes: 510 }, '手动 2 次：meal + manual')
assert.equal(shift(120, { mealBreakCount: 1 }).kind, 'meal', '短班次手动指定 1 次：照样显示')
assert.equal(shift(120, { mealBreakCount: 1 }).manual, true)
// 长班次手动「不扣」：最重要的例外
assert.deepEqual(shift(510, { mealBreakCount: 0 }), { kind: 'none', count: 0, manual: true, deductMinutes: 0, durationMinutes: 510 }, '8.5 小时手动不扣：none')
assert.equal(shift(270, { mealBreakCount: 0 }).kind, 'none', '正好 4.5 小时手动不扣：none')
assert.equal(shift(269, { mealBreakCount: 0 }).kind, 'hidden', '短班次手动不扣：本来就没有午饭，不提示')
assert.equal(shift(600, { mealBreakCount: 0 }).kind, 'none', '10 小时手动不扣：none')

// ---------- 其它 ----------
assert.equal(shift(510, { status: 'Cancelled' }).kind, 'hidden', '已取消的班次不显示标识')
assert.equal(shift(510, { status: 'Active' }).kind, 'meal', '已发布照常')
assert.equal(shift(510, { status: 'Draft' }).kind, 'meal', '草稿照常')
assert.equal(getMealMark({ startTime: '17:00', endTime: '09:00' }).kind, 'hidden', '结束不晚于开始：不下结论')
assert.equal(getMealMark({ startTime: '', endTime: '' }).kind, 'hidden', '时间缺失：不显示')
assert.equal(getMealMark({ startTime: '09:00:00', endTime: '17:30:00' }).kind, 'meal', '带秒的时间照常解析')

// 图标路径是两端 / 导出共用的常量，别被误改成空
assert.ok(MEAL_ICON_PATH.startsWith('M6 3') && MEAL_SLASH_PATH === 'M3 3l18 18')

console.log('mealMarkLogic ok')
