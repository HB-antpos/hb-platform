import assert from "node:assert/strict";
import { DatabaseSync, type SQLInputValue } from "node:sqlite";
import test from "node:test";

import {
  CashMovementRepositoryError,
  buildCashMovement,
  type BuildCashMovementInput,
  type CashMovementCommit,
  type LocalCashMovement,
} from "@hb/pos-domain/core/contracts/cash-movement";
import type { DailyCloseScope } from "@hb/pos-domain/core/contracts/daily-close";

import { CASH_MOVEMENT_TABLE_SQL } from "./cash-movement-schema";
import {
  SqliteCashMovementRepository,
  SqliteCashMovementUploadRepository,
} from "./sqlite-cash-movement-repository";
import type { SqliteConnectionPort, SqlRunResult, SqlValue } from "./types";

const STORE = "S001";
const DEVICE = "IPAD-1";
const TZ = "Australia/Brisbane";

/** 营业日 2026-11-14（Brisbane）= [2026-11-13T14:00Z, 2026-11-14T14:00Z)。 */
const scope: DailyCloseScope = {
  businessDate: "2026-11-14",
  periodFromIso: "2026-11-13T14:00:00.000Z",
  periodToIso: "2026-11-14T14:00:00.000Z",
  storeCode: STORE,
  deviceCode: DEVICE,
};

let counter = 0;
const uuid = (): string => {
  counter += 1;
  return `00000000-0000-4000-8000-${String(counter).padStart(12, "0")}`;
};

function commitOf(overrides: Partial<BuildCashMovementInput> = {}): CashMovementCommit {
  return buildCashMovement({
    movementGuid: uuid(),
    auditEventId: uuid(),
    entry: { kind: "movement", reasonCode: "ChangeTopUp", amountCents: 5_000 },
    note: null,
    occurredAtIso: "2026-11-14T02:00:00.000Z",
    businessTimeZone: TZ,
    storeCode: STORE,
    deviceCode: DEVICE,
    cashier: { cashierId: "c1", cashierName: "Alice", userGuid: null },
    ...overrides,
  });
}

function voidCommitOf(
  original: LocalCashMovement,
  overrides: Partial<BuildCashMovementInput> = {},
): CashMovementCommit {
  return commitOf({
    entry: { kind: "void", original },
    note: "entered wrong amount",
    occurredAtIso: "2026-11-14T03:00:00.000Z",
    ...overrides,
  });
}

test("append：单事务写记录与审计，流水号 CM-0001 递增（含冲正），审计键为安全集合", async () => {
  await withDatabase(async (connection) => {
    const repository = new SqliteCashMovementRepository(connection);
    const first = await repository.append(commitOf());
    const second = await repository.append(
      commitOf({
        entry: { kind: "movement", reasonCode: "SafeDrop", amountCents: 20_000 },
        occurredAtIso: "2026-11-14T02:10:00.000Z",
        authorizedBy: { id: "m1", name: "Manager" },
      }),
    );
    const voided = await repository.append(voidCommitOf(second.movement));
    assert.equal(first.movement.displayNumber, "CM-0001");
    assert.equal(second.movement.displayNumber, "CM-0002");
    assert.equal(voided.movement.displayNumber, "CM-0003");
    assert.equal(first.replayed, false);
    assert.equal(first.movement.uploadState, "pending");
    assert.equal(first.movement.businessDate, "2026-11-14");
    assert.equal(second.movement.authorizedByName, "Manager");
    assert.equal(voided.movement.voidsMovementGuid, second.movement.movementGuid);

    const audits = await connection.getAll<{
      event_type: string;
      order_guid: string | null;
      correlation_id: string;
      scope_store_code: string;
      scope_device_code: string;
      delivery_state: string;
      payload_json: string;
    }>("SELECT * FROM audit_events ORDER BY occurred_at_iso");
    assert.deepEqual(
      audits.map((row) => row.event_type),
      ["CASH_IN", "CASH_OUT", "CASH_MOVEMENT_VOID"],
    );
    assert.equal(audits[0]?.order_guid, null);
    assert.equal(audits[0]?.correlation_id, first.movement.movementGuid);
    assert.equal(audits[0]?.scope_store_code, STORE);
    assert.equal(audits[0]?.scope_device_code, DEVICE);
    assert.equal(audits[0]?.delivery_state, "pending");
    const payload = JSON.parse(audits[1]?.payload_json ?? "{}") as Record<string, unknown>;
    assert.equal(payload.amountDeltaCents, -20_000);
    assert.equal(payload.paymentMethod, "Cash");
    assert.equal(payload.source, "cash-movement");
  });
});

