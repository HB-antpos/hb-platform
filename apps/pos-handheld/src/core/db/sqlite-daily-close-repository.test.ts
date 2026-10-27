import assert from "node:assert/strict";
import { DatabaseSync, type SQLInputValue } from "node:sqlite";
import test from "node:test";

import {
  AUD_CASH_DENOMINATIONS_CENTS,
  normalizeDailyCloseCounts,
  type DailyCloseArchive,
  type DailyCloseScope,
} from "@hb/pos-domain/core/contracts/daily-close";

import { applyMigrations, POS_DATABASE_MIGRATIONS } from "./migrations";
import { SqliteDailyCloseRepository } from "@hb/pos-db/core/db/sqlite-daily-close-repository";
import { createSqliteRepositories } from "./sqlite-repositories";
import type {
  SqliteConnectionPort,
  SqlRunResult,
  SqlValue,
} from "@hb/pos-db/core/db/types";

const T0 = "2026-07-28T00:00:00.000Z";
const T1 = "2026-07-28T23:59:59.000Z";

const scope: DailyCloseScope = {
  businessDate: "2026-07-28",
  periodFromIso: T0,
  periodToIso: "2026-07-29T00:00:00.000Z",
  storeCode: "STORE-1",
  deviceCode: "DEVICE-1",
};

