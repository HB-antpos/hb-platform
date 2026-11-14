import {
  CASH_MOVEMENT_NATURAL_DIRECTIONS,
  CASH_MOVEMENT_UPLOAD_STATES,
  CashMovementRepositoryError,
  buildCashMovementAudit,
  canVoidCashMovement,
  formatCashMovementDisplayNumber,
  isCashMovementGuid,
  isCashMovementReasonCode,
  oppositeCashMovementDirection,
  summarizeCashMovements,
  validateCashMovementInput,
  type CashMovementCommit,
  type CashMovementDirection,
  type CashMovementRepositoryPort,
  type CashMovementSummary,
  type CashMovementUploadState,
  type LocalCashMovement,
} from "@hb/pos-domain/core/contracts/cash-movement";
import {
  CASH_MOVEMENT_UPLOAD_LEASE_SECONDS,
  type CashMovementClaimResult,
  type CashMovementUploadRepositoryPort,
  type CashMovementUploadScope,
  type ClaimedCashMovement,
} from "@hb/pos-domain/core/contracts/cash-movement-upload";
import type { DailyCloseScope } from "@hb/pos-domain/core/contracts/daily-close";

import type { SqliteConnectionPort } from "./types";

const MAXIMUM_ERROR_CODE_LENGTH = 128;
const MAXIMUM_ERROR_MESSAGE_LENGTH = 512;
const MAXIMUM_CLAIM_BATCH_SIZE = 100;

type MovementRow = Readonly<{
  movement_guid: unknown;
  direction: unknown;
  reason_code: unknown;
  amount_cents: unknown;
  note: unknown;
  voids_movement_guid: unknown;
  business_date: unknown;
  occurred_at_iso: unknown;
  cashier_id: unknown;
  cashier_name: unknown;
  authorized_by_id: unknown;
  authorized_by_name: unknown;
  display_number: unknown;
  store_code: unknown;
  device_code: unknown;
  audit_event_id: unknown;
  upload_state: unknown;
  upload_attempts: unknown;
  upload_error_code: unknown;
  uploaded_at_iso: unknown;
  created_at_iso: unknown;
  voided_by_movement_guid: unknown;
}>;
type CountRow = Readonly<{ count: unknown }>;
type SavedAtRow = Readonly<{ saved_at_iso: unknown }>;
type SummaryRow = Readonly<{
  direction: unknown;
  reason_code: unknown;
  amount_cents: unknown;
}>;
type NextReadyRow = Readonly<{ next_ready_at_iso: unknown }>;
type AttemptRow = Readonly<{ upload_attempts: unknown }>;

/** 读取一条记录时同时带出「被哪条冲正记录指向」。 */
const SELECT_MOVEMENT = `
  SELECT movement.*, voider.movement_guid AS voided_by_movement_guid
  FROM local_cash_movements movement
  LEFT JOIN local_cash_movements voider
    ON voider.voids_movement_guid = movement.movement_guid`;

/**
 * 现金进出明细的汇总（供日结汇总与 summarizePeriod 共用，保证两处口径完全一致）：
 * 只看 store + device + occurred_at 落在 [periodFrom, periodTo)，不看上传状态、不看任何开关。
 * connection 可以是事务连接，调用方负责让它与同一快照里的其它汇总一起读。
 */
export async function summarizeCashMovementsInPeriod(
  connection: SqliteConnectionPort,
  scope: DailyCloseScope,
): Promise<CashMovementSummary> {
  const rows = await connection.getAll<SummaryRow>(
    `SELECT direction, reason_code, amount_cents
     FROM local_cash_movements
     WHERE store_code = ?
       AND device_code = ?
       AND julianday(occurred_at_iso) >= julianday(?)
       AND julianday(occurred_at_iso) < julianday(?)`,
    [scope.storeCode, scope.deviceCode, scope.periodFromIso, scope.periodToIso],
  );
  return summarizeCashMovements(
    rows.map((row) => ({
      direction: movementDirection(row.direction),
      reasonCode: reasonCode(row.reason_code),
      amountCents: safeInteger(row.amount_cents, "cash movement amount"),
    })),
  );
}

/**
 * 现金进出的 SQLite 仓储。写入是单事务：流水号、记录、审计事件一起提交，
 * 任何一步失败（含审计失败）整体回滚，不会留下没有审计的记录或没有记录的审计。
 */