test("append：审计写入失败时整体回滚——没有记录、流水号不前进", async () => {
  await withDatabase(async (connection) => {
    const repository = new SqliteCashMovementRepository(connection);
    await connection.exec(`
      CREATE TRIGGER trg_test_audit_fail
      BEFORE INSERT ON audit_events
      WHEN NEW.event_type = 'CASH_OUT'
      BEGIN SELECT RAISE(ABORT, 'AUDIT_BROKEN'); END;
    `);
    await assert.rejects(
      repository.append(
        commitOf({ entry: { kind: "movement", reasonCode: "SafeDrop", amountCents: 100 } }),
      ),
      /AUDIT_BROKEN/u,
    );
    assert.equal((await connection.getFirst<{ n: number }>("SELECT COUNT(*) AS n FROM local_cash_movements"))?.n, 0);
    assert.equal((await connection.getFirst<{ n: number }>("SELECT COUNT(*) AS n FROM audit_events"))?.n, 0);
    // 失败不消耗流水号。
    const next = await repository.append(commitOf());
    assert.equal(next.movement.displayNumber, "CM-0001");
  });
});

test("append：记录写入失败（约束）同样回滚审计", async () => {
  await withDatabase(async (connection) => {
    const repository = new SqliteCashMovementRepository(connection);
    const commit = commitOf();
    // 绕过 buildCashMovement 伪造一个合法性校验通过、但 display_number 撞号的场景：先占用 CM-0001。
    await connection.exec(`
      CREATE TRIGGER trg_test_block_second_movement
      BEFORE INSERT ON local_cash_movements
      WHEN NEW.amount_cents = 777
      BEGIN SELECT RAISE(ABORT, 'MOVEMENT_BROKEN'); END;
    `);
    await assert.rejects(
      repository.append(
        commitOf({ entry: { kind: "movement", reasonCode: "ChangeTopUp", amountCents: 777 } }),
      ),
      /MOVEMENT_BROKEN/u,
    );
    assert.equal((await connection.getFirst<{ n: number }>("SELECT COUNT(*) AS n FROM audit_events"))?.n, 0);
    await repository.append(commit);
    assert.equal((await connection.getFirst<{ n: number }>("SELECT COUNT(*) AS n FROM audit_events"))?.n, 1);
  });
});

test("append：同一 movementGuid 重放返回已有记录；内容不同失败关闭", async () => {
  await withDatabase(async (connection) => {
    const repository = new SqliteCashMovementRepository(connection);
    const commit = commitOf();
    const first = await repository.append(commit);
    const replay = await repository.append(commit);
    assert.equal(replay.replayed, true);
    assert.equal(replay.movement.displayNumber, first.movement.displayNumber);
    assert.equal((await connection.getFirst<{ n: number }>("SELECT COUNT(*) AS n FROM local_cash_movements"))?.n, 1);

    const conflicting = commitOf({
      movementGuid: commit.movement.movementGuid,
      entry: { kind: "movement", reasonCode: "ChangeTopUp", amountCents: 6_000 },
    });
    await assert.rejects(
      repository.append(conflicting),
      (error) => error instanceof CashMovementRepositoryError && error.code === "REPLAY_MISMATCH",
    );
  });
});

test("append：手写的提交（审计与事实不对应、方向与原因码不符）被拒绝", async () => {
  await withDatabase(async (connection) => {
    const repository = new SqliteCashMovementRepository(connection);
    const good = commitOf();
    const invalid = (commit: CashMovementCommit) =>
      repository.append(commit).then(
        () => "accepted",
        (error: unknown) =>
          error instanceof CashMovementRepositoryError ? error.code : "other",
      );
    assert.equal(
      await invalid({ ...good, audit: { ...good.audit, eventType: "CASH_OUT" } }),
      "COMMIT_INVALID",
    );
    assert.equal(
      await invalid({
        ...good,
        movement: { ...good.movement, direction: "Out" },
      }),
      "COMMIT_INVALID",
    );
    assert.equal(
      await invalid({
        ...good,
        movement: { ...good.movement, amountCents: 0 },
      }),
      "COMMIT_INVALID",
    );
    assert.equal(
      await invalid({
        ...good,
        movement: { ...good.movement, amountCents: 100_000_001 },
      }),
      "COMMIT_INVALID",
    );
  });
});