test("M17 无损升级 M6 日结并允许同日保存多个冻结归档", async () => {
  await withDatabase(async (connection) => {
    await applyMigrations(
      connection,
      () => T0,
      POS_DATABASE_MIGRATIONS.filter((migration) => migration.version <= 16),
    );
    await connection.run(
      `INSERT INTO local_daily_closes (
        close_id, business_date, store_code, device_code, state,
        expected_cash_cents, counted_cash_cents, variance_cents,
        created_at_iso, closed_at_iso
      ) VALUES (
        'legacy-close', '2026-07-27', 'STORE-1', 'DEVICE-1', 'closed',
        1200, 1250, 50, ?, ?
      )`,
      [T0, T1],
    );
    await connection.run(
      `INSERT INTO daily_close_totals (
        close_id, tender_method, direction, amount_cents
      ) VALUES ('legacy-close', 'cash', 'sale', 1500)`,
    );
    await connection.run(
      `INSERT INTO daily_close_totals (
        close_id, tender_method, direction, amount_cents
      ) VALUES ('legacy-close', 'cash', 'refund', 300)`,
    );
    await connection.run(
      `INSERT INTO cash_denominations (
        close_id, denomination_cents, quantity
      ) VALUES ('legacy-close', 500, 2)`,
    );
    await connection.run(
      `INSERT INTO local_daily_closes (
        close_id, business_date, store_code, device_code, state,
        expected_cash_cents, counted_cash_cents, variance_cents,
        created_at_iso, closed_at_iso
      ) VALUES (
        'legacy-invalid', '', ?, '', 'unknown',
        9223372036854775807, 9223372036854775807,
        9223372036854775807, 'not-an-iso', NULL
      )`,
      ["S".repeat(129)],
    );
    await connection.run(
      `INSERT INTO daily_close_totals (
        close_id, tender_method, direction, amount_cents
      ) VALUES (
        'legacy-invalid', 'cash', 'refund', -9223372036854775808
      )`,
    );
    await connection.run(
      `INSERT INTO cash_denominations (
        close_id, denomination_cents, quantity
      ) VALUES (
        'legacy-invalid', 10000, 9223372036854775807
      )`,
    );
    await connection.run(
      `INSERT INTO local_daily_closes (
        close_id, business_date, store_code, device_code, state,
        expected_cash_cents, counted_cash_cents, variance_cents,
        created_at_iso, closed_at_iso
      ) VALUES (
        'legacy-nul', '2026-07-26',
        'STORE' || char(0) || 'HIDDEN',
        'DEVICE' || char(0) || 'HIDDEN',
        'closed', 0, 0, 0, ?, ?
      )`,
      [T0, T1],
    );

    await applyMigrations(
      connection,
      () => T1,
      POS_DATABASE_MIGRATIONS.filter((migration) => migration.version <= 17),
    );
    assert.equal(await schemaVersion(connection), 17);

    const repository = new SqliteDailyCloseRepository(connection);
    const legacy = await repository.getArchive("legacy-close");
    assert.equal(legacy?.closeId, "legacy-close");
    assert.equal(legacy?.businessDate, "2026-07-27");
    assert.equal(legacy?.expectedCashCents, 1200);
    assert.equal(legacy?.countedCashCents, 1250);
    assert.equal(legacy?.varianceCents, 50);
    assert.equal(legacy?.denominations.length, 11);
    assert.deepEqual(
      legacy?.tenders.find((item) => item.method === "cash"),
      {
        method: "cash",
        salesCents: 1500,
        refundCents: -300,
        netCents: 1200,
      },
    );
    const invalidLegacy = await repository.getArchive("legacy-invalid");
    assert.equal(invalidLegacy?.businessDate, "1970-01-01");
    assert.equal(invalidLegacy?.storeCode, "legacy-unknown-store");
    assert.equal(invalidLegacy?.deviceCode, "legacy-unknown-device");
    assert.equal(invalidLegacy?.expectedCashCents, 0);
    assert.equal(invalidLegacy?.countedCashCents, 0);
    assert.equal(invalidLegacy?.savedAtIso, "1970-01-01T00:00:00.000Z");
    assert.equal(
      invalidLegacy?.denominations.find(
        (item) => item.denominationCents === 10_000,
      )?.quantity,
      0,
    );
    const nulLegacy = await repository.getArchive("legacy-nul");
    assert.equal(nulLegacy?.storeCode, "legacy-unknown-store");
    assert.equal(nulLegacy?.deviceCode, "legacy-unknown-device");

    // 保存新存档走最新表结构（含 M54 的 cash_rounding_cents 列与重建后的触发器）；
    // 旧 M6 数据在剩余迁移之后仍应可读。
    await applyMigrations(connection, () => T1);
    assert.equal(
      (await repository.getArchive("legacy-close"))?.cashRoundingCents,
      0,
    );

    const first = archive("close-a", "audit-close-a");
    const second = archive("close-b", "audit-close-b");
    assert.equal((await repository.saveArchive(first)).replayed, false);
    assert.equal((await repository.saveArchive(first)).replayed, true);
    assert.equal((await repository.saveArchive(second)).replayed, false);

    const sameDay = await repository.listArchives({
      storeCode: scope.storeCode,
      deviceCode: scope.deviceCode,
      businessDate: scope.businessDate,
      limit: 20,
    });
    assert.deepEqual(
      sameDay.map((item) => item.closeId),
      ["close-b", "close-a"],
    );

    await assert.rejects(
      connection.run(
        `UPDATE local_daily_closes
         SET counted_cash_cents = counted_cash_cents + 1
         WHERE close_id = 'close-a'`,
      ),
      /DAILY_CLOSE_ARCHIVE_IMMUTABLE/,
    );
    await assert.rejects(
      connection.run(
        "DELETE FROM local_daily_closes WHERE close_id = 'close-a'",
      ),
      /DAILY_CLOSE_DELETE_FORBIDDEN/,
    );
  });
});

