import assert from "node:assert/strict";
import test from "node:test";

import { ReturnFeatureError } from "@hb/pos-domain/features/returns/return-domain";

import {
  ReturnLookupAdapter,
  decimalAmountToCents,
  type LocalReceiptReturnSnapshot,
  type LocalReturnCatalogItem,
  type LocalReturnCatalogPort,
  type LocalReturnOrderLookupPort,
  type ProtectedReturnCapacityHandle,
  type ReturnCapacityVaultInput,
  type ReturnCapacityVaultPort,
  type ReturnHistoryApiPort,
  type ReturnHistorySearchInput,
} from "./return-lookup-adapter";

import { HbposApiError } from "@/core/api/hbpos-api";
import type { components } from "@hb/pos-api-client/openapi";

type OrderHistoryQueryResponse =
  components["schemas"]["OrderHistoryQueryResponse"];
type OrderReturnContextDto =
  components["schemas"]["OrderReturnContextDto"];

const orderGuid = "11111111-1111-4111-8111-111111111111";
const lineGuid = "22222222-2222-4222-8222-222222222222";

test("金额按 API decimal 精确转换为分，拒绝半分和非有限值", () => {
  assert.equal(decimalAmountToCents(10.01), 1_001);
  assert.equal(decimalAmountToCents(-2.35), -235);
  assert.equal(decimalAmountToCents(0.1 + 0.2), 30);
  assert.throws(
    () => decimalAmountToCents(1.005),
    hasReturnCode("RETURN_SOURCE_MISMATCH"),
  );
  assert.throws(
    () => decimalAmountToCents(Number.NaN),
    hasReturnCode("RETURN_SOURCE_MISMATCH"),
  );
});

test("关键字严格走 history → 同门店 orderGuid → return-context，并先封存 provider 引用", async () => {
  const remote = new FakeHistoryApi();
  remote.searchResult = {
    orders: [
      {
        orderGuid,
        storeCode: "S01",
      },
    ],
  };
  remote.contextResult = remoteContext();
  const vault = new RecordingVault();
  const adapter = createAdapter({ historyApi: remote, capacityVault: vault });

  const context = await adapter.lookupReceipt("receipt-9001");

  assert.deepEqual(remote.searchInputs, [
    { storeCode: "S01", keyword: "receipt-9001", take: 1 },
  ]);
  assert.deepEqual(remote.contextInputs, [orderGuid]);
  assert.equal(vault.inputs.length, 1);
  assert.equal(
    vault.inputs[0]?.capacities[1]?.protectedProviderMaterial.reference,
    "SQ:payment-secret",
  );
  assert.equal(context?.loadedFrom, "remote");
  assert.equal(context?.returnRecordsMayBeStale, false);
  assert.equal(context?.lines[0]?.availableQuantity, 2);
  assert.equal(context?.lines[0]?.unitRefundCents, 334);
  assert.equal(context?.lines[0]?.remainingAmountCents, 667);
  assert.deepEqual(
    Reflect.get(context?.lines[0] ?? {}, "syncProvenance"),
    {
      referenceCode: "REMOTE-REF",
      priceSource: 0,
    },
  );
  assert.deepEqual(
    context?.tenderCapacities.map((capacity) => ({
      method: capacity.method,
      capacityId: capacity.capacityId,
      evidence: capacity.offlineCashProof?.evidenceId ?? null,
    })),
    [
      {
        method: "cash",
        capacityId: "opaque-remote-capacity:0",
        evidence: "cash-proof-remote-capacity:0",
      },
      {
        method: "card",
        capacityId: "opaque-remote-capacity:1",
        evidence: null,
      },
    ],
  );
  const publicJson = JSON.stringify(context);
  assert.equal(publicJson.includes("SQ:payment-secret"), false);
  assert.equal(publicJson.includes("AUTH-SECRET"), false);
  assert.equal(publicJson.includes("411111"), false);
});

