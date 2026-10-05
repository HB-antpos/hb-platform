import assert from "node:assert/strict";
import test from "node:test";

import {
  ActivePricingCartPaymentLeaseCoordinator,
  type PaymentCartDurableRecoveryFallback,
  type PaymentCartRecoveryMaterial,
} from "./payment-cart-lease-coordinator";

import { PricingCart } from "@/features/sales/domain";
import { ActivePricingCartSession } from "@/features/sales/runtime";

test("小数数量拒绝取得支付独占锁后，原购物车仍可修改", async () => {
  const source = cartWithDiscount().stateSnapshot();
  const active = session(PricingCart.restore({
    ...source,
    lines: [{ ...source.lines[0]!, quantity: 1.25 }],
  }));
  const coordinator = createCoordinator(active, null);

  await assert.rejects(
    () => coordinator.acquireExact({
      checkoutIntentId: "checkout-fractional",
      expectedRevision: active.read().cart.revision,
    }),
    hasCode("PAYMENT_QUANTITY_UNSUPPORTED"),
  );
  assert.equal(active.hasPendingExclusiveOperation(), false);
  assert.equal(active.setLineQuantity("line-1", 2), true);
  assert.equal(active.read().cart.lines[0]?.quantity, "2");

  const lease = await coordinator.acquireExact({
    checkoutIntentId: "checkout-integer",
    expectedRevision: active.read().cart.revision,
  });
  await coordinator.releaseAfterSafeCancel(lease, "order-integer");
});

test("支付 lease 跨异步生命周期独占购物车，安全取消后保留原定价车", async () => {
  const cart = cartWithDiscount();
  const active = session(cart);
  const leaseCoordinator = createCoordinator(active, null);
  const expected = active.read();

  const first = await leaseCoordinator.acquireExact({
    checkoutIntentId: "checkout-1",
    expectedRevision: expected.cart.revision,
  });
  const replay = await leaseCoordinator.acquireExact({
    checkoutIntentId: "checkout-1",
    expectedRevision: expected.cart.revision,
  });
  assert.equal(replay, first);
  await assert.rejects(
    () =>
      leaseCoordinator.acquireExact({
        checkoutIntentId: "checkout-2",
        expectedRevision: expected.cart.revision,
      }),
    hasCode("PAYMENT_CART_LEASE_CONFLICT"),
  );
  assert.throws(
    () =>
      active.addItem({
        lineId: "late-line",
        productCode: "P2",
        itemNumber: null,
        lookupCode: "2",
        displayName: "Late",
        unitPrice: { currency: "AUD", cents: 100 },
        syncProvenance: { referenceCode: null, priceSource: 0 },
      }),
    hasCode("ACTIVE_PRICING_CART_BUSY"),
  );

  await leaseCoordinator.releaseAfterSafeCancel(first, "order-1");
  assert.deepEqual(active.read().pricingState, expected.pricingState);
  assert.deepEqual(active.read().cart, expected.cart);
  active.increaseLine("line-1");
  assert.equal(active.read().cart.lines[0]?.quantity, "2");
  await assert.rejects(
    () => leaseCoordinator.readExact(first),
    hasCode("PAYMENT_CART_LEASE_CONFLICT"),
  );
});

test("订单确认后才清空购物车并释放支付 lease", async () => {
  const active = session(cartWithDiscount());
  const leaseCoordinator = createCoordinator(active, null);
  const lease = await leaseCoordinator.acquireExact({
    checkoutIntentId: "checkout-complete",
    expectedRevision: active.read().cart.revision,
  });

  await leaseCoordinator.clearAfterCompleted(lease, "order-complete");
  assert.equal(active.read().cart.lines.length, 0);
  const afterCompletion = active.read();
  assert.equal(
    active.clearAfterCommittedOrder("order-complete"),
    afterCompletion,
    "同一 OrderGuid 的重复完成只命中 session tombstone",
  );
  active.addItem({
    lineId: "next-line",
    productCode: "P-NEXT",
    itemNumber: null,
    lookupCode: "NEXT",
    displayName: "Next",
    unitPrice: { currency: "AUD", cents: 300 },
    syncProvenance: { referenceCode: null, priceSource: 0 },
  });
  assert.equal(active.read().cart.lines.length, 1);
});