test("summarize 仅统计同门店设备半开区间内的本地完成订单，退款保持负分币", async () => {
  await withDatabase(async (connection) => {
    await applyMigrations(connection, () => T0);
    await seedOrder(connection, {
      orderGuid: "sale",
      sequence: 1,
      soldAtIso: T0,
      state: "PendingSync",
      deviceCode: "DEVICE-1",
      lineKind: "sale",
      quantity: "1",
      tenders: [
        ["cash", 1000],
        ["card", 500],
      ],
    });
    await seedOrder(connection, {
      orderGuid: "refund",
      sequence: 2,
      soldAtIso: T1,
      state: "Blocked403",
      deviceCode: "DEVICE-1",
      lineKind: "return",
      quantity: "1.75",
      tenders: [
        ["cash", -200],
        ["voucher", -300],
      ],
    });
    await seedOrder(connection, {
      orderGuid: "draft",
      sequence: 3,
      soldAtIso: T0,
      state: "Draft",
      deviceCode: "DEVICE-1",
      lineKind: "return",
      quantity: "99",
      tenders: [["cash", -9999]],
    });
    await seedOrder(connection, {
      orderGuid: "other-device",
      sequence: 4,
      soldAtIso: T0,
      state: "Synced",
      deviceCode: "DEVICE-2",
      lineKind: "sale",
      quantity: "1",
      tenders: [["cash", 9999]],
    });
    await seedOrder(connection, {
      orderGuid: "period-end",
      sequence: 5,
      soldAtIso: scope.periodToIso,
      state: "Rejected",
      deviceCode: "DEVICE-1",
      lineKind: "sale",
      quantity: "1",
      tenders: [["cash", 9999]],
    });

    const summary = await new SqliteDailyCloseRepository(connection).summarize(
      scope,
    );
    assert.equal(summary.orderCount, 2);
    assert.equal(summary.returnQuantity, "1.75");
    assert.equal(summary.expectedCashCents, 800);
    assert.deepEqual(summary.tenders, [
      {
        method: "cash",
        salesCents: 1000,
        refundCents: -200,
        netCents: 800,
      },
      {
        method: "card",
        salesCents: 500,
        refundCents: 0,
        netCents: 500,
      },
      {
        method: "voucher",
        salesCents: 0,
        refundCents: -300,
        netCents: -300,
      },
    ]);
  });
});

test("summarize 的应有现金 = 现金净额 + 逐单 5 分进位（销售进位、退款反向、混合多笔现金先合并）", async () => {
  await withDatabase(async (connection) => {
    await applyMigrations(connection, () => T0);
    const seed = (
      orderGuid: string,
      sequence: number,
      lineKind: "sale" | "return",
      tenders: readonly (readonly ["cash" | "card" | "voucher", number])[],
    ) =>
      seedOrder(connection, {
        orderGuid,
        sequence,
        soldAtIso: T0,
        state: "PendingSync",
        deviceCode: "DEVICE-1",
        lineKind,
        quantity: "1",
        tenders,
      });
    // 1003 → 收 1005，进位 +2。
    await seed("round-up", 1, "sale", [["cash", 1003]]);
    // 2001 → 收 2000，进位 -1。
    await seed("round-down", 2, "sale", [["cash", 2001]]);
    // 混合支付里两笔现金 301 + 402 = 703 → 收 705，进位 +2（必须先合并再进位，分开算会得 0 + -2）。
    await seed("mixed", 3, "sale", [
      ["cash", 301],
      ["cash", 402],
      ["card", 500],
    ]);
    // 退款 -1003 → 付出 1005，进位 -2。
    await seed("refund", 4, "return", [["cash", -1003]]);
    // 已是 5 分整数倍、纯刷卡订单都没有进位。
    await seed("exact", 5, "sale", [["cash", 1000]]);
    await seed("card-only", 6, "sale", [["card", 777]]);

    const summary = await new SqliteDailyCloseRepository(connection).summarize(
      scope,
    );
    const cash = summary.tenders.find((item) => item.method === "cash");
    // 现金净额保持订单精确金额：1003 + 2001 + 703 + 1000 - 1003 = 3704。
    assert.equal(cash?.netCents, 3704);
    assert.equal(summary.cashRoundingCents, 1); // +2 -1 +2 -2 +0
    assert.equal(summary.expectedCashCents, 3705);
  });
});

