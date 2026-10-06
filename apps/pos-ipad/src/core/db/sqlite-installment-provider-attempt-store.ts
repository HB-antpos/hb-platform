import {
  canTransitionPaymentAttempt,
  normalizeCardSyncEvidence,
  type PaymentAttempt,
  type PaymentAttemptState,
  type PaymentProviderReferences,
} from "../contracts";
import type {
  InstallmentApprovedPaymentMaterial,
  InstallmentCashSettlement,
  InstallmentProviderAttemptPlan,
  InstallmentProviderAttemptRecord,
  InstallmentProviderAttemptStorePort,
} from "../runtime/production-installment-payment-adapter";
import type { PersistedInstallmentAction } from "../runtime/production-installment-runtime";

import { ProtectedMaterialIntegrityError } from "@hb/pos-db/core/db/protected-material-integrity-error";
import { SqliteInstallmentActionStore } from "./sqlite-installment-action-store";
import type { SensitivePayloadEncryptor } from "./sqlite-repositories";
import type { SqliteConnectionPort } from "@hb/pos-db/core/db/types";

type AttemptRow = Readonly<{
  attempt_id: unknown;
  action_id: unknown;
  payment_guid: unknown;
  source_payment_guid: unknown;
  original_tender_evidence_id: unknown;
  source_attempt_id: unknown;
  sequence: unknown;
  provider: unknown;
  operation: unknown;
  amount_cents: unknown;
  state: unknown;
  idempotency_key: unknown;
  payload_revision: unknown;
  protected_payload_ciphertext: unknown;
  created_at_iso: unknown;
  updated_at_iso: unknown;
  provider_environment: unknown;
  provider_session_id: unknown;
  provider_acknowledged_at_iso: unknown;
}>;

type CashRow = Readonly<{
  settlement_id: unknown;
  action_id: unknown;
  payment_guid: unknown;
  source_payment_guid: unknown;
  original_tender_evidence_id: unknown;
  source_attempt_id: unknown;
  sequence: unknown;
  operation: unknown;
  amount_cents: unknown;
  idempotency_key: unknown;
  state: unknown;
  created_at_iso: unknown;
  updated_at_iso: unknown;
}>;

type MaterialRow = Readonly<{
  attempt_id: unknown;
  payload_revision: unknown;
  material_ciphertext: unknown;
}>;

type AttemptEnvelopeV1 = Readonly<{
  format: "hb-pos-installment-provider-attempt-v1";
  aad: Readonly<{
    revision: 1;
    actionId: string;
    attemptId: string;
    paymentGuid: string;
    sequence: number;
  }>;
  record: InstallmentProviderAttemptRecord;
}>;

type ApprovedMaterialEnvelopeV1 = Readonly<{
  format: "hb-pos-installment-approved-material-v1";
  aad: Readonly<{
    revision: 1;
    actionId: string;
    attemptId: string;
    paymentGuid: string;
  }>;
  material: InstallmentApprovedPaymentMaterial;
}>;

type OriginalTenderEnvelopeV1 = Readonly<{
  format: "hb-pos-installment-original-tender-v1";
  aad: Readonly<{
    revision: 1;
    evidenceId: string;
    originActionId: string;
    paymentGuid: string;
    sourceAttemptId: string;
  }>;
  seed:
    | Readonly<{ kind: "cash" }>
    | Readonly<{ kind: "square"; paymentId: string }>
    | Readonly<{ kind: "linkly-cloud"; rfn: string }>
    | Readonly<{
        kind: "voucher";
        reference: string;
        reservationToken: string;
      }>;
  approvedMaterial: InstallmentApprovedPaymentMaterial | null;
}>;

type PreparedOriginalTenderEvidence = Readonly<{
  evidenceId: string;
  originActionId: string | null;
  storeCode: string;
  deviceCode: string;
  installmentGuid: string;
  paymentGuid: string;
  sourceAttemptId: string;
  method: "cash" | "card" | "voucher";
  amountCents: number;
  provider: "square" | "linkly-cloud" | "voucher" | null;
  provenance: "local-approved-attempt" | "hbpos-protected-details";
  ciphertext: Uint8Array;
  createdAtIso: string;
}>;

const ATTEMPT_RECORD_KEYS = new Set([
  "actionId",
  "paymentGuid",
  "sourcePaymentGuid",
  "originalTenderEvidenceId",
  "sourceAttemptId",
  "sequence",
  "attempt",
]);
const ATTEMPT_KEYS = new Set([
  "attemptId",
  "idempotencyKey",
  "orderGuid",
  "provider",
  "operation",
  "amount",
  "state",
  "references",
  "createdAtIso",
  "updatedAtIso",
  "lastErrorCode",
  "providerEnvironment",
  "providerAcknowledgedAtIso",
  "receiptText",
  "responseCode",
]);
const REFERENCE_KEYS = new Set([
  "checkoutId",
  "paymentId",
  "sessionId",
  "txnRef",
  "rfn",
  "voucherReservationToken",
]);
const CASH_KEYS = new Set([
  "actionId",
  "settlementId",
  "paymentGuid",
  "sourcePaymentGuid",
  "originalTenderEvidenceId",
  "sourceAttemptId",
  "sequence",
  "operation",
  "amountCents",
  "idempotencyKey",
  "state",
]);

