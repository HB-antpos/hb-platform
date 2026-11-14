import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import test from "node:test";

import { InMemorySecureStore } from "../security/secure-storage";

import { KeychainDatabaseKeyProvider } from "./keychain-database-key-provider";
import { POS_DATABASE_MIGRATIONS, applyMigrations } from "./migrations";
import { PosDatabase } from "./pos-database";
import { PosHandheldOtaUpdatePolicyRepository } from "./pos-handheld-ota-update-policy-repository";
import { PosHandheldUpdatePolicyRepository } from "./pos-handheld-update-policy-repository";
import { PosSettingsRepository } from "./pos-settings-repository";
import {
  SqliteCashMovementRepository,
  SqliteCashMovementUploadRepository,
} from "@hb/pos-db/core/db/sqlite-cash-movement-repository";
import { SqliteDailyCloseRepository } from "@hb/pos-db/core/db/sqlite-daily-close-repository";
import { SqliteFulfilmentStore } from "./sqlite-fulfilment-store";
import { SqliteOrderSyncMaterialResolver } from "./sqlite-order-sync-material";
import { SqliteRefundVoucherPrintMaterial } from "./sqlite-refund-voucher-print-material";
import { SqliteReturnFulfilmentPlanStore } from "./sqlite-return-fulfilment-plan-store";
import { SqliteSpecialProductsRepository } from "@hb/pos-db/core/db/sqlite-special-products-repository";
import { SqliteVoucherTenderReversalStore } from "./sqlite-voucher-tender-reversal-store";
import type {
  DatabaseKeyProviderPort,
  PosDatabaseOptions,
  SqliteConnectionPort,
  SqliteDriverPort,
  SqlRunResult,
  SqlValue,
} from "@hb/pos-db/core/db/types";

class RecordingConnection implements SqliteConnectionPort {
  public readonly executed: string[] = [];
  public readonly runs: Readonly<{ sql: string; parameters: readonly SqlValue[] }>[] = [];
  public transactionCount = 0;
  public closed = false;
  public appliedVersions: number[] = [];
  public nextSequence: number | null = null;
  public pendingOrderSyncCount = 0;
  public failWhenSqlIncludes: string | null = null;
  public cipherVersion: string | null | undefined = "4.7.0";
  public cipherVersionError: Error | null = null;
  public readonly firstQueries: string[] = [];
  private readonly drawerColumns = new Map<string, Readonly<{
    type: string;
    pk: number;
  }>>();
  private readonly printColumns = new Map<string, Readonly<{
    type: string;
    pk: number;
  }>>();

  public async exec(sql: string): Promise<void> {
    this.executed.push(sql);
    if (this.failWhenSqlIncludes && sql.includes(this.failWhenSqlIncludes)) {
      throw new Error("DDL failed");
    }
    if (sql.includes("CREATE TABLE IF NOT EXISTS drawer_events")) {
      this.seedM5Schema();
    }
    for (const match of sql.matchAll(
      /ALTER TABLE drawer_events ADD COLUMN\s+([a-z_]+)\s+(TEXT|INTEGER)/gi,
    )) {
      const name = match[1];
      const type = match[2];
      if (!name || !type) continue;
      this.drawerColumns.set(name, {
        type: type.toUpperCase(),
        pk: 0,
      });
    }
  }

  public async run(sql: string, parameters: readonly SqlValue[] = []): Promise<SqlRunResult> {
    this.runs.push({ sql, parameters });
    if (sql.includes("INSERT INTO schema_migrations")) {
      this.appliedVersions.push(Number(parameters[0]));
    }
    return { changes: 1, lastInsertRowId: 1 };
  }

  public async getFirst<T extends object>(sql: string): Promise<T | null> {
    this.firstQueries.push(sql);
    if (sql === "PRAGMA cipher_version;") {
      if (this.cipherVersionError) {
        throw this.cipherVersionError;
      }
      if (this.cipherVersion === undefined) {
        return {} as T;
      }
      return (this.cipherVersion === null
        ? null
        : { cipher_version: this.cipherVersion }) as T | null;
    }
    if (sql.includes("RETURNING setting_value AS next_sequence")) {
      return (this.nextSequence === null ? null : { next_sequence: this.nextSequence }) as T;
    }
    if (sql.includes("COUNT(DISTINCT aggregate_id) AS pending_order_sync_count")) {
      return { pending_order_sync_count: this.pendingOrderSyncCount } as T;
    }
    if (
      sql.includes("ux_catalog_snapshots_single_active") &&
      this.appliedVersions.includes(2)
    ) {
      return { name: "ux_catalog_snapshots_single_active" } as T;
    }
    return null;
  }

  public async getAll<T extends object>(sql: string): Promise<readonly T[]> {
    if (sql.includes("FROM schema_migrations")) {
      return this.appliedVersions.map((version) => ({ version }) as T);
    }
    if (sql.includes("PRAGMA table_info('drawer_events')")) {
      this.seedRecordedM5Schema();
      return Array.from(this.drawerColumns, ([name, column]) => ({
        name,
        type: column.type,
        pk: column.pk,
      }) as T);
    }
    if (sql.includes("PRAGMA table_info('print_jobs')")) {
      this.seedRecordedM5Schema();
      return Array.from(this.printColumns, ([name, column]) => ({
        name,
        type: column.type,
        pk: column.pk,
      }) as T);
    }
    const catalogTable = sql.match(
      /PRAGMA table_info\('(catalog_[a-z_]+|special_products)'\)/,
    )?.[1];
    if (catalogTable) {
      return this.currentCatalogColumns(catalogTable).map(
        ([name, type, pk]) => ({ name, type, pk }) as T,
      );
    }
    return [];
  }

