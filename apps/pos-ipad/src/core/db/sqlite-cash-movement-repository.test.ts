import assert from "node:assert/strict";
import { DatabaseSync, type SQLInputValue } from "node:sqlite";
import test from "node:test";

import { CASH_MOVEMENT_MIGRATION_SQL } from "@hb/pos-db/core/db/cash-movement-schema";
import { SqliteCashMovementRepository } from "@hb/pos-db/core/db/sqlite-cash-movement-repository";
import { SqliteDailyCloseRepository } from "@hb/pos-db/core/db/sqlite-daily-close-repository";
import type { SqliteConnectionPort, SqlRunResult, SqlValue } from "@hb/pos-db/core/db/types";
import {
  CashMovementRepositoryError,
  buildCashMovement,
  cashMovementBusinessDate,
  type BuildCashMovementInput,
  type CashMovementCommit,
} from "@hb/pos-domain/core/contracts/cash-movement";
import { AUD_CASH_DENOMINATIONS_CENTS } from "@hb/pos-domain/core/contracts/daily-close";

import {
  buildDailyCloseArchiveCommit,
  businessDateInTimeZone,
  dailyCloseBusinessDayScope,
} from "../../features/daily-close/daily-close-domain";
import { applyMigrations, POS_DATABASE_MIGRATIONS } from "./migrations";
import { createSqliteRepositories } from "./sqlite-repositories";

/**
 * 现金存入 / 取出的真实迁移 + 真实 SQLite 集成测试（iPad 与手持同一份，随各自最新迁移版本运行）：
 * 迁移登记与升级、日结存档新列与重建的触发器、应有现金口径、冲正与日结的互锁、审计投递范围。
 */

const T0 = "2026-11-14T00:00:00.000Z";
const STORE = "STORE-1";
const DEVICE = "DEVICE-1";
const TZ = "Australia/Brisbane";
const scope = dailyCloseBusinessDayScope({
  businessDate: "2026-11-14",
  businessTimeZone: TZ,
  deviceCode: DEVICE,
  storeCode: STORE,
});

const CASH_MOVEMENT_MIGRATION = POS_DATABASE_MIGRATIONS.find((migration) =>
  migration.name.endsWith("_cash_movements"),
);

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
    cashier: { cashierId: "cashier-1", cashierName: "Cashier One", userGuid: null },
    ...overrides,
  });
}

test("迁移登记：现金进出迁移是最后一个迁移，SQL 与共享 DDL 字节一致，版本号连续", () => {
  assert.ok(CASH_MOVEMENT_MIGRATION);
  assert.equal(POS_DATABASE_MIGRATIONS.at(-1), CASH_MOVEMENT_MIGRATION);
  assert.equal(CASH_MOVEMENT_MIGRATION.sql, CASH_MOVEMENT_MIGRATION_SQL);
  const versions = POS_DATABASE_MIGRATIONS.map((migration) => migration.version);
  assert.deepEqual(versions, versions.map((_, index) => index + 1));
});

test("全新库应用全部迁移后：明细表、新列、触发器齐全，重复应用幂等", async () => {
  await withDatabase(async (connection) => {
    await applyMigrations(connection, () => T0);
    await applyMigrations(connection, () => T0);
    const tables = await connection.getAll<{ name: string }>(
      "SELECT name FROM sqlite_master WHERE type = 'table' AND name = 'local_cash_movements'",
    );
    assert.equal(tables.length, 1);
    const columns = (await connection.getAll<{ name: string }>("PRAGMA table_info('local_daily_closes')")).map((c) => c.name);
    assert.ok(columns.includes("cash_in_cents") && columns.includes("cash_out_cents"));
    const triggers = (await connection.getAll<{ name: string }>(
      "SELECT name FROM sqlite_master WHERE type = 'trigger' AND name LIKE 'trg_cash_movement_%'",
    )).map((row) => row.name).sort();
    assert.deepEqual(triggers, [
      "trg_cash_movement_immutable",
      "trg_cash_movement_no_delete",
      "trg_cash_movement_void_after_close_guard",
      "trg_cash_movement_void_insert_guard",
    ]);
    const versionRows = await connection.getAll<{ version: number }>(
      "SELECT version FROM schema_migrations WHERE version = ?",
      [CASH_MOVEMENT_MIGRATION?.version ?? -1],
    );
    assert.equal(versionRows.length, 1);
  });
});

