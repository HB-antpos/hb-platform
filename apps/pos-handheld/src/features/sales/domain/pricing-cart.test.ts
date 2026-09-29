import assert from "node:assert/strict";
import test from "node:test";

import { createAud } from "../../../core/contracts";

import {
  PricingCart,
  QUICK_DISCOUNT_BASIS_POINTS,
  type AddCartItemInput,
} from "./index";

const performanceTest = process.env.HBPOS_RUN_PERF_TESTS === "1" ? test : test.skip;

const asOfIso = "2026-06-13T12:00:00.000Z";

function item(
  lineId: string,
  overrides: Partial<AddCartItemInput> = {},
): AddCartItemInput {
  return {
    lineId,
    productCode: `SKU-${lineId}`,
    itemNumber: lineId,
    lookupCode: lineId,
    displayName: `Item ${lineId}`,
    unitPrice: createAud(1_000),
    syncProvenance: { referenceCode: null, priceSource: 0 },
    ...overrides,
  };
}

test("cart merges normalized sale lookups but keeps OPENITEM lines independent", () => {
  const cart = new PricingCart({ asOfIso });

  assert.equal(
    cart.addItem(
      item("line-a", {
        lookupCode: " abc-001 ",
        unitPrice: createAud(1_000),
      }),
    ),
    "line-a",
  );
  assert.equal(
    cart.addItem(
      item("ignored-merge-id", {
        // 合并要求同一商品；这里只验证查询码的空白与大小写规范化。
        productCode: "SKU-line-a",
        lookupCode: "ABC-001",
        unitPrice: createAud(1_200),
      }),
    ),
    "line-a",
  );
  cart.addOpenItem({
    lineId: "open-1",
    productCode: "OPEN-SKU",
    itemNumber: null,
    displayName: "Manual item",
    unitPrice: createAud(789),
    syncProvenance: { referenceCode: "OPENITEM", priceSource: 0 },
  });
  cart.addOpenItem({
    lineId: "open-2",
    productCode: "OPEN-SKU",
    itemNumber: null,
    displayName: "Manual item",
    unitPrice: createAud(789),
    syncProvenance: { referenceCode: "OPENITEM", priceSource: 0 },
  });

  const snapshot = cart.snapshot();
  assert.equal(snapshot.lines.length, 3);
  assert.deepEqual(snapshot.lines[0], {
    lineId: "line-a",
    productCode: "SKU-line-a",
    itemNumber: "line-a",
    lookupCode: " abc-001 ",
    displayName: "Item line-a",
    quantity: "2",
    unitPrice: createAud(1_000),
    discount: createAud(0),
    actualAmount: createAud(2_000),
    discountSource: "none",
    priceSource: "catalog",
    syncProvenance: { referenceCode: null, priceSource: 0 },
    kind: "sale",
    returnSourceKey: null,
    originalOrderGuid: null,
    originalOrderDetailGuid: null,
  });
  assert.deepEqual(
    snapshot.lines.slice(1).map((line) => [
      line.lineId,
      line.priceSource,
      line.actualAmount.cents,
    ]),
    [
      ["open-1", "open-item", 789],
      ["open-2", "open-item", 789],
    ],
  );
});

test("catalog 折扣基线参与定价并在清除人工折扣后恢复", () => {
  const cart = new PricingCart({ asOfIso });
  const catalogItem = {
    ...item("catalog", {
      unitPrice: createAud(699),
      quantity: 2,
    }),
    catalogDiscountBasisPoints: 2_000,
  } as AddCartItemInput;

  cart.addItem(catalogItem);
  let line = cart.snapshot().lines[0]!;
  assert.equal(line.discount.cents, 280);
  assert.equal(line.actualAmount.cents, 1_118);

  assert.equal(cart.setLineDiscountPercentBps("catalog", 1_000), true);
  line = cart.snapshot().lines[0]!;
  assert.equal(line.discount.cents, 140);

  assert.equal(cart.setLineDiscountPercentBps("catalog", 0), true);
  line = cart.snapshot().lines[0]!;
  assert.equal(line.discount.cents, 280);
});