  public async withExclusiveTransaction<T>(operation: (transaction: SqliteConnectionPort) => Promise<T>): Promise<T> {
    this.transactionCount += 1;
    return operation(this);
  }

  public async close(): Promise<void> {
    this.closed = true;
  }

  private seedRecordedM5Schema(): void {
    if (
      this.drawerColumns.size === 0 &&
      this.appliedVersions.includes(5)
    ) {
      this.seedM5Schema();
    }
  }

  private seedM5Schema(): void {
    if (this.drawerColumns.size === 0) {
      for (const [name, type, pk] of [
        ["event_id", "TEXT", 1],
        ["order_guid", "TEXT", 0],
        ["print_job_id", "TEXT", 0],
        ["state", "TEXT", 0],
        ["reason", "TEXT", 0],
        ["retry_count", "INTEGER", 0],
        ["requested_at_iso", "TEXT", 0],
        ["completed_at_iso", "TEXT", 0],
        ["last_error_code", "TEXT", 0],
        ["created_at_iso", "TEXT", 0],
        ["updated_at_iso", "TEXT", 0],
      ] as const) {
        this.drawerColumns.set(name, { type, pk });
      }
    }
    if (this.printColumns.size === 0) {
      this.printColumns.set("job_id", { type: "TEXT", pk: 1 });
      this.printColumns.set("order_guid", { type: "TEXT", pk: 0 });
      this.printColumns.set("printer_id", { type: "TEXT", pk: 0 });
    }
  }

  private currentCatalogColumns(
    tableName: string,
  ): readonly (readonly [name: string, type: string, pk: number])[] {
    if (!this.appliedVersions.includes(2)) {
      return [];
    }
    switch (tableName) {
      case "catalog_snapshots":
        return [
          ["snapshot_id", "TEXT", 1],
          ["catalog_version", "TEXT", 0],
          ["checksum", "TEXT", 0],
          ["state", "TEXT", 0],
          ["downloaded_at_iso", "TEXT", 0],
          ["activated_at_iso", "TEXT", 0],
        ];
      case "catalog_items":
        return [
          ["snapshot_id", "TEXT", 1],
          ["store_code", "TEXT", 2],
          ["lookup_code_normalized", "TEXT", 3],
          ["product_code", "TEXT", 0],
          ["reference_code", "TEXT", 0],
          ["item_number", "TEXT", 0],
          ["barcode", "TEXT", 0],
          ["lookup_code", "TEXT", 0],
          ["display_name", "TEXT", 0],
          ["retail_price_cents", "INTEGER", 0],
          ["price_source", "INTEGER", 0],
          ["price_source_label", "TEXT", 0],
          ["quantity_factor", "TEXT", 0],
          ["tax_rate_basis_points", "INTEGER", 0],
          ["row_version", "TEXT", 0],
          ["product_image", "TEXT", 0],
          ["discount_rate", "TEXT", 0],
          ["is_special_product", "INTEGER", 0],
          ["is_active", "INTEGER", 0],
          ["updated_at_iso", "TEXT", 0],
        ];
      case "catalog_promotions":
        return [
          ["snapshot_id", "TEXT", 1],
          ["promotion_id", "TEXT", 2],
          ["definition_json", "TEXT", 0],
          ["valid_from_iso", "TEXT", 0],
          ["valid_until_iso", "TEXT", 0],
          ["priority", "INTEGER", 0],
        ];
      case "special_products":
        return [
          ["snapshot_id", "TEXT", 1],
          ["store_code", "TEXT", 2],
          ["lookup_code_normalized", "TEXT", 3],
          ["sort_order", "INTEGER", 0],
          ["is_marked", "INTEGER", 0],
          ["updated_at_iso", "TEXT", 0],
        ];
      default:
        return [];
    }
  }
}

class RecordingDriver implements SqliteDriverPort {
  public openedName: string | null = null;

  public constructor(public readonly connection: RecordingConnection) {}

  public async open(databaseName: string): Promise<SqliteConnectionPort> {
    this.openedName = databaseName;
    return this.connection;
  }
}

const keyProvider: DatabaseKeyProviderPort = {
  async getOrCreateDatabaseKey() {
    return "ab".repeat(32);
  },
};

function options(connection: RecordingConnection): PosDatabaseOptions {
  return {
    databaseName: "hb-pos-test.db",
    driver: new RecordingDriver(connection),
    keyProvider,
    nowIso: () => "2026-07-28T00:00:00.000Z",
  };
}

test("开库先注入 SQLCipher 密钥，启用 WAL 与外键，再原子执行 M1-M10", async () => {
  const connection = new RecordingConnection();
  await PosDatabase.open(options(connection));

  assert.equal(
    connection.executed[0],
    `PRAGMA key = '${"ab".repeat(32)}';`,
  );
  assert.ok(connection.executed.includes("PRAGMA foreign_keys = ON;"));
  assert.ok(connection.executed.includes("PRAGMA journal_mode = WAL;"));
  assert.deepEqual(connection.appliedVersions, POS_DATABASE_MIGRATIONS.map((migration) => migration.version));
  assert.equal(connection.transactionCount, 1);
});

