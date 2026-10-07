import type { DailyCloseArchive } from "@hb/pos-domain/core/contracts/daily-close";
import {
  DAILY_CLOSE_UPLOAD_LEASE_SECONDS,
  type DailyCloseUploadLease,
  type DailyCloseUploadRepositoryPort,
  type DailyCloseUploadScope,
} from "@hb/pos-domain/core/contracts/daily-close-upload";

import { SqliteDailyCloseRepository } from "./sqlite-daily-close-repository";
import type { SqliteConnectionPort } from "./types";

const MAXIMUM_ERROR_CODE_LENGTH = 128;
const MAXIMUM_ERROR_MESSAGE_LENGTH = 512;
const MAXIMUM_BATCH_SIZE = 100;

type CloseIdRow = Readonly<{ close_id: unknown }>;
type AttemptCountRow = Readonly<{ attempt_count: unknown }>;
type NextReadyRow = Readonly<{ next_ready_at_iso: unknown }>;

/**
 * 日结记录上传 outbox 的 SQLite 实现（状态列由 M49/M50 补在 local_daily_closes 上）。
 * 状态流转：pending →(认领)→ uploading →(成功)→ synced；失败回 pending 带退避；
 * 永久拒绝 rejected；非 GUID 的遗留 closeId skipped。
 *
 * 上传状态列不在存档不可变触发器的列清单里，所以这里的 UPDATE 不会碰存档事实；
 * 日结行仍然禁止 DELETE，历史补传全靠这些状态列驱动。
 */
