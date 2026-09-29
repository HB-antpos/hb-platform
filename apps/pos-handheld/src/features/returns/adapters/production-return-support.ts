import {
  ReturnFeatureError,
  type ReceiptReturnLine,
  type ReturnRefundPlan,
} from "@hb/pos-domain/features/returns/return-domain";
import type { ReturnExecutionCommand } from "@hb/pos-domain/features/returns/return-workflow";

import type {
  DurableReturnLine,
  ReturnExecutionLineMaterialPort,
  ReturnRequestFingerprintPort,
  TrustedReturnIdentity,
} from "@hb/pos-domain/features/returns/adapters/durable-return-execution-orchestrator";
import type {
  LocalReceiptReturnSnapshot,
  LocalReturnCatalogItem,
  LocalReturnCatalogPort,
  LocalReturnOrderLookupPort,
  ProtectedReturnCapacityHandle,
  ProtectedTenderCapacityMaterial,
  ReturnCapacityVaultInput,
  ReturnCapacityVaultPort,
} from "./return-lookup-adapter";
import { isManualCardReturnMaterial } from "./return-lookup-adapter";

import type { CartLine } from "@hb/pos-domain/core/contracts/cart";
import {
  normalizeLineSyncProvenance,
  type LineSyncProvenance,
} from "@hb/pos-domain/core/contracts/line-sync-provenance";
import type { LocalOrder, OrderTender } from "@hb/pos-domain/core/contracts/order";
import type { OrderRepositoryPort } from "@hb/pos-domain/core/contracts/repositories";
import type { LocalCatalogMatch, SqliteCatalogSnapshotRepository } from "@/core/db/catalog-repository";
import type { SqliteReturnCapacityVault } from "@/core/db/sqlite-return-capacity-vault";

const LOCAL_ORDER_PAGE_SIZE = 32;
const LOCAL_ORDER_MAX_PAGES = 4;
const LOCAL_CATALOG_MAX_RESULTS = 32;

/** 只读取已完成的售卖单；草稿、退货单及跨门店记录永远不参与本地回退。 */
export class OrderRepositoryLocalReturnLookup
  implements LocalReturnOrderLookupPort
{
  public constructor(
    private readonly orders: Pick<OrderRepositoryPort, "getByGuid" | "listLocal">,
  ) {}

  public async findSameStore(input: Readonly<{
    storeCode: string;
    query: string;
  }>): Promise<LocalReceiptReturnSnapshot | null> {
    const storeCode = requiredText(input.storeCode);
    const query = requiredText(input.query);
    const order = isGuid(query)
      ? await this.orders.getByGuid(query)
      : await this.findFromBoundedPages(query);
    if (!order || !isReturnableSale(order, storeCode)) return null;

    const lines = order.lines.map((line) => mapSaleLine(order, line));
    if (!lines.length) return null;
    return {
      storeCode: order.storeCode,
      originalOrderGuid: order.orderGuid,
      receiptLabel: order.orderGuid,
      lines,
      capacities: groupLocalTenderCapacities(order.orderGuid, order.tenders),
    };
  }

  private async findFromBoundedPages(query: string): Promise<LocalOrder | null> {
    let beforeSequence: number | undefined;
    const visited = new Set<number>();
    for (let page = 0; page < LOCAL_ORDER_MAX_PAGES; page += 1) {
      const orders = await this.orders.listLocal(LOCAL_ORDER_PAGE_SIZE, beforeSequence);
      if (!orders.length) return null;
      const found = orders.find((order) => matchesLocalOrderQuery(order, query));
      if (found) return found;
      const last = orders.at(-1);
      if (!last || !Number.isSafeInteger(last.localSequence) || visited.has(last.localSequence)) {
        return null;
      }
      visited.add(last.localSequence);
      beforeSequence = last.localSequence;
    }
    return null;
  }
}

/** 当前 active 目录没有门店参数，适配器必须在边界再次强制门店隔离。 */
export class CatalogLocalReturnAdapter implements LocalReturnCatalogPort {
  public constructor(
    private readonly catalog: Pick<
      SqliteCatalogSnapshotRepository,
      "findExact" | "searchByName"
    >,
  ) {}

