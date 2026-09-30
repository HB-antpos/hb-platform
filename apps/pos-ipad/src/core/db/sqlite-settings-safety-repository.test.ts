import assert from "node:assert/strict";
import { DatabaseSync, type SQLInputValue } from "node:sqlite";
import test from "node:test";

import { SqliteCatalogSnapshotRepository } from "./catalog-repository";
import { applyMigrations } from "./migrations";
import { PosDatabase } from "./pos-database";
import { SqliteSettingsSafetyRepository } from "@hb/pos-db/core/db/sqlite-settings-safety-repository";
import type {
  SqliteConnectionPort,
  SqliteDriverPort,
  SqlRunResult,
  SqlValue,
} from "@hb/pos-db/core/db/types";

const NOW = "2026-07-28T00:00:00.000Z";

test("真实 SQLite：每类待处理事实均从持久状态计数，Rejected 仍保留待处理", async () => {
  await withMigratedDatabase(async (connection) => {
    await insertOrder(connection, "pending-sale", "CompletedLocal", "sale");
    await insertOrder(connection, "pending-return", "Rejected", "return");
    await insertOrder(connection, "payment-order", "Draft", "sale");
    await insertPaymentAttempt(connection, {
      attemptId: "pending-payment",
      orderGuid: "payment-order",
      state: "Unknown",
    });
    await insertOutbox(connection, "pending-outbox", "blocked403");
    await insertAudit(connection, "pending-audit", null);
    await insertPrintJob(connection, "pending-print", "Ambiguous");
    await insertDrawerEvent(connection, "pending-drawer", "Unknown");

    const snapshot = await new SqliteSettingsSafetyRepository(
      connection,
    ).read();

    assert.deepEqual(snapshot, {
      pendingDurableWriteCount: 4,
      pendingReturnCount: 1,
      pendingSaleCount: 2,
      unresolvedPaymentCount: 1,
      paymentConfigurationSensitiveOrderCount: 0,
    });
  });
});

test("真实 SQLite：仅明确终态和已上传事实排除，Approved 完成订单且被严格消费后清零", async () => {
  await withMigratedDatabase(async (connection) => {
    await insertOrder(connection, "synced-sale", "Synced", "sale");
    await insertOrder(connection, "synced-return", "Synced", "return");
    await insertOrder(connection, "approved-completed", "CompletedLocal", "sale");
    await insertPaymentAttempt(connection, {
      attemptId: "approved-consumed",
      orderGuid: "approved-completed",
      state: "Approved",
    });
    await insertTender(connection, {
      tenderGuid: "approved-consumed-tender",
      orderGuid: "approved-completed",
      paymentAttemptId: "approved-consumed",
    });
    await insertPaymentAttempt(connection, {
      attemptId: "declined-payment",
      orderGuid: "synced-sale",
      state: "Declined",
    });
    await insertPaymentAttempt(connection, {
      attemptId: "cancelled-payment",
      orderGuid: "synced-return",
      state: "Cancelled",
    });
    await insertOutbox(connection, "uploaded-outbox", "succeeded");
    await insertAudit(connection, "uploaded-audit", NOW);
    await insertPrintJob(connection, "printed-job", "Printed");
    await insertDrawerEvent(connection, "completed-drawer", "Completed");

    assert.deepEqual(
      await new SqliteSettingsSafetyRepository(connection).read(),
      {
        pendingDurableWriteCount: 0,
        pendingReturnCount: 0,
        pendingSaleCount: 1,
        unresolvedPaymentCount: 0,
        paymentConfigurationSensitiveOrderCount: 0,
      },
    );
  });
});

