import assert from "node:assert/strict";
import test from "node:test";

import { createConnectedSalesDependencies } from "./connected-sales-runtime";

import type { AuditEventDraft } from "@/core/contracts";
import type { LocalCatalogMatch } from "@/core/db/catalog-repository";
import type {
  CatalogLookupRevalidationResult,
} from "@/features/catalog/catalog-lookup-revalidation";
import { PricingCart } from "@/features/sales/domain";
import { ActivePricingCartSession } from "@/features/sales/runtime/active-pricing-cart-session";
import {
  beginOpenItemTiming,
  finishOpenItemTiming,
  setOpenItemTimingSink,
  type OpenItemTimingReport,
} from "@/features/sales/runtime/open-item-timing";

const CATALOG_ITEM: LocalCatalogMatch = {
  storeCode: "STORE-1",
  productCode: "SKU-TEA",
  referenceCode: "REF-TEA",
  itemNumber: "1001",
  displayName: "Tea",
  barcode: "930000000001",
  lookupCode: "930000000001",
  lookupCodeNormalized: "930000000001",
  retailPriceCents: 500,
  priceSource: 1,
  priceSourceLabel: "Store retail",
  quantityFactor: 1,
  taxRateBasisPoints: 1_000,
  updatedAtIso: null,
  rowVersion: "1",
  productImage: null,
  discountRate: null,
  isSpecialProduct: false,
};

const OPEN_ITEM: LocalCatalogMatch = {
  ...CATALOG_ITEM,
  productCode: "OPEN-SKU",
  referenceCode: "REF-OPEN",
  itemNumber: null,
  displayName: "Open item",
  barcode: null,
  lookupCode: "OPENITEM",
  lookupCodeNormalized: "OPENITEM",
  retailPriceCents: 0,
};

test("商品搜索和扫码加入分别请求 Sales.View 与 Sales.AddItem，加入后写 WPF 等价审计", async () => {
  const harness = connected();

  const results = await harness.dependencies.workflow.searchProducts("tea");
  await harness.dependencies.workflow.addByLookupCode("930000000001");

  assert.equal(results.length, 1);
  assert.deepEqual(
    harness.authorizationRequests.map((request) => ({
      permissionCode: request.permissionCode,
      action: request.action,
    })),
    [
      {
        permissionCode: "Permissions.PosTerminal.Sales.View",
        action: "search-products",
      },
      {
        permissionCode: "Permissions.PosTerminal.Sales.AddItem",
        action: "scan-add-item",
      },
    ],
  );
  assert.equal(
    harness.dependencies.cart.getSnapshot().lines[0]?.productCode,
    "SKU-TEA",
  );
  assert.equal(harness.audits.length, 1);
  assert.equal(harness.audits[0]?.eventType, "CART_ITEM_ADD");
  assert.equal(harness.audits[0]?.payload.outcome, "Succeeded");
  assert.equal(harness.audits[0]?.payload.permissionCode, "Permissions.PosTerminal.Sales.AddItem");
  assert.deepEqual(
    {
      requestingCashierId: harness.audits[0]?.payload.requestingCashierId,
      requestingCashierName: harness.audits[0]?.payload.requestingCashierName,
      requestingUserGuid: harness.audits[0]?.payload.requestingUserGuid,
    },
    {
      requestingCashierId: "CASHIER-1",
      requestingCashierName: "Alice",
      requestingUserGuid: "USER-1",
    },
  );
  assert.deepEqual(harness.audits[0]?.payload.items, [
    {
      productCode: "SKU-TEA",
      itemNumber: "1001",
      referenceCode: "REF-TEA",
      lookupCode: "930000000001",
      displayName: "Tea",
      lineKind: "sale",
      beforeQuantity: 0,
      afterQuantity: 1,
      quantityDelta: 1,
      beforeUnitPriceCents: 0,
      afterUnitPriceCents: 500,
      unitPriceDeltaCents: 500,
      beforeDiscountCents: 0,
      afterDiscountCents: 0,
      discountDeltaCents: 0,
      beforeGrossCents: 0,
      afterGrossCents: 500,
      grossDeltaCents: 500,
      beforeActualCents: 0,
      afterActualCents: 500,
      actualDeltaCents: 500,
    },
  ]);
});

