import assert from "node:assert/strict";
import test from "node:test";

import {
  receiptCode128,
  receiptCode128ModuleWidth,
} from "@hb/pos-receipt-core/features/receipts/receipt-code128";
import {
  buildBankReceiptDocument,
  buildDailyCloseDocument,
  buildSaleReceiptDocument,
  documentToEscPosBytes,
  displayWidth,
  receiptOrderShortCode,
} from "@hb/pos-receipt-core/features/receipts/receipt-document";

const sale = {
  locale: "en" as const,
  paper: "58mm" as const,
  store: { brandName: "Hot Bargain", storeName: "Brisbane", address: "1 Queen St", phone: "0712345678", abn: "12 345 678 901", returnPolicy: "" },
  orderNumber: "S1-100", soldAtIso: "2026-07-28T10:11:12.000Z", cashierName: "Alice", deviceCode: "IPAD-1",
  lines: [{ name: "Long bottled spring water with a very long name", lookupCode: "930000000001", quantity: "2", discountCents: 50, totalCents: 548 }],
  subtotalCents: 598, discountCents: 50, totalCents: 548, tenders: [{ method: "cash" as const, amountCents: 600 }], cashChangeCents: 52,
};

const fullOrderGuid = "11111111-2222-3333-4444-555555555555";

function localReceiptTime(value: string): string {
  const date = new Date(value);
  const pad = (part: number) => String(part).padStart(2, "0");
  return `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())} ${pad(date.getHours())}:${pad(date.getMinutes())}:${pad(date.getSeconds())}`;
}

function receiptText(document: ReturnType<typeof buildSaleReceiptDocument>): string {
  return document.lines
    .filter((line) => line.kind === "text" || line.kind === "separator")
    .map((line) => line.text)
    .join("\n");
}

function containsBytes(bytes: Uint8Array, expected: readonly number[]): boolean {
  return Array.from(
    { length: Math.max(0, bytes.length - expected.length + 1) },
    (_, start) => expected.every((value, offset) => bytes[start + offset] === value),
  ).some(Boolean);
}

test("58mm 英文销售小票按整数分币输出确定性文档与 ESC/POS 字节", () => {
  const document = buildSaleReceiptDocument(sale);
  const text = receiptText(document);
  const bytes = documentToEscPosBytes(document);

  assert.match(text, /TAX INVOICE/);
  assert.match(text, /^Brisbane$/m);
  assert.match(text, /^1 Queen St$/m);
  assert.match(text, /^Order: S1-100$/m);
  assert.match(text, /GST\s+\$0\.50/);
  assert.doesNotMatch(text, /Change\s+\$0\.52/, "WPF 正式小票不打印成功页找零信息");
  assert.match(text, /Payment:/);
  assert.equal((text.match(/Device: IPAD-1/g) ?? []).length, 2, "WPF 在订单信息和尾部各打印一次设备");
  assert.ok(document.lines.every((line) => line.kind !== "text" || displayWidth(line.text) <= 32));
  assert.deepEqual([...bytes.slice(0, 4)], [0x1b, 0x40, 0x1c, 0x26]);
  assert.deepEqual(
    [...bytes.slice(-3)],
    [0x1d, 0x56, 0x00],
    "每个冻结小票作业只在字节尾部切纸一次",
  );
  assert.ok(document.lines.every((line) => line.kind !== "barcode" && line.kind !== "qr"));
});

test("Payment 后按纸宽换行输出本地化 Return Policy 标题与正文，空值不输出", () => {
  const policy = "Change of mind returns are accepted within 14 days with proof of purchase.";
  const en = buildSaleReceiptDocument({
    ...sale,
    paper: "58mm",
    store: { ...sale.store, returnPolicy: policy },
  });
  const enText = receiptText(en);
  assert.match(enText, /Refunds and returns/);
  assert.ok(enText.indexOf("Payment:") < enText.indexOf("Refunds and returns"));
  assert.ok(en.lines.every((line) => line.kind !== "text" || displayWidth(line.text) <= 32));

  const zh = buildSaleReceiptDocument({
    ...sale,
    locale: "zh-CN",
    store: { ...sale.store, returnPolicy: "退货政策\r\n14 天内可退货" },
  });
  assert.match(receiptText(zh), /退款与退货/);

  const empty = buildSaleReceiptDocument({ ...sale, store: { ...sale.store, returnPolicy: "   " } });
  assert.doesNotMatch(receiptText(empty), /Refunds and returns/);
});