  public async findExactMatches(input: Readonly<{
    storeCode: string;
    query: string;
  }>): Promise<readonly LocalReturnCatalogItem[]> {
    const storeCode = requiredText(input.storeCode);
    const match = await this.catalog.findExact(requiredText(input.query));
    return match && sameStore(match.storeCode, storeCode) ? [mapCatalog(match)] : [];
  }

  public async search(input: Readonly<{
    storeCode: string;
    query: string;
    limit: number;
  }>): Promise<readonly LocalReturnCatalogItem[]> {
    const storeCode = requiredText(input.storeCode);
    const limit = boundedPositiveInteger(input.limit, LOCAL_CATALOG_MAX_RESULTS);
    const matches = await this.catalog.searchByName(requiredText(input.query), limit, 0);
    return matches
      .filter((match) => sameStore(match.storeCode, storeCode))
      .map(mapCatalog);
  }
}

export type DurableCapacityVaultAdapterOptions = Readonly<{
  vault: Pick<SqliteReturnCapacityVault, "seedOrLoad">;
  createOpaqueId(kind: "return-capacity" | "offline-cash-evidence"): string;
  nowIso(): string;
}>;

/**
 * Vault facade 现仅提供单条 seedOrLoad，无法回滚已 seed 的前项。
 * 因而任一项失败时本适配器 fail-closed：不返回任何 handle；前项只会成为
 * 不可被 workflow 引用的加密孤儿记录，绝不误报为已完成的批量保护。
 */
export class DurableCapacityVaultAdapter implements ReturnCapacityVaultPort {
  public constructor(private readonly options: DurableCapacityVaultAdapterOptions) {}

  public async protect(
    input: ReturnCapacityVaultInput,
  ): Promise<readonly ProtectedReturnCapacityHandle[]> {
    const storeCode = requiredText(input.storeCode);
    const originalOrderGuid = requiredText(input.originalOrderGuid);
    if (input.loadedFrom !== "local" && input.loadedFrom !== "remote") {
      throw sourceMismatch();
    }
    const sources = new Set<string>();
    const handles: ProtectedReturnCapacityHandle[] = [];
    // 券引用不能进入 Vault context；同单多张券无法安全识别历史容量，必须拒绝歧义。
    if (input.capacities.filter((material) => material.method === "voucher").length > 1) {
      throw sourceMismatch();
    }
    try {
      for (const material of input.capacities) {
        const context = validateCapacityMaterial(
          material,
          originalOrderGuid,
          sources,
        );
        const capacityId = opaqueId(this.options.createOpaqueId("return-capacity"));
        const offlineCashEvidenceId =
          material.method === "cash"
            ? opaqueId(this.options.createOpaqueId("offline-cash-evidence"))
            : null;
        assertOpaqueIds(capacityId, offlineCashEvidenceId, material);
        const protectedCapacity = await this.options.vault.seedOrLoad({
          capacityId,
          originalOrderGuid,
          method: material.method,
          originalAmountCents: material.remainingCents,
          remainingAmountCents: material.remainingCents,
          protectedContext: context,
          observedAtIso: this.options.nowIso(),
        });
        handles.push({
          sourceKey: material.sourceKey,
          capacityId: protectedCapacity.capacityId,
          remainingCents: protectedCapacity.remainingAmountCents,
          offlineCashEvidenceId,
        });
      }
    } catch {
      // 不能泄露部分 handle；调用方只能把整次 lookup 当作失败。
      throw sourceMismatch();
    }
    // storeCode 已在入口校验，用它避免日后误删门店边界检查。
    void storeCode;
    return handles;
  }
}

export type ReturnLineMaterialCacheRecord = Readonly<{
  workflowId: string;
  identity: TrustedReturnIdentity;
  lines: readonly DurableReturnLine[];
}>;

/** 进程内的短生命周期桥接；重启或未 bind action 都安全失败，绝不补猜行资料。 */
export class ReturnLineMaterialCache implements ReturnExecutionLineMaterialPort {
  private readonly workflows = new Map<string, ReturnLineMaterialCacheRecord>();
  private readonly actions = new Map<string, Readonly<{
    workflowId: string;
    identity: TrustedReturnIdentity;
    planJson: string;
  }>>();