test("真实 SQLite：Approved 未消费或订单尚未完成均保持 unresolved", async () => {
  await withMigratedDatabase(async (connection) => {
    await insertOrder(connection, "approved-unconsumed", "CompletedLocal", "sale");
    await insertPaymentAttempt(connection, {
      attemptId: "approved-unconsumed-attempt",
      orderGuid: "approved-unconsumed",
      state: "Approved",
    });

    await insertOrder(connection, "approved-unfinished", "Completing", "sale");
    await insertPaymentAttempt(connection, {
      attemptId: "approved-unfinished-attempt",
      orderGuid: "approved-unfinished",
      state: "Approved",
    });
    await insertTender(connection, {
      tenderGuid: "approved-unfinished-tender",
      orderGuid: "approved-unfinished",
      paymentAttemptId: "approved-unfinished-attempt",
    });

    await insertOrder(connection, "wrong-method", "CompletedLocal", "sale");
    await insertPaymentAttempt(connection, {
      attemptId: "wrong-method-attempt",
      orderGuid: "wrong-method",
      state: "Approved",
    });
    await insertTender(connection, {
      tenderGuid: "wrong-method-tender",
      orderGuid: "wrong-method",
      paymentAttemptId: "wrong-method-attempt",
      method: "voucher",
    });

    assert.equal(
      (
        await new SqliteSettingsSafetyRepository(connection).read()
      ).unresolvedPaymentCount,
      3,
    );
  });
});

test("真实 SQLite：仅按未同步订单去重统计 Linkly 配置敏感支付", async () => {
  await withMigratedDatabase(async (connection) => {
    await insertOrder(connection, "linkly-purchase", "PendingSync", "sale");
    await insertPaymentAttempt(connection, {
      attemptId: "linkly-purchase-first",
      orderGuid: "linkly-purchase",
      state: "Approved",
      provider: "linkly-cloud",
      amountCents: 400,
    });
    await insertTender(connection, {
      tenderGuid: "linkly-purchase-first-tender",
      orderGuid: "linkly-purchase",
      paymentAttemptId: "linkly-purchase-first",
      amountCents: 400,
    });
    await insertPaymentAttempt(connection, {
      attemptId: "linkly-purchase-second",
      orderGuid: "linkly-purchase",
      state: "Approved",
      provider: "linkly-cloud",
      amountCents: 600,
    });
    await insertTender(connection, {
      tenderGuid: "linkly-purchase-second-tender",
      orderGuid: "linkly-purchase",
      paymentAttemptId: "linkly-purchase-second",
      amountCents: 600,
    });

    await insertOrder(connection, "linkly-refund", "Rejected", "return");
    await insertPaymentAttempt(connection, {
      attemptId: "linkly-refund-attempt",
      orderGuid: "linkly-refund",
      state: "Approved",
      provider: "linkly-cloud",
      operation: "refund",
    });
    await insertTender(connection, {
      tenderGuid: "linkly-refund-tender",
      orderGuid: "linkly-refund",
      paymentAttemptId: "linkly-refund-attempt",
      amountCents: -1_000,
    });

    await insertOrder(connection, "synced-linkly", "Synced", "sale");
    await insertPaymentAttempt(connection, {
      attemptId: "synced-linkly-attempt",
      orderGuid: "synced-linkly",
      state: "Approved",
      provider: "linkly-cloud",
    });
    await insertTender(connection, {
      tenderGuid: "synced-linkly-tender",
      orderGuid: "synced-linkly",
      paymentAttemptId: "synced-linkly-attempt",
    });

    await insertOrder(
      connection,
      "cancelled-linkly-history",
      "PendingSync",
      "sale",
    );
    await insertPaymentAttempt(connection, {
      attemptId: "cancelled-linkly-history-attempt",
      orderGuid: "cancelled-linkly-history",
      state: "Cancelled",
      provider: "linkly-cloud",
    });
    await insertPaymentAttempt(connection, {
      attemptId: "declined-linkly-history-attempt",
      orderGuid: "cancelled-linkly-history",
      state: "Declined",
      provider: "linkly-cloud",
    });

    await insertOrder(connection, "square-backlog", "PendingSync", "sale");
    await insertPaymentAttempt(connection, {
      attemptId: "square-backlog-attempt",
      orderGuid: "square-backlog",
      state: "Declined",
      provider: "square",
    });
    await insertTender(connection, {
      tenderGuid: "square-backlog-tender",
      orderGuid: "square-backlog",
      paymentAttemptId: "square-backlog-attempt",
    });
    await insertOrder(connection, "voucher-backlog", "PendingSync", "sale");
    await insertPaymentAttempt(connection, {
      attemptId: "voucher-backlog-attempt",
      orderGuid: "voucher-backlog",
      state: "Declined",
      provider: "voucher",
    });
    await insertTender(connection, {
      tenderGuid: "voucher-backlog-tender",
      orderGuid: "voucher-backlog",
      paymentAttemptId: "voucher-backlog-attempt",
      method: "voucher",
    });
    await insertOrder(connection, "cash-backlog", "PendingSync", "sale");

    assert.deepEqual(
      await new SqliteSettingsSafetyRepository(connection).read(),
      {
        pendingDurableWriteCount: 0,
        pendingReturnCount: 1,
        pendingSaleCount: 5,
        unresolvedPaymentCount: 0,
        paymentConfigurationSensitiveOrderCount: 2,
      },
    );
  });
});

