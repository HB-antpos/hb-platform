import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const directory = dirname(fileURLToPath(import.meta.url));
const sheet = readFileSync(resolve(directory, "SalesOrderDetailSheet.tsx"), "utf8");

// 明细行必须渲染商品缩略图，且走 COS 缩略图参数，避免列表里加载原图。
assert.match(
  sheet,
  /<ProductImageBox[^>]*uri=\{productThumbnailUri\(line\.productImage,/,
  "POS 订单明细行必须用 productThumbnailUri(line.productImage) 显示商品图",
);

// 三套 UI 文案里入口名统一为「POS 订单 / POS orders」。
for (const [language, label] of [
  ["zh", "POS 订单"],
  ["en", "POS orders"],
] as const) {
  const common = JSON.parse(
    readFileSync(resolve(directory, `../../locales/${language}/common.json`), "utf8"),
  );
  assert.equal(common.tabs.salesOrders, label);
}

console.log("sales-order-detail-line-image-contract.test.ts: ok");
