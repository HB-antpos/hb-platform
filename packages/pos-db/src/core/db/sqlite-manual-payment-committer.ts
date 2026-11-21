import type { SqliteConnectionPort } from "./types";
import {
  auditActorSnapshotFromPayload,
  type AuditActorSnapshot,
} from "@hb/pos-domain/core/contracts/audit-actor";
import type {
  ApprovedPaymentOrderCommitResult,
  AuditEventDraft,
  OutboxMessageDraft,
  RecalledHoldCompletion,
} from "@hb/pos-domain/core/contracts/order";
import type { RecallActiveBinding } from "@hb/pos-domain/core/contracts/terminal-cart";

/**
 * 取单订单的人工已收款收尾所需的「盖章」：取单审计事件号、完成时刻和操作人。
 * 取单绑定本身不由调用方给，而是从草稿指纹里读（恢复中心场景下活动购物车可能早已不在）。
 */
export type ManualPaymentRecallStamp = Readonly<{
  eventId: string;
  recalledAtIso: string;
  actor: AuditActorSnapshot;
}>;

/**
 * 取单收尾的写库逻辑（取单围栏、挂单记录、共享挂单 claim、订单来源）在各 App 的数据库层，共享包不能依赖它们，
 * 所以由 App 注入；与卡通道最后一笔 tender 的取单收尾（SqliteApprovedPaymentOrderCommitter）是同一套。
 */
export interface ManualPaymentRecallPort {
  /**
   * 写 tender 之前调用：按取单绑定与盖章生成取单收尾，并校验取单围栏 / 挂单仍与绑定一致。
   * 不一致就抛错，整个事务回滚，案件保持 manual-paid、可重试。
   */
  prepare(
    transaction: SqliteConnectionPort,
    input: Readonly<{
      binding: RecallActiveBinding;
      orderGuid: string;
      stamp: ManualPaymentRecallStamp;
    }>,
  ): Promise<RecalledHoldCompletion>;
  /** 订单、tender、审计、outbox 都已写入之后调用：绑定围栏、写订单来源 / 完成共享 claim、挂单置 Recalled、写取单审计、删除围栏。 */
  complete(
    transaction: SqliteConnectionPort,
    input: Readonly<{ orderGuid: string; completion: RecalledHoldCompletion }>,
  ): Promise<void>;
}

export type ManualPaymentOrderCommit = Readonly<{
  recordId: string;
  actionId: string;
  orderGuid: string;
  attemptId: string;
  storeCode: string;
  deviceCode: string;
  authorizationId: string;
  tenderGuid: string;
  completionAuditEvent: AuditEventDraft;
  outbox: OutboxMessageDraft;
  /** 订单若是取单订单（草稿指纹带取单绑定），最后一笔人工已收款必须同事务完成取单；没带盖章时按取单订单提交会 fail-closed。 */
  recallStamp?: ManualPaymentRecallStamp;
}>;

type ManualPaymentTruthRow = Readonly<{
  record_id: unknown;
  case_order_guid: unknown;
  case_attempt_id: unknown;
  case_store_code: unknown;
  case_device_code: unknown;
  case_state: unknown;
  is_parked: unknown;
  order_state: unknown;
  actual_amount_cents: unknown;
  order_store_code: unknown;
  order_device_code: unknown;
  attempt_order_guid: unknown;
  provider: unknown;
  operation: unknown;
  amount_cents: unknown;
  attempt_state: unknown;
  draft_state: unknown;
  draft_store_code: unknown;
  draft_device_code: unknown;
  request_fingerprint: unknown;
  action_record_id: unknown;
  action_id: unknown;
  finding: unknown;
  verified_amount_cents: unknown;
  authorization_id: unknown;
  supervisor_actor_json: unknown;
  requesting_actor_json: unknown;
  reconciliation_id: unknown;
  authorization_record_id: unknown;
  authorization_record_id_record: unknown;
  authorization_order_guid: unknown;
  authorization_attempt_id: unknown;
  authorization_action_id: unknown;
  authorization_finding: unknown;
  authorization_requesting_actor_json: unknown;
  authorization_supervisor_actor_json: unknown;
  reconciliation_record_id: unknown;
  reconciliation_order_guid: unknown;
  reconciliation_attempt_id: unknown;
  reconciliation_provider: unknown;
  reconciliation_observed_state: unknown;
  reconciliation_observed_at_iso: unknown;
  attempt_state_snapshot: unknown;
  action_created_at_iso: unknown;
}>;