test("无码商品分段计时：真实工作流依次打出 授权 / 目录 / 购物车 / 审计 四个点，价格非法时不会留下计时残留", async () => {
  const reports: OpenItemTimingReport[] = [];
  const release = setOpenItemTimingSink((report) => reports.push(report));
  try {
    const harness = connected();

    beginOpenItemTiming();
    await harness.dependencies.workflow.addOpenItem(1_234);
    finishOpenItemTiming("success");

    assert.equal(reports.length, 1);
    const [report] = reports;
    assert.equal(report?.outcome, "success");
    // 四段都必须有值（而不是 null）：说明 authorized → catalog → cart → audited 全部按序触发。
    for (const phase of [
      report?.authorizeMs,
      report?.catalogMs,
      report?.cartMs,
      report?.auditMs,
    ]) {
      assert.equal(typeof phase, "number");
    }

    // 价格非法在进入授权回调之前就抛错：只有授权前失败，后三段为 null。
    beginOpenItemTiming();
    await assert.rejects(() => harness.dependencies.workflow.addOpenItem(0));
    finishOpenItemTiming("failure", "cart-mutation-rejected");
    assert.equal(reports[1]?.outcome, "failure");
    assert.equal(reports[1]?.authorizeMs, null);
    assert.equal(reports[1]?.catalogMs, null);
  } finally {
    release();
  }
});

test("无码商品使用本地唯一 OPENITEM、正整数分币价格和 AddOpenItem 权限，且每次建立独立行", async () => {
  const harness = connected();

  await harness.dependencies.workflow.addOpenItem(1_234);
  await harness.dependencies.workflow.addOpenItem(500);

  const lines = harness.dependencies.cart.getSnapshot().lines;
  assert.equal(lines.length, 2);
  assert.deepEqual(
    lines.map((line) => ({
      lookupCode: line.lookupCode,
      unitPriceCents: line.unitPrice.cents,
      priceSource: line.priceSource,
    })),
    [
      {
        lookupCode: "OPENITEM",
        unitPriceCents: 1_234,
        priceSource: "open-item",
      },
      {
        lookupCode: "OPENITEM",
        unitPriceCents: 500,
        priceSource: "open-item",
      },
    ],
  );
  assert.deepEqual(
    harness.authorizationRequests.map((request) => request.permissionCode),
    [
      "Permissions.PosTerminal.Sales.AddOpenItem",
      "Permissions.PosTerminal.Sales.AddOpenItem",
    ],
  );
  assert.deepEqual(
    harness.audits.map((event) => event.eventType),
    ["CART_ITEM_ADD", "CART_ITEM_ADD"],
  );
});

test("数量、价格、行折扣和整单折扣使用各自 WPF 权限并记录真实 CART 变化", async () => {
  const harness = connected(populatedCart());
  const lineId = harness.dependencies.cart.getSnapshot().lines[0]!.lineId;

  await harness.dependencies.cart.setLineQuantity(lineId, 3);
  await harness.dependencies.cart.setLineUnitPriceCents(lineId, 650);
  await harness.dependencies.cart.applyLineDiscountAmountCents(lineId, 100);
  await harness.dependencies.cart.applyLineManualDiscountBasisPoints(
    lineId,
    850,
  );
  await harness.dependencies.cart.applyLineDiscountBasisPoints(lineId, 2_000);
  await harness.dependencies.cart.applyOrderDiscountAmountCents(250);
  await harness.dependencies.cart.applyOrderManualDiscountBasisPoints(1_250);
  await harness.dependencies.cart.applyOrderQuickDiscountBasisPoints(5_000);

  assert.deepEqual(
    harness.authorizationRequests.map((request) => request.permissionCode),
    [
      "Permissions.PosTerminal.Sales.ChangeQuantity",
      "Permissions.PosTerminal.Sales.ChangePrice",
      "Permissions.PosTerminal.Sales.LineManualDiscount",
      "Permissions.PosTerminal.Sales.LineManualDiscount",
      "Permissions.PosTerminal.Sales.LineQuickDiscount20Percent",
      "Permissions.PosTerminal.Sales.OrderManualDiscount",
      "Permissions.PosTerminal.Sales.OrderManualDiscount",
      "Permissions.PosTerminal.Sales.OrderQuickDiscount50Percent",
    ],
  );
  assert.deepEqual(
    harness.audits.map((event) => event.eventType),
    [
      "CART_ITEM_QUANTITY_CHANGE",
      "CART_ITEM_PRICE_CHANGE",
      "CART_LINE_DISCOUNT_CHANGE",
      "CART_LINE_DISCOUNT_CHANGE",
      "CART_LINE_DISCOUNT_CHANGE",
      "CART_ORDER_DISCOUNT_CHANGE",
      "CART_ORDER_DISCOUNT_CHANGE",
      "CART_ORDER_DISCOUNT_CHANGE",
    ],
  );
  assert.equal(
    harness.dependencies.cart.getSnapshot().actualAmount.cents,
    975,
  );
});