export class SqliteCashMovementRepository implements CashMovementRepositoryPort {
  public constructor(private readonly connection: SqliteConnectionPort) {}

  public async append(
    commit: CashMovementCommit,
  ): Promise<Readonly<{ replayed: boolean; movement: LocalCashMovement }>> {
    const checked = validateCommit(commit);
    return this.connection.withExclusiveTransaction(async (transaction) => {
      const { movement, audit } = checked;

      // 幂等重放：同一 movementGuid 已入库时，事实与审计必须逐字一致，否则失败关闭。
      const existing = await readMovement(transaction, movement.movementGuid);
      if (existing) {
        const existingAudit = await readAuditJson(transaction, existing.auditEventId);
        if (
          stableJson(factOf(existing)) !== stableJson(movement) ||
          existingAudit !== stableJson(audit)
        ) {
          throw new CashMovementRepositoryError("REPLAY_MISMATCH");
        }
        return Object.freeze({ replayed: true, movement: existing });
      }

      if (movement.voidsMovementGuid !== null) {
        await assertVoidAllowed(transaction, movement);
      }

      // 流水号：本库全部记录数 + 1（含冲正），不按日重置。事务独占，不会并发撞号；display_number 另有 UNIQUE 兜底。
      const countRow = await transaction.getFirst<CountRow>(
        "SELECT COUNT(*) AS count FROM local_cash_movements",
      );
      const displayNumber = formatCashMovementDisplayNumber(
        safeInteger(countRow?.count, "cash movement count") + 1,
      );

      await transaction.run(
        `INSERT INTO audit_events (
          event_id, event_type, occurred_at_iso, order_guid, correlation_id,
          payload_json, uploaded_at_iso, delivery_state, attempt_count,
          next_attempt_at_iso, last_error_code, scope_store_code, scope_device_code
        ) VALUES (?, ?, ?, NULL, ?, ?, NULL, 'pending', 0, ?, NULL, ?, ?)`,
        [
          audit.eventId,
          audit.eventType,
          audit.occurredAtIso,
          audit.correlationId,
          stableJson(audit.payload),
          audit.occurredAtIso,
          movement.storeCode,
          movement.deviceCode,
        ],
      );
      try {
        await transaction.run(
          `INSERT INTO local_cash_movements (
            movement_guid, direction, reason_code, amount_cents, note,
            voids_movement_guid, business_date, occurred_at_iso,
            cashier_id, cashier_name, authorized_by_id, authorized_by_name,
            display_number, store_code, device_code, audit_event_id,
            upload_state, upload_attempts, created_at_iso
          ) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, 'pending', 0, ?)`,
          [
            movement.movementGuid,
            movement.direction,
            movement.reasonCode,
            movement.amountCents,
            movement.note,
            movement.voidsMovementGuid,
            movement.businessDate,
            movement.occurredAtIso,
            movement.cashierId,
            movement.cashierName,
            movement.authorizedById,
            movement.authorizedByName,
            displayNumber,
            movement.storeCode,
            movement.deviceCode,
            audit.eventId,
            movement.occurredAtIso,
          ],
        );
      } catch (error) {
        // VoidsMovementGuid 唯一索引兜底：并发或绕过应用层检查时，同一原记录第二次冲正在这里被拒。
        if (
          error instanceof Error &&
          /voids_movement_guid/iu.test(error.message) &&
          /unique/iu.test(error.message)
        ) {
          throw new CashMovementRepositoryError("VOID_NOT_ALLOWED", "ALREADY_VOIDED");
        }
        throw error;
      }
      const persisted = await readMovement(transaction, movement.movementGuid);
      if (!persisted) {
        throw new Error("Cash movement disappeared during save.");
      }
      return Object.freeze({ replayed: false, movement: persisted });
    });
  }

  public getByGuid(movementGuid: string): Promise<LocalCashMovement | null> {
    return readMovement(this.connection, movementGuid);
  }

  public async listToday(scope: DailyCloseScope): Promise<readonly LocalCashMovement[]> {
    const normalized = validatePeriodScope(scope);
    const rows = await this.connection.getAll<MovementRow>(
      `${SELECT_MOVEMENT}
       WHERE movement.store_code = ?
         AND movement.device_code = ?
         AND julianday(movement.occurred_at_iso) >= julianday(?)
         AND julianday(movement.occurred_at_iso) < julianday(?)
       ORDER BY julianday(movement.occurred_at_iso), movement.created_at_iso, movement.rowid`,
      [
        normalized.storeCode,
        normalized.deviceCode,
        normalized.periodFromIso,
        normalized.periodToIso,
      ],
    );
    return Object.freeze(rows.map(readMovementRow));
  }

