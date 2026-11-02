import assert from "node:assert/strict";
import { encodeCode128 } from "./receipt-code128";
import {
  RECEIPT_BARCODE_MODULE_DOTS,
  RECEIPT_COLUMNS,
  receiptBarRects,
  receiptDotsToPixels,
  receiptFontSize,
} from "./receipt-layout";

// 宽屏取上限 13；窄屏等比缩小；极窄取下限 8，永远不折行靠字号而不是换行。
assert.equal(receiptFontSize(600), 13);
assert.equal(receiptFontSize(346), 13);
assert.ok(receiptFontSize(300) < 13 && receiptFontSize(300) > 10);
assert.equal(receiptFontSize(120), 8);

// 缩小后的 42 列文字宽度不会超过可用宽度。
for (const width of [260, 300, 320, 346, 390]) {
  const fontSize = receiptFontSize(width);
  assert.ok(RECEIPT_COLUMNS * fontSize * 0.62 <= width + 0.001, `宽度 ${width} 放得下 42 列`);
}

// 8 位短码条码：123 模块 × 3 点 = 369 点，约占纸宽 64%。
const encoding = encodeCode128("6E0F13AB");
const barcodePixels = receiptDotsToPixels(encoding.moduleCount * RECEIPT_BARCODE_MODULE_DOTS, 13);
const paperPixels = receiptDotsToPixels(576, 13);
assert.ok(Math.abs(barcodePixels / paperPixels - 369 / 576) < 1e-9);

// 条的矩形：从 x=0 起交替，总宽不超过模块数，最后一个条贴着终止符。
const bars = receiptBarRects(encoding.runs);
assert.equal(bars[0].x, 0);
assert.ok(bars.every((bar) => bar.width >= 1 && bar.x + bar.width <= encoding.moduleCount));
assert.equal(bars.length, (encoding.runs.length + 1) / 2);

console.log("receipt-layout.test.ts: ok");