test("非零整单百分比舍入为零仍保留 manual-amount:0 并覆盖目录折扣", () => {
  const cart = new PricingCart({ asOfIso });
  cart.addItem({
    ...item("cent-catalog", { unitPrice: createAud(1) }),
    catalogDiscountBasisPoints: 10_000,
  } as AddCartItemInput);

  assert.equal(cart.snapshot().lines[0]?.discount.cents, 1);
  assert.equal(cart.setOrderDiscountPercentBps(1), true);
  assert.deepEqual(cart.stateSnapshot().lines[0]?.discountState, {
    kind: "manual-amount",
    cents: 0,
  });
  assert.equal(cart.snapshot().lines[0]?.discount.cents, 0);

  assert.equal(cart.setOrderDiscountPercentBps(0), true);
  assert.equal(cart.snapshot().lines[0]?.discount.cents, 1);
});

test("加购 disposition 明确区分新增行与合并行，旧 string API 保持兼容", () => {
  const cart = new PricingCart({ asOfIso });

  assert.deepEqual(
    cart.addItemWithDisposition(
      item("first", { lookupCode: "same-code", productCode: "SAME-SKU" }),
    ),
    { lineId: "first", kind: "added" },
  );
  assert.deepEqual(
    cart.addItemWithDisposition(
      item("ignored", { lookupCode: " SAME-CODE ", productCode: "same-sku" }),
    ),
    { lineId: "first", kind: "incremented" },
  );
  assert.equal(cart.addItem(item("legacy", { lookupCode: "legacy" })), "legacy");

  assert.deepEqual(
    cart.addScannedItemWithDisposition(
      item("scan-first", {
        lookupCode: "scan-code",
        productCode: "SCAN-SKU",
      }),
    ),
    { lineId: "scan-first", kind: "added" },
  );
  assert.deepEqual(
    cart.addScannedItemWithDisposition(
      item("scan-ignored", {
        lookupCode: "SCAN-CODE",
        productCode: "SCAN-SKU",
      }),
    ),
    { lineId: "scan-first", kind: "incremented" },
  );
});

test("一码多商品先后选择不同商品时分行加购，各按自身价格，同商品再选才合并", () => {
  const cart = new PricingCart({ asOfIso });
  const fly = {
    productCode: "P-FLY",
    itemNumber: "EXT-FLY",
    lookupCode: "6405090401470",
    displayName: "EXTENSION Fly Swatter",
    unitPrice: createAud(899),
    syncProvenance: { referenceCode: null, priceSource: 2 as const },
  };
  const flower = {
    productCode: "P-FLOWER",
    itemNumber: "FLW-1",
    lookupCode: "6405090401470",
    displayName: "flower",
    unitPrice: createAud(299),
    syncProvenance: { referenceCode: null, priceSource: 0 as const },
  };

  assert.deepEqual(
    cart.addItemWithDisposition({ ...fly, lineId: "fly" }),
    { lineId: "fly", kind: "added" },
  );
  assert.deepEqual(
    cart.addItemWithDisposition({ ...flower, lineId: "flower" }),
    { lineId: "flower", kind: "added" },
  );
  assert.deepEqual(
    cart.addItemWithDisposition({ ...fly, lineId: "fly-again" }),
    { lineId: "fly", kind: "incremented" },
  );

  const snapshot = cart.snapshot();
  assert.deepEqual(
    snapshot.lines.map((line) => [
      line.productCode,
      line.displayName,
      line.quantity,
      line.unitPrice.cents,
      line.actualAmount.cents,
    ]),
    [
      ["P-FLY", "EXTENSION Fly Swatter", "2", 899, 1_798],
      ["P-FLOWER", "flower", "1", 299, 299],
    ],
  );
  assert.equal(snapshot.actualAmount.cents, 2_097);
});

