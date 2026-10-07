import type { SensitivePayloadEncryptor } from "./sqlite-repositories";
import type { SqliteConnectionPort } from "@hb/pos-db/core/db/types";

import {
  auditActorPayload,
  auditActorSnapshotFromPayload,
} from "@hb/pos-domain/core/contracts/audit-actor";
import {
  normalizeLineSyncProvenance,
  type LineSyncProvenance,
} from "@hb/pos-domain/core/contracts/line-sync-provenance";
import type {
  CompleteDurableReturnAction,
  DurableExternalAttemptKind,
  DurableReturnAction,
  DurableReturnRecoveryAction,
  DurableReturnActionStatus,
  DurableReturnAllocation,
  DurableReturnAllocationStatus,
  DurableReturnLine,
  PrepareDurableReturnAction,
  ReturnExecutionLedgerPort,
  ReturnRecoveryListPort,
  ReturnRecoveryScope,
  ReturnRecordDraft,
  TrustedReturnIdentity,
} from "@hb/pos-domain/features/returns/adapters/durable-return-execution-orchestrator";
import type {
  OfflineCashCapacityProof,
  ReturnRefundAllocation,
  ReturnRefundLine,
  ReturnRefundPlan,
  ReturnSourceKind,
  ReturnTenderMethod,
} from "@hb/pos-domain/features/returns/return-domain";

export type ReturnExecutionPersistenceIds = Readonly<{
  createTenderGuid(): string;
  createAuditEventId(): string;
}>;

type ReturnActionRow = Readonly<{
  action_id: unknown;
  request_fingerprint: unknown;
  return_order_guid: unknown;
  action_recovery_token: unknown;
  source_kind: unknown;
  total_refund_cents: unknown;
  online: unknown;
  store_code: unknown;
  device_code: unknown;
  cashier_id: unknown;
  cashier_name: unknown;
  session_epoch: unknown;
  supervisor_grant_id: unknown;
  plan_json: unknown;
  state: unknown;
  created_at_iso: unknown;
  completed_at_iso: unknown;
}>;

type ReturnLineRow = Readonly<{
  line_id: unknown;
  line_index: unknown;
  selection_key: unknown;
  source_kind: unknown;
  return_source_key: unknown;
  original_order_guid: unknown;
  original_order_detail_guid: unknown;
  product_code: unknown;
  item_number: unknown;
  lookup_code: unknown;
  display_name: unknown;
  quantity: unknown;
  unit_refund_cents: unknown;
  signed_amount_cents: unknown;
  available_quantity: unknown;
  remaining_amount_cents: unknown;
  reference_code: unknown;
  sync_price_source: unknown;
}>;

type ReturnAllocationRow = Readonly<{
  allocation_id: unknown;
  allocation_index: unknown;
  execution_kind: unknown;
  method: unknown;
  signed_amount_cents: unknown;
  capacity_id: unknown;
  original_order_guid: unknown;
  offline_evidence_id: unknown;
  offline_evidence_remaining_cents: unknown;
  external_attempt_id: unknown;
  external_attempt_kind: unknown;
  external_action_id: unknown;
  durable_attempt_id: unknown;
  status: unknown;
  protected_recovery_ciphertext: unknown;
  capacity_reservation_state: unknown;
}>;

type ReturnRecoveryActionRow = Readonly<{
  action_id: unknown;
  return_order_guid: unknown;
  source_kind: unknown;
  total_refund_cents: unknown;
  state: unknown;
}>;

type ReturnRecoveryLineRow = Readonly<{
  line_index: unknown;
  source_kind: unknown;
  item_number: unknown;
  display_name: unknown;
  quantity: unknown;
  unit_refund_cents: unknown;
  signed_amount_cents: unknown;
  reference_code: unknown;
  sync_price_source: unknown;
}>;

type AllocationBindingRow = ReturnAllocationRow &
  Readonly<{
    action_id: unknown;
    return_order_guid: unknown;
    action_state: unknown;
  }>;

/**
 * DurableReturnExecutionOrchestrator 的 SQLCipher 实现。
 *
 * prepare 阶段冻结完整 line/identity/容量并取得 reservation；Unknown 保留所有
 * reservation。只有所有 allocation 都明确 completed 后，才以同一事务写本地退货
 * 订单、tender、审计、outbox、履约计划和 return capacity CAS。
 */
export class SqliteReturnExecutionLedger
implements ReturnExecutionLedgerPort, ReturnRecoveryListPort {
  public constructor(
    private readonly connection: SqliteConnectionPort,
    private readonly encryptor: SensitivePayloadEncryptor,
    private readonly ids: ReturnExecutionPersistenceIds,
    private readonly nowIso: () => string,
  ) {}

  public prepareOrLoad(
    input: PrepareDurableReturnAction,
  ): Promise<DurableReturnAction> {
    const draft = normalizePrepare(input);
    return this.connection.withExclusiveTransaction(async (transaction) => {
      const existing = await loadAction(
        transaction,
        this.encryptor,
        draft.actionId,
      );
      if (existing) {
        assertPrepareReplay(existing, draft);
        return existing;
      }
      const existingOrder = await transaction.getFirst<{ value: unknown }>(
        `SELECT order_guid AS value
         FROM local_orders
         WHERE order_guid = ?`,
        [draft.returnOrderGuid],
      );
      if (existingOrder) {
        throw new Error("Return OrderGuid is already owned by another order.");
      }
      await transaction.run(
        `INSERT INTO return_actions (
          action_id, request_fingerprint, return_order_guid,
          action_recovery_token, source_kind, total_refund_cents,
          online, store_code, device_code, cashier_id, cashier_name,
          session_epoch, supervisor_grant_id, plan_json, state,
          created_at_iso, completed_at_iso, updated_at_iso
        ) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?,
          'processing', ?, NULL, ?)`,
        [
          draft.actionId,
          draft.requestFingerprint,
          draft.returnOrderGuid,
          draft.actionRecoveryToken,
          draft.plan.sourceKind,
          draft.plan.totalRefundCents,
          draft.plan.online ? 1 : 0,
          draft.identity.storeCode,
          draft.identity.deviceCode,
          draft.identity.cashierId,
          draft.identity.cashierName,
          draft.identity.sessionEpoch,
          draft.supervisorGrantKey,
          serializePersistedPlan(draft.plan, draft.identity),
          draft.createdAtIso,
          draft.createdAtIso,
        ],
      );
      if (draft.supervisorGrantKey !== null) {
        await transaction.run(
          `INSERT INTO return_supervisor_grant_consumptions (
            supervisor_grant_id, action_id, consumed_at_iso
          ) VALUES (?, ?, ?)`,
          [
            draft.supervisorGrantKey,
            draft.actionId,
            draft.createdAtIso,
          ],
        );
      }

      for (const [index, line] of draft.lines.entries()) {
        await insertReturnLine(transaction, draft.actionId, index, line);
        if (line.sourceKind === "receipt") {
          await reserveLineCapacity(
            transaction,
            draft.actionId,
            line,
            draft.createdAtIso,
          );
        }
      }
      for (const allocation of draft.allocations) {
        await reserveAndInsertAllocation(
          transaction,
          draft,
          allocation,
        );
      }
      // provider payment_attempts 以 local_orders 为 FK；必须在任何外部调用前
      // 原子落同一 ReturnOrderGuid 的 Draft/完整行，最终完成只做状态 CAS。
      await insertPreparedReturnOrderDraft(transaction, draft);
      const inserted = await loadAction(
        transaction,
        this.encryptor,
        draft.actionId,
      );
      if (!inserted) throw new Error("Durable return action commit is missing.");
      return inserted;
    });
  }

  public load(actionIdInput: string): Promise<DurableReturnAction | null> {
    const actionId = strictText(actionIdInput, "return action id", 128);
    return this.connection.withExclusiveTransaction((transaction) =>
      loadAction(transaction, this.encryptor, actionId),
    );
  }

  public listRecoverable(
    input: ReturnRecoveryScope,
  ): Promise<readonly DurableReturnRecoveryAction[]> {
    const scope = normalizeRecoveryScope(input);
    return this.connection.withExclusiveTransaction(async (transaction) => {
      const rows = await transaction.getAll<ReturnRecoveryActionRow>(
        `SELECT action_id, return_order_guid, source_kind,
          total_refund_cents, state
         FROM return_actions
         WHERE store_code = ? AND device_code = ? AND cashier_id = ?
           AND state IN ('processing', 'unknown')
         ORDER BY created_at_iso, action_id
         LIMIT 2`,
        [scope.storeCode, scope.deviceCode, scope.cashierId],
      );
      if (rows.length > 1) {
        throw new Error(
          "Return recovery scope contains multiple active actions.",
        );
      }
      const candidates: DurableReturnRecoveryAction[] = [];
      for (const row of rows) {
        const actionId = strictText(
          row.action_id,
          "recoverable return action id",
          128,
        );
        const status = actionStatus(row.state);
        if (status !== "processing" && status !== "unknown") {
          throw new Error("Recoverable return action state is invalid.");
        }
        const sourceKind =
          row.source_kind === "receipt" ||
          row.source_kind === "no-receipt"
            ? row.source_kind
            : invalid<DurableReturnRecoveryAction["sourceKind"]>(
                "Recoverable return source kind is invalid.",
              );
        const lineRows = await transaction.getAll<ReturnRecoveryLineRow>(
          `SELECT line.line_index, line.source_kind, line.item_number,
            line.display_name, line.quantity, line.unit_refund_cents,
            line.signed_amount_cents, order_line.reference_code,
            order_line.sync_price_source
           FROM return_action_lines AS line
           INNER JOIN return_actions AS action
             ON action.action_id = line.action_id
           INNER JOIN local_order_lines AS order_line
             ON order_line.order_guid = action.return_order_guid
            AND order_line.line_id = line.line_id
           WHERE line.action_id = ?
           ORDER BY line.line_index`,
          [actionId],
        );
        if (!lineRows.length) {
          throw new Error("Recoverable return action has no lines.");
        }
        let totalRefundCents = 0;
        const lines = lineRows.map((line, index) => {
          if (
            nonNegativeInteger(
              line.line_index,
              "recoverable return line index",
            ) !== index
          ) {
            throw new Error(
              "Recoverable return line indexes are not contiguous.",
            );
          }
          const lineSourceKind = returnSourceKind(line.source_kind);
          if (
            (sourceKind === "receipt") !==
            (lineSourceKind === "receipt")
          ) {
            throw new Error(
              "Recoverable return line source is inconsistent.",
            );
          }
          const signedAmountCents = negativeInteger(
            line.signed_amount_cents,
            "recoverable return line amount",
          );
          totalRefundCents = safeAdd(
            totalRefundCents,
            -signedAmountCents,
          );
          return Object.freeze({
            sourceKind: lineSourceKind,
            itemNumber: nullableText(line.item_number),
            displayName: strictText(
              line.display_name,
              "recoverable return display name",
              512,
            ),
            quantity: positiveInteger(
              line.quantity,
              "recoverable return line quantity",
            ),
            unitRefundCents: positiveInteger(
              line.unit_refund_cents,
              "recoverable return unit refund",
            ),
            signedAmountCents,
            syncProvenance:
              persistedReturnLineSyncProvenance(line),
          });
        });
        const persistedTotal = positiveInteger(
          row.total_refund_cents,
          "recoverable return total",
        );
        if (totalRefundCents !== persistedTotal) {
          throw new Error(
            "Recoverable return line total is inconsistent.",
          );
        }
        candidates.push(
          Object.freeze({
            actionId,
            returnOrderGuid: strictText(
              row.return_order_guid,
              "recoverable return order guid",
              128,
            ),
            sourceKind,
            totalRefundCents: persistedTotal,
            status,
            lines: Object.freeze(lines),
          }),
        );
      }
      return Object.freeze(candidates);
    });
  }

  public async markAllocationSubmitted(input: Readonly<{
    actionId: string;
    allocationId: string;
  }>): Promise<boolean> {
    const actionId = strictText(input.actionId, "return action id", 128);
    const allocationId = strictText(
      input.allocationId,
      "return allocation id",
      128,
    );
    const updatedAtIso = canonicalIso(
      this.nowIso(),
      "return allocation submitted time",
    );
    const changed = await this.connection.run(
      `UPDATE return_action_allocations
       SET status = 'submitted', updated_at_iso = ?
       WHERE action_id = ? AND allocation_id = ? AND status = 'created'
         AND EXISTS (
           SELECT 1 FROM return_actions action
           WHERE action.action_id = return_action_allocations.action_id
             AND action.state = 'processing'
         )`,
      [updatedAtIso, actionId, allocationId],
    );
    return changed.changes === 1;
  }

  public bindAllocationAttempt(input: Readonly<{
    actionId: string;
    allocationId: string;
    attemptKind: DurableExternalAttemptKind;
    externalActionId: string;
    durableAttemptId: string;
  }>): Promise<boolean> {
    const normalized = {
      actionId: strictText(input.actionId, "return action id", 128),
      allocationId: strictText(
        input.allocationId,
        "return allocation id",
        128,
      ),
      attemptKind: externalAttemptKind(input.attemptKind),
      externalActionId: strictText(
        input.externalActionId,
        "return external action id",
        128,
      ),
      durableAttemptId: strictText(
        input.durableAttemptId,
        "return durable attempt id",
        128,
      ),
    };
    return this.connection.withExclusiveTransaction(async (transaction) => {
      const allocation = await requireAllocationBindingRow(
        transaction,
        normalized.actionId,
        normalized.allocationId,
      );
      assertAttemptKindForMethod(
        returnTenderMethod(allocation.method),
        normalized.attemptKind,
      );
      const existingKind = nullableText(allocation.external_attempt_kind);
      const existingAction = nullableText(allocation.external_action_id);
      const existingAttempt = nullableText(allocation.durable_attempt_id);
      if (
        existingKind !== null ||
        existingAction !== null ||
        existingAttempt !== null
      ) {
        if (
          existingKind === normalized.attemptKind &&
          existingAction === normalized.externalActionId &&
          existingAttempt === normalized.durableAttemptId
        ) {
          return true;
        }
        throw new Error(
          "Return allocation is already bound to another durable attempt.",
        );
      }
      if (
        text(allocation.execution_kind, "allocation execution kind") !==
          "online-refund" ||
        (text(allocation.status, "allocation status") !== "submitted" &&
          text(allocation.status, "allocation status") !== "unknown") ||
        (text(allocation.action_state, "return action state") !==
          "processing" &&
          text(allocation.action_state, "return action state") !== "unknown")
      ) {
        return false;
      }

      if (normalized.attemptKind === "payment-provider") {
        await assertPaymentProviderAttemptBinding(
          transaction,
          allocation,
          normalized.externalActionId,
          normalized.durableAttemptId,
        );
      } else {
        await assertApiAttemptBinding(
          transaction,
          allocation,
          normalized.externalActionId,
          normalized.durableAttemptId,
        );
      }
      const changed = await transaction.run(
        `UPDATE return_action_allocations
         SET external_attempt_kind = ?, external_action_id = ?,
           durable_attempt_id = ?, updated_at_iso = ?
         WHERE action_id = ? AND allocation_id = ?
           AND external_attempt_kind IS NULL
           AND external_action_id IS NULL
           AND durable_attempt_id IS NULL
           AND status IN ('submitted', 'unknown')`,
        [
          normalized.attemptKind,
          normalized.externalActionId,
          normalized.durableAttemptId,
          canonicalIso(this.nowIso(), "return attempt bound time"),
          normalized.actionId,
          normalized.allocationId,
        ],
      );
      return changed.changes === 1;
    });
  }

  public async recordAllocationOutcome(input: Readonly<{
    actionId: string;
    allocationId: string;
    expectedStatuses: readonly Extract<
      DurableReturnAllocationStatus,
      "submitted" | "unknown"
    >[];
    status: Extract<
      DurableReturnAllocationStatus,
      "completed" | "declined" | "unknown"
    >;
    protectedRecoveryKey: string | null;
  }>): Promise<boolean> {
    const actionId = strictText(input.actionId, "return action id", 128);
    const allocationId = strictText(
      input.allocationId,
      "return allocation id",
      128,
    );
    const expectedStatuses = [...new Set(input.expectedStatuses)].map(
      expectedOutcomeStatus,
    );
    if (!expectedStatuses.length) {
      throw new TypeError("Return allocation expected status is required.");
    }
    const next = allocationOutcomeStatus(input.status);
    const recoveryKey =
      input.protectedRecoveryKey === null
        ? null
        : strictText(
            input.protectedRecoveryKey,
            "return protected recovery key",
            4096,
          );
    const recoveryCiphertext =
      recoveryKey === null
        ? null
        : await this.encryptor.encrypt(
            JSON.stringify({ version: 1, value: recoveryKey }),
          );
    const outcomeAtIso = canonicalIso(
      this.nowIso(),
      "return allocation outcome time",
    );
    return this.connection.withExclusiveTransaction(async (transaction) => {
      const allocation = await requireAllocationBindingRow(
        transaction,
        actionId,
        allocationId,
      );
      const current = allocationStatus(allocation.status);
      const existingRecoveryKey = await decryptRecoveryKey(
        allocation.protected_recovery_ciphertext,
        this.encryptor,
      );
      if (existingRecoveryKey !== null && recoveryKey !== null) {
        if (existingRecoveryKey !== recoveryKey) {
          throw new Error(
            "Return allocation recovery key is already immutable.",
          );
        }
      }
      if (current === next) {
        if (
          next !== "unknown" ||
          recoveryKey === null ||
          existingRecoveryKey === recoveryKey
        ) {
          if (
            next === "completed" &&
            isPaymentProviderAllocation(allocation)
          ) {
            await ensureApprovedProviderTender(
              transaction,
              allocation,
              this.ids,
              outcomeAtIso,
              true,
            );
          }
          return true;
        }
      }
      if (!expectedStatuses.includes(current as "submitted" | "unknown")) {
        return false;
      }
      await assertExternalOutcomeState(transaction, allocation, next);
      const effectiveRecoveryCiphertext =
        existingRecoveryKey !== null || recoveryKey === null
          ? optionalCiphertext(allocation.protected_recovery_ciphertext)
          : recoveryCiphertext;
      const changed = await transaction.run(
        `UPDATE return_action_allocations
         SET status = ?, protected_recovery_ciphertext = ?,
           updated_at_iso = ?
         WHERE action_id = ? AND allocation_id = ?
           AND status IN (${expectedStatuses.map(() => "?").join(", ")})`,
        [
          next,
          effectiveRecoveryCiphertext,
          outcomeAtIso,
          actionId,
          allocationId,
          ...expectedStatuses,
        ],
      );
      if (changed.changes !== 1) return false;
      if (
        next === "completed" &&
        isPaymentProviderAllocation(allocation)
      ) {
        // provider 已批准即形成不可丢失的退款 tender 事实，使下一笔 allocation
        // 不会被通用 Approved blocker 卡住；状态与 tender 必须同事务提交。
        await ensureApprovedProviderTender(
          transaction,
          allocation,
          this.ids,
          outcomeAtIso,
          true,
        );
      }
      if (
        next === "declined" &&
        text(
          allocation.capacity_reservation_state,
          "capacity reservation state",
        ) === "Reserved"
      ) {
        await releaseAllocationCapacity(
          transaction,
          actionId,
          allocationId,
        );
      }
      return true;
    });
  }

  public markActionUnknown(input: Readonly<{ actionId: string }>): Promise<void> {
    const actionId = strictText(input.actionId, "return action id", 128);
    return this.connection.withExclusiveTransaction(async (transaction) => {
      const row = await transaction.getFirst<{ state: unknown }>(
        "SELECT state FROM return_actions WHERE action_id = ?",
        [actionId],
      );
      if (!row) throw new Error("Return action is missing.");
      const state = actionStatus(row.state);
      if (state === "unknown") return;
      if (state !== "processing") {
        throw new Error("Terminal return action cannot become Unknown.");
      }
      const changed = await transaction.run(
        `UPDATE return_actions
         SET state = 'unknown', updated_at_iso = ?
         WHERE action_id = ? AND state = 'processing'`,
        [canonicalIso(this.nowIso(), "return unknown time"), actionId],
      );
      if (changed.changes !== 1) {
        throw new Error("Return action Unknown CAS failed.");
      }
    });
  }

  public async resumeUnknownAction(input: Readonly<{
    actionId: string;
  }>): Promise<boolean> {
    const actionId = strictText(input.actionId, "return action id", 128);
    const changed = await this.connection.run(
      `UPDATE return_actions
       SET state = 'processing', updated_at_iso = ?
       WHERE action_id = ? AND state = 'unknown'`,
      [canonicalIso(this.nowIso(), "return resume time"), actionId],
    );
    return changed.changes === 1;
  }

  public async markActionDeclined(input: Readonly<{
    actionId: string;
  }>): Promise<void> {
    const actionId = strictText(input.actionId, "return action id", 128);
    const partialCompletion =
      await this.connection.withExclusiveTransaction(async (transaction) => {
      const counts = await transaction.getFirst<{
        declined_count: unknown;
        completed_count: unknown;
      }>(
        `SELECT
          SUM(CASE WHEN status = 'declined' THEN 1 ELSE 0 END)
            AS declined_count,
          SUM(CASE WHEN status = 'completed' THEN 1 ELSE 0 END)
            AS completed_count
         FROM return_action_allocations
         WHERE action_id = ?`,
        [actionId],
      );
      if (
        integer(counts?.declined_count ?? 0, "declined allocation count") < 1
      ) {
        throw new Error("Return action has no declined allocation.");
      }
      if (
        integer(counts?.completed_count ?? 0, "completed allocation count") > 0
      ) {
        const changed = await transaction.run(
          `UPDATE return_actions
           SET state = 'unknown', updated_at_iso = ?
           WHERE action_id = ? AND state IN ('processing', 'unknown')`,
          [canonicalIso(this.nowIso(), "partial return unknown time"), actionId],
        );
        if (changed.changes !== 1) {
          throw new Error("Partial return Unknown CAS failed.");
        }
        return true;
      }
      const changed = await transaction.run(
        `UPDATE return_actions
         SET state = 'declined', updated_at_iso = ?
         WHERE action_id = ? AND state IN ('processing', 'unknown')`,
        [canonicalIso(this.nowIso(), "return declined time"), actionId],
      );
      if (changed.changes !== 1) {
        const terminal = await transaction.getFirst<{ state: unknown }>(
          "SELECT state FROM return_actions WHERE action_id = ?",
          [actionId],
        );
        if (terminal && actionStatus(terminal.state) === "declined") {
          return false;
        }
        throw new Error("Return action declined CAS failed.");
      }
      await transaction.run(
        `UPDATE return_action_allocations
         SET capacity_reservation_state = 'Released', updated_at_iso = ?
         WHERE action_id = ? AND capacity_reservation_state = 'Reserved'`,
        [canonicalIso(this.nowIso(), "return capacity release time"), actionId],
      );
      await transaction.run(
        `UPDATE return_line_capacity_reservations
         SET state = 'Released', updated_at_iso = ?
         WHERE action_id = ? AND state = 'Reserved'`,
        [canonicalIso(this.nowIso(), "return line release time"), actionId],
      );
      return false;
    });
    if (partialCompletion) {
      // 事务已提交 Unknown，随后抛错阻止 orchestrator 把部分退款伪装成 Declined。
      throw new Error(
        "Partially completed return cannot be marked declined.",
      );
    }
  }

  public completeAtomically(
    input: CompleteDurableReturnAction,
  ): Promise<DurableReturnAction> {
    const completion = normalizeCompletion(input);
    return this.connection.withExclusiveTransaction(async (transaction) => {
      const action = await loadAction(
        transaction,
        this.encryptor,
        completion.actionId,
      );
      if (!action) throw new Error("Return action is missing.");
      assertCompletionMatchesAction(completion, action);
      if (action.status === "completed") {
        await assertCompletedReplayFacts(transaction, action, completion);
        return action;
      }
      if (action.status !== "processing") {
        throw new Error("Return action is not ready for final completion.");
      }
      if (
        action.allocations.some(
          (allocation) => allocation.status !== "completed",
        )
      ) {
        throw new Error("All return allocations must be completed first.");
      }
      await assertCompletedAttemptBindings(transaction, action);
      await commitLineCapacities(transaction, action, completion.completedAtIso);
      await commitTenderCapacities(
        transaction,
        action,
        completion.completedAtIso,
      );
      await insertCompletedReturnOrder(
        transaction,
        action,
        completion,
        this.ids,
      );
      const changed = await transaction.run(
        `UPDATE return_actions
         SET state = 'completed', completed_at_iso = ?, updated_at_iso = ?
         WHERE action_id = ? AND return_order_guid = ?
           AND state = 'processing' AND completed_at_iso IS NULL`,
        [
          completion.completedAtIso,
          completion.completedAtIso,
          action.actionId,
          action.returnOrderGuid,
        ],
      );
      if (changed.changes !== 1) {
        throw new Error("Return action completion CAS failed.");
      }
      const completed = await loadAction(
        transaction,
        this.encryptor,
        action.actionId,
      );
      if (!completed || completed.status !== "completed") {
        throw new Error("Completed return action could not be observed.");
      }
      return completed;
    });
  }
}

