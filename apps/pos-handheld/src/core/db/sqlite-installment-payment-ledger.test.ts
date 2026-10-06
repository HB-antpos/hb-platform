import assert from "node:assert/strict";
import { DatabaseSync, type SQLInputValue } from "node:sqlite";
import test from "node:test";

import type {
  InstallmentApprovedPaymentMaterial,
  InstallmentOriginalTenderEvidence,
  InstallmentProviderAttemptPlan,
  InstallmentProviderAttemptRecord,
} from "../runtime/production-installment-payment-adapter";
import type { InstallmentProtectedProvenanceImport } from "../runtime/production-installment-refund-provenance";
import type { PersistedInstallmentAction } from "../runtime/production-installment-runtime";

import { applyMigrations, POS_DATABASE_MIGRATIONS } from "./migrations";
import { PosDatabase } from "./pos-database";
import { SqliteInstallmentActionStore } from "./sqlite-installment-action-store";
import { SqliteInstallmentPaymentPersistenceFacade } from "./sqlite-installment-payment-persistence";
import { SqliteInstallmentProviderAttemptStore } from "./sqlite-installment-provider-attempt-store";
import { SqliteInstallmentRefundProvenanceVault } from "./sqlite-installment-refund-provenance-vault";
import {
  SqliteInstallmentVoucherContext,
  SqliteInstallmentVoucherIntentVault,
  SqliteInstallmentVoucherMaterialStore,
  SqliteInstallmentVoucherProtectedTokenStore,
} from "./sqlite-installment-voucher-persistence";
import type { SensitivePayloadEncryptor } from "./sqlite-repositories";
import type {
  SqliteConnectionPort,
  SqliteDriverPort,
  SqlRunResult,
  SqlValue,
} from "@hb/pos-db/core/db/types";

const NOW = "2026-07-29T01:00:00.000Z";
const ACTION_ID = "10000000-0000-4000-8000-000000000001";
const INSTALLMENT_GUID = "20000000-0000-4000-8000-000000000001";
const PAYMENT_GUID = "30000000-0000-4000-8000-000000000001";
const STORE_CODE = "STORE-1";
const DEVICE_CODE = "IPAD-1";
const CASHIER_ID = "cashier-1";
const ATTEMPT_ID = "40000000-0000-4000-8000-000000000001";
const EVIDENCE_ID = "50000000-0000-4000-8000-000000000001";
const REFUND_PAYMENT_GUID =
  "30000000-0000-4000-8000-000000000010";

test("M22 原子建立独立分期支付、券和退款来源账本，失败不推进版本", async () => {
  await withDatabase(async (connection) => {
    const throughM21 = POS_DATABASE_MIGRATIONS.filter(
      (migration) => migration.version <= 21,
    );
    await applyMigrations(connection, () => NOW, throughM21);
    assert.equal(await schemaVersion(connection), 21);

    const m22 = POS_DATABASE_MIGRATIONS.find(
      (migration) => migration.version === 22,
    );
    assert.ok(m22);

    await assert.rejects(
      () =>
        applyMigrations(connection, () => NOW, [
          ...throughM21,
          {
            ...m22,
            sql: `${m22.sql}\nCREATE TABL invalid_m22;`,
          },
        ]),
      /syntax|near/i,
    );
    assert.equal(await schemaVersion(connection), 21);
    assert.equal(
      await tableExists(connection, "installment_provider_attempts"),
      false,
    );

    await applyMigrations(connection, () => NOW);
    assert.equal(
      await schemaVersion(connection),
      POS_DATABASE_MIGRATIONS.at(-1)?.version,
    );
    for (const tableName of [
      "installment_voucher_intents",
      "installment_provider_plans",
      "installment_provider_attempts",
      "installment_cash_settlements",
      "installment_approved_materials",
      "installment_original_tender_evidence",
      "installment_refund_provenance_snapshots",
      "installment_refund_provenance_items",
      "installment_voucher_protected_states",
    ]) {
      assert.equal(await tableExists(connection, tableName), true, tableName);
    }
    for (const triggerName of [
      "trg_installment_provider_attempts_update_guard",
      "trg_installment_provider_attempts_no_delete",
      "trg_installment_cash_settlements_update_guard",
      "trg_installment_cash_settlements_no_delete",
      "trg_installment_voucher_intents_immutable",
      "trg_installment_voucher_intents_no_delete",
    ]) {
      assert.equal(
        await triggerExists(connection, triggerName),
        true,
        triggerName,
      );
    }
  });
});

test("voucher intent 按完整 action/terminal/payment 绑定，secret 仅入二次密文且冲突拒绝", async () => {
  await withMigratedDatabase(async (connection) => {
    const encryptor = new RecordingEncryptor();
    const vault = new SqliteInstallmentVoucherIntentVault(
      connection,
      encryptor,
      () => NOW,
    );
    const intent = voucherIntent();

    await vault.stage(intent);
    await vault.stage(intent);
    assert.equal(encryptor.encryptedPlaintexts.length, 1);
    assert.deepEqual(await vault.load(ACTION_ID), intent);

    const row = await connection.getFirst<Record<string, unknown>>(
      "SELECT * FROM installment_voucher_intents WHERE action_id = ?",
      [ACTION_ID],
    );
    assert.ok(row);
    assert.ok(row.intent_ciphertext instanceof Uint8Array);
    const plainColumns = JSON.stringify(row);
    assert.equal(plainColumns.includes("PRIVATE-VOUCHER-CODE"), false);
    assert.equal(plainColumns.includes("PRIVATE-LOCK-TOKEN"), false);
    const encryptedEnvelope = encryptor.encryptedPlaintexts[0] ?? "";
    assert.equal(encryptedEnvelope.includes("PRIVATE-VOUCHER-CODE"), true);
    assert.equal(encryptedEnvelope.includes("PRIVATE-LOCK-TOKEN"), false);

    for (const conflict of [
      voucherIntent({ voucherReference: "OTHER-CODE" }),
      voucherIntent({ voucherReservationToken: "PRIVATE-LOCK-TOKEN" }),
      voucherIntent({ deviceCode: "IPAD-OTHER" }),
      voucherIntent({ paymentGuid: "30000000-0000-4000-8000-000000000002" }),
      voucherIntent({ cashierId: "cashier-2" }),
      voucherIntent({ amountCents: 2_001 }),
    ]) {
      await assert.rejects(() => vault.stage(conflict), /conflict|bound|intent/i);
    }
    assert.equal(
      await scalar(
        connection,
        "SELECT COUNT(*) AS count FROM installment_voucher_intents",
      ),
      1,
    );

    await assert.rejects(
      () =>
        connection.run(
          "UPDATE installment_voucher_intents SET store_code = 'OTHER' WHERE action_id = ?",
          [ACTION_ID],
        ),
      /IMMUTABLE/i,
    );
    await assert.rejects(
      () =>
        connection.run(
          "DELETE FROM installment_voucher_intents WHERE action_id = ?",
          [ACTION_ID],
        ),
      /DELETE_FORBIDDEN/i,
    );
  });
});

