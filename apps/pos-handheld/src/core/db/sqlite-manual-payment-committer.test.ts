import assert from "node:assert/strict";
import { DatabaseSync, type SQLInputValue } from "node:sqlite";
import test from "node:test";

import type {
  SqliteConnectionPort,
  SqlRunResult,
  SqlValue,
} from "@hb/pos-db/core/db/types";
import type { LocalOrder } from "@hb/pos-domain/core/contracts/order";

import { applyMigrations } from "./migrations";
import {
  SqliteManualPaymentOrderCommitter,
  type ManualPaymentOrderCommit,
} from "./sqlite-manual-payment-committer";
import { readManualPaymentSyncEvidence } from "./sqlite-manual-payment-sync";
import { SqliteOrderSyncMaterialResolver } from "./sqlite-order-sync-material";

const T0 = "2026-09-11T00:00:00.000Z";
const T1 = "2026-09-11T00:01:00.000Z";

test("真实 SQLite：人工已收款绑定原 attempt，完整付款原子完成且重复提交不重复入账", async () => {
  await withDatabase(async (connection) => {
    await seedRecovery(connection);
    const committer = new SqliteManualPaymentOrderCommitter(connection, () => T1);
    const first = await committer.completeManualPaymentOrder(command());
    assert.deepEqual(first, {
      replayed: false,
      orderGuid: "order-1",
      tenderGuid: "manual-tender-1",
      completed: true,
      signedTenderAmountCents: 900,
    });
    assert.equal((await committer.completeManualPaymentOrder(command())).replayed, true);
    assert.deepEqual(
      await readManualPaymentSyncEvidence(connection, {
        tenderGuid: "manual-tender-1",
        orderGuid: "order-1",
        storeCode: "STORE-1",
        deviceCode: "DEVICE-1",
        amountCents: 900,
      }),
      {
        version: 1,
        provider: "linkly-cloud",
        operation: "purchase",
        processor: "ANZ",
        txnRef: null,
        authCode: null,
        cardType: null,
        cardBin: null,
        maskedCardNumber: null,
        merchantId: null,
        responseCode: "MANUAL",
        responseText: "Supervisor confirmed payment; not provider approval",
        stan: null,
        bankDateTimeIso: null,
        amountCents: 900,
        refundReference: null,
      },
    );
    const sync = await new SqliteOrderSyncMaterialResolver(connection, {
      returnCapacityVault: { async resolveProtectedContext() { return null; } },
      voucherProtectedTokens: { async getByAttempt() { return null; } },
      paymentProtectedMaterials: { async read() { throw new Error("manual payment must not read provider material"); } },
    }).resolveForSync(manualOrder(), "Production");
    assert.equal(sync.order.tenders[0]?.reference, "MANUAL_CARD:attempt-1");
    assert.equal(
      sync.cardSyncEvidenceByTenderGuid.get("manual-tender-1")?.responseCode,
      "MANUAL",
    );
    await assert.rejects(
      () => readManualPaymentSyncEvidence(connection, {
        tenderGuid: "manual-tender-1", orderGuid: "order-1",
        storeCode: "STORE-OTHER", deviceCode: "DEVICE-1", amountCents: 900,
      }),
      /EVIDENCE_MISMATCH/,
    );
    await assert.rejects(
      () => readManualPaymentSyncEvidence(connection, {
        tenderGuid: "manual-tender-1", orderGuid: "order-1",
        storeCode: "STORE-1", deviceCode: "DEVICE-1", amountCents: 899,
      }),
      /EVIDENCE_MISMATCH/,
    );
    assert.deepEqual(await one(connection, `SELECT
      (SELECT state FROM local_orders WHERE order_guid = 'order-1') AS order_state,
      (SELECT state FROM payment_attempts WHERE attempt_id = 'attempt-1') AS attempt_state,
      (SELECT COUNT(*) FROM order_tenders WHERE payment_attempt_id = 'attempt-1') AS tenders,
      (SELECT COUNT(*) FROM manual_payment_tender_bindings WHERE attempt_id = 'attempt-1') AS bindings,
      (SELECT COUNT(*) FROM outbox_messages WHERE aggregate_id = 'order-1') AS outboxes
    `), {
      order_state: "PendingSync",
      attempt_state: "Unknown",
      tenders: 1,
      bindings: 1,
      outboxes: 1,
    });
  });
});

