import assert from "node:assert/strict";
import test from "node:test";

import {
  ReturnFeatureError,
  buildReturnRefundPlan,
  createNoReceiptDraftLine,
  createReceiptDraftLines,
  updateReturnLineQuantity,
  validateReceiptReturnContext,
  type OriginalReturnTenderCapacity,
  type ReceiptReturnContext,
} from "./return-domain";

test("小票行严格绑定原单、原明细和 returnSourceKey", () => {
  const mismatched = receiptContext({
    lines: [
      {
        ...receiptContext().lines[0]!,
        originalOrderGuid: "another-order",
      },
    ],
  });

  assert.throws(
    () => validateReceiptReturnContext(mismatched),
    hasCode("RETURN_SOURCE_MISMATCH"),
  );

  const missingDetail = receiptContext({
    lines: [
      {
        ...receiptContext().lines[0]!,
        originalOrderDetailGuid: "",
      },
    ],
  });
  assert.throws(
    () => validateReceiptReturnContext(missingDetail),
    hasCode("RETURN_SOURCE_MISMATCH"),
  );

  const duplicateCapacity = capacity("cash", 1_000, true);
  assert.throws(
    () =>
      validateReceiptReturnContext(
        receiptContext({
          tenderCapacities: [duplicateCapacity, duplicateCapacity],
        }),
      ),
    hasCode("RETURN_SOURCE_MISMATCH"),
  );
});

test("数量和金额均不能超过后端核验的行容量", () => {
  const lines = createReceiptDraftLines(receiptContext());
  assert.throws(
    () => updateReturnLineQuantity(lines, "line-a", 4),
    hasCode("RETURN_QUANTITY_EXCEEDED"),
  );
  assert.throws(
    () => updateReturnLineQuantity(lines, "line-a", 1.5),
    hasCode("RETURN_QUANTITY_INVALID"),
  );

  const amountBound = createReceiptDraftLines(
    receiptContext({
      lines: [
        {
          ...receiptContext().lines[0]!,
          availableQuantity: 3,
          unitRefundCents: 500,
          remainingAmountCents: 900,
        },
      ],
    }),
  );
  assert.throws(
    () => updateReturnLineQuantity(amountBound, "line-a", 2),
    hasCode("RETURN_AMOUNT_EXCEEDED"),
  );
});

test("整行退货用剩余整数分币闭合除不尽的尾差", () => {
  const context = receiptContext({
    lines: [
      {
        ...receiptContext().lines[0]!,
        availableQuantity: 3,
        unitRefundCents: 333,
        remainingAmountCents: 1_000,
      },
    ],
    tenderCapacities: [capacity("cash", 1_000, true)],
  });
  const selected = updateReturnLineQuantity(
    createReceiptDraftLines(context),
    "line-a",
    3,
  );
  const plan = buildReturnRefundPlan({
    sourceKind: "receipt",
    originalOrderGuid: "order-a",
    lines: selected,
    capacities: context.tenderCapacities,
    online: false,
    preferredMethod: "cash",
  });

  assert.equal(plan.totalRefundCents, 1_000);
  assert.equal(plan.lines[0]?.signedAmountCents, -1_000);
  assert.equal(plan.allocations[0]?.signedAmountCents, -1_000);
});

test("混合原 tender 容量按优先方式稳定拆分且从不超额", () => {
  const context = receiptContext({
    lines: [
      {
        ...receiptContext().lines[0]!,
        availableQuantity: 1,
        unitRefundCents: 10_000,
        remainingAmountCents: 10_000,
      },
    ],
    tenderCapacities: [
      capacity("cash", 4_000, true),
      capacity("card", 6_000, false),
      capacity("voucher", 2_000, false),
    ],
  });
  const selected = updateReturnLineQuantity(
    createReceiptDraftLines(context),
    "line-a",
    1,
  );
  const plan = buildReturnRefundPlan({
    sourceKind: "receipt",
    originalOrderGuid: "order-a",
    lines: selected,
    capacities: context.tenderCapacities,
    online: true,
    preferredMethod: "card",
  });

  assert.deepEqual(
    plan.allocations.map((allocation) => [
      allocation.method,
      allocation.signedAmountCents,
    ]),
    [
      ["card", -6_000],
      ["cash", -4_000],
    ],
  );
  assert.equal(
    plan.allocations.reduce(
      (total, allocation) => total + allocation.signedAmountCents,
      0,
    ),
    -10_000,
  );
});

