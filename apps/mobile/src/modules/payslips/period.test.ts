import assert from "node:assert/strict";
import {
  addDays,
  DEFAULT_PAYSLIP_PRESET,
  fiscalYearStart,
  isValidIsoDate,
  localTodayIso,
  normalizeCustomRange,
  resolvePreset,
} from "./period";

assert.equal(DEFAULT_PAYSLIP_PRESET, "threeMonths");

// 日期加减：跨月 / 跨年 / 闰年
assert.equal(addDays("2026-10-29", -13), "2026-10-16");
assert.equal(addDays("2026-01-03", -5), "2025-12-29");
assert.equal(addDays("2028-03-01", -1), "2028-02-29");
assert.equal(addDays("bad", 3), "bad");

// 校验：拒绝进位日期
assert.equal(isValidIsoDate("2026-02-31"), false);
assert.equal(isValidIsoDate("2026-02-28"), true);
assert.equal(isValidIsoDate("2026-2-8"), false);
assert.equal(isValidIsoDate(undefined), false);

// 澳洲财年：7/1 起；6 月仍属上一财年
assert.equal(fiscalYearStart("2026-10-29"), "2026-07-01");
assert.equal(fiscalYearStart("2026-07-01"), "2026-07-01");
assert.equal(fiscalYearStart("2026-06-30"), "2025-07-01");
assert.equal(fiscalYearStart("2026-01-15"), "2025-07-01");

// 预设：最近两周 = 14 天含今天；近 3 个月 = 90 天含今天；本财年 = 财年首日至今天
assert.deepEqual(resolvePreset("twoWeeks", "2026-10-29"), { from: "2026-10-16", to: "2026-10-29" });
assert.deepEqual(resolvePreset("threeMonths", "2026-10-29"), { from: "2026-08-01", to: "2026-10-29" });
assert.deepEqual(resolvePreset("fiscalYear", "2026-10-29"), { from: "2026-07-01", to: "2026-10-29" });
assert.deepEqual(resolvePreset("custom", "2026-10-29", { from: "2026-09-01", to: "2026-09-30" }), { from: "2026-09-01", to: "2026-09-30" });
// 自定义没有区间时退回默认预设
assert.deepEqual(resolvePreset("custom", "2026-10-29"), resolvePreset("threeMonths", "2026-10-29"));

// 自定义区间：起止颠倒自动对调，缺失 / 非法返回 null
assert.deepEqual(normalizeCustomRange("2026-09-30", "2026-09-01"), { from: "2026-09-01", to: "2026-09-30" });
assert.equal(normalizeCustomRange("2026-09-01", undefined), null);
assert.equal(normalizeCustomRange("2026-13-01", "2026-09-01"), null);

// 设备本地「今天」
assert.equal(localTodayIso(new Date(2026, 9, 5, 23, 59)), "2026-10-05");
