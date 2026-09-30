import { isDeviceRevocationCode } from "@hb/pos-api-client/forbidden-response";
import { HbposApiError, type HbposEnvelope, type HbposTransport, unwrapHbposEnvelope } from "@hb/pos-api-client/transport";
import { auditActorSnapshotFromPayload } from "@hb/pos-domain/core/contracts/audit-actor";
import { freezeAuditScope, type AuditScope } from "@hb/pos-domain/core/contracts/audit-scope";
import { normalizeLineSyncProvenance } from "@hb/pos-domain/core/contracts/line-sync-provenance";
import type { AuditEventDraft, LocalOrder, OrderTender } from "@hb/pos-domain/core/contracts/order";
import {
  normalizeCardSyncEvidence,
  type CardSyncEvidenceV1,
} from "@hb/pos-domain/core/contracts/payment";
import type { OrderRepositoryPort } from "@hb/pos-domain/core/contracts/repositories";
import type { OrderSyncPort, SyncOrderResult } from "@hb/pos-domain/core/contracts/sync";
import {
  OrderSyncMaterialError,
  type ResolvedHeldOrderSource,
} from "@hb/pos-db/core/db/order-sync-material-contract";

import type { AuditBatchUploadPort, AuditUploadResult } from "./sync-coordinator";

import type { components } from "@hb/pos-api-client/openapi";

type OrderSyncRequest = components["schemas"]["OrderSyncRequest"];
type OrderSyncResponse = components["schemas"]["OrderSyncResponse"];
type OperationAuditBatchRequestDto = components["schemas"]["OperationAuditBatchRequestDto"];
type OperationAuditBatchResultDto = components["schemas"]["OperationAuditBatchResultDto"];
type OperationAuditEventDto = components["schemas"]["OperationAuditEventDto"];
type OperationAuditItemDto = components["schemas"]["OperationAuditItemDto"];

