import assert from "node:assert/strict";
import test from "node:test";

import {
  ACTIVE_PRICING_CART_BUSY,
  ACTIVE_PRICING_CART_DEVICE_SCOPE_INVALIDATED,
  ACTIVE_PRICING_CART_RECALLED_CART_CLEAR_REQUIRED,
  ACTIVE_PRICING_CART_STALE_LEASE,
  ACTIVE_PRICING_CART_TERMINAL_RECOVERY_REQUIRED,
  ACTIVE_PRICING_CART_UPDATE_TRANSITION,
  ActivePricingCartSession,
  type ActivePricingCartLease,
} from "./active-pricing-cart-session";
import {
  createConnectedSalesPresenter,
  PricingCartSalesAdapter,
  type LocalCatalogPort,
} from "./connected-sales-runtime";
import {
  AuthorizedSalesOperationExecutor,
  type SalesOperationSecurity,
} from "@hb/pos-domain/features/sales/runtime/sales-operation-security";

import type { LocalCatalogMatch } from "@/core/db/catalog-repository";
import { PricingCart } from "@/features/sales/domain";

const catalogItem: LocalCatalogMatch = {
  storeCode: "S1",
  productCode: "P-TEA",
  referenceCode: null,
  itemNumber: "100",
  displayName: "Tea",
  barcode: "930000000001",
  lookupCode: "930000000001",
  lookupCodeNormalized: "930000000001",
  retailPriceCents: 500,
  priceSource: 0,
  priceSourceLabel: "Retail",
  quantityFactor: 1,
  taxRateBasisPoints: 1_000,
  updatedAtIso: null,
  rowVersion: "1",
  productImage: null,
  discountRate: null,
  isSpecialProduct: false,
};

const alwaysActiveSessionGuard = Object.freeze({
  assertActive(): void {},
});

class Catalog implements LocalCatalogPort {
  public async findExact(code: string): Promise<LocalCatalogMatch | null> {
    return code.trim() === catalogItem.lookupCode ? catalogItem : null;
  }

  public async searchByName(): Promise<readonly LocalCatalogMatch[]> {
    return [catalogItem];
  }
}

test("两个 Presenter 委托同一 session；销毁一个只取消自身订阅", async () => {
  const activeCart = session();
  let nextLineId = 0;
  const input = {
    activeCartSession: activeCart,
    catalog: new Catalog(),
    identity: identity(),
    sessionGuard: alwaysActiveSessionGuard,
    newTransactionGate: {
      canStartNewTransaction: () => true,
    },
    createCheckoutIntentId: () => "intent-1",
    createLineId: () => `line-${++nextLineId}`,
    operationSecurity: security(),
  };
  const first = createConnectedSalesPresenter(input);
  const second = createConnectedSalesPresenter(input);

  first.setQuery(catalogItem.lookupCode);
  await first.addLookupCode();
  assert.equal(first.getState().cart.lines[0]?.quantity, "1");
  assert.equal(second.getState().cart.lines[0]?.quantity, "1");

  first.destroy();
  await second.increaseLine("line-1");
  assert.equal(second.getState().cart.lines[0]?.quantity, "2");
  assert.equal(
    first.getState().cart.lines[0]?.quantity,
    "1",
    "已销毁 Presenter 不应继续接收 session 发布",
  );

  second.destroy();
});

test("整体清车后旧 adapter 只会访问新车，不能继续修改旧 PricingCart", async () => {
  const original = cartWithLine();
  const activeCart = session(original);
  const oldAdapter = new PricingCartSalesAdapter(
    activeCart,
    alwaysActiveSessionGuard,
    operations(),
  );
  const otherAdapter = new PricingCartSalesAdapter(
    activeCart,
    alwaysActiveSessionGuard,
    operations(),
  );

  await oldAdapter.clearAfterCommittedOrder("ORDER-1");
  await assert.rejects(
    () => otherAdapter.increaseLine("line-1"),
    /unable to increase/i,
  );
  assert.equal(activeCart.getSnapshot().lines.length, 0);
  assert.equal(
    original.snapshot().lines[0]?.quantity,
    "1",
    "session 初始化时必须断开调用方持有的旧实例",
  );

  oldAdapter.destroy();
  otherAdapter.destroy();
});

