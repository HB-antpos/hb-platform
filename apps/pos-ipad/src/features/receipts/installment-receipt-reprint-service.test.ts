import assert from "node:assert/strict";
import test from "node:test";

import {
  InstallmentReceiptReprintPreparationService,
  isInstallmentReceiptReprintEligible,
  type FrozenInstallmentReceiptSettings,
  type InstallmentReceiptSettingsSource,
} from "./installment-receipt-reprint-service";
import type { FrozenReceiptReprintSettings } from "@hb/pos-receipt-core/features/receipts/receipt-reprint-service";

import type { InstallmentDetails, InstallmentsRemotePort } from "@/features/installments/installment-models";

const decoder = new TextDecoder();
const installmentGuid = "12345678-1234-1234-1234-abcdef47c164";

const settings: FrozenReceiptReprintSettings = {
  printerId: "printer-installment",
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
};

function payment(
  paymentGuid: string,
  amountCents: number,
  recordedAtIso: string,
  overrides: Partial<InstallmentDetails["payments"][number]> = {},
): InstallmentDetails["payments"][number] {
  return {
    paymentGuid,
    method: "card",
    amountCents,
    status: "Recorded",
    recordedAtIso,
    cashierId: "cashier-1",
    deviceCode: "POS-1",
    cardType: "VISA",
    maskedCardNumber: "****4321",
    ...overrides,
  };
}

function details(overrides: Partial<InstallmentDetails> = {}): InstallmentDetails {
  return {
    installmentGuid,
    installmentNumber: "INS-100",
    storeCode: "BNE",
    deviceCode: "POS-1",
    cashierId: "cashier-1",
    cashierName: "Alice",
    customerName: "Customer One",
    customerPhone: "0400000000",
    createdAtIso: "2026-08-01T01:00:00.000Z",
    updatedAtIso: "2026-08-02T01:00:00.000Z",
    totalCents: 10_000,
    minimumDownPaymentCents: 2_000,
    downPaymentCents: 4_000,
    paidCents: 10_000,
    balanceCents: 0,
    status: "PaidOff",
    lines: [{
      installmentLineGuid: "12345678-1234-1234-1234-000000000001",
      productCode: "P-1",
      referenceCode: null,
      displayName: "Spring water",
      lookupCode: "930000000001",
      quantity: "2",
      unitPriceCents: 5_000,
      discountCents: 0,
      actualAmountCents: 10_000,
      itemNumber: "SKU-1",
    }],
    payments: [
      payment("12345678-1234-1234-1234-000000000003", 6_000, "2026-08-02T02:00:00.000Z"),
      payment("12345678-1234-1234-1234-000000000002", 4_000, "2026-08-01T02:00:00.000Z"),
      payment("12345678-1234-1234-1234-000000000004", 999, "2026-08-03T02:00:00.000Z", {
        status: "Voided",
        maskedCardNumber: "****9999",
      }),
    ],
    pickupInfo: null,
    cancellationInfo: null,
    note: null,
    ...overrides,
  };
}

function createService(input: Readonly<{
  response?: InstallmentDetails | null;
  getDetails?: InstallmentsRemotePort["getDetails"];
  settingValue?: FrozenInstallmentReceiptSettings | null;
  trustedStoreCode?: string;
  trustedDeviceCode?: string;
  refundVouchers?: Readonly<{
    renderVouchers(installmentGuid: string, orderLabel: string): Promise<Readonly<{ printerId: string; receiptBytes: Uint8Array }> | null>;
  }>;
}>) {
  const detailCalls: string[] = [];
  let settingsCalls = 0;
  const installments: Pick<InstallmentsRemotePort, "getDetails"> = {
    async getDetails(requestedGuid) {
      detailCalls.push(requestedGuid);
      return input.getDetails
        ? input.getDetails(requestedGuid)
        : (input.response ?? null);
    },
  };
  const settingSource: InstallmentReceiptSettingsSource = {
    async getFrozenReceiptSettings() {
      settingsCalls += 1;
      return input.settingValue === undefined ? settings : input.settingValue;
    },
  };
  return {
    detailCalls,
    get settingsCalls() {
      return settingsCalls;
    },
    service: new InstallmentReceiptReprintPreparationService({
      installments,
      settings: settingSource,
      trustedStoreCode: input.trustedStoreCode ?? "BNE",
      trustedDeviceCode: input.trustedDeviceCode ?? "POS-1",
      nowIso: () => "2026-08-03T03:04:05.000Z",
      ...(input.refundVouchers ? { refundVouchers: input.refundVouchers } : {}),
    }),
  };
}