test("异常支付耐久移交后清空当前车，并可按原材料精确恢复", async () => {
  const original = cartWithDiscount();
  const active = session(original);
  const coordinator = createCoordinator(active, null);
  await coordinator.acquireExact({
    checkoutIntentId: "checkout-parked",
    expectedRevision: active.read().cart.revision,
  });

  await coordinator.clearAfterRecoveryParked("checkout-parked", "order-parked");
  assert.equal(active.read().cart.lines.length, 0);
  active.addItem({
    lineId: "next-sale",
    productCode: "NEXT",
    itemNumber: null,
    lookupCode: "NEXT",
    displayName: "Next sale",
    unitPrice: { currency: "AUD", cents: 100 },
    syncProvenance: { referenceCode: null, priceSource: 0 },
  });
  await assert.rejects(
    () => coordinator.prepareParkedRecovery({
      checkoutIntentId: "checkout-parked",
      cart: original.snapshot(),
      pricingState: original.stateSnapshot(),
      recallBinding: null,
    }),
    hasCode("ACTIVE_PRICING_CART_BUSY"),
  );
  active.clearManually();
  const restored = await coordinator.prepareParkedRecovery({
    checkoutIntentId: "checkout-parked",
    cart: original.snapshot(),
    pricingState: original.stateSnapshot(),
    recallBinding: null,
  });
  assert.equal(restored.checkoutIntentId, "checkout-parked");
  assert.deepEqual(active.read().cart, original.snapshot());
  await coordinator.clearAfterCompleted(restored, "order-parked");
  assert.equal(active.read().cart.lines.length, 0);
});

test("设备 scope 失效后已耐久完成订单仍由原支付 lease 清车并释放", async () => {
  const active = session(cartWithDiscount());
  const leaseCoordinator = createCoordinator(active, null);
  const lease = await leaseCoordinator.acquireExact({
    checkoutIntentId: "checkout-scope-invalidated",
    expectedRevision: active.read().cart.revision,
  });

  await assert.rejects(
    () =>
      leaseCoordinator.clearAfterCompleted(
        { ...lease },
        "wrong-lease-must-not-clear",
      ),
    hasCode("PAYMENT_CART_LEASE_CONFLICT"),
  );
  assert.equal(active.read().cart.lines.length, 1);

  assert.equal(active.invalidateForDeviceScope(), true);
  await leaseCoordinator.clearAfterCompleted(lease, "durable-completed-order");
  assert.equal(active.hasPendingExclusiveOperation(), false);
  await assert.rejects(
    () => leaseCoordinator.readExact(lease),
    hasCode("PAYMENT_CART_LEASE_CONFLICT"),
  );
});

test("清车失败时仍保留原支付 lease，不能把完成态误当作安全退出", async () => {
  const active = new ActivePricingCartSession(
    cartWithDiscount(),
    () => cartWithDiscount(),
  );
  const leaseCoordinator = createCoordinator(active, null);
  const lease = await leaseCoordinator.acquireExact({
    checkoutIntentId: "checkout-clear-failure",
    expectedRevision: active.read().cart.revision,
  });

  await assert.rejects(
    () => leaseCoordinator.clearAfterCompleted(lease, "order-clear-failure"),
    /Empty cart factory must return a cart without lines/u,
  );
  assert.equal(await leaseCoordinator.readExact(lease), lease);
  assert.equal(active.read().cart.lines.length, 1);

  await leaseCoordinator.releaseAfterSafeCancel(lease, "order-clear-failure");
});

