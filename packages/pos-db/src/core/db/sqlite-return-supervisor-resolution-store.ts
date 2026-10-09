import {
  auditActorPayload,
  auditActorSnapshotFromPayload,
  type AuditActorSnapshot,
} from "@hb/pos-domain/core/contracts/audit-actor";
import {
  ReturnFeatureError,
  type ReturnErrorCode,
} from "@hb/pos-domain/features/returns/return-domain";
import type {
  ReturnSupervisorResolutionPort,
  ReturnSupervisorResolutionRecord,
} from "@hb/pos-domain/features/returns/adapters/durable-return-execution-orchestrator";

import { enqueueSupervisorResolvedAckInTransaction } from "./sqlite-payment-supervisor-acknowledgement-queue";
import type { SqliteConnectionPort } from "./types";

/**
 * 退款结果未知的主管结案存储（H9）。与退货账本同库同事务，但只通过窄 SQL 触达
 * return_actions / return_action_allocations / 额度预留，不改 payment_attempts：
 * provider attempt 保持原状态，迟到的 Approved 仍可被识别，不会被人工结论抹掉。
 *
 * 当前只开放：
 * - not-refunded：主管线下核对确认钱没有退到顾客卡上 → 作废整张 action、释放额度、
 *   登记后端会话的 supervisorResolved ACK；
 * - keep-waiting：只写不可变审计，action 继续锁定。
 * 「已退款」需要人工退款凭据进入回单同步，合同未就绪前不在此实现。
 */
