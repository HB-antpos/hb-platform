import {
  OrderSyncMaterialError,
  type OrderSyncMaterialErrorCode,
  type ResolvedHeldOrderSource,
} from "@hb/pos-db/core/db/order-sync-material-contract";
import { ProtectedMaterialIntegrityError } from "@hb/pos-db/core/db/protected-material-integrity-error";
import type { SqlitePaymentProtectedMaterialReader } from "@hb/pos-db/core/db/sqlite-payment-protected-material";
import type { SqliteConnectionPort } from "@hb/pos-db/core/db/types";
import { auditActorSnapshotFromPayload } from "@hb/pos-domain/core/contracts/audit-actor";
import type { CartLine } from "@hb/pos-domain/core/contracts/cart";
import {
  normalizeLineSyncProvenance,
  type LineSyncProvenance,
} from "@hb/pos-domain/core/contracts/line-sync-provenance";
import type { LocalOrder, OrderTender } from "@hb/pos-domain/core/contracts/order";
import type { CardSyncEvidenceV1 } from "@hb/pos-domain/core/contracts/payment";

import { readManualPaymentSyncEvidence } from "./sqlite-manual-payment-sync";
import type { SqliteReturnCapacityVault } from "./sqlite-return-capacity-vault";
import type { SqliteVoucherProtectedTokenStore } from "./sqlite-voucher-protected-token-store";

export { OrderSyncMaterialError };
export type { OrderSyncMaterialErrorCode, ResolvedHeldOrderSource };

export type LinklyOrderSyncEnvironment = "Sandbox" | "Production";

export type SqliteOrderSyncMaterialResolverOptions = Readonly<{
  returnCapacityVault: Pick<
    SqliteReturnCapacityVault,
    "resolveProtectedContext"
  >;
  voucherProtectedTokens: Pick<
    SqliteVoucherProtectedTokenStore,
    "getByAttempt"
  >;
  paymentProtectedMaterials?: Pick<
    SqlitePaymentProtectedMaterialReader,
    "read"
  >;
}>;

export type ResolvedSqliteOrderSyncMaterial = Readonly<{
  order: LocalOrder;
  cardSyncEvidenceByTenderGuid: ReadonlyMap<string, CardSyncEvidenceV1>;
  heldOrderSource: ResolvedHeldOrderSource | null;
}>;

/**
 * 同步前临时恢复 provider 引用的受信任边界。普通 OrderRepository 仍只返回
 * 脱敏 tender；本 resolver 不缓存、不写库，也不把密文或引用写入日志。
 */
export class SqliteOrderSyncMaterialResolver {
  public constructor(
    private readonly connection: SqliteConnectionPort,
    private readonly options: SqliteOrderSyncMaterialResolverOptions,
  ) {}

  public async resolve(
    order: LocalOrder,
    linklyEnvironmentInput: string | null,
  ): Promise<LocalOrder> {
    const persistedOrder = await this.assertPersistedOrder(order);
    const lines = await this.readPersistedLines(order);
    const reversalMemberGuids =
      await this.readLedgerReversalMemberGuids(order.orderGuid);
    const rows = await this.connection.getAll<TenderAttemptRow>(
      `SELECT
        tender.tender_guid,
        tender.order_guid AS tender_order_guid,
        tender.method AS tender_method,
        tender.amount_cents AS tender_amount_cents,
        tender.payment_attempt_id,
        attempt.attempt_id,
        attempt.idempotency_key,
        attempt.order_guid AS attempt_order_guid,
        attempt.provider,
        attempt.operation,
        attempt.amount_cents AS attempt_amount_cents,
        attempt.state AS attempt_state,
        attempt.checkout_id,
        attempt.payment_id,
        attempt.session_id,
        attempt.txn_ref,
        attempt.rfn,
        attempt.provider_response_code
       FROM order_tenders tender
       LEFT JOIN payment_attempts attempt
         ON attempt.attempt_id = tender.payment_attempt_id
       WHERE tender.order_guid = ?
       ORDER BY tender.created_at_iso, tender.tender_guid`,
      [order.orderGuid],
    );
    if (rows.length !== order.tenders.length) {
      throw materialError("ORDER_SYNC_TENDER_MISMATCH");
    }
    const byTender = new Map<string, TenderAttemptRow>();
    for (const row of rows) {
      const tenderGuid = persistedText(
        row.tender_guid,
        "ORDER_SYNC_TENDER_MISMATCH",
      );
      if (byTender.has(tenderGuid)) {
        throw materialError("ORDER_SYNC_TENDER_MISMATCH");
      }
      byTender.set(tenderGuid, row);
    }

    const seen = new Set<string>();
    const tenders: OrderTender[] = [];
    for (const inputTender of order.tenders) {
      const tenderGuid = inputText(
        inputTender.tenderGuid,
        "ORDER_SYNC_TENDER_MISMATCH",
      );
      if (seen.has(tenderGuid)) {
        throw materialError("ORDER_SYNC_TENDER_MISMATCH");
      }
      seen.add(tenderGuid);
      const row = byTender.get(tenderGuid);
      if (!row) throw materialError("ORDER_SYNC_TENDER_MISMATCH");
      tenders.push(
        await this.resolveTender(
          order,
          inputTender,
          row,
          linklyEnvironmentInput,
          reversalMemberGuids.has(tenderGuid),
        ),
      );
    }

    return Object.freeze({
      ...persistedOrder,
      lines: Object.freeze(lines),
      tenders: Object.freeze(tenders),
    });
  }

  public async resolveForSync(
    order: LocalOrder,
    linklyEnvironmentInput: string | null,
  ): Promise<ResolvedSqliteOrderSyncMaterial> {
    const resolvedOrder = await this.resolve(
      order,
      linklyEnvironmentInput,
    );
    if (
      resolvedOrder.lines.some(
        (line) => line.syncProvenance === undefined,
      )
    ) {
      throw materialError("ORDER_SYNC_LINE_PROVENANCE_MISSING");
    }
    const wireOrder = await this.projectTenderReversalsForSync(
      resolvedOrder,
    );
    const heldOrderSource = await this.resolveHeldOrderSourceForSync(
      wireOrder.orderGuid,
    );
    const cardTenders = new Map(
      wireOrder.tenders
        .filter((tender) => tender.method === "card")
        .map((tender) => [tender.tenderGuid, tender] as const),
    );
    const cardSyncEvidenceByTenderGuid =
      new Map<string, CardSyncEvidenceV1>();
    if (cardTenders.size === 0) {
      return Object.freeze({
        order: wireOrder,
        cardSyncEvidenceByTenderGuid,
        heldOrderSource,
      });
    }

    const reader = this.options.paymentProtectedMaterials;
    if (!reader) {
      throw new Error(
        "Payment protected material reader is not configured.",
      );
    }
    const rows = await this.connection.getAll<TenderAttemptRow>(
      `SELECT
        tender.tender_guid,
        tender.order_guid AS tender_order_guid,
        tender.method AS tender_method,
        tender.amount_cents AS tender_amount_cents,
        tender.payment_attempt_id,
        attempt.attempt_id,
        attempt.idempotency_key,
        attempt.order_guid AS attempt_order_guid,
        attempt.provider,
        attempt.operation,
        attempt.amount_cents AS attempt_amount_cents,
        attempt.state AS attempt_state,
        attempt.checkout_id,
        attempt.payment_id,
        attempt.session_id,
        attempt.txn_ref,
        attempt.rfn,
        attempt.provider_response_code
       FROM order_tenders tender
       LEFT JOIN payment_attempts attempt
         ON attempt.attempt_id = tender.payment_attempt_id
      WHERE tender.order_guid = ? AND tender.method = 'card'
       ORDER BY tender.created_at_iso, tender.tender_guid`,
      [wireOrder.orderGuid],
    );
    if (rows.length !== cardTenders.size) {
      throw materialError("ORDER_SYNC_ATTEMPT_MISMATCH");
    }
    for (const row of rows) {
      const tenderGuid = persistedText(
        row.tender_guid,
        "ORDER_SYNC_TENDER_MISMATCH",
      );
      const tender = cardTenders.get(tenderGuid);
      if (!tender) throw materialError("ORDER_SYNC_TENDER_MISMATCH");
      const manualEvidence = await readManualPaymentSyncEvidence(this.connection, {
        tenderGuid, orderGuid: wireOrder.orderGuid, storeCode: wireOrder.storeCode,
        deviceCode: wireOrder.deviceCode, amountCents: tender.amount.cents,
      });
      if (manualEvidence) {
        cardSyncEvidenceByTenderGuid.set(tenderGuid, manualEvidence);
        continue;
      }
      const attempt = readApprovedAttempt(row, wireOrder, tender);
      const provider = attempt.provider;
      if (provider === "voucher") {
        throw materialError("ORDER_SYNC_ATTEMPT_MISMATCH");
      }
      const evidence = await resolveProtectedMaterial(
        () => reader.read({
          attemptId: attempt.attemptId,
          orderGuid: wireOrder.orderGuid,
          provider,
          operation: attempt.operation,
          amountCents: tender.amount.cents,
        }),
        "ORDER_SYNC_CARD_EVIDENCE_MISMATCH",
      );
      if (evidence !== null) {
        cardSyncEvidenceByTenderGuid.set(tenderGuid, evidence);
      }
    }
    return Object.freeze({
      order: wireOrder,
      cardSyncEvidenceByTenderGuid,
      heldOrderSource,
    });
  }