test("无效 restore 在候选车验证阶段失败，保留原车与 sessionRevision", () => {
  const activeCart = session(cartWithLine());
  const before = activeCart.read();
  const line = before.pricingState.lines[0]!;

  assert.throws(
    () =>
      activeCart.replace({
        ...before.pricingState,
        lines: [line, line],
      }),
    /duplicate cart line id/i,
  );
  assert.equal(activeCart.read(), before);
  assert.equal(activeCart.read().sessionRevision, 0);
  assert.equal(activeCart.getSnapshot().lines[0]?.quantity, "1");
});

test("exclusive lease 忙时拒绝普通 mutation，并在成功或异常后可靠释放", async () => {
  const activeCart = session(cartWithLine());
  const adapter = new PricingCartSalesAdapter(
    activeCart,
    alwaysActiveSessionGuard,
    operations(),
  );
  const gate = deferred<void>();
  let retainedLease: ActivePricingCartLease | null = null;
  const running = activeCart.runExclusive(async (lease) => {
    retainedLease = lease;
    assert.equal(lease.read().sessionRevision, 0);
    await gate.promise;
    lease.replace(lease.read().pricingState, null);
    assert.equal(
      lease.read().sessionRevision,
      1,
      "lease.read 必须返回锁内交换后的最新快照",
    );
  });

  await assert.rejects(
    () => adapter.increaseLine("line-1"),
    hasCode(ACTIVE_PRICING_CART_BUSY),
  );
  await assert.rejects(
    () => activeCart.runExclusive(() => undefined),
    hasCode(ACTIVE_PRICING_CART_BUSY),
  );

  gate.resolve();
  await running;
  await adapter.increaseLine("line-1");
  assert.equal(activeCart.getSnapshot().lines[0]?.quantity, "2");
  assert.throws(
    () => retainedLease?.read(),
    hasCode(ACTIVE_PRICING_CART_STALE_LEASE),
  );

  await assert.rejects(
    () =>
      activeCart.runExclusive(() => {
        throw new Error("exclusive failed");
      }),
    /exclusive failed/i,
  );
  await adapter.decreaseLine("line-1");
  assert.equal(activeCart.getSnapshot().lines[0]?.quantity, "1");
  adapter.destroy();
});

test("安全重启探针只在 exclusive durable 操作期间报告 pending", async () => {
  const activeCart = session(cartWithLine());
  const operation = deferred<void>();

  assert.equal(activeCart.hasPendingExclusiveOperation(), false);
  const pending = activeCart.runExclusive(async () => operation.promise);
  assert.equal(activeCart.hasPendingExclusiveOperation(), true);

  operation.resolve();
  await pending;
  assert.equal(activeCart.hasPendingExclusiveOperation(), false);
});

test("更新切换以事件等待当前 lease，并在成功或异常释放后唤醒全部 waiter", async () => {
  const activeCart = session(cartWithLine());
  const release = deferred<void>();
  const running = activeCart.runExclusive(async () => {
    await release.promise;
    throw new Error("durable operation failed");
  });
  let firstReleased = false;
  let secondReleased = false;
  const first = activeCart.waitForExclusiveLeaseRelease().then(() => {
    firstReleased = true;
  });
  const second = activeCart.waitForExclusiveLeaseRelease().then(() => {
    secondReleased = true;
  });

  await Promise.resolve();
  assert.equal(firstReleased, false);
  assert.equal(secondReleased, false);

  release.resolve();
  await assert.rejects(running, /durable operation failed/u);
  await Promise.all([first, second]);
  assert.equal(firstReleased, true);
  assert.equal(secondReleased, true);
  await activeCart.waitForExclusiveLeaseRelease();
});

