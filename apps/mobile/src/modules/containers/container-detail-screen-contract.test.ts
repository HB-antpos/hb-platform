import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import {
  CONTAINER_DETAIL_DEFAULT_PAGE_SIZE,
  CONTAINER_DETAIL_MATCH_TYPE_OPTIONS,
  CONTAINER_DETAIL_PAGE_SIZE_OPTIONS,
  CONTAINER_DETAIL_PRODUCT_TYPE_OPTIONS,
  CONTAINER_DETAIL_RANGE_FILTER_PAIRS,
  CONTAINER_DETAIL_SEARCH_FIELDS,
  CONTAINER_DETAIL_SORT_OPTIONS,
  CONTAINER_DETAIL_WAREHOUSE_STATUS_OPTIONS,
} from "./query";
import { CONTAINER_DETAIL_SCROLL_COLUMNS, getContainerDetailFieldLabelKey } from "./container-detail-table-columns";

/**
 * 货柜明细页重设计的源码契约：分页位置、虚拟化表格、创建新商品/提交整柜确认、权限闸门、
 * 主题 token、i18n 全量迁移。与 container-detail-edit/concurrency-contract 互补（那两个守并发保护）。
 */
const directory = dirname(fileURLToPath(import.meta.url));
const read = (name: string) => readFileSync(join(directory, name), "utf8");
const screen = read("container-detail-screen.tsx");
const table = read("container-detail-table.tsx");
const sheets = read("container-detail-sheets.tsx");
const confirmSheets = read("container-detail-confirm-sheets.tsx");
const header = read("container-detail-header.tsx");
const chip = read("container-detail-chip.tsx");
const uiSources = { screen, table, sheets, confirmSheets, header, chip };
const zh = JSON.parse(readFileSync(join(directory, "../../locales/zh/screens/containerDetail.json"), "utf8")) as Record<string, unknown>;
const en = JSON.parse(readFileSync(join(directory, "../../locales/en/screens/containerDetail.json"), "utf8")) as Record<string, unknown>;
const i18nSource = readFileSync(join(directory, "../../shared/i18n/i18n.ts"), "utf8");

const lookup = (bundle: Record<string, unknown>, key: string) =>
  key.split(".").reduce<unknown>((current, part) => (current as Record<string, unknown> | undefined)?.[part], bundle);