test("真实 SQLite：手动刷卡支付按正式 provider 计数，不再让安全检查失败关闭", async () => {
  await withMigratedDatabase(async (connection) => {
    await insertOrder(connection, "manual-completed", "Synced", "sale");
    await insertPaymentAttempt(connection, {
      attemptId: "manual-consumed",
      orderGuid: "manual-completed",
      state: "Approved",
      provider: "manual-card",
    });
    await insertTender(connection, {
      tenderGuid: "manual-consumed-tender",
      orderGuid: "manual-completed",
      paymentAttemptId: "manual-consumed",
    });
    await insertOrder(connection, "manual-draft", "Draft", "sale");
    await insertPaymentAttempt(connection, {
      attemptId: "manual-pending",
      orderGuid: "manual-draft",
      state: "Pending",
      provider: "manual-card",
    });

    assert.deepEqual(
      await new SqliteSettingsSafetyRepository(connection).read(),
      {
        pendingDurableWriteCount: 0,
        pendingReturnCount: 0,
        pendingSaleCount: 1,
        unresolvedPaymentCount: 1,
        paymentConfigurationSensitiveOrderCount: 0,
      },
    );
  });
});

test("真实 SQLite：已拒收审计是投递终态，不再永久阻断危险设置操作", async () => {
  await withMigratedDatabase(async (connection) => {
    await insertAudit(connection, "pending-audit", null);
    await insertAudit(connection, "rejected-audit", null);
    await connection.run(
      `UPDATE audit_events
       SET delivery_state = 'rejected', last_error_code = 'HTTP_400'
       WHERE event_id = 'rejected-audit'`,
    );

    assert.equal(
      (await new SqliteSettingsSafetyRepository(connection).read())
        .pendingDurableWriteCount,
      1,
    );

    // 未上传却标记 uploaded 属于自相矛盾的状态，仍须失败关闭。
    await connection.run(
      `UPDATE audit_events SET delivery_state = 'uploaded'
       WHERE event_id = 'pending-audit'`,
    );
    await assert.rejects(
      new SqliteSettingsSafetyRepository(connection).read(),
      /Invalid persisted settings safety audit delivery/,
    );
  });
});

test("真实 SQLite：所有读取只在单个独占事务内完成，损坏状态失败关闭", async () => {
  await withMigratedDatabase(async (connection) => {
    await insertOrder(connection, "atomic-order", "CompletedLocal", "sale");
    const tracked = new TransactionRequiredConnection(connection);

    assert.deepEqual(await new SqliteSettingsSafetyRepository(tracked).read(), {
      pendingDurableWriteCount: 0,
      pendingReturnCount: 0,
      pendingSaleCount: 1,
      unresolvedPaymentCount: 0,
      paymentConfigurationSensitiveOrderCount: 0,
    });
    assert.equal(tracked.transactions, 1);

    await connection.run(
      "UPDATE local_orders SET state = 'Corrupt' WHERE order_guid = ?",
      ["atomic-order"],
    );
    await assert.rejects(
      () => new SqliteSettingsSafetyRepository(connection).read(),
      /persisted|state|safety/i,
    );
  });
});