test("设备 scope 终态失效可在 active lease 内同步执行，旧 lease 与后续写入都 fail-closed", async () => {
  const activeCart = session(cartWithLine());
  const release = deferred<void>();
  let retainedLease: ActivePricingCartLease | null = null;
  let waiterReleased = false;
  const running = activeCart.runExclusive(async (lease) => {
    retainedLease = lease;
    assert.equal(activeCart.invalidateForDeviceScope(), true);
    assert.equal(
      activeCart.invalidateForDeviceScope(),
      false,
      "重复 scope 失效必须幂等，不能重复发布或改变 lease 释放语义",
    );
    assert.throws(
      () => lease.read(),
      hasCode(ACTIVE_PRICING_CART_DEVICE_SCOPE_INVALIDATED),
    );
    await release.promise;
  });
  const waiter = activeCart.waitForExclusiveLeaseRelease().then(() => {
    waiterReleased = true;
  });

  assert.throws(
    () =>
      activeCart.replace(activeCart.read().pricingState, null),
    hasCode(ACTIVE_PRICING_CART_DEVICE_SCOPE_INVALIDATED),
  );
  assert.throws(
    () => activeCart.increaseLine("line-1"),
    hasCode(ACTIVE_PRICING_CART_DEVICE_SCOPE_INVALIDATED),
  );
  await assert.rejects(
    () => activeCart.runExclusive(() => undefined),
    hasCode(ACTIVE_PRICING_CART_DEVICE_SCOPE_INVALIDATED),
  );
  assert.throws(
    () => activeCart.runUpdateTransitionExclusive(() => undefined),
    hasCode(ACTIVE_PRICING_CART_DEVICE_SCOPE_INVALIDATED),
  );
  assert.equal(waiterReleased, false, "现有 waiter 仍只由原 lease 正常释放");

  release.resolve();
  await running;
  await waiter;
  assert.equal(waiterReleased, true);
  assert.throws(
    () => retainedLease?.read(),
    hasCode(ACTIVE_PRICING_CART_DEVICE_SCOPE_INVALIDATED),
  );
  await assert.rejects(
    () => activeCart.runExclusive(() => undefined),
    hasCode(ACTIVE_PRICING_CART_DEVICE_SCOPE_INVALIDATED),
  );
});

test("设备 scope 失效后仅原持有 lease 可完成已提交订单清车，随后仍保持 fail-closed", async () => {
  const activeCart = session(cartWithLine());
  const beforeInvalidation = activeCart.read();
  let retainedLease: ActivePricingCartLease | null = null;
  const completed = await activeCart.runExclusive((lease) => {
    retainedLease = lease;
    assert.equal(activeCart.invalidateForDeviceScope(), true);
    return lease.clearAfterCommittedOrder("durable-completed-order");
  });

  assert.equal(completed.cart.lines.length, 0);
  assert.equal(activeCart.hasPendingExclusiveOperation(), false);
  assert.throws(
    () => activeCart.read(),
    hasCode(ACTIVE_PRICING_CART_DEVICE_SCOPE_INVALIDATED),
  );
  assert.throws(
    () => activeCart.replace(beforeInvalidation.pricingState, null),
    hasCode(ACTIVE_PRICING_CART_DEVICE_SCOPE_INVALIDATED),
  );
  await assert.rejects(
    () => activeCart.runExclusive(() => undefined),
    hasCode(ACTIVE_PRICING_CART_DEVICE_SCOPE_INVALIDATED),
  );
  assert.throws(
    () => retainedLease?.clearAfterCommittedOrder("must-not-clear-again"),
    hasCode(ACTIVE_PRICING_CART_STALE_LEASE),
  );
});

test("可选更新 guard 仅在 transition 活跃时拒绝新 mutation，默认与 finally 恢复后行为不变", async () => {
  let transitionActive = false;
  const activeCart = new ActivePricingCartSession(
    new PricingCart(),
    () => new PricingCart(),
    {
      canStartMutation: () => !transitionActive,
    },
  );
  const input = {
    lineId: "guarded-line",
    productCode: "P-GUARD",
    itemNumber: null,
    lookupCode: "guard",
    displayName: "Guarded",
    unitPrice: { currency: "AUD" as const, cents: 500 },
    syncProvenance: { referenceCode: null, priceSource: 0 as const },
  };

  activeCart.addItem(input);
  transitionActive = true;
  assert.throws(
    () =>
      activeCart.addItem({
        ...input,
        lineId: "must-not-add",
      }),
    hasCode(ACTIVE_PRICING_CART_UPDATE_TRANSITION),
  );
  await assert.rejects(
    activeCart.runExclusive(async () => undefined),
    hasCode(ACTIVE_PRICING_CART_UPDATE_TRANSITION),
  );
  await activeCart.runUpdateTransitionExclusive(async (lease) => {
    assert.equal(lease.read().cart.lines.length, 1);
  });

  transitionActive = false;
  activeCart.addItem({ ...input, lineId: "after-transition" });
  await activeCart.runExclusive(async (lease) => {
    assert.equal(lease.read().cart.lines.length, 1);
    assert.equal(lease.read().cart.lines[0]?.quantity, "2");
  });
});

