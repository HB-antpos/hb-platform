import {
  ReturnFeatureError,
  type NoReceiptReturnItem,
  type OriginalReturnTenderCapacity,
  type ReceiptReturnContext,
  type ReceiptReturnLine,
  type ReturnTenderMethod,
  type VoucherFundedRefundBasis,
} from "@hb/pos-domain/features/returns/return-domain";
import type { ReturnLookupPort } from "@hb/pos-domain/features/returns/return-workflow";

import {
  HbposApiError,
  type HbposEnvelope,
  type HbposTransport,
  unwrapHbposEnvelope,
} from "@/core/api/hbpos-api";
import {
  normalizeLineSyncProvenance,
  type LineSyncProvenance,
} from "@hb/pos-domain/core/contracts/line-sync-provenance";
import type { components } from "@hb/pos-api-client/openapi";

type OrderHistoryQueryResponse =
  components["schemas"]["OrderHistoryQueryResponse"];
type OrderReturnContextDto =
  components["schemas"]["OrderReturnContextDto"];
type OrderHistoryLineDto =
  components["schemas"]["OrderHistoryLineDto"];
type OrderReturnPaymentCapacityDto =
  components["schemas"]["OrderReturnPaymentCapacityDto"];
type CardTransactionDto = components["schemas"]["CardTransactionDto"];

export type ReturnHistorySearchInput = Readonly<{
  storeCode: string;
  keyword: string;
  take: number;
}>;

/** 远端端口只暴露查询合同；网络错误分类仍由 HbposApiError 保留。 */
export interface ReturnHistoryApiPort {
  search(input: ReturnHistorySearchInput): Promise<OrderHistoryQueryResponse>;
  getReturnContext(orderGuid: string): Promise<OrderReturnContextDto | null>;
}

export class HbposReturnHistoryApi implements ReturnHistoryApiPort {
  public constructor(private readonly transport: HbposTransport) {}

  public async search(
    input: ReturnHistorySearchInput,
  ): Promise<OrderHistoryQueryResponse> {
    const response = await this.transport.request<
      HbposEnvelope<OrderHistoryQueryResponse>
    >({
      method: "GET",
      url: "/api/v1/orders/history",
      params: {
        storeCode: input.storeCode,
        keyword: input.keyword,
        take: input.take,
      },
    });
    return unwrapHbposEnvelope(response.data);
  }

  public async getReturnContext(
    orderGuid: string,
  ): Promise<OrderReturnContextDto | null> {
    const response = await this.transport.request<
      HbposEnvelope<OrderReturnContextDto | null>
    >({
      method: "GET",
      url: `/api/v1/orders/history/${encodeURIComponent(orderGuid)}/return-context`,
    });
    return unwrapNullableEnvelope(response.data);
  }
}

export type ProtectedTenderCapacityMaterial = Readonly<{
  sourceKey: string;
  method: ReturnTenderMethod;
  originalOrderGuid: string;
  remainingCents: number;
  /**
   * 该字段只能交给加密 Vault，不能写入 ReceiptReturnContext、日志或 UI 状态。
   */
  protectedProviderMaterial: Readonly<{
    reference: string | null;
    cardTransactions: readonly CardTransactionDto[];
  }>;
}>;

export type ReturnCapacityVaultInput = Readonly<{
  storeCode: string;
  originalOrderGuid: string;
  loadedFrom: "remote" | "local";
  capacities: readonly ProtectedTenderCapacityMaterial[];
}>;

export type ProtectedReturnCapacityHandle = Readonly<{
  sourceKey: string;
  capacityId: string;
  /** Vault 已扣减后的真实余额。 */
  remainingCents: number;
  /** 仅现金容量允许生成可离线验证的一次性证据。 */
  offlineCashEvidenceId: string | null;
}>;

/**
 * 实现必须在一个受保护的耐久事务中保存整批原支付引用和现金证据。
 * 返回前若事务未提交，必须抛错，不能产出任何公开 capacityId。
 */
export interface ReturnCapacityVaultPort {
  protect(
    input: ReturnCapacityVaultInput,
  ): Promise<readonly ProtectedReturnCapacityHandle[]>;
}

export type LocalReceiptReturnSnapshot = Readonly<{
  storeCode: string;
  originalOrderGuid: string;
  receiptLabel: string;
  lines: readonly ReceiptReturnLine[];
  capacities: readonly ProtectedTenderCapacityMaterial[];
  /** 原单全部支付（含代金券）的原始付款额口径；缺省视为无法取得，不额外约束。 */
  voucherFundedBasis?: VoucherFundedRefundBasis | null;
}>;