test("SQLCipher 版本有效时才继续开库", async () => {
  const connection = new RecordingConnection();
  connection.cipherVersion = "4.7.0";

  await PosDatabase.open(options(connection));

  assert.ok(connection.firstQueries.includes("PRAGMA cipher_version;"));
  assert.equal(connection.closed, false);
});

test("SQLCipher 版本缺失、为空或查询失败时关闭连接并拒绝开库", async () => {
  const cases: readonly Readonly<{
    name: string;
    configure: (connection: RecordingConnection) => void;
  }>[] = [
    {
      name: "版本列缺失",
      configure(connection) {
        connection.cipherVersion = undefined;
      },
    },
    {
      name: "版本为空",
      configure(connection) {
        connection.cipherVersion = "";
      },
    },
    {
      name: "查询异常",
      configure(connection) {
        connection.cipherVersionError = new Error("cipher unavailable");
      },
    },
  ];

  for (const scenario of cases) {
    const connection = new RecordingConnection();
    scenario.configure(connection);

    await assert.rejects(
      PosDatabase.open(options(connection)),
      /SQLCipher cipher version failed/,
      scenario.name,
    );

    assert.ok(
      connection.firstQueries.includes("PRAGMA cipher_version;"),
      `${scenario.name} 必须执行 SQLCipher 版本校验`,
    );
    assert.equal(connection.closed, true, `${scenario.name} 必须关闭连接`);
    assert.deepEqual(connection.appliedVersions, [], `${scenario.name} 不得执行迁移`);
  }
});

test("拒绝非 32 字节十六进制 Keychain 密钥，避免生成不可恢复数据库", async () => {
  const connection = new RecordingConnection();
  const invalidOptions = options(connection);

  await assert.rejects(
    PosDatabase.open({
      ...invalidOptions,
      keyProvider: {
        async getOrCreateDatabaseKey() {
          return "not-a-raw-key";
        },
      },
    }),
    /64 lowercase hexadecimal/,
  );

  assert.equal(connection.closed, false);
  assert.equal(connection.executed.length, 0);
});

test("数据库密钥只保存到本机 Keychain，后续开库复用同一密钥", async () => {
  const secureStore = new InMemorySecureStore();
  let generated = 0;
  const provider = new KeychainDatabaseKeyProvider(secureStore, async () => {
    generated += 1;
    return "random-key";
  });

  assert.equal(await provider.getOrCreateDatabaseKey(), "random-key");
  assert.equal(await provider.getOrCreateDatabaseKey(), "random-key");
  assert.equal(generated, 1);
  assert.equal(
    secureStore.lastWriteKey,
    "hbpos.handheld.sqlcipher-key.v1",
  );
  assert.deepEqual(secureStore.lastWriteOptions, { requireThisDeviceOnly: true });
});

test("PosDatabase 只暴露履约 facade，不向 feature 泄露 SQLCipher 连接", async () => {
  const database = await PosDatabase.open(options(new RecordingConnection()));

  const fulfilment = database.fulfilmentStore(
    { async encrypt(value) { return new TextEncoder().encode(value); }, async decrypt(value) { return new TextDecoder().decode(value); } },
    () => "reprint-1",
  );

  assert.ok(fulfilment instanceof SqliteFulfilmentStore);
  assert.ok(
    database.returnFulfilmentPlans({
      async encrypt(value) {
        return new TextEncoder().encode(value);
      },
      async decrypt(value) {
        return new TextDecoder().decode(value);
      },
    }) instanceof SqliteReturnFulfilmentPlanStore,
  );
  assert.ok(
    database.refundVoucherPrintMaterial({
      async encrypt(value) {
        return new TextEncoder().encode(value);
      },
      async decrypt(value) {
        return new TextDecoder().decode(value);
      },
    }) instanceof SqliteRefundVoucherPrintMaterial,
  );
  assert.ok(
    database.orderSyncMaterial(
      {
        async encrypt(value) {
          return new TextEncoder().encode(value);
        },
        async decrypt(value) {
          return new TextDecoder().decode(value);
        },
      },
      () => "vpr_abcdefghijklmnop",
    ) instanceof SqliteOrderSyncMaterialResolver,
  );
  assert.ok(
    database.voucherTenderReversals(
      {
        async encrypt(value) {
          return new TextEncoder().encode(value);
        },
        async decrypt(value) {
          return new TextDecoder().decode(value);
        },
      },
      {
        createReversalTenderGuid: () => "voucher-reversal-tender-1",
        createAuditEventId: () => "voucher-reversal-audit-1",
      },
    ) instanceof SqliteVoucherTenderReversalStore,
  );
  assert.ok(database.settings() instanceof PosSettingsRepository);
  assert.ok(
    database.appUpdatePolicy({
      kind: "native",
      apiOrigin: "https://hotbargain.vip",
      storeCode: "S001",
      appKey: "pos-handheld",
      platform: "iOS",
      installedVersion: "1.2.3",
      installedBuild: "42",
    }) instanceof PosHandheldUpdatePolicyRepository,
  );
  assert.ok(
    database.otaUpdatePolicy({
      kind: "ota",
      apiOrigin: "https://hotbargain.vip",
      storeCode: "S001",
      appKey: "pos-handheld",
      projectId: "123e4567-e89b-42d3-a456-426614174000",
      projectName: "hb-pos-handheld",
      platform: "iOS",
      configuredChannel: "pos-handheld-production",
      runtimeVersion: "1.2.3",
      currentUpdateId: null,
      currentUpdateGroupId: null,
    }) instanceof PosHandheldOtaUpdatePolicyRepository,
  );
  assert.ok(database.dailyCloses() instanceof SqliteDailyCloseRepository);
  assert.ok(database.cashMovements() instanceof SqliteCashMovementRepository);
  assert.ok(
    database.cashMovementUploads() instanceof SqliteCashMovementUploadRepository,
  );
  assert.ok(
    database.specialProducts() instanceof SqliteSpecialProductsRepository,
  );
});