test("监听器异常被隔离，且 committed OrderGuid tombstone 令重复 clear 幂等", () => {
  const activeCart = session(cartWithLine());
  let healthyNotifications = 0;
  activeCart.subscribe(() => {
    throw new Error("stale screen");
  });
  activeCart.subscribe(() => {
    healthyNotifications += 1;
  });

  const firstClear = activeCart.clearAfterCommittedOrder(" Order-1 ");
  assert.equal(firstClear.cart.lines.length, 0);
  assert.equal(healthyNotifications, 1);

  activeCart.addItem({
    lineId: "line-2",
    productCode: "P2",
    itemNumber: null,
    lookupCode: "2",
    displayName: "Coffee",
    unitPrice: { currency: "AUD", cents: 700 },
    syncProvenance: { referenceCode: null, priceSource: 0 },
  });
  const beforeDuplicate = activeCart.read();
  const duplicate = activeCart.clearAfterCommittedOrder("order-1");
  assert.equal(duplicate, beforeDuplicate);
  assert.equal(activeCart.getSnapshot().lines[0]?.productCode, "P2");
  assert.equal(healthyNotifications, 2);
});

test("连续扫码返回实际目标行，兼容行合并只发布一次 session 变更", () => {
  const source = new PricingCart();
  const tea = {
    productCode: "P-TEA",
    itemNumber: "100",
    lookupCode: "TEA",
    displayName: "Tea",
    unitPrice: { currency: "AUD" as const, cents: 500 },
    syncProvenance: {
      referenceCode: "REF-TEA",
      priceSource: 1 as const,
    },
  };
  source.addScannedItem({ ...tea, lineId: "tea-1" });
  source.addScannedItem({
    ...tea,
    lineId: "coffee",
    productCode: "P-COFFEE",
    lookupCode: "COFFEE",
  });
  source.addScannedItem({ ...tea, lineId: "tea-2" });
  const activeCart = session(source);
  let notifications = 0;
  activeCart.subscribe(() => {
    notifications += 1;
  });

  assert.equal(activeCart.hasMergeCompatibleLines(), true);
  assert.deepEqual(activeCart.mergeCompatibleLines(), {
    groups: [
      {
        keptLineId: "tea-1",
        removedLineIds: ["tea-2"],
      },
    ],
    removedLineCount: 1,
  });
  assert.equal(notifications, 1);
  assert.equal(activeCart.read().sessionRevision, 1);

  assert.equal(
    activeCart.addScannedItem({
      ...tea,
      lineId: "unused-id",
      productCode: "P-COFFEE",
      lookupCode: "COFFEE",
    }),
    "coffee",
  );
  assert.equal(notifications, 2);
});

test("启动 RecallActive 只安装隐藏恢复围栏，不向快照泄漏 binding 或冻结购物车", () => {
  const activeCart = session();
  const expectedBinding = recallBinding();

  activeCart.blockForRecallRecovery(expectedBinding);

  assert.deepEqual(activeCart.read(), {
    sessionRevision: 1,
    transactionEpoch: 0,
    pricingState: activeCart.read().pricingState,
    cart: activeCart.read().cart,
    recallBinding: null,
    terminalRecoveryRequired: true,
  });
  assert.equal(activeCart.read().cart.lines.length, 0);
  assert.equal(activeCart.read().pricingState.lines.length, 0);
  assert.equal(
    JSON.stringify(activeCart.read()).includes(expectedBinding.holdId),
    false,
  );
  assert.equal(
    JSON.stringify(activeCart.read()).includes(
      expectedBinding.recallAttemptId,
    ),
    false,
  );
  assert.throws(
    () =>
      activeCart.addItem({
        lineId: "blocked-line",
        productCode: "P-BLOCKED",
        itemNumber: null,
        lookupCode: "blocked",
        displayName: "Blocked",
        unitPrice: { currency: "AUD", cents: 100 },
        syncProvenance: { referenceCode: null, priceSource: 0 },
      }),
    hasCode(ACTIVE_PRICING_CART_TERMINAL_RECOVERY_REQUIRED),
  );
  assert.throws(
    () => activeCart.replace(cartWithLine().stateSnapshot()),
    hasCode(ACTIVE_PRICING_CART_TERMINAL_RECOVERY_REQUIRED),
  );
});