  /**
   * 普通订单零来源查询：先读 local_orders 标记，只有挂单来源订单才查询不可变来源表。
   * 标记为 1 但来源行缺失/损坏时稳定失败关闭，禁止按内存对象猜测 wire。
   */
  private async resolveHeldOrderSourceForSync(
    orderGuidInput: string,
  ): Promise<ResolvedHeldOrderSource | null> {
    const orderGuid = inputText(
      orderGuidInput,
      "ORDER_SYNC_ORDER_MISMATCH",
    );
    const marker = await this.connection.getFirst<{
      is_shared_held_origin: number;
    }>(
      "SELECT is_shared_held_origin FROM local_orders WHERE order_guid = ?",
      [orderGuid],
    );
    if (!marker) {
      throw materialError("ORDER_SYNC_ORDER_MISMATCH");
    }
    if (marker.is_shared_held_origin !== 1) {
      return null;
    }
    const row = await this.connection.getFirst<{
      hold_guid: string;
      claim_guid: string | null;
      source_kind: number;
    }>(
      `SELECT hold_guid, claim_guid, source_kind
       FROM order_held_order_sources
       WHERE order_guid = ?`,
      [orderGuid],
    );
    if (!row) {
      throw materialError("ORDER_SYNC_HELD_SOURCE_MISSING");
    }
    const sourceKind = row.source_kind;
    if (sourceKind !== 1 && sourceKind !== 2) {
      throw materialError("ORDER_SYNC_HELD_SOURCE_MISSING");
    }
    return Object.freeze({
      holdGuid: persistedText(row.hold_guid, "ORDER_SYNC_HELD_SOURCE_MISSING"),
      claimGuid: row.claim_guid === null
        ? null
        : persistedText(
            row.claim_guid,
            "ORDER_SYNC_HELD_SOURCE_MISSING",
          ),
      sourceKind: sourceKind as 1 | 2,
    });
  }

  private async assertPersistedOrder(
    order: LocalOrder,
  ): Promise<PersistedOrderRoot> {
    const row = await this.connection.getFirst<OrderIdentityRow>(
      `SELECT order_guid, local_sequence, store_code, device_code,
        cashier_id, cashier_name, sold_at_iso, state, total_cents,
        discount_cents, actual_amount_cents, original_order_guid
       FROM local_orders
       WHERE order_guid = ?`,
      [inputText(order.orderGuid, "ORDER_SYNC_ORDER_MISMATCH")],
    );
    if (!row) throw materialError("ORDER_SYNC_ORDER_MISMATCH");
    const persisted = Object.freeze({
      orderGuid: persistedText(
        row.order_guid,
        "ORDER_SYNC_ORDER_MISMATCH",
      ),
      localSequence: persistedInteger(
        row.local_sequence,
        "ORDER_SYNC_ORDER_MISMATCH",
      ),
      storeCode: persistedText(
        row.store_code,
        "ORDER_SYNC_ORDER_MISMATCH",
      ),
      deviceCode: persistedText(
        row.device_code,
        "ORDER_SYNC_ORDER_MISMATCH",
      ),
      cashierId: persistedText(
        row.cashier_id,
        "ORDER_SYNC_ORDER_MISMATCH",
      ),
      cashierName: persistedText(
        row.cashier_name,
        "ORDER_SYNC_ORDER_MISMATCH",
      ),
      soldAtIso: persistedText(
        row.sold_at_iso,
        "ORDER_SYNC_ORDER_MISMATCH",
      ),
      state: localOrderState(row.state),
      total: frozenAud(
        persistedInteger(row.total_cents, "ORDER_SYNC_ORDER_MISMATCH"),
      ),
      discount: frozenAud(
        persistedInteger(row.discount_cents, "ORDER_SYNC_ORDER_MISMATCH"),
      ),
      actualAmount: frozenAud(
        persistedInteger(
          row.actual_amount_cents,
          "ORDER_SYNC_ORDER_MISMATCH",
        ),
      ),
      originalOrderGuid: persistedNullableText(
        row.original_order_guid,
        "ORDER_SYNC_ORDER_MISMATCH",
      ),
    });
    if (
      persisted.orderGuid !== order.orderGuid ||
      persisted.localSequence !== order.localSequence ||
      persisted.storeCode !== order.storeCode ||
      persisted.deviceCode !== order.deviceCode ||
      persisted.cashierId !== order.cashierId ||
      persisted.cashierName !== order.cashierName ||
      persisted.soldAtIso !== order.soldAtIso ||
      persisted.state !== order.state ||
      !sameMoney(persisted.total, order.total) ||
      !sameMoney(persisted.discount, order.discount) ||
      !sameMoney(persisted.actualAmount, order.actualAmount) ||
      persisted.originalOrderGuid !== order.originalOrderGuid
    ) {
      throw materialError("ORDER_SYNC_ORDER_MISMATCH");
    }
    return persisted;
  }

  private async readPersistedLines(
    order: LocalOrder,
  ): Promise<readonly CartLine[]> {
    const rows = await this.connection.getAll<OrderLineRow>(
      `SELECT line_id, order_guid, line_sequence, product_code,
        item_number, lookup_code, display_name, quantity,
        unit_price_cents, discount_cents, actual_amount_cents,
        price_source, line_kind, return_source_key,
        original_order_guid, original_order_detail_guid,
        reference_code, sync_price_source
       FROM local_order_lines
       WHERE order_guid = ?
       ORDER BY line_sequence`,
      [order.orderGuid],
    );
    if (rows.length !== order.lines.length) {
      throw materialError("ORDER_SYNC_ORDER_MISMATCH");
    }
    let previousSequence = 0;
    return rows.map((row, index) => {
      const sequence = persistedInteger(
        row.line_sequence,
        "ORDER_SYNC_ORDER_MISMATCH",
      );
      if (
        sequence <= previousSequence ||
        persistedText(row.order_guid, "ORDER_SYNC_ORDER_MISMATCH") !==
          order.orderGuid
      ) {
        throw materialError("ORDER_SYNC_ORDER_MISMATCH");
      }
      previousSequence = sequence;
      const line = persistedLine(row);
      const input = order.lines[index];
      if (!input || !sameLine(line, input)) {
        throw materialError("ORDER_SYNC_ORDER_MISMATCH");
      }
      if (!sameLineSyncProvenance(line, input)) {
        throw materialError("ORDER_SYNC_LINE_PROVENANCE_MISMATCH");
      }
      return line;
    });
  }