test("provider plan 按 action 原子 bind-or-get，identity 与 protected payload 不可改删", async () => {
  await withMigratedDatabase(async (connection) => {
    const encryptor = new RecordingEncryptor();
    const actionStore = new SqliteInstallmentActionStore(
      connection,
      encryptor,
      () => NOW,
    );
    const action = createAction();
    await actionStore.createIfNone(action);
    const providerPending = await actionStore.transition({
      actionId: ACTION_ID,
      expectedState: "Created",
      nextState: "ProviderPending",
      terminal: { storeCode: STORE_CODE, deviceCode: DEVICE_CODE },
    });

    const store = new SqliteInstallmentProviderAttemptStore(
      connection,
      encryptor,
      () => NOW,
    );
    assert.deepEqual(await store.loadAction(ACTION_ID), providerPending);
    const plan = purchasePlan();
    assert.deepEqual(await store.bindPlanOrGet(plan), plan);
    encryptor.failEncryption = true;
    assert.deepEqual(await store.bindPlanOrGet(plan), plan);
    encryptor.failEncryption = false;
    assert.deepEqual(await store.loadPlan(ACTION_ID), plan);

    const row = await connection.getFirst<Record<string, unknown>>(
      "SELECT * FROM installment_provider_attempts WHERE attempt_id = ?",
      [ATTEMPT_ID],
    );
    assert.ok(row);
    assert.ok(row.protected_payload_ciphertext instanceof Uint8Array);
    assert.equal(JSON.stringify(row).includes("SQ-CHECKOUT-SECRET"), false);
    assert.equal(
      encryptor.encryptedPlaintexts.some((plain) =>
        plain.includes("SQ-CHECKOUT-SECRET"),
      ),
      true,
    );

    await assert.rejects(
      () =>
        store.bindPlanOrGet(
          purchasePlan({
            paymentGuid: "30000000-0000-4000-8000-000000000099",
          }),
        ),
      /conflict|plan/i,
    );
    await assert.rejects(
      () =>
        connection.run(
          "UPDATE installment_provider_attempts SET payment_guid = 'OTHER' WHERE attempt_id = ?",
          [ATTEMPT_ID],
        ),
      /UPDATE_INVALID/i,
    );
    await assert.rejects(
      () =>
        connection.run(
          "DELETE FROM installment_provider_attempts WHERE attempt_id = ?",
          [ATTEMPT_ID],
        ),
      /DELETE_FORBIDDEN/i,
    );
  });
});

test("attempt 仅按合法状态与完整 expected CAS，Approved material 和原付款证据同事务提交", async () => {
  await withMigratedDatabase(async (connection) => {
    const encryptor = new RecordingEncryptor();
    const store = await createPurchaseStore(connection, encryptor);
    const created = purchasePlan().attempts[0]!;
    const submitted = withAttempt(created, {
      state: "Submitted",
      updatedAtIso: "2026-07-29T01:00:00.001Z",
    });
    assert.equal(
      await store.compareAndUpdateAttempt({
        expected: created,
        nextAttempt: submitted.attempt,
      }),
      true,
    );
    assert.equal(
      await store.compareAndUpdateAttempt({
        expected: created,
        nextAttempt: withAttempt(created, {
          state: "Cancelled",
          updatedAtIso: "2026-07-29T01:00:00.002Z",
        }).attempt,
      }),
      false,
    );

    const approved = withAttempt(submitted, {
      state: "Approved",
      updatedAtIso: "2026-07-29T01:00:00.003Z",
      receiptText: "PRIVATE RECEIPT",
      references: {
        ...submitted.attempt.references,
        paymentId: "SQ-PAYMENT-PRIVATE",
      },
    });
    const material = cardMaterial();
    await assert.rejects(
      () =>
        store.compareAndUpdateAttempt({
          expected: submitted,
          nextAttempt: approved.attempt,
          approvedMaterial: {
            ...material,
            evidence: { ...material.evidence, amountCents: 1_999 },
          },
        }),
      /material|amount|evidence/i,
    );
    assert.equal(
      (await store.loadPlan(ACTION_ID))?.attempts[0]?.attempt.state,
      "Submitted",
    );

    encryptor.failEncryption = true;
    await assert.rejects(
      () =>
        store.compareAndUpdateAttempt({
          expected: submitted,
          nextAttempt: approved.attempt,
          approvedMaterial: material,
        }),
      /TEST_ENCRYPTION_FAILURE/,
    );
    encryptor.failEncryption = false;
    assert.equal(
      (await store.loadPlan(ACTION_ID))?.attempts[0]?.attempt.state,
      "Submitted",
    );

    assert.equal(
      await store.compareAndUpdateAttempt({
        expected: submitted,
        nextAttempt: approved.attempt,
        approvedMaterial: material,
      }),
      true,
    );
    assert.deepEqual(await store.loadApprovedMaterial(ATTEMPT_ID), material);
    assert.deepEqual(
      (await store.loadPlan(ACTION_ID))?.attempts[0],
      approved,
    );

    const evidence = await connection.getFirst<Record<string, unknown>>(
      `SELECT * FROM installment_original_tender_evidence
       WHERE evidence_id = ?`,
      [EVIDENCE_ID],
    );
    assert.ok(evidence);
    assert.equal(evidence.origin_action_id, ACTION_ID);
    assert.equal(evidence.payment_guid, PAYMENT_GUID);
    assert.equal(evidence.source_attempt_id, ATTEMPT_ID);
    assert.ok(evidence.protected_payload_ciphertext instanceof Uint8Array);
    assert.equal(JSON.stringify(evidence).includes("SQ-PAYMENT-PRIVATE"), false);
    assert.equal(
      encryptor.encryptedPlaintexts.some((plain) =>
        plain.includes("SQ-PAYMENT-PRIVATE"),
      ),
      true,
    );

    await assert.rejects(
      () =>
        connection.run(
          "DELETE FROM installment_approved_materials WHERE attempt_id = ?",
          [ATTEMPT_ID],
        ),
      /DELETE_FORBIDDEN/i,
    );
    await assert.rejects(
      () =>
        connection.run(
          "DELETE FROM installment_original_tender_evidence WHERE evidence_id = ?",
          [EVIDENCE_ID],
        ),
      /DELETE_FORBIDDEN/i,
    );
  });
});

