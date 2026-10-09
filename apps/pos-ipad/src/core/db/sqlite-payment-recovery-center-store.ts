import { enqueueSupervisorResolvedAckInTransaction } from "@hb/pos-db/core/db/sqlite-payment-supervisor-acknowledgement-queue";
import type { SqliteConnectionPort } from "@hb/pos-db/core/db/types";

import {
  auditActorPayload,
  auditActorSnapshotFromPayload,
  type AuditActorSnapshot,
  type PaymentAttempt,
} from "../contracts";

export type PaymentRecoveryCenterStatus =
  | "result-unknown"
  | "payment-failed"
  | "charged-order-incomplete"
  | "manual-paid"
  | "manual-unpaid"
  | "manual-uncertain"
  | "provider-recovered"
  | "review-required";

export type PaymentRecoveryCenterRecord = Readonly<{
  recordId: string;
  checkoutIntentId: string;
  orderGuid: string;
  attemptId: string;
  storeCode: string;
  deviceCode: string;
  /** 当前账本尚未持久化 provider terminal 名称时必须如实为空。 */
  terminalName: string | null;
  occurredAtIso: string;
  amountCents: number;
  provider: "square" | "linkly-cloud";
  attemptState: PaymentAttempt["state"];
  orderState: string;
  isParked: boolean;
  status: PaymentRecoveryCenterStatus;
  transactionReference: string | null;
  receiptReference: string | null;
  lines: readonly Readonly<{
    id: string;
    name: string;
    quantity: string;
    amountCents: number;
  }>[];
  events: readonly Readonly<{
    id: string;
    occurredAtIso: string;
    code: "PAYMENT_RECOVERY_PARKED" | "PAYMENT_RECOVERY_MANUAL_FINDING";
    source: "system" | "operator";
    params: Readonly<Record<string, string | number | null>>;
  }>[];
}>;

export type PaymentRecoveryCenterScope = Readonly<{
  storeCode: string;
  deviceCode: string;
}>;

export type ParkPaymentRecoveryInput = PaymentRecoveryCenterScope & Readonly<{
  orderGuid: string;
  attemptId: string;
  actionId: string;
  actor: AuditActorSnapshot;
}>;

export type ManualPaymentRecoveryFindingInput = PaymentRecoveryCenterScope & Readonly<{
  recordId: string;
  actionId: string;
  finding: "paid" | "unpaid" | "uncertain";
  verifiedAmountCents: number | null;
  evidenceReference: string;
  note: string;
  authorizationId: string;
  supervisorActor: AuditActorSnapshot;
  requestingActor: AuditActorSnapshot;
  /** paid 结论必须引用刚完成且绑定原 attempt 的只读 provider 对账。 */
  reconciliationId?: string;
}>;

export type PaymentRecoveryReconciliationInput = PaymentRecoveryCenterScope & Readonly<{
  recordId: string;
  reconciliationId: string;
}>;

export type ManualPaymentRecoveryFindingResult = Readonly<{
  record: PaymentRecoveryCenterRecord;
  actionId: string;
  authorizationId: string;
  replayed: boolean;
}>;

export type ManualPaidRecoveryCommitContext = Readonly<{
  record: PaymentRecoveryCenterRecord;
  actionId: string;
  authorizationId: string;
  supervisorActor: AuditActorSnapshot;
  tenderGuid: string | null;
}>;

type CaseRow = Readonly<{
  record_id: unknown; order_guid: unknown; attempt_id: unknown;
  store_code: unknown; device_code: unknown; case_state: unknown;
  opened_at_iso: unknown; updated_at_iso: unknown; amount_cents: unknown;
  provider: unknown; attempt_state: unknown; order_state: unknown;
  txn_ref: unknown; rfn: unknown;
  draft_id: unknown;
  is_parked: unknown;
}>;

/** 异常支付与当前购物车分离后的唯一耐久账本；所有写入均按原 order/attempt 精确 CAS。 */
export class SqlitePaymentRecoveryCenterStore {
  public constructor(
    private readonly connection: SqliteConnectionPort,
    private readonly createRecordId: () => string,
    private readonly createAuditEventId: () => string,
    private readonly nowIso: () => string,
  ) {}