test("刷卡订单可选现金代替退款：整单统一现金且仍绑定原卡容量防超退", () => {
  const context = receiptContext({
    lines: [
      {
        ...receiptContext().lines[0]!,
        availableQuantity: 1,
        unitRefundCents: 6_000,
        remainingAmountCents: 6_000,
      },
    ],
    tenderCapacities: [capacity("card", 6_000, false)],
  });
  const selected = updateReturnLineQuantity(
    createReceiptDraftLines(context),
    "line-a",
    1,
  );
  const plan = buildReturnRefundPlan({
    sourceKind: "receipt",
    originalOrderGuid: "order-a",
    lines: selected,
    capacities: context.tenderCapacities,
    online: true,
    preferredMethod: "cash",
  });

  // 代替为现金：method 统一为 cash，但额度仍从原 card capacity 扣减。
  assert.deepEqual(plan.allocations, [
    {
      method: "cash",
      signedAmountCents: -6_000,
      originalCapacityId: "capacity-card",
      originalOrderGuid: "order-a",
      offlineCashProof: null,
    },
  ]);
});

test("礼券可代替单一刷卡/现金额度并绑定原额度；分期额度与多笔原支付被拒", () => {
  const context = receiptContext({
    lines: [
      {
        ...receiptContext().lines[0]!,
        availableQuantity: 1,
        unitRefundCents: 5_000,
        remainingAmountCents: 5_000,
      },
    ],
    tenderCapacities: [capacity("card", 5_000, false)],
  });
  const selected = updateReturnLineQuantity(
    createReceiptDraftLines(context),
    "line-a",
    1,
  );
  for (const original of ["card", "cash", "voucher"] as const) {
    const plan = buildReturnRefundPlan({
      sourceKind: "receipt",
      originalOrderGuid: "order-a",
      lines: selected,
      capacities: [capacity(original, 5_000, false)],
      online: true,
      preferredMethod: "voucher",
    });
    assert.deepEqual(
      plan.allocations.map((allocation) => [
        allocation.method,
        allocation.signedAmountCents,
        allocation.originalCapacityId,
      ]),
      [["voucher", -5_000, `capacity-${original}`]],
    );
  }

  assert.throws(
    () =>
      buildReturnRefundPlan({
        sourceKind: "receipt",
        originalOrderGuid: "order-a",
        lines: selected,
        capacities: [capacity("installment", 5_000, false)],
        online: true,
        preferredMethod: "voucher",
      }),
    hasCode("RETURN_VOUCHER_SUBSTITUTE_UNAVAILABLE"),
  );
  // 多笔原支付会拆成多张券，退款券打印与同步只支持单张。
  assert.throws(
    () =>
      buildReturnRefundPlan({
        sourceKind: "receipt",
        originalOrderGuid: "order-a",
        lines: selected,
        capacities: [capacity("cash", 2_000, false), capacity("card", 3_000, false)],
        online: true,
        preferredMethod: "voucher",
      }),
    hasCode("RETURN_VOUCHER_SUBSTITUTE_UNAVAILABLE"),
  );
});

test("手工刷卡额度只能现金代替：默认原路退回被拒，现金代替绑定原卡额度", () => {
  const manualCard: OriginalReturnTenderCapacity = {
    ...capacity("card", 250, false),
    substituteOnly: true,
  };
  const context = receiptContext({
    lines: [
      {
        ...receiptContext().lines[0]!,
        availableQuantity: 1,
        unitRefundCents: 250,
        remainingAmountCents: 250,
      },
    ],
    tenderCapacities: [manualCard],
  });
  const selected = updateReturnLineQuantity(
    createReceiptDraftLines(context),
    "line-a",
    1,
  );
  for (const preferredMethod of [null, "card"] as const) {
    assert.throws(
      () =>
        buildReturnRefundPlan({
          sourceKind: "receipt",
          originalOrderGuid: "order-a",
          lines: selected,
          capacities: [manualCard],
          online: true,
          preferredMethod,
        }),
      hasCode("RETURN_ORIGINAL_REFUND_UNAVAILABLE"),
    );
  }

  const cashPlan = buildReturnRefundPlan({
    sourceKind: "receipt",
    originalOrderGuid: "order-a",
    lines: selected,
    capacities: [manualCard],
    online: true,
    preferredMethod: "cash",
  });
  assert.deepEqual(
    cashPlan.allocations.map((allocation) => [
      allocation.method,
      allocation.signedAmountCents,
      allocation.originalCapacityId,
    ]),
    [["cash", -250, "capacity-card"]],
  );
});