test("分期重打纯资格与 prepare 共用金额和状态约束", () => {
  assert.equal(isInstallmentReceiptReprintEligible(details()), true);
  assert.equal(isInstallmentReceiptReprintEligible(details({
    lines: [{ ...details().lines[0]!, actualAmountCents: 9_999 }],
  })), false);
  assert.equal(isInstallmentReceiptReprintEligible(details({
    paidCents: 9_999,
  })), false);
  assert.equal(isInstallmentReceiptReprintEligible(details({
    customerName: "Customer\u001b@",
  })), false);
});

test("合法 VoidCancel 保留原定金和未付余额时仍可重打", async () => {
  const voided = details({
    totalCents: 8_000,
    minimumDownPaymentCents: 2_000,
    downPaymentCents: 2_000,
    paidCents: 2_000,
    balanceCents: 6_000,
    status: "Cancelled",
    lines: [{
      ...details().lines[0]!,
      unitPriceCents: 4_000,
      actualAmountCents: 8_000,
    }],
    payments: [payment(
      "12345678-1234-1234-1234-000000000002",
      2_000,
      "2026-08-01T02:00:00.000Z",
      { method: "cash", cardType: null, maskedCardNumber: null },
    )],
    cancellationInfo: {
      kind: "VoidCancel",
      cancelledAtIso: "2026-08-03T01:02:03.000Z",
      cancelledBy: "Alice",
      reason: "Entered by mistake",
    },
  });

  assert.equal(isInstallmentReceiptReprintEligible(voided), true);
  const prepared = await createService({ response: voided }).service.prepare(installmentGuid);
  const receipt = decoder.decode(prepared?.receiptBytes);
  assert.equal(prepared?.orderGuid, installmentGuid);
  assert.match(receipt, /\*\*\* Installment Cancelled \*\*\*/u);
  assert.match(receipt, /Deposit paid: \$20\.00/u);
  assert.match(receipt, /Balance due: \$60\.00/u);
  assert.match(receipt, /Cash\s+\$20\.00/u);
});

test("合法 RefundCancel 使用正定金与负退款的 Recorded 净额归零", () => {
  const refunded = details({
    totalCents: 8_000,
    minimumDownPaymentCents: 2_000,
    downPaymentCents: 2_000,
    paidCents: 0,
    balanceCents: 0,
    status: "Cancelled",
    lines: [{
      ...details().lines[0]!,
      unitPriceCents: 4_000,
      actualAmountCents: 8_000,
    }],
    payments: [
      payment(
        "12345678-1234-1234-1234-000000000002",
        2_000,
        "2026-08-01T02:00:00.000Z",
        { method: "cash", cardType: null, maskedCardNumber: null },
      ),
      payment(
        "12345678-1234-1234-1234-000000000005",
        -2_000,
        "2026-08-03T01:02:03.000Z",
        { method: "cash", cardType: null, maskedCardNumber: null },
      ),
    ],
    cancellationInfo: {
      kind: "RefundCancel",
      cancelledAtIso: "2026-08-03T01:02:03.000Z",
      cancelledBy: "Alice",
      reason: "Customer request",
    },
  });

  assert.equal(isInstallmentReceiptReprintEligible(refunded), true);
});

