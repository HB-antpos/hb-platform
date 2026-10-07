import assert from "node:assert/strict";
import {
  DAILY_CLOSE_MAX_RANGE_DAYS,
  buildDailyCloseListParams,
  canPickDailyCloseDevice,
  classifyDailyCloseError,
  classifyDifference,
  clearDailyCloseStores,
  clientKindLabelKey,
  collectDeviceCodes,
  countActiveDailyCloseFilters,
  createDefaultDailyCloseFilters,
  describeBusinessDate,
  formatDailyCloseCount,
  formatDailyCloseDifference,
  formatDailyCloseMoney,
  formatDailyCloseRangeDates,
  formatDenomination,
  formatSavedLabel,
  formatZonedDateTime,
  groupDailyClosesByBusinessDate,
  hasCashCountData,
  hasCashReconciliation,
  hasTenderData,
  isBackfilledRecord,
  mergeDailyClosePages,
  nextDailyClosePage,
  orderTenders,
  resolveBackfillNotice,
  resolveDailyCloseRange,
  resolveSaveLogLink,
  saveSequenceMark,
  selectDailyClosePreset,
  splitCashCounts,
  statusTabCounts,
  sumTenders,
  summarizeDailyCloseStores,
  summarizeDailyCloseTotals,
  toCents,
  toZonedMoment,
  todayInSydney,
  toggleDailyCloseStore,
  validateDailyCloseFilters,
  validateDailyCloseRange,
} from "./logic";
import type { DailyCloseCashCount, DailyCloseListItem, DailyCloseTender } from "./types";

const MINUS = "−";

function item(overrides: Partial<DailyCloseListItem> = {}): DailyCloseListItem {
  return {
    dailyCloseGuid: "00000000-0000-0000-0000-000000000001",
    storeCode: "1008",
    storeName: "Bankstown",
    storeTimeZoneId: "Australia/Sydney",
    deviceCode: "POS01",
    clientKind: "Wpf",
    detailLevel: "Full",
    dataSource: "ClientUpload",
    businessDate: "2026-10-06",
    businessDateInferred: false,
    cashierId: "E1",
    cashierName: "Mei Lin",
    savedAtUtc: "2026-10-06T11:48:00Z",
    orderCount: 142,
    expectedCashAmount: 1842.3,
    countedCashAmount: 1838.8,
    cashDifference: -3.5,
    cardNetAmount: 5206.15,
    differenceKind: "short",
    saveSequence: 1,
    saveCountInDay: 1,
    ...overrides,
  };
}

// —— 金额：按分取整、差额分类、格式化 ——
assert.equal(toCents(0.1 + 0.2), 30, "浮点误差按分取整");
assert.equal(toCents(null), 0);
assert.equal(toCents(Number.NaN), 0);
assert.equal(classifyDifference(-3.5), "short");
assert.equal(classifyDifference(2), "over");
assert.equal(classifyDifference(0), "even");
assert.equal(classifyDifference(-0.004), "even", "亚分值按已平处理，不显示 −$0.00");
assert.equal(classifyDifference(null), "none");
assert.equal(classifyDifference(undefined), "none");
assert.equal(classifyDifference(Number.NaN), "none");

assert.equal(formatDailyCloseMoney(1842.3), "$1,842.30");
assert.equal(formatDailyCloseMoney(-3.5), `${MINUS}$3.50`, "负数用 U+2212 减号");
assert.equal(formatDailyCloseMoney(0), "$0.00");
assert.equal(formatDailyCloseMoney(-0.001), "$0.00", "不出现 −$0.00");
assert.equal(formatDailyCloseMoney(1234567.8), "$1,234,567.80");
assert.equal(formatDailyCloseMoney(null), "—");
assert.equal(formatDailyCloseMoney(undefined), "—");
assert.equal(formatDailyCloseDifference(2), "+$2.00");
assert.equal(formatDailyCloseDifference(-3.5), `${MINUS}$3.50`);
assert.equal(formatDailyCloseDifference(0), "$0.00", "已平不带符号");
assert.equal(formatDailyCloseDifference(0.004), "$0.00");
assert.equal(formatDailyCloseDifference(null), "—");
assert.equal(formatDailyCloseCount(142), "142");
assert.equal(formatDailyCloseCount(1234), "1,234");
assert.equal(formatDailyCloseCount(null), "—");
assert.equal(formatDenomination(10000), "$100");
assert.equal(formatDenomination(500), "$5");
assert.equal(formatDenomination(200), "$2");
assert.equal(formatDenomination(100), "$1");
assert.equal(formatDenomination(150), "$1.50");
assert.equal(formatDenomination(50), "50c");
assert.equal(formatDenomination(5), "5c");
assert.equal(formatDenomination(Number.NaN), "—");