function normalizePrepare(
  input: PrepareDurableReturnAction,
): PrepareDurableReturnAction {
  const identity = normalizeIdentity(input.identity);
  const plan = normalizePlan(input.plan);
  const actionId = strictText(input.actionId, "return action id", 128);
  const lines = input.lines.map(normalizeLine);
  const allocations = input.allocations.map(normalizeInitialAllocation);
  assertLinePlanIdentity(lines, plan.lines);
  assertAllocationPlanIdentity(allocations, plan.allocations);
  if (
    lines.length !== plan.lines.length ||
    allocations.length !== plan.allocations.length
  ) {
    throw new TypeError("Return durable material count is invalid.");
  }
  for (const [index, allocation] of allocations.entries()) {
    if (allocation.index !== index) {
      throw new TypeError("Return allocation indexes must be contiguous.");
    }
    if (allocation.method === "installment") {
      // 当前 LocalOrder TenderMethod 尚未冻结 installment；必须在外部调用前失败。
      throw new TypeError(
        "Installment return persistence is not enabled in M13.",
      );
    }
  }
  const supervisorGrantKey =
    input.supervisorGrantKey === null
      ? null
      : strictText(
          input.supervisorGrantKey,
          "return supervisor grant id",
          256,
        );
  if (
    (plan.sourceKind === "receipt" && supervisorGrantKey !== null) ||
    (plan.sourceKind === "no-receipt" && supervisorGrantKey === null)
  ) {
    throw new TypeError("Return supervisor grant identity is invalid.");
  }
  return Object.freeze({
    actionId,
    requestFingerprint: strictText(
      input.requestFingerprint,
      "return request fingerprint",
      1_048_576,
    ),
    returnOrderGuid: strictText(
      input.returnOrderGuid,
      "return order guid",
      128,
    ),
    actionRecoveryToken: strictText(
      input.actionRecoveryToken,
      "return recovery token",
      128,
    ),
    identity,
    plan,
    supervisorGrantKey,
    createdAtIso: canonicalIso(
      input.createdAtIso,
      "return action created time",
    ),
    lines: Object.freeze(lines),
    allocations: Object.freeze(allocations),
  });
}

function normalizePlan(input: ReturnRefundPlan): ReturnRefundPlan {
  if (!input || typeof input !== "object") {
    throw new TypeError("Return refund plan is required.");
  }
  const sourceKind =
    input.sourceKind === "receipt" || input.sourceKind === "no-receipt"
      ? input.sourceKind
      : invalid<ReturnRefundPlan["sourceKind"]>(
          "Return source kind is invalid.",
        );
  const totalRefundCents = positiveInteger(
    input.totalRefundCents,
    "return total refund",
  );
  if (typeof input.online !== "boolean") {
    throw new TypeError("Return online state is invalid.");
  }
  if (!Array.isArray(input.lines) || !input.lines.length) {
    throw new TypeError("Return plan lines are required.");
  }
  if (!Array.isArray(input.allocations) || !input.allocations.length) {
    throw new TypeError("Return plan allocations are required.");
  }
  const lines = input.lines.map(normalizePlanLine);
  const allocations = input.allocations.map(normalizePlanAllocation);
  const lineTotal = lines.reduce(
    (sum, line) => safeAdd(sum, -line.signedAmountCents),
    0,
  );
  const allocationTotal = allocations.reduce(
    (sum, allocation) => safeAdd(sum, -allocation.signedAmountCents),
    0,
  );
  if (lineTotal !== totalRefundCents || allocationTotal !== totalRefundCents) {
    throw new TypeError("Return plan monetary totals are inconsistent.");
  }
  const originalOrders = new Set(
    lines
      .map((line) => line.originalOrderGuid)
      .filter((value): value is string => value !== null),
  );
  if (
    (sourceKind === "receipt" &&
      (originalOrders.size !== 1 ||
        lines.some(
          (line) =>
            line.sourceKind !== "receipt" ||
            line.originalOrderGuid === null ||
            line.originalOrderDetailGuid === null,
        ) ||
        allocations.some(
          (allocation) =>
            allocation.originalCapacityId === null ||
            allocation.originalOrderGuid !== [...originalOrders][0],
        ))) ||
    (sourceKind === "no-receipt" &&
      (originalOrders.size !== 0 ||
        lines.some((line) => line.sourceKind === "receipt") ||
        allocations.some(
          (allocation) =>
            allocation.originalCapacityId !== null ||
            allocation.originalOrderGuid !== null ||
            allocation.offlineCashProof !== null,
        )))
  ) {
    throw new TypeError("Return plan original identity is inconsistent.");
  }
  if (
    !input.online &&
    allocations.some(
      (allocation) =>
        allocation.method !== "cash" ||
        allocation.offlineCashProof === null,
    )
  ) {
    throw new TypeError("Offline return plan may only use proven cash.");
  }
  return Object.freeze({
    sourceKind,
    totalRefundCents,
    lines: Object.freeze(lines),
    allocations: Object.freeze(allocations),
    online: input.online,
  });
}

