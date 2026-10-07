import assert from "node:assert/strict";
import test from "node:test";

import { REFUND_VOUCHER_TERMS } from "./refund-voucher-terms";

test("券使用说明常量与业主审定的英文稿逐字一致，且不可被修改", () => {
  assert.deepEqual(REFUND_VOUCHER_TERMS, {
    title: "VOUCHER TERMS",
    lines: [
      "Use at the issuing store only.",
      "Pay with it at checkout by scanning the barcode or QR code.",
      "Can be used across several purchases until the balance is $0.00.",
      "Not redeemable for cash.",
    ],
  });
  assert.ok(Object.isFrozen(REFUND_VOUCHER_TERMS));
  assert.ok(Object.isFrozen(REFUND_VOUCHER_TERMS.lines));
});

test("券使用说明只含可打印 ASCII，不会带入可注入 ESC/POS 的控制字符", () => {
  for (const value of [REFUND_VOUCHER_TERMS.title, ...REFUND_VOUCHER_TERMS.lines]) {
    assert.match(value, /^[\x20-\x7e]+$/u);
  }
});