/** 独立分期 provider ledger；不会访问通用 payment_attempts/local_orders。 */
export class SqliteInstallmentProviderAttemptStore
implements InstallmentProviderAttemptStorePort {
  private readonly actions: SqliteInstallmentActionStore;

  public constructor(
    private readonly connection: SqliteConnectionPort,
    private readonly encryptor: SensitivePayloadEncryptor,
    private readonly nowIso: () => string,
  ) {
    this.actions = new SqliteInstallmentActionStore(
      connection,
      encryptor,
      nowIso,
    );
  }

  public loadAction(
    actionId: string,
  ): Promise<PersistedInstallmentAction | null> {
    return this.actions.loadById(actionId);
  }

  public async loadPlan(
    actionIdInput: string,
  ): Promise<InstallmentProviderAttemptPlan | null> {
    const actionId = uuid(actionIdInput, "installment action ID");
    const action = await this.actions.loadById(actionId);
    if (!action) return null;
    return this.loadPlanFrom(this.connection, action);
  }

  public async loadAttemptBinding(
    attemptIdInput: string,
  ): Promise<Readonly<{
    action: PersistedInstallmentAction;
    record: InstallmentProviderAttemptRecord;
  }> | null> {
    const attemptId = strictText(
      attemptIdInput,
      "provider attempt ID",
      256,
    );
    const row = await selectAttemptRow(this.connection, attemptId);
    if (!row) return null;
    const actionId = uuid(row.action_id, "installment action ID");
    const action = await this.actions.loadById(actionId);
    if (!action) {
      throw integrity("PROTECTED_MATERIAL_BINDING_MISMATCH");
    }
    return Object.freeze({
      action,
      record: await this.decodeAttempt(row, action),
    });
  }

  public async get(attemptIdInput: string): Promise<PaymentAttempt | null> {
    const binding = await this.loadAttemptBinding(attemptIdInput);
    return binding?.record.attempt ?? null;
  }

  /**
   * 旧账本没有冻结环境时，只能由已认证 Linkly 服务端列出的 session 反查。
   * 这里逐条解密并校验 action/attempt 绑定，拒绝模糊 session 或全历史扫描。
   */
  public async findLegacyLinklyAttemptForSession(
    terminal: Readonly<{ storeCode: string; deviceCode: string }>,
    sessionIdInput: string,
    idempotencyKeyInput: string,
  ): Promise<PaymentAttempt | null> {
    const storeCode = strictText(terminal.storeCode, "store code", 50);
    const deviceCode = strictText(terminal.deviceCode, "device code", 128);
    const sessionId = strictText(sessionIdInput, "Linkly session ID", 2_048);
    // Linkly notification UID 可省略 UUID 连字符并使用不同大小写；比较其 32 位
    // 稳定 UUID 形式，仍由本地密文 action/attempt 绑定作第二道校验。
    const idempotencyKey = normalizedLinklyAttemptUid(idempotencyKeyInput);
    const rows = await this.connection.getAll<Readonly<{ attempt_id: unknown }>>(
      `SELECT attempts.attempt_id AS attempt_id
       FROM installment_provider_attempts attempts
       INNER JOIN installment_actions actions ON actions.action_id = attempts.action_id
       WHERE actions.store_code = ? AND actions.device_code = ?
         AND attempts.provider = 'linkly-cloud'
         AND lower(replace(attempts.idempotency_key, '-', '')) = ?
         AND attempts.provider_acknowledged_at_iso IS NULL
         AND attempts.state IN ('Submitted', 'Pending', 'Unknown', 'Approved', 'Declined', 'Cancelled')`,
      [storeCode, deviceCode, idempotencyKey],
    );
    const matches: PaymentAttempt[] = [];
    for (const row of rows) {
      const binding = await this.loadAttemptBinding(String(row.attempt_id));
      const attempt = binding?.record.attempt;
      if (
        attempt &&
        !attempt.providerEnvironment &&
        attempt.references.sessionId === sessionId
      ) matches.push(attempt);
    }
    return matches.length === 1 ? matches[0]! : null;
  }

  /**
   * 普通首付/还款须等整个 action 完成。唯有取消退款的同一笔 Linkly refund，
   * 在不可变来源绑定、耐久 Approved 与加密证据齐全后可逐笔 ACK，释放终端给
   * 后续退款；取消 action 仍未 resolved，因而绝不会放行新业务。
   */
  public async canProviderAcknowledged(attempt: PaymentAttempt): Promise<boolean> {
    if (attempt.provider !== "linkly-cloud" || attempt.state !== "Approved") return false;
    const binding = await this.loadAttemptBinding(attempt.attemptId);
    if (!binding || binding.record.attempt.provider !== "linkly-cloud" || binding.record.attempt.state !== "Approved") return false;
    const material = await this.loadApprovedMaterial(attempt.attemptId);
    if (!material) return false;
    if (
      binding.action.action.kind === "cancel-refund" &&
      binding.record.attempt.operation === "refund"
    ) return true;
    const row = await this.connection.getFirst<Readonly<{ resolution: unknown }>>(
      `SELECT resolution FROM installment_actions WHERE action_id = ?`,
      [binding.record.actionId],
    );
    return row?.resolution === "Completed";
  }

  public async markProviderAcknowledged(
    expectedInput: PaymentAttempt,
    acknowledgedAtIsoInput: string,
  ): Promise<boolean> {
    const acknowledgedAtIso = canonicalIso(acknowledgedAtIsoInput, "provider acknowledgement time");
    const binding = await this.loadAttemptBinding(expectedInput.attemptId);
    if (!binding || JSON.stringify(binding.record.attempt) !== JSON.stringify(expectedInput)) return false;
    const expected = binding.record;
    if (
      expected.attempt.provider !== "linkly-cloud" ||
      !isLinklyFinal(expected.attempt.state) ||
      expected.attempt.providerAcknowledgedAtIso
    ) return false;
    const next: InstallmentProviderAttemptRecord = Object.freeze({
      ...expected,
      attempt: Object.freeze({ ...expected.attempt, providerAcknowledgedAtIso: acknowledgedAtIso }),
    });
    const ciphertext = await encryptAttempt(this.encryptor, next);
    const result = await this.connection.run(
      `UPDATE installment_provider_attempts
       SET protected_payload_ciphertext = ?, provider_acknowledged_at_iso = ?
       WHERE attempt_id = ? AND action_id = ? AND provider = 'linkly-cloud'
         AND state IN ('Approved', 'Declined', 'Cancelled')
         AND provider_acknowledged_at_iso IS NULL
         AND state = ? AND updated_at_iso = ?`,
      [ciphertext, acknowledgedAtIso, expected.attempt.attemptId, expected.actionId, expected.attempt.state, expected.attempt.updatedAtIso],
    );
    return result.changes === 1;
  }

  public async verifyProviderEnvironment(
    expectedInput: PaymentAttempt,
    verifiedEnvironmentInput: string,
  ): Promise<boolean> {
    const verifiedEnvironment = providerEnvironment(verifiedEnvironmentInput);
    const binding = await this.loadAttemptBinding(expectedInput.attemptId);
    if (!binding || JSON.stringify(binding.record.attempt) !== JSON.stringify(expectedInput)) return false;
    const expected = binding.record;
    if (
      expected.attempt.provider !== "linkly-cloud" ||
      expected.attempt.state === "Created" ||
      expected.attempt.providerEnvironment
    ) return false;
    const next: InstallmentProviderAttemptRecord = Object.freeze({
      ...expected,
      attempt: Object.freeze({ ...expected.attempt, providerEnvironment: verifiedEnvironment }),
    });
    const ciphertext = await encryptAttempt(this.encryptor, next);
    const result = await this.connection.run(
      `UPDATE installment_provider_attempts
       SET protected_payload_ciphertext = ?, provider_environment = ?,
           provider_session_id = ?
       WHERE attempt_id = ? AND action_id = ? AND provider = 'linkly-cloud'
         AND state = ? AND updated_at_iso = ?
         AND provider_environment IS NULL`,
      [ciphertext, verifiedEnvironment, expected.attempt.references.sessionId, expected.attempt.attemptId, expected.actionId, expected.attempt.state, expected.attempt.updatedAtIso],
    );
    return result.changes === 1;
  }

  public async bindPlanOrGet(
    candidateInput: InstallmentProviderAttemptPlan,
  ): Promise<InstallmentProviderAttemptPlan> {
    const actionId = uuid(
      candidateInput?.actionId,
      "installment action ID",
    );
    const action = await this.actions.loadById(actionId);
    if (!action) {
      throw new Error("Installment provider plan action was not found.");
    }
    const candidate = normalizePlan(candidateInput, action, true);
    const existing = await this.loadPlanFrom(this.connection, action);
    if (existing) {
      assertSamePlan(existing, candidate);
      return existing;
    }
    const prepared = await Promise.all(
      candidate.attempts.map(async (record) => ({
        record,
        ciphertext: await encryptAttempt(this.encryptor, record),
      })),
    );
    const createdAtIso = canonicalIso(
      this.nowIso(),
      "installment provider plan creation time",
    );

    return this.connection.withExclusiveTransaction(
      async (transaction) => {
        const actionRow = await transaction.getFirst<{
          resolution: unknown;
        }>(
          `SELECT resolution
           FROM installment_actions
           WHERE action_id = ?`,
          [actionId],
        );
        if (!actionRow || actionRow.resolution !== null) {
          throw new Error(
            "Installment provider plan action is resolved or missing.",
          );
        }
        const existing = await this.loadPlanFrom(transaction, action);
        if (existing) {
          assertSamePlan(existing, candidate);
          return existing;
        }
        await assertRefundEvidenceBindings(
          transaction,
          candidate,
          action,
        );
        await transaction.run(
          `INSERT INTO installment_provider_plans (
            action_id, created_at_iso
          ) VALUES (?, ?)`,
          [actionId, createdAtIso],
        );
        for (const item of prepared) {
          const record = item.record;
          const attempt = record.attempt;
          await transaction.run(
            `INSERT INTO installment_provider_attempts (
              attempt_id, action_id, payment_guid, source_payment_guid,
              original_tender_evidence_id, source_attempt_id, sequence,
              provider, operation, amount_cents, state, idempotency_key,
              payload_revision, protected_payload_ciphertext,
              created_at_iso, updated_at_iso, provider_environment,
              provider_session_id
            ) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, 1, ?, ?, ?, ?, ?)`,
            [
              attempt.attemptId,
              record.actionId,
              record.paymentGuid,
              record.sourcePaymentGuid,
              record.originalTenderEvidenceId,
              record.sourceAttemptId,
              record.sequence,
              attempt.provider,
              attempt.operation,
              attempt.amount.cents,
              attempt.state,
              attempt.idempotencyKey,
              item.ciphertext,
              attempt.createdAtIso,
              attempt.updatedAtIso,
              attempt.providerEnvironment ?? null,
              attempt.references.sessionId,
            ],
          );
        }
        for (const settlement of candidate.cashSettlements) {
          await transaction.run(
            `INSERT INTO installment_cash_settlements (
              settlement_id, action_id, payment_guid, source_payment_guid,
              original_tender_evidence_id, source_attempt_id, sequence,
              operation, amount_cents, idempotency_key, state,
              created_at_iso, updated_at_iso
            ) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)`,
            [
              settlement.settlementId,
              settlement.actionId,
              settlement.paymentGuid,
              settlement.sourcePaymentGuid,
              settlement.originalTenderEvidenceId,
              settlement.sourceAttemptId,
              settlement.sequence,
              settlement.operation,
              settlement.amountCents,
              settlement.idempotencyKey,
              settlement.state,
              createdAtIso,
              createdAtIso,
            ],
          );
        }
        return candidate;
      },
    );
  }

  public async compareAndUpdateAttempt(
    input: Parameters<
      InstallmentProviderAttemptStorePort["compareAndUpdateAttempt"]
    >[0],
  ): Promise<boolean> {
    const actionId = uuid(
      input?.expected?.actionId,
      "installment action ID",
    );
    const action = await this.actions.loadById(actionId);
    if (!action) {
      throw new Error("Installment provider attempt action was not found.");
    }
    const expected = normalizeAttemptRecord(
      input.expected,
      action,
      false,
    );
    const next = normalizeAttemptRecord(
      { ...expected, attempt: input.nextAttempt },
      action,
      false,
    );
    validateAttemptUpdate(expected, next);
    const material =
      input.approvedMaterial === undefined
        ? undefined
        : normalizeApprovedMaterial(input.approvedMaterial, next.attempt);
    if (
      (next.attempt.state === "Approved") !==
      (material !== undefined)
    ) {
      throw new Error(
        "Approved installment attempt material is incomplete.",
      );
    }
    const nextCiphertext = await encryptAttempt(this.encryptor, next);
    const materialCiphertext =
      material === undefined
        ? null
        : await encryptApprovedMaterial(
            this.encryptor,
            next,
            material,
          );
    const evidence =
      material !== undefined && next.attempt.operation === "purchase"
        ? await prepareLocalEvidence(
            this.encryptor,
            action,
            next,
            material,
            this.nowIso(),
          )
        : null;

    return this.connection.withExclusiveTransaction(
      async (transaction) => {
        const currentRow = await selectAttemptRow(
          transaction,
          expected.attempt.attemptId,
        );
        if (!currentRow) return false;
        const current = await this.decodeAttempt(currentRow, action);
        if (JSON.stringify(current) !== JSON.stringify(expected)) {
          return false;
        }
        if (evidence) {
          await insertOriginalTenderEvidence(transaction, evidence);
        }
        if (material && materialCiphertext) {
          await transaction.run(
            `INSERT INTO installment_approved_materials (
              attempt_id, payload_revision, material_ciphertext,
              created_at_iso
            ) VALUES (?, 1, ?, ?)`,
            [
              next.attempt.attemptId,
              materialCiphertext,
              next.attempt.updatedAtIso,
            ],
          );
        }
        const result = await transaction.run(
          `UPDATE installment_provider_attempts
           SET state = ?, protected_payload_ciphertext = ?,
             updated_at_iso = ?, provider_session_id = ?
           WHERE attempt_id = ? AND action_id = ? AND state = ?
             AND updated_at_iso = ?
             AND protected_payload_ciphertext = ?`,
          [
            next.attempt.state,
            nextCiphertext,
            next.attempt.updatedAtIso,
            next.attempt.references.sessionId,
            next.attempt.attemptId,
            next.actionId,
            expected.attempt.state,
            expected.attempt.updatedAtIso,
            bytes(
              currentRow.protected_payload_ciphertext,
              "provider attempt ciphertext",
            ),
          ],
        );
        if (result.changes !== 1) {
          throw new Error("Installment provider attempt CAS failed.");
        }
        return true;
      },
    );
  }

  public async loadApprovedMaterial(
    attemptIdInput: string,
  ): Promise<InstallmentApprovedPaymentMaterial | null> {
    const attemptId = strictText(
      attemptIdInput,
      "provider attempt ID",
      256,
    );
    const attemptRow = await selectAttemptRow(this.connection, attemptId);
    if (!attemptRow) return null;
    const actionId = uuid(
      attemptRow.action_id,
      "installment action ID",
    );
    const action = await this.actions.loadById(actionId);
    if (!action) {
      throw integrity("PROTECTED_MATERIAL_BINDING_MISMATCH");
    }
    const record = await this.decodeAttempt(attemptRow, action);
    const row = await this.connection.getFirst<MaterialRow>(
      `SELECT attempt_id, payload_revision, material_ciphertext
       FROM installment_approved_materials
       WHERE attempt_id = ?`,
      [attemptId],
    );
    if (record.attempt.state !== "Approved") {
      if (row) throw integrity("PROTECTED_MATERIAL_BINDING_MISMATCH");
      return null;
    }
    if (!row) {
      throw integrity("PROTECTED_MATERIAL_SHAPE_INVALID");
    }
    return decodeApprovedMaterial(this.encryptor, row, record);
  }

  public async approveCashSettlements(
    actionIdInput: string,
  ): Promise<readonly InstallmentCashSettlement[]> {
    const actionId = uuid(actionIdInput, "installment action ID");
    const action = await this.actions.loadById(actionId);
    if (!action) {
      throw new Error("Installment cash action was not found.");
    }
    return this.connection.withExclusiveTransaction(
      async (transaction) => {
        const plan = await this.loadPlanFrom(transaction, action);
        if (!plan || plan.cashSettlements.length === 0) {
          throw new Error("Installment cash plan was not found.");
        }
        const approved: InstallmentCashSettlement[] = [];
        for (const settlement of plan.cashSettlements) {
          if (settlement.state === "Approved") {
            if (settlement.operation === "purchase") {
              await assertLocalCashEvidence(transaction, settlement);
            }
            approved.push(settlement);
            continue;
          }
          if (settlement.operation === "purchase") {
            const evidence = await prepareLocalCashEvidence(
              this.encryptor,
              action,
              settlement,
              this.nowIso(),
            );
            await insertOriginalTenderEvidence(transaction, evidence);
          }
          const updatedAtIso = nextIso(
            canonicalIso(
              (
                await transaction.getFirst<{ updated_at_iso: unknown }>(
                  `SELECT updated_at_iso
                   FROM installment_cash_settlements
                   WHERE settlement_id = ?`,
                  [settlement.settlementId],
                )
              )?.updated_at_iso,
              "cash settlement update time",
            ),
            this.nowIso(),
          );
          const result = await transaction.run(
            `UPDATE installment_cash_settlements
             SET state = 'Approved', updated_at_iso = ?
             WHERE settlement_id = ? AND action_id = ?
               AND state = 'Prepared'`,
            [
              updatedAtIso,
              settlement.settlementId,
              settlement.actionId,
            ],
          );
          if (result.changes !== 1) {
            throw new Error("Installment cash approval CAS failed.");
          }
          approved.push(
            Object.freeze({ ...settlement, state: "Approved" }),
          );
        }
        return Object.freeze(approved);
      },
    );
  }

  private async loadPlanFrom(
    connection: SqliteConnectionPort,
    action: PersistedInstallmentAction,
  ): Promise<InstallmentProviderAttemptPlan | null> {
    const actionId = action.action.actionId;
    const plan = await connection.getFirst<{ action_id: unknown }>(
      `SELECT action_id
       FROM installment_provider_plans
       WHERE action_id = ?`,
      [actionId],
    );
    if (!plan) return null;
    if (text(plan.action_id, "provider plan action ID") !== actionId) {
      throw integrity("PROTECTED_MATERIAL_BINDING_MISMATCH");
    }
    const [attemptRows, cashRows] = await Promise.all([
      connection.getAll<AttemptRow>(
        `${attemptColumns()}
         WHERE action_id = ?
         ORDER BY sequence, attempt_id`,
        [actionId],
      ),
      connection.getAll<CashRow>(
        `${cashColumns()}
         WHERE action_id = ?
         ORDER BY sequence, settlement_id`,
        [actionId],
      ),
    ]);
    const attempts: InstallmentProviderAttemptRecord[] = [];
    for (const row of attemptRows) {
      attempts.push(await this.decodeAttempt(row, action));
    }
    const cashSettlements = cashRows.map((row) =>
      cashFromRow(row, action),
    );
    return normalizePlan(
      {
        actionId,
        attempts,
        cashSettlements,
      },
      action,
      false,
    );
  }

  private async decodeAttempt(
    row: AttemptRow,
    action: PersistedInstallmentAction,
  ): Promise<InstallmentProviderAttemptRecord> {
    if (
      integer(row.payload_revision, "attempt payload revision") !== 1
    ) {
      throw integrity("PROTECTED_MATERIAL_VERSION_INVALID");
    }
    const raw = await this.encryptor.decrypt(
      bytes(
        row.protected_payload_ciphertext,
        "provider attempt ciphertext",
      ),
    );
    let parsed: unknown;
    try {
      parsed = JSON.parse(raw);
    } catch {
      throw integrity("PROTECTED_MATERIAL_JSON_INVALID");
    }
    if (
      !isRecord(parsed) ||
      parsed.format !== "hb-pos-installment-provider-attempt-v1" ||
      !isRecord(parsed.aad)
    ) {
      throw integrity("PROTECTED_MATERIAL_SHAPE_INVALID");
    }
    let record: InstallmentProviderAttemptRecord;
    try {
      record = normalizeAttemptRecord(
        (parsed as { record?: unknown }).record,
        action,
        false,
      );
    } catch (error) {
      if (error instanceof TypeError) {
        throw integrity("PROTECTED_MATERIAL_SHAPE_INVALID");
      }
      throw error;
    }
    const envelope = parsed as AttemptEnvelopeV1;
    if (
      envelope.aad.revision !== 1 ||
      envelope.aad.actionId !== record.actionId ||
      envelope.aad.attemptId !== record.attempt.attemptId ||
      envelope.aad.paymentGuid !== record.paymentGuid ||
      envelope.aad.sequence !== record.sequence ||
      !attemptRowMatches(row, record)
    ) {
      throw integrity("PROTECTED_MATERIAL_BINDING_MISMATCH");
    }
    return record;
  }
}

