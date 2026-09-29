import assert from "node:assert/strict";
import { mkdtempSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { DatabaseSync, type SQLInputValue } from "node:sqlite";
import test from "node:test";

import {
  auditActorPayload,
  type AuditActorSnapshot,
  type PaymentAttempt,
  type PaymentProvider,
  type PaymentProviderReferences,
  type PaymentProviderResult,
} from "@/core/contracts";
import { POS_DATABASE_MIGRATIONS } from "@/core/db/migrations";
import { SqliteApprovedPaymentOrderCommitter } from "@/core/db/pos-database";
import { SqliteMixedPaymentOrderTruthStore } from "@/core/db/sqlite-mixed-payment-order-truth-store";
import { SqlitePaymentActionBindingStore } from "@/core/db/sqlite-payment-action-binding-store";
import { createSqliteRepositories } from "@/core/db/sqlite-repositories";
import type { SqliteConnectionPort, SqlRunResult, SqlValue } from "@hb/pos-db/core/db/types";
import { ApprovedPaymentOrderCompletionService } from "@/features/payments/approved-payment-order-completion";
import { MixedPaymentCoordinator } from "@/features/payments/mixed";
import { ManualCardPaymentAdapter } from "@/features/payments/manual/manual-card-payment-adapter";
import {
  PaymentAttemptBlockedError,
  PaymentAttemptOfflineError,
  PaymentAttemptService,
} from "@hb/pos-payments-core/features/payments/payment-attempt-service";

test("真实 SQLite：manual-card confirmation 经 MixedPaymentCoordinator 到 PaymentAttemptService 后完成入账", async () => {
  await withSqlite(async (connection) => {
    const harness = await createHarness(connection, true);
    const completion = new ApprovedPaymentOrderCompletionService({
      planner: {
        async plan(execution, actor) {
          return {
            tenderGuid: "tender-manual-card",
            completionAuditEvents: [{
              eventId: "audit-manual-card",
              eventType: "PAYMENT_APPROVED_COMPLETE",
              occurredAtIso: T0,
              orderGuid: execution.attempt.orderGuid,
              correlationId: execution.attempt.attemptId,
              payload: { attemptId: execution.attempt.attemptId, ...auditActorPayload(actor) },
            }],
            outbox: {
              messageId: "outbox-manual-card",
              aggregateId: execution.attempt.orderGuid,
              kind: "order-sync",
              payloadJson: JSON.stringify({ orderGuid: execution.attempt.orderGuid }),
              nextAttemptAtIso: T0,
            },
            fulfilment: { print: null, drawer: null },
          };
        },
      },
      committer: new SqliteApprovedPaymentOrderCommitter(connection, testEncryptor, () => T0),
    });
    const coordinator = new MixedPaymentCoordinator({
      actor: input().actor,
      orderTruth: new SqliteMixedPaymentOrderTruthStore(connection),
      paymentAttempts: harness.service,
      approvedCompletion: completion,
    });

    const unconfirmed = await coordinator.addOnlineTender({
        actionId: "manual-unconfirmed",
        orderGuid: "order-payment-matrix",
        provider: "manual-card",
        amount: { currency: "AUD", cents: 500 },
      });
    assert.equal(unconfirmed.status, "recovery-required");
    assert.equal(await scalar(connection, "SELECT COUNT(*) AS count FROM payment_attempts"), 0);

    const result = await coordinator.addOnlineTender({
      actionId: "manual-confirmed",
      orderGuid: "order-payment-matrix",
      provider: "manual-card",
      amount: { currency: "AUD", cents: 500 },
      manualConfirmed: true,
    });
    assert.equal(result.status, "completed");
    assert.equal(await scalar(connection, "SELECT COUNT(*) AS count FROM payment_attempts"), 1);
    assert.equal(await scalar(connection, "SELECT COUNT(*) AS count FROM order_tenders"), 1);
    const order = await connection.getFirst<{ state: string }>(
      "SELECT state FROM local_orders WHERE order_guid = ?",
      ["order-payment-matrix"],
    );
    assert.equal(order?.state, "PendingSync");
    assert.equal(await scalar(connection, "SELECT COUNT(*) AS count FROM audit_events"), 1);
    assert.equal(await scalar(connection, "SELECT COUNT(*) AS count FROM outbox_messages"), 1);
  });
});

const T0 = "2026-07-28T00:00:00.000Z";
const references = (): PaymentProviderReferences => ({
  checkoutId: null,
  paymentId: null,
  sessionId: null,
  txnRef: null,
  rfn: null,
  voucherReservationToken: null,
});

test("fault matrix: known offline binds the action but never creates an attempt or crosses a provider boundary", async () => {
  await withSqlite(async (connection) => {
    const harness = await createHarness(connection, false);

    await assert.rejects(() => harness.service.startAttempt(input()), PaymentAttemptOfflineError);

    assert.equal(await scalar(connection, "SELECT COUNT(*) AS count FROM payment_attempts"), 0);
    assert.equal(await scalar(connection, "SELECT COUNT(*) AS count FROM payment_action_bindings"), 1);
    const binding = await connection.getFirst<{ audit_actor_json: unknown }>(
      `SELECT audit_actor_json
       FROM payment_action_bindings
       WHERE order_guid = 'order-payment-matrix'
         AND action_id = 'payment-action-1'`,
    );
    assert.deepEqual(JSON.parse(String(binding?.audit_actor_json)), {
      requestingCashierId: "cashier-alice",
      requestingCashierName: "Alice",
      requestingUserGuid: "user-alice",
    });
    assert.equal(harness.square.submitCalls, 0);
  });
});

test("fault matrix: response loss persists Unknown; replay/cold recovery retain the same action identity and prohibit switching provider", async () => {
  await withSqlite(async (connection) => {
    const first = await createHarness(connection, true);
    first.square.submitImpl = async () => {
      const error = new Error("timeout after provider accepted request") as Error & { code: string };
      error.code = "NETWORK_RESPONSE_LOST";
      throw error;
    };

    const unknown = await first.service.startAttempt(input());
    assert.equal(unknown.attempt.state, "Unknown");
    assert.equal(unknown.attempt.lastErrorCode, "NETWORK_RESPONSE_LOST");
    assert.equal(first.square.submitCalls, 1);

    await assert.rejects(
      () => first.service.startAttempt({ ...input(), actionId: "switch-provider", provider: "linkly-cloud" }),
      PaymentAttemptBlockedError,
    );
    assert.equal(first.linkly.submitCalls, 0);

    const restarted = await createHarness(connection, true);
    restarted.square.recoverImpl = async (attempt) => {
      assert.equal(attempt.attemptId, unknown.attempt.attemptId);
      assert.equal(attempt.idempotencyKey, unknown.attempt.idempotencyKey);
      return approved({ checkoutId: "checkout-recovered", paymentId: "payment-recovered" });
    };
    const recovered = await restarted.service.startAttempt(input());

    assert.equal(recovered.attempt.state, "Approved");
    assert.equal(recovered.attempt.attemptId, unknown.attempt.attemptId);
    assert.equal(recovered.attempt.orderGuid, "order-payment-matrix");
    assert.equal(restarted.square.submitCalls, 0);
    assert.equal(restarted.square.recoverCalls, 1);
  });
});

test("fault matrix: Submitted, Pending, and Unknown cold recovery query the original provider only", async () => {
  for (const state of ["Submitted", "Pending", "Unknown"] as const) {
    await withSqlite(async (connection) => {
      const initial = await createHarness(connection, true);
      const created = await initial.repositories.payments.insertIfUnblocked(attempt({ state: "Created" }));
      assert.equal(created, null);
      const expected = await initial.repositories.payments.get("attempt-seeded");
      assert.ok(expected);
      assert.equal(
        await initial.repositories.payments.compareAndUpdate(
          expected,
          { ...expected, state, updatedAtIso: "2026-07-28T00:00:01.000Z" },
        ),
        true,
      );

      const restarted = await createHarness(connection, true);
      restarted.square.recoverImpl = async (value) => {
        assert.equal(value.state, state);
        return approved({ paymentId: `payment-${state.toLowerCase()}` });
      };
      const recovered = await restarted.service.recoverAttempt("attempt-seeded");

      assert.equal(recovered.attempt.state, "Approved");
      assert.equal(restarted.square.submitCalls, 0);
      assert.equal(restarted.square.recoverCalls, 1);
      assert.equal(restarted.linkly.submitCalls, 0);
      assert.equal(restarted.linkly.recoverCalls, 0);
    });
  }
});

test("fault matrix: Approved local-completion crash keeps its OrderGuid blocked and duplicate action replay is side-effect free", async () => {
  await withSqlite(async (connection) => {
    const first = await createHarness(connection, true);
    first.square.submitImpl = async () => approved({ paymentId: "payment-approved" });
    const approvedAttempt = await first.service.startAttempt(input());

    const restarted = await createHarness(connection, true);
    const replay = await restarted.service.startAttempt(input());
    assert.equal(replay.attempt.attemptId, approvedAttempt.attempt.attemptId);
    assert.equal(restarted.square.submitCalls, 0);
    assert.equal(restarted.square.recoverCalls, 0);
    await assert.rejects(
      () => restarted.service.startAttempt({ ...input(), actionId: "new-action-after-approved" }),
      PaymentAttemptBlockedError,
    );
  });
});

test("真实 SQLite：Alice 批准后崩溃，Bob 冷恢复完成订单仍写入 Alice 的员工审计", async () => {
  await withSqlite(async (connection, restartConnection) => {
    const first = await createHarness(connection, true);
    first.square.submitImpl = async () =>
      approved({ paymentId: "payment-alice-approved" });
    const approvedAttempt = await first.service.startAttempt(input());
    assert.equal(approvedAttempt.attempt.state, "Approved");

    // 模拟进程重启与员工切换；当前会话是 Bob，但 action binding 已由 Alice 冻结。
    const reopened = await restartConnection();
    const restarted = await createHarness(reopened, true);
    const bob: AuditActorSnapshot = {
      cashierId: "cashier-bob",
      cashierName: "Bob",
      userGuid: "user-bob",
    };
    const completion = new ApprovedPaymentOrderCompletionService({
      planner: {
        async plan(execution, actor) {
          return {
            tenderGuid: "tender-alice-recovered",
            completionAuditEvents: [
              {
                eventId: "audit-alice-recovered",
                eventType: "PAYMENT_APPROVED_COMPLETE",
                occurredAtIso: T0,
                orderGuid: execution.attempt.orderGuid,
                correlationId: execution.attempt.attemptId,
                payload: {
                  attemptId: execution.attempt.attemptId,
                  ...auditActorPayload(actor),
                },
              },
            ],
            outbox: {
              messageId: "outbox-alice-recovered",
              aggregateId: execution.attempt.orderGuid,
              kind: "order-sync",
              payloadJson: JSON.stringify({
                orderGuid: execution.attempt.orderGuid,
              }),
              nextAttemptAtIso: T0,
            },
            fulfilment: { print: null, drawer: null },
          };
        },
      },
      committer: new SqliteApprovedPaymentOrderCommitter(
        reopened,
        testEncryptor,
        () => T0,
      ),
    });
    const coordinator = new MixedPaymentCoordinator({
      actor: bob,
      orderTruth: new SqliteMixedPaymentOrderTruthStore(reopened),
      paymentAttempts: restarted.service,
      approvedCompletion: completion,
    });

    const recovered = await coordinator.recoverOnlineAttempt({
      orderGuid: approvedAttempt.attempt.orderGuid,
      attemptId: approvedAttempt.attempt.attemptId,
    });
    const audit = await reopened.getFirst<{ payload_json: unknown }>(
      `SELECT payload_json
       FROM audit_events
       WHERE event_id = 'audit-alice-recovered'`,
    );

    assert.equal(recovered.status, "completed");
    assert.deepEqual(JSON.parse(String(audit?.payload_json)), {
      attemptId: approvedAttempt.attempt.attemptId,
      requestingCashierId: "cashier-alice",
      requestingCashierName: "Alice",
      requestingUserGuid: "user-alice",
    });
    assert.equal(String(audit?.payload_json).includes("cashier-bob"), false);
    assert.equal(restarted.square.submitCalls, 0);
    assert.equal(restarted.square.recoverCalls, 0);
  });
});

async function createHarness(connection: NodeSqliteConnection, online: boolean) {
  await insertDraftOrder(connection, "order-payment-matrix");
  const repositories = createSqliteRepositories(connection, {
    nowIso: () => T0,
    createLeaseId: () => "lease-payment-matrix",
    encryptor: testEncryptor,
  });
  const square = new FakeProvider("square");
  const linkly = new FakeProvider("linkly-cloud");
  const manual = new ManualCardPaymentAdapter();
  let id = 0;
  const service = new PaymentAttemptService({
    ledger: repositories.payments,
    actionBindings: new SqlitePaymentActionBindingStore(connection),
    drafts: {
      async assertPersisted(orderGuid) {
        assert.ok(await repositories.orders.getByGuid(orderGuid), "payment must retain its local order draft");
      },
    },
    providers: {
      get(provider) {
        if (provider === "square") return square;
        if (provider === "linkly-cloud") return linkly;
        return manual;
      },
    },
    connectivity: { isOnline: async () => online },
    createAttemptId: () => `attempt-${++id === 1 ? "seeded" : id}`,
    createIdempotencyKey: () => `idempotency-${id}`,
    nowIso: () => T0,
  });
  return { service, repositories, square, linkly, manual };
}

function input() {
  return {
    actionId: "payment-action-1",
    orderGuid: "order-payment-matrix",
    provider: "square" as const,
    operation: "purchase" as const,
    amount: { currency: "AUD" as const, cents: 500 },
    actor: {
      cashierId: "cashier-alice",
      cashierName: "Alice",
      userGuid: "user-alice",
    },
  };
}

function attempt(overrides: Partial<PaymentAttempt>): PaymentAttempt {
  return {
    attemptId: "attempt-seeded",
    idempotencyKey: "idempotency-seeded",
    orderGuid: "order-payment-matrix",
    provider: "square",
    operation: "purchase",
    amount: { currency: "AUD", cents: 500 },
    state: "Created",
    references: references(),
    createdAtIso: T0,
    updatedAtIso: T0,
    lastErrorCode: null,
    providerEnvironment: "Sandbox",
    ...overrides,
  };
}

function approved(overrides: Partial<PaymentProviderReferences> = {}): PaymentProviderResult {
  return { state: "Approved", references: { ...references(), ...overrides }, receiptText: null, responseCode: "APPROVED" };
}

class FakeProvider {
  public submitCalls = 0;
  public recoverCalls = 0;
  public readonly providerEnvironment: string;
  public submitImpl: (attempt: PaymentAttempt) => Promise<PaymentProviderResult> = async () => approved();
  public recoverImpl: (attempt: PaymentAttempt) => Promise<PaymentProviderResult> = async () => approved();
  public constructor(public readonly provider: PaymentProvider) {
    this.providerEnvironment = provider === "square" ? "Sandbox" : "Production";
  }
  public async submit(attempt: PaymentAttempt): Promise<PaymentProviderResult> { this.submitCalls += 1; return withApprovedCardEvidence(await this.submitImpl(attempt), attempt); }
  public async recover(attempt: PaymentAttempt): Promise<PaymentProviderResult> { this.recoverCalls += 1; return withApprovedCardEvidence(await this.recoverImpl(attempt), attempt); }
  public async cancel(): Promise<PaymentProviderResult> { return { state: "Cancelled", references: references(), receiptText: null, responseCode: null }; }
  public async refund(attempt: PaymentAttempt): Promise<PaymentProviderResult> { return withApprovedCardEvidence(approved(), attempt); }
}

const testEncryptor = {
  async encrypt(value: string) {
    return new TextEncoder().encode(value);
  },
  async decrypt(value: Uint8Array) {
    return new TextDecoder().decode(value);
  },
};

function withApprovedCardEvidence(
  result: PaymentProviderResult,
  attempt: PaymentAttempt,
): PaymentProviderResult {
  if (result.state !== "Approved" || attempt.provider === "voucher" || attempt.provider === "manual-card") {
    return result;
  }
  return {
    ...result,
    protectedSyncEvidence: {
      version: 1,
      provider: attempt.provider,
      operation: attempt.operation,
      processor: attempt.provider === "square" ? "Square" : "ANZ",
      txnRef: null,
      authCode: null,
      cardType: null,
      cardBin: null,
      maskedCardNumber: null,
      merchantId: null,
      responseCode: result.responseCode,
      responseText: null,
      stan: null,
      bankDateTimeIso: null,
      amountCents: Math.abs(attempt.amount.cents),
      refundReference: null,
    },
  };
}

class NodeSqliteConnection implements SqliteConnectionPort {
  private readonly database: DatabaseSync;
  public constructor(path: string) { this.database = new DatabaseSync(path); this.database.exec("PRAGMA foreign_keys = ON"); }
  public async exec(sql: string): Promise<void> { this.database.exec(sql); }
  public async run(sql: string, parameters: readonly SqlValue[] = []): Promise<SqlRunResult> {
    const result = this.database.prepare(sql).run(...parameters as readonly SQLInputValue[]);
    return { changes: Number(result.changes), lastInsertRowId: Number(result.lastInsertRowid) };
  }
  public async getFirst<T extends object>(sql: string, parameters: readonly SqlValue[] = []): Promise<T | null> {
    const row = this.database.prepare(sql).get(...parameters as readonly SQLInputValue[]);
    return row === undefined ? null : row as T;
  }
  public async getAll<T extends object>(sql: string, parameters: readonly SqlValue[] = []): Promise<readonly T[]> {
    return this.database.prepare(sql).all(...parameters as readonly SQLInputValue[]) as unknown as readonly T[];
  }
  public async withExclusiveTransaction<T>(operation: (transaction: SqliteConnectionPort) => Promise<T>): Promise<T> {
    this.database.exec("BEGIN IMMEDIATE");
    try { const result = await operation(this); this.database.exec("COMMIT"); return result; }
    catch (error) { this.database.exec("ROLLBACK"); throw error; }
  }
  public async close(): Promise<void> { this.database.close(); }
}

async function withSqlite(
  operation: (
    connection: NodeSqliteConnection,
    restartConnection: () => Promise<NodeSqliteConnection>,
  ) => Promise<void>,
): Promise<void> {
  const folder = mkdtempSync(join(tmpdir(), "hb-pos-ipad-payment-matrix-"));
  const databasePath = join(folder, "pos.db");
  let connection = new NodeSqliteConnection(databasePath);
  try {
    await connection.exec(POS_DATABASE_MIGRATIONS.map((migration) => migration.sql).join("\n"));
    await operation(connection, async () => {
      await connection.close();
      connection = new NodeSqliteConnection(databasePath);
      return connection;
    });
  } finally {
    await connection.close();
    rmSync(folder, { recursive: true, force: true });
  }
}

async function insertDraftOrder(connection: NodeSqliteConnection, orderGuid: string): Promise<void> {
  const current = await scalar(connection, "SELECT COUNT(*) AS count FROM local_orders WHERE order_guid = ?", [orderGuid]);
  if (current > 0) return;
  await connection.run(
    "INSERT INTO local_orders (order_guid, local_sequence, store_code, device_code, cashier_id, cashier_name, sold_at_iso, state, total_cents, discount_cents, actual_amount_cents, original_order_guid, created_at_iso, updated_at_iso) VALUES (?, 1, 'S1', 'IPAD1', 'cashier-1', 'Cashier', ?, 'Draft', 500, 0, 500, NULL, ?, ?)",
    [orderGuid, T0, T0, T0],
  );
}

async function scalar(connection: NodeSqliteConnection, sql: string, parameters: readonly SqlValue[] = []): Promise<number> {
  const row = await connection.getFirst<{ count: unknown }>(sql, parameters);
  return Number(row?.count ?? 0);
}
