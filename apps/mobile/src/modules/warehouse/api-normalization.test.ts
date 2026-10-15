import assert from "node:assert/strict";
import { normalizeWarehouseProduct } from "./api-normalization";
import type { WarehouseProductPatchRequest } from "./types";

function run() {
  const newFieldProduct = normalizeWarehouseProduct({
    ProductCode: "W-001",
    ProductName: "Warehouse Tea",
    WarehouseIsActive: false,
    IsActive: true,
  });

  assert.equal(newFieldProduct.warehouseIsActive, false, "新字段 WarehouseIsActive 必须优先生效");
  assert.equal(newFieldProduct.warehouseStatus, "offShelf", "warehouseIsActive=false 应映射为下架状态");
  assert.equal(newFieldProduct.isActive, false, "旧 isActive 兼容字段必须跟随仓库上下架状态");

  const camelFieldProduct = normalizeWarehouseProduct({
    productCode: "W-002",
    productName: "Warehouse Milk",
    warehouseIsActive: true,
    isActive: false,
  });

  assert.equal(camelFieldProduct.warehouseIsActive, true, "camelCase 新字段也必须优先生效");
  assert.equal(camelFieldProduct.warehouseStatus, "onShelf", "warehouseIsActive=true 应映射为上架状态");

  const legacyFallbackProduct = normalizeWarehouseProduct({
    ProductCode: "W-003",
    ProductName: "Warehouse Candy",
    IsActive: 0,
  });

  assert.equal(legacyFallbackProduct.warehouseIsActive, false, "缺少新字段时应回退旧 IsActive 字段");
  assert.equal(legacyFallbackProduct.warehouseStatus, "offShelf", "旧字段回退结果也应生成下架状态");

  const retailSyncPatch: WarehouseProductPatchRequest = {
    retailPrice: 12.99,
    oemPrice: 12.99,
    syncStoreRetailPrices: true,
    generateTasks: false,
  };

  assert.equal(
    retailSyncPatch.syncStoreRetailPrices,
    true,
    "零售价同步分店价格表必须使用 camelCase PATCH 字段"
  );
  assert.equal(
    retailSyncPatch.generateTasks,
    false,
    "是否生成价格任务必须使用 camelCase PATCH 字段 generateTasks"
  );

  console.log("api-normalization.test.ts: ok");
}

run();
