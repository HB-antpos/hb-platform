import assert from "node:assert/strict";
import { buildWarehouseProductPatchRequest, type WarehouseProductFormSnapshot } from "./product-patch";

const baseForm: WarehouseProductFormSnapshot = {
  purchasePrice: "4.28",
  retailPrice: "11.99",
  domesticPrice: "",
  stockQuantity: "140",
  middlePackageQuantity: "0",
  packingQuantity: "450",
  volume: "0.11",
  grade: "D",
  warehouseIsActive: true,
};

function parseNullableNumber(value: string) {
  if (!value.trim()) {
    return null;
  }
  return Number(value);
}

function run() {
  const statusOnly = buildWarehouseProductPatchRequest(
    { ...baseForm, warehouseIsActive: false },
    parseNullableNumber,
    { statusOnly: true }
  );

  assert.deepEqual(
    statusOnly,
    { warehouseIsActive: false },
    "上下架弹窗保存必须只提交仓库上下架字段，不能夹带价格、库存、等级"
  );

  const fullPatch = buildWarehouseProductPatchRequest(baseForm, parseNullableNumber);
  assert.equal(fullPatch.warehouseIsActive, true, "普通业务保存仍应携带当前仓库上下架状态");
  assert.equal(fullPatch.purchasePrice, 4.28, "普通业务保存应保留原有价格字段");
  assert.equal(fullPatch.packingQuantity, 450, "普通业务保存应保留原有装箱数字段");
  assert.equal(
    fullPatch.syncStoreRetailPrices,
    false,
    "整表保存没有确认弹窗，后端缺省改为同步后仍须显式不动分店价，保持原行为"
  );
  assert.equal(fullPatch.generateTasks, true, "整表保存仍由分店收到改价任务");

  const importPricePatch = buildWarehouseProductPatchRequest(baseForm, parseNullableNumber, {
    field: "purchasePrice",
  });

  assert.deepEqual(
    importPricePatch,
    { purchasePrice: 4.28, importPrice: 4.28 },
    "进口价保存必须只提交 Product.PurchasePrice 与 WarehouseProduct.ImportPrice"
  );

  assert.equal("syncStoreRetailPrices" in importPricePatch, false, "进口价保存不能带同步分店零售价字段");
  assert.equal("generateTasks" in importPricePatch, false, "进口价保存不能带生成价格任务字段");

  const retailPricePatch = buildWarehouseProductPatchRequest(baseForm, parseNullableNumber, {
    field: "retailPrice",
    syncStoreRetailPrices: true,
    generateTasks: true,
  });

  assert.deepEqual(
    retailPricePatch,
    { retailPrice: 11.99, oemPrice: 11.99, syncStoreRetailPrices: true, generateTasks: true },
    "零售价保存必须同时带上是否同步分店价格与是否生成价格任务的明确意图"
  );

  // 确认弹窗默认两个开关都开；调用方没有传值时必须按「同步 + 生成任务」提交，不再默认不同步。
  const defaultRetailPricePatch = buildWarehouseProductPatchRequest(baseForm, parseNullableNumber, {
    field: "retailPrice",
  });
  assert.deepEqual(
    defaultRetailPricePatch,
    { retailPrice: 11.99, oemPrice: 11.99, syncStoreRetailPrices: true, generateTasks: true },
    "零售价保存缺省必须同步分店零售价并生成价格任务"
  );

  const noSyncPatch = buildWarehouseProductPatchRequest(baseForm, parseNullableNumber, {
    field: "retailPrice",
    syncStoreRetailPrices: false,
  });
  assert.equal(noSyncPatch.syncStoreRetailPrices, false, "关闭同步分店零售价必须原样传 false");
  assert.equal(noSyncPatch.generateTasks, true, "只关同步时生成价格任务仍按默认 true 传出");

  const noTaskPatch = buildWarehouseProductPatchRequest(baseForm, parseNullableNumber, {
    field: "retailPrice",
    generateTasks: false,
  });
  assert.equal(noTaskPatch.generateTasks, false, "关闭生成价格任务必须原样传 false");
  assert.equal(noTaskPatch.syncStoreRetailPrices, true, "只关任务时同步分店零售价仍按默认 true 传出");

  const bothOffPatch = buildWarehouseProductPatchRequest(baseForm, parseNullableNumber, {
    field: "retailPrice",
    syncStoreRetailPrices: false,
    generateTasks: false,
  });
  assert.deepEqual(
    bothOffPatch,
    { retailPrice: 11.99, oemPrice: 11.99, syncStoreRetailPrices: false, generateTasks: false },
    "两个开关都关时两个字段都必须显式传 false，不能被缺省值吞掉"
  );

  // 其它单字段保存即使调用方误传了两个开关，也不能把它们带进请求体。
  const domesticPricePatch = buildWarehouseProductPatchRequest(baseForm, parseNullableNumber, {
    field: "domesticPrice",
    syncStoreRetailPrices: true,
    generateTasks: true,
  });
  assert.deepEqual(domesticPricePatch, { domesticPrice: null }, "非零售价单字段保存不带同步/任务字段");

  const statusPatchWithFlags = buildWarehouseProductPatchRequest(baseForm, parseNullableNumber, {
    statusOnly: true,
    syncStoreRetailPrices: true,
    generateTasks: true,
  });
  assert.deepEqual(statusPatchWithFlags, { warehouseIsActive: true }, "上下架保存不带同步/任务字段");

  console.log("product-patch.test.ts: ok");
}

run();
