import assert from "node:assert/strict";
import { DatabaseSync, type SQLInputValue } from "node:sqlite";
import test from "node:test";

import type { SpecialProductCategorySnapshot } from "@hb/pos-domain/core/contracts/special-product-categories";
import { SqliteSpecialProductCategoriesRepository } from "@hb/pos-db/core/db/sqlite-special-product-categories-repository";
import { SqliteSpecialProductsRepository } from "@hb/pos-db/core/db/sqlite-special-products-repository";
import type {
  SqliteConnectionPort,
  SqlRunResult,
  SqlValue,
} from "@hb/pos-db/core/db/types";

import { applyMigrations, POS_DATABASE_MIGRATIONS } from "./migrations";

const T0 = "2026-11-11T00:00:00.000Z";
const T1 = "2026-11-11T01:00:00.000Z";

test("M60 是最新迁移，建出两张分类表和索引，且不动 local_special_products", async () => {
  await withDatabase(async (connection) => {
    const latest = POS_DATABASE_MIGRATIONS.at(-1);
    assert.equal(latest?.version, 60);
    assert.equal(latest?.name, "M60_special_product_categories");

    // 先停在 M59，造一条特殊商品和一条设置，再升级到 M60，确认旧数据原样保留。
    await applyMigrations(
      connection,
      () => T0,
      POS_DATABASE_MIGRATIONS.filter((migration) => migration.version <= 59),
    );
    await new SqliteSpecialProductsRepository(connection).replaceDownloaded(
      "S1",
      [specialProduct("P1")],
    );
    await applyMigrations(connection, () => T1);

    assert.equal(await maxVersion(connection), 60);
    const names = (
      await connection.getAll<{ name: string }>(
        `SELECT name FROM sqlite_master
         WHERE name LIKE '%special_product_categor%'
           AND name NOT LIKE 'sqlite_autoindex_%'
         ORDER BY name`,
      )
    ).map((row) => row.name);
    assert.deepEqual(names, [
      "ix_local_special_product_categories_store_sort",
      "ix_local_special_product_category_items_category",
      "local_special_product_categories",
      "local_special_product_category_items",
    ]);
    const kept = await new SqliteSpecialProductsRepository(connection).list(
      "S1",
      10,
      0,
    );
    assert.deepEqual(
      kept.map((item) => item.productCode),
      ["P1"],
    );
  });
});

test("readSnapshot 分类并列 SortOrder 时按 categoryId 升序", async () => {
  await withDatabase(async (connection) => {
    const repository = await migratedRepository(connection);
    await repository.replaceSnapshot(
      snapshot("S1", {
        assignments: [],
        categories: [
          category("b-guid", "Aaa", 1),
          category("c-guid", "Bbb", 0),
          category("a-guid", "Zzz", 1),
        ],
      }),
    );
    assert.deepEqual(
      (await repository.readSnapshot("S1")).categories.map((item) => item.categoryId),
      ["c-guid", "a-guid", "b-guid"],
    );
  });
});

test("readSnapshot 没有数据时返回空数组", async () => {
  await withDatabase(async (connection) => {
    const repository = await migratedRepository(connection);
    const snapshot = await repository.readSnapshot("S1");
    assert.deepEqual(snapshot, { categories: [], assignments: [] });
  });
});

