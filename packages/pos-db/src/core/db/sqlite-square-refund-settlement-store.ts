import {
  auditActorPayload,
  type AuditActorSnapshot,
} from "@hb/pos-domain/core/contracts/audit-actor";

import { decryptPaymentProtectedMaterial } from "./sqlite-payment-protected-material";
import type { SqliteConnectionPort } from "./types";

/**
 * Square 退款结算跟踪（M53）。退货单在 Square 返回 PENDING 时已当场完成；这里只记录同一笔退款
 * 之后的结算结果，绝不改动 payment attempt 或退货账本。
 */
export type SquareRefundSettlementStatus =
  | "settling"
  | "settled"
  | "rejected"
  | "refunded-otherwise";

export type SquareRefundSettlementScope = Readonly<{
  storeCode: string;
  deviceCode: string;
}>;

export type SquareRefundSettlementRecord = Readonly<{
  attemptId: string;
  returnOrderGuid: string;
  storeCode: string;
  deviceCode: string;
  providerEnvironment: string | null;
  amountCents: number;
  status: SquareRefundSettlementStatus;
  squareStatus: string | null;
  lastCheckedAtIso: string | null;
  lastErrorCode: string | null;
  resolutionNote: string | null;
  resolvedAtIso: string | null;
  registeredAtIso: string;
}>;

/** 后台检查的结论：只有 Square 明确的终态才推进状态，其余只记录检查时间或错误码。 */
export type SquareRefundSettlementObservation =
  | Readonly<{ kind: "settled"; squareStatus: string }>
  | Readonly<{ kind: "rejected"; squareStatus: string }>
  | Readonly<{ kind: "still-settling"; squareStatus: string }>
  | Readonly<{ kind: "check-failed"; errorCode: string }>;

export type ResolveRejectedSquareRefundInput = SquareRefundSettlementScope & Readonly<{
  attemptId: string;
  note: string;
  actor: AuditActorSnapshot;
}>;

type SettlementRow = Readonly<{
  attempt_id: unknown;
  order_guid: unknown;
  store_code: unknown;
  device_code: unknown;
  provider_environment: unknown;
  amount_cents: unknown;
  status: unknown;
  square_status: unknown;
  last_checked_at_iso: unknown;
  last_error_code: unknown;
  resolution_note: unknown;
  resolved_at_iso: unknown;
  registered_at_iso: unknown;
}>;

type Decryptor = Readonly<{ decrypt(ciphertext: Uint8Array): Promise<string> }>;

const SELECT_COLUMNS = `attempt_id, order_guid, store_code, device_code, provider_environment,
  amount_cents, status, square_status, last_checked_at_iso, last_error_code,
  resolution_note, resolved_at_iso, registered_at_iso`;

export class SqliteSquareRefundSettlementStore {
  public constructor(
    private readonly connection: SqliteConnectionPort,
    private readonly decryptor: Decryptor,
    private readonly nowIso: () => string,
  ) {}

  /** 本设备的结算记录，未结案的（结算中 / 被拒）排在前面。 */
  public async list(
    scope: SquareRefundSettlementScope,
  ): Promise<readonly SquareRefundSettlementRecord[]> {
    const rows = await this.connection.getAll<SettlementRow>(
      `SELECT ${SELECT_COLUMNS}
       FROM square_refund_settlements
       WHERE store_code = ? AND device_code = ?
       ORDER BY CASE status WHEN 'rejected' THEN 0 WHEN 'settling' THEN 1 ELSE 2 END,
         registered_at_iso DESC, attempt_id DESC
       LIMIT 200`,
      [scopeText(scope.storeCode), scopeText(scope.deviceCode)],
    );
    return rows.map(toRecord);
  }

  /** 后台检查只取仍在结算中的记录。 */
  public async listSettling(
    scope: SquareRefundSettlementScope,
  ): Promise<readonly SquareRefundSettlementRecord[]> {
    const rows = await this.connection.getAll<SettlementRow>(
      `SELECT ${SELECT_COLUMNS}
       FROM square_refund_settlements
       WHERE store_code = ? AND device_code = ? AND status = 'settling'
       ORDER BY registered_at_iso ASC, attempt_id ASC
       LIMIT 50`,
      [scopeText(scope.storeCode), scopeText(scope.deviceCode)],
    );
    return rows.map(toRecord);
  }

  /** 被拒且未由主管结案的数量，供恢复中心角标使用。 */
  public async countUnresolvedRejected(scope: SquareRefundSettlementScope): Promise<number> {
    const row = await this.connection.getFirst<{ count: unknown }>(
      `SELECT COUNT(*) AS count FROM square_refund_settlements
       WHERE store_code = ? AND device_code = ? AND status = 'rejected'`,
      [scopeText(scope.storeCode), scopeText(scope.deviceCode)],
    );
    return Number(row?.count ?? 0);
  }