test("扫码仅合并最后一行的完整同源商品，非连续重复与不兼容折扣保留独立行", () => {
  const cart = new PricingCart({ asOfIso });
  const sameTea = {
    productCode: "P-TEA",
    lookupCode: " 930000000001 ",
    unitPrice: createAud(500),
    syncProvenance: { referenceCode: "REF-TEA", priceSource: 1 as const },
  };

  assert.equal(
    cart.addScannedItem(item("tea-1", sameTea)),
    "tea-1",
  );
  assert.equal(
    cart.addScannedItem(
      item("unused-consecutive-id", {
        ...sameTea,
        lookupCode: "930000000001",
      }),
    ),
    "tea-1",
  );
  assert.deepEqual(
    cart.snapshot().lines.map((line) => [line.lineId, line.quantity]),
    [["tea-1", "2"]],
  );

  cart.addScannedItem(item("coffee", { lookupCode: "COFFEE" }));
  assert.equal(
    cart.addScannedItem(item("tea-2", sameTea)),
    "tea-2",
  );
  assert.equal(cart.setLineDiscountAmount("tea-2", createAud(100)), true);
  assert.equal(
    cart.addScannedItem(item("tea-3", sameTea)),
    "tea-3",
  );

  assert.deepEqual(
    cart.snapshot().lines.map((line) => [line.lineId, line.quantity]),
    [
      ["tea-1", "2"],
      ["coffee", "1"],
      ["tea-2", "1"],
      ["tea-3", "1"],
    ],
  );
});

test("扫码仅在商品、单价、基础价格来源与完整同步来源均相同时连续合并", () => {
  const cases: readonly [
    string,
    Partial<AddCartItemInput>,
  ][] = [
    ["product", { productCode: "P-OTHER" }],
    ["price", { unitPrice: createAud(501) }],
    ["base-price-source", { priceSource: "manual" }],
    [
      "reference",
      {
        syncProvenance: {
          referenceCode: "REF-OTHER",
          priceSource: 1,
        },
      },
    ],
    [
      "backend-price-source",
      {
        syncProvenance: {
          referenceCode: "REF-TEA",
          priceSource: 2,
        },
      },
    ],
  ];

  for (const [name, overrides] of cases) {
    const cart = new PricingCart({ asOfIso });
    const base = item(`${name}-base`, {
      productCode: "P-TEA",
      lookupCode: "TEA",
      unitPrice: createAud(500),
      syncProvenance: {
        referenceCode: "REF-TEA",
        priceSource: 1,
      },
    });
    cart.addScannedItem(base);
    assert.equal(
      cart.addScannedItem(
        item(`${name}-next`, {
          ...base,
          lineId: `${name}-next`,
          ...overrides,
        }),
      ),
      `${name}-next`,
      name,
    );
    assert.equal(cart.snapshot().lines.length, 2, name);
  }
});

test("合并兼容行保留最早位置并汇总固定折扣，来源不同与退货/open item 不参与", () => {
  const cart = new PricingCart({ asOfIso });
  const tea = {
    productCode: "P-TEA",
    lookupCode: "TEA",
    unitPrice: createAud(500),
    syncProvenance: { referenceCode: "REF-TEA", priceSource: 1 as const },
  };
  cart.addScannedItem(item("tea-1", tea));
  cart.addScannedItem(item("coffee", { lookupCode: "COFFEE" }));
  cart.addScannedItem(item("tea-2", tea));
  assert.equal(cart.setLineDiscountAmount("tea-1", createAud(100)), true);
  assert.equal(cart.setLineDiscountAmount("tea-2", createAud(200)), true);
  cart.addScannedItem(
    item("tea-other-source", {
      ...tea,
      syncProvenance: { referenceCode: "REF-TEA", priceSource: 2 },
    }),
  );
  cart.addOpenItem({
    lineId: "open-tea",
    productCode: "P-TEA",
    itemNumber: null,
    lookupCode: "TEA",
    displayName: "Open tea",
    unitPrice: createAud(500),
    syncProvenance: { referenceCode: "REF-TEA", priceSource: 1 },
  });
  cart.addScannedItem(
    item("return-tea", {
      ...tea,
      kind: "return",
      returnSourceKey: "order-1:detail-1",
      originalOrderGuid: "order-1",
      originalOrderDetailGuid: "detail-1",
    }),
  );
  const before = cart.snapshot();

  assert.equal(cart.hasMergeCompatibleLines(), true);
  assert.deepEqual(cart.mergeCompatibleLines(), {
    groups: [
      {
        keptLineId: "tea-1",
        removedLineIds: ["tea-2"],
      },
    ],
    removedLineCount: 1,
  });

  const after = cart.snapshot();
  assert.deepEqual(
    after.lines.map((line) => line.lineId),
    [
      "tea-1",
      "coffee",
      "tea-other-source",
      "open-tea",
      "return-tea",
    ],
  );
  assert.equal(after.lines[0]?.quantity, "2");
  assert.equal(after.lines[0]?.discount.cents, 300);
  assert.equal(after.subtotal.cents, before.subtotal.cents);
  assert.equal(after.discount.cents, before.discount.cents);
  assert.equal(after.actualAmount.cents, before.actualAmount.cents);
  assert.equal(cart.hasMergeCompatibleLines(), false);
});

