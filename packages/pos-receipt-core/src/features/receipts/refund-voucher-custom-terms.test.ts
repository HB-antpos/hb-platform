import assert from "node:assert/strict";
import test from "node:test";

import type { LocalOrder } from "@hb/pos-domain/core/contracts/order";

import { encodeEscPosText } from "./esc-pos-text-encoding";
import {
  encodeRefundVoucherDocuments,
  ProtectedRefundVoucherReceiptRenderer,
  type FrozenRefundVoucherReceiptSettings,
} from "./refund-voucher-receipt-renderer";
import { REFUND_VOUCHER_TERMS } from "./refund-voucher-terms";

/**
 * 总部下发「券使用说明」自定义正文后的退款券面：
 * 标题 VOUCHER TERMS 固定，正文按行输出；未定制 / 全空白 / 不合规时与默认文案逐字节一致。
 */

const PRINTED_AT = new Date(2026, 6, 10, 9, 30, 0);
const decoder = new TextDecoder();

function hexOf(bytes: Uint8Array | readonly number[]): string {
  return Buffer.from(bytes).toString("hex");
}

/** 独立于实现的期望字节：一行 ESC/POS 文本（对齐、加粗、文本、换行）。 */
function escLineHex(value: string, alignment: "left" | "center", bold: boolean): string {
  return hexOf([
    0x1b, 0x61, alignment === "center" ? 1 : 0,
    0x1b, 0x45, bold ? 1 : 0,
    ...encodeEscPosText(value),
    0x0a,
  ]);
}

/** 每张券面末尾固定的「走纸 3 行 + 全切」；使用说明块紧贴在它之前。 */
const FEED_AND_CUT_HEX = "1b64031d5600";

function termsBlockHex(title: string, wrappedLines: readonly string[], width: number): string {
  return [
    escLineHex("", "left", false),
    escLineHex("-".repeat(width), "left", false),
    escLineHex(title, "center", true),
    ...wrappedLines.map((line) => escLineHex(line, "left", false)),
  ].join("");
}

function settings(
  overrides: Partial<FrozenRefundVoucherReceiptSettings> = {},
): FrozenRefundVoucherReceiptSettings {
  return {
    printerId: "printer-1",
    paper: "80mm",
    locale: "en",
    store: {
      brandName: "Hot Bargain",
      storeName: "Brisbane",
      address: "1 Queen St",
      phone: "0712345678",
      abn: "12 345 678 901",
      returnPolicy: "",
    },
    ...overrides,
  };
}

function render(
  frozen: FrozenRefundVoucherReceiptSettings,
  voucherCodes: readonly string[] = ["RF123"],
): { hex: string; text: string } {
  const rendered = encodeRefundVoucherDocuments({
    settings: frozen,
    storeCode: "S001",
    orderLabel: "return-order-1",
    vouchers: voucherCodes.map((voucherCode) => ({ voucherCode, amountCents: 800 })),
    printedAt: PRINTED_AT,
  });
  return { hex: hexOf(rendered.receiptBytes), text: decoder.decode(rendered.receiptBytes) };
}

test("未定制（缺省 / null / 空串 / 纯空白）：与不带该字段时逐字节一致，且印默认文案", () => {
  for (const paper of ["58mm", "80mm"] as const) {
    const baseline = render(settings({ paper }));
    for (const blank of [undefined, null, "", "   ", "\r\n \t\n"]) {
      const withField = render(settings({ paper, voucherTerms: blank }));
      assert.equal(withField.hex, baseline.hex, `${paper} / ${JSON.stringify(blank)}`);
    }
    for (const line of REFUND_VOUCHER_TERMS.lines) {
      // 默认文案按词换行后是逐字的（拼回去与原句一致）；这里只确认标题与首句存在。
      assert.match(baseline.text, /VOUCHER TERMS/u);
      assert.ok(baseline.text.includes(line.split(" ").slice(0, 3).join(" ")), line);
    }
  }
});

test("定制：标题固定为 VOUCHER TERMS，正文按行输出，空行被丢弃、每行 trim", () => {
  const custom = "  Valid at all stores.  \r\n\r\n   \nNo cash refunds on vouchers.\rKeep this voucher safe.";
  const { hex } = render(settings({ voucherTerms: custom }));

  const expectedBlock = termsBlockHex(
    "VOUCHER TERMS",
    ["Valid at all stores.", "No cash refunds on vouchers.", "Keep this voucher safe."],
    48,
  );
  assert.ok(hex.endsWith(`${expectedBlock}${FEED_AND_CUT_HEX}`), "使用说明块在 QR 之后、走纸切纸之前");
  // 默认文案不能再出现。
  for (const defaultLine of REFUND_VOUCHER_TERMS.lines) {
    assert.equal(hex.includes(escLineHex(defaultLine, "left", false)), false, defaultLine);
  }
});

test("定制长行按渲染器自己的纸宽在词边界换行（58mm=32、80mm=48），每行不超宽", () => {
  const sentence = "Present this voucher together with the original receipt at the issuing store counter.";
  const expectations = [
    ["58mm", 32, ["Present this voucher together", "with the original receipt at the", "issuing store counter."]],
    ["80mm", 48, ["Present this voucher together with the original", "receipt at the issuing store counter."]],
  ] as const;
  for (const [paper, width, wrapped] of expectations) {
    const { hex } = render(settings({ paper, voucherTerms: sentence }));
    assert.ok(wrapped.every((line) => line.length <= width), `${paper}: 期望值自身不得超宽`);
    assert.ok(
      hex.endsWith(`${termsBlockHex("VOUCHER TERMS", wrapped, width)}${FEED_AND_CUT_HEX}`),
      paper,
    );
  }
});