// —— 时区：保存时间按门店时区显示，缺失用悉尼 ——
// 2026-10-07 起悉尼进入夏令时（UTC+11）；布里斯班不用夏令时（UTC+10）
assert.deepEqual(toZonedMoment("2026-10-06T11:48:00Z", "Australia/Sydney"), { date: "2026-10-06", time: "22:48" }, "10 月 6 日仍是标准时间 UTC+10");
assert.deepEqual(toZonedMoment("2026-10-07T04:30:00Z", "Australia/Sydney"), { date: "2026-10-07", time: "15:30" }, "10 月 4 日起夏令时 UTC+11");
assert.deepEqual(toZonedMoment("2026-10-07T04:30:00Z", "Australia/Brisbane"), { date: "2026-10-07", time: "14:30" });
assert.deepEqual(toZonedMoment("2026-07-01T12:00:00Z", "Australia/Sydney"), { date: "2026-07-01", time: "22:00" }, "冬令时 UTC+10");
assert.deepEqual(toZonedMoment("2026-10-07T20:00:00Z", "Australia/Sydney"), { date: "2026-10-08", time: "07:00" }, "跨日");
assert.deepEqual(toZonedMoment("2026-10-07T04:30:00Z", null), { date: "2026-10-07", time: "15:30" }, "时区缺失回退悉尼");
assert.deepEqual(toZonedMoment("2026-10-07T04:30:00Z", "  "), { date: "2026-10-07", time: "15:30" }, "空白时区回退悉尼");
assert.deepEqual(toZonedMoment("2026-10-07T04:30:00Z", "Not/AZone"), { date: "2026-10-07", time: "15:30" }, "无效时区回退悉尼");
assert.equal(toZonedMoment(null, "Australia/Sydney"), null);
assert.equal(toZonedMoment("", "Australia/Sydney"), null);
assert.equal(toZonedMoment("not-a-date", "Australia/Sydney"), null);
// 「今天」按悉尼日期
assert.equal(todayInSydney(new Date("2026-10-06T12:59:00Z")), "2026-10-06", "悉尼 23:59");
assert.equal(todayInSydney(new Date("2026-10-06T13:00:00Z")), "2026-10-07", "悉尼 00:00（夏令时）");
// 保存标签：与营业日同一天只显示时分，跨天带上月日
assert.equal(formatSavedLabel("2026-10-06T11:48:00Z", "Australia/Sydney", "2026-10-06"), "22:48");
assert.equal(formatSavedLabel("2026-10-06T14:12:00Z", "Australia/Sydney", "2026-10-06"), "10-07 01:12", "过零点保存");
assert.equal(formatSavedLabel("bad", "Australia/Sydney", "2026-10-06"), "—");
assert.equal(formatZonedDateTime("2026-10-06T11:48:00Z", null), "2026-10-06 22:48");
assert.equal(formatZonedDateTime(null, null), "—");

// —— 营业日：拆分年月日与星期 ——
assert.deepEqual(describeBusinessDate("2026-10-06"), { year: 2026, month: 10, day: 6, weekday: 2 }, "2026-10-06 是周二");
assert.equal(describeBusinessDate("2026-10-04")?.weekday, 0, "周日");
assert.equal(describeBusinessDate("2026-10-10")?.weekday, 6, "周六");
assert.equal(describeBusinessDate("2026-02-30"), null, "无效日期");
assert.equal(describeBusinessDate(""), null);
assert.equal(describeBusinessDate("abc"), null);