test("远端稳定拒绝或跨门店结果绝不回退本地订单", async () => {
  const remote = new FakeHistoryApi();
  const local = new FakeLocalOrders();
  remote.searchError = new HbposApiError("forbidden", {
    kind: "http",
    status: 403,
  });
  const adapter = createAdapter({ historyApi: remote, localOrders: local });

  await assert.rejects(() => adapter.lookupReceipt("abc"), /forbidden/u);
  assert.equal(local.calls.length, 0);

  remote.searchError = null;
  remote.searchResult = {
    orders: [{ orderGuid, storeCode: "OTHER" }],
  };
  assert.equal(await adapter.lookupReceipt("abc"), null);
  assert.equal(local.calls.length, 0);

  remote.searchResult = {
    orders: [{ orderGuid, storeCode: "S01" }],
  };
  remote.contextResult = {
    ...remoteContext(),
    order: {
      ...remoteContext().order,
      storeCode: "OTHER",
    },
  };
  await assert.rejects(
    () => adapter.lookupReceipt("abc"),
    hasReturnCode("RETURN_SOURCE_MISMATCH"),
  );
  assert.equal(local.calls.length, 0);
});

test("公开退款上下文使用 Vault 复用后余额及现金证明余额", async () => {
  const remote = new FakeHistoryApi();
  remote.contextResult = remoteContext();
  const adapter = createAdapter({
    historyApi: remote,
    capacityVault: {
      async protect(input) {
        return input.capacities.map((capacity) => ({
          sourceKey: capacity.sourceKey,
          capacityId: `opaque-${capacity.sourceKey}`,
          remainingCents: 0,
          offlineCashEvidenceId: capacity.method === "cash" ? `proof-${capacity.sourceKey}` : null,
        }));
      },
    },
  });
  const context = await adapter.lookupReceipt(orderGuid);
  assert.deepEqual(context?.tenderCapacities.map((capacity) => capacity.remainingCents), [0, 0]);
  assert.equal(context?.tenderCapacities[0]?.offlineCashProof?.remainingCents, 0);
});

test("手工刷卡原付款可查出，额度只允许现金代替；集成刷卡与现金额度不受影响", async () => {
  const remote = new FakeHistoryApi();
  const base = remoteContext();
  remote.contextResult = {
    ...base,
    paymentCapacities: [
      ...(base.paymentCapacities ?? []),
      {
        method: 2,
        originalAmount: 2.5,
        refundedAmount: 0,
        remainingAmount: 2.5,
        originalOrderGuid: orderGuid,
        reference: "MANUAL:0d4c2c7e9a8b4f7c8e6a5b4c3d2e1f00",
        cardTransactions: [{ processor: "Manual" }],
      },
    ],
  };
  const adapter = createAdapter({
    historyApi: remote,
    capacityVault: {
      async protect(input) {
        return input.capacities.map((capacity) => ({
          sourceKey: capacity.sourceKey,
          capacityId: `opaque-${capacity.sourceKey}`,
          remainingCents: capacity.remainingCents,
          offlineCashEvidenceId: capacity.method === "cash" ? `proof-${capacity.sourceKey}` : null,
        }));
      },
    },
  });
  const context = await adapter.lookupReceipt(orderGuid);
  assert.deepEqual(
    context?.tenderCapacities.map((capacity) => [
      capacity.method,
      capacity.remainingCents,
      capacity.substituteOnly === true,
    ]),
    [
      ["cash", 500, false],
      ["card", 501, false],
      ["card", 250, true],
    ],
  );
});