test("Linkly 分期 ACK 只在本地完成后写 marker，环境冻结并拒绝重复标记", async () => {
  await withMigratedDatabase(async (connection) => {
    const encryptor = new RecordingEncryptor();
    const actions = new SqliteInstallmentActionStore(connection, encryptor, () => NOW);
    await actions.createIfNone(createAction());
    await actions.transition({ actionId: ACTION_ID, expectedState: "Created", nextState: "ProviderPending", terminal: { storeCode: STORE_CODE, deviceCode: DEVICE_CODE } });
    const store = new SqliteInstallmentProviderAttemptStore(connection, encryptor, () => NOW);
    const created = linklyPurchasePlan().attempts[0]!;
    await store.bindPlanOrGet(linklyPurchasePlan());
    const submitted = withAttempt(created, { state: "Submitted", updatedAtIso: "2026-07-29T01:00:00.001Z", references: { ...created.attempt.references, sessionId: "linkly-session-1" } });
    assert.equal(await store.compareAndUpdateAttempt({ expected: created, nextAttempt: submitted.attempt }), true);
    const approved = withAttempt(submitted, { state: "Approved", updatedAtIso: "2026-07-29T01:00:00.002Z", receiptText: "PRIVATE RECEIPT", references: { ...submitted.attempt.references, txnRef: "SQ-TXN", rfn: "LINKLY-RFN" } });
    assert.equal(await store.compareAndUpdateAttempt({ expected: submitted, nextAttempt: approved.attempt, approvedMaterial: cardMaterial("linkly-cloud") }), true);
    assert.equal((await store.get(ATTEMPT_ID))?.providerEnvironment, "production");
    assert.equal(await store.canProviderAcknowledged(approved.attempt), false);
    await actions.transition({ actionId: ACTION_ID, expectedState: "ProviderPending", nextState: "Approved", terminal: { storeCode: STORE_CODE, deviceCode: DEVICE_CODE } });
    await actions.transition({ actionId: ACTION_ID, expectedState: "Approved", nextState: "BackendPending", terminal: { storeCode: STORE_CODE, deviceCode: DEVICE_CODE } });
    await actions.complete({ actionId: ACTION_ID, expectedState: "BackendPending", terminal: { storeCode: STORE_CODE, deviceCode: DEVICE_CODE } });
    assert.equal((await connection.getFirst<{ resolution: unknown }>("SELECT resolution FROM installment_actions WHERE action_id = ?", [ACTION_ID]))?.resolution, "Completed");
    assert.ok(await store.loadApprovedMaterial(ATTEMPT_ID));
    assert.equal(await store.canProviderAcknowledged(approved.attempt), true);
    assert.equal((await connection.getFirst<{ provider_session_id: unknown }>("SELECT provider_session_id FROM installment_provider_attempts WHERE attempt_id = ?", [ATTEMPT_ID]))?.provider_session_id, "linkly-session-1");
    assert.equal((await actions.loadProviderAcknowledgementPending({ storeCode: STORE_CODE, deviceCode: DEVICE_CODE }))?.action.actionId, ACTION_ID);
    const current = await store.get(ATTEMPT_ID);
    assert.ok(current);
    assert.equal(await store.markProviderAcknowledged(current, "2026-07-29T01:01:00.000Z"), true);
    assert.equal((await store.get(ATTEMPT_ID))?.providerAcknowledgedAtIso, "2026-07-29T01:01:00.000Z");
    assert.equal(await actions.loadProviderAcknowledgementPending({ storeCode: STORE_CODE, deviceCode: DEVICE_CODE }), null);
    assert.equal(await store.markProviderAcknowledged(current, "2026-07-29T01:02:00.000Z"), false);
  });
});

test("无 session 的 Linkly Declined 不建立 ACK 冷启动门禁", async () => {
  await withMigratedDatabase(async (connection) => {
    const encryptor = new RecordingEncryptor();
    const actions = new SqliteInstallmentActionStore(connection, encryptor, () => NOW);
    await actions.createIfNone(createAction());
    await actions.transition({ actionId: ACTION_ID, expectedState: "Created", nextState: "ProviderPending", terminal: { storeCode: STORE_CODE, deviceCode: DEVICE_CODE } });
    const store = new SqliteInstallmentProviderAttemptStore(connection, encryptor, () => NOW);
    const created = linklyPurchasePlan().attempts[0]!;
    await store.bindPlanOrGet(linklyPurchasePlan());
    const submitted = withAttempt(created, { state: "Submitted", updatedAtIso: "2026-07-29T01:00:00.001Z" });
    assert.equal(await store.compareAndUpdateAttempt({ expected: created, nextAttempt: submitted.attempt }), true);
    const declined = withAttempt(submitted, { state: "Declined", updatedAtIso: "2026-07-29T01:00:00.002Z" });
    assert.equal(await store.compareAndUpdateAttempt({ expected: submitted, nextAttempt: declined.attempt }), true);
    await actions.decline({ actionId: ACTION_ID, expectedState: "ProviderPending", terminal: { storeCode: STORE_CODE, deviceCode: DEVICE_CODE } });
    assert.equal(await actions.loadProviderAcknowledgementPending({ storeCode: STORE_CODE, deviceCode: DEVICE_CODE }), null);
  });
});

test("旧 Linkly Pending 仅由精确 server session 冻结环境，之后仍恢复原 attempt", async () => {
  await withMigratedDatabase(async (connection) => {
    const encryptor = new RecordingEncryptor();
    const actions = new SqliteInstallmentActionStore(connection, encryptor, () => NOW);
    await actions.createIfNone(createAction());
    await actions.transition({ actionId: ACTION_ID, expectedState: "Created", nextState: "ProviderPending", terminal: { storeCode: STORE_CODE, deviceCode: DEVICE_CODE } });
    const store = new SqliteInstallmentProviderAttemptStore(connection, encryptor, () => NOW);
    const plan = legacyLinklyPurchasePlan();
    const created = plan.attempts[0]!;
    await store.bindPlanOrGet(plan);
    const submitted = withAttempt(created, { state: "Submitted", updatedAtIso: "2026-07-29T01:00:00.001Z", references: { ...created.attempt.references, sessionId: "server-active-session" } });
    assert.equal(await store.compareAndUpdateAttempt({ expected: created, nextAttempt: submitted.attempt }), true);
    const pending = withAttempt(submitted, { state: "Pending", updatedAtIso: "2026-07-29T01:00:00.002Z" });
    assert.equal(await store.compareAndUpdateAttempt({ expected: submitted, nextAttempt: pending.attempt }), true);
    assert.equal(await store.findLegacyLinklyAttemptForSession({ storeCode: STORE_CODE, deviceCode: DEVICE_CODE }, "other-session", created.attempt.idempotencyKey), null);
    const found = await store.findLegacyLinklyAttemptForSession({ storeCode: STORE_CODE, deviceCode: DEVICE_CODE }, "server-active-session", created.attempt.idempotencyKey);
    assert.equal(found?.attemptId, ATTEMPT_ID);
    assert.equal(await store.verifyProviderEnvironment(found!, "production"), true);
    assert.equal((await store.get(ATTEMPT_ID))?.providerEnvironment, "production");
    assert.equal((await store.get(ATTEMPT_ID))?.references.sessionId, "server-active-session");
  });
});

test("M43 升级后的旧 Linkly session 仍可窄查并 CAS 冻结环境", async () => {
  await withDatabase(async (connection) => {
    const encryptor = new RecordingEncryptor();
    await applyMigrations(connection, () => NOW, POS_DATABASE_MIGRATIONS.filter((migration) => migration.version <= 43));
    const actions = new SqliteInstallmentActionStore(connection, encryptor, () => NOW);
    await actions.createIfNone(createAction());
    await actions.transition({ actionId: ACTION_ID, expectedState: "Created", nextState: "ProviderPending", terminal: { storeCode: STORE_CODE, deviceCode: DEVICE_CODE } });
    const record = legacyLinklyPurchasePlan().attempts[0]!;
    const submitted = withAttempt(record, { state: "Submitted", updatedAtIso: "2026-07-29T01:00:00.001Z", references: { ...record.attempt.references, sessionId: "m43-active-session" } });
    await connection.run("INSERT INTO installment_provider_plans (action_id, created_at_iso) VALUES (?, ?)", [ACTION_ID, NOW]);
    await connection.run(`INSERT INTO installment_provider_attempts (attempt_id, action_id, payment_guid, source_payment_guid, original_tender_evidence_id, source_attempt_id, sequence, provider, operation, amount_cents, state, idempotency_key, payload_revision, protected_payload_ciphertext, created_at_iso, updated_at_iso) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, 1, ?, ?, ?)`, [record.attempt.attemptId, record.actionId, record.paymentGuid, record.sourcePaymentGuid, record.originalTenderEvidenceId, record.sourceAttemptId, record.sequence, record.attempt.provider, record.attempt.operation, record.attempt.amount.cents, record.attempt.state, record.attempt.idempotencyKey, await legacyAttemptCiphertext(encryptor, record), record.attempt.createdAtIso, record.attempt.updatedAtIso]);
    await connection.run("UPDATE installment_provider_attempts SET state = ?, protected_payload_ciphertext = ?, updated_at_iso = ? WHERE attempt_id = ?", [submitted.attempt.state, await legacyAttemptCiphertext(encryptor, submitted), submitted.attempt.updatedAtIso, ATTEMPT_ID]);
    const m44 = POS_DATABASE_MIGRATIONS.find((migration) => migration.version === 44);
    assert.ok(m44);
    await applyMigrations(connection, () => NOW, [m44]);
    const store = new SqliteInstallmentProviderAttemptStore(connection, encryptor, () => NOW);
    const found = await store.findLegacyLinklyAttemptForSession({ storeCode: STORE_CODE, deviceCode: DEVICE_CODE }, "m43-active-session", submitted.attempt.idempotencyKey.replaceAll("-", "").toUpperCase());
    assert.equal(found?.attemptId, ATTEMPT_ID);
    assert.equal(await store.verifyProviderEnvironment(found!, "production"), true);
    assert.equal((await store.get(ATTEMPT_ID))?.providerEnvironment, "production");
  });
});