test("带现金进位的日结可以归档，读回一致；应有现金与净额+进位不符、进位超出订单数上限都被拒绝", async () => {
  await withDatabase(async (connection) => {
    await applyMigrations(connection, () => T0);
    const repository = new SqliteDailyCloseRepository(connection);

    // 进位 +2：归档转换触发器必须接受 expected = 净额 + 进位（旧触发器会拒绝）。
    const saved = await repository.saveArchive(
      archive("close-rounded", "audit-close-rounded", 2),
    );
    assert.equal(saved.archive.cashRoundingCents, 2);
    assert.equal(saved.archive.expectedCashCents, 802);
    const readBack = await repository.getArchive("close-rounded");
    assert.equal(readBack?.cashRoundingCents, 2);
    assert.equal(readBack?.expectedCashCents, 802);
    assert.equal(
      await scalar(
        connection,
        `SELECT cash_rounding_cents AS count
         FROM local_daily_closes WHERE close_id = 'close-rounded'`,
      ),
      2,
    );

    // 声称进位 2 却把应有现金写成净额 800：与 净额+进位 不符。
    const base = archive("close-mismatch", "audit-close-mismatch", 2);
    await assert.rejects(
      async () =>
        repository.saveArchive({
          ...base,
          archive: { ...base.archive, expectedCashCents: 800 },
        }),
      /expected cash does not match cash net plus rounding/,
    );

    // 订单数 2 单，进位最多 ±4 分：5 分一定是数据有问题。
    await assert.rejects(
      async () =>
        repository.saveArchive(
          archive("close-too-much", "audit-close-too-much", 5),
        ),
      /cash rounding exceeds/,
    );
    assert.equal(await repository.getArchive("close-mismatch"), null);
    assert.equal(await repository.getArchive("close-too-much"), null);
  });
});

test("M54 升级：旧存档补 cash_rounding_cents = 0，应有现金口径不变，且归档字段不可变", async () => {
  await withDatabase(async (connection) => {
    const previous = POS_DATABASE_MIGRATIONS.filter(
      (migration) => migration.version <= 53,
    );
    await applyMigrations(connection, () => T0, previous);
    // 升级前的原生存档：按 M53 的表结构、走触发器认可的完整归档流程写入（没有 cash_rounding_cents 列）。
    await connection.run(
      `INSERT INTO audit_events (
        event_id, event_type, occurred_at_iso, order_guid,
        correlation_id, payload_json, uploaded_at_iso
      ) VALUES (
        'audit-pre-m54', 'DAILY_CLOSE_SAVE', ?, NULL, 'close-pre-m54', '{}', NULL
      )`,
      [T1],
    );
    await connection.run(
      `INSERT INTO local_daily_closes (
        close_id, business_date, period_from_iso, period_to_iso,
        store_code, device_code, saved_cashier_id, saved_cashier_name,
        order_count, return_quantity, expected_cash_cents,
        counted_cash_cents, notes_subtotal_cents, coins_subtotal_cents,
        variance_cents, terminal_audit_event_id, saved_at_iso,
        source_kind, state
      ) VALUES (
        'close-pre-m54', ?, ?, ?, 'STORE-1', 'DEVICE-1', 'cashier-1',
        'Cashier One', 1, '0', 800, 800, 0, 800, 0, 'audit-pre-m54', ?,
        'native', 'Preparing'
      )`,
      [scope.businessDate, scope.periodFromIso, scope.periodToIso, T1],
    );
    for (const [method, net] of [
      ["cash", 800],
      ["card", 0],
      ["voucher", 0],
    ] as const) {
      await connection.run(
        `INSERT INTO daily_close_totals (
          close_id, tender_method, sales_cents, refund_cents, net_cents
        ) VALUES ('close-pre-m54', ?, ?, 0, ?)`,
        [method, net, net],
      );
    }
    for (const denomination of AUD_CASH_DENOMINATIONS_CENTS) {
      const quantity = denomination === 200 ? 4 : 0;
      await connection.run(
        `INSERT INTO cash_denominations (
          close_id, denomination_cents, quantity, subtotal_cents
        ) VALUES ('close-pre-m54', ?, ?, ?)`,
        [denomination, quantity, denomination * quantity],
      );
    }
    await connection.run(
      `UPDATE local_daily_closes SET state = 'Archived'
       WHERE close_id = 'close-pre-m54'`,
    );

    await applyMigrations(connection, () => T1);
    assert.equal(
      await schemaVersion(connection),
      POS_DATABASE_MIGRATIONS.at(-1)?.version,
    );
    assert.equal(
      await scalar(
        connection,
        `SELECT cash_rounding_cents AS count
         FROM local_daily_closes WHERE close_id = 'close-pre-m54'`,
      ),
      0,
    );
    // 已归档的存档不能事后改进位（不可变触发器包含新列）。
    await assert.rejects(
      connection.run(
        `UPDATE local_daily_closes SET cash_rounding_cents = 5
         WHERE close_id = 'close-pre-m54'`,
      ),
      /DAILY_CLOSE_ARCHIVE_IMMUTABLE/,
    );
  });
});