test("replaceSnapshot 整体覆盖本店快照，不影响其他门店", async () => {
  await withDatabase(async (connection) => {
    const repository = await migratedRepository(connection);
    await repository.replaceSnapshot(snapshot("S1"));
    await repository.replaceSnapshot(
      snapshot("S2", {
        categories: [category("X1", "Other store", 0)],
        assignments: [{ categoryId: "X1", productCode: "P1", sortOrder: 0 }],
      }),
    );

    const s1 = await repository.readSnapshot("S1");
    assert.deepEqual(
      s1.categories.map((item) => [item.categoryId, item.name, item.color, item.sortOrder]),
      [
        ["C1", "Balloons", "#1D6FB8", 0],
        ["C2", "Cards", null, 1],
      ],
    );
    assert.deepEqual(
      s1.assignments.map((item) => [item.productCode, item.categoryId, item.sortOrder]),
      [
        ["P1", "C1", 0],
        ["P2", "C1", 1],
        ["P3", "C2", 0],
      ],
    );

    // 第二次覆盖：删掉分类 C2 及其归属，S2 不受影响。
    await repository.replaceSnapshot(
      snapshot("S1", {
        categories: [category("C1", "Balloons", 0, "#1D6FB8")],
        assignments: [{ categoryId: "C1", productCode: "P2", sortOrder: 0 }],
      }),
    );
    const replaced = await repository.readSnapshot("S1");
    assert.deepEqual(
      replaced.categories.map((item) => item.categoryId),
      ["C1"],
    );
    assert.deepEqual(
      replaced.assignments.map((item) => item.productCode),
      ["P2"],
    );
    const other = await repository.readSnapshot("S2");
    assert.deepEqual(
      other.categories.map((item) => item.categoryId),
      ["X1"],
    );

    // 没有任何分类的快照 = 清空本店（分类被全部删除）。
    await repository.replaceSnapshot(
      snapshot("S1", { categories: [], assignments: [] }),
    );
    assert.deepEqual(await repository.readSnapshot("S1"), {
      categories: [],
      assignments: [],
    });
    assert.equal((await repository.readSnapshot("S2")).categories.length, 1);
  });
});

test("replaceSnapshot 校验失败时整体回滚，旧快照原样保留", async () => {
  await withDatabase(async (connection) => {
    const repository = await migratedRepository(connection);
    await repository.replaceSnapshot(snapshot("S1"));
    const before = await repository.readSnapshot("S1");

    const invalid: readonly SpecialProductCategorySnapshot[] = [
      // 归属指向不存在的分类
      snapshot("S1", {
        assignments: [{ categoryId: "NOPE", productCode: "P1", sortOrder: 0 }],
      }),
      // 同一商品两个归属
      snapshot("S1", {
        assignments: [
          { categoryId: "C1", productCode: "P1", sortOrder: 0 },
          { categoryId: "C2", productCode: "P1", sortOrder: 0 },
        ],
      }),
      // 分类 id 重复
      snapshot("S1", {
        categories: [
          category("C1", "A", 0),
          category("C1", "B", 1),
        ],
        assignments: [],
      }),
      // 颜色不合规
      snapshot("S1", {
        categories: [category("C1", "A", 0, "red")],
        assignments: [],
      }),
    ];
    for (const candidate of invalid) {
      await assert.rejects(() => repository.replaceSnapshot(candidate));
      assert.deepEqual(await repository.readSnapshot("S1"), before);
    }

    // 写入中途失败（CHECK 约束：名称超长）同样整体回滚。
    await assert.rejects(() =>
      repository.replaceSnapshot(
        snapshot("S1", {
          categories: [
            category("C9", "ok", 0),
            category("C10", "x".repeat(200), 1),
          ],
          assignments: [],
        }),
      ),
    );
    assert.deepEqual(await repository.readSnapshot("S1"), before);
  });
});

test("listCategoryProducts 只返回本机仍是特殊商品的归属，按分类内服务器顺序", async () => {
  await withDatabase(async (connection) => {
    const repository = await migratedRepository(connection);
    const products = new SqliteSpecialProductsRepository(connection);
    // P2 在本机已取消标记（不在 local_special_products），归属行还在。
    await products.replaceDownloaded("S1", [
      specialProduct("P1"),
      specialProduct("P3"),
      specialProduct("P4"),
    ]);
    await repository.replaceSnapshot(
      snapshot("S1", {
        assignments: [
          { categoryId: "C1", productCode: "P1", sortOrder: 5 },
          { categoryId: "C1", productCode: "P2", sortOrder: 1 },
          { categoryId: "C1", productCode: "P4", sortOrder: 2 },
          { categoryId: "C2", productCode: "P3", sortOrder: 0 },
        ],
      }),
    );

    assert.deepEqual(
      (await repository.listCategoryProducts("S1", "C1")).map(
        (item) => item.productCode,
      ),
      ["P4", "P1"],
    );
    assert.deepEqual(
      (await repository.listCategoryProducts("S1", "C2")).map(
        (item) => item.productCode,
      ),
      ["P3"],
    );
    assert.deepEqual(await repository.listCategoryProducts("S1", "NOPE"), []);
    assert.deepEqual(await repository.listCategoryProducts("S2", "C1"), []);
  });
});