test("混合容量选现金代替：整单统一现金，容量依次扣减且总额不超", () => {
  const context = receiptContext({
    lines: [
      {
        ...receiptContext().lines[0]!,
        availableQuantity: 1,
        unitRefundCents: 10_000,
        remainingAmountCents: 10_000,
      },
    ],
    tenderCapacities: [
      capacity("cash", 4_000, true),
      capacity("card", 6_000, false),
    ],
  });
  const selected = updateReturnLineQuantity(
    createReceiptDraftLines(context),
    "line-a",
    1,
  );
  const plan = buildReturnRefundPlan({
    sourceKind: "receipt",
    originalOrderGuid: "order-a",
    lines: selected,
    capacities: context.tenderCapacities,
    online: true,
    preferredMethod: "cash",
  });

  assert.deepEqual(
    plan.allocations.map((allocation) => [
      allocation.method,
      allocation.signedAmountCents,
      allocation.originalCapacityId,
    ]),
    [
      ["cash", -4_000, "capacity-cash"],
      ["cash", -6_000, "capacity-card"],
    ],
  );
});

test("代替退款仍受原订单总剩余额度约束：容量不足时报容量超限", () => {
  const context = receiptContext({
    lines: [
      {
        ...receiptContext().lines[0]!,
        availableQuantity: 1,
        unitRefundCents: 10_000,
        remainingAmountCents: 10_000,
      },
    ],
    tenderCapacities: [capacity("card", 6_000, false)],
  });
  const selected = updateReturnLineQuantity(
    createReceiptDraftLines(context),
    "line-a",
    1,
  );
  assert.throws(
    () =>
      buildReturnRefundPlan({
        sourceKind: "receipt",
        originalOrderGuid: "order-a",
        lines: selected,
        capacities: context.tenderCapacities,
        online: true,
        preferredMethod: "cash",
      }),
    hasCode("RETURN_CAPACITY_EXCEEDED"),
  );
});

test("离线代替不可用：刷卡订单离线选现金代替仍被门禁", () => {
  const selected = updateReturnLineQuantity(
    createReceiptDraftLines(
      receiptContext({
        tenderCapacities: [capacity("card", 1_000, false)],
      }),
    ),
    "line-a",
    1,
  );
  assert.throws(
    () =>
      buildReturnRefundPlan({
        sourceKind: "receipt",
        originalOrderGuid: "order-a",
        lines: selected,
        capacities: [capacity("card", 1_000, false)],
        online: false,
        preferredMethod: "cash",
      }),
    hasCode("RETURN_ONLINE_REQUIRED"),
  );
});

test("离线选代金券代替被领域层直接拒绝，不生成携带现金证明的非现金分配", () => {
  // 原单有可证明的现金容量，但用户离线选择代金券代替：必须直接拒绝，
  // 避免生成 method=voucher 却携带 cash proof 的 allocation（预览与确认不一致）。
  const selected = updateReturnLineQuantity(
    createReceiptDraftLines(
      receiptContext({
        tenderCapacities: [capacity("cash", 1_000, true)],
      }),
    ),
    "line-a",
    1,
  );
  assert.throws(
    () =>
      buildReturnRefundPlan({
        sourceKind: "receipt",
        originalOrderGuid: "order-a",
        lines: selected,
        capacities: [capacity("cash", 1_000, true)],
        online: false,
        preferredMethod: "voucher",
      }),
    hasCode("RETURN_ONLINE_REQUIRED"),
  );

  // 离线选择现金代替（等于原现金容量原路退回）仍被允许。
  const cashPlan = buildReturnRefundPlan({
    sourceKind: "receipt",
    originalOrderGuid: "order-a",
    lines: selected,
    capacities: [capacity("cash", 1_000, true)],
    online: false,
    preferredMethod: "cash",
  });
  assert.deepEqual(cashPlan.allocations[0]?.method, "cash");

  // 离线选择 card/installment 排序偏好不改变 method，仍按原现金容量原路退回。
  const cardPrefPlan = buildReturnRefundPlan({
    sourceKind: "receipt",
    originalOrderGuid: "order-a",
    lines: selected,
    capacities: [capacity("cash", 1_000, true)],
    online: false,
    preferredMethod: "card",
  });
  assert.deepEqual(cardPrefPlan.allocations[0]?.method, "cash");

  // installment 排序偏好同样不改变 method，离线按现金原路退回。
  const installmentPrefPlan = buildReturnRefundPlan({
    sourceKind: "receipt",
    originalOrderGuid: "order-a",
    lines: selected,
    capacities: [capacity("cash", 1_000, true)],
    online: false,
    preferredMethod: "installment",
  });
  assert.deepEqual(installmentPrefPlan.allocations[0]?.method, "cash");
});