export interface LocalReturnOrderLookupPort {
  findSameStore(input: Readonly<{
    storeCode: string;
    query: string;
  }>): Promise<LocalReceiptReturnSnapshot | null>;
}

export type LocalReturnCatalogItem = Readonly<{
  storeCode: string;
  productCode: string;
  itemNumber: string | null;
  lookupCode: string;
  displayName: string;
  retailPriceCents: number;
  syncProvenance: LineSyncProvenance;
}>;

/** Port 必须查询当前 active 本地目录快照，不能静默调用远端目录。 */
export interface LocalReturnCatalogPort {
  findExactMatches(input: Readonly<{
    storeCode: string;
    query: string;
  }>): Promise<readonly LocalReturnCatalogItem[]>;
  search(input: Readonly<{
    storeCode: string;
    query: string;
    limit: number;
  }>): Promise<readonly LocalReturnCatalogItem[]>;
}

export type ReturnLookupAdapterOptions = Readonly<{
  storeCode: string;
  historyApi: ReturnHistoryApiPort;
  localOrders: LocalReturnOrderLookupPort;
  localCatalog: LocalReturnCatalogPort;
  capacityVault: ReturnCapacityVaultPort;
  createOpaqueId(kind: "selection" | "source"): string;
}>;

/**
 * WPF 等价退货查询：
 * 关键字先解析 orderGuid，否则远端 history → return-context；
 * 仅传输故障允许回退同门店本地订单，并强制标记 return records 可能过期。
 */
export class ReturnLookupAdapter implements ReturnLookupPort {
  private readonly storeCode: string;

  public constructor(private readonly options: ReturnLookupAdapterOptions) {
    this.storeCode = requiredText(options.storeCode);
  }

  public async lookupReceipt(
    query: string,
  ): Promise<ReceiptReturnContext | null> {
    const normalizedQuery = query.trim();
    if (!normalizedQuery) {
      throw new ReturnFeatureError("RETURN_QUERY_REQUIRED");
    }

    try {
      const orderGuid = isGuid(normalizedQuery)
        ? normalizedQuery
        : await this.resolveRemoteOrderGuid(normalizedQuery);
      if (!orderGuid) return null;

      const remote = await this.options.historyApi.getReturnContext(orderGuid);
      if (!remote) return null;
      return this.mapRemoteContext(remote);
    } catch (error: unknown) {
      if (!isTransportFailure(error)) throw error;

      const local = await this.options.localOrders.findSameStore({
        storeCode: this.storeCode,
        query: normalizedQuery,
      });
      if (!local) throw error;
      return this.mapLocalContext(local);
    }
  }

  public async lookupNoReceiptProduct(
    query: string,
  ): Promise<NoReceiptReturnItem | null> {
    const normalized = query.trim();
    if (!normalized) return null;
    const exact = this.sameStoreCatalogItems(
      await this.options.localCatalog.findExactMatches({
        storeCode: this.storeCode,
        query: normalized,
      }),
    );
    const matches =
      exact.length > 0
        ? exact
        : this.sameStoreCatalogItems(
            await this.options.localCatalog.search({
              storeCode: this.storeCode,
              query: normalized,
              limit: 8,
            }),
          );
    const item = matches.find(
      (candidate) => candidate.lookupCode.trim().toUpperCase() !== "OPENITEM",
    );
    return item ? this.mapNoReceiptItem(item, "no-receipt-product") : null;
  }

  public async createNoReceiptOpenItem(input: Readonly<{
    displayName: string;
    unitRefundCents: number;
  }>): Promise<NoReceiptReturnItem | null> {
    const displayName = input.displayName.trim();
    assertPositiveCents(input.unitRefundCents);
    if (!displayName) {
      throw new ReturnFeatureError("RETURN_OPEN_ITEM_INVALID");
    }

    const matches = this.sameStoreCatalogItems(
      await this.options.localCatalog.findExactMatches({
        storeCode: this.storeCode,
        query: "OPENITEM",
      }),
    ).filter((item) => item.lookupCode.trim().toUpperCase() === "OPENITEM");
    if (matches.length === 0) return null;
    if (matches.length !== 1) {
      throw new ReturnFeatureError("RETURN_OPEN_ITEM_INVALID");
    }

    return {
      ...this.mapNoReceiptItem(matches[0]!, "no-receipt-open-item"),
      displayName,
      unitRefundCents: input.unitRefundCents,
    };
  }