test("真实 SQLite：跨 scope、错误授权及人工金额与原 attempt 不符均失败关闭", async () => {
  await withDatabase(async (connection) => {
    await seedRecovery(connection);
    const committer = new SqliteManualPaymentOrderCommitter(connection, () => T1);
    await assert.rejects(
      () => committer.completeManualPaymentOrder(command({ deviceCode: "DEVICE-OTHER" })),
      /SCOPE_MISMATCH/,
    );
    await assert.rejects(
      () => committer.completeManualPaymentOrder(command({ authorizationId: "auth-other" })),
      /AUTHORIZATION_MISMATCH/,
    );
    assert.equal(await scalar(connection, "SELECT COUNT(*) AS count FROM order_tenders"), 0);
  });
  await withDatabase(async (connection) => {
    // action 表不可变，用一笔原始金额与人工金额不一致的持久事实证明提交器不信任人工输入。
    await seedRecovery(connection, {
      verifiedAmountCents: 899,
    });
    const committer = new SqliteManualPaymentOrderCommitter(connection, () => T1);
    await assert.rejects(
      () => committer.completeManualPaymentOrder(command()),
      /AMOUNT_MISMATCH/,
    );
    assert.equal(await scalar(connection, "SELECT COUNT(*) AS count FROM order_tenders"), 0);
  });
  await withDatabase(async (connection) => {
    await seedRecovery(connection, { supervisorActorJson: "{}" });
    const committer = new SqliteManualPaymentOrderCommitter(connection, () => T1);
    await assert.rejects(
      () => committer.completeManualPaymentOrder(command()),
      /SUPERVISOR_ACTOR_INVALID/,
    );
  });
});

test("真实 SQLite：完成阶段任何写入失败都会回滚订单、tender、人工来源绑定和 outbox", async () => {
  await withDatabase(async (connection) => {
    await seedRecovery(connection);
    await connection.run(
      `INSERT INTO audit_events (
        event_id, event_type, occurred_at_iso, order_guid, correlation_id,
        payload_json, uploaded_at_iso, delivery_state, attempt_count,
        next_attempt_at_iso, last_error_code, scope_store_code, scope_device_code
      ) VALUES ('manual-complete-audit', 'EXISTING', ?, 'order-1', 'existing', '{}',
        NULL, 'pending', 0, ?, NULL, 'STORE-1', 'DEVICE-1')`,
      [T0, T0],
    );
    const committer = new SqliteManualPaymentOrderCommitter(connection, () => T1);
    await assert.rejects(() => committer.completeManualPaymentOrder(command()), /UNIQUE|constraint/i);
    assert.deepEqual(await one(connection, `SELECT
      (SELECT state FROM local_orders WHERE order_guid = 'order-1') AS order_state,
      (SELECT COUNT(*) FROM order_tenders WHERE order_guid = 'order-1') AS tenders,
      (SELECT COUNT(*) FROM manual_payment_tender_bindings WHERE record_id = 'record-1') AS bindings,
      (SELECT COUNT(*) FROM outbox_messages WHERE aggregate_id = 'order-1') AS outboxes
    `), { order_state: "Draft", tenders: 0, bindings: 0, outboxes: 0 });
  });
});

test("真实 SQLite：人工来源绑定和 case scope 不可篡改，tender 金额偏离后同步失败关闭", async () => {
  await withDatabase(async (connection) => {
    await seedRecovery(connection);
    const committer = new SqliteManualPaymentOrderCommitter(connection, () => T1);
    await committer.completeManualPaymentOrder(command());
    await assert.rejects(
      () => connection.run(
        "UPDATE manual_payment_tender_bindings SET action_id = 'forged-action' WHERE tender_guid = 'manual-tender-1'",
      ),
      /MANUAL_PAYMENT_TENDER_BINDING_IMMUTABLE/,
    );
    await assert.rejects(
      () => connection.run(
        "UPDATE payment_recovery_cases SET store_code = 'STORE-OTHER' WHERE record_id = 'record-1'",
      ),
      /PAYMENT_RECOVERY_CASE_IDENTITY_IMMUTABLE/,
    );
    await connection.run(
      "UPDATE order_tenders SET amount_cents = 899 WHERE tender_guid = 'manual-tender-1'",
    );
    await assert.rejects(
      () => readManualPaymentSyncEvidence(connection, {
        tenderGuid: "manual-tender-1", orderGuid: "order-1",
        storeCode: "STORE-1", deviceCode: "DEVICE-1", amountCents: 900,
      }),
      /EVIDENCE_MISMATCH/,
    );
  });
});