// —— 筛选：默认值、预设区间 ——
const defaults = createDefaultDailyCloseFilters();
assert.deepEqual(defaults, {
  storeCodes: [],
  preset: "last7",
  customFrom: "",
  customTo: "",
  clientKind: null,
  deviceCode: null,
  keyword: "",
  status: "all",
});
const today = "2026-10-07";
assert.deepEqual(resolveDailyCloseRange({ ...defaults, preset: "today" }, today), { from: "2026-10-07", to: "2026-10-07" });
assert.deepEqual(resolveDailyCloseRange({ ...defaults, preset: "yesterday" }, today), { from: "2026-10-06", to: "2026-10-06" });
assert.deepEqual(resolveDailyCloseRange({ ...defaults, preset: "last7" }, today), { from: "2026-10-01", to: "2026-10-07" }, "近 7 天含今天");
assert.deepEqual(resolveDailyCloseRange({ ...defaults, preset: "last7" }, "2026-03-02"), { from: "2026-02-24", to: "2026-03-02" }, "跨月");
assert.deepEqual(resolveDailyCloseRange({ ...defaults, preset: "thisMonth" }, today), { from: "2026-10-01", to: "2026-10-07" });
assert.deepEqual(resolveDailyCloseRange({ ...defaults, preset: "thisMonth" }, "2026-10-01"), { from: "2026-10-01", to: "2026-10-01" }, "1 号只有一天");
assert.deepEqual(resolveDailyCloseRange({ ...defaults, preset: "yesterday" }, "2026-10-01"), { from: "2026-09-30", to: "2026-09-30" }, "昨天跨月");
assert.deepEqual(resolveDailyCloseRange({ ...defaults, preset: "custom", customFrom: "2026-09-01", customTo: "2026-09-30" }, today), { from: "2026-09-01", to: "2026-09-30" });

// 筛选 chip 上的区间：同年省略年份，跨年带上
assert.deepEqual(formatDailyCloseRangeDates({ from: "2026-09-22", to: "2026-10-04" }, today), { from: "09-22", to: "10-04" });
assert.deepEqual(formatDailyCloseRangeDates({ from: "2025-12-20", to: "2026-01-05" }, today), { from: "2025-12-20", to: "01-05" }, "跨年的一端保留年份");
assert.deepEqual(formatDailyCloseRangeDates({ from: "", to: "2026-10-04" }, today), { from: "", to: "10-04" });

// —— 区间校验：最长 93 天（含首尾） ——
assert.equal(DAILY_CLOSE_MAX_RANGE_DAYS, 93);
// 2026-07-07 到 2026-10-07 含首尾恰好 93 天（相差 92 天）；再早一天就是 94 天
assert.equal(validateDailyCloseRange({ from: "2026-07-07", to: "2026-10-07" }), null, "恰好 93 天合法");
assert.equal(validateDailyCloseRange({ from: "2026-07-06", to: "2026-10-07" }), "tooLong", "94 天超限");
assert.equal(validateDailyCloseRange({ from: "2026-10-08", to: "2026-10-07" }), "reversed");
assert.equal(validateDailyCloseRange({ from: "2026-10-07", to: "2026-10-07" }), null, "单日合法");
assert.equal(validateDailyCloseRange({ from: "", to: "2026-10-07" }), "invalid");
assert.equal(validateDailyCloseRange({ from: "2026-02-30", to: "2026-10-07" }), "invalid");
assert.equal(validateDailyCloseFilters(defaults, today), null, "预设恒合法");
assert.equal(validateDailyCloseFilters({ ...defaults, preset: "custom", customFrom: "2026-01-01", customTo: "2026-10-07" }, today), "tooLong");
assert.equal(validateDailyCloseFilters({ ...defaults, preset: "custom" }, today), "invalid", "自定义没填日期");

