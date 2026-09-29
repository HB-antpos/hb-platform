import assert from "node:assert/strict";
import test from "node:test";

import {
  ACTIVE_PRICING_CART_BUSY,
  ACTIVE_PRICING_CART_STALE_SNAPSHOT,
  ActivePricingCartSession,
} from "./active-pricing-cart-session";
import {
  createConnectedSalesDependencies,
  createConnectedSalesPresenter,
  SALES_CHECKOUT_PREPARED,
  SALES_NEW_TRANSACTIONS_DISABLED,
  PricingCartSalesAdapter,
  type DurableCashCheckoutPort,
  type LocalCatalogPort,
} from "./connected-sales-runtime";
import {
  AuthorizedSalesOperationExecutor,
  SALES_PERMISSIONS,
  type SalesOperationSecurity,
} from "@hb/pos-domain/features/sales/runtime/sales-operation-security";
import { scanTiming } from "./scan-timing";

import type { LocalCatalogMatch } from "@/core/db/catalog-repository";
import type {
  CatalogLookupRevalidationPort,
  CatalogLookupRevalidationResult,
} from "@/features/catalog/catalog-lookup-revalidation";
import { PAYMENT_PERMISSION } from "@/features/payments/runtime/payment-checkout-runtime";
import { PricingCart } from "@/features/sales/domain";
import type {
  SalesFeedbackEvent,
  SalesLookupSelection,
} from "@/features/sales/ui/sales-presenter";

const item = (overrides: Partial<LocalCatalogMatch> = {}): LocalCatalogMatch => ({
  storeCode: "S1", productCode: "P-TEA", referenceCode: null, itemNumber: "100", displayName: "Tea", barcode: "930000000001", lookupCode: "930000000001", lookupCodeNormalized: "930000000001", retailPriceCents: 500, priceSource: 0, priceSourceLabel: "Retail", quantityFactor: 1, taxRateBasisPoints: 1_000, updatedAtIso: null, rowVersion: "1", productImage: null, discountRate: null, isSpecialProduct: false,
  ...overrides,
});

class Catalog implements LocalCatalogPort {
  public constructor(private readonly values: readonly LocalCatalogMatch[]) {}
  public async findExact(code: string): Promise<LocalCatalogMatch | null> {
    const normalized = code.trim().toUpperCase();
    return this.values.find((value) => value.lookupCodeNormalized === normalized) ?? null;
  }
  public async searchByName(query: string): Promise<readonly LocalCatalogMatch[]> {
    const normalized = query.trim().toLowerCase();
    return this.values.filter((value) => value.displayName.toLowerCase().includes(normalized));
  }
}

class CashCheckout implements DurableCashCheckoutPort {
  public readonly calls: unknown[] = [];
  public fail = false;
  public async complete(input: Parameters<DurableCashCheckoutPort["complete"]>[0]) {
    this.calls.push(input);
    if (this.fail) throw new Error("disk full");
    return { completed: true as const, canClearCart: true as const, orderGuid: "order-1", cashDueCents: 500, changeCents: 0, postCommit: { requestDrawer: true, drawerDisposition: "queued" as const, printPolicy: "automatic" as const } };
  }
}

test("目录搜索和 HID 精确扫码只使用本地目录，并以稳定 product/lookup 身份加入定价购物车", async () => {
  const dependencies = connected({
    catalog: new Catalog([item({ quantityFactor: 6 })]),
  });

  const results = await dependencies.workflow.searchProducts("tea");
  const lineId = await dependencies.workflow.addByLookupCode(
    "930000000001",
  );

  assert.deepEqual(results, [{
    productCode: "P-TEA",
    itemNumber: "100",
    barcode: "930000000001",
    lookupCode: "930000000001",
    displayName: "Tea",
    unitPriceCents: 500,
    discountRate: null,
  }]);
  assert.equal(lineId, "line-1");
  assert.equal(dependencies.cart.getSnapshot().lines[0]?.productCode, "P-TEA");
  assert.equal(dependencies.cart.getSnapshot().lines[0]?.lookupCode, "930000000001");
  assert.equal(dependencies.cart.getSnapshot().lines[0]?.quantity, "6");
  await assert.rejects(
    () => dependencies.workflow.addProduct({ ...results[0]!, productCode: "forged" }),
    /identity/i,
  );
});

test("HID 扫码先发布权威购物车结果再结束 timing", async () => {
  const dependencies = connected({ catalog: new Catalog([item()]) });
  const order: string[] = [];
  const originalComplete = scanTiming.complete;
  scanTiming.complete = () => {
    order.push("timing-complete");
  };
  const unsubscribe = dependencies.workflow.subscribeLookupOutcome?.(() => {
    order.push("lookup-outcome-published");
  });

  try {
    await dependencies.workflow.addByLookupCode("930000000001", {
      source: "hid",
      timingId: "scan-order",
    });
    assert.deepEqual(order, [
      "lookup-outcome-published",
      "timing-complete",
    ]);
  } finally {
    unsubscribe?.();
    scanTiming.complete = originalComplete;
  }
});

test("扫码 timing 旁路异常不影响权威购物车发布", async () => {
  const dependencies = connected({ catalog: new Catalog([item()]) });
  const outcomes: SalesFeedbackEvent[] = [];
  const originalComplete = scanTiming.complete;
  scanTiming.complete = () => {
    throw new Error("metric unavailable");
  };
  const unsubscribe = dependencies.workflow.subscribeLookupOutcome?.((outcome) => {
    outcomes.push(outcome);
  });

  try {
    assert.equal(
      await dependencies.workflow.addByLookupCode("930000000001", {
        source: "hid",
        timingId: "scan-side-channel",
      }),
      "line-1",
    );
    assert.equal(dependencies.cart.getSnapshot().lines.length, 1);
    assert.equal(outcomes.at(-1)?.kind, "added");
  } finally {
    unsubscribe?.();
    scanTiming.complete = originalComplete;
  }
});

test("目录搜索过滤同商品同价普通编码，并保留套装和不同价格结果", async () => {
  const dependencies = connected({
    catalog: new Catalog([
      item(),
      item({
        lookupCode: "100",
        lookupCodeNormalized: "100",
      }),
      item({
        lookupCode: "MULTI-SAME",
        lookupCodeNormalized: "MULTI-SAME",
        priceSource: 3,
        priceSourceLabel: "multi-code",
      }),
      item({
        lookupCode: "CLEARANCE-DIFFERENT",
        lookupCodeNormalized: "CLEARANCE-DIFFERENT",
        retailPriceCents: 450,
        priceSource: 4,
        priceSourceLabel: "clearance",
      }),
      item({
        lookupCode: "SET-SAME",
        lookupCodeNormalized: "SET-SAME",
        priceSource: 2,
        priceSourceLabel: "set",
      }),
      item({
        lookupCode: "SET-STORE-SAME",
        lookupCodeNormalized: "SET-STORE-SAME",
        priceSource: 3,
        priceSourceLabel: "set-store-multi-code",
      }),
      item({
        lookupCode: "SET-DIFFERENT",
        lookupCodeNormalized: "SET-DIFFERENT",
        retailPriceCents: 900,
        priceSource: 2,
        priceSourceLabel: "set",
      }),
    ]),
  });

  const results = await dependencies.workflow.searchProducts("tea");

  assert.deepEqual(
    results.map((result) => result.lookupCode),
    [
      "930000000001",
      "CLEARANCE-DIFFERENT",
      "SET-SAME",
      "SET-STORE-SAME",
      "SET-DIFFERENT",
    ],
  );
});

test("本地、搜索和远程目录结果都把 discountRate 转为目录基线并保留 revision 变化", async () => {
  const localItem = item({
    retailPriceCents: 699,
    discountRate: 0.2,
  });
  const local = connected({ catalog: new Catalog([localItem]) });

  await local.workflow.addByLookupCode(localItem.lookupCode);
  let line = local.cart.getSnapshot().lines[0]!;
  assert.equal(line.discount.cents, 140);
  assert.equal(line.actualAmount.cents, 559);
  assert.equal(
    (line as { discountSource?: string }).discountSource,
    "catalog",
  );

  const [localSearchResult] = await local.workflow.searchProducts("tea");
  assert.equal(localSearchResult?.barcode, localItem.barcode);
  assert.equal(localSearchResult?.discountRate, 0.2);
  await local.workflow.addProduct(localSearchResult!);
  line = local.cart.getSnapshot().lines[0]!;
  assert.equal(line.quantity, "2");
  assert.equal(line.discount.cents, 280);
  assert.equal(line.actualAmount.cents, 1_118);

  const remote = deferred<CatalogLookupRevalidationResult>();
  const remoteDependencies = connected({
    catalog: new Catalog([]),
    catalogRevalidation: new SharedRevalidation(remote.promise),
    catalogWorkScheduler: immediateScheduler(),
  });
  assert.equal(
    await remoteDependencies.workflow.addByLookupCode(
      localItem.lookupCode,
    ),
    null,
  );
  remote.resolve({
    kind: "found",
    baseSnapshotId: "snapshot-1",
    item: localItem,
  });
  await waitFor(
    () => remoteDependencies.workflow.getPendingCatalogWorkCount() === 0,
  );
  line = remoteDependencies.cart.getSnapshot().lines[0]!;
  assert.equal(line.discount.cents, 140);
  assert.equal(line.actualAmount.cents, 559);

  const refreshRemote = deferred<CatalogLookupRevalidationResult>();
  const refreshed = connected({
    catalog: new Catalog([item({ retailPriceCents: 699, discountRate: 0.1 })]),
    catalogRevalidation: new SharedRevalidation(refreshRemote.promise),
    catalogWorkScheduler: immediateScheduler(),
  });
  await refreshed.workflow.addByLookupCode(localItem.lookupCode);
  const revisionBefore = refreshed.cart.getSnapshot().revision;
  refreshRemote.resolve({
    kind: "found",
    baseSnapshotId: "snapshot-1",
    item: localItem,
  });
  await waitFor(
    () => refreshed.workflow.getPendingCatalogWorkCount() === 0,
  );
  assert.equal(refreshed.cart.getSnapshot().revision, revisionBefore + 1);
  assert.equal(refreshed.cart.getSnapshot().lines[0]?.discount.cents, 140);
});