export class SqliteDailyCloseUploadRepository
  implements DailyCloseUploadRepositoryPort
{
  private readonly archives: SqliteDailyCloseRepository;

  public constructor(private readonly connection: SqliteConnectionPort) {
    this.archives = new SqliteDailyCloseRepository(connection);
  }

  public async recoverExpiredUploading(
    input: Readonly<{ staleBeforeIso: string; nextAttemptAtIso: string }>,
  ): Promise<number> {
    // 进程中途退出会遗留 uploading；last_attempt 不晚于 staleBefore 的退回 pending 重新排队。
    const result = await this.connection.run(
      `UPDATE local_daily_closes
       SET upload_state = 'pending',
           upload_next_attempt_at_iso = ?,
           upload_error_code = 'UPLOAD_LEASE_EXPIRED',
           upload_error_message = 'The previous upload lease expired and was queued for retry.'
       WHERE upload_state = 'uploading'
         AND (upload_last_attempt_at_iso IS NULL OR upload_last_attempt_at_iso <= ?)`,
      [input.nextAttemptAtIso, input.staleBeforeIso],
    );
    return result.changes;
  }

  public async listDue(
    scope: DailyCloseUploadScope,
    limit: number,
    nowIso: string,
  ): Promise<readonly string[]> {
    // 只取当前设备授权范围内的行：别的范围（例如设备换绑前的旧记录）保持 pending 不动，
    // 避免它们被服务端 403 后阻塞整个批次。next 为 NULL 表示立即到期：新保存的日结与升级后的历史行都是这个状态。
    const rows = await this.connection.getAll<CloseIdRow>(
      `SELECT close_id
       FROM local_daily_closes
       WHERE upload_state = 'pending'
         AND state = 'Archived'
         AND store_code = ?
         AND device_code = ?
         AND (upload_next_attempt_at_iso IS NULL OR upload_next_attempt_at_iso <= ?)
       ORDER BY COALESCE(upload_next_attempt_at_iso, ''),
                julianday(saved_at_iso),
                close_id
       LIMIT ?`,
      [
        scope.storeCode,
        scope.deviceCode,
        nowIso,
        Math.min(Math.max(Math.trunc(limit), 1), MAXIMUM_BATCH_SIZE),
      ],
    );
    return rows.flatMap((row) =>
      typeof row.close_id === "string" ? [row.close_id] : [],
    );
  }

  public async tryClaim(
    closeId: string,
    scope: DailyCloseUploadScope,
    attemptedAtIso: string,
  ): Promise<DailyCloseUploadLease | null> {
    // 关键逻辑：认领是单条 UPDATE ... RETURNING，条件（pending + 已到期 + 范围一致 + 已归档）
    // 与状态变更在同一条语句里原子完成；并发认领同一条记录时只有一个能把 pending 改成 uploading，其余拿到 null。
    const row = await this.connection.getFirst<AttemptCountRow>(
      `UPDATE local_daily_closes
       SET upload_state = 'uploading',
           upload_attempt_count = upload_attempt_count + 1,
           upload_last_attempt_at_iso = ?,
           upload_next_attempt_at_iso = NULL,
           upload_error_code = NULL,
           upload_error_message = NULL
       WHERE close_id = ?
         AND state = 'Archived'
         AND store_code = ?
         AND device_code = ?
         AND upload_state = 'pending'
         AND (upload_next_attempt_at_iso IS NULL OR upload_next_attempt_at_iso <= ?)
       RETURNING upload_attempt_count AS attempt_count`,
      [attemptedAtIso, closeId, scope.storeCode, scope.deviceCode, attemptedAtIso],
    );
    if (!row) return null;
    const attemptCount = Number(row.attempt_count);
    if (!Number.isSafeInteger(attemptCount) || attemptCount < 1) {
      throw new Error("Daily close upload attempt count is invalid.");
    }
    return Object.freeze({ closeId, attemptCount });
  }

  public readArchive(closeId: string): Promise<DailyCloseArchive | null> {
    // 复用既有归档读取（含 11 档面额校验），不在这里“修正”任何本地金额。
    return this.archives.getArchive(closeId);
  }

  public async markSucceeded(
    closeId: string,
    uploadedAtIso: string,
  ): Promise<void> {
    // 不限定 uploading：服务端已确认收下，即使租约恰好被回收成 pending 也应记为已同步，避免重复上传。
    await this.connection.run(
      `UPDATE local_daily_closes
       SET upload_state = 'synced',
           upload_next_attempt_at_iso = NULL,
           upload_error_code = NULL,
           upload_error_message = NULL,
           uploaded_at_iso = ?
       WHERE close_id = ?
         AND upload_state IN ('pending', 'uploading')`,
      [uploadedAtIso, closeId],
    );
  }

  public async markPending(
    closeId: string,
    nextAttemptAtIso: string,
    errorCode: string,
    errorMessage: string,
  ): Promise<void> {
    // 只有持有租约（uploading）的一方才能改状态，避免旧请求的迟到结果覆盖已被别处接管的记录。
    await this.connection.run(
      `UPDATE local_daily_closes
       SET upload_state = 'pending',
           upload_next_attempt_at_iso = ?,
           upload_error_code = ?,
           upload_error_message = ?
       WHERE close_id = ?
         AND upload_state = 'uploading'`,
      [
        nextAttemptAtIso,
        boundedCode(errorCode),
        boundedMessage(errorMessage),
        closeId,
      ],
    );
  }

  public async releaseWithoutAttempt(
    closeId: string,
    nextAttemptAtIso: string,
    errorCode: string,
    errorMessage: string,
  ): Promise<void> {
    // 设备授权问题不是这条记录自己的失败：退回 pending 并撤销认领时累加的尝试次数，不烧掉它的尝试次数/退避。
    await this.connection.run(
      `UPDATE local_daily_closes
       SET upload_state = 'pending',
           upload_attempt_count = CASE
             WHEN upload_attempt_count > 0 THEN upload_attempt_count - 1
             ELSE 0
           END,
           upload_next_attempt_at_iso = ?,
           upload_error_code = ?,
           upload_error_message = ?
       WHERE close_id = ?
         AND upload_state = 'uploading'`,
      [
        nextAttemptAtIso,
        boundedCode(errorCode),
        boundedMessage(errorMessage),
        closeId,
      ],
    );
  }

  public async markRejected(
    closeId: string,
    errorCode: string,
    errorMessage: string,
  ): Promise<void> {
    await this.connection.run(
      `UPDATE local_daily_closes
       SET upload_state = 'rejected',
           upload_next_attempt_at_iso = NULL,
           upload_error_code = ?,
           upload_error_message = ?
       WHERE close_id = ?
         AND upload_state = 'uploading'`,
      [boundedCode(errorCode), boundedMessage(errorMessage), closeId],
    );
  }

  public async markSkipped(
    closeId: string,
    errorCode: string,
    errorMessage: string,
  ): Promise<void> {
    await this.connection.run(
      `UPDATE local_daily_closes
       SET upload_state = 'skipped',
           upload_next_attempt_at_iso = NULL,
           upload_error_code = ?,
           upload_error_message = ?
       WHERE close_id = ?
         AND upload_state IN ('pending', 'uploading')`,
      [boundedCode(errorCode), boundedMessage(errorMessage), closeId],
    );
  }

  public async nextReadyAtIso(
    scope: DailyCloseUploadScope,
  ): Promise<string | null> {
    // 只看带退避时间的 pending：没有 next 时间的 pending 是“立即到期”，
    // 由保存后唤醒与启动/前台/联网触发处理，不能让调度器据此 0ms 自旋。
    const retry = await this.connection.getFirst<NextReadyRow>(
      `SELECT MIN(upload_next_attempt_at_iso) AS next_ready_at_iso
       FROM local_daily_closes
       WHERE upload_state = 'pending'
         AND state = 'Archived'
         AND store_code = ?
         AND device_code = ?
         AND upload_next_attempt_at_iso IS NOT NULL`,
      [scope.storeCode, scope.deviceCode],
    );
    // 崩溃遗留的 uploading 在租约到期后才会被回收，也要有一次定时唤醒。
    const stale = await this.connection.getFirst<NextReadyRow>(
      `SELECT MIN(upload_last_attempt_at_iso) AS next_ready_at_iso
       FROM local_daily_closes
       WHERE upload_state = 'uploading'`,
    );
    const candidates: number[] = [];
    if (typeof retry?.next_ready_at_iso === "string") {
      candidates.push(Date.parse(retry.next_ready_at_iso));
    }
    if (typeof stale?.next_ready_at_iso === "string") {
      candidates.push(
        Date.parse(stale.next_ready_at_iso) +
          DAILY_CLOSE_UPLOAD_LEASE_SECONDS * 1_000,
      );
    }
    const earliest = Math.min(
      ...candidates.filter((value) => Number.isFinite(value)),
    );
    return Number.isFinite(earliest) ? new Date(earliest).toISOString() : null;
  }
}

function boundedCode(value: string): string {
  return value.slice(0, MAXIMUM_ERROR_CODE_LENGTH);
}

function boundedMessage(value: string): string {
  return value.slice(0, MAXIMUM_ERROR_MESSAGE_LENGTH);
}