async function encryptAttempt(
  encryptor: SensitivePayloadEncryptor,
  record: InstallmentProviderAttemptRecord,
): Promise<Uint8Array> {
  const envelope: AttemptEnvelopeV1 = Object.freeze({
    format: "hb-pos-installment-provider-attempt-v1",
    aad: Object.freeze({
      revision: 1,
      actionId: record.actionId,
      attemptId: record.attempt.attemptId,
      paymentGuid: record.paymentGuid,
      sequence: record.sequence,
    }),
    record,
  });
  const ciphertext = await encryptor.encrypt(JSON.stringify(envelope));
  if (!(ciphertext instanceof Uint8Array) || ciphertext.length === 0) {
    throw new Error("Installment provider attempt encryption failed.");
  }
  return ciphertext;
}

async function encryptApprovedMaterial(
  encryptor: SensitivePayloadEncryptor,
  record: InstallmentProviderAttemptRecord,
  material: InstallmentApprovedPaymentMaterial,
): Promise<Uint8Array> {
  const envelope: ApprovedMaterialEnvelopeV1 = Object.freeze({
    format: "hb-pos-installment-approved-material-v1",
    aad: Object.freeze({
      revision: 1,
      actionId: record.actionId,
      attemptId: record.attempt.attemptId,
      paymentGuid: record.paymentGuid,
    }),
    material,
  });
  const ciphertext = await encryptor.encrypt(JSON.stringify(envelope));
  if (!(ciphertext instanceof Uint8Array) || ciphertext.length === 0) {
    throw new Error("Installment approved material encryption failed.");
  }
  return ciphertext;
}

