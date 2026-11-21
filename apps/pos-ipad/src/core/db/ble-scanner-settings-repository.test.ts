import assert from "node:assert/strict";
import test from "node:test";

import type {
  SqliteConnectionPort,
  SqlRunResult,
  SqlValue,
} from "@hb/pos-db/core/db/types";

import type { SavedBleScanner } from "../peripherals/scanner/ble/ble-scanner-core";

import { BleScannerSettingsRepository } from "./ble-scanner-settings-repository";

/** 只模拟 app_settings 单表的键值读写，验证 SQL 只碰 ble_scanner_v1 一个键。 */
class FakeAppSettings implements SqliteConnectionPort {
  public readonly rows = new Map<string, { value: string; updatedAt: string }>();
  public readonly statements: string[] = [];

  public async exec(): Promise<void> {}

  public async run(sql: string, parameters: readonly SqlValue[] = []): Promise<SqlRunResult> {
    this.statements.push(sql.replace(/\s+/g, " ").trim());
    if (sql.includes("INSERT INTO app_settings")) {
      const [key, value, updatedAt] = parameters as string[];
      this.rows.set(key as string, { value: value as string, updatedAt: updatedAt as string });
      return { changes: 1, lastInsertRowId: 0 };
    }
    if (sql.startsWith("DELETE FROM app_settings WHERE setting_key = ?")) {
      const deleted = this.rows.delete(parameters[0] as string);
      return { changes: deleted ? 1 : 0, lastInsertRowId: 0 };
    }
    throw new Error(`unexpected sql: ${sql}`);
  }

  public async getFirst<T extends object>(
    _sql: string,
    parameters: readonly SqlValue[] = [],
  ): Promise<T | null> {
    const row = this.rows.get(parameters[0] as string);
    return row ? ({ setting_value: row.value } as T) : null;
  }

  public async getAll<T extends object>(): Promise<readonly T[]> {
    return [];
  }

  public async withExclusiveTransaction<T>(
    operation: (transaction: SqliteConnectionPort) => Promise<T>,
  ): Promise<T> {
    return operation(this);
  }

  public async close(): Promise<void> {}
}

const SAVED: SavedBleScanner = Object.freeze({
  version: 1,
  peripheralId: "D2DA1459-BCE1-0B6C-B7A7-8DE157AAD5CF",
  name: "BarCode Scanner BLE",
  alias: "1 号背夹",
  profileId: "yichip-feea",
  serviceUuid: "FEEA",
  notifyUuid: "2AA1",
  autoReconnect: true,
  savedAt: "2026-11-21T00:00:00.000Z",
});

test("保存后读回，UUID 规范成 128 位", async () => {
  const db = new FakeAppSettings();
  const repository = new BleScannerSettingsRepository(db, () => "2026-11-21T01:00:00.000Z");
  assert.equal(await repository.load(), null);
  await repository.save(SAVED);
  assert.equal(db.rows.get("ble_scanner_v1")?.updatedAt, "2026-11-21T01:00:00.000Z");
  const loaded = await repository.load();
  assert.equal(loaded?.peripheralId, SAVED.peripheralId);
  assert.equal(loaded?.alias, "1 号背夹");
  assert.equal(loaded?.serviceUuid, "0000FEEA-0000-1000-8000-00805F9B34FB");
  assert.equal(loaded?.notifyUuid, "00002AA1-0000-1000-8000-00805F9B34FB");
});

test("损坏或含未知结构的存储一律当未配对", async () => {
  const db = new FakeAppSettings();
  const repository = new BleScannerSettingsRepository(db, () => "x");
  db.rows.set("ble_scanner_v1", { value: "{not json", updatedAt: "x" });
  assert.equal(await repository.load(), null);
  db.rows.set("ble_scanner_v1", {
    value: JSON.stringify({ ...SAVED, version: 2 }),
    updatedAt: "x",
  });
  assert.equal(await repository.load(), null);
});

test("不合规的配置拒绝落盘", async () => {
  const db = new FakeAppSettings();
  const repository = new BleScannerSettingsRepository(db, () => "x");
  await assert.rejects(() => repository.save({ ...SAVED, serviceUuid: "" }));
  assert.equal(db.rows.size, 0);
});

test("忘记扫码枪只删除本键", async () => {
  const db = new FakeAppSettings();
  db.rows.set("receipt_printer_v1", { value: "{}", updatedAt: "x" });
  const repository = new BleScannerSettingsRepository(db, () => "x");
  await repository.save(SAVED);
  await repository.save(null);
  assert.equal(await repository.load(), null);
  assert.equal(db.rows.has("receipt_printer_v1"), true, "其他设置不得被删除");
  assert.ok(
    db.statements.every((sql) => !sql.startsWith("DELETE") || sql.endsWith("WHERE setting_key = ?")),
    "删除语句必须限定键",
  );
});