test("升级：旧存档补 cash_in / cash_out = 0，读回与应有现金口径不变；新列与应有现金事后不可改", async () => {
  await withDatabase(async (connection) => {
    assert.ok(CASH_MOVEMENT_MIGRATION);
    const previous = POS_DATABASE_MIGRATIONS.filter(
      (migration) => migration.version < CASH_MOVEMENT_MIGRATION.version,
    );
    await applyMigrations(connection, () => T0, previous);
    await insertArchivedClose(connection, "close-old", { expected: 802 });

    await applyMigrations(connection, () => T0);
    const repository = new SqliteDailyCloseRepository(connection);
    const archive = await repository.getArchive("close-old");
    assert.equal(archive?.cashInCents, 0);
    assert.equal(archive?.cashOutCents, 0);
    assert.equal(archive?.expectedCashCents, 802);
    assert.equal(archive?.cashRoundingCents, 2);

    for (const column of ["cash_in_cents = 5", "cash_out_cents = 5", "expected_cash_cents = 1"]) {
      await assert.rejects(
        connection.run(`UPDATE local_daily_closes SET ${column} WHERE close_id = 'close-old'`),
        /DAILY_CLOSE_ARCHIVE_IMMUTABLE/u,
        column,
      );
    }
    // 升级后明细表为空，上传状态列与其余存档字段不受影响。
    assert.equal((await connection.getFirst<{ n: number }>("SELECT COUNT(*) AS n FROM local_cash_movements"))?.n, 0);
    const state = await connection.getFirst<{ upload_state: string }>(
      "SELECT upload_state FROM local_daily_closes WHERE close_id = 'close-old'",
    );
    assert.ok(state?.upload_state);
  });
});

test("归档转换触发器：应有现金必须 = 现金净额 + 分期现金 + 进位 + 存入 − 取出；漏掉、漏减、符号反了都被数据库拒绝", async () => {
  await withDatabase(async (connection) => {
    await applyMigrations(connection, () => T0);
    // 现金净额 800 + 分期现金 (600 − 100) + 进位 3 + 存入 1000 − 取出 200 = 2103。
    const prepare = async (closeId: string, expected: number) => {
      await prepareClose(connection, closeId, expected, { cashIn: 1_000, cashOut: 200 });
    };
    for (const [closeId, expected] of [
      ["bad-omit-both", 1_303], // 漏掉存入与取出
      ["bad-omit-out", 2_303], // 漏减取出
      ["bad-omit-in", 1_103], // 漏加存入
      ["bad-sign", 1_503], // 存入取出符号反了（−1000 + 200）
    ] as const) {
      await prepare(closeId, expected);
      await assert.rejects(
        connection.run(`UPDATE local_daily_closes SET state = 'Archived' WHERE close_id = ?`, [closeId]),
        /DAILY_CLOSE_ARCHIVE_INCOMPLETE/u,
        closeId,
      );
    }
    await prepare("good", 2_103);
    await connection.run(`UPDATE local_daily_closes SET state = 'Archived' WHERE close_id = 'good'`);
    // 负的存入 / 取出合计由 CHECK 拒绝（服务端 INVALID_CASH_MOVEMENT_TOTAL）。
    await assert.rejects(
      connection.run(
        `INSERT INTO local_daily_closes (
          close_id, business_date, period_from_iso, period_to_iso, store_code, device_code,
          saved_cashier_id, saved_cashier_name, order_count, return_quantity, expected_cash_cents,
          counted_cash_cents, notes_subtotal_cents, coins_subtotal_cents, variance_cents,
          terminal_audit_event_id, saved_at_iso, cash_in_cents, source_kind, state
        ) VALUES ('neg', '2026-11-14', ?, ?, 'S', 'D', 'c', 'c', 0, '0', 0, 0, 0, 0, 0, 'audit-good', ?, -1, 'native', 'Preparing')`,
        [scope.periodFromIso, scope.periodToIso, T0],
      ),
      /CHECK/iu,
    );
  });
});