test("恢复围栏只接受精确 pending binding；错误或空 binding 均保持空车且继续阻断", async () => {
  const activeCart = session();
  const expectedBinding = recallBinding();
  activeCart.blockForRecallRecovery(expectedBinding);

  await assert.rejects(
    () =>
      activeCart.runExclusive((lease) =>
        lease.replace(cartWithLine().stateSnapshot(), {
          ...expectedBinding,
          recallAttemptId: "wrong-attempt",
        }),
      ),
    hasCode(ACTIVE_PRICING_CART_TERMINAL_RECOVERY_REQUIRED),
  );
  await assert.rejects(
    () =>
      activeCart.runExclusive((lease) =>
        lease.replace(cartWithLine().stateSnapshot(), null),
      ),
    hasCode(ACTIVE_PRICING_CART_TERMINAL_RECOVERY_REQUIRED),
  );

  assert.equal(activeCart.read().terminalRecoveryRequired, true);
  assert.equal(activeCart.read().recallBinding, null);
  assert.equal(activeCart.read().cart.lines.length, 0);
});

test("精确恢复后 recalled 购物车可继续编辑，并在最终订单清车时清除 binding", async () => {
  const activeCart = session();
  const recalled = cartWithLine();
  const binding = recallBinding();
  activeCart.blockForRecallRecovery(binding);

  await activeCart.runExclusive((lease) => {
    lease.replace(recalled.stateSnapshot(), binding);
    assert.deepEqual(lease.read().recallBinding, binding);
    assert.equal(lease.read().terminalRecoveryRequired, false);
  });
  assert.notEqual(
    activeCart.read().recallBinding,
    binding,
    "session 必须克隆 binding，不能保留调用方的可变引用",
  );

  activeCart.increaseLine("line-1");
  assert.equal(activeCart.read().cart.lines[0]?.quantity, "2");

  activeCart.clearAfterCommittedOrder("order-recalled");
  assert.equal(activeCart.read().recallBinding, null);
  assert.equal(activeCart.read().terminalRecoveryRequired, false);
  assert.equal(activeCart.read().cart.lines.length, 0);
});

test("同车释放 active binding 在 lease 内递增 revision，过期 lease 无法事后复写", async () => {
  const activeCart = session();
  const binding = recallBinding();
  activeCart.blockForRecallRecovery(binding);
  let retainedLease: ActivePricingCartLease | null = null;
  await activeCart.runExclusive((lease) => {
    retainedLease = lease;
    lease.replace(cartWithLine().stateSnapshot(), binding);
    assert.equal(lease.read().sessionRevision, 2);
    lease.setRecallBinding(null);
    assert.equal(lease.read().sessionRevision, 3);
  });

  assert.equal(activeCart.read().recallBinding, null);
  assert.throws(
    () => retainedLease?.setRecallBinding(null),
    hasCode(ACTIVE_PRICING_CART_STALE_LEASE),
  );
});

test("已召回购物车删除最后一行/减到 0 被阻止并引导普通清车，非最后一行仍可编辑", async () => {
  const activeCart = session();
  const binding = recallBinding();
  activeCart.blockForRecallRecovery(binding);
  const recalled = cartWithLine();
  await activeCart.runExclusive((lease) => {
    lease.replace(recalled.stateSnapshot(), binding);
  });

  // 最后一行删除/递减会留下空车 + Active binding（孤儿 Active），必须阻止。
  assert.throws(
    () => activeCart.removeLine("line-1"),
    hasCode(ACTIVE_PRICING_CART_RECALLED_CART_CLEAR_REQUIRED),
  );
  assert.throws(
    () => activeCart.decreaseLine("line-1"),
    hasCode(ACTIVE_PRICING_CART_RECALLED_CART_CLEAR_REQUIRED),
  );
  assert.equal(activeCart.read().cart.lines.length, 1);
  assert.equal(activeCart.read().recallBinding?.holdId, "hold-1");

  // 第二行加入后删除其中一行仍允许（购物车不会因此清空）。
  activeCart.addItem({
    lineId: "line-2",
    productCode: "P2",
    itemNumber: null,
    lookupCode: "2",
    displayName: "Coffee",
    unitPrice: { currency: "AUD", cents: 700 },
    syncProvenance: { referenceCode: null, priceSource: 0 },
  });
  assert.equal(activeCart.removeLine("line-2"), true);
  assert.equal(activeCart.read().cart.lines.length, 1);
  assert.equal(activeCart.read().recallBinding?.holdId, "hold-1");
});

