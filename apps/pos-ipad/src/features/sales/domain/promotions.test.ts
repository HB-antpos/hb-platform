import assert from "node:assert/strict";
import test from "node:test";

import { createAud } from "../../../core/contracts";

import {
  PricingCart,
  type AddCartItemInput,
  type PromotionDefinition,
} from "./index";

const asOfIso = "2026-06-13T12:00:00.000Z";

function item(
  lineId: string,
  overrides: Partial<AddCartItemInput> = {},
): AddCartItemInput {
  return {
    lineId,
    productCode: "SKU-001",
    itemNumber: lineId,
    lookupCode: lineId,
    displayName: lineId,
    unitPrice: createAud(1_000),
    syncProvenance: { referenceCode: null, priceSource: 0 },
    ...overrides,
  };
}

function promotion(
  id: string,
  overrides: Partial<PromotionDefinition> = {},
): PromotionDefinition {
  return {
    id,
    name: id,
    effectiveStartIso: "2026-06-12T00:00:00.000Z",
    effectiveEndIso: "2026-06-14T00:00:00.000Z",
    isExclusive: false,
    priority: 10,
    applyQuantity: 2,
    fixedPrice: createAud(1_500),
    maxApplicationsPerOrder: null,
    products: [{ productCode: "SKU-001", unitWeight: 1 }],
    ...overrides,
  };
}

test("fixed-price promotion applies per quantity and disappears after decrement", () => {
  const cart = new PricingCart({
    asOfIso,
    promotions: [promotion("PROMO-2-FOR-15")],
  });
  cart.addItem(item("line-a"));
  cart.addItem(item("merged", { lookupCode: "line-a" }));

  let line = cart.snapshot().lines[0]!;
  assert.equal(line.quantity, "2");
  assert.equal(line.discount.cents, 500);
  assert.equal(line.actualAmount.cents, 1_500);
  assert.equal(line.priceSource, "promotion");

  assert.equal(cart.decreaseLine("line-a"), true);
  line = cart.snapshot().lines[0]!;
  assert.equal(line.discount.cents, 0);
  assert.equal(line.actualAmount.cents, 1_000);
  assert.equal(line.priceSource, "catalog");
});

test("manual discount excludes a line until it is reset", () => {
  const cart = new PricingCart({
    asOfIso,
    promotions: [promotion("PROMO-MANUAL")],
  });
  cart.addItem(item("line-a"));
  cart.setLineQuantity("line-a", 2);

  assert.equal(cart.setLineDiscountAmount("line-a", createAud(200)), true);
  assert.equal(cart.snapshot().lines[0]!.discount.cents, 200);

  assert.equal(cart.setLineDiscountAmount("line-a", createAud(0)), true);
  assert.equal(cart.snapshot().lines[0]!.discount.cents, 500);
  assert.equal(
    cart.stateSnapshot().lines[0]!.discountState.kind,
    "promotion",
  );
});

test("兼容行合并会重新计算系统促销且保持交易金额不变", () => {
  const cart = new PricingCart({
    asOfIso,
    promotions: [promotion("PROMO-MERGE")],
  });
  const sameProduct = {
    productCode: "SKU-001",
    lookupCode: "SKU-001",
  };
  cart.addScannedItem(item("line-a", sameProduct));
  cart.addScannedItem(
    item("separator", {
      productCode: "SKU-OTHER",
      lookupCode: "SKU-OTHER",
    }),
  );
  cart.addScannedItem(item("line-b", sameProduct));
  const before = cart.snapshot();

  assert.equal(before.discount.cents, 500);
  assert.deepEqual(cart.mergeCompatibleLines(), {
    groups: [
      {
        keptLineId: "line-a",
        removedLineIds: ["line-b"],
      },
    ],
    removedLineCount: 1,
  });

  const after = cart.snapshot();
  assert.equal(after.lines[0]?.priceSource, "promotion");
  assert.equal(after.lines[0]?.quantity, "2");
  assert.equal(after.subtotal.cents, before.subtotal.cents);
  assert.equal(after.discount.cents, before.discount.cents);
  assert.equal(after.actualAmount.cents, before.actualAmount.cents);
});

test("非连续同商品若会改变受上限促销的分组则保持分离", () => {
  const cart = new PricingCart({
    asOfIso,
    promotions: [
      promotion("PROMO-CAPPED", {
        maxApplicationsPerOrder: 1,
        products: [
          { productCode: "SKU-A", unitWeight: 1 },
          { productCode: "SKU-B", unitWeight: 1 },
        ],
      }),
    ],
  });
  const source = {
    syncProvenance: { referenceCode: null, priceSource: 0 as const },
  };
  cart.addScannedItem(
    item("line-a-1", {
      ...source,
      productCode: "SKU-A",
      lookupCode: "SKU-A",
      unitPrice: createAud(1_000),
    }),
  );
  cart.addScannedItem(
    item("line-b", {
      ...source,
      productCode: "SKU-B",
      lookupCode: "SKU-B",
      unitPrice: createAud(2_000),
    }),
  );
  cart.addScannedItem(
    item("line-a-2", {
      ...source,
      productCode: "SKU-A",
      lookupCode: "SKU-A",
      unitPrice: createAud(1_000),
    }),
  );
  const before = cart.snapshot();

  assert.equal(cart.hasMergeCompatibleLines(), false);
  assert.deepEqual(cart.mergeCompatibleLines(), {
    groups: [],
    removedLineCount: 0,
  });
  assert.deepEqual(cart.snapshot(), before);
});