test("百分比折扣仅在比例相同且分币金额不变时合并", () => {
  const safe = new PricingCart({ asOfIso });
  const safeItem = {
    productCode: "P-SAFE",
    lookupCode: "SAFE",
    unitPrice: createAud(100),
  };
  safe.addScannedItem(item("safe-1", safeItem));
  safe.addScannedItem(item("separator", { lookupCode: "SEPARATOR" }));
  safe.addScannedItem(item("safe-2", safeItem));
  safe.setLineDiscountPercentBps("safe-1", 1_000);
  safe.setLineDiscountPercentBps("safe-2", 1_000);
  assert.equal(safe.hasMergeCompatibleLines(), true);
  assert.equal(safe.mergeCompatibleLines().removedLineCount, 1);
  assert.equal(safe.snapshot().lines[0]?.discount.cents, 20);

  const rounded = new PricingCart({ asOfIso });
  const centItem = {
    productCode: "P-CENT",
    lookupCode: "CENT",
    unitPrice: createAud(1),
  };
  rounded.addScannedItem(item("cent-1", centItem));
  rounded.addScannedItem(item("separator", { lookupCode: "SEPARATOR" }));
  rounded.addScannedItem(item("cent-2", centItem));
  rounded.setLineDiscountPercentBps("cent-1", 5_000);
  rounded.setLineDiscountPercentBps("cent-2", 5_000);
  const revisionBefore = rounded.stateSnapshot().revision;

  assert.equal(rounded.hasMergeCompatibleLines(), false);
  assert.deepEqual(rounded.mergeCompatibleLines(), {
    groups: [],
    removedLineCount: 0,
  });
  assert.equal(rounded.stateSnapshot().revision, revisionBefore);
  assert.equal(rounded.snapshot().lines.length, 3);
});

test("quantity, unit price and remove mutations use integer cents", () => {
  const cart = new PricingCart({ asOfIso });
  cart.addItem(item("line-a"));

  assert.equal(cart.setLineQuantity("line-a", 3), true);
  assert.equal(cart.setLineUnitPrice("line-a", createAud(1_255)), true);
  assert.equal(cart.increaseLine("line-a"), true);
  assert.equal(cart.decreaseLine("line-a"), true);

  let line = cart.snapshot().lines[0]!;
  assert.equal(line.quantity, "3");
  assert.equal(line.unitPrice.cents, 1_255);
  assert.equal(line.actualAmount.cents, 3_765);
  assert.equal(line.priceSource, "manual");

  assert.equal(cart.setLineQuantity("line-a", 0), false);
  assert.equal(cart.setLineQuantity("line-a", 1.5), false);
  assert.equal(cart.removeLine("line-a"), true);
  assert.equal(cart.snapshot().lines.length, 0);
});

test("fixed and percent line discounts match WPF recomputation behavior", () => {
  const cart = new PricingCart({ asOfIso });
  cart.addItem(item("line-a"));
  cart.setLineQuantity("line-a", 2);

  assert.equal(cart.setLineDiscountAmount("line-a", createAud(300)), true);
  assert.equal(cart.snapshot().lines[0]!.actualAmount.cents, 1_700);
  assert.equal(
    cart.setLineDiscountAmount("line-a", createAud(2_001)),
    false,
  );

  assert.equal(cart.setLineDiscountPercentBps("line-a", 850), true);
  assert.equal(cart.snapshot().lines[0]!.discount.cents, 170);
  assert.equal(cart.setLineQuantity("line-a", 3), true);
  assert.equal(cart.snapshot().lines[0]!.discount.cents, 255);
  assert.equal(cart.snapshot().lines[0]!.actualAmount.cents, 2_745);

  assert.deepEqual(QUICK_DISCOUNT_BASIS_POINTS, [
    1_000,
    2_000,
    3_000,
    4_000,
    5_000,
  ]);
  assert.equal(cart.applyQuickLineDiscount("line-a", 2_000), true);
  assert.equal(cart.snapshot().lines[0]!.discount.cents, 600);
});