async function decodeApprovedMaterial(
  encryptor: SensitivePayloadEncryptor,
  row: MaterialRow,
  record: InstallmentProviderAttemptRecord,
): Promise<InstallmentApprovedPaymentMaterial> {
  if (
    integer(row.payload_revision, "approved material revision") !== 1
  ) {
    throw integrity("PROTECTED_MATERIAL_VERSION_INVALID");
  }
  const raw = await encryptor.decrypt(
    bytes(row.material_ciphertext, "approved material ciphertext"),
  );
  let parsed: unknown;
  try {
    parsed = JSON.parse(raw);
  } catch {
    throw integrity("PROTECTED_MATERIAL_JSON_INVALID");
  }
  if (
    !isRecord(parsed) ||
    parsed.format !== "hb-pos-installment-approved-material-v1" ||
    !isRecord(parsed.aad)
  ) {
    throw integrity("PROTECTED_MATERIAL_SHAPE_INVALID");
  }
  const envelope = parsed as ApprovedMaterialEnvelopeV1;
  let material: InstallmentApprovedPaymentMaterial;
  try {
    material = normalizeApprovedMaterial(
      envelope.material,
      record.attempt,
    );
  } catch (error) {
    if (error instanceof TypeError) {
      throw integrity("PROTECTED_MATERIAL_SHAPE_INVALID");
    }
    throw error;
  }
  if (
    envelope.aad.revision !== 1 ||
    envelope.aad.actionId !== record.actionId ||
    envelope.aad.attemptId !== record.attempt.attemptId ||
    envelope.aad.paymentGuid !== record.paymentGuid ||
    !matches(row.attempt_id, record.attempt.attemptId)
  ) {
    throw integrity("PROTECTED_MATERIAL_BINDING_MISMATCH");
  }
  return material;
}

function normalizeApprovedMaterial(
  input: InstallmentApprovedPaymentMaterial,
  attempt: PaymentAttempt,
): InstallmentApprovedPaymentMaterial {
  if (!isRecord(input) || attempt.state !== "Approved") {
    throw new TypeError("Approved installment material is invalid.");
  }
  if (input.kind === "card") {
    if (
      !exactKeys(input, ["kind", "evidence", "receiptText"]) ||
      (attempt.provider !== "square" &&
        attempt.provider !== "linkly-cloud")
    ) {
      throw new TypeError("Approved card material is invalid.");
    }
    const evidence = normalizeCardSyncEvidence(input.evidence);
    const receiptText = optionalReceipt(input.receiptText);
    if (
      evidence.provider !== attempt.provider ||
      evidence.operation !== attempt.operation ||
      evidence.amountCents !== Math.abs(attempt.amount.cents) ||
      receiptText !== (attempt.receiptText ?? null)
    ) {
      throw new TypeError(
        "Approved card material does not match attempt.",
      );
    }
    return Object.freeze({
      kind: "card",
      evidence,
      receiptText,
    });
  }
  if (
    input.kind !== "voucher" ||
    !exactKeys(input, ["kind", "reference", "reservationToken"]) ||
    attempt.provider !== "voucher"
  ) {
    throw new TypeError("Approved voucher material is invalid.");
  }
  const reference = strictText(
    input.reference,
    "approved voucher reference",
    512,
  );
  const reservationToken = optionalText(
    input.reservationToken,
    "approved voucher reservation token",
    4_096,
  );
  if (
    (attempt.operation === "purchase" &&
      reservationToken === null) ||
    (attempt.operation === "refund" &&
      reservationToken !== null)
  ) {
    throw new TypeError(
      "Approved voucher material operation is invalid.",
    );
  }
  return Object.freeze({
    kind: "voucher",
    reference,
    reservationToken,
  });
}

