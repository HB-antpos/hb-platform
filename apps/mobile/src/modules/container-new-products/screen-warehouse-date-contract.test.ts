import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { join } from "node:path";

// HB新品 页的到仓库日期展示契约：
// - 商品卡片右列显示到仓库日期（原「预计到店」列的位置与写法），左侧只有货号/零售价/货柜/新品·到仓状态；
// - 货柜选择面板柜号下方只显示仓库日期；
// - 到店日期在界面上任何地方都不再显示。
const screen = readFileSync(join(__dirname, "container-new-products-screen.tsx"), "utf8");
const sheets = readFileSync(join(__dirname, "container-new-products-sheets.tsx"), "utf8");
const [zh, en] = ["zh", "en"].map((language) => JSON.parse(readFileSync(join(__dirname, `../../locales/${language}/screens/containerNewProducts.json`), "utf8")));

// ───────── 商品卡片 ─────────
// 右列用 getWarehouseDateColumn；没有仓库日期（旧版后端）整列不渲染，不回退到店日期
assert.match(screen, /getWarehouseDateColumn\(item\.basis, item\.warehouseArrivalDate\)/);
assert.match(screen, /\{warehouseColumn \? <View style=\{\[styles\.dateBlock, \{ width: dateWidth \}\]\}>/);
// 右列结构与原来的单日期写法一致：labelMedium 标签 + titleLarge 大字日期 + bodySmall 年份
assert.match(screen, /<Text variant="labelMedium" style=\{styles\.dateLabel\}>\{t\(warehouseColumn\.labelKey\)\}<\/Text>/);
assert.match(screen, /<Text variant="titleLarge" style=\{styles\.date\}>\{warehouseColumn\.day\}<\/Text>/);
assert.match(screen, /<Text variant="bodySmall" style=\{styles\.dateYear\}>\{warehouseColumn\.year\}<\/Text>/);
// 样式沿用现有 dateBlock 一组，没有新增样式，也没有 adjustsFontSizeToFit 之类缩字号的补丁
assert.match(screen, /dateBlock: \{ width: 100, borderLeftWidth: 1, borderLeftColor: HB_COLORS\.outlineMuted, paddingLeft: HB_SPACING\.sm \}/);
assert.match(screen, /date: \{ color: HB_COLORS\.action, fontWeight: "700", marginTop: 4 \}/);
assert.match(screen, /dateYear: \{ color: HB_COLORS\.textSecondary, marginTop: 2 \}/);
assert.doesNotMatch(screen, /adjustsFontSizeToFit|styles\.warehouseDate|dateEnd/, "左侧详情里不应再有仓库日期行");
// 到店日期块已撤掉：卡片不再读取到店日期，也不再有「预计到店」文案
assert.doesNotMatch(screen, /formatArrivalDateRange|labels\.estimatedArrival|date\.start|date\.end|estimatedStoreArrivalDateEnd/, "卡片不应再显示到店日期");
// 左侧详情仍是 货号 / 零售价 / 货柜 / 新品·到仓状态 四块，且在右列之前
const details = screen.indexOf("<View style={styles.details}>");
const tagRow = screen.indexOf("<View style={styles.tagRow}>");
const column = screen.indexOf("{warehouseColumn ?");
assert.ok(details > 0 && tagRow > details && column > tagRow, "右列应在左侧详情之后");
// 窄屏：dateWidth / imageSize 的计算保持不变
assert.match(screen, /const imageSize = width < 360 \? 72 : 88;/);
assert.match(screen, /const dateWidth = width < 360 \? 88 : 100;/);

// ───────── 货柜选择面板 ─────────
assert.match(sheets, /getWarehouseDateText\(option\.basis, option\.warehouseArrivalDate, localToday\)/);
assert.doesNotMatch(sheets, /storeRange|formatArrivalDateRange|estimatedStoreArrival/, "货柜面板不应再显示到店日期");

// ───────── 文案 ─────────
for (const messages of [zh, en]) {
  // 卡片右列标签；货柜面板整行文字仍保留
  assert.equal(typeof messages.labels.warehouseDateLabel, "string");
  assert.equal(typeof messages.labels.warehouseDateEstimatedLabel, "string");
  assert.match(messages.containerSheet.warehouseDate, /\{\{date\}\}/);
  assert.match(messages.containerSheet.warehouseDateEstimated, /\{\{date\}\}/);
  // 不再引用的键已删除
  for (const key of ["estimatedArrival", "warehouseDate", "warehouseDateEstimated"]) assert.equal(key in messages.labels, false, `labels.${key} 应已删除`);
  for (const key of ["storeRange", "storeRangeEstimated"]) assert.equal(key in messages.containerSheet, false, `containerSheet.${key} 应已删除`);
}
assert.equal(zh.labels.warehouseDateLabel, "到仓库");
assert.equal(zh.labels.warehouseDateEstimatedLabel, "预计到仓库");
assert.equal(en.labels.warehouseDateLabel, "In warehouse");
assert.equal(en.labels.warehouseDateEstimatedLabel, "Est. in warehouse");
assert.equal(zh.containerSheet.warehouseDate, "到仓库 {{date}}");
assert.equal(zh.containerSheet.warehouseDateEstimated, "预计到仓库 {{date}}");
assert.equal(en.containerSheet.warehouseDate, "In warehouse {{date}}");
assert.equal(en.containerSheet.warehouseDateEstimated, "Est. in warehouse {{date}}");
// 时间轴说明改为按到仓库日期
assert.equal(zh.labels.range, "按到仓库日期：过去 1 周 · 未来 3 周");
assert.equal(zh.labels.sortByArrival, "到仓日 ↑");
assert.equal(zh.messages.dateNoticeShort, "已到仓为实际日期，在途为预计日期");
// 中文文案里不再出现「到店」
assert.doesNotMatch(JSON.stringify(zh), /到店/, "中文文案不应再提到店日期");
assert.doesNotMatch(JSON.stringify(en), /[Ss]tore arrival|in store/, "英文文案不应再提到店日期");

console.log("container-new-products screen warehouse date contract tests passed");