  private async resolveTender(
    order: LocalOrder,
    input: OrderTender,
    row: TenderAttemptRow,
    linklyEnvironmentInput: string | null,
    isLedgerReversalMember = false,
  ): Promise<OrderTender> {
    const tenderGuid = persistedText(
      row.tender_guid,
      "ORDER_SYNC_TENDER_MISMATCH",
    );
    const method = tenderMethod(row.tender_method);
    const amountCents = persistedInteger(
      row.tender_amount_cents,
      "ORDER_SYNC_TENDER_MISMATCH",
    );
    const tender = frozenTender(
      {
        tenderGuid,
        method,
        amount: frozenAud(amountCents),
        reference: null,
        reservationToken: null,
      },
      null,
      null,
    );
    if (
      persistedText(
        row.tender_order_guid,
        "ORDER_SYNC_TENDER_MISMATCH",
      ) !== order.orderGuid ||
      tender.tenderGuid !== input.tenderGuid ||
      tender.method !== input.method ||
      !sameMoney(tender.amount, input.amount)
    ) {
      throw materialError("ORDER_SYNC_TENDER_MISMATCH");
    }

    // 本地历史必须保留 source/reversal 两端，但 provider release 后 source
    // 的受保护状态已不再是 approved；严格剔除与 released 校验只在同步边界执行。
    if (isLedgerReversalMember) return tender;

    if (method === "cash") {
      if (persistedNullableText(
        row.payment_attempt_id,
        "ORDER_SYNC_ATTEMPT_MISMATCH",
      ) !== null) {
        throw materialError("ORDER_SYNC_ATTEMPT_MISMATCH");
      }
      return tender;
    }

    if (method === "card") {
      const manualEvidence = await readManualPaymentSyncEvidence(this.connection, {
        tenderGuid: tender.tenderGuid, orderGuid: order.orderGuid, storeCode: order.storeCode,
        deviceCode: order.deviceCode, amountCents: tender.amount.cents,
      });
      if (manualEvidence) return Object.freeze({ ...tender, reference: `MANUAL_CARD:${String(row.payment_attempt_id)}` });
    }
    const attempt = readApprovedAttempt(row, order, tender);
    const bindings = await this.readReturnBindings(tender.tenderGuid);
    if (attempt.operation === "purchase") {
      if (bindings.length !== 0) {
        throw materialError("ORDER_SYNC_RETURN_BINDING_MISMATCH");
      }
    } else if (bindings.length !== 1) {
      throw materialError("ORDER_SYNC_RETURN_BINDING_MISMATCH");
    }

    if (attempt.provider === "square") {
      return this.resolveSquare(
        tender,
        attempt,
        bindings[0] ?? null,
        order,
      );
    }
    if (attempt.provider === "linkly-cloud") {
      return this.resolveLinkly(
        tender,
        attempt,
        bindings[0] ?? null,
        order,
        linklyEnvironmentInput,
      );
    }
    if (attempt.provider === "manual-card") {
      if (attempt.operation !== "purchase" || attempt.txnRef !== `MANUAL:${attempt.attemptId}` || attempt.paymentId !== null || attempt.sessionId !== null || attempt.rfn !== null || attempt.checkoutId !== null) throw materialError("ORDER_SYNC_ATTEMPT_MISMATCH");
      return frozenTender(tender, attempt.txnRef, null);
    }
    return this.resolveVoucher(
      tender,
      attempt,
      bindings[0] ?? null,
      order,
    );
  }

  private async resolveSquare(
    tender: OrderTender,
    attempt: ApprovedAttempt,
    binding: ReturnBindingRow | null,
    order: LocalOrder,
  ): Promise<OrderTender> {
    if (
      tender.method !== "card" ||
      attempt.provider !== "square" ||
      attempt.paymentId === null ||
      attempt.sessionId !== null ||
      attempt.txnRef !== null ||
      attempt.rfn !== null
    ) {
      throw materialError("ORDER_SYNC_ATTEMPT_MISMATCH");
    }
    if (attempt.operation === "purchase") {
      return frozenTender(tender, `SQ:${attempt.paymentId}`, null);
    }
    const returnIdentity = requireReturnBinding(
      binding,
      order,
      tender,
      attempt,
    );
    const context = await resolveProtectedMaterial(
      () => this.options.returnCapacityVault
        .resolveProtectedContext(returnIdentity.capacityId),
      "ORDER_SYNC_RETURN_CONTEXT_MISMATCH",
    );
    if (
      !isExactContext(context, ["version", "provider", "paymentId"]) ||
      context.version !== 1 ||
      context.provider !== "square" ||
      context.paymentId !== attempt.paymentId ||
      attempt.responseCode === null
    ) {
      throw materialError("ORDER_SYNC_RETURN_CONTEXT_MISMATCH");
    }
    return frozenTender(
      tender,
      formatCardRefundReference(
        `SQRF:${attempt.responseCode}`,
        `SQ:${context.paymentId}`,
      ),
      null,
    );
  }

  private async resolveLinkly(
    tender: OrderTender,
    attempt: ApprovedAttempt,
    binding: ReturnBindingRow | null,
    order: LocalOrder,
    linklyEnvironmentInput: string | null,
  ): Promise<OrderTender> {
    const linklyEnvironment = environment(linklyEnvironmentInput);
    if (
      tender.method !== "card" ||
      attempt.provider !== "linkly-cloud" ||
      attempt.checkoutId !== null ||
      attempt.paymentId !== null ||
      attempt.sessionId === null ||
      attempt.txnRef === null ||
      attempt.rfn === null
    ) {
      throw materialError("ORDER_SYNC_ATTEMPT_MISMATCH");
    }
    if (attempt.operation === "purchase") {
      return frozenTender(
        tender,
        formatLinklyReference(
          attempt.txnRef,
          attempt.sessionId,
          linklyEnvironment,
          attempt.rfn,
        ),
        null,
      );
    }

    const returnIdentity = requireReturnBinding(
      binding,
      order,
      tender,
      attempt,
    );
    const context = await resolveProtectedMaterial(
      () => this.options.returnCapacityVault
        .resolveProtectedContext(returnIdentity.capacityId),
      "ORDER_SYNC_RETURN_CONTEXT_MISMATCH",
    );
    if (
      !isExactContext(
        context,
        ["version", "provider", "rfn", "originalReference"],
      ) ||
      context.version !== 1 ||
      context.provider !== "linkly-cloud" ||
      context.rfn !== attempt.rfn ||
      typeof context.originalReference !== "string" ||
      !validOriginalLinklyReference(
        context.originalReference,
        linklyEnvironment,
      )
    ) {
      throw materialError("ORDER_SYNC_RETURN_CONTEXT_MISMATCH");
    }
    // 当前 payment_attempts.rfn 是原交易 RFN，不能冒充本次退款 RFN。
    const refundReference = formatLinklyReference(
      attempt.txnRef,
      attempt.sessionId,
      linklyEnvironment,
      null,
    );
    return frozenTender(
      tender,
      formatCardRefundReference(
        refundReference,
        context.originalReference,
      ),
      null,
    );
  }

  private async resolveVoucher(
    tender: OrderTender,
    attempt: ApprovedAttempt,
    binding: ReturnBindingRow | null,
    order: LocalOrder,
  ): Promise<OrderTender> {
    if (
      tender.method !== "voucher" ||
      attempt.provider !== "voucher" ||
      attempt.checkoutId !== null ||
      attempt.paymentId !== null ||
      attempt.sessionId !== null ||
      attempt.txnRef !== null ||
      attempt.rfn !== null
    ) {
      throw materialError("ORDER_SYNC_ATTEMPT_MISMATCH");
    }
    if (attempt.operation === "refund") {
      const returnIdentity = requireReturnBinding(
        binding,
        order,
        tender,
        attempt,
      );
      // 原礼券额度原路退回时核对受保护 context；代替刷卡/现金额度签发的新券不引用原支付。
      if (returnIdentity.capacityMethod === "voucher") {
        const context = await resolveProtectedMaterial(
          () => this.options.returnCapacityVault
            .resolveProtectedContext(returnIdentity.capacityId),
          "ORDER_SYNC_RETURN_CONTEXT_MISMATCH",
        );
        if (
          !isExactContext(context, ["version", "provider"]) ||
          context.version !== 1 ||
          context.provider !== "voucher"
        ) {
          throw materialError("ORDER_SYNC_RETURN_CONTEXT_MISMATCH");
        }
      }
    }

    const state = await resolveProtectedMaterial(
      () => this.options.voucherProtectedTokens.getByAttempt(
        attempt.attemptId,
      ),
      "ORDER_SYNC_VOUCHER_STATE_MISMATCH",
    );
    if (
      !state ||
      state.phase !== "approved" ||
      state.attemptId !== attempt.attemptId ||
      state.idempotencyKey !== attempt.idempotencyKey ||
      state.orderGuid !== order.orderGuid ||
      state.operation !== attempt.operation ||
      state.storeCode !== order.storeCode ||
      state.cashierId !== order.cashierId ||
      state.amountCents !== tender.amount.cents ||
      typeof state.voucherCode !== "string" ||
      !validProviderPart(state.voucherCode, 512) ||
      (attempt.operation === "purchase" &&
        (typeof state.reservationToken !== "string" ||
          !validProviderPart(state.reservationToken, 4_096))) ||
      (attempt.operation === "refund" && state.reservationToken !== null)
    ) {
      throw materialError("ORDER_SYNC_VOUCHER_STATE_MISMATCH");
    }
    return frozenTender(
      tender,
      state.voucherCode,
      attempt.operation === "purchase" ? state.reservationToken : null,
    );
  }