test("离线混合容量：退款额不超现金证明容量时按现金原路退回，超出则拒绝", () => {
  // 原单 = 卡 4,000（无证明）+ 现金 6,000（有证明），离线退款 5,000：
  // 卡容量被过滤，现金证明容量足够，按现金原路退回。
  const context = receiptContext({
    lines: [
      {
        ...receiptContext().lines[0]!,
        availableQuantity: 1,
        unitRefundCents: 5_000,
        remainingAmountCents: 5_000,
      },
    ],
    tenderCapacities: [
      capacity("card", 4_000, false),
      capacity("cash", 6_000, true),
    ],
  });
  const selected = updateReturnLineQuantity(
    createReceiptDraftLines(context),
    "line-a",
    1,
  );
  const cashPlan = buildReturnRefundPlan({
    sourceKind: "receipt",
    originalOrderGuid: "order-a",
    lines: selected,
    capacities: context.tenderCapacities,
    online: false,
    preferredMethod: null,
  });
  assert.deepEqual(
    cashPlan.allocations.map((allocation) => [
      allocation.method,
      allocation.signedAmountCents,
      allocation.originalCapacityId,
    ]),
    [["cash", -5_000, "capacity-cash"]],
  );

  // 退款额超过现金证明容量（7,000 > 6,000）时无剩余可退容量，拒绝。
  const overSelected = updateReturnLineQuantity(
    createReceiptDraftLines(
      receiptContext({
        lines: [
          {
            ...receiptContext().lines[0]!,
            availableQuantity: 1,
            unitRefundCents: 7_000,
            remainingAmountCents: 7_000,
          },
        ],
        tenderCapacities: [
          capacity("card", 4_000, false),
          capacity("cash", 6_000, true),
        ],
      }),
    ),
    "line-a",
    1,
  );
  assert.throws(
    () =>
      buildReturnRefundPlan({
        sourceKind: "receipt",
        originalOrderGuid: "order-a",
        lines: overSelected,
        capacities: context.tenderCapacities,
        online: false,
        preferredMethod: null,
      }),
    hasCode("RETURN_ONLINE_REQUIRED"),
  );
});

test("离线只接受带原单证明的现金容量，卡券分期均被门禁", () => {
  const selected = updateReturnLineQuantity(
    createReceiptDraftLines(
      receiptContext({
        tenderCapacities: [capacity("card", 1_000, false)],
      }),
    ),
    "line-a",
    1,
  );
  // 卡容量离线被 eligible 过滤（remaining>0 但非 cash+proof），无剩余可退
  // 容量而拒绝；与代替拦截无关（收紧意图由"离线选代金券代替"用例覆盖）。
  assert.throws(
    () =>
      buildReturnRefundPlan({
        sourceKind: "receipt",
        originalOrderGuid: "order-a",
        lines: selected,
        capacities: [capacity("card", 1_000, false)],
        online: false,
        preferredMethod: "card",
      }),
    hasCode("RETURN_ONLINE_REQUIRED"),
  );

  const unprovenCash = capacity("cash", 1_000, false);
  assert.throws(
    () =>
      buildReturnRefundPlan({
        sourceKind: "receipt",
        originalOrderGuid: "order-a",
        lines: selected,
        capacities: [unprovenCash],
        online: false,
        preferredMethod: "cash",
      }),
    hasCode("RETURN_ONLINE_REQUIRED"),
  );

  const cash = capacity("cash", 1_000, true);
  const plan = buildReturnRefundPlan({
    sourceKind: "receipt",
    originalOrderGuid: "order-a",
    lines: selected,
    capacities: [cash],
    online: false,
    preferredMethod: "cash",
  });
  assert.equal(plan.allocations[0]?.offlineCashProof?.evidenceId, "proof-cash");
});

test("OPENITEM 不能通过普通无小票商品路径绕过专用输入校验", () => {
  assert.throws(
    () =>
      createNoReceiptDraftLine({
        sourceKind: "no-receipt-product",
        selectionKey: "line-open",
        returnSourceKey: "noreceipt:BNE:open",
        productCode: "OPEN",
        itemNumber: null,
        lookupCode: "OPENITEM",
        displayName: "Open item",
        unitRefundCents: 500,
        syncProvenance: {
          referenceCode: null,
          priceSource: 0,
        },
      }),
    hasCode("RETURN_OPEN_ITEM_INVALID"),
  );
});