test("冲正：成功后原记录被标记 voidedByMovementGuid；同一原记录第二次冲正被拒（应用层与唯一索引双重兜底）", async () => {
  await withDatabase(async (connection) => {
    const repository = new SqliteCashMovementRepository(connection);
    const original = (await repository.append(
      commitOf({ entry: { kind: "movement", reasonCode: "PaidOut", amountCents: 1_250 }, note: "milk" }),
    )).movement;
    const reversal = (await repository.append(voidCommitOf(original))).movement;
    assert.equal(reversal.direction, "In");
    assert.equal((await repository.getByGuid(original.movementGuid))?.voidedByMovementGuid, reversal.movementGuid);

    await assert.rejects(
      repository.append(voidCommitOf(original, { occurredAtIso: "2026-11-14T04:00:00.000Z" })),
      (error) =>
        error instanceof CashMovementRepositoryError &&
        error.code === "VOID_NOT_ALLOWED" &&
        error.denial === "ALREADY_VOIDED",
    );
    // 绕过应用层直接插入第二条冲正：VoidsMovementGuid 唯一索引兜底。
    await assert.rejects(
      insertRawMovement(connection, {
        guid: uuid(),
        voids: original.movementGuid,
        reason: "PaidOut",
        direction: "In",
        amount: 1_250,
        displayNumber: "CM-9998",
      }),
      /UNIQUE/iu,
    );
  });
});

test("冲正：不能冲冲正；原记录不存在 / 其它设备的原记录被拒", async () => {
  await withDatabase(async (connection) => {
    const repository = new SqliteCashMovementRepository(connection);
    const original = (await repository.append(commitOf())).movement;
    const reversal = (await repository.append(voidCommitOf(original))).movement;
    // 对冲正记录再发起冲正：构造器要求原记录不是冲正；伪造绕过构造器时，仓储与数据库触发器也拒绝。
    assert.throws(() => voidCommitOf(reversal));
    const forged = commitOf({
      entry: {
        kind: "void",
        original: { ...reversal, voidsMovementGuid: null, direction: "In" },
      },
      note: "forged void",
      occurredAtIso: "2026-11-14T05:00:00.000Z",
    });
    await assert.rejects(repository.append(forged), CashMovementRepositoryError);
    await assert.rejects(
      insertRawMovement(connection, {
        guid: uuid(),
        voids: reversal.movementGuid,
        reason: "ChangeTopUp",
        direction: "In",
        amount: 5_000,
        displayNumber: "CM-9997",
      }),
      /CASH_MOVEMENT_VOID_INVALID/u,
    );

    const missing = commitOf({
      entry: {
        kind: "void",
        original: {
          movementGuid: uuid(),
          reasonCode: "SafeDrop",
          amountCents: 100,
          direction: "Out",
          voidsMovementGuid: null,
        },
      },
      note: "unknown original",
    });
    await assert.rejects(
      repository.append(missing),
      (error) => error instanceof CashMovementRepositoryError && error.code === "VOID_TARGET_NOT_FOUND",
    );

    const otherDevice = (await repository.append(commitOf({ deviceCode: "IPAD-2" }))).movement;
    await assert.rejects(
      repository.append(voidCommitOf(otherDevice)),
      (error) => error instanceof CashMovementRepositoryError && error.code === "COMMIT_INVALID",
    );
  });
});

test("冲正：不是今天的记录不能冲正（营业日不同）", async () => {
  await withDatabase(async (connection) => {
    const repository = new SqliteCashMovementRepository(connection);
    const yesterday = (await repository.append(
      commitOf({ occurredAtIso: "2026-11-13T05:00:00.000Z" }), // Brisbane 2026-11-13
    )).movement;
    assert.equal(yesterday.businessDate, "2026-11-13");
    await assert.rejects(
      repository.append(voidCommitOf(yesterday)),
      (error) =>
        error instanceof CashMovementRepositoryError &&
        error.code === "VOID_NOT_ALLOWED" &&
        error.denial === "NOT_TODAY",
    );
  });
});