function normalizePlanLine(input: ReturnRefundLine): ReturnRefundLine {
  const sourceKind = returnSourceKind(input.sourceKind);
  const quantity = positiveInteger(input.quantity, "return line quantity");
  const signedAmountCents = negativeInteger(
    input.signedAmountCents,
    "return line signed amount",
  );
  const originalOrderGuid = optionalText(
    input.originalOrderGuid,
    "return line original order",
    128,
  );
  const originalOrderDetailGuid = optionalText(
    input.originalOrderDetailGuid,
    "return line original detail",
    128,
  );
  if (
    (sourceKind === "receipt" &&
      (originalOrderGuid === null || originalOrderDetailGuid === null)) ||
    (sourceKind !== "receipt" &&
      (originalOrderGuid !== null || originalOrderDetailGuid !== null))
  ) {
    throw new TypeError("Return plan line source identity is invalid.");
  }
  return Object.freeze({
    sourceKind,
    returnSourceKey: strictText(
      input.returnSourceKey,
      "return source key",
      512,
    ),
    originalOrderGuid,
    originalOrderDetailGuid,
    productCode: strictText(
      input.productCode,
      "return product code",
      256,
    ),
    quantity,
    signedAmountCents,
    syncProvenance: normalizeReturnLineSyncProvenance(
      input.syncProvenance,
    ),
  });
}

function normalizePlanAllocation(
  input: ReturnRefundAllocation,
): ReturnRefundAllocation {
  const method = returnTenderMethod(input.method);
  const signedAmountCents = negativeInteger(
    input.signedAmountCents,
    "return allocation signed amount",
  );
  const originalCapacityId = optionalText(
    input.originalCapacityId,
    "return original capacity id",
    128,
  );
  const originalOrderGuid = optionalText(
    input.originalOrderGuid,
    "return allocation original order",
    128,
  );
  const proof =
    input.offlineCashProof === null
      ? null
      : normalizeOfflineProof(input.offlineCashProof);
  if (
    (originalCapacityId === null) !== (originalOrderGuid === null) ||
    (proof !== null &&
      (method !== "cash" ||
        proof.capacityId !== originalCapacityId ||
        proof.originalOrderGuid !== originalOrderGuid ||
        proof.remainingCents < -signedAmountCents))
  ) {
    throw new TypeError("Return allocation capacity identity is invalid.");
  }
  return Object.freeze({
    method,
    signedAmountCents,
    originalCapacityId,
    originalOrderGuid,
    offlineCashProof: proof,
  });
}

function normalizeLine(input: DurableReturnLine): DurableReturnLine {
  const sourceKind = returnSourceKind(input.sourceKind);
  const quantity = positiveInteger(input.quantity, "durable return quantity");
  const unitRefundCents = positiveInteger(
    input.unitRefundCents,
    "durable return unit refund",
  );
  const signedAmountCents = negativeInteger(
    input.signedAmountCents,
    "durable return signed amount",
  );
  const originalOrderGuid = optionalText(
    input.originalOrderGuid,
    "durable return original order",
    128,
  );
  const originalOrderDetailGuid = optionalText(
    input.originalOrderDetailGuid,
    "durable return original detail",
    128,
  );
  const availableQuantity =
    input.availableQuantity === null
      ? null
      : positiveInteger(
          input.availableQuantity,
          "durable return available quantity",
        );
  const remainingAmountCents =
    input.remainingAmountCents === null
      ? null
      : nonNegativeInteger(
          input.remainingAmountCents,
          "durable return remaining amount",
        );
  if (
    (sourceKind === "receipt" &&
      (originalOrderGuid === null ||
        originalOrderDetailGuid === null ||
        availableQuantity === null ||
        availableQuantity < quantity ||
        remainingAmountCents === null ||
        remainingAmountCents < -signedAmountCents)) ||
    (sourceKind !== "receipt" &&
      (originalOrderGuid !== null ||
        originalOrderDetailGuid !== null ||
        availableQuantity !== null ||
        remainingAmountCents !== null))
  ) {
    throw new TypeError("Durable return line capacity is invalid.");
  }
  const isReceiptTailAmount =
    sourceKind === "receipt" &&
    quantity === availableQuantity &&
    remainingAmountCents === -signedAmountCents;
  if (
    safeMultiply(quantity, unitRefundCents) !== -signedAmountCents &&
    !isReceiptTailAmount
  ) {
    // 原单金额不能整除数量时，最后一次全量退货必须按服务端剩余金额收尾。
    throw new TypeError("Durable return line amount is inconsistent.");
  }
  return Object.freeze({
    lineId: strictText(input.lineId, "durable return line id", 128),
    selectionKey: strictText(
      input.selectionKey,
      "durable return selection key",
      128,
    ),
    sourceKind,
    returnSourceKey: strictText(
      input.returnSourceKey,
      "durable return source key",
      512,
    ),
    originalOrderGuid,
    originalOrderDetailGuid,
    productCode: strictText(
      input.productCode,
      "durable return product code",
      256,
    ),
    itemNumber: optionalText(
      input.itemNumber,
      "durable return item number",
      256,
    ),
    lookupCode: strictText(
      input.lookupCode,
      "durable return lookup code",
      256,
    ),
    displayName: strictText(
      input.displayName,
      "durable return display name",
      1024,
    ),
    quantity,
    unitRefundCents,
    signedAmountCents,
    availableQuantity,
    remainingAmountCents,
    syncProvenance: normalizeReturnLineSyncProvenance(
      input.syncProvenance,
    ),
  });
}

function normalizeInitialAllocation(
  input: DurableReturnAllocation,
): DurableReturnAllocation {
  const method = returnTenderMethod(input.method);
  const executionKind =
    input.executionKind === "offline-cash" ||
    input.executionKind === "online-refund"
      ? input.executionKind
      : invalid<DurableReturnAllocation["executionKind"]>(
          "Return allocation execution kind is invalid.",
        );
  const signedAmountCents = negativeInteger(
    input.signedAmountCents,
    "durable allocation signed amount",
  );
  const capacityId = optionalText(
    input.capacityId,
    "durable allocation capacity",
    128,
  );
  const originalOrderGuid = optionalText(
    input.originalOrderGuid,
    "durable allocation original order",
    128,
  );
  const proof =
    input.offlineCashProof === null
      ? null
      : normalizeOfflineProof(input.offlineCashProof);
  const externalAttemptId = optionalText(
    input.externalAttemptId,
    "return external attempt id",
    128,
  );
  if (
    input.status !== "created" ||
    input.protectedRecoveryKey !== null ||
    input.externalAttemptKind !== null ||
    input.externalActionId !== null ||
    input.durableAttemptId !== null ||
    (capacityId === null) !== (originalOrderGuid === null) ||
    (executionKind === "offline-cash" &&
      (method !== "cash" ||
        proof === null ||
        externalAttemptId !== null ||
        proof.capacityId !== capacityId ||
        proof.originalOrderGuid !== originalOrderGuid)) ||
    (executionKind === "online-refund" &&
      (proof !== null || externalAttemptId === null))
  ) {
    throw new TypeError("Initial durable return allocation is invalid.");
  }
  return Object.freeze({
    allocationId: strictText(
      input.allocationId,
      "return allocation id",
      128,
    ),
    index: nonNegativeInteger(input.index, "return allocation index"),
    executionKind,
    method,
    signedAmountCents,
    capacityId,
    originalOrderGuid,
    offlineCashProof: proof,
    externalAttemptId,
    externalAttemptKind: null,
    externalActionId: null,
    durableAttemptId: null,
    status: "created",
    protectedRecoveryKey: null,
  });
}

function normalizeOfflineProof(
  input: OfflineCashCapacityProof,
): OfflineCashCapacityProof {
  return Object.freeze({
    evidenceId: strictText(
      input.evidenceId,
      "offline cash evidence id",
      256,
    ),
    capacityId: strictText(
      input.capacityId,
      "offline cash capacity id",
      128,
    ),
    originalOrderGuid: strictText(
      input.originalOrderGuid,
      "offline cash original order",
      128,
    ),
    remainingCents: nonNegativeInteger(
      input.remainingCents,
      "offline cash remaining amount",
    ),
  });
}

async function loadAction(
  connection: SqliteConnectionPort,
  encryptor: SensitivePayloadEncryptor,
  actionId: string,
): Promise<DurableReturnAction | null> {
  const row = await connection.getFirst<ReturnActionRow>(
    `SELECT action_id, request_fingerprint, return_order_guid,
      action_recovery_token, source_kind, total_refund_cents, online,
      store_code, device_code, cashier_id, cashier_name, session_epoch,
      supervisor_grant_id, plan_json, state, created_at_iso, completed_at_iso
     FROM return_actions
     WHERE action_id = ?`,
    [actionId],
  );
  if (!row) return null;
  const persistedPlan = parsePersistedPlan(row.plan_json);
  const plan = persistedPlan.plan;
  if (
    plan.sourceKind !== text(row.source_kind, "return source kind") ||
    plan.totalRefundCents !==
      positiveInteger(row.total_refund_cents, "return total refund") ||
    plan.online !== booleanInteger(row.online, "return online state")
  ) {
    throw new Error("Persisted return plan header is inconsistent.");
  }
  const persistedCashierId = text(row.cashier_id, "return cashier id");
  const persistedCashierName = text(row.cashier_name, "return cashier name");
  if (
    persistedPlan.actor !== null &&
    (persistedPlan.actor.cashierId !== persistedCashierId ||
      persistedPlan.actor.cashierName !== persistedCashierName)
  ) {
    throw new Error("Persisted return audit actor does not match action identity.");
  }
  const lineRows = await connection.getAll<ReturnLineRow>(
    `SELECT line.line_id, line.line_index, line.selection_key,
      line.source_kind, line.return_source_key,
      line.original_order_guid, line.original_order_detail_guid,
      line.product_code, line.item_number, line.lookup_code,
      line.display_name, line.quantity, line.unit_refund_cents,
      line.signed_amount_cents, line.available_quantity,
      line.remaining_amount_cents, order_line.reference_code,
      order_line.sync_price_source
     FROM return_action_lines AS line
     INNER JOIN return_actions AS action
       ON action.action_id = line.action_id
     INNER JOIN local_order_lines AS order_line
       ON order_line.order_guid = action.return_order_guid
      AND order_line.line_id = line.line_id
     WHERE line.action_id = ?
     ORDER BY line.line_index`,
    [actionId],
  );
  const allocationRows = await connection.getAll<ReturnAllocationRow>(
    `SELECT allocation_id, allocation_index, execution_kind, method,
      signed_amount_cents, capacity_id, original_order_guid,
      offline_evidence_id, offline_evidence_remaining_cents,
      external_attempt_id, external_attempt_kind, external_action_id,
      durable_attempt_id, status, protected_recovery_ciphertext,
      capacity_reservation_state
     FROM return_action_allocations
     WHERE action_id = ?
     ORDER BY allocation_index`,
    [actionId],
  );
  const lines = lineRows.map(mapPersistedLine);
  const allocations: DurableReturnAllocation[] = [];
  for (const allocationRow of allocationRows) {
    allocations.push(await mapPersistedAllocation(allocationRow, encryptor));
  }
  assertLinePlanIdentity(lines, plan.lines);
  assertAllocationPlanIdentity(allocations, plan.allocations);
  const status = actionStatus(row.state);
  const completedAtIso = nullableIso(
    row.completed_at_iso,
    "return completed time",
  );
  if ((status === "completed") !== (completedAtIso !== null)) {
    throw new Error("Persisted return completion state is inconsistent.");
  }
  return Object.freeze({
    actionId: strictText(row.action_id, "persisted return action id", 128),
    requestFingerprint: strictText(
      row.request_fingerprint,
      "persisted return fingerprint",
      1_048_576,
    ),
    returnOrderGuid: strictText(
      row.return_order_guid,
      "persisted return order guid",
      128,
    ),
    actionRecoveryToken: strictText(
      row.action_recovery_token,
      "persisted return recovery token",
      128,
    ),
    identity: normalizeIdentity({
      storeCode: text(row.store_code, "return store code"),
      deviceCode: text(row.device_code, "return device code"),
      cashierId: persistedCashierId,
      cashierName: persistedCashierName,
      userGuid: persistedPlan.actor?.userGuid ?? null,
      sessionEpoch: text(row.session_epoch, "return session epoch"),
    }),
    plan,
    supervisorGrantKey: nullableText(row.supervisor_grant_id),
    createdAtIso: canonicalIso(
      text(row.created_at_iso, "return created time"),
      "return created time",
    ),
    lines: Object.freeze(lines),
    allocations: Object.freeze(allocations),
    status,
    completedAtIso,
  });
}

function mapPersistedLine(row: ReturnLineRow): DurableReturnLine {
  return normalizeLine({
    lineId: text(row.line_id, "return line id"),
    selectionKey: text(row.selection_key, "return selection key"),
    sourceKind: returnSourceKind(row.source_kind),
    returnSourceKey: text(row.return_source_key, "return source key"),
    originalOrderGuid: nullableText(row.original_order_guid),
    originalOrderDetailGuid: nullableText(row.original_order_detail_guid),
    productCode: text(row.product_code, "return product code"),
    itemNumber: nullableText(row.item_number),
    lookupCode: text(row.lookup_code, "return lookup code"),
    displayName: text(row.display_name, "return display name"),
    quantity: positiveInteger(row.quantity, "return line quantity"),
    unitRefundCents: positiveInteger(
      row.unit_refund_cents,
      "return unit refund",
    ),
    signedAmountCents: negativeInteger(
      row.signed_amount_cents,
      "return signed amount",
    ),
    availableQuantity: nullableInteger(
      row.available_quantity,
      "return available quantity",
    ),
    remainingAmountCents: nullableInteger(
      row.remaining_amount_cents,
      "return remaining amount",
    ),
    syncProvenance: persistedReturnLineSyncProvenance(row),
  });
}