// —— 查询参数 ——
assert.deepEqual(buildDailyCloseListParams(defaults, 1, 30, today), {
  businessDateFrom: "2026-10-01",
  businessDateTo: "2026-10-07",
  page: 1,
  pageSize: 30,
});
const full = buildDailyCloseListParams(
  { ...defaults, storeCodes: [" 1008 ", "1015", "1008", ""], clientKind: "Handheld", keyword: "  Mei  ", status: "short" },
  3,
  30,
  today,
)!;
assert.equal(full.storeCodes, "1008,1015", "分店去空白去重，逗号分隔");
assert.equal(full.clientKind, "Handheld");
assert.equal(full.keyword, "Mei");
assert.equal(full.status, "short");
assert.equal(full.page, 3);
assert.equal("deviceCode" in full, false, "多店时不带终端");
assert.equal(buildDailyCloseListParams({ ...defaults, deviceCode: "POS01" }, 1, 30, today)!.deviceCode, undefined, "全部分店时不带终端");
assert.equal(buildDailyCloseListParams({ ...defaults, storeCodes: ["1008"], deviceCode: " POS01 " }, 1, 30, today)!.deviceCode, "POS01", "单店时带终端");
assert.equal("status" in buildDailyCloseListParams({ ...defaults, status: "all" }, 1, 30, today)!, false, "全部不传 status");
assert.equal(buildDailyCloseListParams({ ...defaults, keyword: "   " }, 1, 30, today)!.keyword, undefined, "空白关键字不传");
assert.equal(buildDailyCloseListParams({ ...defaults, keyword: "x".repeat(150) }, 1, 30, today)!.keyword?.toString().length, 100, "关键字截断到 100");
assert.equal(buildDailyCloseListParams({ ...defaults, preset: "custom", customFrom: "2026-01-01", customTo: "2026-10-07" }, 1, 30, today), null, "区间超限不发请求");
assert.equal(buildDailyCloseListParams({ ...defaults, preset: "custom" }, 1, 30, today), null, "自定义未填不发请求");

// —— 已生效筛选数（角标） ——
assert.equal(countActiveDailyCloseFilters(defaults), 0);
assert.equal(countActiveDailyCloseFilters({ ...defaults, status: "short" }), 0, "状态页签不算筛选项");
assert.equal(countActiveDailyCloseFilters({ ...defaults, storeCodes: ["1008", "1015"] }), 1, "多家分店算一项");
assert.equal(countActiveDailyCloseFilters({ ...defaults, storeCodes: [" ", ""] }), 0, "空白分店不算");
assert.equal(countActiveDailyCloseFilters({ ...defaults, preset: "today" }), 1);
assert.equal(
  countActiveDailyCloseFilters({ ...defaults, storeCodes: ["1008"], preset: "custom", clientKind: "Ipad", deviceCode: "POS01", keyword: "a" }),
  5,
  "分店、营业日、来源、终端、收银员各一项",
);

// —— 分店与终端联动 ——
const withDevice = { ...defaults, storeCodes: ["1008"], deviceCode: "POS01" };
assert.deepEqual(toggleDailyCloseStore(withDevice, "1015").storeCodes, ["1008", "1015"]);
assert.equal(toggleDailyCloseStore(withDevice, "1015").deviceCode, null, "分店变化后清空终端");
assert.deepEqual(toggleDailyCloseStore(withDevice, "1008").storeCodes, [], "再点一次取消");
assert.equal(toggleDailyCloseStore(withDevice, "  "), withDevice, "空编号忽略");
assert.deepEqual(clearDailyCloseStores(withDevice), { ...withDevice, storeCodes: [], deviceCode: null });
assert.equal(canPickDailyCloseDevice({ storeCodes: ["1008"] }), true);
assert.equal(canPickDailyCloseDevice({ storeCodes: [] }), false);
assert.equal(canPickDailyCloseDevice({ storeCodes: ["1008", "1015"] }), false);
assert.equal(canPickDailyCloseDevice({ storeCodes: ["1008", "1008"] }), true, "重复编号按一家算");