test("端到端：明细 → 日结汇总（应有现金含存入 / 取出，冲正抵消，不看上传状态）→ 存档 → 读回；同日多次日结每次都含当天全部明细", async () => {
  await withDatabase(async (connection) => {
    await applyMigrations(connection, () => T0);
    const cash = new SqliteCashMovementRepository(connection);
    const closes = new SqliteDailyCloseRepository(connection);

    await cash.append(commitOf({ occurredAtIso: "2026-11-14T01:00:00.000Z" })); // ChangeTopUp 50.00
    const drop = (await cash.append(
      commitOf({
        occurredAtIso: "2026-11-14T01:10:00.000Z",
        entry: { kind: "movement", reasonCode: "SafeDrop", amountCents: 20_000 },
      }),
    )).movement;
    await cash.append(
      commitOf({
        occurredAtIso: "2026-11-14T01:20:00.000Z",
        entry: { kind: "movement", reasonCode: "PaidOut", amountCents: 1_250 },
        note: "milk",
      }),
    );
    // 设备时区的前一天 / 下一天与其它设备的记录不计入。
    await cash.append(commitOf({ occurredAtIso: "2026-11-13T13:59:59.999Z" }));
    await cash.append(commitOf({ occurredAtIso: "2026-11-14T14:00:00.000Z" }));
    await cash.append(commitOf({ deviceCode: "DEVICE-2", occurredAtIso: "2026-11-14T01:30:00.000Z" }));
    // 冲正 SafeDrop：抵消后取出只剩 PaidOut。
    await cash.append(
      commitOf({
        occurredAtIso: "2026-11-14T01:40:00.000Z",
        entry: { kind: "void", original: drop },
        note: "wrong amount",
      }),
    );
    // 上传状态不影响汇总：全部改成被拒 / 已同步。
    await connection.run("UPDATE local_cash_movements SET upload_state = 'rejected' WHERE amount_cents = 5000");
    await connection.run("UPDATE local_cash_movements SET upload_state = 'synced' WHERE amount_cents = 1250");

    const summary = await closes.summarize(scope);
    assert.equal(summary.cashInCents, 5_000);
    assert.equal(summary.cashOutCents, 1_250);
    assert.equal(summary.expectedCashCents, 5_000 - 1_250);
    // 与 summarizePeriod 同口径。
    assert.deepEqual(
      { in: (await cash.summarizePeriod(scope)).cashInCents, out: (await cash.summarizePeriod(scope)).cashOutCents },
      { in: 5_000, out: 1_250 },
    );

    const commit = buildDailyCloseArchiveCommit({
      auditEventId: uuid(),
      closeId: uuid(),
      counts: [{ denominationCents: 2_000, quantity: 1 }, { denominationCents: 500, quantity: 3 }],
      savedAtIso: "2026-11-14T08:00:00.000Z",
      savedCashierId: "cashier-1",
      savedCashierName: "Cashier One",
      savedUserGuid: null,
      summary,
    });
    assert.equal(commit.archive.cashInCents, 5_000);
    assert.equal(commit.archive.varianceCents, 3_500 - 3_750);
    const saved = await closes.saveArchive(commit);
    assert.equal(saved.archive.cashInCents, 5_000);
    assert.equal(saved.archive.cashOutCents, 1_250);
    assert.equal(saved.archive.expectedCashCents, 3_750);
    assert.deepEqual((await closes.getArchive(commit.archive.closeId))?.cashOutCents, 1_250);

    // 漏掉存入 / 取出的存档（应有现金没带上）被仓储拒绝。
    await assert.rejects(
      async () =>
        closes.saveArchive({
          archive: { ...commit.archive, closeId: uuid(), cashInCents: 0, cashOutCents: 0 },
          audit: { ...commit.audit, eventId: uuid() },
        }),
      /expected cash/iu,
    );

    // 日结之后又发生一笔：第二次日结同样含当天全部明细（不切段）。
    await cash.append(
      commitOf({
        occurredAtIso: "2026-11-14T09:00:00.000Z",
        entry: { kind: "movement", reasonCode: "OtherIn", amountCents: 700 },
        note: "float",
      }),
    );
    const second = await closes.summarize(scope);
    assert.equal(second.cashInCents, 5_700);
    assert.equal(second.cashOutCents, 1_250);
    assert.equal(second.expectedCashCents, 4_450);
    const secondCommit = buildDailyCloseArchiveCommit({
      auditEventId: uuid(),
      closeId: uuid(),
      counts: [{ denominationCents: 2_000, quantity: 1 }],
      savedAtIso: "2026-11-14T10:00:00.000Z",
      savedCashierId: "cashier-1",
      savedCashierName: "Cashier One",
      savedUserGuid: null,
      summary: second,
    });
    assert.equal((await closes.saveArchive(secondCommit)).archive.cashInCents, 5_700);
  });
});

