import assert from "node:assert/strict";
import { DatabaseSync } from "node:sqlite";
import test from "node:test";

import { SqliteLocalReturnRecordReader } from "./sqlite-local-return-records";
import type { SqliteConnectionPort, SqlRunResult, SqlValue } from "./types";

const original = "11111111-1111-4111-8111-111111111111";
const detail = "22222222-2222-4222-8222-222222222222";

function seed(): DatabaseSync {
  const database = new DatabaseSync(":memory:");
  database.exec(`
    CREATE TABLE local_orders (order_guid TEXT PRIMARY KEY, state TEXT NOT NULL);
    CREATE TABLE local_return_records (
      return_detail_guid TEXT PRIMARY KEY,
      return_order_guid TEXT NOT NULL,
      original_order_guid TEXT NULL,
      original_order_detail_guid TEXT NULL,
      return_quantity INTEGER NOT NULL,
      return_amount_cents INTEGER NOT NULL,
      created_at_iso TEXT NOT NULL
    );
    INSERT INTO local_orders VALUES
      ('r-synced', 'Synced'), ('r-pending', 'PendingSync'),
      ('r-local', 'CompletedLocal'), ('r-other', 'PendingSync');
    INSERT INTO local_return_records VALUES
      ('d1', 'r-synced', '${original}', '${detail}', 1, 334, '2026-10-27T01:00:00Z'),
      ('d2', 'r-pending', '${original}', '${detail}', 2, 668, '2026-10-27T02:00:00Z'),
      ('d3', 'r-local', '${original}', NULL, 1, 100, '2026-10-27T03:00:00Z'),
      ('d4', 'r-other', 'another-original', '${detail}', 1, 50, '2026-10-27T04:00:00Z');
  `);
  return database;
}

test("本机退货记录按原单读取，unsyncedOnly 只返回尚未同步成功的退货单", async () => {
  const database = seed();
  try {
    const reader = new SqliteLocalReturnRecordReader(connection(database));

    const all = await reader.listForOriginalOrder({
      originalOrderGuid: original,
      unsyncedOnly: false,
    });
    assert.deepEqual(
      all.map((record) => [record.returnOrderGuid, record.synced]),
      [
        ["r-synced", true],
        ["r-pending", false],
        ["r-local", false],
      ],
    );
    assert.equal(all[2]?.originalOrderDetailGuid, null);

    const unsynced = await reader.listForOriginalOrder({
      originalOrderGuid: original,
      unsyncedOnly: true,
    });
    assert.deepEqual(
      unsynced.map((record) => [record.returnOrderGuid, record.returnQuantity, record.returnAmountCents]),
      [
        ["r-pending", 2, 668],
        ["r-local", 1, 100],
      ],
    );
  } finally {
    database.close();
  }
});

test("本机退货记录出现非法数量时抛错，不静默当作 0", async () => {
  const database = seed();
  try {
    const reader = new SqliteLocalReturnRecordReader({
      ...connection(database),
      async getAll<T extends object>() {
        return [
          {
            return_order_guid: "r-1",
            original_order_guid: original,
            original_order_detail_guid: detail,
            return_quantity: 0,
            return_amount_cents: 1,
            state: "PendingSync",
          },
        ] as unknown as readonly T[];
      },
    });

    await assert.rejects(
      () => reader.listForOriginalOrder({ originalOrderGuid: original, unsyncedOnly: false }),
      TypeError,
    );
  } finally {
    database.close();
  }
});

function connection(database: DatabaseSync): SqliteConnectionPort {
  const unsupported = (): Promise<never> =>
    Promise.reject(new Error("unsupported test connection operation"));
  return {
    exec: unsupported,
    run: unsupported as (sql: string, parameters?: readonly SqlValue[]) => Promise<SqlRunResult>,
    getFirst: unsupported,
    async getAll<T extends object>(sql: string, parameters: readonly SqlValue[] = []) {
      return database.prepare(sql).all(...(parameters as never[])) as unknown as readonly T[];
    },
    withExclusiveTransaction: unsupported,
    close: async () => undefined,
  };
}
