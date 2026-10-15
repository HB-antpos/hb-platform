import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import {
  DEFAULT_RETAIL_PRICE_SYNC_CHOICE,
  buildRetailPriceSyncQuery,
  buildRetailPriceSyncRequestFields,
  isBulkRetailPriceFilled,
  isEditRetailPriceChangeForExistingProduct,
} from "./retail-price-sync";
import type { ContainerDetail } from "./types";

const currentDir = dirname(fileURLToPath(import.meta.url));
const read = (relative: string) => readFileSync(resolve(currentDir, relative), "utf8");

// 默认两个开关都开。
assert.deepEqual({ ...DEFAULT_RETAIL_PRICE_SYNC_CHOICE }, { syncStoreRetailPrices: true, generateTasks: true });

// 本次没改零售价：不带任何字段；改了则两个字段都显式发送（关闭就是 false）。
assert.deepEqual(buildRetailPriceSyncRequestFields(DEFAULT_RETAIL_PRICE_SYNC_CHOICE, false), {});
assert.deepEqual(
  buildRetailPriceSyncRequestFields({ syncStoreRetailPrices: false, generateTasks: true }, true),
  { syncStoreRetailPrices: false, generateTasks: true },
);
assert.deepEqual(
  buildRetailPriceSyncRequestFields({ syncStoreRetailPrices: true, generateTasks: false }, true),
  { syncStoreRetailPrices: true, generateTasks: false },
);

// 查询串：没有字段 = 空串；只有给出的字段才出现。
assert.equal(buildRetailPriceSyncQuery(), "");
assert.equal(buildRetailPriceSyncQuery({}), "");
assert.equal(buildRetailPriceSyncQuery({ syncStoreRetailPrices: false, generateTasks: false }), "?syncStoreRetailPrices=false&generateTasks=false");
assert.equal(buildRetailPriceSyncQuery({ generateTasks: true }), "?generateTasks=true");

// 编辑弹窗：只有「已有商品 + 填了与当前不同的有效零售价」才出现开关。
const existing = { 是否新商品: false, 实时零售价: 5, WarehouseOEMPrice: 5 } as unknown as ContainerDetail;
const newProduct = { 是否新商品: true, 贴牌价格: 5 } as unknown as ContainerDetail;
assert.equal(isEditRetailPriceChangeForExistingProduct(existing, "6.66"), true, "已有商品改了零售价要出现开关");
assert.equal(isEditRetailPriceChangeForExistingProduct(existing, "5"), false, "填了和当前一样的价不算改");
assert.equal(isEditRetailPriceChangeForExistingProduct(existing, ""), false, "没填不算改");
assert.equal(isEditRetailPriceChangeForExistingProduct(existing, "abc"), false, "无效数字不算改");
assert.equal(isEditRetailPriceChangeForExistingProduct(newProduct, "6.66"), false, "新商品零售价只存货柜明细，不出现开关");

// 批量设价：填了有效零售价才出现。
assert.equal(isBulkRetailPriceFilled("7.77"), true);
assert.equal(isBulkRetailPriceFilled("  "), false);
assert.equal(isBulkRetailPriceFilled("abc"), false);

// 屏幕接线：默认勾选、打开重置、只在会改零售价时展示、提交带上开关、预览不受开关影响。
const screenSource = read("./container-detail-screen.tsx");
assert.match(screenSource, /useState<RetailPriceSyncChoice>\(\{ \.\.\.DEFAULT_RETAIL_PRICE_SYNC_CHOICE \}\)/, "两个开关默认勾选");
assert.match(
  screenSource,
  /setEditingDetail\(detail\);\s*setEditForm\(buildContainerDetailEditForm\(detail\)\);\s*setEditRetailPriceSync\(\{ \.\.\.DEFAULT_RETAIL_PRICE_SYNC_CHOICE \}\);/,
  "每次打开编辑弹窗都回到默认勾选",
);
assert.match(
  screenSource,
  /afterSheetClosed\(\(\) => setBulkModalType\(action\)\)/,
  "批量弹窗入口仍在",
);
assert.match(
  screenSource,
  /setBulkRetailPriceSync\(\{ \.\.\.DEFAULT_RETAIL_PRICE_SYNC_CHOICE \}\);\s*afterSheetClosed\(\(\) => setBulkModalType\(action\)\)/,
  "每次打开批量设价弹窗都回到默认勾选",
);
assert.match(
  screenSource,
  /editingDetail && isEditRetailPriceChangeForExistingProduct\(editingDetail, editForm\.oemPrice\) \? \(\s*<ContainerRetailPriceSyncSwitches/,
  "编辑弹窗只在已有商品零售价被改时展示开关",
);
assert.match(
  screenSource,
  /isBulkRetailPriceFilled\(bulkOemPrice\) \? \(\s*<ContainerRetailPriceSyncSwitches/,
  "批量设价弹窗只在填了零售价时展示开关",
);
assert.match(
  screenSource,
  /batchUpdateDetails\(containerGuid, \[submittedPayload\], retailPriceSync\)/,
  "保存明细把开关随请求提交",
);
assert.match(
  screenSource,
  /buildRetailPriceSyncRequestFields\(bulkRetailPriceSync, isBulkRetailPriceFilled\(bulkOemPrice\)\)/,
  "批量设价把开关随请求提交",
);
// 预览参数（previewContainerDetailBatchAction 的 parameters）里不能有开关：开关只决定副作用，不决定命中行。
const previewSource = screenSource.slice(
  screenSource.indexOf("const previewBulkMutation = useMutation"),
  screenSource.indexOf("const pushHqMutation = useMutation"),
);
assert.equal(/RetailPriceSync|syncStoreRetailPrices|generateTasks/.test(previewSource), false, "预览参数不含两个开关");

// 文案：中英文齐全，且与仓库商品页的零售价确认弹窗一致。
for (const language of ["zh", "en"] as const) {
  const containerLocale = JSON.parse(read(`../../locales/${language}/screens/containerDetail.json`)) as {
    retailSync: Record<string, string>;
  };
  const warehouseLocale = JSON.parse(read(`../../locales/${language}/screens/warehouse.json`)) as {
    product: Record<string, string>;
  };
  assert.equal(containerLocale.retailSync.storesLabel, warehouseLocale.product.retailSyncStoresLabel, `${language} 同步分店零售价标题应一致`);
  assert.equal(containerLocale.retailSync.storesHelper, warehouseLocale.product.retailSyncStoresHelper, `${language} 同步分店零售价说明应一致`);
  assert.equal(containerLocale.retailSync.tasksLabel, warehouseLocale.product.retailGenerateTasksLabel, `${language} 生成价格任务标题应一致`);
  assert.equal(containerLocale.retailSync.tasksHelper, warehouseLocale.product.retailGenerateTasksHelper, `${language} 生成价格任务说明应一致`);
}
const zhRetailSync = (JSON.parse(read("../../locales/zh/screens/containerDetail.json")) as { retailSync: Record<string, string> }).retailSync;
assert.equal(zhRetailSync.storesLabel, "同步分店零售价");
assert.equal(zhRetailSync.tasksLabel, "生成价格任务");

console.log("containers/retail-price-sync.test.ts: ok");