// —— 预设切换：选自定义时以当前区间为初值 ——
const toCustom = selectDailyClosePreset(defaults, "custom", today);
assert.deepEqual([toCustom.preset, toCustom.customFrom, toCustom.customTo], ["custom", "2026-10-01", "2026-10-07"]);
const editedCustom = { ...toCustom, customFrom: "2026-09-20" };
assert.deepEqual(selectDailyClosePreset(editedCustom, "custom", today), editedCustom, "已在自定义时原样保留");
assert.equal(selectDailyClosePreset(editedCustom, "custom", today).customFrom, "2026-09-20", "已在自定义时不重置日期");
assert.equal(selectDailyClosePreset(editedCustom, "today", today).preset, "today");
assert.deepEqual(
  (({ customFrom, customTo }) => [customFrom, customTo])(selectDailyClosePreset({ ...defaults, preset: "thisMonth" }, "custom", "2026-10-15")),
  ["2026-10-01", "2026-10-15"],
);

// —— 分店摘要 ——
const names = new Map([["1008", "Bankstown"], ["1015", "Greenhills"]]);
assert.deepEqual(summarizeDailyCloseStores([], names), { key: "scope.allStores" });
assert.deepEqual(summarizeDailyCloseStores(["1008"], names), { key: "scope.oneStore", params: { store: "Bankstown" } });
assert.deepEqual(summarizeDailyCloseStores(["9999"], names), { key: "scope.oneStore", params: { store: "9999" } }, "没有名称回退编号");
assert.deepEqual(summarizeDailyCloseStores(["1015", "1008", "1015"], names), { key: "scope.manyStores", params: { store: "Greenhills", count: "2" } }, "去重后计数");

// —— 终端选项 ——
assert.deepEqual(
  collectDeviceCodes(
    [
      { storeCode: "1008", deviceCode: "POS10" },
      { storeCode: "1008", deviceCode: "POS2" },
      { storeCode: "1008", deviceCode: "POS2" },
      { storeCode: "1015", deviceCode: "POS01" },
      { storeCode: "1008", deviceCode: "  " },
    ],
    "1008",
  ),
  ["POS2", "POS10"],
  "只取该店、去重、自然序",
);
assert.deepEqual(collectDeviceCodes([], "1008", "POS07"), ["POS07"], "当前已选的终端始终在选项里");
assert.deepEqual(collectDeviceCodes([{ storeCode: "1008", deviceCode: "POS01" }], "1008", "POS01"), ["POS01"]);

// —— 状态页签与摘要卡 ——
const counts = { all: 58, short: 9, over: 4, even: 43, none: 2 };
assert.deepEqual(statusTabCounts(counts), { all: 58, short: 9, over: 4, even: 43 }, "无金额只在全部里");
const totals = { expectedCash: 71356.2, countedCash: 71314.7, difference: -41.5 };
assert.deepEqual(summarizeDailyCloseTotals(counts, totals, "all"), {
  hasAmounts: true,
  kind: "short",
  difference: -41.5,
  expectedCash: 71356.2,
  countedCash: 71314.7,
  noAmountCount: 2,
});
assert.equal(summarizeDailyCloseTotals(counts, totals, "short").noAmountCount, 0, "非全部页签不提示无金额");
assert.equal(summarizeDailyCloseTotals({ ...counts, short: 0, over: 3, even: 0 }, { ...totals, difference: 12 }, "all").kind, "over");
assert.equal(summarizeDailyCloseTotals({ ...counts, short: 0, over: 0, even: 5 }, { expectedCash: 100, countedCash: 100, difference: 0 }, "all").kind, "even");
const noAmounts = summarizeDailyCloseTotals({ all: 2, short: 0, over: 0, even: 0, none: 2 }, { expectedCash: 0, countedCash: 0, difference: 0 }, "all");
assert.equal(noAmounts.hasAmounts, false, "只有无金额记录时不显示 $0.00 合计");
assert.equal(noAmounts.kind, "none");
assert.equal(summarizeDailyCloseTotals(counts, totals, "over").hasAmounts, true);
assert.equal(summarizeDailyCloseTotals({ ...counts, over: 0 }, totals, "over").hasAmounts, false, "该状态没有记录");