test("无小票草稿保留目录 lookupCode 供耐久退货行重建", () => {
  const line = createNoReceiptDraftLine({
    sourceKind: "no-receipt-product",
    selectionKey: "line-product",
    returnSourceKey: "noreceipt:BNE:product",
    productCode: "P-100",
    itemNumber: "1001",
    lookupCode: "9320001000012",
    displayName: "Product",
    unitRefundCents: 500,
    syncProvenance: {
      referenceCode: "REF-100",
      priceSource: 1,
    },
  });

  assert.equal(line.lookupCode, "9320001000012");
});

test("退货草稿冻结并严格规范化交易行同步来源", () => {
  const base = receiptContext();
  const receiptLines = createReceiptDraftLines({
    ...base,
    lines: [
      {
        ...base.lines[0]!,
        syncProvenance: {
          referenceCode: "  RECEIPT-REF  ",
          priceSource: 3,
        },
      },
    ],
  } as ReceiptReturnContext);
  assert.deepEqual(
    Reflect.get(receiptLines[0]!, "syncProvenance"),
    {
      referenceCode: "RECEIPT-REF",
      priceSource: 3,
    },
  );
  assert.equal(
    Object.isFrozen(
      Reflect.get(receiptLines[0]!, "syncProvenance"),
    ),
    true,
  );
  const selected = updateReturnLineQuantity(
    receiptLines,
    "line-a",
    1,
  );
  const plan = buildReturnRefundPlan({
    sourceKind: "receipt",
    originalOrderGuid: "order-a",
    lines: selected,
    capacities: base.tenderCapacities,
    online: false,
    preferredMethod: "cash",
  });
  assert.deepEqual(
    Reflect.get(plan.lines[0] ?? {}, "syncProvenance"),
    {
      referenceCode: "RECEIPT-REF",
      priceSource: 3,
    },
  );

  const noReceipt = createNoReceiptDraftLine({
    sourceKind: "no-receipt-product",
    selectionKey: "line-product-provenance",
    returnSourceKey: "noreceipt:BNE:product-provenance",
    productCode: "P-200",
    itemNumber: "2001",
    lookupCode: "9320002000011",
    displayName: "Product 200",
    unitRefundCents: 700,
    syncProvenance: {
      referenceCode: null,
      priceSource: 1,
    },
  } as Parameters<typeof createNoReceiptDraftLine>[0]);
  assert.deepEqual(
    Reflect.get(noReceipt, "syncProvenance"),
    {
      referenceCode: null,
      priceSource: 1,
    },
  );

  assert.throws(
    () =>
      createReceiptDraftLines({
        ...base,
        lines: [
          {
            ...base.lines[0]!,
            syncProvenance: {
              referenceCode: " ",
              priceSource: 0,
            },
          },
        ],
      } as ReceiptReturnContext),
    hasCode("RETURN_SOURCE_MISMATCH"),
  );
});

function receiptContext(
  patch: Partial<ReceiptReturnContext> = {},
): ReceiptReturnContext {
  return {
    originalOrderGuid: "order-a",
    receiptLabel: "HB-1001",
    loadedFrom: "remote",
    returnRecordsMayBeStale: false,
    lines: [
      {
        selectionKey: "line-a",
        originalOrderGuid: "order-a",
        originalOrderDetailGuid: "detail-a",
        returnSourceKey: "return:order-a:detail-a",
        productCode: "P-1",
        itemNumber: "1001",
        lookupCode: "1001",
        displayName: "Product",
        availableQuantity: 3,
        unitRefundCents: 1_000,
        remainingAmountCents: 3_000,
        syncProvenance: {
          referenceCode: "REF-1",
          priceSource: 0,
        },
      },
    ],
    tenderCapacities: [capacity("cash", 3_000, true)],
    ...patch,
  };
}

function capacity(
  method: OriginalReturnTenderCapacity["method"],
  remainingCents: number,
  provenOffline: boolean,
): OriginalReturnTenderCapacity {
  const capacityId = `capacity-${method}`;
  return {
    capacityId,
    originalOrderGuid: "order-a",
    method,
    remainingCents,
    offlineCashProof:
      method === "cash" && provenOffline
        ? {
            evidenceId: "proof-cash",
            capacityId,
            originalOrderGuid: "order-a",
            remainingCents,
          }
        : null,
  };
}

function hasCode(code: string): (error: unknown) => boolean {
  return (error) =>
    error instanceof ReturnFeatureError && error.code === code;
}
