import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const currentDir = dirname(fileURLToPath(import.meta.url));
const warehouseSource = readFileSync(resolve(currentDir, "../../../app/(shell)/warehouse.tsx"), "utf8");
const previewSource = readFileSync(
  resolve(currentDir, "../../components/price-updates/WarehousePriceNotificationFields.tsx"),
  "utf8"
);
const readLocale = (language: "zh" | "en") =>
  JSON.parse(readFileSync(resolve(currentDir, `../../locales/${language}/screens/warehouse.json`), "utf8")) as {
    product: Record<string, string>;
  };

// 打开确认弹窗时两个开关都默认开启（同步分店零售价 + 生成价格任务）。
assert.match(
  warehouseSource,
  /setPendingRetailPriceSync\(\{\s*retailPrice: numericInputModal\.value,\s*syncStoreRetailPrices: true,\s*generateTasks: true,\s*\}\)/,
  "零售价确认弹窗必须以「同步分店零售价 + 生成价格任务」均为 true 打开"
);

// 保存按钮提交的是两个开关的当前取值，不能再写死 true/false 二选一。
assert.match(
  warehouseSource,
  /field: "retailPrice",\s*syncStoreRetailPrices: pendingRetailPriceSync\.syncStoreRetailPrices,\s*generateTasks: pendingRetailPriceSync\.generateTasks,/,
  "零售价保存必须把两个开关的当前取值随请求提交"
);
assert.equal(
  /handleConfirmRetailPriceSync\((?:true|false)\)/.test(warehouseSource),
  false,
  "确认弹窗不再是同步/不同步二选一按钮"
);

// 两个开关各有标签和说明，且预告组件跟随当前选择。
for (const key of [
  "product.retailSyncStoresLabel",
  "product.retailSyncStoresHelper",
  "product.retailGenerateTasksLabel",
  "product.retailGenerateTasksHelper",
]) {
  assert.ok(warehouseSource.includes(`t("${key}")`), `确认弹窗必须使用文案 ${key}`);
}
assert.match(
  warehouseSource,
  /<RetailPriceNotificationPreview[^>]*syncStoreRetailPrices=\{pendingRetailPriceSync\.syncStoreRetailPrices\}[^>]*generateTasks=\{pendingRetailPriceSync\.generateTasks\}/s,
  "改价预告必须跟随两个开关的当前选择"
);
assert.match(
  previewSource,
  /generateTasks \? buildPriceNotificationPreviewMessage\(preview, syncStoreRetailPrices, t\) : null/,
  "不生成价格任务时不会通知分店，预告必须隐藏"
);

// 中英文都要有四条文案，说明文字与产品约定一致。
for (const language of ["zh", "en"] as const) {
  const product = readLocale(language).product;
  for (const key of ["retailSyncStoresLabel", "retailSyncStoresHelper", "retailGenerateTasksLabel", "retailGenerateTasksHelper"]) {
    assert.ok(product[key]?.trim(), `${language} 缺少 warehouse.product.${key}`);
  }
}
const zh = readLocale("zh").product;
assert.equal(zh.retailSyncStoresLabel, "同步分店零售价");
assert.equal(zh.retailGenerateTasksLabel, "生成价格任务");
assert.equal(
  zh.retailSyncStoresHelper,
  "开启后直接把各分店零售价改成新价；关闭则不动分店价。是否通知分店、是否换标签，由下面的「生成价格任务」决定"
);
assert.equal(
  zh.retailGenerateTasksHelper,
  "开启后给分店生成价格更新任务（同时开启「同步分店零售价」是「待换标签」任务，否则是「改价格」任务）；关闭则不通知分店、不发任何任务，已同步的分店价会立即同步总部 HQ（价签不会换）"
);

console.log("retail-price-confirm-source.test.ts: ok");
