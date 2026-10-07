import assert from "node:assert/strict";
import {
  amountToInputText,
  diffAmounts,
  formatAud,
  formatSignedAud,
  fromCents,
  parseMoneyInput,
  sanitizeMoneyInput,
  sumAmounts,
  toCents,
} from "./money";

// ───────── 分与求和 ─────────
assert.equal(toCents(0.1 + 0.2), 30, "浮点误差按分四舍五入");
assert.equal(fromCents(1999), 19.99);
assert.equal(sumAmounts([0.1, 0.2]), 0.3, "0.1 + 0.2 必须精确等于 0.3");
assert.equal(sumAmounts([100.1, 200.2, null, undefined, Number.NaN]), 300.3, "忽略 null / undefined / NaN");
assert.equal(sumAmounts([]), 0);
assert.equal(diffAmounts(100.3, 100.1), 0.2);

// ───────── 输入过滤 ─────────
assert.equal(sanitizeMoneyInput("12a.5b"), "12.5");
assert.equal(sanitizeMoneyInput("1.2.3"), "1.23");
assert.equal(sanitizeMoneyInput("12.345"), "12.34", "小数最多两位");
assert.equal(sanitizeMoneyInput("-5"), "5", "负号被过滤");
assert.equal(sanitizeMoneyInput(""), "");
assert.equal(sanitizeMoneyInput("12."), "12.", "输入中途保留小数点");

// ───────── 金额解析 ─────────
const ok = (text: string, options?: Parameters<typeof parseMoneyInput>[1]) => {
  const result = parseMoneyInput(text, options);
  assert.equal(result.ok, true, `${text} 应合法`);
  return result.ok ? result.value : NaN;
};
const issue = (text: string, options?: Parameters<typeof parseMoneyInput>[1]) => {
  const result = parseMoneyInput(text, options);
  assert.equal(result.ok, false, `${text} 应非法`);
  return result.ok ? null : result.issue;
};
assert.equal(ok("12"), 12);
assert.equal(ok("12.5"), 12.5);
assert.equal(ok("12.50"), 12.5);
assert.equal(ok(".5"), 0.5);
assert.equal(ok("12."), 12);
assert.equal(ok(" 7.25 "), 7.25, "首尾空白忽略");
assert.equal(ok("0.10"), 0.1);
assert.equal(ok("10000000"), 10_000_000, "上限本身允许");
assert.equal(issue(""), "empty");
assert.equal(issue("   "), "empty");
assert.equal(issue("-5"), "negative");
assert.equal(issue("abc"), "invalid");
assert.equal(issue("1,000"), "invalid", "千分位逗号拒绝");
assert.equal(issue("1e3"), "invalid", "科学计数法拒绝");
assert.equal(issue("12.345"), "tooManyDecimals", "超过两位小数不静默截断");
assert.equal(issue("0"), "notPositive", "存款与支出必须大于 0");
assert.equal(issue("0.00"), "notPositive");
assert.equal(ok("0", { allowZero: true }), 0, "期初与盘点允许 0");
assert.equal(issue("10000000.01"), "tooLarge");
assert.equal(issue("1000", { max: 500 }), "tooLarge");

// ───────── 展示格式 ─────────
assert.equal(formatAud(1234.5), "$1,234.50");
assert.equal(formatAud(0), "$0.00");
assert.equal(formatAud(1234567.891), "$1,234,567.89");
assert.equal(formatAud(-12), "-$12.00");
assert.equal(formatAud(0.005), "$0.01");
assert.equal(formatAud(null), "--", "日结未接入的 null 绝不能显示成 $0.00");
assert.equal(formatAud(undefined), "--");
assert.equal(formatAud(Number.NaN), "--");
assert.equal(formatSignedAud(5), "+$5.00");
assert.equal(formatSignedAud(-5.5), "-$5.50");
assert.equal(formatSignedAud(0), "$0.00");
assert.equal(formatSignedAud(null), "--");
assert.equal(amountToInputText(1234), "1234");
assert.equal(amountToInputText(1234.5), "1234.50");
assert.equal(amountToInputText(null), "");

console.log("money.test.ts: ok");