test("真实 SQLite：活动目录元数据返回同一快照的条数与激活时间", async () => {
  await withMigratedDatabase(async (connection) => {
    const repository = new SqliteCatalogSnapshotRepository(connection);
    assert.equal(await repository.getActiveMetadata(), null);

    await insertCatalogSnapshot(connection, "active-one", "active", NOW);
    await insertCatalogItem(connection, "active-one", "A");
    await insertCatalogItem(connection, "active-one", "B");

    assert.deepEqual(await repository.getActiveMetadata(), {
      snapshotId: "active-one",
      storeCode: "STORE-1",
      catalogVersion: "v1",
      itemCount: 2,
      activatedAt: NOW,
    });
  });
});

test("真实 SQLite：多个 active 或损坏的 active 元数据均失败关闭", async () => {
  await withMigratedDatabase(async (connection) => {
    const repository = new SqliteCatalogSnapshotRepository(connection);
    await insertCatalogSnapshot(connection, "active-one", "active", NOW);
    await connection.exec("DROP INDEX ux_catalog_snapshots_single_active;");
    await insertCatalogSnapshot(connection, "active-two", "active", NOW);

    await assert.rejects(
      () => repository.getActiveMetadata(),
      /active|catalog|snapshot/i,
    );
  });

  await withMigratedDatabase(async (connection) => {
    const repository = new SqliteCatalogSnapshotRepository(connection);
    await insertCatalogSnapshot(connection, "active-corrupt", "active", null);

    await assert.rejects(
      () => repository.getActiveMetadata(),
      /active|catalog|snapshot/i,
    );
  });

  await withMigratedDatabase(async (connection) => {
    const repository = new SqliteCatalogSnapshotRepository(connection);
    await insertCatalogSnapshot(connection, "active-blank-version", "active", NOW);
    await connection.run(
      "UPDATE catalog_snapshots SET catalog_version = ? WHERE snapshot_id = ?",
      ["   ", "active-blank-version"],
    );

    await assert.rejects(
      () => repository.getActiveMetadata(),
      /catalog version/i,
    );
  });
});

test("真实 SQLite：暂存写边界拒绝非法目录版本且不落任何快照", async () => {
  await withMigratedDatabase(async (connection) => {
    const repository = new SqliteCatalogSnapshotRepository(connection);
    await assert.rejects(
      () => repository.beginStaging({
        snapshotId: "staging-invalid-version",
        catalogVersion: " catalog-v2",
        checksum: "checksum",
        downloadedAtIso: NOW,
      }),
      /catalog version/i,
    );
    const row = await connection.getFirst<{ count: number }>(
      "SELECT COUNT(*) AS count FROM catalog_snapshots WHERE snapshot_id = ?",
      ["staging-invalid-version"],
    );
    assert.equal(row?.count, 0);
  });
});

test("PosDatabase.settingsSafety 仅暴露窄只读仓储", async () => {
  const database = await PosDatabase.open({
    databaseName: ":memory:",
    driver: new SystemSqliteDriver(),
    keyProvider: {
      getOrCreateDatabaseKey: async () => "a".repeat(64),
    },
    nowIso: () => NOW,
  });
  try {
    assert.deepEqual(await database.settingsSafety().read(), {
      pendingDurableWriteCount: 0,
      pendingReturnCount: 0,
      pendingSaleCount: 0,
      unresolvedPaymentCount: 0,
      paymentConfigurationSensitiveOrderCount: 0,
    });
  } finally {
    await database.close();
  }
});

