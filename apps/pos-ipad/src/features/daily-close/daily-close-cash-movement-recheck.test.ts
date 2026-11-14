import assert from "node:assert/strict";
import { DatabaseSync, type SQLInputValue } from "node:sqlite";
import test from "node:test";

import { SqliteCashMovementRepository } from "@hb/pos-db/core/db/sqlite-cash-movement-repository";
import { SqliteDailyCloseRepository } from "@hb/pos-db/core/db/sqlite-daily-close-repository";
import type { SqliteConnectionPort, SqlRunResult, SqlValue } from "@hb/pos-db/core/db/types";
import { buildCashMovement } from "@hb/pos-domain/core/contracts/cash-movement";

import { applyMigrations } from "../../core/db/migrations";

import { DailyClosePresenter } from "./daily-close-presenter";

/**
 * 日结的「重新核对」：点钞期间新增订单或新增一笔现金存入 / 取出，走的是同一条路径——
 * 日结页持有的是 load() 时的汇总快照，点「刷新」（presenter.load）才会重新汇总；
 * 汇总（订单 tender、分期、进位、存入 / 取出）都在同一个 summarizeSnapshot 事务里读，所以两者行为一致。
 * 这里用真实迁移 + 真实 SQLite 仓储证明：两种新增都同样触发（也只在 load 时触发）重新核对，已点的张数保留，差额重算。
 */

const TZ = "Australia/Brisbane";
const DATE = "2026-11-14";
const STORE = "STORE-1";
const DEVICE = "DEVICE-1";

let counter = 0;
const uuid = (): string => `00000000-0000-4000-8000-${String(++counter).padStart(12, "0")}`;

async function withPresenter(
  operation: (context: {
    presenter: DailyClosePresenter;
    connection: SqliteConnectionPort;
    cash: SqliteCashMovementRepository;
  }) => Promise<void>,
): Promise<void> {
  const connection = new SystemSqliteConnection(new DatabaseSync(":memory:"));
  try {
    await applyMigrations(connection, () => "2026-11-14T00:00:00.000Z");
    const cash = new SqliteCashMovementRepository(connection);
    const presenter = new DailyClosePresenter({
      businessTimeZone: TZ,
      createId: uuid,
      audit: { append: async () => undefined },
      identity: {
        cashierId: "cashier-1",
        cashierName: "Cashier One",
        userGuid: null,
        deviceCode: DEVICE,
        permissions: [
          "Permissions.PosTerminal.DailyClose.View",
          "Permissions.PosTerminal.DailyClose.Save",
          "Permissions.PosTerminal.DailyClose.Reprint",
        ],
        storeCode: STORE,
      },
      initialBusinessDate: DATE,
      now: () => new Date("2026-11-14T08:00:00.000Z"),
      printer: { print: async () => undefined },
      receiptLocale: "en",
      receiptPaper: "80mm",
      repository: new SqliteDailyCloseRepository(connection),
      storeName: "Brisbane",
    });
    try {
      await operation({ presenter, connection, cash });
    } finally {
      presenter.destroy();
    }
  } finally {
    await connection.close();
  }
}

async function addCashOrder(connection: SqliteConnectionPort, orderGuid: string, cents: number): Promise<void> {
  const soldAtIso = "2026-11-14T02:00:00.000Z";
  await connection.run(
    `INSERT INTO local_orders (
      order_guid, local_sequence, store_code, device_code, cashier_id, cashier_name, sold_at_iso, state,
      total_cents, discount_cents, actual_amount_cents, original_order_guid, created_at_iso, updated_at_iso
    ) VALUES (?, ?, ?, ?, 'cashier-1', 'Cashier One', ?, 'PendingSync', ?, 0, ?, NULL, ?, ?)`,
    [orderGuid, ++counter, STORE, DEVICE, soldAtIso, cents, cents, soldAtIso, soldAtIso],
  );
  await connection.run(
    `INSERT INTO local_order_lines (
      line_id, order_guid, line_sequence, product_code, item_number, lookup_code, display_name, quantity,
      unit_price_cents, discount_cents, actual_amount_cents, price_source, line_kind, return_source_key,
      original_order_guid, original_order_detail_guid, reference_code, sync_price_source
    ) VALUES (?, ?, 1, 'P1', 'I1', 'L1', 'Product', '1', ?, 0, ?, 'catalog', 'sale', NULL, NULL, NULL, 'REF', 0)`,
    [`line-${orderGuid}`, orderGuid, cents, cents],
  );
  await connection.run(
    `INSERT INTO order_tenders (tender_guid, order_guid, method, amount_cents, payment_attempt_id, created_at_iso)
     VALUES (?, ?, 'cash', ?, NULL, ?)`,
    [`tender-${orderGuid}`, orderGuid, cents, soldAtIso],
  );
}