test("冲正与日结互锁（真实库）：日结后不能冲正之前的记录，日结之后发生的可以；数据库触发器兜底", async () => {
  await withDatabase(async (connection) => {
    await applyMigrations(connection, () => T0);
    const cash = new SqliteCashMovementRepository(connection);
    const closes = new SqliteDailyCloseRepository(connection);
    const before = (await cash.append(commitOf({ occurredAtIso: "2026-11-14T01:00:00.000Z" }))).movement;
    const summary = await closes.summarize(scope);
    const commit = buildDailyCloseArchiveCommit({
      auditEventId: uuid(),
      closeId: uuid(),
      counts: [{ denominationCents: 5_000, quantity: 1 }],
      savedAtIso: "2026-11-14T02:00:00.000Z",
      savedCashierId: "cashier-1",
      savedCashierName: "Cashier One",
      savedUserGuid: null,
      summary,
    });
    await closes.saveArchive(commit);
    const after = (await cash.append(commitOf({ occurredAtIso: "2026-11-14T03:00:00.000Z" }))).movement;
    assert.equal(await cash.latestDailyCloseSavedAtIso(scope), "2026-11-14T02:00:00.000Z");

    await assert.rejects(
      cash.append(commitOf({ occurredAtIso: "2026-11-14T04:00:00.000Z", entry: { kind: "void", original: before }, note: "mistake" })),
      (error) => error instanceof CashMovementRepositoryError && error.denial === "DAILY_CLOSE_SAVED",
    );
    const voided = await cash.append(
      commitOf({ occurredAtIso: "2026-11-14T04:00:00.000Z", entry: { kind: "void", original: after }, note: "mistake" }),
    );
    assert.equal(voided.movement.voidsMovementGuid, after.movementGuid);
    assert.equal((await cash.listToday(scope)).find((m) => m.movementGuid === after.movementGuid)?.voidedByMovementGuid, voided.movement.movementGuid);
    // 绕过应用层直接插入对 before 的冲正：触发器拒绝。
    await assert.rejects(
      connection.run(
        `INSERT INTO local_cash_movements (
          movement_guid, direction, reason_code, amount_cents, voids_movement_guid, business_date,
          occurred_at_iso, cashier_id, cashier_name, display_number, store_code, device_code,
          audit_event_id, created_at_iso
        ) VALUES (?, 'Out', 'ChangeTopUp', 5000, ?, '2026-11-14', '2026-11-14T05:00:00.000Z', 'c', 'c',
          'CM-RAW', ?, ?, ?, '2026-11-14T05:00:00.000Z')`,
        [uuid(), before.movementGuid, STORE, DEVICE, commit.audit.eventId],
      ),
      /CASH_MOVEMENT_VOID_AFTER_DAILY_CLOSE|UNIQUE/iu,
    );
  });
});

