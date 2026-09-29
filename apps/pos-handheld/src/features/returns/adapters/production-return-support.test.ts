import assert from "node:assert/strict";
import test from "node:test";


import type { DurableReturnLine, TrustedReturnIdentity } from "@hb/pos-domain/features/returns/adapters/durable-return-execution-orchestrator";
import {
  CanonicalReturnFingerprint,
  CatalogLocalReturnAdapter,
  DurableCapacityVaultAdapter,
  OrderRepositoryLocalReturnLookup,
  ReturnLineMaterialCache,
} from "./production-return-support";
import type { ProtectedTenderCapacityMaterial } from "./return-lookup-adapter";

import type { LocalOrder } from "@hb/pos-domain/core/contracts/order";
import type { OrderRepositoryPort } from "@hb/pos-domain/core/contracts/repositories";

const orderGuid = "11111111-1111-4111-8111-111111111111";
const identity: TrustedReturnIdentity = {
  storeCode: "S01", deviceCode: "IPAD-1", cashierId: "cashier", cashierName: "Cashier", sessionEpoch: "epoch-1",
};

test("本地订单只接受同门店已完成销售单，且只封存公开可验证的现金和 SQ reference", async () => {
  const repository = new FakeOrders([
    makeOrder({ state: "Draft", localSequence: 7 }),
    makeOrder({ storeCode: "OTHER", localSequence: 6 }),
    makeOrder({ originalOrderGuid: orderGuid, localSequence: 5 }),
    makeOrder({ state: "PendingSync" }),
  ]);
  const lookup = new OrderRepositoryLocalReturnLookup(repository);

  const snapshot = await lookup.findSameStore({ storeCode: "s01", query: "4" });

  assert.equal(snapshot?.originalOrderGuid, orderGuid);
  assert.deepEqual(snapshot?.lines[0], {
    selectionKey: `local-receipt-line:${orderGuid}:detail-1`,
    originalOrderGuid: orderGuid,
    originalOrderDetailGuid: "detail-1",
    returnSourceKey: `receipt:${orderGuid}:detail-1`,
    productCode: "P1", itemNumber: "I1", lookupCode: "P1", displayName: "Product", availableQuantity: 2,
    unitRefundCents: 250, remainingAmountCents: 500,
    syncProvenance: {
      referenceCode: "LOCAL-REF",
      priceSource: 2,
    },
  });
  assert.deepEqual(snapshot?.capacities.map((item) => ({ method: item.method, reference: item.protectedProviderMaterial.reference })), [
    { method: "cash", reference: null },
    { method: "card", reference: "SQ:payment-id" },
  ]);
  assert.equal(JSON.stringify(snapshot).includes("RFN-SECRET"), false);
  assert.equal(JSON.stringify(snapshot).includes("VOUCHER-SECRET"), false);
  assert.equal(repository.getByGuidCalls, 0);
});

test("本地小票旧订单缺失冻结来源时失败关闭，不按当前目录补猜", async () => {
  const original = makeOrder();
  const {
    syncProvenance: _legacyMissingProvenance,
    ...legacyLine
  } = original.lines[0]!;
  const repository = new FakeOrders([
    makeOrder({
      lines: [legacyLine],
    }),
  ]);
  const lookup = new OrderRepositoryLocalReturnLookup(repository);

  await assert.rejects(
    () =>
      lookup.findSameStore({
        storeCode: "S01",
        query: orderGuid,
      }),
  );
});

test("整行一分优惠按远端四舍五入单价及尾差提供本地退货", async () => {
  const sale = makeOrder();
  const lookup = new OrderRepositoryLocalReturnLookup(new FakeOrders([makeOrder({
    lines: [{ ...sale.lines[0]!, quantity: "2", actualAmount: money(1999), discount: money(1) }],
    actualAmount: money(1999),
  })]));
  const receipt = await lookup.findSameStore({ storeCode: "S01", query: orderGuid });
  assert.equal(receipt?.lines[0]?.availableQuantity, 2);
  assert.equal(receipt?.lines[0]?.unitRefundCents, 1000);
  assert.equal(receipt?.lines[0]?.remainingAmountCents, 1999);
});

test("GUID 查询优先 getByGuid，非 returnable 或跨店命中均 fail closed", async () => {
  const repository = new FakeOrders([makeOrder({ storeCode: "OTHER" })]);
  const lookup = new OrderRepositoryLocalReturnLookup(repository);
  assert.equal(await lookup.findSameStore({ storeCode: "S01", query: orderGuid }), null);
  assert.equal(repository.getByGuidCalls, 1);
});