const stripComments = (source: string) => source.replace(/\/\*[\s\S]*?\*\//g, "").replace(/(^|[^:"'`\\])\/\/.*$/gm, "$1");

// ---------------------------------------------------------------------------
// 1. 分页条在表格上方，只有一个，没有底部翻页
// ---------------------------------------------------------------------------
const paginationAt = screen.indexOf("<PaginationBar");
const tableAt = screen.search(/<ContainerDetailTable\s/);
assert.ok(paginationAt > 0 && tableAt > 0, "页面必须渲染 PaginationBar 和 ContainerDetailTable");
assert.ok(paginationAt < tableAt, "PaginationBar 必须渲染在表格之前（表格上方）");
assert.equal(screen.split("<PaginationBar").length - 1, 1, "只能有一个分页条，不再有底部翻页条");
assert.match(screen, /from "@\/components\/ui\/pagination"/, "必须使用公共分页组件");
assert.match(screen, /pageSizeOptions=\{CONTAINER_DETAIL_PAGE_SIZE_OPTIONS\}/, "每页条数选项来自数据层常量");
assert.doesNotMatch(screen, /上一页|下一页|totalPages/, "旧的底部上一页/下一页不应残留");
assert.deepEqual([...CONTAINER_DETAIL_PAGE_SIZE_OPTIONS], [50, 100, 200, 500]);
assert.equal(CONTAINER_DETAIL_DEFAULT_PAGE_SIZE, 50);
assert.match(screen, /peekRememberedContainerDetailPageSize\(\) \?\? CONTAINER_DETAIL_DEFAULT_PAGE_SIZE/, "默认 50，并优先使用本机记住的值");
assert.match(
  screen,
  /const changePageSize = \(value: number\) => \{[\s\S]*rememberContainerDetailPageSize\(next\);[\s\S]*setPageSize\(next\);[\s\S]*resetPaging\(\);[\s\S]*\};/,
  "换每页条数必须记住并回到第 1 页",
);
assert.match(screen, /const resetPaging = \(\) => \{\s*setPage\(1\);\s*setSelectedHguids\(\[\]\);\s*\};/, "改变结果集时回第 1 页并清空勾选");

// ---------------------------------------------------------------------------
// 2. 表格：单个纵向 FlatList 虚拟化 + 原生驱动的固定列
// ---------------------------------------------------------------------------
assert.equal((table.match(/<FlatList\s/g) ?? []).length, 1, "只有一个纵向 FlatList，行对齐是结构性的，无需同步两个列表偏移");
for (const prop of ["getItemLayout={getContainerDetailRowLayout}", "windowSize={7}", "initialNumToRender={14}", "removeClippedSubviews={Platform.OS === \"android\"}", "maxToRenderPerBatch", "keyExtractor"]) {
  assert.ok(table.includes(prop), `表格 FlatList 必须设置 ${prop}`);
}
assert.match(table, /Animated\.event\(\s*\[\{ nativeEvent: \{ contentOffset: \{ x: scrollX \} \} \}\],\s*\{ useNativeDriver: true \}/, "横向滚动偏移必须由原生驱动");
assert.match(table, /transform: \[\{ translateX: scrollX \}\]/, "固定列通过 translateX 钉在左缘");
assert.ok((table.match(/translateX: scrollX/g) ?? []).length >= 2, "表头固定格与数据行固定格都要钉住");
assert.match(table, /const TableRow = memo\(/, "行必须 memo，避免勾选一行重渲染整页");
assert.doesNotMatch(table, /scrollToOffset\(\{ offset: [a-zA-Z]+\.y/, "不能再用 onScroll 去同步另一个列表");
assert.ok(table.includes("scrollToIndex"), "定位到行使用 scrollToIndex（行高固定，结果精确）");
assert.doesNotMatch(screen, /details\.map\(/, "页面不得再 map 渲染整页卡片");
assert.doesNotMatch(screen, /<DetailCard|<Card\b/, "旧的明细卡片已删除");
assert.doesNotMatch(screen, /<ScrollView[\s\S]{0,400}details\.map/, "不得出现 ScrollView + details.map 的整页渲染");
// 表格状态：加载骨架 / 空 / 列表错误 + 重试
assert.match(table, /status === "loading"/);
assert.match(table, /status === "error"/);
assert.match(table, /status === "empty"/);
assert.match(table, /onPress=\{onRetry\}/, "列表错误态必须有重试按钮");
assert.match(screen, /onRetry=\{\(\) => void productsQuery\.refetch\(\)\}/, "重试要重新请求明细");
assert.match(screen, /productsQuery\.isError && details\.length === 0/, "列表请求失败且没有数据时进入错误态");
// 下拉刷新：头部 + 列表
assert.match(screen, /Promise\.allSettled\(\[headerQuery\.refetch\(\), productsQuery\.refetch\(\)\]\)/, "下拉刷新同时刷新货柜头和明细列表");

// ---------------------------------------------------------------------------
// 3. 创建新商品确认
// ---------------------------------------------------------------------------
assert.match(screen, /const \[createSyncToHq, setCreateSyncToHq\] = useState\(true\)/, "同步 HQ 默认勾选，且不持久化");
assert.doesNotMatch(screen, /AsyncStorage|Storage\.set.*[Ss]yncToHq/, "同步 HQ 勾选不得持久化");
assert.match(screen, /const openCreateSheet = \(\) => \{[\s\S]*setCreateSyncToHq\(true\);[\s\S]*setCreateSheetOpen\(true\);/, "每次打开确认框都重置为勾选");
assert.equal(screen.split("createProductsMutation.mutate()").length - 1, 1, "创建新商品只能从确认框的确认键触发");
assert.match(screen, /onConfirm=\{\(\) => createProductsMutation\.mutate\(\)\}/);
assert.match(screen, /onPress=\{openCreateSheet\}/, "底部「创建新商品」只打开确认框");
assert.match(screen, /createNewProductsAndSyncHq\(\{[\s\S]*syncToHq: createSyncToHq/, "必须经 createNewProductsAndSyncHq 并带上勾选结果");
assert.match(screen, /findContainerDetailsMissingRetailPrice\(creatableDetails\)/, "缺零售价清单来自数据层 findContainerDetailsMissingRetailPrice");
assert.match(screen, /onLocate=\{locateMissingRow\}/, "缺价清单每行有「定位到该行」");
assert.match(screen, /tableRef\.current\?\.scrollToRow\(located\.index\)/, "定位要滚动表格到该行");
assert.match(screen, /located\.kind === "not-on-page"[\s\S]*messages\.locateNotOnPage/, "行不在当前页要提示清除筛选");
assert.match(confirmSheets, /accessibilityRole="checkbox"/, "确认框里有同步 HQ 复选框");
assert.match(confirmSheets, /checked: syncToHq/);
assert.match(confirmSheets, /disabled=\{blocked\}/, "存在缺零售价的新商品时确认键禁用");
assert.match(confirmSheets, /blocked \? <Text style=\{styles\.caption\}>\{t\("createProducts\.confirmDisabledCaption"\)\}/, "禁用时必须给出原因文案");
assert.match(confirmSheets, /borderColor: HB_COLORS\.danger/, "缺价面板使用红色边框");
assert.match(confirmSheets, /dismissable=\{phase !== "running"\}/, "执行中不能关闭确认框");
for (const [bundle, label] of [[zh, "zh"], [en, "en"]] as const) {
  assert.equal(typeof lookup(bundle, "createProducts.syncToHq"), "string", `${label} 缺少同步 HQ 文案`);
  assert.equal(typeof lookup(bundle, "createProducts.confirmDisabledCaption"), "string");
}
assert.equal(lookup(zh, "createProducts.warning"), "将为已选的 {{count}} 个新商品创建商品档案并上架，创建后不可撤销。");
assert.equal(lookup(zh, "createProducts.syncToHq"), "创建完成后同时更新 HQ 数据库");
assert.equal(lookup(zh, "createProducts.confirmDisabledCaption"), "补价后此按钮可用");
assert.equal(lookup(zh, "createProducts.locate"), "定位到该行");

// ---------------------------------------------------------------------------
// 4. 提交整柜确认
// ---------------------------------------------------------------------------
assert.match(screen, /<ContainerDetailSubmitSheet/, "提交整柜必须有确认框");
assert.equal(screen.split("submitMutation.mutate()").length - 1, 1, "提交整柜只能从确认框的确认键触发");
assert.match(screen, /onConfirm=\{\(\) => submitMutation\.mutate\(\)\}/);
assert.match(screen, /case "submitContainer":[\s\S]*setSubmitPhase\("confirm"\);[\s\S]*setSubmitSheetOpen\(true\)/, "菜单项只打开确认框");
assert.match(screen, /createSubmitJob\(\{[\s\S]*waitSubmitJob\(job\.jobId\)/, "保持原有任务轮询");
assert.match(confirmSheets, /submitContainer\.scope/, "确认框说明范围是整柜");
assert.match(String(lookup(zh, "submitContainer.scope")), /整个货柜.*整柜全部明细.*不受当前搜索、筛选和勾选影响/);

// ---------------------------------------------------------------------------
// 5. 权限闸门保持不变
// ---------------------------------------------------------------------------
assert.match(screen, /const canRunProductJobs = access\.canEditContainer && access\.hasPermission\("PosProducts\.Manage"\);/);
assert.match(screen, /const canAlignDomesticProductCode = canEditContainer && \(access\.isAdmin \|\| access\.hasPermission\("Products\.Edit"\)\);/);
assert.match(screen, /\{canRunProductJobs \? \(\s*<Button[\s\S]*openCreateSheet/, "创建新商品按钮受 canRunProductJobs 控制");
assert.match(sheets, /\{canEditContainer \? \(/, "批量编辑项受 canEditContainer 控制");
assert.match(sheets, /\{canRunProductJobs \? \(/, "推送 HQ / 提交整柜受 canRunProductJobs 控制");
assert.match(sheets, /\{canDeleteContainer \? \(/, "删除受 canDeleteContainer 控制");
assert.match(sheets, /const canAlign = canAlignDomesticProductCode && hasConflict && Boolean\(localProductCode && domesticProductCode\) && !isSetChildDetail\(detail\)/, "对齐编码的出现条件与旧版一致");
assert.match(screen, /canAlignDomesticProductCode=\{canAlignDomesticProductCode\}/);
assert.match(screen, /function handleRowPress/);
// 推送 HQ 使用默认字段并先确认
assert.match(screen, /runPushProductsToHqJob\(\{ containerGuid, selection \}\)/, "推送 HQ 使用默认可更新字段");
assert.match(screen, /case "pushHq":[\s\S]*confirmPushHq/, "推送 HQ 先确认");
// 导出保留
assert.match(screen, /case "exportExcel":[\s\S]*exportMutation\.mutate\("excel"\)/);
assert.match(screen, /case "exportPdf":[\s\S]*exportMutation\.mutate\("pdf"\)/);
// 全部筛选结果范围：未勾选时批量操作使用 buildBatchScope(detailQueryPayload, ...)
assert.match(screen, /buildBatchScope\(detailQueryPayload, selectedHguids\)/);
// 查询参数全部来自数据层
assert.match(screen, /buildContainerDetailQuery\(containerGuid, \{[\s\S]*searchField,[\s\S]*sort,[\s\S]*filters,[\s\S]*pageSize,/);
assert.match(screen, /resolveContainerDetailOverviewStats\(/, "概览卡必须使用整柜统计，不随搜索变化");
assert.match(sheets, /findInvalidContainerDetailRangePairs\(draft\)/, "筛选应用前必须校验区间");
assert.match(sheets, /if \(invalidPairs\.length\) return;/);

// ---------------------------------------------------------------------------
// 6. 主题 token：不允许硬编码十六进制颜色；页面用 token 背景
// ---------------------------------------------------------------------------
for (const [name, source] of Object.entries(uiSources)) {
  assert.doesNotMatch(stripComments(source), /#[0-9a-fA-F]{3,8}\b/, `${name} 不得硬编码十六进制颜色，必须用 HB_COLORS`);
}
assert.match(screen, /backgroundColor: HB_COLORS\.background/);
assert.doesNotMatch(stripComments(table), /shadow|elevation|LinearGradient/i, "无渐变、无阴影");

// ---------------------------------------------------------------------------
// 7. i18n：界面代码里没有中文字面量，所有静态文案键都存在于 zh/en
// ---------------------------------------------------------------------------
for (const [name, source] of Object.entries(uiSources)) {
  const code = stripComments(source);
  const hit = code.match(/[㐀-鿿]/);
  assert.equal(hit, null, `${name} 的代码里不应有中文字面量（应放入 containerDetail 命名空间）`);
}
assert.match(i18nSource, /containerDetail: containerDetailZh/);
assert.match(i18nSource, /containerDetail: containerDetailEn/);
assert.match(i18nSource, /"containerDetail"/);
const staticKeys = new Set<string>();
for (const source of Object.values({ ...uiSources, job: read("container-detail-job-results.ts") })) {
  for (const match of source.matchAll(/\bt\(\s*"([A-Za-z0-9_.]+)"/g)) staticKeys.add(match[1]);
  for (const match of source.matchAll(/\bkey: "([A-Za-z0-9_.]+)"/g)) staticKeys.add(match[1]);
}
// 带条件的 t(cond ? "a" : "b") 形式
for (const source of Object.values(uiSources)) {
  for (const match of source.matchAll(/\bt\([^()"]*\?\s*"([A-Za-z0-9_.]+)"\s*:\s*"([A-Za-z0-9_.]+)"/g)) {
    staticKeys.add(match[1]);
    staticKeys.add(match[2]);
  }
}
assert.ok(staticKeys.size > 80, "应能扫描到足够多的文案键");
for (const key of staticKeys) {
  assert.equal(typeof lookup(zh, key), "string", `zh 缺少文案键：${key}`);
  assert.equal(typeof lookup(en, key), "string", `en 缺少文案键：${key}`);
}
// 动态键：按数据层的枚举逐项校验
const dynamicKeys: string[] = [
  ...CONTAINER_DETAIL_SEARCH_FIELDS.map((item) => `searchField.${item.field}`),
  ...CONTAINER_DETAIL_SORT_OPTIONS.map((item) => `sort.options.${item.field}`),
  ...CONTAINER_DETAIL_RANGE_FILTER_PAIRS.map((item) => `filter.ranges.${item.key}`),
  ...CONTAINER_DETAIL_PRODUCT_TYPE_OPTIONS.map((item) => `filter.productTypeOptions.${item.value}`),
  ...CONTAINER_DETAIL_WAREHOUSE_STATUS_OPTIONS.map((item) => `filter.warehouseStatusOptions.${item.value}`),
  ...CONTAINER_DETAIL_MATCH_TYPE_OPTIONS.map((item) => `info.match.${item.value}`),
  ...CONTAINER_DETAIL_SCROLL_COLUMNS.map((item) => `table.columns.${item.key}`),
  "table.columns.product",
  ...["all", "new", "existing", "noOemPrice", "abnormalImport", "active", "inactive"].map((tag) => `tags.${tag}`),
  ...["0", "1", "2", "7"].map((status) => `status.${status}`),
  ...["float", "prices", "recalculate", "backfill", "pushHq", "submitContainer", "delete", "exportExcel", "exportPdf"].map((action) => `bulk.actions.${action}`),
  ...["商品名称", "英文名称", "国内价格", "进口价格", "贴牌价格", "调整浮率", "装柜数量", "中包数", "IsActive"].map((field) => `edit.fields.${getContainerDetailFieldLabelKey(field)}`),
];
for (const key of dynamicKeys) {
  assert.equal(typeof lookup(zh, key), "string", `zh 缺少动态文案键：${key}`);
  assert.equal(typeof lookup(en, key), "string", `en 缺少动态文案键：${key}`);
}
// 设计要求的搜索字段文案
assert.deepEqual(
  ["itemNumber", "productName", "barcode", "englishName"].map((field) => lookup(zh, `searchField.${field}`)),
  ["货号", "商品名", "条码", "英文名"],
);

console.log("container-detail-screen-contract.test.ts: ok");