test("M14 退货履约策略和身份不可改删且物化时间只能由空值推进一次", () => {
  const migration =
    POS_DATABASE_MIGRATIONS.find((item) => item.version === 14)?.sql ?? "";

  assert.match(migration, /receipt_kind/);
  assert.match(migration, /refund-voucher/);
  assert.match(migration, /refund-receipt/);
  assert.match(
    migration,
    /RETURN_FULFILMENT_PLAN_IDENTITY_IMMUTABLE/,
  );
  assert.match(
    migration,
    /RETURN_FULFILMENT_MATERIALIZATION_IMMUTABLE/,
  );
  assert.match(
    migration,
    /RETURN_FULFILMENT_PLAN_DELETE_FORBIDDEN/,
  );
});

test("M15 保留历史空来源，并要求新订单行使用不可变的整数枚举来源", () => {
  const migration =
    POS_DATABASE_MIGRATIONS.find((item) => item.version === 15)?.sql ?? "";

  assert.match(
    migration,
    /ALTER TABLE local_order_lines\s+ADD COLUMN reference_code TEXT NULL/,
  );
  assert.match(
    migration,
    /ALTER TABLE local_order_lines\s+ADD COLUMN sync_price_source INTEGER NULL/,
  );
  assert.match(
    migration,
    /typeof\(sync_price_source\) = 'integer'[\s\S]*sync_price_source IN \(0, 1, 2, 3, 4\)/,
  );
  assert.match(
    migration,
    /BEFORE INSERT ON local_order_lines[\s\S]*WHEN NEW\.sync_price_source IS NULL/,
  );
  assert.match(
    migration,
    /NEW\.reference_code IS NOT OLD\.reference_code[\s\S]*NEW\.sync_price_source IS NOT OLD\.sync_price_source/,
  );
  assert.match(
    migration,
    /ORDER_LINE_SYNC_PROVENANCE_IMMUTABLE/,
  );
});

test("M16 建立券 tender 撤销耐久账本，并约束退货履约 action/order 同行绑定", () => {
  const migration =
    POS_DATABASE_MIGRATIONS.find((item) => item.version === 16)?.sql ?? "";

  assert.match(migration, /voucher_tender_reversal_actions/);
  assert.match(
    migration,
    /state IN \('Prepared', 'Submitted', 'Unknown', 'Reversed', 'Blocked'\)/,
  );
  assert.match(
    migration,
    /WHERE state IN \('Prepared', 'Submitted', 'Unknown', 'Blocked'\)/,
  );
  assert.match(
    migration,
    /reason IN \('SALE', 'CARD_FAILURE_AUTO_RELEASE'\)/,
  );
  assert.match(migration, /VOUCHER_TENDER_REVERSAL_IDENTITY_IMMUTABLE/);
  assert.match(migration, /VOUCHER_TENDER_REVERSAL_INVALID_TRANSITION/);
  assert.match(migration, /VOUCHER_TENDER_REVERSAL_DELETE_FORBIDDEN/);
  assert.match(
    migration,
    /RETURN_FULFILMENT_PLAN_ACTION_ORDER_MISMATCH/,
  );
});

test("迁移 DDL 失败时不写入失败版本", async () => {
  const connection = new RecordingConnection();
  connection.failWhenSqlIncludes = "CREATE TABLE IF NOT EXISTS local_orders";

  await assert.rejects(applyMigrations(connection, () => "2026-07-28T00:00:00.000Z"), /DDL failed/);

  assert.deepEqual(connection.appliedVersions, [1, 2]);
});

test("M7 失败不推进版本，成功后继续执行 M8-M17 且重复开库不改 schema", async () => {
  const connection = new RecordingConnection();
  connection.appliedVersions = [1, 2, 3, 4, 5, 6];
  connection.failWhenSqlIncludes = "ALTER TABLE drawer_events ADD COLUMN printer_id";

  await assert.rejects(
    applyMigrations(connection, () => "2026-07-28T00:00:00.000Z"),
    /DDL failed/,
  );
  assert.deepEqual(connection.appliedVersions, [1, 2, 3, 4, 5, 6]);

  connection.failWhenSqlIncludes = null;
  await applyMigrations(
    connection,
    () => "2026-07-28T00:01:00.000Z",
    POS_DATABASE_MIGRATIONS.filter((migration) => migration.version <= 17),
  );
  await applyMigrations(
    connection,
    () => "2026-07-28T00:02:00.000Z",
    POS_DATABASE_MIGRATIONS.filter((migration) => migration.version <= 17),
  );

  assert.deepEqual(
    connection.appliedVersions,
    [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17],
  );
  assert.equal(
    connection.executed.filter((sql) =>
      sql.includes("ALTER TABLE drawer_events ADD COLUMN printer_id")).length,
    2,
  );
});