test("本地目录精确与搜索都重新限定门店并限制搜索量", async () => {
  const calls: unknown[] = [];
  const catalog = new CatalogLocalReturnAdapter({
    async findExact() { return catalogMatch("OTHER"); },
    async searchByName(_query: string, limit: number) {
      calls.push(limit);
      return [catalogMatch("S01"), catalogMatch("OTHER")];
    },
  });
  assert.deepEqual(await catalog.findExactMatches({ storeCode: "S01", query: "P1" }), []);
  assert.deepEqual(await catalog.search({ storeCode: "S01", query: "product", limit: 999 }), [{
    storeCode: "S01", productCode: "P1", itemNumber: "I1", lookupCode: "P1", displayName: "Product", retailPriceCents: 500,
    syncProvenance: {
      referenceCode: "CATALOG-REF",
      priceSource: 3,
    },
  }]);
  assert.deepEqual(calls, [32]);
});

test("Vault 全量验证并加密最小上下文；任一 seed 失败不泄露部分公开 handle", async () => {
  const seeded: unknown[] = [];
  const vault = new DurableCapacityVaultAdapter({
    vault: { async seedOrLoad(seed) { seeded.push(seed); if (seed.capacityId === "capacity-3") throw new Error("disk"); return seed; } },
    createOpaqueId: (() => { let value = 0; return () => `capacity-${++value}`; })(),
    nowIso: () => "2026-07-28T00:00:00.000Z",
  });
  const materials = [cashMaterial(), squareMaterial()];
  await assert.rejects(() => vault.protect({ storeCode: "S01", originalOrderGuid: orderGuid, loadedFrom: "local", capacities: materials }));
  assert.equal(seeded.length, 2);
  assert.deepEqual(seeded[0], {
    capacityId: "capacity-1", originalOrderGuid: orderGuid, method: "cash", originalAmountCents: 100, remainingAmountCents: 100,
    protectedContext: null, observedAtIso: "2026-07-28T00:00:00.000Z",
  });
  assert.deepEqual(seeded[1], {
    capacityId: "capacity-3", originalOrderGuid: orderGuid, method: "card", originalAmountCents: 400, remainingAmountCents: 400,
    protectedContext: { version: 1, provider: "square", paymentId: "payment-id" }, observedAtIso: "2026-07-28T00:00:00.000Z",
  });
  await assert.rejects(() => vault.protect({ storeCode: "S01", originalOrderGuid: orderGuid, loadedFrom: "local", capacities: [squareMaterial(), squareMaterial()] }));
});

test("远端 Linkly capacity 仅保存 RFN 与原始 ANZ reference，拒绝缺失或歧义 RFN", async () => {
  const seeded: unknown[] = [];
  const vault = createVault(seeded);
  const handles = await vault.protect({
    storeCode: "S01", originalOrderGuid: orderGuid, loadedFrom: "remote", capacities: [linklyMaterial()],
  });
  assert.equal(handles[0]?.offlineCashEvidenceId, null);
  assert.deepEqual(seeded[0], {
    capacityId: "capacity-1", originalOrderGuid: orderGuid, method: "card", originalAmountCents: 400, remainingAmountCents: 400,
    protectedContext: { version: 1, provider: "linkly-cloud", rfn: "RFN-1", originalReference: "ANZCLOUD:original-1" },
    observedAtIso: "2026-07-28T00:00:00.000Z",
  });
  assert.equal(JSON.stringify(seeded[0]).includes("AUTH-SECRET"), false);
  assert.equal(JSON.stringify(seeded[0]).includes("411111"), false);
  assert.equal(JSON.stringify(seeded[0]).includes("receipt secret"), false);

  await assert.rejects(() => createVault([]).protect({
    storeCode: "S01", originalOrderGuid: orderGuid, loadedFrom: "remote", capacities: [linklyMaterial({ cardTransactions: [linklyTransaction(null)] })],
  }));
  await assert.rejects(() => createVault([]).protect({
    storeCode: "S01", originalOrderGuid: orderGuid, loadedFrom: "remote", capacities: [linklyMaterial({
      cardTransactions: [linklyTransaction("RFN-1"), linklyTransaction("RFN-2")],
    })],
  }));
  await assert.rejects(() => createVault([]).protect({
    storeCode: "S01", originalOrderGuid: orderGuid, loadedFrom: "remote", capacities: [linklyMaterial({
      cardTransactions: [{ ...linklyTransaction("RFN-1"), processor: "Square" }],
    })],
  }));
});