test("cash settlement 全 plan 原子 Prepared→Approved、幂等并同事务生成原付款证据", async () => {
  await withMigratedDatabase(async (connection) => {
    const encryptor = new RecordingEncryptor();
    const actionStore = new SqliteInstallmentActionStore(
      connection,
      encryptor,
      () => NOW,
    );
    await actionStore.createIfNone(createCashAction());
    await actionStore.transition({
      actionId: ACTION_ID,
      expectedState: "Created",
      nextState: "ProviderPending",
      terminal: { storeCode: STORE_CODE, deviceCode: DEVICE_CODE },
    });
    const store = new SqliteInstallmentProviderAttemptStore(
      connection,
      encryptor,
      () => NOW,
    );
    await store.bindPlanOrGet(cashPlan());

    await assert.rejects(
      () =>
        connection.run(
          `UPDATE installment_cash_settlements
           SET state = 'Approved'
           WHERE settlement_id = ?`,
          [ATTEMPT_ID],
        ),
      /EVIDENCE_REQUIRED/i,
    );
    encryptor.failEncryption = true;
    await assert.rejects(
      () => store.approveCashSettlements(ACTION_ID),
      /TEST_ENCRYPTION_FAILURE/,
    );
    encryptor.failEncryption = false;
    assert.equal(
      (await store.loadPlan(ACTION_ID))?.cashSettlements[0]?.state,
      "Prepared",
    );

    const approved = await store.approveCashSettlements(ACTION_ID);
    assert.equal(approved[0]?.state, "Approved");
    assert.deepEqual(
      await store.approveCashSettlements(ACTION_ID),
      approved,
    );
    assert.equal(
      await scalar(
        connection,
        `SELECT COUNT(*) AS count
         FROM installment_original_tender_evidence
         WHERE evidence_id = '${EVIDENCE_ID}'`,
      ),
      1,
    );
  });
});

test("refund provenance 原子导入 Square/Linkly/券受保护来源，公开 snapshot 脱敏并安全 seed", async () => {
  await withMigratedDatabase(async (connection) => {
    const encryptor = new RecordingEncryptor();
    const actionStore = new SqliteInstallmentActionStore(
      connection,
      encryptor,
      () => NOW,
    );
    await actionStore.createIfNone(createCancelAction());
    await actionStore.transition({
      actionId: ACTION_ID,
      expectedState: "Created",
      nextState: "ProviderPending",
      terminal: { storeCode: STORE_CODE, deviceCode: DEVICE_CODE },
    });
    const vault = new SqliteInstallmentRefundProvenanceVault(
      connection,
      encryptor,
      () => NOW,
    );
    const protectedImport = refundImport();
    const snapshot = await vault.importProtected(protectedImport);
    assert.equal(snapshot.complete, true);
    assert.equal(snapshot.tenders.length, 3);
    assert.equal(
      JSON.stringify(snapshot).includes("SQ-PAYMENT-PRIVATE"),
      false,
    );
    assert.equal(
      JSON.stringify(snapshot).includes("LINKLY-RFN-PRIVATE"),
      false,
    );
    assert.equal(
      JSON.stringify(snapshot).includes("VOUCHER-CODE-PRIVATE"),
      false,
    );
    assert.deepEqual(
      await vault.resolve({
        installmentGuid: INSTALLMENT_GUID,
        storeCode: STORE_CODE,
        requestingDeviceCode: DEVICE_CODE,
      }),
      snapshot,
    );
    assert.deepEqual(
      await vault.importProtected(protectedImport),
      snapshot,
    );
    await assert.rejects(
      () =>
        vault.importProtected({
          ...protectedImport,
          tenders: Object.freeze([
            {
              ...protectedImport.tenders[0]!,
              reference: "SQ-PAYMENT-CONFLICT",
            },
            ...protectedImport.tenders.slice(1),
          ]),
        }),
      /conflict|binding|provenance/i,
    );

    const square = snapshot.tenders[0]!;
    const squareAttempt = refundAttempt(square, "square");
    const seededSquare = await vault.seedRefundAttempt({
      evidence: square,
      attempt: squareAttempt,
    });
    assert.equal(
      seededSquare.references.paymentId,
      "SQ-PAYMENT-PRIVATE",
    );
    assert.equal(seededSquare.references.rfn, null);

    const linkly = snapshot.tenders[1]!;
    const seededLinkly = await vault.seedRefundAttempt({
      evidence: linkly,
      attempt: refundAttempt(linkly, "linkly-cloud"),
    });
    assert.equal(
      seededLinkly.references.rfn,
      "LINKLY-RFN-PRIVATE",
    );
    assert.equal(seededLinkly.references.paymentId, null);

    const voucher = snapshot.tenders[2]!;
    const seededVoucher = await vault.seedRefundAttempt({
      evidence: voucher,
      attempt: refundAttempt(voucher, "voucher"),
    });
    assert.deepEqual(seededVoucher.references, emptyReferences());

    const refundPlan = Object.freeze({
      actionId: ACTION_ID,
      attempts: Object.freeze([
        refundRecord(
          square,
          seededSquare,
          0,
          "30000000-0000-4000-8000-000000000021",
        ),
        refundRecord(
          linkly,
          seededLinkly,
          1,
          "30000000-0000-4000-8000-000000000022",
        ),
        refundRecord(
          voucher,
          seededVoucher,
          2,
          "30000000-0000-4000-8000-000000000023",
        ),
      ]),
      cashSettlements: Object.freeze([]),
    });
    const providerStore = new SqliteInstallmentProviderAttemptStore(
      connection,
      encryptor,
      () => NOW,
    );
    assert.deepEqual(
      await providerStore.bindPlanOrGet(refundPlan),
      refundPlan,
    );
    assert.deepEqual(
      await providerStore.loadPlan(ACTION_ID),
      refundPlan,
    );

    const plainRows = JSON.stringify(
      await connection.getAll<Record<string, unknown>>(
        "SELECT * FROM installment_original_tender_evidence",
      ),
    );
    assert.equal(plainRows.includes("SQ-PAYMENT-PRIVATE"), false);
    assert.equal(plainRows.includes("LINKLY-RFN-PRIVATE"), false);
    assert.equal(plainRows.includes("VOUCHER-CODE-PRIVATE"), false);
    assert.equal(
      encryptor.encryptedPlaintexts.some((plain) =>
        plain.includes("SQ-PAYMENT-PRIVATE"),
      ),
      true,
    );

    await assert.rejects(
      () =>
        vault.seedRefundAttempt({
          evidence: {
            ...square,
            sourcePaymentGuid:
              "30000000-0000-4000-8000-000000000099",
          },
          attempt: squareAttempt,
        }),
      /binding|evidence|provenance/i,
    );
    await assert.rejects(
      () =>
        vault.importProtected({
          ...protectedImport,
          requestingDeviceCode: "IPAD-OTHER",
        }),
      /action|scope|found/i,
    );
    await assert.rejects(
      () =>
        connection.run(
          `DELETE FROM installment_refund_provenance_snapshots
           WHERE refund_action_id = ?`,
          [ACTION_ID],
        ),
      /DELETE_FORBIDDEN/i,
    );
  });
});