function addMovement(
  cash: SqliteCashMovementRepository,
  reasonCode: "ChangeTopUp" | "SafeDrop",
  amountCents: number,
) {
  return cash.append(
    buildCashMovement({
      movementGuid: uuid(),
      auditEventId: uuid(),
      entry: { kind: "movement", reasonCode, amountCents },
      note: null,
      occurredAtIso: "2026-11-14T03:00:00.000Z",
      businessTimeZone: TZ,
      storeCode: STORE,
      deviceCode: DEVICE,
      cashier: { cashierId: "cashier-1", cashierName: "Cashier One", userGuid: null },
    }),
  );
}

test("点钞期间新增一笔存入：与新增订单同样要点「刷新」才重新核对，之后应有现金 / 差额按新汇总重算，已点张数保留", async () => {
  await withPresenter(async ({ presenter, cash }) => {
    await presenter.load();
    assert.equal(presenter.getState().summary?.expectedCashCents, 0);
    presenter.setCount(10_000, 1); // 已点 $100
    assert.equal(presenter.getState().varianceCents, 10_000);

    await addMovement(cash, "ChangeTopUp", 5_000);
    // 没刷新之前页面仍是旧快照（订单也是这样）。
    assert.equal(presenter.getState().summary?.cashInCents, 0);
    assert.equal(presenter.getState().summary?.expectedCashCents, 0);

    await presenter.load();
    const state = presenter.getState();
    assert.equal(state.summary?.cashInCents, 5_000);
    assert.equal(state.summary?.expectedCashCents, 5_000);
    assert.equal(state.countedCashCents, 10_000, "已点张数保留");
    assert.equal(state.varianceCents, 5_000, "差额按新应有现金重算");
  });
});

test("点钞期间新增一笔取出：重新核对后取出合计与应有现金（减去取出）同步更新", async () => {
  await withPresenter(async ({ presenter, connection, cash }) => {
    await addCashOrder(connection, "order-base", 20_000);
    await presenter.load();
    assert.equal(presenter.getState().summary?.expectedCashCents, 20_000);
    presenter.setCount(10_000, 1);

    await addMovement(cash, "SafeDrop", 8_000);
    assert.equal(presenter.getState().summary?.cashOutCents, 0);
    await presenter.load();
    assert.equal(presenter.getState().summary?.cashOutCents, 8_000);
    assert.equal(presenter.getState().summary?.expectedCashCents, 12_000);
    assert.equal(presenter.getState().varianceCents, -2_000);
  });
});

test("新增订单与新增存取走同一机制：各自刷新前都不变，一次刷新同时反映两者", async () => {
  await withPresenter(async ({ presenter, connection, cash }) => {
    await presenter.load();
    await addCashOrder(connection, "order-late", 3_000);
    await addMovement(cash, "ChangeTopUp", 1_000);
    const before = presenter.getState().summary;
    assert.equal(before?.expectedCashCents, 0);
    assert.equal(before?.orderCount, 0);

    await presenter.load();
    const after = presenter.getState().summary;
    assert.equal(after?.orderCount, 1);
    assert.equal(after?.cashInCents, 1_000);
    assert.equal(after?.expectedCashCents, 4_000, "订单现金 3000 + 存入 1000");
  });
});

class SystemSqliteConnection implements SqliteConnectionPort {
  public constructor(protected readonly database: DatabaseSync) {
    this.database.exec("PRAGMA foreign_keys = ON;");
  }
  public async exec(sql: string): Promise<void> {
    this.database.exec(sql);
  }
  public async run(sql: string, parameters: readonly SqlValue[] = []): Promise<SqlRunResult> {
    const result = this.database.prepare(sql).run(...parameters.map((value) => value as SQLInputValue));
    return { changes: Number(result.changes), lastInsertRowId: Number(result.lastInsertRowid) };
  }
  public async getFirst<T extends object>(sql: string, parameters: readonly SqlValue[] = []): Promise<T | null> {
    return (this.database.prepare(sql).get(...parameters.map((value) => value as SQLInputValue)) as T | undefined) ?? null;
  }
  public async getAll<T extends object>(sql: string, parameters: readonly SqlValue[] = []): Promise<readonly T[]> {
    return this.database.prepare(sql).all(...parameters.map((value) => value as SQLInputValue)) as unknown as readonly T[];
  }
  public async withExclusiveTransaction<T>(operation: (transaction: SqliteConnectionPort) => Promise<T>): Promise<T> {
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