test("冷启动先恢复 promotion/asOf/手工折扣状态并立即持有写锁", async () => {
  const recoveredCart = cartWithPromotionAndManualDiscount();
  const recovered = recoveredCart.snapshot();
  const pricingState = recoveredCart.stateSnapshot();
  const active = session();
  const material: PaymentCartRecoveryMaterial = {
    checkoutIntentId: "checkout-recovery",
    cart: recovered,
    pricingState,
    recallBinding: null,
  };
  const leaseCoordinator = createCoordinator(active, material);

  const initialized = await leaseCoordinator.initializeRecovery();
  assert.ok(initialized);
  assert.equal(initialized.checkoutIntentId, "checkout-recovery");
  assert.deepEqual(initialized.pricingState, pricingState);
  assert.deepEqual(initialized.cart, recovered);
  assert.throws(
    () => active.increaseLine("line-promo"),
    hasCode("ACTIVE_PRICING_CART_BUSY"),
  );

  const replay = await leaseCoordinator.initializeRecovery();
  assert.equal(replay, initialized);
  await leaseCoordinator.releaseAfterSafeCancel(initialized, "order-recovery");
  assert.deepEqual(active.read().pricingState, pricingState);
  assert.deepEqual(active.read().cart, recovered);
});

test("冷启动比较持久化购物车时允许缺少定价引擎生成的折扣展示来源", async () => {
  const cart = cartWithPromotionAndManualDiscount();
  const snapshot = cart.snapshot();
  const material: PaymentCartRecoveryMaterial = {
    checkoutIntentId: "checkout-persisted-projection",
    cart: {
      ...snapshot,
      lines: snapshot.lines.map(({ discountSource: _displayOnly, ...line }) => line),
    },
    pricingState: cart.stateSnapshot(),
    recallBinding: null,
  };
  const active = session();
  const coordinator = createCoordinator(active, material);
  const recovered = await coordinator.initializeRecovery();
  assert.ok(recovered);
  assert.deepEqual(recovered.total, material.cart.actualAmount);
  assert.deepEqual(recovered.pricingState, material.pricingState);
  await coordinator.releaseAfterSafeCancel(recovered, "order-persisted-projection");
});

for (const field of ["quantity", "productCode", "actualAmount", "discount", "syncProvenance"] as const) {
  test(`冷启动耐久商品 ${field} 无法由定价状态复现时降级为按耐久购物车持有 lease，不让启动失败`, async () => {
    const cart = cartWithDiscount();
    const snapshot = cart.snapshot();
    const changed = field === "quantity" ? "2"
      : field === "productCode" ? "different-product"
      : field === "syncProvenance" ? { referenceCode: "changed", priceSource: 0 }
      : { currency: "AUD", cents: 1 };
    const material: PaymentCartRecoveryMaterial = {
      checkoutIntentId: `checkout-drift-${field}`,
      cart: {
        ...snapshot,
        lines: snapshot.lines.map(({ discountSource: _displayOnly, ...line }) => ({
          ...line, [field]: changed,
        })),
      },
      pricingState: cart.stateSnapshot(),
      recallBinding: null,
    };
    const active = session();
    const fallbacks: PaymentCartDurableRecoveryFallback[] = [];
    const coordinator = createCoordinator(active, material, fallbacks);

    const lease = await coordinator.initializeRecovery();
    assert.ok(lease);
    // lease 以耐久 cart 为准：金额与草稿订单行一致，绝不把重算出的不同金额交给支付运行时。
    assert.equal(lease.cart, material.cart);
    assert.equal(lease.pricingState, material.pricingState);
    assert.deepEqual(lease.total, material.cart.actualAmount);
    assert.equal(lease.revision, material.cart.revision);
    assert.deepEqual(fallbacks, [{
      checkoutIntentId: material.checkoutIntentId,
      differences: field === "actualAmount" || field === "discount"
        ? [...cartTotalsDiffering(material), `lines[0].${field}`]
        : [`lines[0].${field}`],
    }]);
    // 销售车保持为空并被支付 lease 独占，收银员不能在恢复完成前开新单。
    assert.equal(active.read().cart.lines.length, 0);
    assert.throws(
      () => active.addItem(lateItem()),
      hasCode("ACTIVE_PRICING_CART_BUSY"),
    );
    assert.equal(await coordinator.readExact(lease), lease);
    assert.equal(await coordinator.initializeRecovery(), lease);
    assert.equal(
      await coordinator.acquireExact({
        checkoutIntentId: material.checkoutIntentId,
        expectedRevision: material.cart.revision,
      }),
      lease,
    );

    // 安全取消后释放写锁，销售车为空、可重新扫码。
    await coordinator.releaseAfterSafeCancel(lease, `order-drift-${field}`);
    assert.equal(active.read().cart.lines.length, 0);
    active.addItem(lateItem());
    assert.equal(active.read().cart.lines.length, 1);
  });
}

