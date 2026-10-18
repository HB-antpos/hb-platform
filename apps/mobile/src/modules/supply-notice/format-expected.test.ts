import assert from "node:assert/strict";
import en from "../../locales/en/screens/supplyNotice.json";
import zh from "../../locales/zh/screens/supplyNotice.json";
import { formatSupplyDay, formatSupplyExpected } from "./format-expected";

// 用真实语言包做 {{key}} 插值，中英文日期格式都按实际界面断言。
function translator(messages: Record<string, unknown>) {
  return (key: string, params?: Record<string, unknown>) => {
    const template = messages[key];
    assert.equal(typeof template, "string", `缺少文案 ${key}`);
    return (template as string).replace(/\{\{(\w+)\}\}/g, (_, name: string) => String(params?.[name] ?? ""));
  };
}
const zhT = translator(zh);
const enT = translator(en);
const year = 2026;

assert.equal(formatSupplyExpected({ expectedFrom: null, expectedTo: null, expectedPrecision: "Unknown", isOverdue: false }, zhT, year), zh.expectedUnknown);
assert.equal(formatSupplyExpected({ expectedFrom: "2026-10-05", expectedTo: "2026-10-05", expectedPrecision: "Day", isOverdue: false }, zhT, year), "10月5日");
assert.equal(formatSupplyExpected({ expectedFrom: "2027-10-05", expectedTo: "2027-10-05", expectedPrecision: "Day", isOverdue: false }, zhT, year), "2027年10月5日");
assert.equal(formatSupplyExpected({ expectedFrom: "2026-10-05", expectedTo: "2026-10-10", expectedPrecision: "Range", isOverdue: false }, zhT, year), "10月5日 – 10月10日");
assert.equal(formatSupplyExpected({ expectedFrom: "2026-10-01", expectedTo: "2026-10-31", expectedPrecision: "Month", isOverdue: false }, zhT, year), "10 月");
assert.equal(formatSupplyExpected({ expectedFrom: "2027-09-01", expectedTo: "2027-09-30", expectedPrecision: "Month", isOverdue: false }, zhT, year), "2027 年 9 月");

// 英文界面不能出现「月」「日」，按澳洲习惯日在前。
assert.equal(formatSupplyExpected({ expectedFrom: "2026-10-05", expectedTo: "2026-10-05", expectedPrecision: "Day", isOverdue: false }, enT, year), "5 Oct");
assert.equal(formatSupplyExpected({ expectedFrom: "2027-10-05", expectedTo: "2027-10-05", expectedPrecision: "Day", isOverdue: false }, enT, year), "5 Oct 2027");
assert.equal(formatSupplyExpected({ expectedFrom: "2026-10-05", expectedTo: "2026-10-10", expectedPrecision: "Range", isOverdue: false }, enT, year), "5 Oct – 10 Oct");
assert.equal(formatSupplyExpected({ expectedFrom: "2026-10-01", expectedTo: "2026-10-31", expectedPrecision: "Month", isOverdue: false }, enT, year), "Oct");
assert.equal(formatSupplyExpected({ expectedFrom: "2027-09-01", expectedTo: "2027-09-30", expectedPrecision: "Month", isOverdue: false }, enT, year), "Sep 2027");

// 逾期优先：不再把过期日期当承诺展示。
assert.equal(formatSupplyExpected({ expectedFrom: "2020-01-01", expectedTo: "2020-01-01", expectedPrecision: "Day", isOverdue: true }, enT, year), en.expectedOverdue);
// 后端只给到日期，带时间的 ISO 串也要能解析。
assert.equal(formatSupplyExpected({ expectedFrom: "2026-10-05T00:00:00", expectedTo: "2026-10-05T00:00:00", expectedPrecision: "Day", isOverdue: false }, zhT, year), "10月5日");

// 「最近更新」日期与预计日期同一格式。
assert.equal(formatSupplyDay("2026-03-09T10:00:00Z", enT, year), "9 Mar");
assert.equal(formatSupplyDay("2026-03-09T10:00:00Z", zhT, year), "3月9日");
assert.equal(formatSupplyDay(null, enT, year), null);

console.log("format-expected.test: ok");