  public parkExact(input: ParkPaymentRecoveryInput): Promise<PaymentRecoveryCenterRecord> {
    const scope = normalizeScope(input);
    const orderGuid = strictText(input.orderGuid, "recovery order guid", 128);
    const attemptId = strictText(input.attemptId, "recovery attempt id", 128);
    const actionId = strictText(input.actionId, "recovery park action id", 128);
    const actor = normalizeActor(input.actor);
    return this.connection.withExclusiveTransaction(async (transaction) => {
      const existingRows = await transaction.getAll<{
        record_id: unknown; order_guid: unknown; attempt_id: unknown;
        store_code: unknown; device_code: unknown; park_action_id: unknown; is_parked: unknown;
        matched_action_id: unknown;
      }>(
        `SELECT c.record_id, c.order_guid, c.attempt_id, c.store_code, c.device_code,
           c.park_action_id, c.is_parked,
           (SELECT pa.action_id FROM payment_recovery_park_actions pa
            WHERE pa.record_id = c.record_id AND pa.action_id = ?) AS matched_action_id
         FROM payment_recovery_cases c
         WHERE c.attempt_id = ? OR EXISTS (
           SELECT 1 FROM payment_recovery_park_actions pa
           WHERE pa.record_id = c.record_id AND pa.action_id = ?
         )`,
        [actionId, attemptId, actionId],
      );
      const existing = existingRows.find((row) =>
        text(row.order_guid, "recovery order guid") === orderGuid &&
        text(row.attempt_id, "recovery attempt id") === attemptId &&
        text(row.store_code, "recovery store code") === scope.storeCode &&
        text(row.device_code, "recovery device code") === scope.deviceCode,
      );
      const actionBoundElsewhere = existingRows.some((row) =>
        row.matched_action_id !== null &&
        (!existing || text(row.record_id, "recovery record id") !== text(existing.record_id, "recovery record id")),
      );
      if (existingRows.length > (existing ? 1 : 0) || actionBoundElsewhere) {
        throw new Error("PAYMENT_RECOVERY_PARK_IDENTITY_CONFLICT");
      }
      if (existing) {
        const recordId = text(existing.record_id, "recovery record id");
        const actionAlreadyBound = existing.matched_action_id !== null;
        if (integer(existing.is_parked, "recovery parked state") === 1) {
          if (!actionAlreadyBound) throw new Error("PAYMENT_RECOVERY_PARK_IDENTITY_CONFLICT");
          return requireRecord(transaction, scope, recordId);
        }
        if (!actionAlreadyBound) {
          await transaction.run(
            `INSERT INTO payment_recovery_park_actions (action_id, record_id, actor_json, created_at_iso)
             VALUES (?, ?, ?, ?)`,
            [actionId, recordId, JSON.stringify(auditActorPayload(actor)), canonicalIso(this.nowIso(), "recovery repark time")],
          );
        }
        await transaction.run(
          `UPDATE payment_recovery_cases SET is_parked = 1, updated_at_iso = ?
           WHERE record_id = ? AND store_code = ? AND device_code = ? AND is_parked = 0`,
          [canonicalIso(this.nowIso(), "recovery repark time"), recordId, scope.storeCode, scope.deviceCode],
        );
        await appendAudit(transaction, {
          eventId: strictText(this.createAuditEventId(), "recovery audit event id", 128),
          eventType: "PAYMENT_RECOVERY_REPARKED",
          occurredAtIso: canonicalIso(this.nowIso(), "recovery repark time"),
          orderGuid,
          correlationId: actionId,
          payload: { action: "payment-recovery-reparked", ...auditActorPayload(actor) },
          scope,
        });
        return requireRecord(transaction, scope, recordId);
      }
      const truth = await transaction.getFirst<{
        order_state: unknown; store_code: unknown; device_code: unknown;
        attempt_order_guid: unknown; provider: unknown; operation: unknown;
        amount_cents: unknown; attempt_state: unknown;
      }>(
        `SELECT o.state AS order_state, o.store_code, o.device_code,
          p.order_guid AS attempt_order_guid, p.provider, p.operation,
          p.amount_cents, p.state AS attempt_state
         FROM local_orders o INNER JOIN payment_attempts p ON p.attempt_id = ?
         WHERE o.order_guid = ?`,
        [attemptId, orderGuid],
      );
      if (!truth || text(truth.store_code, "recovery store code") !== scope.storeCode ||
          text(truth.device_code, "recovery device code") !== scope.deviceCode ||
          text(truth.attempt_order_guid, "recovery attempt order guid") !== orderGuid ||
          text(truth.operation, "recovery operation") !== "purchase" ||
          !isCardProvider(truth.provider) ||
          !isParkableAttemptState(truth.attempt_state) ||
          !isUnfinishedOrderState(truth.order_state) ||
          integer(truth.amount_cents, "recovery amount") <= 0) {
        throw new Error("PAYMENT_RECOVERY_PARK_TRUTH_MISMATCH");
      }
      const now = canonicalIso(this.nowIso(), "recovery park time");
      const recordId = strictText(this.createRecordId(), "recovery record id", 128);
      await transaction.run(
        `INSERT INTO payment_recovery_cases (
          record_id, order_guid, attempt_id, store_code, device_code, state, is_parked,
          park_action_id, opened_at_iso, updated_at_iso
        ) VALUES (?, ?, ?, ?, ?, 'pending', 1, ?, ?, ?)`,
        [recordId, orderGuid, attemptId, scope.storeCode, scope.deviceCode, actionId, now, now],
      );
      await transaction.run(
        `INSERT INTO payment_recovery_park_actions (action_id, record_id, actor_json, created_at_iso)
         VALUES (?, ?, ?, ?)`,
        [actionId, recordId, JSON.stringify(auditActorPayload(actor)), now],
      );
      await appendAudit(transaction, {
        eventId: strictText(this.createAuditEventId(), "recovery audit event id", 128),
        eventType: "PAYMENT_RECOVERY_PARKED",
        occurredAtIso: now,
        orderGuid,
        correlationId: actionId,
        payload: { action: "payment-recovery-parked", attemptId, ...auditActorPayload(actor) },
        scope,
      });
      return requireRecord(transaction, scope, recordId);
    });
  }

