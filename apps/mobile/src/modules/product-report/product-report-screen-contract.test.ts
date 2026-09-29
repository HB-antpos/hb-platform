import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

const directory = dirname(fileURLToPath(import.meta.url));
const source = readFileSync(join(directory, "product-report-screen.tsx"), "utf8");
const hubSource = readFileSync(join(directory, "../reports/reports-hub-screen.tsx"), "utf8");

assert.match(
  source,
  /getCashierEnabledStoreCodes\(storeOptionsQuery\.data \?\? \[\]\)/,
  "商品报告必须以 Store.IsActive 分店选项生成同一份收银启用白名单",
);
assert.match(
  source,
  /getCashierScopedBranchCodes\(cashierEnabledStoreCodes, selectedStoreCode\)/,
  "商品报告的单店筛选必须受收银启用白名单约束",
);
assert.equal(
  (source.match(/buildProductReportDateQuery\(activeRange, cashierEnabledStoreCodes\)/g) ?? []).length,
  4,
  "三表联动的全店数据、供应商与商品分店下钻及完整快照都必须显式限定在全部收银启用分店",
);
assert.match(
  source,
  /storeOptionsQuery\.isSuccess[\s\S]*?branchCodes\.length > 0[\s\S]*?enabled: Boolean\(queryParams\)/,
  "白名单未完成、为空或单店选择已失效时不得执行商品报告主查询",
);
assert.match(
  source,
  /const cashierStoreScopeVersion = storeOptionsQuery\.dataUpdatedAt;/,
  "商品报告必须以白名单权威回读时间作为查询 revision",
);
assert.equal(
  (source.match(/cashierStoreScopeVersion/g) ?? []).length >= 7,
  true,
  "白名单 revision 必须覆盖变量定义、三个主查询和两个分店下钻查询",
);
assert.match(
  source,
  /const mainReportStatisticsPending =\s*storeOptionsQuery\.isFetching/,
  "白名单后台重验期间必须隐藏商品报告旧缓存",
);

function assertSourceOrder(scope: string, markers: string[], message: string) {
  let previousIndex = -1;
  markers.forEach((marker) => {
    const markerIndex = scope.indexOf(marker, previousIndex + 1);
    assert.ok(markerIndex > previousIndex, `${message}：${marker}`);
    previousIndex = markerIndex;
  });
}

const pickerStart = source.indexOf("function StorePickerModal(");
const pickerEnd = source.indexOf("function BranchDrilldownModal(", pickerStart);

assert.ok(pickerStart >= 0, "商品报告必须保留分店选择弹层");
assert.ok(pickerEnd > pickerStart, "必须能够隔离分店选择弹层源码");

assert.match(
  hubSource,
  /if \(value !== "revenue" && value !== "product"\) return;[\s\S]*?if \(value === tab\) return;[\s\S]*?markReportNavigationStart\(value\);[\s\S]*?setTab\(value\)/,
  "商品页签只有确实切换时才记录商品报告点击起点",
);
assert.match(
  hubSource,
  /const \[focusNavigationActionId, setFocusNavigationActionId\] = useState<number \| null>\(null\);[\s\S]*?<ProductReportScreen[\s\S]*?reportNavigationActionId=\{focusNavigationActionId\}/,
  "Hub 必须把当前焦点 actionId 显式传给活动商品屏",
);
assert.match(
  source,
  /productLoadTimer\.start\(cacheState, "product"\)/,
  "商品首次查询必须继承商品导航起点",
);
assert.match(
  source,
  /reportNavigationActionId\?: number \| null;[\s\S]*?reportNavigationActionId = null/,
  "商品屏必须接收当前焦点 actionId",
);
assert.match(
  source,
  /if \(reportNavigationActionId === null\) return;[\s\S]*?if \(dateRangeValid && storeOptionsQuery\.isPending\) return;[\s\S]*?!dateRangeValid[\s\S]*?storeOptionsQuery\.isError[\s\S]*?storeOptionsQuery\.isSuccess && cashierEnabledStoreCodes\.length === 0[\s\S]*?discardReportNavigationStart\("product", reportNavigationActionId\)/,
  "商品报告只在日期无效或白名单已失败、已确认为空时清理 marker，白名单加载中不得提前清理",
);
assert.match(
  source,
  /storeOptionsQuery\.isFetching[\s\S]*?!storeOptionsQuery\.isSuccess[\s\S]*?!selectedStoreCode[\s\S]*?branchCodes\.length > 0[\s\S]*?setSelectedStoreCode\(undefined\)[\s\S]*?setDrilldown\(null\)/,
  "已停用的陈旧单店选择必须先阻断请求，再清除选择和下钻",
);
assert.equal(
  (source.match(/void storeOptionsQuery\.refetch\(\);/g) ?? []).length >= 3,
  true,
  "刷新、错误重试和未完成重试都必须先重验收银启用白名单",
);
assert.doesNotMatch(
  source,
  /totalRevenueQuery\.refetch\(\)|supplierQuery\.refetch\(\)|productQuery\.refetch\(\)/,
  "白名单重验与业务请求不得并发；成功回读后必须由 scope revision 自动触发业务查询",
);
assert.match(
  source,
  /storeOptionsQuery\.isSuccess && cashierEnabledStoreCodes\.length === 0[\s\S]*?reports\.states\.noCashierEnabledStores/,
  "零启用收银分店必须显示明确空态",
);

const pickerSource = source.slice(pickerStart, pickerEnd);

assert.match(pickerSource, /<ScrollView/);
assert.match(pickerSource, /nestedScrollEnabled/);
assert.match(pickerSource, /showsVerticalScrollIndicator/);
assert.match(pickerSource, /keyboardShouldPersistTaps="handled"/);
assert.match(pickerSource, /\{labelAll\}/);
assert.match(pickerSource, /options\.map/);
assert.match(pickerSource, /style=\{styles\.storeModalList\}/);
assert.match(pickerSource, /contentContainerStyle=\{styles\.storeModalListContent\}/);