test("冷启动耐久降级后订单完成仍能清车并释放 lease", async () => {
  const cart = cartWithDiscount();
  const snapshot = cart.snapshot();
  const material: PaymentCartRecoveryMaterial = {
    checkoutIntentId: "checkout-drift-complete",
    cart: {
      ...snapshot,
      lines: snapshot.lines.map((line) => ({ ...line, priceSource: "manual" as const })),
    },
    pricingState: cart.stateSnapshot(),
    recallBinding: null,
  };
  const active = session();
  const coordinator = createCoordinator(active, material);
  const lease = await coordinator.initializeRecovery();
  assert.ok(lease);
  await coordinator.clearAfterCompleted(lease, "order-drift-complete");
  assert.equal(active.hasPendingExclusiveOperation(), false);
  assert.equal(active.read().cart.lines.length, 0);
  await assert.rejects(
    () => coordinator.readExact(lease),
    hasCode("PAYMENT_CART_LEASE_CONFLICT"),
  );
});

test("定价快照已不被当前定价引擎接受时同样降级为耐久 lease", async () => {
  const cart = cartWithDiscount();
  const pricingState = cart.stateSnapshot();
  const material: PaymentCartRecoveryMaterial = {
    checkoutIntentId: "checkout-unrestorable",
    cart: cart.snapshot(),
    // 模拟新版本定价引擎拒绝旧快照（例如校验规则收紧）。
    pricingState: {
      ...pricingState,
      lines: pricingState.lines.map((line) => ({ ...line, unitPriceCents: Number.NaN })),
    },
    recallBinding: null,
  };
  const fallbacks: PaymentCartDurableRecoveryFallback[] = [];
  const active = session();
  const lease = await createCoordinator(active, material, fallbacks).initializeRecovery();
  assert.ok(lease);
  assert.equal(lease.cart, material.cart);
  assert.deepEqual(fallbacks.map((fallback) => fallback.differences), [["pricingState"]]);
  assert.equal(active.read().cart.lines.length, 0);
});

test("RecallActive 挂单的耐久降级由空车承接 binding，完成后解除挂单围栏", async () => {
  const binding = {
    kind: "recalled",
    scope: { storeCode: "S1", deviceCode: "D1" },
    holdId: "hold-drift",
    recallAttemptId: "recall-drift",
  } as const;
  const cart = cartWithDiscount();
  const snapshot = cart.snapshot();
  const material: PaymentCartRecoveryMaterial = {
    checkoutIntentId: "checkout-drift-recall",
    cart: {
      ...snapshot,
      lines: snapshot.lines.map((line) => ({ ...line, displayName: "Tea (v0.1.0)" })),
    },
    pricingState: cart.stateSnapshot(),
    recallBinding: binding,
  };
  const active = session();
  active.blockForRecallRecovery(binding);
  const coordinator = createCoordinator(active, material);
  const lease = await coordinator.initializeRecovery();
  assert.ok(lease);
  assert.equal(active.read().terminalRecoveryRequired, false);
  assert.deepEqual(active.read().recallBinding, binding);
  assert.equal(active.read().cart.lines.length, 0);

  await coordinator.clearAfterCompleted(lease, "order-drift-recall");
  assert.equal(active.read().recallBinding, null);
  assert.equal(active.read().cart.lines.length, 0);
});