test("order discounts allocate rounded remainders deterministically", () => {
  const proportional = new PricingCart({ asOfIso });
  proportional.addItem(
    item("low", { productCode: "SKU-SAME", unitPrice: createAud(400) }),
  );
  proportional.addItem(
    item("high", { productCode: "SKU-SAME", unitPrice: createAud(1_000) }),
  );

  assert.equal(
    proportional.setOrderDiscountAmount(createAud(400)),
    true,
  );
  assert.deepEqual(
    proportional.snapshot().lines.map((line) => line.discount.cents),
    [114, 286],
  );

  const cents = new PricingCart({ asOfIso });
  cents.addItem(item("cent-a", { unitPrice: createAud(1) }));
  cents.addItem(item("cent-b", { unitPrice: createAud(1) }));
  cents.addItem(item("cent-c", { unitPrice: createAud(1) }));

  assert.equal(cents.setOrderDiscountPercentBps(5_000), true);
  assert.deepEqual(
    cents.snapshot().lines.map((line) => line.discount.cents),
    [1, 1, 0],
  );
  assert.equal(cents.snapshot().discount.cents, 2);
  assert.equal(cents.snapshot().actualAmount.cents, 1);
});

test("feature-private state restores percent behavior while frozen snapshots stay stable", () => {
  const first = new PricingCart({ asOfIso });
  first.addItem(item("line-a", { unitPrice: createAud(1_000) }));
  first.setLineQuantity("line-a", 2);
  first.setLineDiscountPercentBps("line-a", 850);

  const state = first.stateSnapshot();
  const restored = PricingCart.restore(state);

  assert.deepEqual(restored.snapshot(), first.snapshot());
  assert.deepEqual(restored.stateSnapshot(), state);
  assert.equal(restored.setLineQuantity("line-a", 3), true);
  assert.equal(restored.snapshot().lines[0]!.discount.cents, 255);
  assert.equal(
    restored.stateSnapshot().lines[0]!.discountState.kind,
    "manual-percent",
  );
  assert.deepEqual(restored.snapshot(), restored.snapshot());
});

test("restore 精确接受 frozen SharedSaleCart 的正有限小数数量，普通加购仍拒绝小数", () => {
  const source = new PricingCart({ asOfIso });
  source.addItem(item("weighted", { unitPrice: createAud(1_000) }));
  const state = source.stateSnapshot();

  const restored = PricingCart.restore({
    ...state,
    lines: [{ ...state.lines[0]!, quantity: 1.25 }],
  });

  assert.equal(restored.stateSnapshot().lines[0]!.quantity, 1.25);
  assert.equal(restored.snapshot().lines[0]!.quantity, "1.25");
  assert.equal(restored.snapshot().lines[0]!.actualAmount.cents, 1_250);
  assert.equal(
    PricingCart.restore(restored.stateSnapshot()).stateSnapshot().lines[0]!
      .quantity,
    1.25,
  );

  const regular = new PricingCart({ asOfIso });
  assert.throws(
    () => regular.addItem(item("weighted-new", { quantity: 1.25 })),
    /positive integer/,
  );
});

test("restore 的小数数量兼容行不参与整数合并且销售页能力探测不抛错", () => {
  const source = new PricingCart({ asOfIso });
  const weighted = {
    productCode: "P-WEIGHTED",
    lookupCode: "WEIGHTED",
    unitPrice: createAud(100),
  };
  source.addScannedItem(item("weighted-1", weighted));
  source.addScannedItem(item("separator", { lookupCode: "SEPARATOR" }));
  source.addScannedItem(item("weighted-2", weighted));
  const state = source.stateSnapshot();
  const restored = PricingCart.restore({
    ...state,
    lines: state.lines.map((line) =>
      line.lineId === "weighted-1"
        ? { ...line, quantity: 0.25 }
        : line.lineId === "weighted-2"
          ? { ...line, quantity: 0.75 }
          : line,
    ),
  });
  const before = restored.stateSnapshot();

  assert.equal(restored.hasMergeCompatibleLines(), false);
  assert.deepEqual(restored.mergeCompatibleLines(), {
    groups: [],
    removedLineCount: 0,
  });
  assert.deepEqual(restored.stateSnapshot(), before);
});