test("退代金券取消：现金与刷卡原付款以退款券按合计抵平仍可补打，混入非券退款则不可", () => {
  const cancelled = (refunds: InstallmentDetails["payments"]) =>
    details({
      totalCents: 8_000,
      minimumDownPaymentCents: 2_000,
      downPaymentCents: 2_000,
      paidCents: 0,
      balanceCents: 0,
      status: "Cancelled",
      lines: [{
        ...details().lines[0]!,
        unitPriceCents: 4_000,
        actualAmountCents: 8_000,
      }],
      payments: [
        payment(
          "12345678-1234-1234-1234-000000000002",
          2_000,
          "2026-08-01T02:00:00.000Z",
          { method: "cash", cardType: null, maskedCardNumber: null },
        ),
        payment(
          "12345678-1234-1234-1234-000000000003",
          6_000,
          "2026-08-02T02:00:00.000Z",
        ),
        ...refunds,
      ],
      cancellationInfo: {
        kind: "RefundCancel",
        cancelledAtIso: "2026-08-03T01:02:03.000Z",
        cancelledBy: "Alice",
        reason: "Customer request",
      },
    });
  const voucherRefund = (paymentGuid: string, amountCents: number) =>
    payment(paymentGuid, amountCents, "2026-08-03T01:02:03.000Z", {
      method: "voucher",
      cardType: null,
      maskedCardNumber: null,
    });

  assert.equal(
    isInstallmentReceiptReprintEligible(
      cancelled([
        voucherRefund("12345678-1234-1234-1234-000000000005", -2_000),
        voucherRefund("12345678-1234-1234-1234-000000000006", -6_000),
      ]),
    ),
    true,
  );
  // 合计抵平但退款里混入现金：既不满足按方式逐项抵平，也不满足"全为代金券"。
  assert.equal(
    isInstallmentReceiptReprintEligible(
      cancelled([
        voucherRefund("12345678-1234-1234-1234-000000000005", -6_000),
        payment(
          "12345678-1234-1234-1234-000000000006",
          -2_000,
          "2026-08-03T01:02:03.000Z",
          { method: "card" },
        ),
      ]),
    ),
    false,
  );
  // 全为代金券但合计未抵平。
  assert.equal(
    isInstallmentReceiptReprintEligible(
      cancelled([
        voucherRefund("12345678-1234-1234-1234-000000000005", -2_000),
        voucherRefund("12345678-1234-1234-1234-000000000006", -5_000),
      ]),
    ),
    false,
  );
});

test("补打已取消分期时在小票后追加本机退款券券面；无券、打印机不一致或券面异常时只补打小票", async () => {
  const cancelled = details({
    paidCents: 0,
    balanceCents: 0,
    status: "Cancelled",
    payments: [
      payment(
        "12345678-1234-1234-1234-000000000002",
        10_000,
        "2026-08-01T02:00:00.000Z",
        { method: "cash", cardType: null, maskedCardNumber: null },
      ),
      payment(
        "12345678-1234-1234-1234-000000000005",
        -10_000,
        "2026-08-03T01:02:03.000Z",
        { method: "voucher", cardType: null, maskedCardNumber: null },
      ),
    ],
    cancellationInfo: {
      kind: "RefundCancel",
      cancelledAtIso: "2026-08-03T01:02:03.000Z",
      cancelledBy: "Alice",
      reason: "Customer request",
    },
  });
  const labels: string[] = [];
  const render = async (rendered: Readonly<{ printerId: string; receiptBytes: Uint8Array }> | null | "throw") => {
    const harness = createService({
      response: cancelled,
      refundVouchers: {
        async renderVouchers(guid, label) {
          assert.equal(guid, installmentGuid);
          labels.push(label);
          if (rendered === "throw") throw new Error("material broken");
          return rendered;
        },
      },
    });
    const prepared = await harness.service.prepare(installmentGuid);
    return decoder.decode(prepared?.receiptBytes);
  };
  const voucherBytes = new TextEncoder().encode("REFUND VOUCHER APPENDIX");

  const appended = await render({ printerId: "printer-installment", receiptBytes: voucherBytes });
  assert.match(appended, /\*\*\* REPRINT \*\*\*/u);
  assert.ok(appended.endsWith("REFUND VOUCHER APPENDIX"));
  assert.deepEqual(labels, ["INS-100"]);

  for (const variant of [null, { printerId: "printer-other", receiptBytes: voucherBytes }, "throw"] as const) {
    const receiptOnly = await render(variant);
    assert.match(receiptOnly, /\*\*\* REPRINT \*\*\*/u);
    assert.doesNotMatch(receiptOnly, /REFUND VOUCHER APPENDIX/u);
  }

  // 未取消的分期补打不读取退款券。
  const active = createService({
    response: details(),
    refundVouchers: {
      async renderVouchers() {
        throw new Error("must not be called");
      },
    },
  });
  assert.ok(await active.service.prepare(installmentGuid));
});

