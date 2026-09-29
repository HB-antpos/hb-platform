import assert from "node:assert/strict";
import { DatabaseSync, type SQLInputValue } from "node:sqlite";
import test from "node:test";

import type {
  SqliteConnectionPort,
  SqliteDriverPort,
  SqlRunResult,
  SqlValue,
} from "@hb/pos-db/core/db/types";

import { SqliteCatalogCodeConflictRepository } from "./catalog-code-conflict-repository";
import { SqliteCatalogLookupOverlayRepository } from "./catalog-lookup-overlay-repository";
import {
  SqliteCatalogSnapshotRepository,
  type LocalCatalogMatch,
} from "./catalog-repository";
import { applyMigrations, POS_DATABASE_MIGRATIONS } from "./migrations";
import { PosDatabase } from "./pos-database";

import { findExactCatalogCandidates } from "@/features/catalog/catalog-code-conflicts";

const T0 = "2026-09-26T00:00:00.000Z";
const T1 = "2026-09-26T01:00:00.000Z";
const CONFLICT_CODE = "6405090401470";

test("M45 从 M44 增量新增码冲突候选表，主键前缀即门店 + 查询码点查索引", async () => {
  await withDatabase(async (connection) => {
    await applyMigrations(
      connection,
      () => T0,
      POS_DATABASE_MIGRATIONS.filter((migration) => migration.version <= 44),
    );
    await applyMigrations(connection, () => T1);
    await applyMigrations(connection, () => T1);

    assert.equal(await schemaVersion(connection), 45);
    const columns = await connection.getAll<{ name: string; pk: number }>(
      "PRAGMA table_info('catalog_code_conflicts')",
    );
    assert.deepEqual(
      columns.filter((column) => Number(column.pk) > 0)
        .sort((left, right) => Number(left.pk) - Number(right.pk))
        .map((column) => column.name),
      ["store_code", "lookup_code_normalized", "product_code"],
    );

    const repository = new SqliteCatalogCodeConflictRepository(connection);
    await repository.replaceStoreConflicts("1042", [fly(), flower()]);
    const plan = await connection.getAll<{ detail: string }>(
      `EXPLAIN QUERY PLAN
       SELECT product_code FROM catalog_code_conflicts
       WHERE store_code = ? AND lookup_code_normalized = ?
       ORDER BY candidate_order ASC, product_code ASC`,
      ["1042", CONFLICT_CODE],
    );
    assert.ok(
      plan.some((row) => /SEARCH catalog_code_conflicts USING/u.test(row.detail)),
      JSON.stringify(plan),
    );
    assert.equal(
      plan.some((row) => /SCAN catalog_code_conflicts/u.test(row.detail)),
      false,
    );
  });
});

test("按门店原子替换候选：保留服务端顺序，同商品大小写重复只留首条，其他门店不受影响", async () => {
  await withMigratedDatabase(async (connection) => {
    const repository = new SqliteCatalogCodeConflictRepository(connection);
    await repository.replaceStoreConflicts("OTHER", [
      fly({ storeCode: "OTHER" }),
      flower({ storeCode: "OTHER" }),
    ]);

    const result = await repository.replaceStoreConflicts("1042", [
      fly(),
      flower(),
      flower({ productCode: "p-flower", retailPriceCents: 1 }),
      fly({
        lookupCode: "9503009941",
        lookupCodeNormalized: "9503009941",
      }),
    ]);

    assert.deepEqual(result, {
      storeCode: "1042",
      itemCount: 3,
      lookupCodeCount: 2,
    });
    assert.deepEqual(
      (await repository.findCandidates("1042", ` ${CONFLICT_CODE} `)).map(
        (candidate) => [candidate.productCode, candidate.retailPriceCents],
      ),
      [
        ["P-FLY", 899],
        ["P-FLOWER", 299],
      ],
    );
    assert.deepEqual(await repository.findCandidates("1042", "UNKNOWN"), []);
    assert.equal((await repository.findCandidates("OTHER", CONFLICT_CODE)).length, 2);

    await repository.replaceStoreConflicts("1042", [flower()]);
    assert.deepEqual(
      (await repository.findCandidates("1042", CONFLICT_CODE)).map(
        (candidate) => candidate.productCode,
      ),
      ["P-FLOWER"],
    );
    assert.deepEqual(await repository.findCandidates("1042", "9503009941"), []);
    assert.equal((await repository.findCandidates("OTHER", CONFLICT_CODE)).length, 2);
  });
});