test("M2 允许 staging 快照与旧 active 快照完整并存，并以门店和规范化售卖码保持同商品多行", () => {
  const catalogMigration = POS_DATABASE_MIGRATIONS.find((migration) => migration.version === 2)?.sql ?? "";

  assert.match(catalogMigration, /PRIMARY KEY \(snapshot_id, store_code, lookup_code_normalized\)/);
  assert.match(catalogMigration, /catalog_version TEXT NOT NULL/);
  assert.match(catalogMigration, /lookup_code TEXT NOT NULL/);
  assert.match(catalogMigration, /barcode TEXT NULL/);
  assert.match(catalogMigration, /retail_price_cents INTEGER NOT NULL/);
  assert.match(catalogMigration, /updated_at_iso TEXT NULL/);
  assert.match(catalogMigration, /ix_catalog_items_active_lookup\s+ON catalog_items \(snapshot_id, store_code, lookup_code_normalized\)/);
  assert.match(catalogMigration, /ux_catalog_snapshots_single_active[\s\S]*WHERE state = 'active'/);
  assert.doesNotMatch(catalogMigration, /PRIMARY KEY \(snapshot_id, product_code\)/);
  assert.doesNotMatch(catalogMigration, /catalog_version TEXT NOT NULL UNIQUE/);
});

test("M4 仅让 order-sync 按聚合幂等，不阻断同一聚合的多批审计", () => {
  const syncMigration = POS_DATABASE_MIGRATIONS.find((migration) => migration.version === 4)?.sql ?? "";

  assert.match(syncMigration, /ux_outbox_order_sync_aggregate[\s\S]*WHERE kind = 'order-sync'/);
  assert.doesNotMatch(syncMigration, /UNIQUE \(aggregate_id, kind\)/);
});

test("M3/M4 将 payment attempt 的 tender 消费限制为唯一，并为 Approved 恢复查询建立索引", () => {
  const orderMigration = POS_DATABASE_MIGRATIONS.find((migration) => migration.version === 3)?.sql ?? "";
  const paymentMigration = POS_DATABASE_MIGRATIONS.find((migration) => migration.version === 4)?.sql ?? "";

  assert.match(orderMigration, /ux_order_tenders_payment_attempt[\s\S]*payment_attempt_id\) WHERE payment_attempt_id IS NOT NULL/);
  assert.match(paymentMigration, /ix_payment_attempts_approved_recovery[\s\S]*order_guid, state, attempt_id, amount_cents, provider/);
  assert.match(paymentMigration, /provider_receipt_ciphertext BLOB NULL/);
  assert.match(paymentMigration, /provider_response_code TEXT NULL/);
});

test("M3 以 checkout intent 唯一绑定现金订单，保证崩溃重放不会创建第二笔订单", () => {
  const orderMigration = POS_DATABASE_MIGRATIONS.find((migration) => migration.version === 3)?.sql ?? "";

  assert.match(orderMigration, /cash_checkout_intents[\s\S]*checkout_intent_id TEXT PRIMARY KEY/);
  assert.match(orderMigration, /request_signature TEXT NOT NULL/);
  assert.match(orderMigration, /order_guid TEXT NOT NULL UNIQUE REFERENCES local_orders\(order_guid\)/);
  assert.match(orderMigration, /cash_due_cents INTEGER NOT NULL[\s\S]*change_cents INTEGER NOT NULL/);
});

test("M5 保存加密小票重打标志和钱箱人工重试计数，状态不确定时可跨重启保留", () => {
  const fulfilmentMigration = POS_DATABASE_MIGRATIONS.find((migration) => migration.version === 5)?.sql ?? "";

  assert.match(fulfilmentMigration, /receipt_ciphertext BLOB NOT NULL/);
  assert.match(fulfilmentMigration, /is_reprint INTEGER NOT NULL DEFAULT 0 CHECK \(is_reprint IN \(0, 1\)\)/);
  assert.match(fulfilmentMigration, /drawer_events[\s\S]*retry_count INTEGER NOT NULL DEFAULT 0/);
  assert.match(fulfilmentMigration, /drawer_events[\s\S]*created_at_iso TEXT NOT NULL[\s\S]*updated_at_iso TEXT NOT NULL/);
  assert.match(fulfilmentMigration, /ix_print_jobs_state_created/);
  assert.match(fulfilmentMigration, /ix_drawer_events_state/);
});

