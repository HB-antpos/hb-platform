import assert from "node:assert/strict";
import { mkdtempSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { DatabaseSync, type SQLInputValue } from "node:sqlite";
import test from "node:test";

import type { SqliteConnectionPort, SqlRunResult, SqlValue } from "@hb/pos-db/core/db/types";
import { HbposOrderSyncAdapter } from "@hb/pos-sync/core/sync/hbpos-sync-adapters";

import type {
  HbposTransport,
  HbposTransportRequest,
  HbposTransportResponse,
} from "../api/hbpos-api";
import type {
  CartSnapshot,
  PricingCartStateSnapshot,
  RecallActiveBinding,
} from "../contracts";
import { ActivePricingCartPaymentLeaseCoordinator } from "../runtime/payment-cart-lease-coordinator";

import { applyMigrations, POS_DATABASE_MIGRATIONS } from "./migrations";
import { SqliteManualPaymentOrderCommitter } from "./sqlite-manual-payment-committer";
import {
  SqliteMixedPaymentTenderStore,
  type MixedCashOrderCompletionPlan,
} from "./sqlite-mixed-payment-tender-store";
import { SqliteOrderSyncMaterialResolver } from "./sqlite-order-sync-material";
import { SqlitePaymentDraftRecoveryStore } from "./sqlite-payment-draft-recovery-store";
import { SqlitePaymentRecoveryCenterStore } from "./sqlite-payment-recovery-center-store";
import { SqlitePaymentSupervisorAckQueue } from "@hb/pos-db/core/db/sqlite-payment-supervisor-acknowledgement-queue";
import { SqliteReturnSupervisorResolutionStore } from "@hb/pos-db/core/db/sqlite-return-supervisor-resolution-store";
import { DurableReturnExecutionOrchestrator } from "@hb/pos-domain/features/returns/adapters/durable-return-execution-orchestrator";
import {
  createSqliteRepositories,
  type SensitivePayloadEncryptor,
} from "./sqlite-repositories";
import { SqliteReturnApiAttemptStore } from "./sqlite-return-api-attempt-store";
import { ProductionReturnOnlineRefundRouter } from "../runtime/production-return-online-refund-router";
import { SqliteReturnCapacityVault } from "./sqlite-return-capacity-vault";
import { SqliteReturnExecutionLedger } from "./sqlite-return-execution-ledger";
import { SqliteReturnFulfilmentPlanStore } from "./sqlite-return-fulfilment-plan-store";
import { SqliteVoucherPreparationStore } from "./sqlite-voucher-preparation-store";
import { SqliteVoucherProtectedTokenStore } from "./sqlite-voucher-protected-token-store";

import { PricingCart } from "@/features/sales/domain";
import { ActivePricingCartSession } from "@/features/sales/runtime";

const T0 = "2026-07-28T00:00:00.000Z";
const T1 = "2026-07-28T00:01:00.000Z";
const T2 = "2026-07-28T00:02:00.000Z";

test("真实 SQLite：M44 经 M45 升级 M46 保留 active draft、Linkly ACK 与 Square attempt，重复 apply 幂等", async () => {
  await withDatabase("m44-to-m45-recovery-fixture", async (connection) => {
    await applyMigrations(connection, () => T0, POS_DATABASE_MIGRATIONS.filter((migration) => migration.version <= 44));
    await insertOrder(connection, {
      orderGuid: "m44-linkly-order", sequence: 901, storeCode: "S-M44",
      deviceCode: "D-M44", cashierId: "cashier-m44", amountCents: 900,
      state: "Draft", syncProvenance: TEST_SYNC_PROVENANCE,
    });
    await connection.run(
      "INSERT INTO payment_order_draft_bindings (draft_id, request_fingerprint, pricing_state_json, order_guid, store_code, device_code, state, created_at_iso) VALUES ('m44-draft-linkly', '{}', '{}', 'm44-linkly-order', 'S-M44', 'D-M44', 'Active', ?)",
      [T0],
    );
    await insertAttempt(connection, {
      attemptId: "m44-linkly-attempt", idempotencyKey: "m44-linkly-key",
      orderGuid: "m44-linkly-order", provider: "linkly-cloud",
      operation: "purchase", amountCents: 900, state: "Declined",
    });
    await connection.run(
      "UPDATE payment_attempts SET provider_environment = 'Production', session_id = 'm44-linkly-session' WHERE attempt_id = 'm44-linkly-attempt'",
    );
    await insertOrder(connection, {
      orderGuid: "m44-square-order", sequence: 902, storeCode: "S-M44",
      deviceCode: "D-M44", cashierId: "cashier-m44", amountCents: 700,
      state: "Draft", syncProvenance: TEST_SYNC_PROVENANCE,
    });
    await connection.run(
      "INSERT INTO payment_order_draft_bindings (draft_id, request_fingerprint, pricing_state_json, order_guid, store_code, device_code, state, created_at_iso) VALUES ('m44-draft-square', '{}', '{}', 'm44-square-order', 'S-M44', 'D-M44', 'Active', ?)",
      [T0],
    );
    await insertAttempt(connection, {
      attemptId: "m44-square-attempt", idempotencyKey: "m44-square-key",
      orderGuid: "m44-square-order", provider: "square",
      operation: "purchase", amountCents: 700, state: "Unknown",
    });

    await applyMigrations(connection, () => T1, POS_DATABASE_MIGRATIONS.filter((migration) => migration.version === 45));
    await applyMigrations(connection, () => T2, POS_DATABASE_MIGRATIONS.filter((migration) => migration.version === 45));
    assert.equal(
      Number((await connection.getFirst<{ version: unknown }>(
        "SELECT MAX(version) AS version FROM schema_migrations",
      ))?.version),
      45,
    );
    await applyMigrations(connection, () => T2, POS_DATABASE_MIGRATIONS.filter((migration) => migration.version === 46));
    await applyMigrations(connection, () => T2, POS_DATABASE_MIGRATIONS.filter((migration) => migration.version === 46));
    assert.equal(Number((await connection.getFirst<{ version: unknown }>(
      "SELECT MAX(version) AS version FROM schema_migrations",
    ))?.version), 46);
    assert.deepEqual({
      ...(await connection.getFirst<{ state: string; provider_environment: string; session_id: string }>(
        "SELECT state, provider_environment, session_id FROM payment_attempts WHERE attempt_id = 'm44-linkly-attempt'",
      )),
    }, { state: "Declined", provider_environment: "Production", session_id: "m44-linkly-session" });
    assert.deepEqual({
      ...(await connection.getFirst<{ state: string }>(
        "SELECT state FROM payment_order_draft_bindings WHERE draft_id = 'm44-draft-square'",
      )),
    }, { state: "Active" });
    assert.equal(await scalar(connection,
      "SELECT COUNT(*) AS count FROM sqlite_master WHERE type = 'index' AND name = 'ix_payment_recovery_actions_record_time'",
    ), 1);
    assert.equal(await scalar(connection,
      "SELECT COUNT(*) AS count FROM sqlite_master WHERE type = 'table' AND name = 'payment_recovery_authorizations'",
    ), 1);
    assert.equal(await scalar(connection,
      "SELECT COUNT(*) AS count FROM sqlite_master WHERE type = 'table' AND name = 'payment_recovery_reconciliations'",
    ), 1);
    assert.equal(await scalar(connection,
      "SELECT COUNT(*) AS count FROM sqlite_master WHERE type = 'trigger' AND name = 'trg_payment_recovery_authorizations_immutable'",
    ), 1);
    assert.equal(await scalar(connection,
      "SELECT COUNT(*) AS count FROM sqlite_master WHERE type = 'trigger' AND name = 'trg_payment_recovery_reconciliations_immutable'",
    ), 1);
    assert.equal(await scalar(connection,
      "SELECT COUNT(*) AS count FROM sqlite_master WHERE type = 'trigger' AND name = 'trg_payment_attempts_linkly_ack_lane_insert'",
    ), 1);
  });
});

test("真实 SQLite：移交恢复中心释放购物车门闩但 Linkly ACK 仍占用物理 lane", async () => {
  await withDatabase("recovery-parked-ack", async (connection) => {
    await migrateFresh(connection);
    const drafts = new SqlitePaymentDraftRecoveryStore(connection, sequenceIds("ack-order", "ack-audit"), () => T2);
    const input = draftInput({ draftId: "ack-recovery-draft" });
    const order = await drafts.createOrReuseDraft(input);
    await insertAttempt(connection, { attemptId: "ack-attempt", idempotencyKey: "ack-key", orderGuid: order.orderGuid,
      provider: "linkly-cloud", operation: "purchase", amountCents: input.cart.actualAmount.cents, state: "Declined" });
    await connection.run("UPDATE payment_attempts SET provider_environment = 'production', session_id = 'ack-session' WHERE attempt_id = 'ack-attempt'");
    assert.equal(await drafts.findPendingLinklyAcknowledgement(input.identity), "ack-attempt");
    const center = new SqlitePaymentRecoveryCenterStore(connection, () => "ack-case", () => "ack-park-audit", () => T2);
    const parked = await center.parkExact({ ...input.identity, orderGuid: order.orderGuid, attemptId: "ack-attempt",
      actionId: "ack-park", actor: { cashierId: "C1", cashierName: "Cashier", userGuid: null } });
    assert.equal(await drafts.findPendingLinklyAcknowledgement(input.identity), "ack-attempt");
    assert.equal(await drafts.findBlockingRecovery(input.identity), null);
    await center.resumeExact(input.identity, parked.recordId);
    assert.equal(await drafts.findPendingLinklyAcknowledgement(input.identity), "ack-attempt");
    assert.equal((await connection.getFirst<{ count: number }>("SELECT COUNT(*) AS count FROM payment_attempts"))?.count, 1);
  });
});

test("真实 SQLite：Linkly ACK lane 对 Approved、Declined、Cancelled 均阻挡第二笔", async () => {
  for (const state of ["Approved", "Declined", "Cancelled"] as const) {
    await withDatabase(`recovery-ack-${state}`, async (connection) => {
      await migrateFresh(connection);
      await insertOrder(connection, {
        orderGuid: `ack-${state}-old`, sequence: 910, storeCode: "S-ACK",
        deviceCode: "D-ACK", cashierId: "cashier", amountCents: 900,
        state: "Draft", syncProvenance: TEST_SYNC_PROVENANCE,
      });
      await connection.run(
        "INSERT INTO payment_order_draft_bindings (draft_id, request_fingerprint, pricing_state_json, order_guid, store_code, device_code, state, created_at_iso) VALUES (?, '{}', '{}', ?, 'S-ACK', 'D-ACK', 'Active', ?)",
        [`ack-draft-${state}`, `ack-${state}-old`, T0],
      );
      await insertAttempt(connection, {
        attemptId: `ack-${state}-attempt`, idempotencyKey: `ack-${state}-key`,
        orderGuid: `ack-${state}-old`, provider: "linkly-cloud",
        operation: "purchase", amountCents: 900, state,
      });
      await connection.run(
        "UPDATE payment_attempts SET provider_environment = 'Production', session_id = ? WHERE attempt_id = ?",
        [`ack-session-${state}`, `ack-${state}-attempt`],
      );
      await insertOrder(connection, {
        orderGuid: `ack-${state}-new`, sequence: 911, storeCode: "S-ACK",
        deviceCode: "D-ACK", cashierId: "cashier", amountCents: 900,
        state: "Draft", syncProvenance: TEST_SYNC_PROVENANCE,
      });
      await connection.run(
        "INSERT INTO payment_order_draft_bindings (draft_id, request_fingerprint, pricing_state_json, order_guid, store_code, device_code, state, created_at_iso) VALUES (?, '{}', '{}', ?, 'S-ACK', 'D-ACK', 'Active', ?)",
        [`ack-draft-new-${state}`, `ack-${state}-new`, T0],
      );
      await assert.rejects(
        () => insertAttempt(connection, {
          attemptId: `ack-${state}-blocked`, idempotencyKey: `ack-${state}-blocked-key`,
          orderGuid: `ack-${state}-new`, provider: "linkly-cloud",
          operation: "purchase", amountCents: 900, state: "Created",
        }),
        /PAYMENT_LINKLY_ACK_PENDING|PAYMENT_TERMINAL_BLOCKING_ATTEMPT_EXISTS/,
      );
    });
  }
});
const TEST_SYNC_PROVENANCE = {
  referenceCode: null,
  priceSource: 0,
} as const;

type TestSyncProvenance = Readonly<{
  referenceCode: string | null;
  priceSource: number;
}>;

class SystemSqliteConnection implements SqliteConnectionPort {
  private readonly database: DatabaseSync;
  private readonly queue = new AsyncSerialQueue();

  public constructor(databasePath: string) {
    this.database = new DatabaseSync(databasePath);
    this.database.exec("PRAGMA foreign_keys = ON");
  }

  public exec(sql: string): Promise<void> {
    return this.queue.enqueue(async () => this.database.exec(sql));
  }

  public run(
    sql: string,
    parameters: readonly SqlValue[] = [],
  ): Promise<SqlRunResult> {
    return this.queue.enqueue(async () =>
      runStatement(this.database, sql, parameters));
  }

  public getFirst<T extends object>(
    sql: string,
    parameters: readonly SqlValue[] = [],
  ): Promise<T | null> {
    return this.queue.enqueue(async () =>
      getFirst<T>(this.database, sql, parameters));
  }

  public getAll<T extends object>(
    sql: string,
    parameters: readonly SqlValue[] = [],
  ): Promise<readonly T[]> {
    return this.queue.enqueue(async () =>
      getAll<T>(this.database, sql, parameters));
  }

  public withExclusiveTransaction<T>(
    operation: (transaction: SqliteConnectionPort) => Promise<T>,
  ): Promise<T> {
    return this.queue.enqueue(async () => {
      this.database.exec("BEGIN IMMEDIATE");
      const transaction = new TransactionConnection(this.database);
      try {
        const result = await operation(transaction);
        this.database.exec("COMMIT");
        return result;
      } catch (error) {
        this.database.exec("ROLLBACK");
        throw error;
      }
    });
  }

  public close(): Promise<void> {
    return this.queue.enqueue(async () => this.database.close());
  }
}

class TransactionConnection implements SqliteConnectionPort {
  public constructor(private readonly database: DatabaseSync) {}
  public async exec(sql: string): Promise<void> { this.database.exec(sql); }
  public async run(
    sql: string,
    parameters: readonly SqlValue[] = [],
  ): Promise<SqlRunResult> {
    return runStatement(this.database, sql, parameters);
  }
  public async getFirst<T extends object>(
    sql: string,
    parameters: readonly SqlValue[] = [],
  ): Promise<T | null> {
    return getFirst<T>(this.database, sql, parameters);
  }
  public async getAll<T extends object>(
    sql: string,
    parameters: readonly SqlValue[] = [],
  ): Promise<readonly T[]> {
    return getAll<T>(this.database, sql, parameters);
  }
  public withExclusiveTransaction<T>(): Promise<T> {
    return Promise.reject(new Error("Nested test transaction."));
  }
  public close(): Promise<void> {
    return Promise.reject(new Error("Transaction cannot close database."));
  }
}

class AsyncSerialQueue {
  private tail: Promise<void> = Promise.resolve();
  public enqueue<T>(operation: () => Promise<T>): Promise<T> {
    const result = this.tail.then(operation, operation);
    this.tail = result.then(() => undefined, () => undefined);
    return result;
  }
}

const encryptor: SensitivePayloadEncryptor = {
  async encrypt(plaintext) {
    return Uint8Array.from(
      new TextEncoder().encode(plaintext),
      (value) => value ^ 0xa5,
    );
  },
  async decrypt(ciphertext) {
    return new TextDecoder().decode(
      Uint8Array.from(ciphertext, (value) => value ^ 0xa5),
    );
  },
};

test("真实 SQLite：M12 原子升级 M17，失败不推进版本且敏感字段不出现在明文 schema", async () => {
  await withDatabase("migration", async (connection) => {
    await applyMigrations(
      connection,
      () => T0,
      POS_DATABASE_MIGRATIONS.filter((migration) => migration.version <= 12),
    );
    assert.equal(
      Number(
        (await connection.getFirst<{ version: unknown }>(
          "SELECT MAX(version) AS version FROM schema_migrations",
        ))?.version,
      ),
      12,
    );
    await applyMigrations(
      connection,
      () => T1,
      POS_DATABASE_MIGRATIONS.filter((migration) => migration.version <= 17),
    );
    const versions = await connection.getAll<{ version: unknown }>(
      "SELECT version FROM schema_migrations ORDER BY version",
    );
    assert.equal(Number(versions.at(-1)?.version), 17);
    const cashActionColumns = await connection.getAll<{
      name: unknown;
      pk: unknown;
    }>("PRAGMA table_info('mixed_cash_tender_actions')");
    assert.deepEqual(
      cashActionColumns
        .filter((column) => Number(column.pk) > 0)
        .sort((left, right) => Number(left.pk) - Number(right.pk))
        .map((column) => String(column.name)),
      ["order_guid", "action_id"],
    );
    assert.equal(
      await scalar(
        connection,
        `SELECT COUNT(*) AS count
         FROM pragma_table_info('payment_order_draft_bindings')
         WHERE name = 'pricing_state_json' AND "notnull" = 1`,
      ),
      1,
    );
    assert.equal(
      await scalar(
        connection,
        `SELECT COUNT(*) AS count
         FROM pragma_table_info('payment_order_draft_bindings')
         WHERE name IN (
           'close_action_id', 'close_attempt_id',
           'close_audit_event_id', 'closed_at_iso'
         )`,
      ),
      4,
    );
    const voucherColumns = await connection.getAll<{ name: unknown }>(
      "PRAGMA table_info('voucher_protected_attempt_states')",
    );
    const preparedColumns = await connection.getAll<{ name: unknown }>(
      "PRAGMA table_info('voucher_prepared_contexts')",
    );
    const names = [...voucherColumns, ...preparedColumns].map((row) =>
      String(row.name).toLowerCase());
    assert.equal(names.some((name) => name.includes("voucher_code")), false);
    assert.equal(names.some((name) => name.includes("reservation_token")), false);
    assert.equal(names.some((name) => name.includes("refund_reason")), false);

    const failing = {
      version: 18,
      name: "M18_failure",
      sql: "CREATE TABLE rolled_back_test (id TEXT); INVALID SQL;",
    };
    await assert.rejects(
      () => applyMigrations(connection, () => T2, [failing]),
      /near "INVALID"/,
    );
    assert.equal(
      await scalar(
        connection,
        "SELECT COUNT(*) AS count FROM schema_migrations WHERE version = 18",
      ),
      0,
    );
  });
});

test("真实 SQLite：M15 保留历史 NULL/NULL，强制新行整数枚举来源且冻结来源对", async () => {
  await withDatabase("m15-line-provenance", async (connection) => {
    await applyMigrations(
      connection,
      () => T0,
      POS_DATABASE_MIGRATIONS.filter(
        (migration) => migration.version <= 14,
      ),
    );
    await insertOrder(connection, {
      orderGuid: "m15-legacy-order",
      sequence: 1,
      storeCode: "S-M15",
      deviceCode: "D-M15",
      cashierId: "C-M15",
      amountCents: 500,
      state: "PendingSync",
      syncProvenance: null,
    });
    await applyMigrations(connection, () => T1);
    const legacy = await connection.getFirst<{
      reference_code: unknown;
      sync_price_source: unknown;
    }>(
      `SELECT reference_code, sync_price_source
       FROM local_order_lines
       WHERE order_guid = 'm15-legacy-order'`,
    );
    assert.deepEqual(
      legacy ? { ...legacy } : null,
      { reference_code: null, sync_price_source: null },
    );

    const insertLine = (
      suffix: string,
      lineSequence: number,
      referenceCode: string | null,
      syncPriceSource: number | null,
    ) =>
      connection.run(
        `INSERT INTO local_order_lines (
          line_id, order_guid, line_sequence, product_code, item_number,
          lookup_code, display_name, quantity, unit_price_cents,
          discount_cents, actual_amount_cents, price_source, line_kind,
          return_source_key, original_order_guid, original_order_detail_guid,
          reference_code, sync_price_source
        ) VALUES (?, 'm15-legacy-order', ?, 'P', NULL, ?, 'Product', '1',
          100, 0, 100, 'catalog', 'sale', NULL, NULL, NULL, ?, ?)`,
        [
          `line-m15-${suffix}`,
          lineSequence,
          `P-${suffix}`,
          referenceCode,
          syncPriceSource,
        ],
      );

    const invalidInserts = await Promise.allSettled(
      [
        { suffix: "missing", source: null },
        { suffix: "negative", source: -1 },
        { suffix: "fractional", source: 2.5 },
        { suffix: "overflow", source: 5 },
      ].map((input, index) =>
        insertLine(input.suffix, index + 2, null, input.source)),
    );
    const validInserts = await Promise.allSettled(
      [0, 1, 2, 3, 4].map((source, index) =>
        insertLine(`source-${source}`, index + 6, null, source)),
    );
    const immutableUpdates = await Promise.allSettled([
      connection.run(
        `UPDATE local_order_lines
         SET reference_code = 'REF-M15-CHANGED'
         WHERE line_id = 'line-m15-source-2'`,
      ),
      connection.run(
        `UPDATE local_order_lines
         SET sync_price_source = 3
         WHERE line_id = 'line-m15-source-4'`,
      ),
      connection.run(
        `UPDATE local_order_lines
         SET reference_code = 'REF-M15-LEGACY', sync_price_source = 1
         WHERE line_id = 'line-m15-legacy-order'`,
      ),
    ]);

    assert.deepEqual(
      {
        invalidInserts: invalidInserts.map((result) => result.status),
        validInserts: validInserts.map((result) => result.status),
        immutableUpdates: immutableUpdates.map((result) => result.status),
      },
      {
        invalidInserts: ["rejected", "rejected", "rejected", "rejected"],
        validInserts: ["fulfilled", "fulfilled", "fulfilled", "fulfilled", "fulfilled"],
        immutableUpdates: ["rejected", "rejected", "rejected"],
      },
    );
    invalidInserts.forEach((result, index) => {
      assert.equal(result.status, "rejected");
      if (result.status !== "rejected") return;
      assert.match(
        String(result.reason),
        index === 0
          ? /ORDER_LINE_SYNC_PROVENANCE_INCOMPLETE/
          : /CHECK constraint failed/,
      );
    });
    immutableUpdates.forEach((result) => {
      assert.equal(result.status, "rejected");
      if (result.status !== "rejected") return;
      assert.match(
        String(result.reason),
        /ORDER_LINE_SYNC_PROVENANCE_IMMUTABLE/,
      );
    });
    const legacyAfterRejectedUpdate = await connection.getFirst<{
      reference_code: unknown;
      sync_price_source: unknown;
    }>(
      `SELECT reference_code, sync_price_source
       FROM local_order_lines
       WHERE line_id = 'line-m15-legacy-order'`,
    );
    assert.deepEqual(
      legacyAfterRejectedUpdate ? { ...legacyAfterRejectedUpdate } : null,
      { reference_code: null, sync_price_source: null },
    );
    const validAfterRejectedUpdate = await connection.getFirst<{
      reference_code: unknown;
      sync_price_source: unknown;
    }>(
      `SELECT reference_code, sync_price_source
       FROM local_order_lines
       WHERE line_id = 'line-m15-source-2'`,
    );
    assert.deepEqual(
      validAfterRejectedUpdate ? { ...validAfterRejectedUpdate } : null,
      { reference_code: null, sync_price_source: 2 },
    );
    assert.equal(
      await scalar(
        connection,
        `SELECT COUNT(*) AS count
         FROM schema_migrations
         WHERE version = 15`,
      ),
      1,
    );
    await applyMigrations(connection, () => T2);
    assert.equal(
      await scalar(
        connection,
        `SELECT COUNT(*) AS count
         FROM schema_migrations
         WHERE version = 15`,
      ),
      1,
    );
    const foreignKeys = await connection.getAll<{ table: unknown }>(
      "PRAGMA foreign_key_list('mixed_cash_tender_actions')",
    );
    assert.deepEqual(
      new Set(foreignKeys.map((row) => String(row.table))),
      new Set(["local_orders", "order_tenders", "audit_events"]),
    );
    const indexes = await connection.getAll<{ unique: unknown }>(
      "PRAGMA index_list('mixed_cash_tender_actions')",
    );
    assert.ok(
      indexes.filter((row) => Number(row.unique) === 1).length >= 3,
      "M39 必须保留主键及 tender/audit 的唯一索引语义",
    );
    await assert.rejects(
      () =>
        connection.run(
          `UPDATE local_order_lines
           SET reference_code = 'REF-M15-RETRY'
           WHERE line_id = 'line-m15-source-2'`,
        ),
      /ORDER_LINE_SYNC_PROVENANCE_IMMUTABLE/,
    );
  });
});

test("真实 SQLite：M13 未物化和已物化计划原子升级 M14，历史一律冻结为 refund-receipt", async () => {
  await withDatabase("return-fulfilment-m14-upgrade", async (connection) => {
    await applyMigrations(
      connection,
      () => T0,
      POS_DATABASE_MIGRATIONS.filter((migration) => migration.version <= 13),
    );
    await insertM13ReturnFulfilmentPlan(connection, {
      suffix: "pending",
      sequence: 201,
      materializedAtIso: null,
    });
    await insertM13ReturnFulfilmentPlan(connection, {
      suffix: "materialized",
      sequence: 202,
      materializedAtIso: T1,
    });

    const m14 = POS_DATABASE_MIGRATIONS.find(
      (migration) => migration.version === 14,
    );
    assert.ok(m14);
    const failingM14 = {
      ...m14,
      sql: `${m14.sql}\nINVALID SQL;`,
    };
    await assert.rejects(
      () => applyMigrations(connection, () => T2, [failingM14]),
      /near "INVALID"/,
    );
    assert.equal(
      await scalar(
        connection,
        "SELECT COUNT(*) AS count FROM schema_migrations WHERE version = 14",
      ),
      0,
    );
    assert.equal(
      await scalar(
        connection,
        `SELECT COUNT(*) AS count
         FROM pragma_table_info('return_fulfilment_plans')
         WHERE name = 'receipt_kind'`,
      ),
      0,
    );

    await applyMigrations(connection, () => T2);
    const migratedPlans = await connection.getAll<{
        action_id: unknown;
        receipt_kind: unknown;
        print_job_id: unknown;
        materialized_at_iso: unknown;
      }>(
        `SELECT action_id, receipt_kind, print_job_id, materialized_at_iso
         FROM return_fulfilment_plans
         ORDER BY action_id`,
      );
    assert.deepEqual(
      migratedPlans.map((row) => ({
        action_id: row.action_id,
        receipt_kind: row.receipt_kind,
        print_job_id: row.print_job_id,
        materialized_at_iso: row.materialized_at_iso,
      })),
      [
        {
          action_id: "m14-action-materialized",
          receipt_kind: "refund-receipt",
          print_job_id: "m13-print-materialized",
          materialized_at_iso: T1,
        },
        {
          action_id: "m14-action-pending",
          receipt_kind: "refund-receipt",
          print_job_id: "m13-print-pending",
          materialized_at_iso: null,
        },
      ],
    );
    assert.equal(
      await scalar(
        connection,
        "SELECT COUNT(*) AS count FROM schema_migrations WHERE version = 14",
      ),
      1,
    );
    await applyMigrations(connection, () => T2);
    assert.equal(
      await scalar(
        connection,
        "SELECT COUNT(*) AS count FROM schema_migrations WHERE version = 14",
      ),
      1,
    );
    await assert.rejects(
      () =>
        connection.run(
          `UPDATE return_fulfilment_plans
           SET receipt_kind = 'refund-voucher'
           WHERE action_id = 'm14-action-pending'`,
        ),
      /RETURN_FULFILMENT_PLAN_IDENTITY_IMMUTABLE/,
    );
    await assert.rejects(
      () =>
        connection.run(
          `UPDATE return_fulfilment_plans
           SET materialized_at_iso = ?
           WHERE action_id = 'm14-action-materialized'`,
          [T2],
        ),
      /RETURN_FULFILMENT_MATERIALIZATION_IMMUTABLE/,
    );
    await assert.rejects(
      () =>
        connection.run(
          `DELETE FROM return_fulfilment_plans
           WHERE action_id = 'm14-action-pending'`,
        ),
      /RETURN_FULFILMENT_PLAN_DELETE_FORBIDDEN/,
    );
  });
});

test("真实 SQLite：cash-only 退货只物化无 print link 的钱箱事件，重放不打印也不加密", async () => {
  await withDatabase("return-fulfilment-drawer-only", async (connection) => {
    await migrateFresh(connection);
    await insertM14ReturnFulfilmentPlan(connection, {
      suffix: "drawer-only",
      sequence: 211,
      receiptKind: "none",
      printJobId: null,
      drawerEventId: "m14-drawer-drawer-only",
    });
    let encryptCalls = 0;
    const plans = new SqliteReturnFulfilmentPlanStore(
      connection,
      {
        async encrypt() {
          encryptCalls += 1;
          throw new Error("Drawer-only plan must not encrypt a receipt.");
        },
        async decrypt() {
          throw new Error("Drawer-only plan must not decrypt a receipt.");
        },
      },
      () => T2,
    );
    const input = {
      actionId: "m14-action-drawer-only",
      expectedReturnOrderGuid: "m14-order-drawer-only",
      expectedPrintJobId: null,
      expectedDrawerEventId: "m14-drawer-drawer-only",
      printerId: "m14-printer",
      receiptBytes: null,
      drawerReason: "cash-return",
    } as unknown as Parameters<typeof plans.materialize>[0];

    await assert.rejects(
      () =>
        plans.materialize({
          ...input,
          expectedPrintJobId: "must-not-encrypt",
          receiptBytes: new Uint8Array([0x00]),
        }),
      /plan identity has diverged/,
    );
    assert.equal(encryptCalls, 0);
    const first = await plans.materialize(input);
    const replay = await plans.materialize(input);
    assert.equal(first.receiptKind, "none");
    assert.equal(first.printJobId, null);
    assert.equal(first.printReceipt, false);
    assert.equal(first.materializedAtIso, T2);
    assert.deepEqual(replay, first);
    assert.equal(encryptCalls, 0);
    assert.equal(
      await scalar(
        connection,
        `SELECT COUNT(*) AS count
         FROM print_jobs
         WHERE order_guid = 'm14-order-drawer-only'`,
      ),
      0,
    );
    const drawer = await connection.getFirst<{
        print_job_id: unknown;
        state: unknown;
        reason: unknown;
      }>(
        `SELECT print_job_id, state, reason
         FROM drawer_events
         WHERE event_id = 'm14-drawer-drawer-only'`,
      );
    assert.deepEqual(
      drawer && {
        print_job_id: drawer.print_job_id,
        state: drawer.state,
        reason: drawer.reason,
      },
      {
        print_job_id: null,
        state: "Required",
        reason: "cash-return",
      },
    );
    assert.equal((await plans.listPending()).length, 0);
  });
});

test("真实 SQLite：打印加钱箱计划保持同一 print link，非法 receipt kind/print id 组合被拒绝", async () => {
  await withDatabase("return-fulfilment-print-drawer", async (connection) => {
    await migrateFresh(connection);
    await insertM14ReturnFulfilmentPlan(connection, {
      suffix: "print-drawer",
      sequence: 221,
      receiptKind: "refund-receipt",
      printJobId: "m14-print-print-drawer",
      drawerEventId: "m14-drawer-print-drawer",
    });
    const plans = new SqliteReturnFulfilmentPlanStore(
      connection,
      encryptor,
      () => T2,
    );
    const input = {
      actionId: "m14-action-print-drawer",
      expectedReturnOrderGuid: "m14-order-print-drawer",
      expectedPrintJobId: "m14-print-print-drawer",
      expectedDrawerEventId: "m14-drawer-print-drawer",
      printerId: "m14-printer",
      receiptBytes: new Uint8Array([0x1b, 0x40, 0x0a]),
      drawerReason: "cash-return",
    };
    const first = await plans.materialize(input);
    const replay = await plans.materialize(input);
    assert.equal(first.receiptKind, "refund-receipt");
    assert.equal(first.printReceipt, true);
    assert.deepEqual(replay, first);
    assert.equal(
      await scalar(
        connection,
        `SELECT COUNT(*) AS count
         FROM print_jobs
         WHERE job_id = 'm14-print-print-drawer' AND state = 'Queued'`,
      ),
      1,
    );
    assert.equal(
      await scalar(
        connection,
        `SELECT COUNT(*) AS count
         FROM drawer_events
         WHERE event_id = 'm14-drawer-print-drawer'
           AND print_job_id = 'm14-print-print-drawer'
           AND state = 'Required'`,
      ),
      1,
    );

    for (const invalid of [
      {
        suffix: "none-with-print",
        receiptKind: "none",
        printJobId: "invalid-print",
        printReceipt: 0,
      },
      {
        suffix: "receipt-without-print",
        receiptKind: "refund-receipt",
        printJobId: null,
        printReceipt: 1,
      },
      {
        suffix: "voucher-with-false-print",
        receiptKind: "refund-voucher",
        printJobId: "invalid-voucher-print",
        printReceipt: 0,
      },
      {
        suffix: "unknown-kind",
        receiptKind: "unknown",
        printJobId: "invalid-kind-print",
        printReceipt: 1,
      },
    ] as const) {
      await insertReturnActionAndOrder(
        connection,
        invalid.suffix,
        230 + invalid.suffix.length,
        TEST_SYNC_PROVENANCE,
      );
      await assert.rejects(
        () =>
          connection.run(
            `INSERT INTO return_fulfilment_plans (
              action_id, return_order_guid, print_job_id, drawer_event_id,
              receipt_kind, print_receipt, drawer_required,
              materialized_at_iso, created_at_iso
            ) VALUES (?, ?, ?, NULL, ?, ?, 0, NULL, ?)`,
            [
              `m14-action-${invalid.suffix}`,
              `m14-order-${invalid.suffix}`,
              invalid.printJobId,
              invalid.receiptKind,
              invalid.printReceipt,
              T0,
            ],
          ),
        /CHECK constraint failed/,
      );
    }
  });
});

test("真实 SQLite：M27 只恢复旧 payment 行号触发的同步 Guid 拒绝", async () => {
  await withDatabase("legacy-payment-line-recovery", async (connection) => {
    await applyMigrations(
      connection,
      () => T0,
      POS_DATABASE_MIGRATIONS.filter((migration) => migration.version <= 26),
    );
    const eligibleOrderGuid = "018f1b9b-47c5-7c1b-9f8e-39c5cb3b9d31";
    const unrelatedOrderGuid = "018f1b9b-47c5-7c1b-9f8e-39c5cb3b9d32";
    const returnOrderGuid = "018f1b9b-47c5-7c1b-9f8e-39c5cb3b9d33";
    const legacyOrderLineId = `payment:${eligibleOrderGuid}:1`;
    for (const [index, orderGuid] of [
      eligibleOrderGuid,
      unrelatedOrderGuid,
    ].entries()) {
      await connection.run(
        `INSERT INTO local_orders (
          order_guid, local_sequence, store_code, device_code,
          cashier_id, cashier_name, sold_at_iso, state,
          total_cents, discount_cents, actual_amount_cents,
          original_order_guid, created_at_iso, updated_at_iso
        ) VALUES (?, ?, '1003', 'IPAD_1', 'cashier-1', 'Alice', ?,
          'Rejected', 100, 0, 100, NULL, ?, ?)`,
        [orderGuid, index + 1, T0, T0, T1],
      );
      const lineId =
        orderGuid === eligibleOrderGuid
          ? legacyOrderLineId
          : `corrupt:${orderGuid}:1`;
      await connection.run(
        `INSERT INTO local_order_lines (
          line_id, order_guid, line_sequence, product_code, item_number,
          lookup_code, display_name, quantity, unit_price_cents,
          discount_cents, actual_amount_cents, price_source, line_kind,
          return_source_key, original_order_guid, original_order_detail_guid,
          reference_code, sync_price_source
        ) VALUES (?, ?, 1, 'P1', NULL, 'P1', 'Product', '1', 100, 0, 100,
          'catalog', 'sale', NULL, NULL, NULL, NULL, 0)`,
        [lineId, orderGuid],
      );
      await connection.run(
        `INSERT INTO payment_order_draft_line_bindings (
          order_guid, cart_line_id, order_line_id, line_sequence
        ) VALUES (?, ?, ?, 1)`,
        [orderGuid, `cart-line-${index + 1}`, lineId],
      );
      await insertTender(
        connection,
        `00000000-0000-4000-8000-${String(index + 1).padStart(12, "0")}`,
        orderGuid,
        "cash",
        100,
      );
      await connection.run(
        `INSERT INTO outbox_messages (
          message_id, aggregate_id, kind, payload_json, state,
          attempt_count, next_attempt_at_iso, lease_id, lease_expires_at_iso,
          last_error_code, created_at_iso, updated_at_iso
        ) VALUES (?, ?, 'order-sync', ?, 'rejected', 1, ?, NULL, NULL,
          'ORDER_LINE_INVALID', ?, ?)`,
        [
          `outbox-${index + 1}`,
          orderGuid,
          JSON.stringify({ orderGuid }),
          T1,
          T0,
          T1,
        ],
      );
    }
    await connection.run(
      `INSERT INTO local_orders (
        order_guid, local_sequence, store_code, device_code,
        cashier_id, cashier_name, sold_at_iso, state,
        total_cents, discount_cents, actual_amount_cents,
        original_order_guid, created_at_iso, updated_at_iso
      ) VALUES (?, 3, '1003', 'IPAD_1', 'cashier-1', 'Alice', ?,
        'Rejected', -100, 0, -100, ?, ?, ?)`,
      [returnOrderGuid, T0, eligibleOrderGuid, T0, T1],
    );
    await connection.run(
      `INSERT INTO local_order_lines (
        line_id, order_guid, line_sequence, product_code, item_number,
        lookup_code, display_name, quantity, unit_price_cents,
        discount_cents, actual_amount_cents, price_source, line_kind,
        return_source_key, original_order_guid, original_order_detail_guid,
        reference_code, sync_price_source
      ) VALUES (
        '00000000-0000-4000-8000-000000000099', ?, 1, 'P1', NULL,
        'P1', 'Product return', '1', 100, 0, -100, 'catalog', 'return',
        'return-source-1', ?, ?, NULL, 0
      )`,
      [returnOrderGuid, eligibleOrderGuid, legacyOrderLineId],
    );
    await insertTender(
      connection,
      "00000000-0000-4000-8000-000000000098",
      returnOrderGuid,
      "cash",
      -100,
    );
    await connection.run(
      `INSERT INTO outbox_messages (
        message_id, aggregate_id, kind, payload_json, state,
        attempt_count, next_attempt_at_iso, lease_id, lease_expires_at_iso,
        last_error_code, created_at_iso, updated_at_iso
      ) VALUES ('outbox-return', ?, 'order-sync', ?, 'rejected', 1, ?,
        NULL, NULL, 'ORDER_RETURN_REFERENCE_INVALID', ?, ?)`,
      [
        returnOrderGuid,
        JSON.stringify({ orderGuid: returnOrderGuid }),
        T1,
        T0,
        T1,
      ],
    );

    await applyMigrations(
      connection,
      () => T2,
      POS_DATABASE_MIGRATIONS.filter((migration) => migration.version > 26),
    );

    assert.deepEqual(
      (
        await connection.getAll<{
          order_guid: unknown;
          order_state: unknown;
          outbox_state: unknown;
          last_error_code: unknown;
          attempt_count: unknown;
        }>(
          `SELECT o.order_guid, o.state AS order_state,
            ob.state AS outbox_state, ob.last_error_code, ob.attempt_count
           FROM local_orders o
           INNER JOIN outbox_messages ob
             ON ob.aggregate_id = o.order_guid
            AND ob.kind = 'order-sync'
           ORDER BY o.local_sequence`,
        )
      ).map((row) => ({ ...row })),
      [
        {
          order_guid: eligibleOrderGuid,
          order_state: "PendingSync",
          outbox_state: "pending",
          last_error_code: null,
          attempt_count: 1,
        },
        {
          order_guid: unrelatedOrderGuid,
          order_state: "Rejected",
          outbox_state: "rejected",
          last_error_code: "ORDER_LINE_INVALID",
          attempt_count: 1,
        },
        {
          order_guid: returnOrderGuid,
          order_state: "PendingSync",
          outbox_state: "pending",
          last_error_code: null,
          attempt_count: 1,
        },
      ],
    );

    const repositories = createSqliteRepositories(connection, {
      nowIso: () => T2,
      createLeaseId: () => "legacy-payment-sync-lease",
      encryptor,
    });
    const calls: HbposTransportRequest[] = [];
    const adapter = new HbposOrderSyncAdapter(
      {
        async request<T>(
          request: HbposTransportRequest,
        ): Promise<HbposTransportResponse<T>> {
          calls.push(request);
          const requestOrderGuid = (request.data as { orderGuid: string })
            .orderGuid;
          return {
            status: 200,
            data: {
              success: true,
              data: {
                orderGuid: requestOrderGuid,
                accepted: true,
                alreadySynced: false,
              },
            } as T,
          };
        },
      },
      repositories.orders,
    );
    assert.deepEqual(
      await adapter.sync(
        eligibleOrderGuid,
        JSON.stringify({ orderGuid: eligibleOrderGuid }),
      ),
      { kind: "synced", alreadySynced: false },
    );
    const request = calls[0]?.data as {
      lines: readonly { orderLineGuid: string }[];
    };
    assert.match(
      request.lines[0]?.orderLineGuid ?? "",
      /^[0-9a-f]{8}-[0-9a-f]{4}-[1-8][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i,
    );
    assert.equal(
      request.lines[0]?.orderLineGuid,
      "018f1b9b-47c5-7c1b-9f8e-39c5cb3b9d30",
    );
    assert.deepEqual(
      await adapter.sync(
        returnOrderGuid,
        JSON.stringify({ orderGuid: returnOrderGuid }),
      ),
      { kind: "synced", alreadySynced: false },
    );
    const returnRequest = calls[1]?.data as {
      lines: readonly {
        originalOrderGuid: string | null;
        originalOrderDetailGuid: string | null;
      }[];
    };
    assert.equal(
      returnRequest.lines[0]?.originalOrderGuid,
      eligibleOrderGuid,
    );
    assert.equal(
      returnRequest.lines[0]?.originalOrderDetailGuid,
      request.lines[0]?.orderLineGuid,
    );
  });
});

test("真实 SQLite：payment draft 同事务创建并按完整 cart/身份重放，soldAt 变化不换单", async () => {
  await withDatabase("draft-replay", async (connection) => {
    await migrateFresh(connection);
    const ids = sequenceIds("order-guid", "audit-draft");
    const store = new SqlitePaymentDraftRecoveryStore(
      connection,
      ids,
      () => T2,
    );
    const input = draftInput();
    const first = await store.createOrReuseDraft(input);
    const replay = await store.createOrReuseDraft({
      ...input,
      soldAtIso: T1,
    });
    assert.equal(first.replayed, false);
    assert.equal(replay.replayed, true);
    assert.equal(replay.orderGuid, first.orderGuid);
    assert.equal(replay.localSequence, first.localSequence);
    assert.equal(replay.soldAtIso, T0);
    assert.equal(
      await scalar(connection, "SELECT COUNT(*) AS count FROM local_orders"),
      1,
    );
    assert.equal(
      await scalar(
        connection,
        "SELECT COUNT(*) AS count FROM local_order_lines",
      ),
      input.cart.lines.length,
    );
    await assert.rejects(
      () => store.createOrReuseDraft({
        ...input,
        identity: { ...input.identity, cashierId: "cashier-other" },
      }),
      /different cart or identity/,
    );
    await assert.rejects(
      async () => store.createOrReuseDraft({
        ...input,
        cart: {
          ...input.cart,
          actualAmount: { currency: "AUD", cents: 901 },
        },
      }),
      /monetary truth|different cart or identity/,
    );
    const recovery = await store.findBlockingRecovery(input.identity);
    assert.equal(recovery?.kind, "DraftPrepared");
    assert.equal(recovery?.attemptId, null);
    assert.equal(recovery?.orderGuid, first.orderGuid);
    assert.equal(recovery?.cart.revision, input.cart.revision);
    assert.deepEqual(recovery?.cart.lines, input.cart.lines);
    assert.deepEqual(recovery?.pricingState, input.pricingState);
    assert.equal(recovery?.pricingState.asOfIso, input.pricingState.asOfIso);
    assert.equal(
      recovery?.pricingState.lines[0]?.discountState.kind,
      "promotion",
    );
    assert.equal(
      recovery?.pricingState.lines[1]?.discountState.kind,
      "manual-percent",
    );
    assert.ok(recovery);
    const activeCart = new ActivePricingCartSession(new PricingCart(), () => new PricingCart());
    const coordinator = new ActivePricingCartPaymentLeaseCoordinator(
      activeCart,
      { async findBlockingCart() {
        return {
          checkoutIntentId: input.draftId,
          cart: recovery.cart,
          pricingState: recovery.pricingState,
          recallBinding: recovery.recallBinding,
        };
      } },
      () => "sqlite-cold-start-lease",
    );
    const lease = await coordinator.initializeRecovery();
    assert.ok(lease);
    assert.deepEqual(lease.total, recovery.cart.actualAmount);
    assert.deepEqual(lease.pricingState, recovery.pricingState);
    await coordinator.releaseAfterSafeCancel(lease, first.orderGuid);
  });
});

test("真实 SQLite：异常刷卡移交恢复中心后不阻断新订单，人工结论按原 attempt 幂等审计", async () => {
  await withDatabase("payment-recovery-center", async (connection) => {
    await migrateFresh(connection);
    const ids = sequenceIds("recovery-order", "recovery-audit");
    const drafts = new SqlitePaymentDraftRecoveryStore(connection, ids, () => T2);
    const firstInput = draftInput({ draftId: "recovery-draft-1" });
    const first = await drafts.createOrReuseDraft(firstInput);
    await insertAttempt(connection, {
      attemptId: "recovery-attempt-old-declined",
      idempotencyKey: "recovery-key-old",
      orderGuid: first.orderGuid,
      provider: "linkly-cloud",
      operation: "purchase",
      amountCents: firstInput.cart.actualAmount.cents,
      state: "Declined",
    });
    await insertAttempt(connection, {
      attemptId: "recovery-attempt-1",
      idempotencyKey: "recovery-key-1",
      orderGuid: first.orderGuid,
      provider: "linkly-cloud",
      operation: "purchase",
      amountCents: firstInput.cart.actualAmount.cents,
      state: "Unknown",
    });
    const center = new SqlitePaymentRecoveryCenterStore(
      connection,
      (() => { let value = 0; return () => `recovery-record-${++value}`; })(),
      (() => { let value = 0; return () => `recovery-center-audit-${++value}`; })(),
      () => T2,
    );
    const actor = { cashierId: "supervisor-1", cashierName: "Supervisor", userGuid: "user-supervisor-1" };
    assert.equal(
      (await center.findCurrentCandidate(firstInput.identity))?.attemptId,
      "recovery-attempt-1",
    );
    const parked = await center.parkExact({
      ...firstInput.identity,
      orderGuid: first.orderGuid,
      attemptId: "recovery-attempt-1",
      actionId: "park-action-1",
      actor,
    });
    assert.equal(parked.status, "result-unknown");
    await assert.rejects(
      () => center.parkExact({
        ...firstInput.identity,
        orderGuid: first.orderGuid,
        attemptId: "recovery-attempt-1",
        actionId: "park-action-changed",
        actor,
      }),
      /IDENTITY_CONFLICT/,
    );
    const earlierAttempt = await center.parkExact({
      ...firstInput.identity,
      orderGuid: first.orderGuid,
      attemptId: "recovery-attempt-old-declined",
      actionId: "park-action-other",
      actor,
    });
    assert.notEqual(earlierAttempt.recordId, parked.recordId);
    assert.equal(await drafts.findBlockingRecovery(firstInput.identity), null);

    const second = await drafts.createOrReuseDraft(
      draftInput({ draftId: "recovery-draft-2" }),
    );
    assert.notEqual(second.orderGuid, first.orderGuid);
    // 已移交 Linkly 异常不能封锁其他通道的新收银，但同一 Linkly 通道仍须失败关闭。
    await insertAttempt(connection, {
      attemptId: "recovery-attempt-square-next",
      idempotencyKey: "recovery-key-square-next",
      orderGuid: second.orderGuid,
      provider: "square",
      operation: "purchase",
      amountCents: firstInput.cart.actualAmount.cents,
      state: "Created",
    });
    await assert.rejects(
      () => insertAttempt(connection, {
        attemptId: "recovery-attempt-linkly-same-lane",
        idempotencyKey: "recovery-key-linkly-same-lane",
        orderGuid: second.orderGuid,
        provider: "linkly-cloud",
        operation: "purchase",
        amountCents: firstInput.cart.actualAmount.cents,
        state: "Created",
      }),
      /PAYMENT_TERMINAL_BLOCKING_ATTEMPT_EXISTS/,
    );
    assert.equal((await center.list(firstInput.identity)).length, 2);

    const command = {
      ...firstInput.identity,
      recordId: parked.recordId,
      actionId: "manual-action-1",
      finding: "paid" as const,
      verifiedAmountCents: firstInput.cart.actualAmount.cents,
      evidenceReference: "receipt-123",
      note: "Terminal receipt verified",
      authorizationId: "supervisor-auth-1",
      supervisorActor: actor,
      requestingActor: {
        cashierId: "cashier-requester-1",
        cashierName: "Cashier Requester",
        userGuid: "user-requester-1",
      },
      reconciliationId: await center.recordProviderReconciliation({
        ...firstInput.identity,
        recordId: parked.recordId,
        reconciliationId: "reconciliation-1",
      }),
    };
    assert.throws(
      () => center.recordManualFinding({
        ...command,
        actionId: "manual-action-same-actor",
        requestingActor: actor,
      }),
      /REQUESTING_ACTOR_MUST_DIFFER/,
    );
    assert.throws(
      () => center.recordManualFinding({
        ...command,
        actionId: "manual-action-same-cashier",
        requestingActor: { ...command.requestingActor, cashierId: actor.cashierId },
      }),
      /REQUESTING_ACTOR_MUST_DIFFER/,
    );
    assert.throws(
      () => center.recordManualFinding({
        ...command,
        actionId: "manual-action-same-user",
        requestingActor: { ...command.requestingActor, userGuid: actor.userGuid },
      }),
      /REQUESTING_ACTOR_MUST_DIFFER/,
    );
    const resolved = await center.recordManualFinding(command);
    assert.equal(resolved.record.status, "charged-order-incomplete");
    assert.equal((await center.recordManualFinding(command)).record.events.length, 2);
    assert.deepEqual(
      { ...await connection.getFirst<Record<string, unknown>>(
        `SELECT record_id, order_guid, attempt_id, action_id, finding
         FROM payment_recovery_authorizations WHERE authorization_id = ?`,
        [command.authorizationId],
      ) },
      {
        record_id: parked.recordId,
        order_guid: first.orderGuid,
        attempt_id: parked.attemptId,
        action_id: command.actionId,
        finding: "paid",
      },
    );
    await assert.rejects(
      () => center.recordManualFinding({ ...command, evidenceReference: "changed" }),
      /ACTION_CONFLICT/,
    );
    assert.equal(
      await scalar(connection, "SELECT COUNT(*) AS count FROM audit_events WHERE event_type LIKE 'PAYMENT_RECOVERY_%'"),
      3,
    );
  });
});

test("真实 SQLite：人工未扣款保留 provider 状态并允许原单重付，迟到 Approved 转人工复核", async () => {
  await withDatabase("payment-recovery-manual-unpaid", async (connection) => {
    await migrateFresh(connection);
    const ids = sequenceIds("manual-unpaid-order", "manual-unpaid-audit");
    const drafts = new SqlitePaymentDraftRecoveryStore(connection, ids, () => T2);
    const input = draftInput({ draftId: "manual-unpaid-draft" });
    const created = await drafts.createOrReuseDraft(input);
    await insertAttempt(connection, {
      attemptId: "manual-unpaid-attempt",
      idempotencyKey: "manual-unpaid-key",
      orderGuid: created.orderGuid,
      provider: "linkly-cloud",
      operation: "purchase",
      amountCents: input.cart.actualAmount.cents,
      state: "Unknown",
    });
    const center = new SqlitePaymentRecoveryCenterStore(
      connection,
      (() => { let value = 0; return () => `manual-unpaid-record-${++value}`; })(),
      (() => { let value = 0; return () => `manual-unpaid-center-audit-${++value}`; })(),
      () => T2,
    );
    const actor = { cashierId: "supervisor-2", cashierName: "Supervisor Two", userGuid: "user-supervisor-2" };
    const parked = await center.parkExact({
      ...input.identity,
      orderGuid: created.orderGuid,
      attemptId: "manual-unpaid-attempt",
      actionId: "manual-unpaid-park",
      actor,
    });
    await center.recordManualFinding({
      ...input.identity,
      recordId: parked.recordId,
      actionId: "manual-unpaid-action",
      finding: "unpaid",
      verifiedAmountCents: null,
      evidenceReference: "terminal-history",
      note: "No debit found",
      authorizationId: "manual-unpaid-auth",
      supervisorActor: actor,
      requestingActor: { cashierId: "cashier-unpaid", cashierName: "Cashier", userGuid: "user-cashier-unpaid" },
    });
    assert.equal(
      String((await connection.getFirst<{ state: unknown }>(
        "SELECT state FROM payment_attempts WHERE attempt_id = ?",
        ["manual-unpaid-attempt"],
      ))?.state),
      "Unknown",
    );
    await center.resumeExact(input.identity, parked.recordId);
    await assert.rejects(
      () => center.recordManualFinding({
        ...input.identity,
        recordId: parked.recordId,
        actionId: "manual-unpaid-unparked-action",
        finding: "unpaid",
        verifiedAmountCents: null,
        evidenceReference: "terminal-history",
        note: "Must be parked first",
        authorizationId: "manual-unpaid-unparked-auth",
        supervisorActor: actor,
        requestingActor: { cashierId: "cashier-unpaid", cashierName: "Cashier", userGuid: "user-cashier-unpaid" },
      }),
      /MANUAL_REQUIRES_PARKED_CASE/,
    );
    const reparked = await center.parkExact({
      ...input.identity,
      orderGuid: created.orderGuid,
      attemptId: "manual-unpaid-attempt",
      actionId: "manual-unpaid-repark",
      actor,
    });
    assert.equal(reparked.isParked, true);
    await center.resumeExact(input.identity, parked.recordId);
    assert.equal((await drafts.findBlockingRecovery(input.identity))?.kind, "DraftPrepared");
    const nextOrderGuid = "manual-unpaid-next-order";
    await insertOrder(connection, {
      orderGuid: nextOrderGuid, sequence: 811, ...input.identity,
      cashierId: "cashier-next", amountCents: input.cart.actualAmount.cents,
      state: "Draft", syncProvenance: TEST_SYNC_PROVENANCE,
    });
    await insertActivePaymentDraft(connection, nextOrderGuid, input.identity);
    await insertAttempt(connection, {
      attemptId: "manual-unpaid-next-order-attempt",
      idempotencyKey: "manual-unpaid-next-order-key",
      orderGuid: nextOrderGuid,
      provider: "linkly-cloud",
      operation: "purchase",
      amountCents: input.cart.actualAmount.cents,
      state: "Created",
    });
    await connection.run(
      "UPDATE payment_attempts SET state = 'Declined', updated_at_iso = ? WHERE attempt_id = ?",
      [T2, "manual-unpaid-next-order-attempt"],
    );
    const repositories = createSqliteRepositories(connection, {
      nowIso: () => T2,
      createLeaseId: () => "manual-unpaid-lease",
      encryptor,
    });
    assert.equal(await repositories.payments.findBlocking(created.orderGuid), null);
    assert.equal(await repositories.payments.insertIfUnblocked({
      attemptId: "manual-unpaid-retry-attempt",
      idempotencyKey: "manual-unpaid-retry-key",
      orderGuid: created.orderGuid,
      provider: "linkly-cloud",
      operation: "purchase",
      amount: { currency: "AUD", cents: input.cart.actualAmount.cents },
      state: "Created",
      references: {
        checkoutId: null, paymentId: null, sessionId: null,
        txnRef: null, rfn: null, voucherReservationToken: null,
      },
      createdAtIso: T2,
      updatedAtIso: T2,
      lastErrorCode: null,
    }), null);
    await connection.run(
      "UPDATE payment_attempts SET state = 'Unknown', updated_at_iso = ? WHERE attempt_id = ?",
      [T2, "manual-unpaid-retry-attempt"],
    );
    const retryParked = await center.parkExact({
      ...input.identity,
      orderGuid: created.orderGuid,
      attemptId: "manual-unpaid-retry-attempt",
      actionId: "manual-unpaid-retry-park",
      actor,
    });
    assert.notEqual(retryParked.recordId, parked.recordId);
    assert.equal(retryParked.attemptId, "manual-unpaid-retry-attempt");
    await connection.run(
      "UPDATE payment_attempts SET state = 'Approved', updated_at_iso = ? WHERE attempt_id = ?",
      [T2, "manual-unpaid-attempt"],
    );
    assert.equal((await center.getExact(input.identity, parked.recordId))?.status, "review-required");
  });
});

test("真实 SQLite：主管结案（未收款/已收款）与结论同事务入队 supervisorResolved ACK，仍未知与终态 attempt 不入队", async () => {
  await withDatabase("payment-recovery-supervisor-ack", async (connection) => {
    await migrateFresh(connection);
    const ids = sequenceIds("sack-order", "sack-audit");
    const drafts = new SqlitePaymentDraftRecoveryStore(connection, ids, () => T2);
    const input = draftInput({ draftId: "sack-draft" });
    const created = await drafts.createOrReuseDraft(input);
    const center = new SqlitePaymentRecoveryCenterStore(
      connection,
      (() => { let value = 0; return () => `sack-record-${++value}`; })(),
      (() => { let value = 0; return () => `sack-center-audit-${++value}`; })(),
      () => T2,
    );
    const queue = new SqlitePaymentSupervisorAckQueue(connection);
    const actor = { cashierId: "supervisor-sack", cashierName: "Supervisor", userGuid: "user-supervisor-sack" };
    const requester = { cashierId: "cashier-sack", cashierName: "Cashier", userGuid: "user-cashier-sack" };
    const finding = (recordId: string, actionId: string, kind: "unpaid" | "uncertain") => center.recordManualFinding({
      ...input.identity,
      recordId,
      actionId,
      finding: kind,
      verifiedAmountCents: null,
      evidenceReference: "terminal-history",
      note: "checked",
      authorizationId: `auth-${actionId}`,
      supervisorActor: actor,
      requestingActor: requester,
    });
    const park = (attemptId: string) => center.parkExact({
      ...input.identity, orderGuid: created.orderGuid, attemptId, actionId: `park-${attemptId}`, actor,
    });
    const attempt = async (attemptId: string, provider: string, state: string, sessionId: string | null) => {
      await insertAttempt(connection, {
        attemptId, idempotencyKey: `key-${attemptId}`, orderGuid: created.orderGuid,
        provider, operation: "purchase", amountCents: input.cart.actualAmount.cents, state,
      });
      await connection.run("UPDATE payment_attempts SET session_id = ? WHERE attempt_id = ?", [sessionId, attemptId]);
    };

    // 仍未知：不是结案，不入队。
    await attempt("sack-uncertain", "linkly-cloud", "Unknown", "session-uncertain");
    const uncertain = await park("sack-uncertain");
    await finding(uncertain.recordId, "sack-action-uncertain", "uncertain");
    assert.deepEqual(await queue.listPending(), []);

    // 未收款：与结论同事务入队；重放同一 action 不重复入队。
    await attempt("sack-unpaid", "linkly-cloud", "Unknown", "session-unpaid");
    const unpaid = await park("sack-unpaid");
    await finding(unpaid.recordId, "sack-action-unpaid", "unpaid");
    await finding(unpaid.recordId, "sack-action-unpaid", "unpaid");
    const pending = await queue.listPending();
    assert.deepEqual(pending.map((row) => [row.attemptId, row.resolution, row.sourceActionId]),
      [["sack-unpaid", "unpaid", "sack-action-unpaid"]]);

    // 已收款：同样入队（订单入账由人工 tender 提交完成，后端会话仍需关闭）。
    await attempt("sack-paid", "linkly-cloud", "Unknown", "session-paid");
    const paid = await park("sack-paid");
    await center.recordManualFinding({
      ...input.identity,
      recordId: paid.recordId,
      actionId: "sack-action-paid",
      finding: "paid",
      verifiedAmountCents: input.cart.actualAmount.cents,
      evidenceReference: "receipt-1",
      note: "verified",
      authorizationId: "auth-sack-paid",
      supervisorActor: actor,
      requestingActor: requester,
      reconciliationId: await center.recordProviderReconciliation({
        ...input.identity, recordId: paid.recordId, reconciliationId: "sack-reconciliation",
      }),
    });
    assert.deepEqual((await queue.listPending()).map((row) => [row.attemptId, row.resolution]),
      [["sack-paid", "paid"], ["sack-unpaid", "unpaid"]]);

    // Square 或没有后端会话（Created / 无 SessionId）的 attempt 无会话可关闭，不入队。
    await attempt("sack-square", "square", "Unknown", null);
    const square = await park("sack-square");
    await finding(square.recordId, "sack-action-square", "unpaid");
    await attempt("sack-nosession", "linkly-cloud", "Unknown", null);
    const noSession = await park("sack-nosession");
    await finding(noSession.recordId, "sack-action-nosession", "unpaid");
    assert.equal((await queue.listPending()).length, 2);

    // 失败只累计次数；确认后不再待办且不可改回。
    await queue.recordFailure("sack-unpaid", "LINKLY_NETWORK_DOWN", T2);
    assert.deepEqual((await queue.listPending()).map((row) => [row.attemptCount, row.lastErrorCode]), [[0, null], [1, "LINKLY_NETWORK_DOWN"]]);
    assert.equal(await queue.markAcknowledged("sack-unpaid", T2), true);
    assert.equal(await queue.markAcknowledged("sack-unpaid", T2), false);
    assert.deepEqual((await queue.listPending()).map((row) => row.attemptId), ["sack-paid"]);
    await assert.rejects(
      () => connection.run("UPDATE payment_supervisor_session_acks SET acknowledged_at_iso = NULL WHERE attempt_id = 'sack-unpaid'"),
      /PAYMENT_SUPERVISOR_ACK_IMMUTABLE/,
    );
    await assert.rejects(
      () => connection.run("DELETE FROM payment_supervisor_session_acks WHERE attempt_id = 'sack-unpaid'"),
      /PAYMENT_SUPERVISOR_ACK_IMMUTABLE/,
    );
  });
});

test("真实 SQLite：退款结果未知经主管确认未退款，作废 action、释放额度、登记 ACK，且 provider/部分完成/同人结案一律拒绝", async () => {
  await withDatabase("return-supervisor-resolution", async (connection) => {
    await migrateFresh(connection);
    const vault = new SqliteReturnCapacityVault(connection, encryptor, () => T0);
    await vault.seedOrLoad({
      capacityId: "return-capacity-card",
      originalOrderGuid: "original-return-order",
      method: "card",
      originalAmountCents: 500,
      remainingAmountCents: 500,
      protectedContext: { paymentId: "SECRET-PAYMENT-ID", rfn: "SECRET-RFN" },
      observedAtIso: T0,
    });
    let tenderId = 0;
    let auditId = 0;
    const ledger = new SqliteReturnExecutionLedger(
      connection,
      encryptor,
      { createTenderGuid: () => `rs-tender-${++tenderId}`, createAuditEventId: () => `rs-audit-${++auditId}` },
      () => T2,
    );
    const base = durableReturnDraft();
    const cardAllocation = { ...base.allocations[1]!, index: 0 };
    const draft = {
      ...base,
      allocations: [cardAllocation],
      plan: {
        ...base.plan,
        allocations: [{
          method: "card" as const,
          signedAmountCents: -500,
          originalCapacityId: "return-capacity-card",
          originalOrderGuid: "original-return-order",
          offlineCashProof: null,
        }],
      },
    };
    const cardOnly = { ...draft, allocations: [{ ...cardAllocation, signedAmountCents: -500 }] };
    await ledger.prepareOrLoad(cardOnly);
    await ledger.markAllocationSubmitted({ actionId: cardOnly.actionId, allocationId: "return-allocation-card" });
    await insertActionBinding(
      connection, cardOnly.returnOrderGuid, "rs-provider-action", "rs-attempt", "rs-idempotency",
      ["linkly-cloud", "refund", "AUD", -500],
    );
    await insertAttempt(connection, {
      attemptId: "rs-attempt", idempotencyKey: "rs-idempotency", orderGuid: cardOnly.returnOrderGuid,
      provider: "linkly-cloud", operation: "refund", amountCents: -500, state: "Unknown",
    });
    await connection.run(
      "UPDATE payment_attempts SET session_id = 'rs-session', provider_environment = 'Sandbox' WHERE attempt_id = 'rs-attempt'",
    );
    await ledger.bindAllocationAttempt({
      actionId: cardOnly.actionId, allocationId: "return-allocation-card",
      attemptKind: "payment-provider", externalActionId: "rs-provider-action", durableAttemptId: "rs-attempt",
    });
    await ledger.recordAllocationOutcome({
      actionId: cardOnly.actionId, allocationId: "return-allocation-card",
      expectedStatuses: ["submitted"], status: "unknown", protectedRecoveryKey: "SECRET-RECOVERY-KEY",
    });
    await ledger.markActionUnknown({ actionId: cardOnly.actionId });

    let resolutionId = 0;
    let resolutionAuditId = 0;
    const store = new SqliteReturnSupervisorResolutionStore(
      connection,
      {
        createResolutionId: () => `rs-resolution-${++resolutionId}`,
        createAuditEventId: () => `rs-resolution-audit-${++resolutionAuditId}`,
      },
      () => T2,
    );
    const supervisor = { cashierId: "SUP-RETURN", cashierName: "Supervisor", userGuid: "user-sup" };
    const requesting = { cashierId: "C-RETURN", cashierName: "Return Cashier", userGuid: "user-cash" };
    const scope = { storeCode: "S-RETURN", deviceCode: "D-RETURN" };
    const command = {
      actionId: cardOnly.actionId,
      finding: "not-refunded" as const,
      evidenceReference: "terminal receipt 8841",
      note: "Terminal and Linkly portal show no refund",
      authorizationId: "rs-auth-1",
      supervisorActor: supervisor,
      requestingActor: requesting,
      scope,
    };

    // 拒绝路径不得留下任何半截状态。
    await assert.rejects(() => store.resolve({ ...command, supervisorActor: requesting }), /RETURN_SUPERVISOR_REQUIRED/);
    await assert.rejects(() => store.resolve({ ...command, scope: { ...scope, deviceCode: "OTHER" } }), /RETURN_RECOVERY_FAILED/);
    await connection.run("UPDATE payment_attempts SET state = 'Approved' WHERE attempt_id = 'rs-attempt'");
    await assert.rejects(() => store.resolve(command), /RETURN_SUPERVISOR_RESOLUTION_UNSUPPORTED/);
    await connection.run("UPDATE payment_attempts SET state = 'Unknown' WHERE attempt_id = 'rs-attempt'");
    assert.equal(await scalar(connection, "SELECT COUNT(*) AS count FROM return_supervisor_resolutions"), 0);
    assert.equal(await scalar(connection, "SELECT COUNT(*) AS count FROM return_actions WHERE state = 'unknown'"), 1);

    // 继续等待：只留审计，action 继续锁定，ACK 不入队。
    assert.equal(await store.resolve({ ...command, finding: "keep-waiting", authorizationId: "rs-auth-wait" }), "waiting");
    assert.equal(await scalar(connection, "SELECT COUNT(*) AS count FROM return_actions WHERE state = 'unknown'"), 1);
    assert.equal(await scalar(connection, "SELECT COUNT(*) AS count FROM payment_supervisor_session_acks"), 0);

    // 未退款：作废 action、释放额度、登记后端会话 ACK。
    assert.equal(await store.resolve(command), "declined");
    assert.equal(await scalar(connection, "SELECT COUNT(*) AS count FROM return_actions WHERE state = 'declined'"), 1);
    assert.equal(
      await scalar(connection, "SELECT COUNT(*) AS count FROM return_action_allocations WHERE status = 'declined' AND capacity_reservation_state = 'Released'"),
      1,
    );
    assert.equal(
      String((await connection.getFirst<{ state: unknown }>("SELECT state FROM payment_attempts WHERE attempt_id = 'rs-attempt'"))?.state),
      "Unknown",
      "provider attempt 状态不被人工结论改写，迟到 Approved 仍可识别",
    );
    const queue = new SqlitePaymentSupervisorAckQueue(connection);
    assert.deepEqual(
      (await queue.listPending()).map((row) => [row.attemptId, row.resolution]),
      [["rs-attempt", "not-refunded"]],
    );
    assert.equal(await scalar(connection, "SELECT COUNT(*) AS count FROM return_supervisor_resolutions WHERE finding = 'not-refunded'"), 1);
    assert.equal(
      await scalar(connection, "SELECT COUNT(*) AS count FROM audit_events WHERE event_type = 'RETURN_REFUND_SUPERVISOR_NOT_REFUNDED' AND scope_store_code = 'S-RETURN'"),
      1,
    );
    // 已作废的 action 不得再被主管结论改写。
    await assert.rejects(() => store.resolve({ ...command, authorizationId: "rs-auth-2" }), /RETURN_SUPERVISOR_RESOLUTION_UNSUPPORTED/);
    await assert.rejects(
      () => connection.run("UPDATE return_supervisor_resolutions SET note = 'tampered'"),
      /RETURN_SUPERVISOR_RESOLUTION_IMMUTABLE/,
    );

    // 额度已释放：同一原卡额度可以重新发起新的退款 action。
    const retry = await ledger.prepareOrLoad({
      ...cardOnly,
      actionId: "rs-retry-action",
      requestFingerprint: "rs-retry-fingerprint",
      returnOrderGuid: "rs-retry-order",
      actionRecoveryToken: "rs-retry-token",
      lines: cardOnly.lines.map((line) => ({ ...line, lineId: "rs-retry-line" })),
      allocations: cardOnly.allocations.map((allocation) => ({
        ...allocation, allocationId: "rs-retry-allocation", externalAttemptId: "rs-retry-external",
      })),
    });
    assert.equal(retry.status, "processing");
  });
});

test("真实 SQLite：多笔/部分完成的退款不开放主管结案（失败关闭）", async () => {
  await withDatabase("return-supervisor-resolution-partial", async (connection) => {
    await migrateFresh(connection);
    const vault = new SqliteReturnCapacityVault(connection, encryptor, () => T0);
    await vault.seedOrLoad({
      capacityId: "return-capacity-cash", originalOrderGuid: "original-return-order", method: "cash",
      originalAmountCents: 500, remainingAmountCents: 500, protectedContext: null, observedAtIso: T0,
    });
    await vault.seedOrLoad({
      capacityId: "return-capacity-card", originalOrderGuid: "original-return-order", method: "card",
      originalAmountCents: 500, remainingAmountCents: 500,
      protectedContext: { paymentId: "SECRET-PAYMENT-ID", rfn: "SECRET-RFN" }, observedAtIso: T0,
    });
    let tenderId = 0;
    let auditId = 0;
    const ledger = new SqliteReturnExecutionLedger(
      connection, encryptor,
      { createTenderGuid: () => `rp-tender-${++tenderId}`, createAuditEventId: () => `rp-audit-${++auditId}` },
      () => T2,
    );
    const draft = durableReturnDraft();
    await ledger.prepareOrLoad(draft);
    await ledger.markAllocationSubmitted({ actionId: draft.actionId, allocationId: "return-allocation-cash" });
    await ledger.recordAllocationOutcome({
      actionId: draft.actionId, allocationId: "return-allocation-cash",
      expectedStatuses: ["submitted"], status: "completed", protectedRecoveryKey: null,
    });
    await ledger.markAllocationSubmitted({ actionId: draft.actionId, allocationId: "return-allocation-card" });
    await insertActionBinding(connection, draft.returnOrderGuid, "rp-provider-action", "rp-attempt", "rp-idempotency", ["square", "refund", "AUD", -300]);
    await insertAttempt(connection, {
      attemptId: "rp-attempt", idempotencyKey: "rp-idempotency", orderGuid: draft.returnOrderGuid,
      provider: "square", operation: "refund", amountCents: -300, state: "Unknown",
    });
    await ledger.bindAllocationAttempt({
      actionId: draft.actionId, allocationId: "return-allocation-card",
      attemptKind: "payment-provider", externalActionId: "rp-provider-action", durableAttemptId: "rp-attempt",
    });
    await ledger.recordAllocationOutcome({
      actionId: draft.actionId, allocationId: "return-allocation-card",
      expectedStatuses: ["submitted"], status: "unknown", protectedRecoveryKey: null,
    });
    await ledger.markActionUnknown({ actionId: draft.actionId });
    const store = new SqliteReturnSupervisorResolutionStore(
      connection,
      { createResolutionId: () => "rp-resolution", createAuditEventId: () => "rp-resolution-audit" },
      () => T2,
    );
    await assert.rejects(() => store.resolve({
      actionId: draft.actionId, finding: "not-refunded", evidenceReference: "receipt", note: "checked",
      authorizationId: "rp-auth",
      supervisorActor: { cashierId: "SUP", cashierName: "Supervisor", userGuid: null },
      requestingActor: { cashierId: "C-RETURN", cashierName: "Return Cashier", userGuid: null },
      scope: { storeCode: "S-RETURN", deviceCode: "D-RETURN" },
    }), /RETURN_SUPERVISOR_RESOLUTION_UNSUPPORTED/);
    assert.equal(await scalar(connection, "SELECT COUNT(*) AS count FROM return_actions WHERE state = 'unknown'"), 1);
    assert.equal(await scalar(connection, "SELECT COUNT(*) AS count FROM return_supervisor_resolutions"), 0);
  });
});

test("真实 SQLite + 编排器：主管未退款先让 provider 真实结果优先，仍未知才落结论；ACK 在结论之后补发", async () => {
  for (const scenario of ["still-unknown", "provider-declined", "keep-waiting"] as const) {
    await withDatabase(`return-orchestrator-resolution-${scenario}`, async (connection) => {
      await migrateFresh(connection);
      const vault = new SqliteReturnCapacityVault(connection, encryptor, () => T0);
      await vault.seedOrLoad({
        capacityId: "return-capacity-card", originalOrderGuid: "original-return-order", method: "card",
        originalAmountCents: 500, remainingAmountCents: 500,
        protectedContext: { paymentId: "SECRET-PAYMENT-ID", rfn: "SECRET-RFN" }, observedAtIso: T0,
      });
      let auditId = 0;
      const ledger = new SqliteReturnExecutionLedger(
        connection, encryptor,
        { createTenderGuid: () => `ro-tender-${scenario}`, createAuditEventId: () => `ro-audit-${scenario}-${++auditId}` },
        () => T2,
      );
      const base = durableReturnDraft();
      const card = { ...base.allocations[1]!, index: 0, signedAmountCents: -500 };
      const draft = {
        ...base,
        allocations: [card],
        plan: {
          ...base.plan,
          allocations: [{
            method: "card" as const, signedAmountCents: -500, originalCapacityId: "return-capacity-card",
            originalOrderGuid: "original-return-order", offlineCashProof: null,
          }],
        },
      };
      await ledger.prepareOrLoad(draft);
      await ledger.markAllocationSubmitted({ actionId: draft.actionId, allocationId: "return-allocation-card" });
      await insertActionBinding(connection, draft.returnOrderGuid, "ro-provider-action", "ro-attempt", "ro-idempotency", ["linkly-cloud", "refund", "AUD", -500]);
      await insertAttempt(connection, {
        attemptId: "ro-attempt", idempotencyKey: "ro-idempotency", orderGuid: draft.returnOrderGuid,
        provider: "linkly-cloud", operation: "refund", amountCents: -500, state: "Unknown",
      });
      await connection.run("UPDATE payment_attempts SET session_id = 'ro-session', provider_environment = 'Sandbox' WHERE attempt_id = 'ro-attempt'");
      await ledger.bindAllocationAttempt({
        actionId: draft.actionId, allocationId: "return-allocation-card",
        attemptKind: "payment-provider", externalActionId: "ro-provider-action", durableAttemptId: "ro-attempt",
      });
      await ledger.recordAllocationOutcome({
        actionId: draft.actionId, allocationId: "return-allocation-card",
        expectedStatuses: ["submitted"], status: "unknown", protectedRecoveryKey: null,
      });
      await ledger.markActionUnknown({ actionId: draft.actionId });

      const recoverCalls: string[] = [];
      const orchestrator = new DurableReturnExecutionOrchestrator({
        ledger,
        supervisorResolution: new SqliteReturnSupervisorResolutionStore(
          connection,
          { createResolutionId: () => `ro-resolution-${scenario}`, createAuditEventId: () => `ro-resolution-audit-${scenario}` },
          () => T2,
        ),
        trustedIdentity: { getTrustedIdentity: async () => ({ ...draft.identity, sessionEpoch: "later-epoch" }) },
        cashRefund: { async submit() { throw new Error("not used"); }, async recover() { throw new Error("not used"); } },
        onlineRefund: {
          async prepareAttempt() { throw new Error("not used"); },
          async submit() { throw new Error("must not resubmit"); },
          async recover() {
            recoverCalls.push(scenario);
            if (scenario === "provider-declined") {
              // 模拟迟到的 provider 明确拒绝：先把 attempt 落成 Declined，再报告 declined。
              await connection.run("UPDATE payment_attempts SET state = 'Declined' WHERE attempt_id = 'ro-attempt'");
              return { status: "declined" as const };
            }
            return { status: "unknown" as const, protectedRecoveryKey: null };
          },
        },
        fingerprint: { async digest() { throw new Error("not used"); } },
        lineMaterial: { async resolveForAction() { throw new Error("not used"); } },
        createOpaqueId: () => "ro-opaque-id",
        nowIso: () => T2,
      });
      const outcome = await orchestrator.resolveUnknown({
        actionId: draft.actionId,
        finding: scenario === "keep-waiting" ? "keep-waiting" : "not-refunded",
        evidenceReference: "terminal receipt 8841",
        note: "checked",
        authorization: {
          authorizationId: "ro-auth",
          supervisorActor: { cashierId: "SUP-RETURN", cashierName: "Supervisor", userGuid: "user-sup" },
          requestingActor: { cashierId: "C-RETURN", cashierName: "Return Cashier", userGuid: null },
        },
      });
      const queued = (await new SqlitePaymentSupervisorAckQueue(connection).listPending()).map((row) => row.attemptId);
      const resolutions = await scalar(connection, "SELECT COUNT(*) AS count FROM return_supervisor_resolutions");
      if (scenario === "still-unknown") {
        assert.equal(outcome.status, "declined");
        assert.deepEqual(recoverCalls, ["still-unknown"], "结案前必须先按原 attempt 恢复一次");
        assert.deepEqual(queued, ["ro-attempt"]);
        assert.equal(resolutions, 1);
      } else if (scenario === "provider-declined") {
        assert.equal(outcome.status, "declined");
        assert.deepEqual(recoverCalls, ["provider-declined"]);
        // provider 的真实拒绝优先：不写主管结论，ACK 交给标准终态 ACK 管线。
        assert.deepEqual(queued, []);
        assert.equal(resolutions, 0);
      } else {
        assert.equal(outcome.status, "unknown");
        assert.deepEqual(recoverCalls, [], "继续等待不触发恢复也不改状态");
        assert.deepEqual(queued, []);
        assert.equal(resolutions, 1);
        assert.equal(await scalar(connection, "SELECT COUNT(*) AS count FROM return_actions WHERE state = 'unknown'"), 1);
      }
    });
  }
});

test("真实 SQLite：M34 终端批准但实扣金额不符：展示实扣金额，禁止确认已收款，未收款必须确认冲正并留审计", async () => {
  await withDatabase("payment-recovery-approved-amount-mismatch", async (connection) => {
    await migrateFresh(connection);
    const ids = sequenceIds("mismatch-order", "mismatch-audit");
    const drafts = new SqlitePaymentDraftRecoveryStore(connection, ids, () => T2);
    const input = draftInput({ draftId: "mismatch-draft" });
    const created = await drafts.createOrReuseDraft(input);
    await insertAttempt(connection, {
      attemptId: "mismatch-attempt", idempotencyKey: "mismatch-key", orderGuid: created.orderGuid,
      provider: "linkly-cloud", operation: "purchase", amountCents: input.cart.actualAmount.cents, state: "Unknown",
    });
    const terminalCharged = input.cart.actualAmount.cents + 500;
    await connection.run(
      "UPDATE payment_attempts SET session_id = 'mismatch-session', provider_environment = 'Sandbox', txn_ref = 'TXN-MISMATCH', provider_response_code = ? WHERE attempt_id = 'mismatch-attempt'",
      [`LINKLY_APPROVED_AMOUNT_MISMATCH:${terminalCharged}`],
    );
    const center = new SqlitePaymentRecoveryCenterStore(
      connection,
      (() => { let value = 0; return () => `mismatch-record-${++value}`; })(),
      (() => { let value = 0; return () => `mismatch-center-audit-${++value}`; })(),
      () => T2,
    );
    const supervisor = { cashierId: "supervisor-mm", cashierName: "Supervisor", userGuid: "user-supervisor-mm" };
    const requester = { cashierId: "cashier-mm", cashierName: "Cashier", userGuid: "user-cashier-mm" };
    const parked = await center.parkExact({
      ...input.identity, orderGuid: created.orderGuid, attemptId: "mismatch-attempt", actionId: "mismatch-park", actor: supervisor,
    });
    assert.equal(parked.terminalAmountMismatchCents, terminalCharged);
    assert.equal(parked.status, "result-unknown");
    const command = (finding: "paid" | "unpaid" | "uncertain", actionId: string, extra: Record<string, unknown> = {}) => ({
      ...input.identity,
      recordId: parked.recordId,
      actionId,
      finding,
      verifiedAmountCents: finding === "paid" ? input.cart.actualAmount.cents : null,
      evidenceReference: "terminal receipt 9001",
      note: "Terminal shows a different amount",
      authorizationId: `auth-${actionId}`,
      supervisorActor: supervisor,
      requestingActor: requester,
      ...extra,
    });
    const reconciliationId = await center.recordProviderReconciliation({
      ...input.identity, recordId: parked.recordId, reconciliationId: "mismatch-reconciliation",
    });

    // 即使主管输入订单金额，也不能把实扣不同金额的交易确认为已收款。
    await assert.rejects(
      () => center.recordManualFinding(command("paid", "mismatch-paid", { reconciliationId })),
      /PAYMENT_RECOVERY_APPROVED_AMOUNT_MISMATCH/,
    );
    // 未收款不带确认：拒绝（顾客已被扣款，不能悄悄放行再次收款）。
    await assert.rejects(
      () => center.recordManualFinding(command("unpaid", "mismatch-unpaid-no-ack")),
      /PAYMENT_RECOVERY_REVERSAL_ACK_REQUIRED/,
    );
    await assert.rejects(
      () => center.recordManualFinding(command("unpaid", "mismatch-unpaid-false-ack", { terminalChargeAcknowledged: false })),
      /PAYMENT_RECOVERY_REVERSAL_ACK_REQUIRED/,
    );
    assert.equal(await scalar(connection, "SELECT COUNT(*) AS count FROM payment_recovery_actions"), 0);
    assert.equal(
      await scalar(connection, "SELECT COUNT(*) AS count FROM audit_events WHERE event_type = 'PAYMENT_RECOVERY_REVERSAL_REQUIRED'"),
      0,
    );

    // 仍未知不受影响，也不产生冲正需求。
    await center.recordManualFinding(command("uncertain", "mismatch-uncertain"));
    assert.equal(
      await scalar(connection, "SELECT COUNT(*) AS count FROM audit_events WHERE event_type = 'PAYMENT_RECOVERY_REVERSAL_REQUIRED'"),
      0,
    );

    // 明确确认后登记冲正需求：结论 + 冲正审计 + 后端会话 ACK 同事务。
    const resolved = await center.recordManualFinding(
      command("unpaid", "mismatch-unpaid", { terminalChargeAcknowledged: true }),
    );
    assert.equal(resolved.record.status, "manual-unpaid");
    const audit = await connection.getFirst<{ payload_json: unknown; scope_store_code: unknown }>(
      "SELECT payload_json, scope_store_code FROM audit_events WHERE event_type = 'PAYMENT_RECOVERY_REVERSAL_REQUIRED'",
    );
    assert.ok(audit);
    const payload = JSON.parse(String(audit.payload_json)) as Record<string, unknown>;
    assert.equal(payload.terminalChargedCents, terminalCharged);
    assert.equal(payload.orderAmountCents, input.cart.actualAmount.cents);
    assert.equal(payload.attemptId, "mismatch-attempt");
    assert.equal(payload.transactionReference, "TXN-MISMATCH");
    assert.equal(audit.scope_store_code, input.identity.storeCode);
    assert.equal(await scalar(connection, "SELECT COUNT(*) AS count FROM payment_supervisor_session_acks"), 1);
  });
});

test("真实 SQLite：人工仍未知可把精确 case 交回原支付上下文", async () => {
  await withDatabase("payment-recovery-manual-uncertain-resume", async (connection) => {
    await migrateFresh(connection);
    const ids = sequenceIds("manual-uncertain-order", "manual-uncertain-audit");
    const drafts = new SqlitePaymentDraftRecoveryStore(connection, ids, () => T2);
    const input = draftInput({ draftId: "manual-uncertain-draft" });
    const created = await drafts.createOrReuseDraft(input);
    await insertAttempt(connection, {
      attemptId: "manual-uncertain-attempt",
      idempotencyKey: "manual-uncertain-key",
      orderGuid: created.orderGuid,
      provider: "linkly-cloud",
      operation: "purchase",
      amountCents: input.cart.actualAmount.cents,
      state: "Unknown",
    });
    const center = new SqlitePaymentRecoveryCenterStore(
      connection,
      () => "manual-uncertain-record",
      (() => {
        let value = 0;
        return () => `manual-uncertain-audit-event-${++value}`;
      })(),
      () => T2,
    );
    const actor = {
      cashierId: "supervisor-uncertain",
      cashierName: "Supervisor Uncertain",
      userGuid: "user-supervisor-uncertain",
    };
    const parked = await center.parkExact({
      ...input.identity,
      orderGuid: created.orderGuid,
      attemptId: "manual-uncertain-attempt",
      actionId: "manual-uncertain-park",
      actor,
    });
    await center.recordManualFinding({
      ...input.identity,
      recordId: parked.recordId,
      actionId: "manual-uncertain-action",
      finding: "uncertain",
      verifiedAmountCents: null,
      evidenceReference: "terminal-history-inconclusive",
      note: "Provider result is still unavailable",
      authorizationId: "manual-uncertain-auth",
      supervisorActor: actor,
      requestingActor: { cashierId: "cashier-uncertain", cashierName: "Cashier", userGuid: "user-cashier-uncertain" },
    });

    const resumed = await center.resumeExact(input.identity, parked.recordId);
    assert.equal(resumed.status, "manual-uncertain");
    assert.equal(resumed.isParked, false);
    assert.equal(
      String((await connection.getFirst<{ state: unknown }>(
        "SELECT state FROM payment_attempts WHERE attempt_id = ?",
        ["manual-uncertain-attempt"],
      ))?.state),
      "Unknown",
    );
    const nextOrderGuid = "manual-uncertain-next-order";
    await insertOrder(connection, {
      orderGuid: nextOrderGuid, sequence: 812, ...input.identity,
      cashierId: "cashier-next", amountCents: input.cart.actualAmount.cents,
      state: "Draft", syncProvenance: TEST_SYNC_PROVENANCE,
    });
    await insertActivePaymentDraft(connection, nextOrderGuid, input.identity);
    await insertAttempt(connection, {
      attemptId: "manual-uncertain-next-order-attempt",
      idempotencyKey: "manual-uncertain-next-order-key",
      orderGuid: nextOrderGuid,
      provider: "linkly-cloud",
      operation: "purchase",
      amountCents: input.cart.actualAmount.cents,
      state: "Created",
    });
    await connection.run(
      "UPDATE payment_attempts SET state = 'Declined', updated_at_iso = ? WHERE attempt_id = ?",
      [T2, "manual-uncertain-attempt"],
    );
    assert.equal(
      (await center.getExact(input.identity, parked.recordId))?.status,
      "payment-failed",
    );
  });
});

test("真实 SQLite：MANUAL outbox 取得租约后 provider Approved 仍在 HTTP 前失败关闭", async () => {
  await withDatabase("manual-payment-sync-race", async (connection) => {
    await migrateFresh(connection);
    let nextId = 0;
    const createId = () =>
      `00000000-0000-4000-8000-${String(++nextId).padStart(12, "0")}`;
    const drafts = new SqlitePaymentDraftRecoveryStore(connection, {
      createOrderGuid: createId,
      createOrderLineGuid: createId,
      createAuditEventId: createId,
    }, () => T2);
    const input = draftInput({ draftId: "manual-sync-race-draft" });
    const created = await drafts.createOrReuseDraft(input);
    const attemptId = createId();
    await insertAttempt(connection, {
      attemptId,
      idempotencyKey: createId(),
      orderGuid: created.orderGuid,
      provider: "square",
      operation: "purchase",
      amountCents: input.cart.actualAmount.cents,
      state: "Unknown",
    });
    const center = new SqlitePaymentRecoveryCenterStore(connection, createId, createId, () => T2);
    const parked = await center.parkExact({
      ...input.identity, orderGuid: created.orderGuid, attemptId,
      actionId: createId(),
      actor: { cashierId: "cashier-1", cashierName: "Cashier", userGuid: "cashier-user-1" },
    });
    const reconciliationId = await center.recordProviderReconciliation({
      ...input.identity, recordId: parked.recordId, reconciliationId: createId(),
    });
    const finding = await center.recordManualFinding({
      ...input.identity, recordId: parked.recordId, actionId: createId(),
      finding: "paid", verifiedAmountCents: parked.amountCents,
      evidenceReference: "terminal-history", note: "Read-only result remained unknown",
      authorizationId: createId(), reconciliationId,
      requestingActor: { cashierId: "cashier-1", cashierName: "Cashier", userGuid: "cashier-user-1" },
      supervisorActor: { cashierId: "supervisor-2", cashierName: "Supervisor", userGuid: "supervisor-user-2" },
    });
    const committer = new SqliteManualPaymentOrderCommitter(connection, () => T2);
    const tenderGuid = createId();
    await committer.completeManualPaymentOrder({
      recordId: parked.recordId, actionId: finding.actionId,
      orderGuid: created.orderGuid, attemptId, ...input.identity,
      authorizationId: finding.authorizationId, tenderGuid,
      completionAuditEvent: {
        eventId: createId(), eventType: "PAYMENT_COMPLETE", occurredAtIso: T2,
        orderGuid: created.orderGuid, correlationId: finding.actionId,
        payload: { action: "manual-payment-complete" },
      },
      outbox: {
        messageId: createId(), aggregateId: created.orderGuid, kind: "order-sync",
        payloadJson: JSON.stringify({ orderGuid: created.orderGuid }), nextAttemptAtIso: T2,
      },
    });
    const repositories = createSqliteRepositories(connection, {
      nowIso: () => T2, createLeaseId: createId, encryptor,
    });
    const [lease] = await repositories.outbox.leaseReady(1, 60);
    assert.ok(lease);
    await connection.run(
      "UPDATE payment_attempts SET state = 'Approved', updated_at_iso = ? WHERE attempt_id = ?",
      [T2, attemptId],
    );
    let httpCalls = 0;
    const adapter = new HbposOrderSyncAdapter(
      { async request() { httpCalls += 1; throw new Error("HTTP must not run"); } },
      repositories.orders,
      {
        resolver: new SqliteOrderSyncMaterialResolver(connection, {
          returnCapacityVault: { async resolveProtectedContext() { return null; } },
          voucherProtectedTokens: { async getByAttempt() { return null; } },
          paymentProtectedMaterials: { async read() { throw new Error("provider material must not be read"); } },
        }),
        linklyEnvironment: "Production",
      },
    );
    assert.deepEqual(
      await adapter.sync(created.orderGuid, JSON.stringify({ orderGuid: created.orderGuid })),
      { kind: "retry", failure: "server" },
    );
    assert.equal(httpCalls, 0);
  });
});

test("真实 SQLite：payment draft 完成现金支付后可通过 Hbpos 同步适配器上传 UUID 行标识", async () => {
  await withDatabase("payment-draft-sync-line-guid", async (connection) => {
    await migrateFresh(connection);
    let nextId = 0;
    const createId = () =>
      `00000000-0000-4000-8000-${String(++nextId).padStart(12, "0")}`;
    const ids = {
      createOrderGuid: createId,
      createOrderLineGuid: createId,
      createAuditEventId: createId,
    };
    const store = new SqlitePaymentDraftRecoveryStore(
      connection,
      ids,
      () => T2,
    );
    const input = draftInput({ draftId: "draft-sync-line-guid" });
    const created = await store.createOrReuseDraft(input);
    await insertTender(
      connection,
      createId(),
      created.orderGuid,
      "cash",
      input.cart.actualAmount.cents,
    );
    await connection.run(
      `UPDATE local_orders
       SET state = 'PendingSync', updated_at_iso = ?
       WHERE order_guid = ? AND state = 'Draft'`,
      [T2, created.orderGuid],
    );
    const repositories = createSqliteRepositories(connection, {
      nowIso: () => T2,
      createLeaseId: createId,
      encryptor,
    });
    const calls: HbposTransportRequest[] = [];
    const transport: HbposTransport = {
      async request<T>(
        request: HbposTransportRequest,
      ): Promise<HbposTransportResponse<T>> {
        calls.push(request);
        return {
          status: 200,
          data: {
            success: true,
            data: {
              orderGuid: created.orderGuid,
              accepted: true,
              alreadySynced: false,
            },
          } as T,
        };
      },
    };
    const adapter = new HbposOrderSyncAdapter(
      transport,
      repositories.orders,
    );

    assert.deepEqual(
      await adapter.sync(
        created.orderGuid,
        JSON.stringify({ orderGuid: created.orderGuid }),
      ),
      { kind: "synced", alreadySynced: false },
    );
    assert.equal(calls.length, 1);
    const request = calls[0]?.data as {
      lines: readonly { orderLineGuid: string }[];
    };
    assert.equal(request.lines.length, input.cart.lines.length);
    for (const line of request.lines) {
      assert.match(
        line.orderLineGuid,
        /^[0-9a-f]{8}-[0-9a-f]{4}-[1-8][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i,
      );
    }
  });
});

test("真实 SQLite：payment draft 要求 cart/定价状态冻结同步来源并逐行原子写入", async () => {
  await withDatabase("draft-line-provenance", async (connection) => {
    await migrateFresh(connection);
    const store = new SqlitePaymentDraftRecoveryStore(
      connection,
      sequenceIds("order-provenance", "audit-provenance"),
      () => T2,
    );
    const input = draftInput({ draftId: "draft-line-provenance" });
    const created = await store.createOrReuseDraft(input);
    const rows = await connection.getAll<{
      reference_code: unknown;
      sync_price_source: unknown;
    }>(
      `SELECT reference_code, sync_price_source
       FROM local_order_lines
       WHERE order_guid = ?
       ORDER BY line_sequence`,
      [created.orderGuid],
    );
    assert.deepEqual(rows.map((row) => ({ ...row })), [
      { reference_code: "REF-P1", sync_price_source: 2 },
      { reference_code: null, sync_price_source: 4 },
    ]);

    const legacyCart = saleCart();
    const firstLine = legacyCart.lines[0];
    assert.ok(firstLine);
    const { syncProvenance: _syncProvenance, ...legacyFirstLine } =
      firstLine;
    assert.throws(
      () =>
        store.createOrReuseDraft({
          ...draftInput({ draftId: "draft-missing-provenance" }),
          cart: {
            ...legacyCart,
            lines: [
              legacyFirstLine,
              ...legacyCart.lines.slice(1),
            ],
          },
        }),
      /line sync provenance/i,
    );
    assert.equal(
      await scalar(
        connection,
        `SELECT COUNT(*) AS count
         FROM local_orders
         WHERE order_guid <> ?`,
        [created.orderGuid],
      ),
      0,
    );
  });
});

test("真实 SQLite：readDraft 始终投影当前订单和未被 reversal 抵消的活动 tender", async () => {
  await withDatabase("draft-current-projection", async (connection) => {
    await migrateFresh(connection);
    const store = new SqlitePaymentDraftRecoveryStore(
      connection,
      sequenceIds("order-projection", "audit-projection"),
      () => T2,
    );
    const input = draftInput({ draftId: "draft-current-projection" });
    const created = await store.createOrReuseDraft(input);
    assert.deepEqual(
      await store.readDraft(created.orderGuid, input.identity),
      {
        checkoutIntentId: input.draftId,
        orderGuid: created.orderGuid,
        cartRevision: input.cart.revision,
        state: "Draft",
        total: { currency: "AUD", cents: 900 },
        remaining: { currency: "AUD", cents: 900 },
        cancellableAfterReversal: false,
        tenders: [],
      },
    );

    await insertTender(
      connection,
      "projection-cash-source",
      created.orderGuid,
      "cash",
      300,
    );
    await insertTender(
      connection,
      "projection-card",
      created.orderGuid,
      "card",
      200,
    );
    let projected = await store.readDraft(created.orderGuid, input.identity);
    assert.equal(projected?.remaining.cents, 400);
    assert.deepEqual(
      projected?.tenders.map((tender) => [
        tender.tenderGuid,
        tender.method,
        tender.amount.cents,
      ]),
      [
        ["projection-card", "card", 200],
        ["projection-cash-source", "cash", 300],
      ],
    );

    await insertTender(
      connection,
      "projection-cash-reversal",
      created.orderGuid,
      "cash",
      -300,
    );
    await connection.run(
      `INSERT INTO payment_tender_reversal_links (
        order_guid, action_id, source_tender_guid,
        reversal_tender_guid, created_at_iso
      ) VALUES (?, 'projection-reversal', 'projection-cash-source',
        'projection-cash-reversal', ?)`,
      [created.orderGuid, T1],
    );
    projected = await store.readDraft(created.orderGuid, input.identity);
    assert.equal(projected?.remaining.cents, 700);
    assert.deepEqual(
      projected?.tenders.map((tender) => tender.tenderGuid),
      ["projection-card"],
    );

    await insertTender(
      connection,
      "projection-cash-final",
      created.orderGuid,
      "cash",
      700,
    );
    await connection.run(
      `UPDATE local_orders
       SET state = 'PendingSync', updated_at_iso = ?
       WHERE order_guid = ?`,
      [T2, created.orderGuid],
    );
    projected = await store.readDraft(created.orderGuid, input.identity);
    assert.equal(projected?.checkoutIntentId, input.draftId);
    assert.equal(projected?.cartRevision, input.cart.revision);
    assert.equal(projected?.state, "PendingSync");
    assert.equal(projected?.remaining.cents, 0);
    assert.equal(
      JSON.stringify(projected).includes("pricingState"),
      false,
    );
  });
});

test("真实 SQLite：Cancelled 零活动 tender 以 actionId 幂等关闭且重启后保留同一草稿投影", async () => {
  const folder = mkdtempSync(join(tmpdir(), "hb-pos-draft-cancel-close-"));
  const path = join(folder, "cancel-close.db");
  const connection = new SystemSqliteConnection(path);
  try {
    await migrateFresh(connection);
    const input = draftInput({
      draftId: "draft-cancelled-close",
      identity: {
        storeCode: "S-CANCEL",
        deviceCode: "D-CANCEL",
        cashierId: "C-CANCEL",
        cashierName: "Cancelled Cashier",
      },
    });
    const store = new SqlitePaymentDraftRecoveryStore(
      connection,
      sequenceIds("order-cancelled", "audit-cancelled"),
      () => T1,
    );
    const created = await store.createOrReuseDraft(input);
    await insertActionBinding(
      connection,
      created.orderGuid,
      "cancel-action",
      "cancel-attempt",
      "cancel-idempotency",
    );
    await insertAttempt(connection, {
      attemptId: "cancel-attempt",
      idempotencyKey: "cancel-idempotency",
      orderGuid: created.orderGuid,
      provider: "square",
      operation: "purchase",
      amountCents: 900,
      state: "Cancelled",
    });
    const closed = await store.closeCancelledDraft({
      actionId: "cancel-action",
      orderGuid: created.orderGuid,
      actor: paymentAuditActor(),
      ...input.identity,
    });
    assert.equal(closed.replayed, false);
    assert.deepEqual(closed.draft, {
      checkoutIntentId: input.draftId,
      orderGuid: created.orderGuid,
      cartRevision: input.cart.revision,
      state: "Draft",
      total: { currency: "AUD", cents: 900 },
      remaining: { currency: "AUD", cents: 900 },
      cancellableAfterReversal: false,
      tenders: [],
    });
    assert.equal(await store.findBlockingRecovery(input.identity), null);
    assert.equal(
      await scalar(
        connection,
        `SELECT COUNT(*) AS count
         FROM payment_order_draft_bindings
         WHERE order_guid = ? AND state = 'CancelledClosed'
           AND close_action_id = 'cancel-action'
           AND close_attempt_id = 'cancel-attempt'`,
        [created.orderGuid],
      ),
      1,
    );
    const closeAudit = await connection.getFirst<{
      payload_json: unknown;
    }>(
      `SELECT payload_json
       FROM audit_events
       WHERE event_type = 'PAYMENT_DRAFT_CANCELLED_CLOSED'
         AND correlation_id = ?`,
      ["cancel-action"],
    );
    const closePayload = JSON.parse(String(closeAudit?.payload_json));
    assert.equal(closePayload.requestingCashierId, "cashier-actor");
    assert.equal(closePayload.requestingCashierName, "Actor Alice");
    assert.equal(closePayload.requestingUserGuid, "user-guid-actor");
    assert.equal(
      await scalar(
        connection,
        `SELECT COUNT(*) AS count
         FROM local_orders order_row
         INNER JOIN local_order_lines line
           ON line.order_guid = order_row.order_guid
         INNER JOIN payment_attempts attempt
           ON attempt.order_guid = order_row.order_guid
         INNER JOIN payment_action_bindings action_binding
           ON action_binding.order_guid = order_row.order_guid
         WHERE order_row.order_guid = ?`,
        [created.orderGuid],
      ),
      input.cart.lines.length,
    );
    await connection.close();

    const reopened = new SystemSqliteConnection(path);
    const reopenedStore = new SqlitePaymentDraftRecoveryStore(
      reopened,
      sequenceIds("unused-order", "unused-audit"),
      () => T2,
    );
    const replay = await reopenedStore.closeCancelledDraft({
      actionId: "cancel-action",
      orderGuid: created.orderGuid,
      actor: paymentAuditActor(),
      ...input.identity,
    });
    assert.equal(replay.replayed, true);
    assert.deepEqual(
      await reopenedStore.readDraft(created.orderGuid, input.identity),
      closed.draft,
    );
    await assert.rejects(
      () => reopenedStore.closeCancelledDraft({
        actionId: "different-cancel-action",
        orderGuid: created.orderGuid,
        actor: paymentAuditActor(),
        ...input.identity,
      }),
      /different immutable action/,
    );
    await assert.rejects(
      () => insertActionBinding(
        reopened,
        created.orderGuid,
        "late-payment-action",
        "late-payment-attempt",
        "late-payment-idempotency",
      ),
      /PAYMENT_ORDER_DRAFT_CANCELLED_CLOSED/,
    );
    await reopened.close();
  } finally {
    try { await connection.close(); } catch { /* already closed */ }
    rmSync(folder, { recursive: true, force: true });
  }
});

test("真实 SQLite：Cancelled close 遇到活动 tender、非 Cancelled 或其他 blocking attempt 一律失败关闭", async () => {
  await withDatabase("draft-cancel-close-guards", async (connection) => {
    await migrateFresh(connection);
    const store = new SqlitePaymentDraftRecoveryStore(
      connection,
      sequenceIds("order-cancel-guard", "audit-cancel-guard"),
      () => T2,
    );
    const withTender = draftInput({
      draftId: "draft-cancel-with-tender",
      identity: {
        storeCode: "S-CANCEL-TENDER",
        deviceCode: "D-CANCEL-TENDER",
        cashierId: "C",
        cashierName: "Cashier",
      },
    });
    const tenderOrder = await store.createOrReuseDraft(withTender);
    await insertActionBinding(
      connection,
      tenderOrder.orderGuid,
      "cancel-with-tender",
      "attempt-with-tender",
      "idempotency-with-tender",
    );
    await insertAttempt(connection, {
      attemptId: "attempt-with-tender",
      idempotencyKey: "idempotency-with-tender",
      orderGuid: tenderOrder.orderGuid,
      provider: "square",
      operation: "purchase",
      amountCents: 900,
      state: "Cancelled",
    });
    await insertTender(
      connection,
      "active-cash-tender",
      tenderOrder.orderGuid,
      "cash",
      100,
    );
    await assert.rejects(
      () => store.closeCancelledDraft({
        actionId: "cancel-with-tender",
        orderGuid: tenderOrder.orderGuid,
        actor: paymentAuditActor(),
        ...withTender.identity,
      }),
      /active positive tender/,
    );

    const wrongState = draftInput({
      draftId: "draft-cancel-wrong-state",
      identity: {
        storeCode: "S-CANCEL-STATE",
        deviceCode: "D-CANCEL-STATE",
        cashierId: "C",
        cashierName: "Cashier",
      },
    });
    const wrongStateOrder = await store.createOrReuseDraft(wrongState);
    await insertActionBinding(
      connection,
      wrongStateOrder.orderGuid,
      "declined-action",
      "declined-attempt",
      "declined-idempotency",
    );
    await insertAttempt(connection, {
      attemptId: "declined-attempt",
      idempotencyKey: "declined-idempotency",
      orderGuid: wrongStateOrder.orderGuid,
      provider: "square",
      operation: "purchase",
      amountCents: 900,
      state: "Declined",
    });
    await assert.rejects(
      () => store.closeCancelledDraft({
        actionId: "declined-action",
        orderGuid: wrongStateOrder.orderGuid,
        actor: paymentAuditActor(),
        ...wrongState.identity,
      }),
      /identity are inconsistent/,
    );

    const blocked = draftInput({
      draftId: "draft-cancel-other-blocking",
      identity: {
        storeCode: "S-CANCEL-BLOCK",
        deviceCode: "D-CANCEL-BLOCK",
        cashierId: "C",
        cashierName: "Cashier",
      },
    });
    const blockedOrder = await store.createOrReuseDraft(blocked);
    await insertActionBinding(
      connection,
      blockedOrder.orderGuid,
      "cancel-primary",
      "cancel-primary-attempt",
      "cancel-primary-idempotency",
    );
    await insertAttempt(connection, {
      attemptId: "cancel-primary-attempt",
      idempotencyKey: "cancel-primary-idempotency",
      orderGuid: blockedOrder.orderGuid,
      provider: "square",
      operation: "purchase",
      amountCents: 900,
      state: "Cancelled",
    });
    await insertActionBinding(
      connection,
      blockedOrder.orderGuid,
      "other-blocking",
      "other-blocking-attempt",
      "other-blocking-idempotency",
    );
    await insertAttempt(connection, {
      attemptId: "other-blocking-attempt",
      idempotencyKey: "other-blocking-idempotency",
      orderGuid: blockedOrder.orderGuid,
      provider: "linkly-cloud",
      operation: "purchase",
      amountCents: 900,
      state: "Unknown",
    });
    await assert.rejects(
      () => store.closeCancelledDraft({
        actionId: "cancel-primary",
        orderGuid: blockedOrder.orderGuid,
        actor: paymentAuditActor(),
        ...blocked.identity,
      }),
      /Another blocking payment attempt/,
    );
    assert.equal(
      await scalar(
        connection,
        `SELECT COUNT(*) AS count
         FROM payment_order_draft_bindings
         WHERE order_guid = ? AND state = 'Active'`,
        [blockedOrder.orderGuid],
      ),
      1,
    );
  });
});

test("真实 SQLite：退货在线退款 attempt 未决（Created/Unknown/Approved 未入 tender）不被销售支付恢复当作阻塞单，销售 attempt 仍正常阻塞", async () => {
  // 回归：退货单在 prepare 阶段即以 Draft 写入 local_orders，退款 attempt 的 OrderGuid 是退货单；
  // 销售支付恢复曾把它当成阻塞单去重建购物车，抛 "Payment recovery order has no lines."，
  // 冷启动、销售页恢复探针全部失败，而退货恢复又要等运行时起来，形成死锁。
  await withDatabase("return-refund-not-sale-blocking", async (connection) => {
    await migrateFresh(connection);
    const vault = new SqliteReturnCapacityVault(connection, encryptor, () => T0);
    await vault.seedOrLoad({
      capacityId: "return-capacity-cash",
      originalOrderGuid: "original-return-order",
      method: "cash",
      originalAmountCents: 500,
      remainingAmountCents: 500,
      protectedContext: null,
      observedAtIso: T0,
    });
    await vault.seedOrLoad({
      capacityId: "return-capacity-card",
      originalOrderGuid: "original-return-order",
      method: "card",
      originalAmountCents: 500,
      remainingAmountCents: 500,
      protectedContext: { paymentId: "PAYMENT-ID", rfn: "RFN" },
      observedAtIso: T0,
    });
    let tenderId = 0;
    let auditId = 0;
    const ledger = new SqliteReturnExecutionLedger(
      connection,
      encryptor,
      {
        createTenderGuid: () => `return-tender-${++tenderId}`,
        createAuditEventId: () => `return-audit-${++auditId}`,
      },
      () => T2,
    );
    const draft = durableReturnDraft();
    await ledger.prepareOrLoad(draft);
    const scope = {
      storeCode: draft.identity.storeCode,
      deviceCode: draft.identity.deviceCode,
    };
    const store = new SqlitePaymentDraftRecoveryStore(
      connection,
      sequenceIds("sale-order", "sale-audit"),
      () => T2,
    );
    assert.equal(await store.findBlockingRecovery(scope), null);

    assert.equal(
      await ledger.markAllocationSubmitted({
        actionId: draft.actionId,
        allocationId: "return-allocation-card",
      }),
      true,
    );
    await insertActionBinding(
      connection,
      draft.returnOrderGuid,
      "return-provider-action",
      "return-payment-attempt",
      "return-payment-idempotency",
      ["square", "refund", "AUD", -300],
    );
    await insertAttempt(connection, {
      attemptId: "return-payment-attempt",
      idempotencyKey: "return-payment-idempotency",
      orderGuid: draft.returnOrderGuid,
      provider: "square",
      operation: "refund",
      amountCents: -300,
      state: "Created",
    });
    // provider 调用前 attempt 已 Created、但尚未回写 allocation 绑定的崩溃窗口，同样不得误判。
    assert.equal(await store.findBlockingRecovery(scope), null);

    await connection.run(
      "UPDATE payment_attempts SET state = 'Unknown' WHERE attempt_id = ?",
      ["return-payment-attempt"],
    );
    assert.equal(
      await ledger.bindAllocationAttempt({
        actionId: draft.actionId,
        allocationId: "return-allocation-card",
        attemptKind: "payment-provider",
        externalActionId: "return-provider-action",
        durableAttemptId: "return-payment-attempt",
      }),
      true,
    );
    assert.equal(
      await ledger.recordAllocationOutcome({
        actionId: draft.actionId,
        allocationId: "return-allocation-card",
        expectedStatuses: ["submitted"],
        status: "unknown",
        protectedRecoveryKey: "RECOVERY-KEY",
      }),
      true,
    );
    await ledger.markActionUnknown({ actionId: draft.actionId });
    assert.equal(await store.findBlockingRecovery(scope), null);

    await connection.run(
      "UPDATE payment_attempts SET state = 'Approved' WHERE attempt_id = ?",
      ["return-payment-attempt"],
    );
    assert.equal(
      await scalar(
        connection,
        "SELECT COUNT(*) AS count FROM order_tenders WHERE order_guid = ?",
        [draft.returnOrderGuid],
      ),
      0,
    );
    assert.equal(await store.findBlockingRecovery(scope), null);

    // 退货自身的恢复入口不受影响：仍按 return_actions 发现这笔未决退货。
    const recoverable = await ledger.listRecoverable({
      ...scope,
      cashierId: draft.identity.cashierId,
      sessionEpoch: "new-session-epoch",
    });
    assert.equal(recoverable.length, 1);
    assert.equal(recoverable[0]?.returnOrderGuid, draft.returnOrderGuid);
    assert.equal(recoverable[0]?.status, "unknown");

    // 同机销售支付仍被正确识别：草稿与 purchase attempt 依旧是唯一阻塞单。
    const saleInput = draftInput({
      draftId: "sale-draft-beside-return",
      identity: {
        storeCode: scope.storeCode,
        deviceCode: scope.deviceCode,
        cashierId: draft.identity.cashierId,
        cashierName: draft.identity.cashierName,
      },
    });
    const sale = await store.createOrReuseDraft(saleInput);
    const prepared = await store.findBlockingRecovery(scope);
    assert.equal(prepared?.kind, "DraftPrepared");
    assert.equal(prepared?.orderGuid, sale.orderGuid);
    await insertActionBinding(
      connection,
      sale.orderGuid,
      "sale-pay-action",
      "sale-payment-attempt",
      "sale-payment-idempotency",
    );
    await insertAttempt(connection, {
      attemptId: "sale-payment-attempt",
      idempotencyKey: "sale-payment-idempotency",
      orderGuid: sale.orderGuid,
      provider: "square",
      operation: "purchase",
      amountCents: 900,
      state: "Unknown",
    });
    const blocking = await store.findBlockingRecovery(scope);
    assert.equal(blocking?.kind, "AttemptBlocking");
    if (blocking?.kind === "AttemptBlocking") {
      assert.equal(blocking.orderGuid, sale.orderGuid);
      assert.equal(blocking.attemptId, "sale-payment-attempt");
      assert.equal(blocking.operation, "purchase");
      assert.equal(blocking.state, "Unknown");
    }
    // 已有阻塞销售单时，新的销售草稿仍必须被挡回原单。
    await assert.rejects(
      () => store.createOrReuseDraft(
        draftInput({
          draftId: "sale-draft-second",
          identity: saleInput.identity,
        }),
      ),
      /must resume existing order/,
    );
  });
});

test("真实 SQLite：退货多 allocation 在 provider 前耐久绑定，Unknown 跨恢复冻结容量并以同一 OrderGuid 完成", async () => {
  await withDatabase("durable-return-ledger", async (connection) => {
    await migrateFresh(connection);
    const vault = new SqliteReturnCapacityVault(
      connection,
      encryptor,
      () => T0,
    );
    await vault.seedOrLoad({
      capacityId: "return-capacity-cash",
      originalOrderGuid: "original-return-order",
      method: "cash",
      originalAmountCents: 500,
      remainingAmountCents: 500,
      protectedContext: null,
      observedAtIso: T0,
    });
    await vault.seedOrLoad({
      capacityId: "return-capacity-card",
      originalOrderGuid: "original-return-order",
      method: "card",
      originalAmountCents: 500,
      remainingAmountCents: 500,
      protectedContext: {
        paymentId: "SECRET-PAYMENT-ID",
        rfn: "SECRET-RFN",
      },
      observedAtIso: T0,
    });
    let tenderId = 0;
    let auditId = 0;
    const ledger = new SqliteReturnExecutionLedger(
      connection,
      encryptor,
      {
        createTenderGuid: () => `return-tender-${++tenderId}`,
        createAuditEventId: () => `return-audit-${++auditId}`,
      },
      () => T2,
    );
    const baseDraft = durableReturnDraft();
    const draft = {
      ...baseDraft,
      identity: {
        ...baseDraft.identity,
        userGuid: "return-user-guid",
      },
    };
    const [prepared, replay] = await Promise.all([
      ledger.prepareOrLoad(draft),
      ledger.prepareOrLoad(draft),
    ]);
    assert.equal(prepared.returnOrderGuid, "return-order-guid-1");
    assert.equal(replay.returnOrderGuid, prepared.returnOrderGuid);
    assert.equal(prepared.status, "processing");
    assert.equal(
      await scalar(
        connection,
        `SELECT COUNT(*) AS count
         FROM local_orders
         WHERE order_guid = 'return-order-guid-1' AND state = 'Draft'`,
      ),
      1,
    );
    assert.equal(
      await scalar(
        connection,
        `SELECT COUNT(*) AS count
         FROM local_order_lines
         WHERE order_guid = 'return-order-guid-1'`,
      ),
      1,
    );

    await assert.rejects(
      () => ledger.prepareOrLoad({
        ...draft,
        actionId: "return-action-capacity-race",
        requestFingerprint: "return-fingerprint-race",
        returnOrderGuid: "return-order-guid-race",
        actionRecoveryToken: "return-recovery-race",
        identity: {
          ...draft.identity,
          deviceCode: "D-RETURN-OTHER",
        },
        lines: draft.lines.map((line) => ({
          ...line,
          lineId: "return-line-race",
        })),
        allocations: draft.allocations.map((allocation, index) => ({
          ...allocation,
          allocationId: `return-allocation-race-${index}`,
          externalAttemptId:
            allocation.externalAttemptId === null
              ? null
              : `return-external-race-${index}`,
        })),
      }),
      /capacity is exhausted or reserved/,
    );

    assert.equal(
      await ledger.markAllocationSubmitted({
        actionId: draft.actionId,
        allocationId: "return-allocation-cash",
      }),
      true,
    );
    assert.equal(
      await ledger.recordAllocationOutcome({
        actionId: draft.actionId,
        allocationId: "return-allocation-cash",
        expectedStatuses: ["submitted"],
        status: "completed",
        protectedRecoveryKey: null,
      }),
      true,
    );
    assert.equal(
      await ledger.recordAllocationOutcome({
        actionId: draft.actionId,
        allocationId: "return-allocation-cash",
        expectedStatuses: ["submitted"],
        status: "completed",
        protectedRecoveryKey: null,
      }),
      true,
    );
    assert.equal(
      await scalar(
        connection,
        `SELECT COUNT(*) AS count
         FROM order_tenders
         WHERE order_guid = 'return-order-guid-1'`,
      ),
      0,
    );
    assert.equal(
      await ledger.markAllocationSubmitted({
        actionId: draft.actionId,
        allocationId: "return-allocation-card",
      }),
      true,
    );
    await insertActionBinding(
      connection,
      draft.returnOrderGuid,
      "return-provider-action",
      "return-payment-attempt",
      "return-payment-idempotency",
      ["square", "refund", "AUD", -300],
    );
    await insertAttempt(connection, {
      attemptId: "return-payment-attempt",
      idempotencyKey: "return-payment-idempotency",
      orderGuid: draft.returnOrderGuid,
      provider: "square",
      operation: "refund",
      amountCents: -300,
      state: "Unknown",
    });
    assert.equal(
      await ledger.bindAllocationAttempt({
        actionId: draft.actionId,
        allocationId: "return-allocation-card",
        attemptKind: "payment-provider",
        externalActionId: "return-provider-action",
        durableAttemptId: "return-payment-attempt",
      }),
      true,
    );
    assert.equal(
      await ledger.recordAllocationOutcome({
        actionId: draft.actionId,
        allocationId: "return-allocation-card",
        expectedStatuses: ["submitted"],
        status: "unknown",
        protectedRecoveryKey: "SECRET-RECOVERY-KEY",
      }),
      true,
    );
    await ledger.markActionUnknown({ actionId: draft.actionId });
    const unknown = await ledger.load(draft.actionId);
    assert.equal(unknown?.status, "unknown");
    assert.equal(
      unknown?.allocations[1]?.protectedRecoveryKey,
      "SECRET-RECOVERY-KEY",
    );
    const rawRecovery = await connection.getFirst<{ value: unknown }>(
      `SELECT HEX(protected_recovery_ciphertext) AS value
       FROM return_action_allocations
       WHERE action_id = ? AND allocation_id = 'return-allocation-card'`,
      [draft.actionId],
    );
    assert.equal(
      String(rawRecovery?.value).includes("SECRET-RECOVERY-KEY"),
      false,
    );
    assert.equal(
      JSON.stringify(
        await connection.getAll<{ name: unknown }>(
          "PRAGMA table_info('return_tender_capacities')",
        ),
      ).includes("payment_id"),
      false,
    );

    const recoverable = await ledger.listRecoverable({
      storeCode: draft.identity.storeCode,
      deviceCode: draft.identity.deviceCode,
      cashierId: draft.identity.cashierId,
      sessionEpoch: "new-session-epoch",
    });
    assert.deepEqual(recoverable, [
      {
        actionId: draft.actionId,
        returnOrderGuid: draft.returnOrderGuid,
        sourceKind: "receipt",
        totalRefundCents: 500,
        status: "unknown",
        lines: [
          {
            sourceKind: "receipt",
            itemNumber: "I-RETURN",
            displayName: "Returned Product",
            quantity: 1,
            unitRefundCents: 500,
            signedAmountCents: -500,
            syncProvenance: {
              referenceCode: "REF-P-RETURN",
              priceSource: 0,
            },
          },
        ],
      },
    ]);
    const recoveryJson = JSON.stringify(recoverable);
    for (const secret of [
      "SECRET-RECOVERY-KEY",
      "SECRET-PAYMENT-ID",
      "SECRET-RFN",
      draft.actionRecoveryToken,
      "return-capacity-card",
      "return-capacity-cash",
      "protected_recovery_ciphertext",
    ]) {
      assert.equal(
        recoveryJson.includes(secret),
        false,
        `recoverable projection leaked ${secret}`,
      );
    }
    assert.deepEqual(
      await ledger.listRecoverable({
        storeCode: draft.identity.storeCode,
        deviceCode: draft.identity.deviceCode,
        cashierId: "OTHER-CASHIER",
        sessionEpoch: "new-session-epoch",
      }),
      [],
    );
    assert.deepEqual(
      await ledger.listRecoverable({
        storeCode: "OTHER-STORE",
        deviceCode: draft.identity.deviceCode,
        cashierId: draft.identity.cashierId,
        sessionEpoch: "new-session-epoch",
      }),
      [],
    );
    assert.deepEqual(
      await ledger.listRecoverable({
        storeCode: draft.identity.storeCode,
        deviceCode: "OTHER-DEVICE",
        cashierId: draft.identity.cashierId,
        sessionEpoch: "new-session-epoch",
      }),
      [],
    );

    assert.equal(
      await ledger.resumeUnknownAction({ actionId: draft.actionId }),
      true,
    );
    await connection.run(
      `UPDATE payment_attempts
       SET state = 'Approved', updated_at_iso = ?
       WHERE attempt_id = 'return-payment-attempt'`,
      [T2],
    );
    assert.equal(
      await ledger.recordAllocationOutcome({
        actionId: draft.actionId,
        allocationId: "return-allocation-card",
        expectedStatuses: ["unknown"],
        status: "completed",
        protectedRecoveryKey: null,
      }),
      true,
    );
    assert.equal(
      await ledger.recordAllocationOutcome({
        actionId: draft.actionId,
        allocationId: "return-allocation-card",
        expectedStatuses: ["unknown"],
        status: "completed",
        protectedRecoveryKey: null,
      }),
      true,
    );
    assert.equal(
      await scalar(
        connection,
        `SELECT COUNT(*) AS count
         FROM order_tenders
         WHERE order_guid = 'return-order-guid-1'
           AND payment_attempt_id = 'return-payment-attempt'`,
      ),
      1,
    );
    const completion = durableReturnCompletion(draft);
    assert.throws(
      () =>
        ledger.completeAtomically({
          ...completion,
          fulfilment: {
            ...completion.fulfilment,
            receiptKind: "refund-voucher",
          },
        }),
      /fulfilment policy/,
    );
    assert.throws(
      () =>
        ledger.completeAtomically({
          ...completion,
          fulfilment: {
            ...completion.fulfilment,
            receiptKind: "voucher" as never,
          },
      }),
      /receipt kind/,
    );
    assert.throws(
      () =>
        ledger.completeAtomically({
          ...completion,
          plan: {
            ...completion.plan,
            allocations: [
              {
                method: "voucher",
                signedAmountCents: -200,
                originalCapacityId: "voucher-capacity-a",
                originalOrderGuid: "original-return-order",
                offlineCashProof: null,
              },
              {
                method: "voucher",
                signedAmountCents: -300,
                originalCapacityId: "voucher-capacity-b",
                originalOrderGuid: "original-return-order",
                offlineCashProof: null,
              },
            ],
          },
          // 多张退款券已允许（逐张出票），但必须打印券面，不能按旧口径只开钱箱。
          fulfilment: {
            printJobId: null,
            drawerEventId: null,
            receiptKind: "none",
            drawerRequired: false,
          },
        }),
      /fulfilment policy/,
    );
    const completed = await ledger.completeAtomically(completion);
    assert.equal(completed.status, "completed");
    assert.equal(completed.returnOrderGuid, draft.returnOrderGuid);
    const completionAudit = await connection.getFirst<{
      payload_json: unknown;
    }>(
      `SELECT payload_json
       FROM audit_events
       WHERE event_type = 'RETURN_ORDER_COMPLETED'
         AND order_guid = ?`,
      [draft.returnOrderGuid],
    );
    assert.deepEqual(
      JSON.parse(String(completionAudit?.payload_json)),
      {
        action: "return-order-completed",
        requestingCashierId: draft.identity.cashierId,
        requestingCashierName: draft.identity.cashierName,
        requestingUserGuid: "return-user-guid",
        sourceKind: draft.plan.sourceKind,
        totalRefundCents: draft.plan.totalRefundCents,
        lineCount: draft.lines.length,
        allocations: draft.allocations.map((allocation) => ({
          method: allocation.method,
          amountCents: allocation.signedAmountCents,
        })),
      },
    );
    assert.equal(
      (await ledger.completeAtomically(completion)).status,
      "completed",
    );
    assert.equal(
      (await ledger.load(draft.actionId))?.allocations[1]
        ?.protectedRecoveryKey,
      "SECRET-RECOVERY-KEY",
    );
    await assert.rejects(
      () => ledger.completeAtomically({
        ...completion,
        outbox: {
          ...completion.outbox,
          messageId: "different-replayed-outbox",
        },
      }),
      /outbox was replayed differently/,
    );
    assert.equal(
      await scalar(
        connection,
        `SELECT COUNT(*) AS count
         FROM local_orders
         WHERE order_guid = 'return-order-guid-1'
           AND state = 'PendingSync'
           AND actual_amount_cents = -500`,
      ),
      1,
    );
    assert.deepEqual(
      {
        ...(await connection.getFirst<{
          receipt_kind: unknown;
          print_job_id: unknown;
          drawer_event_id: unknown;
        }>(
          `SELECT receipt_kind, print_job_id, drawer_event_id
           FROM return_fulfilment_plans
           WHERE action_id = ?`,
          [draft.actionId],
        )),
      },
      {
        receipt_kind: "refund-receipt",
        print_job_id: completion.fulfilment.printJobId,
        drawer_event_id: completion.fulfilment.drawerEventId,
      },
    );
    assert.equal(
      await scalar(
        connection,
        `SELECT COUNT(*) AS count
         FROM order_tenders
         WHERE order_guid = 'return-order-guid-1'
           AND amount_cents < 0`,
      ),
      2,
    );
    assert.equal(
      Number(
        (await vault.get("return-capacity-card"))?.remainingAmountCents,
      ),
      200,
    );
    assert.equal(
      Number(
        (await vault.get("return-capacity-cash"))?.remainingAmountCents,
      ),
      300,
    );
    assert.equal(
      Number(
        (
          await connection.getFirst<{ value: unknown }>(
            `SELECT remaining_quantity AS value
             FROM return_capacity
             WHERE return_source_key = 'return-source-1'`,
          )
        )?.value,
      ),
      0,
    );
    assert.equal(
      await scalar(
        connection,
        `SELECT COUNT(*) AS count
         FROM outbox_messages
         WHERE aggregate_id = 'return-order-guid-1'
           AND kind = 'order-sync'`,
      ),
      1,
    );
    assert.deepEqual(
      JSON.parse(
        String(
          (
            await connection.getFirst<{ payload_json: unknown }>(
              `SELECT payload_json
               FROM outbox_messages
               WHERE aggregate_id = 'return-order-guid-1'
                 AND kind = 'order-sync'`,
            )
          )?.payload_json,
        ),
      ),
      { orderGuid: "return-order-guid-1" },
    );
    assert.equal(
      await scalar(
        connection,
        `SELECT COUNT(*) AS count
         FROM return_fulfilment_plans
         WHERE return_order_guid = 'return-order-guid-1'
           AND materialized_at_iso IS NULL`,
      ),
      1,
    );
    const fulfilmentPlans = new SqliteReturnFulfilmentPlanStore(
      connection,
      encryptor,
      () => T2,
    );
    assert.equal((await fulfilmentPlans.listPending())[0]?.actionId, draft.actionId);
    const materialization = {
      actionId: draft.actionId,
      expectedReturnOrderGuid: draft.returnOrderGuid,
      expectedPrintJobId: completion.fulfilment.printJobId,
      expectedDrawerEventId: completion.fulfilment.drawerEventId,
      printerId: "return-printer-1",
      receiptBytes: new Uint8Array([0x1b, 0x40, 0x0a]),
      drawerReason: "cash-return",
    };
    assert.equal(
      (await fulfilmentPlans.materialize(materialization)).materializedAtIso,
      T2,
    );
    assert.equal(
      (await fulfilmentPlans.materialize(materialization)).materializedAtIso,
      T2,
    );
    assert.equal((await fulfilmentPlans.listPending()).length, 0);
    assert.equal(
      await scalar(
        connection,
        `SELECT COUNT(*) AS count
         FROM print_jobs
         WHERE job_id = ? AND state = 'Queued'`,
        [completion.fulfilment.printJobId],
      ),
      1,
    );
    assert.equal(
      await scalar(
        connection,
        `SELECT COUNT(*) AS count
         FROM drawer_events
         WHERE event_id = ? AND state = 'Required'`,
        [completion.fulfilment.drawerEventId],
      ),
      1,
    );
    await assert.rejects(
      () =>
        fulfilmentPlans.materialize({
          ...materialization,
          receiptBytes: new Uint8Array([0x00]),
        }),
      /receipt bytes have diverged/,
    );
    const repositories = createSqliteRepositories(connection, {
      nowIso: () => T2,
      createLeaseId: () => "return-sync-lease",
      encryptor,
    });
    const [leased] = await repositories.outbox.leaseReady(1, 60);
    assert.equal(leased?.kind, "order-sync");
    assert.equal(leased?.aggregateId, draft.returnOrderGuid);
  });
});

test("真实 SQLite：原单金额不可整除数量时，全量退货按剩余金额收尾且部分数量仍严格校验单价", async () => {
  await withDatabase("durable-return-tail-rounding", async (connection) => {
    await migrateFresh(connection);
    const vault = new SqliteReturnCapacityVault(
      connection,
      encryptor,
      () => T0,
    );
    await vault.seedOrLoad({
      capacityId: "return-tail-capacity",
      originalOrderGuid: "original-return-order",
      method: "cash",
      originalAmountCents: 1_001,
      remainingAmountCents: 1_001,
      protectedContext: null,
      observedAtIso: T0,
    });
    const ledger = new SqliteReturnExecutionLedger(
      connection,
      encryptor,
      {
        createTenderGuid: () => "return-tail-tender",
        createAuditEventId: () => "return-tail-audit",
      },
      () => T2,
    );
    const base = durableReturnDraft({
      actionId: "return-tail-action",
      requestFingerprint: "return-tail-fingerprint",
      returnOrderGuid: "return-tail-order",
      actionRecoveryToken: "return-tail-recovery",
      returnSourceKey: "return-tail-source",
      capacityId: "return-tail-capacity",
      onlineCashOnly: true,
    });
    const fullTail = {
      ...base,
      plan: {
        ...base.plan,
        totalRefundCents: 1_001,
        lines: base.plan.lines.map((line) => ({
          ...line,
          quantity: 3,
          signedAmountCents: -1_001,
        })),
        allocations: base.plan.allocations.map((allocation) => ({
          ...allocation,
          signedAmountCents: -1_001,
        })),
      },
      lines: base.lines.map((line) => ({
        ...line,
        quantity: 3,
        unitRefundCents: 334,
        signedAmountCents: -1_001,
        availableQuantity: 3,
        remainingAmountCents: 1_001,
      })),
      allocations: base.allocations.map((allocation) => ({
        ...allocation,
        signedAmountCents: -1_001,
      })),
    };

    assert.throws(
      () =>
        ledger.prepareOrLoad({
          ...fullTail,
          actionId: "return-partial-mismatch-action",
          requestFingerprint: "return-partial-mismatch-fingerprint",
          returnOrderGuid: "return-partial-mismatch-order",
          actionRecoveryToken: "return-partial-mismatch-recovery",
          plan: {
            ...fullTail.plan,
            totalRefundCents: 667,
            lines: fullTail.plan.lines.map((line) => ({
              ...line,
              quantity: 2,
              signedAmountCents: -667,
            })),
            allocations: fullTail.plan.allocations.map((allocation) => ({
              ...allocation,
              signedAmountCents: -667,
            })),
          },
          lines: fullTail.lines.map((line) => ({
            ...line,
            quantity: 2,
            signedAmountCents: -667,
          })),
          allocations: fullTail.allocations.map((allocation) => ({
            ...allocation,
            signedAmountCents: -667,
          })),
        }),
      /line amount is inconsistent/,
    );

    const prepared = await ledger.prepareOrLoad(fullTail);
    assert.equal(prepared.lines[0]?.signedAmountCents, -1_001);
  });
});

test("真实 SQLite：双 provider 逐笔 Approved 即落 tender，写失败可重放且 final 不重复", async () => {
  await withDatabase("durable-return-two-provider", async (connection) => {
    await migrateFresh(connection);
    const vault = new SqliteReturnCapacityVault(
      connection,
      encryptor,
      () => T0,
    );
    await vault.seedOrLoad({
      capacityId: "return-capacity-provider-a",
      originalOrderGuid: "original-provider-order",
      method: "card",
      originalAmountCents: 200,
      remainingAmountCents: 200,
      protectedContext: { paymentId: "SECRET-PROVIDER-A" },
      observedAtIso: T0,
    });
    await vault.seedOrLoad({
      capacityId: "return-capacity-provider-b",
      originalOrderGuid: "original-provider-order",
      method: "card",
      originalAmountCents: 300,
      remainingAmountCents: 300,
      protectedContext: { rfn: "SECRET-PROVIDER-B" },
      observedAtIso: T0,
    });
    await insertOrder(connection, {
      orderGuid: "occupied-tender-order",
      sequence: 99,
      storeCode: "S-SEED",
      deviceCode: "D-SEED",
      cashierId: "C-SEED",
      amountCents: 1,
      state: "Synced",
      syncProvenance: TEST_SYNC_PROVENANCE,
    });
    await insertTender(
      connection,
      "occupied-provider-tender",
      "occupied-tender-order",
      "cash",
      1,
    );

    let tenderCall = 0;
    let auditCall = 0;
    const ledger = new SqliteReturnExecutionLedger(
      connection,
      encryptor,
      {
        createTenderGuid: () => {
          tenderCall += 1;
          return tenderCall === 1
            ? "occupied-provider-tender"
            : `two-provider-tender-${tenderCall}`;
        },
        createAuditEventId: () => `two-provider-audit-${++auditCall}`,
      },
      () => T2,
    );
    const draft = durableTwoProviderReturnDraft();
    await ledger.prepareOrLoad(draft);

    await ledger.markAllocationSubmitted({
      actionId: draft.actionId,
      allocationId: "provider-allocation-a",
    });
    await insertActionBinding(
      connection,
      draft.returnOrderGuid,
      "provider-action-a",
      "provider-attempt-a",
      "provider-idempotency-a",
      ["square", "refund", "AUD", -200],
    );
    await insertAttempt(connection, {
      attemptId: "provider-attempt-a",
      idempotencyKey: "provider-idempotency-a",
      orderGuid: draft.returnOrderGuid,
      provider: "square",
      operation: "refund",
      amountCents: -200,
      state: "Approved",
    });
    await ledger.bindAllocationAttempt({
      actionId: draft.actionId,
      allocationId: "provider-allocation-a",
      attemptKind: "payment-provider",
      externalActionId: "provider-action-a",
      durableAttemptId: "provider-attempt-a",
    });
    await assert.rejects(
      () =>
        ledger.recordAllocationOutcome({
          actionId: draft.actionId,
          allocationId: "provider-allocation-a",
          expectedStatuses: ["submitted"],
          status: "completed",
          protectedRecoveryKey: null,
        }),
      /UNIQUE constraint failed/,
    );
    assert.equal(
      (await ledger.load(draft.actionId))?.allocations[0]?.status,
      "submitted",
    );
    assert.equal(
      await scalar(
        connection,
        `SELECT COUNT(*) AS count
         FROM order_tenders
         WHERE payment_attempt_id = 'provider-attempt-a'`,
      ),
      0,
    );
    assert.equal(
      await ledger.recordAllocationOutcome({
        actionId: draft.actionId,
        allocationId: "provider-allocation-a",
        expectedStatuses: ["submitted"],
        status: "completed",
        protectedRecoveryKey: null,
      }),
      true,
    );
    assert.equal(
      await ledger.recordAllocationOutcome({
        actionId: draft.actionId,
        allocationId: "provider-allocation-a",
        expectedStatuses: ["submitted"],
        status: "completed",
        protectedRecoveryKey: null,
      }),
      true,
    );
    const repositories = createSqliteRepositories(connection, {
      nowIso: () => T2,
      createLeaseId: () => "unused-return-lease",
      encryptor,
    });
    assert.equal(
      await repositories.payments.findBlocking(draft.returnOrderGuid),
      null,
    );

    await ledger.markAllocationSubmitted({
      actionId: draft.actionId,
      allocationId: "provider-allocation-b",
    });
    await insertActionBinding(
      connection,
      draft.returnOrderGuid,
      "provider-action-b",
      "provider-attempt-b",
      "provider-idempotency-b",
      ["linkly-cloud", "refund", "AUD", -300],
    );
    await insertAttempt(connection, {
      attemptId: "provider-attempt-b",
      idempotencyKey: "provider-idempotency-b",
      orderGuid: draft.returnOrderGuid,
      provider: "linkly-cloud",
      operation: "refund",
      amountCents: -300,
      state: "Approved",
    });
    await ledger.bindAllocationAttempt({
      actionId: draft.actionId,
      allocationId: "provider-allocation-b",
      attemptKind: "payment-provider",
      externalActionId: "provider-action-b",
      durableAttemptId: "provider-attempt-b",
    });
    await assert.rejects(
      () =>
        ledger.bindAllocationAttempt({
          actionId: draft.actionId,
          allocationId: "provider-allocation-a",
          attemptKind: "payment-provider",
          externalActionId: "provider-action-b",
          durableAttemptId: "provider-attempt-b",
        }),
      /already bound to another durable attempt/,
    );
    assert.equal(
      (
        await repositories.payments.findBlocking(draft.returnOrderGuid)
      )?.attemptId,
      "provider-attempt-b",
    );
    assert.equal(
      await ledger.recordAllocationOutcome({
        actionId: draft.actionId,
        allocationId: "provider-allocation-b",
        expectedStatuses: ["submitted"],
        status: "completed",
        protectedRecoveryKey: null,
      }),
      true,
    );
    assert.equal(
      await ledger.recordAllocationOutcome({
        actionId: draft.actionId,
        allocationId: "provider-allocation-b",
        expectedStatuses: ["submitted"],
        status: "completed",
        protectedRecoveryKey: null,
      }),
      true,
    );
    assert.equal(
      await repositories.payments.findBlocking(draft.returnOrderGuid),
      null,
    );
    assert.equal(
      await scalar(
        connection,
        `SELECT COUNT(*) AS count
         FROM order_tenders
         WHERE order_guid = ? AND payment_attempt_id IS NOT NULL`,
        [draft.returnOrderGuid],
      ),
      2,
    );

    const completion = durableReturnCompletion(draft);
    assert.equal(
      (await ledger.completeAtomically(completion)).status,
      "completed",
    );
    assert.equal(
      (await ledger.completeAtomically(completion)).status,
      "completed",
    );
    assert.equal(
      await scalar(
        connection,
        `SELECT COUNT(*) AS count
         FROM order_tenders
         WHERE order_guid = ?`,
        [draft.returnOrderGuid],
      ),
      2,
    );
    assert.equal(
      await scalar(
        connection,
        `SELECT COUNT(*) AS count
         FROM return_tender_attempt_bindings
         WHERE action_id = ?`,
        [draft.actionId],
      ),
      2,
    );
  });
});

test("真实 SQLite：provider 退款金额或签名 provider 不一致时拒绝 allocation 绑定", async () => {
  await withDatabase("durable-return-provider-mismatch", async (connection) => {
    await migrateFresh(connection);
    const vault = new SqliteReturnCapacityVault(
      connection,
      encryptor,
      () => T0,
    );
    for (const [capacityId, amount] of [
      ["return-capacity-provider-a", 200],
      ["return-capacity-provider-b", 300],
    ] as const) {
      await vault.seedOrLoad({
        capacityId,
        originalOrderGuid: "original-provider-order",
        method: "card",
        originalAmountCents: amount,
        remainingAmountCents: amount,
        protectedContext: { providerRef: `SECRET-${capacityId}` },
        observedAtIso: T0,
      });
    }
    const ledger = new SqliteReturnExecutionLedger(
      connection,
      encryptor,
      {
        createTenderGuid: () => "unused-mismatch-tender",
        createAuditEventId: () => "unused-mismatch-audit",
      },
      () => T2,
    );
    const draft = durableTwoProviderReturnDraft({
      actionId: "provider-mismatch-action",
      returnOrderGuid: "provider-mismatch-order",
      returnSourceKey: "provider-mismatch-source",
    });
    await ledger.prepareOrLoad(draft);
    for (const allocation of draft.allocations) {
      await ledger.markAllocationSubmitted({
        actionId: draft.actionId,
        allocationId: allocation.allocationId,
      });
    }
    await insertActionBinding(
      connection,
      draft.returnOrderGuid,
      "mismatch-amount-action",
      "mismatch-amount-attempt",
      "mismatch-amount-idempotency",
      ["square", "refund", "AUD", -199],
    );
    await insertAttempt(connection, {
      attemptId: "mismatch-amount-attempt",
      idempotencyKey: "mismatch-amount-idempotency",
      orderGuid: draft.returnOrderGuid,
      provider: "square",
      operation: "refund",
      amountCents: -200,
      state: "Approved",
    });
    await assert.rejects(
      () =>
        ledger.bindAllocationAttempt({
          actionId: draft.actionId,
          allocationId: "provider-allocation-a",
          attemptKind: "payment-provider",
          externalActionId: "mismatch-amount-action",
          durableAttemptId: "mismatch-amount-attempt",
        }),
      /identity is inconsistent/,
    );

    await insertActionBinding(
      connection,
      draft.returnOrderGuid,
      "mismatch-provider-action",
      "mismatch-provider-attempt",
      "mismatch-provider-idempotency",
      ["square", "refund", "AUD", -300],
    );
    await insertAttempt(connection, {
      attemptId: "mismatch-provider-attempt",
      idempotencyKey: "mismatch-provider-idempotency",
      orderGuid: draft.returnOrderGuid,
      provider: "linkly-cloud",
      operation: "refund",
      amountCents: -300,
      state: "Approved",
    });
    await assert.rejects(
      () =>
        ledger.bindAllocationAttempt({
          actionId: draft.actionId,
          allocationId: "provider-allocation-b",
          attemptKind: "payment-provider",
          externalActionId: "mismatch-provider-action",
          durableAttemptId: "mismatch-provider-attempt",
        }),
      /identity is inconsistent/,
    );
    assert.equal(
      await scalar(
        connection,
        `SELECT COUNT(*) AS count
         FROM return_tender_attempt_bindings
         WHERE action_id = ?`,
        [draft.actionId],
      ),
      0,
    );
  });
});

test("真实 SQLite：Hbpos API attempt 先耐久再绑定，最终写失败整体回滚且可用同一 returnOrderGuid 重放", async () => {
  await withDatabase("durable-return-api-rollback", async (connection) => {
    await migrateFresh(connection);
    const vault = new SqliteReturnCapacityVault(
      connection,
      encryptor,
      () => T0,
    );
    await vault.seedOrLoad({
      capacityId: "api-cash-capacity",
      originalOrderGuid: "api-original-order",
      method: "cash",
      originalAmountCents: 500,
      remainingAmountCents: 500,
      protectedContext: null,
      observedAtIso: T0,
    });
    let tender = 0;
    let audit = 0;
    const ledger = new SqliteReturnExecutionLedger(
      connection,
      encryptor,
      {
        createTenderGuid: () => `api-return-tender-${++tender}`,
        createAuditEventId: () => `api-return-audit-${++audit}`,
      },
      () => T2,
    );
    const draft = durableReturnDraft({
      actionId: "api-return-action",
      requestFingerprint: "api-return-fingerprint",
      returnOrderGuid: "api-return-order",
      actionRecoveryToken: "api-return-recovery",
      originalOrderGuid: "api-original-order",
      returnSourceKey: "api-return-source",
      capacityId: "api-cash-capacity",
      onlineCashOnly: true,
    });
    await ledger.prepareOrLoad(draft);
    await ledger.markAllocationSubmitted({
      actionId: draft.actionId,
      allocationId: "return-allocation-cash",
    });
    const apiAttempts = new SqliteReturnApiAttemptStore(
      connection,
      encryptor,
    );
    const apiAttempt = {
      durableAttemptId: "hbpos-api-attempt-1",
      externalAttemptId: "return-external-cash",
      returnOrderGuid: draft.returnOrderGuid,
      actionId: draft.actionId,
      allocationId: "return-allocation-cash",
      externalActionId: "hbpos-api-action-1",
      idempotencyKey: "hbpos-api-idempotency-1",
      method: "cash" as const,
      signedAmountCents: -500,
      protectedContext: { backendRefundReference: "SECRET-BACKEND-REF" },
      createdAtIso: T1,
    };
    assert.equal(
      (await apiAttempts.prepareOrLoad(apiAttempt)).state,
      "Created",
    );
    assert.equal(
      (await apiAttempts.prepareOrLoad(apiAttempt)).durableAttemptId,
      apiAttempt.durableAttemptId,
    );
    assert.equal(
      await apiAttempts.compareAndSetState({
        durableAttemptId: apiAttempt.durableAttemptId,
        expected: "Created",
        next: "Submitted",
        updatedAtIso: T1,
      }),
      true,
    );
    assert.equal(
      await apiAttempts.compareAndSetState({
        durableAttemptId: apiAttempt.durableAttemptId,
        expected: "Submitted",
        next: "Approved",
        updatedAtIso: T2,
      }),
      true,
    );
    assert.equal(
      await ledger.bindAllocationAttempt({
        actionId: draft.actionId,
        allocationId: "return-allocation-cash",
        attemptKind: "hbpos-api",
        externalActionId: apiAttempt.externalActionId,
        durableAttemptId: apiAttempt.durableAttemptId,
      }),
      true,
    );
    assert.equal(
      await ledger.recordAllocationOutcome({
        actionId: draft.actionId,
        allocationId: "return-allocation-cash",
        expectedStatuses: ["submitted"],
        status: "completed",
        protectedRecoveryKey: null,
      }),
      true,
    );
    assert.equal(
      await ledger.recordAllocationOutcome({
        actionId: draft.actionId,
        allocationId: "return-allocation-cash",
        expectedStatuses: ["submitted"],
        status: "completed",
        protectedRecoveryKey: null,
      }),
      true,
    );
    assert.equal(
      await scalar(
        connection,
        `SELECT COUNT(*) AS count
         FROM order_tenders
         WHERE order_guid = 'api-return-order'`,
      ),
      0,
    );
    await connection.run(
      `INSERT INTO outbox_messages (
        message_id, aggregate_id, kind, payload_json, state,
        attempt_count, next_attempt_at_iso, lease_id, lease_expires_at_iso,
        last_error_code, created_at_iso, updated_at_iso
      ) VALUES ('duplicate-return-outbox', 'seed', 'audit-batch', '{}',
        'pending', 0, ?, NULL, NULL, NULL, ?, ?)`,
      [T0, T0, T0],
    );
    const failingCompletion = durableReturnCompletion(draft, {
      messageId: "duplicate-return-outbox",
    });
    await assert.rejects(
      () => ledger.completeAtomically(failingCompletion),
      /UNIQUE constraint failed/,
    );
    assert.equal((await ledger.load(draft.actionId))?.status, "processing");
    assert.equal(
      await scalar(
        connection,
        `SELECT COUNT(*) AS count
         FROM local_orders
         WHERE order_guid = 'api-return-order' AND state = 'Draft'`,
      ),
      1,
    );
    assert.equal(
      await scalar(
        connection,
        `SELECT COUNT(*) AS count
         FROM order_tenders
         WHERE order_guid = 'api-return-order'`,
      ),
      0,
    );
    assert.equal(
      (await vault.get("api-cash-capacity"))?.remainingAmountCents,
      500,
    );
    const completed = await ledger.completeAtomically(
      durableReturnCompletion(draft),
    );
    assert.equal(completed.status, "completed");
    assert.equal(completed.returnOrderGuid, "api-return-order");
    assert.deepEqual(
      await apiAttempts.resolveProtectedContext(
        apiAttempt.durableAttemptId,
      ),
      { backendRefundReference: "SECRET-BACKEND-REF" },
    );
    const completion = durableReturnCompletion(draft);
    await connection.run(
      `INSERT INTO drawer_events (
        event_id, order_guid, printer_id, print_job_id, state, reason,
        retry_count, requested_at_iso, completed_at_iso, last_error_code,
        created_at_iso, updated_at_iso
      ) VALUES (?, ?, 'conflicting-printer', NULL, 'Required', 'seed',
        0, NULL, NULL, NULL, ?, ?)`,
      [
        completion.fulfilment.drawerEventId,
        completion.returnOrderGuid,
        T2,
        T2,
      ],
    );
    const fulfilmentPlans = new SqliteReturnFulfilmentPlanStore(
      connection,
      encryptor,
      () => T2,
    );
    await assert.rejects(
      () =>
        fulfilmentPlans.materialize({
          actionId: draft.actionId,
          expectedReturnOrderGuid: draft.returnOrderGuid,
          expectedPrintJobId: completion.fulfilment.printJobId,
          expectedDrawerEventId: completion.fulfilment.drawerEventId,
          printerId: "api-return-printer",
          receiptBytes: null,
          drawerReason: "cash-return",
        }),
      /UNIQUE constraint failed/,
    );
    assert.equal(
      await scalar(
        connection,
        `SELECT COUNT(*) AS count
         FROM print_jobs
         WHERE job_id = ?`,
        [completion.fulfilment.printJobId],
      ),
      0,
    );
    assert.equal(
      (
        await fulfilmentPlans.get(draft.actionId)
      )?.materializedAtIso,
      null,
    );
  });
});

test("真实 SQLite：代替刷卡退款允许退款方式与原 capacity 不同，额度仍从原卡容量扣减", async () => {
  await withDatabase("durable-return-substitute", async (connection) => {
    await migrateFresh(connection);
    const vault = new SqliteReturnCapacityVault(
      connection,
      encryptor,
      () => T0,
    );
    // 原订单为刷卡支付（capacity method = card）。
    await vault.seedOrLoad({
      capacityId: "return-capacity-card",
      originalOrderGuid: "original-return-order",
      method: "card",
      originalAmountCents: 500,
      remainingAmountCents: 500,
      protectedContext: {
        paymentId: "SECRET-PAYMENT-ID",
        rfn: "SECRET-RFN",
      },
      observedAtIso: T0,
    });
    let tender = 0;
    let audit = 0;
    const ledger = new SqliteReturnExecutionLedger(
      connection,
      encryptor,
      {
        createTenderGuid: () => `sub-return-tender-${++tender}`,
        createAuditEventId: () => `sub-return-audit-${++audit}`,
      },
      () => T2,
    );
    // 代替为现金：allocation.method=cash 绑定原卡 capacity（method 不一致）。
    const draft = durableReturnDraft({
      actionId: "sub-return-action",
      requestFingerprint: "sub-return-fingerprint",
      returnOrderGuid: "sub-return-order",
      actionRecoveryToken: "sub-return-recovery",
      returnSourceKey: "sub-return-source",
      capacityId: "return-capacity-card",
      onlineCashOnly: true,
    });
    assert.equal(draft.allocations[0]?.method, "cash");
    assert.equal(
      draft.allocations[0]?.capacityId,
      "return-capacity-card",
    );

    // 原实现会因 method 不一致抛“capacity is missing or exhausted”；代替后必须成功预留。
    const prepared = await ledger.prepareOrLoad(draft);
    assert.equal(prepared.status, "processing");

    await ledger.markAllocationSubmitted({
      actionId: draft.actionId,
      allocationId: "return-allocation-cash",
    });
    const apiAttempts = new SqliteReturnApiAttemptStore(
      connection,
      encryptor,
    );
    const apiAttempt = {
      durableAttemptId: "sub-hbpos-api-attempt",
      externalAttemptId: "return-external-cash",
      returnOrderGuid: draft.returnOrderGuid,
      actionId: draft.actionId,
      allocationId: "return-allocation-cash",
      externalActionId: "sub-hbpos-api-action",
      idempotencyKey: "sub-hbpos-api-idempotency",
      method: "cash" as const,
      signedAmountCents: -500,
      protectedContext: { backendRefundReference: "SECRET-BACKEND-REF" },
      createdAtIso: T1,
    };
    await apiAttempts.prepareOrLoad(apiAttempt);
    await apiAttempts.compareAndSetState({
      durableAttemptId: apiAttempt.durableAttemptId,
      expected: "Created",
      next: "Submitted",
      updatedAtIso: T1,
    });
    await apiAttempts.compareAndSetState({
      durableAttemptId: apiAttempt.durableAttemptId,
      expected: "Submitted",
      next: "Approved",
      updatedAtIso: T2,
    });
    assert.equal(
      await ledger.bindAllocationAttempt({
        actionId: draft.actionId,
        allocationId: "return-allocation-cash",
        attemptKind: "hbpos-api",
        externalActionId: apiAttempt.externalActionId,
        durableAttemptId: apiAttempt.durableAttemptId,
      }),
      true,
    );
    assert.equal(
      await ledger.recordAllocationOutcome({
        actionId: draft.actionId,
        allocationId: "return-allocation-cash",
        expectedStatuses: ["submitted"],
        status: "completed",
        protectedRecoveryKey: null,
      }),
      true,
    );
    const completed = await ledger.completeAtomically(
      durableReturnCompletion(draft),
    );
    assert.equal(completed.status, "completed");
    // 代替退款仍从原卡 capacity 扣减额度，防止超额退款。
    assert.equal(
      (await vault.get("return-capacity-card"))?.remainingAmountCents,
      0,
    );
  });
});

test("真实 SQLite：在线现金退款经生产路由写入 API attempt 后绑定并完成，恢复重放幂等", async () => {
  await withDatabase("durable-return-online-cash-router", async (connection) => {
    await migrateFresh(connection);
    const vault = new SqliteReturnCapacityVault(
      connection,
      encryptor,
      () => T0,
    );
    // 手工刷卡原单：只能现金代替，capacity 仍按原卡扣减。
    await vault.seedOrLoad({
      capacityId: "return-capacity-manual-card",
      originalOrderGuid: "original-return-order",
      method: "card",
      originalAmountCents: 500,
      remainingAmountCents: 500,
      protectedContext: { version: 1, provider: "manual-card" },
      observedAtIso: T0,
    });
    let tender = 0;
    let audit = 0;
    const ledger = new SqliteReturnExecutionLedger(
      connection,
      encryptor,
      {
        createTenderGuid: () => `router-cash-tender-${++tender}`,
        createAuditEventId: () => `router-cash-audit-${++audit}`,
      },
      () => T2,
    );
    const draft = durableReturnDraft({
      actionId: "router-cash-action",
      requestFingerprint: "router-cash-fingerprint",
      returnOrderGuid: "router-cash-order",
      actionRecoveryToken: "router-cash-recovery",
      returnSourceKey: "router-cash-source",
      capacityId: "return-capacity-manual-card",
      onlineCashOnly: true,
    });
    await ledger.prepareOrLoad(draft);
    await ledger.markAllocationSubmitted({
      actionId: draft.actionId,
      allocationId: "return-allocation-cash",
    });

    let clock = T1;
    const router = new ProductionReturnOnlineRefundRouter({
      providerRefund: null,
      cashAttempts: new SqliteReturnApiAttemptStore(connection, encryptor),
      nowIso: () => clock,
    });
    const allocation = draft.allocations[0]!;
    const preparation = {
      actionId: draft.actionId,
      allocationId: allocation.allocationId,
      externalAttemptId: allocation.externalAttemptId!,
      returnOrderGuid: draft.returnOrderGuid,
      actor: { cashierId: "cashier-1", cashierName: "Cashier", userGuid: null },
      method: "cash" as const,
      signedAmountCents: allocation.signedAmountCents,
      capacityId: allocation.capacityId,
      originalOrderGuid: allocation.originalOrderGuid,
    };
    const binding = await router.prepareAttempt(preparation);
    // 原实现不写 return_api_attempts，账本在这里抛“attempt identity is inconsistent”。
    assert.equal(
      await ledger.bindAllocationAttempt({
        actionId: draft.actionId,
        allocationId: allocation.allocationId,
        ...binding,
      }),
      true,
    );
    // 恢复路径会再次 prepare：时钟前进也必须按首次创建时间幂等重放。
    clock = T2;
    assert.deepEqual(await router.prepareAttempt(preparation), binding);
    const bound = { ...preparation, ...binding };
    assert.deepEqual(await router.submit(bound), { status: "completed" });
    assert.deepEqual(
      await router.recover({ ...bound, protectedRecoveryKey: null }),
      { status: "completed" },
    );
    assert.equal(
      await ledger.recordAllocationOutcome({
        actionId: draft.actionId,
        allocationId: allocation.allocationId,
        expectedStatuses: ["submitted"],
        status: "completed",
        protectedRecoveryKey: null,
      }),
      true,
    );
    const completed = await ledger.completeAtomically(
      durableReturnCompletion(draft),
    );
    assert.equal(completed.status, "completed");
    assert.equal(
      (await vault.get("return-capacity-manual-card"))?.remainingAmountCents,
      0,
    );
  });
});

test("真实 SQLite：DraftPrepared 可安全 abandon 并重放，账本不删除且旧异步支付被数据库拒绝", async () => {
  await withDatabase("draft-abandon", async (connection) => {
    await migrateFresh(connection);
    const ids = sequenceIds("order-abandon", "audit-abandon");
    const store = new SqlitePaymentDraftRecoveryStore(
      connection,
      ids,
      () => T1,
    );
    const input = draftInput({ draftId: "draft-abandon" });
    const created = await store.createOrReuseDraft(input);
    const command = {
      actionId: "abandon-action-1",
      draftId: input.draftId,
      orderGuid: created.orderGuid,
      storeCode: input.identity.storeCode,
      deviceCode: input.identity.deviceCode,
      actor: paymentAuditActor(),
    };
    const abandoned = await store.abandonPreparedDraft(command);
    assert.equal(abandoned.replayed, false);
    assert.equal(abandoned.draftId, input.draftId);
    assert.equal(abandoned.orderGuid, created.orderGuid);
    assert.deepEqual(abandoned.cart, input.cart);
    assert.deepEqual(abandoned.pricingState, input.pricingState);
    const abandonReplay = await store.abandonPreparedDraft(command);
    assert.equal(abandonReplay.replayed, true);
    assert.deepEqual(abandonReplay.pricingState, input.pricingState);
    const abandonAudit = await connection.getFirst<{
      payload_json: unknown;
    }>(
      `SELECT payload_json
       FROM audit_events
       WHERE event_type = 'PAYMENT_DRAFT_ABANDONED'
         AND correlation_id = ?`,
      [command.actionId],
    );
    const abandonPayload = JSON.parse(String(abandonAudit?.payload_json));
    assert.equal(abandonPayload.requestingCashierId, "cashier-actor");
    assert.equal(abandonPayload.requestingCashierName, "Actor Alice");
    assert.equal(abandonPayload.requestingUserGuid, "user-guid-actor");
    assert.equal(await store.findBlockingRecovery(input.identity), null);
    assert.equal(
      await scalar(
        connection,
        "SELECT COUNT(*) AS count FROM local_orders WHERE order_guid = ? AND state = 'Draft'",
        [created.orderGuid],
      ),
      1,
    );
    assert.equal(
      await scalar(
        connection,
        "SELECT COUNT(*) AS count FROM local_order_lines WHERE order_guid = ?",
        [created.orderGuid],
      ),
      input.cart.lines.length,
    );
    await assert.rejects(
      () => store.abandonPreparedDraft({
        ...command,
        actionId: "abandon-action-2",
      }),
      /different immutable action/,
    );
    await assert.rejects(
      () => insertActionBinding(
        connection,
        created.orderGuid,
        "late-action",
        "late-attempt",
        "late-idempotency",
      ),
      /PAYMENT_ORDER_DRAFT_ABANDONED/,
    );
    await assert.rejects(
      () => insertAttempt(connection, {
        attemptId: "late-attempt",
        idempotencyKey: "late-idempotency",
        orderGuid: created.orderGuid,
        provider: "square",
        operation: "purchase",
        amountCents: 900,
        state: "Created",
      }),
      /PAYMENT_ORDER_DRAFT_ABANDONED/,
    );

    const next = await store.createOrReuseDraft(
      draftInput({ draftId: "draft-after-abandon" }),
    );
    assert.notEqual(next.orderGuid, created.orderGuid);
    assert.equal(next.localSequence, created.localSequence + 1);
  });
});

test("真实 SQLite：仅有已拒付历史的 DraftPrepared 可安全 abandon 且完整保留账本", async () => {
  await withDatabase("draft-abandon-declined-history", async (connection) => {
    await migrateFresh(connection);
    const input = draftInput({
      draftId: "draft-abandon-declined-history",
      identity: {
        storeCode: "S-ABANDON-DECLINED",
        deviceCode: "D-ABANDON-DECLINED",
        cashierId: "C-ABANDON-DECLINED",
        cashierName: "Abandon declined history",
      },
    });
    const store = new SqlitePaymentDraftRecoveryStore(
      connection,
      sequenceIds(
        "order-abandon-declined-history",
        "audit-abandon-declined-history",
      ),
      () => T1,
    );
    const created = await store.createOrReuseDraft(input);

    for (const suffix of ["one", "two"] as const) {
      await insertActionBinding(
        connection,
        created.orderGuid,
        `binding-abandon-declined-${suffix}`,
        `attempt-abandon-declined-${suffix}`,
        `idempotency-abandon-declined-${suffix}`,
        ["linkly-cloud", "purchase", "AUD", 900],
      );
      await insertAttempt(connection, {
        attemptId: `attempt-abandon-declined-${suffix}`,
        idempotencyKey: `idempotency-abandon-declined-${suffix}`,
        orderGuid: created.orderGuid,
        provider: "linkly-cloud",
        operation: "purchase",
        amountCents: 900,
        state: "Declined",
      });
    }

    const recovery = await store.findBlockingRecovery(input.identity);
    assert.equal(recovery?.kind, "DraftPrepared");
    assert.equal(recovery?.attemptId, null);
    assert.equal(recovery?.boundAction, null);

    const abandoned = await store.abandonPreparedDraft({
      actionId: "abandon-declined-history",
      draftId: input.draftId,
      orderGuid: created.orderGuid,
      actor: paymentAuditActor(),
      ...input.identity,
    });
    assert.equal(abandoned.replayed, false);
    assert.equal(await store.findBlockingRecovery(input.identity), null);
    assert.equal(
      await scalar(
        connection,
        `SELECT COUNT(*) AS count
         FROM payment_order_draft_bindings
         WHERE order_guid = ? AND state = 'Abandoned'`,
        [created.orderGuid],
      ),
      1,
    );
    assert.equal(
      await scalar(
        connection,
        "SELECT COUNT(*) AS count FROM payment_action_bindings WHERE order_guid = ?",
        [created.orderGuid],
      ),
      2,
    );
    assert.equal(
      await scalar(
        connection,
        "SELECT COUNT(*) AS count FROM payment_attempts WHERE order_guid = ? AND state = 'Declined'",
        [created.orderGuid],
      ),
      2,
    );
    assert.equal(
      await scalar(
        connection,
        "SELECT COUNT(*) AS count FROM order_tenders WHERE order_guid = ?",
        [created.orderGuid],
      ),
      0,
    );
  });
});

test("真实 SQLite：孤立 attempt 禁止 abandon；单个未提交 binding 可安全关闭并阻断陈旧 attempt", async () => {
  for (const history of ["attempt-only", "binding-only"] as const) {
    await withDatabase(`draft-abandon-${history}`, async (connection) => {
      await migrateFresh(connection);
      const input = draftInput({
        draftId: `draft-abandon-${history}`,
        identity: {
          storeCode: `S-ABANDON-${history}`,
          deviceCode: `D-ABANDON-${history}`,
          cashierId: "C-ABANDON-ORPHAN",
          cashierName: "Abandon orphan history",
        },
      });
      const store = new SqlitePaymentDraftRecoveryStore(
        connection,
        sequenceIds(`order-abandon-${history}`, `audit-abandon-${history}`),
        () => T1,
      );
      const created = await store.createOrReuseDraft(input);

      if (history === "attempt-only") {
        await insertAttempt(connection, {
          attemptId: "attempt-abandon-orphan",
          idempotencyKey: "idempotency-abandon-orphan",
          orderGuid: created.orderGuid,
          provider: "linkly-cloud",
          operation: "purchase",
          amountCents: 900,
          state: "Declined",
        });
      } else {
        await insertActionBinding(
          connection,
          created.orderGuid,
          "binding-abandon-orphan",
          "attempt-abandon-orphan",
          "idempotency-abandon-orphan",
          ["linkly-cloud", "purchase", "AUD", 900],
        );
      }

      const abandon = () => store.abandonPreparedDraft({
        actionId: `abandon-${history}`,
        draftId: input.draftId,
        orderGuid: created.orderGuid,
        actor: paymentAuditActor(),
        ...input.identity,
      });
      if (history === "attempt-only") {
        await assert.rejects(abandon, /tender or unresolved payment history/);
      } else {
        assert.equal((await abandon()).replayed, false);
        await assert.rejects(
          () => insertAttempt(connection, {
            attemptId: "attempt-abandon-orphan",
            idempotencyKey: "idempotency-abandon-orphan",
            orderGuid: created.orderGuid,
            provider: "linkly-cloud",
            operation: "purchase",
            amountCents: 900,
            state: "Created",
          }),
          /PAYMENT_ORDER_DRAFT_ABANDONED/,
        );
        assert.equal(await store.findBlockingRecovery(input.identity), null);
      }
      assert.equal(
        await scalar(
          connection,
          `SELECT COUNT(*) AS count
           FROM payment_order_draft_bindings
           WHERE order_guid = ? AND state = 'Active'`,
          [created.orderGuid],
        ),
        history === "attempt-only" ? 1 : 0,
      );
    });
  }
});

test("真实 SQLite：Approved、Cancelled 或任意 tender 均阻止 DraftPrepared abandon", async () => {
  for (const history of ["approved", "cancelled", "cash-tender"] as const) {
    await withDatabase(`draft-abandon-${history}`, async (connection) => {
      await migrateFresh(connection);
      const input = draftInput({
        draftId: `draft-abandon-${history}`,
        identity: {
          storeCode: `S-ABANDON-${history}`,
          deviceCode: `D-ABANDON-${history}`,
          cashierId: "C-ABANDON-BLOCKED",
          cashierName: "Abandon blocked history",
        },
      });
      const store = new SqlitePaymentDraftRecoveryStore(
        connection,
        sequenceIds(`order-abandon-${history}`, `audit-abandon-${history}`),
        () => T1,
      );
      const created = await store.createOrReuseDraft(input);

      if (history === "cash-tender") {
        await insertTender(
          connection,
          "tender-abandon-cash",
          created.orderGuid,
          "cash",
          100,
        );
      } else {
        await insertActionBinding(
          connection,
          created.orderGuid,
          `binding-abandon-${history}`,
          `attempt-abandon-${history}`,
          `idempotency-abandon-${history}`,
          ["linkly-cloud", "purchase", "AUD", 900],
        );
        await insertAttempt(connection, {
          attemptId: `attempt-abandon-${history}`,
          idempotencyKey: `idempotency-abandon-${history}`,
          orderGuid: created.orderGuid,
          provider: "linkly-cloud",
          operation: "purchase",
          amountCents: 900,
          state: history === "approved" ? "Approved" : "Cancelled",
        });
        if (history === "approved") {
          await insertTender(
            connection,
            "tender-abandon-approved",
            created.orderGuid,
            "card",
            900,
            "attempt-abandon-approved",
          );
        }
      }

      const recovery = await store.findBlockingRecovery(input.identity);
      assert.equal(recovery?.kind, "DraftPrepared", history);
      assert.equal(
        recovery?.boundAction === null,
        history !== "cancelled",
        history,
      );
      await assert.rejects(
        () => store.abandonPreparedDraft({
          actionId: `abandon-${history}`,
          draftId: input.draftId,
          orderGuid: created.orderGuid,
          actor: paymentAuditActor(),
          ...input.identity,
        }),
        /tender or unresolved payment history/,
      );
    });
  }
});

test("真实 SQLite：全部现金 tender 已反冲时可幂等关闭，且保留原款、反向款和关联", async () => {
  await withDatabase("draft-fully-reversed-close", async (connection) => {
    await migrateFresh(connection);
    const input = draftInput({ draftId: "draft-fully-reversed-close" });
    const store = new SqlitePaymentDraftRecoveryStore(
      connection,
      sequenceIds(
        "order-fully-reversed-close",
        "audit-fully-reversed-close",
      ),
      () => T1,
    );
    const created = await store.createOrReuseDraft(input);
    await connection.run(
      `UPDATE local_orders
       SET state = 'Completing', updated_at_iso = ?
       WHERE order_guid = ?`,
      [T1, created.orderGuid],
    );
    await insertTender(
      connection,
      "cash-source-fully-reversed",
      created.orderGuid,
      "cash",
      400,
    );
    const command = {
      actionId: "close-fully-reversed",
      draftId: input.draftId,
      orderGuid: created.orderGuid,
      storeCode: input.identity.storeCode,
      deviceCode: input.identity.deviceCode,
      actor: paymentAuditActor(),
    };
    await assert.rejects(
      () => store.closeFullyReversedDraft(command),
      /fully reversed cash payment draft/,
    );
    await insertTender(
      connection,
      "cash-reversal-fully-reversed",
      created.orderGuid,
      "cash",
      -400,
    );
    await connection.run(
      `INSERT INTO payment_tender_reversal_links (
        order_guid, action_id, source_tender_guid,
        reversal_tender_guid, created_at_iso
      ) VALUES (?, 'reverse-fully-reversed',
        'cash-source-fully-reversed',
        'cash-reversal-fully-reversed', ?)`,
      [created.orderGuid, T1],
    );

    const closed = await store.closeFullyReversedDraft(command);
    const replay = await store.closeFullyReversedDraft(command);

    assert.equal(closed.replayed, false);
    assert.equal(replay.replayed, true);
    assert.equal(await store.findBlockingRecovery(input.identity), null);
    assert.equal(
      await scalar(
        connection,
        `SELECT COUNT(*) AS count
         FROM order_tenders
         WHERE order_guid = ?`,
        [created.orderGuid],
      ),
      2,
    );
    assert.equal(
      await scalar(
        connection,
        `SELECT COUNT(*) AS count
         FROM payment_tender_reversal_links
         WHERE order_guid = ?`,
        [created.orderGuid],
      ),
      1,
    );
    assert.equal(
      await scalar(
        connection,
        `SELECT COUNT(*) AS count
         FROM payment_order_draft_bindings
         WHERE order_guid = ? AND state = 'Abandoned'
           AND abandon_action_id = ?`,
        [created.orderGuid, command.actionId],
      ),
      1,
    );
  });
});

test("真实 SQLite：全反冲关闭拒绝不完整或不安全的历史 payment 账本", async () => {
  const scenarios = [
    {
      name: "部分现金未反冲",
      activeTenderCount: 1,
      balanceRestored: false,
      expectedRecoveryKind: "DraftPrepared",
      prepare: async (
        connection: SqliteConnectionPort,
        orderGuid: string,
      ) => {
        await insertImmutableTenderReversal(
          connection,
          orderGuid,
          "partial-cash-reversed",
          "cash",
          400,
        );
        await insertTender(
          connection,
          "partial-cash-still-active",
          orderGuid,
          "cash",
          500,
        );
      },
    },
    {
      name: "混入 card",
      activeTenderCount: 0,
      balanceRestored: true,
      expectedRecoveryKind: "DraftPrepared",
      prepare: async (
        connection: SqliteConnectionPort,
        orderGuid: string,
      ) => {
        await insertImmutableTenderReversal(
          connection,
          orderGuid,
          "mixed-cash",
          "cash",
          400,
        );
        await insertImmutableTenderReversal(
          connection,
          orderGuid,
          "mixed-card",
          "card",
          500,
        );
      },
    },
    {
      name: "混入 voucher",
      activeTenderCount: 0,
      balanceRestored: true,
      expectedRecoveryKind: "DraftPrepared",
      prepare: async (
        connection: SqliteConnectionPort,
        orderGuid: string,
      ) => {
        await insertImmutableTenderReversal(
          connection,
          orderGuid,
          "voucher-cash",
          "cash",
          400,
        );
        await insertImmutableTenderReversal(
          connection,
          orderGuid,
          "mixed-voucher",
          "voucher",
          500,
        );
      },
    },
    {
      name: "孤立负 tender",
      activeTenderCount: 0,
      balanceRestored: true,
      expectedRecoveryKind: "DraftPrepared",
      prepare: async (
        connection: SqliteConnectionPort,
        orderGuid: string,
      ) => {
        await insertImmutableTenderReversal(
          connection,
          orderGuid,
          "cash-with-orphan-negative",
          "cash",
          900,
        );
        await insertTender(
          connection,
          "orphan-negative-cash",
          orderGuid,
          "cash",
          -1,
        );
      },
    },
    {
      name: "存在 payment attempt",
      activeTenderCount: 0,
      balanceRestored: true,
      expectedRecoveryKind: "AttemptBlocking",
      prepare: async (
        connection: SqliteConnectionPort,
        orderGuid: string,
      ) => {
        await insertImmutableTenderReversal(
          connection,
          orderGuid,
          "cash-with-attempt",
          "cash",
          900,
        );
        await insertAttempt(connection, {
          attemptId: "reversed-close-attempt",
          idempotencyKey: "reversed-close-idempotency",
          orderGuid,
          provider: "square",
          operation: "purchase",
          amountCents: 900,
          state: "Unknown",
        });
      },
    },
    {
      name: "存在 payment action binding",
      activeTenderCount: 0,
      balanceRestored: true,
      expectedRecoveryKind: "DraftPrepared",
      prepare: async (
        connection: SqliteConnectionPort,
        orderGuid: string,
      ) => {
        await insertImmutableTenderReversal(
          connection,
          orderGuid,
          "cash-with-action-binding",
          "cash",
          900,
        );
        await insertActionBinding(
          connection,
          orderGuid,
          "reversed-close-action-binding",
          "reversed-close-bound-attempt",
          "reversed-close-bound-idempotency",
        );
      },
    },
  ] as const;

  for (const scenario of scenarios) {
    await withDatabase(
      `draft-fully-reversed-close-${scenario.name}`,
      async (connection) => {
        await migrateFresh(connection);
        const input = draftInput({
          draftId: `draft-fully-reversed-close-${scenario.name}`,
        });
        const store = new SqlitePaymentDraftRecoveryStore(
          connection,
          sequenceIds(
            `order-fully-reversed-close-${scenario.name}`,
            `audit-fully-reversed-close-${scenario.name}`,
          ),
          () => T1,
        );
        const created = await store.createOrReuseDraft(input);
        await connection.run(
          `UPDATE local_orders
           SET state = 'Completing', updated_at_iso = ?
           WHERE order_guid = ?`,
          [T1, created.orderGuid],
        );
        await scenario.prepare(connection, created.orderGuid);
        const command = {
          actionId: `close-fully-reversed-${scenario.name}`,
          draftId: input.draftId,
          orderGuid: created.orderGuid,
          storeCode: input.identity.storeCode,
          deviceCode: input.identity.deviceCode,
          actor: paymentAuditActor(),
        };

        const draft = await store.readDraft(created.orderGuid, input.identity);
        assert.equal(draft?.state, "Completing", scenario.name);
        assert.equal(
          draft?.tenders.length,
          scenario.activeTenderCount,
          scenario.name,
        );
        assert.equal(
          draft?.remaining.cents === draft?.total.cents,
          scenario.balanceRestored,
          scenario.name,
        );
        assert.equal(
          draft?.cancellableAfterReversal,
          false,
          scenario.name,
        );
        await assert.rejects(
          () => store.closeFullyReversedDraft(command),
          /fully reversed cash payment draft/,
          scenario.name,
        );
        assert.equal(
          await scalar(
            connection,
            `SELECT COUNT(*) AS count
             FROM payment_order_draft_bindings
             WHERE order_guid = ? AND state = 'Active'`,
            [created.orderGuid],
          ),
          1,
          scenario.name,
        );
        const recovery = await store.findBlockingRecovery(input.identity);
        assert.equal(recovery?.orderGuid, created.orderGuid, scenario.name);
        assert.equal(recovery?.kind, scenario.expectedRecoveryKind, scenario.name);
      },
    );
  }
});

test("手动刷卡action先落库后崩溃仍恢复明确确认，缺少确认标记失败关闭", async () => {
  for (const confirmed of [true, false]) {
    await withDatabase(confirmed ? "manual-confirmed-recovery" : "manual-unconfirmed-recovery", async (connection) => {
      await migrateFresh(connection);
      const store = new SqlitePaymentDraftRecoveryStore(connection, sequenceIds("manual-order", "manual-audit"), () => T1);
      const input = draftInput({ draftId: "manual-draft" });
      const created = await store.createOrReuseDraft(input);
      await insertActionBinding(connection, created.orderGuid, "manual-action", "manual-attempt", "manual-key",
        confirmed ? ["manual-card", "purchase", "AUD", 900, "confirmed"] : ["manual-card", "purchase", "AUD", 900]);
      if (confirmed) {
        const recovered = await store.findBlockingRecovery(input.identity);
        assert.equal(recovered?.kind, "DraftPrepared");
        assert.equal(recovered?.boundAction?.manualConfirmed, true);
        assert.equal(recovered?.boundAction?.provider, "manual-card");
      } else await assert.rejects(() => store.findBlockingRecovery(input.identity), /confirmation signature/);
    });
  }
});

test("真实 SQLite：手动刷卡已确认 binding 即使无 attempt 也禁止放弃", async () => {
  await withDatabase("manual-confirmed-abandon-blocked", async (connection) => {
    await migrateFresh(connection);
    const store = new SqlitePaymentDraftRecoveryStore(
      connection,
      sequenceIds("manual-abandon-order", "manual-abandon-audit"),
      () => T1,
    );
    const input = draftInput({ draftId: "manual-abandon-draft" });
    const created = await store.createOrReuseDraft(input);
    await insertActionBinding(
      connection,
      created.orderGuid,
      "manual-abandon-binding",
      "manual-abandon-attempt",
      "manual-abandon-key",
      ["manual-card", "purchase", "AUD", 900, "confirmed"],
    );

    await assert.rejects(
      () => store.abandonPreparedDraft({
        actionId: "manual-unsafe-abandon",
        draftId: input.draftId,
        orderGuid: created.orderGuid,
        actor: paymentAuditActor(),
        ...input.identity,
      }),
      /unresolved payment history/,
    );
    assert.equal(
      await scalar(
        connection,
        "SELECT COUNT(*) AS count FROM payment_order_draft_bindings WHERE order_guid = ? AND state = 'Active'",
        [created.orderGuid],
      ),
      1,
    );
    assert.equal((await store.findBlockingRecovery(input.identity))?.boundAction?.manualConfirmed, true);
  });
});

test("真实 SQLite：blocking attempt 与无 attempt prepared draft 均跨重启恢复，完成态 binding 不再阻塞", async () => {
  const folder = mkdtempSync(join(tmpdir(), "hb-pos-payment-recovery-"));
  const path = join(folder, "recovery.db");
  const firstConnection = new SystemSqliteConnection(path);
  try {
    await migrateFresh(firstConnection);
    const ids = sequenceIds("order-recovery", "audit-recovery");
    const store = new SqlitePaymentDraftRecoveryStore(
      firstConnection,
      ids,
      () => T1,
    );
    const input = draftInput({
      draftId: "draft-recovery",
      identity: {
        storeCode: "S-REC",
        deviceCode: "D-REC",
        cashierId: "C-REC",
        cashierName: "Recovery",
      },
    });
    const created = await store.createOrReuseDraft(input);
    // 已知离线可能只落 action binding；仍必须作为 DraftPrepared 返回原购物车。
    await insertActionBinding(
      firstConnection,
      created.orderGuid,
      "pay-action",
      "attempt-recovery",
      "idempotency-recovery",
    );
    const actionOnly = await store.findBlockingRecovery(input.identity);
    assert.equal(actionOnly?.kind, "DraftPrepared");
    assert.deepEqual(actionOnly?.boundAction, {
      actionId: "pay-action",
      attemptId: "attempt-recovery",
      provider: "square",
      operation: "purchase",
      amount: { currency: "AUD", cents: 900 },
    });
    assert.equal(
      await scalar(
        firstConnection,
        "SELECT COUNT(*) AS count FROM payment_action_bindings WHERE order_guid = ?",
        [created.orderGuid],
      ),
      1,
    );
    await insertAttempt(firstConnection, {
      attemptId: "attempt-recovery",
      idempotencyKey: "idempotency-recovery",
      orderGuid: created.orderGuid,
      provider: "square",
      operation: "purchase",
      amountCents: 900,
      state: "Unknown",
    });
    await firstConnection.close();

    const reopened = new SystemSqliteConnection(path);
    const reopenedStore = new SqlitePaymentDraftRecoveryStore(
      reopened,
      sequenceIds("unused-order", "unused-audit"),
      () => T2,
    );
    const blocking = await reopenedStore.findBlockingRecovery(input.identity);
    assert.equal(blocking?.kind, "AttemptBlocking");
    if (blocking?.kind === "AttemptBlocking") {
      assert.equal(blocking.attemptId, "attempt-recovery");
      assert.equal(blocking.orderGuid, created.orderGuid);
      assert.equal(blocking.state, "Unknown");
      assert.deepEqual(blocking.boundAction, {
        actionId: "pay-action",
        attemptId: "attempt-recovery",
        provider: "square",
        operation: "purchase",
        amount: { currency: "AUD", cents: 900 },
      });
      assert.deepEqual(blocking.pricingState, input.pricingState);
    }
    await assert.rejects(
      () => reopenedStore.abandonPreparedDraft({
        actionId: "cannot-abandon",
        draftId: input.draftId,
        orderGuid: created.orderGuid,
        actor: paymentAuditActor(),
        ...input.identity,
      }),
      /tender or unresolved payment history/,
    );
    await reopened.run(
      "UPDATE payment_attempts SET state = 'Approved', updated_at_iso = ? WHERE attempt_id = ?",
      [T2, "attempt-recovery"],
    );
    await reopened.run(
      `INSERT INTO order_tenders (
        tender_guid, order_guid, method, amount_cents,
        payment_attempt_id, created_at_iso
      ) VALUES ('tender-recovery', ?, 'card', 900, 'attempt-recovery', ?)`,
      [created.orderGuid, T2],
    );
    await reopened.run(
      "UPDATE local_orders SET state = 'PendingSync', updated_at_iso = ? WHERE order_guid = ?",
      [T2, created.orderGuid],
    );
    assert.equal(
      await reopenedStore.findBlockingRecovery(input.identity),
      null,
    );
    const next = await reopenedStore.createOrReuseDraft(
      draftInput({
        draftId: "draft-after-complete",
        identity: input.identity,
      }),
    );
    assert.notEqual(next.orderGuid, created.orderGuid);
    await reopened.close();
  } finally {
    try { await firstConnection.close(); } catch { /* already closed */ }
    rmSync(folder, { recursive: true, force: true });
  }
});

test("真实 SQLite：多次已拒付 binding 可重试且不遮蔽 Cancelled 关闭恢复", async () => {
  await withDatabase("draft-declined-binding-history", async (connection) => {
    await migrateFresh(connection);
    const store = new SqlitePaymentDraftRecoveryStore(
      connection,
      sequenceIds("order-declined-history", "audit-declined-history"),
      () => T1,
    );
    const input = draftInput({
      draftId: "draft-declined-history",
      identity: {
        storeCode: "S-DECLINED-HISTORY",
        deviceCode: "D-DECLINED-HISTORY",
        cashierId: "C-DECLINED-HISTORY",
        cashierName: "Declined history",
      },
    });
    const draft = await store.createOrReuseDraft(input);

    for (const suffix of ["one", "two"] as const) {
      await insertActionBinding(
        connection,
        draft.orderGuid,
        `binding-${suffix}`,
        `attempt-${suffix}`,
        `idempotency-${suffix}`,
        ["linkly-cloud", "purchase", "AUD", 900],
      );
      await insertAttempt(connection, {
        attemptId: `attempt-${suffix}`,
        idempotencyKey: `idempotency-${suffix}`,
        orderGuid: draft.orderGuid,
        provider: "linkly-cloud",
        operation: "purchase",
        amountCents: 900,
        state: "Declined",
      });
    }

    const recovery = await store.findBlockingRecovery(input.identity);
    assert.equal(recovery?.kind, "DraftPrepared");
    assert.equal(recovery?.orderGuid, draft.orderGuid);
    assert.equal(recovery?.attemptId, null);
    assert.equal(recovery?.boundAction, null);
    assert.equal(
      await scalar(
        connection,
        "SELECT COUNT(*) AS count FROM payment_action_bindings WHERE order_guid = ?",
        [draft.orderGuid],
      ),
      2,
    );

    await insertActionBinding(
      connection,
      draft.orderGuid,
      "binding-cancelled",
      "attempt-cancelled",
      "idempotency-cancelled",
      ["linkly-cloud", "purchase", "AUD", 900],
    );
    await insertAttempt(connection, {
      attemptId: "attempt-cancelled",
      idempotencyKey: "idempotency-cancelled",
      orderGuid: draft.orderGuid,
      provider: "linkly-cloud",
      operation: "purchase",
      amountCents: 900,
      state: "Cancelled",
    });

    const cancelledRecovery = await store.findBlockingRecovery(input.identity);
    assert.equal(cancelledRecovery?.kind, "DraftPrepared");
    assert.deepEqual(cancelledRecovery?.boundAction, {
      actionId: "binding-cancelled",
      attemptId: "attempt-cancelled",
      provider: "linkly-cloud",
      operation: "purchase",
      amount: { currency: "AUD", cents: 900 },
    });

    const closed = await store.closeCancelledDraft({
      actionId: "binding-cancelled",
      orderGuid: draft.orderGuid,
      actor: paymentAuditActor(),
      ...input.identity,
    });
    assert.equal(closed.replayed, false);
    assert.equal(await store.findBlockingRecovery(input.identity), null);
    assert.equal(
      await scalar(
        connection,
        `SELECT COUNT(*) AS count
         FROM payment_order_draft_bindings
         WHERE order_guid = ? AND state = 'CancelledClosed'`,
        [draft.orderGuid],
      ),
      1,
    );
    assert.equal(
      await scalar(
        connection,
        "SELECT COUNT(*) AS count FROM payment_action_bindings WHERE order_guid = ?",
        [draft.orderGuid],
      ),
      3,
    );
    assert.equal(
      await scalar(
        connection,
        "SELECT COUNT(*) AS count FROM payment_attempts WHERE order_guid = ?",
        [draft.orderGuid],
      ),
      3,
    );
  });
});

test("真实 SQLite：Declined 历史的 binding/attempt 身份不一致时失败关闭", async () => {
  const cases = [
    {
      name: "idempotency",
      bindingIdempotencyKey: "binding-idempotency",
      attemptIdempotencyKey: "attempt-idempotency",
      signature: ["linkly-cloud", "purchase", "AUD", 900] as const,
      provider: "linkly-cloud",
      operation: "purchase",
      amountCents: 900,
    },
    {
      name: "provider",
      bindingIdempotencyKey: "shared-idempotency",
      attemptIdempotencyKey: "shared-idempotency",
      signature: ["linkly-cloud", "purchase", "AUD", 900] as const,
      provider: "square",
      operation: "purchase",
      amountCents: 900,
    },
  ];

  for (const mismatch of cases) {
    await withDatabase(
      `draft-declined-binding-${mismatch.name}`,
      async (connection) => {
        await migrateFresh(connection);
        const store = new SqlitePaymentDraftRecoveryStore(
          connection,
          sequenceIds(
            `order-declined-binding-${mismatch.name}`,
            `audit-declined-binding-${mismatch.name}`,
          ),
          () => T1,
        );
        const input = draftInput({
          draftId: `draft-declined-binding-${mismatch.name}`,
          identity: {
            storeCode: `S-DECLINED-BINDING-${mismatch.name}`,
            deviceCode: `D-DECLINED-BINDING-${mismatch.name}`,
            cashierId: "C-DECLINED-BINDING",
            cashierName: "Declined binding",
          },
        });
        const draft = await store.createOrReuseDraft(input);
        await insertActionBinding(
          connection,
          draft.orderGuid,
          `binding-${mismatch.name}`,
          `attempt-${mismatch.name}`,
          mismatch.bindingIdempotencyKey,
          mismatch.signature,
        );
        await insertAttempt(connection, {
          attemptId: `attempt-${mismatch.name}`,
          idempotencyKey: mismatch.attemptIdempotencyKey,
          orderGuid: draft.orderGuid,
          provider: mismatch.provider,
          operation: mismatch.operation,
          amountCents: mismatch.amountCents,
          state: "Declined",
        });

        await assert.rejects(
          () => store.findBlockingRecovery(input.identity),
          /binding and attempt identity diverged/,
        );
        await assert.rejects(
          () => store.abandonPreparedDraft({
            actionId: `abandon-${mismatch.name}`,
            draftId: input.draftId,
            orderGuid: draft.orderGuid,
            actor: paymentAuditActor(),
            ...input.identity,
          }),
          /binding and attempt identity diverged/,
        );
      },
    );
  }
});

test("真实 SQLite：Declined attempt 关联 tender 时失败关闭", async () => {
  await withDatabase("draft-declined-with-tender", async (connection) => {
    await migrateFresh(connection);
    const store = new SqlitePaymentDraftRecoveryStore(
      connection,
      sequenceIds("order-declined-with-tender", "audit-declined-with-tender"),
      () => T1,
    );
    const input = draftInput({
      draftId: "draft-declined-with-tender",
      identity: {
        storeCode: "S-DECLINED-TENDER",
        deviceCode: "D-DECLINED-TENDER",
        cashierId: "C-DECLINED-TENDER",
        cashierName: "Declined tender",
      },
    });
    const draft = await store.createOrReuseDraft(input);
    await insertActionBinding(
      connection,
      draft.orderGuid,
      "binding-declined-with-tender",
      "attempt-declined-with-tender",
      "idempotency-declined-with-tender",
      ["linkly-cloud", "purchase", "AUD", 900],
    );
    await insertAttempt(connection, {
      attemptId: "attempt-declined-with-tender",
      idempotencyKey: "idempotency-declined-with-tender",
      orderGuid: draft.orderGuid,
      provider: "linkly-cloud",
      operation: "purchase",
      amountCents: 900,
      state: "Declined",
    });
    await insertTender(
      connection,
      "tender-declined-with-tender",
      draft.orderGuid,
      "card",
      900,
      "attempt-declined-with-tender",
    );

    await assert.rejects(
      () => store.findBlockingRecovery(input.identity),
      /Declined payment action unexpectedly has a tender/,
    );
    await assert.rejects(
      () => store.abandonPreparedDraft({
        actionId: "abandon-declined-with-tender",
        draftId: input.draftId,
        orderGuid: draft.orderGuid,
        actor: paymentAuditActor(),
        ...input.identity,
      }),
      /Declined payment action unexpectedly has a tender/,
    );
  });
});

test("真实 SQLite：payment action-only recovery 严格解析签名，多未决 binding 或非法签名失败关闭", async () => {
  await withDatabase("draft-bound-action-fail-closed", async (connection) => {
    await migrateFresh(connection);
    const store = new SqlitePaymentDraftRecoveryStore(
      connection,
      sequenceIds("order-bound", "audit-bound"),
      () => T1,
    );
    const invalidInput = draftInput({
      draftId: "draft-invalid-binding",
      identity: {
        storeCode: "S-INVALID-BINDING",
        deviceCode: "D-INVALID-BINDING",
        cashierId: "C-INVALID-BINDING",
        cashierName: "Invalid binding",
      },
    });
    const invalidDraft = await store.createOrReuseDraft(invalidInput);
    await connection.run(
      `INSERT INTO payment_action_bindings (
        order_guid, action_id, request_signature,
        attempt_id, idempotency_key, created_at_iso, audit_actor_json
      ) VALUES (?, 'invalid-action', 'not-json',
        'invalid-attempt', 'invalid-idempotency', ?, ?)`,
      [invalidDraft.orderGuid, T0, paymentActorJson()],
    );
    await assert.rejects(
      () => store.findBlockingRecovery(invalidInput.identity),
      /request signature is invalid JSON/,
    );

    const multipleInput = draftInput({
      draftId: "draft-multiple-binding",
      identity: {
        storeCode: "S-MULTIPLE-BINDING",
        deviceCode: "D-MULTIPLE-BINDING",
        cashierId: "C-MULTIPLE-BINDING",
        cashierName: "Multiple binding",
      },
    });
    const multipleDraft = await store.createOrReuseDraft(multipleInput);
    await insertActionBinding(
      connection,
      multipleDraft.orderGuid,
      "binding-one",
      "attempt-one",
      "idempotency-one",
    );
    await insertActionBinding(
      connection,
      multipleDraft.orderGuid,
      "binding-two",
      "attempt-two",
      "idempotency-two",
    );
    await assert.rejects(
      () => store.findBlockingRecovery(multipleInput.identity),
      /Multiple payment action bindings require support/,
    );
  });
});

test("真实 SQLite：mixed partial cash 并发/重放只追加一次，现金 reversal 不删原 tender", async () => {
  await withDatabase("mixed-partial", async (connection) => {
    await migrateFresh(connection);
    await insertOrder(connection, {
      orderGuid: "order-mixed",
      sequence: 1,
      storeCode: "S-MIX",
      deviceCode: "D-MIX",
      cashierId: "C-MIX",
      amountCents: 1000,
      state: "Draft",
      syncProvenance: TEST_SYNC_PROVENANCE,
    });
    const ids = sequenceIds("tender-mixed", "audit-mixed");
    const store = new SqliteMixedPaymentTenderStore(
      connection,
      {
        createTenderGuid: ids.createOrderGuid,
        createAuditEventId: ids.createAuditEventId,
      },
      () => T1,
    );
    const command = {
      actionId: "cash-action",
      orderGuid: "order-mixed",
      actor: paymentAuditActor(),
      amount: { currency: "AUD", cents: 400 } as const,
      tenderedAmount: { currency: "AUD", cents: 500 } as const,
      change: { currency: "AUD", cents: 100 } as const,
    };
    const [one, two] = await Promise.all([
      store.appendCashTenderAtomically(command),
      store.appendCashTenderAtomically(command),
    ]);
    assert.deepEqual(
      [one.replayed, two.replayed].sort(),
      [false, true],
    );
    assert.equal(one.tenderGuid, two.tenderGuid);
    assert.equal(one.truth.state, "Completing");
    assert.equal(
      await scalar(
        connection,
        "SELECT COUNT(*) AS count FROM order_tenders WHERE order_guid = ?",
        ["order-mixed"],
      ),
      1,
    );
    assert.equal(
      await scalar(
        connection,
        "SELECT COUNT(*) AS count FROM outbox_messages WHERE aggregate_id = ?",
        ["order-mixed"],
      ),
      0,
    );
    await assert.rejects(
      () => store.appendCashTenderAtomically({
        ...command,
        amount: { currency: "AUD", cents: 401 },
        change: { currency: "AUD", cents: 99 },
      }),
      /different immutable content|tender/i,
    );
    await assert.rejects(
      () => store.appendCashTenderAtomically({
        ...command,
        tenderedAmount: { currency: "AUD", cents: 600 },
        change: { currency: "AUD", cents: 200 },
      }),
      /different immutable content/,
    );
    assert.equal(
      await scalar(
        connection,
        `SELECT tendered_cents AS count
         FROM mixed_cash_tender_actions
         WHERE order_guid = 'order-mixed' AND action_id = 'cash-action'`,
      ),
      500,
    );
    assert.equal(
      await scalar(
        connection,
        `SELECT change_cents AS count
         FROM mixed_cash_tender_actions
         WHERE order_guid = 'order-mixed' AND action_id = 'cash-action'`,
      ),
      100,
    );
    const appendedAudit = await connection.getFirst<{
      payload_json: unknown;
    }>(
      `SELECT payload_json
       FROM audit_events
       WHERE order_guid = 'order-mixed'
         AND correlation_id = 'cash-action'`,
    );
    assert.deepEqual(JSON.parse(String(appendedAudit?.payload_json)), {
      action: "mixed-cash-tender-appended",
      appliedCents: 400,
      tenderedCents: 500,
      changeCents: 100,
      tenderGuid: one.tenderGuid,
      requestingCashierId: "cashier-actor",
      requestingCashierName: "Actor Alice",
      requestingUserGuid: "user-guid-actor",
    });

    const reversed = await store.reverseTender({
      actionId: "reverse-shared",
      orderGuid: "order-mixed",
      tenderGuid: one.tenderGuid,
      actor: paymentAuditActor(),
    });
    assert.equal(reversed.state, "reversed");
    assert.equal(reversed.replayed, false);
    assert.equal(
      reversed.truth.tenders.find(
        (tender) => tender.tenderGuid === one.tenderGuid,
      )?.amount.cents,
      400,
    );
    assert.equal(
      reversed.truth.tenders.find(
        (tender) => tender.tenderGuid === reversed.reversalTenderGuid,
      )?.amount.cents,
      -400,
    );
    assert.equal(
      (await store.reverseTender({
        actionId: "reverse-shared",
        orderGuid: "order-mixed",
        tenderGuid: one.tenderGuid,
        actor: paymentAuditActor(),
      })).replayed,
      true,
    );
    await assert.rejects(
      () => store.reverseTender({
        actionId: "reverse-other",
        orderGuid: "order-mixed",
        tenderGuid: one.tenderGuid,
        actor: paymentAuditActor(),
      }),
      /already has an immutable reversal/,
    );
  });
});

test("真实 SQLite：reversal action 以 order+action 查询，两订单同 action 不误读且 card 只返回 pending", async () => {
  await withDatabase("mixed-reversal-scope", async (connection) => {
    await migrateFresh(connection);
    for (const [sequence, orderGuid] of ["order-a", "order-b"].entries()) {
      await insertOrder(connection, {
        orderGuid,
        sequence: sequence + 1,
        storeCode: `S-${sequence}`,
        deviceCode: `D-${sequence}`,
        cashierId: "C",
        amountCents: 500,
        state: "Completing",
        syncProvenance: TEST_SYNC_PROVENANCE,
      });
      await insertTender(
        connection,
        `cash-${orderGuid}`,
        orderGuid,
        "cash",
        200,
      );
    }
    const ids = sequenceIds("reverse-guid", "reverse-audit");
    const store = new SqliteMixedPaymentTenderStore(
      connection,
      {
        createTenderGuid: ids.createOrderGuid,
        createAuditEventId: ids.createAuditEventId,
      },
      () => T1,
    );
    const a = await store.reverseTender({
      actionId: "same-action",
      orderGuid: "order-a",
      tenderGuid: "cash-order-a",
      actor: paymentAuditActor(),
    });
    const b = await store.reverseTender({
      actionId: "same-action",
      orderGuid: "order-b",
      tenderGuid: "cash-order-b",
      actor: paymentAuditActor(),
    });
    assert.notEqual(a.reversalTenderGuid, b.reversalTenderGuid);
    assert.equal(a.truth.orderGuid, "order-a");
    assert.equal(b.truth.orderGuid, "order-b");

    await insertTender(
      connection,
      "card-source",
      "order-b",
      "card",
      100,
    );
    const pending = await store.reverseTender({
      actionId: "card-reverse",
      orderGuid: "order-b",
      tenderGuid: "card-source",
      actor: paymentAuditActor(),
    });
    assert.equal(pending.state, "pending");
    assert.equal(pending.reversalTenderGuid, null);
    assert.equal(
      await scalar(
        connection,
        "SELECT COUNT(*) AS count FROM payment_tender_reversal_links WHERE order_guid = ? AND action_id = ?",
        ["order-b", "card-reverse"],
      ),
      0,
    );
  });
});

test("真实 SQLite：最终现金 round-down 保留订单入账并持久化规范化实收", async () => {
  await withDatabase("mixed-cash-round-down", async (connection) => {
    await migrateFresh(connection);
    await insertOrder(connection, {
      orderGuid: "order-round-down",
      sequence: 1,
      storeCode: "S-MIX",
      deviceCode: "D-MIX",
      cashierId: "C-MIX",
      amountCents: 1_002,
      state: "Draft",
      syncProvenance: TEST_SYNC_PROVENANCE,
    });
    const ids = sequenceIds("round-down-tender", "round-down-audit");
    const store = new SqliteMixedPaymentTenderStore(
      connection,
      {
        createTenderGuid: ids.createOrderGuid,
        createAuditEventId: ids.createAuditEventId,
      },
      () => T1,
      {
        planner: {
          async planFinalCash(input) {
            return completionPlan(input.orderGuid, "round-down");
          },
        },
        encryptor,
      },
    );

    await assert.rejects(
      () => store.appendCashTenderAtomically({
        actionId: "round-down-non-canonical-action",
        orderGuid: "order-round-down",
        actor: paymentAuditActor(),
        amount: { currency: "AUD", cents: 1_002 },
        tenderedAmount: { currency: "AUD", cents: 1_001 },
        change: { currency: "AUD", cents: 1 },
      }),
      /tendered, applied and change amounts are inconsistent/,
    );

    const result = await store.appendCashTenderAtomically({
      actionId: "round-down-action",
      orderGuid: "order-round-down",
      actor: paymentAuditActor(),
      amount: { currency: "AUD", cents: 1_002 },
      tenderedAmount: { currency: "AUD", cents: 1_000 },
      change: { currency: "AUD", cents: 0 },
    });

    assert.equal(result.replayed, false);
    assert.equal(result.truth.tenders[0]?.amount.cents, 1_002);
    assert.deepEqual(result.cashSettlement, {
      tendered: { currency: "AUD", cents: 1_000 },
      applied: { currency: "AUD", cents: 1_002 },
      change: { currency: "AUD", cents: 0 },
    });
    const replay = await store.appendCashTenderAtomically({
      actionId: "round-down-action",
      orderGuid: "order-round-down",
      actor: paymentAuditActor(),
      amount: { currency: "AUD", cents: 1_002 },
      tenderedAmount: { currency: "AUD", cents: 1_000 },
      change: { currency: "AUD", cents: 0 },
    });
    assert.equal(replay.replayed, true);
    assert.deepEqual(replay.cashSettlement, result.cashSettlement);
    await assert.rejects(
      () => store.appendCashTenderAtomically({
        actionId: "round-down-action",
        orderGuid: "order-round-down",
        actor: paymentAuditActor(),
        amount: { currency: "AUD", cents: 1_002 },
        tenderedAmount: { currency: "AUD", cents: 1_005 },
        change: { currency: "AUD", cents: 5 },
      }),
      /different immutable content/,
    );
    assert.equal(
      await scalar(
        connection,
        `SELECT tendered_cents AS count
         FROM mixed_cash_tender_actions
         WHERE order_guid = 'order-round-down'`,
      ),
      1_000,
    );
    assert.equal(
      await scalar(
        connection,
        `SELECT change_cents AS count
         FROM mixed_cash_tender_actions
         WHERE order_guid = 'order-round-down'`,
      ),
      0,
    );
  });
});

test("真实 SQLite：最终现金 round-up 与 1/2 分零实收均保持完整入账", async () => {
  await withDatabase("mixed-cash-rounding-boundaries", async (connection) => {
    await migrateFresh(connection);
    for (const [orderGuid, amountCents, tenderedCents] of [
      ["order-round-up", 1_003, 1_005],
      ["order-one-cent", 1, 0],
      ["order-two-cents", 2, 0],
    ] as const) {
      await insertOrder(connection, {
        orderGuid,
        sequence: amountCents,
        storeCode: "S-MIX",
        deviceCode: "D-MIX",
        cashierId: "C-MIX",
        amountCents,
        state: "Draft",
        syncProvenance: TEST_SYNC_PROVENANCE,
      });
      const ids = sequenceIds(`${orderGuid}-tender`, `${orderGuid}-audit`);
      const store = new SqliteMixedPaymentTenderStore(
        connection,
        {
          createTenderGuid: ids.createOrderGuid,
          createAuditEventId: ids.createAuditEventId,
        },
        () => T1,
        {
          planner: {
            async planFinalCash(input) {
              return completionPlan(input.orderGuid, orderGuid);
            },
          },
          encryptor,
        },
      );
      const result = await store.appendCashTenderAtomically({
        actionId: `${orderGuid}-action`,
        orderGuid,
        actor: paymentAuditActor(),
        amount: { currency: "AUD", cents: amountCents },
        tenderedAmount: { currency: "AUD", cents: tenderedCents },
        change: { currency: "AUD", cents: 0 },
      });
      assert.equal(result.truth.tenders[0]?.amount.cents, amountCents);
      assert.equal(
        await scalar(
          connection,
          `SELECT tendered_cents AS count
           FROM mixed_cash_tender_actions WHERE order_guid = ?`,
          [orderGuid],
        ),
        tenderedCents,
      );
    }
  });
});

test("真实 SQLite：M39 保留 M38 旧 action，拒绝伪造部分舍入并保持不可变", async () => {
  await withDatabase("mixed-cash-m39-upgrade", async (connection) => {
    await applyMigrations(
      connection,
      () => T0,
      POS_DATABASE_MIGRATIONS.filter((migration) => migration.version <= 38),
    );
    await insertOrder(connection, {
      orderGuid: "order-m39-legacy",
      sequence: 1,
      storeCode: "S-MIX",
      deviceCode: "D-MIX",
      cashierId: "C-MIX",
      amountCents: 1_000,
      state: "Completing",
      syncProvenance: TEST_SYNC_PROVENANCE,
    });
    await insertTender(connection, "m39-legacy-tender", "order-m39-legacy", "cash", 400);
    await connection.run(
      `INSERT INTO audit_events (
        event_id, event_type, occurred_at_iso, order_guid,
        correlation_id, payload_json, uploaded_at_iso
      ) VALUES ('m39-legacy-audit', 'MIXED_CASH_TENDER_APPENDED', ?,
        'order-m39-legacy', 'm39-legacy-action', '{}', NULL)`,
      [T0],
    );
    await connection.run(
      `INSERT INTO mixed_cash_tender_actions (
        action_id, order_guid, amount_cents, tendered_cents, change_cents,
        tender_guid, audit_event_id, created_at_iso
      ) VALUES ('m39-legacy-action', 'order-m39-legacy', 400, 400, 0,
        'm39-legacy-tender', 'm39-legacy-audit', ?)`,
      [T0],
    );

    await applyMigrations(
      connection,
      () => T1,
      POS_DATABASE_MIGRATIONS.filter((migration) => migration.version === 39),
    );
    assert.equal(
      await scalar(
        connection,
        `SELECT COUNT(*) AS count FROM mixed_cash_tender_actions
         WHERE action_id = 'm39-legacy-action'`,
      ),
      1,
    );
    await assert.rejects(
      () => connection.run(
        `UPDATE mixed_cash_tender_actions SET amount_cents = 401
         WHERE action_id = 'm39-legacy-action'`,
      ),
      /MIXED_CASH_ACTION_IMMUTABLE/,
    );
    await assert.rejects(
      () => connection.run(
        `DELETE FROM mixed_cash_tender_actions
         WHERE action_id = 'm39-legacy-action'`,
      ),
      /MIXED_CASH_ACTION_IMMUTABLE/,
    );

    await insertOrder(connection, {
      orderGuid: "order-m39-partial",
      sequence: 2,
      storeCode: "S-MIX",
      deviceCode: "D-MIX",
      cashierId: "C-MIX",
      amountCents: 1_000,
      state: "Draft",
      syncProvenance: TEST_SYNC_PROVENANCE,
    });
    await insertTender(connection, "m39-partial-tender", "order-m39-partial", "cash", 402);
    await connection.run(
      `INSERT INTO audit_events (
        event_id, event_type, occurred_at_iso, order_guid,
        correlation_id, payload_json, uploaded_at_iso
      ) VALUES ('m39-partial-audit', 'MIXED_CASH_TENDER_APPENDED', ?,
        'order-m39-partial', 'm39-partial-action', '{}', NULL)`,
      [T1],
    );
    await assert.rejects(
      () => new SqliteMixedPaymentTenderStore(
        connection,
        {
          createTenderGuid: () => "m39-store-partial-tender",
          createAuditEventId: () => "m39-store-partial-audit",
        },
        () => T1,
      ).appendCashTenderAtomically({
        actionId: "m39-store-partial-action",
        orderGuid: "order-m39-partial",
        actor: paymentAuditActor(),
        amount: { currency: "AUD", cents: 402 },
        tenderedAmount: { currency: "AUD", cents: 400 },
        change: { currency: "AUD", cents: 0 },
      }),
      /tendered, applied and change amounts are inconsistent/,
    );
    await assert.rejects(
      () => connection.run(
        `INSERT INTO mixed_cash_tender_actions (
          action_id, order_guid, amount_cents, tendered_cents, change_cents,
          tender_guid, audit_event_id, created_at_iso
        ) VALUES ('m39-partial-action', 'order-m39-partial', 402, 400, 0,
          'm39-partial-tender', 'm39-partial-audit', ?)`,
        [T1],
      ),
      /MIXED_CASH_ACTION_AMOUNTS_INVALID/,
    );
  });
});

test("真实 SQLite：M39 trigger 仅允许部分现金走入账差额，最终现金必须走 cashDue", async () => {
  await withDatabase("mixed-cash-m39-final-boundary", async (connection) => {
    await migrateFresh(connection);

    for (const [index, scenario] of ([
      {
        name: "部分现金入账差额",
        amountCents: 1_002,
        tenderedCents: 1_002,
        changeCents: 0,
        accepted: true,
      },
      {
        name: "部分现金合法找零",
        amountCents: 1_002,
        tenderedCents: 1_005,
        changeCents: 3,
        accepted: true,
      },
      {
        name: "最终现金伪造未舍入实收",
        amountCents: 1_002,
        tenderedCents: 1_001,
        changeCents: 1,
        accepted: false,
      },
      {
        name: "最终现金 round-down cashDue",
        amountCents: 1_002,
        tenderedCents: 1_000,
        changeCents: 0,
        accepted: true,
      },
      {
        name: "最终现金一分伪造实收",
        amountCents: 1,
        tenderedCents: 1,
        changeCents: 1,
        accepted: false,
      },
      {
        name: "最终现金一分零实收",
        amountCents: 1,
        tenderedCents: 0,
        changeCents: 0,
        accepted: true,
      },
      {
        name: "最终现金两分伪造实收",
        amountCents: 2,
        tenderedCents: 2,
        changeCents: 2,
        accepted: false,
      },
      {
        name: "最终现金两分零实收",
        amountCents: 2,
        tenderedCents: 0,
        changeCents: 0,
        accepted: true,
      },
      {
        name: "最终现金伪造未舍入入账",
        amountCents: 1_003,
        tenderedCents: 1_003,
        changeCents: 0,
        accepted: false,
      },
      {
        name: "最终现金伪造未舍入找零",
        amountCents: 1_003,
        tenderedCents: 1_010,
        changeCents: 7,
        accepted: false,
      },
      {
        name: "最终现金 cashDue",
        amountCents: 1_003,
        tenderedCents: 1_005,
        changeCents: 0,
        accepted: true,
      },
      {
        name: "最终现金 cashDue 找零",
        amountCents: 1_003,
        tenderedCents: 1_010,
        changeCents: 5,
        accepted: true,
      },
    ] as const).entries()) {
      const suffix = scenario.name.replaceAll("现金", "").replaceAll(" ", "");
      const orderGuid = `order-m39-${suffix}`;
      const tenderGuid = `tender-m39-${suffix}`;
      const auditEventId = `audit-m39-${suffix}`;
      const actionId = `action-m39-${suffix}`;
      const remainingCents = scenario.name.startsWith("部分")
        ? scenario.amountCents + 1
        : scenario.amountCents;

      // 先写入非现金 tender，再写当前现金 tender；remaining 必须只排除当前 tender。
      await insertOrder(connection, {
        orderGuid,
        sequence: 100 + index,
        storeCode: "S-MIX",
        deviceCode: "D-MIX",
        cashierId: "C-MIX",
        amountCents: 1_002 + remainingCents,
        state: "Draft",
        syncProvenance: TEST_SYNC_PROVENANCE,
      });
      await insertTender(connection, `card-${tenderGuid}`, orderGuid, "card", 1_002);
      await insertTender(connection, tenderGuid, orderGuid, "cash", scenario.amountCents);
      await connection.run(
        `INSERT INTO audit_events (
          event_id, event_type, occurred_at_iso, order_guid,
          correlation_id, payload_json, uploaded_at_iso
        ) VALUES (?, 'MIXED_CASH_TENDER_APPENDED', ?, ?, ?, '{}', NULL)`,
        [auditEventId, T1, orderGuid, actionId],
      );

      const insertAction = () => connection.run(
        `INSERT INTO mixed_cash_tender_actions (
          order_guid, action_id, amount_cents, tendered_cents, change_cents,
          tender_guid, audit_event_id, created_at_iso
        ) VALUES (?, ?, ?, ?, ?, ?, ?, ?)`,
        [
          orderGuid,
          actionId,
          scenario.amountCents,
          scenario.tenderedCents,
          scenario.changeCents,
          tenderGuid,
          auditEventId,
          T1,
        ],
      );

      if (scenario.accepted) {
        await insertAction();
      } else {
        await assert.rejects(insertAction, /MIXED_CASH_ACTION_AMOUNTS_INVALID/);
      }
    }
  });
});

test("真实 SQLite：M39 trigger 在缺失订单时 fail-close", async () => {
  await withDatabase("mixed-cash-m39-missing-order", async (connection) => {
    await migrateFresh(connection);
    await connection.exec("PRAGMA foreign_keys = OFF");
    try {
      await insertTender(connection, "tender-m39-missing", "order-m39-missing", "cash", 1_003);
      await connection.run(
        `INSERT INTO audit_events (
          event_id, event_type, occurred_at_iso, order_guid,
          correlation_id, payload_json, uploaded_at_iso
        ) VALUES ('audit-m39-missing', 'MIXED_CASH_TENDER_APPENDED', ?,
          'order-m39-missing', 'action-m39-missing', '{}', NULL)`,
        [T1],
      );
      await assert.rejects(
        () => connection.run(
          `INSERT INTO mixed_cash_tender_actions (
            order_guid, action_id, amount_cents, tendered_cents, change_cents,
            tender_guid, audit_event_id, created_at_iso
          ) VALUES ('order-m39-missing', 'action-m39-missing', 1_003, 1_003, 0,
            'tender-m39-missing', 'audit-m39-missing', ?)`,
          [T1],
        ),
        /MIXED_CASH_ACTION_AMOUNTS_INVALID/,
      );
    } finally {
      await connection.exec("PRAGMA foreign_keys = ON");
    }
  });
});

test("真实 SQLite：M23 旧现金 action 升级 M24 后按实收等于入账、找零为零重放", async () => {
  await withDatabase("mixed-cash-m24-upgrade", async (connection) => {
    await applyMigrations(
      connection,
      () => T0,
      POS_DATABASE_MIGRATIONS.filter(
        (migration) => migration.version <= 23,
      ),
    );
    await insertOrder(connection, {
      orderGuid: "order-mixed-legacy",
      sequence: 1,
      storeCode: "S-MIX",
      deviceCode: "D-MIX",
      cashierId: "C-MIX",
      amountCents: 1_000,
      state: "Completing",
      syncProvenance: TEST_SYNC_PROVENANCE,
    });
    await connection.run(
      `INSERT INTO order_tenders (
        tender_guid, order_guid, method, amount_cents,
        payment_attempt_id, created_at_iso
      ) VALUES ('tender-mixed-legacy', 'order-mixed-legacy',
        'cash', 400, NULL, ?)`,
      [T0],
    );
    await connection.run(
      `INSERT INTO audit_events (
        event_id, event_type, occurred_at_iso, order_guid,
        correlation_id, payload_json, uploaded_at_iso
      ) VALUES ('audit-mixed-legacy', 'MIXED_CASH_TENDER_APPENDED',
        ?, 'order-mixed-legacy', 'cash-action-legacy',
        '{"action":"mixed-cash-tender-appended","amountCents":400,"tenderGuid":"tender-mixed-legacy"}',
        NULL)`,
      [T0],
    );
    await connection.run(
      `INSERT INTO mixed_cash_tender_actions (
        action_id, order_guid, amount_cents, tender_guid,
        audit_event_id, created_at_iso
      ) VALUES ('cash-action-legacy', 'order-mixed-legacy', 400,
        'tender-mixed-legacy', 'audit-mixed-legacy', ?)`,
      [T0],
    );

    await applyMigrations(connection, () => T1);
    const upgraded = await connection.getFirst<{
      tendered_cents: unknown;
      change_cents: unknown;
    }>(
      `SELECT tendered_cents, change_cents
       FROM mixed_cash_tender_actions
       WHERE action_id = 'cash-action-legacy'`,
    );
    assert.equal(upgraded?.tendered_cents, null);
    assert.equal(upgraded?.change_cents, null);

    const store = new SqliteMixedPaymentTenderStore(
      connection,
      {
        createTenderGuid: () => "unused-tender",
        createAuditEventId: () => "unused-audit",
      },
      () => T1,
    );
    const replay = await store.appendCashTenderAtomically({
      actionId: "cash-action-legacy",
      orderGuid: "order-mixed-legacy",
      amount: { currency: "AUD", cents: 400 },
      actor: paymentAuditActor(),
    });
    assert.equal(replay.replayed, true);
    assert.equal(replay.tenderGuid, "tender-mixed-legacy");
  });
});

test("真实 SQLite：final cash 原子完成订单/outbox/履约，规划或写入失败不会留下 zero-balance Completing", async () => {
  await withDatabase("mixed-final", async (connection) => {
    await migrateFresh(connection);
    await insertOrder(connection, {
      orderGuid: "order-final",
      sequence: 1,
      storeCode: "S-FINAL",
      deviceCode: "D-FINAL",
      cashierId: "C-FINAL",
      amountCents: 500,
      state: "Draft",
      syncProvenance: TEST_SYNC_PROVENANCE,
    });
    const finalPlan = completionPlan("order-final", "final");
    const ids = sequenceIds("final-tender", "final-audit");
    const store = new SqliteMixedPaymentTenderStore(
      connection,
      {
        createTenderGuid: ids.createOrderGuid,
        createAuditEventId: ids.createAuditEventId,
      },
      () => T1,
      {
        planner: {
          async planFinalCash(input) {
            assert.equal(input.amount.cents, input.expectedRemaining.cents);
            return finalPlan;
          },
        },
        encryptor,
      },
    );
    const result = await store.appendCashTenderAtomically({
      actionId: "final-action",
      orderGuid: "order-final",
      amount: { currency: "AUD", cents: 500 },
      actor: paymentAuditActor(),
    });
    assert.equal(result.truth.state, "PendingSync");
    assert.equal(
      result.truth.tenders.reduce(
        (sum, tender) => sum + tender.amount.cents,
        0,
      ),
      500,
    );
    assert.equal(
      await scalar(
        connection,
        "SELECT COUNT(*) AS count FROM outbox_messages WHERE aggregate_id = 'order-final'",
      ),
      1,
    );
    assert.equal(
      await scalar(
        connection,
        "SELECT COUNT(*) AS count FROM print_jobs WHERE order_guid = 'order-final'",
      ),
      1,
    );
    assert.equal(
      await scalar(
        connection,
        "SELECT COUNT(*) AS count FROM drawer_events WHERE order_guid = 'order-final'",
      ),
      1,
    );

    await insertOrder(connection, {
      orderGuid: "order-plan-fail",
      sequence: 2,
      storeCode: "S-PLAN",
      deviceCode: "D-PLAN",
      cashierId: "C-PLAN",
      amountCents: 300,
      state: "Draft",
      syncProvenance: TEST_SYNC_PROVENANCE,
    });
    const plannerFailure = new SqliteMixedPaymentTenderStore(
      connection,
      {
        createTenderGuid: () => "unused-tender",
        createAuditEventId: () => "unused-audit",
      },
      () => T1,
      {
        planner: {
          async planFinalCash() {
            throw new Error("planner failed");
          },
        },
        encryptor,
      },
    );
    await assert.rejects(
      () => plannerFailure.appendCashTenderAtomically({
        actionId: "planner-fail",
        orderGuid: "order-plan-fail",
        amount: { currency: "AUD", cents: 300 },
        actor: paymentAuditActor(),
      }),
      /planner failed/,
    );
    await assertDraftHasNoTender(connection, "order-plan-fail");

    await insertOrder(connection, {
      orderGuid: "order-write-fail",
      sequence: 3,
      storeCode: "S-WRITE",
      deviceCode: "D-WRITE",
      cashierId: "C-WRITE",
      amountCents: 300,
      state: "Draft",
      syncProvenance: TEST_SYNC_PROVENANCE,
    });
    const duplicateOutboxPlan = {
      ...completionPlan("order-write-fail", "write-fail"),
      outbox: {
        ...completionPlan("order-write-fail", "write-fail").outbox,
        messageId: finalPlan.outbox.messageId,
      },
    };
    const writeFailure = new SqliteMixedPaymentTenderStore(
      connection,
      {
        createTenderGuid: () => "write-fail-tender",
        createAuditEventId: () => "write-fail-audit",
      },
      () => T1,
      {
        planner: { async planFinalCash() { return duplicateOutboxPlan; } },
        encryptor,
      },
    );
    await assert.rejects(
      () => writeFailure.appendCashTenderAtomically({
        actionId: "write-fail",
        orderGuid: "order-write-fail",
        amount: { currency: "AUD", cents: 300 },
        actor: paymentAuditActor(),
      }),
      /UNIQUE constraint failed/,
    );
    await assertDraftHasNoTender(connection, "order-write-fail");

    await insertOrder(connection, {
      orderGuid: "order-no-planner",
      sequence: 4,
      storeCode: "S-NO-PLANNER",
      deviceCode: "D-NO-PLANNER",
      cashierId: "C-NO-PLANNER",
      amountCents: 300,
      state: "Draft",
      syncProvenance: TEST_SYNC_PROVENANCE,
    });
    const noPlanner = new SqliteMixedPaymentTenderStore(
      connection,
      {
        createTenderGuid: () => "no-planner-tender",
        createAuditEventId: () => "no-planner-audit",
      },
      () => T1,
    );
    await assert.rejects(
      () => noPlanner.appendCashTenderAtomically({
        actionId: "no-planner",
        orderGuid: "order-no-planner",
        amount: { currency: "AUD", cents: 300 },
        actor: paymentAuditActor(),
      }),
      /requires a durable completion planner/,
    );
    await assertDraftHasNoTender(connection, "order-no-planner");

    await insertOrder(connection, {
      orderGuid: "order-partial-rollback",
      sequence: 5,
      storeCode: "S-PARTIAL-ROLLBACK",
      deviceCode: "D-PARTIAL-ROLLBACK",
      cashierId: "C-PARTIAL-ROLLBACK",
      amountCents: 300,
      state: "Draft",
      syncProvenance: TEST_SYNC_PROVENANCE,
    });
    await connection.run(
      `INSERT INTO audit_events (
        event_id, event_type, occurred_at_iso, order_guid,
        correlation_id, payload_json, uploaded_at_iso
      ) VALUES ('duplicate-partial-audit', 'TEST', ?, NULL, 'seed', '{}', NULL)`,
      [T0],
    );
    const partialWriteFailure = new SqliteMixedPaymentTenderStore(
      connection,
      {
        createTenderGuid: () => "partial-rollback-tender",
        createAuditEventId: () => "duplicate-partial-audit",
      },
      () => T1,
    );
    await assert.rejects(
      () => partialWriteFailure.appendCashTenderAtomically({
        actionId: "partial-rollback",
        orderGuid: "order-partial-rollback",
        amount: { currency: "AUD", cents: 100 },
        actor: paymentAuditActor(),
      }),
      /UNIQUE constraint failed/,
    );
    await assertDraftHasNoTender(connection, "order-partial-rollback");
  });
});

test("真实 SQLite：voucher preparation 响应丢失/重启仍稳定绑定，券码和退款原因只在密文", async () => {
  const folder = mkdtempSync(join(tmpdir(), "hb-pos-voucher-prepare-"));
  const path = join(folder, "voucher-prepare.db");
  const connection = new SystemSqliteConnection(path);
  try {
    await migrateFresh(connection);
    await insertOrder(connection, {
      orderGuid: "order-voucher-prepared",
      sequence: 1,
      storeCode: "S-VOUCHER",
      deviceCode: "D-VOUCHER",
      cashierId: "C-VOUCHER",
      amountCents: 500,
      state: "Draft",
      syncProvenance: TEST_SYNC_PROVENANCE,
    });
    const store = new SqliteVoucherPreparationStore(
      connection,
      encryptor,
      () => "vpc_abcdefghijklmnop",
      () => T0,
    );
    const context = {
      actionId: "voucher-action",
      orderGuid: "order-voucher-prepared",
      operation: "purchase" as const,
      storeCode: "S-VOUCHER",
      cashierId: "C-VOUCHER",
      voucherCode: "SECRET-VOUCHER-123",
      refundReason: null,
    };
    assert.equal(await store.prepare(context), "vpc_abcdefghijklmnop");
    assert.equal(await store.prepare(context), "vpc_abcdefghijklmnop");
    await assert.rejects(
      () => store.prepare({ ...context, voucherCode: "DIFFERENT" }),
      /different protected content/,
    );
    const raw = await connection.getFirst<{
      plaintext: unknown;
      cipher_hex: unknown;
    }>(
      `SELECT
        protected_reference || order_guid || action_id || operation AS plaintext,
        HEX(context_ciphertext) AS cipher_hex
       FROM voucher_prepared_contexts`,
    );
    assert.equal(String(raw?.plaintext).includes("SECRET-VOUCHER-123"), false);
    assert.equal(
      Buffer.from(String(raw?.cipher_hex), "hex")
        .toString("utf8")
        .includes("SECRET-VOUCHER-123"),
      false,
    );
    await insertActionBinding(
      connection,
      context.orderGuid,
      context.actionId,
      "attempt-voucher-prepared",
      "idempotency-voucher-prepared",
      ["voucher", "purchase", "AUD", 500],
    );
    await insertAttempt(connection, {
      attemptId: "attempt-voucher-prepared",
      idempotencyKey: "idempotency-voucher-prepared",
      orderGuid: context.orderGuid,
      provider: "voucher",
      operation: "purchase",
      amountCents: 500,
      state: "Submitted",
    });
    const binding = {
      orderGuid: context.orderGuid,
      operation: "purchase" as const,
      attemptId: "attempt-voucher-prepared",
      idempotencyKey: "idempotency-voucher-prepared",
    };
    await store.bindToAttempt(binding); // 模拟响应丢失
    await connection.close();
    const reopened = new SystemSqliteConnection(path);
    const reopenedStore = new SqliteVoucherPreparationStore(
      reopened,
      encryptor,
      () => "vpc_should_not_be_used",
      () => T1,
    );
    const recovered = await reopenedStore.bindToAttempt(binding);
    assert.equal(recovered?.voucherCode, "SECRET-VOUCHER-123");
    assert.equal(recovered?.protectedReference, "vpc_abcdefghijklmnop");
    assert.equal(
      await reopenedStore.bindToAttempt({
        ...binding,
        attemptId: "another-attempt",
      }),
      null,
    );
    await reopened.close();
  } finally {
    try { await connection.close(); } catch { /* already closed */ }
    rmSync(folder, { recursive: true, force: true });
  }
});

test("真实 SQLite：VoucherProtectedToken stable vpr_、phase CAS、重启恢复且敏感状态不入明文列", async () => {
  const folder = mkdtempSync(join(tmpdir(), "hb-pos-voucher-state-"));
  const path = join(folder, "voucher-state.db");
  const connection = new SystemSqliteConnection(path);
  try {
    await migrateFresh(connection);
    await insertOrder(connection, {
      orderGuid: "order-voucher-state",
      sequence: 1,
      storeCode: "S-VSTATE",
      deviceCode: "D-VSTATE",
      cashierId: "C-VSTATE",
      amountCents: 500,
      state: "Draft",
      syncProvenance: TEST_SYNC_PROVENANCE,
    });
    await insertAttempt(connection, {
      attemptId: "attempt-voucher-state",
      idempotencyKey: "idempotency-voucher-state",
      orderGuid: "order-voucher-state",
      provider: "voucher",
      operation: "purchase",
      amountCents: 500,
      state: "Submitted",
    });
    const store = new SqliteVoucherProtectedTokenStore(
      connection,
      encryptor,
      () => "vpr_abcdefghijklmnop",
      () => T0,
    );
    const prepared = {
      attemptId: "attempt-voucher-state",
      idempotencyKey: "idempotency-voucher-state",
      orderGuid: "order-voucher-state",
      operation: "purchase" as const,
      phase: "purchase-prepared" as const,
      storeCode: "S-VSTATE",
      cashierId: "C-VSTATE",
      voucherCode: "VOUCHER-SENSITIVE",
      reservationToken: null,
      amountCents: 500,
      expiresAtIso: null,
      reason: null,
    };
    assert.equal(await store.save(prepared), "vpr_abcdefghijklmnop");
    assert.equal(await store.save(prepared), "vpr_abcdefghijklmnop");
    await store.save({ ...prepared, phase: "lock-submitted" });
    const approved = {
      ...prepared,
      phase: "approved" as const,
      reservationToken: "RESERVATION-SENSITIVE",
      expiresAtIso: "2026-07-29T00:00:00.000Z",
    };
    await store.save(approved);
    await assert.rejects(
      () => store.save({ ...prepared, phase: "purchase-prepared" }),
      /transition is invalid/,
    );
    const raw = await connection.getFirst<{
      plaintext: unknown;
      cipher_hex: unknown;
    }>(
      `SELECT
        protected_reference || attempt_id || idempotency_key || order_guid
          AS plaintext,
        HEX(state_ciphertext) AS cipher_hex
       FROM voucher_protected_attempt_states`,
    );
    const rawCipher = Buffer.from(String(raw?.cipher_hex), "hex")
      .toString("utf8");
    assert.equal(String(raw?.plaintext).includes("VOUCHER-SENSITIVE"), false);
    assert.equal(rawCipher.includes("VOUCHER-SENSITIVE"), false);
    assert.equal(rawCipher.includes("RESERVATION-SENSITIVE"), false);
    await connection.close();

    const reopened = new SystemSqliteConnection(path);
    const reopenedStore = new SqliteVoucherProtectedTokenStore(
      reopened,
      encryptor,
      () => "vpr_should_not_be_used",
      () => T2,
    );
    assert.deepEqual(
      await reopenedStore.getByAttempt("attempt-voucher-state"),
      {
        ...approved,
        protectedReference: "vpr_abcdefghijklmnop",
      },
    );
    assert.deepEqual(
      await reopenedStore.resolve("vpr_abcdefghijklmnop"),
      await reopenedStore.getByAttempt("attempt-voucher-state"),
    );
    await assert.rejects(
      () => reopenedStore.save({
        ...approved,
        orderGuid: "different-order",
      }),
      /persisted attempt and order/,
    );
    await reopened.close();
  } finally {
    try { await connection.close(); } catch { /* already closed */ }
    rmSync(folder, { recursive: true, force: true });
  }
});

test("真实 SQLite：支付草稿重启恢复保留精确召回 binding", async () => {
  await withDatabase("draft-recalled-binding-recovery", async (connection) => {
    await migrateFresh(connection);
    const identity = {
      storeCode: "S-RECALLED",
      deviceCode: "D-RECALLED",
      cashierId: "C-RECALLED",
      cashierName: "Recalled cashier",
    };
    const recallBinding: RecallActiveBinding = {
      kind: "recalled",
      scope: {
        storeCode: identity.storeCode,
        deviceCode: identity.deviceCode,
      },
      holdId: "hold-recalled-payment",
      recallAttemptId: "attempt-recalled-payment",
    };
    const store = new SqlitePaymentDraftRecoveryStore(
      connection,
      sequenceIds("order-recalled-payment", "audit-recalled-payment"),
      () => T1,
    );
    await store.createOrReuseDraft(draftInput({
      draftId: "draft-recalled-payment",
      identity,
      recallBinding,
    }));

    const recovery = await store.findBlockingRecovery({
      storeCode: identity.storeCode,
      deviceCode: identity.deviceCode,
    });
    assert.ok(recovery);
    assert.deepEqual(recovery.recallBinding, recallBinding);

    assert.throws(
      () => store.createOrReuseDraft(draftInput({
        draftId: "draft-wrong-recalled-scope",
        identity,
        recallBinding: {
          ...recallBinding,
          scope: { ...recallBinding.scope, deviceCode: "D-OTHER" },
        },
      })),
      /recall binding scope/u,
    );
  });
});

function draftInput(
  overrides: Partial<{
    draftId: string;
    identity: {
      storeCode: string;
      deviceCode: string;
      cashierId: string;
      cashierName: string;
    };
    cart: CartSnapshot;
    pricingState: PricingCartStateSnapshot;
    recallBinding: RecallActiveBinding | null;
  }> = {},
) {
  return {
    draftId: overrides.draftId ?? "draft-1",
    identity: overrides.identity ?? {
      storeCode: "S1",
      deviceCode: "D1",
      cashierId: "C1",
      cashierName: "Cashier",
    },
    soldAtIso: T0,
    cart: overrides.cart ?? saleCart(),
    pricingState: overrides.pricingState ?? salePricingState(),
    recallBinding: overrides.recallBinding ?? null,
  };
}

function saleCart(): CartSnapshot {
  return {
    revision: 7,
    mode: "sale",
    lines: [
      {
        lineId: "line-1",
        productCode: "P1",
        itemNumber: "I1",
        lookupCode: "L1",
        displayName: "Promotion product",
        quantity: "1",
        unitPrice: { currency: "AUD", cents: 600 },
        discount: { currency: "AUD", cents: 100 },
        actualAmount: { currency: "AUD", cents: 500 },
        priceSource: "promotion",
        syncProvenance: {
          referenceCode: "REF-P1",
          priceSource: 2,
        },
        kind: "sale",
        returnSourceKey: null,
        originalOrderGuid: null,
        originalOrderDetailGuid: null,
      },
      {
        lineId: "line-2",
        productCode: "P2",
        itemNumber: "I2",
        lookupCode: "L2",
        displayName: "Manual discount product",
        quantity: "1",
        unitPrice: { currency: "AUD", cents: 500 },
        discount: { currency: "AUD", cents: 100 },
        actualAmount: { currency: "AUD", cents: 400 },
        priceSource: "catalog",
        syncProvenance: {
          referenceCode: null,
          priceSource: 4,
        },
        kind: "sale",
        returnSourceKey: null,
        originalOrderGuid: null,
        originalOrderDetailGuid: null,
      },
    ],
    subtotal: { currency: "AUD", cents: 1100 },
    discount: { currency: "AUD", cents: 200 },
    actualAmount: { currency: "AUD", cents: 900 },
  };
}

function salePricingState(): PricingCartStateSnapshot {
  return {
    revision: 7,
    mode: "sale",
    asOfIso: "2026-07-27T23:30:00.000Z",
    promotions: [
      {
        id: "promo-1",
        name: "Promotion one",
        effectiveStartIso: "2026-07-01T00:00:00.000Z",
        effectiveEndIso: "2026-07-31T23:59:59.999Z",
        isExclusive: false,
        priority: 10,
        applyQuantity: 1,
        fixedPrice: { currency: "AUD", cents: 500 },
        maxApplicationsPerOrder: 1,
        products: [{ productCode: "P1", unitWeight: 1 }],
      },
    ],
    lines: [
      {
        lineId: "line-1",
        productCode: "P1",
        itemNumber: "I1",
        lookupCode: "L1",
        displayName: "Promotion product",
        quantity: 1,
        unitPriceCents: 600,
        basePriceSource: "catalog",
        syncProvenance: {
          referenceCode: "REF-P1",
          priceSource: 2,
        },
        kind: "sale",
        returnSourceKey: null,
        originalOrderGuid: null,
        originalOrderDetailGuid: null,
        discountState: {
          kind: "promotion",
          cents: 100,
          promotionIds: ["promo-1"],
        },
      },
      {
        lineId: "line-2",
        productCode: "P2",
        itemNumber: "I2",
        lookupCode: "L2",
        displayName: "Manual discount product",
        quantity: 1,
        unitPriceCents: 500,
        basePriceSource: "catalog",
        syncProvenance: {
          referenceCode: null,
          priceSource: 4,
        },
        kind: "sale",
        returnSourceKey: null,
        originalOrderGuid: null,
        originalOrderDetailGuid: null,
        discountState: {
          kind: "manual-percent",
          basisPoints: 2_000,
        },
      },
    ],
  };
}

async function insertReturnActionAndOrder(
  connection: SqliteConnectionPort,
  suffix: string,
  sequence: number,
  syncProvenance: TestSyncProvenance | null,
): Promise<void> {
  const actionId = `m14-action-${suffix}`;
  const orderGuid = `m14-order-${suffix}`;
  await insertOrder(connection, {
    orderGuid,
    sequence,
    storeCode: "S-M14",
    deviceCode: "D-M14",
    cashierId: "C-M14",
    amountCents: -100,
    state: "PendingSync",
    syncProvenance,
  });
  await connection.run(
    `INSERT INTO return_actions (
      action_id, request_fingerprint, return_order_guid,
      action_recovery_token, source_kind, total_refund_cents, online,
      store_code, device_code, cashier_id, cashier_name, session_epoch,
      supervisor_grant_id, plan_json, state, created_at_iso,
      completed_at_iso, updated_at_iso
    ) VALUES (?, ?, ?, ?, 'receipt', 100, 1, 'S-M14', 'D-M14',
      'C-M14', 'M14 Cashier', ?, NULL, '{}', 'completed', ?, ?, ?)`,
    [
      actionId,
      `m14-fingerprint-${suffix}`,
      orderGuid,
      `m14-recovery-${suffix}`,
      `m14-session-${suffix}`,
      T0,
      T1,
      T1,
    ],
  );
}

async function insertM13ReturnFulfilmentPlan(
  connection: SqliteConnectionPort,
  input: Readonly<{
    suffix: string;
    sequence: number;
    materializedAtIso: string | null;
  }>,
): Promise<void> {
  await insertReturnActionAndOrder(
    connection,
    input.suffix,
    input.sequence,
    null,
  );
  const printJobId = `m13-print-${input.suffix}`;
  if (input.materializedAtIso !== null) {
    await connection.run(
      `INSERT INTO print_jobs (
        job_id, order_guid, state, printer_id, receipt_ciphertext,
        is_reprint, retry_count, last_error_code, created_at_iso,
        updated_at_iso
      ) VALUES (?, ?, 'Printed', 'm13-printer', ?, 0, 0, NULL, ?, ?)`,
      [
        printJobId,
        `m14-order-${input.suffix}`,
        new Uint8Array([0xa5]),
        input.materializedAtIso,
        input.materializedAtIso,
      ],
    );
  }
  await connection.run(
    `INSERT INTO return_fulfilment_plans (
      action_id, return_order_guid, print_job_id, drawer_event_id,
      print_receipt, drawer_required, materialized_at_iso, created_at_iso
    ) VALUES (?, ?, ?, NULL, 1, 0, ?, ?)`,
    [
      `m14-action-${input.suffix}`,
      `m14-order-${input.suffix}`,
      printJobId,
      input.materializedAtIso,
      T0,
    ],
  );
}

async function insertM14ReturnFulfilmentPlan(
  connection: SqliteConnectionPort,
  input: Readonly<{
    suffix: string;
    sequence: number;
    receiptKind: "none" | "refund-voucher" | "refund-receipt";
    printJobId: string | null;
    drawerEventId: string | null;
  }>,
): Promise<void> {
  await insertReturnActionAndOrder(
    connection,
    input.suffix,
    input.sequence,
    TEST_SYNC_PROVENANCE,
  );
  await connection.run(
    `INSERT INTO return_fulfilment_plans (
      action_id, return_order_guid, print_job_id, drawer_event_id,
      receipt_kind, print_receipt, drawer_required,
      materialized_at_iso, created_at_iso
    ) VALUES (?, ?, ?, ?, ?, ?, ?, NULL, ?)`,
    [
      `m14-action-${input.suffix}`,
      `m14-order-${input.suffix}`,
      input.printJobId,
      input.drawerEventId,
      input.receiptKind,
      input.receiptKind === "none" ? 0 : 1,
      input.drawerEventId === null ? 0 : 1,
      T0,
    ],
  );
}

function durableReturnDraft(
  options: {
    actionId?: string;
    requestFingerprint?: string;
    returnOrderGuid?: string;
    actionRecoveryToken?: string;
    originalOrderGuid?: string;
    returnSourceKey?: string;
    capacityId?: string;
    onlineCashOnly?: boolean;
  } = {},
) {
  const actionId = options.actionId ?? "return-action-1";
  const returnOrderGuid = options.returnOrderGuid ?? "return-order-guid-1";
  const originalOrderGuid =
    options.originalOrderGuid ?? "original-return-order";
  const returnSourceKey = options.returnSourceKey ?? "return-source-1";
  const onlineCashOnly = options.onlineCashOnly ?? false;
  const cashCapacityId =
    options.capacityId ?? "return-capacity-cash";
  const allocations = onlineCashOnly
    ? [
        {
          allocationId: "return-allocation-cash",
          index: 0,
          executionKind: "online-refund" as const,
          method: "cash" as const,
          signedAmountCents: -500,
          capacityId: cashCapacityId,
          originalOrderGuid,
          offlineCashProof: null,
          externalAttemptId: "return-external-cash",
          externalAttemptKind: null,
          externalActionId: null,
          durableAttemptId: null,
          status: "created" as const,
          protectedRecoveryKey: null,
        },
      ]
    : [
        {
          allocationId: "return-allocation-cash",
          index: 0,
          executionKind: "offline-cash" as const,
          method: "cash" as const,
          signedAmountCents: -200,
          capacityId: cashCapacityId,
          originalOrderGuid,
          offlineCashProof: {
            evidenceId: "offline-cash-evidence",
            capacityId: cashCapacityId,
            originalOrderGuid,
            remainingCents: 500,
          },
          externalAttemptId: null,
          externalAttemptKind: null,
          externalActionId: null,
          durableAttemptId: null,
          status: "created" as const,
          protectedRecoveryKey: null,
        },
        {
          allocationId: "return-allocation-card",
          index: 1,
          executionKind: "online-refund" as const,
          method: "card" as const,
          signedAmountCents: -300,
          capacityId: "return-capacity-card",
          originalOrderGuid,
          offlineCashProof: null,
          externalAttemptId: "return-external-card",
          externalAttemptKind: null,
          externalActionId: null,
          durableAttemptId: null,
          status: "created" as const,
          protectedRecoveryKey: null,
        },
      ];
  return {
    actionId,
    requestFingerprint:
      options.requestFingerprint ?? "return-request-fingerprint",
    returnOrderGuid,
    actionRecoveryToken:
      options.actionRecoveryToken ?? "return-action-recovery-token",
    identity: {
      storeCode: "S-RETURN",
      deviceCode: "D-RETURN",
      cashierId: "C-RETURN",
      cashierName: "Return Cashier",
      sessionEpoch: "return-session-epoch",
    },
    plan: {
      sourceKind: "receipt" as const,
      totalRefundCents: 500,
      lines: [
        {
          sourceKind: "receipt" as const,
          returnSourceKey,
          originalOrderGuid,
          originalOrderDetailGuid: "original-return-detail",
          productCode: "P-RETURN",
          quantity: 1,
          signedAmountCents: -500,
          syncProvenance: {
            referenceCode: "REF-P-RETURN",
            priceSource: 0 as const,
          },
        },
      ],
      allocations: allocations.map((allocation) => ({
        method: allocation.method,
        signedAmountCents: allocation.signedAmountCents,
        originalCapacityId: allocation.capacityId,
        originalOrderGuid: allocation.originalOrderGuid,
        offlineCashProof: allocation.offlineCashProof,
      })),
      online: true,
    },
    supervisorGrantKey: null,
    createdAtIso: T0,
    lines: [
      {
        lineId:
          options.returnOrderGuid === undefined
            ? "return-line-1"
            : `line-${returnOrderGuid}`,
        selectionKey: `selection-${returnSourceKey}`,
        sourceKind: "receipt" as const,
        returnSourceKey,
        originalOrderGuid,
        originalOrderDetailGuid: "original-return-detail",
        productCode: "P-RETURN",
        itemNumber: "I-RETURN",
        lookupCode: "LOOKUP-RETURN",
        displayName: "Returned Product",
        quantity: 1,
        unitRefundCents: 500,
        signedAmountCents: -500,
        availableQuantity: 1,
        remainingAmountCents: 500,
        syncProvenance: {
          referenceCode: "REF-P-RETURN",
          priceSource: 0 as const,
        },
      },
    ],
    allocations,
  };
}

function durableTwoProviderReturnDraft(
  options: {
    actionId?: string;
    returnOrderGuid?: string;
    returnSourceKey?: string;
  } = {},
) {
  const base = durableReturnDraft({
    actionId: options.actionId ?? "two-provider-return-action",
    requestFingerprint: `fingerprint-${options.actionId ?? "two-provider"}`,
    returnOrderGuid: options.returnOrderGuid ?? "two-provider-return-order",
    actionRecoveryToken: `recovery-${options.actionId ?? "two-provider"}`,
    originalOrderGuid: "original-provider-order",
    returnSourceKey:
      options.returnSourceKey ?? "two-provider-return-source",
  });
  const allocations = [
    {
      allocationId: "provider-allocation-a",
      index: 0,
      executionKind: "online-refund" as const,
      method: "card" as const,
      signedAmountCents: -200,
      capacityId: "return-capacity-provider-a",
      originalOrderGuid: "original-provider-order",
      offlineCashProof: null,
      externalAttemptId: "provider-external-a",
      externalAttemptKind: null,
      externalActionId: null,
      durableAttemptId: null,
      status: "created" as const,
      protectedRecoveryKey: null,
    },
    {
      allocationId: "provider-allocation-b",
      index: 1,
      executionKind: "online-refund" as const,
      method: "card" as const,
      signedAmountCents: -300,
      capacityId: "return-capacity-provider-b",
      originalOrderGuid: "original-provider-order",
      offlineCashProof: null,
      externalAttemptId: "provider-external-b",
      externalAttemptKind: null,
      externalActionId: null,
      durableAttemptId: null,
      status: "created" as const,
      protectedRecoveryKey: null,
    },
  ];
  return {
    ...base,
    plan: {
      ...base.plan,
      allocations: allocations.map((allocation) => ({
        method: allocation.method,
        signedAmountCents: allocation.signedAmountCents,
        originalCapacityId: allocation.capacityId,
        originalOrderGuid: allocation.originalOrderGuid,
        offlineCashProof: allocation.offlineCashProof,
      })),
    },
    allocations,
  };
}

function durableReturnCompletion(
  draft: ReturnType<typeof durableReturnDraft>,
  options: { messageId?: string } = {},
) {
  const methods = draft.plan.allocations.map((allocation) =>
    String(allocation.method));
  const hasCash = methods.includes("cash");
  const hasCard = methods.includes("card");
  // 与 deriveReturnFulfilmentPolicy 同口径：含刷卡出退货小票，否则有退款券就出券面。
  const hasVoucher = methods.includes("voucher");
  const receiptKind:
    | "none"
    | "refund-voucher"
    | "refund-receipt" = hasCard
    ? "refund-receipt"
    : hasVoucher
      ? "refund-voucher"
      : "none";
  return {
    actionId: draft.actionId,
    returnOrderGuid: draft.returnOrderGuid,
    completedAtIso: T2,
    identity: draft.identity,
    plan: draft.plan,
    lines: draft.lines,
    returnRecords: draft.lines.map((line, index) => ({
      returnDetailGuid: `return-detail-${draft.actionId}-${index}`,
      returnOrderGuid: draft.returnOrderGuid,
      originalOrderGuid: line.originalOrderGuid,
      originalOrderDetailGuid: line.originalOrderDetailGuid,
      returnSourceKey: line.returnSourceKey,
      productCode: line.productCode,
      returnQuantity: line.quantity,
      returnAmountCents: -line.signedAmountCents,
    })),
    outbox: {
      messageId: options.messageId ?? `return-outbox-${draft.actionId}`,
      aggregateId: draft.returnOrderGuid,
      idempotencyKey: draft.returnOrderGuid,
      kind: "return-order-sync" as const,
    },
    fulfilment: {
      printJobId:
        receiptKind === "none" ? null : `return-print-${draft.actionId}`,
      drawerEventId:
        hasCash ? `return-drawer-${draft.actionId}` : null,
      receiptKind,
      drawerRequired: hasCash,
    },
  };
}

let paymentLineGuidNamespace = 0;

function sequenceIds(orderPrefix: string, auditPrefix: string) {
  let order = 0;
  let orderLine = 0;
  let audit = 0;
  const lineNamespace = String(++paymentLineGuidNamespace).padStart(8, "0");
  return {
    createOrderGuid: () => `${orderPrefix}-${++order}`,
    createOrderLineGuid: () =>
      `${lineNamespace}-0000-4000-8000-${String(++orderLine).padStart(12, "0")}`,
    createAuditEventId: () => `${auditPrefix}-${++audit}`,
  };
}

function completionPlan(
  orderGuid: string,
  suffix: string,
): MixedCashOrderCompletionPlan {
  return {
    completionAuditEvents: [
      {
        eventId: `completion-audit-${suffix}`,
        eventType: "ORDER_COMPLETED",
        occurredAtIso: T1,
        orderGuid,
        correlationId: `completion-${suffix}`,
        payload: {
          action: "mixed-cash-order-completed",
          amountCents: 500,
        },
      },
    ],
    outbox: {
      messageId: `outbox-${suffix}`,
      aggregateId: orderGuid,
      kind: "order-sync",
      payloadJson: JSON.stringify({ orderGuid }),
      nextAttemptAtIso: T1,
    },
    fulfilment: {
      print: {
        jobId: `print-${suffix}`,
        orderGuid,
        printerId: "printer-1",
        receiptBytes: new Uint8Array([1, 2, 3]),
        isReprint: false,
      },
      drawer: {
        eventId: `drawer-${suffix}`,
        orderGuid,
        printerId: "printer-1",
        printJobId: `print-${suffix}`,
        reason: "cash-sale",
      },
    },
  };
}

function paymentAuditActor() {
  return {
    cashierId: "cashier-actor",
    cashierName: "Actor Alice",
    userGuid: "user-guid-actor",
  } as const;
}

async function migrateFresh(connection: SqliteConnectionPort): Promise<void> {
  await applyMigrations(connection, () => T0);
}

async function insertOrder(
  connection: SqliteConnectionPort,
  input: {
    orderGuid: string;
    sequence: number;
    storeCode: string;
    deviceCode: string;
    cashierId: string;
    amountCents: number;
    state: string;
    syncProvenance: TestSyncProvenance | null;
  },
): Promise<void> {
  await connection.run(
    `INSERT INTO local_orders (
      order_guid, local_sequence, store_code, device_code,
      cashier_id, cashier_name, sold_at_iso, state,
      total_cents, discount_cents, actual_amount_cents,
      original_order_guid, created_at_iso, updated_at_iso
    ) VALUES (?, ?, ?, ?, ?, 'Cashier', ?, ?, ?, 0, ?, NULL, ?, ?)`,
    [
      input.orderGuid,
      input.sequence,
      input.storeCode,
      input.deviceCode,
      input.cashierId,
      T0,
      input.state,
      input.amountCents,
      input.amountCents,
      T0,
      T0,
    ],
  );
  const syncProvenanceColumns =
    input.syncProvenance === null
      ? ""
      : ",\n      reference_code, sync_price_source";
  const syncProvenanceValues =
    input.syncProvenance === null ? "" : ", ?, ?";
  const syncProvenanceParameters =
    input.syncProvenance === null
      ? []
      : [
          input.syncProvenance.referenceCode,
          input.syncProvenance.priceSource,
        ];
  await connection.run(
    `INSERT INTO local_order_lines (
      line_id, order_guid, line_sequence, product_code, item_number,
      lookup_code, display_name, quantity, unit_price_cents,
      discount_cents, actual_amount_cents, price_source, line_kind,
      return_source_key, original_order_guid, original_order_detail_guid${syncProvenanceColumns}
    ) VALUES (?, ?, 1, 'P', NULL, 'P', 'Product', '1', ?, 0, ?,
      'catalog', 'sale', NULL, NULL, NULL${syncProvenanceValues})`,
    [
      `line-${input.orderGuid}`,
      input.orderGuid,
      input.amountCents,
      input.amountCents,
      ...syncProvenanceParameters,
    ],
  );
}

async function insertActivePaymentDraft(
  connection: SqliteConnectionPort,
  orderGuid: string,
  scope: Readonly<{ storeCode: string; deviceCode: string }>,
): Promise<void> {
  await connection.run(
    `INSERT INTO payment_order_draft_bindings (
      draft_id, request_fingerprint, pricing_state_json, order_guid,
      store_code, device_code, state, created_at_iso
    ) VALUES (?, '{}', '{}', ?, ?, ?, 'Active', ?)`,
    [`draft-${orderGuid}`, orderGuid, scope.storeCode, scope.deviceCode, T2],
  );
}

function insertAttempt(
  connection: SqliteConnectionPort,
  input: {
    attemptId: string;
    idempotencyKey: string;
    orderGuid: string;
    provider: string;
    operation: string;
    amountCents: number;
    state: string;
  },
): Promise<void> {
  return connection.run(
    `INSERT INTO payment_attempts (
      attempt_id, idempotency_key, order_guid, provider, operation,
      amount_cents, state, checkout_id, payment_id, session_id,
      txn_ref, rfn, provider_payload_ciphertext, provider_receipt_ciphertext,
      provider_response_code, created_at_iso, updated_at_iso, last_error_code
    ) VALUES (?, ?, ?, ?, ?, ?, ?, NULL, NULL, NULL, NULL, NULL,
      NULL, NULL, NULL, ?, ?, NULL)`,
    [
      input.attemptId,
      input.idempotencyKey,
      input.orderGuid,
      input.provider,
      input.operation,
      input.amountCents,
      input.state,
      T0,
      T0,
    ],
  ).then(() => undefined);
}

function insertActionBinding(
  connection: SqliteConnectionPort,
  orderGuid: string,
  actionId: string,
  attemptId: string,
  idempotencyKey: string,
  signature: readonly [string, string, "AUD", number, ...string[]] = [
    "square",
    "purchase",
    "AUD",
    900,
  ],
): Promise<void> {
  return connection.run(
    `INSERT INTO payment_action_bindings (
      order_guid, action_id, request_signature,
      attempt_id, idempotency_key, created_at_iso, audit_actor_json
    ) VALUES (?, ?, ?, ?, ?, ?, ?)`,
    [
      orderGuid,
      actionId,
      JSON.stringify(signature),
      attemptId,
      idempotencyKey,
      T0,
      paymentActorJson(),
    ],
  ).then(() => undefined);
}

function paymentActorJson(): string {
  return JSON.stringify({
    requestingCashierId: "cashier-test",
    requestingCashierName: "Test Cashier",
    requestingUserGuid: "user-test",
  });
}

function insertTender(
  connection: SqliteConnectionPort,
  tenderGuid: string,
  orderGuid: string,
  method: string,
  amountCents: number,
  paymentAttemptId: string | null = null,
): Promise<void> {
  return connection.run(
    `INSERT INTO order_tenders (
      tender_guid, order_guid, method, amount_cents,
      payment_attempt_id, created_at_iso
    ) VALUES (?, ?, ?, ?, ?, ?)`,
    [
      tenderGuid,
      orderGuid,
      method,
      amountCents,
      paymentAttemptId,
      T0,
    ],
  ).then(() => undefined);
}

async function insertImmutableTenderReversal(
  connection: SqliteConnectionPort,
  orderGuid: string,
  tenderPrefix: string,
  method: string,
  amountCents: number,
): Promise<void> {
  const sourceTenderGuid = `${tenderPrefix}-source`;
  const reversalTenderGuid = `${tenderPrefix}-reversal`;
  await insertTender(
    connection,
    sourceTenderGuid,
    orderGuid,
    method,
    amountCents,
  );
  await insertTender(
    connection,
    reversalTenderGuid,
    orderGuid,
    method,
    -amountCents,
  );
  await connection.run(
    `INSERT INTO payment_tender_reversal_links (
      order_guid, action_id, source_tender_guid,
      reversal_tender_guid, created_at_iso
    ) VALUES (?, ?, ?, ?, ?)`,
    [
      orderGuid,
      `reverse-${tenderPrefix}`,
      sourceTenderGuid,
      reversalTenderGuid,
      T1,
    ],
  );
}

async function assertDraftHasNoTender(
  connection: SqliteConnectionPort,
  orderGuid: string,
): Promise<void> {
  assert.equal(
    await scalar(
      connection,
      "SELECT COUNT(*) AS count FROM order_tenders WHERE order_guid = ?",
      [orderGuid],
    ),
    0,
  );
  assert.equal(
    String(
      (await connection.getFirst<{ state: unknown }>(
        "SELECT state FROM local_orders WHERE order_guid = ?",
        [orderGuid],
      ))?.state,
    ),
    "Draft",
  );
}

async function scalar(
  connection: SqliteConnectionPort,
  sql: string,
  parameters: readonly SqlValue[] = [],
): Promise<number> {
  return Number(
    (await connection.getFirst<{ count: unknown }>(sql, parameters))?.count,
  );
}

async function withDatabase(
  suffix: string,
  operation: (connection: SystemSqliteConnection) => Promise<void>,
): Promise<void> {
  const folder = mkdtempSync(join(tmpdir(), `hb-pos-${suffix}-`));
  const connection = new SystemSqliteConnection(join(folder, "test.db"));
  try {
    await operation(connection);
  } finally {
    try { await connection.close(); } catch { /* already closed */ }
    rmSync(folder, { recursive: true, force: true });
  }
}

function runStatement(
  database: DatabaseSync,
  sql: string,
  parameters: readonly SqlValue[],
): SqlRunResult {
  const result = database.prepare(sql).run(
    ...parameters.map(toSqlInputValue),
  );
  return {
    changes: Number(result.changes),
    lastInsertRowId: Number(result.lastInsertRowid),
  };
}

function getFirst<T extends object>(
  database: DatabaseSync,
  sql: string,
  parameters: readonly SqlValue[],
): T | null {
  return (
    database.prepare(sql).get(...parameters.map(toSqlInputValue)) as
      T | undefined
  ) ?? null;
}

function getAll<T extends object>(
  database: DatabaseSync,
  sql: string,
  parameters: readonly SqlValue[],
): readonly T[] {
  return database.prepare(sql).all(
    ...parameters.map(toSqlInputValue),
  ) as unknown as readonly T[];
}

function toSqlInputValue(value: SqlValue): SQLInputValue {
  return value as SQLInputValue;
}