test("Return Policy 拒绝除 CR/LF/TAB 外的控制字符", () => {
  assert.throws(
    () => buildSaleReceiptDocument({
      ...sale,
      store: { ...sale.store, returnPolicy: "Unsafe\u001b@" },
    }),
    /control characters/i,
  );
});

test("中文销售文本使用 GB18030 双字节并替换 N160 不可靠字形", () => {
  const document = buildSaleReceiptDocument({
    ...sale,
    locale: "zh-CN",
    store: { ...sale.store, brandName: "商品😀𠀀" },
  });
  const bytes = documentToEscPosBytes(document);

  assert.ok(containsBytes(bytes, [0xc9, 0xcc, 0xc6, 0xb7, 0x3f, 0x3f]));
  assert.ok(containsBytes(bytes, [0xb8, 0xd0, 0xd0, 0xbb, 0xbb, 0xdd, 0xb9, 0xcb]));
  assert.equal(containsBytes(bytes, [...new TextEncoder().encode("商品")]), false);
  assert.equal(containsBytes(bytes, [...new TextEncoder().encode("感谢惠顾")]), false);
});

test("销售小票按 WPF 顺序加入显示元数据和完整 GUID 机读码", () => {
  const document = buildSaleReceiptDocument({
    ...sale,
    paper: "80mm",
    orderGuid: fullOrderGuid,
    orderDisplay: "S1-100",
    orderPresentation: "guid-only",
    storeCode: "S001",
    statusText: "*** Paid ***",
    printedAtIso: "2026-08-02T12:34:56.000Z",
    includeMachineCodes: true,
  });
  const text = receiptText(document);
  const qr = document.lines.find((line) => line.kind === "qr");
  const bytes = documentToEscPosBytes(document);
  const code128 = receiptCode128(fullOrderGuid);
  const code128Width = receiptCode128ModuleWidth(code128, document.paper);

  assert.equal(document.lines.some((line) => line.kind === "barcode"), false);
  assert.equal(qr?.value, fullOrderGuid);
  assert.match(text, new RegExp(`^${fullOrderGuid}$`, "m"));
  assert.doesNotMatch(text, /^Order(?:#|:)/m);
  assert.doesNotMatch(text, /^S1-100$/m);
  assert.match(text, /Store: Brisbane \(S001\)/);
  assert.match(text, new RegExp(`Print Time: ${localReceiptTime("2026-08-02T12:34:56.000Z")}`));
  assert.ok(text.indexOf("TAX INVOICE") < text.indexOf("*** Paid ***"));
  assert.ok(text.indexOf("*** Paid ***") < text.indexOf(fullOrderGuid));
  assert.ok(text.indexOf(fullOrderGuid) < text.indexOf("Date:"));
  assert.ok(text.indexOf("Date:") < text.indexOf("ITEM"));
  assert.ok(text.indexOf("Payment:") < text.indexOf("Print Time:"));
  assert.ok(document.lines.every((line) => line.kind === "feed" || line.kind === "barcode" || line.kind === "qr" || displayWidth(line.text) <= 42));
  assert.equal(code128Width, null);
  assert.equal(containsBytes(bytes, [0x1d, 0x6b, 73]), false);
  assert.ok(containsBytes(bytes, [
    0x1d, 0x28, 0x6b, 4, 0, 49, 65, 50, 0,
    0x1d, 0x28, 0x6b, 3, 0, 49, 67, 6,
    0x1d, 0x28, 0x6b, 3, 0, 49, 69, 49,
    0x1d, 0x28, 0x6b, 39, 0, 49, 80, 48,
    ...new TextEncoder().encode(fullOrderGuid),
    0x1d, 0x28, 0x6b, 3, 0, 49, 81, 48,
  ]));
});

test("销售小票在设备与商品表之间安全渲染扩展信息行", () => {
  const document = buildSaleReceiptDocument({
    ...sale,
    extraInfoLines: [
      "Installment No: INS-100",
      "Customer: Alice Example",
      "Payment history:",
    ],
  });
  const text = receiptText(document);

  assert.ok(text.indexOf("Device: IPAD-1") < text.indexOf("Installment No: INS-100"));
  assert.ok(text.indexOf("Installment No: INS-100") < text.indexOf("ITEM"));
  assert.throws(
    () => buildSaleReceiptDocument({
      ...sale,
      extraInfoLines: ["Customer: Alice\u001b@"],
    }),
    /control characters/i,
  );
});

test("80mm 销售小票严格使用 WPF 商品列、标题和正式打印语义", () => {
  const document = buildSaleReceiptDocument({
    ...sale,
    paper: "80mm",
    store: {
      ...sale.store,
      brandName: "Brisbane",
      storeName: "Brisbane",
      address: "Shop 1 Sunnybank Shopping Centre Brisbane Queensland",
    },
    orderGuid: fullOrderGuid,
    orderDisplay: "#100",
    orderPresentation: "guid-only",
    storeCode: "S001",
    isReprint: true,
    includeMachineCodes: true,
    printedAtIso: sale.soldAtIso,
    lines: [{
      name: "Spring water",
      lookupCode: "930000000001",
      quantity: "2",
      discountCents: 50,
      totalCents: 548,
    }],
  });
  const lines = document.lines
    .filter((line) => line.kind === "text" || line.kind === "separator")
    .map((line) => line.text);

  assert.equal(lines.filter((line) => line === "Brisbane").length, 1, "品牌与分店同名时只显示一次");
  assert.ok(lines.includes("Shop 1 Sunnybank Shopping Centre"));
  assert.ok(lines.includes("Brisbane Queensland"));
  assert.ok(lines.includes(fullOrderGuid));
  assert.equal(lines.some((line) => /^Order(?:#|:)/.test(line)), false);
  assert.equal(lines.includes("#100"), false);
  assert.ok(lines.includes("===== TAX INVOICE ====="));
  assert.ok(lines.includes("*** Paid ***"));
  assert.ok(lines.includes("*** REPRINT ***"));
  assert.equal(lines.some((line) => /^Change\b|^找零/.test(line)), false);
  assert.ok(lines.includes(`${"ITEM".padEnd(25)}${"QTY".padStart(5)}${"PRICE".padStart(12)}`));
  assert.ok(lines.includes(`${"930000000001".padEnd(25)}${"2".padStart(5)}${"$5.48".padStart(12)}`));
  assert.ok(lines.includes(`${"Dis"}${"-$0.50".padStart(42 - 3)}`));
  assert.equal(lines.some((line) => line.includes("$2.99")), false, "WPF 商品行不打印单价");
  assert.ok(lines.every((line) => displayWidth(line) <= 42));
  assert.ok(containsBytes(
    documentToEscPosBytes(document),
    [0x1b, 0x61, 0, 0x1b, 0x45, 0, ...new TextEncoder().encode("----------")],
  ), "分隔线必须显式恢复左对齐和非粗体，不能继承 Total 的状态");
});

test("分期小票顶部条码：取显示单号末 8 位并转大写，印在「Order:」行之前；不传时与原输出逐字节一致", () => {
  const installmentNumber = "IP-1013-0d4c2c7e9a8b4f7c8e6a5b4c3d2e1f00";
  assert.equal(receiptOrderShortCode(installmentNumber), "3D2E1F00");
  assert.equal(receiptOrderShortCode("  ins-100  "), "INS-100", "不足 8 位取全部，去掉首尾空白");
  assert.equal(receiptOrderShortCode("ab"), "AB");

  const base = { ...sale, orderGuid: fullOrderGuid, orderDisplay: installmentNumber, includeMachineCodes: true };
  for (const paper of ["58mm", "80mm"] as const) {
    const without = buildSaleReceiptDocument({ ...base, paper });
    const withBarcode = buildSaleReceiptDocument({ ...base, paper, orderBarcode: receiptOrderShortCode(installmentNumber) });

    const barcodeIndexes = withBarcode.lines.flatMap((line, index) => (line.kind === "barcode" ? [index] : []));
    assert.equal(barcodeIndexes.length, 1, `${paper}：只多出顶部这一条条码`);
    const barcode = withBarcode.lines[barcodeIndexes[0]!]!;
    assert.ok(barcode.kind === "barcode" && barcode.value === "3D2E1F00");
    const orderIndex = withBarcode.lines.findIndex((line) => line.kind === "text" && line.text.startsWith("Order: "));
    assert.ok(orderIndex > barcodeIndexes[0]!, `${paper}：条码在「Order:」行之前`);
    assert.ok(
      withBarcode.lines.filter((line) => line.kind === "text" && line.text.startsWith("Order: ")).length === 1,
      "完整单号仍由「Order:」行印出",
    );
    // 去掉那一条条码行后，其余内容与不传时完全相同。
    assert.deepEqual(
      withBarcode.lines.filter((_, index) => index !== barcodeIndexes[0]),
      without.lines,
    );
    // 字节层：标准 2 点模块 Code128 命令出现在带条码的小票里，不传时没有。
    assert.ok(containsBytes(documentToEscPosBytes(withBarcode), [0x1d, 0x77, 2]));
    assert.equal(containsBytes(documentToEscPosBytes(without), [0x1d, 0x77, 2]), false);

    // 空串、纯空白：与不传（上面的 without）逐字节一致，其余所有小票的输出保持不变。
    const baseline = documentToEscPosBytes(without);
    for (const orderBarcode of ["", "   "]) {
      assert.deepEqual(documentToEscPosBytes(buildSaleReceiptDocument({ ...base, paper, orderBarcode })), baseline);
    }
  }
});

test("分期小票顶部条码只是辅助扫码：放不下、非可打印 ASCII 时静默省略，小票照常生成；控制字符照旧拒绝", () => {
  const base = { ...sale, orderGuid: fullOrderGuid, orderDisplay: "IP-1013-AAAA", includeMachineCodes: true };
  const withoutBarcode = buildSaleReceiptDocument(base);
  // 58mm 放不下的超长值、含中文（receiptCode128 会抛 TypeError）都不得让小票失败。
  for (const orderBarcode of ["A".repeat(60), "单号-1"]) {
    const document = buildSaleReceiptDocument({ ...base, orderBarcode });
    assert.deepEqual(document.lines, withoutBarcode.lines, orderBarcode);
    assert.doesNotThrow(() => documentToEscPosBytes(document));
  }
  assert.throws(
    () => buildSaleReceiptDocument({ ...base, orderBarcode: "AB\u001bC" }),
    /control characters/i,
  );
});

test("销售小票统一拒绝可注入 ESC/POS 命令的控制字符", () => {
  assert.throws(
    () => buildSaleReceiptDocument({
      ...sale,
      store: { ...sale.store, brandName: "Hot\u001b@Bargain" },
    }),
    /control characters/i,
  );
  assert.throws(
    () => buildSaleReceiptDocument({
      ...sale,
      lines: [{ ...sale.lines[0]!, lookupCode: "9300\u001dpulse" }],
    }),
    /control characters/i,
  );
});

test("58/80mm 对放不下的完整 GUID 都省略一维码并保留完整 QR", () => {
  const worstCaseGuid = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";
  const narrow = buildSaleReceiptDocument({
    ...sale,
    orderGuid: worstCaseGuid,
    orderDisplay: "#42",
    orderPresentation: "guid-only",
    includeMachineCodes: true,
  });
  const wide = buildSaleReceiptDocument({
    ...sale,
    paper: "80mm",
    orderGuid: worstCaseGuid,
    orderDisplay: "#42",
    orderPresentation: "guid-only",
    includeMachineCodes: true,
  });

  assert.equal(narrow.lines.some((line) => line.kind === "barcode"), false);
  assert.ok(narrow.lines.some((line) => line.kind === "qr" && line.value === worstCaseGuid));
  assert.equal(
    narrow.lines
      .filter((line) => line.kind === "text")
      .map((line) => line.text)
      .filter((line) => worstCaseGuid.includes(line))
      .join(""),
    worstCaseGuid,
  );
  assert.equal(narrow.lines.some((line) => line.kind === "text" && /^Order(?:#|:)/.test(line.text)), false);
  assert.equal(wide.lines.some((line) => line.kind === "barcode"), false);
  assert.ok(wide.lines.some((line) => line.kind === "qr" && line.value === worstCaseGuid));
  assert.ok(wide.lines.some((line) => line.kind === "text" && line.text === worstCaseGuid));
});

test("可装入纸宽的短 Code 128 使用标准双点模块输出", () => {
  const document = {
    locale: "en" as const,
    paper: "58mm" as const,
    lines: [{ kind: "barcode" as const, value: "ORDER-42" }],
  };
  const bytes = documentToEscPosBytes(document);

  assert.ok(containsBytes(bytes, [0x1d, 0x77, 2]));
  assert.equal(containsBytes(bytes, [0x1d, 0x77, 1]), false);
});

test("中文 CJK 双宽换行与 80mm 对齐不截断 surrogate，重打票显示规范标记", () => {
  const document = buildSaleReceiptDocument({
    ...sale,
    locale: "zh-CN",
    paper: "80mm",
    isReprint: true,
    title: "退货小票",
    statusText: "*** 已退款 ***",
    lines: [{ name: "超长中文商品名称测试用矿泉水😀", lookupCode: "OPENITEM", quantity: "-1", discountCents: 0, totalCents: -500 }],
    subtotalCents: -500,
    discountCents: 0,
    totalCents: -500,
    tenders: [{ method: "cash", amountCents: -500 }],
    cashChangeCents: 0,
  });
  const text = receiptText(document);

  assert.match(text, /\*\*\* 重打 \/ REPRINT \*\*\*/);
  assert.match(text, /退货小票/);
  assert.match(text, /\$-5\.00/);
  assert.ok(document.lines.every((line) => line.kind !== "text" || displayWidth(line.text) <= 42));
  assert.ok(document.lines.every((line) => line.kind !== "text" || !/[\ud800-\udbff]$/.test(line.text)));
});

test("零金额订单和日结文档不产生浮点误差", () => {
  const zero = buildSaleReceiptDocument({ ...sale, totalCents: 0, subtotalCents: 0, discountCents: 0, tenders: [], cashChangeCents: null });
  const close = buildDailyCloseDocument({
    locale: "en", paper: "80mm", storeName: "Hot Bargain", businessDate: "2026-07-28", deviceCode: "IPAD-1", cashierName: "Alice",
    paymentTotals: [{ method: "cash", salesCents: 105, refundCents: -25, netCents: 80 }], orderCount: 2, salesCents: 105, refundCents: -25, netCents: 80,
    expectedCashCents: 80, countedCashCents: 75, differenceCents: -5,
  });
  assert.match(receiptText(zero), /\$0\.00/);
  assert.match(close.lines.filter((line) => line.kind === "text" || line.kind === "separator").map((line) => line.text).join("\n"), /Cash Difference\s+\$-0\.05/);
});

test("银行卡回单只保留掩码引用，禁止 PAN、token 与 voucher 明文", () => {
  const document = buildBankReceiptDocument({
    locale: "en", paper: "58mm", status: "APPROVED", cardType: "VISA", maskedCardNumber: "4111 1111 1111 1234",
    reference: "ABCDEF123456", rawText: "PAN 4111 1111 1111 1111\nTOKEN: secret-token\nVOUCHER: ABC123\nRRN: 9876543210",
  });
  const text = document.lines.filter((line) => line.kind === "text" || line.kind === "separator").map((line) => line.text).join("\n");
  assert.match(text, /\*\*\*\*1234/);
  assert.match(text, /\*\*\*\*3210/);
  assert.doesNotMatch(text, /4111 1111|secret-token|VOUCHER: ABC123|ABCDEF123456/);
});