test("目录行校准先在副本更新再单次发布，并以交易代次隔离清车后的迟到结果", () => {
  const activeCart = session(cartWithLine());
  let notifications = 0;
  activeCart.subscribe(() => {
    notifications += 1;
  });
  const transactionEpoch = activeCart.read().transactionEpoch;

  const updated = activeCart.refreshCatalogItem(
    {
      expected: {
        productCode: "P1",
        referenceCode: null,
        lookupCode: "1",
      },
      item: {
        productCode: "P1",
        referenceCode: null,
        itemNumber: "NEW-1",
        lookupCode: "1",
        displayName: "Fresh tea",
        retailPriceCents: 700,
        priceSource: 1,
      },
    },
    transactionEpoch,
  );

  assert.deepEqual(updated, ["line-1"]);
  assert.equal(activeCart.getSnapshot().lines[0]?.unitPrice.cents, 700);
  assert.equal(notifications, 1);

  activeCart.clearManually();
  assert.equal(activeCart.read().transactionEpoch, transactionEpoch + 1);
  assert.deepEqual(
    activeCart.refreshCatalogItem(
      {
        expected: {
          productCode: "P1",
          referenceCode: null,
          lookupCode: "1",
        },
        item: {
          productCode: "P1",
          referenceCode: null,
          itemNumber: "LATE",
          lookupCode: "1",
          displayName: "Late tea",
          retailPriceCents: 900,
          priceSource: 1,
        },
      },
      transactionEpoch,
    ),
    [],
  );
  assert.equal(notifications, 2);
});

test("目录校准无真实差异时不交换购物车且不发布", () => {
  const activeCart = session(cartWithLine());
  const snapshotBefore = activeCart.read();
  let notifications = 0;
  activeCart.subscribe(() => {
    notifications += 1;
  });

  const updated = activeCart.refreshCatalogItem(
    {
      expected: {
        productCode: "P1",
        referenceCode: null,
        lookupCode: "1",
      },
      item: {
        productCode: "p1",
        referenceCode: null,
        itemNumber: null,
        lookupCode: " 1 ",
        displayName: "Tea",
        retailPriceCents: 500,
        priceSource: 0,
      },
    },
    snapshotBefore.transactionEpoch,
  );

  assert.deepEqual(updated, []);
  assert.equal(activeCart.read(), snapshotBefore);
  assert.equal(activeCart.read().pricingState.revision, snapshotBefore.pricingState.revision);
  assert.equal(notifications, 0);
});

function session(initial = new PricingCart()): ActivePricingCartSession {
  return new ActivePricingCartSession(initial, () => new PricingCart());
}

/** 一条 2 件 8 澳元的固定价促销，09:00 才开始生效（单价 5 澳元，满 2 件优惠 2 澳元）。 */
function lateTwoForEightPromotion() {
  return {
    id: "PROMO-LATE",
    name: "PROMO-LATE",
    effectiveStartIso: "2026-06-13T09:00:00.000Z",
    effectiveEndIso: "2026-06-14T00:00:00.000Z",
    isExclusive: false,
    priority: 10,
    applyQuantity: 2,
    fixedPrice: { currency: "AUD" as const, cents: 800 },
    maxApplicationsPerOrder: null,
    products: [{ productCode: "P1", unitWeight: 1 }],
  };
}

function cartWithLine(): PricingCart {
  const cart = new PricingCart();
  cart.addItem({
    lineId: "line-1",
    productCode: "P1",
    itemNumber: null,
    lookupCode: "1",
    displayName: "Tea",
    unitPrice: { currency: "AUD", cents: 500 },
    syncProvenance: { referenceCode: null, priceSource: 0 },
  });
  return cart;
}