test("prepare 点击时重读可信分期并按 WPF 字段生成带 REPRINT 的票据", async () => {
  const harness = createService({ response: details() });

  const prepared = await harness.service.prepare(installmentGuid);
  const receipt = decoder.decode(prepared?.receiptBytes);

  assert.deepEqual(harness.detailCalls, [installmentGuid]);
  assert.equal(harness.settingsCalls, 1);
  assert.equal(prepared?.orderGuid, installmentGuid);
  assert.equal(
    (prepared as (typeof prepared & { externalOrderGuid?: string }))
      ?.externalOrderGuid,
    installmentGuid,
  );
  assert.equal(prepared?.printerId, "printer-installment");
  assert.match(receipt, /\*\*\* REPRINT \*\*\*/u);
  assert.match(receipt, /\*\*\* Paid - Pickup Pending \*\*\*/u);
  assert.match(receipt, /Order: INS-100/u);
  assert.match(receipt, /Installment No: INS-100/u);
  assert.match(receipt, /Customer: Customer One/u);
  assert.match(receipt, /Phone: 0400000000/u);
  assert.match(receipt, /Deposit paid: \$40\.00/u);
  assert.match(receipt, /Balance due: \$0\.00/u);
  assert.match(receipt, /Payment history:/u);
  assert.ok(receipt.indexOf("$40.00") < receipt.indexOf("$60.00"));
  assert.match(receipt, /VISA/u);
  assert.match(receipt, /\*\*\*\*4321/u);
  assert.doesNotMatch(receipt, /\*\*\*\*9999/u);
  assert.match(receipt, new RegExp(installmentGuid, "u"));
});

test("prepare 对四种分期状态使用 WPF 状态与提货语义", async () => {
  const scenarios = [
    {
      value: details({
        status: "Active",
        paidCents: 4_000,
        balanceCents: 6_000,
        payments: [payment("12345678-1234-1234-1234-000000000002", 4_000, "2026-08-01T02:00:00.000Z")],
      }),
      expected: "*** Deposit Received ***",
      showsTerms: true,
    },
    { value: details(), expected: "*** Paid - Pickup Pending ***", showsTerms: false },
    {
      value: details({
        status: "PickedUp",
        pickupInfo: {
          pickedUpAtIso: "2026-08-03T01:02:03.000Z",
          pickedUpBy: "Bob",
          note: "Back door",
        },
      }),
      expected: "*** Paid - Picked Up ***",
      showsTerms: false,
    },
    {
      value: details({
        status: "Cancelled",
        paidCents: 0,
        balanceCents: 0,
        payments: [
          payment(
            "12345678-1234-1234-1234-000000000002",
            10_000,
            "2026-08-01T02:00:00.000Z",
          ),
          payment(
            "12345678-1234-1234-1234-000000000005",
            -10_000,
            "2026-08-03T01:02:03.000Z",
          ),
        ],
        cancellationInfo: {
          kind: "RefundCancel",
          cancelledAtIso: "2026-08-03T01:02:03.000Z",
          cancelledBy: "Alice",
          reason: "Customer request",
        },
      }),
      expected: "*** Installment Cancelled ***",
      showsTerms: false,
    },
  ] as const;

  for (const scenario of scenarios) {
    const prepared = await createService({ response: scenario.value }).service.prepare(installmentGuid);
    const receipt = decoder.decode(prepared?.receiptBytes);
    assert.match(receipt, new RegExp(scenario.expected.replace(/[.*+?^${}()|[\]\\]/gu, "\\$&"), "u"));
    // 分期条款只在进行中（Active）的补打小票上打印；已付清、已提货、已取消都不带。
    if (scenario.showsTerms) {
      assert.match(receipt, /INSTALLMENT TERMS/u, scenario.expected);
    } else {
      assert.doesNotMatch(receipt, /INSTALLMENT TERMS/u, scenario.expected);
      assert.doesNotMatch(receipt, /Order total: \$50\.00 minimum\./u, scenario.expected);
    }
  }
  const pickedUp = decoder.decode(
    (await createService({ response: scenarios[2].value }).service.prepare(installmentGuid))?.receiptBytes,
  );
  assert.match(pickedUp, /Pickup: Confirmed/u);
  assert.match(pickedUp, /Picked up by: Bob/u);
  assert.match(pickedUp, /Pickup note: Back door/u);
});

function activeInstallment(overrides: Partial<InstallmentDetails> = {}): InstallmentDetails {
  return details({
    status: "Active",
    paidCents: 4_000,
    balanceCents: 6_000,
    payments: [payment("12345678-1234-1234-1234-000000000002", 4_000, "2026-08-01T02:00:00.000Z")],
    ...overrides,
  });
}

