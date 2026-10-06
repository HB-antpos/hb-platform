import type {
  PaymentAttempt,
  PaymentProvider,
  PaymentProviderReferences,
} from "../contracts";
import type {
  InstallmentCancelRefundMode,
  InstallmentOriginalTenderEvidence,
  InstallmentRefundProvenanceSnapshot,
} from "../runtime/production-installment-payment-adapter";
import type {
  InstallmentProtectedProvenanceImport,
  InstallmentProtectedTenderImport,
  InstallmentRefundProvenanceVaultPort,
} from "../runtime/production-installment-refund-provenance";

import { ProtectedMaterialIntegrityError } from "@hb/pos-db/core/db/protected-material-integrity-error";
import { SqliteInstallmentActionStore } from "./sqlite-installment-action-store";
import type { SensitivePayloadEncryptor } from "./sqlite-repositories";
import type { SqliteConnectionPort } from "@hb/pos-db/core/db/types";

type ProvenanceScope = Pick<
  InstallmentProtectedProvenanceImport,
  "installmentGuid" | "storeCode" | "requestingDeviceCode"
>;

type SnapshotRow = Readonly<{
  refund_action_id: unknown;
  store_code: unknown;
  device_code: unknown;
  installment_guid: unknown;
  paid_amount_cents: unknown;
}>;

type EvidenceRow = Readonly<{
  refund_action_id: unknown;
  sequence: unknown;
  evidence_id: unknown;
  origin_action_id: unknown;
  store_code: unknown;
  device_code: unknown;
  installment_guid: unknown;
  payment_guid: unknown;
  source_attempt_id: unknown;
  method: unknown;
  amount_cents: unknown;
  provider: unknown;
  provenance: unknown;
  payload_revision: unknown;
  protected_payload_ciphertext: unknown;
}>;

type ImportedTenderEnvelopeV1 = Readonly<{
  format: "hb-pos-installment-imported-tender-v1";
  aad: Readonly<{
    revision: 1;
    refundActionId: string;
    evidenceId: string;
    sourcePaymentGuid: string;
    sourceAttemptId: string;
    installmentGuid: string;
    storeCode: string;
    deviceCode: string;
  }>;
  tender: InstallmentProtectedTenderImport;
}>;

type PreparedImport = Readonly<{
  tender: InstallmentProtectedTenderImport;
  ciphertext: Uint8Array;
}>;

const CARD_TRANSACTION_KEYS = new Set([
  "processor",
  "txnRef",
  "authCode",
  "cardType",
  "cardBin",
  "maskedCardNumber",
  "merchantId",
  "responseCode",
  "responseText",
  "stan",
  "bankDateTime",
  "amount",
  "receiptText",
  "refundReference",
]);