test("真实 SQLite：人工金额不足时只进入 Completing，召回 draft 或终端 fence 一律拒绝", async () => {
  await withDatabase(async (connection) => {
    await seedRecovery(connection, { amountCents: 400 });
    const committer = new SqliteManualPaymentOrderCommitter(connection, () => T1);
    const result = await committer.completeManualPaymentOrder(command());
    assert.equal(result.completed, false);
    assert.equal(result.signedTenderAmountCents, 400);
    assert.deepEqual(await one(connection, `SELECT
      (SELECT state FROM local_orders WHERE order_guid = 'order-1') AS order_state,
      (SELECT COUNT(*) FROM outbox_messages WHERE aggregate_id = 'order-1') AS outboxes
    `), { order_state: "Completing", outboxes: 0 });
  });
  await withDatabase(async (connection) => {
    await seedRecovery(connection, { recallBinding: { kind: "recalled" } });
    const committer = new SqliteManualPaymentOrderCommitter(connection, () => T1);
    await assert.rejects(() => committer.completeManualPaymentOrder(command()), /RECALL_UNSUPPORTED/);
    assert.equal(await scalar(connection, "SELECT COUNT(*) AS count FROM order_tenders"), 0);
  });
});

test("真实 SQLite：人工判定后的非终态 Pending 变化可提交，provider 终态仍优先", async () => {
  await withDatabase(async (connection) => {
    await seedRecovery(connection);
    await connection.run(
      "UPDATE payment_attempts SET state = 'Pending', updated_at_iso = ? WHERE attempt_id = 'attempt-1'",
      [T1],
    );
    const committer = new SqliteManualPaymentOrderCommitter(connection, () => T1);
    assert.equal((await committer.completeManualPaymentOrder(command())).completed, true);
  });
  await withDatabase(async (connection) => {
    await seedRecovery(connection);
    await connection.run(
      "UPDATE payment_attempts SET state = 'Approved', updated_at_iso = ? WHERE attempt_id = 'attempt-1'",
      [T1],
    );
    const committer = new SqliteManualPaymentOrderCommitter(connection, () => T1);
    await assert.rejects(
      () => committer.completeManualPaymentOrder(command()),
      /TRUTH_MISMATCH/,
    );
  });
});

test("真实 SQLite：人工已收款后 provider 终态冲突让同步保持 retryable", async () => {
  await withDatabase(async (connection) => {
    await seedRecovery(connection);
    const committer = new SqliteManualPaymentOrderCommitter(connection, () => T1);
    await committer.completeManualPaymentOrder(command());
    await connection.run(
      "UPDATE payment_attempts SET state = 'Approved', updated_at_iso = ? WHERE attempt_id = 'attempt-1'",
      [T1],
    );
    await assert.rejects(
      () => readManualPaymentSyncEvidence(connection, {
        tenderGuid: "manual-tender-1", orderGuid: "order-1",
        storeCode: "STORE-1", deviceCode: "DEVICE-1", amountCents: 900,
      }),
      (error: unknown) => error instanceof Error &&
        error.message.includes("ORDER_SYNC_MANUAL_PROVIDER_CONFLICT"),
    );
    assert.equal(
      (await connection.getFirst<{ state: unknown }>(
        "SELECT state FROM outbox_messages WHERE aggregate_id = 'order-1'",
      ))?.state,
      "pending",
    );
  });
});

test("真实 SQLite：重启后的提交器拒绝授权换绑及同一人员伪造双人授权", async () => {
  await withDatabase(async (connection) => {
    await seedRecovery(connection, { authorizationActionId: "forged-action" });
    // 新实例只依赖持久记录，模拟授权完成后进程重启。
    const restarted = new SqliteManualPaymentOrderCommitter(connection, () => T1);
    await assert.rejects(
      () => restarted.completeManualPaymentOrder(command()),
      /AUTHORIZATION_BINDING_MISMATCH/,
    );
  });
  await withDatabase(async (connection) => {
    const sameIdentity = JSON.stringify({
      requestingCashierId: "supervisor-1",
      requestingCashierName: "Same Person",
      requestingUserGuid: "cashier-user-1",
    });
    await seedRecovery(connection, { supervisorActorJson: sameIdentity });
    const restarted = new SqliteManualPaymentOrderCommitter(connection, () => T1);
    await assert.rejects(
      () => restarted.completeManualPaymentOrder(command()),
      /AUTHORIZATION_ACTORS_NOT_DISTINCT/,
    );
  });
});

