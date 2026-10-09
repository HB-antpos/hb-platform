import { OrderSyncMaterialError } from "./order-sync-material-contract";
import type { SqliteConnectionPort } from "./types";
import { auditActorSnapshotFromPayload } from "@hb/pos-domain/core/contracts/audit-actor";
import type { CardSyncEvidenceV1 } from "@hb/pos-domain/core/contracts/payment";

/** 人工来源只能由不可变结案绑定证明，不能仅凭一条人工备注绕过支付证据。 */
export async function readManualPaymentSyncEvidence(
  db: SqliteConnectionPort,
  input: Readonly<{ tenderGuid: string; orderGuid: string; storeCode: string; deviceCode: string; amountCents: number }>,
): Promise<CardSyncEvidenceV1 | null> {
  const binding = await db.getFirst<{ attempt_id: unknown }>(
    "SELECT attempt_id FROM manual_payment_tender_bindings WHERE tender_guid = ?", [input.tenderGuid],
  );
  if (!binding) return null;
  const row = await db.getFirst<Record<string, unknown>>(
    `SELECT c.record_id, c.order_guid, c.attempt_id, c.store_code, c.device_code, c.state AS case_state,
      a.action_id, a.finding, a.verified_amount_cents, a.authorization_id,
      a.supervisor_actor_json, a.requesting_actor_json, a.reconciliation_id,
      a.attempt_state_snapshot,
      auth.record_id AS authorization_record_id, auth.order_guid AS authorization_order,
      auth.attempt_id AS authorization_attempt, auth.action_id AS authorization_action,
      auth.finding AS authorization_finding,
      auth.requesting_actor_json AS authorization_requester,
      auth.supervisor_actor_json AS authorization_supervisor,
      reconcile.record_id AS reconciliation_record_id,
      reconcile.order_guid AS reconciliation_order,
      reconcile.attempt_id AS reconciliation_attempt,
      reconcile.provider AS reconciliation_provider,
      reconcile.observed_state AS reconciliation_state,
      p.provider, p.operation, p.order_guid AS attempt_order, p.amount_cents AS attempt_amount,
      p.state AS attempt_state, t.amount_cents AS tender_amount, t.method,
      t.payment_attempt_id AS tender_attempt, t.order_guid AS tender_order
     FROM manual_payment_tender_bindings b
     JOIN payment_recovery_cases c ON c.record_id = b.record_id AND c.attempt_id = b.attempt_id
     JOIN payment_recovery_actions a ON a.action_id = b.action_id AND a.record_id = c.record_id
     JOIN payment_recovery_authorizations auth
       ON auth.authorization_id = a.authorization_id
     JOIN payment_recovery_reconciliations reconcile
       ON reconcile.reconciliation_id = a.reconciliation_id
     JOIN payment_attempts p ON p.attempt_id = b.attempt_id
     JOIN order_tenders t ON t.tender_guid = b.tender_guid
     WHERE b.tender_guid = ?`, [input.tenderGuid],
  );
  if (row && ["Approved", "Declined", "Cancelled"].includes(String(row.attempt_state)) &&
      (row.case_state === "manual-paid" || row.case_state === "review-required")) {
    throw new OrderSyncMaterialError("ORDER_SYNC_MANUAL_PROVIDER_CONFLICT");
  }
  if (!row || row.order_guid !== input.orderGuid || row.attempt_order !== input.orderGuid || row.tender_order !== input.orderGuid ||
      row.store_code !== input.storeCode || row.device_code !== input.deviceCode ||
      row.attempt_id !== binding.attempt_id || row.tender_attempt !== binding.attempt_id ||
      row.case_state !== "manual-paid" || row.finding !== "paid" || row.method !== "card" || row.operation !== "purchase" ||
      row.attempt_amount !== input.amountCents || row.tender_amount !== input.amountCents || row.verified_amount_cents !== input.amountCents ||
      input.amountCents <= 0 || !Number.isSafeInteger(input.amountCents) ||
      (row.provider !== "linkly-cloud" && row.provider !== "square") ||
      typeof row.authorization_id !== "string" || !row.authorization_id.trim() ||
      typeof row.supervisor_actor_json !== "string" ||
      row.authorization_record_id !== row.record_id || row.authorization_order !== input.orderGuid ||
      row.authorization_attempt !== row.attempt_id || row.authorization_action !== row.action_id ||
      row.authorization_finding !== "paid" ||
      row.authorization_requester !== row.requesting_actor_json ||
      row.authorization_supervisor !== row.supervisor_actor_json ||
      row.reconciliation_record_id !== row.record_id || row.reconciliation_order !== input.orderGuid ||
      row.reconciliation_attempt !== row.attempt_id || row.reconciliation_provider !== row.provider ||
      row.reconciliation_state !== row.attempt_state_snapshot)
    throw new Error("MANUAL_PAYMENT_SYNC_EVIDENCE_MISMATCH");
  // 人工结案后 provider 又写入终态，说明同一笔交易出现了双重事实。
  // 这不是可安全上传的人工卡证据；必须让 order-sync outbox 保持 retryable，交恢复中心处理。
  if (["Approved", "Declined", "Cancelled"].includes(String(row.attempt_state))) {
    throw new OrderSyncMaterialError("ORDER_SYNC_MANUAL_PROVIDER_CONFLICT");
  }
  const supervisor = auditActorSnapshotFromPayload(JSON.parse(row.supervisor_actor_json));
  const requester = auditActorSnapshotFromPayload(JSON.parse(String(row.requesting_actor_json)));
  if (!supervisor || !requester || requester.cashierId === supervisor.cashierId ||
      (requester.userGuid !== null && supervisor.userGuid !== null && requester.userGuid === supervisor.userGuid)) {
    throw new Error("MANUAL_PAYMENT_SYNC_ACTOR_MISSING");
  }
  // MANUAL 明确表示主管结论；不构造支付方批准码、授权码、原 RFN 或卡号。
  return {
    version: 1, provider: row.provider, operation: "purchase",
    processor: row.provider === "square" ? "Square" : "ANZ",
    txnRef: null, authCode: null, cardType: null, cardBin: null, maskedCardNumber: null, merchantId: null,
    responseCode: "MANUAL", responseText: "Supervisor confirmed payment; not provider approval",
    stan: null, bankDateTimeIso: null, amountCents: input.amountCents, refundReference: null,
  };
}