test("exclusive priority and ID tie-break are deterministic and respect effective time", () => {
  const rules = [
    promotion("PROMO-NORMAL", {
      priority: 100,
      fixedPrice: createAud(100),
    }),
    promotion("PROMO-EXCLUSIVE-B", {
      isExclusive: true,
      priority: 20,
      fixedPrice: createAud(1_500),
    }),
    promotion("PROMO-EXCLUSIVE-A", {
      isExclusive: true,
      priority: 20,
      fixedPrice: createAud(1_900),
    }),
    promotion("PROMO-EXPIRED", {
      isExclusive: true,
      priority: 999,
      effectiveEndIso: "2026-06-12T00:00:00.000Z",
      fixedPrice: createAud(0),
    }),
  ];
  const first = new PricingCart({ asOfIso, promotions: rules });
  const second = new PricingCart({
    asOfIso,
    promotions: [...rules].reverse(),
  });
  for (const cart of [first, second]) {
    cart.addItem(item("line-a"));
    cart.setLineQuantity("line-a", 2);
  }

  assert.equal(first.snapshot().discount.cents, 100);
  assert.deepEqual(first.snapshot(), second.snapshot());
  assert.deepEqual(
    first.stateSnapshot().lines[0]!.discountState,
    {
      kind: "promotion",
      cents: 100,
      promotionIds: ["PROMO-EXCLUSIVE-A"],
    },
  );
});

test("nonexclusive rules evaluate independently and cap accumulated discount", () => {
  const cart = new PricingCart({
    asOfIso,
    promotions: [
      promotion("PROMO-A", {
        priority: 20,
        fixedPrice: createAud(1_500),
      }),
      promotion("PROMO-B", {
        priority: 10,
        fixedPrice: createAud(1_600),
      }),
    ],
  });
  cart.addItem(item("line-a"));
  cart.setLineQuantity("line-a", 2);

  assert.equal(cart.snapshot().discount.cents, 900);
  assert.equal(cart.snapshot().actualAmount.cents, 1_100);
  assert.deepEqual(
    cart.stateSnapshot().lines[0]!.discountState,
    {
      kind: "promotion",
      cents: 900,
      promotionIds: ["PROMO-A", "PROMO-B"],
    },
  );
});

test("weighted thresholds count expanded units once and split cents in cart order", () => {
  const cart = new PricingCart({
    asOfIso,
    promotions: [
      promotion("PROMO-WEIGHTED", {
        applyQuantity: 3,
        fixedPrice: createAud(1_500),
        products: [
          { productCode: "SKU-A", unitWeight: 2 },
          { productCode: "SKU-B", unitWeight: 1 },
        ],
      }),
    ],
  });
  cart.addItem(
    item("line-a", {
      productCode: "SKU-A",
      lookupCode: "SKU-A",
      unitPrice: createAud(1_200),
    }),
  );
  cart.addItem(
    item("line-b", {
      productCode: "SKU-B",
      lookupCode: "SKU-B",
      unitPrice: createAud(600),
    }),
  );

  assert.deepEqual(
    cart.snapshot().lines.map((line) => line.discount.cents),
    [200, 100],
  );
  assert.equal(cart.snapshot().discount.cents, 300);
});

test("promotion remainder allocation matches WPF cart-order fixture", () => {
  const cart = new PricingCart({
    asOfIso,
    promotions: [
      promotion("PROMO-CART-ORDER", {
        fixedPrice: createAud(1_000),
      }),
    ],
  });
  cart.addItem(
    item("low", {
      lookupCode: "low",
      unitPrice: createAud(400),
    }),
  );
  cart.addItem(
    item("high", {
      lookupCode: "high",
      unitPrice: createAud(1_000),
    }),
  );
  cart.setLineQuantity("high", 2);

  assert.deepEqual(
    cart.snapshot().lines.map((line) => line.discount.cents),
    [114, 286],
  );
  assert.equal(cart.snapshot().discount.cents, 400);
});

test("OPENITEM never participates in automatic promotions", () => {
  const cart = new PricingCart({
    asOfIso,
    promotions: [promotion("PROMO-OPEN")],
  });
  cart.addItem(item("sale"));
  cart.addOpenItem({
    lineId: "open",
    productCode: "SKU-001",
    itemNumber: null,
    displayName: "Open",
    unitPrice: createAud(1_000),
    syncProvenance: { referenceCode: "OPENITEM", priceSource: 0 },
  });

  assert.equal(cart.snapshot().discount.cents, 0);
});