  private async readLedgerReversalMemberGuids(
    orderGuid: string,
  ): Promise<ReadonlySet<string>> {
    const rows = await this.connection.getAll<ReversalMemberRow>(
      `SELECT source_tender_guid, reversal_tender_guid
       FROM payment_tender_reversal_links
       WHERE order_guid = ?`,
      [inputText(orderGuid, "ORDER_SYNC_ORDER_MISMATCH")],
    );
    const members = new Set<string>();
    for (const row of rows) {
      members.add(
        persistedText(
          row.source_tender_guid,
          "ORDER_SYNC_TENDER_MISMATCH",
        ),
      );
      members.add(
        persistedText(
          row.reversal_tender_guid,
          "ORDER_SYNC_TENDER_MISMATCH",
        ),
      );
    }
    return members;
  }

  private async projectTenderReversalsForSync(
    order: LocalOrder,
  ): Promise<LocalOrder> {
    const actions =
      await this.connection.getAll<VoucherReversalActionSyncRow>(
        `SELECT action_id, order_guid, source_tender_guid,
          source_attempt_id, amount_cents, reason, state,
          attempt_count, last_error_code, reversal_tender_guid,
          terminal_audit_event_id
         FROM voucher_tender_reversal_actions
         WHERE order_guid = ?
         ORDER BY created_at_iso, action_id`,
        [order.orderGuid],
      );
    for (const action of actions) {
      const state = persistedText(
        action.state,
        "ORDER_SYNC_VOUCHER_REVERSAL_MISMATCH",
      );
      if (
        state === "Prepared" ||
        state === "Submitted" ||
        state === "Unknown" ||
        state === "Blocked"
      ) {
        throw materialError(
          "ORDER_SYNC_VOUCHER_REVERSAL_UNRESOLVED",
        );
      }
      if (state !== "Reversed") {
        throw materialError("ORDER_SYNC_VOUCHER_REVERSAL_MISMATCH");
      }
    }

    const links = await this.connection.getAll<TenderReversalSyncRow>(
      `SELECT
        link.order_guid AS link_order_guid,
        link.action_id AS link_action_id,
        link.source_tender_guid AS link_source_tender_guid,
        link.reversal_tender_guid AS link_reversal_tender_guid,
        source.order_guid AS source_order_guid,
        source.method AS source_method,
        source.amount_cents AS source_amount_cents,
        source.payment_attempt_id AS source_payment_attempt_id,
        reversal.order_guid AS reversal_order_guid,
        reversal.method AS reversal_method,
        reversal.amount_cents AS reversal_amount_cents,
        reversal.payment_attempt_id AS reversal_payment_attempt_id,
        action.action_id,
        action.order_guid AS action_order_guid,
        action.source_tender_guid AS action_source_tender_guid,
        action.source_attempt_id AS action_source_attempt_id,
        action.amount_cents AS action_amount_cents,
        action.reason AS action_reason,
        action.state AS action_state,
        action.attempt_count AS action_attempt_count,
        action.last_error_code AS action_last_error_code,
        action.reversal_tender_guid AS action_reversal_tender_guid,
        action.terminal_audit_event_id,
        attempt.attempt_id,
        attempt.idempotency_key,
        attempt.order_guid AS attempt_order_guid,
        attempt.provider,
        attempt.operation,
        attempt.amount_cents AS attempt_amount_cents,
        attempt.state AS attempt_state,
        protected.attempt_id AS protected_attempt_id,
        protected.idempotency_key AS protected_idempotency_key,
        protected.order_guid AS protected_order_guid,
        audit.event_id AS audit_event_id,
        audit.event_type AS audit_event_type,
        audit.order_guid AS audit_order_guid,
        audit.correlation_id AS audit_correlation_id,
        audit.payload_json AS audit_payload_json
       FROM payment_tender_reversal_links link
       INNER JOIN order_tenders source
         ON source.tender_guid = link.source_tender_guid
       INNER JOIN order_tenders reversal
         ON reversal.tender_guid = link.reversal_tender_guid
       LEFT JOIN voucher_tender_reversal_actions action
         ON action.action_id = link.action_id
        AND action.order_guid = link.order_guid
       LEFT JOIN payment_attempts attempt
         ON attempt.attempt_id = source.payment_attempt_id
       LEFT JOIN voucher_protected_attempt_states protected
         ON protected.attempt_id = attempt.attempt_id
       LEFT JOIN audit_events audit
         ON audit.event_id = action.terminal_audit_event_id
       WHERE link.order_guid = ?
       ORDER BY link.created_at_iso, link.action_id`,
      [order.orderGuid],
    );
    if (actions.length === 0 && links.length === 0) return order;

    const matchedActions = new Set<string>();
    const excludedTenderGuids = new Set<string>();
    for (const row of links) {
      const sourceMethod = reversalMethod(row.source_method);
      const reversalTenderMethod = reversalMethod(row.reversal_method);
      if (
        sourceMethod === "card" ||
        reversalTenderMethod === "card"
      ) {
        throw materialError("ORDER_SYNC_CARD_REVERSAL_UNSUPPORTED");
      }
      if (
        sourceMethod === "cash" &&
        reversalTenderMethod === "cash"
      ) {
        continue;
      }
      if (
        sourceMethod !== "voucher" ||
        reversalTenderMethod !== "voucher"
      ) {
        throw materialError("ORDER_SYNC_VOUCHER_REVERSAL_MISMATCH");
      }

      const actionId = persistedText(
        row.action_id,
        "ORDER_SYNC_VOUCHER_REVERSAL_MISMATCH",
      );
      const sourceTenderGuid = persistedText(
        row.link_source_tender_guid,
        "ORDER_SYNC_VOUCHER_REVERSAL_MISMATCH",
      );
      const reversalTenderGuid = persistedText(
        row.link_reversal_tender_guid,
        "ORDER_SYNC_VOUCHER_REVERSAL_MISMATCH",
      );
      const sourceAmountCents = persistedInteger(
        row.source_amount_cents,
        "ORDER_SYNC_VOUCHER_REVERSAL_MISMATCH",
      );
      const attemptId = persistedText(
        row.attempt_id,
        "ORDER_SYNC_VOUCHER_REVERSAL_MISMATCH",
      );
      const idempotencyKey = persistedText(
        row.idempotency_key,
        "ORDER_SYNC_VOUCHER_REVERSAL_MISMATCH",
      );
      const reason = persistedText(
        row.action_reason,
        "ORDER_SYNC_VOUCHER_REVERSAL_MISMATCH",
      );
      const sourceTender = order.tenders.find(
        (tender) => tender.tenderGuid === sourceTenderGuid,
      );
      const reversalTender = order.tenders.find(
        (tender) => tender.tenderGuid === reversalTenderGuid,
      );
      if (
        persistedText(
          row.link_order_guid,
          "ORDER_SYNC_VOUCHER_REVERSAL_MISMATCH",
        ) !== order.orderGuid ||
        persistedText(
          row.source_order_guid,
          "ORDER_SYNC_VOUCHER_REVERSAL_MISMATCH",
        ) !== order.orderGuid ||
        persistedText(
          row.reversal_order_guid,
          "ORDER_SYNC_VOUCHER_REVERSAL_MISMATCH",
        ) !== order.orderGuid ||
        persistedText(
          row.link_action_id,
          "ORDER_SYNC_VOUCHER_REVERSAL_MISMATCH",
        ) !== actionId ||
        persistedText(
          row.action_order_guid,
          "ORDER_SYNC_VOUCHER_REVERSAL_MISMATCH",
        ) !== order.orderGuid ||
        persistedText(
          row.action_source_tender_guid,
          "ORDER_SYNC_VOUCHER_REVERSAL_MISMATCH",
        ) !== sourceTenderGuid ||
        persistedText(
          row.action_reversal_tender_guid,
          "ORDER_SYNC_VOUCHER_REVERSAL_MISMATCH",
        ) !== reversalTenderGuid ||
        row.action_state !== "Reversed" ||
        persistedInteger(
          row.action_attempt_count,
          "ORDER_SYNC_VOUCHER_REVERSAL_MISMATCH",
        ) <= 0 ||
        row.action_last_error_code !== null ||
        sourceAmountCents <= 0 ||
        persistedInteger(
          row.reversal_amount_cents,
          "ORDER_SYNC_VOUCHER_REVERSAL_MISMATCH",
        ) !== -sourceAmountCents ||
        persistedInteger(
          row.action_amount_cents,
          "ORDER_SYNC_VOUCHER_REVERSAL_MISMATCH",
        ) !== sourceAmountCents ||
        (reason !== "SALE" &&
          reason !== "CARD_FAILURE_AUTO_RELEASE") ||
        row.reversal_payment_attempt_id !== null ||
        persistedText(
          row.source_payment_attempt_id,
          "ORDER_SYNC_VOUCHER_REVERSAL_MISMATCH",
        ) !== attemptId ||
        persistedText(
          row.action_source_attempt_id,
          "ORDER_SYNC_VOUCHER_REVERSAL_MISMATCH",
        ) !== attemptId ||
        persistedText(
          row.attempt_order_guid,
          "ORDER_SYNC_VOUCHER_REVERSAL_MISMATCH",
        ) !== order.orderGuid ||
        row.provider !== "voucher" ||
        row.operation !== "purchase" ||
        row.attempt_state !== "Approved" ||
        persistedInteger(
          row.attempt_amount_cents,
          "ORDER_SYNC_VOUCHER_REVERSAL_MISMATCH",
        ) !== sourceAmountCents ||
        persistedText(
          row.protected_attempt_id,
          "ORDER_SYNC_VOUCHER_REVERSAL_MISMATCH",
        ) !== attemptId ||
        persistedText(
          row.protected_idempotency_key,
          "ORDER_SYNC_VOUCHER_REVERSAL_MISMATCH",
        ) !== idempotencyKey ||
        persistedText(
          row.protected_order_guid,
          "ORDER_SYNC_VOUCHER_REVERSAL_MISMATCH",
        ) !== order.orderGuid ||
        !sourceTender ||
        sourceTender.method !== "voucher" ||
        sourceTender.amount.cents !== sourceAmountCents ||
        !reversalTender ||
        reversalTender.method !== "voucher" ||
        reversalTender.amount.cents !== -sourceAmountCents ||
        !validVoucherReversalAudit(row, {
          actionId,
          orderGuid: order.orderGuid,
          sourceTenderGuid,
          sourceAttemptId: attemptId,
          reversalTenderGuid,
          reason,
          amountCents: sourceAmountCents,
        })
      ) {
        throw materialError("ORDER_SYNC_VOUCHER_REVERSAL_MISMATCH");
      }

      const state = await resolveProtectedMaterial(
        () => this.options.voucherProtectedTokens.getByAttempt(attemptId),
        "ORDER_SYNC_VOUCHER_REVERSAL_MISMATCH",
      );
      if (
        !state ||
        state.phase !== "released" ||
        state.attemptId !== attemptId ||
        state.idempotencyKey !== idempotencyKey ||
        state.orderGuid !== order.orderGuid ||
        state.operation !== "purchase" ||
        state.storeCode !== order.storeCode ||
        state.cashierId !== order.cashierId ||
        state.amountCents !== sourceAmountCents ||
        typeof state.voucherCode !== "string" ||
        !validProviderPart(state.voucherCode, 512) ||
        typeof state.reservationToken !== "string" ||
        !validProviderPart(state.reservationToken, 4_096)
      ) {
        throw materialError("ORDER_SYNC_VOUCHER_REVERSAL_MISMATCH");
      }
      if (matchedActions.has(actionId)) {
        throw materialError("ORDER_SYNC_VOUCHER_REVERSAL_MISMATCH");
      }
      matchedActions.add(actionId);
      excludedTenderGuids.add(sourceTenderGuid);
      excludedTenderGuids.add(reversalTenderGuid);
    }

    if (matchedActions.size !== actions.length) {
      throw materialError("ORDER_SYNC_VOUCHER_REVERSAL_MISMATCH");
    }
    return Object.freeze({
      ...order,
      tenders: Object.freeze(
        order.tenders.filter(
          (tender) => !excludedTenderGuids.has(tender.tenderGuid),
        ),
      ),
    });
  }