test("搜索重扫使用最新本地目录折扣，支持同价变更与移除", async () => {
  let current = item({ retailPriceCents: 699, discountRate: 0.2 });
  const catalog: LocalCatalogPort = {
    findExact: async () => current,
    searchByName: async () => [current],
  };
  const dependencies = connected({ catalog });

  let [searchResult] = await dependencies.workflow.searchProducts("tea");
  await dependencies.workflow.addProduct(searchResult!);
  assert.equal(dependencies.cart.getSnapshot().lines[0]?.discount.cents, 140);

  current = item({ retailPriceCents: 699, discountRate: 0.1 });
  [searchResult] = await dependencies.workflow.searchProducts("tea");
  await dependencies.workflow.addProduct(searchResult!);
  let snapshot = dependencies.cart.getSnapshot();
  assert.equal(snapshot.lines.length, 1);
  assert.equal(snapshot.lines[0]?.quantity, "2");
  assert.equal(snapshot.lines[0]?.discount.cents, 140);
  assert.equal(snapshot.lines[0]?.actualAmount.cents, 1_258);

  current = item({ retailPriceCents: 699, discountRate: 0 });
  [searchResult] = await dependencies.workflow.searchProducts("tea");
  await dependencies.workflow.addProduct(searchResult!);
  snapshot = dependencies.cart.getSnapshot();
  assert.equal(snapshot.lines.length, 1);
  assert.equal(snapshot.lines[0]?.quantity, "3");
  assert.equal(snapshot.lines[0]?.discount.cents, 0);
  assert.equal(snapshot.lines[0]?.actualAmount.cents, 2_097);
});

test("扫码重扫使用最新本地目录折扣并保持连续合并", async () => {
  let current = item({ retailPriceCents: 699, discountRate: 0.2 });
  const catalog: LocalCatalogPort = {
    findExact: async () => current,
    searchByName: async () => [current],
  };
  const dependencies = connected({ catalog });

  await dependencies.workflow.addByLookupCode(current.lookupCode, { source: "hid" });
  current = item({ retailPriceCents: 699, discountRate: 0.1 });
  await dependencies.workflow.addByLookupCode(current.lookupCode, { source: "hid" });
  let snapshot = dependencies.cart.getSnapshot();
  assert.equal(snapshot.lines.length, 1);
  assert.equal(snapshot.lines[0]?.quantity, "2");
  assert.equal(snapshot.lines[0]?.discount.cents, 140);

  current = item({ retailPriceCents: 699, discountRate: 0 });
  await dependencies.workflow.addByLookupCode(current.lookupCode, { source: "hid" });
  snapshot = dependencies.cart.getSnapshot();
  assert.equal(snapshot.lines.length, 1);
  assert.equal(snapshot.lines[0]?.quantity, "3");
  assert.equal(snapshot.lines[0]?.discount.cents, 0);
});

test("同价在线复核把目录折扣从非零清零并递增 revision", async () => {
  const discounted = item({ retailPriceCents: 699, discountRate: 0.2 });
  const firstRemote = deferred<CatalogLookupRevalidationResult>();
  const secondRemote = deferred<CatalogLookupRevalidationResult>();
  let revalidationCalls = 0;
  const dependencies = connected({
    catalog: new Catalog([discounted]),
    catalogRevalidation: {
      revalidate: () =>
        revalidationCalls++ === 0
          ? firstRemote.promise
          : secondRemote.promise,
      isCurrentBaseSnapshot: async () => true,
    },
    catalogWorkScheduler: immediateScheduler(),
  });

  await dependencies.workflow.addByLookupCode(discounted.lookupCode);
  firstRemote.resolve({
    kind: "found",
    baseSnapshotId: "snapshot-1",
    item: discounted,
  });
  await waitFor(
    () => dependencies.workflow.getPendingCatalogWorkCount() === 0,
  );
  assert.equal(dependencies.cart.getSnapshot().lines[0]?.discount.cents, 140);

  await dependencies.workflow.addByLookupCode(discounted.lookupCode);
  const revisionBeforeRemoval = dependencies.cart.getSnapshot().revision;
  secondRemote.resolve({
    kind: "found",
    baseSnapshotId: "snapshot-1",
    item: item({ retailPriceCents: 699, discountRate: 0 }),
  });
  await waitFor(
    () => dependencies.workflow.getPendingCatalogWorkCount() === 0,
  );

  const snapshot = dependencies.cart.getSnapshot();
  assert.equal(snapshot.revision, revisionBeforeRemoval + 1);
  assert.equal(snapshot.lines[0]?.quantity, "2");
  assert.equal(snapshot.lines[0]?.discount.cents, 0);
  assert.equal(snapshot.lines[0]?.actualAmount.cents, 1_398);
  assert.equal(
    (snapshot.lines[0] as { discountSource?: string } | undefined)
      ?.discountSource,
    "none",
  );
});

test("离线重扫同一目录基线连续合并并按合并数量重算", async () => {
  const discounted = item({ retailPriceCents: 699, discountRate: 0.2 });
  const dependencies = connected({ catalog: new Catalog([discounted]) });

  await dependencies.workflow.addByLookupCode(discounted.lookupCode, {
    source: "hid",
  });
  await dependencies.workflow.addByLookupCode(discounted.lookupCode, {
    source: "hid",
  });

  const snapshot = dependencies.cart.getSnapshot();
  assert.equal(snapshot.lines.length, 1);
  assert.equal(snapshot.lines[0]?.quantity, "2");
  assert.equal(snapshot.lines[0]?.discount.cents, 280);
  assert.equal(snapshot.lines[0]?.actualAmount.cents, 1_118);
  assert.equal(
    (snapshot.lines[0] as { discountSource?: string } | undefined)
      ?.discountSource,
    "catalog",
  );
});

test("每次扫码仅发布一个权威 outcome，并以 disposition 区分新增和连续合并", async () => {
  const dependencies = connected({
    catalog: new Catalog([
      item(),
      item({
        productCode: "OPENITEM",
        itemNumber: null,
        lookupCode: "OPENITEM",
        lookupCodeNormalized: "OPENITEM",
      }),
    ]),
  });
  const outcomes: unknown[] = [];
  dependencies.workflow.subscribeLookupOutcome?.((outcome) => {
    outcomes.push(outcome);
  });

  await dependencies.workflow.addByLookupCode("930000000001", {
    source: "hid",
  });
  await dependencies.workflow.addByLookupCode("930000000001", {
    source: "hid",
  });
  await dependencies.workflow.addProduct({
    productCode: "P-TEA",
    itemNumber: "100",
    barcode: "930000000001",
    lookupCode: "930000000001",
    displayName: "Tea",
    unitPriceCents: 500,
    discountRate: null,
  });
  await dependencies.workflow.addOpenItem(120);

  assert.deepEqual(
    outcomes.map((outcome: any) => ({
      source: outcome.source,
      kind: outcome.kind,
      lineId: outcome.lineId,
    })),
    [
      { source: "hid", kind: "added", lineId: "line-1" },
      { source: "hid", kind: "incremented", lineId: "line-1" },
      { source: "manual", kind: "incremented", lineId: "line-1" },
      { source: "manual", kind: "added", lineId: "line-4" },
    ],
  );
  assert.equal(new Set(outcomes.map((outcome: any) => outcome.attemptId)).size, 4);
});

test("本地命中已经发布成功后，远程 not-found 或异常不会产生第二个终态", async () => {
  const notFound = deferred<CatalogLookupRevalidationResult>();
  const first = connected({
    catalogRevalidation: new SharedRevalidation(notFound.promise),
    catalogWorkScheduler: immediateScheduler(),
  });
  const firstOutcomes: SalesFeedbackEvent[] = [];
  first.workflow.subscribeLookupOutcome?.((outcome) => {
    firstOutcomes.push(outcome);
  });

  await first.workflow.addByLookupCode("930000000001", {
    source: "hid",
  });
  notFound.resolve({
    kind: "not-found",
    baseSnapshotId: "snapshot-1",
  });
  await waitFor(() => first.workflow.getPendingCatalogWorkCount() === 0);

  assert.deepEqual(
    firstOutcomes.map(({ kind, source }) => ({ kind, source })),
    [{ kind: "added", source: "hid" }],
  );

  const failed = deferred<CatalogLookupRevalidationResult>();
  const second = connected({
    catalogRevalidation: new SharedRevalidation(failed.promise),
    catalogWorkScheduler: immediateScheduler(),
  });
  const secondOutcomes: SalesFeedbackEvent[] = [];
  second.workflow.subscribeLookupOutcome?.((outcome) => {
    secondOutcomes.push(outcome);
  });

  await second.workflow.addByLookupCode("930000000001", {
    source: "camera",
  });
  failed.reject(new Error("remote unavailable"));
  await waitFor(() => second.workflow.getPendingCatalogWorkCount() === 0);

  assert.deepEqual(
    secondOutcomes.map(({ kind, source }) => ({ kind, source })),
    [{ kind: "added", source: "camera" }],
  );
});

test("本地 miss 的当前远程 unavailable、错码与错门店响应各发布一次 failed-blocked", async () => {
  const unavailable = deferred<CatalogLookupRevalidationResult>();
  const first = connected({
    catalog: new Catalog([]),
    catalogRevalidation: new SharedRevalidation(unavailable.promise),
    catalogWorkScheduler: immediateScheduler(),
  });
  const firstOutcomes: SalesFeedbackEvent[] = [];
  first.workflow.subscribeLookupOutcome?.((outcome) => {
    firstOutcomes.push(outcome);
  });

  assert.equal(
    await first.workflow.addByLookupCode("930000000001", {
      source: "hid",
    }),
    null,
  );
  unavailable.resolve({ kind: "unavailable" });
  await waitFor(() => first.workflow.getPendingCatalogWorkCount() === 0);

  assert.deepEqual(
    firstOutcomes.map(({ kind, source }) => ({ kind, source })),
    [{ kind: "failed-blocked", source: "hid" }],
  );

  const invalid = deferred<CatalogLookupRevalidationResult>();
  const second = connected({
    catalog: new Catalog([]),
    catalogRevalidation: new SharedRevalidation(invalid.promise),
    catalogWorkScheduler: immediateScheduler(),
  });
  const secondOutcomes: SalesFeedbackEvent[] = [];
  second.workflow.subscribeLookupOutcome?.((outcome) => {
    secondOutcomes.push(outcome);
  });

  assert.equal(
    await second.workflow.addByLookupCode("930000000001", {
      source: "camera",
    }),
    null,
  );
  invalid.resolve({
    kind: "found",
    baseSnapshotId: "snapshot-1",
    item: item({
      barcode: "DIFFERENT",
      lookupCode: "DIFFERENT",
      lookupCodeNormalized: "DIFFERENT",
    }),
  });
  await waitFor(() => second.workflow.getPendingCatalogWorkCount() === 0);

  assert.deepEqual(
    secondOutcomes.map(({ kind, source }) => ({ kind, source })),
    [{ kind: "failed-blocked", source: "camera" }],
  );
  assert.equal(second.cart.getSnapshot().lines.length, 0);

  const wrongStore = deferred<CatalogLookupRevalidationResult>();
  const third = connected({
    catalog: new Catalog([]),
    catalogRevalidation: new SharedRevalidation(wrongStore.promise),
    catalogWorkScheduler: immediateScheduler(),
  });
  const thirdOutcomes: SalesFeedbackEvent[] = [];
  third.workflow.subscribeLookupOutcome?.((outcome) => {
    thirdOutcomes.push(outcome);
  });

  assert.equal(
    await third.workflow.addByLookupCode("930000000001", {
      source: "manual",
    }),
    null,
  );
  wrongStore.resolve({
    kind: "found",
    baseSnapshotId: "snapshot-1",
    item: item({ storeCode: "S2" }),
  });
  await waitFor(() => third.workflow.getPendingCatalogWorkCount() === 0);

  assert.deepEqual(
    thirdOutcomes.map(({ kind, source }) => ({ kind, source })),
    [{ kind: "failed-blocked", source: "manual" }],
  );
  assert.equal(third.cart.getSnapshot().lines.length, 0);
});