test("只有传输失败才回退同门店本地订单，并始终标记 stale", async () => {
  const remote = new FakeHistoryApi();
  remote.searchError = new HbposApiError("offline", { kind: "transport" });
  const local = new FakeLocalOrders();
  local.result = localSnapshot();
  const vault = new RecordingVault();
  const adapter = createAdapter({
    historyApi: remote,
    localOrders: local,
    capacityVault: vault,
  });

  const context = await adapter.lookupReceipt("receipt-local");

  assert.deepEqual(local.calls, [
    { storeCode: "S01", query: "receipt-local" },
  ]);
  assert.equal(context?.loadedFrom, "local");
  assert.equal(context?.returnRecordsMayBeStale, true);
  assert.equal(vault.inputs[0]?.loadedFrom, "local");
  assert.deepEqual(
    Reflect.get(context?.lines[0] ?? {}, "syncProvenance"),
    {
      referenceCode: "LOCAL-REF",
      priceSource: 2,
    },
  );

  local.result = { ...localSnapshot(), storeCode: "OTHER" };
  await assert.rejects(
    () => adapter.lookupReceipt("receipt-local"),
    hasReturnCode("RETURN_SOURCE_MISMATCH"),
  );

  local.result = null;
  await assert.rejects(() => adapter.lookupReceipt("receipt-local"), /offline/u);
});

test("本地小票回退拒绝缺失交易行同步来源，绝不查询当前目录补猜", async () => {
  const remote = new FakeHistoryApi();
  remote.searchError = new HbposApiError("offline", {
    kind: "transport",
  });
  const local = new FakeLocalOrders();
  const snapshot = localSnapshot();
  local.result = {
    ...snapshot,
    lines: [
      {
        ...snapshot.lines[0]!,
        syncProvenance: undefined,
      },
    ],
  } as unknown as LocalReceiptReturnSnapshot;
  const adapter = createAdapter({
    historyApi: remote,
    localOrders: local,
  });

  await assert.rejects(
    () => adapter.lookupReceipt("receipt-local"),
    hasReturnCode("RETURN_SOURCE_MISMATCH"),
  );
});

test("无小票商品只查同门店本地目录，OPENITEM 必须走唯一专用映射", async () => {
  const catalog = new FakeCatalog();
  catalog.exact = [];
  catalog.searchResult = [catalogItem()];
  let nextId = 0;
  const adapter = createAdapter({
    localCatalog: catalog,
    createOpaqueId: (kind) => `${kind}-${++nextId}`,
  });

  const item = await adapter.lookupNoReceiptProduct("milk");
  assert.equal(item?.productCode, "P100");
  assert.equal(item?.sourceKind, "no-receipt-product");
  assert.deepEqual(
    Reflect.get(item ?? {}, "syncProvenance"),
    {
      referenceCode: "CATALOG-REF",
      priceSource: 3,
    },
  );
  assert.deepEqual(catalog.searchInputs, [
    { storeCode: "S01", query: "milk", limit: 8 },
  ]);

  catalog.exact = [catalogItem({ lookupCode: "OPENITEM" })];
  const open = await adapter.createNoReceiptOpenItem({
    displayName: " Damaged item ",
    unitRefundCents: 1_299,
  });
  assert.equal(open?.sourceKind, "no-receipt-open-item");
  assert.equal(open?.lookupCode, "OPENITEM");
  assert.equal(open?.displayName, "Damaged item");
  assert.equal(open?.unitRefundCents, 1_299);
  assert.deepEqual(
    Reflect.get(open ?? {}, "syncProvenance"),
    {
      referenceCode: "CATALOG-REF",
      priceSource: 3,
    },
  );

  catalog.exact = [
    catalogItem({ lookupCode: "OPENITEM" }),
    catalogItem({ lookupCode: "OPENITEM", productCode: "P-OPEN-2" }),
  ];
  await assert.rejects(
    () =>
      adapter.createNoReceiptOpenItem({
        displayName: "duplicate",
        unitRefundCents: 100,
      }),
    hasReturnCode("RETURN_OPEN_ITEM_INVALID"),
  );

  catalog.exact = [
    catalogItem({ storeCode: "OTHER", lookupCode: "OPENITEM" }),
  ];
  await assert.rejects(
    () =>
      adapter.createNoReceiptOpenItem({
        displayName: "cross-store",
        unitRefundCents: 100,
      }),
    hasReturnCode("RETURN_SOURCE_MISMATCH"),
  );
});