  public list(scopeInput: PaymentRecoveryCenterScope): Promise<readonly PaymentRecoveryCenterRecord[]> {
    const scope = normalizeScope(scopeInput);
    return this.connection.withExclusiveTransaction(async (transaction) => {
      const rows = await transaction.getAll<CaseRow>(caseSelectSql(""), [scope.storeCode, scope.deviceCode]);
      const records: PaymentRecoveryCenterRecord[] = [];
      for (const row of rows) records.push(await projectRecord(transaction, row));
      return Object.freeze(records);
    });
  }

  /** 当前支付页可移交的最新卡 attempt；包含明确 Declined/Cancelled。 */
  public async findCurrentCandidate(scopeInput: PaymentRecoveryCenterScope): Promise<Readonly<{ orderGuid: string; attemptId: string; checkoutIntentId: string }> | null> {
    const scope = normalizeScope(scopeInput);
    const rows = await this.connection.getAll<{ order_guid: unknown; attempt_id: unknown; draft_id: unknown }>(
      `SELECT o.order_guid, p.attempt_id, d.draft_id
       FROM local_orders o
       INNER JOIN payment_order_draft_bindings d ON d.order_guid = o.order_guid AND d.state = 'Active'
       INNER JOIN payment_attempts p ON p.attempt_id = (
         SELECT selected.attempt_id FROM payment_attempts selected
         WHERE selected.order_guid = o.order_guid
           AND selected.operation = 'purchase'
           AND selected.provider IN ('square', 'linkly-cloud')
           AND selected.state IN ('Created', 'Submitted', 'Pending', 'Unknown', 'Approved', 'Declined', 'Cancelled')
           AND NOT EXISTS (
             SELECT 1 FROM payment_recovery_cases selected_case
             WHERE selected_case.attempt_id = selected.attempt_id
               AND selected_case.is_parked = 1
           )
         ORDER BY CASE WHEN selected.state IN ('Created', 'Submitted', 'Pending', 'Unknown', 'Approved') THEN 0 ELSE 1 END,
           selected.updated_at_iso DESC, selected.attempt_id DESC
         LIMIT 1
       )
       WHERE o.store_code = ? AND o.device_code = ?
         AND o.state IN ('Draft', 'Completing')
         AND p.operation = 'purchase' AND p.provider IN ('square', 'linkly-cloud')
         AND p.state IN ('Created', 'Submitted', 'Pending', 'Unknown', 'Approved', 'Declined', 'Cancelled')
         AND NOT EXISTS (SELECT 1 FROM payment_recovery_cases c WHERE c.attempt_id = p.attempt_id AND c.is_parked = 1)
       ORDER BY o.local_sequence DESC LIMIT 2`,
      [scope.storeCode, scope.deviceCode],
    );
    if (rows.length > 1) throw new Error("PAYMENT_RECOVERY_MULTIPLE_CURRENT_CANDIDATES");
    const row = rows[0];
    return row ? Object.freeze({ orderGuid: text(row.order_guid, "recovery order guid"), attemptId: text(row.attempt_id, "recovery attempt id"), checkoutIntentId: text(row.draft_id, "recovery draft id") }) : null;
  }