test("business_date 与日结窗口同一营业时区：与 businessDateInTimeZone 逐点一致，落在对应日结窗口内", () => {
  const instants = [
    "2026-11-13T13:59:59.999Z",
    "2026-11-13T14:00:00.000Z",
    "2026-11-14T13:59:59.999Z",
    "2026-11-14T14:00:00.000Z",
    "2026-10-03T16:30:00.000Z", // 悉尼夏令时切换日前后
    "2027-01-01T00:00:00.000Z",
  ];
  for (const zone of ["Australia/Brisbane", "Australia/Sydney", "UTC", "America/Los_Angeles"]) {
    for (const iso of instants) {
      const fromMovement = cashMovementBusinessDate(iso, zone);
      assert.equal(fromMovement, businessDateInTimeZone(new Date(iso), zone), `${zone} ${iso}`);
      const window = dailyCloseBusinessDayScope({
        businessDate: fromMovement,
        businessTimeZone: zone,
        deviceCode: DEVICE,
        storeCode: STORE,
      });
      assert.ok(Date.parse(iso) >= Date.parse(window.periodFromIso));
      assert.ok(Date.parse(iso) < Date.parse(window.periodToIso));
    }
  }
});

test("审计：CASH_IN 在审计表里冻结终端 scope，只由相同终端投递；审计写入失败时记录整体回滚", async () => {
  await withDatabase(async (connection) => {
    await applyMigrations(connection, () => T0);
    const cash = new SqliteCashMovementRepository(connection);
    const { movement } = await cash.append(commitOf());
    const row = await connection.getFirst<{ scope_store_code: string; scope_device_code: string; event_type: string }>(
      "SELECT scope_store_code, scope_device_code, event_type FROM audit_events WHERE event_id = ?",
      [movement.auditEventId],
    );
    assert.deepEqual({ ...row }, { scope_store_code: STORE, scope_device_code: DEVICE, event_type: "CASH_IN" });

    const delivery = (storeCode: string, deviceCode: string) =>
      createSqliteRepositories(connection, {
        nowIso: () => "2026-11-15T00:00:00.000Z",
        createLeaseId: () => "lease",
        encryptor: {
          async encrypt(plaintext) {
            return new TextEncoder().encode(plaintext);
          },
          async decrypt(ciphertext) {
            return new TextDecoder().decode(ciphertext);
          },
        },
        auditScope: { storeCode, deviceCode },
      }).auditDelivery;
    const ready = await delivery(STORE, DEVICE).listReady(10);
    assert.deepEqual(ready.map((event) => event.eventType), ["CASH_IN"]);
    assert.deepEqual(await delivery(STORE, "OTHER").listReady(10), []);

    await connection.exec(`
      CREATE TRIGGER trg_test_cash_out_audit_fail BEFORE INSERT ON audit_events
      WHEN NEW.event_type = 'CASH_OUT' BEGIN SELECT RAISE(ABORT, 'AUDIT_BROKEN'); END;
    `);
    await assert.rejects(
      cash.append(commitOf({ entry: { kind: "movement", reasonCode: "SafeDrop", amountCents: 100 } })),
      /AUDIT_BROKEN/u,
    );
    assert.equal((await connection.getFirst<{ n: number }>("SELECT COUNT(*) AS n FROM local_cash_movements"))?.n, 1);
  });
});

// ---------------- 辅助 ----------------

async function prepareClose(
  connection: SqliteConnectionPort,
  closeId: string,
  expectedCashCents: number,
  movements: Readonly<{ cashIn: number; cashOut: number }>,
): Promise<void> {
  await connection.run(
    `INSERT INTO audit_events (
      event_id, event_type, occurred_at_iso, order_guid, correlation_id, payload_json, uploaded_at_iso
    ) VALUES (?, 'DAILY_CLOSE_SAVE', ?, NULL, ?, '{}', NULL)`,
    [`audit-${closeId}`, T0, closeId],
  );
  await connection.run(
    `INSERT INTO local_daily_closes (
      close_id, business_date, period_from_iso, period_to_iso,
      store_code, device_code, saved_cashier_id, saved_cashier_name,
      order_count, return_quantity, expected_cash_cents,
      counted_cash_cents, notes_subtotal_cents, coins_subtotal_cents,
      variance_cents, terminal_audit_event_id, saved_at_iso,
      cash_rounding_cents, installment_payment_count,
      installment_cash_sales_cents, installment_cash_refund_cents,
      cash_in_cents, cash_out_cents, source_kind, state
    ) VALUES (?, ?, ?, ?, 'STORE-1', 'DEVICE-1', 'cashier-1', 'Cashier One', 1, '0', ?, 0, 0, 0, ?, ?, ?, 3, 2, 600, -100, ?, ?, 'native', 'Preparing')`,
    [
      closeId, scope.businessDate, scope.periodFromIso, scope.periodToIso,
      expectedCashCents, -expectedCashCents, `audit-${closeId}`, T0, movements.cashIn, movements.cashOut,
    ],
  );
  for (const [method, net] of [["cash", 800], ["card", 0], ["voucher", 0]] as const) {
    await connection.run(
      `INSERT INTO daily_close_totals (close_id, tender_method, sales_cents, refund_cents, net_cents)
       VALUES (?, ?, ?, 0, ?)`,
      [closeId, method, net, net],
    );
  }
  for (const denomination of AUD_CASH_DENOMINATIONS_CENTS) {
    await connection.run(
      `INSERT INTO cash_denominations (close_id, denomination_cents, quantity, subtotal_cents) VALUES (?, ?, 0, 0)`,
      [closeId, denomination],
    );
  }
}