test("手动刷卡原付款只登记代替退款额度，不生成集成刷卡机退款凭据", async () => {
  const seeded: unknown[] = [];
  const handles = await createVault(seeded).protect({
    storeCode: "S01", originalOrderGuid: orderGuid, loadedFrom: "remote", capacities: [{
      sourceKey: "manual-remote", method: "card", originalOrderGuid: orderGuid, remainingCents: 400,
      protectedProviderMaterial: { reference: "MANUAL:attempt-1", cardTransactions: [{ ...linklyTransaction(null), processor: "Manual" }] },
    }],
  });
  assert.equal(handles.length, 1);
  assert.equal(handles[0]?.remainingCents, 400);
  // 执行层对 manual-card context 一律拒绝原路退款；引用与交易号不进入 Vault。
  assert.deepEqual((seeded[0] as { protectedContext: unknown }).protectedContext, { version: 1, provider: "manual-card" });
  assert.equal(JSON.stringify(seeded[0]).includes("attempt-1"), false);
});

test("远端 voucher capacity 允许保护，但 context 不含原券引用", async () => {
  const seeded: unknown[] = [];
  const handles = await createVault(seeded).protect({
    storeCode: "S01", originalOrderGuid: orderGuid, loadedFrom: "remote", capacities: [{
      sourceKey: "voucher-remote", method: "voucher", originalOrderGuid: orderGuid, remainingCents: 400,
      protectedProviderMaterial: { reference: "VOUCHER-SECRET", cardTransactions: [] },
    }],
  });
  assert.equal(handles[0]?.offlineCashEvidenceId, null);
  assert.deepEqual((seeded[0] as { protectedContext: unknown }).protectedContext, { version: 1, provider: "voucher" });
  assert.equal(JSON.stringify(seeded[0]).includes("VOUCHER-SECRET"), false);
});

test("行材料必须绑定同一 workflow、action、身份 epoch 和精确 plan", async () => {
  const cache = new ReturnLineMaterialCache();
  const line = durableLine();
  const plan = receiptPlan();
  cache.record({ workflowId: "workflow-1", identity, lines: [line] });
  cache.bindAction({ workflowId: "workflow-1", actionId: "action-1", identity, plan });
  assert.deepEqual(await cache.resolveForAction({ actionId: "action-1", identity, plan }), [line]);
  await assert.rejects(() => cache.resolveForAction({ actionId: "action-1", identity: { ...identity, sessionEpoch: "new" }, plan }));
  await assert.rejects(() => new ReturnLineMaterialCache().resolveForAction({ actionId: "action-1", identity, plan }));
});

test("指纹使用固定 canonical 材料，不包含主管密钥或 provider reference", async () => {
  let material = "";
  const fingerprint = new CanonicalReturnFingerprint(async (input) => { material = input; return "digest"; });
  const result = await fingerprint.digest({
    command: { actionId: "action-1", plan: receiptPlan(), noReceiptAuthorizationKey: "SUPERVISOR-SECRET" },
    identity,
    lines: [durableLine()],
  });
  assert.equal(result, "digest");
  assert.equal(material.includes("SUPERVISOR-SECRET"), false);
  assert.equal(material.includes("SQ:payment-id"), false);
  assert.deepEqual(
    JSON.parse(material).lines[0].syncProvenance,
    {
      referenceCode: "RETURN-REF",
      priceSource: 4,
    },
  );
  assert.equal(material, JSON.stringify(JSON.parse(material)));
});

test("行材料拒绝非法同步来源，不能在 fingerprint 或重放时静默丢弃", () => {
  const cache = new ReturnLineMaterialCache();
  assert.throws(() =>
    cache.record({
      workflowId: "workflow-invalid-provenance",
      identity,
      lines: [
        {
          ...durableLine(),
          syncProvenance: {
            referenceCode: " ",
            priceSource: 0,
          },
        },
      ],
    }),
  );
});

class FakeOrders implements Pick<OrderRepositoryPort, "getByGuid" | "listLocal"> {
  public getByGuidCalls = 0;
  public constructor(private readonly orders: readonly LocalOrder[]) {}
  public async getByGuid(guid: string): Promise<LocalOrder | null> { this.getByGuidCalls += 1; return this.orders.find((order) => order.orderGuid === guid) ?? null; }
  public async listLocal(_limit: number, before?: number): Promise<readonly LocalOrder[]> { return this.orders.filter((order) => before === undefined || order.localSequence < before); }
}