  public summarizePeriod(scope: DailyCloseScope): Promise<CashMovementSummary> {
    return summarizeCashMovementsInPeriod(this.connection, validatePeriodScope(scope));
  }

  public async latestDailyCloseSavedAtIso(scope: DailyCloseScope): Promise<string | null> {
    const normalized = validatePeriodScope(scope);
    return latestDailyCloseSavedAt(
      this.connection,
      normalized.storeCode,
      normalized.deviceCode,
      normalized.businessDate,
    );
  }
}

/**
 * 现金进出上传 outbox 的 SQLite 实现（状态列在 local_cash_movements 上，不在不可变触发器的列清单里）。
 * pending →(认领)→ uploading（租约）→ synced / rejected；重试类失败回 pending 带退避。
 * 所有时间都由调用方以 UTC ISO 毫秒串传入，SQL 里按字符串比较“是否到期”。
 */
export class SqliteCashMovementUploadRepository
  implements CashMovementUploadRepositoryPort
{
  public constructor(private readonly connection: SqliteConnectionPort) {}

  public async recoverExpiredUploading(
    input: Readonly<{ nowIso: string; nextAttemptAtIso: string }>,
  ): Promise<number> {
    // 进程中途退出会遗留 uploading；租约到期的退回 pending 重新排队。
    const result = await this.connection.run(
      `UPDATE local_cash_movements
       SET upload_state = 'pending',
           next_attempt_at_iso = ?,
           lease_expires_at_iso = NULL,
           upload_error_code = 'UPLOAD_LEASE_EXPIRED',
           upload_error_message = 'The previous upload lease expired and was queued for retry.'
       WHERE upload_state = 'uploading'
         AND (lease_expires_at_iso IS NULL OR lease_expires_at_iso <= ?)`,
      [input.nextAttemptAtIso, input.nowIso],
    );
    return result.changes;
  }

  public claimDueBatch(
    scope: CashMovementUploadScope,
    limit: number,
    nowIso: string,
  ): Promise<CashMovementClaimResult> {
    const batchSize = Math.min(Math.max(Math.trunc(limit), 1), MAXIMUM_CLAIM_BATCH_SIZE);
    const leaseExpiresAtIso = new Date(
      Date.parse(nowIso) + CASH_MOVEMENT_UPLOAD_LEASE_SECONDS * 1_000,
    ).toISOString();
    return this.connection.withExclusiveTransaction(async (transaction) => {
      // 关键逻辑：按 occurred_at 升序取到期的 pending。冲正记录（voids 非空）要等原记录已 synced 才能上传
      // （服务端要求原记录先到，否则 VOIDED_MOVEMENT_NOT_FOUND）；原记录若同样 pending 且已到期，
      // 因为它的发生时间不晚于冲正，必然排在同一批更靠前的位置（要么同批、要么都不在批里）；
      // 原记录已 rejected 的冲正永远传不上去，直接本地 rejected。
      const candidates = await transaction.getAll<
        MovementRow & Readonly<{ original_upload_state: unknown }>
      >(
        `SELECT movement.*, NULL AS voided_by_movement_guid,
                original.upload_state AS original_upload_state
         FROM local_cash_movements movement
         LEFT JOIN local_cash_movements original
           ON original.movement_guid = movement.voids_movement_guid
         WHERE movement.upload_state = 'pending'
           AND movement.store_code = ?
           AND movement.device_code = ?
           AND (movement.next_attempt_at_iso IS NULL OR movement.next_attempt_at_iso <= ?)
           AND (
             movement.voids_movement_guid IS NULL
             OR original.upload_state IN ('synced', 'rejected')
             OR (
               original.upload_state = 'pending'
               AND (original.next_attempt_at_iso IS NULL OR original.next_attempt_at_iso <= ?)
             )
           )
         ORDER BY julianday(movement.occurred_at_iso), movement.created_at_iso, movement.rowid
         LIMIT ?`,
        [scope.storeCode, scope.deviceCode, nowIso, nowIso, batchSize],
      );
      const claimed: ClaimedCashMovement[] = [];
      let rejectedLocally = 0;
      for (const candidate of candidates) {
        const guid = requiredText(candidate.movement_guid, "cash movement guid");
        if (candidate.original_upload_state === "rejected") {
          const rejected = await transaction.run(
            `UPDATE local_cash_movements
             SET upload_state = 'rejected',
                 next_attempt_at_iso = NULL,
                 lease_expires_at_iso = NULL,
                 upload_error_code = 'VOIDED_MOVEMENT_REJECTED',
                 upload_error_message = 'The voided movement was permanently rejected, so this void cannot be uploaded.'
             WHERE movement_guid = ? AND upload_state = 'pending'`,
            [guid],
          );
          rejectedLocally += rejected.changes;
          continue;
        }
        // 认领是单条 UPDATE ... RETURNING：条件与状态变更在同一语句里原子完成。
        const row = await transaction.getFirst<AttemptRow>(
          `UPDATE local_cash_movements
           SET upload_state = 'uploading',
               upload_attempts = upload_attempts + 1,
               last_attempt_at_iso = ?,
               lease_expires_at_iso = ?,
               next_attempt_at_iso = NULL,
               upload_error_code = NULL,
               upload_error_message = NULL
           WHERE movement_guid = ?
             AND upload_state = 'pending'
             AND store_code = ?
             AND device_code = ?
             AND (next_attempt_at_iso IS NULL OR next_attempt_at_iso <= ?)
           RETURNING upload_attempts`,
          [nowIso, leaseExpiresAtIso, guid, scope.storeCode, scope.deviceCode, nowIso],
        );
        if (!row) continue;
        const attemptCount = safeInteger(row.upload_attempts, "cash movement attempts");
        if (attemptCount < 1) {
          throw new Error("Cash movement upload attempt count is invalid.");
        }
        const movement = await readMovement(transaction, guid);
        if (!movement) throw new Error("Cash movement disappeared during claim.");
        claimed.push(Object.freeze({ movement, attemptCount }));
      }
      return Object.freeze({
        claimed: Object.freeze(claimed),
        rejectedLocally,
      });
    });
  }

  public async markSynced(
    movementGuids: readonly string[],
    uploadedAtIso: string,
  ): Promise<void> {
    for (const guid of movementGuids) {
      // 不限定 uploading：服务端已确认收下，即使租约恰好被回收成 pending 也应记为已同步，避免重复上传。
      await this.connection.run(
        `UPDATE local_cash_movements
         SET upload_state = 'synced',
             next_attempt_at_iso = NULL,
             lease_expires_at_iso = NULL,
             upload_error_code = NULL,
             upload_error_message = NULL,
             uploaded_at_iso = ?
         WHERE movement_guid = ?
           AND upload_state IN ('pending', 'uploading')`,
        [uploadedAtIso, guid],
      );
    }
  }

  public async markRetry(
    movementGuid: string,
    nextAttemptAtIso: string,
    errorCode: string,
    errorMessage: string,
  ): Promise<void> {
    // 只有持有租约（uploading）的一方才能改状态，避免迟到的旧请求结果覆盖已被别处接管的记录。
    await this.connection.run(
      `UPDATE local_cash_movements
       SET upload_state = 'pending',
           next_attempt_at_iso = ?,
           lease_expires_at_iso = NULL,
           upload_error_code = ?,
           upload_error_message = ?
       WHERE movement_guid = ?
         AND upload_state = 'uploading'`,
      [
        nextAttemptAtIso,
        boundedCode(errorCode),
        boundedMessage(errorMessage),
        movementGuid,
      ],
    );
  }

  public async releaseWithoutAttempt(
    movementGuids: readonly string[],
    nextAttemptAtIso: string,
    errorCode: string,
    errorMessage: string,
  ): Promise<void> {
    // 设备授权问题 / 本批未发送不是这条记录自己的失败：撤销认领时累加的尝试次数，不烧掉它的退避。
    for (const guid of movementGuids) {
      await this.connection.run(
        `UPDATE local_cash_movements
         SET upload_state = 'pending',
             upload_attempts = CASE
               WHEN upload_attempts > 0 THEN upload_attempts - 1
               ELSE 0
             END,
             next_attempt_at_iso = ?,
             lease_expires_at_iso = NULL,
             upload_error_code = ?,
             upload_error_message = ?
         WHERE movement_guid = ?
           AND upload_state = 'uploading'`,
        [
          nextAttemptAtIso,
          boundedCode(errorCode),
          boundedMessage(errorMessage),
          guid,
        ],
      );
    }
  }

  public async markRejected(
    movementGuid: string,
    errorCode: string,
    errorMessage: string,
  ): Promise<void> {
    await this.connection.run(
      `UPDATE local_cash_movements
       SET upload_state = 'rejected',
           next_attempt_at_iso = NULL,
           lease_expires_at_iso = NULL,
           upload_error_code = ?,
           upload_error_message = ?
       WHERE movement_guid = ?
         AND upload_state = 'uploading'`,
      [boundedCode(errorCode), boundedMessage(errorMessage), movementGuid],
    );
  }

  public async nextReadyAtIso(scope: CashMovementUploadScope): Promise<string | null> {
    // 只看带退避时间的 pending：没有 next 时间的 pending 是“立即到期”，由保存后唤醒与启动/前台/联网触发处理。
    const retry = await this.connection.getFirst<NextReadyRow>(
      `SELECT MIN(next_attempt_at_iso) AS next_ready_at_iso
       FROM local_cash_movements
       WHERE upload_state = 'pending'
         AND store_code = ?
         AND device_code = ?
         AND next_attempt_at_iso IS NOT NULL`,
      [scope.storeCode, scope.deviceCode],
    );
    // 崩溃遗留的 uploading 在租约到期后才会被回收，也要有一次定时唤醒。
    const lease = await this.connection.getFirst<NextReadyRow>(
      `SELECT MIN(lease_expires_at_iso) AS next_ready_at_iso
       FROM local_cash_movements
       WHERE upload_state = 'uploading'
         AND store_code = ?
         AND device_code = ?
         AND lease_expires_at_iso IS NOT NULL`,
      [scope.storeCode, scope.deviceCode],
    );
    const candidates = [retry?.next_ready_at_iso, lease?.next_ready_at_iso]
      .filter((value): value is string => typeof value === "string")
      .map((value) => Date.parse(value))
      .filter((value) => Number.isFinite(value));
    if (candidates.length === 0) return null;
    return new Date(Math.min(...candidates)).toISOString();
  }
}