type ExistingTenderRow = Readonly<{
  tender_guid: unknown;
  order_guid: unknown;
  method: unknown;
  amount_cents: unknown;
  binding_record_id: unknown;
  binding_action_id: unknown;
  binding_attempt_id: unknown;
}>;

/**
 * 人工核实只补齐本地订单账本，不修改支付提供方 attempt，也不制造 provider approval。
 * case、授权、原 attempt、tender、订单状态、审计和 outbox 均在一个独占事务中核对并提交。
 */
export class SqliteManualPaymentOrderCommitter {
  public constructor(
    private readonly connection: SqliteConnectionPort,
    private readonly nowIso: () => string,
    /** 未注入时取单订单一律 fail-closed（MANUAL_PAYMENT_RECALL_UNSUPPORTED）。 */
    private readonly recallPort?: ManualPaymentRecallPort,
  ) {}

  public completeManualPaymentOrder(
    input: ManualPaymentOrderCommit,
  ): Promise<ApprovedPaymentOrderCommitResult> {
    const normalized = normalizeInput(input);
    return this.connection.withExclusiveTransaction((transaction) =>
      this.complete(transaction, normalized));
  }

  private async complete(
    transaction: SqliteConnectionPort,
    input: ManualPaymentOrderCommit,
  ): Promise<ApprovedPaymentOrderCommitResult> {
    const truth = await transaction.getFirst<ManualPaymentTruthRow>(
      `SELECT
        c.record_id, c.order_guid AS case_order_guid,
        c.attempt_id AS case_attempt_id, c.store_code AS case_store_code,
        c.device_code AS case_device_code, c.state AS case_state, c.is_parked,
        o.state AS order_state, o.actual_amount_cents,
        o.store_code AS order_store_code, o.device_code AS order_device_code,
        p.order_guid AS attempt_order_guid, p.provider, p.operation,
        p.amount_cents, p.state AS attempt_state,
        d.state AS draft_state, d.store_code AS draft_store_code,
        d.device_code AS draft_device_code, d.request_fingerprint,
        a.record_id AS action_record_id, a.action_id, a.finding,
        a.verified_amount_cents, a.authorization_id, a.supervisor_actor_json,
        a.requesting_actor_json, a.reconciliation_id, a.attempt_state_snapshot,
        a.created_at_iso AS action_created_at_iso,
        auth.authorization_id AS authorization_record_id,
        auth.record_id AS authorization_record_id_record,
        auth.order_guid AS authorization_order_guid,
        auth.attempt_id AS authorization_attempt_id,
        auth.action_id AS authorization_action_id,
        auth.finding AS authorization_finding,
        auth.requesting_actor_json AS authorization_requesting_actor_json,
        auth.supervisor_actor_json AS authorization_supervisor_actor_json,
        reconcile.record_id AS reconciliation_record_id,
        reconcile.order_guid AS reconciliation_order_guid,
        reconcile.attempt_id AS reconciliation_attempt_id,
        reconcile.provider AS reconciliation_provider,
        reconcile.observed_state AS reconciliation_observed_state,
        reconcile.observed_at_iso AS reconciliation_observed_at_iso
       FROM payment_recovery_cases c
       INNER JOIN local_orders o ON o.order_guid = c.order_guid
       INNER JOIN payment_attempts p
         ON p.attempt_id = c.attempt_id AND p.order_guid = c.order_guid
       INNER JOIN payment_order_draft_bindings d ON d.order_guid = c.order_guid
       INNER JOIN payment_recovery_actions a
         ON a.action_id = ? AND a.record_id = c.record_id
       INNER JOIN payment_recovery_authorizations auth
         ON auth.authorization_id = a.authorization_id
       INNER JOIN payment_recovery_reconciliations reconcile
         ON reconcile.reconciliation_id = a.reconciliation_id
       WHERE c.record_id = ?`,
      [input.actionId, input.recordId],
    );
    if (!truth) throw new Error("MANUAL_PAYMENT_TRUTH_NOT_FOUND");
    assertExactIdentity(truth, input);

    const latestOther = await transaction.getFirst<{ action_id: unknown }>(
      `SELECT action_id FROM payment_recovery_actions
       WHERE record_id = ? AND action_id <> ?
         AND (created_at_iso > (
           SELECT created_at_iso FROM payment_recovery_actions WHERE action_id = ?
         ) OR (created_at_iso = (
           SELECT created_at_iso FROM payment_recovery_actions WHERE action_id = ?
         ) AND action_id > ?))
       LIMIT 1`,
      [input.recordId, input.actionId, input.actionId, input.actionId, input.actionId],
    );
    if (latestOther) throw new Error("MANUAL_PAYMENT_ACTION_NOT_LATEST");

    const amountCents = integer(truth.amount_cents, "manual payment attempt amount");
    if (amountCents <= 0 || integer(truth.verified_amount_cents, "manual verified amount") !== amountCents) {
      throw new Error("MANUAL_PAYMENT_AMOUNT_MISMATCH");
    }
    const orderAmountCents = integer(truth.actual_amount_cents, "manual payment order amount");
    if (orderAmountCents <= 0) throw new Error("MANUAL_PAYMENT_ORDER_AMOUNT_INVALID");
    assertSupervisorActor(truth.supervisor_actor_json);
    assertAuthorizationBinding(truth);
    assertReconciliationBinding(truth);

    const existing = await transaction.getFirst<ExistingTenderRow>(
      `SELECT t.tender_guid, t.order_guid, t.method, t.amount_cents,
        b.record_id AS binding_record_id, b.action_id AS binding_action_id,
        b.attempt_id AS binding_attempt_id
       FROM order_tenders t
       LEFT JOIN manual_payment_tender_bindings b ON b.tender_guid = t.tender_guid
       WHERE t.payment_attempt_id = ?`,
      [input.attemptId],
    );
    if (existing) {
      if (
        text(existing.tender_guid, "manual tender guid") !== input.tenderGuid ||
        text(existing.order_guid, "manual tender order guid") !== input.orderGuid ||
        text(existing.method, "manual tender method") !== "card" ||
        integer(existing.amount_cents, "manual tender amount") !== amountCents ||
        text(existing.binding_record_id, "manual binding record id") !== input.recordId ||
        text(existing.binding_action_id, "manual binding action id") !== input.actionId ||
        text(existing.binding_attempt_id, "manual binding attempt id") !== input.attemptId
      ) {
        throw new Error("MANUAL_PAYMENT_TENDER_CONFLICT");
      }
      return {
        replayed: true,
        orderGuid: input.orderGuid,
        tenderGuid: input.tenderGuid,
        completed: completedOrderState(truth.order_state),
        signedTenderAmountCents: amountCents,
      };
    }

    assertNewCommitState(truth);
    const recallBinding = decodeRecallBinding(truth.request_fingerprint);
    const recallPort = this.recallPort;
    if (recallBinding === null) {
      // 普通订单：本机不能有绑定到该订单的取单围栏（原有防线，保持不变）。
      const boundFence = await transaction.getFirst<{ kind: unknown }>(
        `SELECT kind FROM terminal_cart_fences
         WHERE store_code = ? AND device_code = ? AND bound_order_guid = ?`,
        [input.storeCode, input.deviceCode, input.orderGuid],
      );
      if (boundFence) throw new Error("MANUAL_PAYMENT_RECALL_UNSUPPORTED");
    } else if (
      !recallPort ||
      recallBinding.scope.storeCode !== input.storeCode ||
      recallBinding.scope.deviceCode !== input.deviceCode
    ) {
      // 取单订单：必须接入了取单收尾，且取单绑定属于本订单所在的门店 / 终端；否则 fail-closed。
      throw new Error("MANUAL_PAYMENT_RECALL_UNSUPPORTED");
    }

    const total = await transaction.getFirst<{ tender_total: unknown }>(
      "SELECT COALESCE(SUM(amount_cents), 0) AS tender_total FROM order_tenders WHERE order_guid = ?",
      [input.orderGuid],
    );
    const completedAmountCents = checkedAdd(
      integer(total?.tender_total ?? 0, "manual existing tender total"),
      amountCents,
    );
    if (completedAmountCents < 0 || completedAmountCents > orderAmountCents) {
      throw new Error("MANUAL_PAYMENT_WOULD_OVERPAY");
    }
    const completed = completedAmountCents === orderAmountCents;
    let recalledHold: RecalledHoldCompletion | null = null;
    if (completed) {
      assertCompletion(input);
      if (recallBinding !== null && recallPort) {
        // 取单订单的最后一笔人工已收款：取单收尾与订单完成必须在同一事务内（与卡通道同口径）。
        // 部分付款不动取单围栏与挂单，等最后一笔再收尾。
        const stamp = input.recallStamp;
        if (!stamp) throw new Error("MANUAL_PAYMENT_RECALL_STAMP_REQUIRED");
        strictText(stamp.eventId, "manual recall audit event id", 128);
        canonicalIso(stamp.recalledAtIso, "manual recall time");
        // 写 tender 之前先校验围栏 / 挂单仍与取单绑定一致；不一致整事务回滚，案件保持 manual-paid 可重试。
        recalledHold = await recallPort.prepare(transaction, {
          binding: recallBinding,
          orderGuid: input.orderGuid,
          stamp,
        });
      }
    }

    const currentOrderState = text(truth.order_state, "manual payment order state");
    const now = canonicalIso(this.nowIso(), "manual payment commit time");
    const changed = await transaction.run(
      `UPDATE local_orders SET state = ?, updated_at_iso = ?
       WHERE order_guid = ? AND state = ?`,
      [completed ? "PendingSync" : "Completing", now, input.orderGuid, currentOrderState],
    );
    if (changed.changes !== 1) throw new Error("MANUAL_PAYMENT_ORDER_CAS_FAILED");
    await transaction.run(
      `INSERT INTO order_tenders (
        tender_guid, order_guid, method, amount_cents, payment_attempt_id, created_at_iso
      ) VALUES (?, ?, 'card', ?, ?, ?)`,
      [input.tenderGuid, input.orderGuid, amountCents, input.attemptId, now],
    );
    await transaction.run(
      `INSERT INTO manual_payment_tender_bindings (
        tender_guid, record_id, action_id, attempt_id, created_at_iso
      ) VALUES (?, ?, ?, ?, ?)`,
      [input.tenderGuid, input.recordId, input.actionId, input.attemptId, now],
    );

    if (completed) {
      await appendAudit(transaction, input.completionAuditEvent, input, now);
      await transaction.run(
        `INSERT INTO outbox_messages (
          message_id, aggregate_id, kind, payload_json, state, attempt_count,
          next_attempt_at_iso, lease_id, lease_expires_at_iso, last_error_code,
          created_at_iso, updated_at_iso
        ) VALUES (?, ?, 'order-sync', ?, 'pending', 0, ?, NULL, NULL, NULL, ?, ?)`,
        [input.outbox.messageId, input.orderGuid, input.outbox.payloadJson,
          input.outbox.nextAttemptAtIso, now, now],
      );
      if (recalledHold !== null && recallPort) {
        // 订单与 tender 都已写入后再收尾：绑定围栏、写订单来源 / 完成共享 claim、挂单置 Recalled、写取单审计、删除围栏。
        await recallPort.complete(transaction, {
          orderGuid: input.orderGuid,
          completion: recalledHold,
        });
      }
    }
    return {
      replayed: false,
      orderGuid: input.orderGuid,
      tenderGuid: input.tenderGuid,
      completed,
      signedTenderAmountCents: amountCents,
    };
  }
}

