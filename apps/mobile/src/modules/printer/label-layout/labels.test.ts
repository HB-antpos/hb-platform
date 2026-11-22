import assert from "node:assert/strict";
import { test } from "node:test";

import type { ProductLabelPrintPayload } from "@/modules/printer/types";
import type { LabelDoc, LabelDocBitmap } from "./label-doc";
import { labelDocToCgBytes, labelDocToEgCommand } from "./label-doc";
import {
  buildBigDiscountLabelDoc,
  buildClearanceLabelDoc,
  buildDiscountLabelDoc,
  buildProductLabelDoc,
  buildWarehouseLocationLabelDoc,
  buildWarehouseProductLabelDoc,
  qrCode,
  qrModules,
} from "./labels";
import { createFakeRenderer } from "./testing/fake-renderer";

const now = new Date(2026, 10, 23);
const product: ProductLabelPrintPayload = {
  productName: "Gift Wrap Kraft Paper Roll 70cm x 3m Assorted Designs",
  itemNumber: "VD028",
  grade: "a",
  supplierName: "Art Wrap Pty Ltd",
  barcode: "9313559558795",
  retailPrice: 12.99,
  discountRate: 0.25,
};

function bitmaps(doc: LabelDoc) {
  return doc.items.filter((item): item is LabelDocBitmap => item.kind === "bitmap");
}

function commands(doc: LabelDoc) {
  return doc.items.flatMap((item) => (item.kind === "command" ? item.lines : []));
}

function ctx() {
  return { ...createFakeRenderer(), now };
}

/** 每张标签都要能序列化成 EG 与 CG，且所有位图都落在页面内。 */
function assertPrintable(doc: LabelDoc) {
  assert.match(labelDocToEgCommand(doc), /^! 0 200 200 \d+ 1\r\nPAGE-WIDTH \d+\r\n[\s\S]*PRINT\r\n$/);
  assert.ok(labelDocToCgBytes(doc).length > 0);
  for (const { x, y, bitmap } of bitmaps(doc)) {
    assert.ok(x >= 0 && y >= 0, `bitmap at ${x},${y} is off the page`);
    assert.ok(y + bitmap.height <= doc.height, `bitmap at ${x},${y} overflows the page height`);
  }
}

test("普通商品标签：坐标按 Kotlin 公式；合法 EAN13 走 EAN13 且 x=30；小号纸 472×320", async () => {
  const { renderer, calls } = ctx();
  const doc = await buildProductLabelDoc(product, null, { renderer, now });
  assert.equal(doc.width, 570);
  assert.equal(doc.height, 400);
  const [name, item, supplier] = bitmaps(doc);
  assert.deepEqual([name.x, name.y, item.x, item.y], [5, 5, 5, 120]);
  assert.deepEqual([supplier.x, supplier.y], [5 + item.bitmap.width + 10, 118]);
  assert.deepEqual(commands(doc), ["BARCODE-TEXT 7 0 5", "BARCODE EAN13 1 2 30 30 145 9313559558795"]);
  // 价格的小数贴右边：x = 宽 - 小数宽，y = 30。
  const decimal = bitmaps(doc).find((entry) => entry.y === 30 && entry.x + entry.bitmap.width === 570);
  assert.ok(decimal, "decimal part should be right-aligned at y=30");
  // 文字合批：一次 render + 一次 measure 画完全部单行文字，名称换行另外几次。
  assert.ok(calls.length <= 6, `native calls: ${calls.length}`);
  assertPrintable(doc);

  const small = await buildProductLabelDoc({ ...product, barcode: "ABC-123", discountRate: 0, grade: "" }, " Small ", ctx());
  assert.deepEqual([small.width, small.height], [472, 320]);
  assert.deepEqual(commands(small), ["BARCODE-TEXT 7 0 5", "BARCODE 128 1 2 30 5 145 ABC-123"]);
  assertPrintable(small);
});

test("特价标签：新价 = 原价×(1-折扣) 按分五成双；有原价才画 WAS 与删除线；二维码按 64 点占位居中", async () => {
  const doc = await buildDiscountLabelDoc(product, null, ctx());
  const lines = commands(doc);
  const strike = lines.find((line) => line.startsWith("LINE "));
  assert.ok(strike, "strike-through line expected when the original price is higher");
  // 13 位数字：QR 版本 1（21 模块）× 3 点 = 63，在 64 框里偏移 0；框底贴 194。
  assert.deepEqual(lines.slice(0, 3), ["BARCODE QR 10 130 M 2 U 3", "LA,9313559558795", "ENDQR"]);
  assertPrintable(doc);

  const noDiscount = await buildDiscountLabelDoc({ ...product, discountRate: 0 }, null, ctx());
  assert.equal(commands(noDiscount).some((line) => line.startsWith("LINE ")), false);
  // 没有条码时用货号做二维码。
  const itemOnly = await buildDiscountLabelDoc({ ...product, barcode: "" }, "small", ctx());
  assert.ok(commands(itemOnly).includes("LA,VD028"));
  assertPrintable(itemOnly);
});

