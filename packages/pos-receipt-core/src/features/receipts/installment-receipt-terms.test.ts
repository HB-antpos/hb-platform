import assert from "node:assert/strict";
import test from "node:test";

import { INSTALLMENT_RECEIPT_TERMS } from "./installment-receipt-terms";
import {
  buildSaleReceiptDocument,
  displayWidth,
  documentToEscPosBytes,
  type EscPosDocument,
  type ReceiptLine,
} from "./receipt-document";

const orderGuid = "12345678-1234-1234-1234-abcdef47c164";

// 与 buildSaleReceiptDocument 同口径的分期补打入参；termsBlock 由各用例自行决定是否传入。
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
  orderGuid,
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

function localReceiptTime(value: string): string {
  const date = new Date(value);
  const pad = (part: number) => String(part).padStart(2, "0");
  return `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())} ${pad(date.getHours())}:${pad(date.getMinutes())}:${pad(date.getSeconds())}`;
}

function text(line: string, align: "left" | "center" = "left", bold = false): ReceiptLine {
  return { kind: "text", text: line, align, bold };
}

function separator(): ReceiptLine {
  return { kind: "separator", text: "-".repeat(32), align: "left", bold: false };
}

/** 改动前（未引入 termsBlock 时）同一入参在 58mm 下生成的完整文档，用作「不传即不变」的金标准。 */
function goldenDocumentWithoutTerms(): EscPosDocument {
  return {
    paper: "58mm",
    lines: [
      text("Hot Bargain", "center", true),
      text("Brisbane", "center"),
      text("1 Queen St", "center"),
      text("Tel: 0712345678", "center"),
      text("ABN: 12 345 678 901", "center"),
      text(""),
      text("===== TAX INVOICE =====", "center"),
      text(""),
      text("*** Deposit Received ***", "center", true),
      text("*** REPRINT ***", "center", true),
      text(""),
      text("Order: INS-100"),
      text(`Date: ${localReceiptTime("2026-08-01T01:00:00.000Z")}`),
      text("Cashier: Alice"),
      text("Store: Brisbane (BNE)"),
      text("Device: POS-1"),
      text("Installment No: INS-100"),
      text("Balance due: $60.00"),
      separator(),
      text("ITEM             QTY       PRICE"),
      separator(),
      text("Spring water"),
      text("930000000001       2     $100.00"),
      separator(),
      text("Subtotal                 $100.00"),
      text("GST                        $9.09"),
      text("Total(inc GST)           $100.00", "left", true),
      separator(),
      text("Payment:"),
      text("Card                      $40.00"),
      text("  Ref: ****4321"),
      separator(),
      text("Refunds and returns", "left", true),
      text("Refunds within 14 days with"),
      text("proof of purchase."),
      separator(),
      { kind: "qr", value: orderGuid },
      text(`Print Time: ${localReceiptTime("2026-08-03T03:04:05.000Z")}`),
      text("Device: POS-1"),
      text(""),
      text("Thank you for your purchase!", "center", true),
      text(""),
      { kind: "feed" },
    ],
  };
}

function textLines(document: EscPosDocument): string[] {
  return document.lines.flatMap((line) =>
    line.kind === "text" || line.kind === "separator" ? [line.text] : [],
  );
}

test("条款常量与业主审定的英文稿逐字一致，且不可被修改", () => {
  assert.deepEqual(INSTALLMENT_RECEIPT_TERMS, {
    title: "INSTALLMENT TERMS",
    lines: [
      "Order total: $50.00 minimum.",
      "First payment: $20.00 minimum.",
      "Each later payment: $5.00 minimum, or the remaining balance if it is lower.",
    ],
  });
  assert.ok(Object.isFrozen(INSTALLMENT_RECEIPT_TERMS));
  assert.ok(Object.isFrozen(INSTALLMENT_RECEIPT_TERMS.lines));
});

test("不传 termsBlock 时输出与改动前逐行、逐字节一致（58mm 金标准）", () => {
  const golden = goldenDocumentWithoutTerms();
  const withoutField = buildSaleReceiptDocument(installmentSale);

  assert.deepEqual(withoutField, golden);
  assert.deepEqual(
    documentToEscPosBytes(withoutField),
    documentToEscPosBytes(golden),
  );
  assert.equal(textLines(withoutField).some((line) => line.includes("INSTALLMENT TERMS")), false);
});

test("没有可打印内容的 termsBlock 不产生任何输出（不留空分隔线）", () => {
  const golden = goldenDocumentWithoutTerms();
  for (const termsBlock of [
    { title: "", lines: [] },
    { title: "   ", lines: ["", "  "] },
  ]) {
    assert.deepEqual(buildSaleReceiptDocument({ ...installmentSale, termsBlock }), golden);
  }
});

