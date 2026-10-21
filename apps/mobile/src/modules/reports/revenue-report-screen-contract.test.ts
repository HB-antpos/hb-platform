import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

const directory = dirname(fileURLToPath(import.meta.url));
const source = readFileSync(join(directory, "RevenueReportScreen.tsx"), "utf8");
const hubSource = readFileSync(join(directory, "reports-hub-screen.tsx"), "utf8");

assert.match(
  source,
  /const cashierStoreOptionsQuery = useQuery\(\{[\s\S]*?fetchProductReportStoreOptions\(\{ signal \}\)/,
  "营业额报告必须先加载后端已按 Store.IsActive 筛选的分店白名单",
);
assert.match(
  source,
  /getCashierEnabledStoreCodes\(cashierStoreOptionsQuery\.data \?\? \[\]\)/,
  "营业额报告必须把收银启用分店整理成显式查询范围",
);
assert.match(
  source,
  /buildQuery\(period, cashierEnabledStoreCodes\)/,
  "营业额排行、搜索、分店数与汇总必须来自收银启用范围内的服务端结果",
);
assert.match(
  source,
  /cashierStoreOptionsQuery\.isSuccess[\s\S]*?cashierEnabledStoreCodes\.length > 0[\s\S]*?enabled: summaryQueryEnabled/,
  "启用分店白名单未完成或为空时不得执行可能退化为全店的营业额查询",
);
assert.match(
  source,
  /const cashierStoreScopeVersion = cashierStoreOptionsQuery\.dataUpdatedAt;[\s\S]*?\["reports", "revenue-summary", accountIdentity, cashierStoreScopeVersion, queryParams\]/,
  "即使白名单内容未变，每次权威回读也必须用新 revision 自动刷新营业额数据",
);
assert.match(
  source,
  /const summaryLoading =\s*cashierStoreOptionsQuery\.isFetching/,
  "白名单后台重验期间必须隐藏旧营业额业务行",
);
const screenStart = source.indexOf("export function RevenueReportScreen(");
const modalStart = source.indexOf("      <Portal>", screenStart);

assert.ok(screenStart >= 0, "必须能够定位营业额报告屏幕");
assert.ok(modalStart > screenStart, "营业额表格滚动区必须位于详情弹窗之前");

assert.match(
  hubSource,
  /if \(value !== "revenue" && value !== "product"\) return;[\s\S]*?if \(value === tab\) return;[\s\S]*?markReportNavigationStart\(value\);[\s\S]*?setTab\(value\)/,
  "报告页签只有确实切换时才记录对应报表的点击起点",
);
assert.match(
  hubSource,
  /useFocusEffect\([\s\S]*?getPendingReportNavigationToken\(tab\)[\s\S]*?refetchQueries\([\s\S]*?getReportStoreScopeRefreshQueryOptions\(tab\)[\s\S]*?return \(\) => \{[\s\S]*?discardReportNavigationStart\(tab, navigationActionId\)/,
  "返回常驻报告页时必须先重验启用分店范围，并只在焦点会话结束时清理仍未认领的 marker",
);
assert.match(
  hubSource,
  /const \[focusNavigationActionId, setFocusNavigationActionId\] = useState<number \| null>\(null\);[\s\S]*?setFocusNavigationActionId\(navigationActionId\);[\s\S]*?<RevenueReportScreen[\s\S]*?reportNavigationActionId=\{focusNavigationActionId\}/,
  "Hub 必须把当前焦点 actionId 显式传给活动营业额屏",
);
const focusEffectStart = hubSource.indexOf("  useFocusEffect(");
const freshnessTimeStart = hubSource.indexOf("  const freshnessTime", focusEffectStart);
assert.ok(focusEffectStart >= 0 && freshnessTimeStart > focusEffectStart, "必须能够隔离 Reports 焦点会话");
assert.doesNotMatch(
  hubSource.slice(focusEffectStart, freshnessTimeStart),
  /\.finally\([\s\S]*?discardReportNavigationStart/,
  "复用在途请求时不得在 refetch Promise 结束后直接丢弃本次 grouped marker",
);
assert.match(
  source,
  /revenueLoadTimer\.start\(cacheState, "revenue"\)/,
  "营业额首次查询必须继承营业额导航起点",
);
assert.match(
  source,
  /reportNavigationActionId\?: number \| null;[\s\S]*?reportNavigationActionId = null[\s\S]*?const summaryQueryEnabled =[\s\S]*?revenuePeriodAvailable[\s\S]*?cashierStoreOptionsQuery\.isSuccess[\s\S]*?cashierEnabledStoreCodes\.length > 0;/,
  "营业额屏必须接收当前焦点 actionId，并显式判定主查询是否可执行",
);
assert.match(
  source,
  /const summaryQueryTerminallyBlocked =[\s\S]*?cashierStoreOptionsQuery\.isError[\s\S]*?cashierStoreScopeEmpty;[\s\S]*?if \(reportNavigationActionId === null \|\| !summaryQueryTerminallyBlocked\) return;[\s\S]*?discardReportNavigationStart\("revenue", reportNavigationActionId\)/,
  "营业额只在日期无效或白名单已失败、已确认为空时清理 marker，白名单加载中不得提前清理",
);
assert.match(
  source,
  /const summaryQuery = useQuery\(\{[\s\S]*?enabled: summaryQueryEnabled,[\s\S]*?\.\.\.REPORT_QUERY_OPTIONS/,
  "营业额无效周期必须与 marker 清理使用同一个主查询可执行条件",
);

const screenSource = source.slice(screenStart, modalStart);
const flatLists = screenSource.match(/<FlatList\b/g) ?? [];

assert.equal(flatLists.length, 1, "营业额报告主体必须只使用一个 FlatList 承担纵向滚动");

const flatListStart = screenSource.indexOf("<FlatList");

assert.ok(flatListStart >= 0, "必须能够定位营业额报告的主 FlatList");

const flatListSource = screenSource.slice(flatListStart);

assert.match(
  flatListSource,
  /ListHeaderComponent=\{\s*<View style=\{styles\.listHeader\}>[\s\S]*?<View style=\{styles\.filtersCard\}>[\s\S]*?<PeriodSegment[\s\S]*?setBranchPickerVisible\(true\)[\s\S]*?<View style=\{styles\.toolbar\}>[\s\S]*?<MonthDatePickerField[\s\S]*?getPeriodLabel\(period\)/,
  "期间卡（日周月分段、分店选择、翻期 + 日期 + 快捷键）必须完整放入 ListHeaderComponent",
);
assert.doesNotMatch(
  flatListSource,
  /ListHeaderComponent=\{\(\) =>/,
  "ListHeaderComponent 必须使用稳定元素，避免刷新重渲染时重置日期选择器状态",
);
assert.match(
  flatListSource,
  /data=\{summaryListItems\}/,
  "完整分店行和表头必须由稳定的 summaryListItems 驱动",
);
assert.doesNotMatch(
  flatListSource,
  /stickyHeaderIndices|table-header|renderSummaryTableHeader/,
  "分店排行已改为自带标签的卡片行：不再有需要吸顶的列标题哨兵条目",
);
assert.match(
  flatListSource,
  /contentInsetAdjustmentBehavior="automatic"/,
  "主 FlatList 必须自动适配安全区内容边距",
);
assert.match(
  flatListSource,
  /keyExtractor=\{\(item\) => item\.row\.id\}[\s\S]*?renderSummaryRow\(item\.row, item\.rank, index === 0, index === summaryListItems\.length - 1\)/,
  "排行卡片行必须由 summaryListItems 逐条渲染，并带首尾标记以画圆角分组",
);
// 2026-10 重设计：全店汇总提到页面顶部（第一眼看到总营业额），取代此前「移到排行之后保留六行排行」的决定；
// 排行行改为卡片行（每行约两倍高），首屏行数不再是约束，换来「不用左右滑就能看全客单数 / 客单价的本期与同期」。
assert.match(
  flatListSource,
  /<RevenueHeroCard[\s\S]*?reports\.sections\.summary[\s\S]*?reports\.sections\.branchRanking[\s\S]*?ListFooterComponent/,
  "全店汇总卡必须位于排行标题之前（页面顶部）",
);
assert.doesNotMatch(
  flatListSource.slice(flatListSource.indexOf("ListFooterComponent")),
  /RevenueHeroCard|RevenueSummaryCard/,
  "汇总卡不得再出现在列表底部",
);
assert.match(flatListSource, /viewabilityConfig=\{summaryViewabilityConfig\}/, "首屏数据必须接入稳定可见性计时");
assert.match(flatListSource, /onViewableItemsChanged=\{onSummaryViewableItemsChanged\}/, "首条可见分店必须完成性能计时");
assert.match(
  source,
  /fetchExecutiveHourlyTraffic\(detailParams, \{ signal \}\)/,
  "营业额分时下钻必须透传取消信号",
);
assert.match(
  source,
  /fetchBranchDailyPerformance\(detailParams, \{ signal \}\)/,
  "营业额逐日下钻必须透传取消信号",
);
assert.match(
  screenSource,
  /useQuery<RevenueDetailSnapshot<DetailRow>>\(\{[\s\S]*?fetchExecutiveHourlyTraffic[\s\S]*?fetchBranchDailyPerformance/,
  "分时和逐日下钻必须以带完整性元数据的快照作为查询结果",
);
assert.match(
  source,
  /recordReportLoadPerformance\(\s*detailLoadTypeRef\.current === "hourly" \? "revenue-hourly" : "revenue-daily",\s*measurement,?\s*\)/,
  "分时和逐日下钻必须分别记录首行可见耗时",
);
assert.match(
  source,
  /onViewableItemsChanged=\{onDetailViewableItemsChanged\}/,
  "营业额下钻必须以真实可见行完成性能计时",
);
assert.match(source, /InteractionManager\.runAfterInteractions/, "营业额下钻必须等待 Paper Modal 展示动画完成");
assert.match(source, /detailLoadGate\.setPresentationReady\(true\)/, "弹窗展示完成必须成为独立计时门禁");
assert.match(
  source,
  /const isSameDetailQuery = detailLoadQueryKeyRef\.current === detailQueryKey;[\s\S]*?restorePhysicalState: isSameDetailQuery[\s\S]*?firstRowVisible: detailPhysicalRowVisibleRef\.current[\s\S]*?presentationReady: detailPhysicalPresentationReadyRef\.current/,
  "同 key 重试必须从独立物理 refs 恢复首行可见与弹窗展示状态",
);
assert.match(
  source,
  /hasUsableSuccessfulReportCache\([\s\S]*?queryClient\.getQueryState\(summaryQueryKey\)\?\.status[\s\S]*?cachedSummary[\s\S]*?cachedSummary\.isComplete && cachedSummary\.rows\.length > 0/,
  "营业额主排行只有 success 状态的完整非空缓存才能按 warm 预算统计",
);
assert.match(
  source,
  /hasUsableSuccessfulReportCache\([\s\S]*?queryClient\.getQueryState\(detailQueryKey\)\?\.status[\s\S]*?cachedDetailSnapshot[\s\S]*?cachedSnapshot\.isComplete && cachedSnapshot\.rows\.length > 0/,
  "营业额下钻必须排除 error、空缓存和不完整快照的 warm 分类",
);
assert.match(
  source,
  /detailPhysicalRowVisibleRef\.current = hasVisibleDetailRow;[\s\S]*?detailLoadGate\.setFirstRowVisible\(hasVisibleDetailRow\)/,
  "营业额下钻必须独立保存当前物理可见状态",
);
assert.match(
  source,
  /detailPhysicalPresentationReadyRef\.current = true;[\s\S]*?detailLoadGate\.setPresentationReady\(true\)/,
  "营业额下钻必须独立保存当前物理展示状态",
);
assert.match(
  screenSource,
  /detailQuery\.data\?\.isComplete \? detailQuery\.data\.rows : \[\]/,
  "分时和逐日下钻不得直接渲染未完整的响应行",
);
assert.match(
  screenSource,
  /!detailQuery\.data\.isComplete[\s\S]*?detailLoadGate\.fail\(\)/,
  "缺少完整性元数据或有界追数耗尽时不得计入首条数据性能",
);
assert.match(
  screenSource,
  /detailRequestGenerationRef\.current !== requestGeneration[\s\S]*?AbortError/,
  "分时和逐日旧请求晚返回必须按请求代次丢弃，不能覆盖当前弹窗会话",
);
assert.match(
  source.slice(modalStart),
  /detailPending[\s\S]*?reports\.states\.statisticsIncomplete[\s\S]*?retryDetail/,
  "分时和逐日补算耗尽时必须明确提示并允许人工重试",
);
assert.match(
  screenSource,
  /const detailQueryEnabled =[\s\S]*?cashierStoreOptionsQuery\.isSuccess[\s\S]*?detailBranchCodes\.length > 0;[\s\S]*?enabled: detailQueryEnabled/,
  "营业额下钻也必须在收银启用白名单内且白名单成功时才执行",
);
assert.match(
  screenSource,
  /const retryDetail = \(\) => \{\s*if \(detailQueryEnabled\) \{\s*void cashierStoreOptionsQuery\.refetch\(\)/,
  "营业额下钻人工重试必须先重验白名单，再由新 revision 触发业务请求",
);
assert.doesNotMatch(
  screenSource,
  /summaryQuery\.refetch\(\)|detailQuery\.refetch\(\)/,
  "营业额人工重试不得与权威白名单重验并发使用旧范围",
);
assert.match(flatListSource, /<MonthDatePickerField\s+compact/, "营业额日期控件必须使用紧凑模式，为排行保留首屏空间");
assert.doesNotMatch(flatListSource, /supplierPage|pageSize|pagination|slice\(/i, "营业额分店排行不得分页或截断");
assert.match(flatListSource, /<RefreshControl[\s\S]*?onRefresh=\{refresh\}/, "主列表必须保留下拉刷新");
assert.match(flatListSource, /summaryLoading/, "主列表必须合并白名单与营业额加载状态");
assert.match(flatListSource, /summaryError/, "主列表必须合并白名单与营业额错误状态");
assert.match(flatListSource, /cashierStoreScopeEmpty[\s\S]*?noCashierEnabledStores/, "零启用收银分店必须显示明确空态");
assert.match(flatListSource, /visibleRows\.length === 0/, "筛选后的主列表必须保留空状态");
assert.match(
  screenSource,
  /queryFn: async \(\{ signal \}\)[\s\S]*?fetchExecutiveBranchPerformance\(queryParams, \{ signal \}\)/,
  "营业额查询必须把 React Query 取消信号传入同一轮补算轮询",
);
assert.match(
  screenSource,
  /const isSameQuery = revenueLoadQueryKeyRef\.current === summaryQueryKey;[\s\S]*?if \(!isSameQuery\) \{\s*revenueBranchVisibleRef\.current = false;/,
  "同 key 刷新必须保留当前真实可见行，新 key 才能重置可见性",
);
assert.match(
  screenSource,
  /revenueBranchVisibleRef\.current = hasVisibleBranch;/,
  "FlatList 可见性回调必须同时记录行进入和离开首屏",
);
assert.match(
  screenSource,
  /revenueRequestGenerationRef\.current !== requestGeneration[\s\S]*?AbortError/,
  "旧请求晚返回必须通过请求代次丢弃，不能污染新会话",
);
assert.match(
  screenSource,
  /!summaryQuery\.data\.isComplete[\s\S]*?revenueLoadTimer\.fail\(\)/,
  "部分或耗尽快照不得完成首条完整业务数据计时",
);
assert.match(
  screenSource,
  /if \(!reportScopeValid \|\| summaryQuery\.isError[\s\S]*?if \(!summaryQuery\.data\.isComplete\)[\s\S]*?saveCompleteReportSnapshot\(/,
  "营业额排行必须只保存已确认完整的快照",
);
assert.match(
  screenSource,
  /getReportSnapshotDisplay\([\s\S]*?getCompleteReportSnapshot\(completeSummarySnapshotsRef, summarySnapshotKey\)[\s\S]*?snapshot\.isComplete \? snapshot\.rows : undefined,[\s\S]*?reportScopeValid/,
  "补算未完成时只能保留同查询上一次完整排行，不能展示本次部分快照",
);
assert.doesNotMatch(
  screenSource,
  /summaryQuery\.data\?\.rows \?\? \[\]/,
  "排行不得直接渲染未经过完整性门禁的响应行",
);
assert.match(
  flatListSource,
  /summaryPollingExhausted[\s\S]*?reports\.states\.statisticsIncomplete[\s\S]*?icon="refresh"[\s\S]*?retrySummary/,
  "有界轮询耗尽后必须明确停止自动刷新文案，并在排行标题提供重试",
);
assert.match(
  screenSource,
  /summaryQuery\.dataUpdatedAt/,
  "同 key 且结构相同的 refetch 也必须触发一次完成检查",
);
assert.doesNotMatch(
  screenSource,
  /revenueEmptyRetryCount|retryNumber \* 650/,
  "补算退避必须收进单次 API 会话，不能由组件重启成 warm 查询",
);
assert.match(flatListSource, /setBranchPickerVisible\(true\)/, "25 到 30 家分店必须提供快速选择入口");
assert.match(flatListSource, /searchBranchPlaceholder/, "分店排行必须提供名称或编号搜索");
assert.match(
  screenSource,
  /onPress=\{\(\) => setDrilldown\(/,
  "营业额数据行必须保留分店下钻入口",
);
assert.match(screenSource, /formatOrdinal\(rowIndex\)/, "分店排行必须显示连续行号");
assert.match(source.slice(modalStart), /<Modal[\s\S]*?<FlatList[\s\S]*?renderDetailRow/, "分时或每日明细必须在下一层可滚动抽屉中显示");
assert.match(source.slice(modalStart), /data=\{branchPickerRows\}[\s\S]*?initialNumToRender=\{14\}/, "分店选择器必须连续滚动全部分店而不分页");
assert.match(source.slice(modalStart), /branchPickerSearch[\s\S]*?searchBranchPlaceholder/, "分店选择抽屉必须支持从 25 到 30 家中快速搜索");
assert.match(source, /numberOfLines=\{2\}[\s\S]*?getWeekdayLabel/, "每日明细必须完整显示日期与星期");
assert.match(source, /rankCard:\s*\{[\s\S]*?minHeight: 64/, "排行卡片行高不得低于 64，且远大于 44px 触控下限");

// ===== 卡片化后「显示的数据类型不能少」的字段守卫 =====
const summaryRowStart = source.indexOf("const renderSummaryRow =");
const detailRowStart = source.indexOf("const renderDetailRow =");
const screenReturnStart = source.indexOf("  return (\n    <View style={styles.container}>", detailRowStart);
assert.ok(summaryRowStart > 0 && detailRowStart > summaryRowStart && screenReturnStart > detailRowStart, "必须能隔离排行行与明细行渲染函数");
const summaryRowSource = source.slice(summaryRowStart, detailRowStart);
const detailRowSource = source.slice(detailRowStart, screenReturnStart);
for (const field of [
  "item.branchName", "item.branchCode", "item.revenue", "item.compareRevenue", "item.compareUnavailable",
  "item.transactions", "item.compareTransactions", "item.averageTransaction", "item.compareAverageTransaction",
  "GrowthPill", "reports.metrics.transactions", "reports.metrics.averageTransaction", "reports.metrics.compare",
]) {
  assert.ok(summaryRowSource.includes(field), `分店排行卡片必须仍显示 ${field}`);
}
for (const field of [
  "item.revenue", "item.compareRevenue", "item.transactions", "item.compareTransactions",
  "item.averageTransaction", "item.compareAverageTransaction", "GrowthPill", "reports.cumulative.upTo",
  "reports.cumulative.live", "reports.cumulative.upcoming", "reports.cumulative.comparedAt", "isCutoffRow",
]) {
  assert.ok(detailRowSource.includes(field), `明细卡片必须仍显示 ${field}`);
}
const heroStart = source.indexOf("function RevenueHeroCard(");
const heroSource = source.slice(heroStart, source.indexOf("export function RevenueReportScreen(", heroStart));
for (const field of [
  "summary?.revenue", "summary?.compareRevenue", "summary?.transactions", "summary?.compareTransactions",
  "summary?.averageTransaction", "summary?.compareAverageTransaction", "reports.metrics.current", "reports.metrics.compare",
]) {
  assert.ok(heroSource.includes(field), `全店汇总卡必须仍显示 ${field}`);
}
assert.doesNotMatch(source, /detailHorizontalScroll|detailHorizontalContent|minWidth: 500/, "明细抽屉卡片不再需要横向滚动");

console.log("revenue-report-screen-contract.test.ts: ok");