/** Hbpos 付款来源进入此边界后只以安全描述符返回；原引用始终留在密文。 */
export class SqliteInstallmentRefundProvenanceVault
implements InstallmentRefundProvenanceVaultPort {
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

  public async resolve(
    input: ProvenanceScope,
  ): Promise<InstallmentRefundProvenanceSnapshot | null> {
    const scope = normalizeScope(input);
    const action = await this.findCancelAction(this.connection, scope);
    if (!action) return null;
    return this.loadSnapshot(
      this.connection,
      action.actionId,
      scope,
      action.refundMode,
    );
  }

  public async importProtected(
    input: InstallmentProtectedProvenanceImport,
  ): Promise<InstallmentRefundProvenanceSnapshot> {
    const action = await this.findCancelAction(
      this.connection,
      normalizeScope(input),
    );
    if (!action) {
      throw new Error(
        "Installment refund provenance action was not found.",
      );
    }
    // 中文注释：证据严格度以 action 冻结的退款方式为准，不信任调用方自报。
    const { actionId, refundMode } = action;
    const imported = normalizeImport(input, refundMode);
    const scope = normalizeScope(imported);
    const existing = await this.loadProtectedSnapshot(
      this.connection,
      actionId,
      scope,
      refundMode,
    );
    if (existing) {
      assertSameProtectedImport(existing, imported);
      return safeSnapshot(existing);
    }
    const prepared: PreparedImport[] = [];
    for (const tender of imported.tenders) {
      prepared.push({
        tender,
        ciphertext: await encryptImportedTender(
          this.encryptor,
          actionId,
          scope,
          tender,
        ),
      });
    }
    const createdAtIso = canonicalIso(
      this.nowIso(),
      "refund provenance import time",
    );
    return this.connection.withExclusiveTransaction(
      async (transaction) => {
        const current = await this.findCancelAction(transaction, scope);
        if (
          current?.actionId !== actionId ||
          current.refundMode !== refundMode
        ) {
          throw new Error(
            "Installment refund provenance action binding changed.",
          );
        }
        const raced = await this.loadProtectedSnapshot(
          transaction,
          actionId,
          scope,
          refundMode,
        );
        if (raced) {
          assertSameProtectedImport(raced, imported);
          return safeSnapshot(raced);
        }
        await transaction.run(
          `INSERT INTO installment_refund_provenance_snapshots (
            refund_action_id, store_code, device_code, installment_guid,
            paid_amount_cents, created_at_iso
          ) VALUES (?, ?, ?, ?, ?, ?)`,
          [
            actionId,
            scope.storeCode,
            scope.requestingDeviceCode,
            scope.installmentGuid,
            imported.paidAmountCents,
            createdAtIso,
          ],
        );
        for (const [sequence, item] of prepared.entries()) {
          const tender = item.tender;
          await transaction.run(
            `INSERT INTO installment_original_tender_evidence (
              evidence_id, origin_action_id, store_code, device_code,
              installment_guid, payment_guid, source_attempt_id, method,
              amount_cents, provider, provenance, payload_revision,
              protected_payload_ciphertext, created_at_iso
            ) VALUES (?, NULL, ?, ?, ?, ?, ?, ?, ?, ?, ?, 1, ?, ?)`,
            [
              tender.evidenceId,
              scope.storeCode,
              scope.requestingDeviceCode,
              scope.installmentGuid,
              tender.sourcePaymentGuid,
              tender.sourceAttemptId,
              tender.method,
              tender.amountCents,
              tender.provider,
              tender.provenance,
              item.ciphertext,
              createdAtIso,
            ],
          );
          await transaction.run(
            `INSERT INTO installment_refund_provenance_items (
              refund_action_id, sequence, evidence_id,
              source_payment_guid, source_attempt_id
            ) VALUES (?, ?, ?, ?, ?)`,
            [
              actionId,
              sequence,
              tender.evidenceId,
              tender.sourcePaymentGuid,
              tender.sourceAttemptId,
            ],
          );
        }
        return safeSnapshot(imported);
      },
    );
  }

  public async seedRefundAttempt(input: Readonly<{
    evidence: InstallmentOriginalTenderEvidence;
    attempt: PaymentAttempt;
  }>): Promise<PaymentAttempt> {
    const evidence = normalizeEvidence(input.evidence);
    const attempt = normalizeSeedCandidate(input.attempt, evidence);
    const row = await this.connection.getFirst<EvidenceRow>(
      `${evidenceColumns()}
       WHERE evidence.evidence_id = ?
       LIMIT 1`,
      [evidence.evidenceId],
    );
    if (!row) {
      throw new Error("Installment refund evidence was not found.");
    }
    const actionId = strictText(
      row.refund_action_id,
      "refund action ID",
      64,
    );
    const snapshotRow = await this.connection.getFirst<SnapshotRow>(
      `SELECT refund_action_id, store_code, device_code, installment_guid,
        paid_amount_cents
       FROM installment_refund_provenance_snapshots
       WHERE refund_action_id = ?`,
      [actionId],
    );
    if (!snapshotRow) {
      throw integrity("PROTECTED_MATERIAL_BINDING_MISMATCH");
    }
    const scope = scopeFromSnapshot(snapshotRow);
    const current = await this.findCancelAction(this.connection, scope);
    if (current?.actionId !== actionId) {
      throw integrity("PROTECTED_MATERIAL_BINDING_MISMATCH");
    }
    const protectedTender = await this.decodeEvidence(
      row,
      actionId,
      scope,
      current.refundMode,
    );
    const persistedEvidence = normalizeEvidence(protectedTender);
    if (
      JSON.stringify(persistedEvidence) !== JSON.stringify(evidence)
    ) {
      throw integrity("PROTECTED_MATERIAL_BINDING_MISMATCH");
    }
    const references = seededReferences(
      attempt.references,
      protectedTender,
    );
    return Object.freeze({
      ...attempt,
      references,
    });
  }

  private async findCancelAction(
    connection: SqliteConnectionPort,
    scope: ProvenanceScope,
  ): Promise<Readonly<{
    actionId: string;
    refundMode: InstallmentCancelRefundMode;
  }> | null> {
    const rows = await connection.getAll<{ action_id: unknown }>(
      `SELECT action_id
       FROM installment_actions
       WHERE installment_guid = ? AND store_code = ? AND device_code = ?
         AND action_kind = 'cancel-refund' AND resolution IS NULL
       ORDER BY created_at_iso, action_id
       LIMIT 2`,
      [
        scope.installmentGuid,
        scope.storeCode,
        scope.requestingDeviceCode,
      ],
    );
    if (rows.length === 0) return null;
    if (rows.length !== 1) {
      throw new Error(
        "Installment refund provenance action is not unique.",
      );
    }
    const actionId = uuid(rows[0]!.action_id, "refund action ID");
    const action = await this.actions.loadById(actionId);
    if (
      !action ||
      action.action.kind !== "cancel-refund" ||
      action.action.installmentGuid !== scope.installmentGuid ||
      action.storeCode !== scope.storeCode ||
      action.deviceCode !== scope.requestingDeviceCode ||
      action.command.kind !== "cancel-refund"
    ) {
      throw integrity("PROTECTED_MATERIAL_BINDING_MISMATCH");
    }
    return Object.freeze({
      actionId,
      // 旧 action 无 refundMode：按原路退严格校验。
      refundMode: action.command.refundMode ?? "original-route",
    });
  }

  private async loadSnapshot(
    connection: SqliteConnectionPort,
    actionId: string,
    scope: ProvenanceScope,
    refundMode: InstallmentCancelRefundMode,
  ): Promise<InstallmentRefundProvenanceSnapshot | null> {
    const imported = await this.loadProtectedSnapshot(
      connection,
      actionId,
      scope,
      refundMode,
    );
    return imported ? safeSnapshot(imported) : null;
  }

  private async loadProtectedSnapshot(
    connection: SqliteConnectionPort,
    actionId: string,
    scope: ProvenanceScope,
    refundMode: InstallmentCancelRefundMode,
  ): Promise<InstallmentProtectedProvenanceImport | null> {
    const snapshot = await connection.getFirst<SnapshotRow>(
      `SELECT refund_action_id, store_code, device_code, installment_guid,
        paid_amount_cents
       FROM installment_refund_provenance_snapshots
       WHERE refund_action_id = ?`,
      [actionId],
    );
    if (!snapshot) return null;
    const persistedScope = scopeFromSnapshot(snapshot);
    if (
      actionId !==
        uuid(snapshot.refund_action_id, "refund action ID") ||
      JSON.stringify(persistedScope) !== JSON.stringify(scope)
    ) {
      throw integrity("PROTECTED_MATERIAL_BINDING_MISMATCH");
    }
    const rows = await connection.getAll<EvidenceRow>(
      `${evidenceColumns()}
       WHERE item.refund_action_id = ?
       ORDER BY item.sequence, evidence.evidence_id`,
      [actionId],
    );
    if (rows.length === 0) {
      throw integrity("PROTECTED_MATERIAL_SHAPE_INVALID");
    }
    const tenders: InstallmentProtectedTenderImport[] = [];
    for (const row of rows) {
      tenders.push(
        await this.decodeEvidence(row, actionId, scope, refundMode),
      );
    }
    return normalizeImport(
      {
        ...scope,
        paidAmountCents: positiveInteger(
          snapshot.paid_amount_cents,
          "refund paid amount",
        ),
        tenders,
      },
      refundMode,
    );
  }

  private async decodeEvidence(
    row: EvidenceRow,
    actionId: string,
    scope: ProvenanceScope,
    refundMode: InstallmentCancelRefundMode,
  ): Promise<InstallmentProtectedTenderImport> {
    if (
      integer(row.payload_revision, "refund evidence revision") !== 1
    ) {
      throw integrity("PROTECTED_MATERIAL_VERSION_INVALID");
    }
    const raw = await this.encryptor.decrypt(
      bytes(
        row.protected_payload_ciphertext,
        "refund evidence ciphertext",
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
      parsed.format !== "hb-pos-installment-imported-tender-v1" ||
      !isRecord(parsed.aad)
    ) {
      throw integrity("PROTECTED_MATERIAL_SHAPE_INVALID");
    }
    const envelope = parsed as ImportedTenderEnvelopeV1;
    let tender: InstallmentProtectedTenderImport;
    try {
      tender = normalizeProtectedTender(envelope.tender, scope, refundMode);
    } catch (error) {
      if (error instanceof TypeError) {
        throw integrity("PROTECTED_MATERIAL_SHAPE_INVALID");
      }
      throw error;
    }
    if (
      envelope.aad.revision !== 1 ||
      envelope.aad.refundActionId !== actionId ||
      envelope.aad.evidenceId !== tender.evidenceId ||
      envelope.aad.sourcePaymentGuid !== tender.sourcePaymentGuid ||
      envelope.aad.sourceAttemptId !== tender.sourceAttemptId ||
      envelope.aad.installmentGuid !== scope.installmentGuid ||
      envelope.aad.storeCode !== scope.storeCode ||
      envelope.aad.deviceCode !== scope.requestingDeviceCode ||
      !evidenceRowMatches(row, actionId, scope, tender)
    ) {
      throw integrity("PROTECTED_MATERIAL_BINDING_MISMATCH");
    }
    return tender;
  }
}

async function encryptImportedTender(
  encryptor: SensitivePayloadEncryptor,
  actionId: string,
  scope: ProvenanceScope,
  tender: InstallmentProtectedTenderImport,
): Promise<Uint8Array> {
  const envelope: ImportedTenderEnvelopeV1 = Object.freeze({
    format: "hb-pos-installment-imported-tender-v1",
    aad: Object.freeze({
      revision: 1,
      refundActionId: actionId,
      evidenceId: tender.evidenceId,
      sourcePaymentGuid: tender.sourcePaymentGuid,
      sourceAttemptId: tender.sourceAttemptId,
      installmentGuid: scope.installmentGuid,
      storeCode: scope.storeCode,
      deviceCode: scope.requestingDeviceCode,
    }),
    tender,
  });
  const ciphertext = await encryptor.encrypt(JSON.stringify(envelope));
  if (!(ciphertext instanceof Uint8Array) || ciphertext.length === 0) {
    throw new Error("Installment refund evidence encryption failed.");
  }
  return ciphertext;
}

function normalizeImport(
  input: InstallmentProtectedProvenanceImport,
  refundMode: InstallmentCancelRefundMode,
): InstallmentProtectedProvenanceImport {
  if (!isRecord(input) || !Array.isArray(input.tenders)) {
    throw new TypeError("Installment provenance import is invalid.");
  }
  const scope = normalizeScope(input);
  const paidAmountCents = positiveInteger(
    input.paidAmountCents,
    "refund paid amount",
  );
  if (input.tenders.length === 0 || input.tenders.length > 100) {
    throw new TypeError("Installment provenance tenders are invalid.");
  }
  const ids = new Set<string>();
  const paymentGuids = new Set<string>();
  const sourceAttempts = new Set<string>();
  let total = 0;
  const tenders = input.tenders.map((value) => {
    const tender = normalizeProtectedTender(value, scope, refundMode);
    if (
      ids.has(tender.evidenceId) ||
      paymentGuids.has(tender.sourcePaymentGuid) ||
      sourceAttempts.has(tender.sourceAttemptId)
    ) {
      throw new TypeError("Installment provenance tender is duplicate.");
    }
    ids.add(tender.evidenceId);
    paymentGuids.add(tender.sourcePaymentGuid);
    sourceAttempts.add(tender.sourceAttemptId);
    total += tender.amountCents;
    if (!Number.isSafeInteger(total)) {
      throw new TypeError("Installment provenance total is invalid.");
    }
    return tender;
  });
  if (total !== paidAmountCents) {
    throw new TypeError("Installment provenance total does not close.");
  }
  return Object.freeze({
    ...scope,
    paidAmountCents,
    tenders: Object.freeze(tenders),
  });
}

function normalizeProtectedTender(
  input: InstallmentProtectedTenderImport,
  scope: ProvenanceScope,
  refundMode: InstallmentCancelRefundMode,
): InstallmentProtectedTenderImport {
  if (!isRecord(input) || !Array.isArray(input.cardTransactions)) {
    throw new TypeError("Protected installment tender is invalid.");
  }
  const evidence = normalizeEvidence(input);
  if (
    evidence.installmentGuid !== scope.installmentGuid ||
    evidence.provenance !== "hbpos-protected-details"
  ) {
    throw new TypeError("Protected installment tender scope is invalid.");
  }
  const reference = optionalSecret(
    input.reference,
    "protected tender reference",
    4_096,
  );
  const cardTransactions = normalizeCardTransactions(
    input.cardTransactions,
  );
  if (
    (evidence.method === "cash" &&
      (reference !== null || cardTransactions.length !== 0)) ||
    (evidence.method === "voucher" &&
      (reference === null || cardTransactions.length !== 0)) ||
    // 中文注释：退代金券模式不向卡 provider 退款，不要求原卡引用/卡交易；原路退保持严格。
    (evidence.method === "card" &&
      refundMode === "original-route" &&
      (reference === null ||
        cardTransactions.length === 0 ||
        !cardTransactions.some(
          (transaction) =>
            amountCents(transaction.amount) === evidence.amountCents,
        )))
  ) {
    throw new TypeError("Protected installment tender material is invalid.");
  }
  return Object.freeze({
    ...evidence,
    reference,
    cardTransactions,
  });
}

function normalizeEvidence(
  input: InstallmentOriginalTenderEvidence,
): InstallmentOriginalTenderEvidence {
  if (!isRecord(input)) {
    throw new TypeError("Installment tender evidence is invalid.");
  }
  const method =
    input.method === "cash" ||
    input.method === "card" ||
    input.method === "voucher"
      ? input.method
      : invalid<never>("Installment tender method is invalid.");
  const normalizedProvider = paymentProvider(input.provider);
  if (
    (method === "cash" && normalizedProvider !== null) ||
    (method === "voucher" && normalizedProvider !== "voucher") ||
    (method === "card" &&
      normalizedProvider !== "square" &&
      normalizedProvider !== "linkly-cloud")
  ) {
    throw new TypeError("Installment tender provider is invalid.");
  }
  const provenance =
    input.provenance === "local-approved-attempt" ||
    input.provenance === "hbpos-protected-details"
      ? input.provenance
      : invalid<never>("Installment tender provenance is invalid.");
  return Object.freeze({
    evidenceId: strictText(input.evidenceId, "evidence ID", 1_024),
    sourceAttemptId: strictText(
      input.sourceAttemptId,
      "source attempt ID",
      1_024,
    ),
    sourcePaymentGuid: uuid(
      input.sourcePaymentGuid,
      "source payment GUID",
    ),
    installmentGuid: uuid(
      input.installmentGuid,
      "installment GUID",
    ),
    method,
    amountCents: positiveInteger(
      input.amountCents,
      "tender amount",
    ),
    provider: normalizedProvider,
    provenance,
  });
}

function normalizeSeedCandidate(
  input: PaymentAttempt,
  evidence: InstallmentOriginalTenderEvidence,
): PaymentAttempt {
  if (
    !isRecord(input) ||
    !isRecord(input.amount) ||
    input.amount.currency !== "AUD" ||
    input.amount.cents !== -evidence.amountCents ||
    input.operation !== "refund" ||
    input.provider !== evidence.provider ||
    input.orderGuid !== evidence.installmentGuid ||
    input.state !== "Created" ||
    !allReferencesNull(input.references)
  ) {
    throw new TypeError("Installment refund seed attempt is invalid.");
  }
  return Object.freeze({
    ...input,
    attemptId: strictText(input.attemptId, "refund attempt ID", 256),
    idempotencyKey: strictText(
      input.idempotencyKey,
      "refund idempotency key",
      512,
    ),
    orderGuid: uuid(input.orderGuid, "refund installment GUID"),
    amount: Object.freeze({
      currency: "AUD",
      cents: nonZeroInteger(input.amount.cents, "refund amount"),
    }),
    references: emptyReferences(),
    createdAtIso: canonicalIso(
      input.createdAtIso,
      "refund attempt creation time",
    ),
    updatedAtIso: canonicalIso(
      input.updatedAtIso,
      "refund attempt update time",
    ),
    lastErrorCode: null,
    receiptText: null,
    responseCode: null,
  });
}

function seededReferences(
  current: PaymentProviderReferences,
  tender: InstallmentProtectedTenderImport,
): PaymentProviderReferences {
  if (!allReferencesNull(current)) {
    throw new TypeError("Refund seed references must start empty.");
  }
  if (tender.provider === "square") {
    return Object.freeze({
      ...emptyReferences(),
      paymentId: strictText(
        tender.reference,
        "Square payment ID",
        4_096,
      ),
    });
  }
  if (tender.provider === "linkly-cloud") {
    return Object.freeze({
      ...emptyReferences(),
      rfn: strictText(tender.reference, "Linkly RFN", 4_096),
    });
  }
  if (tender.provider === "voucher") return emptyReferences();
  throw new TypeError("Cash evidence cannot seed a provider attempt.");
}

function normalizeScope(input: ProvenanceScope): ProvenanceScope {
  if (!isRecord(input)) {
    throw new TypeError("Installment provenance scope is invalid.");
  }
  return Object.freeze({
    installmentGuid: uuid(input.installmentGuid, "installment GUID"),
    storeCode: strictText(input.storeCode, "store code", 64),
    requestingDeviceCode: strictText(
      input.requestingDeviceCode,
      "requesting device code",
      128,
    ),
  });
}

function scopeFromSnapshot(row: SnapshotRow): ProvenanceScope {
  return Object.freeze({
    installmentGuid: uuid(row.installment_guid, "installment GUID"),
    storeCode: strictText(row.store_code, "store code", 64),
    requestingDeviceCode: strictText(
      row.device_code,
      "requesting device code",
      128,
    ),
  });
}

function safeSnapshot(
  input: InstallmentProtectedProvenanceImport,
): InstallmentRefundProvenanceSnapshot {
  return Object.freeze({
    complete: true,
    installmentGuid: input.installmentGuid,
    storeCode: input.storeCode,
    requestingDeviceCode: input.requestingDeviceCode,
    paidAmountCents: input.paidAmountCents,
    tenders: Object.freeze(
      input.tenders.map((tender) =>
        Object.freeze({
          evidenceId: tender.evidenceId,
          sourceAttemptId: tender.sourceAttemptId,
          sourcePaymentGuid: tender.sourcePaymentGuid,
          installmentGuid: tender.installmentGuid,
          method: tender.method,
          amountCents: tender.amountCents,
          provider: tender.provider,
          provenance: tender.provenance,
        }),
      ),
    ),
  });
}

function assertSameProtectedImport(
  existing: InstallmentProtectedProvenanceImport,
  candidate: InstallmentProtectedProvenanceImport,
): void {
  if (JSON.stringify(existing) !== JSON.stringify(candidate)) {
    throw new Error("Installment refund provenance binding conflict.");
  }
}

function evidenceRowMatches(
  row: EvidenceRow,
  actionId: string,
  scope: ProvenanceScope,
  tender: InstallmentProtectedTenderImport,
): boolean {
  return (
    matches(row.refund_action_id, actionId) &&
    row.origin_action_id === null &&
    matches(row.store_code, scope.storeCode) &&
    matches(row.device_code, scope.requestingDeviceCode) &&
    matches(row.installment_guid, scope.installmentGuid) &&
    matches(row.evidence_id, tender.evidenceId) &&
    matches(row.payment_guid, tender.sourcePaymentGuid) &&
    matches(row.source_attempt_id, tender.sourceAttemptId) &&
    matches(row.method, tender.method) &&
    integer(row.amount_cents, "evidence amount") ===
      tender.amountCents &&
    row.provider === tender.provider &&
    matches(row.provenance, tender.provenance)
  );
}

function evidenceColumns(): string {
  return `SELECT item.refund_action_id, item.sequence,
    evidence.evidence_id, evidence.origin_action_id, evidence.store_code,
    evidence.device_code, evidence.installment_guid,
    evidence.payment_guid, evidence.source_attempt_id, evidence.method,
    evidence.amount_cents, evidence.provider, evidence.provenance,
    evidence.payload_revision, evidence.protected_payload_ciphertext
  FROM installment_refund_provenance_items item
  INNER JOIN installment_original_tender_evidence evidence
    ON evidence.evidence_id = item.evidence_id`;
}

function normalizeCardTransactions(
  input: InstallmentProtectedTenderImport["cardTransactions"],
): InstallmentProtectedTenderImport["cardTransactions"] {
  if (!Array.isArray(input) || input.length > 32) {
    throw new TypeError("Protected card transactions are invalid.");
  }
  return Object.freeze(
    input.map((value) => {
      if (
        !isRecord(value) ||
        !Object.keys(value).every((key) =>
          CARD_TRANSACTION_KEYS.has(key),
        )
      ) {
        throw new TypeError("Protected card transaction is invalid.");
      }
      return Object.freeze({
        processor: nullableText(value.processor, 128),
        txnRef: nullableText(value.txnRef, 1_024),
        authCode: nullableText(value.authCode, 512),
        cardType: nullableText(value.cardType, 128),
        cardBin:
          value.cardBin === null || value.cardBin === undefined
            ? null
            : nonNegativeInteger(value.cardBin, "card BIN"),
        maskedCardNumber: nullableText(
          value.maskedCardNumber,
          128,
        ),
        merchantId: nullableText(value.merchantId, 512),
        responseCode: nullableText(value.responseCode, 128),
        responseText: nullableText(value.responseText, 1_024),
        stan: nullableText(value.stan, 512),
        bankDateTime: nullableIso(value.bankDateTime),
        amount: finiteAmount(value.amount),
        receiptText: nullableSecret(value.receiptText, 16_384),
        refundReference: nullableSecret(
          value.refundReference,
          2_048,
        ),
      });
    }),
  );
}

function paymentProvider(value: unknown): PaymentProvider | null {
  if (
    value === null ||
    value === "square" ||
    value === "linkly-cloud" ||
    value === "voucher"
  ) {
    return value;
  }
  throw new TypeError("Payment provider is invalid.");
}

function allReferencesNull(value: unknown): boolean {
  return (
    isRecord(value) &&
    value.checkoutId === null &&
    value.paymentId === null &&
    value.sessionId === null &&
    value.txnRef === null &&
    value.rfn === null &&
    value.voucherReservationToken === null
  );
}

function emptyReferences(): PaymentProviderReferences {
  return Object.freeze({
    checkoutId: null,
    paymentId: null,
    sessionId: null,
    txnRef: null,
    rfn: null,
    voucherReservationToken: null,
  });
}

function amountCents(value: unknown): number | null {
  if (typeof value !== "number" || !Number.isFinite(value)) return null;
  const scaled = value * 100;
  const rounded = Math.round(scaled);
  return Number.isSafeInteger(rounded) &&
    Math.abs(scaled - rounded) <= 1e-7
    ? rounded
    : null;
}

function finiteAmount(value: unknown): number {
  if (
    typeof value !== "number" ||
    !Number.isFinite(value) ||
    amountCents(value) === null
  ) {
    throw new TypeError("Card transaction amount is invalid.");
  }
  return value;
}

function nullableIso(value: unknown): string | null {
  if (value === null || value === undefined) return null;
  if (typeof value !== "string") {
    throw new TypeError("Card transaction time is invalid.");
  }
  const parsed = Date.parse(value);
  if (!Number.isFinite(parsed)) {
    throw new TypeError("Card transaction time is invalid.");
  }
  return new Date(parsed).toISOString();
}

function nullableText(value: unknown, maxLength: number): string | null {
  return value === null || value === undefined
    ? null
    : strictText(value, "protected card value", maxLength);
}

function nullableSecret(
  value: unknown,
  maxLength: number,
): string | null {
  if (value === null || value === undefined) return null;
  if (
    typeof value !== "string" ||
    value.length > maxLength ||
    value.includes("\u0000")
  ) {
    throw new TypeError("Protected card material is invalid.");
  }
  return value;
}

function optionalSecret(
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

function bytes(value: unknown, label: string): Uint8Array {
  if (!(value instanceof Uint8Array) || value.length === 0) {
    throw new Error(`Persisted ${label} is invalid.`);
  }
  return value;
}

function matches(value: unknown, expected: string): boolean {
  return typeof value === "string" && value === expected;
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