test("冲正：今天在该记录发生之后保存过日结则拒绝；日结早于该记录则允许；其它营业日的日结不影响", async () => {
  await withDatabase(async (connection) => {
    const repository = new SqliteCashMovementRepository(connection);
    const early = (await repository.append(commitOf({ occurredAtIso: "2026-11-14T01:00:00.000Z" }))).movement;
    await insertDailyClose(connection, "close-1", "2026-11-14", "2026-11-14T02:00:00.000Z");
    const late = (await repository.append(
      commitOf({ occurredAtIso: "2026-11-14T03:00:00.000Z", entry: { kind: "movement", reasonCode: "SafeDrop", amountCents: 900 } }),
    )).movement;
    await insertDailyClose(connection, "close-other-day", "2026-11-13", "2026-11-14T09:00:00.000Z");
    await insertDailyClose(connection, "close-preparing", "2026-11-14", "2026-11-14T09:30:00.000Z", "Preparing");

    // early 在日结（02:00）之前发生 → 日结已把它算进去，拒绝。
    await assert.rejects(
      repository.append(voidCommitOf(early, { occurredAtIso: "2026-11-14T04:00:00.000Z" })),
      (error) =>
        error instanceof CashMovementRepositoryError &&
        error.code === "VOID_NOT_ALLOWED" &&
        error.denial === "DAILY_CLOSE_SAVED",
    );
    // late 在日结之后才发生 → 允许。
    const reversed = await repository.append(voidCommitOf(late, { occurredAtIso: "2026-11-14T04:00:00.000Z" }));
    assert.equal(reversed.movement.voidsMovementGuid, late.movementGuid);
    assert.equal(await repository.latestDailyCloseSavedAtIso(scope), "2026-11-14T02:00:00.000Z");

    // 数据库触发器兜底：绕过应用层直接插入对 early 的冲正同样被拒。
    await assert.rejects(
      insertRawMovement(connection, {
        guid: uuid(),
        voids: early.movementGuid,
        reason: "ChangeTopUp",
        direction: "Out",
        amount: 5_000,
        displayNumber: "CM-9996",
        occurredAt: "2026-11-14T04:30:00.000Z",
      }),
      /CASH_MOVEMENT_VOID_AFTER_DAILY_CLOSE/u,
    );
  });
});

test("listToday：按营业日区间 [from, to) 与本设备范围，按发生时间升序并标记已冲正", async () => {
  await withDatabase(async (connection) => {
    const repository = new SqliteCashMovementRepository(connection);
    // 区间外：前一刻、to 边界（不含）、其它设备。
    await repository.append(commitOf({ occurredAtIso: "2026-11-13T13:59:59.999Z" }));
    await repository.append(commitOf({ occurredAtIso: "2026-11-14T14:00:00.000Z" }));
    await repository.append(commitOf({ deviceCode: "IPAD-2", occurredAtIso: "2026-11-14T02:00:00.000Z" }));
    const atFrom = (await repository.append(commitOf({ occurredAtIso: "2026-11-13T14:00:00.000Z" }))).movement;
    const later = (await repository.append(
      commitOf({ occurredAtIso: "2026-11-14T06:00:00.000Z", entry: { kind: "movement", reasonCode: "SafeDrop", amountCents: 100 } }),
    )).movement;
    const reversal = (await repository.append(voidCommitOf(later, { occurredAtIso: "2026-11-14T07:00:00.000Z" }))).movement;

    const list = await repository.listToday(scope);
    assert.deepEqual(
      list.map((entry) => entry.movementGuid),
      [atFrom.movementGuid, later.movementGuid, reversal.movementGuid],
    );
    assert.equal(list[1]?.voidedByMovementGuid, reversal.movementGuid);
    assert.equal(list[0]?.voidedByMovementGuid, null);
    assert.equal(list[2]?.voidedByMovementGuid, null);
    await assert.rejects(
      repository.listToday({ ...scope, periodToIso: scope.periodFromIso }),
      TypeError,
    );
  });
});

test("summarizePeriod：冲正抵消；不按上传状态过滤；其它设备与区间外不计", async () => {
  await withDatabase(async (connection) => {
    const repository = new SqliteCashMovementRepository(connection);
    await repository.append(commitOf({ entry: { kind: "movement", reasonCode: "ChangeTopUp", amountCents: 10_000 } }));
    await repository.append(
      commitOf({ entry: { kind: "movement", reasonCode: "OtherIn", amountCents: 2_000 }, note: "float" }),
    );
    const drop = (await repository.append(
      commitOf({ entry: { kind: "movement", reasonCode: "SafeDrop", amountCents: 30_000 } }),
    )).movement;
    await repository.append(
      commitOf({ entry: { kind: "movement", reasonCode: "PaidOut", amountCents: 1_250 }, note: "milk" }),
    );
    await repository.append(voidCommitOf(drop)); // 冲正抵消 SafeDrop
    await repository.append(commitOf({ deviceCode: "IPAD-2", entry: { kind: "movement", reasonCode: "SafeDrop", amountCents: 999 } }));
    await repository.append(commitOf({ occurredAtIso: "2026-11-14T14:00:00.000Z", entry: { kind: "movement", reasonCode: "SafeDrop", amountCents: 888 } }));
    // 把全部记录改成各种上传状态，汇总不受影响。
    await connection.run("UPDATE local_cash_movements SET upload_state = 'rejected' WHERE amount_cents = 10000");
    await connection.run("UPDATE local_cash_movements SET upload_state = 'synced' WHERE amount_cents = 2000");

    const summary = await repository.summarizePeriod(scope);
    assert.equal(summary.cashInCents, 12_000);
    assert.equal(summary.cashOutCents, 1_250);
    assert.deepEqual(
      { ...summary.byReason },
      { ChangeTopUp: 10_000, OtherIn: 2_000, SafeDrop: 0, PaidOut: 1_250 },
    );
    // 没有任何记录的设备汇总为 0。
    const empty = await repository.summarizePeriod({ ...scope, deviceCode: "NOBODY" });
    assert.equal(empty.cashInCents, 0);
    assert.equal(empty.cashOutCents, 0);
  });
});

