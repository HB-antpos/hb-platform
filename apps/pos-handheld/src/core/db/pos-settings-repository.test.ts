import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { mkdtempSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";

import { POS_DATABASE_MIGRATIONS } from "./migrations";
import {
  DEFAULT_RECEIPT_PRINTER_SETTINGS,
  PosSettingsRepository,
  ReceiptProfileRejectedError,
  type ReceiptPrinterSettings,
  type ReceiptProfileApplyInput,
} from "./pos-settings-repository";
import type { SqliteConnectionPort, SqlRunResult, SqlValue } from "@hb/pos-db/core/db/types";

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

function settings(overrides: Partial<ReceiptPrinterSettings> = {}): ReceiptPrinterSettings {
  return {
    printEnabled: true,
    drawerEnabled: true,
    peripheralId: "XP-N160I",
    paper: "80mm",
    locale: "zh-CN",
    brandName: "HB POS",
    storeName: "Hot Bargain",
    address: "1 Main Street",
    phone: "07 1234 5678",
    abn: "12 345 678 901",
    returnPolicy: "Change of mind returns within 14 days.",
    profileStoreCode: "BNE-01",
    profileVersion: 0,
    profileAckedVersion: 0,
    ...overrides,
  };
}

test("旧 receipt_printer_v1 自动补新资料字段且不清空打印/钱箱/外设", async () => {
  await withDatabase(async (connection) => {
    const legacy = {
      printEnabled: true,
      drawerEnabled: true,
      peripheralId: "XP-N160I",
      paper: "58mm",
      locale: "zh-CN",
      brandName: "Hot Bargain",
      storeName: "Legacy Store",
      address: "1 Old St",
      phone: "0411 111 111",
      abn: "99 999 999 999",
    };
    await connection.run(
      "INSERT INTO app_settings (setting_key, setting_value, updated_at_iso) VALUES (?, ?, ?)",
      ["receipt_printer_v1", JSON.stringify(legacy), "2026-07-28T00:00:00.000Z"],
    );
    const repository = new PosSettingsRepository(connection, () => "2026-07-28T00:01:00.000Z");
    const current = await repository.getReceiptPrinterSettings();
    assert.equal(current.printEnabled, true);
    assert.equal(current.drawerEnabled, true);
    assert.equal(current.peripheralId, "XP-N160I");
    assert.equal(current.paper, "58mm");
    assert.equal(current.returnPolicy, "");
    assert.equal(current.profileStoreCode, "");
  });
});

test("returnPolicy 允许 CR/LF/TAB，其余控制字符拒绝；profileStoreCode 拒绝控制字符", async () => {
  await withDatabase(async (connection) => {
    const repository = new PosSettingsRepository(connection, () => "2026-07-28T00:00:00.000Z");
    const saved = await repository.saveReceiptPrinterSettings(
      settings({ returnPolicy: "Line 1\r\nLine 2\tTabbed" }),
    );
    assert.equal(saved.returnPolicy, "Line 1\r\nLine 2\tTabbed");
    assert.deepEqual(await repository.getReceiptPrinterSettings(), saved);

    await assert.rejects(
      () => repository.saveReceiptPrinterSettings(settings({ returnPolicy: "Bad\u0007policy" })),
      /returnPolicy is invalid/,
    );
    await assert.rejects(
      () => repository.saveReceiptPrinterSettings(settings({ returnPolicy: "Bad\u007fpolicy" })),
      /returnPolicy is invalid/,
    );
    await assert.rejects(
      () => repository.saveReceiptPrinterSettings(settings({ profileStoreCode: "BNE\u001b01" })),
      /profileStoreCode is invalid/,
    );
  });
});

test("真实 SQLite：首次读取使用禁用打印和钱箱的默认值，完整配置跨重开保留 updated_at", async () => {
  await withDatabase(async (connection) => {
    const repository = new PosSettingsRepository(connection, () => "2026-07-28T00:00:00.000Z");
    assert.deepEqual(await repository.getReceiptPrinterSettings(), DEFAULT_RECEIPT_PRINTER_SETTINGS);
    const expected = settings();
    assert.deepEqual(await repository.saveReceiptPrinterSettings(expected), expected);

    const reopened = new PosSettingsRepository(new SystemSqliteConnection(connection.databasePath), () => "2026-07-28T00:01:00.000Z");
    assert.deepEqual(await reopened.getReceiptPrinterSettings(), expected);
    const row = await connection.getFirst<{ updated_at_iso: string; setting_value: string }>("SELECT updated_at_iso, setting_value FROM app_settings WHERE setting_key = 'receipt_printer_v1'");
    assert.equal(row?.updated_at_iso, "2026-07-28T00:00:00.000Z");
    assert.deepEqual(JSON.parse(row?.setting_value ?? "{}"), onDisk(expected));
  });
});

test("损坏或敏感 JSON fail-closed 为禁用默认值，非法写入不覆盖原有效配置", async () => {
  await withDatabase(async (connection) => {
    const repository = new PosSettingsRepository(connection, () => "2026-07-28T00:00:00.000Z");
    await repository.saveReceiptPrinterSettings(settings());
    await assert.rejects(
      () => repository.saveReceiptPrinterSettings({ ...settings(), drawerEnabled: true, peripheralId: null }),
      /Drawer can only be enabled/,
    );
    await assert.rejects(
      () => repository.saveReceiptPrinterSettings({ ...settings(), paper: "57mm" as "80mm" }),
      /paper is invalid/,
    );
    await assert.rejects(
      () => repository.saveReceiptPrinterSettings({ ...settings(), authorizationToken: "forbidden" } as unknown as ReceiptPrinterSettings),
      /unsupported or sensitive/,
    );
    assert.deepEqual(await repository.getReceiptPrinterSettings(), settings());

    await connection.run("UPDATE app_settings SET setting_value = ? WHERE setting_key = 'receipt_printer_v1'", ["{not json"]);
    assert.deepEqual(await repository.getReceiptPrinterSettings(), DEFAULT_RECEIPT_PRINTER_SETTINGS);
    await connection.run("UPDATE app_settings SET setting_value = ? WHERE setting_key = 'receipt_printer_v1'", [JSON.stringify({ ...settings(), cardReference: "forbidden" })]);
    assert.deepEqual(await repository.getReceiptPrinterSettings(), DEFAULT_RECEIPT_PRINTER_SETTINGS);
  });
});

test("并发保存以最后一次完整对象原子替换，禁止字段拼接", async () => {
  await withDatabase(async (connection) => {
    let now = 0;
    const repository = new PosSettingsRepository(connection, () => `2026-07-28T00:00:0${++now}.000Z`);
    const first = settings({ brandName: "First", paper: "58mm", locale: "en", drawerEnabled: false, peripheralId: null });
    const last = settings({ brandName: "Last", storeName: "Last store", phone: "0400 000 000" });
    await Promise.all([
      repository.saveReceiptPrinterSettings(first),
      repository.saveReceiptPrinterSettings(last),
    ]);

    assert.deepEqual(await repository.getReceiptPrinterSettings(), last);
    const row = await connection.getFirst<{ setting_value: string }>("SELECT setting_value FROM app_settings WHERE setting_key = 'receipt_printer_v1'");
    assert.deepEqual(JSON.parse(row?.setting_value ?? "{}"), onDisk(last));
  });
});

function profile(overrides: Partial<ReceiptProfileApplyInput> = {}): ReceiptProfileApplyInput {
  return {
    version: 3,
    storeCode: "BNE-01",
    storeName: "  Hot Bargain Bankstown ",
    brandName: " Hot Bargain ",
    address: "1 Main Street\r\nBankstown NSW",
    phone: " 02 1234 5678 ",
    abn: " 12 345 678 901 ",
    returnPolicy: "Returns within 14 days.\tSee store.",
    ...overrides,
  };
}

/** 落盘形状：下发版本为 0 时两个版本字段不落盘（与旧版格式一致）。 */
function onDisk(value: ReceiptPrinterSettings): Record<string, unknown> {
  const { profileVersion, profileAckedVersion, ...rest } = value;
  return profileVersion > 0 ? { ...rest, profileVersion, profileAckedVersion } : { ...rest };
}

test("未下发时落盘 JSON 不含版本字段（与旧版格式一致，OTA 回滚后旧代码仍能读）；应用下发后才写入两个版本字段", async () => {
  await withDatabase(async (connection) => {
    const repository = new PosSettingsRepository(connection, () => "2026-10-07T00:00:00.000Z");
    const knownByLegacy = [
      "printEnabled", "drawerEnabled", "peripheralId", "paper", "locale",
      "brandName", "storeName", "address", "phone", "abn", "returnPolicy", "profileStoreCode",
    ].sort();

    await repository.saveReceiptPrinterSettings(settings());
    assert.deepEqual(Object.keys(await persistedJson(connection)).sort(), knownByLegacy);
    await repository.saveReceiptPrinterSettingsPreservingProfile(settings({ paper: "58mm" }));
    assert.deepEqual(Object.keys(await persistedJson(connection)).sort(), knownByLegacy);

    await repository.applyReceiptProfile(profile({ version: 2 }));
    assert.deepEqual(
      Object.keys(await persistedJson(connection)).sort(),
      [...knownByLegacy, "profileAckedVersion", "profileVersion"].sort(),
    );

    // 换店清空（整体覆盖为版本 0）后回到旧格式
    await repository.saveReceiptPrinterSettings(settings());
    assert.deepEqual(Object.keys(await persistedJson(connection)).sort(), knownByLegacy);
  });
});

async function persistedJson(connection: SystemSqliteConnection): Promise<Record<string, unknown>> {
  const row = await connection.getFirst<{ setting_value: string }>(
    "SELECT setting_value FROM app_settings WHERE setting_key = 'receipt_printer_v1'",
  );
  return JSON.parse(row?.setting_value ?? "{}") as Record<string, unknown>;
}

test("旧数据缺少下发版本字段时按 0 读取；脏版本号（小数/负数/字符串/null）回落 0 且不重置打印与钱箱设置", async () => {
  await withDatabase(async (connection) => {
    const repository = new PosSettingsRepository(connection, () => "2026-10-07T00:00:00.000Z");
    const { profileVersion: _v, profileAckedVersion: _a, ...legacy } = settings();
    void _v;
    void _a;
    await connection.run(
      "INSERT INTO app_settings (setting_key, setting_value, updated_at_iso) VALUES (?, ?, ?)",
      ["receipt_printer_v1", JSON.stringify(legacy), "2026-10-07T00:00:00.000Z"],
    );
    const migrated = await repository.getReceiptPrinterSettings();
    assert.equal(migrated.profileVersion, 0);
    assert.equal(migrated.profileAckedVersion, 0);
    assert.equal(migrated.printEnabled, true);

    for (const dirty of [1.5, -1, "3", null, 2 ** 60, {}]) {
      await connection.run(
        "UPDATE app_settings SET setting_value = ? WHERE setting_key = 'receipt_printer_v1'",
        [JSON.stringify({ ...legacy, profileVersion: dirty, profileAckedVersion: dirty })],
      );
      const current = await repository.getReceiptPrinterSettings();
      assert.equal(current.profileVersion, 0, `脏值 ${JSON.stringify(dirty)} 回落 0`);
      assert.equal(current.profileAckedVersion, 0);
      // 打印/钱箱/外设不能因为版本号脏值而被重置为默认值
      assert.equal(current.printEnabled, true);
      assert.equal(current.drawerEnabled, true);
      assert.equal(current.peripheralId, "XP-N160I");
    }

    // 合法版本号正常读取
    await repository.saveReceiptPrinterSettings(settings({ profileVersion: 4, profileAckedVersion: 2 }));
    const valid = await repository.getReceiptPrinterSettings();
    assert.equal(valid.profileVersion, 4);
    assert.equal(valid.profileAckedVersion, 2);
  });
});

test("applyReceiptProfile 一次性原子写入六项资料 + 版本 + 绑定门店，保留硬件设置，单行字段去首尾空白", async () => {
  await withDatabase(async (connection) => {
    const repository = new PosSettingsRepository(connection, () => "2026-10-07T01:00:00.000Z");
    await repository.saveReceiptPrinterSettings(settings({ brandName: "Manual", storeName: "Manual store", profileStoreCode: "" }));

    const applied = await repository.applyReceiptProfile(profile());

    const stored = await persistedJson(connection);
    assert.deepEqual(stored, {
      ...settings(),
      brandName: "Hot Bargain",
      storeName: "Hot Bargain Bankstown",
      address: "1 Main Street\r\nBankstown NSW",
      phone: "02 1234 5678",
      abn: "12 345 678 901",
      returnPolicy: "Returns within 14 days.\tSee store.",
      profileStoreCode: "BNE-01",
      profileVersion: 3,
      profileAckedVersion: 0,
    });
    assert.deepEqual(applied, stored);
    // 硬件设置原样保留
    assert.equal(stored.printEnabled, true);
    assert.equal(stored.drawerEnabled, true);
    assert.equal(stored.peripheralId, "XP-N160I");
    assert.equal(stored.paper, "80mm");
  });
});

test("applyReceiptProfile 新版本尚未回执：已回执版本不超过 version-1；服务端回退版本号时也能重新回执", async () => {
  await withDatabase(async (connection) => {
    const repository = new PosSettingsRepository(connection, () => "2026-10-07T01:00:00.000Z");
    await repository.saveReceiptPrinterSettings(settings({ profileVersion: 5, profileAckedVersion: 5 }));

    assert.equal((await repository.applyReceiptProfile(profile({ version: 6 }))).profileAckedVersion, 5);
    assert.equal((await repository.applyReceiptProfile(profile({ version: 3 }))).profileAckedVersion, 2);
    assert.equal((await repository.getReceiptPrinterSettings()).profileVersion, 3);
  });
});

test("applyReceiptProfile 任何一项不合规都抛 ReceiptProfileRejectedError 且不写入任何内容", async () => {
  await withDatabase(async (connection) => {
    const repository = new PosSettingsRepository(connection, () => "2026-10-07T01:00:00.000Z");
    const before = settings({ profileVersion: 2, profileAckedVersion: 2 });
    await repository.saveReceiptPrinterSettings(before);

    const invalid: ReadonlyArray<readonly [string, ReceiptProfileApplyInput]> = [
      ["门店名为空", profile({ storeName: "   " })],
      ["门店名含控制字符", profile({ storeName: "Bad\u0007Name" })],
      ["品牌超长", profile({ brandName: "x".repeat(121) })],
      ["地址超过 240", profile({ address: "x".repeat(241) })],
      ["电话超过 60", profile({ phone: "1".repeat(61) })],
      ["ABN 超过 32", profile({ abn: "1".repeat(33) })],
      ["退货政策超过 500", profile({ returnPolicy: "x".repeat(501) })],
      ["地址含 C1 控制字符", profile({ address: "ok\u0085bad" })],
      ["退货政策含 ESC", profile({ returnPolicy: "a\u001bb" })],
      ["门店代码含控制字符", profile({ storeCode: "BNE\u001b01" })],
      ["版本为 0", profile({ version: 0 })],
      ["版本为小数", profile({ version: 1.5 })],
      ["版本为负数", profile({ version: -3 })],
    ];
    for (const [name, input] of invalid) {
      await assert.rejects(
        () => repository.applyReceiptProfile(input),
        (error: unknown) => error instanceof ReceiptProfileRejectedError,
        name,
      );
      assert.deepEqual(await persistedJson(connection), before, `${name}：落盘内容必须原样不变`);
    }
  });
});

test("markReceiptProfileAcked 只在本机当前版本仍等于该版本时写入（比较并设置）", async () => {
  await withDatabase(async (connection) => {
    const repository = new PosSettingsRepository(connection, () => "2026-10-07T01:00:00.000Z");
    // 没有任何落盘设置：不写入
    assert.equal(await repository.markReceiptProfileAcked(1), false);
    assert.equal(await connection.getFirst("SELECT 1 FROM app_settings WHERE setting_key = 'receipt_printer_v1'"), null);

    await repository.applyReceiptProfile(profile({ version: 4 }));
    // 回执在途期间本机已应用更新的版本：旧版本的回执不能记到新版本头上
    await repository.applyReceiptProfile(profile({ version: 5 }));
    assert.equal(await repository.markReceiptProfileAcked(4), false);
    assert.equal((await repository.getReceiptPrinterSettings()).profileAckedVersion, 0);

    assert.equal(await repository.markReceiptProfileAcked(5), true);
    assert.equal((await repository.getReceiptPrinterSettings()).profileAckedVersion, 5);
    // 已记录则不再写
    assert.equal(await repository.markReceiptProfileAcked(5), false);
    assert.equal(await repository.markReceiptProfileAcked(0), false);
  });
});

test("用户保存（保留下发资料）：下发版本永远以已落盘值为准；未下发时资料取用户输入，已下发时资料也取已落盘值", async () => {
  await withDatabase(async (connection) => {
    const repository = new PosSettingsRepository(connection, () => "2026-10-07T01:00:00.000Z");

    // 未下发：用户手工资料照常保存；草稿里夹带的版本号被忽略（不能凭空变成「已下发」）
    const manual = await repository.saveReceiptPrinterSettingsPreservingProfile(
      settings({ storeName: "Manual store", profileVersion: 9, profileAckedVersion: 9 }),
    );
    assert.equal(manual.storeName, "Manual store");
    assert.equal(manual.profileVersion, 0);
    assert.equal(manual.profileAckedVersion, 0);

    // 后台同步写入下发资料后，设置页里过期的草稿再保存：资料与版本不被盖回旧值，硬件改动照常生效
    await repository.applyReceiptProfile(profile({ version: 7 }));
    const staleDraft = settings({
      storeName: "Manual store",
      brandName: "Manual brand",
      paper: "58mm",
      locale: "zh-CN",
      peripheralId: "NEW-PRINTER",
      profileVersion: 0,
      profileAckedVersion: 0,
    });
    const saved = await repository.saveReceiptPrinterSettingsPreservingProfile(staleDraft);

    assert.equal(saved.storeName, "Hot Bargain Bankstown");
    assert.equal(saved.brandName, "Hot Bargain");
    assert.equal(saved.profileVersion, 7);
    assert.equal(saved.paper, "58mm");
    assert.equal(saved.peripheralId, "NEW-PRINTER");
    assert.deepEqual(await persistedJson(connection), saved);
  });
});

test("saveReceiptPrinterSettings 仍是整体覆盖：换店清空路径可以把版本重置为 0", async () => {
  await withDatabase(async (connection) => {
    const repository = new PosSettingsRepository(connection, () => "2026-10-07T01:00:00.000Z");
    await repository.applyReceiptProfile(profile({ version: 7 }));
    const cleared = await repository.saveReceiptPrinterSettings(
      settings({ brandName: "", storeName: "", address: "", phone: "", abn: "", returnPolicy: "", profileVersion: 0, profileAckedVersion: 0 }),
    );
    assert.equal(cleared.profileVersion, 0);
    assert.equal((await repository.getReceiptPrinterSettings()).profileVersion, 0);
  });
});

test("apply 与用户保存并发：各自在独占事务内读-改-写，最终落盘不丢硬件改动也没有半截资料", async () => {
  await withDatabase(async (connection) => {
    let now = 0;
    const repository = new PosSettingsRepository(connection, () => `2026-10-07T00:00:0${++now}.000Z`);
    await repository.saveReceiptPrinterSettings(settings({ profileVersion: 0, profileAckedVersion: 0 }));

    await Promise.all([
      repository.applyReceiptProfile(profile({ version: 2 })),
      repository.saveReceiptPrinterSettingsPreservingProfile(settings({ paper: "58mm", storeName: "Manual store" })),
      repository.applyReceiptProfile(profile({ version: 3, storeName: "Version three" })),
    ]);

    const final = await repository.getReceiptPrinterSettings();
    assert.equal(final.profileVersion, 3);
    assert.equal(final.storeName, "Version three");
    assert.equal(final.paper, "58mm", "并发的硬件保存不能丢失");
    assert.deepEqual(await persistedJson(connection), final);
  });
});

async function withDatabase(operation: (connection: SystemSqliteConnection) => Promise<void>): Promise<void> {
  const folder = mkdtempSync(join(tmpdir(), "hb-pos-settings-"));
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