  private async resolveRemoteOrderGuid(
    keyword: string,
  ): Promise<string | null> {
    const response = await this.options.historyApi.search({
      storeCode: this.storeCode,
      keyword,
      take: 1,
    });
    const summary = (response.orders ?? []).find(
      (candidate) =>
        sameStore(candidate.storeCode, this.storeCode) &&
        typeof candidate.orderGuid === "string" &&
        candidate.orderGuid.trim().length > 0,
    );
    return summary?.orderGuid?.trim() ?? null;
  }

  private async mapRemoteContext(
    context: OrderReturnContextDto,
  ): Promise<ReceiptReturnContext> {
    const order = context.order;
    if (
      !order ||
      !sameStore(order.storeCode, this.storeCode) ||
      !order.orderGuid
    ) {
      throw new ReturnFeatureError("RETURN_SOURCE_MISMATCH");
    }
    const originalOrderGuid = requiredText(order.orderGuid);
    const returnedByDetail = new Map<string, number>();
    for (const record of context.returnRecords ?? []) {
      if (
        record.originalOrderGuid &&
        record.originalOrderGuid !== originalOrderGuid
      ) {
        continue;
      }
      const detailGuid = record.originalOrderDetailGuid?.trim();
      if (!detailGuid) continue;
      const quantity = assertWholeQuantity(record.returnQuantity ?? 0, true);
      returnedByDetail.set(
        detailGuid,
        safeAdd(returnedByDetail.get(detailGuid) ?? 0, quantity),
      );
    }

    const remainingByDetail = new Map<string, number>();
    for (const capacity of context.lineCapacities ?? []) {
      const detailGuid = capacity.originalOrderLineGuid?.trim();
      if (!detailGuid) continue;
      remainingByDetail.set(
        detailGuid,
        decimalAmountToCents(capacity.remainingAmount ?? 0),
      );
    }

    const lines = (order.lines ?? [])
      .filter((line) => line.kind === undefined || line.kind === 1)
      .map((line) =>
        mapRemoteLine(
          originalOrderGuid,
          line,
          returnedByDetail,
          remainingByDetail,
        ),
      )
      .filter((line): line is ReceiptReturnLine => line !== null);
    // 必须在过滤剩余额为 0 的容量之前计算：已退完的代金券付款仍计入原单代金券占比。
    const voucherFundedBasis = remoteVoucherFundedBasis(
      originalOrderGuid,
      context.paymentCapacities ?? [],
    );
    const materials = this.mapRemoteTenderMaterials(
      originalOrderGuid,
      context.paymentCapacities ?? [],
    );
    const tenderCapacities = await this.protectCapacities(
      originalOrderGuid,
      "remote",
      materials,
    );

    return {
      originalOrderGuid,
      receiptLabel: originalOrderGuid,
      loadedFrom: "remote",
      returnRecordsMayBeStale: false,
      lines,
      tenderCapacities,
      voucherFundedBasis,
    };
  }

  private async mapLocalContext(
    snapshot: LocalReceiptReturnSnapshot,
  ): Promise<ReceiptReturnContext> {
    if (!sameStore(snapshot.storeCode, this.storeCode)) {
      throw new ReturnFeatureError("RETURN_SOURCE_MISMATCH");
    }
    const originalOrderGuid = requiredText(snapshot.originalOrderGuid);
    const lines = snapshot.lines.map((line) => ({
      ...line,
      syncProvenance: normalizeReturnLineSyncProvenance(
        line.syncProvenance,
      ),
    }));
    for (const line of lines) {
      if (line.originalOrderGuid !== originalOrderGuid) {
        throw new ReturnFeatureError("RETURN_SOURCE_MISMATCH");
      }
    }
    const tenderCapacities = await this.protectCapacities(
      originalOrderGuid,
      "local",
      snapshot.capacities,
    );
    return {
      originalOrderGuid,
      receiptLabel: requiredText(snapshot.receiptLabel),
      loadedFrom: "local",
      returnRecordsMayBeStale: true,
      lines,
      tenderCapacities,
      voucherFundedBasis: snapshot.voucherFundedBasis ?? null,
    };
  }