test("手动清空购物车必须取得 ClearCart 授权并写 CART_CLEAR；拒绝授权时状态不变且写 Denied", async () => {
  const allowed = connected(populatedCart());

  await allowed.dependencies.cart.clearCart();

  assert.equal(allowed.dependencies.cart.getSnapshot().lines.length, 0);
  assert.equal(
    allowed.authorizationRequests[0]?.permissionCode,
    "Permissions.PosTerminal.Sales.ClearCart",
  );
  assert.equal(allowed.audits[0]?.eventType, "CART_CLEAR");
  assert.equal(allowed.audits[0]?.payload.outcome, "Succeeded");

  const denied = connected(populatedCart(), { deny: true });
  const before = denied.dependencies.cart.getSnapshot();
  await assert.rejects(
    () => denied.dependencies.cart.clearCart(),
    hasCode("SALES_OPERATION_NOT_AUTHORIZED"),
  );

  assert.equal(denied.dependencies.cart.getSnapshot(), before);
  assert.equal(denied.audits[0]?.eventType, "CART_CLEAR");
  assert.equal(denied.audits[0]?.payload.outcome, "Denied");
  assert.deepEqual(denied.audits[0]?.payload.items, [
    {
      productCode: "SKU-TEA",
      itemNumber: "1001",
      referenceCode: "REF-TEA",
      lookupCode: "930000000001",
      displayName: "Tea",
      lineKind: "sale",
      beforeQuantity: 1,
      afterQuantity: 1,
      quantityDelta: 0,
      beforeUnitPriceCents: 500,
      afterUnitPriceCents: 500,
      unitPriceDeltaCents: 0,
      beforeDiscountCents: 0,
      afterDiscountCents: 0,
      discountDeltaCents: 0,
      beforeGrossCents: 500,
      afterGrossCents: 500,
      grossDeltaCents: 0,
      beforeActualCents: 500,
      afterActualCents: 500,
      actualDeltaCents: 0,
    },
  ]);
});

test("审计仓储失败不回滚已经完成的内存购物车操作", async () => {
  const harness = connected(populatedCart(), { auditFails: true });
  const lineId = harness.dependencies.cart.getSnapshot().lines[0]!.lineId;

  await harness.dependencies.cart.setLineQuantity(lineId, 2);

  assert.equal(
    harness.dependencies.cart.getSnapshot().lines[0]?.quantity,
    "2",
  );
});

test("审计标识或时钟基础设施失败不改变已经完成的购物车操作", async () => {
  const harness = connected(populatedCart(), {
    auditInfrastructureFails: true,
  });
  const lineId = harness.dependencies.cart.getSnapshot().lines[0]!.lineId;

  await harness.dependencies.cart.setLineQuantity(lineId, 4);

  assert.equal(
    harness.dependencies.cart.getSnapshot().lines[0]?.quantity,
    "4",
  );
});

test("值未改变时不写购物车变更审计，与 WPF HasChanged 规则一致", async () => {
  const harness = connected(populatedCart());
  const lineId = harness.dependencies.cart.getSnapshot().lines[0]!.lineId;

  await harness.dependencies.cart.setLineQuantity(lineId, 1);

  assert.equal(harness.audits.length, 0);
});

