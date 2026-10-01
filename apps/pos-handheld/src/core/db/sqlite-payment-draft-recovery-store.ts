import {
  auditActorPayload,
  auditActorSnapshotFromPayload,
  createAud,
  type AuditActorSnapshot,
  type CartLine,
  type CartSnapshot,
  type LocalOrder,
  normalizeLineSyncProvenance,
  type LineSyncProvenance,
  type Money,
  type PaymentAttempt,
  type PaymentOperation,
  type PaymentProvider,
  type PricingCartStateSnapshot,
  type RecallActiveBinding,
} from "../contracts";

import type { SqliteConnectionPort } from "@hb/pos-db/core/db/types";

import type { PersistedOrderDraftPort } from "@hb/pos-payments-core/features/payments/payment-attempt-service";
import type { PaymentCheckoutDraft } from "@/features/payments/runtime/payment-checkout-runtime";
import { PricingCart } from "@/features/sales/domain/pricing-cart";

export type PaymentDraftIdentity = Readonly<{
  storeCode: string;
  deviceCode: string;
  cashierId: string;
  cashierName: string;
}>;

export type CreateOrReusePaymentDraftInput = Readonly<{
  draftId: string;
  cart: CartSnapshot;
  pricingState: PricingCartStateSnapshot;
  identity: PaymentDraftIdentity;
  /** 支付开始于召回购物车时，必须与草稿一起耐久保存精确围栏身份。 */
  recallBinding: RecallActiveBinding | null;
  /** 仅首次插入保存；canonical fingerprint 故意不包含可变化的当前时间。 */
  soldAtIso: string;
}>;

export type PaymentDraftMutation = Readonly<{
  replayed: boolean;
  draftId: string;
  orderGuid: string;
  localSequence: number;
  soldAtIso: string;
  state: LocalOrder["state"];
}>;

export type PaymentRecoveryScope = Readonly<{
  storeCode: string;
  deviceCode: string;
}>;

export type PaymentDraftAbandonInput = PaymentRecoveryScope &
  Readonly<{
    actionId: string;
    draftId: string;
    orderGuid: string;
    actor: AuditActorSnapshot;
  }>;

export type PaymentDraftAbandonResult = Readonly<{
  replayed: boolean;
  draftId: string;
  orderGuid: string;
  cart: CartSnapshot;
  pricingState: PricingCartStateSnapshot;
}>;

export type PaymentDraftCancelledCloseInput = PaymentRecoveryScope &
  Readonly<{
    actionId: string;
    orderGuid: string;
    actor: AuditActorSnapshot;
  }>;

export type PaymentDraftCancelledCloseResult = Readonly<{
  replayed: boolean;
  draft: PaymentCheckoutDraft;
}>;

export type RecoveredPaymentBoundAction = Readonly<{
  actionId: string;
  attemptId: string;
  provider: PaymentProvider;
  operation: PaymentOperation;
  amount: Money;
  manualConfirmed?: boolean;
}>;

type RecoveryBase = Readonly<{
  draftId: string | null;
  orderGuid: string;
  originalOrderGuid: string | null;
  localSequence: number;
  soldAtIso: string;
  identity: PaymentDraftIdentity;
  cart: CartSnapshot;
  pricingState: PricingCartStateSnapshot;
  recallBinding: RecallActiveBinding | null;
  boundAction: RecoveredPaymentBoundAction | null;
}>;

export type PreparedPaymentDraftRecovery = RecoveryBase &
  Readonly<{
    kind: "DraftPrepared";
    attemptId: null;
  }>;

export type BlockingPaymentAttemptRecovery = RecoveryBase &
  Readonly<{
    kind: "AttemptBlocking";
    attemptId: string;
    provider: PaymentProvider;
    operation: PaymentOperation;
    state: PaymentAttempt["state"];
    amountCents: number;
  }>;

export type PaymentDraftRecovery =
  | PreparedPaymentDraftRecovery
  | BlockingPaymentAttemptRecovery;

export type PaymentDraftPersistenceIds = Readonly<{
  createOrderGuid(): string;
  createOrderLineGuid(): string;
  createAuditEventId(): string;
}>;

type DraftBindingRow = Readonly<{
  draft_id: unknown;
  request_fingerprint: unknown;
  pricing_state_json: unknown;
  order_guid: unknown;
  store_code: unknown;
  device_code: unknown;
  state: unknown;
  abandon_action_id: unknown;
  close_action_id: unknown;
  close_attempt_id: unknown;
}>;

type CheckoutDraftRow = Readonly<{
  draft_id: unknown;
  request_fingerprint: unknown;
  order_guid: unknown;
  store_code: unknown;
  device_code: unknown;
  binding_state: unknown;
  order_state: unknown;
  actual_amount_cents: unknown;
}>;

type CheckoutTenderRow = Readonly<{
  tender_guid: unknown;
  method: unknown;
  amount_cents: unknown;
}>;

type FullyReversedCashRow = Readonly<{
  tender_count: unknown;
  reversal_count: unknown;
  invalid_tender_count: unknown;
  attempt_count: unknown;
  action_count: unknown;
}>;

type CancelledAttemptRow = Readonly<{
  action_id: unknown;
  request_signature: unknown;
  attempt_id: unknown;
  order_guid: unknown;
  provider: unknown;
  operation: unknown;
  amount_cents: unknown;
  state: unknown;
}>;