async function mapPersistedAllocation(
  row: ReturnAllocationRow,
  encryptor: SensitivePayloadEncryptor,
): Promise<DurableReturnAllocation> {
  const executionKind = executionKindValue(row.execution_kind);
  const method = returnTenderMethod(row.method);
  const capacityId = nullableText(row.capacity_id);
  const originalOrderGuid = nullableText(row.original_order_guid);
  const evidenceId = nullableText(row.offline_evidence_id);
  const evidenceRemaining = nullableInteger(
    row.offline_evidence_remaining_cents,
    "return offline evidence remaining",
  );
  const proof =
    evidenceId === null && evidenceRemaining === null
      ? null
      : {
          evidenceId: evidenceId ?? invalid<string>(
            "Persisted offline evidence id is missing.",
          ),
          capacityId:
            capacityId ??
            invalid<string>("Persisted offline capacity id is missing."),
          originalOrderGuid:
            originalOrderGuid ??
            invalid<string>("Persisted offline original order is missing."),
          remainingCents:
            evidenceRemaining ??
            invalid<number>("Persisted offline evidence amount is missing."),
        };
  const externalAttemptKindValue = nullableText(
    row.external_attempt_kind,
  );
  const externalAttemptKindNormalized =
    externalAttemptKindValue === null
      ? null
      : externalAttemptKind(externalAttemptKindValue);
  const allocation: DurableReturnAllocation = {
    allocationId: strictText(
      row.allocation_id,
      "persisted return allocation id",
      128,
    ),
    index: nonNegativeInteger(
      row.allocation_index,
      "persisted return allocation index",
    ),
    executionKind,
    method,
    signedAmountCents: negativeInteger(
      row.signed_amount_cents,
      "persisted allocation amount",
    ),
    capacityId,
    originalOrderGuid,
    offlineCashProof: proof,
    externalAttemptId: nullableText(row.external_attempt_id),
    externalAttemptKind: externalAttemptKindNormalized,
    externalActionId: nullableText(row.external_action_id),
    durableAttemptId: nullableText(row.durable_attempt_id),
    status: allocationStatus(row.status),
    protectedRecoveryKey: await decryptRecoveryKey(
      row.protected_recovery_ciphertext,
      encryptor,
    ),
  };
  assertPersistedAllocationShape(
    allocation,
    text(row.capacity_reservation_state, "capacity reservation state"),
  );
  return Object.freeze(allocation);
}

async function insertReturnLine(
  transaction: SqliteConnectionPort,
  actionId: string,
  index: number,
  line: DurableReturnLine,
): Promise<void> {
  await transaction.run(
    `INSERT INTO return_action_lines (
      action_id, line_id, line_index, selection_key, source_kind,
      return_source_key, original_order_guid, original_order_detail_guid,
      product_code, item_number, lookup_code, display_name, quantity,
      unit_refund_cents, signed_amount_cents, available_quantity,
      remaining_amount_cents
    ) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)`,
    [
      actionId,
      line.lineId,
      index,
      line.selectionKey,
      line.sourceKind,
      line.returnSourceKey,
      line.originalOrderGuid,
      line.originalOrderDetailGuid,
      line.productCode,
      line.itemNumber,
      line.lookupCode,
      line.displayName,
      line.quantity,
      line.unitRefundCents,
      line.signedAmountCents,
      line.availableQuantity,
      line.remainingAmountCents,
    ],
  );
}

async function loadLineCapacityBalances(
  connection: SqliteConnectionPort,
  orderGuid: string,
  detailGuid: string,
  inputQuantity: number,
  inputAmount: number,
): Promise<Readonly<{
  quantities: number[];
  amounts: number[];
  originalQuantity: number;
  originalAmount: number;
  completedQuantity: number;
  completedAmount: number;
  ambiguousHistory: boolean;
}>> {
  const quantityRows = await connection.getAll<{
    original_quantity: unknown;
    remaining_quantity: unknown;
  }>(
    `SELECT original_quantity, remaining_quantity FROM return_capacity
     WHERE original_order_guid = ? AND original_order_detail_guid = ?`,
    [orderGuid, detailGuid],
  );
  const amountRows = await connection.getAll<{
    original_amount_cents: unknown;
    remaining_amount_cents: unknown;
  }>(
    `SELECT original_amount_cents, remaining_amount_cents FROM return_amount_capacity
     WHERE original_order_guid = ? AND original_order_detail_guid = ?`,
    [orderGuid, detailGuid],
  );
  const completed = await connection.getFirst<{
    quantity: unknown;
    amount: unknown;
    source_count: unknown;
  }>(
    `SELECT COALESCE(SUM(line.quantity), 0) AS quantity,
       COALESCE(SUM(-line.signed_amount_cents), 0) AS amount,
       COUNT(DISTINCT line.return_source_key) AS source_count
     FROM return_action_lines line
     INNER JOIN return_actions action ON action.action_id = line.action_id
     WHERE line.original_order_guid = ? AND line.original_order_detail_guid = ?
       AND line.source_kind = 'receipt' AND action.state = 'completed'`,
    [orderGuid, detailGuid],
  );
  const quantities = quantityRows.map((row) => nonNegativeInteger(row.remaining_quantity, "return sibling quantity"));
  const amounts = amountRows.map((row) => nonNegativeInteger(row.remaining_amount_cents, "return sibling amount"));
  const originalQuantities = quantityRows.map((row) => nonNegativeInteger(row.original_quantity, "return original quantity"));
  const originalAmounts = amountRows.map((row) => nonNegativeInteger(row.original_amount_cents, "return original amount"));
  // 新快照可能已经包含服务端退款；旧别名若分别完成且基线不一，无法还原时序。
  // 只在无歧义时沿用最大的历史原额，再扣本机已完成量，并与每份余额取小。
  return {
    quantities,
    amounts,
    originalQuantity: originalQuantities.length ? Math.max(...originalQuantities) : inputQuantity,
    originalAmount: originalAmounts.length ? Math.max(...originalAmounts) : inputAmount,
    completedQuantity: nonNegativeInteger(completed?.quantity ?? 0, "completed return quantity"),
    completedAmount: nonNegativeInteger(completed?.amount ?? 0, "completed return amount"),
    ambiguousHistory: nonNegativeInteger(completed?.source_count ?? 0, "completed source count") > 1
      && (new Set(originalQuantities).size > 1 || new Set(originalAmounts).size > 1),
  };
}

async function reserveLineCapacity(
  transaction: SqliteConnectionPort,
  actionId: string,
  line: DurableReturnLine,
  createdAtIso: string,
): Promise<void> {
  const originalOrderGuid =
    line.originalOrderGuid ??
    invalid<string>("Receipt return original order is missing.");
  const originalDetailGuid =
    line.originalOrderDetailGuid ??
    invalid<string>("Receipt return original detail is missing.");
  const availableQuantity =
    line.availableQuantity ??
    invalid<number>("Receipt return quantity capacity is missing.");
  const remainingAmountCents =
    line.remainingAmountCents ??
    invalid<number>("Receipt return amount capacity is missing.");
  // 历史版本分别用了 local-receipt/receipt 键；同一原单明细只能共享一份余额。
  const siblingBalances = await loadLineCapacityBalances(
    transaction, originalOrderGuid, originalDetailGuid,
    availableQuantity, remainingAmountCents,
  );
  if (siblingBalances.ambiguousHistory) {
    throw new Error("Historical return line capacity has ambiguous source balances.");
  }
  const quantityFloor = Math.min(
    availableQuantity, ...siblingBalances.quantities,
    siblingBalances.originalQuantity - siblingBalances.completedQuantity,
  );
  const amountFloor = Math.min(
    remainingAmountCents, ...siblingBalances.amounts,
    siblingBalances.originalAmount - siblingBalances.completedAmount,
  );
  if (quantityFloor < 0 || amountFloor < 0) {
    throw new Error("Historical return line capacity is inconsistent.");
  }
  const existingQuantity = await transaction.getFirst<{
    original_order_guid: unknown;
    original_order_detail_guid: unknown;
    original_quantity: unknown;
    remaining_quantity: unknown;
  }>(
    `SELECT original_order_guid, original_order_detail_guid,
      original_quantity, remaining_quantity
     FROM return_capacity
     WHERE return_source_key = ?`,
    [line.returnSourceKey],
  );
  if (!existingQuantity) {
    await transaction.run(
      `INSERT INTO return_capacity (
        return_source_key, original_order_guid,
        original_order_detail_guid, original_quantity,
        remaining_quantity, updated_at_iso
      ) VALUES (?, ?, ?, ?, ?, ?)`,
      [
        line.returnSourceKey,
        originalOrderGuid,
        originalDetailGuid,
        String(siblingBalances.originalQuantity),
        String(quantityFloor),
        createdAtIso,
      ],
    );
  } else {
    if (
      text(existingQuantity.original_order_guid, "capacity original order") !==
        originalOrderGuid ||
      nullableText(existingQuantity.original_order_detail_guid) !==
        originalDetailGuid
    ) {
      throw new Error("Return line capacity identity is inconsistent.");
    }
    const current = nonNegativeInteger(
      existingQuantity.remaining_quantity,
      "return remaining quantity",
    );
    if (quantityFloor < current) {
      await transaction.run(
        `UPDATE return_capacity
         SET remaining_quantity = ?, updated_at_iso = ?
         WHERE return_source_key = ?
           AND CAST(remaining_quantity AS INTEGER) > ?`,
        [
          String(quantityFloor),
          createdAtIso,
          line.returnSourceKey,
          quantityFloor,
        ],
      );
    }
  }
  const existingAmount = await transaction.getFirst<{
    original_order_guid: unknown;
    original_order_detail_guid: unknown;
    remaining_amount_cents: unknown;
  }>(
    `SELECT original_order_guid, original_order_detail_guid,
      remaining_amount_cents
     FROM return_amount_capacity
     WHERE return_source_key = ?`,
    [line.returnSourceKey],
  );
  if (!existingAmount) {
    await transaction.run(
      `INSERT INTO return_amount_capacity (
        return_source_key, original_order_guid,
        original_order_detail_guid, original_amount_cents,
        remaining_amount_cents, updated_at_iso
      ) VALUES (?, ?, ?, ?, ?, ?)`,
      [
        line.returnSourceKey,
        originalOrderGuid,
        originalDetailGuid,
        siblingBalances.originalAmount,
        amountFloor,
        createdAtIso,
      ],
    );
  } else {
    if (
      text(existingAmount.original_order_guid, "amount capacity order") !==
        originalOrderGuid ||
      text(existingAmount.original_order_detail_guid, "amount capacity detail") !==
        originalDetailGuid
    ) {
      throw new Error("Return amount capacity identity is inconsistent.");
    }
    if (
      amountFloor <
      nonNegativeInteger(
        existingAmount.remaining_amount_cents,
        "return remaining amount",
      )
    ) {
      await transaction.run(
        `UPDATE return_amount_capacity
         SET remaining_amount_cents = ?, updated_at_iso = ?
         WHERE return_source_key = ? AND remaining_amount_cents > ?`,
        [
          amountFloor,
          createdAtIso,
          line.returnSourceKey,
          amountFloor,
        ],
      );
    }
  }
  const available = await transaction.getFirst<{
    quantity: unknown;
    amount: unknown;
  }>(
    `SELECT
      CAST(quantity_capacity.remaining_quantity AS INTEGER) -
        COALESCE((
          SELECT SUM(reservation.quantity)
          FROM return_line_capacity_reservations reservation
          WHERE reservation.return_source_key IN (
            SELECT return_source_key FROM return_capacity
            WHERE original_order_guid = ? AND original_order_detail_guid = ?
          )
            AND reservation.state = 'Reserved'
        ), 0) AS quantity,
      amount_capacity.remaining_amount_cents -
        COALESCE((
          SELECT SUM(reservation.amount_cents)
          FROM return_line_capacity_reservations reservation
          WHERE reservation.return_source_key IN (
            SELECT return_source_key FROM return_capacity
            WHERE original_order_guid = ? AND original_order_detail_guid = ?
          )
            AND reservation.state = 'Reserved'
        ), 0) AS amount
     FROM return_capacity quantity_capacity
     INNER JOIN return_amount_capacity amount_capacity
       ON amount_capacity.return_source_key =
         quantity_capacity.return_source_key
     WHERE quantity_capacity.return_source_key = ?`,
    [originalOrderGuid, originalDetailGuid, originalOrderGuid, originalDetailGuid, line.returnSourceKey],
  );
  if (
    !available ||
    integer(available.quantity, "available return quantity") < line.quantity ||
    integer(available.amount, "available return amount") <
      -line.signedAmountCents
  ) {
    throw new Error("Return line capacity is exhausted or reserved.");
  }
  await transaction.run(
    `INSERT INTO return_line_capacity_reservations (
      action_id, line_id, return_source_key, quantity, amount_cents,
      state, created_at_iso, updated_at_iso
    ) VALUES (?, ?, ?, ?, ?, 'Reserved', ?, ?)`,
    [
      actionId,
      line.lineId,
      line.returnSourceKey,
      line.quantity,
      -line.signedAmountCents,
      createdAtIso,
      createdAtIso,
    ],
  );
}

async function reserveAndInsertAllocation(
  transaction: SqliteConnectionPort,
  draft: PrepareDurableReturnAction,
  allocation: DurableReturnAllocation,
): Promise<void> {
  let reservationState: "None" | "Reserved" = "None";
  if (allocation.capacityId !== null) {
    const capacity = await transaction.getFirst<{
      original_order_guid: unknown;
      method: unknown;
      remaining_amount_cents: unknown;
      reserved_amount_cents: unknown;
    }>(
      `SELECT capacity.original_order_guid, capacity.method,
        capacity.remaining_amount_cents,
        COALESCE((
          SELECT SUM(-reserved.signed_amount_cents)
          FROM return_action_allocations reserved
          WHERE reserved.capacity_id = capacity.capacity_id
            AND reserved.capacity_reservation_state = 'Reserved'
        ), 0) AS reserved_amount_cents
       FROM return_tender_capacities capacity
       WHERE capacity.capacity_id = ?`,
      [allocation.capacityId],
    );
    if (
      !capacity ||
      text(capacity.original_order_guid, "tender capacity original order") !==
        allocation.originalOrderGuid ||
      nonNegativeInteger(
        capacity.remaining_amount_cents,
        "tender capacity remaining amount",
      ) -
        nonNegativeInteger(
          capacity.reserved_amount_cents,
          "tender capacity reserved amount",
        ) <
        -allocation.signedAmountCents
    ) {
      // 代替退款（现金/代金券代替原支付方式）允许 method 与原 capacity 不同，
      // 但仍必须从原 capacity 扣减剩余额度，防止超额退款。
      throw new Error("Original tender capacity is missing or exhausted.");
    }
    if (
      allocation.offlineCashProof &&
      allocation.offlineCashProof.remainingCents <
        -allocation.signedAmountCents
    ) {
      throw new Error("Offline cash capacity proof is insufficient.");
    }
    reservationState = "Reserved";
  } else if (draft.plan.sourceKind === "receipt") {
    throw new Error("Receipt return allocation requires original capacity.");
  }
  await transaction.run(
    `INSERT INTO return_action_allocations (
      action_id, allocation_id, allocation_index, execution_kind,
      method, signed_amount_cents, capacity_id, original_order_guid,
      offline_evidence_id, offline_evidence_remaining_cents,
      external_attempt_id, external_attempt_kind, external_action_id,
      durable_attempt_id, status, protected_recovery_ciphertext,
      capacity_reservation_state, created_at_iso, updated_at_iso
    ) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, NULL, NULL, NULL,
      'created', NULL, ?, ?, ?)`,
    [
      draft.actionId,
      allocation.allocationId,
      allocation.index,
      allocation.executionKind,
      allocation.method,
      allocation.signedAmountCents,
      allocation.capacityId,
      allocation.originalOrderGuid,
      allocation.offlineCashProof?.evidenceId ?? null,
      allocation.offlineCashProof?.remainingCents ?? null,
      allocation.externalAttemptId,
      reservationState,
      draft.createdAtIso,
      draft.createdAtIso,
    ],
  );
}