test("在线同身份校准使用 system 上下文写价格事件，且不再次请求主管授权", async () => {
  const harness = connected(new PricingCart(), {
    revalidationResult: {
      kind: "found",
      baseSnapshotId: "snapshot-1",
      item: {
        ...CATALOG_ITEM,
        itemNumber: "NEW-1001",
        displayName: "Fresh tea",
        retailPriceCents: 725,
        priceSource: 2,
      },
    },
  });

  await harness.dependencies.workflow.addByLookupCode("930000000001");
  await waitFor(
    () =>
      harness.dependencies.workflow.getPendingCatalogWorkCount() === 0,
  );

  assert.deepEqual(
    harness.authorizationRequests.map((request) => request.action),
    ["scan-add-item"],
  );
  assert.deepEqual(
    harness.audits.map((event) => event.eventType),
    ["CART_ITEM_ADD", "CART_ITEM_PRICE_CHANGE"],
  );
  assert.deepEqual(
    {
      action: harness.audits[1]?.payload.action,
      permissionCode: harness.audits[1]?.payload.permissionCode,
      authorizationMode:
        harness.audits[1]?.payload.authorizationMode,
      requestingCashierId:
        harness.audits[1]?.payload.requestingCashierId,
      authorizingCashierId:
        harness.audits[1]?.payload.authorizingCashierId,
    },
    {
      action: "catalog-revalidation",
      permissionCode: "Permissions.PosTerminal.Sales.AddItem",
      authorizationMode: "system",
      requestingCashierId: "CASHIER-1",
      authorizingCashierId: null,
    },
  );
  assert.equal(harness.audits[0]?.payload.afterActualCents, 500);
  assert.equal(harness.audits[1]?.payload.beforeActualCents, 500);
  assert.equal(harness.audits[1]?.payload.afterActualCents, 725);
  assert.equal(
    harness.dependencies.cart.getSnapshot().lines[0]?.unitPrice.cents,
    725,
  );
});

test("校准未命中、身份变化或没有差异时不写价格事件，本地 miss 自动加购仍只写 ADD", async () => {
  const cases: CatalogLookupRevalidationResult[] = [
    {
      kind: "not-found",
      baseSnapshotId: "snapshot-1",
    },
    {
      kind: "found",
      baseSnapshotId: "snapshot-1",
      item: {
        ...CATALOG_ITEM,
        productCode: "SKU-REPLACED",
        referenceCode: "REF-REPLACED",
      },
    },
    {
      kind: "found",
      baseSnapshotId: "snapshot-1",
      item: CATALOG_ITEM,
    },
  ];
  for (const revalidationResult of cases) {
    const harness = connected(new PricingCart(), {
      revalidationResult,
    });
    await harness.dependencies.workflow.addByLookupCode(
      "930000000001",
    );
    await waitFor(
      () =>
        harness.dependencies.workflow.getPendingCatalogWorkCount() ===
        0,
    );
    assert.deepEqual(
      harness.audits.map((event) => event.eventType),
      ["CART_ITEM_ADD"],
    );
  }

  const localMiss = connected(new PricingCart(), {
    localCatalogItem: null,
    revalidationResult: {
      kind: "found",
      baseSnapshotId: "snapshot-1",
      item: CATALOG_ITEM,
    },
  });
  await localMiss.dependencies.workflow.addByLookupCode(
    "930000000001",
  );
  await waitFor(
    () =>
      localMiss.dependencies.workflow.getPendingCatalogWorkCount() ===
      0,
  );
  assert.deepEqual(
    localMiss.audits.map((event) => event.eventType),
    ["CART_ITEM_ADD"],
  );
  assert.deepEqual(
    {
      action: localMiss.audits[0]?.payload.action,
      authorizationMode:
        localMiss.audits[0]?.payload.authorizationMode,
      permissionCode: localMiss.audits[0]?.payload.permissionCode,
    },
    {
      action: "catalog-revalidation-auto-add",
      authorizationMode: "system",
      permissionCode: "Permissions.PosTerminal.Sales.AddItem",
    },
  );
});

test("system 校准的审计仓储、ID 或时钟失败不回滚已更新购物车", async () => {
  for (const failure of [
    { auditFails: true },
    { auditInfrastructureFails: true },
    { systemActionIdFails: true },
    { auditIdFails: true },
    { auditClockFails: true },
  ]) {
    const harness = connected(new PricingCart(), {
      ...failure,
      revalidationResult: {
        kind: "found",
        baseSnapshotId: "snapshot-1",
        item: {
          ...CATALOG_ITEM,
          retailPriceCents: 825,
        },
      },
    });
    await harness.dependencies.workflow.addByLookupCode(
      "930000000001",
    );
    await waitFor(
      () =>
        harness.dependencies.workflow.getPendingCatalogWorkCount() ===
        0,
    );
    assert.equal(
      harness.dependencies.cart.getSnapshot().lines[0]?.unitPrice.cents,
      825,
    );
  }
});