test("HID 扫码只连续合并最后一行，搜索选择仍沿用全局 addItem", async () => {
  const tea = item();
  const coffee = item({
    productCode: "P-COFFEE",
    itemNumber: "200",
    displayName: "Coffee",
    barcode: "COFFEE",
    lookupCode: "COFFEE",
    lookupCodeNormalized: "COFFEE",
  });
  const dependencies = connected({
    catalog: new Catalog([tea, coffee]),
  });

  assert.equal(
    await dependencies.workflow.addByLookupCode(tea.lookupCode),
    "line-1",
  );
  assert.equal(
    await dependencies.workflow.addByLookupCode(coffee.lookupCode),
    "line-2",
  );
  assert.equal(
    await dependencies.workflow.addByLookupCode(tea.lookupCode),
    "line-3",
  );
  assert.deepEqual(
    dependencies.cart
      .getSnapshot()
      .lines.map((line) => [line.lineId, line.quantity]),
    [
      ["line-1", "1"],
      ["line-2", "1"],
      ["line-3", "1"],
    ],
  );

  await dependencies.workflow.addProduct({
    productCode: tea.productCode,
    itemNumber: tea.itemNumber,
    barcode: tea.barcode,
    lookupCode: tea.lookupCode,
    displayName: tea.displayName,
    unitPriceCents: tea.retailPriceCents,
    discountRate: tea.discountRate,
  });
  assert.deepEqual(
    dependencies.cart
      .getSnapshot()
      .lines.map((line) => [line.lineId, line.quantity]),
    [
      ["line-1", "2"],
      ["line-2", "1"],
      ["line-3", "1"],
    ],
  );
});

test("合并购物车复用改数量权限与审计，并只发布一次快照", async () => {
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
  const guard = new SessionGuard();
  const permissionCodes: string[] = [];
  const auditEventTypes: string[] = [];
  let nextId = 0;
  const active = activeCart(source);
  const adapter = new PricingCartSalesAdapter(
    active,
    guard,
    new AuthorizedSalesOperationExecutor(
      {
        authorization: {
          async authorizeAndRun(input, operation) {
            permissionCodes.push(input.permissionCode);
            return {
              authorized: true as const,
              value: await operation({
                authorizationMode: "current-cashier",
                requestingCashierId: "C1",
                authorizingCashierId: null,
                permissionCode: input.permissionCode,
              }),
            };
          },
        },
        audit: {
          async append(events) {
            auditEventTypes.push(
              ...events.map((event) => event.eventType),
            );
          },
        },
        createActionId: () => uuid(++nextId),
        createAuditEventId: () => uuid(++nextId),
        nowIso: () => "2026-07-29T00:00:00.000Z",
      },
      { cashierId: "C1", cashierName: null, userGuid: null },
      guard,
    ),
  );
  let notifications = 0;
  adapter.subscribe(() => {
    notifications += 1;
  });

  assert.equal(adapter.hasMergeCompatibleLines(), true);
  assert.deepEqual(await adapter.mergeCompatibleLines(), {
    groups: [
      {
        keptLineId: "tea-1",
        removedLineIds: ["tea-2"],
      },
    ],
    removedLineCount: 1,
  });
  assert.deepEqual(permissionCodes, [SALES_PERMISSIONS.changeQuantity]);
  assert.deepEqual(auditEventTypes, ["CART_ITEM_QUANTITY_CHANGE"]);
  assert.equal(notifications, 1);
  adapter.destroy();
});

test("更新策略未启用时空车在目录查询前 fail-closed，已有购物车仍可继续当前交易", async () => {
  let exactCalls = 0;
  const sharedCart = activeCart();
  const catalog: LocalCatalogPort = {
    async findExact() {
      exactCalls += 1;
      return item();
    },
    async searchByName() {
      return [];
    },
  };
  const gated = connected({
    activeCartSession: sharedCart,
    catalog,
    newTransactionGate: {
      canStartNewTransaction: () => false,
    },
  });

  await assert.rejects(
    () => gated.workflow.addByLookupCode("930000000001"),
    hasCode(SALES_NEW_TRANSACTIONS_DISABLED),
  );
  assert.equal(exactCalls, 0);
  assert.equal(sharedCart.getSnapshot().lines.length, 0);

  const existingCart = activeCart(cartWithLine());
  const continuing = connected({
    activeCartSession: existingCart,
    catalog,
    createLineId: () => "line-2",
    newTransactionGate: {
      canStartNewTransaction: () => false,
    },
  });
  await continuing.workflow.addByLookupCode("930000000001");

  assert.equal(exactCalls, 1);
  assert.equal(existingCart.getSnapshot().lines.length, 2);
});

test("共享召回购物车 normal clearCart 路由到 owner release，清空购物车与 binding", async () => {
  const sharedCart = activeCart();
  const binding = {
    kind: "recalled" as const,
    scope: { storeCode: "S1", deviceCode: "IPAD1" },
    holdId: "hold-1",
    recallAttemptId: "recall-1",
  };
  sharedCart.blockForRecallRecovery(binding);
  await sharedCart.runExclusive((lease) => {
    lease.replace(cartWithLine().stateSnapshot(), binding);
  });
  const releasedHoldIds: string[] = [];
  const presenter = createConnectedSalesPresenter({
    activeCartSession: sharedCart,
    identity: identity(),
    sessionGuard: new SessionGuard(),
    newTransactionGate: {
      canStartNewTransaction: () => true,
    },
    createCheckoutIntentId: () => "intent-1",
    createLineId: () => "line-2",
    operationSecurity: security(),
    releaseRecalledCart: {
      releaseRecalledCart: async (holdGuid: string) => {
        releasedHoldIds.push(holdGuid);
        // 模拟真实 coordinator：lease 内清车并解除 binding。
        await sharedCart.runExclusive((lease) => {
          const active = lease.read();
          lease.replace(
            {
              ...active.pricingState,
              revision: active.pricingState.revision + 1,
              lines: [],
            },
            active.recallBinding,
          );
          lease.setRecallBinding(null);
        });
      },
    },
  });

  assert.equal(await presenter.clearCart(), true);
  assert.deepEqual(releasedHoldIds, ["hold-1"]);
  assert.equal(sharedCart.getRecallBinding(), null);
  assert.equal(sharedCart.getSnapshot().lines.length, 0);
});

test("共享召回购物车 clearCart 释放失败：返回 false 并保持购物车和 binding", async () => {
  const sharedCart = activeCart();
  const binding = {
    kind: "recalled" as const,
    scope: { storeCode: "S1", deviceCode: "IPAD1" },
    holdId: "hold-1",
    recallAttemptId: "recall-1",
  };
  sharedCart.blockForRecallRecovery(binding);
  await sharedCart.runExclusive((lease) => {
    lease.replace(cartWithLine().stateSnapshot(), binding);
  });
  const presenter = createConnectedSalesPresenter({
    activeCartSession: sharedCart,
    identity: identity(),
    sessionGuard: new SessionGuard(),
    newTransactionGate: {
      canStartNewTransaction: () => true,
    },
    createCheckoutIntentId: () => "intent-1",
    createLineId: () => "line-2",
    operationSecurity: security(),
    releaseRecalledCart: {
      releaseRecalledCart: async () => {
        throw new Error("server down");
      },
    },
  });

  assert.equal(await presenter.clearCart(), false);
  assert.equal(sharedCart.getRecallBinding()?.holdId, "hold-1");
  assert.equal(sharedCart.getSnapshot().lines.length, 1);
});

test("共享召回购物车 clearCart 未接线 release 端口：fail-closed 不清车", async () => {
  const sharedCart = activeCart();
  const binding = {
    kind: "recalled" as const,
    scope: { storeCode: "S1", deviceCode: "IPAD1" },
    holdId: "hold-1",
    recallAttemptId: "recall-1",
  };
  sharedCart.blockForRecallRecovery(binding);
  await sharedCart.runExclusive((lease) => {
    lease.replace(cartWithLine().stateSnapshot(), binding);
  });
  const presenter = createConnectedSalesPresenter({
    activeCartSession: sharedCart,
    identity: identity(),
    sessionGuard: new SessionGuard(),
    newTransactionGate: {
      canStartNewTransaction: () => true,
    },
    createCheckoutIntentId: () => "intent-1",
    createLineId: () => "line-2",
    operationSecurity: security(),
  });

  assert.equal(await presenter.clearCart(), false);
  assert.equal(sharedCart.getRecallBinding()?.holdId, "hold-1");
  assert.equal(sharedCart.getSnapshot().lines.length, 1);
});

test("目录的 referenceCode 与全部后端 priceSource 0..4 原样冻结到购物车", () => {
  for (const priceSource of [0, 1, 2, 3, 4] as const) {
    const adapter = new PricingCartSalesAdapter(
      activeCart(),
      new SessionGuard(),
      operations(new SessionGuard()),
    );
    const referenceCode = `REF-${priceSource}`;

    adapter.addCatalogItem(
      item({
        lookupCode: `LOOKUP-${priceSource}`,
        lookupCodeNormalized: `LOOKUP-${priceSource}`,
        referenceCode,
        priceSource,
      }),
      `line-${priceSource}`,
    );

    assert.deepEqual(adapter.getSnapshot().lines[0]?.syncProvenance, {
      referenceCode,
      priceSource,
    });
    adapter.destroy();
  }
});

test("PricingCart 适配器发布增减和折扣后的快照，并且只有明确订单号才能重建空车", async () => {
  const adapter = new PricingCartSalesAdapter(
    activeCart(),
    new SessionGuard(),
    operations(new SessionGuard()),
  );
  let notifications = 0;
  adapter.subscribe(() => { notifications += 1; });
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
  const populated = new PricingCartSalesAdapter(
    activeCart(cart),
    new SessionGuard(),
    operations(new SessionGuard()),
  );
  populated.subscribe(() => { notifications += 1; });

  await populated.increaseLine("line-1");
  await populated.applyLineDiscountBasisPoints("line-1", 1_000);
  assert.equal(populated.getSnapshot().lines[0]?.actualAmount.cents, 900);
  await assert.rejects(() => populated.clearAfterCommittedOrder(" "), /order guid/i);
  await populated.clearAfterCommittedOrder("order-1");
  assert.equal(populated.getSnapshot().lines.length, 0);
  assert.equal(notifications, 3);
  adapter.destroy();
  populated.destroy();
});