test("不可变与约束：除上传状态列外不可 UPDATE，不可 DELETE；金额、方向、备注由 CHECK 兜底", async () => {
  await withDatabase(async (connection) => {
    const repository = new SqliteCashMovementRepository(connection);
    const movement = (await repository.append(commitOf())).movement;
    for (const column of ["amount_cents = 1", "note = 'x'", "direction = 'Out'", "business_date = '2026-01-01'", "voids_movement_guid = NULL"]) {
      await assert.rejects(
        connection.run(`UPDATE local_cash_movements SET ${column} WHERE movement_guid = ?`, [movement.movementGuid]),
        /CASH_MOVEMENT_IMMUTABLE/u,
        column,
      );
    }
    await assert.rejects(
      connection.run("DELETE FROM local_cash_movements WHERE movement_guid = ?", [movement.movementGuid]),
      /CASH_MOVEMENT_DELETE_FORBIDDEN/u,
    );
    // 上传状态列可以更新。
    await connection.run("UPDATE local_cash_movements SET upload_state = 'synced' WHERE movement_guid = ?", [movement.movementGuid]);

    const raw = (overrides: Parameters<typeof insertRawMovement>[1]) => insertRawMovement(connection, overrides);
    const base = { reason: "ChangeTopUp", direction: "In", amount: 100 } as const;
    await assert.rejects(raw({ guid: uuid(), displayNumber: "CM-A1", ...base, amount: 0 }), /CHECK/iu);
    await assert.rejects(raw({ guid: uuid(), displayNumber: "CM-A2", ...base, amount: 100_000_001 }), /CHECK/iu);
    await raw({ guid: uuid(), displayNumber: "CM-A3", ...base, amount: 100_000_000 });
    await assert.rejects(raw({ guid: uuid(), displayNumber: "CM-A4", ...base, direction: "Out" }), /CHECK/iu);
    await assert.rejects(raw({ guid: uuid(), displayNumber: "CM-A5", reason: "OtherIn", direction: "In", amount: 100, note: "x" }), /CHECK/iu);
    await assert.rejects(raw({ guid: uuid(), displayNumber: "CM-A6", reason: "PaidOut", direction: "Out", amount: 100, note: null }), /CHECK/iu);
    await raw({ guid: uuid(), displayNumber: "CM-A7", reason: "PaidOut", direction: "Out", amount: 100, note: "ab" });
    await assert.rejects(raw({ guid: uuid(), displayNumber: "CM-A8", ...base, note: "x".repeat(201) }), /CHECK/iu);
    await assert.rejects(raw({ guid: uuid(), displayNumber: "CM-A7", ...base }), /UNIQUE/iu);
  });
});

// ---------------- 上传 outbox ----------------

const uploadScope = { storeCode: STORE, deviceCode: DEVICE };
const NOW = "2026-11-14T06:00:00.000Z";

async function seed(
  repository: SqliteCashMovementRepository,
  occurredAtIso: string,
  amountCents = 100,
): Promise<LocalCashMovement> {
  return (
    await repository.append(
      commitOf({ occurredAtIso, entry: { kind: "movement", reasonCode: "ChangeTopUp", amountCents } }),
    )
  ).movement;
}

