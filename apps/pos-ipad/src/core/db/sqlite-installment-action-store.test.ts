import assert from "node:assert/strict";
import { mkdtemp, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { DatabaseSync, type SQLInputValue } from "node:sqlite";
import test from "node:test";

import type { InstallmentSnapshot } from "@hb/pos-domain/core/contracts/installments";
import type {
  PersistedInstallmentAction,
  PersistedInstallmentLifecycleAction,
} from "../runtime/production-installment-runtime";

import { applyMigrations, POS_DATABASE_MIGRATIONS } from "./migrations";
import { PosDatabase } from "./pos-database";
import { SqliteInstallmentActionStore } from "./sqlite-installment-action-store";
import {
  INSTALLMENT_SENSITIVE_PAYLOAD_REVISION,
  SqliteInstallmentSnapshotRepository,
} from "./sqlite-installment-snapshot-repository";
import type { SensitivePayloadEncryptor } from "./sqlite-repositories";
import type {
  SqliteConnectionPort,
  SqliteDriverPort,
  SqlRunResult,
  SqlValue,
} from "@hb/pos-db/core/db/types";

const NOW = "2026-07-29T00:00:00.000Z";
const STORE = "STORE-1";
const DEVICE = "IPAD-1";
const ACTION_A = "10000000-0000-4000-8000-000000000001";
const ACTION_B = "10000000-0000-4000-8000-000000000002";
const ACTION_C = "10000000-0000-4000-8000-000000000003";
const INSTALLMENT = "20000000-0000-4000-8000-000000000001";
const OTHER_INSTALLMENT = "20000000-0000-4000-8000-000000000002";
const PAYMENT = "30000000-0000-4000-8000-000000000001";
const LINE = "40000000-0000-4000-8000-000000000001";
const REFUND_PLAN_FINGERPRINT = `sha256:${"a".repeat(64)}`;
const LATER = "2026-07-29T01:00:00.000Z";

test("M20/M21 分段建立耐久 action ledger 与不可变 CAS triggers，失败不推进版本", async () => {
  await withDatabase(async (connection) => {
    const throughM19 = POS_DATABASE_MIGRATIONS.filter(
      (migration) => migration.version <= 19,
    );
    await applyMigrations(connection, () => NOW, throughM19);
    const m20 = POS_DATABASE_MIGRATIONS.find(
      (migration) => migration.version === 20,
    );
    const m21 = POS_DATABASE_MIGRATIONS.find(
      (migration) => migration.version === 21,
    );
    assert.ok(m20);
    assert.ok(m21);

    const throughM20 = POS_DATABASE_MIGRATIONS.filter(
      (migration) => migration.version <= 20,
    );
    const throughM21 = POS_DATABASE_MIGRATIONS.filter(
      (migration) => migration.version <= 21,
    );
    await applyMigrations(connection, () => NOW, throughM20);
    assert.equal(await schemaVersion(connection), 20);
    assert.equal(await tableExists(connection, "installment_actions"), true);
    assert.equal(
      await triggerExists(
        connection,
        "trg_installment_actions_state_transition",
      ),
      false,
    );

    await assert.rejects(
      () =>
        applyMigrations(connection, () => NOW, [
          ...throughM20,
          {
            ...m21,
            sql: `${m21.sql}\nCREATE TABL invalid_m21;`,
          },
        ]),
      /syntax|near/i,
    );
    assert.equal(await schemaVersion(connection), 20);
    assert.equal(
      await triggerExists(
        connection,
        "trg_installment_actions_state_transition",
      ),
      false,
    );

    await applyMigrations(connection, () => NOW, throughM21);
    assert.equal(await schemaVersion(connection), 21);
    assert.equal(
      await triggerExists(
        connection,
        "trg_installment_actions_state_transition",
      ),
      true,
    );
    assert.equal(
      await triggerExists(
        connection,
        "trg_installment_actions_no_delete",
      ),
      true,
    );
  });
});

test("真实 SQLite：createIfNone 原子保留 terminal 唯一 blocking，冻结 command 只存认证密文", async () => {
  await withMigratedDatabase(async (connection) => {
    const encryptor = new RecordingEncryptor();
    const store = new SqliteInstallmentActionStore(
      connection,
      encryptor,
      () => NOW,
    );
    const original = createCandidate();
    assert.deepEqual(await store.createIfNone(original), {
      created: true,
      action: original,
    });
    assert.deepEqual(
      await store.loadBlocking({ storeCode: STORE, deviceCode: DEVICE }),
      original,
    );

    const row = await connection.getFirst<Record<string, unknown>>(
      "SELECT * FROM installment_actions WHERE action_id = ?",
      [ACTION_A],
    );
    assert.ok(row);
    assert.deepEqual(Object.keys(row), [
      "action_id",
      "store_code",
      "device_code",
      "installment_guid",
      "action_kind",
      "idempotency_key",
      "payment_guid",
      "payment_method",
      "amount_cents",
      "state",
      "resolution",
      "payload_revision",
      "command_ciphertext",
      "created_at_iso",
      "updated_at_iso",
      "resolved_at_iso",
      "resolution_code",
    ]);
    assert.ok(row.command_ciphertext instanceof Uint8Array);
    const plainColumns = JSON.stringify(row);
    assert.equal(plainColumns.includes("Private Customer"), false);
    assert.equal(plainColumns.includes("0400000000"), false);
    assert.equal(plainColumns.includes("Private note"), false);
    assert.equal(plainColumns.includes("cartFingerprint"), false);

    const envelope = JSON.parse(
      encryptor.encryptedPlaintexts[0] ?? "",
    ) as Record<string, unknown>;
    assert.deepEqual(envelope.aad, {
      revision: 1,
      storeCode: STORE,
      deviceCode: DEVICE,
      actionId: ACTION_A,
    });
    assert.equal(
      JSON.stringify(envelope).includes("Private Customer"),
      true,
    );
    assert.equal(
      JSON.stringify(envelope).includes("intentFingerprint"),
      true,
    );

    const blockedCandidate = repaymentCandidate({
      actionId: ACTION_B,
    });
    assert.deepEqual(await store.createIfNone(blockedCandidate), {
      created: false,
      action: original,
    });
    assert.equal(encryptor.encryptedPlaintexts.length, 1);
    assert.equal(
      await scalar(
        connection,
        "SELECT COUNT(*) AS count FROM installment_actions",
      ),
      1,
    );
    assert.equal(
      await store.loadBlocking({
        storeCode: STORE,
        deviceCode: "IPAD-OTHER",
      }),
      null,
    );

    const otherTerminal = repaymentCandidate({
      actionId: ACTION_B,
      deviceCode: "IPAD-OTHER",
    });
    assert.equal((await store.createIfNone(otherTerminal)).created, true);
    assert.equal(
      await scalar(
        connection,
        "SELECT COUNT(*) AS count FROM installment_actions",
      ),
      2,
    );
  });
});

test("真实 SQLite：M36 加密冻结 lifecycle 全命令，重启读取后只允许完成且禁止删除", async () => {
  await withMigratedDatabase(async (connection) => {
    const encryptor = new RecordingEncryptor();
    const store = new SqliteInstallmentActionStore(
      connection,
      encryptor,
      () => NOW,
    );
    const candidate = lifecycleCandidate("pickup");

    assert.deepEqual(await store.createLifecycleIfNone(candidate), {
      created: true,
      action: candidate,
    });
    assert.deepEqual(
      await store.loadLifecycleBlocking({
        storeCode: STORE,
        deviceCode: DEVICE,
      }),
      candidate,
    );

    const row = await connection.getFirst<Record<string, unknown>>(
      "SELECT * FROM installment_lifecycle_actions WHERE operation_guid = ?",
      [ACTION_B],
    );
    assert.ok(row);
    const plaintextColumns = JSON.stringify(row);
    assert.equal(plaintextColumns.includes("IPAD-ORIGINAL"), false);
    assert.equal(plaintextColumns.includes("Cashier Private"), false);
    assert.equal(plaintextColumns.includes("Private pickup note"), false);
    const envelope = encryptor.encryptedPlaintexts[0] ?? "";
    assert.equal(envelope.includes("IPAD-ORIGINAL"), true);
    assert.equal(envelope.includes("Cashier Private"), true);
    assert.equal(envelope.includes("Private pickup note"), true);

    await store.completeLifecycle({
      operationGuid: ACTION_B,
      terminal: { storeCode: STORE, deviceCode: DEVICE },
    });
    assert.equal(
      await store.loadLifecycleBlocking({
        storeCode: STORE,
        deviceCode: DEVICE,
      }),
      null,
    );
    await assert.rejects(
      () =>
        connection.run(
          "DELETE FROM installment_lifecycle_actions WHERE operation_guid = ?",
          [ACTION_B],
        ),
      /INSTALLMENT_LIFECYCLE_DELETE_FORBIDDEN/,
    );
  });
});

test("真实 SQLite：payment 与 lifecycle ledger 在同一 terminal scope 互斥", async () => {
  await withMigratedDatabase(async (connection) => {
    const store = new SqliteInstallmentActionStore(
      connection,
      new RecordingEncryptor(),
      () => NOW,
    );
    await store.createLifecycleIfNone(lifecycleCandidate("void"));
    await assert.rejects(
      () => store.createIfNone(createCandidate()),
      /lifecycle action blocks payment creation/,
    );
  });

  await withMigratedDatabase(async (connection) => {
    const store = new SqliteInstallmentActionStore(
      connection,
      new RecordingEncryptor(),
      () => NOW,
    );
    await store.createIfNone(createCandidate());
    await assert.rejects(
      () => store.createLifecycleIfNone(lifecycleCandidate("pickup")),
      /payment action blocks lifecycle creation/,
    );
  });
});

test("真实 SQLite：旧 V1 密文缺少支付选择字段仍按原形恢复", async () => {
  await withMigratedDatabase(async (connection) => {
    const legacy = createCandidate();
    const ciphertext = new Uint8Array([71]);
    const plaintext = JSON.stringify({
      format: "hb-pos-installment-action-v1",
      aad: {
        revision: 1,
        storeCode: STORE,
        deviceCode: DEVICE,
        actionId: ACTION_A,
      },
      action: legacy.action,
      command: legacy.command,
      intentFingerprint: legacy.intentFingerprint,
    });
    await connection.run(
      `INSERT INTO installment_actions (
        action_id, store_code, device_code, installment_guid,
        action_kind, idempotency_key, payment_guid, payment_method,
        amount_cents, state, resolution, payload_revision,
        command_ciphertext, created_at_iso, updated_at_iso,
        resolved_at_iso
      ) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)`,
      [
        ACTION_A,
        STORE,
        DEVICE,
        INSTALLMENT,
        "create",
        ACTION_A,
        PAYMENT,
        "card",
        2_000,
        "Created",
        null,
        1,
        ciphertext,
        NOW,
        NOW,
        null,
      ],
    );
    const store = new SqliteInstallmentActionStore(
      connection,
      new FixtureDecryptor(ciphertext, plaintext),
      () => NOW,
    );

    const restored = await store.loadBlocking({
      storeCode: STORE,
      deviceCode: DEVICE,
    });

    assert.deepEqual(restored, legacy);
    assert.ok(restored?.command.kind === "create");
    assert.equal(
      Object.hasOwn(restored.command, "cardProvider"),
      false,
    );
    assert.equal(
      Object.hasOwn(restored.command, "cashTenderedCents"),
      false,
    );
  });
});

test("真实 SQLite：cancel 摘要经 create/load、transition 与关闭重开完整恢复", async () => {
  const directory = await mkdtemp(
    join(tmpdir(), "hb-pos-installment-action-"),
  );
  const databasePath = join(directory, "actions.sqlite");
  const encryptor = new RecordingEncryptor();
  let connection: SqliteConnectionPort | null =
    new SystemSqliteConnection(new DatabaseSync(databasePath));

  try {
    await applyMigrations(connection, () => NOW);
    let store = new SqliteInstallmentActionStore(
      connection,
      encryptor,
      () => NOW,
    );
    const candidate = cancelCandidate({
      refundPlanFingerprint: REFUND_PLAN_FINGERPRINT,
    });

    assert.deepEqual(await store.createIfNone(candidate), {
      created: true,
      action: candidate,
    });
    assert.deepEqual(
      await store.loadBlocking({ storeCode: STORE, deviceCode: DEVICE }),
      candidate,
    );
    assert.deepEqual(
      (JSON.parse(encryptor.encryptedPlaintexts[0] ?? "") as {
        command?: unknown;
      }).command,
      candidate.command,
    );

    const transitioned = await store.transition({
      actionId: ACTION_C,
      expectedState: "Created",
      nextState: "ProviderPending",
      terminal: { storeCode: STORE, deviceCode: DEVICE },
    });
    assert.deepEqual(transitioned, {
      ...candidate,
      state: "ProviderPending",
    });

    await connection.close();
    connection = null;
    connection = new SystemSqliteConnection(
      new DatabaseSync(databasePath),
    );
    store = new SqliteInstallmentActionStore(
      connection,
      encryptor,
      () => NOW,
    );
    assert.deepEqual(
      await store.loadBlocking({ storeCode: STORE, deviceCode: DEVICE }),
      {
        ...candidate,
        state: "ProviderPending",
      },
    );
  } finally {
    await connection?.close();
    await rm(directory, { recursive: true, force: true });
  }
});

test("真实 SQLite：旧 cancel 密文缺少摘要仍可恢复", async () => {
  await withMigratedDatabase(async (connection) => {
    const legacy = cancelCandidate();
    const ciphertext = new Uint8Array([72]);
    const plaintext = JSON.stringify({
      format: "hb-pos-installment-action-v1",
      aad: {
        revision: 1,
        storeCode: STORE,
        deviceCode: DEVICE,
        actionId: ACTION_C,
      },
      action: legacy.action,
      command: legacy.command,
      intentFingerprint: legacy.intentFingerprint,
    });
    await connection.run(
      `INSERT INTO installment_actions (
        action_id, store_code, device_code, installment_guid,
        action_kind, idempotency_key, payment_guid, payment_method,
        amount_cents, state, resolution, payload_revision,
        command_ciphertext, created_at_iso, updated_at_iso,
        resolved_at_iso
      ) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)`,
      [
        ACTION_C,
        STORE,
        DEVICE,
        INSTALLMENT,
        "cancel-refund",
        ACTION_C,
        null,
        null,
        null,
        "Created",
        null,
        1,
        ciphertext,
        NOW,
        NOW,
        null,
      ],
    );
    const store = new SqliteInstallmentActionStore(
      connection,
      new FixtureDecryptor(ciphertext, plaintext),
      () => NOW,
    );

    const restored = await store.loadBlocking({
      storeCode: STORE,
      deviceCode: DEVICE,
    });

    assert.deepEqual(restored, legacy);
    assert.ok(restored?.command.kind === "cancel-refund");
    assert.equal(
      Object.hasOwn(restored.command, "refundPlanFingerprint"),
      false,
    );
  });
});

test("真实 SQLite：cancel 带退款方式字段可 create/load，旧数据缺省视为原路退", async () => {
  await withMigratedDatabase(async (connection) => {
    const encryptor = new RecordingEncryptor();
    const store = new SqliteInstallmentActionStore(
      connection,
      encryptor,
      () => NOW,
    );
    const candidate = cancelCandidate({
      refundPlanFingerprint: REFUND_PLAN_FINGERPRINT,
      refundMode: "voucher",
    });

    assert.deepEqual(await store.createIfNone(candidate), {
      created: true,
      action: candidate,
    });
    const restored = await store.loadBlocking({
      storeCode: STORE,
      deviceCode: DEVICE,
    });
    assert.deepEqual(restored, candidate);
    assert.ok(restored?.command.kind === "cancel-refund");
    assert.equal(restored.command.refundMode, "voucher");
  });
});

test("真实 SQLite：cancel 坏摘要与未知字段 fail closed", async () => {
  await withMigratedDatabase(async (connection) => {
    const encryptor = new RecordingEncryptor();
    const store = new SqliteInstallmentActionStore(
      connection,
      encryptor,
      () => NOW,
    );
    const invalidCommands = [
      { refundPlanFingerprint: `sha256:${"a".repeat(63)}` },
      { refundPlanFingerprint: `sha256:${"A".repeat(64)}` },
      { refundPlanFingerprint: "a".repeat(64) },
      { refundPlanFingerprint: `sha256:${"g".repeat(64)}` },
      { refundPlanFingerprint: `${REFUND_PLAN_FINGERPRINT}\n` },
      { refundPlanFingerprint: null },
      {
        refundPlanFingerprint: REFUND_PLAN_FINGERPRINT,
        unexpected: true,
      },
      // 退款方式只允许与摘要同时出现，且只接受两种取值。
      { refundMode: "voucher" },
      { refundPlanFingerprint: REFUND_PLAN_FINGERPRINT, refundMode: "card" },
      { refundPlanFingerprint: REFUND_PLAN_FINGERPRINT, refundMode: null },
      {
        refundPlanFingerprint: REFUND_PLAN_FINGERPRINT,
        refundMode: "voucher",
        unexpected: true,
      },
    ] as const;

    for (const command of invalidCommands) {
      await assert.rejects(
        () => store.createIfNone(cancelCandidate(command)),
        /cancel command|fingerprint|refund mode/i,
      );
    }
    assert.equal(encryptor.encryptedPlaintexts.length, 0);
    assert.equal(
      await scalar(
        connection,
        "SELECT COUNT(*) AS count FROM installment_actions",
      ),
      0,
    );
  });
});

test("真实 SQLite：V1 方法特定字段只保留 card provider 或现金实收", async () => {
  const cases = [
    paymentSelectionCandidate(createCandidate(), {
      method: "card",
      selection: {
        cardProvider: "square",
      },
    }),
    paymentSelectionCandidate(
      repaymentCandidate({ actionId: ACTION_B }),
      {
        method: "card",
        selection: {
          cardProvider: "linkly-cloud",
        },
      },
    ),
    paymentSelectionCandidate(
      repaymentCandidate({ actionId: ACTION_B }),
      {
        method: "cash",
        selection: {
          cashTenderedCents: 800,
        },
      },
    ),
    paymentSelectionCandidate(
      repaymentCandidate({ actionId: ACTION_B }),
      {
        method: "voucher",
        selection: {},
      },
    ),
  ] as const;

  for (const candidate of cases) {
    await withMigratedDatabase(async (connection) => {
      const encryptor = new RecordingEncryptor();
      const store = new SqliteInstallmentActionStore(
        connection,
        encryptor,
        () => NOW,
      );

      assert.deepEqual(await store.createIfNone(candidate), {
        created: true,
        action: candidate,
      });
      assert.deepEqual(
        await store.loadBlocking({
          storeCode: STORE,
          deviceCode: DEVICE,
        }),
        candidate,
      );
      const envelope = JSON.parse(
        encryptor.encryptedPlaintexts[0] ?? "",
      ) as {
        command?: unknown;
      };
      assert.deepEqual(envelope.command, candidate.command);
      assert.equal(
        (
          await connection.getFirst<{ payload_revision: unknown }>(
            `SELECT payload_revision
             FROM installment_actions
             WHERE action_id = ?`,
            [candidate.action.actionId],
          )
        )?.payload_revision,
        1,
      );
    });
  }
});

test("真实 SQLite：拒绝不完整、越界或与支付方式冲突的新支付选择", async () => {
  await withMigratedDatabase(async (connection) => {
    const encryptor = new RecordingEncryptor();
    const store = new SqliteInstallmentActionStore(
      connection,
      encryptor,
      () => NOW,
    );
    const card = createCandidate();
    const cash = repaymentCandidate({ actionId: ACTION_B });
    const invalidCandidates = [
      paymentSelectionCandidate(card, {
        method: "card",
        selection: { cashTenderedCents: 2_000 },
      }),
      paymentSelectionCandidate(card, {
        method: "card",
        selection: {
          cardProvider: null,
        },
      }),
      paymentSelectionCandidate(card, {
        method: "card",
        selection: {
          cardProvider: "adyen",
        },
      }),
      paymentSelectionCandidate(card, {
        method: "card",
        selection: {
          cardProvider: "square",
          cashTenderedCents: 2_001,
        },
      }),
      paymentSelectionCandidate(cash, {
        method: "cash",
        selection: {
          cashTenderedCents: 499,
        },
      }),
      paymentSelectionCandidate(cash, {
        method: "cash",
        selection: {
          cashTenderedCents: Number.MAX_SAFE_INTEGER + 1,
        },
      }),
      paymentSelectionCandidate(cash, {
        method: "cash",
        selection: {
          cardProvider: "square",
        },
      }),
      paymentSelectionCandidate(cash, {
        method: "cash",
        selection: {
          cardProvider: null,
          cashTenderedCents: 500,
        },
      }),
      paymentSelectionCandidate(cash, {
        method: "voucher",
        selection: {
          cashTenderedCents: 501,
        },
      }),
      paymentSelectionCandidate(cash, {
        method: "voucher",
        selection: {
          cardProvider: "linkly-cloud",
        },
      }),
      cancelCandidateWithPaymentSelection(),
    ];

    for (const candidate of invalidCandidates) {
      await assert.rejects(
        () => store.createIfNone(candidate),
        /command|payment selection|cash tendered/i,
      );
    }
    assert.equal(encryptor.encryptedPlaintexts.length, 0);
    assert.equal(
      await scalar(
        connection,
        "SELECT COUNT(*) AS count FROM installment_actions",
      ),
      0,
    );
  });
});

test("真实 SQLite：坏 blocking 密文使 createIfNone fail closed 且不插候选", async () => {
  await withMigratedDatabase(async (connection) => {
    const encryptor = new RecordingEncryptor();
    const store = new SqliteInstallmentActionStore(
      connection,
      encryptor,
      () => NOW,
    );
    await store.createIfNone(createCandidate());
    // 模拟磁盘/旧构建损坏；生产 M21 trigger 本身会拒绝普通 SQL 换密文。
    await connection.exec("DROP TRIGGER trg_installment_actions_immutable;");
    await connection.run(
      `UPDATE installment_actions
       SET command_ciphertext = ?
       WHERE action_id = ?`,
      [new Uint8Array([255]), ACTION_A],
    );

    await assert.rejects(
      () =>
        store.loadBlocking({
          storeCode: STORE,
          deviceCode: DEVICE,
        }),
      /ciphertext|installment action/i,
    );
    await assert.rejects(
      () =>
        store.createIfNone(
          repaymentCandidate({ actionId: ACTION_B }),
        ),
      /ciphertext|installment action/i,
    );
    assert.equal(
      await scalar(
        connection,
        "SELECT COUNT(*) AS count FROM installment_actions",
      ),
      1,
    );
  });
});

test("真实 SQLite：candidate 加密或 SQL 插入失败时不留下 blocking action", async () => {
  await withMigratedDatabase(async (connection) => {
    const encryptor = new RecordingEncryptor();
    const store = new SqliteInstallmentActionStore(
      connection,
      encryptor,
      () => NOW,
    );
    encryptor.failEncryption = true;
    await assert.rejects(
      () => store.createIfNone(createCandidate()),
      /TEST_ENCRYPTION_FAILURE/,
    );
    encryptor.failEncryption = false;
    assert.equal(
      await scalar(
        connection,
        "SELECT COUNT(*) AS count FROM installment_actions",
      ),
      0,
    );

    await connection.exec(`
      CREATE TRIGGER fail_installment_action_insert
      BEFORE INSERT ON installment_actions
      BEGIN
        SELECT RAISE(ABORT, 'INSTALLMENT_ACTION_INSERT_FAILURE');
      END;
    `);
    await assert.rejects(
      () => store.createIfNone(createCandidate()),
      /INSTALLMENT_ACTION_INSERT_FAILURE/,
    );
    assert.equal(
      await scalar(
        connection,
        "SELECT COUNT(*) AS count FROM installment_actions",
      ),
      0,
    );
  });
});

test("真实 SQLite：transition/complete 按 scope+expectedState CAS，resolved fact 保留且不再 blocking", async () => {
  await withMigratedDatabase(async (connection) => {
    const store = new SqliteInstallmentActionStore(
      connection,
      new RecordingEncryptor(),
      () => NOW,
    );
    await store.createIfNone(createCandidate());
    await assert.rejects(
      () =>
        store.transition({
          actionId: ACTION_A,
          expectedState: "Created",
          nextState: "Approved",
          terminal: { storeCode: STORE, deviceCode: DEVICE },
        }),
      /transition|state/i,
    );
    await assert.rejects(
      () =>
        store.transition({
          actionId: ACTION_A,
          expectedState: "Created",
          nextState: "ProviderPending",
          terminal: { storeCode: STORE, deviceCode: "IPAD-OTHER" },
        }),
      /state|scope|action/i,
    );

    let current = await store.transition({
      actionId: ACTION_A,
      expectedState: "Created",
      nextState: "ProviderPending",
      terminal: { storeCode: STORE, deviceCode: DEVICE },
    });
    assert.equal(current.state, "ProviderPending");
    await assert.rejects(
      () =>
        store.transition({
          actionId: ACTION_A,
          expectedState: "Created",
          nextState: "ProviderPending",
          terminal: { storeCode: STORE, deviceCode: DEVICE },
        }),
      /state|CAS|action/i,
    );

    current = await store.transition({
      actionId: ACTION_A,
      expectedState: "ProviderPending",
      nextState: "Unknown",
      terminal: { storeCode: STORE, deviceCode: DEVICE },
    });
    assert.equal(current.state, "Unknown");
    current = await store.transition({
      actionId: ACTION_A,
      expectedState: "Unknown",
      nextState: "Approved",
      terminal: { storeCode: STORE, deviceCode: DEVICE },
    });
    assert.equal(current.state, "Approved");

    await connection.exec(`
      CREATE TRIGGER fail_installment_backend_pending
      AFTER UPDATE OF state ON installment_actions
      WHEN NEW.state = 'BackendPending'
      BEGIN
        SELECT RAISE(ABORT, 'INSTALLMENT_BACKEND_PENDING_FAILURE');
      END;
    `);
    await assert.rejects(
      () =>
        store.transition({
          actionId: ACTION_A,
          expectedState: "Approved",
          nextState: "BackendPending",
          terminal: { storeCode: STORE, deviceCode: DEVICE },
        }),
      /INSTALLMENT_BACKEND_PENDING_FAILURE/,
    );
    assert.equal(
      (
        await store.loadBlocking({
          storeCode: STORE,
          deviceCode: DEVICE,
        })
      )?.state,
      "Approved",
    );
    await connection.exec("DROP TRIGGER fail_installment_backend_pending;");

    current = await store.transition({
      actionId: ACTION_A,
      expectedState: "Approved",
      nextState: "BackendPending",
      terminal: { storeCode: STORE, deviceCode: DEVICE },
    });
    assert.equal(current.state, "BackendPending");
    await store.complete({
      actionId: ACTION_A,
      expectedState: "BackendPending",
      terminal: { storeCode: STORE, deviceCode: DEVICE },
    });
    assert.equal(
      await store.loadBlocking({
        storeCode: STORE,
        deviceCode: DEVICE,
      }),
      null,
    );
    const resolved = await connection.getFirst<{
      state: unknown;
      resolution: unknown;
    }>(
      `SELECT state, resolution
       FROM installment_actions
       WHERE action_id = ?`,
      [ACTION_A],
    );
    assert.deepEqual(resolved === null ? null : { ...resolved }, {
      state: "BackendPending",
      resolution: "Completed",
    });

    await assert.rejects(
      () =>
        connection.run(
          `UPDATE installment_actions
           SET installment_guid = ?
           WHERE action_id = ?`,
          [ACTION_B, ACTION_A],
        ),
      /immutable/i,
    );
    await assert.rejects(
      () =>
        connection.run(
          "DELETE FROM installment_actions WHERE action_id = ?",
          [ACTION_A],
        ),
      /delete|immutable/i,
    );
    await assert.rejects(
      () => store.createIfNone(createCandidate()),
      /unique|constraint/i,
    );
  });
});

test("真实 SQLite：committed repayment 快照与 action 完成共用一个 exclusive transaction", async () => {
  await withMigratedDatabase(async (connection) => {
    const encryptor = new RecordingEncryptor();
    const actionStore = new SqliteInstallmentActionStore(
      connection,
      encryptor,
      () => NOW,
    );
    const snapshotRepository = new SqliteInstallmentSnapshotRepository(
      connection,
      encryptor,
    );
    await actionStore.createIfNone(repaymentCandidate({ actionId: ACTION_A }));
    await actionStore.transition({
      actionId: ACTION_A,
      expectedState: "Created",
      nextState: "ProviderPending",
      terminal: { storeCode: STORE, deviceCode: DEVICE },
    });
    await actionStore.transition({
      actionId: ACTION_A,
      expectedState: "ProviderPending",
      nextState: "Approved",
      terminal: { storeCode: STORE, deviceCode: DEVICE },
    });
    await actionStore.transition({
      actionId: ACTION_A,
      expectedState: "Approved",
      nextState: "BackendPending",
      terminal: { storeCode: STORE, deviceCode: DEVICE },
    });

    const transactionsBefore = connection.transactionCount;
    await actionStore.completeCommittedRepaymentWithSnapshot(
      {
        actionId: ACTION_A,
        expectedState: "BackendPending",
        terminal: { storeCode: STORE, deviceCode: DEVICE },
        snapshot: committedRepaymentSnapshot(),
      },
      snapshotRepository,
    );

    assert.equal(connection.transactionCount - transactionsBefore, 1);
    assert.deepEqual(
      await snapshotRepository.get(STORE, INSTALLMENT),
      committedRepaymentSnapshot(),
    );
    assert.equal(
      await actionStore.loadBlocking({ storeCode: STORE, deviceCode: DEVICE }),
      null,
    );
    const resolved = await connection.getFirst<{
      state: unknown;
      resolution: unknown;
    }>(
      "SELECT state, resolution FROM installment_actions WHERE action_id = ?",
      [ACTION_A],
    );
    assert.deepEqual(resolved === null ? null : { ...resolved }, {
      state: "BackendPending",
      resolution: "Completed",
    });
  });
});

test("committed repayment 快照加密失败发生在事务前，action 仍保持 BackendPending", async () => {
  await withMigratedDatabase(async (connection) => {
    const encryptor = new RecordingEncryptor();
    const actionStore = new SqliteInstallmentActionStore(
      connection,
      encryptor,
      () => NOW,
    );
    const snapshotRepository = new SqliteInstallmentSnapshotRepository(
      connection,
      encryptor,
    );
    await actionStore.createIfNone(repaymentCandidate({ actionId: ACTION_A }));
    await actionStore.transition({
      actionId: ACTION_A,
      expectedState: "Created",
      nextState: "ProviderPending",
      terminal: { storeCode: STORE, deviceCode: DEVICE },
    });
    await actionStore.transition({
      actionId: ACTION_A,
      expectedState: "ProviderPending",
      nextState: "Approved",
      terminal: { storeCode: STORE, deviceCode: DEVICE },
    });
    await actionStore.transition({
      actionId: ACTION_A,
      expectedState: "Approved",
      nextState: "BackendPending",
      terminal: { storeCode: STORE, deviceCode: DEVICE },
    });
    const transactionsBefore = connection.transactionCount;
    encryptor.failEncryption = true;

    await assert.rejects(
      actionStore.completeCommittedRepaymentWithSnapshot(
        {
          actionId: ACTION_A,
          expectedState: "BackendPending",
          terminal: { storeCode: STORE, deviceCode: DEVICE },
          snapshot: committedRepaymentSnapshot(),
        },
        snapshotRepository,
      ),
      /TEST_ENCRYPTION_FAILURE/,
    );

    assert.equal(connection.transactionCount, transactionsBefore);
    assert.equal(
      (await actionStore.loadBlocking({ storeCode: STORE, deviceCode: DEVICE }))
        ?.state,
      "BackendPending",
    );
    assert.equal(await snapshotRepository.get(STORE, INSTALLMENT), null);
  });
});

test("committed repayment action CAS 写失败时快照与 action 一起回滚", async () => {
  await withMigratedDatabase(async (connection) => {
    const encryptor = new RecordingEncryptor();
    const actionStore = new SqliteInstallmentActionStore(
      connection,
      encryptor,
      () => NOW,
    );
    const snapshotRepository = new SqliteInstallmentSnapshotRepository(
      connection,
      encryptor,
    );
    await actionStore.createIfNone(repaymentCandidate({ actionId: ACTION_A }));
    await actionStore.transition({
      actionId: ACTION_A,
      expectedState: "Created",
      nextState: "ProviderPending",
      terminal: { storeCode: STORE, deviceCode: DEVICE },
    });
    await actionStore.transition({
      actionId: ACTION_A,
      expectedState: "ProviderPending",
      nextState: "Approved",
      terminal: { storeCode: STORE, deviceCode: DEVICE },
    });
    await actionStore.transition({
      actionId: ACTION_A,
      expectedState: "Approved",
      nextState: "BackendPending",
      terminal: { storeCode: STORE, deviceCode: DEVICE },
    });
    await connection.exec(`
      CREATE TRIGGER fail_committed_repayment_action_resolution
      BEFORE UPDATE OF resolution ON installment_actions
      WHEN NEW.resolution = 'Completed'
      BEGIN
        SELECT RAISE(ABORT, 'COMMITTED_REPAYMENT_ACTION_CAS_FAILURE');
      END;
    `);

    await assert.rejects(
      actionStore.completeCommittedRepaymentWithSnapshot(
        {
          actionId: ACTION_A,
          expectedState: "BackendPending",
          terminal: { storeCode: STORE, deviceCode: DEVICE },
          snapshot: committedRepaymentSnapshot(),
        },
        snapshotRepository,
      ),
      /COMMITTED_REPAYMENT_ACTION_CAS_FAILURE/,
    );

    assert.equal(await snapshotRepository.get(STORE, INSTALLMENT), null);
    assert.equal(
      (await actionStore.loadBlocking({ storeCode: STORE, deviceCode: DEVICE }))
        ?.state,
      "BackendPending",
    );
    const resolved = await connection.getFirst<{ resolution: unknown }>(
      "SELECT resolution FROM installment_actions WHERE action_id = ?",
      [ACTION_A],
    );
    assert.deepEqual(resolved === null ? null : { ...resolved }, {
      resolution: null,
    });
  });
});

test("committed repayment 拒绝伪造、跨 connection 或跨 encryptor 的 snapshot repository", async () => {
  const outcomes: {
    result: PromiseSettledResult<void>;
    startedTransactions: number;
  }[] = [];

  await withMigratedDatabase(async (connection) => {
    const encryptor = new RecordingEncryptor();
    const actionStore = new SqliteInstallmentActionStore(
      connection,
      encryptor,
      () => NOW,
    );
    await moveRepaymentToBackendPending(actionStore, ACTION_A);
    const transactionsBefore = connection.transactionCount;
    const fakeRepository = {
      async prepareUpsertForStore(
        _storeCode: string,
        snapshots: readonly InstallmentSnapshot[],
      ) {
        return Object.freeze([
          Object.freeze({
            snapshot: snapshots[0]!,
            ciphertext: Uint8Array.of(1),
          }),
        ]);
      },
      async upsertPreparedInTransaction() {
        // 审查复现：旧实现会信任这个空写入并把 action 标成 Completed。
      },
    } as unknown as SqliteInstallmentSnapshotRepository;
    const [result] = await Promise.allSettled([
      actionStore.completeCommittedRepaymentWithSnapshot(
        {
          actionId: ACTION_A,
          expectedState: "BackendPending",
          terminal: { storeCode: STORE, deviceCode: DEVICE },
          snapshot: committedRepaymentSnapshot(),
        },
        fakeRepository,
      ),
    ]);
    assert.ok(result);
    outcomes.push({
      result,
      startedTransactions: connection.transactionCount - transactionsBefore,
    });
  });

  await withMigratedDatabase(async (connection) => {
    const encryptor = new RecordingEncryptor();
    const actionStore = new SqliteInstallmentActionStore(
      connection,
      encryptor,
      () => NOW,
    );
    await moveRepaymentToBackendPending(actionStore, ACTION_A);
    const transactionsBefore = connection.transactionCount;
    const wrongEncryptorRepository = new SqliteInstallmentSnapshotRepository(
      connection,
      new RecordingEncryptor(),
    );
    const [result] = await Promise.allSettled([
      actionStore.completeCommittedRepaymentWithSnapshot(
        {
          actionId: ACTION_A,
          expectedState: "BackendPending",
          terminal: { storeCode: STORE, deviceCode: DEVICE },
          snapshot: committedRepaymentSnapshot(),
        },
        wrongEncryptorRepository,
      ),
    ]);
    assert.ok(result);
    outcomes.push({
      result,
      startedTransactions: connection.transactionCount - transactionsBefore,
    });
  });

  await withMigratedDatabase(async (connection) => {
    const encryptor = new RecordingEncryptor();
    const actionStore = new SqliteInstallmentActionStore(
      connection,
      encryptor,
      () => NOW,
    );
    await moveRepaymentToBackendPending(actionStore, ACTION_A);
    const transactionsBefore = connection.transactionCount;
    await withMigratedDatabase(async (otherConnection) => {
      const wrongConnectionRepository =
        new SqliteInstallmentSnapshotRepository(otherConnection, encryptor);
      const [result] = await Promise.allSettled([
        actionStore.completeCommittedRepaymentWithSnapshot(
          {
            actionId: ACTION_A,
            expectedState: "BackendPending",
            terminal: { storeCode: STORE, deviceCode: DEVICE },
            snapshot: committedRepaymentSnapshot(),
          },
          wrongConnectionRepository,
        ),
      ]);
      assert.ok(result);
      outcomes.push({
        result,
        startedTransactions:
          connection.transactionCount - transactionsBefore,
      });
    });
  });

  assert.deepEqual(
    outcomes.map((outcome) => outcome.result.status),
    ["rejected", "rejected", "rejected"],
  );
  assert.deepEqual(
    outcomes.map((outcome) => outcome.startedTransactions),
    [0, 0, 0],
  );
  for (const outcome of outcomes) {
    assert.equal(outcome.result.status, "rejected");
    assert.match(String(outcome.result.reason), /repository context/i);
  }
});

test("真实 SQLite：committed repayment 分别覆盖 snapshot INSERT 与 UPDATE", async () => {
  await withMigratedDatabase(async (connection) => {
    const encryptor = new RecordingEncryptor();
    const actionStore = new SqliteInstallmentActionStore(
      connection,
      encryptor,
      () => NOW,
    );
    const snapshotRepository = new SqliteInstallmentSnapshotRepository(
      connection,
      encryptor,
    );

    await moveRepaymentToBackendPending(actionStore, ACTION_A);
    await actionStore.completeCommittedRepaymentWithSnapshot(
      {
        actionId: ACTION_A,
        expectedState: "BackendPending",
        terminal: { storeCode: STORE, deviceCode: DEVICE },
        snapshot: committedRepaymentSnapshot(),
      },
      snapshotRepository,
    );

    await moveRepaymentToBackendPending(actionStore, ACTION_B);
    const updatedSnapshot = committedRepaymentSnapshot({
      customerName: "Updated Customer",
      paidCents: 3_000,
      balanceCents: 7_000,
      updatedAtIso: LATER,
    });
    await actionStore.completeCommittedRepaymentWithSnapshot(
      {
        actionId: ACTION_B,
        expectedState: "BackendPending",
        terminal: { storeCode: STORE, deviceCode: DEVICE },
        snapshot: updatedSnapshot,
      },
      snapshotRepository,
    );

    assert.deepEqual(
      await snapshotRepository.get(STORE, INSTALLMENT),
      updatedSnapshot,
    );
    assert.equal(
      await scalar(
        connection,
        "SELECT COUNT(*) AS count FROM installment_snapshots",
      ),
      1,
    );
    assert.deepEqual(
      (
        await connection.getAll<{ action_id: unknown; resolution: unknown }>(
          `SELECT action_id, resolution
           FROM installment_actions
           WHERE action_id IN (?, ?)
           ORDER BY action_id`,
          [ACTION_A, ACTION_B],
        )
      ).map((row) => ({ ...row })),
      [
        { action_id: ACTION_A, resolution: "Completed" },
        { action_id: ACTION_B, resolution: "Completed" },
      ],
    );
  });
});

test("committed repayment action identity 或 CAS 不一致时不留下 snapshot", async () => {
  await withMigratedDatabase(async (connection) => {
    const encryptor = new RecordingEncryptor();
    const actionStore = new SqliteInstallmentActionStore(
      connection,
      encryptor,
      () => NOW,
    );
    const snapshotRepository = new SqliteInstallmentSnapshotRepository(
      connection,
      encryptor,
    );
    await moveRepaymentToBackendPending(actionStore, ACTION_A);

    await assert.rejects(
      actionStore.completeCommittedRepaymentWithSnapshot(
        {
          actionId: ACTION_A,
          expectedState: "BackendPending",
          terminal: { storeCode: STORE, deviceCode: DEVICE },
          snapshot: committedRepaymentSnapshot({
            installmentGuid: OTHER_INSTALLMENT,
          }),
        },
        snapshotRepository,
      ),
      /identity mismatch/i,
    );
    await assert.rejects(
      actionStore.completeCommittedRepaymentWithSnapshot(
        {
          actionId: ACTION_B,
          expectedState: "BackendPending",
          terminal: { storeCode: STORE, deviceCode: DEVICE },
          snapshot: committedRepaymentSnapshot(),
        },
        snapshotRepository,
      ),
      /state CAS|state|action/i,
    );

    assert.equal(await snapshotRepository.get(STORE, INSTALLMENT), null);
    assert.equal(
      await snapshotRepository.get(STORE, OTHER_INSTALLMENT),
      null,
    );
    assert.equal(
      (await actionStore.loadBlocking({ storeCode: STORE, deviceCode: DEVICE }))
        ?.state,
      "BackendPending",
    );
  });
});

test("committed repayment snapshot INSERT/UPDATE SQL 失败均回滚并保留可恢复 action", async () => {
  await withMigratedDatabase(async (connection) => {
    const encryptor = new RecordingEncryptor();
    const actionStore = new SqliteInstallmentActionStore(
      connection,
      encryptor,
      () => NOW,
    );
    const snapshotRepository = new SqliteInstallmentSnapshotRepository(
      connection,
      encryptor,
    );
    await moveRepaymentToBackendPending(actionStore, ACTION_A);
    await connection.exec(`
      CREATE TRIGGER fail_committed_repayment_snapshot_insert
      BEFORE INSERT ON installment_snapshots
      BEGIN
        SELECT RAISE(ABORT, 'COMMITTED_REPAYMENT_SNAPSHOT_INSERT_FAILURE');
      END;
    `);

    await assert.rejects(
      actionStore.completeCommittedRepaymentWithSnapshot(
        {
          actionId: ACTION_A,
          expectedState: "BackendPending",
          terminal: { storeCode: STORE, deviceCode: DEVICE },
          snapshot: committedRepaymentSnapshot(),
        },
        snapshotRepository,
      ),
      /COMMITTED_REPAYMENT_SNAPSHOT_INSERT_FAILURE/,
    );
    assert.equal(await snapshotRepository.get(STORE, INSTALLMENT), null);
    assert.equal(
      (await actionStore.loadBlocking({ storeCode: STORE, deviceCode: DEVICE }))
        ?.state,
      "BackendPending",
    );
  });

  await withMigratedDatabase(async (connection) => {
    const encryptor = new RecordingEncryptor();
    const actionStore = new SqliteInstallmentActionStore(
      connection,
      encryptor,
      () => NOW,
    );
    const snapshotRepository = new SqliteInstallmentSnapshotRepository(
      connection,
      encryptor,
    );
    const originalSnapshot = committedRepaymentSnapshot();
    await snapshotRepository.upsertForStore(STORE, [originalSnapshot]);
    await moveRepaymentToBackendPending(actionStore, ACTION_A);
    await connection.exec(`
      CREATE TRIGGER fail_committed_repayment_snapshot_update
      BEFORE UPDATE ON installment_snapshots
      BEGIN
        SELECT RAISE(ABORT, 'COMMITTED_REPAYMENT_SNAPSHOT_UPDATE_FAILURE');
      END;
    `);

    await assert.rejects(
      actionStore.completeCommittedRepaymentWithSnapshot(
        {
          actionId: ACTION_A,
          expectedState: "BackendPending",
          terminal: { storeCode: STORE, deviceCode: DEVICE },
          snapshot: committedRepaymentSnapshot({
            paidCents: 3_000,
            balanceCents: 7_000,
            updatedAtIso: LATER,
          }),
        },
        snapshotRepository,
      ),
      /COMMITTED_REPAYMENT_SNAPSHOT_UPDATE_FAILURE/,
    );
    assert.deepEqual(
      await snapshotRepository.get(STORE, INSTALLMENT),
      originalSnapshot,
    );
    assert.equal(
      (await actionStore.loadBlocking({ storeCode: STORE, deviceCode: DEVICE }))
        ?.state,
      "BackendPending",
    );
  });
});

test("并发重复 committed repayment 幂等成功且 Completed 始终有同一可读 snapshot", async () => {
  await withMigratedDatabase(async (connection) => {
    const encryptor = new RecordingEncryptor();
    const actionStore = new SqliteInstallmentActionStore(
      connection,
      encryptor,
      () => NOW,
    );
    const snapshotRepository = new SqliteInstallmentSnapshotRepository(
      connection,
      encryptor,
    );
    const snapshot = committedRepaymentSnapshot();
    await moveRepaymentToBackendPending(actionStore, ACTION_C);
    const input = {
      actionId: ACTION_C,
      expectedState: "BackendPending" as const,
      terminal: { storeCode: STORE, deviceCode: DEVICE },
      snapshot,
    };

    const results = await Promise.allSettled([
      actionStore.completeCommittedRepaymentWithSnapshot(
        input,
        snapshotRepository,
      ),
      actionStore.completeCommittedRepaymentWithSnapshot(
        input,
        snapshotRepository,
      ),
    ]);

    assert.deepEqual(
      results.map((result) => result.status),
      ["fulfilled", "fulfilled"],
    );
    assert.deepEqual(
      await snapshotRepository.get(STORE, INSTALLMENT),
      snapshot,
    );
    assert.deepEqual(
      await connection.getFirst<{ resolution: unknown }>(
        "SELECT resolution FROM installment_actions WHERE action_id = ?",
        [ACTION_C],
      ).then((row) => (row === null ? null : { ...row })),
      { resolution: "Completed" },
    );

    await assert.rejects(
      actionStore.completeCommittedRepaymentWithSnapshot(
        {
          ...input,
          snapshot: committedRepaymentSnapshot({
            paidCents: 9_000,
            balanceCents: 1_000,
            updatedAtIso: LATER,
          }),
        },
        snapshotRepository,
      ),
      /snapshot.*mismatch|idempotent/i,
    );
    assert.deepEqual(
      await snapshotRepository.get(STORE, INSTALLMENT),
      snapshot,
    );
  });
});

test("真实 SQLite：decline 仅释放 ProviderPending/Unknown 并保留 Declined 事实", async () => {
  await withMigratedDatabase(async (connection) => {
    const store = new SqliteInstallmentActionStore(
      connection,
      new RecordingEncryptor(),
      () => NOW,
    );
    const candidate = repaymentCandidate({ actionId: ACTION_C });
    await store.createIfNone(candidate);
    await assert.rejects(
      () =>
        store.decline({
          actionId: ACTION_C,
          expectedState: "Unknown",
          terminal: { storeCode: STORE, deviceCode: DEVICE },
        }),
      /state|CAS|action/i,
    );
    await store.transition({
      actionId: ACTION_C,
      expectedState: "Created",
      nextState: "ProviderPending",
      terminal: { storeCode: STORE, deviceCode: DEVICE },
    });
    await store.decline({
      actionId: ACTION_C,
      expectedState: "ProviderPending",
      terminal: { storeCode: STORE, deviceCode: DEVICE },
    });
    assert.equal(
      await store.loadBlocking({
        storeCode: STORE,
        deviceCode: DEVICE,
      }),
      null,
    );
    assert.equal(
      (
        await connection.getFirst<{ resolution: unknown }>(
          "SELECT resolution FROM installment_actions WHERE action_id = ?",
          [ACTION_C],
        )
      )?.resolution,
      "Declined",
    );
    await assert.rejects(
      () =>
        store.decline({
          actionId: ACTION_C,
          expectedState: "ProviderPending",
          terminal: { storeCode: STORE, deviceCode: DEVICE },
        }),
      /state|CAS|action/i,
    );
  });
});

test("真实 SQLite：Created claim 确定失败在单事务终结，并发调用不再进入恢复列表", async () => {
  await withMigratedDatabase(async (connection) => {
    const store = new SqliteInstallmentActionStore(
      connection,
      new RecordingEncryptor(),
      () => NOW,
    );
    await store.createIfNone(repaymentCandidate({ actionId: ACTION_C }));

    const [first, second] = await Promise.allSettled([
      store.finalizeCreatedFailure({
        actionId: ACTION_C,
        reason: "ClaimMismatch",
        terminal: { storeCode: STORE, deviceCode: DEVICE },
      }),
      store.finalizeCreatedFailure({
        actionId: ACTION_C,
        reason: "ClaimMismatch",
        terminal: { storeCode: STORE, deviceCode: DEVICE },
      }),
    ]);

    assert.deepEqual(
      [first.status, second.status].sort(),
      ["fulfilled", "rejected"],
    );
    assert.equal(
      await store.loadBlocking({ storeCode: STORE, deviceCode: DEVICE }),
      null,
    );
    const row = await connection.getFirst<{
      state: unknown;
      resolution: unknown;
    }>(
      "SELECT state, resolution FROM installment_actions WHERE action_id = ?",
      [ACTION_C],
    );
    assert.deepEqual(row === null ? null : { ...row }, {
      state: "ProviderPending",
      resolution: "Declined",
    });
    const audit = await connection.getFirst<{
      event_type: unknown;
      payload_json: unknown;
    }>(
      "SELECT event_type, payload_json FROM audit_events WHERE event_id = ?",
      [ACTION_C],
    );
    assert.equal(audit?.event_type, "INSTALLMENT_REPAYMENT_CLAIM_REVIEW");
    assert.deepEqual(JSON.parse(String(audit?.payload_json)), {
      outcome: "RequiresReview",
      reason: "Repayment claim provider binding mismatch.",
      status: "Failed",
    });
  });
});

test("真实 SQLite：Created claim 终结的 audit 写入失败时状态与 resolution 全部回滚", async () => {
  await withMigratedDatabase(async (connection) => {
    const store = new SqliteInstallmentActionStore(
      connection,
      new RecordingEncryptor(),
      () => NOW,
    );
    await store.createIfNone(repaymentCandidate({ actionId: ACTION_C }));
    await connection.exec(`
      CREATE TRIGGER fail_installment_claim_review_audit
      BEFORE INSERT ON audit_events
      WHEN NEW.event_type = 'INSTALLMENT_REPAYMENT_CLAIM_REVIEW'
      BEGIN
        SELECT RAISE(ABORT, 'INSTALLMENT_CLAIM_REVIEW_AUDIT_FAILURE');
      END;
    `);

    await assert.rejects(
      store.finalizeCreatedFailure({
        actionId: ACTION_C,
        reason: "ClaimMismatch",
        terminal: { storeCode: STORE, deviceCode: DEVICE },
      }),
      /INSTALLMENT_CLAIM_REVIEW_AUDIT_FAILURE/,
    );

    assert.equal(
      (
        await store.loadBlocking({ storeCode: STORE, deviceCode: DEVICE })
      )?.state,
      "Created",
    );
    const row = await connection.getFirst<{
      state: unknown;
      resolution: unknown;
    }>(
      "SELECT state, resolution FROM installment_actions WHERE action_id = ?",
      [ACTION_C],
    );
    assert.deepEqual(row === null ? null : { ...row }, {
      state: "Created",
      resolution: null,
    });
  });
});

test("真实 SQLite：ClaimBusy 普通终结不生成 requires-review audit", async () => {
  await withMigratedDatabase(async (connection) => {
    const store = new SqliteInstallmentActionStore(
      connection,
      new RecordingEncryptor(),
      () => NOW,
    );
    await store.createIfNone(repaymentCandidate({ actionId: ACTION_C }));
    await store.finalizeCreatedFailure({
      actionId: ACTION_C,
      reason: "ClaimBusy",
      terminal: { storeCode: STORE, deviceCode: DEVICE },
    });

    assert.equal(
      await store.loadBlocking({ storeCode: STORE, deviceCode: DEVICE }),
      null,
    );
    assert.equal(
      Number(
        (
          await connection.getFirst<{ count: unknown }>(
            "SELECT COUNT(*) AS count FROM audit_events WHERE event_id = ?",
            [ACTION_C],
          )
        )?.count,
      ),
      0,
    );
  });
});

test("真实 SQLite：Card unsupported 以专用 resolution code 与 audit 原子终结", async () => {
  await withMigratedDatabase(async (connection) => {
    const store = new SqliteInstallmentActionStore(
      connection,
      new RecordingEncryptor(),
      () => NOW,
    );
    await store.createIfNone(repaymentCandidate({ actionId: ACTION_C }));

    await store.finalizeCreatedFailure({
      actionId: ACTION_C,
      reason: "PaymentMethodUnsupported",
      terminal: { storeCode: STORE, deviceCode: DEVICE },
    });

    assert.equal(
      await store.loadBlocking({ storeCode: STORE, deviceCode: DEVICE }),
      null,
    );
    const row = await connection.getFirst<{
      state: unknown;
      resolution: unknown;
      resolution_code: unknown;
    }>(
      "SELECT state, resolution, resolution_code FROM installment_actions WHERE action_id = ?",
      [ACTION_C],
    );
    assert.deepEqual(row === null ? null : { ...row }, {
      state: "ProviderPending",
      resolution: "Declined",
      resolution_code: "PaymentMethodUnsupported",
    });
    const audit = await connection.getFirst<{
      event_type: unknown;
      payload_json: unknown;
    }>(
      "SELECT event_type, payload_json FROM audit_events WHERE event_id = ?",
      [ACTION_C],
    );
    assert.equal(
      audit?.event_type,
      "INSTALLMENT_REPAYMENT_PAYMENT_METHOD_UNSUPPORTED",
    );
    assert.deepEqual(JSON.parse(String(audit?.payload_json)), {
      outcome: "PaymentMethodUnsupported",
      reason: "Card installment repayment is unsupported.",
      status: "Failed",
    });
  });
});

test("PosDatabase.installmentActions(encryptor) 暴露冻结 runtime Port", async () => {
  const database = await PosDatabase.open({
    databaseName: ":memory:",
    driver: new SystemSqliteDriver(),
    keyProvider: {
      getOrCreateDatabaseKey: async () => "a".repeat(64),
    },
    nowIso: () => NOW,
  });
  try {
    const store = database.installmentActions(
      new RecordingEncryptor(),
    );
    assert.ok(store instanceof SqliteInstallmentActionStore);
    assert.equal(
      (await store.createIfNone(createCandidate())).created,
      true,
    );
    assert.deepEqual(
      await store.loadBlocking({
        storeCode: STORE,
        deviceCode: DEVICE,
      }),
      createCandidate(),
    );
  } finally {
    await database.close();
  }
});

function createCandidate(): PersistedInstallmentAction {
  return Object.freeze({
    action: Object.freeze({
      actionId: ACTION_A,
      idempotencyKey: ACTION_A,
      kind: "create" as const,
      installmentGuid: INSTALLMENT,
      paymentGuid: PAYMENT,
      method: "card" as const,
      amountCents: 2_000,
    }),
    command: Object.freeze({
      deviceCode: DEVICE,
      cashierId: "cashier-1",
      cashierName: "Alice",
      kind: "create" as const,
      installmentGuid: INSTALLMENT,
      createdAtIso: NOW,
      totalCents: 10_000,
      downPaymentCents: 2_000,
      lines: Object.freeze([
        Object.freeze({
          installmentLineGuid: LINE,
          productCode: "P-1",
          referenceCode: null,
          displayName: "Tea",
          lookupCode: "TEA",
          quantity: "1",
          unitPriceCents: 10_000,
          discountCents: 0,
          actualAmountCents: 10_000,
          itemNumber: "I-1",
        }),
      ]),
      customerName: "Private Customer",
      customerPhone: "0400000000",
      note: "Private note",
      cartFingerprint: '{"private":"cartFingerprint"}',
      draftRevision: 1,
    }),
    deviceCode: DEVICE,
    intentFingerprint: '{"private":"intentFingerprint"}',
    state: "Created" as const,
    storeCode: STORE,
  });
}

function lifecycleCandidate(
  kind: "void" | "pickup",
): PersistedInstallmentLifecycleAction {
  const common = {
    deviceCode: DEVICE,
    cashierId: "cashier-private",
    cashierName: "Cashier Private",
    installmentGuid: INSTALLMENT,
    operationGuid: ACTION_B,
    idempotencyKey: ACTION_B,
  } as const;
  const command =
    kind === "void"
      ? Object.freeze({
          ...common,
          voidedAtIso: NOW,
          reason: "Private void reason",
        })
      : Object.freeze({
          ...common,
          confirmedAtIso: NOW,
          note: "Private pickup note",
        });
  return Object.freeze({
    operationGuid: ACTION_B,
    idempotencyKey: ACTION_B,
    kind,
    installmentGuid: INSTALLMENT,
    storeCode: STORE,
    deviceCode: DEVICE,
    originalDeviceCode: "IPAD-ORIGINAL",
    command,
    intentFingerprint: `sha256:${"b".repeat(64)}`,
  });
}

function repaymentCandidate(
  overrides: Readonly<{
    actionId: string;
    deviceCode?: string;
  }>,
): PersistedInstallmentAction {
  const deviceCode = overrides.deviceCode ?? DEVICE;
  return Object.freeze({
    action: Object.freeze({
      actionId: overrides.actionId,
      idempotencyKey: overrides.actionId,
      kind: "repayment" as const,
      installmentGuid: INSTALLMENT,
      paymentGuid: PAYMENT,
      method: "cash" as const,
      amountCents: 500,
    }),
    command: Object.freeze({
      deviceCode,
      cashierId: "cashier-1",
      cashierName: "Alice",
      kind: "repayment" as const,
      installmentGuid: INSTALLMENT,
    }),
    deviceCode,
    intentFingerprint: '{"kind":"repayment"}',
    state: "Created" as const,
    storeCode: STORE,
  });
}

function committedRepaymentSnapshot(
  overrides: Partial<InstallmentSnapshot> = {},
): InstallmentSnapshot {
  return {
    installmentGuid: INSTALLMENT,
    installmentNumber: "INST-001",
    storeCode: STORE,
    deviceCode: DEVICE,
    cashierName: "Alice",
    customerName: "Customer",
    customerPhone: "0400000000",
    createdAtIso: NOW,
    totalCents: 10_000,
    downPaymentCents: 2_000,
    paidCents: 2_500,
    balanceCents: 7_500,
    status: "Active",
    updatedAtIso: NOW,
    note: "Private note",
    encryptedSensitiveRevision: INSTALLMENT_SENSITIVE_PAYLOAD_REVISION,
    ...overrides,
  };
}

async function moveRepaymentToBackendPending(
  actionStore: SqliteInstallmentActionStore,
  actionId: string,
): Promise<void> {
  await actionStore.createIfNone(repaymentCandidate({ actionId }));
  await actionStore.transition({
    actionId,
    expectedState: "Created",
    nextState: "ProviderPending",
    terminal: { storeCode: STORE, deviceCode: DEVICE },
  });
  await actionStore.transition({
    actionId,
    expectedState: "ProviderPending",
    nextState: "Approved",
    terminal: { storeCode: STORE, deviceCode: DEVICE },
  });
  await actionStore.transition({
    actionId,
    expectedState: "Approved",
    nextState: "BackendPending",
    terminal: { storeCode: STORE, deviceCode: DEVICE },
  });
}

function paymentSelectionCandidate(
  candidate: PersistedInstallmentAction,
  input: Readonly<{
    method: "cash" | "card" | "voucher";
    selection: Readonly<Record<string, unknown>>;
  }>,
): PersistedInstallmentAction {
  if (
    candidate.action.kind === "cancel-refund" ||
    candidate.command.kind === "cancel-refund"
  ) {
    throw new Error("Test payment candidate must be payable.");
  }
  return Object.freeze({
    ...candidate,
    action: Object.freeze({
      ...candidate.action,
      method: input.method,
    }),
    command: Object.freeze({
      ...candidate.command,
      ...input.selection,
    }),
  }) as unknown as PersistedInstallmentAction;
}

function cancelCandidateWithPaymentSelection(): PersistedInstallmentAction {
  return cancelCandidate({
    cardProvider: null,
    cashTenderedCents: 1,
  });
}

function cancelCandidate(
  commandOverrides: Readonly<Record<string, unknown>> = {},
): PersistedInstallmentAction {
  return Object.freeze({
    action: Object.freeze({
      actionId: ACTION_C,
      idempotencyKey: ACTION_C,
      kind: "cancel-refund",
      installmentGuid: INSTALLMENT,
      paymentGuid: null,
      method: null,
      amountCents: null,
    }),
    command: Object.freeze({
      deviceCode: DEVICE,
      cashierId: "cashier-1",
      cashierName: "Alice",
      kind: "cancel-refund",
      installmentGuid: INSTALLMENT,
      cancelledAtIso: NOW,
      reason: null,
      idempotencyKey: ACTION_C,
      ...commandOverrides,
    }),
    deviceCode: DEVICE,
    intentFingerprint: '{"kind":"cancel-refund"}',
    state: "Created",
    storeCode: STORE,
  }) as unknown as PersistedInstallmentAction;
}

class FixtureDecryptor implements SensitivePayloadEncryptor {
  public constructor(
    private readonly ciphertext: Uint8Array,
    private readonly plaintext: string,
  ) {}

  public encrypt(): Promise<Uint8Array> {
    throw new Error("Fixture encryptor is read-only.");
  }

  public async decrypt(ciphertext: Uint8Array): Promise<string> {
    assert.deepEqual(ciphertext, this.ciphertext);
    return this.plaintext;
  }
}

class RecordingEncryptor implements SensitivePayloadEncryptor {
  public readonly encryptedPlaintexts: string[] = [];
  public failEncryption = false;
  private readonly plaintextById = new Map<number, string>();
  private sequence = 0;

  public async encrypt(plaintext: string): Promise<Uint8Array> {
    if (this.failEncryption) {
      throw new Error("TEST_ENCRYPTION_FAILURE");
    }
    this.encryptedPlaintexts.push(plaintext);
    this.sequence += 1;
    this.plaintextById.set(this.sequence, plaintext);
    return new Uint8Array([this.sequence]);
  }

  public async decrypt(ciphertext: Uint8Array): Promise<string> {
    const id = ciphertext.length === 1 ? ciphertext[0] : undefined;
    const plaintext =
      id === undefined ? undefined : this.plaintextById.get(id);
    if (plaintext === undefined) {
      throw new Error("TEST_INVALID_CIPHERTEXT");
    }
    return plaintext;
  }
}

async function schemaVersion(
  connection: SqliteConnectionPort,
): Promise<number> {
  return Number(
    (
      await connection.getFirst<{ version: unknown }>(
        "SELECT MAX(version) AS version FROM schema_migrations",
      )
    )?.version,
  );
}

async function tableExists(
  connection: SqliteConnectionPort,
  tableName: string,
): Promise<boolean> {
  return (
    Number(
      (
        await connection.getFirst<{ count: unknown }>(
          `SELECT COUNT(*) AS count
           FROM sqlite_master
           WHERE type = 'table' AND name = ?`,
          [tableName],
        )
      )?.count,
    ) === 1
  );
}

async function triggerExists(
  connection: SqliteConnectionPort,
  triggerName: string,
): Promise<boolean> {
  return (
    Number(
      (
        await connection.getFirst<{ count: unknown }>(
          `SELECT COUNT(*) AS count
           FROM sqlite_master
           WHERE type = 'trigger' AND name = ?`,
          [triggerName],
        )
      )?.count,
    ) === 1
  );
}

async function scalar(
  connection: SqliteConnectionPort,
  sql: string,
): Promise<number> {
  return Number(
    (await connection.getFirst<{ count: unknown }>(sql))?.count,
  );
}

class SystemSqliteDriver implements SqliteDriverPort {
  public async open(_databaseName: string): Promise<SqliteConnectionPort> {
    return new SystemSqliteConnection(new DatabaseSync(":memory:"));
  }
}

class SystemSqliteConnection implements SqliteConnectionPort {
  public transactionCount = 0;
  private transactionTail: Promise<void> = Promise.resolve();

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
      .run(...parameters.map(toSqliteValue));
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
      (this.database
        .prepare(sql)
        .get(...parameters.map(toSqliteValue)) as T | undefined) ?? null
    );
  }

  public async getAll<T extends object>(
    sql: string,
    parameters: readonly SqlValue[] = [],
  ): Promise<readonly T[]> {
    return this.database
      .prepare(sql)
      .all(...parameters.map(toSqliteValue)) as T[];
  }

  public withExclusiveTransaction<T>(
    operation: (transaction: SqliteConnectionPort) => Promise<T>,
  ): Promise<T> {
    const execute = async (): Promise<T> => {
      this.transactionCount += 1;
      this.database.exec("BEGIN IMMEDIATE;");
      try {
        const result = await operation(
          new TransactionConnection(this.database),
        );
        this.database.exec("COMMIT;");
        return result;
      } catch (error) {
        this.database.exec("ROLLBACK;");
        throw error;
      }
    };
    const result = this.transactionTail.then(execute, execute);
    this.transactionTail = result.then(
      () => undefined,
      () => undefined,
    );
    return result;
  }

  public async close(): Promise<void> {
    this.database.close();
  }
}

class TransactionConnection extends SystemSqliteConnection {
  public override withExclusiveTransaction<T>(): Promise<T> {
    throw new Error("Nested transaction is not supported.");
  }

  public override async close(): Promise<void> {
    throw new Error("Transaction cannot close the database.");
  }
}

function toSqliteValue(value: SqlValue): SQLInputValue {
  return value;
}

async function withMigratedDatabase(
  operation: (connection: SystemSqliteConnection) => Promise<void>,
): Promise<void> {
  await withDatabase(async (connection) => {
    await applyMigrations(connection, () => NOW);
    await operation(connection);
  });
}

async function withDatabase(
  operation: (connection: SystemSqliteConnection) => Promise<void>,
): Promise<void> {
  const connection = new SystemSqliteConnection(
    new DatabaseSync(":memory:"),
  );
  try {
    await operation(connection);
  } finally {
    await connection.close();
  }
}