test("现金确认透传同一 checkoutIntent，并强制使用安全注入的门店、设备和收银员身份", async () => {
  const checkout = new CashCheckout();
  const dependencies = connected({
    activeCartSession: activeCart(cartWithLine()),
    cashCheckout: checkout,
  });
  const confirmedCart = dependencies.cart.getSnapshot();
  await dependencies.workflow.completeCash({ checkoutIntentId: "intent-1", cart: confirmedCart, cashTenderedCents: 500 });

  assert.deepEqual(checkout.calls[0], {
    checkoutIntentId: "intent-1",
    cart: confirmedCart,
    cashTenderedCents: 500,
    storeCode: "S1",
    deviceCode: "IPAD1",
    cashierId: "C1",
    cashierName: "Alice",
    userGuid: "U1",
  });
});

test("普通现金结账依次要求 Payment.View、TakeCash 与 Confirm 后才耐久提交", async () => {
  const requested: string[] = [];
  const checkout = new CashCheckout();
  const dependencies = connected({
    activeCartSession: activeCart(cartWithLine()),
    cashCheckout: checkout,
    operationSecurity: paymentSecurity(requested),
  });

  await dependencies.workflow.completeCash({
    checkoutIntentId: "intent-permissions",
    cart: dependencies.cart.getSnapshot(),
    cashTenderedCents: 500,
  });

  assert.deepEqual(requested, [
    PAYMENT_PERMISSION.view,
    PAYMENT_PERMISSION.takeCash,
    PAYMENT_PERMISSION.confirm,
  ]);
  assert.equal(checkout.calls.length, 1);
});

test("普通现金任一支付权限被拒绝时都不执行耐久提交", async () => {
  for (const deniedPermission of [
    PAYMENT_PERMISSION.view,
    PAYMENT_PERMISSION.takeCash,
    PAYMENT_PERMISSION.confirm,
  ]) {
    const checkout = new CashCheckout();
    const dependencies = connected({
      activeCartSession: activeCart(cartWithLine()),
      cashCheckout: checkout,
      operationSecurity: paymentSecurity([], deniedPermission),
    });

    await assert.rejects(
      () =>
        dependencies.workflow.completeCash({
          checkoutIntentId: `intent-denied-${deniedPermission}`,
          cart: dependencies.cart.getSnapshot(),
          cashTenderedCents: 500,
        }),
      hasCode("SALES_OPERATION_NOT_AUTHORIZED"),
    );
    assert.equal(checkout.calls.length, 0, deniedPermission);
  }
});

test("零金额订单只要求 Payment.View 与 Confirm，不请求 TakeCash", async () => {
  const requested: string[] = [];
  const checkout = new CashCheckout();
  const dependencies = connected({
    cashCheckout: checkout,
    operationSecurity: paymentSecurity(requested),
  });

  await dependencies.workflow.completeCash({
    checkoutIntentId: "intent-zero-total",
    cart: dependencies.cart.getSnapshot(),
    cashTenderedCents: null,
  });

  assert.deepEqual(requested, [
    PAYMENT_PERMISSION.view,
    PAYMENT_PERMISSION.confirm,
  ]);
  assert.equal(checkout.calls.length, 1);
});

test("零金额订单缺少 Payment.Confirm 时不执行耐久提交", async () => {
  const checkout = new CashCheckout();
  const dependencies = connected({
    cashCheckout: checkout,
    operationSecurity: paymentSecurity([], PAYMENT_PERMISSION.confirm),
  });

  await assert.rejects(
    () =>
      dependencies.workflow.completeCash({
        checkoutIntentId: "intent-zero-denied",
        cart: dependencies.cart.getSnapshot(),
        cashTenderedCents: null,
      }),
    hasCode("SALES_OPERATION_NOT_AUTHORIZED"),
  );
  assert.equal(checkout.calls.length, 0);
});

test("现金失败不清空购物车；Presenter 只在成功 result 后调用 adapter clear", async () => {
  const checkout = new CashCheckout();
  checkout.fail = true;
  const dependencies = connected({ cashCheckout: checkout });
  await dependencies.workflow.addByLookupCode("930000000001");

  await assert.rejects(() => dependencies.workflow.completeCash({ checkoutIntentId: "intent-fail", cart: dependencies.cart.getSnapshot(), cashTenderedCents: 500 }), /disk full/i);
  assert.equal(dependencies.cart.getSnapshot().lines.length, 1);
});

test("现金 durable commit 持有 exclusive lease；并发 Presenter 不能插入商品，成功后在锁内清车", async () => {
  const completion =
    deferred<Awaited<ReturnType<DurableCashCheckoutPort["complete"]>>>();
  const calls: unknown[] = [];
  const checkout: DurableCashCheckoutPort = {
    complete(input) {
      calls.push(input);
      return completion.promise;
    },
  };
  const dependencies = connected({ cashCheckout: checkout });
  await dependencies.workflow.addByLookupCode("930000000001");
  const confirmedCart = dependencies.cart.getSnapshot();
  const pending = dependencies.workflow.completeCash({
    checkoutIntentId: "intent-exclusive",
    cart: confirmedCart,
    cashTenderedCents: 500,
  });

  await assert.rejects(
    () => dependencies.cart.increaseLine("line-1"),
    hasCode(ACTIVE_PRICING_CART_BUSY),
  );
  completion.resolve(cashResult("order-exclusive"));
  await pending;

  assert.equal(calls.length, 1);
  assert.equal(dependencies.cart.getSnapshot().lines.length, 0);
});

test("现金确认快照已被另一个 Presenter 更新时 fail-closed，不能提交旧车", async () => {
  const checkout = new CashCheckout();
  const dependencies = connected({ cashCheckout: checkout });
  await dependencies.workflow.addByLookupCode("930000000001");
  const staleCart = dependencies.cart.getSnapshot();
  await dependencies.workflow.addByLookupCode("930000000001");

  await assert.rejects(
    () =>
      dependencies.workflow.completeCash({
        checkoutIntentId: "intent-stale",
        cart: staleCart,
        cashTenderedCents: 1_000,
      }),
    hasCode(ACTIVE_PRICING_CART_STALE_SNAPSHOT),
  );
  assert.equal(checkout.calls.length, 0);
  assert.equal(dependencies.cart.getSnapshot().lines[0]?.quantity, "2");
});

test("缺少目录、现金、hold 或 lock Port 时 capability 明确禁用，不能伪造成功", async () => {
  const dependencies = connected({ catalog: undefined, cashCheckout: undefined });
  const presenter = createConnectedSalesPresenter({
    activeCartSession: activeCart(),
    catalog: undefined,
    cashCheckout: undefined,
    identity: identity(),
    sessionGuard: new SessionGuard(),
    newTransactionGate: {
      canStartNewTransaction: () => true,
    },
    createCheckoutIntentId: () => "intent-disabled",
    createLineId: () => "line-disabled",
    operationSecurity: security(),
  });

  assert.deepEqual(dependencies.capabilities, { catalog: false, cartEditing: true, cashCheckout: false, hold: false, lock: false });
  await assert.rejects(() => dependencies.workflow.holdCart(dependencies.cart.getSnapshot()), /unavailable/i);
  await assert.rejects(() => dependencies.workflow.lockTerminal(), /unavailable/i);
  assert.equal(presenter.getState().capabilities.cashCheckout, false);
});

test("401、403、锁屏或换收银员后，旧 presenter 不能再增减、删除或修改折扣，新 presenter 正常", async () => {
  const sharedCart = activeCart(cartWithLine());
  const oldSession = new SessionGuard();
  const oldDependencies = connected({
    activeCartSession: sharedCart,
    sessionGuard: oldSession,
  });
  oldSession.invalidate();
  const before = sharedCart.getSnapshot();

  await assert.rejects(
    () => oldDependencies.cart.increaseLine("line-1"),
    /cashier session inactive/i,
  );
  await assert.rejects(
    () => oldDependencies.cart.decreaseLine("line-1"),
    /cashier session inactive/i,
  );
  await assert.rejects(
    () => oldDependencies.cart.removeLine("line-1"),
    /cashier session inactive/i,
  );
  await assert.rejects(
    () => oldDependencies.cart.applyLineDiscountBasisPoints("line-1", 1_000),
    /cashier session inactive/i,
  );
  assert.equal(sharedCart.getSnapshot(), before);

  const currentDependencies = connected({
    activeCartSession: sharedCart,
    sessionGuard: new SessionGuard(),
  });
  await currentDependencies.cart.increaseLine("line-1");
  await currentDependencies.cart.applyLineDiscountBasisPoints("line-1", 1_000);
  assert.equal(sharedCart.getSnapshot().lines[0]?.quantity, "2");
  assert.equal(sharedCart.getSnapshot().lines[0]?.discount.cents, 100);
});

test("findExact 延迟返回前切换收银员，旧 workflow 不能把商品注入共享购物车", async () => {
  const exact = deferred<LocalCatalogMatch | null>();
  const sharedCart = activeCart();
  const oldSession = new SessionGuard();
  const oldDependencies = connected({
    activeCartSession: sharedCart,
    catalog: {
      findExact: () => exact.promise,
      searchByName: async () => [],
    },
    sessionGuard: oldSession,
  });
  const pending = oldDependencies.workflow.addByLookupCode("930000000001");

  oldSession.invalidate();
  const currentDependencies = connected({
    activeCartSession: sharedCart,
    sessionGuard: new SessionGuard(),
  });
  exact.resolve(item());

  await assert.rejects(pending, /cashier session inactive/i);
  assert.equal(sharedCart.getSnapshot().lines.length, 0);
  await currentDependencies.workflow.addByLookupCode("930000000001");
  assert.equal(sharedCart.getSnapshot().lines.length, 1);
});

test("searchByName 延迟返回前会话失效，旧 workflow 不能返回新收银员上下文中的结果", async () => {
  const search = deferred<readonly LocalCatalogMatch[]>();
  const sessionGuard = new SessionGuard();
  const dependencies = connected({
    catalog: {
      findExact: async () => null,
      searchByName: () => search.promise,
    },
    sessionGuard,
  });
  const pending = dependencies.workflow.searchProducts("tea");

  sessionGuard.invalidate();
  search.resolve([item()]);

  await assert.rejects(pending, /cashier session inactive/i);
});

test("旧 adapter 失效后拒绝读取，且不再把新收银员的购物车更新推给旧 presenter", async () => {
  const sharedCart = activeCart(cartWithLine());
  const oldSession = new SessionGuard();
  const oldAdapter = new PricingCartSalesAdapter(
    sharedCart,
    oldSession,
    operations(oldSession),
  );
  const currentAdapter = new PricingCartSalesAdapter(
    sharedCart,
    new SessionGuard(),
    operations(new SessionGuard()),
  );
  let staleNotifications = 0;
  oldAdapter.subscribe(() => {
    staleNotifications += 1;
  });
  oldSession.invalidate();

  assert.throws(
    () => oldAdapter.getSnapshot(),
    /cashier session inactive/i,
  );
  await currentAdapter.increaseLine("line-1");
  assert.equal(staleNotifications, 0);
  assert.equal(currentAdapter.getSnapshot().lines[0]?.quantity, "2");

  oldAdapter.destroy();
  currentAdapter.destroy();
});

