import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { join } from "node:path";

// HB新品 页的仓库日期展示契约：货柜面板柜号下方只显示仓库日期（不再显示到店日期），
// 商品卡片在保留右侧「预计到店」块的前提下，左侧详情新增一行仓库日期。
const screen = readFileSync(join(__dirname, "container-new-products-screen.tsx"), "utf8");
const sheets = readFileSync(join(__dirname, "container-new-products-sheets.tsx"), "utf8");
const [zh, en] = ["zh", "en"].map((language) => JSON.parse(readFileSync(join(__dirname, `../../locales/${language}/screens/containerNewProducts.json`), "utf8")));

// 货柜面板：用仓库日期，且彻底不再引用到店日期区间
assert.match(sheets, /getWarehouseDateText\("containerSheet"/);
assert.doesNotMatch(sheets, /storeRange|formatArrivalDateRange|estimatedStoreArrivalDateEnd/, "货柜面板不应再显示到店日期");

// 商品卡片：仓库日期行单行显示，且保留原有的「预计到店」日期块
assert.match(screen, /getWarehouseDateText\("labels"/);
assert.match(screen, /styles\.warehouseDate\} numberOfLines=\{1\}/);
assert.match(screen, /t\("labels\.estimatedArrival"\)/, "右侧预计到店日期块必须保留");
assert.match(screen, /formatArrivalDateRange\(item\.estimatedStoreArrivalDate, item\.estimatedStoreArrivalDateEnd\)/);

// 仓库日期行在货柜行之后、新品/到仓状态标签行之前
const containerLine = screen.indexOf('t("labels.container"');
const warehouseLine = screen.indexOf("{warehouseDate ?");
const tagRow = screen.indexOf("<View style={styles.tagRow}>");
assert.ok(containerLine > 0 && warehouseLine > containerLine && tagRow > warehouseLine, "仓库日期行应在货柜行与标签行之间");

// 文案键两种语言都有；旧的到店区间文案键已删除
for (const messages of [zh, en]) {
  for (const scope of ["labels", "containerSheet"] as const) {
    assert.match(messages[scope].warehouseDate, /\{\{date\}\}/);
    assert.match(messages[scope].warehouseDateEstimated, /\{\{date\}\}/);
  }
  assert.equal("storeRange" in messages.containerSheet, false);
  assert.equal("storeRangeEstimated" in messages.containerSheet, false);
}
assert.equal(zh.labels.warehouseDate, "到仓库 {{date}}");
assert.equal(zh.labels.warehouseDateEstimated, "预计到仓库 {{date}}");
assert.equal(en.labels.warehouseDate, "In warehouse {{date}}");
assert.equal(en.labels.warehouseDateEstimated, "Est. in warehouse {{date}}");

console.log("container-new-products screen warehouse date contract tests passed");
