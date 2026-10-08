import assert from "node:assert/strict";
import {
  CONTAINER_DETAIL_FIXED_COLUMN_WIDTH,
  CONTAINER_DETAIL_HEADER_HEIGHT,
  CONTAINER_DETAIL_ROW_HEIGHT,
  CONTAINER_DETAIL_SCROLL_COLUMNS,
  CONTAINER_DETAIL_SCROLL_COLUMNS_WIDTH,
  CONTAINER_DETAIL_TABLE_WIDTH,
  buildContainerDetailRowCells,
  buildContainerHeaderInfo,
  formatContainerDetailAmount,
  formatContainerDetailNumber,
  formatContainerDetailQuantity,
  getContainerDetailFieldLabelKey,
  getContainerDetailRowKey,
  getContainerDetailRowLayout,
  getContainerStatusKey,
  isCreatableNewProduct,
  isRetailPriceMissing,
  isSetChildDetail,
} from "./container-detail-table-columns";
import type { ContainerDetail } from "./types";

// ---- 尺寸与列定义 ----
assert.equal(CONTAINER_DETAIL_ROW_HEIGHT, 52, "行高固定 52，FlatList 才能用 getItemLayout");
assert.equal(CONTAINER_DETAIL_FIXED_COLUMN_WIDTH, 128, "固定首列宽约 128");
assert.ok(CONTAINER_DETAIL_HEADER_HEIGHT > 0);
assert.deepEqual(
  CONTAINER_DETAIL_SCROLL_COLUMNS.map((column) => column.key),
  ["status", "containerQuantity", "middlePack", "domesticPrice", "warehouseImportPrice", "importPrice", "retailPrice"],
  "滚动列顺序：状态 | 装柜数量 | 中包 | 国内价 | 实时进货价 | 进口价 | 零售价",
);
assert.equal(
  CONTAINER_DETAIL_SCROLL_COLUMNS_WIDTH,
  CONTAINER_DETAIL_SCROLL_COLUMNS.reduce((sum, column) => sum + column.width, 0),
);
assert.equal(CONTAINER_DETAIL_TABLE_WIDTH, CONTAINER_DETAIL_FIXED_COLUMN_WIDTH + CONTAINER_DETAIL_SCROLL_COLUMNS_WIDTH);
assert.ok(CONTAINER_DETAIL_SCROLL_COLUMNS.every((column) => column.width >= 56), "每列至少 56，数字不被截断");
assert.ok(CONTAINER_DETAIL_SCROLL_COLUMNS.filter((column) => column.key !== "status").every((column) => column.align === "right"), "数值列右对齐");

// ---- getItemLayout：行高固定，offset 线性 ----
assert.deepEqual(getContainerDetailRowLayout(null, 0), { length: 52, offset: 0, index: 0 });
assert.deepEqual(getContainerDetailRowLayout(null, 499), { length: 52, offset: 52 * 499, index: 499 });

// ---- 行 key ----
assert.equal(getContainerDetailRowKey({ hguid: " ABC " }, 3), "ABC");
assert.equal(getContainerDetailRowKey({ HGUID: "XYZ" }, 3), "XYZ");
assert.equal(getContainerDetailRowKey({ id: 7 }, 3), "id:7");
assert.equal(getContainerDetailRowKey({}, 3), "index:3");

// ---- 格式化 ----
assert.equal(formatContainerDetailNumber(12.5), "12.50");
assert.equal(formatContainerDetailNumber(0), "0.00");
assert.equal(formatContainerDetailNumber(undefined), "--");
assert.equal(formatContainerDetailNumber(Number.NaN), "--");
assert.equal(formatContainerDetailNumber(1.23456, 3), "1.235");
assert.equal(formatContainerDetailQuantity(1234), "1,234");
assert.equal(formatContainerDetailQuantity(12.6), "13");
assert.equal(formatContainerDetailQuantity(null), "--");
assert.equal(formatContainerDetailAmount(1234567.5), "1,234,567.50");
assert.equal(formatContainerDetailAmount(undefined), "--");

// ---- 零售价缺失判定 ----
assert.equal(isRetailPriceMissing(0, false), true, "零售价为 0 一定缺失");
assert.equal(isRetailPriceMissing(-1, true), true);
assert.equal(isRetailPriceMissing(9.9, true), false);
assert.equal(isRetailPriceMissing(undefined, true), true, "新商品取不到零售价按缺失处理");
assert.equal(isRetailPriceMissing(undefined, false), false, "已有商品取不到只显示 --，不标红");