test("降级诊断回调抛错不撤销已取得的恢复 lease", async () => {
  const cart = cartWithDiscount();
  const snapshot = cart.snapshot();
  const material: PaymentCartRecoveryMaterial = {
    checkoutIntentId: "checkout-drift-logger",
    cart: { ...snapshot, lines: snapshot.lines.map((line) => ({ ...line, itemNumber: null })) },
    pricingState: cart.stateSnapshot(),
    recallBinding: null,
  };
  const active = session();
  const coordinator = new ActivePricingCartPaymentLeaseCoordinator(
    active,
    { async findBlockingCart() { return material; } },
    () => "payment-lease-logger",
    () => {
      throw new Error("log sink offline");
    },
  );
  const lease = await coordinator.initializeRecovery();
  assert.ok(lease);
  assert.equal(await coordinator.readExact(lease), lease);
});

test("恢复中心按原材料恢复 parked 订单时，无法精确重算也降级为耐久 lease 并可再次移交", async () => {
  const original = cartWithDiscount();
  const snapshot = original.snapshot();
  const material: PaymentCartRecoveryMaterial = {
    checkoutIntentId: "checkout-parked-drift",
    cart: {
      ...snapshot,
      lines: snapshot.lines.map((line) => ({ ...line, priceSource: "manual" as const })),
    },
    pricingState: original.stateSnapshot(),
    recallBinding: null,
  };
  const active = session();
  const fallbacks: PaymentCartDurableRecoveryFallback[] = [];
  const coordinator = createCoordinator(active, null, fallbacks);
  let sessionChecks = 0;
  const lease = await coordinator.prepareParkedRecovery(material, () => {
    sessionChecks += 1;
  });
  assert.equal(sessionChecks, 2);
  assert.equal(lease.cart, material.cart);
  assert.deepEqual(fallbacks.map((fallback) => fallback.differences), [["lines[0].priceSource"]]);
  assert.equal(active.read().cart.lines.length, 0);
  assert.equal(coordinator.heldCheckoutIntentId(), "checkout-parked-drift");

  await coordinator.clearAfterRecoveryParked("checkout-parked-drift", "order-parked-drift");
  assert.equal(coordinator.heldCheckoutIntentId(), null);
  active.addItem(lateItem());
  assert.equal(active.read().cart.lines.length, 1);
});

test("恢复材料不能覆盖普通购物车，RecallActive 只接纳精确耐久 binding", async () => {
  const source = cartWithDiscount();
  const material: PaymentCartRecoveryMaterial = {
    checkoutIntentId: "checkout-conflict",
    cart: source.snapshot(),
    pricingState: source.stateSnapshot(),
    recallBinding: null,
  };
  const nonEmpty = session(cartWithDiscount());
  await assert.rejects(
    () => createCoordinator(nonEmpty, material).initializeRecovery(),
    hasCode("ACTIVE_PRICING_CART_BUSY"),
  );

  const binding = {
    kind: "recalled",
    scope: { storeCode: "S1", deviceCode: "D1" },
    holdId: "hold-1",
    recallAttemptId: "recall-1",
  } as const;
  const recalled = session();
  recalled.blockForRecallRecovery(binding);
  const recovered = await createCoordinator(recalled, {
    ...material,
    recallBinding: binding,
  }).initializeRecovery();
  assert.ok(recovered);
  assert.deepEqual(recalled.read().recallBinding, binding);
  assert.equal(recalled.read().terminalRecoveryRequired, false);

  const missingBinding = session();
  missingBinding.blockForRecallRecovery(binding);
  await assert.rejects(
    () => createCoordinator(missingBinding, material).initializeRecovery(),
    hasCode("ACTIVE_PRICING_CART_TERMINAL_RECOVERY_REQUIRED"),
  );

  const wrongBinding = session();
  wrongBinding.blockForRecallRecovery(binding);
  await assert.rejects(
    () => createCoordinator(wrongBinding, {
      ...material,
      recallBinding: { ...binding, recallAttemptId: "recall-other" },
    }).initializeRecovery(),
    hasCode("ACTIVE_PRICING_CART_TERMINAL_RECOVERY_REQUIRED"),
  );

  await assert.rejects(
    () => createCoordinator(session(), {
      ...material,
      recallBinding: binding,
    }).initializeRecovery(),
    hasCode("ACTIVE_PRICING_CART_TERMINAL_RECOVERY_REQUIRED"),
  );
});