test("退代金券模式：原付款证据不要求卡引用/卡交易，计划只能由退款券 attempt 组成", async () => {
  // 现金、无卡材料的 Square 刷卡、代金券三笔原付款。
  const voucherModeImport = (): InstallmentProtectedProvenanceImport =>
    Object.freeze({
      installmentGuid: INSTALLMENT_GUID,
      storeCode: STORE_CODE,
      requestingDeviceCode: DEVICE_CODE,
      paidAmountCents: 3_000,
      tenders: Object.freeze([
        protectedTender({
          evidenceId: "hbpos:cash",
          sourceAttemptId: "hbpos:cash-attempt",
          sourcePaymentGuid: "30000000-0000-4000-8000-000000000011",
          method: "cash",
          provider: null,
          reference: null,
        }),
        protectedTender({
          evidenceId: "hbpos:square",
          sourceAttemptId: "hbpos:square-attempt",
          sourcePaymentGuid: "30000000-0000-4000-8000-000000000012",
          method: "card",
          provider: "square",
          reference: null,
          cardTransactions: Object.freeze([]),
        }),
        protectedTender({
          evidenceId: "hbpos:voucher",
          sourceAttemptId: "hbpos:voucher-attempt",
          sourcePaymentGuid: "30000000-0000-4000-8000-000000000013",
          method: "voucher",
          provider: "voucher",
          reference: "VOUCHER-CODE-PRIVATE",
        }),
      ]),
    });

  // 原路退仍严格要求卡材料。
  await withMigratedDatabase(async (connection) => {
    const encryptor = new RecordingEncryptor();
    const actionStore = new SqliteInstallmentActionStore(connection, encryptor, () => NOW);
    await actionStore.createIfNone(createCancelAction("original-route"));
    const vault = new SqliteInstallmentRefundProvenanceVault(connection, encryptor, () => NOW);
    await assert.rejects(
      () => vault.importProtected(voucherModeImport()),
      /material|invalid/i,
    );
  });

  await withMigratedDatabase(async (connection) => {
    const encryptor = new RecordingEncryptor();
    const actionStore = new SqliteInstallmentActionStore(connection, encryptor, () => NOW);
    await actionStore.createIfNone(createCancelAction("voucher"));
    await actionStore.transition({
      actionId: ACTION_ID,
      expectedState: "Created",
      nextState: "ProviderPending",
      terminal: { storeCode: STORE_CODE, deviceCode: DEVICE_CODE },
    });
    const vault = new SqliteInstallmentRefundProvenanceVault(connection, encryptor, () => NOW);
    const snapshot = await vault.importProtected(voucherModeImport());
    assert.equal(snapshot.complete, true);
    assert.deepEqual(
      snapshot.tenders.map((tender) => [tender.method, tender.provider]),
      [["cash", null], ["card", "square"], ["voucher", "voucher"]],
    );
    // 本地快照重读同样按 action 冻结的退代金券模式校验。
    assert.deepEqual(
      await vault.resolve({
        installmentGuid: INSTALLMENT_GUID,
        storeCode: STORE_CODE,
        requestingDeviceCode: DEVICE_CODE,
      }),
      snapshot,
    );

    const voucherPlan = Object.freeze({
      actionId: ACTION_ID,
      attempts: Object.freeze(
        snapshot.tenders.map((tender, index) =>
          refundRecord(
            tender,
            Object.freeze({
              ...refundAttempt(tender, "voucher"),
              attemptId: `refund-voucher-${index}`,
              idempotencyKey: `${ACTION_ID}:refund:${tender.sourcePaymentGuid}`,
            }),
            index,
            `30000000-0000-4000-8000-00000000002${index + 1}`,
          ),
        ),
      ),
      cashSettlements: Object.freeze([]),
    });
    const providerStore = new SqliteInstallmentProviderAttemptStore(connection, encryptor, () => NOW);
    // 退代金券模式不得混入现金结算。
    const cashTender = snapshot.tenders[0]!;
    await assert.rejects(
      () =>
        providerStore.bindPlanOrGet(
          Object.freeze({
            actionId: ACTION_ID,
            attempts: Object.freeze(voucherPlan.attempts.slice(1)),
            cashSettlements: Object.freeze([
              Object.freeze({
                actionId: ACTION_ID,
                settlementId: "30000000-0000-4000-8000-000000000031",
                paymentGuid: "30000000-0000-4000-8000-000000000021",
                sourcePaymentGuid: cashTender.sourcePaymentGuid,
                originalTenderEvidenceId: cashTender.evidenceId,
                sourceAttemptId: cashTender.sourceAttemptId,
                sequence: 0,
                operation: "refund" as const,
                amountCents: cashTender.amountCents,
                idempotencyKey: `${ACTION_ID}:refund:${cashTender.sourcePaymentGuid}`,
                state: "Prepared" as const,
              }),
            ]),
          }),
        ),
      /voucher refund plan|cash/i,
    );
    assert.deepEqual(await providerStore.bindPlanOrGet(voucherPlan), voucherPlan);
    assert.deepEqual(await providerStore.loadPlan(ACTION_ID), voucherPlan);
  });
});