test("真实 SQLite：人工已收款拒绝过期对账，租约取得后终态到达仍阻断 MANUAL 证据", async () => {
  await withDatabase(async (connection) => {
    await seedRecovery(connection, {
      reconciliationObservedAtIso: "2026-09-10T23:50:00.000Z",
    });
    const restarted = new SqliteManualPaymentOrderCommitter(
      connection,
      () => "2026-09-11T00:10:00.000Z",
    );
    await assert.rejects(
      () => restarted.completeManualPaymentOrder(command()),
      /RECONCILIATION_MISMATCH/,
    );
  });
  await withDatabase(async (connection) => {
    await seedRecovery(connection);
    const committer = new SqliteManualPaymentOrderCommitter(connection, () => T1);
    await committer.completeManualPaymentOrder(command());
    await connection.run(
      `UPDATE local_orders SET state = 'Syncing' WHERE order_guid = 'order-1'`,
    );
    await connection.run(
      `UPDATE outbox_messages SET state = 'leased', lease_id = 'lease-1',
        lease_expires_at_iso = '2026-09-11T00:02:00.000Z'
       WHERE aggregate_id = 'order-1'`,
    );
    await connection.run(
      "UPDATE payment_attempts SET state = 'Approved', updated_at_iso = ? WHERE attempt_id = 'attempt-1'",
      [T1],
    );
    await assert.rejects(
      () => readManualPaymentSyncEvidence(connection, {
        tenderGuid: "manual-tender-1", orderGuid: "order-1",
        storeCode: "STORE-1", deviceCode: "DEVICE-1", amountCents: 900,
      }),
      /ORDER_SYNC_MANUAL_PROVIDER_CONFLICT/,
    );
    assert.equal(
      (await connection.getFirst<{ state: unknown }>(
        "SELECT state FROM outbox_messages WHERE aggregate_id = 'order-1'",
      ))?.state,
      "leased",
    );
  });
});

function command(overrides: Partial<ManualPaymentOrderCommit> = {}): ManualPaymentOrderCommit {
  return {
    recordId: "record-1",
    actionId: "manual-action-1",
    orderGuid: "order-1",
    attemptId: "attempt-1",
    storeCode: "STORE-1",
    deviceCode: "DEVICE-1",
    authorizationId: "supervisor-auth-1",
    tenderGuid: "manual-tender-1",
    completionAuditEvent: {
      eventId: "manual-complete-audit",
      eventType: "PAYMENT_COMPLETE",
      occurredAtIso: T1,
      orderGuid: "order-1",
      correlationId: "manual-action-1",
      payload: { action: "manual-payment-complete", authorizationId: "supervisor-auth-1" },
    },
    outbox: {
      messageId: "manual-outbox-1",
      aggregateId: "order-1",
      kind: "order-sync",
      payloadJson: JSON.stringify({ orderGuid: "order-1" }),
      nextAttemptAtIso: T1,
    },
    ...overrides,
  };
}