async function assertVoidAllowed(
  transaction: SqliteConnectionPort,
  movement: CashMovementCommit["movement"],
): Promise<void> {
  const originalGuid = movement.voidsMovementGuid;
  const original = originalGuid === null ? null : await readMovement(transaction, originalGuid);
  if (!original) {
    throw new CashMovementRepositoryError("VOID_TARGET_NOT_FOUND");
  }
  // 冲正必须与原记录同店同设备、同原因码同金额、方向相反（服务端会校验 INVALID_VOID，这里先守住）。
  if (
    original.storeCode !== movement.storeCode ||
    original.deviceCode !== movement.deviceCode ||
    original.reasonCode !== movement.reasonCode ||
    original.amountCents !== movement.amountCents ||
    original.direction === movement.direction
  ) {
    throw new CashMovementRepositoryError(
      "COMMIT_INVALID",
      null,
      "Void does not match the original movement.",
    );
  }
  const eligibility = canVoidCashMovement({
    movement: original,
    alreadyVoided: original.voidedByMovementGuid !== null,
    // 冲正发生在「今天」：它的营业日就是今天；原记录必须属于同一营业日。
    todayBusinessDate: movement.businessDate,
    latestDailyCloseSavedAtIso: await latestDailyCloseSavedAt(
      transaction,
      movement.storeCode,
      movement.deviceCode,
      movement.businessDate,
    ),
  });
  if (!eligibility.ok) {
    throw new CashMovementRepositoryError("VOID_NOT_ALLOWED", eligibility.reason);
  }
}