function validateAttemptUpdate(
  expected: InstallmentProviderAttemptRecord,
  next: InstallmentProviderAttemptRecord,
): void {
  const previous = expected.attempt;
  const candidate = next.attempt;
  if (
    expected.actionId !== next.actionId ||
    expected.paymentGuid !== next.paymentGuid ||
    expected.sourcePaymentGuid !== next.sourcePaymentGuid ||
    expected.originalTenderEvidenceId !==
      next.originalTenderEvidenceId ||
    expected.sourceAttemptId !== next.sourceAttemptId ||
    expected.sequence !== next.sequence ||
    previous.attemptId !== candidate.attemptId ||
    previous.idempotencyKey !== candidate.idempotencyKey ||
    previous.orderGuid !== candidate.orderGuid ||
    previous.provider !== candidate.provider ||
    previous.operation !== candidate.operation ||
    previous.amount.currency !== candidate.amount.currency ||
    previous.amount.cents !== candidate.amount.cents ||
    previous.createdAtIso !== candidate.createdAtIso
  ) {
    throw new Error("Installment provider attempt identity changed.");
  }
  if (
    previous.state !== candidate.state &&
    !canTransitionPaymentAttempt(previous.state, candidate.state)
  ) {
    throw new Error("Installment provider attempt transition is invalid.");
  }
  if (candidate.updatedAtIso <= previous.updatedAtIso) {
    throw new Error(
      "Installment provider attempt update time must advance.",
    );
  }
  for (const key of Object.keys(
    previous.references,
  ) as (keyof PaymentProviderReferences)[]) {
    const oldValue = previous.references[key];
    const nextValue = candidate.references[key];
    if (
      oldValue !== null &&
      (nextValue === null || nextValue !== oldValue)
    ) {
      throw new Error(
        "Installment provider reference cannot be replaced.",
      );
    }
  }
  if (
    candidate.provider !== "voucher" &&
    candidate.references.voucherReservationToken !== null
  ) {
    throw new Error(
      "Card attempt cannot bind a voucher protected reference.",
    );
  }
  if (
    candidate.provider === "voucher" &&
    (candidate.references.checkoutId !== null ||
      candidate.references.paymentId !== null ||
      candidate.references.sessionId !== null ||
      candidate.references.txnRef !== null ||
      candidate.references.rfn !== null)
  ) {
    throw new Error("Voucher attempt contains card references.");
  }
}

async function prepareLocalEvidence(
  encryptor: SensitivePayloadEncryptor,
  action: PersistedInstallmentAction,
  record: InstallmentProviderAttemptRecord,
  material: InstallmentApprovedPaymentMaterial,
  nowIsoInput: string,
): Promise<PreparedOriginalTenderEvidence> {
  const attempt = record.attempt;
  // 手动刷卡仅用于普通销售，不得转成分期原卡退款凭据。
  if (attempt.provider === "manual-card") throw new Error("Manual card installment evidence is unsupported.");
  const seed = localEvidenceSeed(attempt, material);
  const envelope: OriginalTenderEnvelopeV1 = Object.freeze({
    format: "hb-pos-installment-original-tender-v1",
    aad: Object.freeze({
      revision: 1,
      evidenceId: record.originalTenderEvidenceId,
      originActionId: record.actionId,
      paymentGuid: record.paymentGuid,
      sourceAttemptId: attempt.attemptId,
    }),
    seed,
    approvedMaterial: material,
  });
  const ciphertext = await encryptor.encrypt(JSON.stringify(envelope));
  if (!(ciphertext instanceof Uint8Array) || ciphertext.length === 0) {
    throw new Error("Original tender evidence encryption failed.");
  }
  return Object.freeze({
    evidenceId: record.originalTenderEvidenceId,
    originActionId: record.actionId,
    storeCode: action.storeCode,
    deviceCode: action.deviceCode,
    installmentGuid: action.action.installmentGuid,
    paymentGuid: record.paymentGuid,
    sourceAttemptId: attempt.attemptId,
    method: attempt.provider === "voucher" ? "voucher" : "card",
    amountCents: Math.abs(attempt.amount.cents),
    provider: attempt.provider,
    provenance: "local-approved-attempt",
    ciphertext,
    createdAtIso: canonicalIso(
      nowIsoInput,
      "original tender evidence time",
    ),
  });
}

async function prepareLocalCashEvidence(
  encryptor: SensitivePayloadEncryptor,
  action: PersistedInstallmentAction,
  settlement: InstallmentCashSettlement,
  nowIsoInput: string,
): Promise<PreparedOriginalTenderEvidence> {
  const envelope: OriginalTenderEnvelopeV1 = Object.freeze({
    format: "hb-pos-installment-original-tender-v1",
    aad: Object.freeze({
      revision: 1,
      evidenceId: settlement.originalTenderEvidenceId,
      originActionId: settlement.actionId,
      paymentGuid: settlement.paymentGuid,
      sourceAttemptId: settlement.settlementId,
    }),
    seed: Object.freeze({ kind: "cash" }),
    approvedMaterial: null,
  });
  const ciphertext = await encryptor.encrypt(JSON.stringify(envelope));
  if (!(ciphertext instanceof Uint8Array) || ciphertext.length === 0) {
    throw new Error("Original cash evidence encryption failed.");
  }
  return Object.freeze({
    evidenceId: settlement.originalTenderEvidenceId,
    originActionId: settlement.actionId,
    storeCode: action.storeCode,
    deviceCode: action.deviceCode,
    installmentGuid: action.action.installmentGuid,
    paymentGuid: settlement.paymentGuid,
    sourceAttemptId: settlement.settlementId,
    method: "cash",
    amountCents: settlement.amountCents,
    provider: null,
    provenance: "local-approved-attempt",
    ciphertext,
    createdAtIso: canonicalIso(
      nowIsoInput,
      "original cash evidence time",
    ),
  });
}

async function assertLocalCashEvidence(
  connection: SqliteConnectionPort,
  settlement: InstallmentCashSettlement,
): Promise<void> {
  const row = await connection.getFirst<{
    origin_action_id: unknown;
    payment_guid: unknown;
    source_attempt_id: unknown;
    method: unknown;
    amount_cents: unknown;
  }>(
    `SELECT origin_action_id, payment_guid, source_attempt_id, method,
      amount_cents
     FROM installment_original_tender_evidence
     WHERE evidence_id = ?`,
    [settlement.originalTenderEvidenceId],
  );
  if (
    !row ||
    !matches(row.origin_action_id, settlement.actionId) ||
    !matches(row.payment_guid, settlement.paymentGuid) ||
    !matches(row.source_attempt_id, settlement.settlementId) ||
    !matches(row.method, "cash") ||
    integer(row.amount_cents, "cash evidence amount") !==
      settlement.amountCents
  ) {
    throw integrity("PROTECTED_MATERIAL_BINDING_MISMATCH");
  }
}