function assertExactIdentity(
  truth: ManualPaymentTruthRow,
  input: ManualPaymentOrderCommit,
): void {
  if (
    text(truth.record_id, "manual record id") !== input.recordId ||
    text(truth.case_order_guid, "manual case order guid") !== input.orderGuid ||
    text(truth.case_attempt_id, "manual case attempt id") !== input.attemptId ||
    text(truth.attempt_order_guid, "manual attempt order guid") !== input.orderGuid ||
    text(truth.action_record_id, "manual action record id") !== input.recordId
  ) {
    throw new Error("MANUAL_PAYMENT_IDENTITY_MISMATCH");
  }
  if (
    text(truth.case_store_code, "manual case store code") !== input.storeCode ||
    text(truth.case_device_code, "manual case device code") !== input.deviceCode ||
    text(truth.order_store_code, "manual order store code") !== input.storeCode ||
    text(truth.order_device_code, "manual order device code") !== input.deviceCode ||
    text(truth.draft_store_code, "manual draft store code") !== input.storeCode ||
    text(truth.draft_device_code, "manual draft device code") !== input.deviceCode
  ) {
    throw new Error("MANUAL_PAYMENT_SCOPE_MISMATCH");
  }
  if (text(truth.authorization_id, "manual authorization id") !== input.authorizationId) {
    throw new Error("MANUAL_PAYMENT_AUTHORIZATION_MISMATCH");
  }
}