async function insertPreparedReturnOrderDraft(
  connection: SqliteConnectionPort,
  draft: PrepareDurableReturnAction,
): Promise<void> {
  const localSequence = await allocateLocalSequence(
    connection,
    draft.createdAtIso,
  );
  const originalOrderGuid =
    draft.plan.sourceKind === "receipt"
      ? draft.lines[0]?.originalOrderGuid ?? null
      : null;
  await connection.run(
    `INSERT INTO local_orders (
      order_guid, local_sequence, store_code, device_code,
      cashier_id, cashier_name, sold_at_iso, state,
      total_cents, discount_cents, actual_amount_cents,
      original_order_guid, created_at_iso, updated_at_iso
    ) VALUES (?, ?, ?, ?, ?, ?, ?, 'Draft', ?, 0, ?, ?, ?, ?)`,
    [
      draft.returnOrderGuid,
      localSequence,
      draft.identity.storeCode,
      draft.identity.deviceCode,
      draft.identity.cashierId,
      draft.identity.cashierName,
      draft.createdAtIso,
      -draft.plan.totalRefundCents,
      -draft.plan.totalRefundCents,
      originalOrderGuid,
      draft.createdAtIso,
      draft.createdAtIso,
    ],
  );
  for (const [index, line] of draft.lines.entries()) {
    await connection.run(
      `INSERT INTO local_order_lines (
        line_id, order_guid, line_sequence, product_code, item_number,
        lookup_code, display_name, quantity, unit_price_cents,
        discount_cents, actual_amount_cents, price_source,
        reference_code, sync_price_source, line_kind,
        return_source_key, original_order_guid, original_order_detail_guid
      ) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, 0, ?, ?, ?, ?, 'return', ?, ?, ?)`,
      [
        line.lineId,
        draft.returnOrderGuid,
        index + 1,
        line.productCode,
        line.itemNumber,
        line.lookupCode,
        line.displayName,
        String(line.quantity),
        line.unitRefundCents,
        line.signedAmountCents,
        line.sourceKind === "no-receipt-open-item"
          ? "open-item"
          : "catalog",
        line.syncProvenance.referenceCode,
        line.syncProvenance.priceSource,
        line.returnSourceKey,
        line.originalOrderGuid,
        line.originalOrderDetailGuid,
      ],
    );
  }
}

async function requireAllocationBindingRow(
  connection: SqliteConnectionPort,
  actionId: string,
  allocationId: string,
): Promise<AllocationBindingRow> {
  const row = await connection.getFirst<AllocationBindingRow>(
    `SELECT allocation.action_id, allocation.allocation_id,
      allocation.allocation_index,
      allocation.execution_kind, allocation.method,
      allocation.signed_amount_cents, allocation.capacity_id,
      allocation.original_order_guid, allocation.offline_evidence_id,
      allocation.offline_evidence_remaining_cents,
      allocation.external_attempt_id, allocation.external_attempt_kind,
      allocation.external_action_id, allocation.durable_attempt_id,
      allocation.status, allocation.protected_recovery_ciphertext,
      allocation.capacity_reservation_state,
      action.return_order_guid, action.state AS action_state
     FROM return_action_allocations allocation
     INNER JOIN return_actions action
       ON action.action_id = allocation.action_id
     WHERE allocation.action_id = ? AND allocation.allocation_id = ?`,
    [actionId, allocationId],
  );
  if (!row) throw new Error("Return allocation is missing.");
  return row;
}

async function assertPaymentProviderAttemptBinding(
  connection: SqliteConnectionPort,
  allocation: AllocationBindingRow,
  externalActionId: string,
  durableAttemptId: string,
): Promise<void> {
  const row = await connection.getFirst<{
    action_id: unknown;
    request_signature: unknown;
    attempt_id: unknown;
    attempt_order_guid: unknown;
    provider: unknown;
    operation: unknown;
    amount_cents: unknown;
  }>(
    `SELECT binding.action_id, binding.request_signature,
      binding.attempt_id, attempt.order_guid AS attempt_order_guid,
      attempt.provider, attempt.operation, attempt.amount_cents
     FROM payment_action_bindings binding
     INNER JOIN payment_attempts attempt
       ON attempt.attempt_id = binding.attempt_id
      AND attempt.order_guid = binding.order_guid
      AND attempt.idempotency_key = binding.idempotency_key
     WHERE binding.order_guid = ? AND binding.action_id = ?
       AND binding.attempt_id = ?`,
    [
      text(allocation.return_order_guid, "return order guid"),
      externalActionId,
      durableAttemptId,
    ],
  );
  if (!row) {
    throw new Error("Return provider payment attempt binding is missing.");
  }
  const signature = parsePaymentSignature(
    text(row.request_signature, "return payment request signature"),
  );
  const method = returnTenderMethod(allocation.method);
  const provider = text(row.provider, "return payment provider");
  if (
    text(row.action_id, "return external action id") !== externalActionId ||
    text(row.attempt_id, "return payment attempt id") !== durableAttemptId ||
    text(row.attempt_order_guid, "return payment order guid") !==
      text(allocation.return_order_guid, "return order guid") ||
    text(row.operation, "return payment operation") !== "refund" ||
    signature.operation !== "refund" ||
    signature.provider !== provider ||
    signature.amountCents !==
      negativeInteger(
        allocation.signed_amount_cents,
        "return allocation amount",
      ) ||
    integer(row.amount_cents, "return payment attempt amount") !==
      signature.amountCents ||
    !(
      (method === "card" &&
        (provider === "square" || provider === "linkly-cloud")) ||
      (method === "voucher" && provider === "voucher")
    )
  ) {
    throw new Error(
      "Return provider payment attempt identity is inconsistent.",
    );
  }
}

async function assertApiAttemptBinding(
  connection: SqliteConnectionPort,
  allocation: AllocationBindingRow,
  externalActionId: string,
  durableAttemptId: string,
): Promise<void> {
  const row = await connection.getFirst<{
    durable_attempt_id: unknown;
    external_attempt_id: unknown;
    return_order_guid: unknown;
    action_id: unknown;
    allocation_id: unknown;
    external_action_id: unknown;
    method: unknown;
    signed_amount_cents: unknown;
  }>(
    `SELECT durable_attempt_id, external_attempt_id, return_order_guid,
      action_id, allocation_id, external_action_id, method,
      signed_amount_cents
     FROM return_api_attempts
     WHERE durable_attempt_id = ? AND external_action_id = ?`,
    [durableAttemptId, externalActionId],
  );
  if (
    !row ||
    text(row.external_attempt_id, "API external attempt id") !==
      text(allocation.external_attempt_id, "allocation external attempt id") ||
    text(row.return_order_guid, "API return order guid") !==
      text(allocation.return_order_guid, "return order guid") ||
    text(row.action_id, "API return action id") !==
      text(allocation.action_id, "allocation action id") ||
    text(row.allocation_id, "API allocation id") !==
      text(allocation.allocation_id, "return allocation id") ||
    text(row.external_action_id, "API external action id") !==
      externalActionId ||
    returnTenderMethod(row.method) !== returnTenderMethod(allocation.method) ||
    integer(row.signed_amount_cents, "API signed amount") !==
      integer(allocation.signed_amount_cents, "allocation signed amount")
  ) {
    throw new Error("Return Hbpos API attempt identity is inconsistent.");
  }
}

async function assertExternalOutcomeState(
  connection: SqliteConnectionPort,
  allocation: AllocationBindingRow,
  outcome: "completed" | "declined" | "unknown",
): Promise<void> {
  const executionKind = executionKindValue(allocation.execution_kind);
  if (executionKind === "offline-cash") return;
  const attemptKindValue = nullableText(allocation.external_attempt_kind);
  const durableAttemptId = nullableText(allocation.durable_attempt_id);
  if (!attemptKindValue || !durableAttemptId) {
    throw new Error("Online return allocation attempt is not bound.");
  }
  const attemptKind = externalAttemptKind(attemptKindValue);
  let state: string;
  if (attemptKind === "payment-provider") {
    const row = await connection.getFirst<{ state: unknown }>(
      "SELECT state FROM payment_attempts WHERE attempt_id = ?",
      [durableAttemptId],
    );
    state = text(row?.state, "provider refund attempt state");
  } else {
    const row = await connection.getFirst<{ state: unknown }>(
      "SELECT state FROM return_api_attempts WHERE durable_attempt_id = ?",
      [durableAttemptId],
    );
    state = text(row?.state, "API refund attempt state");
  }
  if (
    (outcome === "completed" && state !== "Approved") ||
    (outcome === "declined" &&
      state !== "Declined" &&
      state !== "Cancelled") ||
    (outcome === "unknown" &&
      !["Created", "Submitted", "Pending", "Unknown"].includes(state))
  ) {
    throw new Error("Return external outcome and attempt state diverged.");
  }
}

type PersistedReturnTenderBindingRow = Readonly<{
  tender_guid: unknown;
  action_id: unknown;
  allocation_id: unknown;
  external_attempt_kind: unknown;
  external_action_id: unknown;
  durable_attempt_id: unknown;
  order_guid: unknown;
  method: unknown;
  amount_cents: unknown;
  payment_attempt_id: unknown;
}>;

function isPaymentProviderAllocation(
  allocation: AllocationBindingRow,
): boolean {
  const attemptKind = nullableText(allocation.external_attempt_kind);
  return (
    attemptKind !== null &&
    externalAttemptKind(attemptKind) === "payment-provider"
  );
}

async function ensureApprovedProviderTender(
  connection: SqliteConnectionPort,
  allocation: AllocationBindingRow,
  ids: ReturnExecutionPersistenceIds | null,
  createdAtIso: string,
  createIfMissing: boolean,
): Promise<void> {
  if (
    externalAttemptKind(allocation.external_attempt_kind) !==
    "payment-provider"
  ) {
    throw new Error("Return provider tender requires a provider attempt.");
  }
  const externalActionId = text(
    allocation.external_action_id,
    "return external action id",
  );
  const durableAttemptId = text(
    allocation.durable_attempt_id,
    "return durable attempt id",
  );
  await assertPaymentProviderAttemptBinding(
    connection,
    allocation,
    externalActionId,
    durableAttemptId,
  );
  await assertExternalOutcomeState(connection, allocation, "completed");

  const existingRows =
    await connection.getAll<PersistedReturnTenderBindingRow>(
      `SELECT binding.tender_guid, binding.action_id,
        binding.allocation_id, binding.external_attempt_kind,
        binding.external_action_id, binding.durable_attempt_id,
        tender.order_guid, tender.method, tender.amount_cents,
        tender.payment_attempt_id
       FROM return_tender_attempt_bindings binding
       INNER JOIN order_tenders tender
         ON tender.tender_guid = binding.tender_guid
       WHERE (binding.action_id = ? AND binding.allocation_id = ?)
          OR binding.durable_attempt_id = ?`,
      [
        text(allocation.action_id, "return action id"),
        text(allocation.allocation_id, "return allocation id"),
        durableAttemptId,
      ],
    );
  if (existingRows.length > 1) {
    throw new Error("Approved provider tender has conflicting bindings.");
  }
  const existing = existingRows[0];
  if (existing !== undefined) {
    assertReturnTenderBindingIdentity(existing, allocation);
    return;
  }
  if (!createIfMissing || ids === null) {
    throw new Error("Approved provider return tender is missing.");
  }
  const consumed = await connection.getFirst<{ tender_guid: unknown }>(
    `SELECT tender_guid
     FROM order_tenders
     WHERE payment_attempt_id = ?`,
    [durableAttemptId],
  );
  if (consumed) {
    throw new Error(
      "Approved provider attempt is already consumed by another tender.",
    );
  }

  const tenderGuid = strictText(
    ids.createTenderGuid(),
    "return tender guid",
    128,
  );
  await connection.run(
    `INSERT INTO order_tenders (
      tender_guid, order_guid, method, amount_cents,
      payment_attempt_id, created_at_iso
    ) VALUES (?, ?, ?, ?, ?, ?)`,
    [
      tenderGuid,
      text(allocation.return_order_guid, "return order guid"),
      returnTenderMethod(allocation.method),
      negativeInteger(
        allocation.signed_amount_cents,
        "return allocation amount",
      ),
      durableAttemptId,
      createdAtIso,
    ],
  );
  await connection.run(
    `INSERT INTO return_tender_attempt_bindings (
      tender_guid, action_id, allocation_id, external_attempt_kind,
      external_action_id, durable_attempt_id, created_at_iso
    ) VALUES (?, ?, ?, 'payment-provider', ?, ?, ?)`,
    [
      tenderGuid,
      text(allocation.action_id, "return action id"),
      text(allocation.allocation_id, "return allocation id"),
      externalActionId,
      durableAttemptId,
      createdAtIso,
    ],
  );
}

function assertReturnTenderBindingIdentity(
  row: PersistedReturnTenderBindingRow,
  allocation: AllocationBindingRow | DurableReturnAllocation,
  actionIdInput?: string,
  returnOrderGuidInput?: string,
): void {
  const actionId =
    "action_id" in allocation
      ? text(allocation.action_id, "return action id")
      : strictText(actionIdInput, "return action id", 128);
  const returnOrderGuid =
    "return_order_guid" in allocation
      ? text(allocation.return_order_guid, "return order guid")
      : strictText(returnOrderGuidInput, "return order guid", 128);
  const allocationId =
    "allocation_id" in allocation
      ? text(allocation.allocation_id, "return allocation id")
      : allocation.allocationId;
  const externalAttemptKindValue =
    "external_attempt_kind" in allocation
      ? externalAttemptKind(allocation.external_attempt_kind)
      : allocation.externalAttemptKind;
  const externalActionId =
    "external_action_id" in allocation
      ? text(allocation.external_action_id, "return external action id")
      : allocation.externalActionId;
  const durableAttemptId =
    "durable_attempt_id" in allocation
      ? text(allocation.durable_attempt_id, "return durable attempt id")
      : allocation.durableAttemptId;
  const method =
    "signed_amount_cents" in allocation
      ? returnTenderMethod(allocation.method)
      : allocation.method;
  const amountCents =
    "signed_amount_cents" in allocation
      ? negativeInteger(
          allocation.signed_amount_cents,
          "return allocation amount",
        )
      : allocation.signedAmountCents;
  if (
    text(row.action_id, "return tender action id") !== actionId ||
    text(row.allocation_id, "return tender allocation id") !== allocationId ||
    externalAttemptKind(row.external_attempt_kind) !==
      externalAttemptKindValue ||
    text(row.external_action_id, "return tender external action id") !==
      externalActionId ||
    text(row.durable_attempt_id, "return tender attempt id") !==
      durableAttemptId ||
    text(row.order_guid, "return tender order guid") !== returnOrderGuid ||
    returnTenderMethod(row.method) !== method ||
    integer(row.amount_cents, "return tender amount") !== amountCents ||
    nullableText(row.payment_attempt_id) !==
      (externalAttemptKindValue === "payment-provider"
        ? durableAttemptId
        : null)
  ) {
    throw new Error("Persisted return tender attempt identity diverged.");
  }
}