  public getExact(scopeInput: PaymentRecoveryCenterScope, recordIdInput: string): Promise<PaymentRecoveryCenterRecord | null> {
    const scope = normalizeScope(scopeInput);
    const recordId = strictText(recordIdInput, "recovery record id", 128);
    return this.connection.withExclusiveTransaction(async (transaction) => {
      const row = await transaction.getFirst<CaseRow>(caseSelectSql("AND c.record_id = ?"), [scope.storeCode, scope.deviceCode, recordId]);
      return row ? projectRecord(transaction, row) : null;
    });
  }

  /** 崩溃若发生在人工结论与 tender 提交之间，可从不可变 action 精确重放完成事务。 */
  public getManualPaidCommitContext(
    scopeInput: PaymentRecoveryCenterScope,
    recordIdInput: string,
  ): Promise<ManualPaidRecoveryCommitContext | null> {
    const scope = normalizeScope(scopeInput);
    const recordId = strictText(recordIdInput, "recovery record id", 128);
    return this.connection.withExclusiveTransaction(async (transaction) => {
      const record = await requireRecord(transaction, scope, recordId);
      const row = await transaction.getFirst<{
        action_id: unknown; authorization_id: unknown; supervisor_actor_json: unknown;
        tender_guid: unknown;
      }>(
        `SELECT a.action_id, a.authorization_id, a.supervisor_actor_json, b.tender_guid
         FROM payment_recovery_actions a
         LEFT JOIN manual_payment_tender_bindings b ON b.action_id = a.action_id
         WHERE a.record_id = ? AND a.finding = 'paid'
         ORDER BY a.created_at_iso DESC, a.action_id DESC LIMIT 1`,
        [recordId],
      );
      if (!row) return null;
      const supervisorActor = auditActorSnapshotFromPayload(
        JSON.parse(text(row.supervisor_actor_json, "manual supervisor actor")),
      );
      if (!supervisorActor) throw new Error("PAYMENT_RECOVERY_SUPERVISOR_ACTOR_INVALID");
      return Object.freeze({
        record,
        actionId: text(row.action_id, "manual recovery action id"),
        authorizationId: text(row.authorization_id, "manual authorization id"),
        supervisorActor,
        tenderGuid: nullableText(row.tender_guid),
      });
    });
  }

  /** 只把所选 case 暂时交回支付运行时；其他 parked case 继续与当前收银隔离。 */
  public resumeExact(scopeInput: PaymentRecoveryCenterScope, recordIdInput: string): Promise<PaymentRecoveryCenterRecord> {
    const scope = normalizeScope(scopeInput);
    const recordId = strictText(recordIdInput, "recovery record id", 128);
    return this.connection.withExclusiveTransaction(async (transaction) => {
      const current = await requireRecord(transaction, scope, recordId);
      if (current.status === "review-required") {
        throw new Error("PAYMENT_RECOVERY_REVIEW_REQUIRED");
      }
      if (!isUnfinishedOrderState(current.orderState)) return current;
      const changed = await transaction.run(
        `UPDATE payment_recovery_cases SET is_parked = 0, updated_at_iso = ?
         WHERE record_id = ? AND store_code = ? AND device_code = ?
           AND is_parked = 1 AND state IN ('pending', 'manual-paid', 'manual-unpaid', 'manual-uncertain', 'provider-recovered')`,
        [canonicalIso(this.nowIso(), "recovery resume time"), recordId, scope.storeCode, scope.deviceCode],
      );
      if (changed.changes !== 1 && integer((await transaction.getFirst<{ is_parked: unknown }>(
        "SELECT is_parked FROM payment_recovery_cases WHERE record_id = ?", [recordId],
      ))?.is_parked, "recovery parked state") !== 0) {
        throw new Error("PAYMENT_RECOVERY_RESUME_CAS_FAILED");
      }
      return requireRecord(transaction, scope, recordId);
    });
  }