// —— 分组：按营业日、份数、差额合计（按分求和） ——
const rows = [
  item({ dailyCloseGuid: "a", businessDate: "2026-10-06", cashDifference: 0.1 }),
  item({ dailyCloseGuid: "b", businessDate: "2026-10-06", cashDifference: 0.2 }),
  item({ dailyCloseGuid: "c", businessDate: "2026-10-06", cashDifference: null, differenceKind: "none" }),
  item({ dailyCloseGuid: "d", businessDate: "2026-10-05", cashDifference: null, differenceKind: "none" }),
  item({ dailyCloseGuid: "e", businessDate: "2026-10-04", cashDifference: -3.5 }),
];
const sections = groupDailyClosesByBusinessDate(rows, true);
assert.deepEqual(sections.map((section) => section.businessDate), ["2026-10-06", "2026-10-05", "2026-10-04"], "保持接口的营业日降序");
assert.deepEqual(sections.map((section) => section.count), [3, 1, 1]);
assert.deepEqual(sections.map((section) => section.amountCount), [2, 0, 1], "无金额不计入带金额数");
assert.equal(sections[0].difference, 0.3, "0.1 + 0.2 按分求和等于 0.30");
assert.equal(sections[1].difference, null, "整组都没有金额时没有差额合计");
assert.equal(sections[2].difference, -3.5);
assert.deepEqual(sections.map((section) => section.partial), [false, false, false], "已全部加载：没有不完整的组");
assert.deepEqual(groupDailyClosesByBusinessDate(rows, false).map((section) => section.partial), [false, false, true], "还有下一页：最后一组标记不完整");
assert.deepEqual(groupDailyClosesByBusinessDate([], false), []);
// 营业日不连续出现（不应发生）时仍合并成一组，避免 SectionList 出现重复键
const scattered = groupDailyClosesByBusinessDate([item({ dailyCloseGuid: "a", businessDate: "2026-10-06" }), item({ dailyCloseGuid: "b", businessDate: "2026-10-05" }), item({ dailyCloseGuid: "c", businessDate: "2026-10-06" })], true);
assert.deepEqual(scattered.map((section) => [section.key, section.count]), [["2026-10-06", 2], ["2026-10-05", 1]]);

// —— 分页合并与翻页判定 ——
assert.deepEqual(
  mergeDailyClosePages([{ items: [item({ dailyCloseGuid: "a" }), item({ dailyCloseGuid: "b" })] }, { items: [item({ dailyCloseGuid: "b" }), item({ dailyCloseGuid: "c" })] }]).map((row) => row.dailyCloseGuid),
  ["a", "b", "c"],
  "相邻页重复按日结编号去重",
);
assert.equal(nextDailyClosePage({ items: [item()], total: 60, page: 1 }, 30), 2);
assert.equal(nextDailyClosePage({ items: [item()], total: 30, page: 1 }, 30), undefined, "已加载完");
assert.equal(nextDailyClosePage({ items: [], total: 60, page: 3 }, 30), undefined, "空页即使总数没到也停止，防止无限翻页");
assert.equal(nextDailyClosePage({ items: [item()], total: 31, page: 1 }, 30), 2);