async function releaseAllocationCapacity(
  connection: SqliteConnectionPort,
  actionId: string,
  allocationId: string,
): Promise<void> {
  const changed = await connection.run(
    `UPDATE return_action_allocations
     SET capacity_reservation_state = 'Released'
     WHERE action_id = ? AND allocation_id = ?
       AND capacity_reservation_state = 'Reserved'`,
    [actionId, allocationId],
  );
  if (changed.changes !== 1) {
    throw new Error("Return tender capacity release CAS failed.");
  }
}

function parsePaymentSignature(value: string): Readonly<{
  provider: string;
  operation: "refund";
  amountCents: number;
}> {
  let parsed: unknown;
  try {
    parsed = JSON.parse(value);
  } catch {
    throw new Error("Return payment request signature is invalid JSON.");
  }
  if (
    !Array.isArray(parsed) ||
    parsed.length !== 4 ||
    typeof parsed[0] !== "string" ||
    parsed[1] !== "refund" ||
    parsed[2] !== "AUD" ||
    !Number.isSafeInteger(parsed[3]) ||
    Number(parsed[3]) >= 0
  ) {
    throw new Error("Return payment request signature is invalid.");
  }
  return {
    provider: parsed[0],
    operation: "refund",
    amountCents: Number(parsed[3]),
  };
}

type NormalizedCompletion = CompleteDurableReturnAction;

function normalizeCompletion(
  input: CompleteDurableReturnAction,
): NormalizedCompletion {
  const plan = normalizePlan(input.plan);
  const lines = input.lines.map(normalizeLine);
  assertLinePlanIdentity(lines, plan.lines);
  const returnOrderGuid = strictText(
    input.returnOrderGuid,
    "completed return order guid",
    128,
  );
  const records = input.returnRecords.map((record) =>
    normalizeReturnRecord(record, returnOrderGuid),
  );
  if (records.length !== lines.length) {
    throw new TypeError("Completed return record count is invalid.");
  }
  const bySource = new Map(records.map((record) => [record.returnSourceKey, record]));
  if (bySource.size !== records.length) {
    throw new TypeError("Completed return source keys must be unique.");
  }
  for (const line of lines) {
    const record = bySource.get(line.returnSourceKey);
    if (
      !record ||
      record.originalOrderGuid !== line.originalOrderGuid ||
      record.originalOrderDetailGuid !== line.originalOrderDetailGuid ||
      record.productCode !== line.productCode ||
      record.returnQuantity !== line.quantity ||
      record.returnAmountCents !== -line.signedAmountCents
    ) {
      throw new TypeError("Completed return record does not match its line.");
    }
  }
  const outbox = {
    messageId: strictText(
      input.outbox.messageId,
      "return outbox message id",
      128,
    ),
    aggregateId: strictText(
      input.outbox.aggregateId,
      "return outbox aggregate id",
      128,
    ),
    idempotencyKey: strictText(
      input.outbox.idempotencyKey,
      "return outbox idempotency key",
      256,
    ),
    kind:
      input.outbox.kind === "return-order-sync"
        ? input.outbox.kind
        : invalid<"return-order-sync">("Return outbox kind is invalid."),
  } as const;
  if (
    outbox.aggregateId !== returnOrderGuid ||
    outbox.idempotencyKey !== returnOrderGuid
  ) {
    throw new TypeError("Return outbox must bind the same OrderGuid.");
  }
  const receiptKind = returnReceiptKind(
    input.fulfilment.receiptKind,
  );
  const printRequired = receiptKind !== "none";
  const fulfilment = {
    printJobId:
      input.fulfilment.printJobId === null
        ? null
        : strictText(
            input.fulfilment.printJobId,
            "return print job id",
            128,
          ),
    drawerEventId:
      input.fulfilment.drawerEventId === null
        ? null
        : strictText(
            input.fulfilment.drawerEventId,
            "return drawer event id",
            128,
          ),
    receiptKind,
    drawerRequired:
      typeof input.fulfilment.drawerRequired === "boolean"
        ? input.fulfilment.drawerRequired
        : invalid<boolean>("Return drawer plan is invalid."),
  } as const;
  if (
    (fulfilment.printJobId !== null) !== printRequired ||
    fulfilment.drawerRequired !== (fulfilment.drawerEventId !== null)
  ) {
    throw new TypeError("Return fulfilment plan identity is invalid.");
  }
  const hasCash = plan.allocations.some(
    (allocation) => allocation.method === "cash",
  );
  const hasCard = plan.allocations.some(
    (allocation) => allocation.method === "card",
  );
  // 必须与 deriveReturnFulfilmentPolicy 完全一致：含刷卡 → 退货小票（其后追加各张退款券），
  // 不含刷卡但有退款券（含现金 + 券混合）→ 逐张退款券面，其余只开钱箱。
  const hasVoucher = plan.allocations.some(
    (allocation) => allocation.method === "voucher",
  );
  const expectedReceiptKind = hasCard
    ? "refund-receipt"
    : hasVoucher
      ? "refund-voucher"
      : "none";
  if (
    fulfilment.receiptKind !== expectedReceiptKind ||
    fulfilment.drawerRequired !== hasCash
  ) {
    throw new TypeError("Return fulfilment policy is invalid.");
  }
  return Object.freeze({
    actionId: strictText(input.actionId, "return action id", 128),
    returnOrderGuid,
    completedAtIso: canonicalIso(
      input.completedAtIso,
      "return completed time",
    ),
    identity: normalizeIdentity(input.identity),
    plan,
    lines: Object.freeze(lines),
    returnRecords: Object.freeze(records),
    outbox: Object.freeze(outbox),
    fulfilment: Object.freeze(fulfilment),
  });
}

function normalizeReturnRecord(
  input: ReturnRecordDraft,
  returnOrderGuid: string,
): ReturnRecordDraft {
  const originalOrderGuid = optionalText(
    input.originalOrderGuid,
    "return record original order",
    128,
  );
  const originalOrderDetailGuid = optionalText(
    input.originalOrderDetailGuid,
    "return record original detail",
    128,
  );
  if (
    (originalOrderGuid === null) !== (originalOrderDetailGuid === null)
  ) {
    throw new TypeError("Return record original identity is invalid.");
  }
  if (input.returnOrderGuid !== returnOrderGuid) {
    throw new TypeError("Return record OrderGuid is inconsistent.");
  }
  return Object.freeze({
    returnDetailGuid: strictText(
      input.returnDetailGuid,
      "return detail guid",
      128,
    ),
    returnOrderGuid,
    originalOrderGuid,
    originalOrderDetailGuid,
    returnSourceKey: strictText(
      input.returnSourceKey,
      "return record source key",
      512,
    ),
    productCode: strictText(
      input.productCode,
      "return record product code",
      256,
    ),
    returnQuantity: positiveInteger(
      input.returnQuantity,
      "return record quantity",
    ),
    returnAmountCents: positiveInteger(
      input.returnAmountCents,
      "return record amount",
    ),
  });
}

function assertCompletionMatchesAction(
  completion: NormalizedCompletion,
  action: DurableReturnAction,
): void {
  if (
    completion.actionId !== action.actionId ||
    completion.returnOrderGuid !== action.returnOrderGuid ||
    JSON.stringify(completion.identity) !== JSON.stringify(action.identity) ||
    JSON.stringify(completion.plan) !== JSON.stringify(action.plan) ||
    JSON.stringify(completion.lines) !== JSON.stringify(action.lines)
  ) {
    throw new Error("Return completion was replayed with different content.");
  }
}

async function assertCompletedReplayFacts(
  connection: SqliteConnectionPort,
  action: DurableReturnAction,
  completion: NormalizedCompletion,
): Promise<void> {
  const order = await connection.getFirst<{ state: unknown }>(
    "SELECT state FROM local_orders WHERE order_guid = ?",
    [action.returnOrderGuid],
  );
  if (!order || text(order.state, "completed return order state") !== "PendingSync") {
    throw new Error("Completed return order state has diverged.");
  }
  const records = await connection.getAll<{
    return_detail_guid: unknown;
    original_order_guid: unknown;
    original_order_detail_guid: unknown;
    return_source_key: unknown;
    product_code: unknown;
    return_quantity: unknown;
    return_amount_cents: unknown;
  }>(
    `SELECT return_detail_guid, original_order_guid,
      original_order_detail_guid, return_source_key, product_code,
      return_quantity, return_amount_cents
     FROM local_return_records
     WHERE action_id = ?
     ORDER BY return_source_key`,
    [action.actionId],
  );
  const persistedRecords = records
    .map((record) =>
      normalizeReturnRecord(
      {
        returnDetailGuid: text(
          record.return_detail_guid,
          "return detail guid",
        ),
        returnOrderGuid: action.returnOrderGuid,
        originalOrderGuid: nullableText(record.original_order_guid),
        originalOrderDetailGuid: nullableText(
          record.original_order_detail_guid,
        ),
        returnSourceKey: text(
          record.return_source_key,
          "return record source key",
        ),
        productCode: text(record.product_code, "return record product code"),
        returnQuantity: integer(
          record.return_quantity,
          "return record quantity",
        ),
        returnAmountCents: integer(
          record.return_amount_cents,
          "return record amount",
        ),
      },
        action.returnOrderGuid,
      ),
    )
    .sort((left, right) =>
      left.returnSourceKey.localeCompare(right.returnSourceKey),
    );
  const expectedRecords = [...completion.returnRecords].sort((left, right) =>
    left.returnSourceKey.localeCompare(right.returnSourceKey),
  );
  if (JSON.stringify(persistedRecords) !== JSON.stringify(expectedRecords)) {
    throw new Error("Completed return records were replayed differently.");
  }
  const outbox = await connection.getFirst<{
    message_id: unknown;
    payload_json: unknown;
  }>(
    `SELECT message_id, payload_json
     FROM outbox_messages
     WHERE aggregate_id = ? AND kind = 'order-sync'`,
    [action.returnOrderGuid],
  );
  let payload: unknown;
  try {
    payload = JSON.parse(text(outbox?.payload_json, "return outbox payload"));
  } catch {
    throw new Error("Completed return outbox payload is invalid.");
  }
  if (
    text(outbox?.message_id, "return outbox message id") !==
      completion.outbox.messageId ||
    !payload ||
    typeof payload !== "object" ||
    Array.isArray(payload) ||
    Object.keys(payload).length !== 1 ||
    (payload as { orderGuid?: unknown }).orderGuid !==
      action.returnOrderGuid
  ) {
    throw new Error("Completed return outbox was replayed differently.");
  }
  const fulfilment = await connection.getFirst<{
    print_job_id: unknown;
    drawer_event_id: unknown;
    receipt_kind: unknown;
    print_receipt: unknown;
    drawer_required: unknown;
  }>(
    `SELECT print_job_id, drawer_event_id, receipt_kind,
       print_receipt, drawer_required
     FROM return_fulfilment_plans
     WHERE action_id = ? AND return_order_guid = ?`,
    [action.actionId, action.returnOrderGuid],
  );
  if (
    !fulfilment ||
    nullableText(fulfilment.print_job_id) !==
      completion.fulfilment.printJobId ||
    nullableText(fulfilment.drawer_event_id) !==
      completion.fulfilment.drawerEventId ||
    returnReceiptKind(fulfilment.receipt_kind) !==
      completion.fulfilment.receiptKind ||
    booleanInteger(fulfilment.print_receipt, "return print flag") !==
      (completion.fulfilment.receiptKind !== "none") ||
    booleanInteger(fulfilment.drawer_required, "return drawer flag") !==
      completion.fulfilment.drawerRequired
  ) {
    throw new Error("Completed return fulfilment was replayed differently.");
  }
  await assertCompletedTenderFacts(connection, action);
}

async function assertCompletedAttemptBindings(
  connection: SqliteConnectionPort,
  action: DurableReturnAction,
): Promise<void> {
  for (const allocation of action.allocations) {
    if (allocation.executionKind === "offline-cash") {
      if (
        allocation.externalAttemptKind !== null ||
        allocation.externalActionId !== null ||
        allocation.durableAttemptId !== null
      ) {
        throw new Error("Offline cash return has an unexpected attempt.");
      }
      continue;
    }
    if (
      !allocation.externalAttemptKind ||
      !allocation.externalActionId ||
      !allocation.durableAttemptId
    ) {
      throw new Error("Completed online return attempt binding is missing.");
    }
    const binding = await requireAllocationBindingRow(
      connection,
      action.actionId,
      allocation.allocationId,
    );
    await assertExternalOutcomeState(connection, binding, "completed");
    if (allocation.externalAttemptKind === "payment-provider") {
      await assertPaymentProviderAttemptBinding(
        connection,
        binding,
        allocation.externalActionId,
        allocation.durableAttemptId,
      );
      await ensureApprovedProviderTender(
        connection,
        binding,
        null,
        action.completedAtIso ?? action.createdAtIso,
        false,
      );
    }
  }
}