  /**
   * 退款号只在受保护的卡证据里（refundReference），按原 attempt 精确读取并解密；
   * 证据缺失或不是同一笔 Square 退款时返回 null，由调用方记为检查失败。
   */
  public async readRefundId(attemptId: string): Promise<string | null> {
    const row = await this.connection.getFirst<{
      provider: unknown;
      operation: unknown;
      state: unknown;
      provider_payload_ciphertext: unknown;
    }>(
      `SELECT p.provider, p.operation, p.state, p.provider_payload_ciphertext
       FROM payment_attempts p
       INNER JOIN square_refund_settlements s ON s.attempt_id = p.attempt_id
       WHERE p.attempt_id = ?`,
      [scopeText(attemptId)],
    );
    if (!row || row.provider !== "square" || row.operation !== "refund" || row.state !== "Approved") {
      return null;
    }
    const ciphertext = row.provider_payload_ciphertext;
    if (!(ciphertext instanceof Uint8Array)) return null;
    const material = await decryptPaymentProtectedMaterial(this.decryptor, ciphertext);
    const evidence = material.cardSyncEvidence;
    if (!evidence || evidence.provider !== "square" || evidence.operation !== "refund") return null;
    return evidence.refundReference;
  }

  /** 按检查结论推进；只在记录仍为 settling 时生效（CAS），并发检查不会互相覆盖终态。 */
  public async recordObservation(
    attemptId: string,
    observation: SquareRefundSettlementObservation,
  ): Promise<boolean> {
    const nowIso = this.nowIso();
    const id = scopeText(attemptId);
    if (observation.kind === "check-failed") {
      const changed = await this.connection.run(
        `UPDATE square_refund_settlements
         SET last_checked_at_iso = ?, last_error_code = ?, updated_at_iso = ?
         WHERE attempt_id = ? AND status = 'settling'`,
        [nowIso, limitCode(observation.errorCode), nowIso, id],
      );
      return changed.changes === 1;
    }
    const nextStatus = observation.kind === "settled"
      ? "settled"
      : observation.kind === "rejected" ? "rejected" : "settling";
    const changed = await this.connection.run(
      `UPDATE square_refund_settlements
       SET status = ?, square_status = ?, last_checked_at_iso = ?, last_error_code = NULL, updated_at_iso = ?
       WHERE attempt_id = ? AND status = 'settling'`,
      [nextStatus, limitCode(observation.squareStatus), nowIso, nowIso, id],
    );
    return changed.changes === 1;
  }

  /** 主管确认已改用其他方式把被拒的退款退给顾客；只改变本地结算状态，不调用 Square。 */
  public async resolveRejected(input: ResolveRejectedSquareRefundInput): Promise<SquareRefundSettlementRecord> {
    const note = normalizeNote(input.note);
    const nowIso = this.nowIso();
    return this.connection.withExclusiveTransaction(async (transaction) => {
      const changed = await transaction.run(
        `UPDATE square_refund_settlements
         SET status = 'refunded-otherwise', resolution_note = ?, resolution_actor_json = ?,
           resolved_at_iso = ?, updated_at_iso = ?
         WHERE attempt_id = ? AND store_code = ? AND device_code = ? AND status = 'rejected'`,
        [
          note,
          JSON.stringify(auditActorPayload(input.actor)),
          nowIso,
          nowIso,
          scopeText(input.attemptId),
          scopeText(input.storeCode),
          scopeText(input.deviceCode),
        ],
      );
      if (changed.changes !== 1) throw new Error("SQUARE_REFUND_SETTLEMENT_NOT_REJECTED");
      const row = await transaction.getFirst<SettlementRow>(
        `SELECT ${SELECT_COLUMNS} FROM square_refund_settlements WHERE attempt_id = ?`,
        [scopeText(input.attemptId)],
      );
      if (!row) throw new Error("SQUARE_REFUND_SETTLEMENT_NOT_FOUND");
      return toRecord(row);
    });
  }
}

function toRecord(row: SettlementRow): SquareRefundSettlementRecord {
  return Object.freeze({
    attemptId: requiredText(row.attempt_id),
    returnOrderGuid: requiredText(row.order_guid),
    storeCode: requiredText(row.store_code),
    deviceCode: requiredText(row.device_code),
    providerEnvironment: optionalText(row.provider_environment),
    amountCents: Number(row.amount_cents),
    status: settlementStatus(row.status),
    squareStatus: optionalText(row.square_status),
    lastCheckedAtIso: optionalText(row.last_checked_at_iso),
    lastErrorCode: optionalText(row.last_error_code),
    resolutionNote: optionalText(row.resolution_note),
    resolvedAtIso: optionalText(row.resolved_at_iso),
    registeredAtIso: requiredText(row.registered_at_iso),
  });
}

function settlementStatus(value: unknown): SquareRefundSettlementStatus {
  if (value === "settling" || value === "settled" || value === "rejected" || value === "refunded-otherwise") {
    return value;
  }
  throw new Error("SQUARE_REFUND_SETTLEMENT_STATUS_INVALID");
}

function normalizeNote(value: string): string {
  const note = value.replace(/[\r\n\t]+/gu, " ").trim();
  if (!note || note.length > 1000 || /[\u0000-\u001f\u007f]/u.test(note)) {
    throw new Error("SQUARE_REFUND_SETTLEMENT_NOTE_REQUIRED");
  }
  return note;
}

function limitCode(value: string): string {
  const text = value.trim();
  return text.length <= 80 ? text : text.slice(0, 80);
}

function scopeText(value: string): string {
  const text = value.trim();
  if (!text || text.length > 128) throw new Error("SQUARE_REFUND_SETTLEMENT_SCOPE_INVALID");
  return text;
}

function requiredText(value: unknown): string {
  if (typeof value !== "string" || !value.trim()) throw new Error("SQUARE_REFUND_SETTLEMENT_ROW_INVALID");
  return value;
}

function optionalText(value: unknown): string | null {
  return typeof value === "string" && value.trim() ? value : null;
}