  private readReturnBindings(
    tenderGuid: string,
  ): Promise<readonly ReturnBindingRow[]> {
    return this.connection.getAll<ReturnBindingRow>(
      `SELECT
        binding.tender_guid,
        binding.action_id AS binding_action_id,
        binding.allocation_id AS binding_allocation_id,
        binding.external_attempt_kind AS binding_attempt_kind,
        binding.external_action_id AS binding_external_action_id,
        binding.durable_attempt_id AS binding_durable_attempt_id,
        allocation.execution_kind,
        allocation.method AS allocation_method,
        allocation.signed_amount_cents,
        allocation.capacity_id,
        allocation.original_order_guid AS allocation_original_order_guid,
        allocation.external_attempt_kind AS allocation_attempt_kind,
        allocation.external_action_id AS allocation_external_action_id,
        allocation.durable_attempt_id AS allocation_durable_attempt_id,
        allocation.status AS allocation_status,
        allocation.capacity_reservation_state,
        action.return_order_guid,
        action.online AS action_online,
        action.store_code AS action_store_code,
        action.device_code AS action_device_code,
        action.cashier_id AS action_cashier_id,
        action.state AS action_state,
        capacity.original_order_guid AS capacity_original_order_guid,
        capacity.method AS capacity_method,
        capacity.original_amount_cents
       FROM return_tender_attempt_bindings binding
       INNER JOIN return_action_allocations allocation
         ON allocation.action_id = binding.action_id
        AND allocation.allocation_id = binding.allocation_id
       INNER JOIN return_actions action
         ON action.action_id = binding.action_id
       INNER JOIN return_tender_capacities capacity
         ON capacity.capacity_id = allocation.capacity_id
       WHERE binding.tender_guid = ?`,
      [inputText(tenderGuid, "ORDER_SYNC_TENDER_MISMATCH")],
    );
  }
}

type PersistedOrderRoot = Omit<LocalOrder, "lines" | "tenders">;

type OrderIdentityRow = Readonly<{
  order_guid: unknown;
  local_sequence: unknown;
  store_code: unknown;
  device_code: unknown;
  cashier_id: unknown;
  cashier_name: unknown;
  sold_at_iso: unknown;
  state: unknown;
  total_cents: unknown;
  discount_cents: unknown;
  actual_amount_cents: unknown;
  original_order_guid: unknown;
}>;

type OrderLineRow = Readonly<{
  line_id: unknown;
  order_guid: unknown;
  line_sequence: unknown;
  product_code: unknown;
  item_number: unknown;
  lookup_code: unknown;
  display_name: unknown;
  quantity: unknown;
  unit_price_cents: unknown;
  discount_cents: unknown;
  actual_amount_cents: unknown;
  price_source: unknown;
  line_kind: unknown;
  return_source_key: unknown;
  original_order_guid: unknown;
  original_order_detail_guid: unknown;
  reference_code: unknown;
  sync_price_source: unknown;
}>;

type TenderAttemptRow = Readonly<{
  tender_guid: unknown;
  tender_order_guid: unknown;
  tender_method: unknown;
  tender_amount_cents: unknown;
  payment_attempt_id: unknown;
  attempt_id: unknown;
  idempotency_key: unknown;
  attempt_order_guid: unknown;
  provider: unknown;
  operation: unknown;
  attempt_amount_cents: unknown;
  attempt_state: unknown;
  checkout_id: unknown;
  payment_id: unknown;
  session_id: unknown;
  txn_ref: unknown;
  rfn: unknown;
  provider_response_code: unknown;
}>;

type ReversalMemberRow = Readonly<{
  source_tender_guid: unknown;
  reversal_tender_guid: unknown;
}>;

type VoucherReversalActionSyncRow = Readonly<{
  action_id: unknown;
  order_guid: unknown;
  source_tender_guid: unknown;
  source_attempt_id: unknown;
  amount_cents: unknown;
  reason: unknown;
  state: unknown;
  attempt_count: unknown;
  last_error_code: unknown;
  reversal_tender_guid: unknown;
  terminal_audit_event_id: unknown;
}>;