function assertAuthorizationBinding(truth: ManualPaymentTruthRow): void {
  if (
    text(truth.authorization_record_id, "manual authorization record id") !== text(truth.authorization_id, "manual authorization id") ||
    text(truth.authorization_record_id_record, "manual authorization record") !== text(truth.record_id, "manual record id") ||
    text(truth.authorization_order_guid, "manual authorization order") !== text(truth.case_order_guid, "manual case order") ||
    text(truth.authorization_attempt_id, "manual authorization attempt") !== text(truth.case_attempt_id, "manual case attempt") ||
    text(truth.authorization_action_id, "manual authorization action") !== text(truth.action_id, "manual action id") ||
    text(truth.authorization_finding, "manual authorization finding") !== "paid" ||
    text(truth.authorization_requesting_actor_json, "manual requesting actor") !== text(truth.requesting_actor_json, "manual action requesting actor") ||
    text(truth.authorization_supervisor_actor_json, "manual authorizing actor") !== text(truth.supervisor_actor_json, "manual action supervisor actor")
  ) {
    throw new Error("MANUAL_PAYMENT_AUTHORIZATION_BINDING_MISMATCH");
  }
  const requesting = parseActor(truth.requesting_actor_json, "MANUAL_PAYMENT_REQUESTING_ACTOR_INVALID");
  const supervisor = parseActor(truth.supervisor_actor_json, "MANUAL_PAYMENT_SUPERVISOR_ACTOR_INVALID");
  if (requesting.cashierId === supervisor.cashierId ||
      (requesting.userGuid !== null && supervisor.userGuid !== null && requesting.userGuid === supervisor.userGuid)) {
    throw new Error("MANUAL_PAYMENT_AUTHORIZATION_ACTORS_NOT_DISTINCT");
  }
}