test("claimDueBatch：按 occurred_at 升序（与写入顺序无关）、带租约、尝试次数 +1；再认领拿不到", async () => {
  await withDatabase(async (connection) => {
    const repository = new SqliteCashMovementRepository(connection);
    const upload = new SqliteCashMovementUploadRepository(connection);
    const c = await seed(repository, "2026-11-14T03:00:00.000Z", 300);
    const a = await seed(repository, "2026-11-14T01:00:00.000Z", 100);
    const b = await seed(repository, "2026-11-14T02:00:00.000Z", 200);
    await repository.append(commitOf({ deviceCode: "IPAD-2" })); // 其它范围不认领

    const claim = await upload.claimDueBatch(uploadScope, 2, NOW);
    assert.deepEqual(
      claim.claimed.map((entry) => entry.movement.movementGuid),
      [a.movementGuid, b.movementGuid],
    );
    assert.deepEqual(claim.claimed.map((entry) => entry.attemptCount), [1, 1]);
    const row = await connection.getFirst<{ upload_state: string; lease_expires_at_iso: string }>(
      "SELECT upload_state, lease_expires_at_iso FROM local_cash_movements WHERE movement_guid = ?",
      [a.movementGuid],
    );
    assert.equal(row?.upload_state, "uploading");
    assert.equal(row?.lease_expires_at_iso, "2026-11-14T06:02:00.000Z"); // 租约 120 秒
    const again = await upload.claimDueBatch(uploadScope, 10, NOW);
    assert.deepEqual(again.claimed.map((entry) => entry.movement.movementGuid), [c.movementGuid]);
    assert.equal((await upload.claimDueBatch(uploadScope, 10, NOW)).claimed.length, 0);
  });
});

test("租约：未到期不回收，到期回收为 pending 并立即可再认领", async () => {
  await withDatabase(async (connection) => {
    const repository = new SqliteCashMovementRepository(connection);
    const upload = new SqliteCashMovementUploadRepository(connection);
    await seed(repository, "2026-11-14T01:00:00.000Z");
    await upload.claimDueBatch(uploadScope, 10, NOW);
    assert.equal(await upload.recoverExpiredUploading({ nowIso: "2026-11-14T06:01:59.999Z", nextAttemptAtIso: NOW }), 0);
    assert.equal(await upload.recoverExpiredUploading({ nowIso: "2026-11-14T06:02:00.000Z", nextAttemptAtIso: "2026-11-14T06:02:00.000Z" }), 1);
    const reclaimed = await upload.claimDueBatch(uploadScope, 10, "2026-11-14T06:02:00.000Z");
    assert.equal(reclaimed.claimed.length, 1);
    assert.equal(reclaimed.claimed[0]?.attemptCount, 2);
  });
});

test("markRetry：退避期内不再认领、到期后可认领；只有持有租约的一方能改", async () => {
  await withDatabase(async (connection) => {
    const repository = new SqliteCashMovementRepository(connection);
    const upload = new SqliteCashMovementUploadRepository(connection);
    const movement = await seed(repository, "2026-11-14T01:00:00.000Z");
    await upload.claimDueBatch(uploadScope, 10, NOW);
    await upload.markRetry(movement.movementGuid, "2026-11-14T06:00:05.000Z", "NETWORK", "offline");
    assert.equal((await upload.claimDueBatch(uploadScope, 10, "2026-11-14T06:00:04.999Z")).claimed.length, 0);
    assert.equal(await upload.nextReadyAtIso(uploadScope), "2026-11-14T06:00:05.000Z");
    // 不持有租约（已是 pending）再 markRetry / markRejected 不生效。
    await upload.markRejected(movement.movementGuid, "X", "x");
    await upload.markRetry(movement.movementGuid, "2030-01-01T00:00:00.000Z", "Y", "y");
    const read = await repository.getByGuid(movement.movementGuid);
    assert.equal(read?.uploadState, "pending");
    assert.equal(read?.uploadErrorCode, "NETWORK");
    const claimed = await upload.claimDueBatch(uploadScope, 10, "2026-11-14T06:00:05.000Z");
    assert.equal(claimed.claimed[0]?.attemptCount, 2);
  });
});

test("releaseWithoutAttempt 撤销尝试次数；markSynced / markRejected 终态；nextReadyAtIso 含租约到期", async () => {
  await withDatabase(async (connection) => {
    const repository = new SqliteCashMovementRepository(connection);
    const upload = new SqliteCashMovementUploadRepository(connection);
    const a = await seed(repository, "2026-11-14T01:00:00.000Z");
    const b = await seed(repository, "2026-11-14T02:00:00.000Z");
    const c = await seed(repository, "2026-11-14T03:00:00.000Z");
    await upload.claimDueBatch(uploadScope, 10, NOW);
    assert.equal(await upload.nextReadyAtIso(uploadScope), "2026-11-14T06:02:00.000Z");
    await upload.releaseWithoutAttempt([a.movementGuid], "2026-11-14T06:01:00.000Z", "HTTP_401", "unauthorized");
    await upload.markSynced([b.movementGuid], "2026-11-14T06:00:30.000Z");
    await upload.markRejected(c.movementGuid, "INVALID_AMOUNT", "bad");
    const rows = await Promise.all([a, b, c].map((entry) => repository.getByGuid(entry.movementGuid)));
    assert.equal(rows[0]?.uploadState, "pending");
    assert.equal(rows[0]?.uploadAttempts, 0);
    assert.equal(rows[1]?.uploadState, "synced");
    assert.equal(rows[1]?.uploadedAtIso, "2026-11-14T06:00:30.000Z");
    assert.equal(rows[2]?.uploadState, "rejected");
    assert.equal(rows[2]?.uploadErrorCode, "INVALID_AMOUNT");
    // 被拒的记录仍计入本地汇总。
    assert.equal((await repository.summarizePeriod(scope)).cashInCents, 300);
    assert.equal(await upload.nextReadyAtIso(uploadScope), "2026-11-14T06:01:00.000Z");
    assert.equal(await upload.nextReadyAtIso({ storeCode: STORE, deviceCode: "NOBODY" }), null);
  });
});

