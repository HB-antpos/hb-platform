import type { SqliteConnectionPort } from "./types";

/**
 * 主管结案后的 Linkly 会话确认（supervisorResolved ACK）耐久队列。
 *
 * 为什么单独一张表：人工结论（恢复中心 action、退货 allocation 结论）落库在先，
 * ACK 是对后端会话的网络副作用，可能失败或进程中途崩溃；队列行与人工结论同事务写入，
 * 之后可无限次重试，直到后端确认。队列只记录“哪个 attempt 需要补发 ACK”，不承载任何
 * 金融结论，也不改 payment_attempts 的状态。表结构 payment_supervisor_session_acks 由两端 App 的
 * migrations.ts（iPad M51 / 手持对应版本）各自持有。
 */
export type SupervisorAckResolution = "paid" | "unpaid" | "refunded" | "not-refunded";

export type SupervisorAckQueueEntry = Readonly<{
  attemptId: string;
  resolution: SupervisorAckResolution;
  sourceActionId: string;
  createdAtIso: string;
  attemptCount: number;
  lastErrorCode: string | null;
}>;

/**
 * 在调用方已有的事务内入队：只有本地仍未取得 provider 终态的 Linkly 会话才需要补发
 * supervisorResolved；已有 Approved/Declined/Cancelled 的 attempt 由既有终态 ACK 管线
 * 负责，Created 或缺少 SessionId 的 attempt 后端没有会话，同样无需入队。
 * 返回是否实际入队，重复调用幂等。
 */
export async function enqueueSupervisorResolvedAckInTransaction(
  transaction: SqliteConnectionPort,
  input: Readonly<{
    attemptId: string;
    resolution: SupervisorAckResolution;
    sourceActionId: string;
    createdAtIso: string;
  }>,
): Promise<boolean> {
  const attempt = await transaction.getFirst<{
    provider: unknown; state: unknown; session_id: unknown;
  }>(
    "SELECT provider, state, session_id FROM payment_attempts WHERE attempt_id = ?",
    [input.attemptId],
  );
  if (
    !attempt ||
    attempt.provider !== "linkly-cloud" ||
    typeof attempt.session_id !== "string" ||
    attempt.session_id.trim().length === 0 ||
    !["Submitted", "Pending", "Unknown"].includes(String(attempt.state))
  ) {
    return false;
  }
  const inserted = await transaction.run(
    `INSERT OR IGNORE INTO payment_supervisor_session_acks (
       attempt_id, resolution, source_action_id, created_at_iso
     ) VALUES (?, ?, ?, ?)`,
    [input.attemptId, input.resolution, input.sourceActionId, input.createdAtIso],
  );
  return inserted.changes === 1;
}

export class SqlitePaymentSupervisorAckQueue {
  public constructor(private readonly connection: SqliteConnectionPort) {}

  public async listPending(limit = 20): Promise<readonly SupervisorAckQueueEntry[]> {
    const rows = await this.connection.getAll<{
      attempt_id: unknown; resolution: unknown; source_action_id: unknown;
      created_at_iso: unknown; attempt_count: unknown; last_error_code: unknown;
    }>(
      `SELECT attempt_id, resolution, source_action_id, created_at_iso, attempt_count, last_error_code
       FROM payment_supervisor_session_acks
       WHERE acknowledged_at_iso IS NULL
       ORDER BY created_at_iso, attempt_id
       LIMIT ?`,
      [Math.max(1, Math.min(100, Math.trunc(limit)))],
    );
    return Object.freeze(rows.map((row) => Object.freeze({
      attemptId: String(row.attempt_id),
      resolution: row.resolution as SupervisorAckResolution,
      sourceActionId: String(row.source_action_id),
      createdAtIso: String(row.created_at_iso),
      attemptCount: Number(row.attempt_count),
      lastErrorCode: typeof row.last_error_code === "string" ? row.last_error_code : null,
    })));
  }

  /** 后端已确认（或已确认无需确认）后写入标记；只会从未确认变为已确认一次。 */
  public async markAcknowledged(
    attemptId: string,
    acknowledgedAtIso: string,
  ): Promise<boolean> {
    const changed = await this.connection.run(
      `UPDATE payment_supervisor_session_acks
       SET acknowledged_at_iso = ?, last_attempt_at_iso = ?, last_error_code = NULL,
           attempt_count = attempt_count + 1
       WHERE attempt_id = ? AND acknowledged_at_iso IS NULL`,
      [acknowledgedAtIso, acknowledgedAtIso, attemptId],
    );
    return changed.changes === 1;
  }

  public async recordFailure(
    attemptId: string,
    errorCode: string,
    attemptedAtIso: string,
  ): Promise<void> {
    await this.connection.run(
      `UPDATE payment_supervisor_session_acks
       SET attempt_count = attempt_count + 1, last_attempt_at_iso = ?, last_error_code = ?
       WHERE attempt_id = ? AND acknowledged_at_iso IS NULL`,
      [attemptedAtIso, errorCode.slice(0, 128), attemptId],
    );
  }
}