  /** 把只读 provider 查询后的本地事实冻结下来，供 paid 结论和提交事务共同复核。 */
  public recordProviderReconciliation(
    input: PaymentRecoveryReconciliationInput,
  ): Promise<string> {
    const scope = normalizeScope(input);
    const recordId = strictText(input.recordId, "recovery record id", 128);
    const reconciliationId = strictText(
      input.reconciliationId,
      "payment reconciliation id",
      128,
    );
    return this.connection.withExclusiveTransaction(async (transaction) => {
      const current = await requireRecord(transaction, scope, recordId);
      if (!current.isParked || !["Submitted", "Pending", "Unknown"].includes(current.attemptState)) {
        throw new Error("PAYMENT_RECOVERY_RECONCILIATION_NOT_UNRESOLVED");
      }
      const existing = await transaction.getFirst<Record<string, unknown>>(
        `SELECT record_id, order_guid, attempt_id, provider, observed_state
         FROM payment_recovery_reconciliations WHERE reconciliation_id = ?`,
        [reconciliationId],
      );
      if (existing) {
        if (existing.record_id !== recordId || existing.order_guid !== current.orderGuid ||
            existing.attempt_id !== current.attemptId || existing.provider !== current.provider ||
            existing.observed_state !== current.attemptState) {
          throw new Error("PAYMENT_RECOVERY_RECONCILIATION_IDENTITY_CONFLICT");
        }
        return reconciliationId;
      }
      await transaction.run(
        `INSERT INTO payment_recovery_reconciliations (
          reconciliation_id, record_id, order_guid, attempt_id, provider,
          observed_state, observed_at_iso
        ) VALUES (?, ?, ?, ?, ?, ?, ?)`,
        [reconciliationId, recordId, current.orderGuid, current.attemptId,
          current.provider, current.attemptState,
          canonicalIso(this.nowIso(), "payment reconciliation time")],
      );
      return reconciliationId;
    });
  }

  public recordManualFinding(input: ManualPaymentRecoveryFindingInput): Promise<ManualPaymentRecoveryFindingResult> {
    const scope = normalizeScope(input);
    const recordId = strictText(input.recordId, "recovery record id", 128);
    const actionId = strictText(input.actionId, "manual recovery action id", 128);
    const finding = manualFinding(input.finding);
    const evidence = strictText(input.evidenceReference, "manual payment evidence", 256);
    const note = strictText(input.note, "manual payment note", 1000);
    const authorizationId = strictText(input.authorizationId, "manual payment authorization id", 128);
    const supervisor = normalizeActor(input.supervisorActor);
    const requesting = normalizeActor(input.requestingActor);
    if (sameActor(requesting, supervisor)) {
      throw new Error("PAYMENT_RECOVERY_REQUESTING_ACTOR_MUST_DIFFER");
    }
    const reconciliationId = input.reconciliationId === undefined
      ? null
      : strictText(input.reconciliationId, "payment reconciliation id", 128);
    return this.connection.withExclusiveTransaction(async (transaction) => {
      const current = await requireRecord(transaction, scope, recordId);
      if (!current.isParked) throw new Error("PAYMENT_RECOVERY_MANUAL_REQUIRES_PARKED_CASE");
      const amount: number | null = finding === "paid"
        ? positiveInteger(input.verifiedAmountCents, "manual verified amount")
        : input.verifiedAmountCents === null ? null : invalid("MANUAL_PAYMENT_AMOUNT_FORBIDDEN");
      if (finding === "paid" && amount !== current.amountCents) {
        throw new Error("MANUAL_PAYMENT_AMOUNT_MISMATCH");
      }
      if (current.attemptState === "Approved") {
        throw new Error("PAYMENT_RECOVERY_PROVIDER_RESULT_MUST_BE_USED");
      }
      if ((current.attemptState === "Declined" || current.attemptState === "Cancelled") && finding === "paid") {
        throw new Error("PAYMENT_RECOVERY_PROVIDER_MANUAL_CONFLICT");
      }
      const now = canonicalIso(this.nowIso(), "manual payment finding time");
      const supervisorJson = JSON.stringify(auditActorPayload(supervisor));
      const requestingJson = JSON.stringify(auditActorPayload(requesting));
      const signature = JSON.stringify([
        recordId, finding, amount, evidence, note, authorizationId,
        requestingJson, supervisorJson, reconciliationId,
      ]);
      const prior = await transaction.getFirst<{ request_signature: unknown }>(
        "SELECT request_signature FROM payment_recovery_actions WHERE action_id = ?", [actionId],
      );
      if (prior) {
        if (text(prior.request_signature, "manual recovery signature") !== signature) {
          throw new Error("PAYMENT_RECOVERY_ACTION_CONFLICT");
        }
        return { record: await requireRecord(transaction, scope, recordId), actionId, authorizationId, replayed: true };
      }
      if (finding === "paid") {
        if (!reconciliationId) throw new Error("PAYMENT_RECOVERY_RECONCILIATION_REQUIRED");
        const reconciliation = await transaction.getFirst<Record<string, unknown>>(
          `SELECT record_id, order_guid, attempt_id, provider, observed_state, observed_at_iso
           FROM payment_recovery_reconciliations WHERE reconciliation_id = ?`,
          [reconciliationId],
        );
        const observedAt = reconciliation?.observed_at_iso;
        const ageMs = typeof observedAt === "string"
          ? Date.parse(now) - Date.parse(canonicalIso(observedAt, "payment reconciliation time"))
          : Number.POSITIVE_INFINITY;
        if (!reconciliation || reconciliation.record_id !== recordId ||
            reconciliation.order_guid !== current.orderGuid || reconciliation.attempt_id !== current.attemptId ||
            reconciliation.provider !== current.provider || reconciliation.observed_state !== current.attemptState ||
            !Number.isFinite(ageMs) || ageMs < 0 || ageMs > 5 * 60 * 1000) {
          throw new Error("PAYMENT_RECOVERY_RECONCILIATION_MISMATCH");
        }
      } else if (reconciliationId !== null) {
        throw new Error("PAYMENT_RECOVERY_RECONCILIATION_FORBIDDEN");
      }
      const nextState = `manual-${finding}`;
      await transaction.run(
        `INSERT INTO payment_recovery_authorizations (
          authorization_id, record_id, order_guid, attempt_id, action_id, finding,
          requesting_actor_json, supervisor_actor_json, created_at_iso
        ) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?)`,
        [authorizationId, recordId, current.orderGuid, current.attemptId, actionId,
          finding, requestingJson, supervisorJson, now],
      );
      await transaction.run(
        `INSERT INTO payment_recovery_actions (
          action_id, record_id, request_signature, finding,
          verified_amount_cents, evidence_reference, note, authorization_id,
          supervisor_actor_json, requesting_actor_json, reconciliation_id,
          attempt_state_snapshot, created_at_iso
        ) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)`,
        [actionId, recordId, signature, finding, amount, evidence, note,
          authorizationId, supervisorJson, requestingJson, reconciliationId,
          current.attemptState, now],
      );
      const changed = await transaction.run(
        `UPDATE payment_recovery_cases SET state = ?, updated_at_iso = ?
         WHERE record_id = ? AND store_code = ? AND device_code = ?
           AND state IN ('pending', 'manual-uncertain')`,
        [nextState, now, recordId, scope.storeCode, scope.deviceCode],
      );
      if (changed.changes !== 1) throw new Error("PAYMENT_RECOVERY_MANUAL_CAS_FAILED");
      // 主管明确结案（已收款/未收款）后后端会话也要关闭，否则设备继续收到 409 且终端管理被未知会话阻塞；
      // “仍未知”不是结案，不入队。入队与结论同事务，ACK 之后可重试，失败不回滚人工结论。
      if (finding === "paid" || finding === "unpaid") {
        await enqueueSupervisorResolvedAckInTransaction(transaction, {
          attemptId: current.attemptId,
          resolution: finding,
          sourceActionId: actionId,
          createdAtIso: now,
        });
      }
      await appendAudit(transaction, {
        eventId: strictText(this.createAuditEventId(), "recovery audit event id", 128),
        eventType: "PAYMENT_RECOVERY_MANUAL_FINDING",
        occurredAtIso: now,
        orderGuid: current.orderGuid,
        correlationId: actionId,
        payload: { action: "payment-recovery-manual-finding", finding, authorizationId, ...auditActorPayload(supervisor) },
        scope,
      });
      return { record: await requireRecord(transaction, scope, recordId), actionId, authorizationId, replayed: false };
    });
  }
}

