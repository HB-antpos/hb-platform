import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import {
  LEGACY_DANGER_OPERATIONS,
  LEGACY_RULE_CODES,
  POS_DANGER_GROUPS,
  POS_RULE_CODES,
  categoryGroupsFor,
  posOperationKey,
  resolveLogSource,
  resolvePosRange,
  toLocalWallClock,
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
  productThumbnailUri,
  resolveLegacyRange,
  shortFlagEvidence,
  summarizeStores,
  switchLens,
} from "./logic";
import { normalizeLegacyLogItem, normalizeLegacyLogReview, normalizePosEmployeeSummary, normalizePosLogItem } from "./api-normalization";
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

// —— 新收银：来源解析、查询参数、归一化与口径契约 ——
assert.equal(resolveLogSource({ canLegacy: true, canPos: true }), "legacy", "首次默认老收银");
assert.equal(resolveLogSource({ canLegacy: true, canPos: true, remembered: "pos" }), "pos", "记住上次的选择");
assert.equal(resolveLogSource({ canLegacy: true, canPos: true, requested: "legacy", remembered: "pos" }), "legacy", "路由参数优先");
assert.equal(resolveLogSource({ canLegacy: false, canPos: true, requested: "legacy" }), "pos", "无权限的来源不能通过参数进入");
assert.equal(resolveLogSource({ canLegacy: false, canPos: false }), null, "两个都没权限");

const posRange = resolvePosRange("today", now);
assert.equal(posRange.fromUtc, new Date(2026, 8, 30).toISOString(), "新收银区间按设备本地零点换算 UTC");
assert.equal(posRange.toUtc, new Date(2026, 9, 1).toISOString());
const posFilters = { ...createDefaultLegacyLogFilters(["1013", "1013", "1042"], "pos"), employeeId: "c1", lens: "danger" as const, subOperations: ["CART_ITEM_REMOVE", "CART_CLEAR"] };
const posQuery = buildLegacyLogQuery(posFilters, 2, now)!;
assert.deepEqual(posQuery.getAll("storeCodes"), ["1013", "1042"]);
assert.deepEqual([posQuery.get("cashierId"), posQuery.get("riskLens"), posQuery.get("pageNumber"), posQuery.get("sortBy")], ["c1", "danger", "2", "occurredAtUtc"]);
assert.deepEqual(posQuery.getAll("operationTypes"), ["CART_ITEM_REMOVE", "CART_CLEAR"], "危险细分按操作类型传");
assert.equal(posQuery.has("from") || posQuery.has("employeeIds") || posQuery.has("operations"), false, "不带老收银参数");
const posAbnormal = buildLegacyLogQuery({ ...posFilters, lens: "abnormal", ruleCode: "emergencyOverride", reviewStatus: "pending" }, 1, now)!;
assert.deepEqual([posAbnormal.getAll("operationTypes"), posAbnormal.get("ruleCodes"), posAbnormal.get("reviewStatus")], [[], "emergencyOverride", "pending"]);
const posSummary = buildLegacyEmployeeSummaryQuery(posFilters, now)!;
assert.deepEqual([posSummary.has("fromUtc"), posSummary.has("cashierId"), posSummary.has("pageNumber")], [true, false, false], "员工汇总只带基础条件");
assert.equal(filtersFromRouteParams(filtersToRouteParams(posFilters), "legacy").source, "legacy", "来源由页面解析后传入，路由参数只做记录");
assert.equal(filtersToRouteParams(posFilters).source, "pos");
assert.deepEqual(categoryGroupsFor("pos"), [], "新收银「全部」入口没有类别细分");
assert.equal(posOperationKey("CART_ITEM_PRICE_CHANGE"), "cartItemPriceChange");

const wall = toLocalWallClock("2026-10-01T02:00:00.000Z");
const expected = new Date(Date.UTC(2026, 9, 1, 2, 0, 0));
assert.equal(wall, `${expected.getFullYear()}-${String(expected.getMonth() + 1).padStart(2, "0")}-${String(expected.getDate()).padStart(2, "0")}T${String(expected.getHours()).padStart(2, "0")}:00:00`, "UTC 按设备时区转墙钟");
assert.equal(toLocalWallClock("bogus"), "", "无法解析的时间返回空串");