  private mapRemoteTenderMaterials(
    originalOrderGuid: string,
    capacities: readonly OrderReturnPaymentCapacityDto[],
  ): readonly ProtectedTenderCapacityMaterial[] {
    return capacities.flatMap((capacity, index) => {
      const remainingCents = decimalAmountToCents(
        capacity.remainingAmount ?? 0,
      );
      if (remainingCents <= 0) return [];
      if (
        capacity.originalOrderGuid &&
        capacity.originalOrderGuid !== originalOrderGuid
      ) {
        throw new ReturnFeatureError("RETURN_SOURCE_MISMATCH");
      }
      return [
        {
          sourceKey: `remote-capacity:${index}`,
          method: mapPaymentMethod(capacity.method),
          originalOrderGuid,
          remainingCents,
          protectedProviderMaterial: {
            reference: capacity.reference?.trim() || null,
            cardTransactions: capacity.cardTransactions ?? [],
          },
        },
      ];
    });
  }

  private async protectCapacities(
    originalOrderGuid: string,
    loadedFrom: "remote" | "local",
    materials: readonly ProtectedTenderCapacityMaterial[],
  ): Promise<readonly OriginalReturnTenderCapacity[]> {
    if (materials.length === 0) return [];
    for (const material of materials) {
      if (material.originalOrderGuid !== originalOrderGuid) {
        throw new ReturnFeatureError("RETURN_SOURCE_MISMATCH");
      }
      assertPositiveSourceCents(material.remainingCents);
    }

    // 公开上下文只能在此受保护耐久事务成功之后创建。
    const handles = await this.options.capacityVault.protect({
      storeCode: this.storeCode,
      originalOrderGuid,
      loadedFrom,
      capacities: materials,
    });
    const bySource = new Map(
      handles.map((handle) => [handle.sourceKey, handle] as const),
    );
    if (bySource.size !== materials.length || handles.length !== materials.length) {
      throw new ReturnFeatureError("RETURN_SOURCE_MISMATCH");
    }

    const protectedValues = collectProtectedValues(materials);
    const publicIds = new Set<string>();
    return materials.map((material) => {
      const handle = bySource.get(material.sourceKey);
      if (
        !handle ||
        !handle.capacityId.trim() ||
        !Number.isSafeInteger(handle.remainingCents) ||
        handle.remainingCents < 0 ||
        handle.remainingCents > material.remainingCents ||
        publicIds.has(handle.capacityId) ||
        protectedValues.has(handle.capacityId)
      ) {
        throw new ReturnFeatureError("RETURN_SOURCE_MISMATCH");
      }
      publicIds.add(handle.capacityId);
      if (handle.offlineCashEvidenceId && material.method !== "cash") {
        throw new ReturnFeatureError("RETURN_SOURCE_MISMATCH");
      }
      if (
        handle.offlineCashEvidenceId !== null &&
        !handle.offlineCashEvidenceId.trim()
      ) {
        throw new ReturnFeatureError("RETURN_SOURCE_MISMATCH");
      }
      return {
        capacityId: handle.capacityId,
        originalOrderGuid,
        method: material.method,
        remainingCents: handle.remainingCents,
        offlineCashProof:
          material.method === "cash" && handle.offlineCashEvidenceId
            ? {
                evidenceId: handle.offlineCashEvidenceId,
                capacityId: handle.capacityId,
                originalOrderGuid,
                remainingCents: handle.remainingCents,
              }
            : null,
        // 手工刷卡没有集成刷卡机退款凭据，只能现金或代金券代替退款。
        ...(isManualCardReturnMaterial(material)
          ? { substituteOnly: true }
          : {}),
      };
    });
  }

  private sameStoreCatalogItems(
    items: readonly LocalReturnCatalogItem[],
  ): readonly LocalReturnCatalogItem[] {
    if (items.some((item) => !sameStore(item.storeCode, this.storeCode))) {
      throw new ReturnFeatureError("RETURN_SOURCE_MISMATCH");
    }
    return items;
  }

  private mapNoReceiptItem(
    item: LocalReturnCatalogItem,
    sourceKind: NoReceiptReturnItem["sourceKind"],
  ): NoReceiptReturnItem {
    assertPositiveSourceCents(item.retailPriceCents);
    return {
      sourceKind,
      selectionKey: this.options.createOpaqueId("selection"),
      returnSourceKey: this.options.createOpaqueId("source"),
      productCode: requiredText(item.productCode),
      itemNumber: item.itemNumber?.trim() || null,
      lookupCode: requiredText(item.lookupCode),
      displayName: requiredText(item.displayName),
      unitRefundCents: item.retailPriceCents,
      syncProvenance: normalizeReturnLineSyncProvenance(
        item.syncProvenance,
      ),
    };
  }
}