// —— 记录标记：第 N 次、补录、推算 ——
assert.equal(saveSequenceMark(item({ saveSequence: 1, saveCountInDay: 1 })), null, "只保存一次不标");
assert.equal(saveSequenceMark(item({ saveSequence: 2, saveCountInDay: 3 })), 2, "同日多次保存标第 N 次");
assert.equal(saveSequenceMark(item({ saveSequence: 1, saveCountInDay: 2 })), 1, "第 1 次也要标（同日还有后续保存）");
assert.equal(isBackfilledRecord(item()), false);
assert.equal(isBackfilledRecord(item({ dataSource: "AuditBackfill", detailLevel: "CashOnly" })), true);
assert.equal(isBackfilledRecord(item({ dataSource: "ClientUpload", detailLevel: "TraceOnly" })), true, "明细不完整也算补录");
assert.equal(isBackfilledRecord(item({ dataSource: "AuditBackfill", detailLevel: "Full" })), true, "来源是补录就算");
assert.equal(resolveBackfillNotice(item()), null);
assert.deepEqual(resolveBackfillNotice(item({ dataSource: "AuditBackfill", detailLevel: "CashOnly", businessDateInferred: true })), { kind: "cashOnly", inferred: true });
assert.deepEqual(resolveBackfillNotice(item({ dataSource: "AuditBackfill", detailLevel: "TraceOnly", businessDateInferred: false })), { kind: "traceOnly", inferred: false });
assert.deepEqual(resolveBackfillNotice(item({ dataSource: "AuditBackfill", detailLevel: "Full" })), { kind: "generic", inferred: false });
assert.equal(clientKindLabelKey("Wpf"), "wpf");
assert.equal(clientKindLabelKey(" HANDHELD "), "handheld");
assert.equal(clientKindLabelKey("Ipad"), "ipad");
assert.equal(clientKindLabelKey("Android"), null, "未知端类型直接显示原文");

// —— 明细：支付方式、盘点 ——
const tenders: DailyCloseTender[] = [
  { method: "Voucher", salesAmount: 85, refundAmount: 0, netAmount: 85 },
  { method: "Card", salesAmount: 5320.15, refundAmount: 114, netAmount: 5206.15 },
  { method: "Cash", salesAmount: 1905.8, refundAmount: 63.5, netAmount: 1842.3 },
  { method: "GiftCard", salesAmount: 10, refundAmount: 0, netAmount: 10 },
];
assert.deepEqual(orderTenders(tenders).map((tender) => tender.method), ["Cash", "Card", "Voucher", "GiftCard"], "现金、刷卡、代金券，未知排最后");
assert.deepEqual(sumTenders(tenders.slice(0, 3)), { salesAmount: 7310.95, refundAmount: 177.5, netAmount: 7133.45 });
assert.deepEqual(sumTenders([]), { salesAmount: 0, refundAmount: 0, netAmount: 0 });

const cashCounts: DailyCloseCashCount[] = [
  { denominationCents: 5, quantity: 0, subtotalAmount: 0, kind: "Coin" },
  { denominationCents: 5000, quantity: 10, subtotalAmount: 500, kind: "Note" },
  { denominationCents: 10000, quantity: 8, subtotalAmount: 800, kind: "Note" },
  { denominationCents: 200, quantity: 3, subtotalAmount: 6, kind: "Coin" },
  { denominationCents: 50, quantity: 4, subtotalAmount: 2, kind: "Coin" },
];
const groups = splitCashCounts(cashCounts, null, null);
assert.deepEqual(groups.notes.map((row) => row.denominationCents), [10000, 5000], "纸币按面额降序");
assert.deepEqual(groups.coins.map((row) => row.denominationCents), [200, 50, 5], "硬币按面额降序，0 张的档位保留");
assert.equal(groups.noteSubtotal, 1300, "没有接口小计时按明细求和");
assert.equal(groups.coinSubtotal, 8);
const serverGroups = splitCashCounts(cashCounts, 1815, 23.8);
assert.equal(serverGroups.noteSubtotal, 1815, "优先用接口给的小计");
assert.equal(serverGroups.coinSubtotal, 23.8);
assert.deepEqual(splitCashCounts([], null, null), { notes: [], coins: [], noteSubtotal: 0, coinSubtotal: 0 });

assert.equal(hasTenderData({ detailLevel: "Full", tenders }), true);
assert.equal(hasTenderData({ detailLevel: "Full", tenders: [] }), false, "Full 但没有行也显示占位");
assert.equal(hasTenderData({ detailLevel: "CashOnly", tenders }), false, "补录记录一律占位");
assert.equal(hasCashCountData({ detailLevel: "Full", cashCounts }), true);
assert.equal(hasCashCountData({ detailLevel: "TraceOnly", cashCounts }), false);
assert.equal(hasCashCountData({ detailLevel: "Full", cashCounts: [] }), false);
assert.equal(hasCashReconciliation(item()), true);
assert.equal(hasCashReconciliation(item({ expectedCashAmount: null, countedCashAmount: null, cashDifference: null })), false, "TraceOnly 三格都没有数据");
assert.equal(hasCashReconciliation(item({ countedCashAmount: null })), false, "缺任何一项都不能对账");

