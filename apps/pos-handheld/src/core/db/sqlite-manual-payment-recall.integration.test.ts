import assert from "node:assert/strict";
import { DatabaseSync, type SQLInputValue } from "node:sqlite";
import test from "node:test";

import type {
  SqliteConnectionPort,
  SqlRunResult,
  SqlValue,
} from "@hb/pos-db/core/db/types";

import { applyMigrations } from "./migrations";
import { createManualPaymentRecallPort } from "./pos-database";
import {
  SqliteManualPaymentOrderCommitter,
  type ManualPaymentOrderCommit,
} from "./sqlite-manual-payment-committer";

// 取单订单经恢复中心判「已收款」（P3-1）：最后一笔人工 tender 必须与取单收尾（挂单置 Recalled、删围栏）同事务完成，
// 此前提交器对取单订单一律拒绝，主管结论已落库而订单永远完不成。

const T0 = "2026-09-11T00:00:00.000Z";
const T1 = "2026-09-11T00:01:00.000Z";
const STORE = "STORE-1";
const DEVICE = "DEVICE-1";
const ORDER = "order-recall-manual";
const BINDING = {
  kind: "recalled",
  scope: { storeCode: STORE, deviceCode: DEVICE },
  holdId: "hold-1",
  recallAttemptId: "recall-1",
} as const;

test("真实 SQLite：取单订单的最后一笔人工已收款原子完成订单并把挂单置 Recalled、删除围栏", async () => {
  await withDatabase(async (connection) => {
    await seed(connection);
    const committer = productionCommitter(connection);

    const result = await committer.completeManualPaymentOrder(command());

    assert.deepEqual(result, {
      replayed: false,
      orderGuid: ORDER,
      tenderGuid: "manual-tender-1",
      completed: true,
      signedTenderAmountCents: 900,
    });
    assert.deepEqual(await snapshot(connection), {
      order_state: "PendingSync",
      tenders: 1,
      outbox: 1,
      hold_status: "Recalled",
      hold_recalled_at: T1,
      fences: 0,
      recall_audits: 1,
    });
    // 取单审计带主管身份与门店 / 终端，来源与卡通道最后一笔 tender 的取单审计同口径。
    const audit = await connection.getFirst<{ payload_json: string; correlation_id: string; order_guid: string }>(
      "SELECT payload_json, correlation_id, order_guid FROM audit_events WHERE event_type = 'ORDER_RECALL'",
    );
    assert.equal(audit?.correlation_id, "hold-1");
    assert.equal(audit?.order_guid, ORDER);
    assert.deepEqual(JSON.parse(audit?.payload_json ?? "{}"), {
      source: "pos-handheld",
      action: "recall",
      result: "completed",
      storeCode: STORE,
      deviceCode: DEVICE,
      cashierId: "supervisor-1",
      cashierName: "Supervisor",
      userGuid: "supervisor-user-1",
    });
    // 重放只复用结果，不再动取单状态。
    assert.equal((await committer.completeManualPaymentOrder(command())).replayed, true);
    assert.equal((await snapshot(connection)).recall_audits, 1);
  });
});

test("真实 SQLite：取单订单的部分人工已收款只记 tender，不提前动挂单与围栏，最后一笔才收尾", async () => {
  await withDatabase(async (connection) => {
    await seed(connection, { amountCents: 400, verifiedAmountCents: 400 });
    const committer = productionCommitter(connection);

    const partial = await committer.completeManualPaymentOrder(command());
    assert.equal(partial.completed, false);
    assert.deepEqual(await snapshot(connection), {
      order_state: "Completing",
      tenders: 1,
      outbox: 0,
      hold_status: "Recalling",
      hold_recalled_at: null,
      fences: 1,
      recall_audits: 0,
    });
  });
});

test("真实 SQLite：围栏与取单绑定不一致时整事务回滚，案件保持可重试", async () => {
  await withDatabase(async (connection) => {
    // 草稿指纹里的取单绑定指向另一次取单，而本机围栏 / 挂单是 recall-1：不一致即拒绝。
    await seed(connection, { bindingRecallAttemptId: "recall-other" });
    const committer = productionCommitter(connection);

    await assert.rejects(
      () => committer.completeManualPaymentOrder(command()),
      /does not match the active recall fence/,
    );
    assert.deepEqual(await snapshot(connection), {
      order_state: "Draft",
      tenders: 0,
      outbox: 0,
      hold_status: "Recalling",
      hold_recalled_at: null,
      fences: 1,
      recall_audits: 0,
    });
    assert.equal(await count(connection, "SELECT COUNT(*) AS count FROM manual_payment_tender_bindings"), 0);
  });
});

