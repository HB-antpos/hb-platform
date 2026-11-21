import * as SQLite from "expo-sqlite";

import {
  type NativeSqliteOperations,
  SerializedSqliteConnection,
} from "@hb/pos-db/core/db/serialized-sqlite-connection";
import type {
  SqliteConnectionPort,
  SqliteDriverPort,
  SqlValue,
} from "@hb/pos-db/core/db/types";

import { traceNativeCall } from "../performance/native-call-trace";

type ExpoDatabase = Awaited<ReturnType<typeof SQLite.openDatabaseAsync>>;

/** Expo Go 不含 SQLCipher；仅 Development/Preview/Production Build 可使用此实现。 */
export class ExpoSqliteDriver implements SqliteDriverPort {
  public async open(databaseName: string): Promise<SqliteConnectionPort> {
    const database = await SQLite.openDatabaseAsync(databaseName);
    return new SerializedSqliteConnection(toNativeOperations(database));
  }
}

/** 诊断旁路：追踪开启时记录每次原生调用的执行耗时（本包装位于串行队列内部）。 */
function toNativeOperations(
  database: ExpoDatabase,
): NativeSqliteOperations {
  return {
    exec: (sql) => traceNativeCall("exec", sql, () => database.execAsync(sql)),
    run: (sql, parameters) =>
      traceNativeCall("run", sql, async () => {
        const result = await database.runAsync(sql, [...parameters]);
        return {
          changes: result.changes,
          lastInsertRowId: Number(result.lastInsertRowId),
        };
      }),
    getFirst: <T extends object>(
      sql: string,
      parameters: readonly SqlValue[],
    ) =>
      traceNativeCall("getFirst", sql, () =>
        database.getFirstAsync<T>(sql, [...parameters]),
      ),
    getAll: <T extends object>(
      sql: string,
      parameters: readonly SqlValue[],
    ) =>
      traceNativeCall("getAll", sql, () =>
        database.getAllAsync<T>(sql, [...parameters]),
      ),
    close: () => database.closeAsync(),
  };
}