async function latestDailyCloseSavedAt(
  connection: SqliteConnectionPort,
  storeCode: string,
  deviceCode: string,
  businessDate: string,
): Promise<string | null> {
  const row = await connection.getFirst<SavedAtRow>(
    `SELECT saved_at_iso
     FROM local_daily_closes
     WHERE store_code = ?
       AND device_code = ?
       AND business_date = ?
       AND state = 'Archived'
     ORDER BY julianday(saved_at_iso) DESC
     LIMIT 1`,
    [storeCode, deviceCode, businessDate],
  );
  return typeof row?.saved_at_iso === "string" ? row.saved_at_iso : null;
}

async function readMovement(
  connection: SqliteConnectionPort,
  movementGuid: string,
): Promise<LocalCashMovement | null> {
  const row = await connection.getFirst<MovementRow>(
    `${SELECT_MOVEMENT}
     WHERE movement.movement_guid = ?`,
    [movementGuid],
  );
  return row ? readMovementRow(row) : null;
}

async function readAuditJson(
  connection: SqliteConnectionPort,
  eventId: string,
): Promise<string> {
  const row = await connection.getFirst<{
    event_id: unknown;
    event_type: unknown;
    occurred_at_iso: unknown;
    order_guid: unknown;
    correlation_id: unknown;
    payload_json: unknown;
  }>(
    `SELECT event_id, event_type, occurred_at_iso, order_guid, correlation_id, payload_json
     FROM audit_events
     WHERE event_id = ?`,
    [eventId],
  );
  if (!row) throw new Error("Cash movement audit event is missing.");
  return stableJson({
    eventId: requiredText(row.event_id, "audit event id"),
    eventType: requiredText(row.event_type, "audit event type"),
    occurredAtIso: requiredText(row.occurred_at_iso, "audit occurred at"),
    orderGuid: row.order_guid === null ? null : requiredText(row.order_guid, "audit order guid"),
    correlationId: requiredText(row.correlation_id, "audit correlation id"),
    payload: JSON.parse(requiredText(row.payload_json, "audit payload")) as unknown,
  });
}