function createCoordinator(
  active: ActivePricingCartSession,
  material: PaymentCartRecoveryMaterial | null,
  fallbacks: PaymentCartDurableRecoveryFallback[] = [],
): ActivePricingCartPaymentLeaseCoordinator {
  let lease = 0;
  return new ActivePricingCartPaymentLeaseCoordinator(
    active,
    {
      async findBlockingCart() {
        return material;
      },
    },
    () => `payment-lease-${++lease}`,
    (fallback) => fallbacks.push(fallback),
  );
}

function lateItem() {
  return {
    lineId: "late-line",
    productCode: "P2",
    itemNumber: null,
    lookupCode: "2",
    displayName: "Late",
    unitPrice: { currency: "AUD" as const, cents: 100 },
    syncProvenance: { referenceCode: null, priceSource: 0 as const },
  };
}

/** 手工篡改行金额时购物车合计不会随之变化，这里只列出与重算结果不同的合计字段。 */
function cartTotalsDiffering(material: PaymentCartRecoveryMaterial): string[] {
  const replayed = PricingCart.restore(material.pricingState).snapshot();
  return (["subtotal", "discount", "actualAmount"] as const).filter((key) =>
    JSON.stringify(replayed[key]) !== JSON.stringify(material.cart[key]));
}

function session(cart = new PricingCart()): ActivePricingCartSession {
  return new ActivePricingCartSession(cart, () => new PricingCart());
}

function cartWithDiscount(): PricingCart {
  const cart = new PricingCart({
    asOfIso: "2026-07-28T01:00:00.000Z",
  });
  cart.addItem({
    lineId: "line-1",
    productCode: "P1",
    itemNumber: "1001",
    lookupCode: "930000000001",
    displayName: "Tea",
    unitPrice: { currency: "AUD", cents: 1_000 },
    syncProvenance: { referenceCode: null, priceSource: 0 },
  });
  cart.setLineDiscountPercentBps("line-1", 2_000);
  return cart;
}

function cartWithPromotionAndManualDiscount(): PricingCart {
  const asOfIso = "2026-07-28T02:00:00.000Z";
  const cart = new PricingCart({
    asOfIso,
    promotions: [
      {
        id: "promo-1",
        name: "Tea pair",
        effectiveStartIso: "2026-07-28T00:00:00.000Z",
        effectiveEndIso: "2026-07-29T00:00:00.000Z",
        isExclusive: false,
        priority: 1,
        applyQuantity: 2,
        fixedPrice: { currency: "AUD", cents: 1_500 },
        maxApplicationsPerOrder: null,
        products: [{ productCode: "P-PROMO", unitWeight: 1 }],
      },
    ],
  });
  cart.addItem({
    lineId: "line-promo",
    productCode: "P-PROMO",
    itemNumber: "2001",
    lookupCode: "930000000002",
    displayName: "Promo tea",
    quantity: 2,
    unitPrice: { currency: "AUD", cents: 1_000 },
    syncProvenance: { referenceCode: null, priceSource: 0 },
  });
  cart.setOrderDiscountAmount({ currency: "AUD", cents: 100 });
  return cart;
}

function hasCode(code: string): (error: unknown) => boolean {
  return (error) =>
    error instanceof Error &&
    (error as Error & { code?: string }).code === code;
}