test("多张券面共用同一份自定义正文，每张都带", () => {
  const { text, hex } = render(settings({ voucherTerms: "Single custom rule." }), ["RF-A", "RF-B"]);
  assert.equal(text.match(/VOUCHER TERMS/gu)?.length, 2);
  assert.equal(text.match(/Single custom rule\./gu)?.length, 2);
  assert.equal(hex.split(FEED_AND_CUT_HEX).length - 1, 2);
  assert.doesNotMatch(text, /Not redeemable for cash\./u);
});

test("zh-CN 小票：标题仍是英文 VOUCHER TERMS，自定义中文正文按 GB18030 输出", () => {
  const { hex } = render(settings({ locale: "zh-CN", voucherTerms: "仅限本店使用。\n不可兑换现金。" }));
  const expectedBlock = termsBlockHex("VOUCHER TERMS", ["仅限本店使用。", "不可兑换现金。"], 48);
  assert.ok(hex.endsWith(`${expectedBlock}${FEED_AND_CUT_HEX}`));
});

test("行内 TAB 换成空格，TAB 字节（0x09）不会进入票面", () => {
  const { hex } = render(settings({ voucherTerms: "Qty:\t2" }));
  assert.ok(hex.includes(escLineHex("Qty: 2", "left", false)));
  assert.equal(hex.includes(hexOf([...encodeEscPosText("Qty:"), 0x09])), false);
});

test("净化：含 ESC/控制字符的自定义正文整份丢弃，回退默认文案（逐字节一致），不会注入打印指令", () => {
  const baseline = render(settings()).hex;
  const injected = [
    "Valid.\u001b@\u001b!\u0001 Reset printer",
    "Beep\u0007",
    "Cut\u001dV\u0000",
    "C1\u009b control",
    "DEL\u007f",
    "FormFeed\u000c",
  ];
  for (const voucherTerms of injected) {
    const { hex } = render(settings({ voucherTerms }));
    assert.equal(hex, baseline, JSON.stringify(voucherTerms));
  }
});

test("超过 600 个码元的自定义正文整份丢弃，回退默认文案；恰好 600 仍生效", () => {
  const baseline = render(settings()).hex;
  assert.equal(render(settings({ voucherTerms: "x".repeat(601) })).hex, baseline);
  const atLimit = render(settings({ voucherTerms: "y".repeat(600) }));
  assert.notEqual(atLimit.hex, baseline);
  assert.ok(atLimit.text.includes("y".repeat(48)));
});

test("退货首次打印路径：受保护渲染器从冻结设置读取 voucherTerms，缺省时仍是默认文案", async () => {
  const run = async (voucherTerms: string | null | undefined): Promise<string> => {
    const renderer = new ProtectedRefundVoucherReceiptRenderer(
      { async getByGuid() { return pureVoucherReturn(); } },
      {
        async resolveApprovedRefundVouchers() {
          return [{ returnOrderGuid: "return-order-1", voucherCode: "RF123", refundAmountCents: 800 }];
        },
      },
      {
        async getFrozenReturnReceiptSettings() {
          return settings(voucherTerms === undefined ? {} : { voucherTerms });
        },
      },
      () => PRINTED_AT,
    );
    return decoder.decode((await renderer.render("return-action-1", "return-order-1")).receiptBytes);
  };

  assert.match(await run("Custom first-print rule."), /Custom first-print rule\./u);
  assert.doesNotMatch(await run("Custom first-print rule."), /Not redeemable for cash\./u);
  for (const blank of [undefined, null, "  "]) {
    const text = await run(blank);
    assert.match(text, /Use at the issuing store only\./u);
    assert.match(text, /Not redeemable for cash\./u);
  }
});

function pureVoucherReturn(): LocalOrder {
  return {
    orderGuid: "return-order-1",
    localSequence: 8,
    storeCode: "S001",
    deviceCode: "IPAD-1",
    cashierId: "cashier-1",
    cashierName: "Cashier",
    soldAtIso: "2026-07-10T00:00:00.000Z",
    state: "PendingSync",
    total: { currency: "AUD", cents: -800 },
    discount: { currency: "AUD", cents: 0 },
    actualAmount: { currency: "AUD", cents: -800 },
    originalOrderGuid: "sale-order-1",
    lines: [
      {
        lineId: "return-line-1",
        productCode: "P-SECRET",
        itemNumber: "I-SECRET",
        lookupCode: "SECRET",
        displayName: "Secret product",
        quantity: "1",
        unitPrice: { currency: "AUD", cents: 800 },
        discount: { currency: "AUD", cents: 0 },
        actualAmount: { currency: "AUD", cents: -800 },
        priceSource: "catalog",
        kind: "return",
        returnSourceKey: "source-1",
        originalOrderGuid: "sale-order-1",
        originalOrderDetailGuid: "sale-line-1",
      },
    ],
    tenders: [
      {
        tenderGuid: "voucher-tender-1",
        method: "voucher",
        amount: { currency: "AUD", cents: -800 },
        reference: null,
        reservationToken: null,
      },
    ],
  } as unknown as LocalOrder;
}