const posItem = normalizePosLogItem(
  {
    eventId: "11111111-2222-3333-4444-555555555555",
    operationType: "CART_ITEM_REMOVE",
    outcome: "Succeeded",
    cashierId: "c1",
    cashierName: "Gao Jian",
    storeCode: "1013",
    deviceCode: "POS_1013_0222",
    occurredAtUtc: "2026-10-01T02:00:00Z",
    receivedAtUtc: "2026-10-01T02:00:05Z",
    primaryProduct: "Big Bear",
    productCount: 3,
    beforeActual: 30,
    afterActual: 15,
    isDanger: true,
    amountImpact: 15,
    flags: [{ ruleCode: "deleteAfterCheckout", evidence: { anchor: "tender" } }],
    review: { result: "followUp", version: 2, reviewedByName: "M" },
  },
  (type) => `L:${type}`,
);
assert.deepEqual(
  [posItem.id, posItem.source, posItem.tone, posItem.operation, posItem.title, posItem.employeeId, posItem.amountImpact, posItem.review?.version],
  ["11111111-2222-3333-4444-555555555555", "pos", "delete", "L:CART_ITEM_REMOVE", "Big Bear +2", "c1", 15, 2],
);
assert.equal(posItem.operationDetail, "Big Bear +2 · 30.00 → 15.00", "时间线摘要含商品与金额变化");
assert.equal(posItem.pos?.outcome, "Succeeded");
const withItemNumber = normalizePosLogItem(
  { eventId: "e3", operationType: "CART_ITEM_REMOVE", primaryProduct: "Halloween Napkins", primaryItemNumber: "XH0001640", productCount: 2 },
  (type) => `L:${type}`,
);
assert.deepEqual(
  [withItemNumber.title, withItemNumber.itemNumber, withItemNumber.hasProduct, withItemNumber.productImage],
  ["Halloween Napkins +1", "XH0001640", true, null],
  "卡片标题不含货号（货号单独显示，避免长名称截断后看不到）",
);
assert.equal(withItemNumber.operationDetail, "Halloween Napkins (XH0001640) +1", "时间线一行文字里货号紧跟主商品名，剩余数量放最后");
const withImage = normalizePosLogItem(
  { eventId: "e5", operationType: "CART_ITEM_REMOVE", primaryProduct: "Napkins", productCount: 1, primaryProductImage: " https://hb-sales-2019-1300114625.cos.ap-singapore.myqcloud.com/257/XH0001640.jpg " },
  (type) => `L:${type}`,
);
assert.equal(withImage.productImage, "https://hb-sales-2019-1300114625.cos.ap-singapore.myqcloud.com/257/XH0001640.jpg", "主档图片地址去除空白");
assert.equal(
  productThumbnailUri(withImage.productImage, 80),
  "https://hb-sales-2019-1300114625.cos.ap-singapore.myqcloud.com/257/XH0001640.jpg?imageMogr2/thumbnail/80x80/format/webp",
  "COS 图片走缩略图",
);
assert.equal(productThumbnailUri("https://example.com/a.jpg", 80), "https://example.com/a.jpg", "非 COS 图片原样返回");
assert.equal(productThumbnailUri("https://x-1.cos.ap-singapore.myqcloud.com/a.jpg?v=1", 80), "https://x-1.cos.ap-singapore.myqcloud.com/a.jpg?v=1", "已带查询参数的不追加");
assert.equal(productThumbnailUri(null, 80), null, "没有图片返回 null");
const sameAsName = normalizePosLogItem(
  { eventId: "e4", operationType: "CART_ITEM_REMOVE", primaryProduct: "XH0001640", primaryItemNumber: "XH0001640", productCount: 1 },
  (type) => `L:${type}`,
);
assert.deepEqual([sameAsName.title, sameAsName.itemNumber], ["XH0001640", null], "商品名与货号相同时不重复显示");
assert.equal(drawerHasNoProduct(), true, "没有商品的操作不显示缩略图");
function drawerHasNoProduct() {
  return normalizePosLogItem({ eventId: "e6", operationType: "CASH_DRAWER_OPEN" }, (type) => type).hasProduct === false;
}
const drawer = normalizePosLogItem({ eventId: "e2", operationType: "CASH_DRAWER_OPEN", reasonCode: "MANUAL" }, (type) => `L:${type}`);
assert.deepEqual([drawer.title, drawer.operationDetail, drawer.tone], ["L:CASH_DRAWER_OPEN", "MANUAL", "auth"], "没有商品时标题用操作名");
assert.equal(normalizePosEmployeeSummary({ cashierId: "c9", total: 5 }).employeeName, "c9", "没有姓名时用收银员编号");

// 契约：新收银规则编号与危险分组与后端 PosOperationAuditRiskCatalog 一致。
const posCatalog = readFileSync(
  resolve(moduleDir, "../../../../../services/backend/BlazorApp.Api/Services/OperationAudits/Risk/PosOperationAuditRiskCatalog.cs"),
  "utf8",
);
const posConst = (name: string) => {
  const literal = new RegExp(`public const string ${name} = "([^"]+)";`).exec(posCatalog)?.[1];
  if (literal) return literal;
  const legacyName = new RegExp(`public const string ${name} = LegacyRules\\.(\\w+);`).exec(posCatalog)?.[1];
  return legacyName ? constValue(legacyName) : undefined;
};
const posListOf = (name: string) =>
  (new RegExp(`${name} =\\s*\\[([\\s\\S]*?)\\];`).exec(posCatalog)?.[1] ?? "")
    .split(",")
    .map((item) => item.trim())
    .filter(Boolean)
    .map((item) => (item.startsWith('"') ? item.slice(1, -1) : posConst(item.replace(/^Rules\./, "")) ?? item));
assert.deepEqual(POS_RULE_CODES, posListOf("AllRules"), "新收银规则编号与后端一致");
assert.deepEqual(
  POS_DANGER_GROUPS.flatMap((group) => group.operations).sort(),
  [...posListOf("DangerOperations"), "CASH_DRAWER_OPEN"].sort(),
  "新收银危险分组并集 = 后端危险清单 + 手动开钱箱",
);

// 新收银依据：没有身份确认字段时不显示、紧急覆盖有短依据。
assert.deepEqual(
  describeFlagEvidence({ ruleCode: "noSaleDrawer", evidence: { windowSeconds: "120" } }).map((part) => part.key),
  ["noSaleWindow"],
);
assert.equal(describeFlagEvidence({ ruleCode: "deleteAfterCheckout", evidence: { anchor: "tender" } })[0].key, "deleteAfterTender");
assert.equal(shortFlagEvidence({ ruleCode: "emergencyOverride", evidence: { outcome: "Denied" } })?.key, "short.emergencyOverride");

console.log("legacy employee logs mobile tests passed");