export function decimalAmountToCents(value: number): number {
  if (!Number.isFinite(value)) {
    throw new ReturnFeatureError("RETURN_SOURCE_MISMATCH");
  }
  const scaled = value * 100;
  const rounded = Math.round(scaled);
  if (
    !Number.isSafeInteger(rounded) ||
    Math.abs(scaled - rounded) > 0.000_001
  ) {
    throw new ReturnFeatureError("RETURN_SOURCE_MISMATCH");
  }
  return rounded;
}

/**
 * 由原单各支付的原始付款额汇总代金券占比口径（与 WPF VoucherFundedRefundPolicy 一致）。
 * 原始付款额 ≤0 的支付不参与；任一金额非法或付款总额 ≤0 时返回 null（不额外约束）。
 */
export function buildVoucherFundedBasis(
  payments: readonly Readonly<{ isVoucher: boolean; originalCents: number }>[],
): VoucherFundedRefundBasis | null {
  let voucherOriginalCents = 0;
  let paidOriginalCents = 0;
  for (const payment of payments) {
    if (!Number.isSafeInteger(payment.originalCents)) return null;
    if (payment.originalCents <= 0) continue;
    paidOriginalCents += payment.originalCents;
    if (payment.isVoucher) voucherOriginalCents += payment.originalCents;
    if (!Number.isSafeInteger(paidOriginalCents)) return null;
  }
  if (paidOriginalCents <= 0) return null;
  return Object.freeze({ voucherOriginalCents, paidOriginalCents });
}

function remoteVoucherFundedBasis(
  originalOrderGuid: string,
  capacities: readonly OrderReturnPaymentCapacityDto[],
): VoucherFundedRefundBasis | null {
  const payments: { isVoucher: boolean; originalCents: number }[] = [];
  for (const capacity of capacities) {
    // 明确属于其他原单的支付不计入本单占比。
    if (
      capacity.originalOrderGuid &&
      capacity.originalOrderGuid !== originalOrderGuid
    ) {
      continue;
    }
    const originalAmount = capacity.originalAmount;
    if (typeof originalAmount !== "number") return null;
    let originalCents: number;
    try {
      originalCents = decimalAmountToCents(originalAmount);
    } catch {
      return null;
    }
    payments.push({ isVoucher: capacity.method === 3, originalCents });
  }
  return buildVoucherFundedBasis(payments);
}

function mapRemoteLine(
  originalOrderGuid: string,
  line: OrderHistoryLineDto,
  returnedByDetail: ReadonlyMap<string, number>,
  remainingByDetail: ReadonlyMap<string, number>,
): ReceiptReturnLine | null {
  const detailGuid = line.orderLineGuid?.trim();
  if (!detailGuid) {
    throw new ReturnFeatureError("RETURN_SOURCE_MISMATCH");
  }
  const originalQuantity = assertWholeQuantity(line.quantity ?? 0, false);
  const returnedQuantity = returnedByDetail.get(detailGuid) ?? 0;
  const availableQuantity = originalQuantity - returnedQuantity;
  if (!Number.isSafeInteger(availableQuantity) || availableQuantity <= 0) {
    return null;
  }

  const originalAmountCents = decimalAmountToCents(line.actualAmount ?? 0);
  if (originalAmountCents <= 0) return null;
  const unitRefundCents = roundRatioAwayFromZero(
    originalAmountCents,
    originalQuantity,
  );
  const remainingAmountCents =
    remainingByDetail.get(detailGuid) ??
    Math.max(
      0,
      originalAmountCents -
        safeMultiply(unitRefundCents, returnedQuantity),
    );
  if (remainingAmountCents <= 0) return null;

  // WPF ReceiptReturnOrderLineViewModel 对历史行固定使用 ProductBase(0)，
  // referenceCode 仅来自原单 DTO；这是兼容映射，绝不按当前目录反推售卖来源。
  const syncProvenance = normalizeReturnLineSyncProvenance({
    referenceCode: line.referenceCode?.trim() || null,
    priceSource: 0,
  });
  return {
    selectionKey: `receipt-line:${detailGuid}`,
    originalOrderGuid,
    originalOrderDetailGuid: detailGuid,
    returnSourceKey:
      line.returnSourceKey?.trim() ||
      `receipt:${originalOrderGuid}:${detailGuid}`,
    productCode: requiredText(line.productCode),
    itemNumber: line.itemNumber?.trim() || null,
    lookupCode: requiredText(line.lookupCode),
    displayName: requiredText(line.displayName),
    availableQuantity,
    unitRefundCents,
    remainingAmountCents,
    syncProvenance,
  };
}

