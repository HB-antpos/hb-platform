import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import {
  LEGACY_DANGER_OPERATIONS,
  LEGACY_RULE_CODES,
  buildLegacyEmployeeSummaryQuery,
  buildLegacyLogQuery,
  countActiveLegacyFilters,
  createDefaultLegacyLogFilters,
  describeFlagEvidence,
  filtersFromRouteParams,
  filtersToRouteParams,
  groupLegacyLogsByHour,
  isDangerOperation,
  operationTone,
  orderLegacyEmployees,
  resolveLegacyRange,
  shortFlagEvidence,
  summarizeStores,
  switchLens,
} from "./logic";
import { normalizeLegacyLogItem, normalizeLegacyLogReview } from "./api-normalization";
import type { LegacyLogItem } from "./types";

const now = new Date(2026, 8, 30, 15, 30); // 2026-09-30 15:30 设备本地时间

// —— 时间范围：半开区间、按设备本地日期 ——
assert.deepEqual(resolveLegacyRange("today", now), { from: "2026-09-30T00:00:00", to: "2026-10-01T00:00:00" });
assert.deepEqual(resolveLegacyRange("yesterday", now), { from: "2026-09-29T00:00:00", to: "2026-09-30T00:00:00" });
assert.deepEqual(resolveLegacyRange("last7", now), { from: "2026-09-24T00:00:00", to: "2026-10-01T00:00:00" });
assert.deepEqual(resolveLegacyRange("last31", now), { from: "2026-08-31T00:00:00", to: "2026-10-01T00:00:00" }, "近 31 天恰好 31 天，不超过后端上限");

// —— 查询参数：数组用重复键，入口参数只在对应入口下传 ——
const base = { ...createDefaultLegacyLogFilters(["1003", " 1005 ", "1003"]), keyword: " xmas ", employeeId: "E-1", deviceCode: "POS_1003_1903" };
const all = buildLegacyLogQuery({ ...base, subOperations: ["删除商品"] }, 2, now)!;
assert.deepEqual(all.getAll("storeCodes"), ["1003", "1005"], "分店去空白去重");
assert.deepEqual(all.getAll("operations"), ["删除商品"]);
assert.equal(all.get("keyword"), "xmas");
assert.equal(all.get("employeeIds"), "E-1");
assert.equal(all.get("pageNumber"), "2");
assert.equal(all.has("riskLens"), false, "默认入口不传 riskLens");
const abnormal = buildLegacyLogQuery({ ...base, lens: "abnormal", subOperations: ["删除商品"], ruleCode: "noSaleDrawer", reviewStatus: "pending" }, 1, now)!;
assert.equal(abnormal.get("riskLens"), "abnormal");
assert.deepEqual(abnormal.getAll("ruleCodes"), ["noSaleDrawer"]);
assert.equal(abnormal.get("reviewStatus"), "pending");
assert.equal(abnormal.has("operations"), false, "异常入口不带操作类型细分");
const danger = buildLegacyLogQuery({ ...base, lens: "danger", ruleCode: "noSaleDrawer", reviewStatus: "pending" }, 1, now)!;
assert.equal(danger.has("ruleCodes"), false, "规则只在异常入口下传");
assert.equal(danger.has("reviewStatus"), false, "核查状态只在异常入口下传");
assert.equal(buildLegacyLogQuery(createDefaultLegacyLogFilters([]), 1, now), null, "没选分店不发请求");
const summaryQuery = buildLegacyEmployeeSummaryQuery({ ...base, lens: "abnormal" }, now)!;
assert.deepEqual([...summaryQuery.keys()].sort(), ["deviceCode", "from", "storeCodes", "storeCodes", "to"], "员工汇总只带分店、时间、设备");

// —— 入口切换与路由参数 ——
const switched = switchLens({ ...base, lens: "abnormal", ruleCode: "burstDelete", reviewStatus: "followUp", subOperations: ["开钱箱"] }, "danger");
assert.deepEqual([switched.lens, switched.ruleCode, switched.reviewStatus, switched.subOperations], ["danger", null, "all", []]);
const roundTrip = filtersFromRouteParams(filtersToRouteParams({ ...base, preset: "last7" }, { employeeId: "E-9", employeeName: "Yilia", lens: "abnormal" }));
assert.deepEqual(roundTrip.storeCodes, ["1003", " 1005 ", "1003"].map((code) => code.trim()), "路由参数保留分店");
assert.deepEqual([roundTrip.preset, roundTrip.deviceCode, roundTrip.employeeId, roundTrip.employeeName, roundTrip.lens], ["last7", "POS_1003_1903", "E-9", "Yilia", "abnormal"]);
assert.equal(filtersFromRouteParams({ preset: "bogus", lens: "bogus" }).preset, "today", "未知预设回到今天");
assert.equal(filtersFromRouteParams({ lens: "bogus" }).lens, "all", "未知入口回到全部");
assert.equal(countActiveLegacyFilters(base), 3, "员工、设备、关键字各算一项");