test("summarize 的订单数、tender 与退货数量必须在同一 SQLite 快照读取", async () => {
  await withDatabase(async (connection) => {
    await applyMigrations(connection, () => T0);
    await seedOrder(connection, {
      orderGuid: "snapshot-sale",
      sequence: 1,
      soldAtIso: T0,
      state: "PendingSync",
      deviceCode: "DEVICE-1",
      lineKind: "sale",
      quantity: "1",
      tenders: [["cash", 100]],
    });
    const guarded = new SnapshotEnforcingConnection(connection);
    const summary = await new SqliteDailyCloseRepository(guarded).summarize(
      scope,
    );
    assert.equal(summary.orderCount, 1);
    assert.equal(summary.expectedCashCents, 100);
    assert.equal(guarded.transactionCount, 1);
  });
});

test("归档、11 种面额和 DAILY_CLOSE_SAVE 审计同事务提交且冲突重放失败关闭", async () => {
  await withDatabase(async (connection) => {
    await applyMigrations(connection, () => T0);
    const repository = new SqliteDailyCloseRepository(connection);
    const commit = archive("close-atomic", "audit-close-atomic");

    const saved = await repository.saveArchive(commit);
    assert.equal(saved.replayed, false);
    assert.equal(saved.archive.denominations.length, 11);
    assert.equal(
      await scalar(
        connection,
        `SELECT COUNT(*) AS count
         FROM audit_events
         WHERE event_id = 'audit-close-atomic'
           AND event_type = 'DAILY_CLOSE_SAVE'`,
      ),
      1,
    );

    const mismatched: typeof commit = {
      ...commit,
      archive: {
        ...commit.archive,
        countedCashCents: commit.archive.countedCashCents + 5,
        varianceCents: commit.archive.varianceCents + 5,
      },
    };
    await assert.rejects(
      async () => repository.saveArchive(mismatched),
      /cash count facts|Daily close replay does not match/,
    );

    await connection.run(
      `INSERT INTO audit_events (
        event_id, event_type, occurred_at_iso, order_guid,
        correlation_id, payload_json, uploaded_at_iso
      ) VALUES (
        'occupied-audit', 'OTHER', ?, NULL, 'other', '{}', NULL
      )`,
      [T0],
    );
    const failed = archive("close-rollback", "occupied-audit");
    await assert.rejects(repository.saveArchive(failed));
    assert.equal(await repository.getArchive("close-rollback"), null);
  });
});

test("真实 SQLite：日结非订单审计冻结归档 scope，且只由相同终端投递", async () => {
  await withDatabase(async (connection) => {
    await applyMigrations(connection, () => T0);
    const commit = archive("close-audit-scope", "audit-close-audit-scope");
    const repository = new SqliteDailyCloseRepository(connection);

    await repository.saveArchive(commit);

    const persisted = await connection.getFirst<{
      store_code: unknown;
      device_code: unknown;
    }>(
      `SELECT scope_store_code AS store_code, scope_device_code AS device_code
       FROM audit_events
       WHERE event_id = ?`,
      [commit.audit.eventId],
    );
    assert.equal(persisted?.store_code, commit.archive.storeCode);
    assert.equal(persisted?.device_code, commit.archive.deviceCode);

    const repositories = createSqliteRepositories(connection, {
      nowIso: () => "2026-07-29T00:00:00.000Z",
      createLeaseId: () => "audit-delivery-lease",
      encryptor: {
        async encrypt(plaintext) {
          return new TextEncoder().encode(plaintext);
        },
        async decrypt(ciphertext) {
          return new TextDecoder().decode(ciphertext);
        },
      },
      auditScope: {
        storeCode: commit.archive.storeCode,
        deviceCode: commit.archive.deviceCode,
      },
    });
    assert.deepEqual(
      (await repositories.auditDelivery.listReady(10)).map((event) => event.eventId),
      [commit.audit.eventId],
    );
    const otherTerminal = createSqliteRepositories(connection, {
      nowIso: () => "2026-07-29T00:00:00.000Z",
      createLeaseId: () => "other-audit-delivery-lease",
      encryptor: {
        async encrypt(plaintext) {
          return new TextEncoder().encode(plaintext);
        },
        async decrypt(ciphertext) {
          return new TextDecoder().decode(ciphertext);
        },
      },
      auditScope: {
        storeCode: commit.archive.storeCode,
        deviceCode: "DEVICE-OTHER",
      },
    });
    assert.deepEqual(await otherTerminal.auditDelivery.listReady(10), []);
  });
});