function assertReconciliationBinding(truth: ManualPaymentTruthRow): void {
  const observedAt = canonicalIso(truth.reconciliation_observed_at_iso, "manual reconciliation time");
  const ageMs = Date.parse(canonicalIso(truth.action_created_at_iso, "manual action time")) - Date.parse(observedAt);
  if (
    text(truth.reconciliation_record_id, "manual reconciliation record") !== text(truth.record_id, "manual record id") ||
    text(truth.reconciliation_order_guid, "manual reconciliation order") !== text(truth.case_order_guid, "manual case order") ||
    text(truth.reconciliation_attempt_id, "manual reconciliation attempt") !== text(truth.case_attempt_id, "manual case attempt") ||
    text(truth.reconciliation_provider, "manual reconciliation provider") !== text(truth.provider, "manual provider") ||
    text(truth.reconciliation_observed_state, "manual reconciliation state") !== text(truth.attempt_state_snapshot, "manual attempt snapshot") ||
    !Number.isFinite(ageMs) || ageMs < 0 || ageMs > 5 * 60 * 1000
  ) {
    throw new Error("MANUAL_PAYMENT_RECONCILIATION_MISMATCH");
  }
}

function assertNewCommitState(truth: ManualPaymentTruthRow): void {
  const attemptState = text(truth.attempt_state, "manual attempt state");
  if (
    text(truth.case_state, "manual case state") !== "manual-paid" ||
    integer(truth.is_parked, "manual case parked state") !== 1 ||
    text(truth.finding, "manual finding") !== "paid" ||
    // 与恢复中心可移交的刷卡通道保持一致：主管结论已经落库，这里漏掉一个通道，订单就永远完成不了。
    text(truth.provider, "manual provider") !== "square" &&
      text(truth.provider, "manual provider") !== "linkly-cloud" &&
      text(truth.provider, "manual provider") !== "zeller" ||
    text(truth.operation, "manual operation") !== "purchase" ||
    !["Created", "Submitted", "Pending", "Unknown"].includes(attemptState) ||
    !["Created", "Submitted", "Pending", "Unknown"].includes(
      text(truth.attempt_state_snapshot, "manual attempt state snapshot"),
    ) ||
    text(truth.draft_state, "manual draft state") !== "Active" ||
    !["Draft", "Completing"].includes(text(truth.order_state, "manual order state"))
  ) {
    throw new Error("MANUAL_PAYMENT_TRUTH_MISMATCH");
  }
}