// —— 口径：与后端 LegacyEmployeeLogRiskCatalog 一致 ——
const moduleDir = dirname(fileURLToPath(import.meta.url));
const catalog = readFileSync(
  resolve(moduleDir, "../../../../../services/backend/BlazorApp.Api/Features/LegacyEmployeeLogs/Risk/LegacyEmployeeLogRiskCatalog.cs"),
  "utf8",
);
const constValue = (name: string) => new RegExp(`public const string ${name} = "([^"]+)";`).exec(catalog)?.[1];
const listOf = (name: string) =>
  (new RegExp(`${name} =\\s*\\[([\\s\\S]*?)\\];`).exec(catalog)?.[1] ?? "")
    .split(",")
    .map((item) => item.trim())
    .filter(Boolean)
    .map((item) => constValue(item.replace(/^Rules\./, "")) ?? item);
assert.deepEqual([...LEGACY_DANGER_OPERATIONS].sort(), listOf("DangerOperations").sort(), "危险清单与后端一致");
assert.deepEqual(LEGACY_RULE_CODES, listOf("AllRules"), "规则编号与顺序与后端一致");
assert.equal(isDangerOperation("重打印"), false, "重打印不是危险操作");
assert.equal(operationTone(" 开钱箱 "), "auth");
assert.equal(operationTone("未知操作"), "other");

// —— 异常依据 ——
assert.deepEqual(
  describeFlagEvidence({ ruleCode: "noSaleDrawer", evidence: { previousCheckoutAt: "21:38:10", minutesSincePreviousCheckout: "9", identityConfirmed: "true" } }).map((part) => part.key),
  ["noSaleWindow", "noSalePrevious", "identityConfirmed"],
);
assert.equal(describeFlagEvidence({ ruleCode: "repeatReprint", evidence: { orderId: "01A0F5BF-C743-7437-AAA2-A9E53941770F" } })[0].params?.order, "01A0F5BF…770F");
assert.deepEqual(shortFlagEvidence({ ruleCode: "offHours", evidence: { segment: "beforeOpen", firstAt: "06:48:55" } }), { key: "short.beforeOpen", params: { at: "06:48:55" } });
assert.equal(shortFlagEvidence({ ruleCode: "future", evidence: {} }), null);

// —— 分组、分店摘要、员工排序 ——
const item = (id: string, time: string): LegacyLogItem => normalizeLegacyLogItem({ id, operationTime: time, lastUploadTime: time });
const sections = groupLegacyLogsByHour([item("a", "2026-09-30T21:47:12"), item("b", "2026-09-30T21:01:00"), item("c", "2026-09-30T20:58:21")]);
assert.deepEqual(sections.map((section) => [section.hour, section.data.length]), [["21", 2], ["20", 1]]);
const names = new Map([["1003", "Peninsula Fair"]]);
assert.deepEqual(summarizeStores(["1003"], names), { key: "scope.oneStore", params: { store: "Peninsula Fair" } });
assert.deepEqual(summarizeStores(["1003", "1005"], names), { key: "scope.manyStores", params: { store: "Peninsula Fair", count: "2" } });
assert.deepEqual(summarizeStores([], names), { key: "scope.noStore" });
const people = [
  { employeeName: "A", abnormalCount: 1, pendingReview: 1, dangerCount: 10, total: 100, amountImpact: 50 },
  { employeeName: "B", abnormalCount: 3, pendingReview: 0, dangerCount: 1, total: 100, amountImpact: 5 },
  { employeeName: "C", abnormalCount: 3, pendingReview: 2, dangerCount: 2, total: 10, amountImpact: 20 },
];
assert.deepEqual(orderLegacyEmployees(people, "abnormal").map((row) => row.employeeName), ["C", "B", "A"], "异常数相同看待核查");
assert.deepEqual(orderLegacyEmployees(people, "rate").map((row) => row.employeeName), ["C", "A", "B"]);
assert.deepEqual(orderLegacyEmployees(people, "amount").map((row) => row.employeeName), ["A", "C", "B"]);

// —— 接口归一化 ——
assert.equal(normalizeLegacyLogReview({ result: "ok" }), null, "未知核查结论丢弃");
assert.equal(normalizeLegacyLogReview({ result: "revoked", version: 3 })?.version, 3, "撤销结论保留版本号供再次核查");
const normalized = normalizeLegacyLogItem({ id: "x", isDanger: true, flags: [{ ruleCode: "", evidence: {} }, { ruleCode: "offHours", evidence: null }], amountImpact: 12.5 });
assert.deepEqual([normalized.isDanger, normalized.flags.map((flag) => flag.ruleCode), normalized.amountImpact], [true, ["offHours"], 12.5]);

console.log("legacy employee logs mobile tests passed");