test("成功锁屏使当前 lease 失效时，workflow 仍向 presenter 返回真实成功", async () => {
  const sessionGuard = new SessionGuard();
  const presenter = createConnectedSalesPresenter({
    activeCartSession: activeCart(),
    catalog: new Catalog([item()]),
    identity: identity(),
    lock: {
      async lock() {
        sessionGuard.invalidate();
      },
    },
    sessionGuard,
    newTransactionGate: {
      canStartNewTransaction: () => true,
    },
    createCheckoutIntentId: () => "intent-lock",
    createLineId: () => "line-lock",
    operationSecurity: security(),
  });

  assert.equal(await presenter.lockTerminal(), true);
  assert.equal(presenter.getState().phase, "locked");
  assert.equal(presenter.getState().errorCode, null);
  presenter.destroy();
});

test("旧 presenter 提交现金时不会同步抛异常，而是以 rejected Promise fail-closed", async () => {
  const sessionGuard = new SessionGuard();
  const dependencies = connected({ sessionGuard });
  const cart = dependencies.cart.getSnapshot();
  sessionGuard.invalidate();
  let completion:
    | ReturnType<typeof dependencies.workflow.completeCash>
    | undefined;

  assert.doesNotThrow(() => {
    completion = dependencies.workflow.completeCash({
      checkoutIntentId: "intent-invalid-session",
      cart,
      cashTenderedCents: 500,
    });
  });
  assert.ok(completion);
  await assert.rejects(
    completion,
    /cashier session inactive/i,
  );
});

test("本地命中先发布购物车并释放扫码，远程查询与回写分别等待 UI yield", async () => {
  const remote = deferred<CatalogLookupRevalidationResult>();
  const firstYield = deferred<void>();
  const applyYield = deferred<void>();
  const events: string[] = [];
  let yieldCount = 0;
  const dependencies = connected({
    catalog: {
      async findExact() {
        events.push("local");
        return item();
      },
      async searchByName() {
        return [];
      },
    },
    catalogRevalidation: {
      revalidate() {
        events.push("remote");
        return remote.promise;
      },
      async isCurrentBaseSnapshot() {
        events.push("generation-check");
        return true;
      },
    },
    catalogWorkScheduler: {
      yieldToUi() {
        yieldCount += 1;
        events.push(`yield-${yieldCount}`);
        return yieldCount === 1 ? firstYield.promise : applyYield.promise;
      },
      waitForTimeout: () => new Promise(() => undefined),
    },
  });

  await dependencies.workflow.addByLookupCode("930000000001");

  assert.deepEqual(events, ["local", "yield-1"]);
  assert.equal(dependencies.cart.getSnapshot().lines[0]?.unitPrice.cents, 500);
  assert.equal(dependencies.workflow.getPendingCatalogWorkCount(), 1);

  firstYield.resolve();
  await Promise.resolve();
  assert.deepEqual(events, ["local", "yield-1", "remote"]);
  remote.resolve({
    kind: "found",
    baseSnapshotId: "snapshot-1",
    item: item({
      itemNumber: "NEW-100",
      displayName: "Fresh tea",
      retailPriceCents: 725,
      priceSource: 1,
    }),
  });
  await Promise.resolve();
  await Promise.resolve();
  assert.deepEqual(events, ["local", "yield-1", "remote", "yield-2"]);
  assert.equal(dependencies.cart.getSnapshot().lines[0]?.unitPrice.cents, 500);

  applyYield.resolve();
  await waitFor(() => dependencies.workflow.getPendingCatalogWorkCount() === 0);

  assert.deepEqual(events, [
    "local",
    "yield-1",
    "remote",
    "yield-2",
    "generation-check",
  ]);
  assert.equal(dependencies.cart.getSnapshot().lines[0]?.unitPrice.cents, 725);
  assert.equal(dependencies.cart.getSnapshot().lines[0]?.displayName, "Fresh tea");
});

test("本地命中先完成 ADD 审计，再把远程校准排入后台", async () => {
  const auditRelease = deferred<void>();
  const remote = deferred<CatalogLookupRevalidationResult>();
  const events: string[] = [];
  let nextId = 0;
  const dependencies = connected({
    catalog: {
      async findExact() {
        events.push("local");
        return item();
      },
      async searchByName() {
        return [];
      },
    },
    catalogRevalidation: new SharedRevalidation(remote.promise),
    catalogWorkScheduler: {
      async yieldToUi() {
        events.push("remote-yield");
      },
      waitForTimeout: () => new Promise<void>(() => undefined),
    },
    operationSecurity: {
      authorization: {
        async authorizeAndRun(input, operation) {
          return {
            authorized: true as const,
            value: await operation({
              authorizationMode: "current-cashier",
              requestingCashierId: "C1",
              authorizingCashierId: null,
              permissionCode: input.permissionCode,
            }),
          };
        },
      },
      audit: {
        async append() {
          events.push("add-audit");
          await auditRelease.promise;
        },
      },
      createActionId: () => uuid(++nextId),
      createAuditEventId: () => uuid(++nextId),
      nowIso: () => "2026-07-29T00:00:00.000Z",
    },
  });

  const adding = dependencies.workflow.addByLookupCode("930000000001");
  await waitFor(() => events.length >= 2);

  assert.deepEqual(events, ["local", "add-audit"]);
  assert.equal(dependencies.workflow.getPendingCatalogWorkCount(), 0);

  auditRelease.resolve();
  await adding;

  assert.deepEqual(events, ["local", "add-audit", "remote-yield"]);
  assert.equal(dependencies.workflow.getPendingCatalogWorkCount(), 1);
  remote.resolve({ kind: "unavailable" });
  await waitFor(
    () => dependencies.workflow.getPendingCatalogWorkCount() === 0,
  );
});

test("并发扫码目标按购物车写入顺序发布，不受审计逆序完成影响", async () => {
  const firstAuditEntered = deferred<void>();
  const firstAuditRelease = deferred<void>();
  const scanTargets: string[] = [];
  let auditCallCount = 0;
  let nextId = 0;
  const coffee = item({
    productCode: "P-COFFEE",
    referenceCode: "REF-COFFEE",
    itemNumber: "200",
    displayName: "Coffee",
    barcode: "930000000002",
    lookupCode: "930000000002",
    lookupCodeNormalized: "930000000002",
  });
  const dependencies = connected({
    catalog: new Catalog([item(), coffee]),
    operationSecurity: {
      authorization: {
        async authorizeAndRun(input, operation) {
          return {
            authorized: true as const,
            value: await operation({
              authorizationMode: "current-cashier",
              requestingCashierId: "C1",
              authorizingCashierId: null,
              permissionCode: input.permissionCode,
            }),
          };
        },
      },
      audit: {
        async append() {
          auditCallCount += 1;
          if (auditCallCount === 1) {
            firstAuditEntered.resolve();
            await firstAuditRelease.promise;
          }
        },
      },
      createActionId: () => uuid(++nextId),
      createAuditEventId: () => uuid(++nextId),
      nowIso: () => "2026-07-29T00:00:00.000Z",
    },
  });
  dependencies.workflow.subscribeScanTarget((lineId) => {
    scanTargets.push(lineId);
  });

  const first = dependencies.workflow.addByLookupCode("930000000001");
  await firstAuditEntered.promise;
  const second = dependencies.workflow.addByLookupCode("930000000002");
  assert.equal(await second, "line-2");
  assert.deepEqual(scanTargets, ["line-1", "line-2"]);

  firstAuditRelease.resolve();
  assert.equal(await first, "line-1");
  assert.deepEqual(scanTargets, ["line-1", "line-2"]);
});

test("本地命中已加购但 ADD 审计暂停时，结账返回后不得再登记远程改价", async () => {
  const auditEntered = deferred<void>();
  const auditRelease = deferred<void>();
  let remoteCalls = 0;
  let nextId = 0;
  const dependencies = connected({
    catalogRevalidation: {
      async revalidate() {
        remoteCalls += 1;
        return {
          kind: "found" as const,
          baseSnapshotId: "snapshot-1",
          item: item({ retailPriceCents: 725 }),
        };
      },
      async isCurrentBaseSnapshot() {
        return true;
      },
    },
    catalogWorkScheduler: immediateScheduler(),
    operationSecurity: {
      authorization: {
        async authorizeAndRun(input, operation) {
          return {
            authorized: true as const,
            value: await operation({
              authorizationMode: "current-cashier",
              requestingCashierId: "C1",
              authorizingCashierId: null,
              permissionCode: input.permissionCode,
            }),
          };
        },
      },
      audit: {
        async append() {
          auditEntered.resolve();
          await auditRelease.promise;
        },
      },
      createActionId: () => uuid(++nextId),
      createAuditEventId: () => uuid(++nextId),
      nowIso: () => "2026-07-29T00:00:00.000Z",
    },
  });

  const adding = dependencies.workflow.addByLookupCode("930000000001");
  await auditEntered.promise;
  assert.equal(dependencies.cart.getSnapshot().lines[0]?.unitPrice.cents, 500);
  assert.equal(dependencies.workflow.getPendingCatalogWorkCount(), 0);

  assert.deepEqual(
    await dependencies.workflow.settlePendingCatalogWork({
      timeoutMs: 2_000,
    }),
    { timedOut: false },
  );
  auditRelease.resolve();
  await adding;
  await new Promise<void>((resolve) => setImmediate(resolve));

  assert.equal(remoteCalls, 0);
  assert.equal(dependencies.workflow.getPendingCatalogWorkCount(), 0);
  assert.equal(dependencies.cart.getSnapshot().lines[0]?.unitPrice.cents, 500);
});