test("真实 SQLite：取单收尾写库中途失败时整事务回滚，不留下半截订单", async () => {
  await withDatabase(async (connection, database) => {
    await seed(connection);
    const committer = productionCommitter(connection);
    // 让删除围栏这一步失败（模拟围栏被并发改动）：此前已写的 tender / 订单状态 / 挂单状态都必须一起回滚。
    database.failFenceDelete = true;

    await assert.rejects(
      () => committer.completeManualPaymentOrder(command()),
      /Recall fence changed before approved payment completion/,
    );
    assert.deepEqual(await snapshot(connection), {
      order_state: "Draft",
      tenders: 0,
      outbox: 0,
      hold_status: "Recalling",
      hold_recalled_at: null,
      fences: 1,
      recall_audits: 0,
    });

    // 故障消失后同一命令可以重试成功（案件仍是 manual-paid）。
    database.failFenceDelete = false;
    assert.equal((await committer.completeManualPaymentOrder(command())).completed, true);
    assert.equal((await snapshot(connection)).hold_status, "Recalled");
  });
});

test("真实 SQLite：没有接入取单收尾的提交器、或取单绑定不属于本终端时仍 fail-closed", async () => {
  await withDatabase(async (connection) => {
    await seed(connection);
    const withoutPort = new SqliteManualPaymentOrderCommitter(connection, () => T1);
    await assert.rejects(() => withoutPort.completeManualPaymentOrder(command()), /RECALL_UNSUPPORTED/);
    assert.equal((await snapshot(connection)).tenders, 0);
  });
  await withDatabase(async (connection) => {
    // 取单绑定里的终端与订单所在终端不一致：按不支持处理，不去收尾别的终端的取单。
    await seed(connection, { bindingDeviceCode: "DEVICE-OTHER" });
    await assert.rejects(
      () => productionCommitter(connection).completeManualPaymentOrder(command()),
      /RECALL_UNSUPPORTED/,
    );
    assert.equal((await snapshot(connection)).tenders, 0);
  });
});

test("真实 SQLite：取单订单缺少取单盖章时 fail-closed，不会只完成订单不收尾取单", async () => {
  await withDatabase(async (connection) => {
    await seed(connection);
    const { recallStamp: _omitted, ...withoutStamp } = command();
    await assert.rejects(
      () => productionCommitter(connection).completeManualPaymentOrder(withoutStamp),
      /RECALL_STAMP_REQUIRED/,
    );
    assert.deepEqual(await snapshot(connection), {
      order_state: "Draft",
      tenders: 0,
      outbox: 0,
      hold_status: "Recalling",
      hold_recalled_at: null,
      fences: 1,
      recall_audits: 0,
    });
  });
});

function productionCommitter(connection: SqliteConnectionPort): SqliteManualPaymentOrderCommitter {
  return new SqliteManualPaymentOrderCommitter(connection, () => T1, createManualPaymentRecallPort());
}

function command(): ManualPaymentOrderCommit {
  return {
    recordId: "record-1",
    actionId: "manual-action-1",
    orderGuid: ORDER,
    attemptId: "attempt-1",
    storeCode: STORE,
    deviceCode: DEVICE,
    authorizationId: "supervisor-auth-1",
    tenderGuid: "manual-tender-1",
    completionAuditEvent: {
      eventId: "manual-complete-audit",
      eventType: "PAYMENT_COMPLETE",
      occurredAtIso: T1,
      orderGuid: ORDER,
      correlationId: "manual-action-1",
      payload: { action: "manual-payment-complete", authorizationId: "supervisor-auth-1" },
    },
    outbox: {
      messageId: "manual-outbox-1",
      aggregateId: ORDER,
      kind: "order-sync",
      payloadJson: JSON.stringify({ orderGuid: ORDER }),
      nextAttemptAtIso: T1,
    },
    recallStamp: {
      eventId: "manual-recall-audit",
      recalledAtIso: T1,
      actor: {
        cashierId: "supervisor-1",
        cashierName: "Supervisor",
        userGuid: "supervisor-user-1",
      },
    },
  };
}