// ---- 一行展示值 ----
const newProduct: ContainerDetail = {
  hguid: " g-1 ",
  商品信息: { 货号: "A100", 商品名称: "测试商品" },
  是否新商品: true,
  isContainerNewProduct: true,
  warehouseIsActive: true,
  装柜数量: 1200,
  中包数: 12,
  国内价格: 3.5,
  进口价格: 0.7,
  贴牌价格: 0,
  warehouseImportPrice: 0.65,
};
const cells = buildContainerDetailRowCells(newProduct);
assert.equal(cells.hguid, "g-1");
assert.equal(cells.itemNumber, "A100");
assert.equal(cells.productName, "测试商品");
assert.equal(cells.isNewProduct, true);
assert.equal(cells.isActive, true);
assert.equal(cells.containerQuantity, "1,200");
assert.equal(cells.middlePack, "12");
assert.equal(cells.domesticPrice, "3.50");
assert.equal(cells.warehouseImportPrice, "0.65");
assert.equal(cells.importPrice, "0.70");
assert.equal(cells.retailPrice, "0.00");
assert.equal(cells.retailPriceMissing, true, "新商品零售价 0：红色 + 警示图标");

// 已有商品：零售价取仓库实时零售价；建档后 是否新商品=false 但本柜新品仍显示「新品」
const created: ContainerDetail = {
  hguid: "g-2",
  商品信息: { 货号: "B200" },
  商品编码: "PC-2",
  是否新商品: false,
  isContainerNewProduct: true,
  warehouseIsActive: false,
  warehouseOEMPrice: 4.2,
};
const createdCells = buildContainerDetailRowCells(created);
assert.equal(createdCells.isNewProduct, true, "本柜新品建档后仍展示为新品");
assert.equal(isCreatableNewProduct(created), false, "但已建档，不能再次创建");
assert.equal(createdCells.isActive, false);
assert.equal(createdCells.retailPrice, "4.20");
assert.equal(createdCells.retailPriceMissing, false);
assert.equal(createdCells.productName, "");
assert.equal(createdCells.containerQuantity, "--");

// 没有货号时回退商品编码
assert.equal(buildContainerDetailRowCells({ 商品编码: "PC-9" }).itemNumber, "PC-9");

// 状态列提示图标：编码冲突或候选需确认
const conflict: ContainerDetail = { hasProductCodeConflict: true };
assert.equal(buildContainerDetailRowCells(conflict).needsAttention, true);
assert.equal(buildContainerDetailRowCells({ matchType: "supplierItem" }).needsAttention, true);
assert.equal(buildContainerDetailRowCells({ matchType: "productCode" }).needsAttention, false);
assert.equal(buildContainerDetailRowCells({ matchType: "unmatched" }).needsAttention, false, "未匹配（常见于新品）不打扰");

// ---- 套装子商品 ----
assert.equal(isSetChildDetail({ 商品类型: "套装子商品" }), true);
assert.equal(isSetChildDetail({ 商品信息: { 商品类型: "套装子商品" } }), true);
assert.equal(isSetChildDetail({ 商品类型: "普通商品" }), false);

// ---- 头部信息 ----
const header = buildContainerHeaderInfo({ 货柜编号: " HB-01 ", 状态: 1, 预计到岸日期: "2026-10-20T00:00:00", 实际到货日期: undefined });
assert.deepEqual(header, { containerNumber: "HB-01", status: 1, estimatedArrival: "2026-10-20", actualArrival: "" });
assert.deepEqual(buildContainerHeaderInfo(undefined), { containerNumber: "", status: undefined, estimatedArrival: "", actualArrival: "" });
assert.equal(getContainerStatusKey(0), "0");
assert.equal(getContainerStatusKey(7), "7");
assert.equal(getContainerStatusKey(3), null);
assert.equal(getContainerStatusKey(undefined), null);

// ---- 后端字段名映射 ----
assert.equal(getContainerDetailFieldLabelKey("商品名称"), "productName");
assert.equal(getContainerDetailFieldLabelKey("IsActive"), "isActive");
assert.equal(getContainerDetailFieldLabelKey("未知字段"), null);

console.log("container-detail-table-columns.test.ts: ok");