test("分期专用 voucher token/context/material 绑定 provider attempt 与 action scope，secret 全部二次加密", async () => {
  await withMigratedDatabase(async (connection) => {
    const encryptor = new RecordingEncryptor();
    const intentVault = new SqliteInstallmentVoucherIntentVault(
      connection,
      encryptor,
      () => NOW,
    );
    await intentVault.stage(voucherIntent());
    const actionStore = new SqliteInstallmentActionStore(
      connection,
      encryptor,
      () => NOW,
    );
    await actionStore.createIfNone(createVoucherAction());
    const action = await actionStore.transition({
      actionId: ACTION_ID,
      expectedState: "Created",
      nextState: "ProviderPending",
      terminal: { storeCode: STORE_CODE, deviceCode: DEVICE_CODE },
    });
    const providers = new SqliteInstallmentProviderAttemptStore(
      connection,
      encryptor,
      () => NOW,
    );
    const plan = voucherPlan();
    await providers.bindPlanOrGet(plan);
    const record = plan.attempts[0]!;
    const tokens = new SqliteInstallmentVoucherProtectedTokenStore(
      connection,
      encryptor,
      () => "vpr_installment_opaque_reference_0001",
      () => NOW,
    );
    const context = new SqliteInstallmentVoucherContext(
      providers,
      intentVault,
    );
    const materials = new SqliteInstallmentVoucherMaterialStore(
      providers,
      intentVault,
      tokens,
    );

    assert.deepEqual(await context.provide(record.attempt), {
      storeCode: STORE_CODE,
      cashierId: CASHIER_ID,
      voucherCode: "PRIVATE-VOUCHER-CODE",
      refundReason: null,
    });
    await materials.prepare({ action, record });

    const protectedReference = await tokens.save(
      voucherProtectedState({
        phase: "purchase-prepared",
      }),
    );
    assert.equal(
      await tokens.save(
        voucherProtectedState({
          phase: "purchase-prepared",
        }),
      ),
      protectedReference,
    );
    await tokens.save(
      voucherProtectedState({ phase: "lock-submitted" }),
    );
    await tokens.save(
      voucherProtectedState({
        phase: "approved",
        reservationToken: "PRIVATE-LOCK-TOKEN",
        expiresAtIso: "2027-07-29T01:00:00.000Z",
      }),
    );
    const protectedState = await tokens.resolve(protectedReference);
    assert.equal(protectedState?.voucherCode, "PRIVATE-VOUCHER-CODE");
    assert.equal(
      protectedState?.reservationToken,
      "PRIVATE-LOCK-TOKEN",
    );

    const approvedRecord = withAttempt(record, {
      state: "Approved",
      updatedAtIso: "2026-07-29T01:00:00.001Z",
      references: {
        ...record.attempt.references,
        voucherReservationToken: protectedReference,
      },
    });
    assert.deepEqual(
      await materials.resolveApproved({
        action,
        record: approvedRecord,
        protectedReference,
      }),
      {
        reference: "PRIVATE-VOUCHER-CODE",
        reservationToken: "PRIVATE-LOCK-TOKEN",
      },
    );

    const plainRows = JSON.stringify(
      await connection.getAll<Record<string, unknown>>(
        "SELECT * FROM installment_voucher_protected_states",
      ),
    );
    assert.equal(plainRows.includes("PRIVATE-VOUCHER-CODE"), false);
    assert.equal(plainRows.includes("PRIVATE-LOCK-TOKEN"), false);
    await assert.rejects(
      () =>
        connection.run(
          `UPDATE installment_voucher_protected_states
           SET action_id = 'other'
           WHERE protected_reference = ?`,
          [protectedReference],
        ),
      /REBIND_FORBIDDEN/i,
    );
    await assert.rejects(
      () =>
        connection.run(
          `DELETE FROM installment_voucher_protected_states
           WHERE protected_reference = ?`,
          [protectedReference],
        ),
      /DELETE_FORBIDDEN/i,
    );
  });
});

test("voucher refund context 只取冻结 cancel reason，Approved material 不携带 reservation token", async () => {
  await withMigratedDatabase(async (connection) => {
    const encryptor = new RecordingEncryptor();
    const actionStore = new SqliteInstallmentActionStore(
      connection,
      encryptor,
      () => NOW,
    );
    await actionStore.createIfNone(createCancelAction());
    const action = await actionStore.transition({
      actionId: ACTION_ID,
      expectedState: "Created",
      nextState: "ProviderPending",
      terminal: { storeCode: STORE_CODE, deviceCode: DEVICE_CODE },
    });
    const provenance = new SqliteInstallmentRefundProvenanceVault(
      connection,
      encryptor,
      () => NOW,
    );
    const imported = refundImport();
    const voucherOnly = Object.freeze({
      ...imported,
      paidAmountCents: 1_000,
      tenders: Object.freeze([imported.tenders[2]!]),
    });
    const snapshot = await provenance.importProtected(voucherOnly);
    const evidence = snapshot.tenders[0]!;
    const seeded = await provenance.seedRefundAttempt({
      evidence,
      attempt: refundAttempt(evidence, "voucher"),
    });
    const record = refundRecord(
      evidence,
      seeded,
      0,
      REFUND_PAYMENT_GUID,
    );
    const providers = new SqliteInstallmentProviderAttemptStore(
      connection,
      encryptor,
      () => NOW,
    );
    await providers.bindPlanOrGet({
      actionId: ACTION_ID,
      attempts: [record],
      cashSettlements: [],
    });
    const intents = new SqliteInstallmentVoucherIntentVault(
      connection,
      encryptor,
      () => NOW,
    );
    const context = new SqliteInstallmentVoucherContext(
      providers,
      intents,
    );
    assert.deepEqual(await context.provide(record.attempt), {
      storeCode: STORE_CODE,
      cashierId: CASHIER_ID,
      voucherCode: null,
      refundReason: "PRIVATE CANCEL REASON",
    });

    const tokens = new SqliteInstallmentVoucherProtectedTokenStore(
      connection,
      encryptor,
      () => "vpr_installment_refund_reference_0001",
      () => NOW,
    );
    const materials = new SqliteInstallmentVoucherMaterialStore(
      providers,
      intents,
      tokens,
    );
    await materials.prepare({ action, record });
    const protectedReference = await tokens.save(
      voucherRefundProtectedState({
        phase: "refund-submitted",
      }),
    );
    await tokens.save(
      voucherRefundProtectedState({
        phase: "approved",
        voucherCode: "PRIVATE-REFUND-VOUCHER",
        expiresAtIso: "2027-07-29T01:00:00.000Z",
      }),
    );
    const approved = withAttempt(record, {
      state: "Approved",
      updatedAtIso: "2026-07-29T01:00:00.001Z",
      references: {
        ...record.attempt.references,
        voucherReservationToken: protectedReference,
      },
    });
    assert.deepEqual(
      await materials.resolveApproved({
        action,
        record: approved,
        protectedReference,
      }),
      {
        reference: "PRIVATE-REFUND-VOUCHER",
        reservationToken: null,
      },
    );
  });
});

test("PosDatabase 只通过 installmentPaymentPersistence 暴露六个冻结窄成员", async () => {
  const database = await PosDatabase.open({
    databaseName: ":memory:",
    driver: new SystemSqliteDriver(),
    keyProvider: {
      getOrCreateDatabaseKey: async () => "a".repeat(64),
    },
    nowIso: () => NOW,
  });
  try {
    const facade = database.installmentPaymentPersistence(
      new RecordingEncryptor(),
      () => "vpr_installment_facade_reference_0001",
    );
    assert.ok(
      facade instanceof SqliteInstallmentPaymentPersistenceFacade,
    );
    assert.deepEqual(Object.keys(facade), [
      "providerAttempts",
      "voucherIntents",
      "voucherProtectedTokens",
      "voucherContextForAttempt",
      "voucherMaterials",
      "refundProvenance",
    ]);
    assert.equal(typeof facade.voucherContextForAttempt, "function");
    assert.equal("connection" in facade, false);
  } finally {
    await database.close();
  }
});

async function createPurchaseStore(
  connection: SqliteConnectionPort,
  encryptor: RecordingEncryptor,
): Promise<SqliteInstallmentProviderAttemptStore> {
  const actionStore = new SqliteInstallmentActionStore(
    connection,
    encryptor,
    () => NOW,
  );
  await actionStore.createIfNone(createAction());
  await actionStore.transition({
    actionId: ACTION_ID,
    expectedState: "Created",
    nextState: "ProviderPending",
    terminal: { storeCode: STORE_CODE, deviceCode: DEVICE_CODE },
  });
  const store = new SqliteInstallmentProviderAttemptStore(
    connection,
    encryptor,
    () => NOW,
  );
  await store.bindPlanOrGet(purchasePlan());
  return store;
}