test("冲正的上传顺序：原记录未同步时冲正不认领；原记录 synced 后才认领；原记录被拒则冲正本地 rejected", async () => {
  await withDatabase(async (connection) => {
    const repository = new SqliteCashMovementRepository(connection);
    const upload = new SqliteCashMovementUploadRepository(connection);
    const original = await seed(repository, "2026-11-14T01:00:00.000Z");
    const reversal = (await repository.append(voidCommitOf(original, { occurredAtIso: "2026-11-14T02:00:00.000Z" }))).movement;

    // 同一批里原记录排在冲正之前。
    const together = await upload.claimDueBatch(uploadScope, 10, NOW);
    assert.deepEqual(
      together.claimed.map((entry) => entry.movement.movementGuid),
      [original.movementGuid, reversal.movementGuid],
    );
    // 原记录失败退避：冲正也必须带退避回去，之后不会越过原记录先传。
    await upload.markRetry(original.movementGuid, "2026-11-14T06:05:00.000Z", "NETWORK", "x");
    await upload.markRetry(reversal.movementGuid, "2026-11-14T06:00:10.000Z", "VOIDED_MOVEMENT_NOT_FOUND", "x");
    assert.equal((await upload.claimDueBatch(uploadScope, 10, "2026-11-14T06:00:20.000Z")).claimed.length, 0);

    // 原记录到期重传成功，冲正随后可传。
    const retried = await upload.claimDueBatch(uploadScope, 10, "2026-11-14T06:05:00.000Z");
    assert.deepEqual(retried.claimed.map((entry) => entry.movement.movementGuid), [original.movementGuid, reversal.movementGuid]);
    await upload.markSynced([original.movementGuid], "2026-11-14T06:05:01.000Z");
    await upload.markRetry(reversal.movementGuid, "2026-11-14T06:05:10.000Z", "VOIDED_MOVEMENT_NOT_FOUND", "x");
    const last = await upload.claimDueBatch(uploadScope, 10, "2026-11-14T06:05:10.000Z");
    assert.deepEqual(last.claimed.map((entry) => entry.movement.movementGuid), [reversal.movementGuid]);
  });

  await withDatabase(async (connection) => {
    const repository = new SqliteCashMovementRepository(connection);
    const upload = new SqliteCashMovementUploadRepository(connection);
    const original = await seed(repository, "2026-11-14T01:00:00.000Z");
    const reversal = (await repository.append(voidCommitOf(original, { occurredAtIso: "2026-11-14T02:00:00.000Z" }))).movement;
    await upload.claimDueBatch({ storeCode: STORE, deviceCode: DEVICE }, 1, NOW); // 只认领原记录
    await upload.markRejected(original.movementGuid, "INVALID_AMOUNT", "bad");
    const result = await upload.claimDueBatch(uploadScope, 10, NOW);
    assert.equal(result.claimed.length, 0);
    assert.equal(result.rejectedLocally, 1);
    const read = await repository.getByGuid(reversal.movementGuid);
    assert.equal(read?.uploadState, "rejected");
    assert.equal(read?.uploadErrorCode, "VOIDED_MOVEMENT_REJECTED");
  });
});

// ---------------- 辅助 ----------------

