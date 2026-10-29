import assert from "node:assert/strict";
import {
  buildGridRequest,
  buildSavePayload,
  buildToggledPayload,
  countActiveFilters,
  createDefaultListFilters,
  draftFromDetail,
  normalizeDetail,
  normalizeEvaluate,
  normalizeListPage,
  parseDecimal,
  parsePriority,
  sortNamedOptions,
  sparkSegments,
  summarizeDraftRules,
  summarizePricing,
  validateDraft,
  type StrategyDraft,
} from "./logic";
import { defaultCurve, fixedRules } from "./pricing-curve";
import { makeFixed } from "./pricing-intervals";
import type { PricingStrategyListItem } from "./types";

// ---- grid 请求：与 Web 同一格式 ----
assert.deepEqual(buildGridRequest(createDefaultListFilters()), { StartRow: 0, EndRow: 199, PageSize: 200 });
assert.deepEqual(buildGridRequest({ keyword: "  MNB ", storeCode: "1004", supplierCode: "S1" }), {
  StartRow: 0,
  EndRow: 199,
  PageSize: 200,
  GlobalSearch: "MNB",
  FilterModel: {
    storeCode: { FilterType: "text", Type: "equals", Filter: "1004" },
    supplierCode: { FilterType: "text", Type: "equals", Filter: "S1" },
  },
});
assert.equal(countActiveFilters({ keyword: "x", storeCode: "1", supplierCode: null }), 1);

// ---- 归一化：容忍缺字段、字符串数字、算法大小写 ----
const page = normalizeListPage({
  success: true,
  items: [
    { id: "a", name: "1004 MNB", level: "store", priority: "10", isEnabled: true, storeCodes: ["1004"], supplierCodes: ["MNB"], pricingMode: "Fixed", fixedRate: 2.5, intervals: [{ minPrice: 0.1, maxPrice: 99999, type: "Fixed", rate: 2.5, startPrice: 0.25, endPrice: 249997.5 }] },
    { name: "缺 id 的行被丢弃" },
    { id: "b", name: "旧版", level: "Global", priority: 0, isEnabled: false },
  ],
  total: 3,
});
assert.equal(page.items.length, 2);
assert.equal(page.total, 3);
assert.equal(page.items[0].level, "Store");
assert.equal(page.items[0].priority, 10);
assert.deepEqual(page.items[1].intervals, []);
assert.equal(page.items[1].pricingMode, null);

const detail = normalizeDetail({
  id: "x",
  name: "GFA",
  level: "Supplier",
  priority: 2,
  isEnabled: true,
  details: [
    { minPrice: 5, maxPrice: 20, startRate: 2.625, endRate: 2, algorithm: "linear" },
    { minPrice: 0, maxPrice: 5, startRate: 2.625, endRate: 2.625, algorithm: "Step", startRetailPrice: null },
  ],
  targets: [{ targetType: "Supplier", targetCode: "GFA" }, { targetType: "Store", targetCode: "1004" }],
})!;
assert.deepEqual(detail.details.map((r) => [r.minPrice, r.algorithm]), [
  [0, "Step"],
  [5, "Linear"],
], "区间按下限排序，算法名统一成标准写法");
const draft = draftFromDetail(detail);
assert.deepEqual([draft.storeCodes, draft.supplierCodes, draft.priority], [["1004"], ["GFA"], "2"]);
assert.equal(normalizeDetail({ name: "无 id" }), null);

assert.deepEqual(normalizeEvaluate({ retailPrice: 22.5, rate: 2.5, effectiveRate: 2.5168, strategyId: "s", strategyName: "默认全局" }, 8.94), {
  retailPrice: 22.5,
  effectiveRate: 2.5168,
  strategyId: "s",
  strategyName: "默认全局",
});
assert.equal(normalizeEvaluate({ retailPrice: 25 }, 10).effectiveRate, 2.5, "后端较旧时用 零售价 / 进货价 兜底");
assert.equal(normalizeEvaluate({ retailPrice: 25, strategyId: null }, 10).strategyId, null);