  public record(record: ReturnLineMaterialCacheRecord): void {
    const workflowId = requiredText(record.workflowId);
    validateIdentity(record.identity);
    const lines = record.lines.map(copyLine);
    validateMaterialLines(lines);
    this.workflows.set(workflowId, { workflowId, identity: { ...record.identity }, lines });
  }

  public bindAction(input: Readonly<{
    workflowId: string;
    actionId: string;
    identity: TrustedReturnIdentity;
    plan: ReturnRefundPlan;
  }>): void {
    const workflow = this.workflows.get(requiredText(input.workflowId));
    if (!workflow || !sameIdentity(workflow.identity, input.identity)) throw sourceMismatch();
    const planJson = canonicalJson(planMaterial(input.plan));
    assertPlanMatchesLines(input.plan, workflow.lines);
    if (this.actions.has(requiredText(input.actionId))) throw sourceMismatch();
    this.actions.set(input.actionId, {
      workflowId: workflow.workflowId,
      identity: { ...input.identity },
      planJson,
    });
  }

  public async resolveForAction(input: Readonly<{
    actionId: string;
    identity: TrustedReturnIdentity;
    plan: ReturnRefundPlan;
  }>): Promise<readonly DurableReturnLine[]> {
    const action = this.actions.get(requiredText(input.actionId));
    if (!action || !sameIdentity(action.identity, input.identity)) throw sourceMismatch();
    const workflow = this.workflows.get(action.workflowId);
    if (!workflow || !sameIdentity(workflow.identity, input.identity)) throw sourceMismatch();
    if (action.planJson !== canonicalJson(planMaterial(input.plan))) throw sourceMismatch();
    assertPlanMatchesLines(input.plan, workflow.lines);
    return workflow.lines.map(copyLine);
  }
}

export class CanonicalReturnFingerprint implements ReturnRequestFingerprintPort {
  public constructor(
    private readonly sha256Hex: (material: string) => Promise<string>,
  ) {}

  public digest(input: Readonly<{
    command: ReturnExecutionCommand;
    identity: TrustedReturnIdentity;
    lines: readonly DurableReturnLine[];
  }>): Promise<string> {
    validateIdentity(input.identity);
    validateMaterialLines(input.lines);
    // supervisor grant 是一次性密钥，刻意不参与 fingerprint 或任何日志材料。
    return this.sha256Hex(canonicalJson({
      command: { actionId: requiredText(input.command.actionId), plan: planMaterial(input.command.plan) },
      identity: identityMaterial(input.identity),
      lines: [...input.lines].map(lineMaterial).sort(compareMaterialLines),
    }));
  }
}

function isReturnableSale(order: LocalOrder, storeCode: string): boolean {
  return sameStore(order.storeCode, storeCode)
    && order.originalOrderGuid === null
    && (order.state === "CompletedLocal" || order.state === "PendingSync" || order.state === "Synced")
    && order.actualAmount.currency === "AUD"
    && order.actualAmount.cents > 0
    && order.lines.every((line) => line.kind === "sale");
}

function matchesLocalOrderQuery(order: LocalOrder, query: string): boolean {
  return order.orderGuid === query || String(order.localSequence) === query;
}

function mapSaleLine(order: LocalOrder, line: CartLine): ReceiptReturnLine {
  const quantity = Number(line.quantity);
  const amount = line.actualAmount.cents;
  if (
    !line.lineId.trim() || !line.productCode.trim() || !line.displayName.trim()
    || !Number.isSafeInteger(quantity) || quantity <= 0
    || !Number.isSafeInteger(amount) || amount <= 0
  ) throw sourceMismatch();
  return {
    selectionKey: `local-receipt-line:${order.orderGuid}:${line.lineId}`,
    originalOrderGuid: order.orderGuid,
    originalOrderDetailGuid: line.lineId,
    // 本地及服务端历史查询须使用同一个来源键；旧 local-receipt 键由账本按原单明细兼容。
    returnSourceKey: line.returnSourceKey?.trim() || `receipt:${order.orderGuid}:${line.lineId}`,
    productCode: line.productCode,
    itemNumber: line.itemNumber,
    lookupCode: line.lookupCode,
    displayName: line.displayName,
    availableQuantity: quantity,
    unitRefundCents: Math.floor(amount / quantity + 0.5),
    remainingAmountCents: amount,
    syncProvenance: normalizeReturnLineSyncProvenance(
      line.syncProvenance,
    ),
  };
}