test("Vault 未原子提交或返回敏感值作为 capacityId 时不产出公开上下文", async () => {
  const remote = new FakeHistoryApi();
  remote.searchResult = {
    orders: [{ orderGuid, storeCode: "S01" }],
  };
  remote.contextResult = remoteContext();
  const vault: ReturnCapacityVaultPort = {
    protect: async () => [
      {
        sourceKey: "remote-capacity:0",
        capacityId: "opaque-cash",
        remainingCents: 0,
        offlineCashEvidenceId: "proof",
      },
      {
        sourceKey: "remote-capacity:1",
        capacityId: "SQ:payment-secret",
        remainingCents: 0,
        offlineCashEvidenceId: null,
      },
    ],
  };
  const adapter = createAdapter({ historyApi: remote, capacityVault: vault });

  await assert.rejects(
    () => adapter.lookupReceipt("abc"),
    hasReturnCode("RETURN_SOURCE_MISMATCH"),
  );
});

function createAdapter(
  overrides: Partial<{
    historyApi: ReturnHistoryApiPort;
    localOrders: LocalReturnOrderLookupPort;
    localCatalog: LocalReturnCatalogPort;
    capacityVault: ReturnCapacityVaultPort;
    createOpaqueId(kind: "selection" | "source"): string;
  }> = {},
): ReturnLookupAdapter {
  return new ReturnLookupAdapter({
    storeCode: "S01",
    historyApi: overrides.historyApi ?? new FakeHistoryApi(),
    localOrders: overrides.localOrders ?? new FakeLocalOrders(),
    localCatalog: overrides.localCatalog ?? new FakeCatalog(),
    capacityVault: overrides.capacityVault ?? new RecordingVault(),
    createOpaqueId:
      overrides.createOpaqueId ?? ((kind) => `${kind}-opaque-default`),
  });
}

class FakeHistoryApi implements ReturnHistoryApiPort {
  public searchResult: OrderHistoryQueryResponse = { orders: [] };
  public contextResult: OrderReturnContextDto | null = null;
  public searchError: unknown = null;
  public readonly searchInputs: ReturnHistorySearchInput[] = [];
  public readonly contextInputs: string[] = [];

  public async search(
    input: ReturnHistorySearchInput,
  ): Promise<OrderHistoryQueryResponse> {
    this.searchInputs.push(input);
    if (this.searchError) throw this.searchError;
    return this.searchResult;
  }

  public async getReturnContext(
    input: string,
  ): Promise<OrderReturnContextDto | null> {
    this.contextInputs.push(input);
    if (this.searchError) throw this.searchError;
    return this.contextResult;
  }
}

class FakeLocalOrders implements LocalReturnOrderLookupPort {
  public result: LocalReceiptReturnSnapshot | null = null;
  public readonly calls: Readonly<{ storeCode: string; query: string }>[] = [];

  public async findSameStore(input: Readonly<{
    storeCode: string;
    query: string;
  }>): Promise<LocalReceiptReturnSnapshot | null> {
    this.calls.push(input);
    return this.result;
  }
}

class FakeCatalog implements LocalReturnCatalogPort {
  public exact: readonly LocalReturnCatalogItem[] = [];
  public searchResult: readonly LocalReturnCatalogItem[] = [];
  public readonly searchInputs: Readonly<{
    storeCode: string;
    query: string;
    limit: number;
  }>[] = [];

  public async findExactMatches(): Promise<
    readonly LocalReturnCatalogItem[]
  > {
    return this.exact;
  }

  public async search(input: Readonly<{
    storeCode: string;
    query: string;
    limit: number;
  }>): Promise<readonly LocalReturnCatalogItem[]> {
    this.searchInputs.push(input);
    return this.searchResult;
  }
}