function caseSelectSql(extra: string): string {
  return `SELECT c.record_id, c.order_guid, c.attempt_id, c.store_code,
    c.device_code, c.state AS case_state, c.opened_at_iso, c.updated_at_iso,
    p.amount_cents, p.provider, p.state AS attempt_state,
    o.state AS order_state, p.txn_ref, p.rfn, d.draft_id, c.is_parked
   FROM payment_recovery_cases c
   INNER JOIN local_orders o ON o.order_guid = c.order_guid
   INNER JOIN payment_attempts p ON p.attempt_id = c.attempt_id AND p.order_guid = c.order_guid
   INNER JOIN payment_order_draft_bindings d ON d.order_guid = c.order_guid
   WHERE c.store_code = ? AND c.device_code = ? ${extra}
   ORDER BY c.updated_at_iso DESC, c.record_id`;
}

async function requireRecord(transaction: SqliteConnectionPort, scope: PaymentRecoveryCenterScope, recordId: string): Promise<PaymentRecoveryCenterRecord> {
  const row = await transaction.getFirst<CaseRow>(caseSelectSql("AND c.record_id = ?"), [scope.storeCode, scope.deviceCode, recordId]);
  if (!row) throw new Error("PAYMENT_RECOVERY_RECORD_NOT_FOUND");
  return projectRecord(transaction, row);
}