test("带时钟的购物车：购物车建立时还没开始的促销，下一次改动时按当前时间开始生效", () => {
  let now = "2026-06-13T08:00:00.000Z";
  const cart = new PricingCart({
    asOfIso: now,
    nowIso: () => now,
    promotions: [
      promotion("PROMO-LATE", { effectiveStartIso: "2026-06-13T09:00:00.000Z" }),
    ],
  });
  cart.addItem(item("line-a"));
  cart.setLineQuantity("line-a", 2);
  // 还没到 09:00：促销不生效。
  assert.equal(cart.snapshot().lines[0]!.discount.cents, 0);

  now = "2026-06-13T09:30:00.000Z";
  assert.equal(cart.increaseLine("line-a"), true);
  // 3 件里凑满一组（2 件）：1000 * 2 - 1500 = 500。
  assert.equal(cart.snapshot().lines[0]!.discount.cents, 500);
  assert.equal(cart.snapshot().lines[0]!.priceSource, "promotion");
  // 定价时点记录的是实际用于这次重算的时间。
  assert.equal(cart.stateSnapshot().asOfIso, now);
});

test("带时钟的购物车：已结束的促销在下一次改动时失效", () => {
  let now = "2026-06-13T08:30:00.000Z";
  const cart = new PricingCart({
    asOfIso: now,
    nowIso: () => now,
    promotions: [
      promotion("PROMO-ENDING", { effectiveEndIso: "2026-06-13T09:00:00.000Z" }),
    ],
  });
  cart.addItem(item("line-a"));
  cart.setLineQuantity("line-a", 2);
  assert.equal(cart.snapshot().lines[0]!.discount.cents, 500);

  now = "2026-06-13T09:30:00.000Z";
  assert.equal(cart.increaseLine("line-a"), true);
  // 09:00 已过：折扣消失，价格回到目录价。
  assert.equal(cart.snapshot().lines[0]!.discount.cents, 0);
  assert.equal(cart.snapshot().lines[0]!.priceSource, "catalog");
});

test("不带时钟时定价时点保持固定（账务回放与恢复路径的确定性）", () => {
  const cart = new PricingCart({
    asOfIso,
    promotions: [promotion("PROMO-FIXED")],
  });
  cart.addItem(item("line-a"));
  cart.setLineQuantity("line-a", 2);

  assert.equal(cart.stateSnapshot().asOfIso, asOfIso);
  assert.equal(cart.snapshot().lines[0]!.discount.cents, 500);
});

test("restore 可携带时钟；不携带则沿用存下来的定价时点", () => {
  const stale = new PricingCart({
    asOfIso: "2026-06-13T08:00:00.000Z",
    promotions: [
      promotion("PROMO-LATE", { effectiveStartIso: "2026-06-13T09:00:00.000Z" }),
    ],
  });
  stale.addItem(item("line-a"));
  stale.setLineQuantity("line-a", 2);
  const state = stale.stateSnapshot();

  const now = "2026-06-13T09:30:00.000Z";
  const withClock = PricingCart.restore(state, { nowIso: () => now });
  withClock.increaseLine("line-a");
  assert.equal(withClock.snapshot().lines[0]!.discount.cents, 500);
  assert.equal(withClock.stateSnapshot().asOfIso, now);

  // 没有时钟：仍按 08:00 判断，促销没开始，保持确定性。
  const withoutClock = PricingCart.restore(state);
  withoutClock.increaseLine("line-a");
  assert.equal(withoutClock.snapshot().lines[0]!.discount.cents, 0);
  assert.equal(withoutClock.stateSnapshot().asOfIso, "2026-06-13T08:00:00.000Z");
});

test("带时钟：时钟越过生效边界后，合并兼容行会拒绝（不悄悄改金额），下一次普通改动才重新定价", () => {
  let now = "2026-06-13T08:00:00.000Z";
  const cart = new PricingCart({
    asOfIso: now,
    nowIso: () => now,
    promotions: [
      promotion("PROMO-LATE", { effectiveStartIso: "2026-06-13T09:00:00.000Z" }),
    ],
  });
  const sameProduct = { productCode: "SKU-001", lookupCode: "SKU-001" };
  cart.addScannedItem(item("line-a", sameProduct));
  cart.addScannedItem(
    item("separator", { productCode: "SKU-OTHER", lookupCode: "SKU-OTHER" }),
  );
  cart.addScannedItem(item("line-b", sameProduct));
  assert.equal(cart.snapshot().discount.cents, 0);

  now = "2026-06-13T09:30:00.000Z";
  // 合并要求「合并前后交易金额不变」。时钟越过边界后，候选副本按新时间算出折扣，
  // 与当前购物车（仍是旧时间的金额）不一致，所以保守地拒绝合并，不让合并顺带改价。
  const merged = cart.mergeCompatibleLines();
  assert.equal(merged.removedLineCount, 0);
  assert.equal(cart.snapshot().lines.length, 3);
  assert.equal(cart.snapshot().discount.cents, 0);
  assert.equal(cart.stateSnapshot().asOfIso, "2026-06-13T08:00:00.000Z");

  // 下一次普通改动会按当前时间整体重新定价：SKU-001 共 3 件，凑满一组优惠 500。
  assert.equal(cart.increaseLine("line-b"), true);
  assert.equal(cart.snapshot().discount.cents, 500);
  assert.equal(cart.stateSnapshot().asOfIso, now);
});