function makeOrder(overrides: Partial<LocalOrder> = {}): LocalOrder {
  return {
    orderGuid, localSequence: 4, storeCode: "S01", deviceCode: "IPAD-1", cashierId: "c", cashierName: "C", soldAtIso: "2026-07-28T00:00:00.000Z",
    state: "Synced", total: money(500), discount: money(0), actualAmount: money(500), originalOrderGuid: null,
    lines: [{ lineId: "detail-1", productCode: "P1", itemNumber: "I1", lookupCode: "P1", displayName: "Product", quantity: "2", unitPrice: money(250), discount: money(0), actualAmount: money(500), priceSource: "catalog", syncProvenance: { referenceCode: "LOCAL-REF", priceSource: 2 }, kind: "sale", returnSourceKey: null, originalOrderGuid: null, originalOrderDetailGuid: null }],
    tenders: [
      { tenderGuid: "cash-1", method: "cash", amount: money(100), reference: null, reservationToken: null },
      { tenderGuid: "square-1", method: "card", amount: money(400), reference: "SQ:payment-id", reservationToken: null },
      { tenderGuid: "linkly-1", method: "card", amount: money(1), reference: "RFN-SECRET", reservationToken: null },
      { tenderGuid: "voucher-1", method: "voucher", amount: money(1), reference: "VOUCHER-SECRET", reservationToken: "reservation" },
    ],
    ...overrides,
  };
}

function money(cents: number) { return { currency: "AUD" as const, cents }; }
function catalogMatch(storeCode: string) { return { storeCode, productCode: "P1", referenceCode: "CATALOG-REF", itemNumber: "I1", displayName: "Product", barcode: null, lookupCode: "P1", lookupCodeNormalized: "P1", retailPriceCents: 500, priceSource: 3 as const, priceSourceLabel: "catalog", quantityFactor: 1, taxRateBasisPoints: null, updatedAtIso: null, rowVersion: null, productImage: null, discountRate: null, isSpecialProduct: false }; }
function cashMaterial(): ProtectedTenderCapacityMaterial { return { sourceKey: "cash-1", method: "cash", originalOrderGuid: orderGuid, remainingCents: 100, protectedProviderMaterial: { reference: null, cardTransactions: [] } }; }
function squareMaterial(): ProtectedTenderCapacityMaterial { return { sourceKey: "square-1", method: "card", originalOrderGuid: orderGuid, remainingCents: 400, protectedProviderMaterial: { reference: "SQ:payment-id", cardTransactions: [] } }; }
function linklyMaterial(overrides: Partial<ProtectedTenderCapacityMaterial["protectedProviderMaterial"]> = {}): ProtectedTenderCapacityMaterial { return { sourceKey: "linkly-remote", method: "card", originalOrderGuid: orderGuid, remainingCents: 400, protectedProviderMaterial: { reference: "ANZCLOUD:original-1", cardTransactions: [linklyTransaction("RFN-1")], ...overrides } }; }
function linklyTransaction(refundReference: string | null) { return { processor: "Linkly Cloud", txnRef: "TXN-1", refundReference, authCode: "AUTH-SECRET", maskedCardNumber: "****411111", receiptText: "receipt secret" }; }
function createVault(seeded: unknown[]) { return new DurableCapacityVaultAdapter({ vault: { async seedOrLoad(seed) { seeded.push(seed); return seed; } }, createOpaqueId: (() => { let value = 0; return () => `capacity-${++value}`; })(), nowIso: () => "2026-07-28T00:00:00.000Z" }); }
function durableLine(): DurableReturnLine { return { lineId: "return-line-1", selectionKey: "selection-1", sourceKind: "receipt", returnSourceKey: "source-1", originalOrderGuid: orderGuid, originalOrderDetailGuid: "detail-1", productCode: "P1", itemNumber: "I1", lookupCode: "P1", displayName: "Product", quantity: 1, unitRefundCents: 500, signedAmountCents: -500, availableQuantity: 2, remainingAmountCents: 500, syncProvenance: { referenceCode: "RETURN-REF", priceSource: 4 } } as DurableReturnLine; }
function receiptPlan() { return { sourceKind: "receipt" as const, totalRefundCents: 500, lines: [{ sourceKind: "receipt" as const, returnSourceKey: "source-1", originalOrderGuid: orderGuid, originalOrderDetailGuid: "detail-1", productCode: "P1", quantity: 1, signedAmountCents: -500, syncProvenance: { referenceCode: "RETURN-REF", priceSource: 4 as const } }], allocations: [], online: false }; }