test("restore 按 C# decimal AwayFromZero 计算 0.29 × 50 为 15 分", () => {
  const source = new PricingCart({ asOfIso });
  source.addItem(item("weighted", { unitPrice: createAud(50) }));
  const state = source.stateSnapshot();

  const restored = PricingCart.restore({
    ...state,
    lines: [{ ...state.lines[0]!, quantity: 0.29 }],
  });

  assert.equal(restored.snapshot().lines[0]!.actualAmount.cents, 15);
  assert.equal(restored.snapshot().actualAmount.cents, 15);
  assert.equal(restored.snapshot().subtotal.cents, 15);
});

test("restore 仍拒绝零、负数与非有限数量", () => {
  const source = new PricingCart({ asOfIso });
  source.addItem(item("line-a"));
  const state = source.stateSnapshot();

  for (const quantity of [0, -1, Number.NaN, Infinity, -Infinity]) {
    assert.throws(
      () =>
        PricingCart.restore({
          ...state,
          lines: [{ ...state.lines[0]!, quantity }],
        }),
      /positive finite/,
      `quantity ${String(quantity)} 必须被拒绝`,
    );
  }
});

test("zero-price lines remain representable without floating point coercion", () => {
  const cart = new PricingCart({ asOfIso });
  cart.addOpenItem({
    lineId: "open-zero",
    productCode: "OPENITEM",
    itemNumber: null,
    displayName: "Open item",
    unitPrice: createAud(0),
    syncProvenance: { referenceCode: "OPENITEM", priceSource: 0 },
  });

  assert.equal(cart.snapshot().subtotal.cents, 0);
  assert.equal(cart.snapshot().actualAmount.cents, 0);
  assert.equal(cart.snapshot().lines[0]!.priceSource, "open-item");
});

test("在线目录校准更新全部同身份销售行，保留手工价并跳过退货与 open item", () => {
  const source = new PricingCart({ asOfIso });
  source.addItem(
    item("catalog", {
      productCode: "P-TEA",
      lookupCode: "930000000001",
      unitPrice: createAud(500),
      syncProvenance: { referenceCode: "REF-TEA", priceSource: 0 },
    }),
  );
  source.addItem(
    item("manual", {
      productCode: "P-OTHER",
      lookupCode: "OTHER",
      unitPrice: createAud(650),
      syncProvenance: { referenceCode: "REF-OTHER", priceSource: 0 },
    }),
  );
  source.setLineUnitPrice("manual", createAud(675));
  source.addOpenItem({
    lineId: "open",
    productCode: "P-TEA",
    itemNumber: null,
    lookupCode: "930000000001",
    displayName: "Open tea",
    unitPrice: createAud(999),
    syncProvenance: { referenceCode: "REF-TEA", priceSource: 0 },
  });
  const state = source.stateSnapshot();
  const catalogLine = state.lines[0]!;
  const manualLine = state.lines[1]!;
  const cart = PricingCart.restore({
    ...state,
    lines: [
      catalogLine,
      {
        ...manualLine,
        productCode: "P-TEA",
        lookupCode: "930000000001",
        syncProvenance: { referenceCode: "REF-TEA", priceSource: 0 },
      },
      {
        ...catalogLine,
        lineId: "return",
        kind: "return",
        originalOrderGuid: "order-1",
        originalOrderDetailGuid: "detail-1",
        returnSourceKey: "order-1:detail-1",
      },
      state.lines[2]!,
    ],
  });

  const updatedLineIds = cart.refreshCatalogItem({
    expected: {
      productCode: "P-TEA",
      referenceCode: "REF-TEA",
      lookupCode: "930000000001",
    },
    item: {
      productCode: "P-TEA",
      referenceCode: "REF-TEA",
      itemNumber: "NEW-100",
      lookupCode: "930000000001",
      displayName: "Fresh tea",
      retailPriceCents: 725,
      priceSource: 1,
    },
  });

  const snapshot = cart.snapshot();
  assert.deepEqual(updatedLineIds, ["catalog", "manual"]);
  assert.deepEqual(
    snapshot.lines.slice(0, 2).map((line) => ({
      lineId: line.lineId,
      displayName: line.displayName,
      itemNumber: line.itemNumber,
      unitPriceCents: line.unitPrice.cents,
      priceSource: line.priceSource,
      syncProvenance: line.syncProvenance,
    })),
    [
      {
        lineId: "catalog",
        displayName: "Fresh tea",
        itemNumber: "NEW-100",
        unitPriceCents: 725,
        priceSource: "catalog",
        syncProvenance: { referenceCode: "REF-TEA", priceSource: 1 },
      },
      {
        lineId: "manual",
        displayName: "Fresh tea",
        itemNumber: "NEW-100",
        unitPriceCents: 675,
        priceSource: "manual",
        syncProvenance: { referenceCode: "REF-TEA", priceSource: 1 },
      },
    ],
  );
  assert.equal(snapshot.lines[2]?.displayName, "Item catalog");
  assert.equal(snapshot.lines[3]?.displayName, "Open tea");
});