test("M7 回填原打印机并将无法绑定的 Required/Requested 钱箱动作安全停为 Unknown", () => {
  const preM7 = POS_DATABASE_MIGRATIONS
    .filter((migration) => migration.version <= 6)
    .map((migration) => migration.sql)
    .join("\n");
  const migration = POS_DATABASE_MIGRATIONS.find((item) => item.version === 7)?.sql ?? "";

  assert.match(migration, /ALTER TABLE drawer_events ADD COLUMN printer_id TEXT NULL/);
  assert.match(migration, /UPDATE drawer_events[\s\S]*FROM print_jobs/);
  assert.match(migration, /DRAWER_PRINTER_BINDING_MISSING_MIGRATION/);
  assert.match(migration, /SET state = 'Unknown'/);
  assert.match(migration, /state IN \('Required', 'Requested', 'Failed'\)/);
  assert.match(migration, /CREATE TRIGGER[\s\S]*DRAWER_PRINTER_ID_REQUIRED/);
  assert.match(migration, /CREATE TRIGGER[\s\S]*DRAWER_PRINTER_ID_MISMATCH/);

  const result = runSqlite(`${preM7}
    PRAGMA foreign_keys = ON;
    INSERT INTO print_jobs (job_id, order_guid, state, printer_id, receipt_ciphertext, is_reprint, retry_count, last_error_code, created_at_iso, updated_at_iso)
      VALUES ('old-print', NULL, 'Printed', 'XP-OLD', X'1B40', 0, 0, NULL, '2026-07-28T00:00:00.000Z', '2026-07-28T00:00:00.000Z');
    INSERT INTO drawer_events (event_id, order_guid, print_job_id, state, reason, retry_count, requested_at_iso, completed_at_iso, last_error_code, created_at_iso, updated_at_iso)
      VALUES ('drawer-linked', NULL, 'old-print', 'Required', 'cash-sale', 0, NULL, NULL, NULL, '2026-07-28T00:00:00.000Z', '2026-07-28T00:00:00.000Z');
    INSERT INTO drawer_events (event_id, order_guid, print_job_id, state, reason, retry_count, requested_at_iso, completed_at_iso, last_error_code, created_at_iso, updated_at_iso)
      VALUES ('drawer-requested', NULL, NULL, 'Requested', 'cash-sale', 0, '2026-07-28T00:00:01.000Z', NULL, NULL, '2026-07-28T00:00:00.000Z', '2026-07-28T00:00:01.000Z');
    INSERT INTO drawer_events (event_id, order_guid, print_job_id, state, reason, retry_count, requested_at_iso, completed_at_iso, last_error_code, created_at_iso, updated_at_iso)
      VALUES ('drawer-unbound', NULL, NULL, 'Required', 'cash-sale', 0, NULL, NULL, NULL, '2026-07-28T00:00:00.000Z', '2026-07-28T00:00:00.000Z');
    ${migration}
    SELECT event_id || ':' || COALESCE(printer_id, 'NULL') || ':' || state || ':' || COALESCE(last_error_code, 'NULL')
      FROM drawer_events ORDER BY event_id;
  `);

  assert.equal(result.status, 0, result.stderr);
  assert.equal(
    result.stdout.trim(),
    [
      "drawer-linked:XP-OLD:Required:NULL",
      "drawer-requested:NULL:Unknown:DRAWER_PRINTER_BINDING_MISSING_MIGRATION",
      "drawer-unbound:NULL:Unknown:DRAWER_PRINTER_BINDING_MISSING_MIGRATION",
    ].join("\n"),
  );
});

test("M7 由数据库拒绝空绑定、错绑和创建后换绑，仅接受与打印任务一致的 printerId", () => {
  const migrations = POS_DATABASE_MIGRATIONS.map((migration) => migration.sql).join("\n");
  const seed = `${migrations}
    PRAGMA foreign_keys = ON;
    INSERT INTO print_jobs (job_id, order_guid, state, printer_id, receipt_ciphertext, is_reprint, retry_count, last_error_code, created_at_iso, updated_at_iso)
      VALUES ('print-1', NULL, 'Queued', 'XP-1', X'1B40', 0, 0, NULL, '2026-07-28T00:00:00.000Z', '2026-07-28T00:00:00.000Z');
  `;

  const accepted = runSqlite(`${seed}
    INSERT INTO drawer_events (event_id, order_guid, printer_id, print_job_id, state, reason, retry_count, requested_at_iso, completed_at_iso, last_error_code, created_at_iso, updated_at_iso)
      VALUES ('drawer-ok', NULL, 'XP-1', 'print-1', 'Required', 'cash-sale', 0, NULL, NULL, NULL, '2026-07-28T00:00:00.000Z', '2026-07-28T00:00:00.000Z');
    SELECT printer_id FROM drawer_events WHERE event_id = 'drawer-ok';
  `);
  assert.equal(accepted.status, 0, accepted.stderr);
  assert.equal(accepted.stdout.trim(), "XP-1");

  const missing = runSqlite(`${seed}
    INSERT INTO drawer_events (event_id, order_guid, printer_id, print_job_id, state, reason, retry_count, requested_at_iso, completed_at_iso, last_error_code, created_at_iso, updated_at_iso)
      VALUES ('drawer-missing', NULL, NULL, NULL, 'Required', 'cash-sale', 0, NULL, NULL, NULL, '2026-07-28T00:00:00.000Z', '2026-07-28T00:00:00.000Z');
  `);
  assert.notEqual(missing.status, 0);
  assert.match(missing.stderr, /DRAWER_PRINTER_ID_REQUIRED/);

  const mismatch = runSqlite(`${seed}
    INSERT INTO drawer_events (event_id, order_guid, printer_id, print_job_id, state, reason, retry_count, requested_at_iso, completed_at_iso, last_error_code, created_at_iso, updated_at_iso)
      VALUES ('drawer-wrong', NULL, 'XP-2', 'print-1', 'Required', 'cash-sale', 0, NULL, NULL, NULL, '2026-07-28T00:00:00.000Z', '2026-07-28T00:00:00.000Z');
  `);
  assert.notEqual(mismatch.status, 0);
  assert.match(mismatch.stderr, /DRAWER_PRINTER_ID_MISMATCH/);

  const rebound = runSqlite(`${seed}
    INSERT INTO drawer_events (event_id, order_guid, printer_id, print_job_id, state, reason, retry_count, requested_at_iso, completed_at_iso, last_error_code, created_at_iso, updated_at_iso)
      VALUES ('drawer-rebind', NULL, 'XP-1', 'print-1', 'Required', 'cash-sale', 0, NULL, NULL, NULL, '2026-07-28T00:00:00.000Z', '2026-07-28T00:00:00.000Z');
    UPDATE drawer_events SET printer_id = 'XP-2' WHERE event_id = 'drawer-rebind';
  `);
  assert.notEqual(rebound.status, 0);
  assert.match(rebound.stderr, /DRAWER_PRINTER_BINDING_IMMUTABLE/);
});