// —— 查看保存记录：跳员工操作日志的预置参数 ——
const bothAccess = { canLegacy: true, canPos: true };
const saved = item({ savedAtUtc: "2026-10-06T11:48:00Z", storeTimeZoneId: "Australia/Sydney" }); // 悉尼 10-06 22:48
assert.deepEqual(resolveSaveLogLink(saved, bothAccess, "2026-10-06"), { source: "pos", stores: "1008", device: "POS01", preset: "today" }, "客户端上传优先新收银日志");
assert.equal(resolveSaveLogLink(saved, bothAccess, "2026-10-07")?.preset, "yesterday");
assert.equal(resolveSaveLogLink(saved, bothAccess, "2026-10-12")?.preset, "last7", "6 天前仍在近 7 天内");
assert.equal(resolveSaveLogLink(saved, bothAccess, "2026-10-13")?.preset, "last31", "7 天前要用近 31 天");
assert.equal(resolveSaveLogLink(saved, bothAccess, "2026-12-30")?.preset, "last31", "超过 31 天只能落在近 31 天");
assert.equal(resolveSaveLogLink(saved, bothAccess, "2026-10-05")?.preset, "today", "保存日晚于今天（时钟偏差）按今天");
assert.equal(resolveSaveLogLink(item({ dataSource: "AuditBackfill" }), bothAccess, "2026-10-06")?.source, "legacy", "补录记录来自旧收银日志，优先老收银");
assert.equal(resolveSaveLogLink(saved, { canLegacy: true, canPos: false }, "2026-10-06")?.source, "legacy", "没有新收银权限就换老收银");
assert.equal(resolveSaveLogLink(item({ dataSource: "AuditBackfill" }), { canLegacy: false, canPos: true }, "2026-10-06")?.source, "pos");
assert.equal(resolveSaveLogLink(saved, { canLegacy: false, canPos: false }, "2026-10-06"), null, "两个日志来源都没权限不显示入口");
// 保存日按门店时区：悉尼 10-07 01:30 保存（UTC 10-06 14:30），悉尼今天 10-07 → today
assert.equal(resolveSaveLogLink(item({ savedAtUtc: "2026-10-06T14:30:00Z" }), bothAccess, "2026-10-07")?.preset, "today");
assert.equal(resolveSaveLogLink(item({ savedAtUtc: "" }), bothAccess, "2026-10-07")?.preset, "today", "保存时间无效按今天");

// —— 错误分类 ——
assert.equal(classifyDailyCloseError({ response: { status: 400, data: { success: false, errorCode: "INVALID_QUERY" } } }), "invalidQuery");
assert.equal(classifyDailyCloseError({ response: { status: 400, data: { code: "INVALID_QUERY" } } }), "invalidQuery", "兼容 code 字段");
assert.equal(classifyDailyCloseError({ code: "INVALID_QUERY", response: { status: 400, data: {} } }), "invalidQuery");
assert.equal(classifyDailyCloseError({ response: { status: 400, data: { errorCode: "OTHER" } } }), "other", "其他 400 不当成日期超限");
assert.equal(classifyDailyCloseError({ response: { status: 403 } }), "forbidden");
assert.equal(classifyDailyCloseError({ response: { status: 404, data: { errorCode: "NOT_FOUND" } } }), "notFound");
assert.equal(classifyDailyCloseError({ response: { status: 500 } }), "other");
assert.equal(classifyDailyCloseError(new Error("Network Error")), "other");
assert.equal(classifyDailyCloseError(null), "other");
assert.equal(classifyDailyCloseError("boom"), "other");

console.log("daily-closes logic.test.ts: ok");