function readMovementRow(row: MovementRow): LocalCashMovement {
  const uploadState = requiredText(row.upload_state, "upload state");
  if (!(CASH_MOVEMENT_UPLOAD_STATES as readonly string[]).includes(uploadState)) {
    throw new Error("Cash movement upload state is invalid.");
  }
  return Object.freeze({
    movementGuid: requiredText(row.movement_guid, "cash movement guid"),
    direction: movementDirection(row.direction),
    reasonCode: reasonCode(row.reason_code),
    amountCents: safeInteger(row.amount_cents, "cash movement amount"),
    note: row.note === null ? null : requiredText(row.note, "cash movement note"),
    voidsMovementGuid:
      row.voids_movement_guid === null
        ? null
        : requiredText(row.voids_movement_guid, "voids movement guid"),
    businessDate: requiredText(row.business_date, "business date"),
    occurredAtIso: requiredText(row.occurred_at_iso, "occurred at"),
    cashierId: requiredText(row.cashier_id, "cashier id"),
    cashierName: requiredText(row.cashier_name, "cashier name"),
    authorizedById:
      row.authorized_by_id === null ? null : requiredText(row.authorized_by_id, "authorizer id"),
    authorizedByName:
      row.authorized_by_name === null
        ? null
        : requiredText(row.authorized_by_name, "authorizer name"),
    storeCode: requiredText(row.store_code, "store code"),
    deviceCode: requiredText(row.device_code, "device code"),
    displayNumber: requiredText(row.display_number, "display number"),
    voidedByMovementGuid:
      row.voided_by_movement_guid === null || row.voided_by_movement_guid === undefined
        ? null
        : requiredText(row.voided_by_movement_guid, "voided by guid"),
    auditEventId: requiredText(row.audit_event_id, "audit event id"),
    uploadState: uploadState as CashMovementUploadState,
    uploadAttempts: safeInteger(row.upload_attempts, "upload attempts"),
    uploadErrorCode:
      row.upload_error_code === null
        ? null
        : requiredText(row.upload_error_code, "upload error code"),
    uploadedAtIso:
      row.uploaded_at_iso === null ? null : requiredText(row.uploaded_at_iso, "uploaded at"),
    createdAtIso: requiredText(row.created_at_iso, "created at"),
  });
}