// ---- 卡片摘要 ----
const item = (patch: Partial<PricingStrategyListItem>): PricingStrategyListItem => ({
  id: "i",
  name: "n",
  level: "Global",
  priority: 0,
  isEnabled: true,
  storeCodes: [],
  supplierCodes: [],
  pricingMode: null,
  fixedRate: null,
  startRate: 2.5,
  endRate: 2,
  intervals: [],
  ...patch,
});
assert.deepEqual(summarizePricing(page.items[0]), { kind: "fixed", rate: 2.5 });
const mixedIntervals = [
  { minPrice: 0.1, maxPrice: 5, type: "Fixed" as const, rate: 2.5, startPrice: 0.5, endPrice: 12.5 },
  { minPrice: 5, maxPrice: 20, type: "Curve" as const, rate: null, startPrice: 12.5, endPrice: 45.99 },
];
assert.equal(summarizePricing(item({ pricingMode: "Mixed", intervals: mixedIntervals })).kind, "mixed");
assert.equal(summarizePricing(item({ pricingMode: "Legacy", intervals: mixedIntervals })).kind, "legacy");
assert.equal(summarizePricing(item({ pricingMode: "Curve", intervals: [mixedIntervals[1]] })).kind, "curve");
assert.equal(summarizePricing(item({ pricingMode: "Empty" })).kind, "empty");
const spark = sparkSegments(mixedIntervals, 88, 30);
assert.equal(spark.length, 2);
assert.ok(spark.every((segment) => segment.points.every((p) => p.x >= 0 && p.x <= 88 && p.y >= 0 && p.y <= 30)), "缩略图点都在画布内");
assert.ok(spark[1].points[1].y < spark[0].points[0].y, "售价越高越靠上");
assert.deepEqual(sparkSegments([], 88, 30), []);

// ---- 表单校验与保存载荷 ----
const base: StrategyDraft = { name: "测试", level: "Global", priority: "0", isEnabled: true, storeCodes: [], supplierCodes: [], rules: defaultCurve() };
assert.equal(validateDraft(base), null);
assert.deepEqual(validateDraft({ ...base, name: "  " }), { scope: "draft", code: "nameRequired" });
assert.deepEqual(validateDraft({ ...base, priority: "-1" }), { scope: "draft", code: "priorityInvalid" });
assert.deepEqual(validateDraft({ ...base, level: "Store" }), { scope: "draft", code: "storeRequired" });
assert.deepEqual(validateDraft({ ...base, level: "Supplier" }), { scope: "draft", code: "supplierRequired" });
assert.deepEqual(validateDraft({ ...base, rules: detail.details }), { scope: "draft", code: "legacyNotConverted" }, "旧版规则要先转换");
assert.deepEqual(validateDraft({ ...base, rules: [makeFixed(0.1, 10, 3), makeFixed(11, 50, 2.5)] }), { scope: "rules", issue: { code: "notContiguous", params: { n: 2 } } });
assert.equal(parsePriority(""), 0);
assert.equal(parsePriority(" 12 "), 12);
assert.equal(parsePriority("1.5"), null);

const payload = buildSavePayload({ ...base, name: " 1004 MNB ", level: "Store", priority: "10", storeCodes: ["1004"], supplierCodes: ["MNB"], rules: fixedRules(2.5) });
assert.deepEqual(payload, {
  name: "1004 MNB",
  level: "Store",
  priority: 10,
  isEnabled: true,
  details: [{ minPrice: 0.1, maxPrice: 99999, startRetailPrice: null, endRetailPrice: null, curveBend: null, startRate: 2.5, endRate: 2.5, algorithm: "Fixed" }],
  targets: [
    { targetType: "Store", targetCode: "1004" },
    { targetType: "Supplier", targetCode: "MNB" },
  ],
});
const toggled = buildToggledPayload(detail, false);
assert.equal(toggled.isEnabled, false);
assert.deepEqual(toggled.details, detail.details, "启停只改 isEnabled，区间原样提交");
assert.deepEqual(summarizeDraftRules(defaultCurve()), { total: 3, fixedCount: 0, curveCount: 3, drops: 0 });
assert.equal(summarizeDraftRules([makeFixed(0.1, 10, 3), makeFixed(10, 50, 2.5)]).drops, 1);

// ---- 数字输入 ----
assert.equal(parseDecimal("2.5"), 2.5);
assert.equal(parseDecimal(" 2,75 "), 2.75, "逗号当小数点");
assert.equal(parseDecimal(".5"), 0.5);
assert.ok(Number.isNaN(parseDecimal("")));
assert.ok(Number.isNaN(parseDecimal(".")));
assert.ok(Number.isNaN(parseDecimal("不限")));
assert.ok(Number.isNaN(parseDecimal("1.2.3")));

// ---- 分店 / 供应商下拉按名字排序 ----
const scopeOptions = [
  { code: "SP10", name: "Zed" },
  { code: "SP2", name: "alpha" },
  { code: "SP3", name: "" },
  { code: "SP1", name: "Beta 10" },
  { code: "SP4", name: "Beta 2" },
];
assert.deepEqual(
  sortNamedOptions(scopeOptions, "en").map((option) => option.code),
  ["SP2", "SP4", "SP1", "SP3", "SP10"],
  "按名字排（忽略大小写、数字按数值），名字为空退回编号",
);
assert.equal(scopeOptions[0].code, "SP10", "不修改入参");
assert.deepEqual(
  sortNamedOptions([{ code: "B", name: "张三" }, { code: "A", name: "阿里" }, { code: "C", name: "李四" }], "zh-CN").map((option) => option.code),
  ["A", "C", "B"],
  "中文界面按拼音排序",
);

console.log("pricing-strategies logic.test.ts: ok");