function connected(
  cart = new PricingCart(),
  options: Readonly<{
    deny?: boolean;
    auditFails?: boolean;
    auditInfrastructureFails?: boolean;
    systemActionIdFails?: boolean;
    auditIdFails?: boolean;
    auditClockFails?: boolean;
    localCatalogItem?: LocalCatalogMatch | null;
    revalidationResult?: CatalogLookupRevalidationResult;
  }> = {},
) {
  const authorizationRequests: {
    actionId: string;
    permissionCode: string;
    screen: string;
    action: string;
  }[] = [];
  const audits: AuditEventDraft[] = [];
  let nextId = 0;
  let actionIdCalls = 0;
  let nextLineId = 0;
  const dependencies = createConnectedSalesDependencies({
    activeCartSession: new ActivePricingCartSession(
      cart,
      () => new PricingCart(),
    ),
    catalog: {
      async findExact(lookupCode) {
        const normalized = lookupCode.trim().toUpperCase();
        if (normalized === OPEN_ITEM.lookupCodeNormalized) return OPEN_ITEM;
        if (normalized === CATALOG_ITEM.lookupCodeNormalized) {
          return options.localCatalogItem === undefined
            ? CATALOG_ITEM
            : options.localCatalogItem;
        }
        return null;
      },
      async searchByName(query) {
        return query.trim().toLowerCase() === "tea"
          ? [CATALOG_ITEM]
          : [];
      },
    },
    ...(options.revalidationResult === undefined
      ? {}
      : {
          catalogRevalidation: {
            async revalidate() {
              return options.revalidationResult!;
            },
            async isCurrentBaseSnapshot() {
              return true;
            },
          },
          catalogWorkScheduler: {
            async yieldToUi() {},
            waitForTimeout: () =>
              new Promise<void>(() => undefined),
          },
        }),
    cashCheckout: undefined,
    identity: {
      storeCode: "STORE-1",
      deviceCode: "IPAD-1",
      cashierId: "CASHIER-1",
      cashierName: "Alice",
      userGuid: "USER-1",
    },
    sessionGuard: { assertActive() {} },
    newTransactionGate: {
      canStartNewTransaction: () => true,
    },
    createCheckoutIntentId: () => uuid(++nextId),
    createLineId: () => `line-${++nextLineId}`,
    operationSecurity: {
      authorization: {
        async authorizeAndRun(input, operation) {
          authorizationRequests.push(input);
          if (options.deny) {
            return {
              authorized: false as const,
              reason: "PERMISSION_DENIED",
            };
          }
          return {
            authorized: true as const,
            value: await operation({
              authorizationMode: "current-cashier",
              requestingCashierId: "CASHIER-1",
              authorizingCashierId: null,
              permissionCode: input.permissionCode,
            }),
          };
        },
      },
      audit: {
        async append(events) {
          if (options.auditFails) throw new Error("disk unavailable");
          audits.push(...events);
        },
      },
      createActionId: () => {
        actionIdCalls += 1;
        if (options.systemActionIdFails && actionIdCalls > 1) {
          throw new Error("system action id unavailable");
        }
        return uuid(++nextId);
      },
      createAuditEventId: () => {
        if (
          options.auditInfrastructureFails ||
          options.auditIdFails
        ) {
          throw new Error("audit id unavailable");
        }
        return uuid(++nextId);
      },
      nowIso: () =>
        options.auditInfrastructureFails || options.auditClockFails
          ? "invalid-clock"
          : "2026-07-29T00:00:00.000Z",
    },
  });
  return { dependencies, authorizationRequests, audits };
}

function populatedCart(): PricingCart {
  const cart = new PricingCart();
  cart.addItem({
    lineId: "line-existing",
    productCode: CATALOG_ITEM.productCode,
    itemNumber: CATALOG_ITEM.itemNumber,
    lookupCode: CATALOG_ITEM.lookupCode,
    displayName: CATALOG_ITEM.displayName,
    unitPrice: { currency: "AUD", cents: CATALOG_ITEM.retailPriceCents },
    syncProvenance: {
      referenceCode: CATALOG_ITEM.referenceCode,
      priceSource: CATALOG_ITEM.priceSource,
    },
  });
  return cart;
}

function uuid(value: number): string {
  return `00000000-0000-4000-8000-${String(value).padStart(12, "0")}`;
}

function hasCode(code: string): (error: unknown) => boolean {
  return (error) =>
    typeof error === "object" &&
    error !== null &&
    "code" in error &&
    error.code === code;
}

async function waitFor(predicate: () => boolean): Promise<void> {
  for (let attempts = 0; attempts < 50; attempts += 1) {
    if (predicate()) return;
    await new Promise<void>((resolve) => setImmediate(resolve));
  }
  throw new Error("condition was not reached");
}
