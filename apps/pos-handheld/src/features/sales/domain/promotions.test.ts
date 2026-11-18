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

test("自动促销计算预算：超大数量整单不算自动促销且不卡死，预算边界与恢复（与 WPF 同口径）", () => {
  const cart = new PricingCart({
    asOfIso,
    promotions: [promotion("PROMO-BUDGET")],
  });
  cart.addItem(item("line-a"));

  // 边界：每条规则最多 100,000 个实际消费的展开单位，刚好等于上限仍照常计算。
  assert.equal(cart.setLineQuantity("line-a", 100_000), true);
  assert.equal(cart.snapshot().lines[0]!.discount.cents, 50_000 * 500);

  // 超出上限：整单不算自动促销，购物车照常可改、金额按原价。
  assert.equal(cart.setLineQuantity("line-a", 100_002), true);
  const overBudget = cart.snapshot().lines[0]!;
  assert.equal(overBudget.discount.cents, 0);
  assert.equal(overBudget.priceSource, "catalog");
  assert.equal(overBudget.actualAmount.cents, 100_002 * 1_000);

  // 误输入的超大数量：不按件数逐件展开，立即返回。
  const startedAt = Date.now();
  assert.equal(cart.setLineQuantity("line-a", 99_999_999), true);
  assert.ok(Date.now() - startedAt < 2_000);
  assert.equal(cart.snapshot().lines[0]!.discount.cents, 0);

  // 数量回到预算内后促销恢复。
  assert.equal(cart.setLineQuantity("line-a", 2), true);
  assert.equal(cart.snapshot().lines[0]!.discount.cents, 500);
});

test("自动促销：每单最大次数很小而数量极大时只展开会被消费的单位，结果不变且不卡死", () => {
  const cart = new PricingCart({
    asOfIso,
    promotions: [promotion("PROMO-CAPPED", { maxApplicationsPerOrder: 3 })],
  });
  cart.addItem(item("line-a"));

  const startedAt = Date.now();
  assert.equal(cart.setLineQuantity("line-a", 99_999_999), true);
  assert.ok(Date.now() - startedAt < 2_000);
  // 只应用 3 次，每次 2 件 15.00 的固定价，折扣 3 × 5.00。
  assert.equal(cart.snapshot().lines[0]!.discount.cents, 3 * 500);
});

test("自动促销预算：多条规则合计超过整单预算时整单不算自动促销", () => {
  const rules = Array.from({ length: 6 }, (_, index) =>
    promotion(`PROMO-ORDER-${index}`, { priority: 10 + index }),
  );
  const cart = new PricingCart({ asOfIso, promotions: rules });
  cart.addItem(item("line-a"));

  // 每条规则 90,000 个单位都在单规则上限内，但 6 条合计 540,000 超过整单 500,000。
  assert.equal(cart.setLineQuantity("line-a", 90_000), true);
  assert.equal(cart.snapshot().lines[0]!.discount.cents, 0);
});