function localEvidenceSeed(
  attempt: PaymentAttempt,
  material: InstallmentApprovedPaymentMaterial,
): OriginalTenderEnvelopeV1["seed"] {
  if (attempt.provider === "square") {
    return Object.freeze({
      kind: "square",
      paymentId: strictText(
        attempt.references.paymentId,
        "Square payment ID",
        2_048,
      ),
    });
  }
  if (attempt.provider === "linkly-cloud") {
    return Object.freeze({
      kind: "linkly-cloud",
      rfn: strictText(attempt.references.rfn, "Linkly RFN", 2_048),
    });
  }
  if (
    material.kind !== "voucher" ||
    material.reservationToken === null
  ) {
    throw new TypeError(
      "Voucher purchase evidence is missing protected material.",
    );
  }
  return Object.freeze({
    kind: "voucher",
    reference: material.reference,
    reservationToken: material.reservationToken,
  });
}

async function insertOriginalTenderEvidence(
  connection: SqliteConnectionPort,
  evidence: PreparedOriginalTenderEvidence,
): Promise<void> {
  await connection.run(
    `INSERT INTO installment_original_tender_evidence (
      evidence_id, origin_action_id, store_code, device_code,
      installment_guid, payment_guid, source_attempt_id, method,
      amount_cents, provider, provenance, payload_revision,
      protected_payload_ciphertext, created_at_iso
    ) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, 1, ?, ?)`,
    [
      evidence.evidenceId,
      evidence.originActionId,
      evidence.storeCode,
      evidence.deviceCode,
      evidence.installmentGuid,
      evidence.paymentGuid,
      evidence.sourceAttemptId,
      evidence.method,
      evidence.amountCents,
      evidence.provider,
      evidence.provenance,
      evidence.ciphertext,
      evidence.createdAtIso,
    ],
  );
}

async function selectAttemptRow(
  connection: SqliteConnectionPort,
  attemptId: string,
): Promise<AttemptRow | null> {
  return connection.getFirst<AttemptRow>(
    `${attemptColumns()} WHERE attempt_id = ? LIMIT 1`,
    [attemptId],
  );
}

function normalizePlan(
  input: InstallmentProviderAttemptPlan,
  action: PersistedInstallmentAction,
  requireInitial: boolean,
): InstallmentProviderAttemptPlan {
  if (
    !isRecord(input) ||
    !exactKeys(input, ["actionId", "attempts", "cashSettlements"]) ||
    !Array.isArray(input.attempts) ||
    !Array.isArray(input.cashSettlements)
  ) {
    throw new TypeError("Installment provider plan is invalid.");
  }
  const actionId = uuid(input.actionId, "installment action ID");
  if (actionId !== action.action.actionId) {
    throw new Error("Installment provider plan action conflict.");
  }
  const attempts = input.attempts.map((record) =>
    normalizeAttemptRecord(record, action, requireInitial),
  );
  const cashSettlements = input.cashSettlements.map((settlement) =>
    normalizeCashSettlement(settlement, action, requireInitial),
  );
  const sequences = new Set<number>();
  for (const entry of [...attempts, ...cashSettlements]) {
    if (sequences.has(entry.sequence)) {
      throw new Error("Installment provider plan sequence conflict.");
    }
    sequences.add(entry.sequence);
  }
  if (action.action.kind === "cancel-refund") {
    if (attempts.length + cashSettlements.length === 0) {
      throw new Error("Installment refund plan is empty.");
    }
  } else {
    if (
      attempts.length + cashSettlements.length !== 1 ||
      !sequences.has(0)
    ) {
      throw new Error("Installment purchase plan is invalid.");
    }
    const attempt = attempts[0];
    const cash = cashSettlements[0];
    if (action.action.method === "cash") {
      if (!cash || attempt) {
        throw new Error("Installment cash plan conflicts with action.");
      }
    } else {
      if (!attempt || cash) {
        throw new Error(
          "Installment provider plan conflicts with action.",
        );
      }
      if (
        (action.action.method === "voucher") !==
        (attempt.attempt.provider === "voucher")
      ) {
        throw new Error(
          "Installment provider plan method conflict.",
        );
      }
    }
  }
  return Object.freeze({
    actionId,
    attempts: Object.freeze(attempts),
    cashSettlements: Object.freeze(cashSettlements),
  });
}

function normalizeAttemptRecord(
  input: unknown,
  action: PersistedInstallmentAction,
  requireInitial: boolean,
): InstallmentProviderAttemptRecord {
  if (
    !isRecord(input) ||
    !hasOnlyKeys(input, ATTEMPT_RECORD_KEYS) ||
    !isRecord(input.attempt) ||
    !hasOnlyKeys(input.attempt, ATTEMPT_KEYS)
  ) {
    throw new TypeError("Installment provider attempt record is invalid.");
  }
  const attemptInput = input.attempt;
  if (!isRecord(attemptInput.amount)) {
    throw new TypeError("Installment provider attempt amount is invalid.");
  }
  const actionId = uuid(input.actionId, "attempt action ID");
  const paymentGuid = uuid(input.paymentGuid, "attempt payment GUID");
  const sequence = nonNegativeInteger(input.sequence, "attempt sequence");
  const provider = paymentProvider(attemptInput.provider);
  const operation = paymentOperation(attemptInput.operation);
  const amountCents = nonZeroInteger(
    attemptInput.amount.cents,
    "attempt amount",
  );
  if (
    attemptInput.amount.currency !== "AUD" ||
    (operation === "purchase" && amountCents <= 0) ||
    (operation === "refund" && amountCents >= 0)
  ) {
    throw new TypeError("Installment provider attempt amount is invalid.");
  }
  const state = paymentState(attemptInput.state);
  if (requireInitial && state !== "Created") {
    throw new Error("Installment provider attempt must start Created.");
  }
  const sourcePaymentGuid =
    input.sourcePaymentGuid === null
      ? null
      : uuid(input.sourcePaymentGuid, "source payment GUID");
  const sourceAttemptId =
    input.sourceAttemptId === null
      ? null
      : strictText(input.sourceAttemptId, "source attempt ID", 1_024);
  const attempt: PaymentAttempt = Object.freeze({
    attemptId: strictText(
      attemptInput.attemptId,
      "provider attempt ID",
      256,
    ),
    idempotencyKey: strictText(
      attemptInput.idempotencyKey,
      "provider idempotency key",
      512,
    ),
    orderGuid: uuid(
      attemptInput.orderGuid,
      "provider installment GUID",
    ),
    provider,
    operation,
    amount: Object.freeze({ currency: "AUD", cents: amountCents }),
    state,
    references: normalizeReferences(attemptInput.references),
    createdAtIso: canonicalIso(
      attemptInput.createdAtIso,
      "attempt creation time",
    ),
    updatedAtIso: canonicalIso(
      attemptInput.updatedAtIso,
      "attempt update time",
    ),
    lastErrorCode: optionalText(
      attemptInput.lastErrorCode,
      "attempt error code",
      256,
    ),
    receiptText: optionalReceipt(attemptInput.receiptText),
    ...(attemptInput.providerEnvironment === null || attemptInput.providerEnvironment === undefined
      ? {}
      : { providerEnvironment: providerEnvironment(attemptInput.providerEnvironment) }),
    ...(attemptInput.providerAcknowledgedAtIso === null || attemptInput.providerAcknowledgedAtIso === undefined
      ? {}
      : { providerAcknowledgedAtIso: canonicalIso(attemptInput.providerAcknowledgedAtIso, "provider acknowledgement time") }),
    responseCode: optionalText(
      attemptInput.responseCode,
      "attempt response code",
      128,
    ),
  });
  if (
    actionId !== action.action.actionId ||
    attempt.orderGuid !== action.action.installmentGuid
  ) {
    throw new Error("Installment provider attempt action conflict.");
  }
  if (operation === "purchase") {
    if (
      action.action.kind === "cancel-refund" ||
      paymentGuid !== action.action.paymentGuid ||
      amountCents !== action.action.amountCents ||
      sourcePaymentGuid !== null ||
      sourceAttemptId !== null
    ) {
      throw new Error("Installment purchase attempt conflicts with action.");
    }
  } else if (
    action.action.kind !== "cancel-refund" ||
    sourcePaymentGuid === null ||
    sourceAttemptId === null
  ) {
    throw new Error("Installment refund attempt provenance is invalid.");
  }
  return Object.freeze({
    actionId,
    paymentGuid,
    sourcePaymentGuid,
    originalTenderEvidenceId: strictText(
      input.originalTenderEvidenceId,
      "original tender evidence ID",
      1_024,
    ),
    sourceAttemptId,
    sequence,
    attempt,
  });
}

