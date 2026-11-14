import assert from "node:assert/strict";
import test from "node:test";

import {
  amountTextToCents,
  applyAmountKey,
  centsToAmountText,
  formatCents,
  type CashMovementKeypadKey,
} from "./cash-movement-amount";

function press(keys: readonly CashMovementKeypadKey[], start = ""): string {
  return keys.reduce((text, key) => applyAmountKey(text, key), start);
}

test("小数位最多 2 位：第三位小数被忽略，已输入内容不被改写", () => {
  assert.equal(press(["1", "2", ".", "3", "4", "5"]), "12.34");
  assert.equal(press([".", "5"]), "0.5");
  assert.equal(press([".", ".", "5"]), "0.5");
});

test("整数与小数合计最多 9 位数字，第 10 位被忽略", () => {
  assert.equal(press(["1", "2", "3", "4", "5", "6", "7", "8", "9", "0"]), "123456789");
  assert.equal(press(["1", "2", "3", "4", "5", "6", "7", ".", "8", "9", "1"]), "1234567.89");
  // "00" 同样受位数限制：只差 1 位时整体拒绝，不会只塞进一个 0。
  assert.equal(press(["00"], "12345678"), "12345678");
});

test("前导 0 归一，00 键从空开始只得到 0", () => {
  assert.equal(press(["0", "5"]), "5");
  assert.equal(press(["00"]), "0");
  assert.equal(press(["0", "0", "0"]), "0");
  assert.equal(press([".", "0", "0"]), "0.00");
});

test("退格与清除", () => {
  assert.equal(press(["backspace"], "12.5"), "12.");
  assert.equal(press(["backspace"], ""), "");
  assert.equal(press(["clear"], "12.5"), "");
});

test("文本转整数分：不经过浮点，空与 0 都是 0", () => {
  assert.equal(amountTextToCents("12.34"), 1234);
  assert.equal(amountTextToCents("12.3"), 1230);
  assert.equal(amountTextToCents("12."), 1200);
  assert.equal(amountTextToCents("0.07"), 7);
  assert.equal(amountTextToCents(""), 0);
  assert.equal(amountTextToCents("."), 0);
  assert.equal(amountTextToCents("0.00"), 0);
  assert.equal(amountTextToCents("1000000.00"), 100_000_000);
  assert.equal(amountTextToCents("abc"), 0);
});

test("整数分转文本与金额格式", () => {
  assert.equal(centsToAmountText(2_000), "20");
  assert.equal(centsToAmountText(2_050), "20.5");
  assert.equal(centsToAmountText(2_005), "20.05");
  assert.equal(formatCents(1_250), "$12.50");
  assert.equal(formatCents(0), "$0.00");
});