class RecordingVault implements ReturnCapacityVaultPort {
  public readonly inputs: ReturnCapacityVaultInput[] = [];

  public async protect(
    input: ReturnCapacityVaultInput,
  ): Promise<readonly ProtectedReturnCapacityHandle[]> {
    this.inputs.push(input);
    return input.capacities.map((capacity) => ({
      sourceKey: capacity.sourceKey,
      capacityId: `opaque-${capacity.sourceKey}`,
      remainingCents: capacity.remainingCents,
      offlineCashEvidenceId:
        capacity.method === "cash"
          ? `cash-proof-${capacity.sourceKey}`
          : null,
    }));
  }
}

function remoteContext(): OrderReturnContextDto {
  return {
    order: {
      orderGuid,
      storeCode: "S01",
      deviceCode: "POS-1",
      cashierName: "Cashier",
      actualAmount: 10.01,
      lines: [
        {
          orderLineGuid: lineGuid,
          productCode: "P100",
          displayName: "Milk",
          lookupCode: "9300001",
          itemNumber: "100",
          quantity: 3,
          actualAmount: 10.01,
          kind: 1,
          referenceCode: "  REMOTE-REF  ",
        },
      ],
      payments: [],
    },
    returnRecords: [
      {
        originalOrderGuid: orderGuid,
        originalOrderDetailGuid: lineGuid,
        returnQuantity: 1,
        returnAmount: 3.34,
      },
    ],
    lineCapacities: [
      {
        originalOrderLineGuid: lineGuid,
        originalAmount: 10.01,
        returnedAmount: 3.34,
        remainingAmount: 6.67,
      },
    ],
    paymentCapacities: [
      {
        method: 1,
        originalAmount: 5,
        refundedAmount: 0,
        remainingAmount: 5,
        originalOrderGuid: orderGuid,
      },
      {
        method: 2,
        originalAmount: 5.01,
        refundedAmount: 0,
        remainingAmount: 5.01,
        originalOrderGuid: orderGuid,
        reference: "SQ:payment-secret",
        cardTransactions: [
          {
            processor: "Square",
            authCode: "AUTH-SECRET",
            maskedCardNumber: "****411111",
          },
        ],
      },
    ],
  };
}

function localSnapshot(): LocalReceiptReturnSnapshot {
  return {
    storeCode: "S01",
    originalOrderGuid: orderGuid,
    receiptLabel: orderGuid,
    lines: [
      {
        selectionKey: `receipt-line:${lineGuid}`,
        originalOrderGuid: orderGuid,
        originalOrderDetailGuid: lineGuid,
        returnSourceKey: `receipt:${orderGuid}:${lineGuid}`,
        productCode: "P100",
        itemNumber: "100",
        lookupCode: "100",
        displayName: "Milk",
        availableQuantity: 1,
        unitRefundCents: 500,
        remainingAmountCents: 500,
        syncProvenance: {
          referenceCode: "LOCAL-REF",
          priceSource: 2,
        },
      },
    ],
    capacities: [
      {
        sourceKey: "local-cash",
        method: "cash",
        originalOrderGuid: orderGuid,
        remainingCents: 500,
        protectedProviderMaterial: {
          reference: null,
          cardTransactions: [],
        },
      },
    ],
  };
}

function catalogItem(
  overrides: Partial<LocalReturnCatalogItem> = {},
): LocalReturnCatalogItem {
  return {
    storeCode: "S01",
    productCode: "P100",
    itemNumber: "100",
    lookupCode: "9300001",
    displayName: "Milk",
    retailPriceCents: 500,
    syncProvenance: {
      referenceCode: "CATALOG-REF",
      priceSource: 3,
    },
    ...overrides,
  } as LocalReturnCatalogItem;
}

function hasReturnCode(
  code: ReturnFeatureError["code"],
): (error: unknown) => boolean {
  return (error) =>
    error instanceof ReturnFeatureError && error.code === code;
}