function normalizeCashSettlement(
  input: unknown,
  action: PersistedInstallmentAction,
  requireInitial: boolean,
): InstallmentCashSettlement {
  if (!isRecord(input) || !hasOnlyKeys(input, CASH_KEYS)) {
    throw new TypeError("Installment cash settlement is invalid.");
  }
  const actionId = uuid(input.actionId, "cash action ID");
  const paymentGuid = uuid(input.paymentGuid, "cash payment GUID");
  const sourcePaymentGuid =
    input.sourcePaymentGuid === null
      ? null
      : uuid(input.sourcePaymentGuid, "cash source payment GUID");
  const sourceAttemptId =
    input.sourceAttemptId === null
      ? null
      : strictText(input.sourceAttemptId, "cash source attempt ID", 1_024);
  const operation = paymentOperation(input.operation);
  const amountCents = positiveInteger(
    input.amountCents,
    "cash settlement amount",
  );
  const state =
    input.state === "Prepared" || input.state === "Approved"
      ? input.state
      : invalid<never>("Cash settlement state is invalid.");
  if (requireInitial && state !== "Prepared") {
    throw new Error("Cash settlement must start Prepared.");
  }
  if (actionId !== action.action.actionId) {
    throw new Error("Cash settlement action conflict.");
  }
  if (operation === "purchase") {
    if (
      action.action.kind === "cancel-refund" ||
      action.action.method !== "cash" ||
      paymentGuid !== action.action.paymentGuid ||
      amountCents !== action.action.amountCents ||
      sourcePaymentGuid !== null ||
      sourceAttemptId !== null
    ) {
      throw new Error("Purchase cash settlement conflicts with action.");
    }
  } else if (
    action.action.kind !== "cancel-refund" ||
    sourcePaymentGuid === null ||
    sourceAttemptId === null
  ) {
    throw new Error("Refund cash settlement provenance is invalid.");
  }
  return Object.freeze({
    actionId,
    settlementId: strictText(
      input.settlementId,
      "cash settlement ID",
      256,
    ),
    paymentGuid,
    sourcePaymentGuid,
    originalTenderEvidenceId: strictText(
      input.originalTenderEvidenceId,
      "original tender evidence ID",
      1_024,
    ),
    sourceAttemptId,
    sequence: nonNegativeInteger(input.sequence, "cash sequence"),
    operation,
    amountCents,
    idempotencyKey: strictText(
      input.idempotencyKey,
      "cash idempotency key",
      512,
    ),
    state,
  });
}

function cashFromRow(
  row: CashRow,
  action: PersistedInstallmentAction,
): InstallmentCashSettlement {
  return normalizeCashSettlement(
    {
      actionId: row.action_id,
      settlementId: row.settlement_id,
      paymentGuid: row.payment_guid,
      sourcePaymentGuid: row.source_payment_guid,
      originalTenderEvidenceId: row.original_tender_evidence_id,
      sourceAttemptId: row.source_attempt_id,
      sequence: row.sequence,
      operation: row.operation,
      amountCents: row.amount_cents,
      idempotencyKey: row.idempotency_key,
      state: row.state,
    },
    action,
    false,
  );
}

function attemptRowMatches(
  row: AttemptRow,
  record: InstallmentProviderAttemptRecord,
): boolean {
  const attempt = record.attempt;
  return (
    matches(row.attempt_id, attempt.attemptId) &&
    matches(row.action_id, record.actionId) &&
    matches(row.payment_guid, record.paymentGuid) &&
    nullableMatches(row.source_payment_guid, record.sourcePaymentGuid) &&
    matches(
      row.original_tender_evidence_id,
      record.originalTenderEvidenceId,
    ) &&
    nullableMatches(row.source_attempt_id, record.sourceAttemptId) &&
    integer(row.sequence, "attempt sequence") === record.sequence &&
    matches(row.provider, attempt.provider) &&
    matches(row.operation, attempt.operation) &&
    integer(row.amount_cents, "attempt amount") ===
      attempt.amount.cents &&
    matches(row.state, attempt.state) &&
    matches(row.idempotency_key, attempt.idempotencyKey) &&
    matches(row.created_at_iso, attempt.createdAtIso) &&
    matches(row.updated_at_iso, attempt.updatedAtIso) &&
    nullableMatches(row.provider_environment, attempt.providerEnvironment ?? null) &&
    // M44 前的加密记录没有 session projection；只允许旧 Linkly 且未冻结环境
    // 使用 payload 内的 session 参与强匹配。M44 新记录仍必须逐字段一致。
    (
      nullableMatches(row.provider_session_id, attempt.references.sessionId) ||
      (row.provider_session_id === null &&
        attempt.provider === "linkly-cloud" &&
        !attempt.providerEnvironment)
    ) &&
    nullableMatches(row.provider_acknowledged_at_iso, attempt.providerAcknowledgedAtIso ?? null)
  );
}

async function assertRefundEvidenceBindings(
  connection: SqliteConnectionPort,
  plan: InstallmentProviderAttemptPlan,
  action: PersistedInstallmentAction,
): Promise<void> {
  if (action.action.kind !== "cancel-refund") return;
  const refundMode =
    action.command.kind === "cancel-refund"
      ? action.command.refundMode ?? "original-route"
      : "original-route";
  if (refundMode === "voucher" && plan.cashSettlements.length > 0) {
    throw new Error("Installment voucher refund plan cannot settle cash.");
  }
  for (const entry of [...plan.attempts, ...plan.cashSettlements]) {
    const row = await connection.getFirst<{
      refund_action_id: unknown;
      source_payment_guid: unknown;
      source_attempt_id: unknown;
      method: unknown;
      amount_cents: unknown;
      provider: unknown;
    }>(
      `SELECT item.refund_action_id, evidence.payment_guid AS source_payment_guid,
        evidence.source_attempt_id, evidence.method, evidence.amount_cents,
        evidence.provider
       FROM installment_refund_provenance_items item
       INNER JOIN installment_original_tender_evidence evidence
         ON evidence.evidence_id = item.evidence_id
       WHERE item.refund_action_id = ? AND item.evidence_id = ?`,
      [plan.actionId, entry.originalTenderEvidenceId],
    );
    const provider =
      "attempt" in entry ? entry.attempt.provider : null;
    const method =
      "attempt" in entry
        ? provider === "voucher"
          ? "voucher"
          : "card"
        : "cash";
    // 中文注释：退代金券模式下任何原付款（现金/刷卡/代金券）都只能以退款券 attempt 退回，
    // 原付款证据只需来源、金额一致；原路退仍要求方式与 provider 与原付款逐项一致。
    const voucherRefundMode = refundMode === "voucher";
    if (
      !row ||
      !matches(row.refund_action_id, plan.actionId) ||
      !matches(row.source_payment_guid, entry.sourcePaymentGuid!) ||
      !matches(row.source_attempt_id, entry.sourceAttemptId!) ||
      (voucherRefundMode
        ? provider !== "voucher"
        : !matches(row.method, method)) ||
      integer(row.amount_cents, "refund evidence amount") !==
        ("attempt" in entry
          ? Math.abs(entry.attempt.amount.cents)
          : entry.amountCents) ||
      (!voucherRefundMode &&
        method !== "cash" &&
        !matches(row.provider, provider!))
    ) {
      throw new Error("Installment refund evidence binding conflict.");
    }
  }
}