async function snapshot(connection: SqliteConnectionPort): Promise<Record<string, unknown>> {
  return { ...(await connection.getFirst<Record<string, unknown>>(
    `SELECT
      (SELECT state FROM local_orders WHERE order_guid = '${ORDER}') AS order_state,
      (SELECT COUNT(*) FROM order_tenders WHERE order_guid = '${ORDER}') AS tenders,
      (SELECT COUNT(*) FROM outbox_messages WHERE aggregate_id = '${ORDER}') AS outbox,
      (SELECT status FROM held_order_records WHERE hold_id = 'hold-1') AS hold_status,
      (SELECT recalled_at_iso FROM held_order_records WHERE hold_id = 'hold-1') AS hold_recalled_at,
      (SELECT COUNT(*) FROM terminal_cart_fences) AS fences,
      (SELECT COUNT(*) FROM audit_events WHERE event_type = 'ORDER_RECALL') AS recall_audits`,
  )) };
}

async function count(connection: SqliteConnectionPort, sql: string): Promise<number> {
  return Number((await connection.getFirst<{ count: unknown }>(sql))?.count);
}

/**
 * 种子：一张被取回的挂单（Recalling + RecallActive 围栏）、对应的 Draft 订单与草稿绑定，
 * 以及主管已判「已收款」的恢复中心案件（manual-paid，等待提交 tender）。
 */
