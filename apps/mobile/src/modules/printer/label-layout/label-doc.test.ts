import assert from "node:assert/strict";
import { test } from "node:test";

import { MonoBitmap } from "./mono-bitmap";
import {
  bitmapItem,
  commandItem,
  flattenBitmaps,
  labelDocToCgBytes,
  labelDocToEgCommand,
  renderLabelDocBitmap,
  type LabelDoc,
} from "./label-doc";

test("MonoBitmap：位操作、裁剪补边、叠画（or / copy）、缩放、十六进制", () => {
  const bitmap = new MonoBitmap(10, 2);
  assert.equal(bitmap.rowBytes, 2);
  bitmap.set(0, 0, true);
  bitmap.set(9, 1, true);
  assert.equal(bitmap.toHex(), "80000040");
  assert.equal(bitmap.get(9, 1), true);
  assert.equal(bitmap.get(20, 20), false);

  // 超出原图的区域按 outside 填充（反白文字补边要填黑）。
  const cropped = bitmap.crop(-1, 0, 3, 1, true);
  assert.deepEqual([cropped.get(0, 0), cropped.get(1, 0), cropped.get(2, 0)], [true, true, false]);

  const canvas = MonoBitmap.filled(4, 1, true);
  const white = new MonoBitmap(2, 1);
  canvas.draw(white, 1, 0, "or");
  assert.equal(canvas.toHex(), "F0");
  canvas.draw(white, 1, 0, "copy");
  assert.equal(canvas.toHex(), "90");

  const scaled = MonoBitmap.filled(4, 4, true).scale(2, 2);
  assert.equal(scaled.toHex(), "C0C0");
  assert.throws(() => new MonoBitmap(0, 1), /Invalid bitmap size/);
  assert.throws(() => new MonoBitmap(8, 1, new Uint8Array(2)), /expected 1/);
});

test("按字节补齐后会互相覆盖的位图被合成一张，补齐白边不再盖住邻居", () => {
  const a = MonoBitmap.filled(3, 2, true); // 实际 3 点宽，EG 占 8 点
  const b = MonoBitmap.filled(3, 2, true);
  // b 在 x=4，落在 a 的补齐区（4..7）里：旧做法 a 的补齐白点会把 b 擦掉（取决于顺序）。
  const merged = flattenBitmaps([
    { x: 0, y: 0, bitmap: a },
    { x: 4, y: 0, bitmap: b },
  ]);
  assert.equal(merged.length, 1);
  assert.equal(merged[0].bitmap.width, 7);
  assert.equal(merged[0].bitmap.toHex(), "EEEE");

  // 互不重叠的保持分开。
  const separate = flattenBitmaps([
    { x: 0, y: 0, bitmap: a },
    { x: 8, y: 0, bitmap: b },
  ]);
  assert.equal(separate.length, 2);
});

test("EG / CG 输出：页头、位图在前、打印机指令按原顺序在后、PRINT 结尾", () => {
  const doc: LabelDoc = {
    width: 100,
    height: 50,
    items: [
      commandItem("BARCODE-TEXT OFF", "BARCODE 128 1 1 30 0 10 ABC"),
      bitmapItem(2, 3, MonoBitmap.filled(8, 1, true)),
      commandItem("LINE 0 0 10 0 2"),
    ],
  };
  assert.equal(
    labelDocToEgCommand(doc),
    "! 0 200 200 50 1\r\nPAGE-WIDTH 100\r\nEG 1 1 2 3 FF\r\nBARCODE-TEXT OFF\r\nBARCODE 128 1 1 30 0 10 ABC\r\nLINE 0 0 10 0 2\r\nPRINT\r\n"
  );
  const cg = labelDocToCgBytes(doc);
  const text = Buffer.from(cg).toString("latin1");
  assert.equal(
    text,
    "! 0 200 200 50 1\r\nPAGE-WIDTH 100\r\nCG 1 1 2 3 \xff\r\nBARCODE-TEXT OFF\r\nBARCODE 128 1 1 30 0 10 ABC\r\nLINE 0 0 10 0 2\r\nPRINT\r\n"
  );
});

test("页面外（负坐标）的部分被裁掉；CG 遇到非 ASCII 指令报错（调用方改用 EG）", () => {
  const doc: LabelDoc = { width: 20, height: 5, items: [bitmapItem(-4, -1, MonoBitmap.filled(8, 3, true))] };
  assert.match(labelDocToEgCommand(doc), /EG 1 2 0 0 F0F0\r\n/);
  assert.equal(renderLabelDocBitmap(doc).get(3, 1), true);
  assert.throws(
    () => labelDocToCgBytes({ width: 10, height: 10, items: [commandItem("BARCODE 128 1 1 30 0 0 中文")] }),
    /ASCII/
  );
});