test("真实 SQLite 可完整执行 M1-M10；履约列存在且同目录版本允许 active 与 staging 并存", () => {
  const migrations = POS_DATABASE_MIGRATIONS.map((migration) => migration.sql).join("\n");
  const accepted = runSqlite(`${migrations}
    PRAGMA foreign_keys = ON;
    INSERT INTO catalog_snapshots (snapshot_id, catalog_version, checksum, state, downloaded_at_iso, activated_at_iso)
      VALUES ('active-1', 'server-v1', 'checksum-a', 'active', '2026-07-28T00:00:00.000Z', '2026-07-28T00:00:00.000Z');
    INSERT INTO catalog_snapshots (snapshot_id, catalog_version, checksum, state, downloaded_at_iso, activated_at_iso)
      VALUES ('staging-1', 'server-v1', 'checksum-b', 'staging', '2026-07-28T00:01:00.000Z', NULL);
    INSERT INTO print_jobs (job_id, order_guid, state, printer_id, receipt_ciphertext, is_reprint, retry_count, last_error_code, created_at_iso, updated_at_iso)
      VALUES ('print-1', NULL, 'Queued', 'XP-1', X'1B40', 0, 0, NULL, '2026-07-28T00:00:00.000Z', '2026-07-28T00:00:00.000Z');
    INSERT INTO drawer_events (event_id, order_guid, printer_id, print_job_id, state, reason, retry_count, requested_at_iso, completed_at_iso, last_error_code, created_at_iso, updated_at_iso)
      VALUES ('drawer-1', NULL, 'XP-1', 'print-1', 'Required', 'cash-sale', 0, NULL, NULL, NULL, '2026-07-28T00:00:00.000Z', '2026-07-28T00:00:00.000Z');
    SELECT (SELECT COUNT(*) FROM catalog_snapshots WHERE catalog_version = 'server-v1') || '|' ||
      (SELECT COUNT(*) FROM catalog_snapshots WHERE state = 'active') || '|' ||
      (SELECT COUNT(*) FROM drawer_events WHERE created_at_iso IS NOT NULL AND updated_at_iso IS NOT NULL);
  `);

  assert.equal(accepted.status, 0, accepted.stderr);
  assert.equal(accepted.stdout.trim(), "2|1|1");

  const rejected = runSqlite(`${migrations}
    INSERT INTO catalog_snapshots (snapshot_id, catalog_version, checksum, state, downloaded_at_iso, activated_at_iso)
      VALUES ('active-1', 'server-v1', 'checksum-a', 'active', '2026-07-28T00:00:00.000Z', '2026-07-28T00:00:00.000Z');
    INSERT INTO catalog_snapshots (snapshot_id, catalog_version, checksum, state, downloaded_at_iso, activated_at_iso)
      VALUES ('active-2', 'server-v1', 'checksum-b', 'active', '2026-07-28T00:01:00.000Z', '2026-07-28T00:01:00.000Z');
  `);
  assert.notEqual(rejected.status, 0, "同一时刻只能保留一个 active 目录快照。");
  assert.match(rejected.stderr, /UNIQUE constraint failed: catalog_snapshots\.state/);
});

test("M38 修复旧 M36 版本冲突并补建分期生命周期恢复表", () => {
  const throughM35 = POS_DATABASE_MIGRATIONS
    .filter((migration) => migration.version <= 35)
    .map((migration) => migration.sql)
    .join("\n");
  const m37 =
    POS_DATABASE_MIGRATIONS.find((migration) => migration.version === 37)?.sql ??
    "";
  const repair =
    POS_DATABASE_MIGRATIONS.find((migration) => migration.version === 38)?.sql ??
    "";
  const result = runSqlite(`${throughM35}
    INSERT INTO schema_migrations (version, name, applied_at_iso)
      VALUES (36, 'M36_shared_held_order_claims', '2026-08-05T09:18:48.280Z');
    ${m37}
    INSERT INTO schema_migrations (version, name, applied_at_iso)
      VALUES (37, 'M37_installment_action_resolution_code', '2026-08-06T04:42:07.698Z');
    ${repair}
    SELECT
      (SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'installment_lifecycle_actions') || '|' ||
      (SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = 'ux_installment_lifecycle_terminal_blocking');
  `);

  assert.equal(result.status, 0, result.stderr);
  assert.equal(result.stdout.trim(), "1|1");
  assert.match(repair, /CREATE TABLE IF NOT EXISTS installment_lifecycle_actions/);
});

function runSqlite(input: string): Readonly<{ status: number | null; stdout: string; stderr: string }> {
  const result = spawnSync(process.env.SQLITE3_BINARY ?? "sqlite3", [":memory:"], {
    input,
    encoding: "utf8",
  });
  if (result.error) throw result.error;
  return { status: result.status, stdout: result.stdout, stderr: result.stderr };
}

test("local_sequence 在独占事务内递增且不使用设备时间排序", async () => {
  const connection = new RecordingConnection();
  connection.nextSequence = 42;
  const database = await PosDatabase.open(options(connection));
  connection.transactionCount = 0;

  assert.equal(await database.nextLocalSequence(), 42);
  assert.equal(connection.transactionCount, 1);
  assert.ok(connection.runs.some((entry) => entry.sql.includes("INSERT INTO app_settings")));
  assert.ok(connection.executed.every((sql) => !sql.includes("Date.now")));
});