async function assertCompletedTenderFacts(
  connection: SqliteConnectionPort,
  action: DurableReturnAction,
): Promise<void> {
  const tenders = await connection.getAll<{
    method: unknown;
    amount_cents: unknown;
    payment_attempt_id: unknown;
  }>(
    `SELECT method, amount_cents, payment_attempt_id
     FROM order_tenders
     WHERE order_guid = ?`,
    [action.returnOrderGuid],
  );
  const actualTenderFacts = tenders
    .map((row) => ({
      method: returnTenderMethod(row.method),
      amountCents: integer(row.amount_cents, "completed return tender amount"),
      paymentAttemptId: nullableText(row.payment_attempt_id),
    }))
    .sort(compareTenderFacts);
  const expectedTenderFacts = action.allocations
    .map((allocation) => ({
      method: allocation.method,
      amountCents: allocation.signedAmountCents,
      paymentAttemptId:
        allocation.externalAttemptKind === "payment-provider"
          ? allocation.durableAttemptId
          : null,
    }))
    .sort(compareTenderFacts);
  if (JSON.stringify(actualTenderFacts) !== JSON.stringify(expectedTenderFacts)) {
    throw new Error("Completed return tender facts have diverged.");
  }

  const bindings =
    await connection.getAll<PersistedReturnTenderBindingRow>(
      `SELECT binding.tender_guid, binding.action_id,
        binding.allocation_id, binding.external_attempt_kind,
        binding.external_action_id, binding.durable_attempt_id,
        tender.order_guid, tender.method, tender.amount_cents,
        tender.payment_attempt_id
       FROM return_tender_attempt_bindings binding
       INNER JOIN order_tenders tender
         ON tender.tender_guid = binding.tender_guid
       WHERE binding.action_id = ?`,
      [action.actionId],
    );
  const expectedBound = action.allocations.filter(
    (allocation) => allocation.externalAttemptKind !== null,
  );
  if (bindings.length !== expectedBound.length) {
    throw new Error("Completed return tender binding count has diverged.");
  }
  for (const allocation of expectedBound) {
    const binding = bindings.find(
      (candidate) =>
        text(candidate.allocation_id, "return tender allocation id") ===
        allocation.allocationId,
    );
    if (!binding) {
      throw new Error("Completed return tender binding is missing.");
    }
    assertReturnTenderBindingIdentity(
      binding,
      allocation,
      action.actionId,
      action.returnOrderGuid,
    );
  }
}

function compareTenderFacts(
  left: Readonly<{
    method: ReturnTenderMethod;
    amountCents: number;
    paymentAttemptId: string | null;
  }>,
  right: Readonly<{
    method: ReturnTenderMethod;
    amountCents: number;
    paymentAttemptId: string | null;
  }>,
): number {
  return JSON.stringify(left).localeCompare(JSON.stringify(right));
}

async function commitLineCapacities(
  connection: SqliteConnectionPort,
  action: DurableReturnAction,
  completedAtIso: string,
): Promise<void> {
  for (const line of action.lines) {
    if (line.sourceKind !== "receipt") continue;
    const quantityChanged = await connection.run(
      `UPDATE return_capacity
       SET remaining_quantity =
         CAST(CAST(remaining_quantity AS INTEGER) - ? AS TEXT),
         updated_at_iso = ?
       WHERE return_source_key = ? AND original_order_guid = ?
         AND original_order_detail_guid = ?
         AND CAST(remaining_quantity AS INTEGER) >= ?
         AND EXISTS (
           SELECT 1
           FROM return_line_capacity_reservations reservation
           WHERE reservation.action_id = ?
             AND reservation.line_id = ?
             AND reservation.return_source_key = return_capacity.return_source_key
             AND reservation.quantity = ?
             AND reservation.amount_cents = ?
             AND reservation.state = 'Reserved'
         )`,
      [
        line.quantity,
        completedAtIso,
        line.returnSourceKey,
        line.originalOrderGuid,
        line.originalOrderDetailGuid,
        line.quantity,
        action.actionId,
        line.lineId,
        line.quantity,
        -line.signedAmountCents,
      ],
    );
    if (quantityChanged.changes !== 1) {
      throw new Error("Return quantity capacity completion CAS failed.");
    }
    const amountChanged = await connection.run(
      `UPDATE return_amount_capacity
       SET remaining_amount_cents = remaining_amount_cents - ?,
         updated_at_iso = ?
       WHERE return_source_key = ? AND original_order_guid = ?
         AND original_order_detail_guid = ?
         AND remaining_amount_cents >= ?`,
      [
        -line.signedAmountCents,
        completedAtIso,
        line.returnSourceKey,
        line.originalOrderGuid,
        line.originalOrderDetailGuid,
        -line.signedAmountCents,
      ],
    );
    if (amountChanged.changes !== 1) {
      throw new Error("Return amount capacity completion CAS failed.");
    }
    const reservationChanged = await connection.run(
      `UPDATE return_line_capacity_reservations
       SET state = 'Committed', updated_at_iso = ?
       WHERE action_id = ? AND line_id = ? AND state = 'Reserved'`,
      [completedAtIso, action.actionId, line.lineId],
    );
    if (reservationChanged.changes !== 1) {
      throw new Error("Return line reservation completion CAS failed.");
    }
  }
}

async function commitTenderCapacities(
  connection: SqliteConnectionPort,
  action: DurableReturnAction,
  completedAtIso: string,
): Promise<void> {
  for (const allocation of action.allocations) {
    if (allocation.capacityId === null) continue;
    const amountCents = -allocation.signedAmountCents;
    const capacityChanged = await connection.run(
      `UPDATE return_tender_capacities
       SET remaining_amount_cents = remaining_amount_cents - ?,
         updated_at_iso = ?
       WHERE capacity_id = ? AND original_order_guid = ?
         AND remaining_amount_cents >= ?
         AND EXISTS (
           SELECT 1
           FROM return_action_allocations reserved
           WHERE reserved.action_id = ?
             AND reserved.allocation_id = ?
             AND reserved.capacity_id =
               return_tender_capacities.capacity_id
             AND reserved.capacity_reservation_state = 'Reserved'
         )`,
      [
        amountCents,
        completedAtIso,
        allocation.capacityId,
        allocation.originalOrderGuid,
        amountCents,
        action.actionId,
        allocation.allocationId,
      ],
    );
    if (capacityChanged.changes !== 1) {
      throw new Error("Return tender capacity completion CAS failed.");
    }
    const reservationChanged = await connection.run(
      `UPDATE return_action_allocations
       SET capacity_reservation_state = 'Committed', updated_at_iso = ?
       WHERE action_id = ? AND allocation_id = ?
         AND capacity_reservation_state = 'Reserved'`,
      [completedAtIso, action.actionId, allocation.allocationId],
    );
    if (reservationChanged.changes !== 1) {
      throw new Error("Return tender reservation completion CAS failed.");
    }
  }
}

async function insertCompletedReturnOrder(
  connection: SqliteConnectionPort,
  action: DurableReturnAction,
  completion: NormalizedCompletion,
  ids: ReturnExecutionPersistenceIds,
): Promise<void> {
  const originalOrderGuid =
    action.plan.sourceKind === "receipt"
      ? action.lines[0]?.originalOrderGuid ?? null
      : null;
  for (const [index, line] of action.lines.entries()) {
    const persisted = await connection.getFirst<{ count: unknown }>(
      `SELECT COUNT(*) AS count
       FROM local_order_lines
       WHERE line_id = ? AND order_guid = ? AND line_sequence = ?
         AND product_code = ? AND item_number IS ?
         AND lookup_code = ? AND display_name = ? AND quantity = ?
         AND unit_price_cents = ? AND discount_cents = 0
         AND actual_amount_cents = ? AND price_source = ?
         AND reference_code IS ? AND sync_price_source = ?
         AND line_kind = 'return' AND return_source_key = ?
         AND original_order_guid IS ? AND original_order_detail_guid IS ?`,
      [
        line.lineId,
        action.returnOrderGuid,
        index + 1,
        line.productCode,
        line.itemNumber,
        line.lookupCode,
        line.displayName,
        String(line.quantity),
        line.unitRefundCents,
        line.signedAmountCents,
        line.sourceKind === "no-receipt-open-item"
          ? "open-item"
          : "catalog",
        line.syncProvenance.referenceCode,
        line.syncProvenance.priceSource,
        line.returnSourceKey,
        line.originalOrderGuid,
        line.originalOrderDetailGuid,
      ],
    );
    if (integer(persisted?.count, "prepared return line count") !== 1) {
      throw new Error("Prepared return order line identity has diverged.");
    }
  }
  const orderChanged = await connection.run(
    `UPDATE local_orders
     SET state = 'PendingSync', sold_at_iso = ?, updated_at_iso = ?
     WHERE order_guid = ? AND state = 'Draft'
       AND store_code = ? AND device_code = ?
       AND cashier_id = ? AND cashier_name = ?
       AND total_cents = ? AND discount_cents = 0
       AND actual_amount_cents = ? AND original_order_guid IS ?
       AND (
         SELECT COUNT(*) FROM local_order_lines
         WHERE local_order_lines.order_guid = local_orders.order_guid
       ) = ?`,
    [
      completion.completedAtIso,
      completion.completedAtIso,
      action.returnOrderGuid,
      action.identity.storeCode,
      action.identity.deviceCode,
      action.identity.cashierId,
      action.identity.cashierName,
      -action.plan.totalRefundCents,
      -action.plan.totalRefundCents,
      originalOrderGuid,
      action.lines.length,
    ],
  );
  if (orderChanged.changes !== 1) {
    throw new Error("Prepared return order completion CAS failed.");
  }
  for (const allocation of action.allocations) {
    if (allocation.externalAttemptKind === "payment-provider") {
      const binding = await requireAllocationBindingRow(
        connection,
        action.actionId,
        allocation.allocationId,
      );
      await ensureApprovedProviderTender(
        connection,
        binding,
        null,
        completion.completedAtIso,
        false,
      );
      continue;
    }
    const tenderGuid = strictText(
      ids.createTenderGuid(),
      "return tender guid",
      128,
    );
    await connection.run(
      `INSERT INTO order_tenders (
        tender_guid, order_guid, method, amount_cents,
        payment_attempt_id, created_at_iso
      ) VALUES (?, ?, ?, ?, ?, ?)`,
      [
        tenderGuid,
        action.returnOrderGuid,
        allocation.method,
        allocation.signedAmountCents,
        null,
        completion.completedAtIso,
      ],
    );
    if (
      allocation.externalAttemptKind &&
      allocation.externalActionId &&
      allocation.durableAttemptId
    ) {
      await connection.run(
        `INSERT INTO return_tender_attempt_bindings (
          tender_guid, action_id, allocation_id, external_attempt_kind,
          external_action_id, durable_attempt_id, created_at_iso
        ) VALUES (?, ?, ?, ?, ?, ?, ?)`,
        [
          tenderGuid,
          action.actionId,
          allocation.allocationId,
          allocation.externalAttemptKind,
          allocation.externalActionId,
          allocation.durableAttemptId,
          completion.completedAtIso,
        ],
      );
    }
  }
  await assertCompletedTenderFacts(connection, action);
  for (const record of completion.returnRecords) {
    await connection.run(
      `INSERT INTO local_return_records (
        return_detail_guid, action_id, return_order_guid,
        original_order_guid, original_order_detail_guid,
        return_source_key, product_code, return_quantity,
        return_amount_cents, created_at_iso
      ) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?)`,
      [
        record.returnDetailGuid,
        action.actionId,
        action.returnOrderGuid,
        record.originalOrderGuid,
        record.originalOrderDetailGuid,
        record.returnSourceKey,
        record.productCode,
        record.returnQuantity,
        record.returnAmountCents,
        completion.completedAtIso,
      ],
    );
  }
  const auditEventId = strictText(
    ids.createAuditEventId(),
    "return completion audit id",
    128,
  );
  await connection.run(
    `INSERT INTO audit_events (
      event_id, event_type, occurred_at_iso, order_guid,
      correlation_id, payload_json, uploaded_at_iso
    ) VALUES (?, 'RETURN_ORDER_COMPLETED', ?, ?, ?, ?, NULL)`,
    [
      auditEventId,
      completion.completedAtIso,
      action.returnOrderGuid,
      action.actionId,
      JSON.stringify({
        action: "return-order-completed",
        // Actor 来自首次 prepare 时冻结并写入 plan_json；恢复时绝不读取当前会话。
        ...auditActorPayload({
          cashierId: action.identity.cashierId,
          cashierName: action.identity.cashierName,
          userGuid: action.identity.userGuid ?? null,
        }),
        sourceKind: action.plan.sourceKind,
        totalRefundCents: action.plan.totalRefundCents,
        lineCount: action.lines.length,
        allocations: action.allocations.map((allocation) => ({
          method: allocation.method,
          amountCents: allocation.signedAmountCents,
        })),
      }),
    ],
  );
  await connection.run(
    `INSERT INTO outbox_messages (
      message_id, aggregate_id, kind, payload_json, state,
      attempt_count, next_attempt_at_iso, lease_id, lease_expires_at_iso,
      last_error_code, created_at_iso, updated_at_iso
    ) VALUES (?, ?, 'order-sync', ?, 'pending', 0, ?,
      NULL, NULL, NULL, ?, ?)`,
    [
      completion.outbox.messageId,
      action.returnOrderGuid,
      JSON.stringify({
        orderGuid: action.returnOrderGuid,
      }),
      completion.completedAtIso,
      completion.completedAtIso,
      completion.completedAtIso,
    ],
  );
  await connection.run(
    `INSERT INTO return_fulfilment_plans (
      action_id, return_order_guid, print_job_id, drawer_event_id,
      receipt_kind, print_receipt, drawer_required,
      materialized_at_iso, created_at_iso
    ) VALUES (?, ?, ?, ?, ?, ?, ?, NULL, ?)`,
    [
      action.actionId,
      action.returnOrderGuid,
      completion.fulfilment.printJobId,
      completion.fulfilment.drawerEventId,
      completion.fulfilment.receiptKind,
      completion.fulfilment.receiptKind === "none" ? 0 : 1,
      completion.fulfilment.drawerRequired ? 1 : 0,
      completion.completedAtIso,
    ],
  );
}

async function allocateLocalSequence(
  connection: SqliteConnectionPort,
  nowIso: string,
): Promise<number> {
  await connection.run(
    `INSERT INTO app_settings (
      setting_key, setting_value, updated_at_iso
    ) VALUES ('local_sequence', '0', ?)
    ON CONFLICT(setting_key) DO NOTHING`,
    [nowIso],
  );
  const row = await connection.getFirst<{ next_sequence: unknown }>(
    `UPDATE app_settings
     SET setting_value = CAST(setting_value AS INTEGER) + 1,
       updated_at_iso = ?
     WHERE setting_key = 'local_sequence'
     RETURNING setting_value AS next_sequence`,
    [nowIso],
  );
  return positiveInteger(row?.next_sequence, "return local sequence");
}

function assertPrepareReplay(
  existing: DurableReturnAction,
  proposed: PrepareDurableReturnAction,
): void {
  const existingComparable = {
    actionId: existing.actionId,
    requestFingerprint: existing.requestFingerprint,
    returnOrderGuid: existing.returnOrderGuid,
    actionRecoveryToken: existing.actionRecoveryToken,
    identity: existing.identity,
    plan: existing.plan,
    supervisorGrantKey: existing.supervisorGrantKey,
    createdAtIso: existing.createdAtIso,
    lines: existing.lines,
    allocations: existing.allocations.map(allocationImmutableProjection),
  };
  const proposedComparable = {
    actionId: proposed.actionId,
    requestFingerprint: proposed.requestFingerprint,
    returnOrderGuid: proposed.returnOrderGuid,
    actionRecoveryToken: proposed.actionRecoveryToken,
    identity: proposed.identity,
    plan: proposed.plan,
    supervisorGrantKey: proposed.supervisorGrantKey,
    createdAtIso: proposed.createdAtIso,
    lines: proposed.lines,
    allocations: proposed.allocations.map(allocationImmutableProjection),
  };
  if (JSON.stringify(existingComparable) !== JSON.stringify(proposedComparable)) {
    throw new Error(
      "Return actionId was replayed with different immutable content.",
    );
  }
}