function identity() {
  return {
    storeCode: "S1",
    deviceCode: "IPAD1",
    cashierId: "C1",
    cashierName: "Alice",
    userGuid: "U1",
  };
}

function recallBinding() {
  return {
    kind: "recalled" as const,
    scope: { storeCode: "S1", deviceCode: "IPAD1" },
    holdId: "hold-1",
    recallAttemptId: "recall-1",
  };
}

function deferred<T>() {
  let resolve!: (value: T | PromiseLike<T>) => void;
  let reject!: (reason?: unknown) => void;
  const promise = new Promise<T>((resolvePromise, rejectPromise) => {
    resolve = resolvePromise;
    reject = rejectPromise;
  });
  return { promise, resolve, reject };
}

function hasCode(code: string): (error: unknown) => boolean {
  return (error) =>
    typeof error === "object" &&
    error !== null &&
    "code" in error &&
    error.code === code;
}

function operations(): AuthorizedSalesOperationExecutor {
  return new AuthorizedSalesOperationExecutor(
    security(),
    { cashierId: "C1", cashierName: null, userGuid: null },
    alwaysActiveSessionGuard,
  );
}

function security(): SalesOperationSecurity {
  let nextId = 0;
  return {
    authorization: {
      async authorizeAndRun(input, operation) {
        return {
          authorized: true,
          value: await operation({
            authorizationMode: "current-cashier",
            requestingCashierId: "C1",
            authorizingCashierId: null,
            permissionCode: input.permissionCode,
          }),
        };
      },
    },
    audit: { append: async () => undefined },
    createActionId: () => uuid(++nextId),
    createAuditEventId: () => uuid(++nextId),
    nowIso: () => "2026-07-29T00:00:00.000Z",
  };
}

function uuid(value: number): string {
  return `00000000-0000-4000-8000-${String(value).padStart(12, "0")}`;
}


test("会话时钟：登录后才开始生效的促销，在下一次改购物车时开始打折", () => {
  let now = "2026-06-13T08:00:00.000Z";
  const active = new ActivePricingCartSession(
    new PricingCart(),
    () => new PricingCart(),
    { nowIso: () => now },
  );
  active.applyPromotionSnapshot([lateTwoForEightPromotion()], now);
  active.addItemWithDisposition({
    lineId: "line-1",
    productCode: "P1",
    itemNumber: null,
    lookupCode: "1",
    displayName: "Tea",
    unitPrice: { currency: "AUD", cents: 500 },
    syncProvenance: { referenceCode: null, priceSource: 0 },
  });
  active.increaseLine("line-1");
  // 08:00 还没到 09:00：不打折。
  assert.equal(active.getSnapshot().lines[0]!.discount.cents, 0);

  now = "2026-06-13T09:30:00.000Z";
  active.increaseLine("line-1");
  // 3 件里凑满一组：5 * 2 - 8 = 2 澳元。
  assert.equal(active.getSnapshot().lines[0]!.discount.cents, 200);
});

test("会话时钟：召回的旧挂单带着挂起时的时点，恢复后按当前时间判断促销", () => {
  const stale = new PricingCart({
    asOfIso: "2026-06-13T08:00:00.000Z",
    promotions: [lateTwoForEightPromotion()],
  });
  stale.addItem({
    lineId: "line-1",
    productCode: "P1",
    itemNumber: null,
    lookupCode: "1",
    displayName: "Tea",
    unitPrice: { currency: "AUD", cents: 500 },
    syncProvenance: { referenceCode: null, priceSource: 0 },
  });
  stale.increaseLine("line-1");
  const heldState = stale.stateSnapshot();

  const withClock = new ActivePricingCartSession(
    new PricingCart(),
    () => new PricingCart(),
    { nowIso: () => "2026-06-13T09:30:00.000Z" },
  );
  withClock.replace(heldState);
  withClock.increaseLine("line-1");
  assert.equal(withClock.getSnapshot().lines[0]!.discount.cents, 200);

  // 对照：不给时钟时仍沿用挂起时的 08:00，促销没开始（旧行为，保持确定性）。
  const withoutClock = new ActivePricingCartSession(
    new PricingCart(),
    () => new PricingCart(),
  );
  withoutClock.replace(heldState);
  withoutClock.increaseLine("line-1");
  assert.equal(withoutClock.getSnapshot().lines[0]!.discount.cents, 0);
});