test("顶部同步状态只通过窄读接口查询非成功 order-sync 去重数量", async () => {
  const connection = new RecordingConnection();
  connection.pendingOrderSyncCount = 6;
  const database = await PosDatabase.open(options(connection));

  assert.equal(await database.readPendingOrderSyncCount(), 6);
  assert.match(
    connection.firstQueries.at(-1) ?? "",
    /kind = 'order-sync'[\s\S]*state <> 'succeeded'/,
  );
});

test("完整现金单在一个独占事务内写入订单、审计和 outbox", async () => {
  const connection = new RecordingConnection();
  const database = await PosDatabase.open(options(connection));
  connection.transactionCount = 0;

  await database.runInTransaction((transaction) =>
    transaction.completeCashOrder({
      order: {
        orderGuid: "order-1",
        localSequence: 1,
        storeCode: "S1",
        deviceCode: "IPAD1",
        cashierId: "cashier-1",
        cashierName: "Cashier",
        soldAtIso: "2026-07-28T00:00:00.000Z",
        state: "PendingSync",
        total: { currency: "AUD", cents: 500 },
        discount: { currency: "AUD", cents: 0 },
        actualAmount: { currency: "AUD", cents: 500 },
        lines: [
          {
            lineId: "line-1",
            productCode: "P1",
            itemNumber: null,
            lookupCode: "123",
            displayName: "Item",
            quantity: "1",
            unitPrice: { currency: "AUD", cents: 500 },
            discount: { currency: "AUD", cents: 0 },
            actualAmount: { currency: "AUD", cents: 500 },
            priceSource: "catalog",
            syncProvenance: {
              referenceCode: "REF-P1",
              priceSource: 0,
            },
            kind: "sale",
            returnSourceKey: null,
            originalOrderGuid: null,
            originalOrderDetailGuid: null,
          },
        ],
        tenders: [
          { tenderGuid: "tender-1", method: "cash", amount: { currency: "AUD", cents: 500 }, reference: null, reservationToken: null },
        ],
        originalOrderGuid: null,
      },
      auditEvents: [
        { eventId: "audit-1", eventType: "cash-sale", occurredAtIso: "2026-07-28T00:00:00.000Z", orderGuid: "order-1", correlationId: "order-1", payload: { amountCents: 500 } },
      ],
      outbox: { messageId: "outbox-1", aggregateId: "order-1", kind: "order-sync", payloadJson: "{}", nextAttemptAtIso: "2026-07-28T00:00:00.000Z" },
      requiresDrawer: true,
      printPolicy: "automatic",
    }),
  );

  assert.equal(connection.transactionCount, 1);
  assert.ok(connection.runs.some((entry) => entry.sql.includes("INSERT INTO local_orders")));
  assert.ok(connection.runs.some((entry) => entry.sql.includes("INSERT INTO audit_events")));
  assert.ok(connection.runs.some((entry) => entry.sql.includes("INSERT INTO outbox_messages")));
  assert.ok(
    connection.runs.every(
      (entry) =>
        !entry.sql.includes("authorization") &&
        !entry.sql.includes("payment_reference"),
    ),
  );
});

test("审计 payload 出现授权或支付引用时，在事务写入前拒绝并且不落普通 JSON 列", async () => {
  const connection = new RecordingConnection();
  const database = await PosDatabase.open(options(connection));
  connection.runs.length = 0;

  await assert.rejects(
    () => database.runInTransaction((transaction) => transaction.completeCashOrder({
      order: {
        orderGuid: "order-sensitive", localSequence: 2, storeCode: "S1", deviceCode: "IPAD1", cashierId: "cashier-1", cashierName: "Cashier",
        soldAtIso: "2026-07-28T00:00:00.000Z", state: "PendingSync", total: { currency: "AUD", cents: 500 }, discount: { currency: "AUD", cents: 0 }, actualAmount: { currency: "AUD", cents: 500 },
        lines: [{ lineId: "line-sensitive", productCode: "P1", itemNumber: null, lookupCode: "123", displayName: "Item", quantity: "1", unitPrice: { currency: "AUD", cents: 500 }, discount: { currency: "AUD", cents: 0 }, actualAmount: { currency: "AUD", cents: 500 }, priceSource: "catalog", syncProvenance: { referenceCode: "REF-P1", priceSource: 0 }, kind: "sale", returnSourceKey: null, originalOrderGuid: null, originalOrderDetailGuid: null }],
        tenders: [{ tenderGuid: "tender-sensitive", method: "cash", amount: { currency: "AUD", cents: 500 }, reference: null, reservationToken: null }], originalOrderGuid: null,
      },
      auditEvents: [{ eventId: "audit-sensitive", eventType: "cash-sale", occurredAtIso: "2026-07-28T00:00:00.000Z", orderGuid: "order-sensitive", correlationId: "order-sensitive", payload: { nested: { authorizationToken: "must-not-persist" } } }],
      outbox: { messageId: "outbox-sensitive", aggregateId: "order-sensitive", kind: "order-sync", payloadJson: "{}", nextAttemptAtIso: "2026-07-28T00:00:00.000Z" }, requiresDrawer: false, printPolicy: "never",
    })),
    /sensitive audit payload key/i,
  );

  assert.equal(connection.runs.length, 0);
});
