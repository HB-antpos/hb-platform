import assert from "node:assert/strict";
import { test } from "node:test";

import { LABEL_FONTS } from "@/modules/printer/label-render";
import { formatPriceParts, roundHalfEven, formatSupplierAbbreviation, isValidEan13 } from "./format";
import {
  androidFont,
  descending,
  estimateIndex,
  fittedText,
  longTextBitmap,
  pickLargestFitting,
  textBitmap,
  wrapChars,
} from "./text";
import { createFakeRenderer, fakeMetrics } from "./testing/fake-renderer";

test("字体映射对齐 Android minikin 的加粗规则；旧包缺 Condensed 时退回 Bold", () => {
  const all = [...LABEL_FONTS];
  assert.equal(androidFont("sans-serif-black", true, all), "roboto-black");
  assert.equal(androidFont("sans-serif-black", false, all), "roboto-black");
  assert.equal(androidFont("sans-serif-light", true, all), "roboto-medium");
  assert.equal(androidFont("sans-serif-light", false, all), "roboto-light");
  assert.equal(androidFont("Arial", true, all), "roboto-bold");
  assert.equal(androidFont("Arial", false, all), "roboto-regular");
  assert.equal(androidFont("sans-serif-condensed", true, all), "roboto-condensed-bold");
  assert.equal(androidFont("sans-serif-condensed", true, all.filter((font) => font !== "roboto-condensed-bold")), "roboto-bold");
});

test("格式化与 Kotlin 一致：价格分拆、五成双、供应商缩写、EAN13 校验", () => {
  assert.deepEqual(formatPriceParts(12.995), { integer: "13", decimal: "00" });
  assert.deepEqual(formatPriceParts(null), { integer: "0", decimal: "00" });
  assert.equal(roundHalfEven(2.5), 2);
  assert.equal(roundHalfEven(3.5), 4);
  assert.equal(roundHalfEven(974.25), 974);
  assert.equal(formatSupplierAbbreviation("art wrap pty ltd"), "A.W.P.L");
  assert.equal(formatSupplierAbbreviation("kmart"), "KMA");
  assert.equal(isValidEan13("9313559558795"), true);
  assert.equal(isValidEan13("9313559558796"), false);
});

test("pickLargestFitting 与旧代码逐级缩小的线性扫描结果一致（任意估计起点）", async () => {
  const candidates = descending(30, 1, 0.5);
  for (const limit of [0, 1, 7.25, 15, 29.9, 30, 31]) {
    const expected = candidates.find((value) => value <= limit) ?? candidates[candidates.length - 1];
    for (const estimate of [0, 5, 20, 40, candidates.length - 1]) {
      const { value } = await pickLargestFitting(
        candidates,
        estimate,
        async (values) => values,
        (value) => value <= limit
      );
      assert.equal(value, expected, `limit=${limit} estimate=${estimate}`);
    }
  }
  assert.equal(estimateIndex(candidates, 30, 0.5), candidates.indexOf(15));
});

test("wrapChars 与 Kotlin wrapText 逐字累加的结果一致，且批量量字", async () => {
  const { renderer, calls } = createFakeRenderer();
  const size = 30;
  const kotlin = (text: string, maxWidth: number, maxLines: number) => {
    const lines: string[] = [];
    let current = "";
    for (const char of Array.from(text)) {
      const next = current + char;
      if (current && fakeMetrics({ text: next, font: "roboto-regular", size }).advance > maxWidth && lines.length < maxLines - 1) {
        lines.push(current);
        current = char;
      } else current = next;
    }
    if (current || lines.length === 0) lines.push(current);
    return lines.slice(0, maxLines);
  };
  for (const text of ["Gift Wrap Kraft Paper Roll 70cm x 3m", "A", "中文商品名称测试 ABC", "xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx"]) {
    for (const maxLines of [1, 2, 4]) {
      assert.deepEqual(await wrapChars(renderer, text, "roboto-regular", size, 200, maxLines), kotlin(text, 200, maxLines), text);
    }
  }
  // 每行只需一次批量量字（不是逐字往返）。
  assert.ok(calls.every((call) => call.op === "measure"));
});

test("textBitmap 复刻 Android textToBitmap：宽 = 前进宽 + 2×padding，高只包墨迹，反白补边为黑", async () => {
  const { renderer } = createFakeRenderer();
  const bitmap = await textBitmap(renderer, { text: "AB", size: 10, bold: true, family: "sans-serif-black", inverse: true, padding: 2 });
  // 字号 10 → 30 点，每字 15 点宽；墨迹高 0.7 × 30 = 21。
  assert.equal(bitmap.width, 30 + 4);
  assert.equal(bitmap.height, 21 + 4);
  assert.equal(bitmap.get(0, 0), true, "反白：padding 区是黑底");
  assert.equal(bitmap.get(5, 5), false, "反白：墨迹是白字");

  const blank = await textBitmap(renderer, { text: "", size: 10, bold: true, family: "sans-serif-black" });
  assert.equal(blank.height, 1, "空白文字只有 padding 高（至少 1）");
});

test("fittedText 逐级缩到放得下；longTextBitmap 宽度不超过 maxWidth", async () => {
  const { renderer } = createFakeRenderer();
  const fitted = await fittedText(renderer, { text: "1234567890", size: 16, bold: true, family: "sans-serif-black" }, 100);
  // 假渲染器：10 字 × 0.5 × (字号 × 3) = 15 × 字号；≤ 100 的最大 0.5 档是 6.5 号 → 97.5 → 98 点（旧代码同一结果）。
  assert.equal(fitted.width, 98);

  const name = await longTextBitmap(renderer, "Gift Wrap Kraft Paper Roll 70cm x 3m Assorted Designs", 10, false, "Arial", 2, 300);
  assert.ok(name.width <= 300);
  assert.equal(name.height, Math.ceil(30 * 0.9 + 30 * 0.25) * 2);
});