async function insertOrder(
  connection: SqliteConnectionPort,
  orderGuid: string,
  state: string,
  lineKind: "sale" | "return",
): Promise<void> {
  await connection.run(
    `INSERT INTO local_orders (
      order_guid, local_sequence, store_code, device_code, cashier_id,
      cashier_name, sold_at_iso, state, total_cents, discount_cents,
      actual_amount_cents, original_order_guid, created_at_iso, updated_at_iso
    ) VALUES (?, ?, 'STORE-1', 'DEVICE-1', 'CASHIER-1', 'Alice', ?, ?, ?, 0, ?, NULL, ?, ?)`,
    [
      orderGuid,
      await nextOrderSequence(connection),
      NOW,
      state,
      lineKind === "return" ? -1_000 : 1_000,
      lineKind === "return" ? -1_000 : 1_000,
      NOW,
      NOW,
    ],
  );
  await connection.run(
    `INSERT INTO local_order_lines (
      line_id, order_guid, line_sequence, product_code, item_number,
      lookup_code, display_name, quantity, unit_price_cents,
      discount_cents, actual_amount_cents, price_source, line_kind,
      return_source_key, original_order_guid, original_order_detail_guid,
      reference_code, sync_price_source
    ) VALUES (?, ?, 1, 'PRODUCT-1', NULL, 'LOOKUP-1', 'Item', '1', 1000, 0, ?, 'catalog', ?, ?, ?, NULL, NULL, 0)`,
    [
      `${orderGuid}-line`,
      orderGuid,
      lineKind === "return" ? -1_000 : 1_000,
      lineKind,
      lineKind === "return" ? `${orderGuid}-return-source` : null,
      lineKind === "return" ? "original-order" : null,
    ],
  );
}

let sequence = 0;

async function nextOrderSequence(
  _connection: SqliteConnectionPort,
): Promise<number> {
  sequence += 1;
  return sequence;
}

async function insertPaymentAttempt(
  connection: SqliteConnectionPort,
  input: Readonly<{
    attemptId: string;
    orderGuid: string;
    state: string;
    provider?: "square" | "linkly-cloud" | "voucher" | "manual-card";
    operation?: "purchase" | "refund";
    amountCents?: number;
  }>,
): Promise<void> {
  const operation = input.operation ?? "purchase";
  const amountCents =
    input.amountCents ?? (operation === "refund" ? -1_000 : 1_000);
  await connection.run(
    `INSERT INTO payment_attempts (
      attempt_id, idempotency_key, order_guid, provider, operation,
      amount_cents, state, created_at_iso, updated_at_iso
    ) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?)`,
    [
      input.attemptId,
      `${input.attemptId}-idempotency`,
      input.orderGuid,
      input.provider ?? "square",
      operation,
      amountCents,
      input.state,
      NOW,
      NOW,
    ],
  );
}

async function insertTender(
  connection: SqliteConnectionPort,
  input: Readonly<{
    tenderGuid: string;
    orderGuid: string;
    paymentAttemptId: string;
    method?: "card" | "voucher";
    amountCents?: number;
  }>,
): Promise<void> {
  await connection.run(
    `INSERT INTO order_tenders (
      tender_guid, order_guid, method, amount_cents,
      payment_attempt_id, created_at_iso
    ) VALUES (?, ?, ?, ?, ?, ?)`,
    [
      input.tenderGuid,
      input.orderGuid,
      input.method ?? "card",
      input.amountCents ?? 1_000,
      input.paymentAttemptId,
      NOW,
    ],
  );
}

async function insertOutbox(
  connection: SqliteConnectionPort,
  messageId: string,
  state: string,
): Promise<void> {
  await connection.run(
    `INSERT INTO outbox_messages (
      message_id, aggregate_id, kind, payload_json, state, attempt_count,
      next_attempt_at_iso, created_at_iso, updated_at_iso
    ) VALUES (?, ?, 'audit-batch', '{}', ?, 0, ?, ?, ?)`,
    [messageId, `${messageId}-aggregate`, state, NOW, NOW, NOW],
  );
}

async function insertAudit(
  connection: SqliteConnectionPort,
  eventId: string,
  uploadedAtIso: string | null,
): Promise<void> {
  await connection.run(
    `INSERT INTO audit_events (
      event_id, event_type, occurred_at_iso, order_guid,
      correlation_id, payload_json, uploaded_at_iso
    ) VALUES (?, 'TEST', ?, NULL, ?, '{}', ?)`,
    [eventId, NOW, `${eventId}-correlation`, uploadedAtIso],
  );
}