async function seed(connection: SqliteConnectionPort, overrides: Partial<{
  amountCents: number;
  verifiedAmountCents: number;
  bindingRecallAttemptId: string;
  bindingDeviceCode: string;
}> = {}): Promise<void> {
  const amountCents = overrides.amountCents ?? 900;
  const verifiedAmountCents = overrides.verifiedAmountCents ?? amountCents;
  await connection.run(
    `INSERT INTO held_order_records (
      hold_id, local_sequence, store_code, device_code, held_by_cashier_id, held_by_cashier_name,
      status, payload_version, payload_ciphertext, item_count, subtotal_cents, discount_cents,
      actual_amount_cents, recalling_at_iso, recall_attempt_id, recalling_cashier_id,
      recalling_cashier_name, recalled_at_iso, held_at_iso, created_at_iso, updated_at_iso
    ) VALUES ('hold-1', 1, ?, ?, 'cashier-1', 'Cashier', 'Recalling', 1, ?, 1, 900, 0, 900,
      ?, 'recall-1', 'cashier-2', 'Cashier Two', NULL, ?, ?, ?)`,
    [STORE, DEVICE, Uint8Array.of(1), T0, T0, T0, T0],
  );
  await connection.run(
    `INSERT INTO terminal_cart_fences (
      store_code, device_code, kind, hold_id, recall_attempt_id, bound_order_guid, created_at_iso
    ) VALUES (?, ?, 'RecallActive', 'hold-1', 'recall-1', NULL, ?)`,
    [STORE, DEVICE, T0],
  );
  await connection.run(
    `INSERT INTO local_orders (
      order_guid, local_sequence, store_code, device_code, cashier_id, cashier_name,
      sold_at_iso, state, total_cents, discount_cents, actual_amount_cents,
      original_order_guid, created_at_iso, updated_at_iso
    ) VALUES (?, 1, ?, ?, 'cashier-2', 'Cashier Two', ?, 'Draft', 900, 0, 900, NULL, ?, ?)`,
    [ORDER, STORE, DEVICE, T0, T0, T0],
  );
  const fingerprint = JSON.stringify({
    version: 2,
    identity: { storeCode: STORE, deviceCode: DEVICE, cashierId: "cashier-2", cashierName: "Cashier Two" },
    cart: {}, pricingState: {}, originalOrderGuid: null,
    recallBinding: {
      ...BINDING,
      scope: { storeCode: STORE, deviceCode: overrides.bindingDeviceCode ?? DEVICE },
      recallAttemptId: overrides.bindingRecallAttemptId ?? BINDING.recallAttemptId,
    },
  });
  await connection.run(
    `INSERT INTO payment_order_draft_bindings (
      draft_id, request_fingerprint, pricing_state_json, order_guid, store_code,
      device_code, state, abandon_action_id, abandon_audit_event_id, abandoned_at_iso,
      close_action_id, close_attempt_id, close_audit_event_id, closed_at_iso, created_at_iso
    ) VALUES (?, ?, '{}', ?, ?, ?, 'Active', NULL, NULL, NULL, NULL, NULL, NULL, NULL, ?)`,
    [`draft-${ORDER}`, fingerprint, ORDER, STORE, DEVICE, T0],
  );
  await connection.run(
    `INSERT INTO payment_attempts (
      attempt_id, idempotency_key, order_guid, provider, operation, amount_cents,
      state, checkout_id, payment_id, session_id, txn_ref, rfn,
      provider_payload_ciphertext, provider_receipt_ciphertext, provider_response_code,
      created_at_iso, updated_at_iso, last_error_code
    ) VALUES ('attempt-1', 'idem-attempt-1', ?, 'linkly-cloud', 'purchase', ?, 'Unknown', NULL, NULL, NULL,
      NULL, NULL, NULL, NULL, NULL, ?, ?, NULL)`,
    [ORDER, amountCents, T0, T0],
  );
  await connection.run(
    `INSERT INTO payment_recovery_cases (
      record_id, order_guid, attempt_id, store_code, device_code, state, is_parked,
      park_action_id, opened_at_iso, updated_at_iso
    ) VALUES ('record-1', ?, 'attempt-1', ?, ?, 'manual-paid', 1, 'park-record-1', ?, ?)`,
    [ORDER, STORE, DEVICE, T0, T1],
  );
  const supervisorActorJson = JSON.stringify({
    requestingCashierId: "supervisor-1",
    requestingCashierName: "Supervisor",
    requestingUserGuid: "supervisor-user-1",
  });
  const requestingActorJson = JSON.stringify({
    requestingCashierId: "cashier-2",
    requestingCashierName: "Cashier Two",
    requestingUserGuid: "cashier-user-2",
  });
  await connection.run(
    `INSERT INTO payment_recovery_reconciliations (
      reconciliation_id, record_id, order_guid, attempt_id, provider,
      observed_state, observed_at_iso
    ) VALUES ('reconcile-manual-action-1', 'record-1', ?, 'attempt-1', 'linkly-cloud', 'Unknown', ?)`,
    [ORDER, T1],
  );
  await connection.run(
    `INSERT INTO payment_recovery_authorizations (
      authorization_id, record_id, order_guid, attempt_id, action_id, finding,
      requesting_actor_json, supervisor_actor_json, created_at_iso
    ) VALUES ('supervisor-auth-1', 'record-1', ?, 'attempt-1', 'manual-action-1', 'paid', ?, ?, ?)`,
    [ORDER, requestingActorJson, supervisorActorJson, T1],
  );
  await connection.run(
    `INSERT INTO payment_recovery_actions (
      action_id, record_id, request_signature, finding, verified_amount_cents,
      evidence_reference, note, authorization_id, supervisor_actor_json,
      requesting_actor_json, reconciliation_id, attempt_state_snapshot, created_at_iso
    ) VALUES ('manual-action-1', 'record-1', ?, 'paid', ?, 'receipt-1', 'Verified on terminal',
      'supervisor-auth-1', ?, ?, 'reconcile-manual-action-1', 'Unknown', ?)`,
    [JSON.stringify(["record-1", "paid", verifiedAmountCents]), verifiedAmountCents,
      supervisorActorJson, requestingActorJson, T1],
  );
}

async function withDatabase(
  run: (connection: SqliteConnectionPort, control: FaultControl) => Promise<void>,
): Promise<void> {
  const database = new DatabaseSync(":memory:");
  database.exec("PRAGMA foreign_keys = ON");
  const connection = new TestConnection(database);
  try {
    await applyMigrations(connection, () => T0);
    await run(connection, connection);
  } finally {
    database.close();
  }
}

type FaultControl = { failFenceDelete: boolean };

class TestConnection implements SqliteConnectionPort, FaultControl {
  public failFenceDelete = false;

  public constructor(private readonly database: DatabaseSync) {}

  public async exec(sql: string): Promise<void> { this.database.exec(sql); }

  public async run(sql: string, parameters: readonly SqlValue[] = []): Promise<SqlRunResult> {
    // 模拟取单围栏在收尾过程中被并发改动：删除围栏这一步 0 行受影响，提交器随之抛错回滚。
    if (
      this.failFenceDelete &&
      sql.includes("DELETE FROM terminal_cart_fences") &&
      sql.includes("kind = 'RecallActive'")
    ) {
      return { changes: 0, lastInsertRowId: 0 };
    }
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