function mapVerifiedTender(originalOrderGuid: string, tender: OrderTender): readonly ProtectedTenderCapacityMaterial[] {
  if (!tender.tenderGuid.trim() || !Number.isSafeInteger(tender.amount.cents) || tender.amount.cents <= 0) return [];
  if (tender.method === "cash") return [{
    sourceKey: `local-tender:${tender.tenderGuid}`,
    method: "cash",
    originalOrderGuid,
    remainingCents: tender.amount.cents,
    protectedProviderMaterial: { reference: null, cardTransactions: [] },
  }];
  // 本地 receipt 只公开验证 Square 的 SQ: reference；Linkly RFN、券码和 reservation token 不可猜造。
  if (tender.method !== "card" || !tender.reference?.startsWith("SQ:")) return [];
  return [{
    sourceKey: `local-tender:${tender.tenderGuid}`,
    method: "card",
    originalOrderGuid,
    remainingCents: tender.amount.cents,
    protectedProviderMaterial: { reference: tender.reference, cardTransactions: [] },
  }];
}

function groupLocalTenderCapacities(
  originalOrderGuid: string,
  tenders: readonly OrderTender[],
): readonly ProtectedTenderCapacityMaterial[] {
  const grouped = new Map<string, ProtectedTenderCapacityMaterial>();
  for (const tender of tenders) {
    for (const material of mapVerifiedTender(originalOrderGuid, tender)) {
      const key = `${material.method}:${material.protectedProviderMaterial.reference ?? ""}`;
      const prior = grouped.get(key);
      const remainingCents = (prior?.remainingCents ?? 0) + material.remainingCents;
      if (!Number.isSafeInteger(remainingCents)) throw sourceMismatch();
      grouped.set(key, {
        ...material,
        sourceKey: prior?.sourceKey ?? material.sourceKey,
        remainingCents,
      });
    }
  }
  return [...grouped.values()];
}

function mapCatalog(match: LocalCatalogMatch): LocalReturnCatalogItem {
  return {
    storeCode: match.storeCode,
    productCode: match.productCode,
    itemNumber: match.itemNumber,
    lookupCode: match.lookupCode,
    displayName: match.displayName,
    retailPriceCents: match.retailPriceCents,
    syncProvenance: normalizeReturnLineSyncProvenance({
      referenceCode: match.referenceCode,
      priceSource: match.priceSource,
    }),
  };
}

function validateCapacityMaterial(
  material: ProtectedTenderCapacityMaterial,
  originalOrderGuid: string,
  sources: Set<string>,
): Readonly<Record<string, unknown>> | null {
  if (!material.sourceKey.trim() || sources.has(material.sourceKey) || material.originalOrderGuid !== originalOrderGuid || !Number.isSafeInteger(material.remainingCents) || material.remainingCents < 0) throw sourceMismatch();
  sources.add(material.sourceKey);
  if (material.method === "cash") {
    if (material.protectedProviderMaterial.reference !== null || material.protectedProviderMaterial.cardTransactions.length) throw sourceMismatch();
    return null;
  }
  if (material.method === "voucher") {
    // 券退款会签发新券，原券码不能作为退款凭据，更不能落入 Vault context。
    return { version: 1, provider: "voucher" };
  }
  if (material.method !== "card") throw sourceMismatch();
  return cardProtectedContext(material);
}