function allocationImmutableProjection(
  allocation: DurableReturnAllocation,
): Readonly<Record<string, unknown>> {
  return {
    allocationId: allocation.allocationId,
    index: allocation.index,
    executionKind: allocation.executionKind,
    method: allocation.method,
    signedAmountCents: allocation.signedAmountCents,
    capacityId: allocation.capacityId,
    originalOrderGuid: allocation.originalOrderGuid,
    offlineCashProof: allocation.offlineCashProof,
    externalAttemptId: allocation.externalAttemptId,
  };
}

function assertLinePlanIdentity(
  lines: readonly DurableReturnLine[],
  planLines: readonly ReturnRefundLine[],
): void {
  if (lines.length !== planLines.length) {
    throw new TypeError("Durable return lines do not match the plan.");
  }
  const seenIds = new Set<string>();
  const seenSources = new Set<string>();
  const bySource = new Map(lines.map((line) => [line.returnSourceKey, line]));
  for (const line of lines) {
    if (seenIds.has(line.lineId) || seenSources.has(line.returnSourceKey)) {
      throw new TypeError("Durable return line identity must be unique.");
    }
    seenIds.add(line.lineId);
    seenSources.add(line.returnSourceKey);
  }
  for (const planned of planLines) {
    const line = bySource.get(planned.returnSourceKey);
    if (
      !line ||
      line.sourceKind !== planned.sourceKind ||
      line.originalOrderGuid !== planned.originalOrderGuid ||
      line.originalOrderDetailGuid !== planned.originalOrderDetailGuid ||
      line.productCode !== planned.productCode ||
      line.quantity !== planned.quantity ||
      line.signedAmountCents !== planned.signedAmountCents ||
      !sameReturnLineSyncProvenance(
        line.syncProvenance,
        planned.syncProvenance,
      )
    ) {
      throw new TypeError("Durable return line identity diverged from plan.");
    }
  }
}

function assertAllocationPlanIdentity(
  allocations: readonly DurableReturnAllocation[],
  planAllocations: readonly ReturnRefundAllocation[],
): void {
  if (allocations.length !== planAllocations.length) {
    throw new TypeError("Durable return allocations do not match the plan.");
  }
  const ids = new Set<string>();
  for (const [index, allocation] of allocations.entries()) {
    const planned = planAllocations[index];
    if (
      !planned ||
      ids.has(allocation.allocationId) ||
      allocation.index !== index ||
      allocation.method !== planned.method ||
      allocation.signedAmountCents !== planned.signedAmountCents ||
      allocation.capacityId !== planned.originalCapacityId ||
      allocation.originalOrderGuid !== planned.originalOrderGuid ||
      JSON.stringify(allocation.offlineCashProof) !==
        JSON.stringify(planned.offlineCashProof)
    ) {
      throw new TypeError(
        "Durable return allocation identity diverged from plan.",
      );
    }
    ids.add(allocation.allocationId);
  }
}

function assertPersistedAllocationShape(
  allocation: DurableReturnAllocation,
  reservationState: string,
): void {
  const bindingParts = [
    allocation.externalAttemptKind,
    allocation.externalActionId,
    allocation.durableAttemptId,
  ];
  const boundCount = bindingParts.filter((value) => value !== null).length;
  if (
    (boundCount !== 0 && boundCount !== 3) ||
    (allocation.executionKind === "offline-cash" &&
      (allocation.method !== "cash" ||
        allocation.offlineCashProof === null ||
        allocation.externalAttemptId !== null ||
        boundCount !== 0)) ||
    (allocation.executionKind === "online-refund" &&
      (allocation.offlineCashProof !== null ||
        allocation.externalAttemptId === null)) ||
    (allocation.capacityId === null &&
      (allocation.originalOrderGuid !== null || reservationState !== "None")) ||
    (allocation.capacityId !== null &&
      (allocation.originalOrderGuid === null ||
        !["Reserved", "Committed", "Released"].includes(reservationState)))
  ) {
    throw new Error("Persisted return allocation shape is invalid.");
  }
  if (allocation.externalAttemptKind) {
    assertAttemptKindForMethod(
      allocation.method,
      allocation.externalAttemptKind,
    );
  }
}

function assertAttemptKindForMethod(
  method: ReturnTenderMethod,
  kind: DurableExternalAttemptKind,
): void {
  if (
    (method === "card" && kind !== "payment-provider") ||
    ((method === "cash" || method === "installment") &&
      kind !== "hbpos-api")
  ) {
    throw new Error("Return attempt kind is invalid for tender method.");
  }
}

function normalizeRecoveryScope(
  input: ReturnRecoveryScope,
): ReturnRecoveryScope {
  return Object.freeze({
    storeCode: strictText(
      input.storeCode,
      "return recovery store code",
      64,
    ),
    deviceCode: strictText(
      input.deviceCode,
      "return recovery device code",
      128,
    ),
    cashierId: strictText(
      input.cashierId,
      "return recovery cashier id",
      128,
    ),
    // 必须由当前可信 lease 提供，但 SQL 匹配刻意不使用它。
    sessionEpoch: strictText(
      input.sessionEpoch,
      "return recovery session epoch",
      256,
    ),
  });
}

function normalizeIdentity(
  input: TrustedReturnIdentity,
): TrustedReturnIdentity {
  return Object.freeze({
    storeCode: strictText(input.storeCode, "return store code", 64),
    deviceCode: strictText(input.deviceCode, "return device code", 128),
    cashierId: strictText(input.cashierId, "return cashier id", 128),
    cashierName: strictText(
      input.cashierName,
      "return cashier name",
      256,
    ),
    userGuid:
      input.userGuid === null || input.userGuid === undefined
        ? null
        : strictText(input.userGuid, "return user guid", 256),
    sessionEpoch: strictText(
      input.sessionEpoch,
      "return session epoch",
      256,
    ),
  });
}

function serializePersistedPlan(
  plan: ReturnRefundPlan,
  identity: TrustedReturnIdentity,
): string {
  return JSON.stringify({
    version: 1,
    plan,
    auditActor: auditActorPayload({
      cashierId: identity.cashierId,
      cashierName: identity.cashierName,
      userGuid: identity.userGuid ?? null,
    }),
  });
}

function parsePersistedPlan(value: unknown): Readonly<{
  plan: ReturnRefundPlan;
  actor: ReturnType<typeof auditActorSnapshotFromPayload>;
}> {
  if (typeof value !== "string") {
    throw new Error("Persisted return plan JSON is invalid.");
  }
  let parsed: unknown;
  try {
    parsed = JSON.parse(value);
  } catch {
    throw new Error("Persisted return plan JSON is corrupt.");
  }
  // Migrate in place through the already-present plan_json envelope: no new column,
  // and legacy actions remain recoverable with a deliberately unknown userGuid.
  if (
    parsed &&
    typeof parsed === "object" &&
    !Array.isArray(parsed) &&
    (parsed as { version?: unknown }).version === 1 &&
    "plan" in parsed &&
    "auditActor" in parsed
  ) {
    const record = parsed as Record<string, unknown>;
    const actorPayload = record.auditActor;
    if (!actorPayload || typeof actorPayload !== "object" || Array.isArray(actorPayload)) {
      throw new Error("Persisted return audit actor is invalid.");
    }
    const actor = auditActorSnapshotFromPayload(
      actorPayload as Readonly<Record<string, unknown>>,
    );
    if (!actor) throw new Error("Persisted return audit actor is incomplete.");
    return Object.freeze({
      plan: normalizePlan(record.plan as ReturnRefundPlan),
      actor,
    });
  }
  return Object.freeze({
    plan: normalizePlan(parsed as ReturnRefundPlan),
    actor: null,
  });
}

async function decryptRecoveryKey(
  value: unknown,
  encryptor: SensitivePayloadEncryptor,
): Promise<string | null> {
  if (value === null || value === undefined) return null;
  if (!(value instanceof Uint8Array) || value.length === 0) {
    throw new Error("Return recovery ciphertext is invalid.");
  }
  let parsed: unknown;
  try {
    parsed = JSON.parse(await encryptor.decrypt(value));
  } catch {
    throw new Error("Return recovery ciphertext is corrupt.");
  }
  if (
    !parsed ||
    typeof parsed !== "object" ||
    (parsed as { version?: unknown }).version !== 1
  ) {
    throw new Error("Return recovery payload version is invalid.");
  }
  return strictText(
    (parsed as { value?: unknown }).value,
    "persisted return recovery key",
    4096,
  );
}

function optionalCiphertext(value: unknown): Uint8Array | null {
  if (value === null || value === undefined) return null;
  if (value instanceof Uint8Array && value.length > 0) return value;
  throw new Error("Return recovery ciphertext is invalid.");
}

function actionStatus(value: unknown): DurableReturnActionStatus {
  if (
    value === "processing" ||
    value === "unknown" ||
    value === "declined" ||
    value === "completed"
  ) {
    return value;
  }
  throw new Error("Return action state is invalid.");
}

function allocationStatus(value: unknown): DurableReturnAllocationStatus {
  if (
    value === "created" ||
    value === "submitted" ||
    value === "completed" ||
    value === "declined" ||
    value === "unknown"
  ) {
    return value;
  }
  throw new Error("Return allocation state is invalid.");
}

function expectedOutcomeStatus(
  value: unknown,
): "submitted" | "unknown" {
  if (value === "submitted" || value === "unknown") return value;
  throw new TypeError("Return allocation expected state is invalid.");
}

function allocationOutcomeStatus(
  value: unknown,
): "completed" | "declined" | "unknown" {
  if (value === "completed" || value === "declined" || value === "unknown") {
    return value;
  }
  throw new TypeError("Return allocation outcome state is invalid.");
}

function executionKindValue(
  value: unknown,
): DurableReturnAllocation["executionKind"] {
  if (value === "offline-cash" || value === "online-refund") return value;
  throw new Error("Return allocation execution kind is invalid.");
}

function externalAttemptKind(value: unknown): DurableExternalAttemptKind {
  if (value === "payment-provider" || value === "hbpos-api") return value;
  throw new Error("Return external attempt kind is invalid.");
}

function returnTenderMethod(value: unknown): ReturnTenderMethod {
  if (
    value === "cash" ||
    value === "card" ||
    value === "voucher" ||
    value === "installment"
  ) {
    return value;
  }
  throw new Error("Return tender method is invalid.");
}

function returnSourceKind(value: unknown): ReturnSourceKind {
  if (
    value === "receipt" ||
    value === "no-receipt-product" ||
    value === "no-receipt-open-item"
  ) {
    return value;
  }
  throw new Error("Return line source kind is invalid.");
}

function returnReceiptKind(
  value: unknown,
): CompleteDurableReturnAction["fulfilment"]["receiptKind"] {
  if (
    value === "none" ||
    value === "refund-voucher" ||
    value === "refund-receipt"
  ) {
    return value;
  }
  throw new TypeError("Return receipt kind is invalid.");
}

function booleanInteger(value: unknown, label: string): boolean {
  const integerValue = integer(value, label);
  if (integerValue === 0) return false;
  if (integerValue === 1) return true;
  throw new Error(`${label} is invalid.`);
}

function strictText(value: unknown, label: string, max: number): string {
  if (
    typeof value !== "string" ||
    value !== value.trim() ||
    !value ||
    value.length > max ||
    /[\u0000-\u001f\u007f]/u.test(value)
  ) {
    throw new TypeError(`${label} is invalid.`);
  }
  return value;
}

function optionalText(
  value: unknown,
  label: string,
  max: number,
): string | null {
  if (value === null || value === undefined) return null;
  return strictText(value, label, max);
}

function nullableText(value: unknown): string | null {
  if (value === null || value === undefined) return null;
  return text(value, "nullable text");
}

function text(value: unknown, label: string): string {
  if (typeof value !== "string" || !value) {
    throw new Error(`Persisted ${label} is invalid.`);
  }
  return value;
}

function integer(value: unknown, label: string): number {
  const parsed = Number(value);
  if (!Number.isSafeInteger(parsed)) {
    throw new Error(`${label} is invalid.`);
  }
  return parsed;
}

function nonNegativeInteger(value: unknown, label: string): number {
  const parsed = integer(value, label);
  if (parsed < 0) throw new Error(`${label} cannot be negative.`);
  return parsed;
}

function positiveInteger(value: unknown, label: string): number {
  const parsed = integer(value, label);
  if (parsed <= 0) throw new Error(`${label} must be positive.`);
  return parsed;
}

function negativeInteger(value: unknown, label: string): number {
  const parsed = integer(value, label);
  if (parsed >= 0) throw new Error(`${label} must be negative.`);
  return parsed;
}

function nullableInteger(value: unknown, label: string): number | null {
  if (value === null || value === undefined) return null;
  return integer(value, label);
}

function normalizeReturnLineSyncProvenance(
  input: unknown,
): LineSyncProvenance {
  try {
    return normalizeLineSyncProvenance(input);
  } catch {
    throw new TypeError("Return line sync provenance is invalid.");
  }
}

function persistedReturnLineSyncProvenance(
  row: Readonly<{
    reference_code: unknown;
    sync_price_source: unknown;
  }>,
): LineSyncProvenance {
  return normalizeReturnLineSyncProvenance({
    referenceCode: nullableText(row.reference_code),
    priceSource: integer(
      row.sync_price_source,
      "return line sync price source",
    ),
  });
}

function sameReturnLineSyncProvenance(
  left: unknown,
  right: unknown,
): boolean {
  const normalizedLeft =
    normalizeReturnLineSyncProvenance(left);
  const normalizedRight =
    normalizeReturnLineSyncProvenance(right);
  return (
    normalizedLeft.referenceCode ===
      normalizedRight.referenceCode &&
    normalizedLeft.priceSource === normalizedRight.priceSource
  );
}

function safeAdd(left: number, right: number): number {
  const result = left + right;
  if (!Number.isSafeInteger(result)) {
    throw new TypeError("Return amount exceeds safe integer bounds.");
  }
  return result;
}

function safeMultiply(left: number, right: number): number {
  const result = left * right;
  if (!Number.isSafeInteger(result)) {
    throw new TypeError("Return amount exceeds safe integer bounds.");
  }
  return result;
}

function canonicalIso(value: string, label: string): string {
  const parsed = Date.parse(value);
  if (!Number.isFinite(parsed) || new Date(parsed).toISOString() !== value) {
    throw new TypeError(`${label} must be canonical ISO UTC.`);
  }
  return value;
}

function nullableIso(value: unknown, label: string): string | null {
  if (value === null || value === undefined) return null;
  return canonicalIso(text(value, label), label);
}

function invalid<T>(message: string): T {
  throw new TypeError(message);
}