test("本地 miss 授权暂停跨越结账边界时，不得登记远程自动加购", async () => {
  const authorizationEntered = deferred<void>();
  const authorizationRelease = deferred<void>();
  let remoteCalls = 0;
  let nextId = 0;
  const dependencies = connected({
    catalog: new Catalog([]),
    catalogRevalidation: {
      async revalidate() {
        remoteCalls += 1;
        return {
          kind: "found" as const,
          baseSnapshotId: "snapshot-1",
          item: item(),
        };
      },
      async isCurrentBaseSnapshot() {
        return true;
      },
    },
    catalogWorkScheduler: immediateScheduler(),
    operationSecurity: {
      authorization: {
        async authorizeAndRun(input, operation) {
          authorizationEntered.resolve();
          await authorizationRelease.promise;
          return {
            authorized: true as const,
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
    },
  });

  const adding = dependencies.workflow.addByLookupCode("930000000001");
  await authorizationEntered.promise;
  assert.deepEqual(
    await dependencies.workflow.settlePendingCatalogWork({
      timeoutMs: 2_000,
    }),
    { timedOut: false },
  );
  authorizationRelease.resolve();

  await assert.rejects(adding, hasCode(SALES_CHECKOUT_PREPARED));
  await new Promise<void>((resolve) => setImmediate(resolve));
  assert.equal(remoteCalls, 0);
  assert.equal(dependencies.workflow.getPendingCatalogWorkCount(), 0);
  assert.equal(dependencies.cart.getSnapshot().lines.length, 0);
});

test("本地 miss 立即释放连续扫码；共享远程结果仍按每次扫码自动加购", async () => {
  const remote = deferred<CatalogLookupRevalidationResult>();
  const revalidation = new SharedRevalidation(remote.promise);
  const dependencies = connected({
    catalog: new Catalog([]),
    catalogRevalidation: revalidation,
    catalogWorkScheduler: immediateScheduler(),
  });
  const scanTargets: string[] = [];
  dependencies.workflow.subscribeScanTarget((lineId) => {
    scanTargets.push(lineId);
  });

  const immediateLineIds = await Promise.all([
    dependencies.workflow.addByLookupCode("930000000001"),
    dependencies.workflow.addByLookupCode(" 930000000001 "),
  ]);

  assert.deepEqual(immediateLineIds, [null, null]);
  assert.equal(dependencies.cart.getSnapshot().lines.length, 0);
  assert.equal(dependencies.workflow.getPendingCatalogWorkCount(), 2);
  remote.resolve({
    kind: "found",
    baseSnapshotId: "snapshot-1",
    item: item(),
  });
  await waitFor(() => dependencies.workflow.getPendingCatalogWorkCount() === 0);

  assert.equal(dependencies.cart.getSnapshot().lines[0]?.quantity, "2");
  assert.deepEqual(scanTargets, ["line-1", "line-1"]);
});

test("本地 miss 的远程补入同样只连续合并最后一行", async () => {
  const source = new PricingCart();
  const teaInput = {
    productCode: "P-TEA",
    itemNumber: "100",
    lookupCode: "930000000001",
    displayName: "Tea",
    unitPrice: { currency: "AUD" as const, cents: 500 },
    syncProvenance: {
      referenceCode: null,
      priceSource: 0 as const,
    },
  };
  source.addScannedItem({ ...teaInput, lineId: "tea-1" });
  source.addScannedItem({
    ...teaInput,
    lineId: "coffee",
    productCode: "P-COFFEE",
    lookupCode: "COFFEE",
    displayName: "Coffee",
  });
  const remote = deferred<CatalogLookupRevalidationResult>();
  const dependencies = connected({
    activeCartSession: activeCart(source),
    catalog: new Catalog([]),
    catalogRevalidation: new SharedRevalidation(remote.promise),
    catalogWorkScheduler: immediateScheduler(),
    createLineId: () => "tea-remote",
  });

  assert.equal(
    await dependencies.workflow.addByLookupCode("930000000001"),
    null,
  );
  remote.resolve({
    kind: "found",
    baseSnapshotId: "snapshot-1",
    item: item(),
  });
  await waitFor(
    () => dependencies.workflow.getPendingCatalogWorkCount() === 0,
  );

  assert.deepEqual(
    dependencies.cart
      .getSnapshot()
      .lines.map((line) => [line.lineId, line.quantity]),
    [
      ["tea-1", "1"],
      ["coffee", "1"],
      ["tea-remote", "1"],
    ],
  );
});

test("结账准备允许既有校准收敛到最终购物车，返回后仍冻结新写入", async () => {
  const remote = deferred<CatalogLookupRevalidationResult>();
  const dependencies = connected({
    catalogRevalidation: new SharedRevalidation(remote.promise),
    catalogWorkScheduler: immediateScheduler(),
  });

  await dependencies.workflow.addByLookupCode("930000000001");
  const settlement = dependencies.workflow.settlePendingCatalogWork({
    timeoutMs: 2_000,
  });
  remote.resolve({
    kind: "found",
    baseSnapshotId: "snapshot-1",
    item: item({ retailPriceCents: 725 }),
  });

  assert.deepEqual(await settlement, { timedOut: false });
  assert.equal(dependencies.cart.getSnapshot().lines[0]?.unitPrice.cents, 725);
  await assert.rejects(
    () => dependencies.cart.increaseLine("line-1"),
    hasCode(SALES_CHECKOUT_PREPARED),
  );

  dependencies.workflow.releasePreparedCheckout();
  await dependencies.cart.increaseLine("line-1");
  assert.equal(dependencies.cart.getSnapshot().lines[0]?.quantity, "2");
});

test("结账等待超时会 fence 迟到购物车写入，但远程目录任务仍可自行完成", async () => {
  const remote = deferred<CatalogLookupRevalidationResult>();
  const timeout = deferred<void>();
  const dependencies = connected({
    catalog: new Catalog([]),
    catalogRevalidation: new SharedRevalidation(remote.promise),
    catalogWorkScheduler: {
      yieldToUi: async () => undefined,
      waitForTimeout: () => timeout.promise,
    },
  });
  let pendingNotifications = 0;
  const outcomes: SalesFeedbackEvent[] = [];
  dependencies.workflow.subscribePendingCatalogWork(() => {
    pendingNotifications += 1;
  });
  dependencies.workflow.subscribeLookupOutcome?.((outcome) => {
    outcomes.push(outcome);
  });

  await dependencies.workflow.addByLookupCode("930000000001");
  const settlement = dependencies.workflow.settlePendingCatalogWork({
    timeoutMs: 2_000,
  });
  timeout.resolve();

  assert.deepEqual(await settlement, { timedOut: true });
  remote.resolve({
    kind: "found",
    baseSnapshotId: "snapshot-1",
    item: item(),
  });
  await waitFor(() => dependencies.workflow.getPendingCatalogWorkCount() === 0);

  assert.equal(dependencies.cart.getSnapshot().lines.length, 0);
  assert.deepEqual(outcomes, []);
  assert.ok(pendingNotifications >= 2);
  await assert.rejects(
    () => dependencies.workflow.addByLookupCode("930000000001"),
    hasCode(SALES_CHECKOUT_PREPARED),
  );
});

test("checkout fence 阻止迟到的目录折扣清零复核改写购物车", async () => {
  const remote = deferred<CatalogLookupRevalidationResult>();
  const timeout = deferred<void>();
  const discounted = item({ retailPriceCents: 699, discountRate: 0.2 });
  const dependencies = connected({
    catalog: new Catalog([discounted]),
    catalogRevalidation: new SharedRevalidation(remote.promise),
    catalogWorkScheduler: {
      yieldToUi: async () => undefined,
      waitForTimeout: () => timeout.promise,
    },
  });

  await dependencies.workflow.addByLookupCode(discounted.lookupCode);
  const before = dependencies.cart.getSnapshot();
  assert.equal(before.lines[0]?.discount.cents, 140);
  assert.equal(before.lines[0]?.actualAmount.cents, 559);

  const settlement = dependencies.workflow.settlePendingCatalogWork({
    timeoutMs: 2_000,
  });
  timeout.resolve();
  assert.deepEqual(await settlement, { timedOut: true });

  remote.resolve({
    kind: "found",
    baseSnapshotId: "snapshot-1",
    item: item({ retailPriceCents: 699, discountRate: 0 }),
  });
  await waitFor(() => dependencies.workflow.getPendingCatalogWorkCount() === 0);

  const after = dependencies.cart.getSnapshot();
  assert.deepEqual(after, before);
});

test("页面销毁同步清空 pending 观察者并 fence 迟到目录结果", async () => {
  const remote = deferred<CatalogLookupRevalidationResult>();
  const dependencies = connected({
    catalog: new Catalog([]),
    catalogRevalidation: new SharedRevalidation(remote.promise),
    catalogWorkScheduler: immediateScheduler(),
  });
  let notifications = 0;
  const outcomes: SalesFeedbackEvent[] = [];
  dependencies.workflow.subscribePendingCatalogWork(() => {
    notifications += 1;
  });
  dependencies.workflow.subscribeLookupOutcome?.((outcome) => {
    outcomes.push(outcome);
  });

  await dependencies.workflow.addByLookupCode("930000000001");
  assert.equal(dependencies.workflow.getPendingCatalogWorkCount(), 1);
  dependencies.workflow.disposePendingCatalogWork();
  const notificationsAfterDispose = notifications;
  assert.equal(dependencies.workflow.getPendingCatalogWorkCount(), 0);

  remote.resolve({
    kind: "found",
    baseSnapshotId: "snapshot-1",
    item: item(),
  });
  await new Promise<void>((resolve) => setImmediate(resolve));
  await new Promise<void>((resolve) => setImmediate(resolve));

  assert.equal(dependencies.cart.getSnapshot().lines.length, 0);
  assert.deepEqual(outcomes, []);
  assert.equal(notifications, notificationsAfterDispose);
});

test("交易 epoch 前进后会静默丢弃迟到目录结果", async () => {
  const remote = deferred<CatalogLookupRevalidationResult>();
  const sharedCart = activeCart();
  const dependencies = connected({
    activeCartSession: sharedCart,
    catalog: new Catalog([]),
    catalogRevalidation: new SharedRevalidation(remote.promise),
    catalogWorkScheduler: immediateScheduler(),
  });
  const outcomes: SalesFeedbackEvent[] = [];
  dependencies.workflow.subscribeLookupOutcome?.((outcome) => {
    outcomes.push(outcome);
  });

  await dependencies.workflow.addByLookupCode("930000000001");
  sharedCart.clearAfterCommittedOrder("order-epoch");
  remote.resolve({
    kind: "found",
    baseSnapshotId: "snapshot-1",
    item: item(),
  });
  await waitFor(() => dependencies.workflow.getPendingCatalogWorkCount() === 0);

  assert.equal(sharedCart.getSnapshot().lines.length, 0);
  assert.deepEqual(outcomes, []);
});

test("结账准备同步冻结延迟授权中的写入，显式释放后才允许继续销售", async () => {
  const authorizationRelease = deferred<void>();
  const authorizationEntered = deferred<void>();
  let nextId = 0;
  const dependencies = connected({
    operationSecurity: {
      authorization: {
        async authorizeAndRun(input, operation) {
          authorizationEntered.resolve();
          await authorizationRelease.promise;
          return {
            authorized: true as const,
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
    },
  });

  const delayedAdd =
    dependencies.workflow.addByLookupCode("930000000001");
  await authorizationEntered.promise;
  const settlement = dependencies.workflow.settlePendingCatalogWork({
    timeoutMs: 2_000,
  });
  authorizationRelease.resolve();

  assert.deepEqual(await settlement, { timedOut: false });
  await assert.rejects(delayedAdd, hasCode(SALES_CHECKOUT_PREPARED));
  assert.equal(dependencies.cart.getSnapshot().lines.length, 0);

  dependencies.workflow.releasePreparedCheckout();
  await dependencies.workflow.addByLookupCode("930000000001");
  assert.equal(dependencies.cart.getSnapshot().lines.length, 1);
});

test("远程身份变化只更新目录，不改写本次交易的现有行", async () => {
  const remote = deferred<CatalogLookupRevalidationResult>();
  const sharedCart = activeCart();
  const dependencies = connected({
    activeCartSession: sharedCart,
    catalogRevalidation: new SharedRevalidation(remote.promise),
    catalogWorkScheduler: immediateScheduler(),
  });

  await dependencies.workflow.addByLookupCode("930000000001");
  remote.resolve({
    kind: "found",
    baseSnapshotId: "snapshot-1",
    item: item({
      productCode: "P-REPLACED",
      referenceCode: "REF-REPLACED",
      retailPriceCents: 999,
    }),
  });
  await waitFor(() => dependencies.workflow.getPendingCatalogWorkCount() === 0);

  const line = sharedCart.getSnapshot().lines[0];
  assert.equal(line?.productCode, "P-TEA");
  assert.equal(line?.unitPrice.cents, 500);
});

test("远程明确不存在保留已加购行，会话失效后迟到结果也不能注入购物车", async () => {
  const notFound = deferred<CatalogLookupRevalidationResult>();
  const first = connected({
    catalogRevalidation: new SharedRevalidation(notFound.promise),
    catalogWorkScheduler: immediateScheduler(),
  });
  await first.workflow.addByLookupCode("930000000001");
  notFound.resolve({
    kind: "not-found",
    baseSnapshotId: "snapshot-1",
  });
  await waitFor(() => first.workflow.getPendingCatalogWorkCount() === 0);
  assert.equal(first.cart.getSnapshot().lines[0]?.unitPrice.cents, 500);

  const sessionGuard = new SessionGuard();
  const late = deferred<CatalogLookupRevalidationResult>();
  const sharedCart = activeCart();
  const second = connected({
    activeCartSession: sharedCart,
    sessionGuard,
    catalog: new Catalog([]),
    catalogRevalidation: new SharedRevalidation(late.promise),
    catalogWorkScheduler: immediateScheduler(),
  });
  const secondOutcomes: SalesFeedbackEvent[] = [];
  second.workflow.subscribeLookupOutcome?.((outcome) => {
    secondOutcomes.push(outcome);
  });
  await second.workflow.addByLookupCode("930000000001");
  sessionGuard.invalidate();
  late.resolve({
    kind: "found",
    baseSnapshotId: "snapshot-1",
    item: item(),
  });
  await waitFor(() => second.workflow.getPendingCatalogWorkCount() === 0);
  assert.equal(sharedCart.getSnapshot().lines.length, 0);
  assert.deepEqual(secondOutcomes, []);
});

test("过期 base snapshot 的 found 与 not-found 都静默丢弃", async () => {
  const found = deferred<CatalogLookupRevalidationResult>();
  const first = connected({
    catalog: new Catalog([]),
    catalogRevalidation: new SharedRevalidation(found.promise, false),
    catalogWorkScheduler: immediateScheduler(),
  });
  const firstOutcomes: SalesFeedbackEvent[] = [];
  first.workflow.subscribeLookupOutcome?.((outcome) => {
    firstOutcomes.push(outcome);
  });

  await first.workflow.addByLookupCode("930000000001");
  found.resolve({
    kind: "found",
    baseSnapshotId: "stale-snapshot",
    item: item(),
  });
  await waitFor(() => first.workflow.getPendingCatalogWorkCount() === 0);

  assert.equal(first.cart.getSnapshot().lines.length, 0);
  assert.deepEqual(firstOutcomes, []);

  const notFound = deferred<CatalogLookupRevalidationResult>();
  const second = connected({
    catalog: new Catalog([]),
    catalogRevalidation: new SharedRevalidation(notFound.promise, false),
    catalogWorkScheduler: immediateScheduler(),
  });
  const secondOutcomes: SalesFeedbackEvent[] = [];
  second.workflow.subscribeLookupOutcome?.((outcome) => {
    secondOutcomes.push(outcome);
  });

  await second.workflow.addByLookupCode("930000000001");
  notFound.resolve({
    kind: "not-found",
    baseSnapshotId: "stale-snapshot",
  });
  await waitFor(() => second.workflow.getPendingCatalogWorkCount() === 0);

  assert.equal(second.cart.getSnapshot().lines.length, 0);
  assert.deepEqual(secondOutcomes, []);
});

test("支付 exclusive lease 期间迟到校准只保留本地行，不竞争共享购物车", async () => {
  const remote = deferred<CatalogLookupRevalidationResult>();
  const releaseLease = deferred<void>();
  const sharedCart = activeCart();
  const dependencies = connected({
    activeCartSession: sharedCart,
    catalogRevalidation: new SharedRevalidation(remote.promise),
    catalogWorkScheduler: immediateScheduler(),
  });
  await dependencies.workflow.addByLookupCode("930000000001");
  const exclusive = sharedCart.runExclusive(async () => {
    await releaseLease.promise;
  });

  remote.resolve({
    kind: "found",
    baseSnapshotId: "snapshot-1",
    item: item({ displayName: "Fresh tea", retailPriceCents: 725 }),
  });
  await waitFor(() => dependencies.workflow.getPendingCatalogWorkCount() === 0);

  assert.equal(sharedCart.getSnapshot().lines[0]?.displayName, "Tea");
  assert.equal(sharedCart.getSnapshot().lines[0]?.unitPrice.cents, 500);
  releaseLease.resolve();
  await exclusive;
});

function connected(overrides: Partial<Parameters<typeof createConnectedSalesDependencies>[0]> = {}) {
  return createConnectedSalesDependencies({
    activeCartSession: activeCart(),
    catalog: new Catalog([item()]),
    cashCheckout: new CashCheckout(),
    identity: identity(),
    sessionGuard: new SessionGuard(),
    newTransactionGate: {
      canStartNewTransaction: () => true,
    },
    createCheckoutIntentId: () => "intent-default",
    createLineId: (() => { let value = 0; return () => `line-${++value}`; })(),
    operationSecurity: security(),
    ...overrides,
  });
}

function activeCart(initial = new PricingCart()) {
  return new ActivePricingCartSession(initial, () => new PricingCart());
}

function cashResult(orderGuid: string) {
  return {
    completed: true as const,
    canClearCart: true as const,
    orderGuid,
    cashDueCents: 500,
    changeCents: 0,
    postCommit: {
      requestDrawer: true,
      drawerDisposition: "queued" as const,
      printPolicy: "automatic" as const,
    },
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

class SharedRevalidation implements CatalogLookupRevalidationPort {
  public constructor(
    private readonly result: Promise<CatalogLookupRevalidationResult>,
    private readonly currentBaseSnapshot = true,
  ) {}

  public revalidate(): Promise<CatalogLookupRevalidationResult> {
    return this.result;
  }

  public async isCurrentBaseSnapshot(): Promise<boolean> {
    return this.currentBaseSnapshot;
  }
}

function immediateScheduler() {
  return {
    yieldToUi: async () => undefined,
    waitForTimeout: () => new Promise<void>(() => undefined),
  };
}

async function waitFor(predicate: () => boolean): Promise<void> {
  for (let attempts = 0; attempts < 50; attempts += 1) {
    if (predicate()) return;
    await new Promise<void>((resolve) => setImmediate(resolve));
  }
  throw new Error("condition was not reached");
}

function hasCode(code: string): (error: unknown) => boolean {
  return (error) =>
    typeof error === "object" &&
    error !== null &&
    "code" in error &&
    error.code === code;
}

function identity() {
  return { storeCode: "S1", deviceCode: "IPAD1", cashierId: "C1", cashierName: "Alice", userGuid: "U1" };
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

class SessionGuard {
  private active = true;

  public assertActive(): void {
    if (!this.active) {
      throw new Error("Cashier session inactive.");
    }
  }

  public invalidate(): void {
    this.active = false;
  }
}

function operations(
  guard: SessionGuard,
): AuthorizedSalesOperationExecutor {
  return new AuthorizedSalesOperationExecutor(
    security(),
    { cashierId: "C1", cashierName: null, userGuid: null },
    guard,
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

function paymentSecurity(
  requested: string[],
  deniedPermission?: string,
): SalesOperationSecurity {
  let nextId = 0;
  return {
    authorization: {
      async authorizeAndRun(input, operation) {
        requested.push(input.permissionCode);
        if (input.permissionCode === deniedPermission) {
          return { authorized: false, reason: "permission-denied" };
        }
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

const CONFLICT_CODE = "6405090401470";

function flySwatter(
  overrides: Partial<LocalCatalogMatch> = {},
): LocalCatalogMatch {
  return item({
    productCode: "P-FLY",
    itemNumber: "EXT-FLY",
    displayName: "EXTENSION Fly Swatter",
    barcode: "9300000000017",
    lookupCode: CONFLICT_CODE,
    lookupCodeNormalized: CONFLICT_CODE,
    retailPriceCents: 899,
    priceSource: 2,
    priceSourceLabel: "set",
    taxRateBasisPoints: null,
    ...overrides,
  });
}

function flower(overrides: Partial<LocalCatalogMatch> = {}): LocalCatalogMatch {
  return flySwatter({
    productCode: "P-FLOWER",
    itemNumber: "FLW-1",
    displayName: "flower",
    barcode: CONFLICT_CODE,
    retailPriceCents: 299,
    priceSource: 0,
    priceSourceLabel: "product",
    ...overrides,
  });
}

/** 一码多商品目录：精确候选首项为目录胜出项，其余为同码其它商品。 */
class ConflictCatalog extends Catalog {
  public candidateReads = 0;

  public constructor(
    values: readonly LocalCatalogMatch[],
    private readonly conflicts: readonly LocalCatalogMatch[],
  ) {
    super(values);
  }

  public async findExactCandidates(
    code: string,
  ): Promise<readonly LocalCatalogMatch[]> {
    this.candidateReads += 1;
    const primary = await this.findExact(code);
    if (primary === null) return [];
    return [
      primary,
      ...this.conflicts.filter(
        (candidate) =>
          candidate.lookupCodeNormalized === primary.lookupCodeNormalized &&
          candidate.productCode !== primary.productCode,
      ),
    ];
  }
}

class CountingRevalidation implements CatalogLookupRevalidationPort {
  public readonly requests: string[] = [];

  public constructor(
    private readonly result: (code: string) => CatalogLookupRevalidationResult,
  ) {}

  public async revalidate(code: string): Promise<CatalogLookupRevalidationResult> {
    this.requests.push(code);
    return this.result(code);
  }

  public async isCurrentBaseSnapshot(): Promise<boolean> {
    return true;
  }
}

function recordingSecurity(actions: string[]): SalesOperationSecurity {
  return {
    ...security(),
    authorization: {
      async authorizeAndRun(input, operation) {
        actions.push(input.action);
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
  };
}

test("一码多商品扫码不自动加购并发布候选；选 Fly、flower、Fly 得到两行共 20.97", async () => {
  const actions: string[] = [];
  const catalog = new ConflictCatalog([flySwatter()], [flySwatter(), flower()]);
  const revalidation = new CountingRevalidation(() => ({
    kind: "found",
    baseSnapshotId: "snapshot-1",
    item: flySwatter(),
  }));
  const dependencies = connected({
    catalog,
    catalogRevalidation: revalidation,
    catalogWorkScheduler: immediateScheduler(),
    operationSecurity: recordingSecurity(actions),
  });
  const selections: SalesLookupSelection[] = [];
  const outcomes: SalesFeedbackEvent[] = [];
  const scanTargets: string[] = [];
  dependencies.workflow.subscribeLookupSelection?.((selection) => {
    selections.push(selection);
  });
  dependencies.workflow.subscribeLookupOutcome?.((outcome) => {
    outcomes.push(outcome);
  });
  dependencies.workflow.subscribeScanTarget((lineId) => {
    scanTargets.push(lineId);
  });

  assert.equal(
    await dependencies.workflow.addByLookupCode(` ${CONFLICT_CODE} `, {
      source: "hid",
    }),
    null,
  );

  assert.equal(dependencies.cart.getSnapshot().lines.length, 0);
  assert.equal(dependencies.workflow.getPendingCatalogWorkCount(), 0);
  assert.deepEqual(revalidation.requests, []);
  assert.equal(outcomes.length, 0);
  assert.equal(selections.length, 1);
  assert.deepEqual(selections[0], {
    selectionId: "lookup-1",
    lookupCode: CONFLICT_CODE,
    source: "hid",
    candidates: [
      {
        productCode: "P-FLY",
        itemNumber: "EXT-FLY",
        barcode: "9300000000017",
        lookupCode: CONFLICT_CODE,
        displayName: "EXTENSION Fly Swatter",
        unitPriceCents: 899,
        discountRate: null,
        priceSource: 2,
      },
      {
        productCode: "P-FLOWER",
        itemNumber: "FLW-1",
        barcode: CONFLICT_CODE,
        lookupCode: CONFLICT_CODE,
        displayName: "flower",
        unitPriceCents: 299,
        discountRate: null,
        priceSource: 0,
      },
    ],
  });

  for (const productCode of ["P-FLY", "P-FLOWER", "P-FLY"]) {
    await dependencies.workflow.addLookupCandidate?.({
      lookupCode: CONFLICT_CODE,
      productCode,
      source: "hid",
    });
    await waitFor(
      () => dependencies.workflow.getPendingCatalogWorkCount() === 0,
    );
  }

  const cart = dependencies.cart.getSnapshot();
  assert.deepEqual(
    cart.lines.map((line) => [
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
  assert.equal(cart.actualAmount.cents, 2_097);
  assert.deepEqual(actions, [
    "scan-add-item",
    "select-match",
    "select-match",
    "select-match",
  ]);
  assert.deepEqual(scanTargets, ["line-1", "line-2", "line-1"]);
  assert.deepEqual(
    outcomes.map((outcome) => [outcome.kind, outcome.source, outcome.lineId]),
    [
      ["added", "hid", "line-1"],
      ["added", "hid", "line-2"],
      ["incremented", "hid", "line-1"],
    ],
  );
  // 中文注释：每次选择都照常排入在线回查，保持与扫码一致的结账前收敛。
  assert.deepEqual(revalidation.requests, [
    CONFLICT_CODE,
    CONFLICT_CODE,
    CONFLICT_CODE,
  ]);
});

test("远程回查只返回胜出项：不改写选中的非胜出商品，也不套用胜出价；胜出项本身仍按回查更新", async () => {
  const catalog = new ConflictCatalog([flySwatter()], [flower()]);
  const revalidation = new CountingRevalidation(() => ({
    kind: "found",
    baseSnapshotId: "snapshot-1",
    item: flySwatter({ retailPriceCents: 950, displayName: "Fly Swatter v2" }),
  }));
  const dependencies = connected({
    catalog,
    catalogRevalidation: revalidation,
    catalogWorkScheduler: immediateScheduler(),
  });

  await dependencies.workflow.addLookupCandidate?.({
    lookupCode: CONFLICT_CODE,
    productCode: "P-FLOWER",
  });
  await waitFor(() => dependencies.workflow.getPendingCatalogWorkCount() === 0);
  assert.equal(revalidation.requests.length, 1);
  assert.deepEqual(
    dependencies.cart.getSnapshot().lines.map((line) => [
      line.productCode,
      line.displayName,
      line.unitPrice.cents,
    ]),
    [["P-FLOWER", "flower", 299]],
  );

  await dependencies.workflow.addLookupCandidate?.({
    lookupCode: CONFLICT_CODE,
    productCode: "P-FLY",
  });
  await waitFor(() => dependencies.workflow.getPendingCatalogWorkCount() === 0);
  assert.deepEqual(
    dependencies.cart.getSnapshot().lines.map((line) => [
      line.productCode,
      line.displayName,
      line.unitPrice.cents,
    ]),
    [
      ["P-FLOWER", "flower", 299],
      ["P-FLY", "Fly Swatter v2", 950],
    ],
  );
});

test("无在线校准时一码多商品同样发布候选，选择后按本地目录加购", async () => {
  const dependencies = connected({
    catalog: new ConflictCatalog([flySwatter()], [flower()]),
  });
  const selections: string[][] = [];
  dependencies.workflow.subscribeLookupSelection?.((selection) => {
    selections.push(selection.candidates.map((candidate) => candidate.productCode));
  });

  assert.equal(await dependencies.workflow.addByLookupCode(CONFLICT_CODE), null);
  assert.deepEqual(selections, [["P-FLY", "P-FLOWER"]]);
  assert.equal(dependencies.cart.getSnapshot().lines.length, 0);

  assert.equal(
    await dependencies.workflow.addLookupCandidate?.({
      lookupCode: CONFLICT_CODE,
      productCode: "P-FLOWER",
    }),
    "line-1",
  );
  assert.equal(dependencies.cart.getSnapshot().lines[0]?.productCode, "P-FLOWER");
  assert.equal(dependencies.cart.getSnapshot().lines[0]?.unitPrice.cents, 299);
});

test("候选待选时丢弃 scan timing；没有页面订阅候选时按失败收口且绝不加购", async () => {
  const originalComplete = scanTiming.complete;
  const originalDiscard = scanTiming.discard;
  const timing: string[] = [];
  scanTiming.complete = (id, outcome) => {
    timing.push(`complete:${id}:${outcome}`);
  };
  scanTiming.discard = (id) => {
    timing.push(`discard:${id}`);
  };
  try {
    const subscribed = connected({
      catalog: new ConflictCatalog([flySwatter()], [flower()]),
    });
    subscribed.workflow.subscribeLookupSelection?.(() => undefined);
    await subscribed.workflow.addByLookupCode(CONFLICT_CODE, {
      source: "hid",
      timingId: "scan-conflict",
    });
    assert.deepEqual(timing, ["discard:scan-conflict"]);

    timing.length = 0;
    const unsubscribed = connected({
      catalog: new ConflictCatalog([flySwatter()], [flower()]),
      catalogRevalidation: new CountingRevalidation(() => ({ kind: "unavailable" })),
      catalogWorkScheduler: immediateScheduler(),
    });
    const outcomes: SalesFeedbackEvent[] = [];
    unsubscribed.workflow.subscribeLookupOutcome?.((outcome) => {
      outcomes.push(outcome);
    });
    assert.equal(
      await unsubscribed.workflow.addByLookupCode(CONFLICT_CODE, {
        source: "hid",
        timingId: "scan-unobserved",
      }),
      null,
    );
    assert.deepEqual(outcomes.map((outcome) => outcome.kind), ["failed-blocked"]);
    assert.deepEqual(timing, ["complete:scan-unobserved:failure"]);
    assert.equal(unsubscribed.cart.getSnapshot().lines.length, 0);
    assert.equal(unsubscribed.workflow.getPendingCatalogWorkCount(), 0);
  } finally {
    scanTiming.complete = originalComplete;
    scanTiming.discard = originalDiscard;
  }
});

test("单商品码仍直接加购并启动在线校准，只读一次精确候选", async () => {
  const catalog = new ConflictCatalog([item()], []);
  const revalidation = new CountingRevalidation(() => ({
    kind: "found",
    baseSnapshotId: "snapshot-1",
    item: item(),
  }));
  const dependencies = connected({
    catalog,
    catalogRevalidation: revalidation,
    catalogWorkScheduler: immediateScheduler(),
  });
  const selections: SalesLookupSelection[] = [];
  dependencies.workflow.subscribeLookupSelection?.((selection) => {
    selections.push(selection);
  });

  assert.equal(await dependencies.workflow.addByLookupCode("930000000001"), "line-1");
  await waitFor(() => dependencies.workflow.getPendingCatalogWorkCount() === 0);

  assert.equal(catalog.candidateReads, 1);
  assert.deepEqual(selections, []);
  assert.deepEqual(revalidation.requests, ["930000000001"]);
  assert.equal(dependencies.cart.getSnapshot().lines[0]?.productCode, "P-TEA");
});

test("搜索框输入冲突码时列出全部候选，选择非胜出商品按其自身价格加购", async () => {
  const dependencies = connected({
    catalog: new ConflictCatalog([flySwatter()], [flower()]),
  });

  const results = await dependencies.workflow.searchProducts(` ${CONFLICT_CODE} `);
  assert.deepEqual(
    results.map((result) => [result.productCode, result.lookupCode, result.unitPriceCents]),
    [
      ["P-FLY", CONFLICT_CODE, 899],
      ["P-FLOWER", CONFLICT_CODE, 299],
    ],
  );

  await dependencies.workflow.addProduct(results[1]!);
  await dependencies.workflow.addProduct(results[0]!);
  assert.deepEqual(
    dependencies.cart.getSnapshot().lines.map((line) => [
      line.productCode,
      line.unitPrice.cents,
    ]),
    [
      ["P-FLOWER", 299],
      ["P-FLY", 899],
    ],
  );
  await assert.rejects(
    () =>
      dependencies.workflow.addProduct({ ...results[1]!, productCode: "P-FORGED" }),
    /identity/iu,
  );
});

test("结账准备冻结后候选选择 fail-closed；候选已从本地目录消失时按未找到失败", async () => {
  const dependencies = connected({
    catalog: new ConflictCatalog([flySwatter()], [flower()]),
  });

  await assert.rejects(
    async () =>
      dependencies.workflow.addLookupCandidate?.({
        lookupCode: CONFLICT_CODE,
        productCode: "P-GONE",
      }),
    hasCode("CATALOG_LOOKUP_NOT_FOUND"),
  );

  await dependencies.workflow.settlePendingCatalogWork({ timeoutMs: 0 });
  await assert.rejects(
    async () =>
      dependencies.workflow.addLookupCandidate?.({
        lookupCode: CONFLICT_CODE,
        productCode: "P-FLOWER",
      }),
    hasCode(SALES_CHECKOUT_PREPARED),
  );
  assert.equal(dependencies.cart.getSnapshot().lines.length, 0);
});