function cardProtectedContext(
  material: ProtectedTenderCapacityMaterial,
): Readonly<Record<string, unknown>> {
  if (isManualCardReturnMaterial(material)) {
    // 手工刷卡只登记额度，不保存引用或交易号：执行层对 manual-card 原路退款一律拒绝，
    // 收银员只能以现金或代金券代替退款（与 WPF 一致）。
    return { version: 1, provider: "manual-card" };
  }
  const originalReference = requiredProviderText(
    material.protectedProviderMaterial.reference,
  );
  const square = /^SQ:([^:\s]+)$/u.exec(originalReference);
  if (square) {
    return { version: 1, provider: "square", paymentId: square[1] };
  }
  if (!/^(ANZCLOUD|ANZBACKEND):[^\s]+$/u.test(originalReference)) {
    throw sourceMismatch();
  }
  const transactions = material.protectedProviderMaterial.cardTransactions;
  if (!transactions.length) throw sourceMismatch();
  const rfnValues = new Set<string>();
  const txnRefs = new Set<string>();
  for (const transaction of transactions) {
    if (!isLinklyProcessor(transaction.processor)) throw sourceMismatch();
    const rfn = optionalProviderText(transaction.refundReference);
    if (rfn) rfnValues.add(rfn);
    const txnRef = optionalProviderText(transaction.txnRef);
    if (txnRef) txnRefs.add(txnRef);
  }
  // 单一容量只对应一个 RFN；txnRef 可协助侦测混合交易，但绝不可替代 RFN。
  if (rfnValues.size !== 1 || txnRefs.size > 1) throw sourceMismatch();
  return {
    version: 1,
    provider: "linkly-cloud",
    rfn: [...rfnValues][0],
    originalReference,
  };
}

function assertOpaqueIds(capacityId: string, evidenceId: string | null, material: ProtectedTenderCapacityMaterial): void {
  const secrets = [
    material.protectedProviderMaterial.reference,
    ...material.protectedProviderMaterial.cardTransactions.flatMap((transaction) => [
      transaction.refundReference,
      transaction.txnRef,
      JSON.stringify(transaction),
    ]),
  ].filter((value): value is string => Boolean(value));
  if (secrets.includes(capacityId) || (evidenceId !== null && secrets.includes(evidenceId))) throw sourceMismatch();
}

function assertPlanMatchesLines(plan: ReturnRefundPlan, lines: readonly DurableReturnLine[]): void {
  if (plan.lines.length !== lines.length) throw sourceMismatch();
  const bySource = new Map(lines.map((line) => [line.returnSourceKey, line]));
  for (const planLine of plan.lines) {
    const line = bySource.get(planLine.returnSourceKey);
    if (!line || line.sourceKind !== planLine.sourceKind || line.originalOrderGuid !== planLine.originalOrderGuid || line.originalOrderDetailGuid !== planLine.originalOrderDetailGuid || line.productCode !== planLine.productCode || line.quantity !== planLine.quantity || line.signedAmountCents !== planLine.signedAmountCents || canonicalJson(normalizeReturnLineSyncProvenance(line.syncProvenance)) !== canonicalJson(normalizeReturnLineSyncProvenance(planLine.syncProvenance))) throw sourceMismatch();
  }
}

function validateMaterialLines(lines: readonly DurableReturnLine[]): void {
  const sources = new Set<string>();
  for (const line of lines) {
    if (!line.lineId.trim() || !line.selectionKey.trim() || !line.returnSourceKey.trim() || !line.productCode.trim() || !line.lookupCode.trim() || !line.displayName.trim() || sources.has(line.returnSourceKey) || !Number.isSafeInteger(line.quantity) || line.quantity <= 0 || !Number.isSafeInteger(line.unitRefundCents) || line.unitRefundCents <= 0 || !Number.isSafeInteger(line.signedAmountCents) || line.signedAmountCents >= 0) throw sourceMismatch();
    normalizeReturnLineSyncProvenance(line.syncProvenance);
    sources.add(line.returnSourceKey);
  }
}