async function insertRawMovement(
  connection: SqliteConnectionPort,
  input: Readonly<{
    guid: string;
    voids?: string | null;
    reason: string;
    direction: string;
    amount: number;
    displayNumber: string;
    note?: string | null;
    occurredAt?: string;
  }>,
): Promise<void> {
  const auditId = uuid();
  await connection.run(
    `INSERT INTO audit_events (event_id, event_type, occurred_at_iso, order_guid, correlation_id, payload_json)
     VALUES (?, 'CASH_IN', '2026-11-14T02:00:00.000Z', NULL, ?, '{}')`,
    [auditId, input.guid],
  );
  await connection.run(
    `INSERT INTO local_cash_movements (
      movement_guid, direction, reason_code, amount_cents, note, voids_movement_guid,
      business_date, occurred_at_iso, cashier_id, cashier_name, display_number,
      store_code, device_code, audit_event_id, created_at_iso
    ) VALUES (?, ?, ?, ?, ?, ?, '2026-11-14', ?, 'c1', 'Alice', ?, ?, ?, ?, '2026-11-14T02:00:00.000Z')`,
    [
      input.guid,
      input.direction,
      input.reason,
      input.amount,
      input.note ?? null,
      input.voids ?? null,
      input.occurredAt ?? "2026-11-14T02:00:00.000Z",
      input.displayNumber,
      STORE,
      DEVICE,
      auditId,
    ],
  );
}

async function insertDailyClose(
  connection: SqliteConnectionPort,
  closeId: string,
  businessDate: string,
  savedAtIso: string,
  state = "Archived",
): Promise<void> {
  await connection.run(
    `INSERT INTO local_daily_closes (close_id, store_code, device_code, business_date, saved_at_iso, state)
     VALUES (?, ?, ?, ?, ?, ?)`,
    [closeId, STORE, DEVICE, businessDate, savedAtIso, state],
  );
}

class SystemSqliteConnection implements SqliteConnectionPort {
  public constructor(protected readonly database: DatabaseSync) {}

  public async exec(sql: string): Promise<void> {
    this.database.exec(sql);
  }

  public async run(sql: string, parameters: readonly SqlValue[] = []): Promise<SqlRunResult> {
    const result = this.database.prepare(sql).run(...parameters.map((value) => value as SQLInputValue));
    return { changes: Number(result.changes), lastInsertRowId: Number(result.lastInsertRowid) };
  }

  public async getFirst<T extends object>(
    sql: string,
    parameters: readonly SqlValue[] = [],
  ): Promise<T | null> {
    return (
      (this.database.prepare(sql).get(...parameters.map((value) => value as SQLInputValue)) as T | undefined) ?? null
    );
  }

  public async getAll<T extends object>(
    sql: string,
    parameters: readonly SqlValue[] = [],
  ): Promise<readonly T[]> {
    return this.database
      .prepare(sql)
      .all(...parameters.map((value) => value as SQLInputValue)) as unknown as readonly T[];
  }

  public async withExclusiveTransaction<T>(
    operation: (transaction: SqliteConnectionPort) => Promise<T>,
  ): Promise<T> {
    this.database.exec("BEGIN IMMEDIATE;");
    const transaction = new TransactionConnection(this.database);
    try {
      const result = await operation(transaction);
      this.database.exec("COMMIT;");
      return result;
    } catch (error) {
      this.database.exec("ROLLBACK;");
      throw error;
    }
  }

  public async close(): Promise<void> {
    this.database.close();
  }
}

class TransactionConnection extends SystemSqliteConnection {
  public override withExclusiveTransaction<T>(): Promise<T> {
    return Promise.reject(new Error("Nested test transaction."));
  }

  public override close(): Promise<void> {
    return Promise.reject(new Error("Transaction cannot close database."));
  }
}

/** 最小 schema：只含现金进出表依赖的 audit_events 与 local_daily_closes（完整迁移的集成测试在两端 App 里）。 */
async function withDatabase(
  operation: (connection: SystemSqliteConnection) => Promise<void>,
): Promise<void> {
  const connection = new SystemSqliteConnection(new DatabaseSync(":memory:"));
  try {
    await connection.exec(`
      CREATE TABLE audit_events (
        event_id TEXT PRIMARY KEY,
        event_type TEXT NOT NULL,
        occurred_at_iso TEXT NOT NULL,
        order_guid TEXT NULL,
        correlation_id TEXT NOT NULL,
        payload_json TEXT NOT NULL,
        uploaded_at_iso TEXT NULL,
        delivery_state TEXT NOT NULL DEFAULT 'pending',
        attempt_count INTEGER NOT NULL DEFAULT 0,
        next_attempt_at_iso TEXT NULL,
        last_error_code TEXT NULL,
        scope_store_code TEXT NULL,
        scope_device_code TEXT NULL
      );
      CREATE TABLE local_daily_closes (
        close_id TEXT PRIMARY KEY,
        store_code TEXT NOT NULL,
        device_code TEXT NOT NULL,
        business_date TEXT NOT NULL,
        saved_at_iso TEXT NOT NULL,
        state TEXT NOT NULL
      );
    `);
    await connection.exec(CASH_MOVEMENT_TABLE_SQL);
    await operation(connection);
  } finally {
    await connection.close();
  }
}