test("当前选中分类按分店记在 app_settings，重复保存会覆盖", async () => {
  await withDatabase(async (connection) => {
    const repository = await migratedRepository(connection);
    assert.equal(await repository.readSelectedCategory("S1"), null);

    await repository.saveSelectedCategory("S1", "C2");
    await repository.saveSelectedCategory("S2", "uncategorized");
    assert.equal(await repository.readSelectedCategory("S1"), "C2");
    assert.equal(await repository.readSelectedCategory("S2"), "uncategorized");

    await repository.saveSelectedCategory("S1", "all");
    assert.equal(await repository.readSelectedCategory("S1"), "all");
    assert.equal(await repository.readSelectedCategory("S2"), "uncategorized");

    // 记录损坏（空串）按没有记录处理。
    await connection.run(
      "UPDATE app_settings SET setting_value = '' WHERE setting_key = ?",
      ["special_products_selected_category_v1:S1"],
    );
    assert.equal(await repository.readSelectedCategory("S1"), null);

    await assert.rejects(() => repository.saveSelectedCategory("S1", " "));
    await assert.rejects(() => repository.saveSelectedCategory(" ", "C1"));
  });
});

test("M60 之后分类表缺失时重新开库会自愈重建，列不完整时抛明确错误码", async () => {
  await withDatabase(async (connection) => {
    const repository = await migratedRepository(connection);
    await repository.replaceSnapshot(snapshot("S1"));

    await connection.exec(`
      DROP TABLE local_special_product_category_items;
      DROP TABLE local_special_product_categories;
    `);
    await applyMigrations(connection, () => T1);
    // 自愈只重建结构，快照为空，之后可以正常写入。
    assert.deepEqual(await repository.readSnapshot("S1"), {
      categories: [],
      assignments: [],
    });
    await repository.replaceSnapshot(snapshot("S1"));
    assert.equal((await repository.readSnapshot("S1")).categories.length, 2);

    // 列缺失：不静默改表，抛明确错误码（整笔事务回滚）。
    await connection.exec(`
      DROP TABLE local_special_product_category_items;
      CREATE TABLE local_special_product_category_items (
        store_code TEXT NOT NULL,
        product_code TEXT NOT NULL,
        PRIMARY KEY (store_code, product_code)
      );
    `);
    await assert.rejects(
      () => applyMigrations(connection, () => T1),
      /LOCAL_SPECIAL_PRODUCT_CATEGORIES_SCHEMA_INVALID:local_special_product_category_items:MISSING_COLUMNS=category_id,sort_order/u,
    );
  });
});

async function migratedRepository(
  connection: SqliteConnectionPort,
): Promise<SqliteSpecialProductCategoriesRepository> {
  await applyMigrations(connection, () => T0);
  return new SqliteSpecialProductCategoriesRepository(connection, () => T1);
}

function category(
  categoryId: string,
  name: string,
  sortOrder: number,
  color: string | null = null,
) {
  return { categoryId, color, name, sortOrder };
}

function snapshot(
  storeCode: string,
  overrides: Partial<SpecialProductCategorySnapshot> = {},
): SpecialProductCategorySnapshot {
  return {
    assignments: [
      { categoryId: "C1", productCode: "P1", sortOrder: 0 },
      { categoryId: "C1", productCode: "P2", sortOrder: 1 },
      { categoryId: "C2", productCode: "P3", sortOrder: 0 },
    ],
    categories: [
      category("C1", "Balloons", 0, "#1D6FB8"),
      category("C2", "Cards", 1),
    ],
    generatedAt: T0,
    revision: "1",
    storeCode,
    ...overrides,
  };
}

function specialProduct(productCode: string) {
  return {
    barcode: `930${productCode}`,
    discountRate: null,
    displayName: `Product ${productCode}`,
    itemNumber: `ITEM-${productCode}`,
    lookupCode: `LOOKUP-${productCode}`,
    priceSource: 0 as const,
    productCode,
    productImage: null,
    quantityFactor: 1,
    referenceCode: null,
    retailPriceCents: 1_000,
    storeCode: "S1",
  };
}

async function maxVersion(connection: SqliteConnectionPort): Promise<number> {
  return Number(
    (
      await connection.getFirst<{ version: unknown }>(
        "SELECT MAX(version) AS version FROM schema_migrations",
      )
    )?.version,
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