function assertSupervisorActor(value: unknown): void {
  let payload: unknown;
  try {
    payload = JSON.parse(text(value, "manual supervisor actor"));
  } catch {
    throw new Error("MANUAL_PAYMENT_SUPERVISOR_ACTOR_INVALID");
  }
  if (!payload || typeof payload !== "object" || Array.isArray(payload) ||
      !auditActorSnapshotFromPayload(payload as Readonly<Record<string, unknown>>)) {
    throw new Error("MANUAL_PAYMENT_SUPERVISOR_ACTOR_INVALID");
  }
}

function parseActor(value: unknown, code: string): AuditActorSnapshot {
  try {
    const actor = auditActorSnapshotFromPayload(JSON.parse(text(value, "manual actor")));
    if (actor) return actor;
  } catch {
    // 统一转换为稳定业务错误码，避免泄露持久化内容。
  }
  throw new Error(code);
}

/**
 * 读草稿指纹里的取单绑定：没有取单绑定返回 null。结构不完整的绑定一律按 MANUAL_PAYMENT_RECALL_UNSUPPORTED
 * fail-closed，不去猜，也不让半截绑定走到取单收尾。
 */
function decodeRecallBinding(value: unknown): RecallActiveBinding | null {
  let decoded: unknown;
  try {
    decoded = JSON.parse(text(value, "manual draft fingerprint"));
  } catch {
    throw new Error("MANUAL_PAYMENT_DRAFT_FINGERPRINT_INVALID");
  }
  if (!decoded || typeof decoded !== "object" || (decoded as { version?: unknown }).version !== 2) {
    throw new Error("MANUAL_PAYMENT_DRAFT_FINGERPRINT_INVALID");
  }
  const raw = (decoded as { recallBinding?: unknown }).recallBinding;
  if (raw === null || raw === undefined) return null;
  const binding = raw as {
    kind?: unknown;
    scope?: { storeCode?: unknown; deviceCode?: unknown } | null;
    holdId?: unknown;
    recallAttemptId?: unknown;
  };
  if (
    typeof raw !== "object" || Array.isArray(raw) ||
    binding.kind !== "recalled" ||
    !binding.scope || typeof binding.scope !== "object" ||
    !isIdentifier(binding.scope.storeCode) || !isIdentifier(binding.scope.deviceCode) ||
    !isIdentifier(binding.holdId) || !isIdentifier(binding.recallAttemptId)
  ) {
    throw new Error("MANUAL_PAYMENT_RECALL_UNSUPPORTED");
  }
  return Object.freeze({
    kind: "recalled",
    scope: Object.freeze({
      storeCode: binding.scope.storeCode,
      deviceCode: binding.scope.deviceCode,
    }),
    holdId: binding.holdId,
    recallAttemptId: binding.recallAttemptId,
  });
}

function isIdentifier(value: unknown): value is string {
  return typeof value === "string" && value.trim() === value && value.length > 0 && value.length <= 128;
}

function assertCompletion(input: ManualPaymentOrderCommit): void {
  if (
    input.outbox.kind !== "order-sync" ||
    input.outbox.aggregateId !== input.orderGuid ||
    !strictText(input.outbox.messageId, "manual outbox id", 128) ||
    !strictText(input.outbox.payloadJson, "manual outbox payload", 1_048_576)
  ) {
    throw new Error("MANUAL_PAYMENT_OUTBOX_INVALID");
  }
  canonicalIso(input.outbox.nextAttemptAtIso, "manual outbox next attempt time");
  const audit = input.completionAuditEvent;
  if (
    audit.eventType !== "PAYMENT_COMPLETE" ||
    audit.orderGuid !== input.orderGuid ||
    audit.correlationId !== input.actionId
  ) {
    throw new Error("MANUAL_PAYMENT_COMPLETION_AUDIT_INVALID");
  }
  strictText(audit.eventId, "manual completion audit id", 128);
  canonicalIso(audit.occurredAtIso, "manual completion audit time");
  assertSafeAuditPayload(audit.payload);
}