export class SqliteReturnSupervisorResolutionStore
implements ReturnSupervisorResolutionPort {
  public constructor(
    private readonly connection: SqliteConnectionPort,
    private readonly ids: Readonly<{
      createResolutionId(): string;
      createAuditEventId(): string;
    }>,
    private readonly nowIso: () => string,
  ) {}

  public async resolve(
    record: ReturnSupervisorResolutionRecord,
  ): Promise<"declined" | "waiting"> {
    const actionId = strict(record.actionId, "return action id", 128);
    const finding = record.finding;
    if (finding !== "not-refunded" && finding !== "keep-waiting") {
      throw feature("RETURN_SUPERVISOR_RESOLUTION_UNSUPPORTED");
    }
    const evidence = strict(record.evidenceReference, "return resolution evidence", 256);
    const note = strict(record.note, "return resolution note", 1000);
    const authorizationId = strict(record.authorizationId, "return resolution authorization", 128);
    const supervisor = normalizeActor(record.supervisorActor);
    const requesting = normalizeActor(record.requestingActor);
    if (
      supervisor.cashierId === requesting.cashierId ||
      (supervisor.userGuid !== null && supervisor.userGuid === requesting.userGuid)
    ) {
      throw feature("RETURN_SUPERVISOR_REQUIRED");
    }
    const storeCode = strict(record.scope.storeCode, "return store code", 64);
    const deviceCode = strict(record.scope.deviceCode, "return device code", 128);
    const now = canonicalIso(this.nowIso());

    return this.connection.withExclusiveTransaction(async (transaction) => {
      const action = await transaction.getFirst<{
        state: unknown; store_code: unknown; device_code: unknown; return_order_guid: unknown;
      }>(
        `SELECT state, store_code, device_code, return_order_guid
         FROM return_actions WHERE action_id = ?`,
        [actionId],
      );
      if (
        !action ||
        action.store_code !== storeCode ||
        action.device_code !== deviceCode
      ) {
        throw feature("RETURN_RECOVERY_FAILED");
      }
      if (action.state !== "processing" && action.state !== "unknown") {
        // completed/declined 已有终局事实，主管结论不得改写。
        throw feature("RETURN_SUPERVISOR_RESOLUTION_UNSUPPORTED");
      }
      const allocations = await transaction.getAll<{
        allocation_id: unknown; status: unknown; execution_kind: unknown; method: unknown;
        external_attempt_kind: unknown; durable_attempt_id: unknown;
        capacity_reservation_state: unknown;
      }>(
        `SELECT allocation_id, status, execution_kind, method, external_attempt_kind,
           durable_attempt_id, capacity_reservation_state
         FROM return_action_allocations WHERE action_id = ?
         ORDER BY allocation_index`,
        [actionId],
      );
      const inDoubt = allocations.filter(
        (allocation) => allocation.status === "submitted" || allocation.status === "unknown",
      );
      // 只处理“恰有一笔在途刷卡退款”的 action；部分已完成（其他额度已退）或多笔在途
      // 无法安全整体作废，保持锁定交给后续人工处理。
      if (
        inDoubt.length !== 1 ||
        allocations.some((allocation) => allocation.status === "completed" || allocation.status === "declined")
      ) {
        throw feature("RETURN_SUPERVISOR_RESOLUTION_UNSUPPORTED");
      }
      const target = inDoubt[0]!;
      const attemptId = typeof target.durable_attempt_id === "string" ? target.durable_attempt_id : null;
      if (
        target.execution_kind !== "online-refund" ||
        target.method !== "card" ||
        target.external_attempt_kind !== "payment-provider" ||
        attemptId === null
      ) {
        throw feature("RETURN_SUPERVISOR_RESOLUTION_UNSUPPORTED");
      }
      const attempt = await transaction.getFirst<{
        provider: unknown; operation: unknown; state: unknown;
      }>(
        "SELECT provider, operation, state FROM payment_attempts WHERE attempt_id = ?",
        [attemptId],
      );
      // provider 已有终态（含迟到 Approved）时必须按真实结果走标准恢复，不能人工覆盖。
      if (
        !attempt ||
        attempt.operation !== "refund" ||
        (attempt.provider !== "linkly-cloud" && attempt.provider !== "square") ||
        !["Created", "Submitted", "Pending", "Unknown"].includes(String(attempt.state))
      ) {
        throw feature("RETURN_SUPERVISOR_RESOLUTION_UNSUPPORTED");
      }
      const allocationId = String(target.allocation_id);
      const resolutionId = strict(this.ids.createResolutionId(), "return resolution id", 128);
      await transaction.run(
        `INSERT INTO return_supervisor_resolutions (
           resolution_id, action_id, allocation_id, attempt_id, finding,
           evidence_reference, note, authorization_id,
           supervisor_actor_json, requesting_actor_json,
           attempt_state_snapshot, created_at_iso
         ) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)`,
        [
          resolutionId, actionId, allocationId, attemptId, finding, evidence, note,
          authorizationId, JSON.stringify(auditActorPayload(supervisor)),
          JSON.stringify(auditActorPayload(requesting)), String(attempt.state), now,
        ],
      );
      await transaction.run(
        `INSERT INTO audit_events (event_id, event_type, occurred_at_iso, order_guid,
           correlation_id, payload_json, uploaded_at_iso, delivery_state, attempt_count,
           next_attempt_at_iso, last_error_code, scope_store_code, scope_device_code)
         VALUES (?, ?, ?, ?, ?, ?, NULL, 'pending', 0, ?, NULL, ?, ?)`,
        [
          strict(this.ids.createAuditEventId(), "return resolution audit id", 128),
          finding === "not-refunded"
            ? "RETURN_REFUND_SUPERVISOR_NOT_REFUNDED"
            : "RETURN_REFUND_SUPERVISOR_KEEP_WAITING",
          now,
          String(action.return_order_guid),
          resolutionId,
          JSON.stringify({
            action: "return-refund-supervisor-resolution",
            finding,
            actionId,
            allocationId,
            attemptId,
            authorizationId,
            ...auditActorPayload(supervisor),
          }),
          now,
          storeCode,
          deviceCode,
        ],
      );
      if (finding === "keep-waiting") return "waiting" as const;

      const declined = await transaction.run(
        `UPDATE return_action_allocations
         SET status = 'declined', updated_at_iso = ?
         WHERE action_id = ? AND allocation_id = ? AND status IN ('submitted', 'unknown')`,
        [now, actionId, allocationId],
      );
      if (declined.changes !== 1) throw feature("RETURN_RECOVERY_FAILED");
      const voided = await transaction.run(
        `UPDATE return_actions
         SET state = 'declined', updated_at_iso = ?
         WHERE action_id = ? AND state IN ('processing', 'unknown')`,
        [now, actionId],
      );
      if (voided.changes !== 1) throw feature("RETURN_RECOVERY_FAILED");
      await transaction.run(
        `UPDATE return_action_allocations
         SET capacity_reservation_state = 'Released', updated_at_iso = ?
         WHERE action_id = ? AND capacity_reservation_state = 'Reserved'`,
        [now, actionId],
      );
      await transaction.run(
        `UPDATE return_line_capacity_reservations
         SET state = 'Released', updated_at_iso = ?
         WHERE action_id = ? AND state = 'Reserved'`,
        [now, actionId],
      );
      await enqueueSupervisorResolvedAckInTransaction(transaction, {
        attemptId,
        resolution: "not-refunded",
        sourceActionId: resolutionId,
        createdAtIso: now,
      });
      return "declined" as const;
    });
  }
}

function feature(code: ReturnErrorCode): ReturnFeatureError {
  return new ReturnFeatureError(code);
}

function normalizeActor(actor: AuditActorSnapshot): AuditActorSnapshot {
  const normalized = auditActorSnapshotFromPayload(auditActorPayload(actor));
  if (!normalized) throw feature("RETURN_SUPERVISOR_REQUIRED");
  return normalized;
}

function strict(value: unknown, label: string, max: number): string {
  if (typeof value !== "string") throw new TypeError(`${label} is invalid.`);
  const result = value.trim();
  if (!result || result.length > max || /[\x00-\x1f\x7f]/u.test(result)) {
    throw new TypeError(`${label} is invalid.`);
  }
  return result;
}

function canonicalIso(value: string): string {
  const ms = Date.parse(value);
  if (!Number.isFinite(ms) || new Date(ms).toISOString() !== value) {
    throw new TypeError("return resolution time is invalid.");
  }
  return value;
}