assert.match(source, /function formatNullableMoney\(value: number \| null\)/);
assert.match(source, /function formatGrossMarginRate\([\s\S]*?costStatus: ProductReportCostStatus[\s\S]*?costPendingLabel: string/);
assert.match(source, /\(value \* 100\)\.toFixed\(1\)/, "毛利率 0.4 必须显示为 40.0%，不能显示成 0.4%");
assert.match(source, /productReport\.states\.costPending/);
assert.match(source, /styles\.rowNumberColumn/);
assert.match(source, /rowNumber: \(supplierPage - 1\) \* SUPPLIER_PAGE_SIZE \+ index \+ 1/);
assert.match(
  source,
  /rowNumber: \(displayedProductPage - 1\) \* PRODUCT_PAGE_SIZE \+ index \+ 1/,
  "明细行号按表里实际显示的那一页计算，局部过渡期间不能把旧行标成新页码",
);
assert.match(source, /grossProfit/);
assert.match(source, /grossMarginRate/);
assert.match(source, /costStatus/);
assert.match(source, /compareCostStatus/);
assert.match(source, /showsHorizontalScrollIndicator>/, "宽表必须显示水平滚动提示，让毛利字段可发现");
assert.match(source, /reports\.metrics\.current[\s\S]*?productReport\.metrics\.compare/, "双行数值必须明确标注本期与同期");
assert.match(source, /accessibilityRole="button"[\s\S]*?productReport\.drilldown\.product/, "商品行必须明确提供可点击的分店下钻入口");
assert.match(source, /new ReportLoadPerformanceTimer\(\)/, "商品报告必须计量首数据耗时");
assert.match(
  source,
  /totalRevenueQuery\.data\?\.isComplete === true[\s\S]*?supplierQuery\.data\?\.isComplete === true[\s\S]*?productQuery\.data\?\.isComplete === true/,
  "商品报告必须等待总额、供应商和商品三块 Fresh 数据后再标记归一化完成",
);
assert.match(
  source,
  /getProductReportCacheVersionState\(\[[\s\S]*?totalRevenueQuery\.data[\s\S]*?supplierQuery\.data[\s\S]*?productQuery\.data[\s\S]*?\]\)/,
  "商品报告必须比较三块并发结果的 cacheVersion",
);
assert.match(
  source,
  /getProductReportCacheVersionSyncDecision\([\s\S]*?decision !== "refetch"[\s\S]*?refetchMainReport\(\)/,
  "版本不一致时必须受控地协调重取三块数据",
);
assert.match(
  source,
  /const refetchMainReport = useCallback\(\(\) => Promise\.all\(\[[\s\S]*?totalRevenueQueryKey[\s\S]*?supplierQueryKey[\s\S]*?productQueryKey/,
  "cacheVersion 协调重取必须覆盖总额、供应商和商品三条精确查询",
);
assert.match(
  source,
  /mainReportCacheVersionState === "mismatch"[\s\S]*?mainReportVersionSyncExhausted[\s\S]*?mainReportStatisticsIncomplete/,
  "版本不一致期间不得展示混合批次，重试耗尽后必须进入未完成态",
);
assert.match(
  source,
  /mainReportStatisticsPending[\s\S]*?mainReportCacheVersionMismatch[\s\S]*?mainReportQueriesFetching/,
  "版本不一致的协调请求在途时必须继续隐藏旧混合批次",
);
const totalRevenueStateStart = source.indexOf("const totalRevenueStatisticsPending =");
const productQueryStart = source.indexOf("const productQuery =", totalRevenueStateStart);
const mainReportPendingStart = source.indexOf("const mainReportStatisticsPending =", productQueryStart);
const mainReportIncompleteStart = source.indexOf("const mainReportStatisticsIncomplete =", mainReportPendingStart);
const productRowStart = source.indexOf("const renderProductRow =");
const reportContentStart = source.indexOf("{!dateRangeValid ? (", productRowStart);
assert.ok(totalRevenueStateStart >= 0 && productQueryStart > totalRevenueStateStart, "必须定义总额统计状态");
assert.ok(
  mainReportPendingStart > productQueryStart && mainReportIncompleteStart > mainReportPendingStart,
  "必须能够隔离主报表全页 Loading 判定",
);
assert.ok(productRowStart >= 0 && reportContentStart > productRowStart, "必须能定位报告内容根分支");
const totalRevenueStateSource = source.slice(totalRevenueStateStart, productQueryStart);
const mainReportPendingSource = source.slice(mainReportPendingStart, mainReportIncompleteStart);
const productRowSource = source.slice(productRowStart, reportContentStart);
assert.match(
  totalRevenueStateSource,
  /!totalRevenueQuery\.data\.isComplete[\s\S]*?!totalRevenueQuery\.data\.pollingExhausted/,
  "总额统计在有界轮询中必须明确处于加载态",
);
assert.match(
  totalRevenueStateSource,
  /const totalRevenueStatisticsIncomplete[\s\S]*?!totalRevenueQuery\.data\.isComplete[\s\S]*?totalRevenueQuery\.data\.pollingExhausted/,
  "有界轮询耗尽的部分总额必须明确为未完成状态",
);
assert.match(
  source.slice(reportContentStart),
  /mainReportStatisticsPending && !mainReportHasDisplay \? \([\s\S]*?<LoadingState label=\{t\("reports\.states\.refreshingStatistics"\)\}/,
  "首次无可展示的完整结果时，供应商或商品主表处于非 Fresh 追数必须停在 Loading",
);
assert.match(
  source.slice(reportContentStart),
  /mainReportRequestError && !mainReportHasDisplay \? \([\s\S]*?<ErrorState[\s\S]*?resetMainReportVersionSync\(\);[\s\S]*?void storeOptionsQuery\.refetch\(\);/,
  "首次无可展示的完整结果时，任一首屏请求失败才显示统一重试",
);
assert.match(
  source.slice(reportContentStart),
  /mainReportStatisticsIncomplete && !mainReportHasDisplay \? \([\s\S]*?<ErrorState[\s\S]*?reports\.states\.statisticsIncomplete[\s\S]*?resetMainReportVersionSync\(\);[\s\S]*?void storeOptionsQuery\.refetch\(\);/,
  "首次无可展示的完整结果时，主表轮询耗尽才显示重试",
);
assert.match(
  source,
  /const mainReportHasDisplay = mainReportHasSnapshot \|\| linkedTransition !== null;/,
  "只有同条件快照或同一份报告内的联动过渡才算可展示结果",
);
assert.match(
  source,
  /const linkedTransitionStaleness = !mainReportCurrentComplete[\s\S]*?!mainReportHasSnapshot[\s\S]*?reportScopeValid[\s\S]*?getLinkedTransitionStaleness\(lastDisplayedMainReport\.context, requestedDisplayContext\)/,
  "联动过渡只在没有同条件快照、权限范围有效时沿用上一次完整展示",
);
assert.match(
  source,
  /displayedMainReport\?\.supplier\.data \?\? \[\][\s\S]*?displayedMainReport\?\.product\.data\.rows \?\? \[\]/,
  "主表业务行必须来自同条件完整快照，不能把根级空 data 当业务空结果",
);
assert.match(
  source,
  /isDrilldownLoading[\s\S]*?\.data\.isComplete[\s\S]*?isDrilldownStatisticsIncomplete[\s\S]*?\.data\.pollingExhausted/,
  "供应商和商品下钻必须识别非 Fresh 追数与耗尽状态",
);
assert.match(
  source,
  /supplierRows=\{displayedSupplierBranchRows\}[\s\S]*?productRows=\{displayedProductBranchRows\}/,
  "下钻行数据必须来自同条件完整快照，非 Fresh 不得渲染为空业务表",
);
assert.doesNotMatch(
  productRowSource,
  /totalRevenueStatisticsPending|totalRevenueStatisticsIncomplete/,
  "总额统计状态不得侵入商品图片或行级渲染",
);
assert.match(
  source,
  /queryFn: async \(\{ signal \}\) => \{[\s\S]*?fetchProductReportTotalRevenue\(allBranchQueryParams!, \{ signal \}\)/,
  "商品页总营业额固定按全部授权分店请求（所选分店在前端切片），并透传 React Query 取消信号",
);
assert.match(
  source,
  /import \{[^}]*keepPreviousData[^}]*\} from "@tanstack\/react-query"/,
  "商品分页、搜索或供应商筛选切换 query key 时必须保留上一份商品上下文",
);
assert.match(
  source,
  /const productQuery = useQuery\(\{[\s\S]*?placeholderData: keepPreviousData[\s\S]*?\}\);/,
  "page1 切换 page2 时商品查询必须使用上一页快照作占位，不能触发全页冷加载",
);
assert.match(
  source,
  /const productSectionLoading =[\s\S]*?productQuery\.isPlaceholderData[\s\S]*?productSectionLoading \? \([\s\S]*?<LoadingState label=\{t\("productReport\.states\.loading"\)\}/,
  "商品 query key 切换只能在商品区域显示局部加载",
);
assert.match(
  source,
  /productSectionLoading \? \([\s\S]*?styles\.productSummaryLoading[\s\S]*?productReport\.states\.loading[\s\S]*?\) : \([\s\S]*?<ProductPageSummaryCard/,
  "placeholder 期间商品汇总卡必须显示局部 loading，不能把旧行按新页码、筛选或搜索语境重新标注",
);
assert.doesNotMatch(
  mainReportPendingSource,
  /isPlaceholderData/,
  "上一页商品占位数据不得让汇总卡和供应商表进入全页 Loading",
);
assert.match(
  mainReportPendingSource,
  /productQuery\.isLoading/,
  "没有上一页占位数据的首次冷加载仍必须使用全页 Loading 门禁",
);
assert.match(
  source,
  /queryFn: async \(\{ signal \}\) => \{[\s\S]*?fetchSupplierReportRows\(kind, queryParams!, 1000, \{ signal \}\)/,
  "供应商首屏请求必须接收并透传 React Query 取消信号",
);
assert.match(
  source,
  /fetchProductReportProductRows\([\s\S]*?productSearch,[\s\S]*?\{ signal \},[\s\S]*?\)/,
  "商品首屏请求必须接收并透传 React Query 取消信号",
);
assert.match(
  source,
  /fetchSupplierBranchBreakdown\([\s\S]*?supplier\.supplierCode,[\s\S]*?\{ signal \},[\s\S]*?\)/,
  "供应商下钻请求必须接收并透传 React Query 取消信号",
);
assert.match(
  source,
  /fetchProductBranchBreakdown\([\s\S]*?product\.productCode,[\s\S]*?\{ signal \},[\s\S]*?\)/,
  "商品下钻请求必须接收并透传 React Query 取消信号",
);
assert.match(source, /recordReportLoadPerformance\("product", measurement\)/, "商品报告必须记录 2 秒预算结果");
assert.match(
  source,
  /recordReportLoadPerformance\(\s*drilldownLoadKindRef\.current === "supplier" \? "supplier-branches" : "product-branches",\s*measurement,?\s*\)/,
  "供应商和商品分店下钻必须分别记录首行可见耗时",
);
assert.match(
  source,
  /onFirstDataVisibilityChange=\{updateDrilldownFirstDataVisibility\}/,
  "分店下钻弹窗必须把第一条业务行可见事件回传给计时器",
);
assert.match(
  source,
  /InteractionManager\.runAfterInteractions[\s\S]*?firstRowRef\.current\?\.measureInWindow/,
  "分店下钻必须等弹窗动画结束并复测第一行与真实视口的交集",
);
assert.match(
  source,
  /const isSameDrilldownQuery = drilldownLoadQueryKeyRef\.current === queryKey;[\s\S]*?restorePhysicalState: isSameDrilldownQuery[\s\S]*?firstRowVisible: drilldownPhysicalRowVisibleRef\.current[\s\S]*?presentationReady: drilldownPhysicalPresentationReadyRef\.current/,
  "同 key 重试必须从独立物理 refs 恢复首行可见与弹窗展示状态",
);
assert.match(
  source,
  /hasUsableSuccessfulReportCache\(\s*queryClient\.getQueryState\(totalRevenueQueryKey\)\?\.status[\s\S]*?hasUsableSuccessfulReportCache\(\s*queryClient\.getQueryState\(supplierQueryKey\)\?\.status[\s\S]*?hasUsableSuccessfulReportCache\(\s*queryClient\.getQueryState\(productQueryKey\)\?\.status/,
  "商品主报表三个缓存都必须处于 success 状态才能按 warm 预算统计",
);
assert.match(
  source,
  /hasUsableSuccessfulReportCache\([\s\S]*?queryClient\.getQueryState\(supplierBranchQueryKey\)\?\.status[\s\S]*?cachedSupplierBranches/,
  "供应商下钻必须排除 error 缓存的 warm 分类",
);
assert.match(
  source,
  /hasUsableSuccessfulReportCache\([\s\S]*?queryClient\.getQueryState\(productBranchQueryKey\)\?\.status[\s\S]*?cachedProductBranches/,
  "商品下钻必须排除 error 缓存的 warm 分类",
);
assert.match(
  source,
  /drilldownPhysicalPresentationReadyRef\.current = true;[\s\S]*?drilldownPhysicalRowVisibleRef\.current = visible/,
  "商品下钻必须独立保存当前物理展示与可见状态",
);
assert.match(
  source,
  /const hasBusinessRows =[\s\S]*?supplierQuery\.data\?\.data\.length[\s\S]*?productQuery\.data\?\.data\.rows\.length[\s\S]*?productLoadTimer\.cancel\(\)/,
  "完整空商品与供应商结果必须取消 first-data 样本",
);
assert.doesNotMatch(
  source,
  /productSummaryRef/,
  "汇总卡可见不代表真实业务行可见，不能用于完成 report_first_data",
);
assert.match(
  source,
  /const firstSupplierReportRowRef = useRef<View>\(null\);[\s\S]*?const firstProductReportRowRef = useRef<View>\(null\);/,
  "商品主报表必须分别追踪第一条供应商行和第一条商品行",
);
assert.match(
  source,
  /\[firstSupplierReportRowRef, firstProductReportRowRef\]\.forEach\(\(rowRef\) => \{[\s\S]*?rowRef\.current\?\.measureInWindow[\s\S]*?x >= width[\s\S]*?x \+ measuredWidth <= 0[\s\S]*?y >= height[\s\S]*?y \+ measuredHeight <= 0[\s\S]*?firstProductReportDataVisibleRef\.current = true;[\s\S]*?completeProductLoad\(\)/,
  "汇总卡已可见但明细行仍在屏外时不得完成，只有真实供应商或商品行与视口相交后才能完成",
);
assert.match(
  source,
  /supplierPageRows\.map\(\(item, index\) => \([\s\S]*?ref=\{index === 0 \? firstSupplierReportRowRef : undefined\}[\s\S]*?onLayout=\{index === 0 \? scheduleProductDataVisibilityCheck : undefined\}/,
  "第一条供应商业务行布局或进入视口后必须触发真实可见性复测",
);
assert.match(
  source,
  /productRows\.map\(\(item, index\) => \([\s\S]*?ref=\{index === 0 \? firstProductReportRowRef : undefined\}[\s\S]*?onLayout=\{index === 0 \? scheduleProductDataVisibilityCheck : undefined\}/,
  "第一条商品业务行布局或进入视口后必须触发真实可见性复测",
);
assert.match(
  source,
  /const markProductDataVisible = useCallback\(\(\) => \{\s*if \(!productLoadActiveRef\.current\) return;/,
  "性能会话结束后滚动不得继续跨桥测量汇总卡",
);
assert.match(source, /onScroll=\{markProductDataVisible\}/, "滚动后必须重新核对首数据是否真实进入视口");
assert.doesNotMatch(source, /firstProductReportRowLaidOutRef/, "性能计时不得继续使用仅布局未可见的旧标记");
assert.match(source, /productReport\.sections\.pageSummary/, "商品页必须在明细前展示本页营业额、毛利额和毛利率汇总");
assert.match(
  source,
  /productTableRow:\s*\{[\s\S]*?gap: 3,[\s\S]*?paddingHorizontal: 4,[\s\S]*?\}/,
  "375px 窄屏必须完整容纳商品核心毛利列",
);
assert.match(
  source,
  /supplierTableRow:\s*\{[\s\S]*?gap: 3,[\s\S]*?paddingHorizontal: 4,[\s\S]*?\}/,
  "375px 窄屏必须完整容纳供应商核心毛利列",
);
assert.match(source, /rowNumberColumn:\s*\{\s*width: 24,/, "行号列不能挤占核心毛利字段空间");
assert.match(source, /productInfoColumn:\s*\{[\s\S]*?width: 80,/, "商品名称列需为毛利率保留首屏空间");
assert.match(source, /supplierNameColumn:\s*\{\s*width: 80,/, "供应商名称列需为毛利率保留首屏空间");
assert.match(source, /formatWholeMoney\(item\.revenue\)/, "供应商高密度排行必须用整元金额，避免大额数值被下钻箭头截断");

const supplierTitleStart = source.indexOf("function getSupplierTitle(");
assert.ok(supplierTitleStart >= 0, "供应商标题函数必须存在");
const supplierTitleBody = source.slice(supplierTitleStart, source.indexOf("\n}", supplierTitleStart));
assert.doesNotMatch(
  supplierTitleBody,
  /\$\{[^}]*supplierCode[^}]*\}/,
  "供应商标题不能把供应商代码拼进名称，代码由下方副行单独展示",
);
assert.doesNotMatch(
  supplierTitleBody,
  /slice\(/,
  "供应商名称不能按固定字数截断，窄列由 numberOfLines 自动省略",
);
assert.match(
  source,
  /shouldShowSupplierCode\(item\) \? item\.supplierCode : ""/,
  "供应商名称回退成代码时，副行必须留空避免上下重复显示同一串代码",
);

const supplierHeaderStart = source.indexOf("function SupplierTableHeader(");
const productHeaderStart = source.indexOf("function ProductTableHeader(");
assert.ok(supplierHeaderStart >= 0 && productHeaderStart > supplierHeaderStart, "供应商表头必须存在");
const supplierHeader = source.slice(supplierHeaderStart, productHeaderStart);
const productHeaderEnd = source.indexOf("function LoadingState(", productHeaderStart);
const productHeader = source.slice(productHeaderStart, productHeaderEnd);
assert.match(supplierHeader, /productReport\.metrics\.grossProfit/);
assert.match(supplierHeader, /productReport\.metrics\.grossMarginRate/);
assert.equal(
  (supplierHeader.match(/productReport\.metrics\.growthRate/g) ?? []).length,
  1,
  "供应商增长列只能保留一列"
);

const supplierRowStart = source.indexOf("const renderSupplierRow =");
const productPageTotalStart = source.indexOf("const renderChinaProductPageTotalRow =", productRowStart);
const mainReportReturnStart = source.indexOf("\n  return (\n    <View style={styles.container}>", productRowStart);
const drilldownStart = source.indexOf("function BranchDrilldownModal(");
const supplierBranchRowStart = source.indexOf("function SupplierBranchRow(", drilldownStart);
const productBranchRowStart = source.indexOf("function ProductBranchRow(", supplierBranchRowStart);
const productBranchRowEnd = source.indexOf("const styles = StyleSheet.create(", productBranchRowStart);
assert.ok(supplierRowStart >= 0 && productRowStart > supplierRowStart, "必须能够隔离供应商与商品主表行");
assert.ok(productPageTotalStart > productRowStart && mainReportReturnStart > productPageTotalStart, "必须能够隔离商品主表行与中国本页合计");
assert.ok(productHeaderEnd > productHeaderStart, "商品表头必须存在");
assert.ok(drilldownStart >= 0 && supplierBranchRowStart > drilldownStart, "分店下钻表头必须存在");
assert.ok(productBranchRowStart > supplierBranchRowStart, "供应商分店行必须存在");
assert.ok(productBranchRowEnd > productBranchRowStart, "商品分店行必须存在");

assertSourceOrder(
  supplierHeader,
  [
    "styles.chinaSupplierAmountColumn",
    "styles.chinaQuantityColumn",
    "styles.chinaShareColumn",
    "styles.chinaShareColumn",
    "styles.chinaAverageColumn",
    "styles.grossProfitColumn",
    "styles.grossMarginColumn",
    "styles.chinaGrowthColumn",
  ],
  "澳洲与中国供应商表头必须使用相同的中国页签列顺序",
);
const supplierRowSource = source.slice(supplierRowStart, productRowStart);
assertSourceOrder(
  supplierRowSource,
  [
    "styles.chinaSupplierAmountColumn",
    "styles.chinaQuantityColumn",
    "styles.chinaShareColumn",
    "styles.chinaShareColumn",
    "styles.chinaAverageColumn",
    "styles.grossProfitColumn",
    "styles.grossMarginColumn",
    "styles.chinaGrowthColumn",
  ],
  "澳洲与中国供应商行必须与统一表头保持相同列顺序",
);
assert.match(supplierRowSource, /const showComparison = isChinaKind;/, "澳洲供应商主表不得显示同期列组");
assert.match(supplierHeader, /\{showComparison \? \([\s\S]*?productReport\.metrics\.compare[\s\S]*?\) : null\}/, "供应商同期表头只能按页签条件显示");

const productColumnOrder = [
  "styles.productImageColumn",
  "infoWidth",
  "styles.chinaQuantityColumn",
  "styles.chinaProductAmountColumn",
  "styles.chinaAverageColumn",
  "styles.grossProfitColumn",
  "styles.grossMarginColumn",
  "styles.chinaGrowthColumn",
];
assertSourceOrder(
  productHeader,
  productColumnOrder,
  "澳洲与中国商品表头必须使用相同的中国页签列顺序",
);
const productMainRowSource = source.slice(productRowStart, productPageTotalStart);
assertSourceOrder(
  productMainRowSource,
  [
    "styles.productImageColumn",
    "productInfoWidth",
    "styles.chinaQuantityColumn",
    "styles.chinaProductAmountColumn",
    "styles.chinaAverageColumn",
    "styles.grossProfitColumn",
    "styles.grossMarginColumn",
    "styles.chinaGrowthColumn",
  ],
  "澳洲与中国商品行必须与统一表头保持相同列顺序",
);
assert.doesNotMatch(productHeader, /productReport\.metrics\.compare|chinaCompare/, "两个商品明细表头都不得显示同期列");
assert.doesNotMatch(
  productMainRowSource,
  /item\.compareQuantity|item\.compareAverageUnitPrice|item\.compareGrossProfit|item\.compareGrossMarginRate|styles\.chinaCompare/,
  "两个商品明细行都不得显示同期数值；同期金额只允许用于计算增长率",
);

const drilldownHeader = source.slice(drilldownStart, supplierBranchRowStart);
const productBranchHeaderStart = drilldownHeader.indexOf('{kind === "product" ? (', drilldownHeader.indexOf("styles.tableHeaderRow"));
const supplierBranchHeaderStart = drilldownHeader.indexOf(") : (", productBranchHeaderStart);
const drilldownHeaderEnd = drilldownHeader.indexOf('{kind === "supplier"', supplierBranchHeaderStart);
assert.ok(productBranchHeaderStart >= 0 && supplierBranchHeaderStart > productBranchHeaderStart, "必须能够隔离商品分店表头");
assert.ok(drilldownHeaderEnd > supplierBranchHeaderStart, "必须能够隔离供应商分店表头");
assertSourceOrder(
  drilldownHeader.slice(productBranchHeaderStart, supplierBranchHeaderStart),
  ["styles.productBranchCountColumn", "styles.productBranchMoneyColumn", "styles.productBranchAverageColumn", "styles.productBranchGrowthColumn", "styles.grossProfitColumn", "styles.grossMarginColumn"],
  "商品分店表毛利列必须位于所有经营字段之后",
);
assertSourceOrder(
  drilldownHeader.slice(supplierBranchHeaderStart, drilldownHeaderEnd),
  ["styles.chinaSupplierAmountColumn", "styles.chinaQuantityColumn", "styles.chinaAverageColumn", "styles.grossProfitColumn", "styles.grossMarginColumn", "styles.chinaGrowthColumn"],
  "澳洲与中国供应商分店表头必须使用相同的中国页签列顺序",
);
const supplierBranchRowSource = source.slice(supplierBranchRowStart, productBranchRowStart);
assertSourceOrder(
  supplierBranchRowSource,
  ["styles.chinaSupplierAmountColumn", "styles.chinaQuantityColumn", "styles.chinaAverageColumn", "styles.grossProfitColumn", "styles.grossMarginColumn", "styles.chinaGrowthColumn"],
  "澳洲与中国供应商分店行必须与统一表头保持相同列顺序",
);
assert.match(
  source,
  /showComparison=\{drilldown\?\.type === "supplier" && drilldown\.kind === "china"\}/,
  "供应商分店同期列只能在中国供应商下钻中显示",
);
assert.match(supplierBranchRowSource, /\{showComparison \? \([\s\S]*?styles\.chinaCompareAmountColumn/, "供应商分店同期值必须受同一条件控制");
assertSourceOrder(
  source.slice(productBranchRowStart, productBranchRowEnd),
  ["styles.productBranchAverageColumn", "renderGrowthCell", "renderGrossProfitCell", "renderGrossMarginCell"],
  "商品分店行必须与表头保持相同列顺序",
);

assert.match(source, /function FrozenHorizontalTable\(/, "宽表必须共用固定列横向滚动容器");
assert.match(
  source,
  /<Animated\.ScrollView[\s\S]*?horizontal[\s\S]*?Animated\.event\([\s\S]*?useNativeDriver: true/,
  "固定列必须由原生横向滚动事件驱动",
);
assert.match(source, /function FrozenLeadingColumns\([\s\S]*?translateX: scrollX/, "固定列必须反向补偿横向位移");
assert.match(
  source.slice(supplierRowStart, productRowStart),
  /<FrozenLeadingColumns[\s\S]*?styles\.rowNumberColumn[\s\S]*?supplierNameWidth[\s\S]*?<\/FrozenLeadingColumns>/,
  "供应商主表必须固定行号和供应商列",
);
assert.match(
  productMainRowSource,
  /<FrozenLeadingColumns[\s\S]*?styles\.productImageColumn[\s\S]*?productInfoWidth[\s\S]*?<\/FrozenLeadingColumns>/,
  "商品主表必须固定带排名的图片与货号名称列",
);
assert.match(
  supplierHeader,
  /<FrozenLeadingColumns[\s\S]*?styles\.rowNumberColumn[\s\S]*?nameWidth[\s\S]*?<\/FrozenLeadingColumns>/,
  "供应商表头必须与固定列对齐",
);
assert.match(
  productHeader,
  /<FrozenLeadingColumns[\s\S]*?styles\.productImageColumn[\s\S]*?infoWidth[\s\S]*?<\/FrozenLeadingColumns>/,
  "商品表头必须固定图片与货号名称列",
);
assert.match(
  drilldownHeader,
  /<FrozenLeadingColumns[\s\S]*?styles\.rowNumberColumn[\s\S]*?productBranchNameColumn[\s\S]*?branchColumn[\s\S]*?<\/FrozenLeadingColumns>/,
  "分店下钻表头必须按表型固定行号和分店列",
);
assert.match(
  source.slice(supplierBranchRowStart, productBranchRowStart),
  /<FrozenLeadingColumns[\s\S]*?styles\.rowNumberColumn[\s\S]*?styles\.branchColumn[\s\S]*?<\/FrozenLeadingColumns>/,
  "供应商分店表必须固定行号和分店列",
);
assert.match(
  source.slice(productBranchRowStart, productBranchRowEnd),
  /<FrozenLeadingColumns[\s\S]*?styles\.rowNumberColumn[\s\S]*?styles\.productBranchNameColumn[\s\S]*?<\/FrozenLeadingColumns>/,
  "商品分店表必须固定行号和分店列",
);

// ===== 中国供应商页签（2026-09-22 重设计）=====
assert.match(
  source,
  /const chinaBranchTotalsQuery = useQuery\(\{[\s\S]*?fetchChinaSupplierBranchTotals\(allBranchQueryParams!, \{ signal \}\)[\s\S]*?enabled: Boolean\(allBranchQueryParams\) && isChinaKind,/,
  "分店中国货合计只在中国页签请求、固定覆盖全部授权分店，并透传取消信号",
);
assert.match(
  source,
  /const mainReportCacheVersionState = getProductReportCacheVersionState\(\[[\s\S]*?productQuery\.data,\s*\.\.\.\(isChinaKind \? \[chinaBranchTotalsQuery\.data\] : \[\]\),\s*\.\.\.\(hasChinaSupplierBranches \? \[chinaSupplierBranchesQuery\.data\] : \[\]\),\s*\]\)/,
  "中国页签必须把分店中国货合计、选中供应商的分店分解并入同一统计批次校验",
);
assert.match(
  source,
  /const refetchMainReport = useCallback[\s\S]*?isChinaKind[\s\S]*?chinaBranchTotalsQueryKey[\s\S]*?\]\), \[/,
  "cacheVersion 协调重取在中国页签必须覆盖分店中国货合计",
);
assert.match(
  source,
  /const mainReportCurrentComplete =[\s\S]*?\(!isChinaKind \|\| chinaBranchTotalsQuery\.data\?\.isComplete === true\)/,
  "中国页签四块数据都 Fresh 才能展示并保存完整快照",
);
assert.match(
  source,
  /const mainReportRequestError =[\s\S]*?\(isChinaKind && chinaBranchTotalsQuery\.isError\)/,
  "分店中国货合计请求失败时必须走统一错误重试",
);
assert.match(
  source,
  /const CHINA_PRODUCT_DEFAULT_SORT: ReportSort = \{ field: "quantity", order: "desc" \};/,
  "中国页签商品明细默认按数量降序",
);
assert.match(
  source,
  /const applyKind = [\s\S]*?setProductSort\(getDefaultProductSort\(next\)\)/,
  "切换页签时商品排序回到该页签默认值",
);
assert.match(
  source,
  /\{isChinaKind \? \([\s\S]*?<ChinaGoodsSummaryCard summary=\{chinaGoodsSummary\} \/>[\s\S]*?<ChinaBranchShareSection\s+rows=\{chinaBranchShareRows\}[\s\S]*?\) : productSectionLoading \? \(/,
  "中国页签顶部展示中国货汇总与分店占比，澳洲页签保留本页商品汇总卡",
);

// ===== 中国页签三表联动（2026-09-29）=====
assert.match(
  source,
  /<ChinaBranchShareSection[\s\S]*?selectedBranchCode=\{selectedStoreCode \?\? null\}[\s\S]*?onSelectBranch=\{toggleLinkedBranch\}[\s\S]*?supplierName=\{getSupplierLabel\(branchTableSupplierCode\)\}[\s\S]*?updating=\{linkedStaleness\.branchTable\}/,
  "分店表点行选中分店（与顶部分店选择共用状态），选中供应商时显示该供应商在各分店的销售，重取期间标记更新中",
);
assert.match(
  source,
  /const chinaSupplierBranchesQuery = useQuery\(\{[\s\S]*?fetchSupplierBranchBreakdown\("china", allBranchQueryParams!, linkedSupplierCode!, \{ signal \}\)[\s\S]*?enabled: Boolean\(allBranchQueryParams\) && hasChinaSupplierBranches,/,
  "选中中国供应商后，分店表数据按全部授权分店请求该供应商的分店分解",
);
assert.match(
  source,
  /const linkedSupplierCode = isChinaKind \? selectedSupplierCode : null;/,
  "分店表按供应商联动只在中国页签生效",
);
assert.match(
  source,
  /const mainReportCurrentComplete =[\s\S]*?\(!hasChinaSupplierBranches \|\| chinaSupplierBranchesQuery\.data\?\.isComplete === true\)/,
  "选中供应商的分店分解 Fresh 后才能与其余主数据一起展示并保存快照",
);
assert.match(
  source,
  /const mainReportRequestError =[\s\S]*?\(hasChinaSupplierBranches && chinaSupplierBranchesQuery\.isError\)/,
  "选中供应商的分店分解失败时必须走统一错误处理",
);
assert.match(
  source,
  /summarizeChinaGoods\(\s*filterRowsByBranch\(chinaBranchTotalRows, selectedStoreCode\),\s*summaryRevenue\.revenue,/,
  "汇总卡只跟随所选分店（前端切片），不随供应商变化",
);
const applyStoreStart = source.indexOf("const applyStore = ");
assert.ok(applyStoreStart >= 0, "必须保留分店选择入口");
const applyStoreSource = source.slice(applyStoreStart, source.indexOf("\n  };", applyStoreStart));
assert.doesNotMatch(applyStoreSource, /setSelectedSupplierCode/, "三表联动：换分店必须保留已选供应商");
assert.doesNotMatch(
  productMainRowSource,
  /toggleLinked|setSelectedStoreCode|setSelectedSupplierCode|applyStore/,
  "明细不反向联动：点商品行只打开商品分店弹窗，不改变分店与供应商选择",
);
assert.match(
  source.slice(reportContentStart),
  /cashierEnabledStoreCodes\.length === 0 \? \([\s\S]*?\) : !displayedMainReport \? \([\s\S]*?<LoadingState label=\{t\("reports\.states\.refreshingStatistics"\)\} \/>/,
  "切页签或改日期后商品明细仍是占位数据、没有任何完整结果可展示时，必须停在整页加载，不能渲染零值汇总与空表",
);
assert.match(
  source,
  /const isRefreshing =[\s\S]*?\(productQuery\.isRefetching && !productQuery\.isPlaceholderData\)/,
  "明细占位重取属于局部加载，不能驱动下拉刷新转圈把整页往下推",
);
assert.equal(
  (source.match(/<View style=\{\{ width: tableViewportWidth \}\}>/g) ?? []).length,
  2,
  "供应商与商品宽表的空态必须限制在首屏可见宽度内居中，联动出现空交集时提示与清除按钮才看得见",
);
assert.match(source, /const LINKED_FILTER_BAR_INDEX = 2;/, "吸顶联动条必须位于日期行与页签之后");
assert.match(source, /stickyHeaderIndices=\{\[LINKED_FILTER_BAR_INDEX\]\}/, "联动条必须吸顶，受影响的表在屏幕外时仍能看到并清除选择");
assert.match(
  source,
  /<SegmentedButtons[\s\S]*?\/>\s*\{\/\*[^*]*\*\/\}\s*<LinkedFilterBar/,
  "吸顶联动条紧跟在页签之后（第 3 个直接子元素）",
);
assert.match(
  source,
  /<ProductTableHeader[\s\S]*?\{isChinaKind && productRows\.length > 0 \? renderChinaProductPageTotalRow\(scrollX\) : null\}[\s\S]*?<ScrollView/,
  "中国页签「本页合计」固定在商品表头下方，不随商品行纵向滚动",
);

assert.doesNotMatch(
  source,
  /renderChinaSupplierRow|renderChinaProductRow|ChinaSupplierTableHeader|ChinaProductTableHeader|ChinaSupplierBranchRow/,
  "澳洲与中国必须共用同一套供应商、商品和供应商分店渲染，避免列顺序再次漂移",
);

const supplierCompareColumnOrder = [
  "styles.chinaCompareAmountColumn",
  "styles.chinaCompareQuantityColumn",
  "styles.chinaShareColumn",
  "styles.chinaShareColumn",
  "styles.chinaAverageColumn",
  "styles.grossProfitColumn",
  "styles.grossMarginColumn",
];
const supplierRowComparisonStart = supplierRowSource.indexOf("{showComparison ? (");
const supplierHeaderComparisonStart = supplierHeader.indexOf("{showComparison ? (");
assert.ok(supplierRowComparisonStart > 0 && supplierHeaderComparisonStart > 0, "中国供应商同期列组必须位于当前值之后");
assertSourceOrder(
  supplierRowSource.slice(supplierRowComparisonStart),
  supplierCompareColumnOrder,
  "中国供应商行的同期列必须放在所有本期经营字段之后",
);
assertSourceOrder(
  supplierHeader.slice(supplierHeaderComparisonStart),
  supplierCompareColumnOrder,
  "中国供应商同期表头必须与同期数据列保持相同顺序",
);
assert.doesNotMatch(
  supplierRowSource.slice(0, supplierRowSource.indexOf("renderGrowthCell(")),
  /item\.compare/,
  "统一供应商行增长率之前的本期格子不得上下叠放同期值",
);

const pageTotalSource = source.slice(productPageTotalStart, mainReportReturnStart);
assertSourceOrder(
  pageTotalSource,
  [
    "styles.chinaQuantityColumn",
    "styles.chinaProductAmountColumn",
    "styles.chinaAverageColumn",
    "styles.grossProfitColumn",
    "styles.grossMarginColumn",
    "styles.chinaGrowthColumn",
  ],
  "中国商品本页合计必须与统一商品表保持相同本期列顺序",
);
assert.doesNotMatch(pageTotalSource, /styles\.chinaCompare/, "中国商品本页合计不得显示同期列");

const supplierBranchCompareColumnOrder = [
  "styles.chinaCompareAmountColumn",
  "styles.chinaCompareQuantityColumn",
  "styles.chinaAverageColumn",
  "styles.grossProfitColumn",
  "styles.grossMarginColumn",
];
const supplierBranchCompareStart = supplierBranchRowSource.indexOf("{showComparison ? (");
assert.ok(supplierBranchCompareStart > 0, "供应商分店同期列必须由 showComparison 控制");
assertSourceOrder(
  supplierBranchRowSource.slice(supplierBranchCompareStart),
  supplierBranchCompareColumnOrder,
  "中国供应商分店同期列必须放在所有本期经营字段之后",
);
assert.match(
  drilldownHeader,
  /\{showComparison \? \([\s\S]*?<TableHeaderGroup muted columnGap=\{8\} label=\{t\("productReport\.metrics\.compare"\)\}>/,
  "供应商分店同期表头只能在中国下钻显示，且子列间距与数据行一致",
);

const unifiedProductColumnOrder = [
  "styles.chinaQuantityColumn",
  "styles.chinaProductAmountColumn",
  "styles.chinaAverageColumn",
  "styles.grossProfitColumn",
  "styles.grossMarginColumn",
  "styles.chinaGrowthColumn",
];
assertSourceOrder(
  productMainRowSource,
  ["styles.productImageColumn", "productInfoWidth", ...unifiedProductColumnOrder],
  "澳洲与中国商品行必须共同使用图片、货号名称、数量、金额、均价、毛利、增长率顺序",
);
assertSourceOrder(
  productHeader,
  ["styles.productImageColumn", "infoWidth", ...unifiedProductColumnOrder],
  "统一商品表头必须与澳洲、中国商品行保持相同列顺序",
);

console.log("product-report-screen-contract.test.ts: ok");

for (const language of ["zh", "en"]) {
  const copy = JSON.parse(readFileSync(join(directory, `../../locales/${language}/common.json`), "utf8"));
  assert.ok(copy.productReport.metrics.productQuantity, "商品数量文案必须位于 productReport.metrics 分组");
  assert.ok(copy.productReport.metrics.productAveragePrice, "商品均价文案必须位于 productReport.metrics 分组");
  assert.doesNotMatch(copy.productReport.chinaGoods.productHint, /同期|\bLY\b/, "商品明细提示不得再引导用户查看同期列");
}
