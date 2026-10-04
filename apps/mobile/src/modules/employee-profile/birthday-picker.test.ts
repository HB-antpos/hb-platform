import assert from "node:assert/strict";
import { validateBirthday } from "./birthday";
import {
  clampBirthdayParts,
  formatBirthdayParts,
  getBirthdayYearOptions,
  getDaysInMonth,
  getMaxBirthdayDay,
  getMaxBirthdayMonth,
  parseBirthdayParts,
  updateBirthdayParts,
} from "./birthday-picker";

const today = new Date(2026, 9, 4); // 2026-10-04 本地时间

// 每月天数含闰年规则。
assert.equal(getDaysInMonth(2024, 2), 29);
assert.equal(getDaysInMonth(2026, 2), 28);
assert.equal(getDaysInMonth(1900, 2), 28, "1900 年不是闰年");
assert.equal(getDaysInMonth(2000, 2), 29, "2000 年是闰年");
assert.equal(getDaysInMonth(2026, 4), 30);

// 年份候选：今年在前，下限 1900。
const years = getBirthdayYearOptions(today);
assert.equal(years[0], 2026);
assert.equal(years[years.length - 1], 1900);
assert.equal(years.length, 127);

// 今年只能选到本月与今天，避免选出未来日期。
assert.equal(getMaxBirthdayMonth(2026, today), 10);
assert.equal(getMaxBirthdayMonth(2025, today), 12);
assert.equal(getMaxBirthdayDay(2026, 10, today), 4);
assert.equal(getMaxBirthdayDay(2026, 9, today), 30);

// 切换月份/年份时日期联动修正。
assert.deepEqual(updateBirthdayParts({ year: 2025, month: 3, day: 31 }, { month: 2 }, today), { year: 2025, month: 2, day: 28 });
assert.deepEqual(updateBirthdayParts({ year: 2024, month: 2, day: 29 }, { year: 2023 }, today), { year: 2023, month: 2, day: 28 });
assert.deepEqual(updateBirthdayParts({ year: 2020, month: 12, day: 25 }, { year: 2026 }, today), { year: 2026, month: 10, day: 4 });
assert.deepEqual(clampBirthdayParts({ year: 1800, month: 0, day: 0 }, today), { year: 1900, month: 1, day: 1 });

// 解析与格式化往返；空值回落到默认年份。
assert.deepEqual(parseBirthdayParts("1998-06-21T00:00:00", today), { year: 1998, month: 6, day: 21 });
assert.deepEqual(parseBirthdayParts("", today), { year: 2001, month: 1, day: 1 });
assert.deepEqual(parseBirthdayParts("2023-02-30", today), { year: 2001, month: 1, day: 1 });
assert.equal(formatBirthdayParts({ year: 1998, month: 6, day: 1 }), "1998-06-01");

// 选择器能产出的任何值都必须通过 birthday.ts 的校验。
for (const parts of [
  updateBirthdayParts({ year: 2026, month: 10, day: 4 }, { day: 31 }, today),
  updateBirthdayParts({ year: 1900, month: 1, day: 1 }, { month: 2, day: 30 }, today),
]) {
  assert.equal(validateBirthday(formatBirthdayParts(parts), today), null);
}

console.log("birthday-picker tests passed");