test("58mm 条款块位于退货政策之后、条码/QR/页脚之前，标题左对齐加粗、条款行左对齐", () => {
  const document = buildSaleReceiptDocument({ ...installmentSale, termsBlock: INSTALLMENT_RECEIPT_TERMS });
  const lines = document.lines;
  const titleIndex = lines.findIndex((line) => line.kind === "text" && line.text === "INSTALLMENT TERMS");
  const policyIndex = lines.findIndex((line) => line.kind === "text" && line.text === "Refunds and returns");
  const qrIndex = lines.findIndex((line) => line.kind === "qr");
  const printTimeIndex = lines.findIndex((line) => line.kind === "text" && line.text.startsWith("Print Time:"));

  assert.ok(titleIndex > policyIndex, "条款在退货政策之后");
  assert.ok(titleIndex < qrIndex, "条款在 QR 之前");
  assert.ok(qrIndex < printTimeIndex);
  // 标题前后各一条分隔线：前一条与退货政策隔开，后一条沿用原有「条码前分隔线」。
  assert.equal(lines[titleIndex - 1]?.kind, "separator");
  assert.deepEqual(lines[titleIndex], { kind: "text", text: "INSTALLMENT TERMS", align: "left", bold: true });
  assert.equal(lines[qrIndex - 1]?.kind, "separator");

  const termBodies = lines.slice(titleIndex + 1, qrIndex - 1);
  assert.ok(termBodies.length > 0);
  for (const line of termBodies) {
    assert.equal(line.kind, "text");
    assert.deepEqual([line.kind === "text" && line.align, line.kind === "text" && line.bold], ["left", false]);
  }
  assert.deepEqual(
    termBodies.map((line) => (line.kind === "text" ? line.text : "")),
    [
      "Order total: $50.00 minimum.",
      "First payment: $20.00 minimum.",
      // 第三条超过 32 字符，按纸宽在单词边界自动换行。
      "Each later payment: $5.00",
      "minimum, or the remaining",
      "balance if it is lower.",
    ],
  );
  assert.ok(document.lines.every((line) => line.kind !== "text" || displayWidth(line.text) <= 32));
});

test("80mm 条款块按 42 字符纸宽换行，三条条款文案原样保留", () => {
  const document = buildSaleReceiptDocument({
    ...installmentSale,
    paper: "80mm",
    termsBlock: INSTALLMENT_RECEIPT_TERMS,
  });
  const lines = textLines(document);
  const titleIndex = lines.indexOf("INSTALLMENT TERMS");
  const policyIndex = lines.indexOf("Refunds and returns");

  assert.ok(policyIndex >= 0 && titleIndex > policyIndex);
  assert.deepEqual(lines.slice(titleIndex, titleIndex + 5), [
    "INSTALLMENT TERMS",
    "Order total: $50.00 minimum.",
    "First payment: $20.00 minimum.",
    "Each later payment: $5.00 minimum, or the",
    "remaining balance if it is lower.",
  ]);
  assert.ok(document.lines.every((line) => line.kind !== "text" || displayWidth(line.text) <= 42));
});

test("无退货政策时条款仍紧跟付款块并以分隔线隔开", () => {
  const document = buildSaleReceiptDocument({
    ...installmentSale,
    store: { ...installmentSale.store, returnPolicy: "" },
    termsBlock: INSTALLMENT_RECEIPT_TERMS,
  });
  const lines = document.lines;
  const titleIndex = lines.findIndex((line) => line.kind === "text" && line.text === "INSTALLMENT TERMS");
  const refIndex = lines.findIndex((line) => line.kind === "text" && line.text === "  Ref: ****4321");

  assert.equal(lines[refIndex + 1]?.kind, "separator");
  assert.equal(titleIndex, refIndex + 2);
});

test("zh-CN 小票同样原样打印英文条款，不自行翻译", () => {
  const document = buildSaleReceiptDocument({
    ...installmentSale,
    locale: "zh-CN",
    termsBlock: INSTALLMENT_RECEIPT_TERMS,
  });
  const lines = textLines(document);
  const titleIndex = lines.indexOf("INSTALLMENT TERMS");

  assert.ok(titleIndex >= 0, "zh-CN 下条款标题仍为英文");
  assert.ok(titleIndex > lines.indexOf("退款与退货"), "条款在退货政策之后");
  assert.equal(lines[titleIndex + 1], "Order total: $50.00 minimum.");
  assert.equal(lines[titleIndex + 2], "First payment: $20.00 minimum.");
  assert.equal(
    lines.slice(titleIndex + 3, titleIndex + 6).join(" "),
    "Each later payment: $5.00 minimum, or the remaining balance if it is lower.",
  );
});

test("条款块文本同样拒绝可注入 ESC/POS 指令的控制字符", () => {
  assert.throws(
    () => buildSaleReceiptDocument({
      ...installmentSale,
      termsBlock: { title: "TERMS", lines: ["Unsafe\u001b@"] },
    }),
    /control characters/i,
  );
  assert.throws(
    () => buildSaleReceiptDocument({
      ...installmentSale,
      termsBlock: { title: "TERMS\u001b@", lines: ["ok"] },
    }),
    /control characters/i,
  );
});