function createCashAction(): PersistedInstallmentAction {
  const action = createAction();
  return Object.freeze({
    ...action,
    action: Object.freeze({
      ...action.action,
      method: "cash" as const,
    }),
  });
}

function createCancelAction(
  refundMode?: "original-route" | "voucher",
): PersistedInstallmentAction {
  return Object.freeze({
    action: Object.freeze({
      actionId: ACTION_ID,
      idempotencyKey: ACTION_ID,
      kind: "cancel-refund" as const,
      installmentGuid: INSTALLMENT_GUID,
      paymentGuid: null,
      method: null,
      amountCents: null,
    }),
    command: Object.freeze({
      deviceCode: DEVICE_CODE,
      cashierId: CASHIER_ID,
      cashierName: "Alice",
      kind: "cancel-refund" as const,
      installmentGuid: INSTALLMENT_GUID,
      cancelledAtIso: NOW,
      reason: "PRIVATE CANCEL REASON",
      idempotencyKey: ACTION_ID,
      ...(refundMode
        ? {
            refundPlanFingerprint: `sha256:${"c".repeat(64)}`,
            refundMode,
          }
        : {}),
    }),
    deviceCode: DEVICE_CODE,
    intentFingerprint: "sha256:".concat("b".repeat(64)),
    state: "Created" as const,
    storeCode: STORE_CODE,
  });
}

function createVoucherAction(): PersistedInstallmentAction {
  const action = createAction();
  return Object.freeze({
    ...action,
    action: Object.freeze({
      ...action.action,
      method: "voucher" as const,
    }),
  });
}

function withAttempt(
  record: InstallmentProviderAttemptRecord,
  overrides: Partial<
    Pick<
      InstallmentProviderAttemptRecord["attempt"],
      "state" | "updatedAtIso" | "references" | "receiptText"
    >
  >,
): InstallmentProviderAttemptRecord {
  return Object.freeze({
    ...record,
    attempt: Object.freeze({
      ...record.attempt,
      ...overrides,
      references: Object.freeze(
        overrides.references ?? record.attempt.references,
      ),
    }),
  });
}

function createAction(): PersistedInstallmentAction {
  return Object.freeze({
    action: Object.freeze({
      actionId: ACTION_ID,
      idempotencyKey: ACTION_ID,
      kind: "repayment" as const,
      installmentGuid: INSTALLMENT_GUID,
      paymentGuid: PAYMENT_GUID,
      method: "card" as const,
      amountCents: 2_000,
    }),
    command: Object.freeze({
      deviceCode: DEVICE_CODE,
      cashierId: CASHIER_ID,
      cashierName: "Alice",
      kind: "repayment" as const,
      installmentGuid: INSTALLMENT_GUID,
    }),
    deviceCode: DEVICE_CODE,
    intentFingerprint: "sha256:".concat("a".repeat(64)),
    state: "Created" as const,
    storeCode: STORE_CODE,
  });
}

function purchasePlan(
  overrides: Partial<{
    paymentGuid: string;
    state: InstallmentProviderAttemptRecord["attempt"]["state"];
  }> = {},
): InstallmentProviderAttemptPlan {
  const state = overrides.state ?? "Created";
  return Object.freeze({
    actionId: ACTION_ID,
    attempts: Object.freeze([
      Object.freeze({
        actionId: ACTION_ID,
        paymentGuid: overrides.paymentGuid ?? PAYMENT_GUID,
        sourcePaymentGuid: null,
        originalTenderEvidenceId: EVIDENCE_ID,
        sourceAttemptId: null,
        sequence: 0,
        attempt: Object.freeze({
          attemptId: ATTEMPT_ID,
          idempotencyKey: "60000000-0000-4000-8000-000000000001",
          orderGuid: INSTALLMENT_GUID,
          provider: "square" as const,
          operation: "purchase" as const,
          amount: Object.freeze({ currency: "AUD" as const, cents: 2_000 }),
          state,
          references: Object.freeze({
            checkoutId: "SQ-CHECKOUT-SECRET",
            paymentId: null,
            sessionId: null,
            txnRef: null,
            rfn: null,
            voucherReservationToken: null,
          }),
          createdAtIso: NOW,
          updatedAtIso: NOW,
          lastErrorCode: null,
          receiptText: null,
          responseCode: null,
        }),
      }),
    ]),
    cashSettlements: Object.freeze([]),
  });
}

function linklyPurchasePlan(): InstallmentProviderAttemptPlan {
  const plan = purchasePlan();
  const record = plan.attempts[0]!;
  return Object.freeze({
    ...plan,
    attempts: Object.freeze([Object.freeze({
      ...record,
      attempt: Object.freeze({ ...record.attempt, provider: "linkly-cloud", providerEnvironment: "production" }),
    })]),
  });
}

function legacyLinklyPurchasePlan(): InstallmentProviderAttemptPlan {
  const plan = purchasePlan();
  const record = plan.attempts[0]!;
  return Object.freeze({
    ...plan,
    attempts: Object.freeze([Object.freeze({
      ...record,
      attempt: Object.freeze({ ...record.attempt, provider: "linkly-cloud" }),
    })]),
  });
}

async function legacyAttemptCiphertext(
  encryptor: RecordingEncryptor,
  record: InstallmentProviderAttemptRecord,
): Promise<Uint8Array> {
  return encryptor.encrypt(JSON.stringify({
    format: "hb-pos-installment-provider-attempt-v1",
    aad: { revision: 1, actionId: record.actionId, attemptId: record.attempt.attemptId, paymentGuid: record.paymentGuid, sequence: record.sequence },
    record,
  }));
}

function cashPlan(): InstallmentProviderAttemptPlan {
  return Object.freeze({
    actionId: ACTION_ID,
    attempts: Object.freeze([]),
    cashSettlements: Object.freeze([
      Object.freeze({
        actionId: ACTION_ID,
        settlementId: ATTEMPT_ID,
        paymentGuid: PAYMENT_GUID,
        sourcePaymentGuid: null,
        originalTenderEvidenceId: EVIDENCE_ID,
        sourceAttemptId: null,
        sequence: 0,
        operation: "purchase" as const,
        amountCents: 2_000,
        idempotencyKey: ACTION_ID,
        state: "Prepared" as const,
      }),
    ]),
  });
}

function voucherPlan(): InstallmentProviderAttemptPlan {
  const plan = purchasePlan();
  const record = plan.attempts[0]!;
  return Object.freeze({
    ...plan,
    attempts: Object.freeze([
      Object.freeze({
        ...record,
        attempt: Object.freeze({
          ...record.attempt,
          provider: "voucher" as const,
          references: emptyReferences(),
        }),
      }),
    ]),
  });
}

function voucherProtectedState(
  overrides: Partial<
    Parameters<SqliteInstallmentVoucherProtectedTokenStore["save"]>[0]
  >,
): Parameters<SqliteInstallmentVoucherProtectedTokenStore["save"]>[0] {
  return Object.freeze({
    attemptId: ATTEMPT_ID,
    idempotencyKey: "60000000-0000-4000-8000-000000000001",
    orderGuid: INSTALLMENT_GUID,
    operation: "purchase" as const,
    phase: "purchase-prepared" as const,
    storeCode: STORE_CODE,
    cashierId: CASHIER_ID,
    voucherCode: "PRIVATE-VOUCHER-CODE",
    reservationToken: null,
    amountCents: 2_000,
    expiresAtIso: null,
    reason: null,
    ...overrides,
  });
}