test("候选校验或中途写入失败整体回滚，本地旧候选原样保留", async () => {
  await withDatabase(async (base) => {
    const connection = new FailingInsertConnection(base);
    await applyMigrations(connection, () => T0);
    const repository = new SqliteCatalogCodeConflictRepository(connection);
    await repository.replaceStoreConflicts("1042", [fly(), flower()]);

    await assert.rejects(
      () =>
        repository.replaceStoreConflicts("1042", [
          fly({ storeCode: "OTHER" }),
        ]),
      /another store/u,
    );
    await assert.rejects(
      () =>
        repository.replaceStoreConflicts("1042", [
          fly({ lookupCodeNormalized: "lower-case" }),
        ]),
      /normalized/u,
    );

    // 中文注释：120 行需要三批 INSERT，第二批失败时删除与第一批都必须回滚。
    connection.armFailure(2);
    const many = Array.from({ length: 120 }, (_, index) =>
      flower({
        productCode: `P-${index}`,
        lookupCode: `CODE-${Math.floor(index / 2)}`,
        lookupCodeNormalized: `CODE-${Math.floor(index / 2)}`,
      }),
    );
    await assert.rejects(
      () => repository.replaceStoreConflicts("1042", many),
      /injected insert failure/u,
    );
    assert.deepEqual(
      (await repository.findCandidates("1042", CONFLICT_CODE)).map(
        (candidate) => candidate.productCode,
      ),
      ["P-FLY", "P-FLOWER"],
    );
  });
});

test("精确候选只给当前目录仍存在的码补商品，覆盖层胜出项与 tombstone 生效且不复活已删除码", async () => {
  await withMigratedDatabase(async (connection) => {
    await insertSnapshot(connection, "snapshot-1", "active");
    await insertCatalogItem(connection, "snapshot-1", fly());
    const overlay = new SqliteCatalogLookupOverlayRepository(
      connection,
      () => T1,
    );
    const conflicts = new SqliteCatalogCodeConflictRepository(connection);
    await conflicts.replaceStoreConflicts("1042", [
      fly({ retailPriceCents: 999 }),
      flower(),
      flower({
        productCode: "P-ORPHAN",
        lookupCode: "DELETED-CODE",
        lookupCodeNormalized: "DELETED-CODE",
      }),
    ]);
    const sources = {
      findExact: (code: string) => overlay.findExact("1042", code),
      findConflicts: (storeCode: string, code: string) =>
        conflicts.findCandidates(storeCode, code),
    };

    // 中文注释：目录行保留自身版本（8.99），候选表中同商品的行不重复出现。
    assert.deepEqual(
      (await findExactCatalogCandidates(sources, CONFLICT_CODE)).map(
        (candidate) => [candidate.productCode, candidate.retailPriceCents],
      ),
      [
        ["P-FLY", 899],
        ["P-FLOWER", 299],
      ],
    );
    assert.deepEqual(
      await findExactCatalogCandidates(sources, "DELETED-CODE"),
      [],
    );

    // 中文注释：扫码后的在线回查只写胜出项覆盖，不会冲掉其它候选。
    assert.equal(
      await overlay.upsert({
        baseSnapshotId: "snapshot-1",
        item: fly({ retailPriceCents: 950 }),
      }),
      "applied",
    );
    assert.deepEqual(
      (await findExactCatalogCandidates(sources, CONFLICT_CODE)).map(
        (candidate) => [candidate.productCode, candidate.retailPriceCents],
      ),
      [
        ["P-FLY", 950],
        ["P-FLOWER", 299],
      ],
    );

    await overlay.tombstone({
      baseSnapshotId: "snapshot-1",
      storeCode: "1042",
      lookupCodeNormalized: CONFLICT_CODE,
    });
    assert.deepEqual(await findExactCatalogCandidates(sources, CONFLICT_CODE), []);
  });
});

test("目录快照退役清理不触及码冲突候选", async () => {
  await withMigratedDatabase(async (connection) => {
    await insertSnapshot(connection, "snapshot-old", "retired");
    await insertCatalogItem(connection, "snapshot-old", fly());
    const conflicts = new SqliteCatalogCodeConflictRepository(connection);
    await conflicts.replaceStoreConflicts("1042", [fly(), flower()]);

    const snapshots = new SqliteCatalogSnapshotRepository(connection);
    while ((await snapshots.cleanupRetiredBatch(500)) > 0) {
      // 中文注释：按批回收直到退役快照完全删除。
    }

    assert.equal(
      Number(
        (
          await connection.getFirst<{ count: number }>(
            "SELECT COUNT(*) AS count FROM catalog_snapshots",
          )
        )?.count,
      ),
      0,
    );
    assert.equal((await conflicts.findCandidates("1042", CONFLICT_CODE)).length, 2);
  });
});

test("PosDatabase 暴露码冲突候选窄仓储", async () => {
  const database = await PosDatabase.open({
    databaseName: "catalog-code-conflicts-test.db",
    driver: new SystemSqliteDriver(),
    keyProvider: {
      async getOrCreateDatabaseKey() {
        return "ab".repeat(32);
      },
    },
    nowIso: () => T1,
  });
  try {
    assert.ok(
      database.catalogCodeConflicts() instanceof
        SqliteCatalogCodeConflictRepository,
    );
  } finally {
    await database.close();
  }
});