async function projectRecord(transaction: SqliteConnectionPort, row: CaseRow): Promise<PaymentRecoveryCenterRecord> {
  const recordId = text(row.record_id, "recovery record id");
  const attemptState = paymentState(row.attempt_state);
  const caseState = text(row.case_state, "recovery case state");
  const orderState = text(row.order_state, "recovery order state");
  const lines = await transaction.getAll<{ line_id: unknown; display_name: unknown; quantity: unknown; actual_amount_cents: unknown }>(
    `SELECT line_id, display_name, quantity, actual_amount_cents FROM local_order_lines
     WHERE order_guid = ? ORDER BY line_sequence`, [text(row.order_guid, "recovery order guid")],
  );
  const actions = await transaction.getAll<{ action_id: unknown; finding: unknown; verified_amount_cents: unknown; evidence_reference: unknown; note: unknown; supervisor_actor_json: unknown; created_at_iso: unknown }>(
    `SELECT action_id, finding, verified_amount_cents, evidence_reference, note,
       supervisor_actor_json, created_at_iso
    FROM payment_recovery_actions WHERE record_id = ? ORDER BY created_at_iso, action_id`, [recordId],
  );
  const manualBinding = await transaction.getFirst<{ tender_guid: unknown }>(
    "SELECT tender_guid FROM manual_payment_tender_bindings WHERE record_id = ? LIMIT 1",
    [recordId],
  );
  return Object.freeze({
    recordId,
    checkoutIntentId: text(row.draft_id, "recovery draft id"),
    orderGuid: text(row.order_guid, "recovery order guid"),
    attemptId: text(row.attempt_id, "recovery attempt id"),
    storeCode: text(row.store_code, "recovery store code"),
    deviceCode: text(row.device_code, "recovery device code"),
    terminalName: null,
    occurredAtIso: canonicalIso(text(row.opened_at_iso, "recovery opened time"), "recovery opened time"),
    amountCents: positiveInteger(row.amount_cents, "recovery amount"),
    provider: cardProvider(row.provider),
    attemptState,
    orderState,
    isParked: integer(row.is_parked, "recovery parked state") === 1,
    status: projectedStatus(caseState, attemptState, orderState, manualBinding !== null),
    transactionReference: nullableText(row.txn_ref),
    receiptReference: nullableText(row.rfn),
    lines: Object.freeze(lines.map((line) => Object.freeze({
      id: text(line.line_id, "recovery line id"), name: text(line.display_name, "recovery line name"),
      quantity: text(line.quantity, "recovery line quantity"), amountCents: integer(line.actual_amount_cents, "recovery line amount"),
    }))),
    events: Object.freeze([
      Object.freeze({ id: `park:${recordId}`, occurredAtIso: canonicalIso(text(row.opened_at_iso, "recovery opened time"), "recovery opened time"), code: "PAYMENT_RECOVERY_PARKED" as const, source: "system" as const, params: Object.freeze({}) }),
      ...actions.map((action) => {
        const supervisor = auditActorSnapshotFromPayload(
          JSON.parse(text(action.supervisor_actor_json, "manual supervisor actor")),
        );
        if (!supervisor) throw new Error("PAYMENT_RECOVERY_SUPERVISOR_ACTOR_INVALID");
        return Object.freeze({
        id: text(action.action_id, "recovery action id"),
        occurredAtIso: canonicalIso(text(action.created_at_iso, "recovery action time"), "recovery action time"),
        code: "PAYMENT_RECOVERY_MANUAL_FINDING" as const,
        source: "operator" as const,
        params: Object.freeze({
          finding: text(action.finding, "manual finding"),
          verifiedAmountCents: action.verified_amount_cents === null ? null : integer(action.verified_amount_cents, "manual amount"),
          evidenceReference: text(action.evidence_reference, "manual evidence"),
          note: text(action.note, "manual note"),
          supervisorName: supervisor.cashierName,
          actorName: supervisor.cashierName,
        }),
      }); }),
    ]),
  });
}