const uuidPattern = /^[0-9a-f]{8}-[0-9a-f]{4}-[1-8][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i;
const decimalPattern = /^-?(?:0|[1-9]\d*)(?:\.\d+)?$/;
const maximumAuditRequestBytes = 4 * 1024 * 1024;

const safeAuditPropertyKeys = new Set([
  "source", "action", "status", "screen", "mode", "reason", "result",
  "paymentMethod", "cashDrawerMode", "itemCount", "requestingCashierId",
  "requestingCashierName", "requestingUserGuid", "authorizingCashierId", "authorizingUserGuid", "permissionCode", "authorizationMode",
]);

const operationTypes = new Set([
  "CASHIER_LOGIN", "CASHIER_LOGOUT", "CART_ITEM_ADD", "CART_ITEM_REMOVE", "CART_ITEM_QUANTITY_CHANGE",
  "CART_ITEM_PRICE_CHANGE", "CART_LINE_DISCOUNT_CHANGE", "CART_ORDER_DISCOUNT_CHANGE", "CART_CLEAR",
  "ORDER_HOLD", "ORDER_RECALL", "ORDER_CANCEL", "CASH_DRAWER_OPEN", "PAYMENT_TENDER_ADD",
  "PAYMENT_TENDER_REMOVE", "PAYMENT_CANCEL", "SALE_COMPLETE", "RETURN_REFUND_COMPLETE", "SALE_VOID",
  "RECEIPT_REPRINT", "INSTALLMENT_REPAYMENT_COMPLETE", "INSTALLMENT_REPAYMENT_CANCEL", "DAILY_CLOSE_SAVE",
  "DAILY_CLOSE_REPRINT", "PERMISSION_OVERRIDE",
]);

type AdapterFailure =
  | Readonly<{ kind: "retry"; failure: "network" | "server" | "unauthorized"; code?: string }>
  | Readonly<{ kind: "blocked"; code: string }>
  | Readonly<{ kind: "rejected"; code: string }>;

export type HbposAuditMetadata = Readonly<{
  storeCode: string;
  deviceCode: string;
  appVersion: string;
  instanceId: string;
}>;

export type OrderSyncMaterialResolverPort = Readonly<{
  resolveForSync(
    order: LocalOrder,
    linklyEnvironment: string | null,
  ): Promise<ResolvedOrderSyncMaterial>;
}>;

export type ResolvedOrderSyncMaterial = Readonly<{
  order: LocalOrder;
  cardSyncEvidenceByTenderGuid: ReadonlyMap<string, CardSyncEvidenceV1>;
  /** 仅由解析器从数据库解析；缺省视为普通订单，wire 不携带来源。 */
  heldOrderSource?: ResolvedHeldOrderSource | null;
}>;

export type HbposOrderSyncMaterialOptions = Readonly<{
  resolver: OrderSyncMaterialResolverPort;
  linklyEnvironment: string | null;
}>;

export type OrderSyncPostAcceptPort = Readonly<{
  /**
   * 只在 Hbpos.Api 已接受订单（包括 AlreadySynced）后调用。
   * 抛错会让现有 order outbox 保留并退避重试。
   */
  afterOrderAccepted(orderGuid: string): Promise<void>;
}>;

/**
 * Hbpos 订单接口只接受 UUID。这里在网络请求前失败关闭，防止错误 outbox 把另一笔订单上传。
 */
function validUuid(value: string | null | undefined): value is string {
  return typeof value === "string" && uuidPattern.test(value);
}

function legacyPaymentOrderLineGuid(
  orderGuid: string,
  lineId: string,
  expectedLineSequence: number | null = null,
): string | null {
  if (!validUuid(orderGuid)) return null;
  const parts = lineId.split(":");
  if (
    parts.length !== 3 ||
    parts[0] !== "payment" ||
    parts[1]?.toLowerCase() !== orderGuid.toLowerCase() ||
    !/^[1-9]\d*$/u.test(parts[2] ?? "")
  ) {
    return null;
  }
  const lineSequence = Number(parts[2]);
  if (
    !Number.isSafeInteger(lineSequence) ||
    lineSequence <= 0 ||
    lineSequence > 0xffff_ffff ||
    (expectedLineSequence !== null && lineSequence !== expectedLineSequence)
  ) {
    return null;
  }

  // 旧支付草稿没有单独持久化 UUID。以随机 OrderGuid 为基底、把行序号
  // 混入末 32 位，既保持跨重试稳定，也保证同单各行得到不同的合法 Guid。
  const compactOrderGuid = orderGuid.replaceAll("-", "").toLowerCase();
  const suffix =
    BigInt(`0x${compactOrderGuid.slice(-8)}`) ^ BigInt(lineSequence);
  const compactLineGuid =
    compactOrderGuid.slice(0, -8) +
    suffix.toString(16).padStart(8, "0");
  const lineGuid = [
    compactLineGuid.slice(0, 8),
    compactLineGuid.slice(8, 12),
    compactLineGuid.slice(12, 16),
    compactLineGuid.slice(16, 20),
    compactLineGuid.slice(20),
  ].join("-");
  return validUuid(lineGuid) ? lineGuid : null;
}

function money(cents: number): number | null {
  return Number.isSafeInteger(cents) ? cents / 100 : null;
}

function quantity(value: string): number | null {
  return decimalPattern.test(value) && Number.isFinite(Number(value)) ? Number(value) : null;
}

function mapFailure(error: unknown): AdapterFailure {
  if (error instanceof HbposApiError) {
    if (error.kind === "transport") return { kind: "retry", failure: "network" };
    if (error.kind === "http") {
      if (error.status === 401) {
        return isDeviceRevocationCode(error.code)
          ? { kind: "blocked", code: error.code! }
          : { kind: "retry", failure: "unauthorized" };
      }
      if (error.status === 403) {
        return isDeviceRevocationCode(error.code)
          ? { kind: "blocked", code: error.code! }
          : {
              kind: "retry",
              failure: "unauthorized",
              code: error.code ?? "HTTP_403",
            };
      }
      // 限流不是业务拒绝：保留本地事件并交给分钟级 durable retry。
      if (error.status === 429) return { kind: "retry", failure: "server" };
      if ((error.status ?? 0) >= 500) return { kind: "retry", failure: "server" };
      // 历史已收款订单可能仍有 POSM int 列不支持的小数数量，等待人工核对和数据迁移后重传。
      if (error.status === 400 && error.code === "ORDER_SYNC_QUANTITY_UNSUPPORTED") {
        return { kind: "retry", failure: "server", code: error.code };
      }
      return { kind: "rejected", code: error.code ?? `HTTP_${error.status ?? "UNKNOWN"}` };
    }
    return { kind: "rejected", code: error.code ?? "API_ENVELOPE_REJECTED" };
  }

  // 传输层把网络异常封装为 HbposApiError；未封装异常也只能保留为可重试，绝不伪造成功。
  return { kind: "retry", failure: "network" };
}

function mapPostAcceptFailure(
  error: unknown,
): Extract<AdapterFailure, { kind: "retry" }> {
  const failure = mapFailure(error);
  if (failure.kind === "retry") return failure;
  // 服务端已接受订单后，余额联旁路故障绝不能再把同一订单改写为 Rejected/Blocked。
  return {
    kind: "retry",
    failure:
      error instanceof HbposApiError &&
      error.kind === "http" &&
      (error.status === 401 || error.status === 403)
        ? "unauthorized"
        : "server",
    code: failure.code,
  };
}

function reject(code: string): AdapterFailure {
  return { kind: "rejected", code };
}

function parseOrderPointer(orderGuid: string, payloadJson: string): string | null {
  try {
    const payload: unknown = JSON.parse(payloadJson);
    if (!payload || typeof payload !== "object" || Array.isArray(payload)) return null;
    const keys = Object.keys(payload);
    const pointer = (payload as { orderGuid?: unknown }).orderGuid;
    return keys.length === 1 && typeof pointer === "string" && pointer === orderGuid ? pointer : null;
  } catch {
    return null;
  }
}

function mapTender(
  tender: OrderTender,
  cardSyncEvidenceByTenderGuid: ReadonlyMap<string, CardSyncEvidenceV1>,
): components["schemas"]["PaymentSyncDto"] | AdapterFailure {
  const amount = money(tender.amount.cents);
  if (!validUuid(tender.tenderGuid) || amount === null) return reject("ORDER_TENDER_INVALID");

  if (tender.method === "cash") {
    return { paymentGuid: tender.tenderGuid, method: 1, amount, reference: tender.reference, reservationToken: null, cardTransactions: null };
  }
  if (tender.method === "card") {
    // 卡支付必须是支付模块写入的不可逆安全引用；不能用空值猜测或重扣。
    if (!tender.reference) return reject("CARD_PAYMENT_REFERENCE_REQUIRED");
    const protectedEvidence = cardSyncEvidenceByTenderGuid.get(
      tender.tenderGuid,
    );
    if (!protectedEvidence) return reject("CARD_SYNC_EVIDENCE_REQUIRED");
    let evidence: CardSyncEvidenceV1;
    try {
      evidence = normalizeCardSyncEvidence(protectedEvidence);
    } catch {
      return reject("CARD_SYNC_EVIDENCE_INVALID");
    }
    const expectedOperation =
      tender.amount.cents < 0 ? "refund" : "purchase";
    if (
      evidence.operation !== expectedOperation ||
      evidence.amountCents !== Math.abs(tender.amount.cents)
    ) {
      return reject("CARD_SYNC_EVIDENCE_MISMATCH");
    }
    return {
      paymentGuid: tender.tenderGuid,
      method: 2,
      amount,
      reference: tender.reference,
      reservationToken: null,
      cardTransactions: [
        {
          processor: evidence.processor,
          txnRef: evidence.txnRef,
          authCode: evidence.authCode,
          cardType: evidence.cardType,
          cardBin: evidence.cardBin,
          maskedCardNumber: evidence.maskedCardNumber,
          merchantId: evidence.merchantId,
          responseCode: evidence.responseCode,
          responseText: evidence.responseText,
          stan: evidence.stan,
          bankDateTime: evidence.bankDateTimeIso,
          amount: evidence.amountCents / 100,
          receiptText: null,
          refundReference: evidence.refundReference,
        },
      ],
    };
  }
  if (!tender.reference || tender.amount.cents === 0) return reject("VOUCHER_REFERENCE_REQUIRED");
  if (tender.amount.cents > 0 && !tender.reservationToken) return reject("VOUCHER_REFERENCE_REQUIRED");
  if (tender.amount.cents < 0 && tender.reservationToken !== null) return reject("VOUCHER_REFUND_RESERVATION_INVALID");
  return { paymentGuid: tender.tenderGuid, method: 3, amount, reference: tender.reference, reservationToken: tender.reservationToken, cardTransactions: null };
}

function detachedOrder(order: LocalOrder): LocalOrder {
  return Object.freeze({
    ...order,
    total: Object.freeze({ ...order.total }),
    discount: Object.freeze({ ...order.discount }),
    actualAmount: Object.freeze({ ...order.actualAmount }),
    lines: Object.freeze(order.lines.map((line) => Object.freeze({
      ...line,
      unitPrice: Object.freeze({ ...line.unitPrice }),
      discount: Object.freeze({ ...line.discount }),
      actualAmount: Object.freeze({ ...line.actualAmount }),
    }))),
    tenders: Object.freeze(order.tenders.map((tender) => Object.freeze({
      ...tender,
      amount: Object.freeze({ ...tender.amount }),
    }))),
  });
}

function mapOrder(
  order: LocalOrder,
  cardSyncEvidenceByTenderGuid: ReadonlyMap<string, CardSyncEvidenceV1>,
  heldOrderSource: ResolvedHeldOrderSource | null,
): OrderSyncRequest | AdapterFailure {
  const totalAmount = money(order.total.cents);
  const discountAmount = money(order.discount.cents);
  const actualAmount = money(order.actualAmount.cents);
  if (!validUuid(order.orderGuid) || totalAmount === null || discountAmount === null || actualAmount === null) {
    return reject("ORDER_INVALID");
  }
  if (order.originalOrderGuid !== null && !validUuid(order.originalOrderGuid)) return reject("ORDER_RETURN_REFERENCE_INVALID");

  const lines: components["schemas"]["OrderLineSyncDto"][] = [];
  for (const [index, line] of order.lines.entries()) {
    const orderLineGuid = validUuid(line.lineId)
      ? line.lineId
      : legacyPaymentOrderLineGuid(order.orderGuid, line.lineId, index + 1);
    const originalOrderDetailGuid =
      line.originalOrderDetailGuid === null
        ? null
        : validUuid(line.originalOrderDetailGuid)
          ? line.originalOrderDetailGuid
          : line.originalOrderGuid === null
            ? null
            : legacyPaymentOrderLineGuid(
                line.originalOrderGuid,
                line.originalOrderDetailGuid,
              );
    const lineQuantity = quantity(line.quantity);
    const unitPrice = money(line.unitPrice.cents);
    const lineDiscount = money(line.discount.cents);
    const lineActual = money(line.actualAmount.cents);
    if (orderLineGuid === null || lineQuantity === null || unitPrice === null || lineDiscount === null || lineActual === null) {
      return reject("ORDER_LINE_INVALID");
    }
    if (
      (line.originalOrderGuid !== null &&
        !validUuid(line.originalOrderGuid)) ||
      (line.originalOrderDetailGuid !== null &&
        originalOrderDetailGuid === null)
    ) {
      return reject("ORDER_RETURN_REFERENCE_INVALID");
    }
    if (line.syncProvenance === undefined) {
      return reject("ORDER_SYNC_LINE_PROVENANCE_MISSING");
    }
    let syncProvenance: ReturnType<typeof normalizeLineSyncProvenance>;
    try {
      syncProvenance = normalizeLineSyncProvenance(line.syncProvenance);
    } catch {
      return reject("ORDER_SYNC_LINE_PROVENANCE_INVALID");
    }
    lines.push({
      orderLineGuid,
      productCode: line.productCode,
      referenceCode: syncProvenance.referenceCode,
      displayName: line.displayName,
      lookupCode: line.lookupCode,
      quantity: lineQuantity,
      unitPrice,
      discountAmount: lineDiscount,
      actualAmount: lineActual,
      // 服务端售卖身份在商品加入购物车时冻结；补传时不得按当前目录反推。
      priceSource: syncProvenance.priceSource,
      itemNumber: line.itemNumber,
      kind: line.kind === "return" ? 2 : 1,
      returnSourceKey: line.returnSourceKey,
      originalOrderGuid: line.originalOrderGuid,
      originalOrderDetailGuid,
    });
  }
  const payments: components["schemas"]["PaymentSyncDto"][] = [];
  let cardTenderCount = 0;
  for (const tender of order.tenders) {
    if (tender.method === "card") cardTenderCount += 1;
    const mapped = mapTender(tender, cardSyncEvidenceByTenderGuid);
    if ("kind" in mapped) return mapped;
    payments.push(mapped);
  }
  if (cardSyncEvidenceByTenderGuid.size !== cardTenderCount) {
    return reject("CARD_SYNC_EVIDENCE_MISMATCH");
  }
  const request: OrderSyncRequest = {
    orderGuid: order.orderGuid, storeCode: order.storeCode, deviceCode: order.deviceCode,
    cashierId: order.cashierId, cashierName: order.cashierName, soldAt: order.soldAtIso,
    totalAmount, discountAmount, actualAmount, lines, payments,
  };
  if (heldOrderSource !== null) {
    request.heldOrderSource = {
      holdGuid: heldOrderSource.holdGuid,
      claimGuid: heldOrderSource.claimGuid,
      sourceKind: heldOrderSource.sourceKind,
    };
  }
  return request;
}

export class HbposOrderSyncAdapter implements OrderSyncPort {
  public constructor(
    private readonly transport: HbposTransport,
    private readonly orders: OrderRepositoryPort,
    private readonly material: HbposOrderSyncMaterialOptions | null = null,
    private readonly postAccept: OrderSyncPostAcceptPort | null = null,
  ) {}

  public async sync(orderGuid: string, payloadJson: string): Promise<SyncOrderResult> {
    if (!validUuid(orderGuid) || parseOrderPointer(orderGuid, payloadJson) !== orderGuid) {
      return { kind: "rejected", failure: "business-rejection", code: "OUTBOX_ORDER_POINTER_INVALID" };
    }
    const order = await this.orders.getByGuid(orderGuid);
    if (!order || order.orderGuid !== orderGuid) {
      return { kind: "rejected", failure: "business-rejection", code: "ORDER_NOT_FOUND" };
    }
    let requestOrder = order;
    let cardSyncEvidenceByTenderGuid: ReadonlyMap<
      string,
      CardSyncEvidenceV1
    > = new Map();
    let heldOrderSource: ResolvedHeldOrderSource | null = null;
    if (this.material) {
      try {
        // 解析器只能接触脱离仓储的只读快照，恢复出的敏感引用也只存活到本次 HTTP 请求结束。
        const resolved = await this.material.resolver.resolveForSync(
          detachedOrder(order),
          this.material.linklyEnvironment,
        );
        requestOrder = resolved.order;
        cardSyncEvidenceByTenderGuid = new Map(
          resolved.cardSyncEvidenceByTenderGuid,
        );
        heldOrderSource = resolved.heldOrderSource ?? null;
      } catch (error) {
        if (error instanceof OrderSyncMaterialError) {
          if (error.code === "ORDER_SYNC_MANUAL_PROVIDER_CONFLICT") {
            // 人工结案与 provider 终态冲突时禁止发 HTTP，也不能永久丢弃订单 outbox。
            return { kind: "retry", failure: "server" };
          }
          return { kind: "rejected", failure: "business-rejection", code: error.code };
        }
        // 非确定性的数据库或 IO 故障必须交回 outbox 重试，不能伪装成稳定业务拒绝。
        throw error;
      }
    }
    const request = mapOrder(
      requestOrder,
      cardSyncEvidenceByTenderGuid,
      heldOrderSource,
    );
    if ("kind" in request) return { kind: "rejected", failure: "business-rejection", code: request.code ?? "ORDER_MAPPING_FAILED" };
    let alreadySynced = false;
    try {
      const response = await this.transport.request<HbposEnvelope<OrderSyncResponse>>({ method: "POST", url: "/api/v1/orders/sync", data: request });
      if (response.status < 200 || response.status >= 300) throw new HbposApiError("Order sync HTTP failure.", { kind: "http", status: response.status });
      const body = unwrapHbposEnvelope(response.data);
      if (body.orderGuid !== orderGuid || body.accepted !== true || typeof body.alreadySynced !== "boolean") {
        return { kind: "rejected", failure: "business-rejection", code: "ORDER_SYNC_RESPONSE_INVALID" };
      }
      // disposition 为新增可选字段：出现时必须是合法枚举，旧 accepted/alreadySynced
      // 语义保持不变，缺省 disposition 的旧服务端响应仍被接受。
      const disposition = body.heldOrderDisposition;
      if (
        disposition !== undefined &&
        (typeof disposition !== "number" ||
          !Number.isInteger(disposition) ||
          disposition < 0 ||
          disposition > 3)
      ) {
        return { kind: "rejected", failure: "business-rejection", code: "ORDER_SYNC_RESPONSE_INVALID" };
      }
      alreadySynced = body.alreadySynced === true;
    } catch (error) {
      const failure = mapFailure(error);
      return failure.kind === "retry" ? failure : failure.kind === "blocked"
        ? { kind: "blocked", failure: "forbidden", code: failure.code }
        : { kind: "rejected", failure: "business-rejection", code: failure.code };
    }
    try {
      await this.postAccept?.afterOrderAccepted(orderGuid);
    } catch (error) {
      return mapPostAcceptFailure(error);
    }
    return { kind: "synced", alreadySynced };
  }
}

function safeProperties(payload: Readonly<Record<string, unknown>>): Record<string, string | null> | null {
  const result: Record<string, string | null> = {};
  for (const key of safeAuditPropertyKeys) {
    const value = payload[key];
    if (typeof value === "string" && value.length <= 256) result[key] = value;
    else if (typeof value === "number" && Number.isFinite(value)) result[key] = String(value);
    else if (typeof value === "boolean") result[key] = String(value);
  }
  return Object.keys(result).length === 0 ? null : result;
}

function auditOutcome(
  payload: Readonly<Record<string, unknown>>,
): "Succeeded" | "Failed" | "Denied" | null {
  const value = payload.outcome;
  if (value === undefined) return "Succeeded";
  // M16 礼券撤销账本保存的是不可变的本地事实；上传时才映射为后端审计枚举。
  if (payload.action === "payment-tender-remove") {
    if (value === "success") return "Succeeded";
    if (value === "blocked") return "Denied";
  }
  return value === "Succeeded" || value === "Failed" || value === "Denied"
    ? value
    : null;
}

function auditItems(order: LocalOrder): OperationAuditItemDto[] | AdapterFailure {
  const items: OperationAuditItemDto[] = [];
  for (const line of order.lines) {
    const lineQuantity = quantity(line.quantity);
    const unitPrice = money(line.unitPrice.cents);
    const actualAmount = money(line.actualAmount.cents);
    if (lineQuantity === null || unitPrice === null || actualAmount === null) return reject("AUDIT_ORDER_INVALID");
    items.push({ productCode: line.productCode, itemNumber: line.itemNumber, lookupCode: line.lookupCode, displayName: line.displayName, lineKind: line.kind, afterQuantity: lineQuantity, afterUnitPrice: unitPrice, afterActualAmount: actualAmount });
  }
  return items;
}

function auditPayloadItems(
  payload: Readonly<Record<string, unknown>>,
): OperationAuditItemDto[] | AdapterFailure | null {
  const source = payload.items;
  if (source === undefined || source === null) return null;
  if (!Array.isArray(source)) return reject("AUDIT_ITEMS_INVALID");

  const items: OperationAuditItemDto[] = [];
  for (const value of source) {
    if (!value || typeof value !== "object" || Array.isArray(value)) {
      return reject("AUDIT_ITEMS_INVALID");
    }
    const item = value as Readonly<Record<string, unknown>>;
    const beforeUnitPrice = moneyFromPayload(item, "beforeUnitPriceCents");
    const afterUnitPrice = moneyFromPayload(item, "afterUnitPriceCents");
    const unitPriceDelta = moneyFromPayload(item, "unitPriceDeltaCents");
    const beforeDiscount = moneyFromPayload(item, "beforeDiscountCents");
    const afterDiscount = moneyFromPayload(item, "afterDiscountCents");
    const discountDelta = moneyFromPayload(item, "discountDeltaCents");
    const beforeGross = moneyFromPayload(item, "beforeGrossCents");
    const afterGross = moneyFromPayload(item, "afterGrossCents");
    const grossDelta = moneyFromPayload(item, "grossDeltaCents");
    const beforeActual = moneyFromPayload(item, "beforeActualCents");
    const afterActual = moneyFromPayload(item, "afterActualCents");
    const actualDelta = moneyFromPayload(item, "actualDeltaCents");
    if (
      beforeUnitPrice === undefined || afterUnitPrice === undefined ||
      unitPriceDelta === undefined || beforeDiscount === undefined ||
      afterDiscount === undefined || discountDelta === undefined ||
      beforeGross === undefined || afterGross === undefined ||
      grossDelta === undefined || beforeActual === undefined ||
      afterActual === undefined || actualDelta === undefined
    ) {
      return reject("AUDIT_ITEMS_INVALID");
    }
    const beforeQuantity = quantityFromPayload(item, "beforeQuantity");
    const afterQuantity = quantityFromPayload(item, "afterQuantity");
    const quantityDelta = quantityFromPayload(item, "quantityDelta");
    if (
      beforeQuantity === undefined || afterQuantity === undefined ||
      quantityDelta === undefined
    ) {
      return reject("AUDIT_ITEMS_INVALID");
    }

    // 购物车审计没有订单快照可回查，只接受销售层已冻结的白名单字段，避免透传任意载荷。
    items.push({
      productCode: textFromPayload(item, "productCode"),
      itemNumber: textFromPayload(item, "itemNumber"),
      referenceCode: textFromPayload(item, "referenceCode"),
      lookupCode: textFromPayload(item, "lookupCode"),
      displayName: textFromPayload(item, "displayName"),
      lineKind: textFromPayload(item, "lineKind"),
      beforeQuantity,
      afterQuantity,
      quantityDelta,
      beforeUnitPrice,
      afterUnitPrice,
      unitPriceDelta,
      beforeDiscountAmount: beforeDiscount,
      afterDiscountAmount: afterDiscount,
      discountAmountDelta: discountDelta,
      beforeGrossAmount: beforeGross,
      afterGrossAmount: afterGross,
      grossAmountDelta: grossDelta,
      beforeActualAmount: beforeActual,
      afterActualAmount: afterActual,
      actualAmountDelta: actualDelta,
    });
  }
  return items;
}

function moneyFromPayload(
  payload: Readonly<Record<string, unknown>>,
  key: string,
): number | null | undefined {
  const value = payload[key];
  if (value === undefined || value === null) return null;
  return typeof value === "number" ? money(value) ?? undefined : undefined;
}

function quantityFromPayload(
  payload: Readonly<Record<string, unknown>>,
  key: string,
): number | null | undefined {
  const value = payload[key];
  if (value === undefined || value === null) return null;
  return typeof value === "number" && Number.isFinite(value) ? value : undefined;
}

function textFromPayload(
  payload: Readonly<Record<string, unknown>>,
  key: string,
): string | null {
  const value = payload[key];
  return typeof value === "string" ? value : null;
}

function auditCartAmounts(
  payload: Readonly<Record<string, unknown>>,
): Pick<
  OperationAuditEventDto,
  "beforeGross" | "afterGross" | "beforeDiscount" | "afterDiscount" |
  "beforeActual" | "afterActual" | "amountDelta"
> | AdapterFailure {
  const mappings = [
    ["beforeSubtotalCents", "beforeGross"],
    ["afterSubtotalCents", "afterGross"],
    ["beforeDiscountCents", "beforeDiscount"],
    ["afterDiscountCents", "afterDiscount"],
    ["beforeActualCents", "beforeActual"],
    ["afterActualCents", "afterActual"],
    ["amountDeltaCents", "amountDelta"],
  ] as const;
  const result: Partial<OperationAuditEventDto> = {};
  for (const [sourceKey, targetKey] of mappings) {
    if (!(sourceKey in payload)) continue;
    const value = moneyFromPayload(payload, sourceKey);
    if (value === undefined) return reject("AUDIT_AMOUNT_INVALID");
    result[targetKey] = value;
  }
  return result;
}

export class HbposAuditBatchAdapter implements AuditBatchUploadPort {
  public constructor(
    private readonly transport: HbposTransport,
    private readonly orders: OrderRepositoryPort,
    private readonly metadata: HbposAuditMetadata,
  ) {}

  public async upload(events: readonly AuditEventDraft[]): Promise<AuditUploadResult> {
    if (events.length === 0 || events.length > 8) return { kind: "rejected", code: "AUDIT_BATCH_SIZE_INVALID" };
    const mapped: OperationAuditEventDto[] = [];
    const mappedEventIds: string[] = [];
    const rejected: { eventId: string; code: string }[] = [];
    for (const event of events) {
      const item = await this.mapEvent(event);
      if ("kind" in item) {
        // 一条遗留或损坏审计不能阻塞同一设备后续的可上传员工操作。
        rejected.push({ eventId: event.eventId, code: item.code ?? "AUDIT_MAPPING_FAILED" });
        continue;
      }
      // 后端 EventId 是 Guid，wire 与回执都会 canonical 为小写；本地主键仍另行保留原值。
      mapped.push({ ...item, eventId: event.eventId.toLowerCase() });
      mappedEventIds.push(event.eventId);
    }
    if (!mapped.length) {
      return { kind: "acknowledged", uploadedEventIds: [], rejected };
    }
    const requestEvents: OperationAuditEventDto[] = [];
    const requestEventIds: string[] = [];
    for (let index = 0; index < mapped.length; index += 1) {
      const candidate = [...requestEvents, mapped[index]!];
      if (auditRequestSize(candidate) <= maximumAuditRequestBytes) {
        requestEvents.push(mapped[index]!);
        requestEventIds.push(mappedEventIds[index]!);
        continue;
      }
      if (!requestEvents.length) {
        // 单条记录本身超过网关上限，隔离它后让下一轮处理后续事件。
        rejected.push({ eventId: mappedEventIds[index]!, code: "AUDIT_REQUEST_TOO_LARGE" });
      }
      // 保持 FIFO：不跨过未发送事件，避免新操作先于旧操作抵达服务端。
      break;
    }
    if (!requestEvents.length) {
      return { kind: "acknowledged", uploadedEventIds: [], rejected };
    }
    try {
      // 员工审计端点直接返回 OperationAuditBatchResultDto；订单接口才使用 HbposEnvelope。
      const response = await this.transport.request<OperationAuditBatchResultDto>({ method: "POST", url: "/api/v1/operation-audits/batch", data: { events: requestEvents } satisfies OperationAuditBatchRequestDto });
      if (response.status < 200 || response.status >= 300) throw new HbposApiError("Audit upload HTTP failure.", { kind: "http", status: response.status });
      const body = response.data;
      const statuses = new Map(
        (body.results ?? []).flatMap((result) =>
          typeof result.eventId === "string"
            ? [[result.eventId.toLowerCase(), result] as const]
            : [],
        ),
      );
      const uploadedEventIds: string[] = [];
      const retryEventIds: string[] = [];
      for (const eventId of requestEventIds) {
        const result = statuses.get(eventId.toLowerCase());
        const status = result?.status?.toLowerCase();
        if (!result || (status !== "accepted" && status !== "duplicate" && status !== "rejected")) {
          retryEventIds.push(eventId);
          continue;
        }
        if (status === "rejected") {
          rejected.push({
            eventId,
            code: result?.errorCode ?? "AUDIT_REJECTED",
          });
          continue;
        }
        uploadedEventIds.push(eventId);
      }
      // 容量截断后的事件仍在本地 pending；只能确认实际发送并收到终态的前缀。
      return rejected.length > 0 || retryEventIds.length > 0 || requestEventIds.length !== mappedEventIds.length
        ? {
            kind: "acknowledged",
            uploadedEventIds,
            rejected,
            ...(retryEventIds.length ? { retryEventIds } : {}),
          }
        : { kind: "uploaded" };
    } catch (error) {
      return mapFailure(error);
    }
  }

  private async mapEvent(event: AuditEventDraft): Promise<OperationAuditEventDto | AdapterFailure> {
    if (!validUuid(event.eventId) || !validUuid(event.correlationId)) return reject("AUDIT_EVENT_INVALID");
    const auditScope = auditScopeFromEvent(event);
    if (!auditScope) return reject("AUDIT_SCOPE_UNPROVEN");
    const outcome = auditOutcome(event.payload);
    if (outcome === null) return reject("AUDIT_OUTCOME_INVALID");
    const payloadActor = auditActorSnapshotFromPayload(event.payload);
    let identity = {
      // scope 在事实入库时冻结；设备重注册后绝不能回退当前 metadata 或订单重算。
      storeCode: auditScope.storeCode,
      deviceCode: auditScope.deviceCode,
      // 非订单操作在发生时把 actor 写入 payload；上传时不得改用当前登录者。
      cashierId: payloadActor?.cashierId ?? null,
      cashierName: payloadActor?.cashierName ?? null,
      userGuid: payloadActor?.userGuid ?? null,
      order: null as LocalOrder | null,
    };
    if (event.orderGuid !== null) {
      if (!validUuid(event.orderGuid)) return reject("AUDIT_ORDER_REFERENCE_INVALID");
      const order = await this.orders.getByGuid(event.orderGuid);
      if (!order || order.orderGuid !== event.orderGuid) return reject("AUDIT_ORDER_NOT_FOUND");
      // 新事件的完整 payload 快照优先；遗留事件才整套回退至订单身份，禁止逐字段混搭。
      const actor = payloadActor ?? {
        cashierId: order.cashierId,
        cashierName: order.cashierName,
        // LocalOrder 未持久化 userGuid，遗留订单绝不猜测或回填当前会话。
        userGuid: null,
      };
      identity = {
        storeCode: auditScope.storeCode,
        deviceCode: auditScope.deviceCode,
        cashierId: actor.cashierId,
        cashierName: actor.cashierName,
        userGuid: actor.userGuid,
        order,
      };
    }
    const operationType = normalizedOperationType(event.eventType, identity.order);
    if (isAdapterFailure(operationType)) return operationType;
    const cartAmounts = auditCartAmounts(event.payload);
    if ("kind" in cartAmounts) return cartAmounts;
    const items = identity.order ? auditItems(identity.order) : auditPayloadItems(event.payload);
    if (items && "kind" in items) return items;
    return {
      eventId: event.eventId, schemaVersion: 1, occurredAtUtc: event.occurredAtIso,
      operationType, outcome, cashierId: identity.cashierId, userGuid: identity.userGuid, cashierName: identity.cashierName,
      isOfflineCached: event.payload.isOfflineCached === true,
      isEmergencyOverride: event.payload.isEmergencyOverride === true,
      storeCode: identity.storeCode, deviceCode: identity.deviceCode,
      appVersion: this.metadata.appVersion, instanceId: this.metadata.instanceId, orderGuid: event.orderGuid,
      // 服务端 Items 是非可空 List（nullable 上下文下隐式必填），发 null 会让整批 400；
      // 非订单且无明细的事件（如 CASHIER_LOGIN）必须发空数组。
      correlationId: event.correlationId, currencyCode: "AUD", properties: safeProperties(event.payload), items: items ?? [],
      ...cartAmounts,
    };
  }
}

function auditScopeFromEvent(event: AuditEventDraft): AuditScope | null {
  if (!event.auditScope) return null;
  try {
    return freezeAuditScope(event.auditScope);
  } catch {
    return null;
  }
}

function normalizedOperationType(
  eventType: string,
  order: LocalOrder | null,
): string | AdapterFailure {
  if (eventType === "PAYMENT_DRAFT_ABANDONED" || eventType === "DAILY_CLOSE_MIGRATED") {
    // 这两类是本地迁移/恢复诊断，后端操作审计没有等价业务语义；隔离而不阻塞队头。
    return reject("AUDIT_LOCAL_DIAGNOSTIC");
  }
  if (eventType === "MIXED_CASH_TENDER_APPENDED") return "PAYMENT_TENDER_ADD";
  if (eventType === "MIXED_CASH_TENDER_REVERSED") return "PAYMENT_TENDER_REMOVE";
  if (eventType === "PAYMENT_DRAFT_CANCELLED_CLOSED") return "PAYMENT_CANCEL";
  if (eventType === "RETURN_ORDER_COMPLETED") return "RETURN_REFUND_COMPLETE";
  if (eventType === "PAYMENT_MIXED_CASH_COMPLETE" || eventType === "PAYMENT_APPROVED_COMPLETE") {
    if (!order) return reject("AUDIT_ORDER_REFERENCE_REQUIRED");
    return isRefundOrder(order) ? "RETURN_REFUND_COMPLETE" : "SALE_COMPLETE";
  }
  return operationTypes.has(eventType) ? eventType : reject("AUDIT_EVENT_INVALID");
}

function isAdapterFailure(value: string | AdapterFailure): value is AdapterFailure {
  return typeof value === "object" && value !== null && "kind" in value;
}

function isRefundOrder(order: LocalOrder): boolean {
  if (order.total.cents < 0 || order.actualAmount.cents < 0) return true;
  // 历史订单可能混入退货行；净额仍为正的混合单是销售完成，不能误记为整单退款。
  const validLines = order.lines.filter(
    (line) => line.kind === "sale" || line.kind === "return",
  );
  return validLines.length > 0 && validLines.every((line) => line.kind === "return");
}

function auditRequestSize(events: readonly OperationAuditEventDto[]): number {
  return new TextEncoder().encode(JSON.stringify({ events })).byteLength;
}