type TenderReversalSyncRow = Readonly<{
  link_order_guid: unknown;
  link_action_id: unknown;
  link_source_tender_guid: unknown;
  link_reversal_tender_guid: unknown;
  source_order_guid: unknown;
  source_method: unknown;
  source_amount_cents: unknown;
  source_payment_attempt_id: unknown;
  reversal_order_guid: unknown;
  reversal_method: unknown;
  reversal_amount_cents: unknown;
  reversal_payment_attempt_id: unknown;
  action_id: unknown;
  action_order_guid: unknown;
  action_source_tender_guid: unknown;
  action_source_attempt_id: unknown;
  action_amount_cents: unknown;
  action_reason: unknown;
  action_state: unknown;
  action_attempt_count: unknown;
  action_last_error_code: unknown;
  action_reversal_tender_guid: unknown;
  terminal_audit_event_id: unknown;
  attempt_id: unknown;
  idempotency_key: unknown;
  attempt_order_guid: unknown;
  provider: unknown;
  operation: unknown;
  attempt_amount_cents: unknown;
  attempt_state: unknown;
  protected_attempt_id: unknown;
  protected_idempotency_key: unknown;
  protected_order_guid: unknown;
  audit_event_id: unknown;
  audit_event_type: unknown;
  audit_order_guid: unknown;
  audit_correlation_id: unknown;
  audit_payload_json: unknown;
}>;

type VoucherReversalAuditIdentity = Readonly<{
  actionId: string;
  orderGuid: string;
  sourceTenderGuid: string;
  sourceAttemptId: string;
  reversalTenderGuid: string;
  reason: string;
  amountCents: number;
}>;

type ReturnBindingRow = Readonly<{
  tender_guid: unknown;
  binding_action_id: unknown;
  binding_allocation_id: unknown;
  binding_attempt_kind: unknown;
  binding_external_action_id: unknown;
  binding_durable_attempt_id: unknown;
  execution_kind: unknown;
  allocation_method: unknown;
  signed_amount_cents: unknown;
  capacity_id: unknown;
  allocation_original_order_guid: unknown;
  allocation_attempt_kind: unknown;
  allocation_external_action_id: unknown;
  allocation_durable_attempt_id: unknown;
  allocation_status: unknown;
  capacity_reservation_state: unknown;
  return_order_guid: unknown;
  action_online: unknown;
  action_store_code: unknown;
  action_device_code: unknown;
  action_cashier_id: unknown;
  action_state: unknown;
  capacity_original_order_guid: unknown;
  capacity_method: unknown;
  original_amount_cents: unknown;
}>;

type ApprovedAttempt = Readonly<{
  attemptId: string;
  idempotencyKey: string;
  provider: "square" | "linkly-cloud" | "voucher" | "manual-card";
  operation: "purchase" | "refund";
  checkoutId: string | null;
  paymentId: string | null;
  sessionId: string | null;
  txnRef: string | null;
  rfn: string | null;
  responseCode: string | null;
}>;

function readApprovedAttempt(
  row: TenderAttemptRow,
  order: LocalOrder,
  tender: OrderTender,
): ApprovedAttempt {
  const paymentAttemptId = persistedNullableText(
    row.payment_attempt_id,
    "ORDER_SYNC_ATTEMPT_MISMATCH",
  );
  const attemptId = persistedNullableText(
    row.attempt_id,
    "ORDER_SYNC_ATTEMPT_MISMATCH",
  );
  const provider = paymentProvider(row.provider);
  const operation = paymentOperation(row.operation);
  if (
    paymentAttemptId === null ||
    attemptId === null ||
    paymentAttemptId !== attemptId ||
    persistedText(
      row.attempt_order_guid,
      "ORDER_SYNC_ATTEMPT_MISMATCH",
    ) !== order.orderGuid ||
    persistedInteger(
      row.attempt_amount_cents,
      "ORDER_SYNC_ATTEMPT_MISMATCH",
    ) !== tender.amount.cents ||
    persistedText(row.attempt_state, "ORDER_SYNC_ATTEMPT_MISMATCH") !==
      "Approved" ||
    (operation === "purchase" && tender.amount.cents <= 0) ||
    (operation === "refund" && tender.amount.cents >= 0) ||
    (tender.method === "card" &&
      provider !== "square" &&
      provider !== "linkly-cloud" && provider !== "manual-card") ||
    (tender.method === "voucher" && provider !== "voucher")
  ) {
    throw materialError("ORDER_SYNC_ATTEMPT_MISMATCH");
  }
  return {
    attemptId,
    idempotencyKey: persistedText(
      row.idempotency_key,
      "ORDER_SYNC_ATTEMPT_MISMATCH",
    ),
    provider,
    operation,
    checkoutId: persistedNullableProviderPart(row.checkout_id),
    paymentId: persistedNullableProviderPart(row.payment_id),
    sessionId: persistedNullableProviderPart(row.session_id),
    txnRef: persistedNullableProviderPart(row.txn_ref),
    rfn: persistedNullableProviderPart(row.rfn),
    responseCode: persistedNullableProviderPart(
      row.provider_response_code,
    ),
  };
}

function requireReturnBinding(
  row: ReturnBindingRow | null,
  order: LocalOrder,
  tender: OrderTender,
  attempt: ApprovedAttempt,
): Readonly<{ capacityId: string; capacityMethod: OrderTender["method"] }> {
  if (!row) throw materialError("ORDER_SYNC_RETURN_BINDING_MISMATCH");
  const bindingActionId = persistedText(
    row.binding_action_id,
    "ORDER_SYNC_RETURN_BINDING_MISMATCH",
  );
  const bindingAllocationId = persistedText(
    row.binding_allocation_id,
    "ORDER_SYNC_RETURN_BINDING_MISMATCH",
  );
  const bindingExternalActionId = persistedText(
    row.binding_external_action_id,
    "ORDER_SYNC_RETURN_BINDING_MISMATCH",
  );
  const capacityId = persistedText(
    row.capacity_id,
    "ORDER_SYNC_RETURN_BINDING_MISMATCH",
  );
  const originalOrderGuid = persistedText(
    row.allocation_original_order_guid,
    "ORDER_SYNC_RETURN_BINDING_MISMATCH",
  );
  if (
    persistedText(
      row.tender_guid,
      "ORDER_SYNC_RETURN_BINDING_MISMATCH",
    ) !== tender.tenderGuid ||
    bindingActionId.length > 128 ||
    bindingAllocationId.length > 128 ||
    persistedText(
      row.binding_attempt_kind,
      "ORDER_SYNC_RETURN_BINDING_MISMATCH",
    ) !== "payment-provider" ||
    persistedText(
      row.binding_durable_attempt_id,
      "ORDER_SYNC_RETURN_BINDING_MISMATCH",
    ) !== attempt.attemptId ||
    persistedText(
      row.execution_kind,
      "ORDER_SYNC_RETURN_BINDING_MISMATCH",
    ) !== "online-refund" ||
    tenderMethod(row.allocation_method) !== tender.method ||
    persistedInteger(
      row.signed_amount_cents,
      "ORDER_SYNC_RETURN_BINDING_MISMATCH",
    ) !== tender.amount.cents ||
    persistedText(
      row.allocation_attempt_kind,
      "ORDER_SYNC_RETURN_BINDING_MISMATCH",
    ) !== "payment-provider" ||
    persistedText(
      row.allocation_external_action_id,
      "ORDER_SYNC_RETURN_BINDING_MISMATCH",
    ) !== bindingExternalActionId ||
    persistedText(
      row.allocation_durable_attempt_id,
      "ORDER_SYNC_RETURN_BINDING_MISMATCH",
    ) !== attempt.attemptId ||
    persistedText(
      row.allocation_status,
      "ORDER_SYNC_RETURN_BINDING_MISMATCH",
    ) !== "completed" ||
    persistedText(
      row.capacity_reservation_state,
      "ORDER_SYNC_RETURN_BINDING_MISMATCH",
    ) !== "Committed" ||
    persistedText(
      row.return_order_guid,
      "ORDER_SYNC_RETURN_BINDING_MISMATCH",
    ) !== order.orderGuid ||
    persistedInteger(
      row.action_online,
      "ORDER_SYNC_RETURN_BINDING_MISMATCH",
    ) !== 1 ||
    persistedText(
      row.action_store_code,
      "ORDER_SYNC_RETURN_BINDING_MISMATCH",
    ) !== order.storeCode ||
    persistedText(
      row.action_device_code,
      "ORDER_SYNC_RETURN_BINDING_MISMATCH",
    ) !== order.deviceCode ||
    persistedText(
      row.action_cashier_id,
      "ORDER_SYNC_RETURN_BINDING_MISMATCH",
    ) !== order.cashierId ||
    persistedText(
      row.action_state,
      "ORDER_SYNC_RETURN_BINDING_MISMATCH",
    ) !== "completed" ||
    persistedText(
      row.capacity_original_order_guid,
      "ORDER_SYNC_RETURN_BINDING_MISMATCH",
    ) !== originalOrderGuid ||
    !isReturnCapacityMethodAllowed(
      tenderMethod(row.capacity_method),
      tender.method,
    ) ||
    persistedInteger(
      row.original_amount_cents,
      "ORDER_SYNC_RETURN_BINDING_MISMATCH",
    ) < -tender.amount.cents
  ) {
    throw materialError("ORDER_SYNC_RETURN_BINDING_MISMATCH");
  }
  return { capacityId, capacityMethod: tenderMethod(row.capacity_method) };
}