function archive(
  closeId: string,
  auditId: string,
  cashRoundingCents = 0,
): Parameters<SqliteDailyCloseRepository["saveArchive"]>[0] {
  const denominations = normalizeDailyCloseCounts([
    { denominationCents: 10_000, quantity: 1 },
    { denominationCents: 500, quantity: 2 },
    { denominationCents: 200, quantity: 1 },
  ]);
  const notesSubtotalCents = denominations
    .filter((item) => item.denominationCents >= 500)
    .reduce((sum, item) => sum + item.subtotalCents, 0);
  const coinsSubtotalCents = denominations
    .filter((item) => item.denominationCents < 500)
    .reduce((sum, item) => sum + item.subtotalCents, 0);
  const countedCashCents = notesSubtotalCents + coinsSubtotalCents;
  const archiveValue: DailyCloseArchive = {
    ...scope,
    closeId,
    savedCashierId: "cashier-1",
    savedCashierName: "Cashier One",
    savedAtIso:
      closeId === "close-b"
        ? "2026-07-28T23:59:59.200Z"
        : "2026-07-28T23:59:59.100Z",
    orderCount: 2,
    returnQuantity: "1.75",
    tenders: [
      {
        method: "cash",
        salesCents: 1000,
        refundCents: -200,
        netCents: 800,
      },
      {
        method: "card",
        salesCents: 500,
        refundCents: 0,
        netCents: 500,
      },
      {
        method: "voucher",
        salesCents: 0,
        refundCents: -300,
        netCents: -300,
      },
    ],
    cashRoundingCents,
    // 应有现金 = 现金净额 800 + 现金进位。
    expectedCashCents: 800 + cashRoundingCents,
    denominations,
    notesSubtotalCents,
    coinsSubtotalCents,
    countedCashCents,
    varianceCents: countedCashCents - (800 + cashRoundingCents),
  };
  return {
    archive: archiveValue,
    audit: {
      eventId: auditId,
      eventType: "DAILY_CLOSE_SAVE",
      occurredAtIso: archiveValue.savedAtIso,
      orderGuid: null,
      correlationId: closeId,
      payload: {
        action: "daily-close-save",
        closeId,
        storeCode: scope.storeCode,
        deviceCode: scope.deviceCode,
      },
    },
  };
}

type SeedOrder = Readonly<{
  orderGuid: string;
  sequence: number;
  soldAtIso: string;
  state: string;
  deviceCode: string;
  lineKind: "sale" | "return";
  quantity: string;
  tenders: readonly (readonly [
    method: "cash" | "card" | "voucher",
    amountCents: number,
  ])[];
}>;