test("进行中分期补打在退货政策之后、条码之前追加英文分期条款，80mm 按纸宽换行", async () => {
  const harness = createService({
    response: activeInstallment(),
    settingValue: { ...settings, store: { ...settings.store, returnPolicy: "Refunds within 14 days with proof of purchase." } },
  });

  const receipt = decoder.decode((await harness.service.prepare(installmentGuid))?.receiptBytes);

  const policy = receipt.indexOf("Refunds and returns");
  const title = receipt.indexOf("INSTALLMENT TERMS");
  // 条款之后依次是分隔线、完整 GUID 的 QR 载荷与页脚，所以取条款后第一次出现的 GUID 比较。
  const machineCode = receipt.indexOf(installmentGuid, title);
  const printTime = receipt.indexOf("Print Time:");
  assert.ok(policy >= 0 && title > policy, "条款在退货政策之后");
  assert.ok(machineCode > title && printTime > machineCode, "条款在条码/QR 与页脚之前");
  assert.match(receipt, /Order total: \$50\.00 minimum\./u);
  assert.match(receipt, /First payment: \$20\.00 minimum\./u);
  // 第三条在 80mm（42 字符）下于单词边界换成两行，不是手工拆行。
  assert.match(receipt, /Each later payment: \$5\.00 minimum, or the[\s\S]{1,12}remaining balance if it is lower\./u);
  assert.equal(receipt.split("INSTALLMENT TERMS").length - 1, 1);
});

test("进行中分期在 zh-CN 与 58mm 下仍原样打印英文条款并按 32 字符换行", async () => {
  const harness = createService({
    response: activeInstallment(),
    settingValue: { ...settings, locale: "zh-CN", paper: "58mm" },
  });

  const receipt = decoder.decode((await harness.service.prepare(installmentGuid))?.receiptBytes);

  assert.match(receipt, /INSTALLMENT TERMS/u);
  assert.match(receipt, /Order total: \$50\.00 minimum\./u);
  assert.match(receipt, /First payment: \$20\.00 minimum\./u);
  assert.match(receipt, /Each later payment: \$5\.00[\s\S]{1,12}minimum, or the remaining[\s\S]{1,12}balance if it is lower\./u);
});

test("状态为进行中但带提货信息的不一致数据不打印分期条款", async () => {
  const receipt = decoder.decode((await createService({
    response: activeInstallment({
      pickupInfo: { pickedUpAtIso: "2026-08-03T01:02:03.000Z", pickedUpBy: "Bob", note: null },
    }),
  }).service.prepare(installmentGuid))?.receiptBytes);

  assert.doesNotMatch(receipt, /INSTALLMENT TERMS/u);
});

test("总部下发了分期条款正文：进行中补打标题仍是 INSTALLMENT TERMS，正文换成自定义行（空行丢弃、逐行 trim），默认条款不再出现", async () => {
  const harness = createService({
    response: activeInstallment(),
    settingValue: {
      ...settings,
      store: { ...settings.store, returnPolicy: "Refunds within 14 days with proof of purchase." },
      installmentTerms: "  Deposit $30 minimum.  \r\n\r\n   \nLater payments from $10.\rBalance due within 90 days.",
    },
  });

  const receipt = decoder.decode((await harness.service.prepare(installmentGuid))?.receiptBytes);

  const policy = receipt.indexOf("Refunds and returns");
  const title = receipt.indexOf("INSTALLMENT TERMS");
  const machineCode = receipt.indexOf(installmentGuid, title);
  assert.ok(policy >= 0 && title > policy, "条款仍在退货政策之后");
  assert.ok(machineCode > title, "条款仍在条码/QR 之前");
  assert.equal(receipt.split("INSTALLMENT TERMS").length - 1, 1);
  // 三行正文依次紧跟标题，空行被丢弃、行首尾空白被去掉。
  assert.ok(receipt.indexOf("Deposit $30 minimum.", title) > title);
  assert.ok(receipt.indexOf("Later payments from $10.", title) > receipt.indexOf("Deposit $30 minimum.", title));
  assert.ok(receipt.indexOf("Balance due within 90 days.", title) > receipt.indexOf("Later payments from $10.", title));
  assert.doesNotMatch(receipt, /Order total: \$50\.00 minimum\./u);
  assert.doesNotMatch(receipt, /First payment: \$20\.00 minimum\./u);
  assert.doesNotMatch(receipt, /Each later payment/u);
});

test("分期条款未定制（缺省 / null / 空串 / 纯空白）时与不带该字段的补打小票逐字节一致，仍是默认条款", async () => {
  const baseline = (await createService({ response: activeInstallment() }).service.prepare(installmentGuid))?.receiptBytes;
  assert.ok(baseline);
  assert.match(decoder.decode(baseline), /Order total: \$50\.00 minimum\./u);

  for (const blank of [undefined, null, "", "   ", "\r\n \t\n"]) {
    const prepared = await createService({
      response: activeInstallment(),
      settingValue: { ...settings, installmentTerms: blank },
    }).service.prepare(installmentGuid);
    assert.deepEqual(prepared?.receiptBytes, baseline, JSON.stringify(blank));
  }
});

