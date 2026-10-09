import assert from "node:assert/strict";
import { mkdtempSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { DatabaseSync, type SQLInputValue } from "node:sqlite";
import test from "node:test";

import type {
  CardSyncEvidenceV1,
  PaymentAttempt,
} from "@hb/pos-domain/core/contracts/payment";
import type { LocalOrderState } from "@hb/pos-domain/core/contracts/state-machines";

import { POS_DATABASE_MIGRATIONS } from "./migrations";
import { SqlitePaymentProtectedMaterialReader } from "@hb/pos-db/core/db/sqlite-payment-protected-material";
import { createSqliteRepositories } from "./sqlite-repositories";
import type { SqliteConnectionPort, SqlRunResult, SqlValue } from "@hb/pos-db/core/db/types";

class SystemSqliteConnection implements SqliteConnectionPort {
  private readonly database: DatabaseSync;
  private readonly queue = new AsyncSerialQueue();

  public constructor(databasePath: string) {
    this.database = new DatabaseSync(databasePath);
    this.database.exec("PRAGMA foreign_keys = ON");
  }

  public exec(sql: string): Promise<void> {
    return this.queue.enqueue(async () => {
      this.database.exec(sql);
    });
  }

  public run(sql: string, parameters: readonly SqlValue[] = []): Promise<SqlRunResult> {
    return this.queue.enqueue(async () =>
      runStatement(this.database, sql, parameters));
  }

  public getFirst<T extends object>(sql: string, parameters: readonly SqlValue[] = []): Promise<T | null> {
    return this.queue.enqueue(async () =>
      getFirst<T>(this.database, sql, parameters));
  }

  public getAll<T extends object>(sql: string, parameters: readonly SqlValue[] = []): Promise<readonly T[]> {
    return this.queue.enqueue(async () =>
      getAll<T>(this.database, sql, parameters));
  }

  public withExclusiveTransaction<T>(
    operation: (transaction: SqliteConnectionPort) => Promise<T>,
  ): Promise<T> {
    return this.queue.enqueue(async () => {
      this.database.exec("BEGIN IMMEDIATE");
      const transaction = new NodeSqliteTransactionConnection(this.database);
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
    return this.queue.enqueue(async () => {
      this.database.close();
    });
  }
}

class NodeSqliteTransactionConnection implements SqliteConnectionPort {
  public constructor(private readonly database: DatabaseSync) {}

  public async exec(sql: string): Promise<void> {
    this.database.exec(sql);
  }

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
    return Promise.reject(new Error("Transaction connection cannot close the database."));
  }
}

class AsyncSerialQueue {
  private tail: Promise<void> = Promise.resolve();

  public enqueue<T>(operation: () => Promise<T>): Promise<T> {
    const result = this.tail.then(operation, operation);
    this.tail = result.then(
      () => undefined,
      () => undefined,
    );
    return result;
  }
}

function payment(overrides: Partial<PaymentAttempt> = {}): PaymentAttempt {
  return {
    attemptId: "attempt-1",
    idempotencyKey: "idempotency-1",
    orderGuid: "order-1",
    provider: "square",
    operation: "purchase",
    amount: { currency: "AUD", cents: 500 },
    state: "Created",
    references: { checkoutId: null, paymentId: null, sessionId: null, txnRef: null, rfn: null, voucherReservationToken: null },
    createdAtIso: "2026-07-28T00:00:00.000Z",
    updatedAtIso: "2026-07-28T00:00:00.000Z",
    lastErrorCode: null,
    ...overrides,
  };
}

test("真实 SQLite：支付尝试保留身份、Approved 仅匹配正确 tender 解锁，并且重开连接后仍然成立", async () => {
  const folder = mkdtempSync(join(tmpdir(), "hb-pos-payment-db-"));
  const databasePath = join(folder, "payment.db");
  try {
    const connection = new SystemSqliteConnection(databasePath);
    await connection.exec(POS_DATABASE_MIGRATIONS.map((migration) => migration.sql).join("\n"));
    await insertDraftOrder(connection, "order-1", 1, 500);
    const encryptor = {
      async encrypt(plaintext: string) { return new TextEncoder().encode(plaintext); },
      async decrypt(ciphertext: Uint8Array) { return new TextDecoder().decode(ciphertext); },
    };
    const repositories = createSqliteRepositories(connection, {
      nowIso: () => "2026-07-28T00:00:00.000Z",
      createLeaseId: () => "lease-1",
      encryptor,
    });

    const created = payment();
    const [firstResult, secondResult] = await Promise.all([
      repositories.payments.insertIfUnblocked(created),
      repositories.payments.insertIfUnblocked(payment({ attemptId: "attempt-2", idempotencyKey: "idempotency-2" })),
    ]);
    assert.equal(firstResult, null);
    assert.equal(secondResult?.attemptId, "attempt-1");

    assert.equal(
      await repositories.payments.compareAndUpdate(
        created,
        payment({ orderGuid: "another-order", state: "Submitted", updatedAtIso: "2026-07-28T00:00:30.000Z" }),
      ),
      false,
    );
    assert.equal((await repositories.payments.get("attempt-1"))?.orderGuid, "order-1");

    const approved = payment({ state: "Approved", updatedAtIso: "2026-07-28T00:01:00.000Z", receiptText: "CARD RECEIPT", responseCode: "APPROVED" });
    assert.equal(await repositories.payments.compareAndUpdate(created, approved), true);
    assert.equal((await repositories.payments.findBlocking("order-1"))?.state, "Approved");

    // 重开连接模拟批准回执已落库、应用在写 tender 前被杀；金额不同仍不能解除阻塞。
    const reopened = new SystemSqliteConnection(databasePath);
    await connection.run(
      "INSERT INTO order_tenders (tender_guid, order_guid, method, amount_cents, payment_attempt_id, created_at_iso) VALUES (?, ?, ?, ?, ?, ?)",
      ["wrong-amount", "order-1", "card", 499, "attempt-1", "2026-07-28T00:01:00.000Z"],
    );
    const reopenedRepositories = createSqliteRepositories(reopened, {
      nowIso: () => "2026-07-28T00:00:00.000Z",
      createLeaseId: () => "lease-1",
      encryptor,
    });
    assert.equal((await reopenedRepositories.payments.findBlocking("order-1"))?.attemptId, "attempt-1");
    assert.deepEqual(await reopenedRepositories.payments.get("attempt-1"), approved);

    await insertDraftOrder(reopened, "order-method", 2, 500);
    const methodAttempt = payment({ attemptId: "attempt-method", idempotencyKey: "idempotency-method", orderGuid: "order-method", provider: "voucher" });
    assert.equal(await reopenedRepositories.payments.insertIfUnblocked(methodAttempt), null);
    assert.equal(await reopenedRepositories.payments.compareAndUpdate(methodAttempt, { ...methodAttempt, state: "Approved", updatedAtIso: "2026-07-28T00:01:00.000Z" }), true);
    await reopened.run("INSERT INTO order_tenders (tender_guid, order_guid, method, amount_cents, payment_attempt_id, created_at_iso) VALUES (?, ?, ?, ?, ?, ?)", ["wrong-method", "order-method", "card", 500, "attempt-method", "2026-07-28T00:01:00.000Z"]);
    assert.equal((await reopenedRepositories.payments.findBlocking("order-method"))?.attemptId, "attempt-method");

    await insertDraftOrder(reopened, "order-mismatch", 3, 500);
    await insertDraftOrder(reopened, "another-order", 4, 500);
    const mismatchAttempt = payment({ attemptId: "attempt-mismatch", idempotencyKey: "idempotency-mismatch", orderGuid: "order-mismatch" });
    assert.equal(await reopenedRepositories.payments.insertIfUnblocked(mismatchAttempt), null);
    assert.equal(await reopenedRepositories.payments.compareAndUpdate(mismatchAttempt, { ...mismatchAttempt, state: "Approved", updatedAtIso: "2026-07-28T00:01:00.000Z" }), true);
    await reopened.run("INSERT INTO order_tenders (tender_guid, order_guid, method, amount_cents, payment_attempt_id, created_at_iso) VALUES (?, ?, ?, ?, ?, ?)", ["wrong-order", "another-order", "card", 500, "attempt-mismatch", "2026-07-28T00:01:00.000Z"]);
    assert.equal((await reopenedRepositories.payments.findBlocking("order-mismatch"))?.attemptId, "attempt-mismatch");

    await insertDraftOrder(reopened, "order-correct", 5, 500);
    const correctAttempt = payment({ attemptId: "attempt-correct", idempotencyKey: "idempotency-correct", orderGuid: "order-correct", provider: "linkly-cloud" });
    assert.equal(await reopenedRepositories.payments.insertIfUnblocked(correctAttempt), null);
    assert.equal(await reopenedRepositories.payments.compareAndUpdate(correctAttempt, { ...correctAttempt, state: "Approved", updatedAtIso: "2026-07-28T00:01:00.000Z" }), true);
    await reopened.run("INSERT INTO order_tenders (tender_guid, order_guid, method, amount_cents, payment_attempt_id, created_at_iso) VALUES (?, ?, ?, ?, ?, ?)", ["correct", "order-correct", "card", 500, "attempt-correct", "2026-07-28T00:01:00.000Z"]);
    assert.equal(await reopenedRepositories.payments.findBlocking("order-correct"), null);
    await assert.rejects(
      () => reopened.run("INSERT INTO order_tenders (tender_guid, order_guid, method, amount_cents, payment_attempt_id, created_at_iso) VALUES (?, ?, ?, ?, ?, ?)", ["duplicate", "order-correct", "card", 500, "attempt-correct", "2026-07-28T00:01:00.000Z"]),
      /UNIQUE constraint failed: order_tenders\.payment_attempt_id/,
    );

    // M44：真实 SQLite 的 ACK marker 只能在唯一、同订单同金额的 card tender
    // 耐久后落库；无环境历史记录不得猜当前环境确认。
    await insertDraftOrder(reopened, "order-linkly-ack", 6, 500);
    const ackAttempt = payment({ attemptId: "attempt-linkly-ack", idempotencyKey: "idempotency-linkly-ack", orderGuid: "order-linkly-ack", provider: "linkly-cloud", providerEnvironment: "production" });
    assert.equal(await reopenedRepositories.payments.insertIfUnblocked(ackAttempt), null);
    const ackApproved = { ...ackAttempt, state: "Approved" as const, updatedAtIso: "2026-07-28T00:03:00.000Z" };
    assert.equal(await reopenedRepositories.payments.compareAndUpdate(ackAttempt, ackApproved), true);
    const marker = reopenedRepositories.payments.markProviderAcknowledged;
    assert.ok(marker);
    assert.equal(await marker.call(reopenedRepositories.payments, ackApproved, "2026-07-28T00:03:01.000Z"), false);
    await reopened.run("INSERT INTO order_tenders (tender_guid, order_guid, method, amount_cents, payment_attempt_id, created_at_iso) VALUES (?, ?, ?, ?, ?, ?)", ["ack-tender", "order-linkly-ack", "card", 500, "attempt-linkly-ack", "2026-07-28T00:03:00.000Z"]);
    assert.equal(await marker.call(reopenedRepositories.payments, ackApproved, "2026-07-28T00:03:01.000Z"), true);
    assert.equal((await reopenedRepositories.payments.get("attempt-linkly-ack"))?.providerAcknowledgedAtIso, "2026-07-28T00:03:01.000Z");

    await insertDraftOrder(reopened, "order-linkly-legacy", 7, 500);
    const legacy = payment({ attemptId: "attempt-linkly-legacy", idempotencyKey: "idempotency-linkly-legacy", orderGuid: "order-linkly-legacy", provider: "linkly-cloud", state: "Declined", updatedAtIso: "2026-07-28T00:05:00.000Z" });
    await reopened.run("INSERT INTO payment_attempts (attempt_id,idempotency_key,order_guid,provider,operation,amount_cents,state,checkout_id,payment_id,session_id,txn_ref,rfn,created_at_iso,updated_at_iso,last_error_code) VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)", [legacy.attemptId, legacy.idempotencyKey, legacy.orderGuid, legacy.provider, legacy.operation, legacy.amount.cents, legacy.state, null, null, null, null, null, legacy.createdAtIso, legacy.updatedAtIso, null]);
    assert.equal(await marker.call(reopenedRepositories.payments, legacy, "2026-07-28T00:05:01.000Z"), false);

    // 退货动作仍 processing 时，首笔已批准退款也必须能凭 allocation、不可变
    // binding 与受保护卡证据逐笔 ACK；不能等待整张 return action 才释放终端。
    await insertDraftOrder(reopened, "return-order-linkly", 9, 100);
    await reopened.run("INSERT INTO return_actions (action_id,request_fingerprint,return_order_guid,action_recovery_token,source_kind,total_refund_cents,online,store_code,device_code,cashier_id,cashier_name,session_epoch,supervisor_grant_id,plan_json,state,created_at_iso,completed_at_iso,updated_at_iso) VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)", ["return-action-linkly", "fingerprint", "return-order-linkly", "return-token", "receipt", 100, 1, "S1", "IPAD1", "cashier-1", "Cashier", "epoch", null, "{}", "processing", "2026-07-28T00:06:00.000Z", null, "2026-07-28T00:06:00.000Z"]);
    await reopened.run("INSERT INTO payment_action_bindings (order_guid,action_id,request_signature,attempt_id,idempotency_key,created_at_iso,audit_actor_json) VALUES (?,?,?,?,?,?,?)", ["return-order-linkly", "external-return-linkly", "signature", "attempt-linkly-refund", "idempotency-linkly-refund", "2026-07-28T00:06:00.000Z", '{"requestingCashierId":"cashier-1","requestingCashierName":"Cashier","requestingUserGuid":null}']);
    await reopened.run("INSERT INTO payment_attempts (attempt_id,idempotency_key,order_guid,provider,operation,amount_cents,state,checkout_id,payment_id,session_id,txn_ref,rfn,provider_payload_ciphertext,created_at_iso,updated_at_iso,last_error_code,provider_environment) VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)", ["attempt-linkly-refund", "idempotency-linkly-refund", "return-order-linkly", "linkly-cloud", "refund", -100, "Approved", null, null, "linkly-session", "sync-txn-1", null, new TextEncoder().encode('{"version":1,"voucherReservationToken":null,"cardSyncEvidence":null}'), "2026-07-28T00:06:00.000Z", "2026-07-28T00:06:00.000Z", null, "production"]);
    await reopened.run("INSERT INTO return_action_allocations (action_id,allocation_id,allocation_index,execution_kind,method,signed_amount_cents,capacity_id,original_order_guid,offline_evidence_id,offline_evidence_remaining_cents,external_attempt_id,external_attempt_kind,external_action_id,durable_attempt_id,status,protected_recovery_ciphertext,capacity_reservation_state,created_at_iso,updated_at_iso) VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)", ["return-action-linkly", "return-allocation-linkly", 0, "online-refund", "card", -100, null, "original-order", null, null, "external-return-linkly", "payment-provider", "external-return-linkly", "attempt-linkly-refund", "completed", null, "None", "2026-07-28T00:06:00.000Z", "2026-07-28T00:06:00.000Z"]);
    const refundAttempt = await reopenedRepositories.payments.get("attempt-linkly-refund");
    assert.ok(refundAttempt);
    // 空 envelope 不是卡支付证据，不能仅凭任意密文字节释放 Linkly server guard。
    assert.equal(await reopenedRepositories.payments.canProviderAcknowledged?.(refundAttempt), false);
    assert.equal(await marker.call(reopenedRepositories.payments, refundAttempt, "2026-07-28T00:06:01.000Z"), false);
    const refundEvidence = cardSyncEvidence({
      provider: "linkly-cloud",
      operation: "refund",
      processor: "ANZ",
      amountCents: 100,
    });
    // 即使 provider、退款类型和金额相同，另一笔交易的加密证据也不能放行。
    await reopened.run("UPDATE payment_attempts SET provider_payload_ciphertext = ? WHERE attempt_id = ?", [new TextEncoder().encode(JSON.stringify({ version: 1, voucherReservationToken: null, cardSyncEvidence: { ...refundEvidence, txnRef: "another-refund" } })), "attempt-linkly-refund"]);
    assert.equal(await reopenedRepositories.payments.canProviderAcknowledged?.(refundAttempt), false);
    assert.equal(await marker.call(reopenedRepositories.payments, refundAttempt, "2026-07-28T00:06:01.000Z"), false);
    assert.equal((await reopenedRepositories.payments.get("attempt-linkly-refund"))?.providerAcknowledgedAtIso ?? null, null);
    await reopened.run("UPDATE payment_attempts SET provider_payload_ciphertext = ? WHERE attempt_id = ?", [new TextEncoder().encode(JSON.stringify({ version: 1, voucherReservationToken: null, cardSyncEvidence: refundEvidence })), "attempt-linkly-refund"]);
    const provenRefundAttempt = await reopenedRepositories.payments.get("attempt-linkly-refund");
    assert.ok(provenRefundAttempt);
    assert.equal(await reopenedRepositories.payments.canProviderAcknowledged?.(provenRefundAttempt), true);
    assert.equal(await marker.call(reopenedRepositories.payments, provenRefundAttempt, "2026-07-28T00:06:01.000Z"), true);

    for (const state of ["Created", "Submitted", "Pending", "Unknown"] as const) {
      const orderGuid = `legacy-${state}`;
      const attemptId = `legacy-${state}`;
      await insertDraftOrder(reopened, orderGuid, 20 + ["Created", "Submitted", "Pending", "Unknown"].indexOf(state), 100);
      await reopened.run("INSERT INTO payment_attempts (attempt_id,idempotency_key,order_guid,provider,operation,amount_cents,state,checkout_id,payment_id,session_id,txn_ref,rfn,created_at_iso,updated_at_iso,last_error_code) VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)", [attemptId, `key-${attemptId}`, orderGuid, "linkly-cloud", "purchase", 100, state, null, null, "legacy-session", "legacy-txn", null, "2026-07-28T00:07:00.000Z", "2026-07-28T00:07:00.000Z", null]);
      const current = await reopenedRepositories.payments.get(attemptId);
      assert.ok(current);
      const verify = reopenedRepositories.payments.verifyProviderEnvironment;
      assert.ok(verify);
      if (state === "Created") {
        assert.equal(await verify.call(reopenedRepositories.payments, current, "production"), false);
        assert.equal((await reopenedRepositories.payments.get(attemptId))?.providerEnvironment ?? null, null);
        continue;
      }
      // 所有错配都在数据库环境仍为 NULL 时验证，防止已有环境的短路掩盖 CAS 漏洞。
      const mismatches: readonly PaymentAttempt[] = [
        { ...current, references: { ...current.references, sessionId: "wrong-session" } },
        { ...current, references: { ...current.references, txnRef: "wrong-txn" } },
        { ...current, state: state === "Submitted" ? "Pending" : "Submitted" },
        { ...current, idempotencyKey: "wrong-key" },
        { ...current, orderGuid: "wrong-order" },
        { ...current, provider: "square" },
        { ...current, operation: "refund" },
        { ...current, amount: { ...current.amount, cents: 101 } },
      ];
      for (const mismatch of mismatches) {
        assert.equal(await verify.call(reopenedRepositories.payments, mismatch, "production"), false);
        assert.equal((await reopenedRepositories.payments.get(attemptId))?.providerEnvironment ?? null, null);
      }
      assert.equal(await verify.call(reopenedRepositories.payments, current, "production"), true);
      const frozen = await reopenedRepositories.payments.get(attemptId);
      assert.ok(frozen);
      assert.equal(frozen.providerEnvironment, "production");
      assert.equal(await verify.call(reopenedRepositories.payments, frozen, "sandbox"), false);
      assert.equal(await verify.call(reopenedRepositories.payments, current, "sandbox"), false);
      assert.equal((await reopenedRepositories.payments.get(attemptId))?.providerEnvironment, "production");
    }

    const stale = payment({ state: "Submitted", updatedAtIso: "2026-07-28T00:02:00.000Z" });
    assert.equal(await repositories.payments.compareAndUpdate(created, stale), false);
    assert.equal((await repositories.payments.get("attempt-1"))?.state, "Approved");
  } finally {
    rmSync(folder, { recursive: true, force: true });
  }
});

test("真实 SQLite：Created attempt 仅允许绑定 Draft/Completing 订单，缺单和所有完成态均失败关闭", async () => {
  const folder = mkdtempSync(join(tmpdir(), "hb-pos-payment-order-gate-"));
  const databasePath = join(folder, "payment-order-gate.db");
  const connection = new SystemSqliteConnection(databasePath);
  try {
    await connection.exec(POS_DATABASE_MIGRATIONS.map((migration) => migration.sql).join("\n"));
    const repositories = createSqliteRepositories(connection, {
      nowIso: () => "2026-07-28T00:00:00.000Z",
      createLeaseId: () => "lease-gate",
      encryptor: {
        async encrypt(plaintext) { return new TextEncoder().encode(plaintext); },
        async decrypt(ciphertext) { return new TextDecoder().decode(ciphertext); },
      },
    });
    const cases = [
      ["Draft", true],
      ["Completing", true],
      ["CompletedLocal", false],
      ["PendingSync", false],
      ["Syncing", false],
      ["Synced", false],
      ["Blocked403", false],
      ["Rejected", false],
    ] as const satisfies readonly (readonly [LocalOrderState, boolean])[];

    for (const [index, [state, allowed]] of cases.entries()) {
      const orderGuid = `order-gate-${state}`;
      const attemptId = `attempt-gate-${state}`;
      await insertDraftOrder(connection, orderGuid, index + 100, 500, state);
      const candidate = payment({
        attemptId,
        idempotencyKey: `idempotency-gate-${state}`,
        orderGuid,
      });

      if (allowed) {
        assert.equal(await repositories.payments.insertIfUnblocked(candidate), null);
      } else {
        await assert.rejects(
          () => repositories.payments.insertIfUnblocked(candidate),
          /Draft or Completing local order/,
        );
      }
      assert.equal(
        await countAttempts(connection, attemptId),
        allowed ? 1 : 0,
        `${state} must ${allowed ? "" : "not "}persist a Created attempt`,
      );
    }

    const missing = payment({
      attemptId: "attempt-gate-missing",
      idempotencyKey: "idempotency-gate-missing",
      orderGuid: "order-gate-missing",
    });
    await assert.rejects(
      () => repositories.payments.insertIfUnblocked(missing),
      /Draft or Completing local order/,
    );
    assert.equal(await countAttempts(connection, missing.attemptId), 0);
  } finally {
    await connection.close();
    rmSync(folder, { recursive: true, force: true });
  }
});

test("真实 SQLite 竞态：订单完成先提交时，Created attempt 持久门拒绝且外部支付调用保持为零", async () => {
  const folder = mkdtempSync(join(tmpdir(), "hb-pos-payment-race-"));
  const databasePath = join(folder, "payment-race.db");
  const connection = new SystemSqliteConnection(databasePath);
  let releaseEncryption!: () => void;
  let reportEncryptionStarted!: () => void;
  const encryptionStarted = new Promise<void>((resolve) => {
    reportEncryptionStarted = resolve;
  });
  const encryptionMayFinish = new Promise<void>((resolve) => {
    releaseEncryption = resolve;
  });

  try {
    await connection.exec(POS_DATABASE_MIGRATIONS.map((migration) => migration.sql).join("\n"));
    await insertDraftOrder(connection, "order-race", 200, 500, "Draft");
    const repositories = createSqliteRepositories(connection, {
      nowIso: () => "2026-07-28T00:00:00.000Z",
      createLeaseId: () => "lease-race",
      encryptor: {
        async encrypt(plaintext) {
          reportEncryptionStarted();
          await encryptionMayFinish;
          return new TextEncoder().encode(plaintext);
        },
        async decrypt(ciphertext) { return new TextDecoder().decode(ciphertext); },
      },
    });
    let externalPaymentCalls = 0;
    const startPayment = async (): Promise<void> => {
      await repositories.payments.insertIfUnblocked(payment({
        attemptId: "attempt-race",
        idempotencyKey: "idempotency-race",
        orderGuid: "order-race",
        provider: "voucher",
        references: {
          checkoutId: null,
          paymentId: null,
          sessionId: null,
          txnRef: null,
          rfn: null,
          voucherReservationToken: "reservation-race",
        },
      }));
      externalPaymentCalls += 1;
    };

    const paymentStart = startPayment();
    await encryptionStarted;
    await connection.withExclusiveTransaction(async (transaction) => {
      const completing = await transaction.run(
        "UPDATE local_orders SET state = 'Completing' WHERE order_guid = ? AND state = 'Draft'",
        ["order-race"],
      );
      const completed = await transaction.run(
        "UPDATE local_orders SET state = 'PendingSync' WHERE order_guid = ? AND state = 'Completing'",
        ["order-race"],
      );
      assert.equal(completing.changes, 1);
      assert.equal(completed.changes, 1);
    });
    releaseEncryption();

    await assert.rejects(paymentStart, /Draft or Completing local order/);
    assert.equal(await countAttempts(connection, "attempt-race"), 0);
    assert.equal(externalPaymentCalls, 0);
  } finally {
    releaseEncryption();
    await connection.close();
    rmSync(folder, { recursive: true, force: true });
  }
});

test("真实 SQLite：订单读取只从同订单 Approved attempt 恢复可证明的支付引用", async () => {
  const folder = mkdtempSync(join(tmpdir(), "hb-pos-order-tender-db-"));
  const databasePath = join(folder, "order-tender.db");
  try {
    const connection = new SystemSqliteConnection(databasePath);
    await connection.exec(POS_DATABASE_MIGRATIONS.map((migration) => migration.sql).join("\n"));
    const encryptor = {
      async encrypt(plaintext: string) { return new TextEncoder().encode(plaintext); },
      async decrypt(ciphertext: Uint8Array) { return new TextDecoder().decode(ciphertext); },
    };
    const repositories = createSqliteRepositories(connection, {
      nowIso: () => "2026-07-28T00:00:00.000Z",
      createLeaseId: () => "lease-tender",
      encryptor,
    });

    await insertDraftOrder(connection, "order-square-sale", 10, 500);
    await insertPaymentAttemptRow(connection, {
      attemptId: "attempt-square-sale",
      orderGuid: "order-square-sale",
      provider: "square",
      operation: "purchase",
      amountCents: 500,
      state: "Approved",
      paymentId: "payment-stable-1",
      receiptCiphertext: new TextEncoder().encode("PAN 4111111111111111"),
    });
    await insertTender(connection, "tender-square-sale", "order-square-sale", "card", 500, "attempt-square-sale");
    assert.equal(
      (await repositories.orders.getByGuid("order-square-sale"))?.tenders[0]?.reference,
      "SQ:payment-stable-1",
    );

    await insertDraftOrder(connection, "order-square-refund", 11, -500);
    // DB/committer 的退款账本使用负分币；PaymentAttemptService/Provider 尚待上游统一
    // operation-aware 符号与 provider 绝对值，本用例只证明落库后的引用恢复，不代表端到端退款已开放。
    await insertPaymentAttemptRow(connection, {
      attemptId: "attempt-square-refund",
      orderGuid: "order-square-refund",
      provider: "square",
      operation: "refund",
      amountCents: -500,
      state: "Approved",
      paymentId: "payment-original",
      responseCode: "refund-1",
    });
    await insertTender(connection, "tender-square-refund", "order-square-refund", "card", -500, "attempt-square-refund");
    assert.equal(
      (await repositories.orders.getByGuid("order-square-refund"))?.tenders[0]?.reference,
      "CARD_REFUND|refund=SQRF%3Arefund-1|original=SQ%3Apayment-original",
    );

    await insertDraftOrder(connection, "order-missing-reference", 12, 500);
    await insertPaymentAttemptRow(connection, {
      attemptId: "attempt-missing-reference",
      orderGuid: "order-missing-reference",
      provider: "square",
      operation: "purchase",
      amountCents: 500,
      state: "Approved",
      paymentId: null,
      receiptCiphertext: new TextEncoder().encode("receipt must never become a reference"),
    });
    await insertTender(connection, "tender-missing-reference", "order-missing-reference", "card", 500, "attempt-missing-reference");
    assert.equal(
      (await repositories.orders.getByGuid("order-missing-reference"))?.tenders[0]?.reference,
      null,
    );

    await insertDraftOrder(connection, "order-pending-attempt", 13, 500);
    await insertPaymentAttemptRow(connection, {
      attemptId: "attempt-pending",
      orderGuid: "order-pending-attempt",
      provider: "square",
      operation: "purchase",
      amountCents: 500,
      state: "Pending",
      paymentId: "payment-must-not-hydrate",
    });
    await insertTender(connection, "tender-pending", "order-pending-attempt", "card", 500, "attempt-pending");
    assert.equal(
      (await repositories.orders.getByGuid("order-pending-attempt"))?.tenders[0]?.reference,
      null,
    );

    await insertDraftOrder(connection, "order-attempt-owner", 14, 500);
    await insertDraftOrder(connection, "order-cross-binding", 15, 500);
    await insertPaymentAttemptRow(connection, {
      attemptId: "attempt-cross-order",
      orderGuid: "order-attempt-owner",
      provider: "square",
      operation: "purchase",
      amountCents: 500,
      state: "Approved",
      paymentId: "payment-cross-order",
    });
    await insertTender(connection, "tender-cross-order", "order-cross-binding", "card", 500, "attempt-cross-order");
    assert.equal(
      (await repositories.orders.getByGuid("order-cross-binding"))?.tenders[0]?.reference,
      null,
    );
    await assert.rejects(
      () => insertTender(connection, "tender-duplicate-binding", "order-attempt-owner", "card", 500, "attempt-cross-order"),
      /UNIQUE constraint failed: order_tenders\.payment_attempt_id/,
    );

    await insertDraftOrder(connection, "order-linkly-gated", 16, 500);
    await insertPaymentAttemptRow(connection, {
      attemptId: "attempt-linkly-gated",
      orderGuid: "order-linkly-gated",
      provider: "linkly-cloud",
      operation: "purchase",
      amountCents: 500,
      state: "Approved",
      sessionId: "session-1",
      txnRef: "txn-1",
      rfn: "rfn-1",
    });
    await insertTender(connection, "tender-linkly-gated", "order-linkly-gated", "card", 500, "attempt-linkly-gated");
    assert.equal(
      (await repositories.orders.getByGuid("order-linkly-gated"))?.tenders[0]?.reference,
      null,
    );

    await insertDraftOrder(connection, "order-voucher-gated", 17, 500);
    await insertPaymentAttemptRow(connection, {
      attemptId: "attempt-voucher-gated",
      orderGuid: "order-voucher-gated",
      provider: "voucher",
      operation: "purchase",
      amountCents: 500,
      state: "Approved",
      providerPayloadCiphertext: new TextEncoder().encode(JSON.stringify({
        voucherReservationToken: "vpr_attempt_voucher_gated",
      })),
    });
    await insertTender(connection, "tender-voucher-gated", "order-voucher-gated", "voucher", 500, "attempt-voucher-gated");
    assert.deepEqual(
      (await repositories.orders.getByGuid("order-voucher-gated"))?.tenders[0],
      {
        tenderGuid: "tender-voucher-gated",
        method: "voucher",
        amount: { currency: "AUD", cents: 500 },
        reference: null,
        reservationToken: null,
      },
    );
  } finally {
    rmSync(folder, { recursive: true, force: true });
  }
});

test("真实 SQLite：支付 CAS 原子合并卡同步证据并保持公开 PaymentAttempt 脱敏", async () => {
  const folder = mkdtempSync(join(tmpdir(), "hb-pos-payment-protected-cas-"));
  const databasePath = join(folder, "payment-protected-cas.db");
  const connection = new SystemSqliteConnection(databasePath);
  const encryptor = {
    async encrypt(plaintext: string) {
      return new TextEncoder().encode(plaintext);
    },
    async decrypt(ciphertext: Uint8Array) {
      return new TextDecoder().decode(ciphertext);
    },
  };
  try {
    await connection.exec(POS_DATABASE_MIGRATIONS.map((migration) => migration.sql).join("\n"));
    await insertDraftOrder(connection, "order-protected", 300, 500);
    const repositories = createSqliteRepositories(connection, {
      nowIso: () => "2026-07-28T00:00:00.000Z",
      createLeaseId: () => "lease-protected",
      encryptor,
    });
    const reader = new SqlitePaymentProtectedMaterialReader(
      connection,
      encryptor,
    );
    const created = payment({
      attemptId: "attempt-protected",
      idempotencyKey: "idempotency-protected",
      orderGuid: "order-protected",
      references: {
        checkoutId: null,
        paymentId: null,
        sessionId: null,
        txnRef: null,
        rfn: null,
        voucherReservationToken: "reservation-protected",
      },
    });
    assert.equal(await repositories.payments.insertIfUnblocked(created), null);
    assert.deepEqual(
      await readProtectedPayload(connection, encryptor, created.attemptId),
      {
        version: 1,
        voucherReservationToken: "reservation-protected",
        cardSyncEvidence: null,
      },
    );

    const evidence = cardSyncEvidence({
      txnRef: "sync-txn-protected",
      authCode: "AUTH-PROTECTED",
      maskedCardNumber: "411111******1111",
    });
    const approved = payment({
      ...created,
      state: "Approved",
      updatedAtIso: "2026-07-28T00:01:00.000Z",
      references: {
        ...created.references,
        paymentId: "square-payment-protected",
      },
    });
    assert.equal(
      await repositories.payments.compareAndUpdate(
        created,
        approved,
        evidence,
      ),
      true,
    );
    assert.deepEqual(
      await readProtectedPayload(connection, encryptor, created.attemptId),
      {
        version: 1,
        voucherReservationToken: "reservation-protected",
        cardSyncEvidence: evidence,
      },
    );

    const publicAttempt = await repositories.payments.get(created.attemptId);
    assert.equal(publicAttempt?.references.voucherReservationToken, "reservation-protected");
    assert.equal(publicAttempt === null ? false : "cardSyncEvidence" in publicAttempt, false);
    assert.equal(JSON.stringify(publicAttempt).includes("AUTH-PROTECTED"), false);
    const publicBlockingAttempt = await repositories.payments.findBlocking(
      created.orderGuid,
    );
    assert.equal(
      publicBlockingAttempt === null
        ? false
        : "cardSyncEvidence" in publicBlockingAttempt,
      false,
    );
    assert.equal(
      JSON.stringify(publicBlockingAttempt).includes("AUTH-PROTECTED"),
      false,
    );
    assert.deepEqual(
      await reader.read({
        attemptId: created.attemptId,
        orderGuid: created.orderGuid,
        provider: "square",
        operation: "purchase",
        amountCents: 500,
      }),
      evidence,
    );

    const preserved = {
      ...approved,
      updatedAtIso: "2026-07-28T00:02:00.000Z",
    };
    assert.equal(
      await repositories.payments.compareAndUpdate(approved, preserved),
      true,
    );
    assert.deepEqual(
      await reader.read({
        attemptId: created.attemptId,
        orderGuid: created.orderGuid,
        provider: "square",
        operation: "purchase",
        amountCents: 500,
      }),
      evidence,
    );

    const replacement = cardSyncEvidence({
      txnRef: "sync-txn-must-not-win",
      authCode: "AUTH-MUST-NOT-WIN",
    });
    assert.equal(
      await repositories.payments.compareAndUpdate(
        approved,
        {
          ...approved,
          updatedAtIso: "2026-07-28T00:03:00.000Z",
        },
        replacement,
      ),
      false,
    );
    assert.deepEqual(
      await reader.read({
        attemptId: created.attemptId,
        orderGuid: created.orderGuid,
        provider: "square",
        operation: "purchase",
        amountCents: 500,
      }),
      evidence,
    );

    await insertDraftOrder(connection, "order-stale-evidence", 301, 500);
    const noEvidence = payment({
      attemptId: "attempt-stale-evidence",
      idempotencyKey: "idempotency-stale-evidence",
      orderGuid: "order-stale-evidence",
    });
    assert.equal(await repositories.payments.insertIfUnblocked(noEvidence), null);
    assert.equal(
      await repositories.payments.compareAndUpdate(
        { ...noEvidence, updatedAtIso: "2026-07-28T00:00:01.000Z" },
        {
          ...noEvidence,
          state: "Approved",
          updatedAtIso: "2026-07-28T00:01:00.000Z",
        },
        evidence,
      ),
      false,
    );
    assert.equal(
      await reader.read({
        attemptId: noEvidence.attemptId,
        orderGuid: noEvidence.orderGuid,
        provider: "square",
        operation: "purchase",
        amountCents: 500,
      }),
      null,
    );

    const forgedExpected = {
      ...noEvidence,
      orderGuid: "order-forged-caller",
    };
    assert.equal(
      await repositories.payments.compareAndUpdate(
        forgedExpected,
        {
          ...forgedExpected,
          state: "Approved",
          updatedAtIso: "2026-07-28T00:03:00.000Z",
        },
        evidence,
      ),
      false,
    );
    assert.equal(
      await reader.read({
        attemptId: noEvidence.attemptId,
        orderGuid: noEvidence.orderGuid,
        provider: "square",
        operation: "purchase",
        amountCents: 500,
      }),
      null,
    );
    assert.equal(
      await reader.read({
        attemptId: "attempt-does-not-exist",
        orderGuid: "order-does-not-exist",
        provider: "square",
        operation: "purchase",
        amountCents: 500,
      }),
      null,
    );

    const approvedWithoutEvidence = {
      ...noEvidence,
      state: "Approved" as const,
      updatedAtIso: "2026-07-28T00:04:00.000Z",
    };
    await assert.rejects(
      () => repositories.payments.compareAndUpdate(
        noEvidence,
        approvedWithoutEvidence,
        {
          ...evidence,
          pan: "4111111111111111",
        } as CardSyncEvidenceV1,
      ),
      /unsupported field: pan/,
    );
    await assert.rejects(
      () => repositories.payments.compareAndUpdate(
        noEvidence,
        approvedWithoutEvidence,
        {
          ...evidence,
          rawPayload: { approved: true },
        } as CardSyncEvidenceV1,
      ),
      /unsupported field: rawPayload/,
    );
    await assert.rejects(
      () => repositories.payments.compareAndUpdate(
        noEvidence,
        approvedWithoutEvidence,
        {
          ...evidence,
          responseText: "{\"approved\":true,\"pan\":\"4111111111111111\"}",
        },
      ),
      /unmasked PAN/,
    );
    await assert.rejects(
      () => repositories.payments.compareAndUpdate(
        noEvidence,
        approvedWithoutEvidence,
        {
          ...evidence,
          maskedCardNumber: "4111111111111111",
        },
      ),
      /masked card number is invalid/,
    );
    assert.equal(
      await reader.read({
        attemptId: noEvidence.attemptId,
        orderGuid: noEvidence.orderGuid,
        provider: "square",
        operation: "purchase",
        amountCents: 500,
      }),
      null,
    );
  } finally {
    await connection.close();
    rmSync(folder, { recursive: true, force: true });
  }
});

test("真实 SQLite：legacy 券 token 可读，并在下一次 CAS 升级为 voucher 与卡证据并存的 v1 envelope", async () => {
  const folder = mkdtempSync(join(tmpdir(), "hb-pos-payment-protected-legacy-"));
  const databasePath = join(folder, "payment-protected-legacy.db");
  const connection = new SystemSqliteConnection(databasePath);
  const encryptor = {
    async encrypt(plaintext: string) {
      return new TextEncoder().encode(plaintext);
    },
    async decrypt(ciphertext: Uint8Array) {
      return new TextDecoder().decode(ciphertext);
    },
  };
  try {
    await connection.exec(POS_DATABASE_MIGRATIONS.map((migration) => migration.sql).join("\n"));
    await insertDraftOrder(connection, "order-legacy-protected", 302, -700);
    await insertPaymentAttemptRow(connection, {
      attemptId: "attempt-legacy-protected",
      orderGuid: "order-legacy-protected",
      provider: "square",
      operation: "refund",
      amountCents: -700,
      state: "Created",
      providerPayloadCiphertext: new TextEncoder().encode(JSON.stringify({
        voucherReservationToken: "reservation-legacy",
      })),
    });
    const repositories = createSqliteRepositories(connection, {
      nowIso: () => "2026-07-28T00:00:00.000Z",
      createLeaseId: () => "lease-legacy",
      encryptor,
    });
    const reader = new SqlitePaymentProtectedMaterialReader(
      connection,
      encryptor,
    );
    const legacy = await repositories.payments.get("attempt-legacy-protected");
    assert.ok(legacy);
    assert.equal(legacy.references.voucherReservationToken, "reservation-legacy");
    assert.equal(
      await reader.read({
        attemptId: legacy.attemptId,
        orderGuid: legacy.orderGuid,
        provider: "square",
        operation: "refund",
        amountCents: -700,
      }),
      null,
    );

    const evidence = cardSyncEvidence({
      operation: "refund",
      amountCents: 700,
      txnRef: "legacy-refund-txn",
      refundReference: "legacy-refund-reference",
    });
    const approved = {
      ...legacy,
      state: "Approved" as const,
      updatedAtIso: "2026-07-28T00:02:00.000Z",
    };
    assert.equal(
      await repositories.payments.compareAndUpdate(
        legacy,
        approved,
        evidence,
      ),
      true,
    );
    assert.deepEqual(
      await readProtectedPayload(
        connection,
        encryptor,
        legacy.attemptId,
      ),
      {
        version: 1,
        voucherReservationToken: "reservation-legacy",
        cardSyncEvidence: evidence,
      },
    );
    assert.deepEqual(
      await reader.read({
        attemptId: legacy.attemptId,
        orderGuid: legacy.orderGuid,
        provider: "square",
        operation: "refund",
        amountCents: -700,
      }),
      evidence,
    );
  } finally {
    await connection.close();
    rmSync(folder, { recursive: true, force: true });
  }
});

test("真实 SQLite：manual-card 已入账后释放终端，未入账仍阻断下一笔交易", async () => {
  const folder = mkdtempSync(join(tmpdir(), "hb-pos-manual-terminal-blocking-"));
  const databasePath = join(folder, "payment.db");
  try {
    const connection = new SystemSqliteConnection(databasePath);
    await connection.exec(POS_DATABASE_MIGRATIONS.map((migration) => migration.sql).join("\n"));
    await insertDraftOrder(connection, "manual-completed", 1, 500);
    await insertDraftOrder(connection, "manual-completed-next", 2, 500);
    await insertDraftOrder(connection, "manual-pending", 3, 500);
    await insertDraftOrder(connection, "manual-pending-next", 4, 500);
    for (const orderGuid of [
      "manual-completed",
      "manual-completed-next",
      "manual-pending",
      "manual-pending-next",
    ]) {
      await connection.run(
        `INSERT INTO payment_order_draft_bindings (
          draft_id, request_fingerprint, pricing_state_json, order_guid,
          store_code, device_code, state, created_at_iso
        ) VALUES (?, ?, ?, ?, 'S1', 'IPAD1', 'Active', ?)`,
        [`draft-${orderGuid}`, `fingerprint-${orderGuid}`, "{}", orderGuid, "2026-07-28T00:00:00.000Z"],
      );
    }
    const encryptor = {
      async encrypt(plaintext: string) { return new TextEncoder().encode(plaintext); },
      async decrypt(ciphertext: Uint8Array) { return new TextDecoder().decode(ciphertext); },
    };
    const repositories = createSqliteRepositories(connection, {
      nowIso: () => "2026-07-28T00:00:00.000Z",
      createLeaseId: () => "lease-manual-terminal",
      encryptor,
    });

    const completed = payment({
      attemptId: "manual-completed-attempt",
      idempotencyKey: "manual-completed-idempotency",
      orderGuid: "manual-completed",
      provider: "manual-card",
    });
    assert.equal(await repositories.payments.insertIfUnblocked(completed), null);
    const completedApproved = {
      ...completed,
      state: "Approved" as const,
      updatedAtIso: "2026-07-28T00:01:00.000Z",
      references: { ...completed.references, txnRef: `MANUAL:${completed.attemptId}` },
      responseCode: "MANUAL_CONFIRMED",
    };
    assert.equal(await repositories.payments.compareAndUpdate(completed, completedApproved), true);
    await connection.run(
      "INSERT INTO order_tenders (tender_guid, order_guid, method, amount_cents, payment_attempt_id, created_at_iso) VALUES (?, ?, ?, ?, ?, ?)",
      ["manual-completed-tender", "manual-completed", "card", 500, completed.attemptId, "2026-07-28T00:01:00.000Z"],
    );
    const completedFacts = await connection.getFirst<{ provider: string; state: string; amount_cents: number; tender_count: number }>(
        `SELECT p.provider, p.state, p.amount_cents,
           (SELECT COUNT(*) FROM order_tenders t WHERE t.payment_attempt_id = p.attempt_id
             AND t.order_guid = p.order_guid AND t.amount_cents = p.amount_cents
             AND p.provider IN ('square', 'linkly-cloud', 'manual-card') AND t.method = 'card') AS tender_count
        FROM payment_attempts p WHERE p.attempt_id = ?`,
        [completed.attemptId],
      );
    assert.equal(completedFacts?.provider, "manual-card");
    assert.equal(completedFacts?.state, "Approved");
    assert.equal(completedFacts?.amount_cents, 500);
    assert.equal(completedFacts?.tender_count, 1);
    const next = payment({
      attemptId: "manual-completed-next-attempt",
      idempotencyKey: "manual-completed-next-idempotency",
      orderGuid: "manual-completed-next",
    });
    assert.equal(await repositories.payments.insertIfUnblocked(next), null);
    const nextApproved = {
      ...next,
      state: "Approved" as const,
      updatedAtIso: "2026-07-28T00:01:30.000Z",
      responseCode: "APPROVED",
    };
    assert.equal(await repositories.payments.compareAndUpdate(next, nextApproved), true);
    await connection.run(
      "INSERT INTO order_tenders (tender_guid, order_guid, method, amount_cents, payment_attempt_id, created_at_iso) VALUES (?, ?, ?, ?, ?, ?)",
      ["manual-completed-next-tender", "manual-completed-next", "card", 500, next.attemptId, "2026-07-28T00:01:30.000Z"],
    );

    const pending = payment({
      attemptId: "manual-pending-attempt",
      idempotencyKey: "manual-pending-idempotency",
      orderGuid: "manual-pending",
      provider: "manual-card",
    });
    assert.equal(await repositories.payments.insertIfUnblocked(pending), null);
    const pendingApproved = {
      ...pending,
      state: "Approved" as const,
      updatedAtIso: "2026-07-28T00:02:00.000Z",
      references: { ...pending.references, txnRef: `MANUAL:${pending.attemptId}` },
      responseCode: "MANUAL_CONFIRMED",
    };
    assert.equal(await repositories.payments.compareAndUpdate(pending, pendingApproved), true);
    const blocked = payment({
      attemptId: "manual-pending-next-attempt",
      idempotencyKey: "manual-pending-next-idempotency",
      orderGuid: "manual-pending-next",
    });
    await assert.rejects(
      () => repositories.payments.insertIfUnblocked(blocked),
      /PAYMENT_TERMINAL_BLOCKING_ATTEMPT_EXISTS/,
    );
    assert.equal(await countAttempts(connection, blocked.attemptId), 0);
    await connection.close();
  } finally {
    rmSync(folder, { recursive: true, force: true });
  }
});

test("真实 SQLite：M46 升级前 manual-card 已入账仍误阻断，升级后允许新交易", async () => {
  const folder = mkdtempSync(join(tmpdir(), "hb-pos-manual-m46-upgrade-"));
  const databasePath = join(folder, "payment.db");
  try {
    const connection = new SystemSqliteConnection(databasePath);
    await connection.exec(
      POS_DATABASE_MIGRATIONS
        .filter((migration) => migration.version <= 45)
        .map((migration) => migration.sql)
        .join("\n"),
    );
    // 仓储的 findBlocking 现在会读取恢复中心表（M51 才创建）；本用例模拟 M46 之前的历史库，
    // 只补两张空占位表，使“无恢复 case”的语义与历史一致，不引入 M51 的触发器。
    await connection.exec(
      "CREATE TABLE payment_recovery_cases (attempt_id TEXT, order_guid TEXT, state TEXT);" +
      "CREATE TABLE manual_payment_tender_bindings (attempt_id TEXT);",
    );
    await insertDraftOrder(connection, "m46-manual-completed", 1, 500);
    await insertDraftOrder(connection, "m46-manual-next", 2, 500);
    for (const orderGuid of ["m46-manual-completed", "m46-manual-next"]) {
      await connection.run(
        `INSERT INTO payment_order_draft_bindings (
          draft_id, request_fingerprint, pricing_state_json, order_guid,
          store_code, device_code, state, created_at_iso
        ) VALUES (?, ?, ?, ?, 'S1', 'IPAD1', 'Active', ?)`,
        [`draft-${orderGuid}`, `fingerprint-${orderGuid}`, "{}", orderGuid, "2026-07-28T00:00:00.000Z"],
      );
    }
    await insertPaymentAttemptRow(connection, {
      attemptId: "m46-manual-attempt",
      orderGuid: "m46-manual-completed",
      provider: "manual-card",
      operation: "purchase",
      amountCents: 500,
      state: "Approved",
      txnRef: "MANUAL:m46-manual-attempt",
      responseCode: "MANUAL_CONFIRMED",
    });
    await connection.run(
      "INSERT INTO order_tenders (tender_guid, order_guid, method, amount_cents, payment_attempt_id, created_at_iso) VALUES (?, ?, ?, ?, ?, ?)",
      ["m46-manual-tender", "m46-manual-completed", "card", 500, "m46-manual-attempt", "2026-07-28T00:01:00.000Z"],
    );
    const encryptor = {
      async encrypt(plaintext: string) { return new TextEncoder().encode(plaintext); },
      async decrypt(ciphertext: Uint8Array) { return new TextDecoder().decode(ciphertext); },
    };
    const repositories = createSqliteRepositories(connection, {
      nowIso: () => "2026-07-28T00:00:00.000Z",
      createLeaseId: () => "lease-m46",
      encryptor,
    });
    const next = payment({
      attemptId: "m46-next-attempt",
      idempotencyKey: "m46-next-idempotency",
      orderGuid: "m46-manual-next",
    });
    await assert.rejects(
      () => repositories.payments.insertIfUnblocked(next),
      /PAYMENT_TERMINAL_BLOCKING_ATTEMPT_EXISTS/,
    );

    const M46 = POS_DATABASE_MIGRATIONS.find((migration) => migration.version === 46);
    assert.ok(M46);
    await connection.exec(M46.sql);
    assert.equal(await repositories.payments.insertIfUnblocked(next), null);
    await connection.close();
  } finally {
    rmSync(folder, { recursive: true, force: true });
  }
});

async function insertDraftOrder(
  connection: SqliteConnectionPort,
  orderGuid: string,
  sequence: number,
  amountCents: number,
  state: LocalOrderState = "Draft",
): Promise<void> {
  await connection.run(
    "INSERT INTO local_orders (order_guid, local_sequence, store_code, device_code, cashier_id, cashier_name, sold_at_iso, state, total_cents, discount_cents, actual_amount_cents, original_order_guid, created_at_iso, updated_at_iso) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)",
    [orderGuid, sequence, "S1", "IPAD1", "cashier-1", "Cashier", "2026-07-28T00:00:00.000Z", state, amountCents, 0, amountCents, null, "2026-07-28T00:00:00.000Z", "2026-07-28T00:00:00.000Z"],
  );
}

async function countAttempts(
  connection: SqliteConnectionPort,
  attemptId: string,
): Promise<number> {
  const row = await connection.getFirst<{ count: number }>(
    "SELECT COUNT(*) AS count FROM payment_attempts WHERE attempt_id = ?",
    [attemptId],
  );
  return Number(row?.count ?? 0);
}

async function insertPaymentAttemptRow(
  connection: SqliteConnectionPort,
  input: Readonly<{
    attemptId: string;
    orderGuid: string;
    provider: PaymentAttempt["provider"];
    operation: PaymentAttempt["operation"];
    amountCents: number;
    state: PaymentAttempt["state"];
    paymentId?: string | null;
    sessionId?: string | null;
    txnRef?: string | null;
    rfn?: string | null;
    providerPayloadCiphertext?: Uint8Array | null;
    receiptCiphertext?: Uint8Array | null;
    responseCode?: string | null;
  }>,
): Promise<void> {
  await connection.run(
    `INSERT INTO payment_attempts (
      attempt_id, idempotency_key, order_guid, provider, operation, amount_cents,
      state, checkout_id, payment_id, session_id, txn_ref, rfn,
      provider_payload_ciphertext, provider_receipt_ciphertext,
      provider_response_code, created_at_iso, updated_at_iso, last_error_code
    ) VALUES (?, ?, ?, ?, ?, ?, ?, NULL, ?, ?, ?, ?, ?, ?, ?, ?, ?, NULL)`,
    [
      input.attemptId,
      `idempotency-${input.attemptId}`,
      input.orderGuid,
      input.provider,
      input.operation,
      input.amountCents,
      input.state,
      input.paymentId ?? null,
      input.sessionId ?? null,
      input.txnRef ?? null,
      input.rfn ?? null,
      input.providerPayloadCiphertext ?? null,
      input.receiptCiphertext ?? null,
      input.responseCode ?? null,
      "2026-07-28T00:00:00.000Z",
      "2026-07-28T00:01:00.000Z",
    ],
  );
}

async function insertTender(
  connection: SqliteConnectionPort,
  tenderGuid: string,
  orderGuid: string,
  method: "cash" | "card" | "voucher",
  amountCents: number,
  attemptId: string | null,
): Promise<void> {
  await connection.run(
    "INSERT INTO order_tenders (tender_guid, order_guid, method, amount_cents, payment_attempt_id, created_at_iso) VALUES (?, ?, ?, ?, ?, ?)",
    [tenderGuid, orderGuid, method, amountCents, attemptId, "2026-07-28T00:02:00.000Z"],
  );
}

async function readProtectedPayload(
  connection: SqliteConnectionPort,
  encryptor: Readonly<{
    decrypt(ciphertext: Uint8Array): Promise<string>;
  }>,
  attemptId: string,
): Promise<unknown> {
  const row = await connection.getFirst<{ provider_payload_ciphertext: unknown }>(
    "SELECT provider_payload_ciphertext FROM payment_attempts WHERE attempt_id = ?",
    [attemptId],
  );
  assert.ok(row?.provider_payload_ciphertext instanceof Uint8Array);
  return JSON.parse(
    await encryptor.decrypt(row.provider_payload_ciphertext),
  ) as unknown;
}

function cardSyncEvidence(
  overrides: Partial<CardSyncEvidenceV1> = {},
): CardSyncEvidenceV1 {
  return {
    version: 1,
    provider: "square",
    operation: "purchase",
    processor: "Square",
    txnRef: "sync-txn-1",
    authCode: null,
    cardType: "VISA",
    cardBin: 411111,
    maskedCardNumber: "411111******1111",
    merchantId: "merchant-1",
    responseCode: "00",
    responseText: "APPROVED",
    stan: "123456",
    bankDateTimeIso: "2026-07-28T00:00:00.000Z",
    amountCents: 500,
    refundReference: null,
    ...overrides,
  };
}

function runStatement(
  database: DatabaseSync,
  sql: string,
  parameters: readonly SqlValue[],
): SqlRunResult {
  const result = database
    .prepare(sql)
    .run(...parameters as readonly SQLInputValue[]);
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
  const row = database
    .prepare(sql)
    .get(...parameters as readonly SQLInputValue[]);
  return row === undefined ? null : row as unknown as T;
}

function getAll<T extends object>(
  database: DatabaseSync,
  sql: string,
  parameters: readonly SqlValue[],
): readonly T[] {
  return database
    .prepare(sql)
    .all(...parameters as readonly SQLInputValue[]) as unknown as readonly T[];
}
