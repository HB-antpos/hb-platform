import assert from "node:assert/strict";
import test from "node:test";

import { INSTALLMENT_RECEIPT_TERMS } from "./installment-receipt-terms";
import {
  isValidReceiptTermsText,
  RECEIPT_TERMS_TEXT_MAX_LENGTH,
  receiptTermsTextLines,
  resolveReceiptTermsBlock,
} from "./receipt-terms-text";
import { REFUND_VOUCHER_TERMS } from "./refund-voucher-terms";

test("长度上限固定为 600（UTF-16 码元），恰好 600 合规、601 不合规", () => {
  assert.equal(RECEIPT_TERMS_TEXT_MAX_LENGTH, 600);
  assert.equal(isValidReceiptTermsText("a".repeat(600)), true);
  assert.equal(isValidReceiptTermsText("a".repeat(601)), false);
  // 长度按 .length 计：每个 emoji 占 2 个码元，300 个恰好 600，301 个超限。
  assert.equal(isValidReceiptTermsText("😀".repeat(300)), true);
  assert.equal(isValidReceiptTermsText("😀".repeat(301)), false);
  // CRLF 也计入长度，与服务端、设置存储同口径。
  assert.equal(isValidReceiptTermsText(`${"a".repeat(599)}\n`), true);
  assert.equal(isValidReceiptTermsText(`${"a".repeat(600)}\n`), false);
});

test("控制字符：只放行 CR/LF/TAB，其余 C0、DEL、C1 一律不合规", () => {
  assert.equal(isValidReceiptTermsText("line1\r\nline2\n\tline3\r"), true);
  // 0x00-0x1f（含 ESC=0x1b）、0x7f（DEL）、0x80-0x9f（C1）里，除 TAB/LF/CR 外全部拒绝。
  const allowed = new Set([0x09, 0x0a, 0x0d]);
  for (let code = 0; code <= 0x9f; code += 1) {
    if (code >= 0x20 && code <= 0x7e) continue;
    assert.equal(
      isValidReceiptTermsText(`ok${String.fromCharCode(code)}ok`),
      allowed.has(code),
      `0x${code.toString(16)}`,
    );
  }
  // 普通可打印字符、NBSP（0xa0 不属于控制字符）与中文不受影响。
  assert.equal(isValidReceiptTermsText("Use   here. 仅限本店使用。"), true);
});

test("非字符串一律不合规（旧快照的 null / undefined、脏数据）", () => {
  for (const value of [null, undefined, 0, 1, true, {}, [], ["a"]]) {
    assert.equal(isValidReceiptTermsText(value), false);
    assert.deepEqual(receiptTermsTextLines(value), []);
  }
});

test("拆行：按 \\r\\n|\\r|\\n 拆、逐行 trim、丢弃空行，其余原样", () => {
  assert.deepEqual(
    receiptTermsTextLines("  First rule.  \r\n\r\nSecond rule.\rThird rule.\n   \n\tFourth rule.\t\n"),
    ["First rule.", "Second rule.", "Third rule.", "Fourth rule."],
  );
  // 行内空白与大小写、标点原样保留（不做词法改写）。
  assert.deepEqual(receiptTermsTextLines("Pay  with   it.  $5.00 min"), ["Pay  with   it.  $5.00 min"]);
});

test("行内 TAB 换成单个空格，不能原样进入票面字节", () => {
  assert.deepEqual(receiptTermsTextLines("Qty:\t2\tpcs"), ["Qty: 2 pcs"]);
});

test("全空白、空串、只有换行与 TAB：没有正文行", () => {
  for (const blank of ["", "   ", "\n", "\r\n\r\n", " \t \r\n \t "]) {
    assert.deepEqual(receiptTermsTextLines(blank), []);
  }
});

test("不合规文本整份丢弃，不做部分剔除", () => {
  assert.deepEqual(receiptTermsTextLines("Good line.\nBad \u001b@ line."), []);
  assert.deepEqual(receiptTermsTextLines(`${"a".repeat(601)}`), []);
});

test("resolveReceiptTermsBlock：未定制（null/undefined/空白）原样返回默认块（同一引用）", () => {
  for (const blank of [null, undefined, "", "  ", "\r\n \t\n"]) {
    assert.equal(resolveReceiptTermsBlock(REFUND_VOUCHER_TERMS, blank), REFUND_VOUCHER_TERMS);
    assert.equal(resolveReceiptTermsBlock(INSTALLMENT_RECEIPT_TERMS, blank), INSTALLMENT_RECEIPT_TERMS);
  }
});

test("resolveReceiptTermsBlock：定制时沿用默认标题，正文换成自定义行，且结果不可变", () => {
  const voucher = resolveReceiptTermsBlock(
    REFUND_VOUCHER_TERMS,
    "Valid at all stores.\r\n\r\n  Not redeemable for cash.  ",
  );
  assert.deepEqual(voucher, {
    title: "VOUCHER TERMS",
    lines: ["Valid at all stores.", "Not redeemable for cash."],
  });
  assert.ok(Object.isFrozen(voucher));
  assert.ok(Object.isFrozen(voucher.lines));

  const installment = resolveReceiptTermsBlock(INSTALLMENT_RECEIPT_TERMS, "Deposit $30 minimum.");
  assert.deepEqual(installment, { title: "INSTALLMENT TERMS", lines: ["Deposit $30 minimum."] });
});

test("resolveReceiptTermsBlock：不合规（含 ESC、超 600、非字符串）回退默认块，不抛错", () => {
  for (const bad of ["Unsafe\u001b@ text", "x".repeat(601), "Bell\u0007", "del\u007f", 42, {}]) {
    assert.equal(resolveReceiptTermsBlock(REFUND_VOUCHER_TERMS, bad), REFUND_VOUCHER_TERMS);
  }
});

test("默认文案常量逐字不变（业主审定稿）", () => {
  assert.deepEqual(REFUND_VOUCHER_TERMS, {
    title: "VOUCHER TERMS",
    lines: [
      "Use at the issuing store only.",
      "Pay with it at checkout by scanning the barcode or QR code.",
      "Can be used across several purchases until the balance is $0.00.",
      "Not redeemable for cash.",
    ],
  });
  assert.deepEqual(INSTALLMENT_RECEIPT_TERMS, {
    title: "INSTALLMENT TERMS",
    lines: [
      "Order total: $50.00 minimum.",
      "First payment: $20.00 minimum.",
      "Each later payment: $5.00 minimum, or the remaining balance if it is lower.",
    ],
  });
});