async function seedOrder(
  connection: SqliteConnectionPort,
  input: SeedOrder,
): Promise<void> {
  await connection.run(
    `INSERT INTO local_orders (
      order_guid, local_sequence, store_code, device_code,
      cashier_id, cashier_name, sold_at_iso, state,
      total_cents, discount_cents, actual_amount_cents,
      original_order_guid, created_at_iso, updated_at_iso
    ) VALUES (?, ?, 'STORE-1', ?, 'cashier-1', 'Cashier One', ?, ?,
      100, 0, 100, NULL, ?, ?)`,
    [
      input.orderGuid,
      input.sequence,
      input.deviceCode,
      input.soldAtIso,
      input.state,
      input.soldAtIso,
      input.soldAtIso,
    ],
  );
  await connection.run(
    `INSERT INTO local_order_lines (
      line_id, order_guid, line_sequence, product_code, item_number,
      lookup_code, display_name, quantity, unit_price_cents,
      discount_cents, actual_amount_cents, price_source, line_kind,
      return_source_key, original_order_guid, original_order_detail_guid,
      reference_code, sync_price_source
    ) VALUES (
      ?, ?, 1, 'PRODUCT-1', 'ITEM-1', 'LOOKUP-1', 'Product',
      ?, 100, 0, 100, 'catalog', ?, NULL, NULL, NULL, 'REF-1', 0
    )`,
    [`line-${input.orderGuid}`, input.orderGuid, input.quantity, input.lineKind],
  );
  for (const [index, [method, amountCents]] of input.tenders.entries()) {
    await connection.run(
      `INSERT INTO order_tenders (
        tender_guid, order_guid, method, amount_cents,
        payment_attempt_id, created_at_iso
      ) VALUES (?, ?, ?, ?, NULL, ?)`,
      [
        `tender-${input.orderGuid}-${index}`,
        input.orderGuid,
        method,
        amountCents,
        input.soldAtIso,
      ],
    );
  }
}

async function schemaVersion(
  connection: SqliteConnectionPort,
): Promise<number> {
  return Number(
    (
      await connection.getFirst<{ version: unknown }>(
        "SELECT MAX(version) AS version FROM schema_migrations",
      )
    )?.version,
  );
}

async function scalar(
  connection: SqliteConnectionPort,
  sql: string,
  parameters: readonly SqlValue[] = [],
): Promise<number> {
  return Number(
    (
      await connection.getFirst<{ count: unknown }>(sql, parameters)
    )?.count,
  );
}

class SystemSqliteConnection implements SqliteConnectionPort {
  public constructor(private readonly database: DatabaseSync) {
    this.database.exec("PRAGMA foreign_keys = ON;");
  }

  public async exec(sql: string): Promise<void> {
    this.database.exec(sql);
  }

  public async run(
    sql: string,
    parameters: readonly SqlValue[] = [],
  ): Promise<SqlRunResult> {
    const result = this.database
      .prepare(sql)
      .run(...parameters.map(toSqlInputValue));
    return {
      changes: Number(result.changes),
      lastInsertRowId: Number(result.lastInsertRowid),
    };
  }

  public async getFirst<T extends object>(
    sql: string,
    parameters: readonly SqlValue[] = [],
  ): Promise<T | null> {
    return (
      this.database
        .prepare(sql)
        .get(...parameters.map(toSqlInputValue)) as T | undefined
    ) ?? null;
  }

  public async getAll<T extends object>(
    sql: string,
    parameters: readonly SqlValue[] = [],
  ): Promise<readonly T[]> {
    return this.database
      .prepare(sql)
      .all(...parameters.map(toSqlInputValue)) as unknown as readonly T[];
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

class SnapshotEnforcingConnection implements SqliteConnectionPort {
  public transactionCount = 0;

  public constructor(private readonly connection: SqliteConnectionPort) {}

  public exec(sql: string): Promise<void> {
    return this.connection.exec(sql);
  }

  public run(
    sql: string,
    parameters: readonly SqlValue[] = [],
  ): Promise<SqlRunResult> {
    return this.connection.run(sql, parameters);
  }

  public getFirst<T extends object>(): Promise<T | null> {
    return Promise.reject(
      new Error("Daily close summary read escaped the snapshot."),
    );
  }

  public getAll<T extends object>(): Promise<readonly T[]> {
    return Promise.reject(
      new Error("Daily close summary read escaped the snapshot."),
    );
  }

  public withExclusiveTransaction<T>(
    operation: (transaction: SqliteConnectionPort) => Promise<T>,
  ): Promise<T> {
    this.transactionCount += 1;
    return this.connection.withExclusiveTransaction(operation);
  }

  public close(): Promise<void> {
    return this.connection.close();
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

function toSqlInputValue(value: SqlValue): SQLInputValue {
  return value as SQLInputValue;
}

assert.deepEqual(AUD_CASH_DENOMINATIONS_CENTS.length, 11);