function voucherRefundProtectedState(
  overrides: Partial<
    Parameters<SqliteInstallmentVoucherProtectedTokenStore["save"]>[0]
  >,
): Parameters<SqliteInstallmentVoucherProtectedTokenStore["save"]>[0] {
  return Object.freeze({
    attemptId: "refund-voucher",
    idempotencyKey: "refund-idempotency-voucher",
    orderGuid: INSTALLMENT_GUID,
    operation: "refund" as const,
    phase: "refund-submitted" as const,
    storeCode: STORE_CODE,
    cashierId: CASHIER_ID,
    voucherCode: null,
    reservationToken: null,
    amountCents: -1_000,
    expiresAtIso: null,
    reason: "PRIVATE CANCEL REASON",
    ...overrides,
  });
}

function refundImport(): InstallmentProtectedProvenanceImport {
  return Object.freeze({
    installmentGuid: INSTALLMENT_GUID,
    storeCode: STORE_CODE,
    requestingDeviceCode: DEVICE_CODE,
    paidAmountCents: 3_000,
    tenders: Object.freeze([
      protectedTender({
        evidenceId: "hbpos:square",
        sourceAttemptId: "hbpos:square-attempt",
        sourcePaymentGuid:
          "30000000-0000-4000-8000-000000000011",
        method: "card",
        provider: "square",
        reference: "SQ-PAYMENT-PRIVATE",
        cardTransactions: Object.freeze([
          { processor: "Square", amount: 10 },
        ]),
      }),
      protectedTender({
        evidenceId: "hbpos:linkly",
        sourceAttemptId: "hbpos:linkly-attempt",
        sourcePaymentGuid:
          "30000000-0000-4000-8000-000000000012",
        method: "card",
        provider: "linkly-cloud",
        reference: "LINKLY-RFN-PRIVATE",
        cardTransactions: Object.freeze([
          { processor: "ANZ", amount: 10 },
        ]),
      }),
      protectedTender({
        evidenceId: "hbpos:voucher",
        sourceAttemptId: "hbpos:voucher-attempt",
        sourcePaymentGuid:
          "30000000-0000-4000-8000-000000000013",
        method: "voucher",
        provider: "voucher",
        reference: "VOUCHER-CODE-PRIVATE",
        cardTransactions: Object.freeze([]),
      }),
    ]),
  });
}

function protectedTender(
  overrides: Partial<
    InstallmentProtectedProvenanceImport["tenders"][number]
  >,
): InstallmentProtectedProvenanceImport["tenders"][number] {
  return Object.freeze({
    evidenceId: "hbpos:evidence",
    sourceAttemptId: "hbpos:attempt",
    sourcePaymentGuid:
      "30000000-0000-4000-8000-000000000011",
    installmentGuid: INSTALLMENT_GUID,
    method: "card" as const,
    amountCents: 1_000,
    provider: "square" as const,
    provenance: "hbpos-protected-details" as const,
    reference: "PRIVATE-REFERENCE",
    cardTransactions: Object.freeze([]),
    ...overrides,
  });
}

function refundAttempt(
  evidence: InstallmentOriginalTenderEvidence,
  provider: "square" | "linkly-cloud" | "voucher",
): InstallmentProviderAttemptRecord["attempt"] {
  return Object.freeze({
    attemptId: `refund-${provider}`,
    idempotencyKey: `refund-idempotency-${provider}`,
    orderGuid: INSTALLMENT_GUID,
    provider,
    operation: "refund" as const,
    amount: Object.freeze({
      currency: "AUD" as const,
      cents: -evidence.amountCents,
    }),
    state: "Created" as const,
    references: emptyReferences(),
    createdAtIso: NOW,
    updatedAtIso: NOW,
    lastErrorCode: null,
    receiptText: null,
    responseCode: null,
  });
}

function refundRecord(
  evidence: InstallmentOriginalTenderEvidence,
  attempt: InstallmentProviderAttemptRecord["attempt"],
  sequence: number,
  paymentGuid: string,
): InstallmentProviderAttemptRecord {
  return Object.freeze({
    actionId: ACTION_ID,
    paymentGuid,
    sourcePaymentGuid: evidence.sourcePaymentGuid,
    originalTenderEvidenceId: evidence.evidenceId,
    sourceAttemptId: evidence.sourceAttemptId,
    sequence,
    attempt,
  });
}

function emptyReferences(): InstallmentProviderAttemptRecord["attempt"]["references"] {
  return Object.freeze({
    checkoutId: null,
    paymentId: null,
    sessionId: null,
    txnRef: null,
    rfn: null,
    voucherReservationToken: null,
  });
}

function voucherIntent(
  overrides: Partial<
    Parameters<SqliteInstallmentVoucherIntentVault["stage"]>[0]
  > = {},
): Parameters<SqliteInstallmentVoucherIntentVault["stage"]>[0] {
  return Object.freeze({
    actionId: ACTION_ID,
    installmentGuid: INSTALLMENT_GUID,
    paymentGuid: PAYMENT_GUID,
    storeCode: STORE_CODE,
    deviceCode: DEVICE_CODE,
    cashierId: CASHIER_ID,
    amountCents: 2_000,
    voucherReference: "PRIVATE-VOUCHER-CODE",
    voucherReservationToken: null,
    ...overrides,
  });
}

function cardMaterial(provider: "square" | "linkly-cloud" = "square"): Extract<
  InstallmentApprovedPaymentMaterial,
  { kind: "card" }
> {
  return Object.freeze({
    kind: "card" as const,
    evidence: Object.freeze({
      version: 1 as const,
      provider,
      operation: "purchase" as const,
      processor: provider === "linkly-cloud" ? "ANZ" as const : "Square" as const,
      txnRef: "SQ-TXN",
      authCode: "AUTH",
      cardType: "VISA",
      cardBin: 411111,
      maskedCardNumber: "****1111",
      merchantId: "MERCHANT",
      responseCode: "00",
      responseText: "APPROVED",
      stan: null,
      bankDateTimeIso: NOW,
      amountCents: 2_000,
      refundReference: "SQ-REFUND",
    }),
    receiptText: "PRIVATE RECEIPT",
  });
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

async function scalar(
  connection: SqliteConnectionPort,
  sql: string,
): Promise<number> {
  return Number(
    (await connection.getFirst<{ count: unknown }>(sql))?.count,
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

class SystemSqliteConnection implements SqliteConnectionPort {
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

  public async withExclusiveTransaction<T>(
    operation: (transaction: SqliteConnectionPort) => Promise<T>,
  ): Promise<T> {
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
  }

  public async close(): Promise<void> {
    this.database.close();
  }
}

class SystemSqliteDriver implements SqliteDriverPort {
  public async open(_databaseName: string): Promise<SqliteConnectionPort> {
    return new SystemSqliteConnection(new DatabaseSync(":memory:"));
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

async function withDatabase(
  operation: (connection: SqliteConnectionPort) => Promise<void>,
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

async function withMigratedDatabase(
  operation: (connection: SqliteConnectionPort) => Promise<void>,
): Promise<void> {
  await withDatabase(async (connection) => {
    await applyMigrations(connection, () => NOW);
    await operation(connection);
  });
}