function fly(overrides: Partial<LocalCatalogMatch> = {}): LocalCatalogMatch {
  return {
    storeCode: "1042",
    productCode: "P-FLY",
    referenceCode: null,
    itemNumber: "EXT-FLY",
    displayName: "EXTENSION Fly Swatter",
    barcode: "9300000000017",
    lookupCode: CONFLICT_CODE,
    lookupCodeNormalized: CONFLICT_CODE,
    retailPriceCents: 899,
    priceSource: 2,
    priceSourceLabel: "set",
    quantityFactor: 1,
    taxRateBasisPoints: null,
    updatedAtIso: T0,
    rowVersion: "row-fly",
    productImage: null,
    discountRate: null,
    isSpecialProduct: false,
    ...overrides,
  };
}

function flower(overrides: Partial<LocalCatalogMatch> = {}): LocalCatalogMatch {
  return fly({
    productCode: "P-FLOWER",
    itemNumber: "FLW-1",
    displayName: "flower",
    barcode: CONFLICT_CODE,
    retailPriceCents: 299,
    priceSource: 0,
    priceSourceLabel: "product",
    rowVersion: "row-flower",
    ...overrides,
  });
}

class SystemSqliteDriver implements SqliteDriverPort {
  public async open(_databaseName: string): Promise<SqliteConnectionPort> {
    return new SystemSqliteConnection(new DatabaseSync(":memory:"));
  }
}

class SystemSqliteConnection implements SqliteConnectionPort {
  public constructor(protected readonly database: DatabaseSync) {
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
    try {
      const result = await operation(this.transactionConnection());
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

  protected transactionConnection(): SqliteConnectionPort {
    return new TransactionConnection(this.database);
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

/** 在事务内第 N 次 INSERT 码冲突表时注入失败，用于验证整体回滚。 */
class FailingInsertConnection extends SystemSqliteConnection {
  private failOnInsertNumber: number | null = null;
  private insertCount = 0;

  public armFailure(insertNumber: number): void {
    this.insertCount = 0;
    this.failOnInsertNumber = insertNumber;
  }

  public constructor(base: SystemSqliteConnection) {
    super((base as unknown as { database: DatabaseSync }).database);
  }

  protected override transactionConnection(): SqliteConnectionPort {
    const transaction = super.transactionConnection();
    return {
      exec: (sql) => transaction.exec(sql),
      getFirst: (sql, parameters) => transaction.getFirst(sql, parameters),
      getAll: (sql, parameters) => transaction.getAll(sql, parameters),
      withExclusiveTransaction: (operation) =>
        transaction.withExclusiveTransaction(operation),
      close: () => transaction.close(),
      run: async (sql, parameters) => {
        if (sql.includes("INSERT INTO catalog_code_conflicts")) {
          this.insertCount += 1;
          if (this.insertCount === this.failOnInsertNumber) {
            throw new Error("injected insert failure");
          }
        }
        return transaction.run(sql, parameters);
      },
    };
  }
}

async function schemaVersion(connection: SqliteConnectionPort): Promise<number> {
  const row = await connection.getFirst<{ version: number | string | null }>(
    "SELECT MAX(version) AS version FROM schema_migrations",
  );
  return Number(row?.version ?? 0);
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

async function withMigratedDatabase(
  operation: (connection: SystemSqliteConnection) => Promise<void>,
): Promise<void> {
  await withDatabase(async (connection) => {
    await applyMigrations(connection, () => T0);
    await operation(connection);
  });
}

function toSqlInputValue(value: SqlValue): SQLInputValue {
  return value as SQLInputValue;
}

async function insertSnapshot(
  connection: SqliteConnectionPort,
  snapshotId: string,
  state: "active" | "retired",
): Promise<void> {
  await connection.run(
    `INSERT INTO catalog_snapshots (
       snapshot_id, catalog_version, checksum, state,
       downloaded_at_iso, activated_at_iso
     ) VALUES (?, ?, ?, ?, ?, ?)`,
    [snapshotId, `version-${snapshotId}`, `checksum-${snapshotId}`, state, T0, T0],
  );
}

async function insertCatalogItem(
  connection: SqliteConnectionPort,
  snapshotId: string,
  value: LocalCatalogMatch,
): Promise<void> {
  await connection.run(
    `INSERT INTO catalog_items (
       snapshot_id, store_code, lookup_code_normalized, product_code,
       reference_code, item_number, barcode, lookup_code, display_name,
       retail_price_cents, price_source, price_source_label, quantity_factor,
       tax_rate_basis_points, row_version, product_image, discount_rate,
       is_special_product, is_active, updated_at_iso
     ) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, 1, ?)`,
    [
      snapshotId,
      value.storeCode,
      value.lookupCodeNormalized,
      value.productCode,
      value.referenceCode,
      value.itemNumber,
      value.barcode,
      value.lookupCode,
      value.displayName,
      value.retailPriceCents,
      value.priceSource,
      value.priceSourceLabel,
      String(value.quantityFactor),
      value.taxRateBasisPoints,
      value.rowVersion,
      value.productImage,
      value.discountRate === null ? null : String(value.discountRate),
      value.isSpecialProduct ? 1 : 0,
      value.updatedAtIso,
    ],
  );
}