async function seedRecovery(connection: SqliteConnectionPort, overrides: Partial<{
  orderGuid: string; attemptId: string; recordId: string; actionId: string;
  tenderGuid: string; amountCents: number; verifiedAmountCents: number;
  localSequence: number; recallBinding: unknown; supervisorActorJson: string;
  authorizationActionId: string;
  reconciliationObservedAtIso: string;
}> = {}): Promise<void> {
  const orderGuid = overrides.orderGuid ?? "order-1";
  const attemptId = overrides.attemptId ?? "attempt-1";
  const recordId = overrides.recordId ?? "record-1";
  const actionId = overrides.actionId ?? "manual-action-1";
  const amountCents = overrides.amountCents ?? 900;
  const verifiedAmountCents = overrides.verifiedAmountCents ?? amountCents;
  const fingerprint = JSON.stringify({
    version: 2,
    identity: { storeCode: "STORE-1", deviceCode: "DEVICE-1", cashierId: "cashier-1", cashierName: "Cashier" },
    cart: {}, pricingState: {}, originalOrderGuid: null,
    recallBinding: overrides.recallBinding ?? null,
  });
  await connection.run(
    `INSERT INTO local_orders (
      order_guid, local_sequence, store_code, device_code, cashier_id, cashier_name,
      sold_at_iso, state, total_cents, discount_cents, actual_amount_cents,
      original_order_guid, created_at_iso, updated_at_iso
    ) VALUES (?, ?, 'STORE-1', 'DEVICE-1', 'cashier-1', 'Cashier', ?, 'Draft', 900, 0, 900, NULL, ?, ?)`,
    [orderGuid, overrides.localSequence ?? 1, T0, T0, T0],
  );
  await connection.run(
    `INSERT INTO local_order_lines (
      line_id, order_guid, line_sequence, product_code, item_number, lookup_code,
      display_name, quantity, unit_price_cents, discount_cents, actual_amount_cents,
      price_source, line_kind, return_source_key, original_order_guid,
      original_order_detail_guid, reference_code, sync_price_source
    ) VALUES (?, ?, 1, 'P1', 'I1', 'L1', 'Product', '1', 900, 0, 900,
      'catalog', 'sale', NULL, NULL, NULL, 'REF-P1', 0)`,
    [`00000000-0000-4000-8000-${String(overrides.localSequence ?? 1).padStart(12, "0")}`, orderGuid],
  );
  await connection.run(
    `INSERT INTO payment_order_draft_bindings (
      draft_id, request_fingerprint, pricing_state_json, order_guid, store_code,
      device_code, state, abandon_action_id, abandon_audit_event_id, abandoned_at_iso,
      close_action_id, close_attempt_id, close_audit_event_id, closed_at_iso, created_at_iso
    ) VALUES (?, ?, '{}', ?, 'STORE-1', 'DEVICE-1', 'Active', NULL, NULL, NULL, NULL, NULL, NULL, NULL, ?)`,
    [`draft-${orderGuid}`, fingerprint, orderGuid, T0],
  );
  await connection.run(
    `INSERT INTO payment_attempts (
      attempt_id, idempotency_key, order_guid, provider, operation, amount_cents,
      state, checkout_id, payment_id, session_id, txn_ref, rfn,
      provider_payload_ciphertext, provider_receipt_ciphertext, provider_response_code,
      created_at_iso, updated_at_iso, last_error_code
    ) VALUES (?, ?, ?, 'linkly-cloud', 'purchase', ?, 'Unknown', NULL, NULL, NULL,
      NULL, NULL, NULL, NULL, NULL, ?, ?, NULL)`,
    [attemptId, `idem-${attemptId}`, orderGuid, amountCents, T0, T0],
  );
  await connection.run(
    `INSERT INTO payment_recovery_cases (
      record_id, order_guid, attempt_id, store_code, device_code, state, is_parked,
      park_action_id, opened_at_iso, updated_at_iso
    ) VALUES (?, ?, ?, 'STORE-1', 'DEVICE-1', 'manual-paid', 1, ?, ?, ?)`,
    [recordId, orderGuid, attemptId, `park-${recordId}`, T0, T1],
  );
  const supervisorActorJson = overrides.supervisorActorJson ?? JSON.stringify({
    requestingCashierId: "supervisor-1",
    requestingCashierName: "Supervisor",
    requestingUserGuid: "supervisor-user-1",
  });
  const requestingActorJson = JSON.stringify({
    requestingCashierId: "cashier-1",
    requestingCashierName: "Cashier",
    requestingUserGuid: "cashier-user-1",
  });
  await connection.run(
    `INSERT INTO payment_recovery_reconciliations (
      reconciliation_id, record_id, order_guid, attempt_id, provider,
      observed_state, observed_at_iso
    ) VALUES (?, ?, ?, ?, 'linkly-cloud', 'Unknown', ?)`,
    [`reconcile-${actionId}`, recordId, orderGuid, attemptId,
      overrides.reconciliationObservedAtIso ?? T1],
  );
  await connection.run(
    `INSERT INTO payment_recovery_authorizations (
      authorization_id, record_id, order_guid, attempt_id, action_id, finding,
      requesting_actor_json, supervisor_actor_json, created_at_iso
    ) VALUES ('supervisor-auth-1', ?, ?, ?, ?, 'paid', ?, ?, ?)`,
    [recordId, orderGuid, attemptId, overrides.authorizationActionId ?? actionId,
      requestingActorJson, supervisorActorJson, T1],
  );
  await connection.run(
    `INSERT INTO payment_recovery_actions (
      action_id, record_id, request_signature, finding, verified_amount_cents,
      evidence_reference, note, authorization_id, supervisor_actor_json,
      requesting_actor_json, reconciliation_id, attempt_state_snapshot, created_at_iso
    ) VALUES (?, ?, ?, 'paid', ?, 'receipt-1', 'Verified on terminal',
      'supervisor-auth-1', ?, ?, ?, 'Unknown', ?)`,
    [
      actionId,
      recordId,
      JSON.stringify([recordId, "paid", verifiedAmountCents]),
      verifiedAmountCents,
      supervisorActorJson,
      requestingActorJson,
      `reconcile-${actionId}`,
      T1,
    ],
  );
}