/**
 * 退款 tender 必须原路绑定同方式额度；唯一例外是礼券代替刷卡/现金额度
 * （签发新券，额度仍按原支付扣减）。
 */
function isReturnCapacityMethodAllowed(
  capacityMethod: OrderTender["method"],
  refundMethod: OrderTender["method"],
): boolean {
  return (
    capacityMethod === refundMethod ||
    (refundMethod === "voucher" &&
      (capacityMethod === "card" || capacityMethod === "cash"))
  );
}

function persistedLine(row: OrderLineRow): CartLine {
  const syncProvenance = persistedLineSyncProvenance(row);
  return Object.freeze({
    lineId: persistedText(row.line_id, "ORDER_SYNC_ORDER_MISMATCH"),
    productCode: persistedText(
      row.product_code,
      "ORDER_SYNC_ORDER_MISMATCH",
    ),
    itemNumber: persistedNullableText(
      row.item_number,
      "ORDER_SYNC_ORDER_MISMATCH",
    ),
    lookupCode: persistedText(
      row.lookup_code,
      "ORDER_SYNC_ORDER_MISMATCH",
    ),
    displayName: persistedText(
      row.display_name,
      "ORDER_SYNC_ORDER_MISMATCH",
    ),
    quantity: persistedText(row.quantity, "ORDER_SYNC_ORDER_MISMATCH"),
    unitPrice: frozenAud(
      persistedInteger(
        row.unit_price_cents,
        "ORDER_SYNC_ORDER_MISMATCH",
      ),
    ),
    discount: frozenAud(
      persistedInteger(row.discount_cents, "ORDER_SYNC_ORDER_MISMATCH"),
    ),
    actualAmount: frozenAud(
      persistedInteger(
        row.actual_amount_cents,
        "ORDER_SYNC_ORDER_MISMATCH",
      ),
    ),
    priceSource: priceSource(row.price_source),
    ...(syncProvenance ? { syncProvenance } : {}),
    kind: lineKind(row.line_kind),
    returnSourceKey: persistedNullableText(
      row.return_source_key,
      "ORDER_SYNC_ORDER_MISMATCH",
    ),
    originalOrderGuid: persistedNullableText(
      row.original_order_guid,
      "ORDER_SYNC_ORDER_MISMATCH",
    ),
    originalOrderDetailGuid: persistedNullableText(
      row.original_order_detail_guid,
      "ORDER_SYNC_ORDER_MISMATCH",
    ),
  });
}

function sameLine(left: CartLine, right: CartLine): boolean {
  return left.lineId === right.lineId &&
    left.productCode === right.productCode &&
    left.itemNumber === right.itemNumber &&
    left.lookupCode === right.lookupCode &&
    left.displayName === right.displayName &&
    left.quantity === right.quantity &&
    sameMoney(left.unitPrice, right.unitPrice) &&
    sameMoney(left.discount, right.discount) &&
    sameMoney(left.actualAmount, right.actualAmount) &&
    left.priceSource === right.priceSource &&
    left.kind === right.kind &&
    left.returnSourceKey === right.returnSourceKey &&
    left.originalOrderGuid === right.originalOrderGuid &&
    left.originalOrderDetailGuid === right.originalOrderDetailGuid;
}

function sameLineSyncProvenance(
  left: CartLine,
  right: CartLine,
): boolean {
  const leftProvenance = left.syncProvenance;
  const rightProvenance = right.syncProvenance;
  if (!leftProvenance || !rightProvenance) {
    return leftProvenance === undefined && rightProvenance === undefined;
  }
  return leftProvenance.referenceCode === rightProvenance.referenceCode &&
    leftProvenance.priceSource === rightProvenance.priceSource;
}

function persistedLineSyncProvenance(
  row: OrderLineRow,
): LineSyncProvenance | undefined {
  const referenceCode = row.reference_code;
  const priceSourceValue = row.sync_price_source;
  if (
    (referenceCode === null || referenceCode === undefined) &&
    (priceSourceValue === null || priceSourceValue === undefined)
  ) {
    return undefined;
  }
  if (
    referenceCode === undefined ||
    priceSourceValue === null ||
    priceSourceValue === undefined
  ) {
    throw materialError("ORDER_SYNC_LINE_PROVENANCE_MISMATCH");
  }
  try {
    return normalizeLineSyncProvenance({
      referenceCode:
        referenceCode === null
          ? null
          : persistedText(
            referenceCode,
            "ORDER_SYNC_LINE_PROVENANCE_MISMATCH",
          ),
      priceSource: persistedInteger(
        priceSourceValue,
        "ORDER_SYNC_LINE_PROVENANCE_MISMATCH",
      ),
    });
  } catch {
    throw materialError("ORDER_SYNC_LINE_PROVENANCE_MISMATCH");
  }
}

function frozenTender(
  tender: OrderTender,
  reference: string | null,
  reservationToken: string | null,
): OrderTender {
  return Object.freeze({
    tenderGuid: tender.tenderGuid,
    method: tender.method,
    amount: Object.freeze({
      currency: "AUD" as const,
      cents: tender.amount.cents,
    }),
    reference,
    reservationToken,
  });
}

function frozenAud(cents: number): LocalOrder["total"] {
  return Object.freeze({ currency: "AUD", cents });
}

function formatCardRefundReference(
  refundReference: string,
  originalReference: string,
): string {
  const refund = providerPart(refundReference, 4_096);
  const original = providerPart(originalReference, 4_096);
  return `CARD_REFUND|refund=${utf8Base64(refund)}|original=${utf8Base64(original)}`;
}

function formatLinklyReference(
  txnRef: string,
  sessionId: string,
  environmentValue: LinklyOrderSyncEnvironment,
  rfn: string | null,
): string {
  let reference = `ANZBACKEND:${escapeUriPart(providerPart(txnRef, 512))}`;
  if (rfn !== null) {
    reference += `:${escapeUriPart(providerPart(rfn, 512))}`;
  }
  return `${reference}:session=${escapeUriPart(
    providerPart(sessionId, 512),
  )}:environment=${escapeUriPart(environmentValue)}`;
}

function validOriginalLinklyReference(
  value: string,
  expectedEnvironment: LinklyOrderSyncEnvironment,
): boolean {
  if (!validProviderPart(value, 4_096)) return false;
  if (/^ANZCLOUD:[^\s]+$/u.test(value)) return true;
  if (!/^ANZBACKEND:[^\s]+$/u.test(value)) return false;
  const parts = value.split(":");
  if (
    (parts.length !== 4 && parts.length !== 5) ||
    parts[0]?.toUpperCase() !== "ANZBACKEND"
  ) {
    return false;
  }
  try {
    if (!decodeURIComponent(parts[1] ?? "")) return false;
    if (parts.length === 5 && !decodeURIComponent(parts[2] ?? "")) {
      return false;
    }
    const sessionPart = parts.at(-2);
    const environmentPart = parts.at(-1);
    if (
      !sessionPart?.toLowerCase().startsWith("session=") ||
      !environmentPart?.toLowerCase().startsWith("environment=")
    ) {
      return false;
    }
    const session = decodeURIComponent(sessionPart.slice("session=".length));
    const persistedEnvironment = decodeURIComponent(
      environmentPart.slice("environment=".length),
    );
    return Boolean(session) && persistedEnvironment === expectedEnvironment;
  } catch {
    return false;
  }
}

