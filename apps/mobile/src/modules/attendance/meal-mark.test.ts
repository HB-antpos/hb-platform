import assert from "node:assert/strict";
import { getMealMark, MEAL_LONG_SHIFT_MINUTES } from "./meal-mark";

// 用「09:00 + N 分钟」造班次，边界值直接用分钟表达
const shift = (minutes: number, extra: Record<string, unknown> = {}) => {
  const end = 9 * 60 + minutes;
  const hh = String(Math.floor(end / 60)).padStart(2, "0");
  const mm = String(end % 60).padStart(2, "0");
  return getMealMark({ startTime: "09:00:00", endTime: `${hh}:${mm}:00`, ...extra });
};

// 阈值：269 分钟不到 4.5 小时 → 没有午饭，也不标；270 分钟起 1 次；正好 9 小时（540）仍 1 次；541 起 2 次
assert.equal(MEAL_LONG_SHIFT_MINUTES, 270);
assert.deepEqual(shift(269), { kind: "hidden", count: 0, manual: false, deductMinutes: 0, durationMinutes: 269 }, "不到 4.5 小时不标识");
assert.deepEqual(shift(270), { kind: "meal", count: 1, manual: false, deductMinutes: 30, durationMinutes: 270 }, "满 4.5 小时自动 1 次");
assert.deepEqual(shift(510), { kind: "meal", count: 1, manual: false, deductMinutes: 30, durationMinutes: 510 }, "09:00–17:30 自动 1 次");
assert.equal(shift(540).count, 1, "正好 9 小时仍按 1 次");
assert.deepEqual(shift(541), { kind: "meal", count: 2, manual: false, deductMinutes: 60, durationMinutes: 541 }, "超过 9 小时自动 2 次");

// 手动指定优先于自动：0 / 1 / 2 / 3 次
assert.deepEqual(shift(510, { mealBreakCount: 0 }), { kind: "none", count: 0, manual: true, deductMinutes: 0, durationMinutes: 510 }, "长班次手动不扣 → 无午饭（例外）");
assert.deepEqual(shift(330, { mealBreakCount: 1 }), { kind: "meal", count: 1, manual: true, deductMinutes: 30, durationMinutes: 330 }, "手动 1 次 → 含午饭且标记为手动");
assert.equal(shift(510, { mealBreakCount: 2 }).count, 2, "手动 2 次");
assert.equal(shift(510, { mealBreakCount: 3 }).deductMinutes, 90, "手动 3 次扣 90 分钟");
assert.equal(shift(600, { mealBreakCount: 1 }).count, 1, "手动值优先于时长默认的 2 次");

// 手动不扣的短班次：本来就没有午饭，不算例外，仍不标识
assert.equal(shift(240, { mealBreakCount: 0 }).kind, "hidden", "短班次手动 0 次不标识");
assert.equal(shift(269, { mealBreakCount: 0 }).kind, "hidden");
assert.equal(shift(270, { mealBreakCount: 0 }).kind, "none", "刚好满 4.5 小时手动不扣 → 无午饭");

// null / undefined 是自动；非 0–3 的脏数据按自动处理（与 Web 一致）
assert.equal(shift(510, { mealBreakCount: null }).manual, false);
assert.equal(shift(510, { mealBreakCount: undefined }).manual, false);
assert.equal(shift(510, { mealBreakCount: 9 }).manual, false, "超范围按自动");
assert.equal(shift(510, { mealBreakCount: 9 }).count, 1);
assert.equal(shift(510, { mealBreakCount: -1 }).count, 1);

// 已取消、时间不合法：不标识
assert.equal(shift(510, { status: "Cancelled" }).kind, "hidden", "已取消不标识");
assert.equal(shift(510, { status: "cancelled" }).kind, "hidden", "状态大小写不敏感");
assert.equal(shift(510, { status: "Draft" }).kind, "meal", "草稿照常标识");
assert.equal(getMealMark({ startTime: "17:00", endTime: "09:00" }).kind, "hidden", "结束早于开始（跨午夜）不下结论");
assert.equal(getMealMark({ startTime: "09:00", endTime: "09:00" }).kind, "hidden", "起止相同");
assert.equal(getMealMark({ startTime: "", endTime: "17:00" }).kind, "hidden", "缺开始时间");
assert.equal(getMealMark({ startTime: "abc", endTime: "17:00" }).kind, "hidden", "时间解析不了");

// 时间格式：HH:mm / HH:mm:ss / 带日期的 ISO 都能解析
assert.equal(getMealMark({ startTime: "09:00", endTime: "17:30" }).count, 1);
assert.equal(getMealMark({ startTime: "2026-11-16T09:00:00", endTime: "2026-11-16T17:30:00" }).count, 1);

console.log("meal-mark tests passed");