async function insertPrintJob(
  connection: SqliteConnectionPort,
  jobId: string,
  state: string,
): Promise<void> {
  await connection.run(
    `INSERT INTO print_jobs (
      job_id, order_guid, state, printer_id, receipt_ciphertext,
      created_at_iso, updated_at_iso
    ) VALUES (?, NULL, ?, 'PRINTER-1', ?, ?, ?)`,
    [jobId, state, new Uint8Array([1]), NOW, NOW],
  );
}

async function insertDrawerEvent(
  connection: SqliteConnectionPort,
  eventId: string,
  state: string,
): Promise<void> {
  await connection.run(
    `INSERT INTO drawer_events (
      event_id, order_guid, print_job_id, state, reason,
      created_at_iso, updated_at_iso, printer_id
    ) VALUES (?, NULL, NULL, ?, 'TEST', ?, ?, 'PRINTER-1')`,
    [eventId, state, NOW, NOW],
  );
}

async function insertCatalogSnapshot(
  connection: SqliteConnectionPort,
  snapshotId: string,
  state: "staging" | "active" | "retired",
  activatedAtIso: string | null,
): Promise<void> {
  await connection.run(
    `INSERT INTO catalog_snapshots (
      snapshot_id, catalog_version, checksum, state,
      downloaded_at_iso, activated_at_iso
    ) VALUES (?, 'v1', 'checksum', ?, ?, ?)`,
    [snapshotId, state, NOW, activatedAtIso],
  );
}

async function insertCatalogItem(
  connection: SqliteConnectionPort,
  snapshotId: string,
  productCode: string,
): Promise<void> {
  await connection.run(
    `INSERT INTO catalog_items (
      snapshot_id, store_code, lookup_code_normalized, product_code,
      reference_code, item_number, barcode, lookup_code, display_name,
      retail_price_cents, price_source, price_source_label, quantity_factor,
      tax_rate_basis_points, row_version, product_image, discount_rate,
      is_special_product, is_active, updated_at_iso
    ) VALUES (?, 'STORE-1', ?, ?, NULL, NULL, NULL, ?, ?, 100, 0,
      'Default', '1', NULL, NULL, NULL, NULL, 0, 1, NULL)`,
    [snapshotId, productCode, productCode, productCode, productCode],
  );
}

class TransactionRequiredConnection implements SqliteConnectionPort {
  public transactions = 0;

  public constructor(private readonly inner: SqliteConnectionPort) {}

  public exec(): Promise<void> {
    return Promise.reject(new Error("Settings safety read escaped transaction."));
  }

  public run(): Promise<SqlRunResult> {
    return Promise.reject(new Error("Settings safety read escaped transaction."));
  }

  public getFirst<T extends object>(): Promise<T | null> {
    return Promise.reject(new Error("Settings safety read escaped transaction."));
  }

  public getAll<T extends object>(): Promise<readonly T[]> {
    return Promise.reject(new Error("Settings safety read escaped transaction."));
  }

  public withExclusiveTransaction<T>(
    operation: (transaction: SqliteConnectionPort) => Promise<T>,
  ): Promise<T> {
    this.transactions += 1;
    return this.inner.withExclusiveTransaction(operation);
  }

  public close(): Promise<void> {
    return Promise.reject(new Error("Settings safety facade cannot close database."));
  }
}

class SystemSqliteDriver implements SqliteDriverPort {
  public async open(_databaseName: string): Promise<SqliteConnectionPort> {
    return new SystemSqliteConnection(new DatabaseSync(":memory:"));
  }
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
    // Node 内置 SQLite 不含 SQLCipher；仅为测试的精确探针提供有效版本。
    if (sql === "PRAGMA cipher_version;") {
      return { cipher_version: "4.6.1" } as unknown as T;
    }
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

async function withMigratedDatabase(
  operation: (connection: SystemSqliteConnection) => Promise<void>,
): Promise<void> {
  const connection = new SystemSqliteConnection(new DatabaseSync(":memory:"));
  try {
    await applyMigrations(connection, () => NOW);
    await operation(connection);
  } finally {
    await connection.close();
  }
}

function toSqlInputValue(value: SqlValue): SQLInputValue {
  return value as SQLInputValue;
}