test("在线目录校准遇到完全相同数据时不递增 revision，手工价不因目录价差异触发更新", () => {
  const cart = new PricingCart({ asOfIso });
  cart.addItem(
    item("catalog", {
      productCode: "P-TEA",
      itemNumber: "TEA-1",
      lookupCode: " tea-1 ",
      displayName: "Fresh tea",
      unitPrice: createAud(725),
      syncProvenance: { referenceCode: "REF-TEA", priceSource: 1 },
    }),
  );
  cart.addItem(
    item("manual", {
      productCode: "P-OTHER",
      lookupCode: "OTHER",
      unitPrice: createAud(650),
      syncProvenance: { referenceCode: "REF-OTHER", priceSource: 0 },
    }),
  );
  cart.setLineUnitPrice("manual", createAud(675));
  const state = cart.stateSnapshot();
  const manualLine = state.lines[1]!;
  const restored = PricingCart.restore({
    ...state,
    lines: [
      state.lines[0]!,
      {
        ...manualLine,
        productCode: "P-TEA",
        itemNumber: "TEA-1",
        lookupCode: "TEA-1",
        displayName: "Fresh tea",
        syncProvenance: { referenceCode: "REF-TEA", priceSource: 1 },
      },
    ],
  });
  const revisionBefore = restored.stateSnapshot().revision;

  const updatedLineIds = restored.refreshCatalogItem({
    expected: {
      productCode: "P-TEA",
      referenceCode: "REF-TEA",
      lookupCode: "TEA-1",
    },
    item: {
      productCode: "p-tea",
      referenceCode: "REF-TEA",
      itemNumber: "TEA-1",
      lookupCode: " tea-1 ",
      displayName: "Fresh tea",
      retailPriceCents: 725,
      priceSource: 1,
    },
  });

  assert.deepEqual(updatedLineIds, []);
  assert.equal(restored.stateSnapshot().revision, revisionBefore);
  assert.equal(restored.snapshot().lines[1]?.unitPrice.cents, 675);
});

performanceTest("300 行不可合并百分比折扣的按钮预测保持近线性", () => {
  const cart = new PricingCart({ asOfIso });
  for (let pass = 0; pass < 2; pass += 1) {
    for (let product = 0; product < 150; product += 1) {
      const lineId = `line-${pass}-${product}`;
      cart.addScannedItem(
        item(lineId, {
          productCode: `P-${product}`,
          lookupCode: `BC-${product}`,
          unitPrice: createAud(995),
          syncProvenance: {
            referenceCode: `REF-${product}`,
            priceSource: 0,
          },
        }),
      );
      assert.equal(
        cart.setLineDiscountPercentBps(lineId, 1_000),
        true,
      );
    }
  }

  // 预热 V8 热路径，避免把首次 JIT 编译时间误算为算法复杂度回归。
  for (let warmup = 0; warmup < 5; warmup += 1) {
    assert.equal(cart.hasMergeCompatibleLines(), false);
  }

  const startedCpu = process.cpuUsage();
  for (let iteration = 0; iteration < 20; iteration += 1) {
    assert.equal(cart.hasMergeCompatibleLines(), false);
  }
  const elapsedCpu = process.cpuUsage(startedCpu);
  const elapsedMs = (elapsedCpu.user + elapsedCpu.system) / 1_000;

  assert.ok(
    elapsedMs < 100,
    `300 行合并预测执行 20 次 CPU 耗时 ${elapsedMs.toFixed(1)}ms`,
  );
});