test("分期条款自定义正文不合规（含 ESC 等控制字符、超过 600）时整份丢弃回退默认条款，补打不失败", async () => {
  const baseline = (await createService({ response: activeInstallment() }).service.prepare(installmentGuid))?.receiptBytes;
  assert.ok(baseline);

  for (const installmentTerms of [
    "Deposit $30 minimum.\u001b@\u001b!\u0001",
    "Bell\u0007",
    "C1\u009b",
    "x".repeat(601),
  ]) {
    const prepared = await createService({
      response: activeInstallment(),
      settingValue: { ...settings, installmentTerms },
    }).service.prepare(installmentGuid);
    assert.deepEqual(prepared?.receiptBytes, baseline, JSON.stringify(installmentTerms).slice(0, 30));
  }

  // 恰好 600 个字符仍生效。
  const atLimit = await createService({
    response: activeInstallment(),
    settingValue: { ...settings, installmentTerms: "y".repeat(600) },
  }).service.prepare(installmentGuid);
  assert.notDeepEqual(atLimit?.receiptBytes, baseline);
  assert.match(decoder.decode(atLimit?.receiptBytes), /yyyyyyyyyyyyyyyyyyyy/u);
});

test("自定义分期条款同样只印在进行中的分期上：已付清、已取消的补打小票不带条款也不带自定义正文", async () => {
  const customSettings = { ...settings, installmentTerms: "Custom installment rule." };
  for (const response of [
    details({ status: "PaidOff" }),
    details({
      status: "Cancelled",
      paidCents: 0,
      balanceCents: 0,
      payments: [payment("12345678-1234-1234-1234-000000000002", 10_000, "2026-08-01T02:00:00.000Z")],
      cancellationInfo: {
        kind: "RefundCancel",
        cancelledAtIso: "2026-08-03T01:02:03.000Z",
        cancelledBy: "Alice",
        reason: "Customer request",
      },
    }),
  ]) {
    const receipt = decoder.decode(
      (await createService({ response, settingValue: customSettings }).service.prepare(installmentGuid))?.receiptBytes,
    );
    assert.doesNotMatch(receipt, /INSTALLMENT TERMS/u);
    assert.doesNotMatch(receipt, /Custom installment rule\./u);
  }
});

test("58mm 下自定义分期条款长句按 32 字符在单词边界换行，zh-CN 小票标题仍为英文", async () => {
  const harness = createService({
    response: activeInstallment(),
    settingValue: {
      ...settings,
      paper: "58mm",
      locale: "zh-CN",
      installmentTerms: "Installment deposits are non-refundable once goods are set aside.",
    },
  });

  const receipt = decoder.decode((await harness.service.prepare(installmentGuid))?.receiptBytes);

  assert.match(receipt, /INSTALLMENT TERMS/u);
  // 32 字符纸宽下在单词边界换行：不会把 non-refundable 拆开。
  assert.match(receipt, /Installment deposits are[\s\S]{1,12}non-refundable once goods are[\s\S]{1,12}set aside\./u);
  assert.doesNotMatch(receipt, /Order total/u);
});

test("prepare 对 GUID、门店或当前设备不一致 fail closed 且不读取设置", async () => {
  for (const response of [
    details({ installmentGuid: `${installmentGuid}-other` }),
    details({ storeCode: "SYD" }),
    details({ deviceCode: "POS-2" }),
  ]) {
    const harness = createService({ response });
    assert.equal(await harness.service.prepare(installmentGuid), null);
    assert.deepEqual(harness.detailCalls, [installmentGuid]);
    assert.equal(harness.settingsCalls, 0);
  }
});

test("prepare 对金额不一致、无效设置、控制字符和远程异常保守拒绝", async () => {
  const rejected = [
    details({ lines: [{ ...details().lines[0]!, actualAmountCents: 9_999 }] }),
    details({ paidCents: 9_999 }),
    details({ customerName: "Customer\u001b@" }),
  ];
  for (const response of rejected) {
    assert.equal(await createService({ response }).service.prepare(installmentGuid), null);
  }
  assert.equal(await createService({
    response: details(),
    settingValue: { ...settings, printerId: " " },
  }).service.prepare(installmentGuid), null);
  assert.equal(await createService({
    getDetails: async () => {
      throw new Error("network unavailable");
    },
  }).service.prepare(installmentGuid), null);
});