function mapPaymentMethod(
  method: components["schemas"]["PaymentMethodKind"] | undefined,
): ReturnTenderMethod {
  if (method === 1) return "cash";
  if (method === 2) return "card";
  if (method === 3) return "voucher";
  throw new ReturnFeatureError("RETURN_SOURCE_MISMATCH");
}

function unwrapNullableEnvelope<T>(envelope: HbposEnvelope<T | null>): T | null {
  if (envelope.success !== true) {
    return unwrapHbposEnvelope(envelope);
  }
  return envelope.data ?? null;
}

const MANUAL_CARD_REFERENCE_PREFIX = "MANUAL:";
const MANUAL_CARD_PROCESSOR = "Manual";

/**
 * 与 WPF ManualCardPaymentReference.IsManualRefundSource 口径一致：
 * 独立刷卡机人工确认的刷卡（引用 MANUAL: 前缀或处理器 Manual）不能经集成刷卡机原路退回。
 */
export function isManualCardReturnMaterial(
  material: ProtectedTenderCapacityMaterial,
): boolean {
  if (material.method !== "card") return false;
  const reference = material.protectedProviderMaterial.reference?.trim() ?? "";
  return (
    reference.toUpperCase().startsWith(MANUAL_CARD_REFERENCE_PREFIX) ||
    material.protectedProviderMaterial.cardTransactions.some(
      (transaction) =>
        typeof transaction.processor === "string" &&
        transaction.processor.trim().toLowerCase() ===
          MANUAL_CARD_PROCESSOR.toLowerCase(),
    )
  );
}

function isTransportFailure(error: unknown): boolean {
  return error instanceof HbposApiError && error.kind === "transport";
}

function isGuid(value: string): boolean {
  return /^[0-9a-f]{8}-[0-9a-f]{4}-[1-5][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/iu.test(
    value,
  );
}

function sameStore(value: string | null | undefined, expected: string): boolean {
  return value?.trim().toUpperCase() === expected.trim().toUpperCase();
}

function collectProtectedValues(
  materials: readonly ProtectedTenderCapacityMaterial[],
): ReadonlySet<string> {
  const values = new Set<string>();
  for (const material of materials) {
    const provider = material.protectedProviderMaterial;
    if (provider.reference) values.add(provider.reference);
    for (const transaction of provider.cardTransactions) {
      for (const value of Object.values(transaction)) {
        if (typeof value === "string" && value) values.add(value);
      }
    }
  }
  return values;
}

function roundRatioAwayFromZero(numerator: number, denominator: number): number {
  const quotient = numerator / denominator;
  return quotient >= 0
    ? Math.floor(quotient + 0.5)
    : Math.ceil(quotient - 0.5);
}

function safeAdd(left: number, right: number): number {
  const value = left + right;
  if (!Number.isSafeInteger(value)) {
    throw new ReturnFeatureError("RETURN_SOURCE_MISMATCH");
  }
  return value;
}

function safeMultiply(left: number, right: number): number {
  const value = left * right;
  if (!Number.isSafeInteger(value)) {
    throw new ReturnFeatureError("RETURN_SOURCE_MISMATCH");
  }
  return value;
}

function assertWholeQuantity(
  value: number,
  allowZero: boolean,
): number {
  if (
    !Number.isSafeInteger(value) ||
    value < (allowZero ? 0 : 1)
  ) {
    throw new ReturnFeatureError("RETURN_SOURCE_MISMATCH");
  }
  return value;
}

function assertPositiveCents(value: number): void {
  if (!Number.isSafeInteger(value) || value <= 0) {
    throw new ReturnFeatureError("RETURN_OPEN_ITEM_INVALID");
  }
}

function assertPositiveSourceCents(value: number): void {
  if (!Number.isSafeInteger(value) || value <= 0) {
    throw new ReturnFeatureError("RETURN_SOURCE_MISMATCH");
  }
}

function requiredText(value: string | null | undefined): string {
  const normalized = value?.trim();
  if (!normalized) {
    throw new ReturnFeatureError("RETURN_SOURCE_MISMATCH");
  }
  return normalized;
}

function normalizeReturnLineSyncProvenance(
  input: unknown,
): LineSyncProvenance {
  try {
    return normalizeLineSyncProvenance(input);
  } catch {
    throw new ReturnFeatureError("RETURN_SOURCE_MISMATCH");
  }
}