function projectedStatus(caseState: string, attemptState: PaymentAttempt["state"], orderState: string, manualTenderBound: boolean): PaymentRecoveryCenterStatus {
  // 任何不可变人工结论都不能被后续 provider 终态覆盖；迟到 Approved 必须进入人工复核。
  if ((caseState === "manual-paid" || caseState === "manual-unpaid" || caseState === "manual-uncertain") && attemptState === "Approved") return "review-required";
  if (caseState === "manual-paid" && attemptState === "Approved" && manualTenderBound) return "review-required";
  if (caseState === "manual-paid" && (attemptState === "Declined" || attemptState === "Cancelled")) return "review-required";
  if (caseState === "manual-uncertain" && (attemptState === "Declined" || attemptState === "Cancelled")) return "payment-failed";
  if (caseState === "manual-paid" && isUnfinishedOrderState(orderState)) return "charged-order-incomplete";
  if (attemptState === "Approved") return isUnfinishedOrderState(orderState) ? "charged-order-incomplete" : "provider-recovered";
  if (caseState === "manual-paid" || caseState === "manual-unpaid" || caseState === "manual-uncertain" || caseState === "review-required" || caseState === "provider-recovered") return caseState;
  if (attemptState === "Declined" || attemptState === "Cancelled") return "payment-failed";
  return "result-unknown";
}

async function appendAudit(transaction: SqliteConnectionPort, input: { eventId: string; eventType: string; occurredAtIso: string; orderGuid: string; correlationId: string; payload: object; scope: PaymentRecoveryCenterScope }): Promise<void> {
  await transaction.run(
    `INSERT INTO audit_events (event_id, event_type, occurred_at_iso, order_guid,
      correlation_id, payload_json, uploaded_at_iso, delivery_state, attempt_count,
      next_attempt_at_iso, last_error_code, scope_store_code, scope_device_code)
     VALUES (?, ?, ?, ?, ?, ?, NULL, 'pending', 0, ?, NULL, ?, ?)`,
    [input.eventId, input.eventType, input.occurredAtIso, input.orderGuid, input.correlationId,
      JSON.stringify(input.payload), input.occurredAtIso, input.scope.storeCode, input.scope.deviceCode],
  );
}

function normalizeScope(input: PaymentRecoveryCenterScope): PaymentRecoveryCenterScope { return { storeCode: strictText(input.storeCode, "recovery store code", 64), deviceCode: strictText(input.deviceCode, "recovery device code", 128) }; }
function normalizeActor(actor: AuditActorSnapshot): AuditActorSnapshot { const normalized = auditActorSnapshotFromPayload(auditActorPayload(actor)); if (!normalized) throw new TypeError("PAYMENT_RECOVERY_ACTOR_INVALID"); return normalized; }
function sameActor(left: AuditActorSnapshot, right: AuditActorSnapshot): boolean {
  return left.cashierId === right.cashierId ||
    (left.userGuid !== null && right.userGuid !== null && left.userGuid === right.userGuid);
}
function isCardProvider(value: unknown): boolean { return value === "square" || value === "linkly-cloud"; }
function cardProvider(value: unknown): "square" | "linkly-cloud" { if (value === "square" || value === "linkly-cloud") return value; return invalid("PAYMENT_RECOVERY_PROVIDER_INVALID"); }
function isParkableAttemptState(value: unknown): boolean { return ["Created", "Submitted", "Pending", "Unknown", "Approved", "Declined", "Cancelled"].includes(String(value)); }
function isUnfinishedOrderState(value: unknown): boolean { return value === "Draft" || value === "Completing"; }
function manualFinding(value: unknown): "paid" | "unpaid" | "uncertain" { if (value === "paid" || value === "unpaid" || value === "uncertain") return value; return invalid("MANUAL_PAYMENT_FINDING_INVALID"); }
function paymentState(value: unknown): PaymentAttempt["state"] { if (["Created", "Submitted", "Pending", "Approved", "Declined", "Cancelled", "Unknown"].includes(String(value))) return value as PaymentAttempt["state"]; return invalid("PAYMENT_RECOVERY_ATTEMPT_STATE_INVALID"); }
function strictText(value: unknown, label: string, max: number): string { if (typeof value !== "string") throw new TypeError(`${label} is invalid.`); const result = value.trim(); if (!result || result.length > max || /[\u0000-\u001f\u007f]/u.test(result)) throw new TypeError(`${label} is invalid.`); return result; }
function text(value: unknown, label: string): string { return strictText(value, label, 4096); }
function nullableText(value: unknown): string | null { return value === null || value === undefined ? null : text(value, "nullable recovery text"); }
function integer(value: unknown, label: string): number { const result = Number(value); if (!Number.isSafeInteger(result)) throw new Error(`${label} is invalid.`); return result; }
function positiveInteger(value: unknown, label: string): number { const result = integer(value, label); if (result <= 0) throw new Error(`${label} is invalid.`); return result; }
function canonicalIso(value: string, label: string): string { const ms = Date.parse(value); if (!Number.isFinite(ms) || new Date(ms).toISOString() !== value) throw new TypeError(`${label} is invalid.`); return value; }
function invalid<T>(message: string): T { throw new Error(message); }
