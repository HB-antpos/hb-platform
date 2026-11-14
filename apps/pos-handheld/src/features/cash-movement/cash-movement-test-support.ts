import { DatabaseSync, type SQLInputValue } from "node:sqlite";

import {
  SqliteCashMovementRepository,
  SqliteCashMovementUploadRepository,
} from "@hb/pos-db/core/db/sqlite-cash-movement-repository";
import type { SqliteConnectionPort, SqlRunResult, SqlValue } from "@hb/pos-db/core/db/types";

import { applyMigrations } from "../../core/db/migrations";

/**
 * 测试支持：基于 node:sqlite 内存库的真实连接（跑完整迁移），供现金进出的运行时 / 组合根测试使用。
 * 不属于生产代码，只被 *.test.ts 引用。
 */
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

export type MemoryCashMovementDatabase = Readonly<{
  connection: SqliteConnectionPort;
  repository: SqliteCashMovementRepository;
  uploads: SqliteCashMovementUploadRepository;
  close(): Promise<void>;
}>;

export async function createMemoryCashMovementDatabase(
  nowIso = "2026-11-14T00:00:00.000Z",
): Promise<MemoryCashMovementDatabase> {
  const connection = new SystemSqliteConnection(new DatabaseSync(":memory:"));
  await applyMigrations(connection, () => nowIso);
  return {
    connection,
    repository: new SqliteCashMovementRepository(connection),
    uploads: new SqliteCashMovementUploadRepository(connection),
    close: () => connection.close(),
  };
}