/** 旧版本库里（还没有 cash_in / cash_out 列）直接写入一个已归档的原生存档。 */
async function insertArchivedClose(
  connection: SqliteConnectionPort,
  closeId: string,
  input: Readonly<{ expected: number }>,
): Promise<void> {
  await connection.run(
    `INSERT INTO audit_events (
      event_id, event_type, occurred_at_iso, order_guid, correlation_id, payload_json, uploaded_at_iso
    ) VALUES (?, 'DAILY_CLOSE_SAVE', ?, NULL, ?, '{}', NULL)`,
    [`audit-${closeId}`, T0, closeId],
  );
  await connection.run(
    `INSERT INTO local_daily_closes (
      close_id, business_date, period_from_iso, period_to_iso,
      store_code, device_code, saved_cashier_id, saved_cashier_name,
      order_count, return_quantity, expected_cash_cents,
      counted_cash_cents, notes_subtotal_cents, coins_subtotal_cents,
      variance_cents, terminal_audit_event_id, saved_at_iso,
      cash_rounding_cents, installment_payment_count,
      installment_cash_sales_cents, installment_cash_refund_cents,
      source_kind, state
    ) VALUES (?, ?, ?, ?, 'STORE-1', 'DEVICE-1', 'cashier-1', 'Cashier One', 1, '0', ?, 0, 0, 0, ?, ?, ?, 2, 0, 0, 0, 'native', 'Preparing')`,
    [
      closeId, scope.businessDate, scope.periodFromIso, scope.periodToIso,
      input.expected, -input.expected, `audit-${closeId}`, T0,
    ],
  );
  for (const [method, net] of [["cash", 800], ["card", 0], ["voucher", 0]] as const) {
    await connection.run(
      `INSERT INTO daily_close_totals (close_id, tender_method, sales_cents, refund_cents, net_cents)
       VALUES (?, ?, ?, 0, ?)`,
      [closeId, method, net, net],
    );
  }
  for (const denomination of AUD_CASH_DENOMINATIONS_CENTS) {
    await connection.run(
      `INSERT INTO cash_denominations (close_id, denomination_cents, quantity, subtotal_cents) VALUES (?, ?, 0, 0)`,
      [closeId, denomination],
    );
  }
  await connection.run(`UPDATE local_daily_closes SET state = 'Archived' WHERE close_id = ?`, [closeId]);
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

  public async getFirst<T extends object>(sql: string, parameters: readonly SqlValue[] = []): Promise<T | null> {
    return (
      (this.database.prepare(sql).get(...parameters.map((value) => value as SQLInputValue)) as T | undefined) ?? null
    );
  }

  public async getAll<T extends object>(sql: string, parameters: readonly SqlValue[] = []): Promise<readonly T[]> {
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

async function withDatabase(
  operation: (connection: SystemSqliteConnection) => Promise<void>,
): Promise<void> {
  const connection = new SystemSqliteConnection(new DatabaseSync(":memory:"));
  try {
    await operation(connection);
  } finally {
    await connection.close();
  }
}
