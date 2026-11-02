import assert from "node:assert/strict";
import {
  formatAbn,
  formatDate,
  formatHourlyRate,
  formatHours,
  formatMoney,
  formatPeriodFull,
  formatPeriodShort,
  formatRate,
  formatShortDate,
  groupThousands,
  toDateOnly,
} from "./format";

// 金额：$、千分位、负数 -$、四舍五入后为 0 不带负号、空值 —
assert.equal(formatMoney(762), "$762.00");
assert.equal(formatMoney(13851.68), "$13,851.68");
assert.equal(formatMoney(1234567.891), "$1,234,567.89");
assert.equal(formatMoney(-138), "-$138.00");
assert.equal(formatMoney(-2208), "-$2,208.00");
assert.equal(formatMoney(-0.004), "$0.00");
assert.equal(formatMoney(0), "$0.00");
assert.equal(formatMoney(null), "—");
assert.equal(formatMoney(undefined, "n/a"), "n/a");
assert.equal(formatMoney(Number.NaN), "—");
assert.equal(groupThousands("1000000"), "1,000,000");
assert.equal(groupThousands("999"), "999");

// 小时数：两位小数 + " h"
assert.equal(formatHours(30), "30.00 h");
assert.equal(formatHours(1.15), "1.15 h");
assert.equal(formatHours(1234.5), "1,234.50 h");
assert.equal(formatHours(null), "—");

// 明细费率：至少两位，有更多有效小数保留到四位；Hourly Rate 固定四位
assert.equal(formatRate(24), "$24.00");
assert.equal(formatRate(30.5), "$30.50");
assert.equal(formatRate(24.5438), "$24.5438");
assert.equal(formatRate(24.5), "$24.50");
assert.equal(formatRate(null), "—");
assert.equal(formatHourlyRate(24), "$24.0000");
assert.equal(formatHourlyRate(24.5438), "$24.5438");

// 日期：d/m/yyyy 不补零
assert.equal(toDateOnly("2026-10-29T00:00:00Z"), "2026-10-29");
assert.equal(toDateOnly("abc"), "");
assert.equal(toDateOnly(null), "");
assert.equal(formatDate("2026-10-29"), "29/10/2026");
assert.equal(formatDate("2026-01-05"), "5/1/2026");
assert.equal(formatDate("2026-01-05T10:00:00"), "5/1/2026");
assert.equal(formatDate(null), "");
assert.equal(formatDate("not a date"), "not a date");
assert.equal(formatShortDate("2026-10-19"), "19/10");
assert.equal(formatShortDate("2026-03-02"), "2/3");
assert.equal(formatPeriodShort("2026-10-19", "2026-10-25"), "19/10 — 25/10");
assert.equal(formatPeriodShort("2026-10-19", null), "19/10");
assert.equal(formatPeriodShort(null, null), "");
assert.equal(formatPeriodFull("2026-10-19", "2026-10-25"), "19/10/2026 — 25/10/2026");

// ABN：11 位纯数字按 2-3-3-3 分组，已有格式原样
assert.equal(formatAbn("12345678901"), "12 345 678 901");
assert.equal(formatAbn("12 345 678 901"), "12 345 678 901");
assert.equal(formatAbn(""), "");
assert.equal(formatAbn(null), "");
