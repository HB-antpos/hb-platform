import assert from "node:assert/strict";
import test from "node:test";

import { sumCashRoundingCents } from "./daily-close";

// 进位规则与收银时一致：AUD 5 分，余 1、2 舍，余 3、4 进，负数远离零对称。

test("sumCashRoundingCents：已是 5 分整数倍的订单没有进位", () => {
  assert.equal(sumCashRoundingCents([1_000, 5, 0, 500]), 0);
});

test("sumCashRoundingCents：余 1、2 分向下舍（负进位），余 3、4 分向上进（正进位）", () => {
  assert.equal(sumCashRoundingCents([1_001]), -1);
  assert.equal(sumCashRoundingCents([1_002]), -2);
  assert.equal(sumCashRoundingCents([1_003]), 2);
  assert.equal(sumCashRoundingCents([1_004]), 1);
});

test("sumCashRoundingCents：退款（负现金）同样远离零对称进位", () => {
  // -1003 进位到 -1005，进位量为 -2（钱箱多付出 2 分）。
  assert.equal(sumCashRoundingCents([-1_003]), -2);
  assert.equal(sumCashRoundingCents([-1_002]), 2);
});

test("sumCashRoundingCents：按订单分别进位后再求和，不是先求和再进位", () => {
  // 逐单：+2、-1、+2、-2 = +1；若先求和 4,707-1,003=3,704 再进位则是 +1（巧合相等），换一组区分。
  assert.equal(sumCashRoundingCents([1_003, 2_001, 703, -1_003]), 1);
  // 两单各 2 分：逐单进位 = -2 + -2 = -4；先合并成 2,004 再进位只有 +1。
  assert.equal(sumCashRoundingCents([1_002, 1_002]), -4);
});

test("sumCashRoundingCents：空列表为 0，非整数分币直接拒绝", () => {
  assert.equal(sumCashRoundingCents([]), 0);
  assert.throws(() => sumCashRoundingCents([10.5]), TypeError);
  assert.throws(() => sumCashRoundingCents([Number.NaN]), TypeError);
});