/** 入库前再校验一遍提交内容：事实自洽、审计与事实逐字对应（防止调用方绕过 buildCashMovement 手写）。 */
function validateCommit(commit: CashMovementCommit): CashMovementCommit {
  const { movement, audit } = commit;
  const fail = (message: string): never => {
    throw new CashMovementRepositoryError("COMMIT_INVALID", null, message);
  };
  if (!isCashMovementGuid(movement.movementGuid)) fail("movementGuid is invalid.");
  if (!isCashMovementReasonCode(movement.reasonCode)) fail("reasonCode is invalid.");
  if (movement.direction !== "In" && movement.direction !== "Out") fail("direction is invalid.");
  const natural = CASH_MOVEMENT_NATURAL_DIRECTIONS[movement.reasonCode];
  const isVoid = movement.voidsMovementGuid !== null;
  if (isVoid) {
    if (!isCashMovementGuid(movement.voidsMovementGuid)) fail("voidsMovementGuid is invalid.");
    if (movement.direction !== oppositeCashMovementDirection(natural)) {
      fail("A void must be opposite to the natural direction.");
    }
  } else if (movement.direction !== natural) {
    fail("direction must match the natural direction of the reason.");
  }
  const checked = validateCashMovementInput({
    reasonCode: movement.reasonCode,
    amountCents: movement.amountCents,
    note: movement.note,
    isVoid,
  });
  if (!checked.ok) fail(`Invalid cash movement: ${checked.code}.`);
  if (movement.note !== null && movement.note !== movement.note.trim()) {
    fail("note must be trimmed.");
  }
  const rebuilt = buildCashMovementAudit(
    movement,
    audit.eventId,
    typeof audit.payload.requestingUserGuid === "string"
      ? audit.payload.requestingUserGuid
      : null,
  );
  if (stableJson(rebuilt) !== stableJson(audit)) {
    fail("Audit event does not match the cash movement.");
  }
  if (!isCashMovementGuid(audit.eventId)) fail("audit event id is invalid.");
  return commit;
}

function factOf(movement: LocalCashMovement): CashMovementCommit["movement"] {
  return {
    movementGuid: movement.movementGuid,
    direction: movement.direction,
    reasonCode: movement.reasonCode,
    amountCents: movement.amountCents,
    note: movement.note,
    voidsMovementGuid: movement.voidsMovementGuid,
    businessDate: movement.businessDate,
    occurredAtIso: movement.occurredAtIso,
    cashierId: movement.cashierId,
    cashierName: movement.cashierName,
    authorizedById: movement.authorizedById,
    authorizedByName: movement.authorizedByName,
    storeCode: movement.storeCode,
    deviceCode: movement.deviceCode,
  };
}

function validatePeriodScope(scope: DailyCloseScope): DailyCloseScope {
  const from = Date.parse(scope.periodFromIso);
  const to = Date.parse(scope.periodToIso);
  if (
    !Number.isFinite(from) ||
    !Number.isFinite(to) ||
    from >= to ||
    typeof scope.storeCode !== "string" ||
    scope.storeCode.trim().length === 0 ||
    typeof scope.deviceCode !== "string" ||
    scope.deviceCode.trim().length === 0
  ) {
    throw new TypeError("Cash movement period scope is invalid.");
  }
  return scope;
}

function movementDirection(value: unknown): CashMovementDirection {
  if (value === "In" || value === "Out") return value;
  throw new Error("Cash movement direction is invalid.");
}

function reasonCode(value: unknown): LocalCashMovement["reasonCode"] {
  if (isCashMovementReasonCode(value)) return value;
  throw new Error("Cash movement reason code is invalid.");
}

function requiredText(value: unknown, label: string): string {
  if (typeof value !== "string") throw new TypeError(`${label} is invalid.`);
  return value;
}

function safeInteger(value: unknown, label: string): number {
  const numeric = Number(value);
  if (!Number.isSafeInteger(numeric)) {
    throw new TypeError(`${label} must be a safe integer.`);
  }
  return numeric;
}

function boundedCode(value: string): string {
  return value.slice(0, MAXIMUM_ERROR_CODE_LENGTH);
}

function boundedMessage(value: string): string {
  return value.slice(0, MAXIMUM_ERROR_MESSAGE_LENGTH);
}

function stableJson(value: unknown): string {
  return JSON.stringify(sortJson(value));
}

function sortJson(value: unknown): unknown {
  if (Array.isArray(value)) return value.map(sortJson);
  if (value && typeof value === "object") {
    return Object.fromEntries(
      Object.entries(value as Readonly<Record<string, unknown>>)
        .sort(([left], [right]) => left.localeCompare(right))
        .map(([key, entry]) => [key, sortJson(entry)]),
    );
  }
  if (
    value === null ||
    typeof value === "string" ||
    typeof value === "number" ||
    typeof value === "boolean"
  ) {
    return value;
  }
  throw new TypeError("Cash movement audit payload is not JSON serializable.");
}
