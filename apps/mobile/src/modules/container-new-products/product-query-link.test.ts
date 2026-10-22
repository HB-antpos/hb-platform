import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { findSameItem, isAwaitingWarehouseCreation, PRODUCT_QUERY_PATH, resolveProductTap, resolveRecheck } from "./product-query-link";
import type { ContainerNewProductItem } from "./types";

function item(overrides: Partial<ContainerNewProductItem> = {}): ContainerNewProductItem {
  return {
    productCode: "P-1", hbProductNo: "HW1", quantity: null, imageUrl: null, barcode: null, retailPrice: null,
    containerNumber: null, containerCode: "C-1", estimatedStoreArrivalDate: "2026-10-27", estimatedStoreArrivalDateEnd: null,
    warehouseArrivalDate: "2026-10-23", basis: "actual", isNewProduct: true, isCreated: true, ...overrides,
  };
}

// 已建档：打开商品查询页，带商品编码与当前门店（本店没有分店价时由商品查询页补建，这里不拦）
assert.deepEqual(resolveProductTap(item(), "1013"), {
  type: "open", pathname: PRODUCT_QUERY_PATH, params: { productCode: "P-1", storeCode: "1013" },
});
// 旧版后端没有该字段（null = 未知）：放行
assert.equal(resolveProductTap(item({ isCreated: null }), "1013").type, "open");
// 明确未建档：不跳转，提示等仓库创建
assert.deepEqual(resolveProductTap(item({ isCreated: false }), "1013"), { type: "pending" });
assert.equal(isAwaitingWarehouseCreation(item({ isCreated: false })), true);
assert.equal(isAwaitingWarehouseCreation(item({ isCreated: null })), false);
// 新商品标记与建档状态无关：本柜新建的新品已建档时照样能打开
assert.equal(resolveProductTap(item({ isNewProduct: true, isCreated: true }), "1013").type, "open");

// 刷新后找回同一行：同一货柜同一商品，不区分大小写；别的货柜里的同一商品不算
const pendingRow = item({ productCode: "P-9", containerCode: "C-2", isCreated: false });
assert.equal(findSameItem([item({ productCode: "p-9", containerCode: "C-3" }), item({ productCode: "p-9", containerCode: "c-2" })], pendingRow)?.containerCode, "c-2");
assert.equal(findSameItem([item()], pendingRow), undefined);

// 重新检查的结论
assert.equal(resolveRecheck(null, pendingRow), "failed");
assert.equal(resolveRecheck([pendingRow], pendingRow), "stillPending");
assert.equal(resolveRecheck([{ ...pendingRow, isCreated: true }], pendingRow), "open");
assert.equal(resolveRecheck([{ ...pendingRow, isCreated: null }], pendingRow), "open");
// 刷新后该行已不在列表（货柜移出时间窗）：交给商品查询页
assert.equal(resolveRecheck([], pendingRow), "open");

// 页面源码契约：卡片可点、按 resolveProductTap 分流；未建档弹窗在「重新检查」时刷新列表再判断，不靠商品详情接口的报错文字
const currentDir = dirname(fileURLToPath(import.meta.url));
const screen = readFileSync(resolve(currentDir, "./container-new-products-screen.tsx"), "utf8");
assert.match(screen, /resolveProductTap\(item, storeCode\)/, "点击必须经 resolveProductTap 分流");
assert.match(screen, /onPress=\{\(\) => handleProductTap\(item\)\}/, "卡片必须可点击");
assert.match(screen, /resolveRecheck\(/, "重新检查必须按刷新后的列表判断");
assert.match(screen, /query\.refetch\(\)/, "重新检查必须刷新列表");
assert.doesNotMatch(screen, /getProductFastDetail/, "不能靠商品详情接口的报错文字判断是否已建档");
assert.match(screen, /t\("pendingDialog\.title"\)/, "未建档必须弹出等待提示");
assert.match(screen, /t\("labels\.notCreatedTag"\)/, "未建档卡片必须显示标签");

console.log("product-query-link.test.ts: ok");