function assertSamePlan(
  existing: InstallmentProviderAttemptPlan,
  candidate: InstallmentProviderAttemptPlan,
): void {
  if (JSON.stringify(existing) !== JSON.stringify(candidate)) {
    throw new Error("Installment provider plan binding conflict.");
  }
}

function attemptColumns(): string {
  return `SELECT attempt_id, action_id, payment_guid, source_payment_guid,
    original_tender_evidence_id, source_attempt_id, sequence, provider,
    operation, amount_cents, state, idempotency_key, payload_revision,
    protected_payload_ciphertext, created_at_iso, updated_at_iso,
    provider_environment, provider_session_id, provider_acknowledged_at_iso
  FROM installment_provider_attempts`;
}

function cashColumns(): string {
  return `SELECT settlement_id, action_id, payment_guid,
    source_payment_guid, original_tender_evidence_id, source_attempt_id,
    sequence, operation, amount_cents, idempotency_key, state,
    created_at_iso, updated_at_iso
  FROM installment_cash_settlements`;
}

function normalizeReferences(input: unknown): PaymentProviderReferences {
  if (!isRecord(input) || !hasOnlyKeys(input, REFERENCE_KEYS)) {
    throw new TypeError("Provider references are invalid.");
  }
  return Object.freeze({
    checkoutId: optionalText(input.checkoutId, "checkout ID", 2_048),
    paymentId: optionalText(input.paymentId, "payment ID", 2_048),
    sessionId: optionalText(input.sessionId, "session ID", 2_048),
    txnRef: optionalText(input.txnRef, "transaction reference", 2_048),
    rfn: optionalText(input.rfn, "RFN", 2_048),
    voucherReservationToken: optionalText(
      input.voucherReservationToken,
      "voucher protected reference",
      4_096,
    ),
  });
}

function paymentProvider(value: unknown): PaymentAttempt["provider"] {
  if (
    value === "square" ||
    value === "linkly-cloud" ||
    value === "voucher"
  ) {
    return value;
  }
  throw new TypeError("Provider is invalid.");
}

function paymentOperation(value: unknown): PaymentAttempt["operation"] {
  if (value === "purchase" || value === "refund") return value;
  throw new TypeError("Payment operation is invalid.");
}

function paymentState(value: unknown): PaymentAttemptState {
  if (
    value === "Created" ||
    value === "Submitted" ||
    value === "Pending" ||
    value === "Approved" ||
    value === "Declined" ||
    value === "Cancelled" ||
    value === "Unknown"
  ) {
    return value;
  }
  throw new TypeError("Payment state is invalid.");
}

function isLinklyFinal(state: PaymentAttemptState): boolean {
  return state === "Approved" || state === "Declined" || state === "Cancelled";
}

function providerEnvironment(value: unknown): string {
  return strictText(value, "provider environment", 64);
}

function normalizedLinklyAttemptUid(value: unknown): string {
  const compact = strictText(value, "Linkly idempotency key", 512)
    .replaceAll("-", "")
    .toLowerCase();
  if (!/^[0-9a-f]{32}$/u.test(compact)) {
    throw new TypeError("Linkly idempotency key is invalid.");
  }
  return compact;
}

function optionalReceipt(value: unknown): string | null {
  if (value === undefined || value === null) return null;
  if (
    typeof value !== "string" ||
    value.length > 32_768 ||
    value.includes("\u0000")
  ) {
    throw new TypeError("Provider receipt is invalid.");
  }
  return value;
}

function optionalText(
  value: unknown,
  label: string,
  maxLength: number,
): string | null {
  return value === null || value === undefined
    ? null
    : strictText(value, label, maxLength);
}

function uuid(value: unknown, label: string): string {
  const normalized = strictText(value, label, 64).toLowerCase();
  if (
    !/^[0-9a-f]{8}-[0-9a-f]{4}-[1-5][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/u.test(
      normalized,
    )
  ) {
    throw new TypeError(`${label} is invalid.`);
  }
  return normalized;
}

function strictText(
  value: unknown,
  label: string,
  maxLength: number,
): string {
  if (typeof value !== "string") {
    throw new TypeError(`${label} is invalid.`);
  }
  const normalized = value.trim();
  if (
    normalized.length === 0 ||
    normalized.length > maxLength ||
    /[\u0000-\u001f\u007f]/u.test(normalized)
  ) {
    throw new TypeError(`${label} is invalid.`);
  }
  return normalized;
}

function canonicalIso(value: unknown, label: string): string {
  if (typeof value !== "string") {
    throw new TypeError(`${label} is invalid.`);
  }
  const parsed = Date.parse(value);
  if (!Number.isFinite(parsed) || new Date(parsed).toISOString() !== value) {
    throw new TypeError(`${label} must be canonical ISO UTC.`);
  }
  return value;
}

function nextIso(previous: string, candidateInput: string): string {
  const candidate = canonicalIso(
    candidateInput,
    "installment payment update time",
  );
  if (candidate > previous) return candidate;
  return new Date(Date.parse(previous) + 1).toISOString();
}

function positiveInteger(value: unknown, label: string): number {
  const parsed = integer(value, label);
  if (parsed <= 0) throw new TypeError(`${label} must be positive.`);
  return parsed;
}

function nonZeroInteger(value: unknown, label: string): number {
  const parsed = integer(value, label);
  if (parsed === 0) throw new TypeError(`${label} cannot be zero.`);
  return parsed;
}

function nonNegativeInteger(value: unknown, label: string): number {
  const parsed = integer(value, label);
  if (parsed < 0) throw new TypeError(`${label} cannot be negative.`);
  return parsed;
}

function integer(value: unknown, label: string): number {
  const parsed = Number(value);
  if (!Number.isSafeInteger(parsed)) {
    throw new TypeError(`${label} must be a safe integer.`);
  }
  return parsed;
}

function text(value: unknown, label: string): string {
  if (typeof value !== "string" || value.trim().length === 0) {
    throw new Error(`Persisted ${label} is invalid.`);
  }
  return value;
}

function bytes(value: unknown, label: string): Uint8Array {
  if (!(value instanceof Uint8Array) || value.length === 0) {
    throw new Error(`Persisted ${label} is invalid.`);
  }
  return value;
}

function matches(value: unknown, expected: string): boolean {
  return typeof value === "string" && value === expected;
}

function nullableMatches(
  value: unknown,
  expected: string | null,
): boolean {
  return value === expected;
}

function exactKeys(
  value: Record<string, unknown>,
  keys: readonly string[],
): boolean {
  const actual = Object.keys(value);
  return (
    actual.length === keys.length &&
    keys.every((key) => Object.hasOwn(value, key))
  );
}

function hasOnlyKeys(
  value: Record<string, unknown>,
  allowed: ReadonlySet<string>,
): boolean {
  return Object.keys(value).every((key) => allowed.has(key));
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return (
    typeof value === "object" &&
    value !== null &&
    !Array.isArray(value)
  );
}

function invalid<T>(message: string): T {
  throw new TypeError(message);
}

function integrity(
  code:
    | "PROTECTED_MATERIAL_JSON_INVALID"
    | "PROTECTED_MATERIAL_SHAPE_INVALID"
    | "PROTECTED_MATERIAL_VERSION_INVALID"
    | "PROTECTED_MATERIAL_BINDING_MISMATCH",
): ProtectedMaterialIntegrityError {
  return new ProtectedMaterialIntegrityError(code);
}