async function appendAudit(
  transaction: SqliteConnectionPort,
  event: AuditEventDraft,
  input: ManualPaymentOrderCommit,
  now: string,
): Promise<void> {
  await transaction.run(
    `INSERT INTO audit_events (
      event_id, event_type, occurred_at_iso, order_guid, correlation_id,
      payload_json, uploaded_at_iso, delivery_state, attempt_count,
      next_attempt_at_iso, last_error_code, scope_store_code, scope_device_code
    ) VALUES (?, ?, ?, ?, ?, ?, NULL, 'pending', 0, ?, NULL, ?, ?)`,
    [event.eventId, event.eventType, event.occurredAtIso, input.orderGuid,
      input.actionId, JSON.stringify(event.payload), now,
      input.storeCode, input.deviceCode],
  );
}

function normalizeInput(input: ManualPaymentOrderCommit): ManualPaymentOrderCommit {
  return Object.freeze({
    ...input,
    recordId: strictText(input.recordId, "manual record id", 128),
    actionId: strictText(input.actionId, "manual action id", 128),
    orderGuid: strictText(input.orderGuid, "manual order guid", 128),
    attemptId: strictText(input.attemptId, "manual attempt id", 128),
    storeCode: strictText(input.storeCode, "manual store code", 64),
    deviceCode: strictText(input.deviceCode, "manual device code", 128),
    authorizationId: strictText(input.authorizationId, "manual authorization id", 128),
    tenderGuid: strictText(input.tenderGuid, "manual tender guid", 128),
  });
}

function completedOrderState(value: unknown): boolean {
  const state = text(value, "manual payment order state");
  if (state === "Draft" || state === "Completing") return false;
  if (["CompletedLocal", "PendingSync", "Syncing", "Synced", "Blocked403", "Rejected"].includes(state)) return true;
  throw new Error("MANUAL_PAYMENT_ORDER_STATE_INVALID");
}

function strictText(value: unknown, label: string, max: number): string {
  if (typeof value !== "string" || value.trim() !== value || value.length === 0 || value.length > max) {
    throw new Error(`${label} is invalid.`);
  }
  return value;
}

function text(value: unknown, label: string): string {
  if (typeof value !== "string") throw new Error(`${label} is invalid.`);
  return value;
}

function integer(value: unknown, label: string): number {
  const number = Number(value);
  if (!Number.isSafeInteger(number)) throw new Error(`${label} must use integer cents.`);
  return number;
}

function checkedAdd(left: number, right: number): number {
  const result = left + right;
  if (!Number.isSafeInteger(result)) throw new Error("MANUAL_PAYMENT_TOTAL_INVALID");
  return result;
}

function canonicalIso(value: unknown, label: string): string {
  const textValue = strictText(value, label, 64);
  const parsed = Date.parse(textValue);
  if (!Number.isFinite(parsed) || new Date(parsed).toISOString() !== textValue) {
    throw new Error(`${label} is invalid.`);
  }
  return textValue;
}

function assertSafeAuditPayload(value: unknown, path = "$", visited = new Set<object>()): void {
  if (value === null || typeof value === "string" || typeof value === "boolean" ||
      typeof value === "number" && Number.isFinite(value)) return;
  if (Array.isArray(value)) {
    if (visited.has(value)) throw new Error(`Manual audit payload is cyclic at ${path}.`);
    visited.add(value);
    value.forEach((entry, index) => assertSafeAuditPayload(entry, `${path}[${index}]`, visited));
    visited.delete(value);
    return;
  }
  if (!value || typeof value !== "object" || Object.getPrototypeOf(value) !== Object.prototype) {
    throw new Error(`Manual audit payload is invalid at ${path}.`);
  }
  if (visited.has(value)) throw new Error(`Manual audit payload is cyclic at ${path}.`);
  visited.add(value);
  for (const [key, entry] of Object.entries(value)) {
    if (!key.trim()) throw new Error(`Manual audit payload has an invalid key at ${path}.`);
    assertSafeAuditPayload(entry, `${path}.${key}`, visited);
  }
  visited.delete(value);
}