function utf8Base64(value: string): string {
  const bytes = new TextEncoder().encode(value);
  const alphabet =
    "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
  let result = "";
  for (let index = 0; index < bytes.length; index += 3) {
    const first = bytes[index] ?? 0;
    const second = bytes[index + 1];
    const third = bytes[index + 2];
    const chunk =
      (first << 16) | ((second ?? 0) << 8) | (third ?? 0);
    result += alphabet[(chunk >>> 18) & 63];
    result += alphabet[(chunk >>> 12) & 63];
    result += second === undefined ? "=" : alphabet[(chunk >>> 6) & 63];
    result += third === undefined ? "=" : alphabet[chunk & 63];
  }
  return result;
}

function escapeUriPart(value: string): string {
  return encodeURIComponent(value).replace(
    /[!'()*]/gu,
    (character) =>
      `%${character.charCodeAt(0).toString(16).toUpperCase()}`,
  );
}

function isExactContext(
  context: Readonly<Record<string, unknown>> | null,
  keys: readonly string[],
): context is Readonly<Record<string, unknown>> {
  if (!context) return false;
  const actual = Object.keys(context).sort();
  const expected = [...keys].sort();
  return actual.length === expected.length &&
    actual.every((key, index) => key === expected[index]);
}

function environment(value: string | null): LinklyOrderSyncEnvironment {
  if (value === "Sandbox" || value === "Production") return value;
  throw materialError("ORDER_SYNC_ENVIRONMENT_INVALID");
}

function localOrderState(value: unknown): LocalOrder["state"] {
  if (
    value === "Draft" ||
    value === "Completing" ||
    value === "CompletedLocal" ||
    value === "PendingSync" ||
    value === "Syncing" ||
    value === "Synced" ||
    value === "Blocked403" ||
    value === "Rejected"
  ) {
    return value;
  }
  throw materialError("ORDER_SYNC_ORDER_MISMATCH");
}

function priceSource(value: unknown): CartLine["priceSource"] {
  if (
    value === "catalog" ||
    value === "promotion" ||
    value === "manual" ||
    value === "open-item"
  ) {
    return value;
  }
  throw materialError("ORDER_SYNC_ORDER_MISMATCH");
}

function lineKind(value: unknown): CartLine["kind"] {
  if (value === "sale" || value === "return") return value;
  throw materialError("ORDER_SYNC_ORDER_MISMATCH");
}

function tenderMethod(value: unknown): OrderTender["method"] {
  if (value === "cash" || value === "card" || value === "voucher") {
    return value;
  }
  throw materialError("ORDER_SYNC_TENDER_MISMATCH");
}

function reversalMethod(value: unknown): OrderTender["method"] {
  if (value === "cash" || value === "card" || value === "voucher") {
    return value;
  }
  throw materialError("ORDER_SYNC_VOUCHER_REVERSAL_MISMATCH");
}

function validVoucherReversalAudit(
  row: TenderReversalSyncRow,
  identity: VoucherReversalAuditIdentity,
): boolean {
  try {
    const terminalAuditEventId = persistedText(
      row.terminal_audit_event_id,
      "ORDER_SYNC_VOUCHER_REVERSAL_MISMATCH",
    );
    const auditEventId = persistedText(
      row.audit_event_id,
      "ORDER_SYNC_VOUCHER_REVERSAL_MISMATCH",
    );
    if (
      terminalAuditEventId !== auditEventId ||
      persistedText(
        row.audit_event_type,
        "ORDER_SYNC_VOUCHER_REVERSAL_MISMATCH",
      ) !== "PAYMENT_TENDER_REMOVE" ||
      persistedText(
        row.audit_order_guid,
        "ORDER_SYNC_VOUCHER_REVERSAL_MISMATCH",
      ) !== identity.orderGuid ||
      persistedText(
        row.audit_correlation_id,
        "ORDER_SYNC_VOUCHER_REVERSAL_MISMATCH",
      ) !== identity.actionId
    ) {
      return false;
    }
    const encoded = persistedText(
      row.audit_payload_json,
      "ORDER_SYNC_VOUCHER_REVERSAL_MISMATCH",
    );
    const payload: unknown = JSON.parse(encoded);
    if (
      payload === null ||
      typeof payload !== "object" ||
      Array.isArray(payload)
    ) {
      return false;
    }
    const record = payload as Record<string, unknown>;
    const keys = Object.keys(record).sort();
    const expectedKeys = [
      "action",
      "amountCents",
      "outcome",
      "reason",
      "requestingCashierId",
      "requestingCashierName",
      "requestingUserGuid",
      "reversalTenderGuid",
      "sourceAttemptId",
      "sourceTenderGuid",
    ];
    return keys.length === expectedKeys.length &&
      keys.every((key, index) => key === expectedKeys[index]) &&
      record.action === "payment-tender-remove" &&
      record.outcome === "success" &&
      record.reason === identity.reason &&
      auditActorSnapshotFromPayload(record) !== null &&
      record.amountCents === identity.amountCents &&
      record.sourceTenderGuid === identity.sourceTenderGuid &&
      record.sourceAttemptId === identity.sourceAttemptId &&
      record.reversalTenderGuid === identity.reversalTenderGuid;
  } catch {
    return false;
  }
}

function paymentProvider(
  value: unknown,
): ApprovedAttempt["provider"] {
  if (
    value === "square" ||
    value === "linkly-cloud" ||
    (value === "voucher" || value === "manual-card")
  ) {
    return value;
  }
  throw materialError("ORDER_SYNC_ATTEMPT_MISMATCH");
}

function paymentOperation(
  value: unknown,
): ApprovedAttempt["operation"] {
  if (value === "purchase" || value === "refund") return value;
  throw materialError("ORDER_SYNC_ATTEMPT_MISMATCH");
}

function sameMoney(
  left: LocalOrder["total"],
  right: LocalOrder["total"],
): boolean {
  return left.currency === "AUD" &&
    right.currency === "AUD" &&
    Number.isSafeInteger(left.cents) &&
    Number.isSafeInteger(right.cents) &&
    left.cents === right.cents;
}

function persistedInteger(
  value: unknown,
  code: OrderSyncMaterialErrorCode,
): number {
  const parsed = Number(value);
  if (!Number.isSafeInteger(parsed)) throw materialError(code);
  return parsed;
}

function inputText(
  value: string,
  code: OrderSyncMaterialErrorCode,
): string {
  if (!validProviderPart(value, 512)) throw materialError(code);
  return value;
}

function persistedText(
  value: unknown,
  code: OrderSyncMaterialErrorCode,
): string {
  if (typeof value !== "string" || !validProviderPart(value, 4_096)) {
    throw materialError(code);
  }
  return value;
}

function persistedNullableText(
  value: unknown,
  code: OrderSyncMaterialErrorCode,
): string | null {
  if (value === null || value === undefined) return null;
  return persistedText(value, code);
}

function persistedNullableProviderPart(value: unknown): string | null {
  if (value === null || value === undefined) return null;
  if (typeof value !== "string" || !validProviderPart(value, 4_096)) {
    throw materialError("ORDER_SYNC_ATTEMPT_MISMATCH");
  }
  return value;
}

function providerPart(value: string, maxLength: number): string {
  const normalized = value.trim();
  if (!validProviderPart(normalized, maxLength)) {
    throw materialError("ORDER_SYNC_ATTEMPT_MISMATCH");
  }
  return normalized;
}

function validProviderPart(value: string, maxLength: number): boolean {
  return value.trim() === value &&
    value.length > 0 &&
    value.length <= maxLength &&
    !/[\u0000-\u001f\u007f]/u.test(value);
}

async function resolveProtectedMaterial<T>(
  operation: () => Promise<T>,
  code: OrderSyncMaterialErrorCode,
): Promise<T> {
  try {
    return await operation();
  } catch (error) {
    // 仅确定性的明文完整性错误是稳定业务拒绝；decrypt/DB/IO 必须继续重试。
    if (error instanceof ProtectedMaterialIntegrityError) {
      throw materialError(code);
    }
    throw error;
  }
}

function materialError(
  code: OrderSyncMaterialErrorCode,
): OrderSyncMaterialError {
  return new OrderSyncMaterialError(code);
}
