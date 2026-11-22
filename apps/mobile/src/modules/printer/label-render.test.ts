import assert from "node:assert/strict";
import { test } from "node:test";

import {
  chunkItems,
  decodeBase64,
  encodeBase64,
  normalizeLabelRenderInfo,
  normalizeTextBitmap,
  normalizeTextMetrics,
} from "./label-render";

test("base64 编解码与 Node Buffer 一致（含 0 / 1 / 2 字节尾巴与全字节值）", () => {
  const all = Uint8Array.from({ length: 256 }, (_, index) => index);
  for (const length of [0, 1, 2, 3, 4, 5, 255, 256]) {
    const bytes = all.slice(0, length);
    const encoded = encodeBase64(bytes);
    assert.equal(encoded, Buffer.from(bytes).toString("base64"));
    assert.deepEqual(decodeBase64(encoded), bytes);
  }
});

test("base64 解码忽略空白、拒绝非法字符", () => {
  assert.deepEqual(decodeBase64("AQID\nBA=="), Uint8Array.from([1, 2, 3, 4]));
  assert.throws(() => decodeBase64("AQ*D"), /Invalid base64/);
  assert.throws(() => decodeBase64("A"), /Invalid base64/);
});

test("渲染能力：旧包 / 版本不够返回 null，缺省上限取默认值", () => {
  assert.equal(normalizeLabelRenderInfo(undefined), null);
  assert.equal(normalizeLabelRenderInfo({ version: 0 }), null);
  assert.deepEqual(normalizeLabelRenderInfo({ version: 1, fonts: ["roboto-black", 3] }), {
    version: 1,
    fonts: ["roboto-black"],
    maxBatch: 64,
    maxBitmapWidth: 2048,
    maxBitmapHeight: 1024,
    supportsFileFonts: false,
  });
});

test("度量结果必须是有限数字", () => {
  const metrics = { advance: 10.5, ascent: 20, descent: 5, inkLeft: 0.5, inkTop: -18, inkRight: 10, inkBottom: 0 };
  assert.deepEqual(normalizeTextMetrics(metrics), metrics);
  assert.throws(() => normalizeTextMetrics({ ...metrics, advance: Number.NaN }), /advance/);
});

test("位图结果：字节数必须等于 ceil(width/8) × height，墨迹框落在图内", () => {
  // 10×2 位图 → 每行 2 字节，共 4 字节。
  const bits = encodeBase64(Uint8Array.from([0xff, 0xc0, 0x00, 0x00]));
  const bitmap = normalizeTextBitmap({
    width: 10,
    height: 2,
    originX: 0,
    originY: 1,
    hasInk: true,
    inkLeft: 0,
    inkTop: 0,
    inkRight: 10,
    inkBottom: 1,
    bits,
  });
  assert.equal(bitmap.rowBytes, 2);
  assert.deepEqual(bitmap.ink, { left: 0, top: 0, right: 10, bottom: 1 });
  assert.deepEqual(Array.from(bitmap.bits), [0xff, 0xc0, 0, 0]);

  const blank = normalizeTextBitmap({ width: 10, height: 2, originX: 0, originY: 1, hasInk: false, bits });
  assert.equal(blank.ink, null);

  assert.throws(
    () => normalizeTextBitmap({ width: 9, height: 3, originX: 0, originY: 0, hasInk: false, bits }),
    /expected 6/
  );
  assert.throws(
    () =>
      normalizeTextBitmap({
        width: 10,
        height: 2,
        originX: 0,
        originY: 0,
        hasInk: true,
        inkLeft: 0,
        inkTop: 0,
        inkRight: 11,
        inkBottom: 1,
        bits,
      }),
    /ink box/
  );
});

test("分批保持顺序，最后一批可以不满", () => {
  assert.deepEqual(chunkItems([1, 2, 3, 4, 5], 2), [[1, 2], [3, 4], [5]]);
  assert.deepEqual(chunkItems([], 64), []);
});
