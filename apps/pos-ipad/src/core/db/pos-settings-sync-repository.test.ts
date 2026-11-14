import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { mkdtempSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";

import { POS_DATABASE_MIGRATIONS } from "./migrations";
import {
  normalizePosSettingsLocalState,
  PosSettingsSyncRepository,
} from "./pos-settings-sync-repository";
import type { SqliteConnectionPort, SqlRunResult, SqlValue } from "@hb/pos-db/core/db/types";
import type { PosSettingsLocalState } from "@hb/pos-sync/features/pos-settings/pos-settings-service";

const NOW = () => "2026-11-14T00:00:00.000Z";

const state: PosSettingsLocalState = Object.freeze({
  storeCode: "S001",
  hash: "abc123",
  values: Object.freeze({
    "payment.manualCard": Object.freeze({ value: true, source: "store" }),
    "cashMovement.enabled": Object.freeze({ value: false, source: "global" }),
  }),
  syncedAtIso: "2026-11-14T00:00:00.000Z",
  overrides: Object.freeze({ "payment.manualCard": true as const }),
  offlineOverrideCount: 2,
  ackedHash: "abc123",
  ackedOverrideCount: 1,
});

class SystemSqliteConnection implements SqliteConnectionPort {
  private tail: Promise<void> = Promise.resolve();

  public constructor(public readonly databasePath: string) {}

  public async exec(sql: string): Promise<void> {
    this.execute(sql);
  }

  public async run(
    sql: string,
    parameters: readonly SqlValue[] = [],
  ): Promise<SqlRunResult> {
    this.execute(`${bind(sql, parameters)}; SELECT changes() AS changes;`);
    return { changes: 1, lastInsertRowId: 0 };
  }

  public async getFirst<T extends object>(
    sql: string,
    parameters: readonly SqlValue[] = [],
  ): Promise<T | null> {
    const result = spawnSqlite(this.databasePath, ["-json"], bind(sql, parameters));
    if (result.status !== 0) throw new Error(result.stderr);
    const rows = result.stdout.trim() ? JSON.parse(result.stdout) as readonly T[] : [];
    return rows[0] ?? null;
  }

  public async getAll<T extends object>(): Promise<readonly T[]> {
    return [];
  }

  public async withExclusiveTransaction<T>(
    operation: (transaction: SqliteConnectionPort) => Promise<T>,
  ): Promise<T> {
    const previous = this.tail;
    let release!: () => void;
    this.tail = new Promise<void>((resolve) => {
      release = resolve;
    });
    await previous;
    try {
      return await operation(this);
    } finally {
      release();
    }
  }

  public async close(): Promise<void> {}

  private execute(sql: string): void {
    const result = spawnSqlite(this.databasePath, [], sql);
    if (result.status !== 0) throw new Error(result.stderr);
  }
}

test("缺失快照返回 null；保存后整份读回且跨重开一致", async () => {
  await withDatabase(async (connection) => {
    const repository = new PosSettingsSyncRepository(connection, NOW);
    assert.equal(await repository.load(), null);
    await repository.save(state);
    assert.deepEqual(await repository.load(), state);
    assert.deepEqual(
      await new PosSettingsSyncRepository(
        new SystemSqliteConnection(connection.databasePath),
        NOW,
      ).load(),
      state,
    );
    // 第二次保存是整份替换，不是合并。
    const replaced: PosSettingsLocalState = { ...state, hash: "def456", values: {}, overrides: {} };
    await repository.save(replaced);
    assert.deepEqual(await repository.load(), replaced);
  });
});

test("损坏或形状不合规的快照读回 null，写入不合规快照被拒绝且不污染已有值", async () => {
  await withDatabase(async (connection) => {
    const repository = new PosSettingsSyncRepository(connection, NOW);
    await repository.save(state);
    await assert.rejects(
      () => repository.save({ ...state, offlineOverrideCount: -1 } as PosSettingsLocalState),
      /Invalid POS settings sync state/,
    );
    await assert.rejects(
      () => repository.save({ ...state, extra: 1 } as unknown as PosSettingsLocalState),
      /Invalid POS settings sync state/,
    );
    assert.deepEqual(await repository.load(), state);

    for (const broken of [
      "{not json",
      JSON.stringify([]),
      JSON.stringify({ ...state, values: { k: { value: "yes", source: "x" } } }),
      JSON.stringify({ ...state, overrides: { k: false } }),
      JSON.stringify({ ...state, syncedAtIso: "not-a-date" }),
      JSON.stringify({ ...state, hash: 5 }),
    ]) {
      await connection.run(
        "UPDATE app_settings SET setting_value = ? WHERE setting_key = 'pos_settings_sync_v1'",
        [broken],
      );
      assert.equal(await repository.load(), null, broken);
    }
  });
});

test("normalizePosSettingsLocalState：空快照合法，缺键或多键不合法", () => {
  const empty = {
    storeCode: "", hash: "", values: {}, syncedAtIso: null,
    overrides: {}, offlineOverrideCount: 0, ackedHash: "", ackedOverrideCount: 0,
  };
  assert.deepEqual(normalizePosSettingsLocalState(empty), empty);
  const { ackedHash: _omit, ...missing } = empty;
  assert.equal(normalizePosSettingsLocalState(missing), null);
  assert.equal(normalizePosSettingsLocalState({ ...empty, x: 1 }), null);
  assert.equal(normalizePosSettingsLocalState(null), null);
});

test("cashMovement 本机值：默认关，只有字面量 true 算开，false 可写回", async () => {
  await withDatabase(async (connection) => {
    const repository = new PosSettingsSyncRepository(connection, NOW);
    assert.equal(await repository.loadCashMovementEnabled(), false);
    await repository.saveCashMovementEnabled(true);
    assert.equal(await repository.loadCashMovementEnabled(), true);
    const row = await connection.getFirst<{ setting_value: string }>(
      "SELECT setting_value FROM app_settings WHERE setting_key = 'pos_cash_movement_enabled_v1'",
    );
    assert.equal(row?.setting_value, "true");
    await repository.saveCashMovementEnabled(false);
    assert.equal(await repository.loadCashMovementEnabled(), false);

    for (const dirty of ['"true"', "1", "{not json", "null", '{"enabled":true}']) {
      await connection.run(
        "UPDATE app_settings SET setting_value = ? WHERE setting_key = 'pos_cash_movement_enabled_v1'",
        [dirty],
      );
      assert.equal(await repository.loadCashMovementEnabled(), false, dirty);
    }
  });
});

test("两个键互不影响，也不碰付款方式设置键", async () => {
  await withDatabase(async (connection) => {
    await connection.run(
      "INSERT INTO app_settings (setting_key, setting_value, updated_at_iso) VALUES ('payment_methods_v1', ?, ?)",
      ['{"useManualCard":true,"giftCardEnabled":false}', NOW()],
    );
    const repository = new PosSettingsSyncRepository(connection, NOW);
    await repository.save(state);
    await repository.saveCashMovementEnabled(true);
    const row = await connection.getFirst<{ setting_value: string }>(
      "SELECT setting_value FROM app_settings WHERE setting_key = 'payment_methods_v1'",
    );
    assert.equal(row?.setting_value, '{"useManualCard":true,"giftCardEnabled":false}');
    assert.deepEqual(await repository.load(), state);
  });
});

async function withDatabase(
  operation: (connection: SystemSqliteConnection) => Promise<void>,
): Promise<void> {
  const folder = mkdtempSync(join(tmpdir(), "hb-pos-settings-sync-"));
  const path = join(folder, "settings-sync.db");
  try {
    const connection = new SystemSqliteConnection(path);
    await connection.exec(POS_DATABASE_MIGRATIONS.map((migration) => migration.sql).join("\n"));
    await operation(connection);
  } finally {
    rmSync(folder, { recursive: true, force: true });
  }
}

function spawnSqlite(
  databasePath: string,
  arguments_: readonly string[],
  input: string,
): Readonly<{ status: number | null; stdout: string; stderr: string }> {
  const result = spawnSync(
    process.env.SQLITE3_BINARY ?? "sqlite3",
    [...arguments_, databasePath],
    { input, encoding: "utf8" },
  );
  if (result.error) throw result.error;
  return { status: result.status, stdout: result.stdout, stderr: result.stderr };
}

function bind(sql: string, parameters: readonly SqlValue[]): string {
  let index = 0;
  return sql.replace(/\?/g, () => sqliteLiteral(parameter(parameters, index++)));
}

function parameter(parameters: readonly SqlValue[], index: number): SqlValue {
  const value = parameters[index];
  if (value === undefined) throw new Error("Missing SQLite parameter.");
  return value;
}

function sqliteLiteral(value: SqlValue): string {
  if (value === null) return "NULL";
  if (typeof value === "number") return String(value);
  if (value instanceof Uint8Array) return `X'${Buffer.from(value).toString("hex")}'`;
  return `'${value.replace(/'/g, "''")}'`;
}
