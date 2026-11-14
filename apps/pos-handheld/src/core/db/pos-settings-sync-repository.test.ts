import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { mkdtempSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";

import type { PosSettingsLocalState } from "@hb/pos-sync/features/pos-settings/pos-settings-service";
import type { SqliteConnectionPort, SqlRunResult, SqlValue } from "@hb/pos-db/core/db/types";

import { POS_DATABASE_MIGRATIONS } from "./migrations";
import {
  parsePosSettingsLocalState,
  POS_CASH_MOVEMENT_ENABLED_KEY,
  POS_SETTINGS_SYNC_KEY,
  PosSettingsSyncRepository,
} from "./pos-settings-sync-repository";

class SystemSqliteConnection implements SqliteConnectionPort {
  private tail: Promise<void> = Promise.resolve();

  public constructor(public readonly databasePath: string) {}

  public async exec(sql: string): Promise<void> { this.execute(sql); }
  public async run(sql: string, parameters: readonly SqlValue[] = []): Promise<SqlRunResult> {
    const lines = this.execute(`${bind(sql, parameters)}; SELECT changes() AS changes;`).trim().split("\n");
    return { changes: Number(lines.at(-1)), lastInsertRowId: 0 };
  }
  public async getFirst<T extends object>(sql: string, parameters: readonly SqlValue[] = []): Promise<T | null> {
    const result = spawnSqlite(this.databasePath, ["-json"], bind(sql, parameters));
    if (result.status !== 0) throw new Error(result.stderr);
    const rows = result.stdout.trim() ? JSON.parse(result.stdout) as readonly T[] : [];
    return rows[0] ?? null;
  }
  public async getAll<T extends object>(): Promise<readonly T[]> { return []; }
  public async withExclusiveTransaction<T>(operation: (transaction: SqliteConnectionPort) => Promise<T>): Promise<T> {
    const previous = this.tail;
    let release!: () => void;
    this.tail = new Promise<void>((resolve) => { release = resolve; });
    await previous;
    try { return await operation(this); } finally { release(); }
  }
  public async close(): Promise<void> {}

  private execute(sql: string): string {
    const result = spawnSqlite(this.databasePath, [], sql);
    if (result.status !== 0) throw new Error(result.stderr);
    return result.stdout;
  }
}

function state(overrides: Partial<PosSettingsLocalState> = {}): PosSettingsLocalState {
  return {
    storeCode: "BNE-01",
    hash: "abcdef1234567890",
    values: {
      "payment.manualCard": { value: true, source: "store" },
      "cashMovement.enabled": { value: false, source: "global" },
    },
    syncedAtIso: "2026-11-14T01:02:03.000Z",
    overrides: { "payment.manualCard": true },
    offlineOverrideCount: 2,
    ackedHash: "abcdef1234567890",
    ackedOverrideCount: 1,
    ...overrides,
  };
}

test("同步快照可保存并原样读回，再次保存整份替换", async () => {
  await withDatabase(async (connection) => {
    const repository = new PosSettingsSyncRepository(connection, () => "2026-11-14T00:00:00Z");
    assert.equal(await repository.load(), null);
    await repository.save(state());
    assert.deepEqual(await repository.load(), state());
    // 第二次保存必须整份替换：旧的 values / overrides 不能残留。
    await repository.save(state({ hash: "ffff", values: {}, overrides: {}, offlineOverrideCount: 5 }));
    assert.deepEqual(
      await repository.load(),
      state({ hash: "ffff", values: {}, overrides: {}, offlineOverrideCount: 5 }),
    );
  });
});

test("快照损坏或形状不对时读取返回 null 而不是抛错", async () => {
  await withDatabase(async (connection) => {
    const repository = new PosSettingsSyncRepository(connection, () => "2026-11-14T00:00:00Z");
    for (const broken of [
      "{not json",
      "null",
      "[]",
      JSON.stringify({ ...state(), hash: 3 }),
      JSON.stringify({ ...state(), offlineOverrideCount: -1 }),
      JSON.stringify({ ...state(), offlineOverrideCount: 1.5 }),
      JSON.stringify({ ...state(), values: { "payment.manualCard": { value: "yes", source: "store" } } }),
      JSON.stringify({ ...state(), values: { "payment.manualCard": { value: true, source: "" } } }),
      JSON.stringify({ ...state(), overrides: { "payment.manualCard": false } }),
      JSON.stringify({ ...state(), syncedAtIso: 123 }),
    ]) {
      await connection.run(
        `INSERT INTO app_settings (setting_key, setting_value, updated_at_iso) VALUES (?, ?, ?)
         ON CONFLICT(setting_key) DO UPDATE SET setting_value = excluded.setting_value`,
        [POS_SETTINGS_SYNC_KEY, broken, "2026-11-14T00:00:00Z"],
      );
      assert.equal(await repository.load(), null, broken);
    }
  });
});

test("保存前校验形状，非法状态拒绝写入且不破坏已有快照", async () => {
  await withDatabase(async (connection) => {
    const repository = new PosSettingsSyncRepository(connection, () => "2026-11-14T00:00:00Z");
    await repository.save(state());
    await assert.rejects(() => repository.save({ ...state(), hash: 7 as unknown as string }));
    await assert.rejects(() =>
      repository.save({ ...state(), values: { k: { value: 1 as unknown as boolean, source: "x" } } }),
    );
    assert.deepEqual(await repository.load(), state());
  });
});

test("落盘快照丢弃未知字段，不接受额外内容", () => {
  const parsed = parsePosSettingsLocalState({ ...state(), cardNumber: "4111", extra: { a: 1 } });
  assert.ok(parsed);
  assert.equal("cardNumber" in parsed, false);
  assert.equal("extra" in parsed, false);
});

test("cashMovement 本机值默认关闭，只有字面量 true 算开", async () => {
  await withDatabase(async (connection) => {
    const repository = new PosSettingsSyncRepository(connection, () => "2026-11-14T00:00:00Z");
    assert.equal(await repository.readCashMovementEnabled(), false);
    await repository.writeCashMovementEnabled(true);
    assert.equal(await repository.readCashMovementEnabled(), true);
    await repository.writeCashMovementEnabled(false);
    assert.equal(await repository.readCashMovementEnabled(), false);
    for (const raw of ['"true"', "1", "{}", "yes", "null", "[true]"]) {
      await connection.run(
        `INSERT INTO app_settings (setting_key, setting_value, updated_at_iso) VALUES (?, ?, ?)
         ON CONFLICT(setting_key) DO UPDATE SET setting_value = excluded.setting_value`,
        [POS_CASH_MOVEMENT_ENABLED_KEY, raw, "2026-11-14T00:00:00Z"],
      );
      assert.equal(await repository.readCashMovementEnabled(), false, raw);
    }
    await assert.rejects(() => repository.writeCashMovementEnabled("true" as unknown as boolean));
  });
});

test("两个键互不影响，也不触碰其他 app_settings 键", async () => {
  await withDatabase(async (connection) => {
    await connection.run(
      "INSERT INTO app_settings (setting_key, setting_value, updated_at_iso) VALUES (?, ?, ?)",
      ["payment_methods_v1", '{"useManualCard":true}', "2026-11-14T00:00:00Z"],
    );
    const repository = new PosSettingsSyncRepository(connection, () => "2026-11-14T00:00:00Z");
    await repository.save(state());
    await repository.writeCashMovementEnabled(true);
    const other = await connection.getFirst<{ setting_value: string }>(
      "SELECT setting_value FROM app_settings WHERE setting_key = ?",
      ["payment_methods_v1"],
    );
    assert.equal(other?.setting_value, '{"useManualCard":true}');
    assert.deepEqual(await repository.load(), state());
  });
});

async function withDatabase(operation: (connection: SystemSqliteConnection) => Promise<void>): Promise<void> {
  const folder = mkdtempSync(join(tmpdir(), "hb-pos-settings-sync-"));
  const path = join(folder, "settings.db");
  try {
    const connection = new SystemSqliteConnection(path);
    await connection.exec(POS_DATABASE_MIGRATIONS.map((migration) => migration.sql).join("\n"));
    await operation(connection);
  } finally {
    rmSync(folder, { recursive: true, force: true });
  }
}

function spawnSqlite(databasePath: string, arguments_: readonly string[], input: string): Readonly<{ status: number | null; stdout: string; stderr: string }> {
  const result = spawnSync(process.env.SQLITE3_BINARY ?? "sqlite3", [...arguments_, databasePath], { input, encoding: "utf8" });
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