function copyLine(line: DurableReturnLine): DurableReturnLine { return { ...line, syncProvenance: normalizeReturnLineSyncProvenance(line.syncProvenance) }; }
function lineMaterial(line: DurableReturnLine): Readonly<Record<string, unknown>> { return { lineId: line.lineId, selectionKey: line.selectionKey, sourceKind: line.sourceKind, returnSourceKey: line.returnSourceKey, originalOrderGuid: line.originalOrderGuid, originalOrderDetailGuid: line.originalOrderDetailGuid, productCode: line.productCode, itemNumber: line.itemNumber, lookupCode: line.lookupCode, displayName: line.displayName, quantity: line.quantity, unitRefundCents: line.unitRefundCents, signedAmountCents: line.signedAmountCents, availableQuantity: line.availableQuantity, remainingAmountCents: line.remainingAmountCents, syncProvenance: normalizeReturnLineSyncProvenance(line.syncProvenance) }; }
function compareMaterialLines(left: Readonly<Record<string, unknown>>, right: Readonly<Record<string, unknown>>): number { return String(left.returnSourceKey).localeCompare(String(right.returnSourceKey)); }
function planMaterial(plan: ReturnRefundPlan): Readonly<Record<string, unknown>> { return { sourceKind: plan.sourceKind, totalRefundCents: plan.totalRefundCents, lines: [...plan.lines].map((line) => ({ sourceKind: line.sourceKind, returnSourceKey: line.returnSourceKey, originalOrderGuid: line.originalOrderGuid, originalOrderDetailGuid: line.originalOrderDetailGuid, productCode: line.productCode, quantity: line.quantity, signedAmountCents: line.signedAmountCents, syncProvenance: normalizeReturnLineSyncProvenance(line.syncProvenance) })).sort((a, b) => a.returnSourceKey.localeCompare(b.returnSourceKey)), allocations: [...plan.allocations].map((allocation) => ({ method: allocation.method, signedAmountCents: allocation.signedAmountCents, originalCapacityId: allocation.originalCapacityId, originalOrderGuid: allocation.originalOrderGuid, offlineCashProof: allocation.offlineCashProof === null ? null : { evidenceId: allocation.offlineCashProof.evidenceId, capacityId: allocation.offlineCashProof.capacityId, originalOrderGuid: allocation.offlineCashProof.originalOrderGuid, remainingCents: allocation.offlineCashProof.remainingCents } })).sort((a, b) => canonicalJson(a).localeCompare(canonicalJson(b))), online: plan.online }; }
function identityMaterial(identity: TrustedReturnIdentity): Readonly<Record<string, string>> { return { storeCode: identity.storeCode, deviceCode: identity.deviceCode, cashierId: identity.cashierId, cashierName: identity.cashierName, sessionEpoch: identity.sessionEpoch }; }
function canonicalJson(value: unknown): string { return JSON.stringify(canonicalize(value)); }
function canonicalize(value: unknown): unknown { if (Array.isArray(value)) return value.map(canonicalize); if (value && typeof value === "object") return Object.fromEntries(Object.entries(value as Record<string, unknown>).sort(([left], [right]) => left.localeCompare(right)).map(([key, child]) => [key, canonicalize(child)])); return value; }
function validateIdentity(identity: TrustedReturnIdentity): void {
  requiredText(identity.storeCode);
  requiredText(identity.deviceCode);
  requiredText(identity.cashierId);
  requiredText(identity.cashierName);
  if (identity.userGuid !== null && identity.userGuid !== undefined) {
    requiredText(identity.userGuid);
  }
  requiredText(identity.sessionEpoch);
}
function sameIdentity(left: TrustedReturnIdentity, right: TrustedReturnIdentity): boolean { return left.storeCode === right.storeCode && left.deviceCode === right.deviceCode && left.cashierId === right.cashierId && left.cashierName === right.cashierName && left.sessionEpoch === right.sessionEpoch; }
function sameStore(left: string, right: string): boolean { return left.trim().toUpperCase() === right.trim().toUpperCase(); }
function isGuid(value: string): boolean { return /^[0-9a-f]{8}-[0-9a-f]{4}-[1-5][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i.test(value); }
function requiredText(value: string): string { const normalized = value.trim(); if (!normalized) throw sourceMismatch(); return normalized; }
function requiredProviderText(value: string | null): string { if (value === null) throw sourceMismatch(); return requiredText(value); }
function optionalProviderText(value: string | null | undefined): string | null { return typeof value === "string" && value.trim() ? value.trim() : null; }
function isLinklyProcessor(value: string | null | undefined): boolean { return typeof value === "string" && /(?:LINKLY|ANZ)/iu.test(value.trim()); }
function opaqueId(value: string): string { const normalized = requiredText(value); if (normalized.length > 128) throw sourceMismatch(); return normalized; }
function boundedPositiveInteger(value: number, max: number): number { if (!Number.isSafeInteger(value) || value <= 0) throw sourceMismatch(); return Math.min(value, max); }
function normalizeReturnLineSyncProvenance(input: unknown): LineSyncProvenance { try { return normalizeLineSyncProvenance(input); } catch { throw sourceMismatch(); } }
function sourceMismatch(): ReturnFeatureError { return new ReturnFeatureError("RETURN_SOURCE_MISMATCH"); }