test("特价标签：超长价格整组等比缩小，不截断（缩到价格区内）", async () => {
  const doc = await buildDiscountLabelDoc({ ...product, retailPrice: 123456.78, discountRate: 0.05 }, "small", ctx());
  const price = bitmaps(doc)[1];
  assert.ok(price.bitmap.width <= 472 - 12 - 160, `price width ${price.bitmap.width}`);
  assert.equal(price.x + price.bitmap.width, 472 - 12);
  assertPrintable(doc);
});

test("清货标签：614×205，二维码在 CLEARANCE 上方、左边 5 点", async () => {
  const doc = await buildClearanceLabelDoc({ ...product, clearanceBarcode: "C123", clearancePrice: 4.5 }, ctx());
  assert.deepEqual([doc.width, doc.height], [614, 205]);
  const qr = commands(doc)[0];
  assert.match(qr, /^BARCODE QR 5 \d+ M 2 U 3$/);
  assert.ok(commands(doc).includes("LA,C123"));
  assertPrintable(doc);
});

test("大折扣标签：表头三种（指定标题 / 1/2 PRICE / Special）与删除线、条码", async () => {
  const titled = await buildBigDiscountLabelDoc(product, " HOT DEAL ", ctx());
  assert.equal(titled.width, 480);
  assert.equal(bitmaps(titled)[0].y, 80);
  assert.equal(commands(titled).filter((line) => line.startsWith("LINE ")).length, 2);
  assert.ok(commands(titled).includes("BARCODE 128 1 2 30 15 560 9313559558795"));
  assertPrintable(titled);

  const half = await buildBigDiscountLabelDoc({ ...product, discountRate: 0.5 }, null, ctx());
  assert.deepEqual(bitmaps(half).slice(0, 2).map((entry) => [entry.x, entry.y]), [[130, 20], [120, 20 + bitmaps(half)[0].bitmap.height]]);

  const special = await buildBigDiscountLabelDoc({ ...product, discountRate: 0.05 }, null, ctx());
  assert.equal(bitmaps(special)[0].y, 40);
  const noDiscount = await buildBigDiscountLabelDoc({ ...product, discountRate: 0, barcode: "" }, null, ctx());
  assert.equal(commands(noDiscount).length, 0);
});

test("仓库商品标签：有条码时价格信息靠右（x=380），无条码时居中", async () => {
  const payload = {
    productCode: "G1",
    productName: "Gift Wrap",
    itemNumber: "VD028",
    barcode: "9313559558795",
    middlePackageQuantity: 12,
    purchasePrice: 3.2,
    retailPrice: 12.99,
    locationCode: "A-01-03",
  };
  const doc = await buildWarehouseProductLabelDoc(payload, ctx());
  assert.deepEqual(commands(doc), ["BARCODE-TEXT 7 0 5", "BARCODE 128 1 1 38 24 146 9313559558795"]);
  assert.deepEqual(bitmaps(doc).slice(-3).map((entry) => [entry.x, entry.y]), [[380, 132], [380, 156], [380, 180]]);
  assertPrintable(doc);

  const noBarcode = await buildWarehouseProductLabelDoc({ ...payload, barcode: "", middlePackageQuantity: 1 }, ctx());
  assert.equal(commands(noBarcode).length, 0);
  const [cost, rrp] = bitmaps(noBarcode).slice(-2);
  assert.equal(cost.x, Math.trunc((570 - cost.bitmap.width) / 2));
  assert.equal(rrp.y, 180);
});

test("仓库货位标签：代码在上方 2/3 居中，超长代码先量再选字号（不会请求超大位图）", async () => {
  const doc = await buildWarehouseLocationLabelDoc({ locationGuid: "g", locationCode: "A-01-03", productCount: 1 }, ctx());
  assert.deepEqual(commands(doc), ["BARCODE-TEXT OFF", "CENTER", "BARCODE 128 1 1 44 0 151 A-01-03"]);
  const [code] = bitmaps(doc);
  assert.ok(code.bitmap.width <= 540 && code.bitmap.height <= 120);
  assert.equal(code.x, Math.trunc((570 - code.bitmap.width) / 2));

  // 假渲染器和原生一样，单图超过 2048 点宽会报错：长代码必须先缩字再画。
  const { renderer, calls } = createFakeRenderer();
  const long = await buildWarehouseLocationLabelDoc(
    { locationGuid: "g", locationCode: "WAREHOUSE-ZONE-B-AISLE-12-SHELF-04-BIN-17-EXTRA-LONG-SUFFIX", productCount: 1 },
    { renderer, now }
  );
  assert.ok(bitmaps(long)[0].bitmap.width <= 540);
  assert.ok(calls.every((call) => call.op === "measure" || call.items.every((item) => item.size < 90)));

  const empty = await buildWarehouseLocationLabelDoc({ locationGuid: "", productCount: 0 }, ctx());
  assert.equal(commands(empty).length, 0, "全空时不出条码，只打占位符");
});

test("二维码版本估算：数字 / 字母数字 / 字节三种模式", () => {
  assert.equal(qrModules("9313559558795"), 21);
  assert.equal(qrModules("VD028-ABCDEFGHIJKLMNOPQRS"), 21);
  assert.equal(qrModules("vd028-lowercase-bytes!"), 25);
  // 25 模块 × 2 点 = 50，在 64 框里居中偏移 7。
  assert.deepEqual(qrCode(10, 100, "vd028-lowercase-bytes!").lines[0], "BARCODE QR 17 107 M 2 U 2");
});
