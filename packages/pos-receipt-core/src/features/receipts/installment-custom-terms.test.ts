import assert from "node:assert/strict";
import test from "node:test";

import { INSTALLMENT_RECEIPT_TERMS } from "./installment-receipt-terms";
import {
  buildSaleReceiptDocument,
  displayWidth,
  documentToEscPosBytes,
  type EscPosDocument,
} from "./receipt-document";
import { resolveReceiptTermsBlock } from "./receipt-terms-text";

/**
 * 总部下发「分期条款」自定义正文后的分期小票：
 * 调用方用 resolveReceiptTermsBlock(INSTALLMENT_RECEIPT_TERMS, 自定义正文) 解析条款块再交给
 * buildSaleReceiptDocument；标题 INSTALLMENT TERMS 固定，未定制 / 全空白 / 不合规时与默认文案逐字一致。
 */

const installmentSale = {
  locale: "en" as const,
  paper: "58mm" as const,
  store: {
    brandName: "Hot Bargain",
    storeName: "Brisbane",
    address: "1 Queen St",
    phone: "0712345678",
    abn: "12 345 678 901",
    returnPolicy: "Refunds within 14 days with proof of purchase.",
  },
  orderNumber: "INS-100",
  orderGuid: "12345678-1234-1234-1234-abcdef47c164",
  orderDisplay: "INS-100",
  soldAtIso: "2026-08-01T01:00:00.000Z",
  cashierName: "Alice",
  deviceCode: "POS-1",
  storeCode: "BNE",
  lines: [{ name: "Spring water", lookupCode: "930000000001", quantity: "2", discountCents: 0, totalCents: 10_000 }],
  subtotalCents: 10_000,
  discountCents: 0,
  totalCents: 10_000,
  tenders: [{ method: "card" as const, amountCents: 4_000, reference: "VISA ****4321" }],
  cashChangeCents: null,
  statusText: "*** Deposit Received ***",
  isReprint: true,
  includeMachineCodes: true,
  printedAtIso: "2026-08-03T03:04:05.000Z",
  extraInfoLines: ["Installment No: INS-100", "Balance due: $60.00"],
};

function textLines(document: EscPosDocument): string[] {
  return document.lines.flatMap((line) => (line.kind === "text" ? [line.text] : []));
}

function build(installmentTerms: unknown, paper: "58mm" | "80mm" = "58mm"): EscPosDocument {
  return buildSaleReceiptDocument({
    ...installmentSale,
    paper,
    termsBlock: resolveReceiptTermsBlock(INSTALLMENT_RECEIPT_TERMS, installmentTerms),
  });
}

test("未定制（null / undefined / 空串 / 纯空白）：与直接传默认条款块逐字节一致", () => {
  const golden = buildSaleReceiptDocument({ ...installmentSale, termsBlock: INSTALLMENT_RECEIPT_TERMS });
  for (const blank of [null, undefined, "", "  ", "\r\n\t\n"]) {
    const document = build(blank);
    assert.deepEqual(document, golden, JSON.stringify(blank));
    assert.deepEqual(documentToEscPosBytes(document), documentToEscPosBytes(golden));
  }
});

test("定制：标题固定为 INSTALLMENT TERMS，正文按行输出，空行被丢弃、每行 trim", () => {
  const lines = textLines(build("  Deposit $30 minimum.  \r\n\r\n   \nLater payments from $10.\rBalance due in 90 days."));
  const titleIndex = lines.indexOf("INSTALLMENT TERMS");

  assert.ok(titleIndex > lines.indexOf("Refunds and returns"), "条款仍在退货政策之后");
  assert.deepEqual(lines.slice(titleIndex, titleIndex + 4), [
    "INSTALLMENT TERMS",
    "Deposit $30 minimum.",
    "Later payments from $10.",
    "Balance due in 90 days.",
  ]);
  // 默认文案不能再出现（第三条会被换行，所以按关键词判断）。
  for (const keyword of ["Order total", "First payment", "Each later payment"]) {
    assert.equal(lines.some((line) => line.includes(keyword)), false, keyword);
  }
});

test("定制长句按纸宽在词边界换行（58mm=32、80mm=42），拼回去与原句一致", () => {
  const sentence = "Installment deposits are non-refundable once the goods have been set aside for you.";
  for (const [paper, width] of [["58mm", 32], ["80mm", 42]] as const) {
    const document = build(sentence, paper);
    const lines = textLines(document);
    const titleIndex = lines.indexOf("INSTALLMENT TERMS");
    // 条款之后紧跟分隔线（非 text），所以条款正文就是标题之后到下一条非条款文本之前的连续 text 行。
    const body: string[] = [];
    const startLine = document.lines.findIndex((line) => line.kind === "text" && line.text === "INSTALLMENT TERMS");
    for (const line of document.lines.slice(startLine + 1)) {
      if (line.kind !== "text") break;
      body.push(line.text);
    }

    assert.ok(titleIndex >= 0);
    assert.ok(body.length >= 2, `${paper}: 应当换行`);
    assert.ok(body.every((line) => displayWidth(line) <= width), `${paper}: 每行不超过 ${width}`);
    assert.equal(body.join(" "), sentence, paper);
  }
});

test("净化：含控制字符（ESC 等）或超过 600 的自定义正文整份丢弃，回退默认文案，且不抛错", () => {
  const golden = buildSaleReceiptDocument({ ...installmentSale, termsBlock: INSTALLMENT_RECEIPT_TERMS });
  for (const bad of ["Unsafe\u001b@ text", "Bell\u0007", "C1\u009b", "x".repeat(601)]) {
    assert.deepEqual(build(bad), golden, JSON.stringify(bad).slice(0, 40));
  }
});

test("行内 TAB 换成空格，TAB 不会让文档构建抛错也不会进入票面", () => {
  const lines = textLines(build("Deposit:\t$30"));
  assert.ok(lines.includes("Deposit: $30"));
  assert.equal(lines.some((line) => line.includes("\t")), false);
});

test("zh-CN 小票：标题仍为英文 INSTALLMENT TERMS，自定义正文按原文打印", () => {
  const document = buildSaleReceiptDocument({
    ...installmentSale,
    locale: "zh-CN",
    termsBlock: resolveReceiptTermsBlock(INSTALLMENT_RECEIPT_TERMS, "订单满 $50 起。"),
  });
  const lines = textLines(document);
  const titleIndex = lines.indexOf("INSTALLMENT TERMS");

  assert.ok(titleIndex > lines.indexOf("退款与退货"));
  assert.equal(lines[titleIndex + 1], "订单满 $50 起。");
});