function manualOrder(): LocalOrder {
  return {
    orderGuid: "order-1",
    localSequence: 1,
    storeCode: "STORE-1",
    deviceCode: "DEVICE-1",
    cashierId: "cashier-1",
    cashierName: "Cashier",
    soldAtIso: T0,
    state: "PendingSync",
    total: { currency: "AUD", cents: 900 },
    discount: { currency: "AUD", cents: 0 },
    actualAmount: { currency: "AUD", cents: 900 },
    lines: [{
      lineId: "00000000-0000-4000-8000-000000000001",
      productCode: "P1",
      itemNumber: "I1",
      lookupCode: "L1",
      displayName: "Product",
      quantity: "1",
      unitPrice: { currency: "AUD", cents: 900 },
      discount: { currency: "AUD", cents: 0 },
      actualAmount: { currency: "AUD", cents: 900 },
      priceSource: "catalog",
      syncProvenance: { referenceCode: "REF-P1", priceSource: 0 },
      kind: "sale",
      returnSourceKey: null,
      originalOrderGuid: null,
      originalOrderDetailGuid: null,
    }],
    tenders: [{
      tenderGuid: "manual-tender-1",
      method: "card",
      amount: { currency: "AUD", cents: 900 },
      reference: null,
      reservationToken: null,
    }],
    originalOrderGuid: null,
  };
}

async function withDatabase(run: (connection: SqliteConnectionPort) => Promise<void>): Promise<void> {
  const database = new DatabaseSync(":memory:");
  database.exec("PRAGMA foreign_keys = ON");
  const connection = new TestConnection(database);
  try {
    await applyMigrations(connection, () => T0);
    await run(connection);
  } finally {
    database.close();
  }
}

class TestConnection implements SqliteConnectionPort {
  public constructor(private readonly database: DatabaseSync) {}
  public async exec(sql: string): Promise<void> { this.database.exec(sql); }
  public async run(sql: string, parameters: readonly SqlValue[] = []): Promise<SqlRunResult> {
    const result = this.database.prepare(sql).run(...(parameters as SQLInputValue[]));
    return { changes: Number(result.changes), lastInsertRowId: Number(result.lastInsertRowid) };
  }
  public async getFirst<T extends object>(sql: string, parameters: readonly SqlValue[] = []): Promise<T | null> {
    return (this.database.prepare(sql).get(...(parameters as SQLInputValue[])) as T | undefined) ?? null;
  }
  public async getAll<T extends object>(sql: string, parameters: readonly SqlValue[] = []): Promise<readonly T[]> {
    return this.database.prepare(sql).all(...(parameters as SQLInputValue[])) as T[];
  }
  public async withExclusiveTransaction<T>(run: (transaction: SqliteConnectionPort) => Promise<T>): Promise<T> {
    this.database.exec("BEGIN IMMEDIATE");
    try {
      const result = await run(this);
      this.database.exec("COMMIT");
      return result;
    } catch (error) {
      this.database.exec("ROLLBACK");
      throw error;
    }
  }
  public async close(): Promise<void> {}
}

async function scalar(connection: SqliteConnectionPort, sql: string): Promise<number> {
  const row = await connection.getFirst<{ count: unknown }>(sql);
  return Number(row?.count);
}

async function one(connection: SqliteConnectionPort, sql: string): Promise<Record<string, unknown>> {
  return { ...(await connection.getFirst<Record<string, unknown>>(sql)) };
}