type RecoveryOrderRow = Readonly<{
  draft_id: unknown;
  request_fingerprint: unknown;
  pricing_state_json: unknown;
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

type RecoveryLineRow = Readonly<{
  line_id: unknown;
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

type BlockingAttemptRow = Readonly<{
  attempt_id: unknown;
  order_guid: unknown;
  provider: unknown;
  operation: unknown;
  amount_cents: unknown;
  state: unknown;
}>;

type BoundActionRow = Readonly<{
  action_id: unknown;
  request_signature: unknown;
  binding_attempt_id: unknown;
  binding_idempotency_key: unknown;
  persisted_attempt_id: unknown;
  attempt_order_guid: unknown;
  attempt_idempotency_key: unknown;
  attempt_provider: unknown;
  attempt_operation: unknown;
  attempt_amount_cents: unknown;
  attempt_state: unknown;
  tender_count: unknown;
  matching_tender_count: unknown;
}>;

/**
 * 支付草稿、行和 idempotent binding 共用一个 BEGIN IMMEDIATE。恢复只查看
 * 同门店/设备的 active M11 draft 与 blocking attempt；多活跃事实一律失败关闭。
 */
export class SqlitePaymentDraftRecoveryStore
implements PersistedOrderDraftPort {
  public constructor(
    private readonly connection: SqliteConnectionPort,
    private readonly ids: PaymentDraftPersistenceIds,
    private readonly nowIso: () => string,
  ) {}

  public createOrReuseDraft(
    input: CreateOrReusePaymentDraftInput,
  ): Promise<PaymentDraftMutation> {
    const draft = normalizeDraft(input);
    return this.connection.withExclusiveTransaction(async (transaction) => {
      const existing = await transaction.getFirst<DraftBindingRow>(
        `SELECT draft_id, request_fingerprint, pricing_state_json,
          order_guid, store_code, device_code, state, abandon_action_id,
          close_action_id, close_attempt_id
         FROM payment_order_draft_bindings
         WHERE draft_id = ?`,
        [draft.draftId],
      );
      if (existing) {
        if (
          text(existing.request_fingerprint, "draft fingerprint") !==
            draft.fingerprint ||
          text(existing.store_code, "draft store code") !==
            draft.identity.storeCode ||
          text(existing.device_code, "draft device code") !==
            draft.identity.deviceCode
        ) {
          throw new Error(
            "Payment draft id was replayed with different cart or identity.",
          );
        }
        const bindingState = text(existing.state, "draft binding state");
        if (bindingState === "Abandoned") {
          throw new Error("Payment draft was already abandoned.");
        }
        if (bindingState === "CancelledClosed") {
          throw new Error("Payment draft was already closed after cancellation.");
        }
        if (bindingState !== "Active") {
          throw new Error("Payment draft binding state is invalid.");
        }
        const order = await requireRecoveryOrder(
          transaction,
          text(existing.order_guid, "draft order guid"),
        );
        if (nullableText(order.draft_id) !== draft.draftId) {
          throw new Error("Payment draft binding identity is inconsistent.");
        }
        assertRecoveryOrderMatchesFingerprint(order, draft);
        const recovered = await recoveryBase(transaction, order);
        if (
          JSON.stringify(recovered.cart) !== JSON.stringify(draft.cart) ||
          JSON.stringify(recovered.pricingState) !==
            JSON.stringify(draft.pricingState) ||
          JSON.stringify(recovered.identity) !== JSON.stringify(draft.identity)
        ) {
          throw new Error("Payment draft rows no longer match their binding.");
        }
        return mutationFromOrder(true, draft.draftId, order);
      }

      const active = await findBlockingRecoveryInTransaction(
        transaction,
        draft.identity,
      );
      if (active) {
        throw new Error(
          `Payment recovery must resume existing order ${active.orderGuid}.`,
        );
      }

      const orderGuid = strictId(
        this.ids.createOrderGuid(),
        "payment order guid",
      );
      const localSequence = await allocateLocalSequence(
        transaction,
        draft.soldAtIso,
      );
      await transaction.run(
        `INSERT INTO local_orders (
          order_guid, local_sequence, store_code, device_code,
          cashier_id, cashier_name, sold_at_iso, state,
          total_cents, discount_cents, actual_amount_cents,
          original_order_guid, created_at_iso, updated_at_iso
        ) VALUES (?, ?, ?, ?, ?, ?, ?, 'Draft', ?, ?, ?, ?, ?, ?)`,
        [
          orderGuid,
          localSequence,
          draft.identity.storeCode,
          draft.identity.deviceCode,
          draft.identity.cashierId,
          draft.identity.cashierName,
          draft.soldAtIso,
          draft.cart.subtotal.cents,
          draft.cart.discount.cents,
          draft.cart.actualAmount.cents,
          draft.originalOrderGuid,
          draft.soldAtIso,
          draft.soldAtIso,
        ],
      );
      for (const [index, line] of draft.cart.lines.entries()) {
        const orderLineId = strictUuid(
          this.ids.createOrderLineGuid(),
          "payment order line guid",
        );
        const syncProvenance = normalizeLineSyncProvenance(
          line.syncProvenance,
        );
        await transaction.run(
          `INSERT INTO local_order_lines (
            line_id, order_guid, line_sequence, product_code, item_number,
            lookup_code, display_name, quantity, unit_price_cents,
            discount_cents, actual_amount_cents, price_source, line_kind,
            return_source_key, original_order_guid, original_order_detail_guid,
            reference_code, sync_price_source
          ) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)`,
          [
            orderLineId,
            orderGuid,
            index + 1,
            line.productCode,
            line.itemNumber,
            line.lookupCode,
            line.displayName,
            line.quantity,
            line.unitPrice.cents,
            line.discount.cents,
            line.actualAmount.cents,
            line.priceSource,
            line.kind,
            line.returnSourceKey,
            line.originalOrderGuid,
            line.originalOrderDetailGuid,
            syncProvenance.referenceCode,
            syncProvenance.priceSource,
          ],
        );
        await transaction.run(
          `INSERT INTO payment_order_draft_line_bindings (
            order_guid, cart_line_id, order_line_id, line_sequence
          ) VALUES (?, ?, ?, ?)`,
          [orderGuid, line.lineId, orderLineId, index + 1],
        );
      }
      await transaction.run(
        `INSERT INTO payment_order_draft_bindings (
          draft_id, request_fingerprint, pricing_state_json,
          order_guid, store_code,
          device_code, state, abandon_action_id, abandon_audit_event_id,
          abandoned_at_iso, created_at_iso
        ) VALUES (?, ?, ?, ?, ?, ?, 'Active', NULL, NULL, NULL, ?)`,
        [
          draft.draftId,
          draft.fingerprint,
          draft.pricingStateJson,
          orderGuid,
          draft.identity.storeCode,
          draft.identity.deviceCode,
          draft.soldAtIso,
        ],
      );
      return {
        replayed: false,
        draftId: draft.draftId,
        orderGuid,
        localSequence,
        soldAtIso: draft.soldAtIso,
        state: "Draft",
      };
    });
  }

  public async assertPersisted(orderGuidInput: string): Promise<void> {
    const orderGuid = strictId(orderGuidInput, "payment order guid");
    const row = await this.connection.getFirst<{
      state: unknown;
      line_count: unknown;
      binding_state: unknown;
    }>(
      `SELECT o.state,
        (SELECT COUNT(*) FROM local_order_lines l
         WHERE l.order_guid = o.order_guid) AS line_count,
        d.state AS binding_state
       FROM local_orders o
       INNER JOIN payment_order_draft_bindings d
         ON d.order_guid = o.order_guid
       WHERE o.order_guid = ?`,
      [orderGuid],
    );
    if (
      !row ||
      (text(row.state, "payment draft state") !== "Draft" &&
        text(row.state, "payment draft state") !== "Completing") ||
      text(row.binding_state, "payment draft binding state") !== "Active" ||
      positiveInteger(row.line_count, "payment draft line count") < 1
    ) {
      throw new Error("Payment order draft is not active and persisted.");
    }
  }

  /**
   * 运行时公开投影只读取订单和活动 tender 真相。定价状态、provider 引用、
   * idempotency key 及受保护字段均不越过此边界。
   */
  public readDraft(
    orderGuidInput: string,
    scopeInput: PaymentRecoveryScope,
  ): Promise<PaymentCheckoutDraft | null> {
    const orderGuid = strictId(orderGuidInput, "payment order guid");
    const scope = normalizeScope(scopeInput);
    return this.connection.withExclusiveTransaction((transaction) =>
      readCheckoutDraftInTransaction(transaction, orderGuid, scope),
    );
  }

  /**
   * 终端明确返回 Cancelled 且当前没有活动正 tender 时，追加审计并关闭 M11
   * binding。该操作只改变 binding 的 CAS 状态；订单、行、attempt 和 action
   * binding 永久保留，崩溃重放仍返回同一 checkoutIntentId/OrderGuid。
   */
  public closeCancelledDraft(
    input: PaymentDraftCancelledCloseInput,
  ): Promise<PaymentDraftCancelledCloseResult> {
    const scope = normalizeScope(input);
    const orderGuid = strictId(input.orderGuid, "payment order guid");
    const actionId = strictId(input.actionId, "payment cancel action id");
    const actor = normalizeAuditActor(input.actor);
    return this.connection.withExclusiveTransaction(async (transaction) => {
      const binding = await transaction.getFirst<DraftBindingRow>(
        `SELECT draft_id, request_fingerprint, pricing_state_json,
          order_guid, store_code, device_code, state, abandon_action_id,
          close_action_id, close_attempt_id
         FROM payment_order_draft_bindings
         WHERE order_guid = ?`,
        [orderGuid],
      );
      if (
        !binding ||
        text(binding.store_code, "draft store code") !== scope.storeCode ||
        text(binding.device_code, "draft device code") !== scope.deviceCode
      ) {
        throw new Error("Cancelled payment draft scope does not match.");
      }
      const bindingState = text(binding.state, "draft binding state");
      if (bindingState === "CancelledClosed") {
        if (nullableText(binding.close_action_id) !== actionId) {
          throw new Error(
            "Payment draft was closed by a different immutable action.",
          );
        }
        const replay = await readCheckoutDraftInTransaction(
          transaction,
          orderGuid,
          scope,
        );
        if (!replay) {
          throw new Error("Closed payment draft projection is missing.");
        }
        return { replayed: true, draft: replay };
      }
      if (bindingState !== "Active") {
        throw new Error("Only an active payment draft can be cancelled closed.");
      }

      const order = await transaction.getFirst<{ state: unknown }>(
        "SELECT state FROM local_orders WHERE order_guid = ?",
        [orderGuid],
      );
      const currentOrderState = orderState(order?.state);
      if (
        currentOrderState !== "Draft" &&
        currentOrderState !== "Completing"
      ) {
        throw new Error(
          "Only an unfinished payment order can be cancelled closed.",
        );
      }

      const attempts = await transaction.getAll<CancelledAttemptRow>(
        `SELECT binding.action_id, binding.request_signature,
          binding.attempt_id, attempt.order_guid, attempt.provider,
          attempt.operation, attempt.amount_cents, attempt.state
         FROM payment_action_bindings binding
         INNER JOIN payment_attempts attempt
           ON attempt.attempt_id = binding.attempt_id
          AND attempt.order_guid = binding.order_guid
          AND attempt.idempotency_key = binding.idempotency_key
         WHERE binding.order_guid = ? AND binding.action_id = ?
         LIMIT 2`,
        [orderGuid, actionId],
      );
      if (attempts.length !== 1) {
        throw new Error(
          "Cancelled payment action must bind exactly one persisted attempt.",
        );
      }
      const attempt = attempts[0]!;
      const attemptId = strictId(
        text(attempt.attempt_id, "cancelled payment attempt id"),
        "cancelled payment attempt id",
      );
      const actionIdentity = parseBoundActionSignature(
        text(
          attempt.request_signature,
          "cancelled payment request signature",
        ),
      );
      if (
        text(attempt.action_id, "cancelled payment action id") !== actionId ||
        text(attempt.order_guid, "cancelled payment order guid") !==
          orderGuid ||
        actionIdentity.operation !== "purchase" ||
        actionIdentity.provider !== paymentProvider(attempt.provider) ||
        actionIdentity.operation !== paymentOperation(attempt.operation) ||
        actionIdentity.amount.cents !==
          integer(attempt.amount_cents, "cancelled payment amount") ||
        text(attempt.state, "cancelled payment state") !== "Cancelled"
      ) {
        throw new Error(
          "Cancelled payment action and attempt identity are inconsistent.",
        );
      }

      const currentDraft = await readCheckoutDraftInTransaction(
        transaction,
        orderGuid,
        scope,
      );
      if (
        !currentDraft ||
        currentDraft.tenders.length !== 0 ||
        currentDraft.remaining.cents !== currentDraft.total.cents
      ) {
        throw new Error(
          "Cancelled payment draft still has an active positive tender.",
        );
      }
      const otherBlocking = await transaction.getFirst<{ count: unknown }>(
        `SELECT COUNT(*) AS count
         FROM payment_attempts candidate
         WHERE candidate.order_guid = ?
           AND candidate.attempt_id <> ?
           AND (
             candidate.state IN ('Created', 'Submitted', 'Pending', 'Unknown')
             OR (
               candidate.state = 'Approved'
               AND NOT EXISTS (
                 SELECT 1
                 FROM order_tenders consumed
                 WHERE consumed.payment_attempt_id = candidate.attempt_id
                   AND consumed.order_guid = candidate.order_guid
                   AND consumed.amount_cents = candidate.amount_cents
                   AND (
                     (candidate.provider IN ('square', 'linkly-cloud', 'manual-card')
                       AND consumed.method = 'card')
                     OR (candidate.provider = 'voucher'
                       AND consumed.method = 'voucher')
                   )
               )
             )
           )`,
        [orderGuid, attemptId],
      );
      if (
        integer(otherBlocking?.count, "other blocking attempt count") !== 0
      ) {
        throw new Error(
          "Another blocking payment attempt prevents cancelled close.",
        );
      }

      const closedAtIso = canonicalIso(
        this.nowIso(),
        "payment draft closed time",
      );
      const auditEventId = strictId(
        this.ids.createAuditEventId(),
        "draft close audit event id",
      );
      const draftId = strictId(
        text(binding.draft_id, "payment draft id"),
        "payment draft id",
      );
      await transaction.run(
        `INSERT INTO audit_events (
          event_id, event_type, occurred_at_iso, order_guid,
          correlation_id, payload_json, uploaded_at_iso
        ) VALUES (?, 'PAYMENT_DRAFT_CANCELLED_CLOSED', ?, ?, ?, ?, NULL)`,
        [
          auditEventId,
          closedAtIso,
          orderGuid,
          actionId,
          JSON.stringify({
            action: "payment-draft-cancelled-closed",
            draftId,
            ...auditActorPayload(actor),
          }),
        ],
      );
      const changed = await transaction.run(
        `UPDATE payment_order_draft_bindings
         SET state = 'CancelledClosed', close_action_id = ?,
           close_attempt_id = ?, close_audit_event_id = ?,
           closed_at_iso = ?
         WHERE order_guid = ? AND draft_id = ? AND store_code = ?
           AND device_code = ? AND state = 'Active'
           AND close_action_id IS NULL AND close_attempt_id IS NULL
           AND close_audit_event_id IS NULL AND closed_at_iso IS NULL`,
        [
          actionId,
          attemptId,
          auditEventId,
          closedAtIso,
          orderGuid,
          draftId,
          scope.storeCode,
          scope.deviceCode,
        ],
      );
      if (changed.changes !== 1) {
        throw new Error("Cancelled payment draft close CAS failed.");
      }
      const closed = await readCheckoutDraftInTransaction(
        transaction,
        orderGuid,
        scope,
      );
      if (!closed) {
        throw new Error("Closed payment draft projection is missing.");
      }
      return { replayed: false, draft: closed };
    });
  }

  public findBlockingRecovery(
    scopeInput: PaymentRecoveryScope,
  ): Promise<PaymentDraftRecovery | null> {
    const scope = normalizeScope(scopeInput);
    return this.connection.withExclusiveTransaction((transaction) =>
      findBlockingRecoveryInTransaction(transaction, scope),
    );
  }

  /**
   * 只判断本地是否存在旧 Linkly 会话候选；用于决定是否值得查询后端
   * active/resumable。范围、provider、NULL 环境、未 ACK、非 Created 和
   * 原 session 均在 SQL 内约束，避免新库冷启动发无意义的 240 秒请求。
   */
  public async hasLegacyLinklyRecovery(
    scopeInput: PaymentRecoveryScope,
  ): Promise<boolean> {
    const scope = normalizeScope(scopeInput);
    const row = await this.connection.getFirst<{ attempt_id: unknown }>(
      `SELECT p.attempt_id FROM payment_attempts p
       INNER JOIN local_orders o ON o.order_guid = p.order_guid
       WHERE o.store_code = ? AND o.device_code = ?
         AND p.provider = 'linkly-cloud'
         AND p.provider_environment IS NULL
         AND p.provider_acknowledged_at_iso IS NULL
         AND p.session_id IS NOT NULL
         AND p.state IN ('Submitted', 'Pending', 'Unknown', 'Approved', 'Declined', 'Cancelled')
         AND NOT EXISTS (
           SELECT 1
           FROM return_action_allocations allocation
           INNER JOIN return_actions action ON action.action_id = allocation.action_id
           WHERE allocation.durable_attempt_id = p.attempt_id
             AND action.state IN ('processing', 'unknown')
         )
       LIMIT 1`,
      [scope.storeCode, scope.deviceCode],
    );
    return row !== null;
  }

  /** 已完成订单仍可能留下 Linkly server guard；只返回 M44 后冻结环境的原 attempt。 */
  public async findPendingLinklyAcknowledgement(
    scopeInput: PaymentRecoveryScope,
  ): Promise<string | null> {
    const scope = normalizeScope(scopeInput);
    const row = await this.connection.getFirst<{ attempt_id: unknown }>(
      `SELECT p.attempt_id FROM payment_attempts p
       INNER JOIN local_orders o ON o.order_guid = p.order_guid
       WHERE o.store_code = ? AND o.device_code = ?
         AND p.provider = 'linkly-cloud'
         AND p.provider_environment IS NOT NULL
         AND p.provider_acknowledged_at_iso IS NULL
         AND p.state IN ('Approved', 'Declined', 'Cancelled')
         AND (p.state = 'Approved' OR p.session_id IS NOT NULL)
         AND NOT EXISTS (
           SELECT 1
           FROM return_action_allocations allocation
           INNER JOIN return_actions action ON action.action_id = allocation.action_id
           WHERE allocation.durable_attempt_id = p.attempt_id
             AND action.state IN ('processing', 'unknown')
         )
       ORDER BY p.updated_at_iso, p.attempt_id LIMIT 2`,
      [scope.storeCode, scope.deviceCode],
    );
    return row ? strictId(text(row.attempt_id, "Linkly acknowledgement attempt"), "Linkly acknowledgement attempt") : null;
  }

  /** 只从已认证后端返回的原 session 查找旧账本，不扫描或猜测所有历史确认。 */
  public async findLegacyLinklyAttemptForSession(
    scopeInput: PaymentRecoveryScope,
    sessionId: string,
    idempotencyKey?: string,
  ): Promise<string | null> {
    const scope = normalizeScope(scopeInput);
    const normalizedUid = idempotencyKey
      ? idempotencyKey.replaceAll("-", "").trim().toLowerCase()
      : null;
    const rows = await this.connection.getAll<{ attempt_id: unknown }>(
      `SELECT p.attempt_id FROM payment_attempts p
       INNER JOIN local_orders o ON o.order_guid = p.order_guid
       WHERE o.store_code = ? AND o.device_code = ?
         AND p.provider = 'linkly-cloud' AND p.session_id = ?
         AND p.provider_environment IS NULL AND p.provider_acknowledged_at_iso IS NULL
         AND p.state IN ('Submitted', 'Pending', 'Unknown', 'Approved', 'Declined', 'Cancelled')
         AND NOT EXISTS (
           SELECT 1
           FROM return_action_allocations allocation
           INNER JOIN return_actions action ON action.action_id = allocation.action_id
           WHERE allocation.durable_attempt_id = p.attempt_id
             AND action.state IN ('processing', 'unknown')
         )
         AND (? IS NULL OR lower(replace(p.idempotency_key, '-', '')) = ?) LIMIT 2`,
      [scope.storeCode, scope.deviceCode, strictId(sessionId, "Linkly session"), normalizedUid, normalizedUid],
    );
    if (rows.length !== 1) return null;
    return strictId(text(rows[0]!.attempt_id, "Linkly legacy attempt"), "Linkly legacy attempt");
  }

  public abandonPreparedDraft(
    input: PaymentDraftAbandonInput,
  ): Promise<PaymentDraftAbandonResult> {
    const scope = normalizeScope(input);
    const actionId = strictId(input.actionId, "draft abandon action id");
    const draftId = strictId(input.draftId, "payment draft id");
    const orderGuid = strictId(input.orderGuid, "payment order guid");
    const actor = normalizeAuditActor(input.actor);
    return this.connection.withExclusiveTransaction(async (transaction) => {
      const binding = await transaction.getFirst<DraftBindingRow>(
        `SELECT draft_id, request_fingerprint, pricing_state_json,
          order_guid, store_code, device_code, state, abandon_action_id,
          close_action_id, close_attempt_id
         FROM payment_order_draft_bindings
         WHERE draft_id = ?`,
        [draftId],
      );
      if (
        !binding ||
        text(binding.order_guid, "draft order guid") !== orderGuid ||
        text(binding.store_code, "draft store code") !== scope.storeCode ||
        text(binding.device_code, "draft device code") !== scope.deviceCode
      ) {
        throw new Error("Payment draft abandon scope does not match.");
      }
      const recovery = await recoveryBase(
        transaction,
        await requireRecoveryOrder(transaction, orderGuid),
      );
      const bindingState = text(binding.state, "draft binding state");
      if (bindingState === "Abandoned") {
        if (
          nullableText(binding.abandon_action_id) !== actionId
        ) {
          throw new Error(
            "Payment draft was abandoned by a different immutable action.",
          );
        }
        return {
          replayed: true,
          draftId,
          orderGuid,
          cart: recovery.cart,
          pricingState: recovery.pricingState,
        };
      }
      if (bindingState !== "Active") {
        throw new Error("Payment draft binding state is invalid.");
      }

      const order = await transaction.getFirst<{ state: unknown }>(
        "SELECT state FROM local_orders WHERE order_guid = ?",
        [orderGuid],
      );
      if (!order || text(order.state, "payment order state") !== "Draft") {
        throw new Error("Only an untouched Draft payment order can be abandoned.");
      }
      // 已明确 Declined 且零 tender 的历史仍是不可变账本事实，但不代表存在未决付款。
      // 复用恢复解析校验每条 binding/attempt 身份；任何其他状态、缺失关联或损坏记录均失败关闭。
      const unresolvedBoundAction = await readBoundAction(
        transaction,
        orderGuid,
      );
      const usage = await transaction.getFirst<{
        tender_count: unknown;
        attempt_count: unknown;
        action_count: unknown;
      }>(
        `SELECT
          (SELECT COUNT(*) FROM order_tenders WHERE order_guid = ?) AS tender_count,
          (SELECT COUNT(*) FROM payment_attempts WHERE order_guid = ?) AS attempt_count,
          (SELECT COUNT(*) FROM payment_action_bindings WHERE order_guid = ?) AS action_count`,
        [orderGuid, orderGuid, orderGuid],
      );
      const tenderCount = integer(usage?.tender_count, "draft tender count");
      const attemptCount = integer(usage?.attempt_count, "draft attempt count");
      const actionCount = integer(usage?.action_count, "draft action count");
      // 单个 binding 尚无 attempt 时，Abandoned 与 attempt INSERT 在同一排他事务序列化；
      // 旧异步流程之后会被 abandoned-draft 触发器拒绝，无法越过首次 provider 调用门。
      const untouchedBoundAction =
        attemptCount === 0 &&
        actionCount === 1 &&
        unresolvedBoundAction !== null &&
        unresolvedBoundAction.provider !== "manual-card";
      const resolvedHistory = attemptCount === actionCount && unresolvedBoundAction === null;
      if (
        tenderCount !== 0 ||
        !(resolvedHistory || untouchedBoundAction)
      ) {
        throw new Error(
          "Payment draft with tender or unresolved payment history cannot be abandoned.",
        );
      }

      const auditEventId = strictId(
        this.ids.createAuditEventId(),
        "draft abandon audit event id",
      );
      const abandonedAtIso = canonicalIso(
        this.nowIso(),
        "draft abandoned time",
      );
      await transaction.run(
        `INSERT INTO audit_events (
          event_id, event_type, occurred_at_iso, order_guid,
          correlation_id, payload_json, uploaded_at_iso
        ) VALUES (?, 'PAYMENT_DRAFT_ABANDONED', ?, ?, ?, ?, NULL)`,
        [
          auditEventId,
          abandonedAtIso,
          orderGuid,
          actionId,
          JSON.stringify({
            action: "payment-draft-abandoned",
            draftId,
            ...auditActorPayload(actor),
          }),
        ],
      );
      const changed = await transaction.run(
        `UPDATE payment_order_draft_bindings
         SET state = 'Abandoned', abandon_action_id = ?,
           abandon_audit_event_id = ?, abandoned_at_iso = ?
         WHERE draft_id = ? AND order_guid = ? AND store_code = ?
           AND device_code = ? AND state = 'Active'
           AND abandon_action_id IS NULL
           AND abandon_audit_event_id IS NULL
           AND abandoned_at_iso IS NULL`,
        [
          actionId,
          auditEventId,
          abandonedAtIso,
          draftId,
          orderGuid,
          scope.storeCode,
          scope.deviceCode,
        ],
      );
      if (changed.changes !== 1) {
        throw new Error("Payment draft abandon CAS failed.");
      }
      return {
        replayed: false,
        draftId,
        orderGuid,
        cart: recovery.cart,
        pricingState: recovery.pricingState,
      };
    });
  }

  /**
   * 现金付款全部通过不可变 reversal 抵消后，允许显式取消并释放购物车。
   * 原 tender、反向 tender、关联和订单都保留；这里只关闭 active draft binding。
   */
  public closeFullyReversedDraft(
    input: PaymentDraftAbandonInput,
  ): Promise<PaymentDraftAbandonResult> {
    const scope = normalizeScope(input);
    const actionId = strictId(input.actionId, "reversed draft close action id");
    const draftId = strictId(input.draftId, "payment draft id");
    const orderGuid = strictId(input.orderGuid, "payment order guid");
    const actor = normalizeAuditActor(input.actor);
    return this.connection.withExclusiveTransaction(async (transaction) => {
      const binding = await transaction.getFirst<DraftBindingRow>(
        `SELECT draft_id, request_fingerprint, pricing_state_json,
          order_guid, store_code, device_code, state, abandon_action_id,
          close_action_id, close_attempt_id
         FROM payment_order_draft_bindings
         WHERE draft_id = ?`,
        [draftId],
      );
      if (
        !binding ||
        text(binding.order_guid, "draft order guid") !== orderGuid ||
        text(binding.store_code, "draft store code") !== scope.storeCode ||
        text(binding.device_code, "draft device code") !== scope.deviceCode
      ) {
        throw new Error("Reversed payment draft scope does not match.");
      }
      const recovery = await recoveryBase(
        transaction,
        await requireRecoveryOrder(transaction, orderGuid),
      );
      const bindingState = text(binding.state, "draft binding state");
      if (bindingState === "Abandoned") {
        if (nullableText(binding.abandon_action_id) !== actionId) {
          throw new Error(
            "Payment draft was abandoned by a different immutable action.",
          );
        }
        return {
          replayed: true,
          draftId,
          orderGuid,
          cart: recovery.cart,
          pricingState: recovery.pricingState,
        };
      }
      if (bindingState !== "Active") {
        throw new Error("Payment draft binding state is invalid.");
      }
      const draft = await readCheckoutDraftInTransaction(
        transaction,
        orderGuid,
        scope,
      );
      if (!draft?.cancellableAfterReversal) {
        throw new Error(
          "Only a fully reversed cash payment draft can be closed.",
        );
      }

      const auditEventId = strictId(
        this.ids.createAuditEventId(),
        "reversed draft close audit event id",
      );
      const abandonedAtIso = canonicalIso(
        this.nowIso(),
        "reversed draft close time",
      );
      await transaction.run(
        `INSERT INTO audit_events (
          event_id, event_type, occurred_at_iso, order_guid,
          correlation_id, payload_json, uploaded_at_iso
        ) VALUES (?, 'PAYMENT_DRAFT_ABANDONED', ?, ?, ?, ?, NULL)`,
        [
          auditEventId,
          abandonedAtIso,
          orderGuid,
          actionId,
          JSON.stringify({
            action: "payment-fully-reversed-draft-closed",
            reason: "ALL_CASH_TENDERS_REVERSED",
            draftId,
            ...auditActorPayload(actor),
          }),
        ],
      );
      const changed = await transaction.run(
        `UPDATE payment_order_draft_bindings
         SET state = 'Abandoned', abandon_action_id = ?,
           abandon_audit_event_id = ?, abandoned_at_iso = ?
         WHERE draft_id = ? AND order_guid = ? AND store_code = ?
           AND device_code = ? AND state = 'Active'
           AND abandon_action_id IS NULL
           AND abandon_audit_event_id IS NULL
           AND abandoned_at_iso IS NULL`,
        [
          actionId,
          auditEventId,
          abandonedAtIso,
          draftId,
          orderGuid,
          scope.storeCode,
          scope.deviceCode,
        ],
      );
      if (changed.changes !== 1) {
        throw new Error("Reversed payment draft close CAS failed.");
      }
      return {
        replayed: false,
        draftId,
        orderGuid,
        cart: recovery.cart,
        pricingState: recovery.pricingState,
      };
    });
  }
}

type NormalizedDraft = Readonly<{
  draftId: string;
  identity: PaymentDraftIdentity;
  cart: CartSnapshot;
  pricingState: PricingCartStateSnapshot;
  pricingStateJson: string;
  soldAtIso: string;
  originalOrderGuid: string | null;
  recallBinding: RecallActiveBinding | null;
  fingerprint: string;
}>;

async function readCheckoutDraftInTransaction(
  transaction: SqliteConnectionPort,
  orderGuid: string,
  scope: PaymentRecoveryScope,
): Promise<PaymentCheckoutDraft | null> {
  const row = await transaction.getFirst<CheckoutDraftRow>(
    `SELECT binding.draft_id, binding.request_fingerprint,
      binding.store_code, binding.device_code,
      binding.state AS binding_state,
      order_row.order_guid, order_row.state AS order_state,
      order_row.actual_amount_cents
     FROM payment_order_draft_bindings binding
     INNER JOIN local_orders order_row
       ON order_row.order_guid = binding.order_guid
     WHERE binding.order_guid = ?
       AND binding.store_code = ?
       AND binding.device_code = ?`,
    [orderGuid, scope.storeCode, scope.deviceCode],
  );
  if (!row) return null;
  const bindingState = text(row.binding_state, "draft binding state");
  if (
    bindingState !== "Active" &&
    bindingState !== "Abandoned" &&
    bindingState !== "CancelledClosed"
  ) {
    throw new Error("Payment draft binding state is invalid.");
  }
  const decoded = decodeFingerprint(
    text(row.request_fingerprint, "payment draft fingerprint"),
  );
  const totalCents = integer(
    row.actual_amount_cents,
    "payment checkout total",
  );
  if (totalCents <= 0 || decoded.cart.actualAmount.cents !== totalCents) {
    throw new Error("Payment checkout total no longer matches its draft.");
  }
  const tenderRows = await transaction.getAll<CheckoutTenderRow>(
    `SELECT tender.tender_guid, tender.method, tender.amount_cents
     FROM order_tenders tender
     WHERE tender.order_guid = ?
       AND tender.amount_cents > 0
       AND NOT EXISTS (
         SELECT 1
         FROM payment_tender_reversal_links reversal
         WHERE reversal.order_guid = tender.order_guid
           AND reversal.source_tender_guid = tender.tender_guid
       )
     ORDER BY tender.created_at_iso, tender.tender_guid`,
    [orderGuid],
  );
  const seenMethods = new Set<string>();
  let paidCents = 0;
  const tenders = tenderRows.map((tender) => {
    const method = checkoutTenderMethod(tender.method);
    if (seenMethods.has(method)) {
      throw new Error(
        "Payment checkout has multiple active tenders for one method.",
      );
    }
    seenMethods.add(method);
    const amountCents = positiveInteger(
      tender.amount_cents,
      "payment checkout tender amount",
    );
    paidCents = safeAdd(paidCents, amountCents);
    return Object.freeze({
      tenderGuid: strictId(
        text(tender.tender_guid, "payment checkout tender guid"),
        "payment checkout tender guid",
      ),
      method,
      amount: createAud(amountCents),
      reversible: true,
    });
  });
  const remainingCents = totalCents - paidCents;
  if (
    !Number.isSafeInteger(remainingCents) ||
    remainingCents < 0 ||
    remainingCents > totalCents
  ) {
    throw new Error("Payment checkout active tender total is invalid.");
  }
  const state = orderState(row.order_state);
  const cancellableAfterReversal =
    bindingState === "Active" &&
    state === "Completing" &&
    tenders.length === 0 &&
    remainingCents === totalCents &&
    await hasFullyReversedCashLedger(transaction, orderGuid);
  return Object.freeze({
    checkoutIntentId: strictId(
      text(row.draft_id, "payment checkout intent id"),
      "payment checkout intent id",
    ),
    orderGuid: strictId(
      text(row.order_guid, "payment checkout order guid"),
      "payment checkout order guid",
    ),
    cartRevision: decoded.cart.revision,
    state,
    total: createAud(totalCents),
    remaining: createAud(remainingCents),
    cancellableAfterReversal,
    tenders: Object.freeze(tenders),
  });
}

async function hasFullyReversedCashLedger(
  transaction: SqliteConnectionPort,
  orderGuid: string,
): Promise<boolean> {
  const row = await transaction.getFirst<FullyReversedCashRow>(
    `SELECT
      COUNT(*) AS tender_count,
      (
        SELECT COUNT(*)
        FROM payment_tender_reversal_links link
        WHERE link.order_guid = ?
      ) AS reversal_count,
      COALESCE(SUM(
        CASE
          WHEN tender.method <> 'cash'
            OR tender.amount_cents = 0
            OR (
              tender.amount_cents > 0
              AND NOT EXISTS (
                SELECT 1
                FROM payment_tender_reversal_links source_link
                WHERE source_link.order_guid = tender.order_guid
                  AND source_link.source_tender_guid = tender.tender_guid
              )
            )
            OR (
              tender.amount_cents < 0
              AND NOT EXISTS (
                SELECT 1
                FROM payment_tender_reversal_links reversal_link
                WHERE reversal_link.order_guid = tender.order_guid
                  AND reversal_link.reversal_tender_guid = tender.tender_guid
              )
            )
          THEN 1
          ELSE 0
        END
      ), 0) AS invalid_tender_count,
      (
        SELECT COUNT(*)
        FROM payment_attempts attempt
        WHERE attempt.order_guid = ?
      ) AS attempt_count,
      (
        SELECT COUNT(*)
        FROM payment_action_bindings action
        WHERE action.order_guid = ?
      ) AS action_count
     FROM order_tenders tender
     WHERE tender.order_guid = ?`,
    [orderGuid, orderGuid, orderGuid, orderGuid],
  );
  return (
    integer(row?.tender_count, "reversed draft tender count") >= 2 &&
    integer(row?.reversal_count, "reversed draft link count") >= 1 &&
    integer(
      row?.invalid_tender_count,
      "reversed draft invalid tender count",
    ) === 0 &&
    integer(row?.attempt_count, "reversed draft attempt count") === 0 &&
    integer(row?.action_count, "reversed draft action count") === 0
  );
}

function normalizeDraft(input: CreateOrReusePaymentDraftInput): NormalizedDraft {
  const draftId = strictId(input.draftId, "payment draft id");
  const identity = normalizeIdentity(input.identity);
  const soldAtIso = canonicalIso(input.soldAtIso, "payment draft sold time");
  const cart = normalizeCart(input.cart);
  const pricingState = normalizePricingState(input.pricingState, cart);
  const pricingStateJson = JSON.stringify(pricingState);
  const originalOrderGuid = deriveOriginalOrderGuid(cart);
  const recallBinding = normalizeRecallBinding(input.recallBinding, identity);
  // soldAtIso 刻意不参与签名：崩溃重放时新进程的 now 不得改变原草稿身份。
  const fingerprint = JSON.stringify({
    version: 2,
    identity,
    cart,
    pricingState,
    originalOrderGuid,
    recallBinding,
  });
  return {
    draftId,
    identity,
    cart,
    pricingState,
    pricingStateJson,
    soldAtIso,
    originalOrderGuid,
    recallBinding,
    fingerprint,
  };
}

function decodeFingerprint(value: string): Readonly<{
  identity: PaymentDraftIdentity;
  cart: CartSnapshot;
  pricingState: PricingCartStateSnapshot;
  recallBinding: RecallActiveBinding | null;
}> {
  let parsed: unknown;
  try {
    parsed = JSON.parse(value);
  } catch {
    throw new Error("Payment draft fingerprint is invalid JSON.");
  }
  if (
    !parsed ||
    typeof parsed !== "object" ||
    (parsed as { version?: unknown }).version !== 2
  ) {
    throw new Error("Payment draft fingerprint version is invalid.");
  }
  const decoded = parsed as {
    identity?: PaymentDraftIdentity;
    cart?: CartSnapshot;
    pricingState?: PricingCartStateSnapshot;
    recallBinding?: RecallActiveBinding | null;
  };
  const identity = normalizeIdentity(decoded.identity as PaymentDraftIdentity);
  const cart = normalizeCart(decoded.cart as CartSnapshot, false);
  return {
    identity,
    cart,
    pricingState: normalizePricingState(
      decoded.pricingState as PricingCartStateSnapshot,
      cart,
      false,
    ),
    // 旧版 version 2 指纹没有该可选字段，只能按普通支付恢复；不得猜测挂单身份。
    recallBinding: normalizeRecallBinding(
      decoded.recallBinding ?? null,
      identity,
    ),
  };
}

function normalizePricingState(
  input: PricingCartStateSnapshot,
  expectedCart: CartSnapshot,
  requireSyncProvenance = true,
): PricingCartStateSnapshot {
  let restored: PricingCart;
  try {
    restored = PricingCart.restore(input);
  } catch (error) {
    throw new TypeError(
      `Payment pricing state is invalid: ${errorMessage(error)}`,
    );
  }
  const pricingState = restored.stateSnapshot();
  const restoredCart = normalizeCart(
    restored.snapshot(),
    requireSyncProvenance,
  );
  if (JSON.stringify(restoredCart) !== JSON.stringify(expectedCart)) {
    throw new TypeError(
      "Payment pricing state does not restore the persisted cart.",
    );
  }
  return Object.freeze(pricingState);
}

function normalizeCart(
  input: CartSnapshot,
  requireSyncProvenance = true,
): CartSnapshot {
  if (!input || typeof input !== "object") {
    throw new TypeError("Payment cart is required.");
  }
  const mode = input.mode;
  if (
    !Number.isSafeInteger(input.revision) ||
    input.revision < 0 ||
    (mode !== "sale" && mode !== "return") ||
    !Array.isArray(input.lines) ||
    input.lines.length === 0
  ) {
    throw new TypeError("Payment cart identity is invalid.");
  }
  const lineIds = new Set<string>();
  const lines = input.lines.map((line) =>
    normalizeLine(line, mode, requireSyncProvenance));
  let lineActualCents = 0;
  let lineDiscountCents = 0;
  for (const line of lines) {
    if (lineIds.has(line.lineId)) {
      throw new TypeError("Payment cart line ids must be unique.");
    }
    lineIds.add(line.lineId);
    lineActualCents = safeAdd(lineActualCents, line.actualAmount.cents);
    lineDiscountCents = safeAdd(lineDiscountCents, line.discount.cents);
  }
  const subtotal = audInteger(input.subtotal, "cart subtotal");
  const discount = audInteger(input.discount, "cart discount");
  const actualAmount = audInteger(input.actualAmount, "cart actual amount");
  if (
    discount.cents < 0 ||
    actualAmount.cents === 0 ||
    lineActualCents !== actualAmount.cents ||
    lineDiscountCents !== discount.cents ||
    (mode === "sale" &&
      (subtotal.cents <= 0 || actualAmount.cents <= 0)) ||
    (mode === "return" &&
      (subtotal.cents >= 0 || actualAmount.cents >= 0))
  ) {
    throw new TypeError("Payment cart monetary truth is inconsistent.");
  }
  return Object.freeze({
    revision: input.revision,
    mode,
    lines: Object.freeze(lines),
    subtotal,
    discount,
    actualAmount,
  });
}

function normalizeLine(
  input: CartLine,
  mode: "sale" | "return",
  requireSyncProvenance: boolean,
): CartLine {
  if (!input || typeof input !== "object" || input.kind !== mode) {
    throw new TypeError("Payment cart line mode is inconsistent.");
  }
  const actualAmount = audInteger(input.actualAmount, "line actual amount");
  if (
    (mode === "sale" && actualAmount.cents <= 0) ||
    (mode === "return" && actualAmount.cents >= 0)
  ) {
    throw new TypeError("Payment cart line amount sign is invalid.");
  }
  const priceSource =
    input.priceSource === "catalog" ||
    input.priceSource === "promotion" ||
    input.priceSource === "manual" ||
    input.priceSource === "open-item"
      ? input.priceSource
      : invalid<CartLine["priceSource"]>("Payment line price source is invalid.");
  const syncProvenance = normalizeOptionalLineSyncProvenance(
    input.syncProvenance,
    requireSyncProvenance,
  );
  return Object.freeze({
    lineId: strictId(input.lineId, "payment line id"),
    productCode: strictText(input.productCode, "payment product code", 256),
    itemNumber: optionalText(input.itemNumber, "payment item number", 256),
    lookupCode: strictText(input.lookupCode, "payment lookup code", 256),
    displayName: strictText(input.displayName, "payment display name", 1024),
    quantity: canonicalPositiveDecimal(input.quantity),
    unitPrice: audInteger(input.unitPrice, "line unit price"),
    discount: nonNegativeAud(input.discount, "line discount"),
    actualAmount,
    priceSource,
    ...(syncProvenance ? { syncProvenance } : {}),
    kind: mode,
    returnSourceKey: optionalText(
      input.returnSourceKey,
      "payment return source",
      512,
    ),
    originalOrderGuid: optionalText(
      input.originalOrderGuid,
      "payment original order guid",
      128,
    ),
    originalOrderDetailGuid: optionalText(
      input.originalOrderDetailGuid,
      "payment original order detail guid",
      128,
    ),
  });
}

function normalizeOptionalLineSyncProvenance(
  input: unknown,
  required: boolean,
): LineSyncProvenance | undefined {
  if (input === undefined && !required) return undefined;
  try {
    return normalizeLineSyncProvenance(input);
  } catch (error) {
    throw new TypeError(
      `Payment line sync provenance is invalid: ${errorMessage(error)}`,
    );
  }
}

function deriveOriginalOrderGuid(cart: CartSnapshot): string | null {
  const originals = new Set(
    cart.lines
      .map((line) => line.originalOrderGuid)
      .filter((value): value is string => value !== null),
  );
  if (cart.mode === "sale") {
    if (
      originals.size !== 0 ||
      cart.lines.some(
        (line) =>
          line.returnSourceKey !== null ||
          line.originalOrderDetailGuid !== null,
      )
    ) {
      throw new TypeError("Sale payment draft contains return identity.");
    }
    return null;
  }
  if (
    originals.size !== 1 ||
    cart.lines.some(
      (line) =>
        line.returnSourceKey === null ||
        line.originalOrderGuid === null ||
        line.originalOrderDetailGuid === null,
    )
  ) {
    throw new TypeError("Return payment draft identity is incomplete.");
  }
  return [...originals][0] ?? null;
}

async function findBlockingRecoveryInTransaction(
  transaction: SqliteConnectionPort,
  scope: PaymentRecoveryScope,
): Promise<PaymentDraftRecovery | null> {
  const activeDrafts = await transaction.getAll<RecoveryOrderRow>(
    `SELECT d.draft_id, d.request_fingerprint, d.pricing_state_json,
      o.order_guid, o.local_sequence, o.store_code, o.device_code,
      o.cashier_id, o.cashier_name, o.sold_at_iso, o.state,
      o.total_cents, o.discount_cents, o.actual_amount_cents, o.original_order_guid
     FROM payment_order_draft_bindings d
     INNER JOIN local_orders o ON o.order_guid = d.order_guid
     WHERE d.store_code = ? AND d.device_code = ? AND d.state = 'Active'
       AND o.state IN ('Draft', 'Completing')
     ORDER BY o.local_sequence DESC
     LIMIT 3`,
    [scope.storeCode, scope.deviceCode],
  );
  const attempts = await transaction.getAll<BlockingAttemptRow>(
    `SELECT p.attempt_id, p.order_guid, p.provider, p.operation,
      p.amount_cents, p.state
     FROM payment_attempts p
     INNER JOIN local_orders o ON o.order_guid = p.order_guid
     WHERE o.store_code = ? AND o.device_code = ?
       AND (
         p.state IN ('Created', 'Submitted', 'Pending', 'Unknown')
         OR (
           p.state = 'Approved'
           AND NOT EXISTS (
             SELECT 1 FROM order_tenders t
             WHERE t.payment_attempt_id = p.attempt_id
               AND t.order_guid = p.order_guid
               AND t.amount_cents = p.amount_cents
               AND (
                 (p.provider IN ('square', 'linkly-cloud', 'manual-card') AND t.method = 'card')
                 OR (p.provider = 'voucher' AND t.method = 'voucher')
               )
           )
         )
       )
     ORDER BY p.updated_at_iso DESC, p.attempt_id DESC
     LIMIT 3`,
    [scope.storeCode, scope.deviceCode],
  );
  if (attempts.length > 1) {
    throw new Error("Multiple blocking payment attempts require support.");
  }
  const orderGuids = new Set<string>();
  for (const row of activeDrafts) {
    orderGuids.add(text(row.order_guid, "active payment order guid"));
  }
  for (const row of attempts) {
    orderGuids.add(text(row.order_guid, "blocking attempt order guid"));
  }
  if (orderGuids.size > 1 || activeDrafts.length > 1) {
    throw new Error("Multiple active payment drafts require support.");
  }
  const orderGuid = [...orderGuids][0];
  if (!orderGuid) return null;
  const active =
    activeDrafts.find(
      (row) => text(row.order_guid, "active payment order guid") === orderGuid,
    ) ??
    await requireRecoveryOrder(transaction, orderGuid);
  const base = await recoveryBase(transaction, active);
  const blocking = attempts[0];
  if (!blocking) {
    if (base.draftId === null) {
      throw new Error("Prepared payment draft binding is missing.");
    }
    return {
      ...base,
      kind: "DraftPrepared",
      attemptId: null,
    };
  }
  if (base.boundAction) {
    if (
      base.boundAction.attemptId !==
        text(blocking.attempt_id, "blocking attempt id") ||
      base.boundAction.provider !== paymentProvider(blocking.provider) ||
      base.boundAction.operation !== paymentOperation(blocking.operation) ||
      base.boundAction.amount.cents !==
        integer(blocking.amount_cents, "blocking attempt amount")
    ) {
      throw new Error(
        "Payment bound action and blocking attempt identity diverged.",
      );
    }
  }
  return {
    ...base,
    kind: "AttemptBlocking",
    attemptId: strictId(
      text(blocking.attempt_id, "blocking attempt id"),
      "blocking attempt id",
    ),
    provider: paymentProvider(blocking.provider),
    operation: paymentOperation(blocking.operation),
    state: blockingState(blocking.state),
    amountCents: integer(blocking.amount_cents, "blocking attempt amount"),
  };
}

async function requireRecoveryOrder(
  transaction: SqliteConnectionPort,
  orderGuid: string,
): Promise<RecoveryOrderRow> {
  const row = await transaction.getFirst<RecoveryOrderRow>(
    `SELECT d.draft_id, d.request_fingerprint, d.pricing_state_json,
      o.order_guid, o.local_sequence, o.store_code, o.device_code,
      o.cashier_id, o.cashier_name, o.sold_at_iso, o.state,
      o.total_cents, o.discount_cents, o.actual_amount_cents,
      o.original_order_guid
     FROM local_orders o
     LEFT JOIN payment_order_draft_bindings d
       ON d.order_guid = o.order_guid
     WHERE o.order_guid = ?`,
    [orderGuid],
  );
  if (!row) throw new Error("Payment recovery order is missing.");
  return row;
}

async function recoveryBase(
  transaction: SqliteConnectionPort,
  order: RecoveryOrderRow,
): Promise<RecoveryBase> {
  const orderGuid = text(order.order_guid, "recovery order guid");
  const lines = await transaction.getAll<RecoveryLineRow>(
    `SELECT binding.cart_line_id AS line_id,
      line.product_code, line.item_number, line.lookup_code,
      line.display_name, line.quantity, line.unit_price_cents,
      line.discount_cents, line.actual_amount_cents, line.price_source,
      line.line_kind, line.return_source_key, line.original_order_guid,
      line.original_order_detail_guid, line.reference_code,
      line.sync_price_source
     FROM local_order_lines line
     INNER JOIN payment_order_draft_line_bindings binding
       ON binding.order_line_id = line.line_id
      AND binding.order_guid = line.order_guid
      AND binding.line_sequence = line.line_sequence
     WHERE line.order_guid = ?
     ORDER BY line.line_sequence`,
    [orderGuid],
  );
  if (!lines.length) throw new Error("Payment recovery order has no lines.");
  const cartLines = lines.map(readRecoveryLine);
  const mode = cartLines[0]?.kind;
  if (
    (mode !== "sale" && mode !== "return") ||
    cartLines.some((line) => line.kind !== mode)
  ) {
    throw new Error("Payment recovery cart mode is invalid.");
  }
  const persistedIdentity = {
    storeCode: text(order.store_code, "recovery store code"),
    deviceCode: text(order.device_code, "recovery device code"),
    cashierId: text(order.cashier_id, "recovery cashier id"),
    cashierName: text(order.cashier_name, "recovery cashier name"),
  };
  const reconstructedCart: CartSnapshot = {
    revision: 0,
    mode,
    lines: cartLines,
    subtotal: createAud(integer(order.total_cents, "recovery subtotal")),
    discount: createAud(
      integer(order.discount_cents, "recovery discount"),
    ),
    actualAmount: createAud(
      integer(order.actual_amount_cents, "recovery actual amount"),
    ),
  };
  const decoded = decodeFingerprint(
    text(order.request_fingerprint, "payment draft fingerprint"),
  );
  const pricingState = parsePricingStateJson(
    text(order.pricing_state_json, "payment pricing state JSON"),
    decoded.cart,
  );
  if (
    JSON.stringify(decoded.identity) !== JSON.stringify(persistedIdentity) ||
    JSON.stringify(decoded.cart.lines) !== JSON.stringify(cartLines) ||
    decoded.cart.subtotal.cents !== reconstructedCart.subtotal.cents ||
    decoded.cart.discount.cents !== reconstructedCart.discount.cents ||
    decoded.cart.actualAmount.cents !==
      reconstructedCart.actualAmount.cents ||
    decoded.cart.mode !== reconstructedCart.mode ||
    JSON.stringify(decoded.pricingState) !== JSON.stringify(pricingState)
  ) {
    throw new Error("Payment draft fingerprint and order rows diverged.");
  }
  const boundAction = await readBoundAction(transaction, orderGuid);
  return {
    draftId: nullableText(order.draft_id),
    orderGuid,
    originalOrderGuid: nullableText(order.original_order_guid),
    localSequence: positiveInteger(
      order.local_sequence,
      "recovery local sequence",
    ),
    soldAtIso: canonicalIso(
      text(order.sold_at_iso, "recovery sold time"),
      "recovery sold time",
    ),
    identity: persistedIdentity,
    cart: decoded.cart,
    pricingState,
    recallBinding: decoded.recallBinding,
    boundAction,
  };
}

function parsePricingStateJson(
  value: string,
  expectedCart: CartSnapshot,
): PricingCartStateSnapshot {
  let parsed: unknown;
  try {
    parsed = JSON.parse(value);
  } catch {
    throw new Error("Payment pricing state is invalid JSON.");
  }
  return normalizePricingState(
    parsed as PricingCartStateSnapshot,
    expectedCart,
    false,
  );
}

async function readBoundAction(
  transaction: SqliteConnectionPort,
  orderGuid: string,
): Promise<RecoveredPaymentBoundAction | null> {
  const rows = await transaction.getAll<BoundActionRow>(
    `SELECT binding.action_id, binding.request_signature,
       binding.attempt_id AS binding_attempt_id,
       binding.idempotency_key AS binding_idempotency_key,
       attempt.attempt_id AS persisted_attempt_id,
       attempt.order_guid AS attempt_order_guid,
       attempt.idempotency_key AS attempt_idempotency_key,
       attempt.provider AS attempt_provider,
       attempt.operation AS attempt_operation,
       attempt.amount_cents AS attempt_amount_cents,
       attempt.state AS attempt_state,
       (
         SELECT COUNT(*) FROM order_tenders tender
         WHERE tender.payment_attempt_id = binding.attempt_id
       ) AS tender_count,
       (
         SELECT COUNT(*) FROM order_tenders tender
         WHERE tender.payment_attempt_id = attempt.attempt_id
           AND tender.order_guid = attempt.order_guid
           AND tender.amount_cents = attempt.amount_cents
           AND (
             (attempt.provider IN ('square', 'linkly-cloud', 'manual-card') AND tender.method = 'card')
             OR (attempt.provider = 'voucher' AND tender.method = 'voucher')
           )
       ) AS matching_tender_count
     FROM payment_action_bindings binding
     LEFT JOIN payment_attempts attempt
       ON attempt.attempt_id = binding.attempt_id
     WHERE binding.order_guid = ?
     ORDER BY binding.created_at_iso, binding.action_id`,
    [orderGuid],
  );

  // 先校验每条不可变历史，再忽略可证明安全的终态，避免损坏记录被 SQL 过滤掉。
  const candidates = rows.flatMap((row) => {
    const candidate = recoverBoundActionCandidate(row, orderGuid);
    return candidate ? [candidate] : [];
  });
  if (candidates.length > 1) {
    throw new Error("Multiple payment action bindings require support.");
  }
  return candidates[0] ?? null;
}

function recoverBoundActionCandidate(
  row: BoundActionRow,
  orderGuid: string,
): RecoveredPaymentBoundAction | null {
  const identity = parseBoundActionSignature(
    text(row.request_signature, "payment action request signature"),
  );
  const action: RecoveredPaymentBoundAction = {
    actionId: strictId(
      text(row.action_id, "payment action id"),
      "payment action id",
    ),
    attemptId: strictId(
      text(row.binding_attempt_id, "payment action attempt id"),
      "payment action attempt id",
    ),
    ...identity,
  };
  const bindingIdempotencyKey = text(
    row.binding_idempotency_key,
    "payment action idempotency key",
  );
  const tenderCount = integer(
    row.tender_count,
    "payment action tender count",
  );
  const matchingTenderCount = integer(
    row.matching_tender_count,
    "matching payment action tender count",
  );
  if (
    tenderCount < 0 ||
    matchingTenderCount < 0 ||
    matchingTenderCount > tenderCount
  ) {
    throw new Error("Payment action tender history is inconsistent.");
  }

  const persistedAttemptId = nullableText(row.persisted_attempt_id);
  if (persistedAttemptId === null) {
    if (tenderCount !== 0) {
      throw new Error(
        "Payment action without attempt unexpectedly has a tender.",
      );
    }
    return action;
  }

  const state = paymentAttemptState(row.attempt_state);
  if (
    action.attemptId !==
      strictId(persistedAttemptId, "persisted payment attempt id") ||
    orderGuid !==
      strictId(
        text(row.attempt_order_guid, "payment attempt order guid"),
        "payment attempt order guid",
      ) ||
    bindingIdempotencyKey !==
      text(row.attempt_idempotency_key, "payment attempt idempotency key") ||
    action.provider !== paymentProvider(row.attempt_provider) ||
    action.operation !== paymentOperation(row.attempt_operation) ||
    action.amount.cents !==
      integer(row.attempt_amount_cents, "payment attempt amount")
  ) {
    throw new Error("Payment action binding and attempt identity diverged.");
  }

  if (state === "Declined") {
    if (tenderCount !== 0) {
      throw new Error(
        "Declined payment action unexpectedly has a tender.",
      );
    }
    return null;
  }
  // Approved 已由唯一匹配 tender 消费后不再是未决 action；abandon 仍由订单级 tender 门禁拒绝。
  if (
    state === "Approved" &&
    tenderCount === 1 &&
    matchingTenderCount === 1
  ) {
    return null;
  }
  return action;
}

function parseBoundActionSignature(
  value: string,
): Pick<
  RecoveredPaymentBoundAction,
  "provider" | "operation" | "amount" | "manualConfirmed"
> {
  let decoded: unknown;
  try {
    decoded = JSON.parse(value);
  } catch {
    throw new Error("Payment action request signature is invalid JSON.");
  }
  if (!Array.isArray(decoded) || (decoded.length !== 4 && decoded.length !== 5)) {
    throw new Error("Payment action request signature shape is invalid.");
  }
  const [providerValue, operationValue, currency, amountValue] = decoded;
  const provider = paymentProvider(providerValue);
  const operation = paymentOperation(operationValue);
  const manualConfirmed = decoded.length === 5 ? decoded[4] === "confirmed" : undefined;
  if (
    provider === "manual-card" &&
    (decoded.length !== 5 || manualConfirmed !== true || operation !== "purchase")
  ) {
    throw new Error("Manual card payment action confirmation is invalid.");
  }
  if (provider !== "manual-card" && decoded.length !== 4) {
    throw new Error("Payment action request signature shape is invalid.");
  }
  if (
    currency !== "AUD" ||
    !Number.isSafeInteger(amountValue) ||
    (operation === "purchase" && Number(amountValue) <= 0) ||
    (operation === "refund" &&
      (Number(amountValue) >= 0 ||
        Number(amountValue) === Number.MIN_SAFE_INTEGER))
  ) {
    throw new Error("Payment action request amount is invalid.");
  }
  return {
    provider,
    operation,
    amount: createAud(Number(amountValue)),
    ...(manualConfirmed !== undefined ? { manualConfirmed } : {}),
  };
}

function readRecoveryLine(row: RecoveryLineRow): CartLine {
  const kind = text(row.line_kind, "recovery line kind");
  if (kind !== "sale" && kind !== "return") {
    throw new Error("Payment recovery line kind is invalid.");
  }
  const priceSource = text(row.price_source, "recovery price source");
  if (
    priceSource !== "catalog" &&
    priceSource !== "promotion" &&
    priceSource !== "manual" &&
    priceSource !== "open-item"
  ) {
    throw new Error("Payment recovery price source is invalid.");
  }
  const syncProvenance = readOptionalLineSyncProvenance(row);
  return {
    lineId: text(row.line_id, "recovery line id"),
    productCode: text(row.product_code, "recovery product code"),
    itemNumber: nullableText(row.item_number),
    lookupCode: text(row.lookup_code, "recovery lookup code"),
    displayName: text(row.display_name, "recovery display name"),
    quantity: canonicalPositiveDecimal(
      text(row.quantity, "recovery quantity"),
    ),
    unitPrice: createAud(
      integer(row.unit_price_cents, "recovery unit price"),
    ),
    discount: createAud(
      integer(row.discount_cents, "recovery line discount"),
    ),
    actualAmount: createAud(
      integer(row.actual_amount_cents, "recovery line actual amount"),
    ),
    priceSource,
    ...(syncProvenance ? { syncProvenance } : {}),
    kind,
    returnSourceKey: nullableText(row.return_source_key),
    originalOrderGuid: nullableText(row.original_order_guid),
    originalOrderDetailGuid: nullableText(
      row.original_order_detail_guid,
    ),
  };
}

function readOptionalLineSyncProvenance(
  row: RecoveryLineRow,
): LineSyncProvenance | undefined {
  const referenceCode = row.reference_code;
  const priceSource = row.sync_price_source;
  if (
    (referenceCode === null || referenceCode === undefined) &&
    (priceSource === null || priceSource === undefined)
  ) {
    return undefined;
  }
  if (
    referenceCode === undefined ||
    priceSource === null ||
    priceSource === undefined
  ) {
    throw new Error("Payment recovery line sync provenance is invalid.");
  }
  try {
    return normalizeLineSyncProvenance({
      referenceCode:
        referenceCode === null
          ? null
          : text(referenceCode, "recovery line reference code"),
      priceSource: integer(
        priceSource,
        "recovery line backend price source",
      ),
    });
  } catch {
    throw new Error("Payment recovery line sync provenance is invalid.");
  }
}

function mutationFromOrder(
  replayed: boolean,
  draftId: string,
  order: RecoveryOrderRow,
): PaymentDraftMutation {
  return {
    replayed,
    draftId,
    orderGuid: text(order.order_guid, "payment order guid"),
    localSequence: positiveInteger(
      order.local_sequence,
      "payment local sequence",
    ),
    soldAtIso: canonicalIso(
      text(order.sold_at_iso, "payment sold time"),
      "payment sold time",
    ),
    state: orderState(order.state),
  };
}

function assertRecoveryOrderMatchesFingerprint(
  order: RecoveryOrderRow,
  draft: NormalizedDraft,
): void {
  if (
    text(order.request_fingerprint, "payment draft fingerprint") !==
      draft.fingerprint ||
    text(order.pricing_state_json, "payment pricing state JSON") !==
      draft.pricingStateJson ||
    text(order.store_code, "payment store code") !== draft.identity.storeCode ||
    text(order.device_code, "payment device code") !==
      draft.identity.deviceCode ||
    text(order.cashier_id, "payment cashier id") !==
      draft.identity.cashierId ||
    text(order.cashier_name, "payment cashier name") !==
      draft.identity.cashierName ||
    integer(order.total_cents, "payment subtotal") !==
      draft.cart.subtotal.cents ||
    integer(order.discount_cents, "payment discount") !==
      draft.cart.discount.cents ||
    integer(order.actual_amount_cents, "payment actual amount") !==
      draft.cart.actualAmount.cents ||
    nullableText(order.original_order_guid) !== draft.originalOrderGuid
  ) {
    throw new Error("Payment draft order no longer matches its binding.");
  }
}

async function allocateLocalSequence(
  transaction: SqliteConnectionPort,
  nowIso: string,
): Promise<number> {
  await transaction.run(
    `INSERT INTO app_settings (setting_key, setting_value, updated_at_iso)
     VALUES ('local_sequence', '0', ?)
     ON CONFLICT(setting_key) DO NOTHING`,
    [nowIso],
  );
  const row = await transaction.getFirst<{ next_sequence: unknown }>(
    `UPDATE app_settings
     SET setting_value = CAST(setting_value AS INTEGER) + 1,
       updated_at_iso = ?
     WHERE setting_key = 'local_sequence'
     RETURNING setting_value AS next_sequence`,
    [nowIso],
  );
  return positiveInteger(row?.next_sequence, "payment local sequence");
}

function normalizeIdentity(input: PaymentDraftIdentity): PaymentDraftIdentity {
  return Object.freeze({
    storeCode: strictText(input.storeCode, "payment store code", 64),
    deviceCode: strictId(input.deviceCode, "payment device code"),
    cashierId: strictId(input.cashierId, "payment cashier id"),
    cashierName: strictText(input.cashierName, "payment cashier name", 256),
  });
}

function normalizeRecallBinding(
  input: RecallActiveBinding | null,
  identity: PaymentDraftIdentity,
): RecallActiveBinding | null {
  if (input === null) return null;
  if (!input || typeof input !== "object" || input.kind !== "recalled") {
    throw new TypeError("Payment recall binding is invalid.");
  }
  const storeCode = strictText(
    input.scope?.storeCode,
    "payment recall store code",
    64,
  );
  const deviceCode = strictId(
    input.scope?.deviceCode,
    "payment recall device code",
  );
  if (
    storeCode !== identity.storeCode ||
    deviceCode !== identity.deviceCode
  ) {
    throw new TypeError(
      "Payment recall binding scope does not match draft identity.",
    );
  }
  return Object.freeze({
    kind: "recalled",
    scope: Object.freeze({ storeCode, deviceCode }),
    holdId: strictId(input.holdId, "payment recall hold id"),
    recallAttemptId: strictId(
      input.recallAttemptId,
      "payment recall attempt id",
    ),
  });
}

function normalizeScope(input: PaymentRecoveryScope): PaymentRecoveryScope {
  return {
    storeCode: strictText(input.storeCode, "payment store code", 64),
    deviceCode: strictId(input.deviceCode, "payment device code"),
  };
}

function normalizeAuditActor(
  input: AuditActorSnapshot,
): AuditActorSnapshot {
  if (!input || typeof input !== "object") {
    throw new TypeError("Payment draft audit actor is required.");
  }
  const actor = auditActorSnapshotFromPayload(auditActorPayload(input));
  if (!actor) {
    throw new TypeError("Payment draft audit actor is invalid.");
  }
  return actor;
}

function audInteger(
  input: Readonly<{ currency: string; cents: number }>,
  label: string,
): ReturnType<typeof createAud> {
  if (input.currency !== "AUD" || !Number.isSafeInteger(input.cents)) {
    throw new TypeError(`${label} must be AUD integer cents.`);
  }
  return createAud(input.cents);
}

function nonNegativeAud(
  input: Readonly<{ currency: string; cents: number }>,
  label: string,
): ReturnType<typeof createAud> {
  const amount = audInteger(input, label);
  if (amount.cents < 0) throw new TypeError(`${label} cannot be negative.`);
  return amount;
}

function canonicalPositiveDecimal(value: string): string {
  const normalized = value.trim();
  if (!/^\d+(?:\.\d+)?$/u.test(normalized)) {
    throw new TypeError("Payment quantity must be a positive decimal.");
  }
  const [wholeInput = "", fractionInput = ""] = normalized.split(".");
  const whole = wholeInput.replace(/^0+(?=\d)/u, "");
  const fraction = fractionInput.replace(/0+$/u, "");
  const result = fraction ? `${whole}.${fraction}` : whole;
  if (result === "0") {
    throw new TypeError("Payment quantity must be greater than zero.");
  }
  return result;
}

function paymentProvider(value: unknown): PaymentProvider {
  const provider = text(value, "payment provider");
  if (
    provider === "square" ||
    provider === "linkly-cloud" ||
    provider === "voucher" ||
    provider === "manual-card"
  ) {
    return provider;
  }
  throw new Error("Payment recovery provider is invalid.");
}

function checkoutTenderMethod(
  value: unknown,
): PaymentCheckoutDraft["tenders"][number]["method"] {
  const method = text(value, "payment checkout tender method");
  if (method === "cash" || method === "card" || method === "voucher") {
    return method;
  }
  throw new Error("Payment checkout tender method is invalid.");
}

function paymentOperation(value: unknown): PaymentOperation {
  const operation = text(value, "payment operation");
  if (operation === "purchase" || operation === "refund") return operation;
  throw new Error("Payment recovery operation is invalid.");
}

function paymentAttemptState(value: unknown): PaymentAttempt["state"] {
  const state = text(value, "payment attempt state");
  if (
    state === "Created" ||
    state === "Submitted" ||
    state === "Pending" ||
    state === "Approved" ||
    state === "Declined" ||
    state === "Cancelled" ||
    state === "Unknown"
  ) {
    return state;
  }
  throw new Error("Payment recovery state is invalid.");
}

function blockingState(value: unknown): PaymentAttempt["state"] {
  const state = text(value, "blocking payment state");
  if (
    state === "Created" ||
    state === "Submitted" ||
    state === "Pending" ||
    state === "Approved" ||
    state === "Unknown"
  ) {
    return state;
  }
  throw new Error("Payment recovery state is not blocking.");
}

function orderState(value: unknown): LocalOrder["state"] {
  const state = text(value, "payment order state");
  if (
    state === "Draft" ||
    state === "Completing" ||
    state === "CompletedLocal" ||
    state === "PendingSync" ||
    state === "Syncing" ||
    state === "Synced" ||
    state === "Blocked403" ||
    state === "Rejected"
  ) {
    return state;
  }
  throw new Error("Payment order state is invalid.");
}

function safeAdd(left: number, right: number): number {
  const value = left + right;
  if (!Number.isSafeInteger(value)) {
    throw new TypeError("Payment cart amount exceeds safe integer bounds.");
  }
  return value;
}

function strictId(value: string, label: string): string {
  return strictText(value, label, 128);
}

function strictUuid(value: string, label: string): string {
  const normalized = strictId(value, label);
  if (
    !/^[0-9a-f]{8}-[0-9a-f]{4}-[1-8][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/iu.test(
      normalized,
    )
  ) {
    throw new TypeError(`${label} must be a UUID.`);
  }
  return normalized;
}

function strictText(value: string, label: string, max: number): string {
  if (typeof value !== "string") throw new TypeError(`${label} is invalid.`);
  const normalized = value.trim();
  if (
    !normalized ||
    normalized.length > max ||
    /[\u0000-\u001f\u007f]/u.test(normalized)
  ) {
    throw new TypeError(`${label} is invalid.`);
  }
  return normalized;
}

function optionalText(
  value: unknown,
  label = "optional text",
  max = 512,
): string | null {
  if (value === null || value === undefined) return null;
  if (typeof value !== "string") throw new TypeError(`${label} is invalid.`);
  return strictText(value, label, max);
}

function nullableText(value: unknown): string | null {
  return value === null || value === undefined
    ? null
    : text(value, "nullable text");
}

function canonicalIso(value: string, label: string): string {
  const parsed = Date.parse(value);
  if (!Number.isFinite(parsed) || new Date(parsed).toISOString() !== value) {
    throw new TypeError(`${label} must be canonical ISO UTC.`);
  }
  return value;
}

function text(value: unknown, label: string): string {
  if (typeof value !== "string" || !value.trim()) {
    throw new Error(`Persisted ${label} is invalid.`);
  }
  return value;
}

function integer(value: unknown, label: string): number {
  const parsed = Number(value);
  if (!Number.isSafeInteger(parsed)) {
    throw new Error(`Persisted ${label} is invalid.`);
  }
  return parsed;
}

function positiveInteger(value: unknown, label: string): number {
  const parsed = integer(value, label);
  if (parsed <= 0) throw new Error(`Persisted ${label} is invalid.`);
  return parsed;
}

function errorMessage(error: unknown): string {
  return error instanceof Error ? error.message : String(error);
}

function invalid<T>(message: string): T {
  throw new TypeError(message);
}
