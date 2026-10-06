import type { InstallmentSnapshot } from "@hb/pos-domain/core/contracts/installments";
import type {
  InstallmentActionCommand,
  InstallmentActionState,
  InstallmentActionStorePort,
  InstallmentPaymentAction,
  PersistedInstallmentAction,
  PersistedInstallmentLifecycleAction,
} from "../runtime/production-installment-runtime";

import {
  prepareCommittedInstallmentSnapshotUpsert,
  type SqliteInstallmentSnapshotRepository,
} from "./sqlite-installment-snapshot-repository";
import type { SensitivePayloadEncryptor } from "./sqlite-repositories";
import type { SqliteConnectionPort } from "@hb/pos-db/core/db/types";

export type {
  InstallmentActionState,
  InstallmentActionStorePort,
  PersistedInstallmentAction,
};

export const INSTALLMENT_ACTION_PAYLOAD_REVISION = 1;
export const INSTALLMENT_LIFECYCLE_PAYLOAD_REVISION = 1;

type TerminalScope = Parameters<
  InstallmentActionStorePort["loadBlocking"]
>[0];

type ActionRow = Readonly<{
  action_id: unknown;
  store_code: unknown;
  device_code: unknown;
  installment_guid: unknown;
  action_kind: unknown;
  idempotency_key: unknown;
  payment_guid: unknown;
  payment_method: unknown;
  amount_cents: unknown;
  state: unknown;
  resolution: unknown;
  resolution_code: unknown;
  payload_revision: unknown;
  command_ciphertext: unknown;
}>;

type ActionEnvelopeV1 = Readonly<{
  format: "hb-pos-installment-action-v1";
  aad: Readonly<{
    revision: 1;
    storeCode: string;
    deviceCode: string;
    actionId: string;
  }>;
  action: InstallmentPaymentAction;
  command: InstallmentActionCommand;
  intentFingerprint: string;
}>;

type LifecycleRow = Readonly<{
  operation_guid: unknown;
  store_code: unknown;
  device_code: unknown;
  installment_guid: unknown;
  action_kind: unknown;
  idempotency_key: unknown;
  resolution: unknown;
  payload_revision: unknown;
  command_ciphertext: unknown;
}>;

type LifecycleEnvelopeV1 = Readonly<{
  format: "hb-pos-installment-lifecycle-v1";
  aad: Readonly<{
    revision: 1;
    storeCode: string;
    deviceCode: string;
    operationGuid: string;
  }>;
  originalDeviceCode: string;
  command: PersistedInstallmentLifecycleAction["command"];
  intentFingerprint: string;
}>;

export class SqliteInstallmentActionStore
  implements InstallmentActionStorePort
{
  public constructor(
    private readonly connection: SqliteConnectionPort,
    private readonly encryptor: SensitivePayloadEncryptor,
    private readonly nowIso: () => string,
  ) {}

  public async loadBlocking(
    terminal: TerminalScope,
  ): Promise<PersistedInstallmentAction | null> {
    const scope = normalizeTerminal(terminal);
    const rows = await selectBlockingRows(this.connection, scope);
    if (rows.length === 0) return null;
    if (rows.length !== 1) {
      throw new Error(
        "Persisted installment action terminal uniqueness is invalid.",
      );
    }
    return this.readRow(rows[0]!);
  }

  /** 已经完成业务但尚未 ACK 的 Linkly attempt 仍要挡住下一单，且只允许 ACK 恢复。 */
  public async loadProviderAcknowledgementPending(
    terminalInput: TerminalScope,
  ): Promise<PersistedInstallmentAction | null> {
    const terminal = normalizeTerminal(terminalInput);
    const row = await this.connection.getFirst<ActionRow>(
      `${selectColumns()}
       WHERE store_code = ? AND device_code = ?
         AND resolution IN ('Completed', 'Declined')
         AND EXISTS (
           SELECT 1 FROM installment_provider_attempts attempts
           WHERE attempts.action_id = installment_actions.action_id
             AND attempts.provider = 'linkly-cloud'
             AND attempts.provider_environment IS NOT NULL
             AND attempts.state IN ('Approved', 'Declined', 'Cancelled')
             AND (attempts.state = 'Approved' OR attempts.provider_session_id IS NOT NULL)
             AND attempts.provider_acknowledged_at_iso IS NULL
         )
       ORDER BY resolved_at_iso ASC, action_id ASC LIMIT 1`,
      [terminal.storeCode, terminal.deviceCode],
    );
    // 中文注释：按稳定 actionId 读取终态只用于账本证明/ACK；blocking 查询仍拒绝已解决 action。
    return row ? this.readBoundActionRow(row) : null;
  }

  public async loadLifecycleBlocking(
    terminal: TerminalScope,
  ): Promise<PersistedInstallmentLifecycleAction | null> {
    const scope = normalizeTerminal(terminal);
    const rows = await selectLifecycleBlockingRows(this.connection, scope);
    if (rows.length === 0) return null;
    if (rows.length !== 1) {
      throw new Error(
        "Persisted installment lifecycle terminal uniqueness is invalid.",
      );
    }
    return this.readLifecycleRow(rows[0]!);
  }

  /** provider 恢复按稳定 actionId 读取；终态事实也必须继续可验证。 */
  public async loadById(
    actionIdInput: string,
  ): Promise<PersistedInstallmentAction | null> {
    const actionId = uuid(actionIdInput, "action ID");
    const row = await this.connection.getFirst<ActionRow>(
      `${selectColumns()} WHERE action_id = ? LIMIT 1`,
      [actionId],
    );
    // 中文注释：按稳定 actionId 读取终态只用于账本证明/ACK；blocking 查询仍拒绝已解决 action。
    return row ? this.readBoundActionRow(row) : null;
  }

  public async createIfNone(
    candidateValue: PersistedInstallmentAction,
  ): Promise<Readonly<{
    created: boolean;
    action: PersistedInstallmentAction;
  }>> {
    const candidate = normalizePersistedAction(candidateValue);
    if (candidate.state !== "Created") {
      throw new TypeError(
        "New installment action state must be Created.",
      );
    }
    return this.connection.withExclusiveTransaction(
      async (transaction) => {
        const scope = {
          storeCode: candidate.storeCode,
          deviceCode: candidate.deviceCode,
        };
        if ((await selectLifecycleBlockingRows(transaction, scope)).length > 0) {
          throw new Error(
            "A persisted installment lifecycle action blocks payment creation.",
          );
        }
        const existing = await selectBlockingRows(transaction, scope);
        if (existing.length > 1) {
          throw new Error(
            "Persisted installment action terminal uniqueness is invalid.",
          );
        }
        if (existing.length === 1) {
          // 旧 blocking 必须先成功解密；坏事实不能被新 action 覆盖。
          return Object.freeze({
            created: false,
            action: await this.readRow(existing[0]!),
          });
        }
        const ciphertext = await encryptEnvelope(
          this.encryptor,
          createEnvelope(candidate),
        );
        const timestamp = strictIso(
          this.nowIso(),
          "installment action creation time",
        );
        const action = candidate.action;
        await transaction.run(
          `INSERT INTO installment_actions (
            action_id, store_code, device_code, installment_guid,
            action_kind, idempotency_key, payment_guid, payment_method,
            amount_cents, state, resolution, payload_revision,
            command_ciphertext, created_at_iso, updated_at_iso,
            resolved_at_iso
          ) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, NULL, ?, ?, ?, ?, NULL)`,
          [
            action.actionId,
            candidate.storeCode,
            candidate.deviceCode,
            action.installmentGuid,
            action.kind,
            action.idempotencyKey,
            action.paymentGuid,
            action.method,
            action.amountCents,
            candidate.state,
            INSTALLMENT_ACTION_PAYLOAD_REVISION,
            ciphertext,
            timestamp,
            timestamp,
          ],
        );
        return Object.freeze({ created: true, action: candidate });
      },
    );
  }

  public async createLifecycleIfNone(
    candidateValue: PersistedInstallmentLifecycleAction,
  ): Promise<Readonly<{
    created: boolean;
    action: PersistedInstallmentLifecycleAction;
  }>> {
    const candidate = normalizePersistedLifecycleAction(candidateValue);
    return this.connection.withExclusiveTransaction(async (transaction) => {
      const scope = {
        storeCode: candidate.storeCode,
        deviceCode: candidate.deviceCode,
      };
      if ((await selectBlockingRows(transaction, scope)).length > 0) {
        throw new Error(
          "A persisted installment payment action blocks lifecycle creation.",
        );
      }
      const existing = await selectLifecycleBlockingRows(transaction, scope);
      if (existing.length > 1) {
        throw new Error(
          "Persisted installment lifecycle terminal uniqueness is invalid.",
        );
      }
      if (existing.length === 1) {
        return Object.freeze({
          created: false,
          action: await this.readLifecycleRow(existing[0]!),
        });
      }
      const ciphertext = await encryptLifecycleEnvelope(
        this.encryptor,
        createLifecycleEnvelope(candidate),
      );
      const timestamp = strictIso(
        this.nowIso(),
        "installment lifecycle creation time",
      );
      await transaction.run(
        `INSERT INTO installment_lifecycle_actions (
          operation_guid, store_code, device_code, installment_guid,
          action_kind, idempotency_key, resolution, payload_revision,
          command_ciphertext, created_at_iso, updated_at_iso, resolved_at_iso
        ) VALUES (?, ?, ?, ?, ?, ?, NULL, ?, ?, ?, ?, NULL)`,
        [
          candidate.operationGuid,
          candidate.storeCode,
          candidate.deviceCode,
          candidate.installmentGuid,
          candidate.kind,
          candidate.idempotencyKey,
          INSTALLMENT_LIFECYCLE_PAYLOAD_REVISION,
          ciphertext,
          timestamp,
          timestamp,
        ],
      );
      return Object.freeze({ created: true, action: candidate });
    });
  }

  public async completeLifecycle(
    input: Parameters<InstallmentActionStorePort["completeLifecycle"]>[0],
  ): Promise<void> {
    const scope = normalizeTerminal(input.terminal);
    const operationGuid = uuid(input.operationGuid, "lifecycle operation GUID");
    const timestamp = strictIso(
      this.nowIso(),
      "installment lifecycle resolution time",
    );
    await this.connection.withExclusiveTransaction(async (transaction) => {
      const row = await selectLifecycleRow(
        transaction,
        scope,
        operationGuid,
      );
      if (row === null) {
        throw new Error("Installment lifecycle resolution CAS failed.");
      }
      // 坏密文或被换绑的 row 不能被直接标成成功。
      await this.readLifecycleRow(row);
      const result = await transaction.run(
        `UPDATE installment_lifecycle_actions
         SET resolution = 'Completed', resolved_at_iso = ?, updated_at_iso = ?
         WHERE operation_guid = ? AND store_code = ? AND device_code = ?
           AND resolution IS NULL`,
        [
          timestamp,
          timestamp,
          operationGuid,
          scope.storeCode,
          scope.deviceCode,
        ],
      );
      if (result.changes !== 1) {
        throw new Error("Installment lifecycle resolution CAS failed.");
      }
    });
  }

  public async transition(
    input: Parameters<InstallmentActionStorePort["transition"]>[0],
  ): Promise<PersistedInstallmentAction> {
    const scope = normalizeTerminal(input.terminal);
    const actionId = uuid(input.actionId, "installment action ID");
    const expectedState = state(input.expectedState);
    const nextState = state(input.nextState);
    if (!allowedTransition(expectedState, nextState)) {
      throw new Error("Installment action state transition is invalid.");
    }
    const timestamp = strictIso(
      this.nowIso(),
      "installment action transition time",
    );
    return this.connection.withExclusiveTransaction(
      async (transaction) => {
        const current = await this.requireBlocking(
          transaction,
          scope,
          actionId,
        );
        if (current.state !== expectedState) {
          throw new Error("Installment action state CAS failed.");
        }
        const result = await transaction.run(
          `UPDATE installment_actions
           SET state = ?, updated_at_iso = ?
           WHERE action_id = ? AND store_code = ? AND device_code = ?
             AND state = ? AND resolution IS NULL`,
          [
            nextState,
            timestamp,
            actionId,
            scope.storeCode,
            scope.deviceCode,
            expectedState,
          ],
        );
        if (result.changes !== 1) {
          throw new Error("Installment action state CAS failed.");
        }
        const row = await selectActionRow(
          transaction,
          scope,
          actionId,
        );
        if (row === null) {
          throw new Error("Installment action state CAS failed.");
        }
        return this.readRow(row);
      },
    );
  }

  public decline(
    input: Parameters<InstallmentActionStorePort["decline"]>[0],
  ): Promise<void> {
    if (
      input.expectedState !== "ProviderPending" &&
      input.expectedState !== "Unknown"
    ) {
      throw new TypeError(
        "Installment action decline state is invalid.",
      );
    }
    return this.resolve(
      input.actionId,
      input.terminal,
      input.expectedState,
      "Declined",
    );
  }

  public async finalizeCreatedFailure(
    input: Parameters<
      NonNullable<InstallmentActionStorePort["finalizeCreatedFailure"]>
    >[0],
  ): Promise<void> {
    if (
      input.reason !== "ClaimBusy" &&
      input.reason !== "ClaimMismatch" &&
      input.reason !== "ClaimReleased" &&
      input.reason !== "PaymentMethodUnsupported"
    ) {
      throw new TypeError("Installment created failure reason is invalid.");
    }
    const scope = normalizeTerminal(input.terminal);
    const actionId = uuid(input.actionId, "installment action ID");
    const timestamp = strictIso(
      this.nowIso(),
      "installment action resolution time",
    );
    await this.connection.withExclusiveTransaction(
      async (transaction) => {
        const current = await this.requireBlocking(
          transaction,
          scope,
          actionId,
        );
        if (current.state !== "Created") {
          throw new Error(
            "Installment created failure state CAS failed.",
          );
        }

        // 中文注释：既有 schema 只允许 ProviderPending 被 Declined。两次写入处于
        // 同一 BEGIN IMMEDIATE 事务，外部永远观察不到 ProviderPending 中间态；
        // 此路径也从未绑定或调用 provider，只用于保留不可删除的终态事实。
        const transitioned = await transaction.run(
          `UPDATE installment_actions
           SET state = 'ProviderPending', updated_at_iso = ?
           WHERE action_id = ? AND store_code = ? AND device_code = ?
             AND state = 'Created' AND resolution IS NULL`,
          [
            timestamp,
            actionId,
            scope.storeCode,
            scope.deviceCode,
          ],
        );
        if (transitioned.changes !== 1) {
          throw new Error(
            "Installment created failure state CAS failed.",
          );
        }
        const resolved = await transaction.run(
          `UPDATE installment_actions
           SET resolution = 'Declined',
               resolution_code = ?,
               resolved_at_iso = ?, updated_at_iso = ?
           WHERE action_id = ? AND store_code = ? AND device_code = ?
             AND state = 'ProviderPending' AND resolution IS NULL`,
          [
            input.reason === "PaymentMethodUnsupported"
              ? "PaymentMethodUnsupported"
              : null,
            timestamp,
            timestamp,
            actionId,
            scope.storeCode,
            scope.deviceCode,
          ],
        );
        if (resolved.changes !== 1) {
          throw new Error(
            "Installment created failure resolution CAS failed.",
          );
        }

        if (
          input.reason === "ClaimMismatch" ||
          input.reason === "PaymentMethodUnsupported"
        ) {
          const paymentMethodUnsupported =
            input.reason === "PaymentMethodUnsupported";
          const audit = await transaction.run(
            `INSERT INTO audit_events (
               event_id, event_type, occurred_at_iso, order_guid,
               correlation_id, payload_json, uploaded_at_iso,
               scope_store_code, scope_device_code, external_order_guid
             ) VALUES (?, ?, ?, NULL, ?, ?, NULL, ?, ?, NULL)`,
            [
              actionId,
              paymentMethodUnsupported
                ? "INSTALLMENT_REPAYMENT_PAYMENT_METHOD_UNSUPPORTED"
                : "INSTALLMENT_REPAYMENT_CLAIM_REVIEW",
              timestamp,
              actionId,
              JSON.stringify({
                outcome: paymentMethodUnsupported
                  ? "PaymentMethodUnsupported"
                  : "RequiresReview",
                reason: paymentMethodUnsupported
                  ? "Card installment repayment is unsupported."
                  : "Repayment claim provider binding mismatch.",
                status: "Failed",
              }),
              scope.storeCode,
              scope.deviceCode,
            ],
          );
          if (audit.changes !== 1) {
            throw new Error(
              "Installment claim review audit write failed.",
            );
          }
        }
      },
    );
  }

  public complete(
    input: Parameters<InstallmentActionStorePort["complete"]>[0],
  ): Promise<void> {
    if (input.expectedState !== "BackendPending") {
      throw new TypeError(
        "Installment action completion state is invalid.",
      );
    }
    return this.resolve(
      input.actionId,
      input.terminal,
      input.expectedState,
      "Completed",
    );
  }

  /**
   * committed repayment 的快照和 action resolution 必须属于同一个 SQLite exclusive
   * transaction。快照校验、敏感字段加密由仓储在 BEGIN 前完成；任一事务内写失败都会
   * 让 BackendPending action 保持可恢复。
   */
  public async completeCommittedRepaymentWithSnapshot(
    input: Readonly<{
      actionId: string;
      expectedState: "BackendPending";
      terminal: TerminalScope;
      snapshot: InstallmentSnapshot;
    }>,
    snapshotRepository: SqliteInstallmentSnapshotRepository,
  ): Promise<void> {
    if (input.expectedState !== "BackendPending") {
      throw new TypeError(
        "Installment action completion state is invalid.",
      );
    }
    const scope = normalizeTerminal(input.terminal);
    const actionId = uuid(input.actionId, "installment action ID");
    const prepared = await prepareCommittedInstallmentSnapshotUpsert(
      snapshotRepository,
      this.connection,
      this.encryptor,
      scope.storeCode,
      input.snapshot,
    );
    const timestamp = strictIso(
      this.nowIso(),
      "installment action resolution time",
    );

    await this.connection.withExclusiveTransaction(async (transaction) => {
      const row = await selectAnyActionRow(
        transaction,
        scope,
        actionId,
      );
      if (row === null) {
        throw new Error("Installment action resolution state CAS failed.");
      }
      const current = await this.readBoundActionRow(row);
      if (current.state !== "BackendPending") {
        throw new Error("Installment action resolution state CAS failed.");
      }
      if (
        current.action.kind !== "repayment" ||
        current.action.installmentGuid !== prepared.installmentGuid
      ) {
        throw new Error("Committed repayment action identity mismatch.");
      }

      if (row.resolution === "Completed") {
        if (!(await prepared.matchesPersistedInTransaction(transaction))) {
          throw new Error(
            "Committed repayment idempotent snapshot mismatch.",
          );
        }
        return;
      }
      if (row.resolution !== null) {
        throw new Error("Installment action resolution state CAS failed.");
      }

      await prepared.upsertInTransaction(transaction);
      const result = await transaction.run(
        `UPDATE installment_actions
         SET resolution = 'Completed', resolved_at_iso = ?, updated_at_iso = ?
         WHERE action_id = ? AND store_code = ? AND device_code = ?
           AND state = 'BackendPending' AND resolution IS NULL`,
        [
          timestamp,
          timestamp,
          actionId,
          scope.storeCode,
          scope.deviceCode,
        ],
      );
      if (result.changes !== 1) {
        throw new Error("Installment action resolution state CAS failed.");
      }
    });
  }

  private async resolve(
    actionIdValue: string,
    terminal: TerminalScope,
    expectedState: InstallmentActionState,
    resolution: "Declined" | "Completed",
  ): Promise<void> {
    const scope = normalizeTerminal(terminal);
    const actionId = uuid(actionIdValue, "installment action ID");
    const timestamp = strictIso(
      this.nowIso(),
      "installment action resolution time",
    );
    await this.connection.withExclusiveTransaction(
      async (transaction) => {
        await this.resolveInTransaction(
          transaction,
          scope,
          actionId,
          expectedState,
          resolution,
          undefined,
          timestamp,
        );
      },
    );
  }

  private async resolveInTransaction(
    transaction: SqliteConnectionPort,
    scope: TerminalScope,
    actionId: string,
    expectedState: InstallmentActionState,
    resolution: "Declined" | "Completed",
    expectedInstallmentGuid: string | undefined,
    timestamp: string,
  ): Promise<void> {
    const current = await this.requireBlocking(
      transaction,
      scope,
      actionId,
    );
    if (current.state !== expectedState) {
      throw new Error(
        "Installment action resolution state CAS failed.",
      );
    }
    if (
      expectedInstallmentGuid !== undefined &&
      (current.action.kind !== "repayment" ||
        current.action.installmentGuid !== expectedInstallmentGuid)
    ) {
      throw new Error("Committed repayment action identity mismatch.");
    }
    const result = await transaction.run(
      `UPDATE installment_actions
       SET resolution = ?, resolved_at_iso = ?, updated_at_iso = ?
       WHERE action_id = ? AND store_code = ? AND device_code = ?
         AND state = ? AND resolution IS NULL`,
      [
        resolution,
        timestamp,
        timestamp,
        actionId,
        scope.storeCode,
        scope.deviceCode,
        expectedState,
      ],
    );
    if (result.changes !== 1) {
      throw new Error(
        "Installment action resolution state CAS failed.",
      );
    }
  }

  private async requireBlocking(
    connection: SqliteConnectionPort,
    scope: TerminalScope,
    actionId: string,
  ): Promise<PersistedInstallmentAction> {
    const row = await selectActionRow(connection, scope, actionId);
    if (row === null) {
      throw new Error("Installment action state CAS failed.");
    }
    return this.readRow(row);
  }

  private async readRow(row: ActionRow): Promise<PersistedInstallmentAction> {
    if (row.resolution !== null) {
      throw new Error(
        "Persisted installment action ciphertext or binding is invalid.",
      );
    }
    return this.readBoundActionRow(row);
  }

  private async readBoundActionRow(
    row: ActionRow,
  ): Promise<PersistedInstallmentAction> {
    try {
      const action = actionFromRow(row);
      const persistedState = state(row.state);
      const revision = integer(row.payload_revision, "payload revision");
      if (revision !== INSTALLMENT_ACTION_PAYLOAD_REVISION) {
        throw new Error("revision");
      }
      const storeCode = identity(row.store_code, "store code", 50);
      const deviceCode = identity(row.device_code, "device code", 128);
      const ciphertext = bytes(row.command_ciphertext);
      const envelope = await decryptEnvelope(
        this.encryptor,
        ciphertext,
      );
      if (
        envelope.aad.revision !== revision ||
        envelope.aad.storeCode !== storeCode ||
        envelope.aad.deviceCode !== deviceCode ||
        envelope.aad.actionId !== action.actionId ||
        JSON.stringify(envelope.action) !== JSON.stringify(action)
      ) {
        throw new Error("AAD");
      }
      return normalizePersistedAction({
        action,
        command: envelope.command,
        deviceCode,
        intentFingerprint: envelope.intentFingerprint,
        state: persistedState,
        storeCode,
      });
    } catch {
      throw new Error(
        "Persisted installment action ciphertext or binding is invalid.",
      );
    }
  }

  private async readLifecycleRow(
    row: LifecycleRow,
  ): Promise<PersistedInstallmentLifecycleAction> {
    try {
      if (row.resolution !== null) throw new Error("resolved");
      const revision = integer(row.payload_revision, "payload revision");
      if (revision !== INSTALLMENT_LIFECYCLE_PAYLOAD_REVISION) {
        throw new Error("revision");
      }
      const operationGuid = uuid(
        row.operation_guid,
        "lifecycle operation GUID",
      );
      const storeCode = identity(row.store_code, "store code", 50);
      const deviceCode = identity(row.device_code, "device code", 128);
      const installmentGuid = uuid(
        row.installment_guid,
        "lifecycle installment GUID",
      );
      const kind = lifecycleKind(row.action_kind);
      const idempotencyKey = uuid(
        row.idempotency_key,
        "lifecycle idempotency key",
      );
      const envelope = await decryptLifecycleEnvelope(
        this.encryptor,
        bytes(row.command_ciphertext),
      );
      if (
        envelope.aad.revision !== revision ||
        envelope.aad.storeCode !== storeCode ||
        envelope.aad.deviceCode !== deviceCode ||
        envelope.aad.operationGuid !== operationGuid
      ) {
        throw new Error("AAD");
      }
      return normalizePersistedLifecycleAction({
        operationGuid,
        idempotencyKey,
        kind,
        installmentGuid,
        storeCode,
        deviceCode,
        originalDeviceCode: envelope.originalDeviceCode,
        command: envelope.command,
        intentFingerprint: envelope.intentFingerprint,
      });
    } catch {
      throw new Error(
        "Persisted installment lifecycle ciphertext or binding is invalid.",
      );
    }
  }
}

async function selectBlockingRows(
  connection: SqliteConnectionPort,
  scope: TerminalScope,
): Promise<readonly ActionRow[]> {
  return connection.getAll<ActionRow>(
    `${selectColumns()}
     WHERE store_code = ? AND device_code = ? AND resolution IS NULL
     ORDER BY created_at_iso, action_id LIMIT 2`,
    [scope.storeCode, scope.deviceCode],
  );
}

async function selectActionRow(
  connection: SqliteConnectionPort,
  scope: TerminalScope,
  actionId: string,
): Promise<ActionRow | null> {
  return connection.getFirst<ActionRow>(
    `${selectColumns()}
     WHERE action_id = ? AND store_code = ? AND device_code = ?
       AND resolution IS NULL LIMIT 1`,
    [actionId, scope.storeCode, scope.deviceCode],
  );
}

async function selectAnyActionRow(
  connection: SqliteConnectionPort,
  scope: TerminalScope,
  actionId: string,
): Promise<ActionRow | null> {
  return connection.getFirst<ActionRow>(
    `${selectColumns()}
     WHERE action_id = ? AND store_code = ? AND device_code = ? LIMIT 1`,
    [actionId, scope.storeCode, scope.deviceCode],
  );
}

async function selectLifecycleBlockingRows(
  connection: SqliteConnectionPort,
  scope: TerminalScope,
): Promise<readonly LifecycleRow[]> {
  return connection.getAll<LifecycleRow>(
    `${selectLifecycleColumns()}
     WHERE store_code = ? AND device_code = ? AND resolution IS NULL
     ORDER BY created_at_iso, operation_guid LIMIT 2`,
    [scope.storeCode, scope.deviceCode],
  );
}

async function selectLifecycleRow(
  connection: SqliteConnectionPort,
  scope: TerminalScope,
  operationGuid: string,
): Promise<LifecycleRow | null> {
  return connection.getFirst<LifecycleRow>(
    `${selectLifecycleColumns()}
     WHERE operation_guid = ? AND store_code = ? AND device_code = ?
       AND resolution IS NULL LIMIT 1`,
    [operationGuid, scope.storeCode, scope.deviceCode],
  );
}

function selectLifecycleColumns(): string {
  return `SELECT operation_guid, store_code, device_code,
    installment_guid, action_kind, idempotency_key, resolution,
    payload_revision, command_ciphertext
  FROM installment_lifecycle_actions`;
}

function selectColumns(): string {
  return `SELECT action_id, store_code, device_code, installment_guid,
    action_kind, idempotency_key, payment_guid, payment_method,
    amount_cents, state, resolution, resolution_code, payload_revision,
    command_ciphertext
  FROM installment_actions`;
}

function createEnvelope(
  value: PersistedInstallmentAction,
): ActionEnvelopeV1 {
  return Object.freeze({
    format: "hb-pos-installment-action-v1",
    aad: Object.freeze({
      revision: INSTALLMENT_ACTION_PAYLOAD_REVISION,
      storeCode: value.storeCode,
      deviceCode: value.deviceCode,
      actionId: value.action.actionId,
    }),
    action: value.action,
    command: value.command,
    intentFingerprint: value.intentFingerprint,
  });
}

function createLifecycleEnvelope(
  value: PersistedInstallmentLifecycleAction,
): LifecycleEnvelopeV1 {
  return Object.freeze({
    format: "hb-pos-installment-lifecycle-v1",
    aad: Object.freeze({
      revision: INSTALLMENT_LIFECYCLE_PAYLOAD_REVISION,
      storeCode: value.storeCode,
      deviceCode: value.deviceCode,
      operationGuid: value.operationGuid,
    }),
    originalDeviceCode: value.originalDeviceCode,
    command: value.command,
    intentFingerprint: value.intentFingerprint,
  });
}

async function encryptEnvelope(
  encryptor: SensitivePayloadEncryptor,
  envelope: ActionEnvelopeV1,
): Promise<Uint8Array> {
  const ciphertext = await encryptor.encrypt(JSON.stringify(envelope));
  if (!(ciphertext instanceof Uint8Array) || ciphertext.length === 0) {
    throw new Error("Installment action encryption failed.");
  }
  return ciphertext;
}

async function encryptLifecycleEnvelope(
  encryptor: SensitivePayloadEncryptor,
  envelope: LifecycleEnvelopeV1,
): Promise<Uint8Array> {
  const ciphertext = await encryptor.encrypt(JSON.stringify(envelope));
  if (!(ciphertext instanceof Uint8Array) || ciphertext.length === 0) {
    throw new Error("Installment lifecycle encryption failed.");
  }
  return ciphertext;
}

async function decryptEnvelope(
  encryptor: SensitivePayloadEncryptor,
  ciphertext: Uint8Array,
): Promise<ActionEnvelopeV1> {
  const value = JSON.parse(await encryptor.decrypt(ciphertext)) as unknown;
  if (
    !exact(value, [
      "format",
      "aad",
      "action",
      "command",
      "intentFingerprint",
    ]) ||
    value.format !== "hb-pos-installment-action-v1" ||
    !exact(value.aad, [
      "revision",
      "storeCode",
      "deviceCode",
      "actionId",
    ]) ||
    value.aad.revision !== INSTALLMENT_ACTION_PAYLOAD_REVISION
  ) {
    throw new Error("Invalid installment action envelope.");
  }
  const action = normalizeAction(value.action);
  const storeCode = identity(value.aad.storeCode, "store code", 50);
  const deviceCode = identity(value.aad.deviceCode, "device code", 128);
  const actionId = uuid(value.aad.actionId, "action ID");
  if (action.actionId !== actionId) throw new Error("Invalid AAD.");
  return Object.freeze({
    format: "hb-pos-installment-action-v1",
    aad: Object.freeze({
      revision: INSTALLMENT_ACTION_PAYLOAD_REVISION,
      storeCode,
      deviceCode,
      actionId,
    }),
    action,
    command: normalizeCommand(value.command, action, deviceCode),
    intentFingerprint: confidential(
      value.intentFingerprint,
      "intent fingerprint",
      1_048_576,
    ),
  });
}

async function decryptLifecycleEnvelope(
  encryptor: SensitivePayloadEncryptor,
  ciphertext: Uint8Array,
): Promise<LifecycleEnvelopeV1> {
  const value = JSON.parse(await encryptor.decrypt(ciphertext)) as unknown;
  if (
    !exact(value, [
      "format",
      "aad",
      "originalDeviceCode",
      "command",
      "intentFingerprint",
    ]) ||
    value.format !== "hb-pos-installment-lifecycle-v1" ||
    !exact(value.aad, [
      "revision",
      "storeCode",
      "deviceCode",
      "operationGuid",
    ]) ||
    value.aad.revision !== INSTALLMENT_LIFECYCLE_PAYLOAD_REVISION
  ) {
    throw new Error("Invalid installment lifecycle envelope.");
  }
  const operationGuid = uuid(
    value.aad.operationGuid,
    "lifecycle operation GUID",
  );
  const deviceCode = identity(value.aad.deviceCode, "device code", 128);
  return Object.freeze({
    format: "hb-pos-installment-lifecycle-v1",
    aad: Object.freeze({
      revision: INSTALLMENT_LIFECYCLE_PAYLOAD_REVISION,
      storeCode: identity(value.aad.storeCode, "store code", 50),
      deviceCode,
      operationGuid,
    }),
    originalDeviceCode: identity(
      value.originalDeviceCode,
      "original device code",
      128,
    ),
    command: normalizeLifecycleCommand(
      value.command,
      operationGuid,
      deviceCode,
    ),
    intentFingerprint: sha256Fingerprint(
      value.intentFingerprint,
      "lifecycle intent fingerprint",
    ),
  });
}

function normalizePersistedAction(value: unknown): PersistedInstallmentAction {
  if (
    !exact(value, [
      "action",
      "command",
      "deviceCode",
      "intentFingerprint",
      "state",
      "storeCode",
    ])
  ) {
    throw new TypeError("Persisted installment action is invalid.");
  }
  const storeCode = identity(value.storeCode, "store code", 50);
  const deviceCode = identity(value.deviceCode, "device code", 128);
  const action = normalizeAction(value.action);
  return Object.freeze({
    action,
    command: normalizeCommand(value.command, action, deviceCode),
    deviceCode,
    intentFingerprint: confidential(
      value.intentFingerprint,
      "intent fingerprint",
      1_048_576,
    ),
    state: state(value.state),
    storeCode,
  });
}

function normalizePersistedLifecycleAction(
  value: unknown,
): PersistedInstallmentLifecycleAction {
  if (
    !exact(value, [
      "operationGuid",
      "idempotencyKey",
      "kind",
      "installmentGuid",
      "storeCode",
      "deviceCode",
      "originalDeviceCode",
      "command",
      "intentFingerprint",
    ])
  ) {
    throw new TypeError("Persisted installment lifecycle action is invalid.");
  }
  const operationGuid = uuid(value.operationGuid, "lifecycle operation GUID");
  const idempotencyKey = uuid(
    value.idempotencyKey,
    "lifecycle idempotency key",
  );
  if (idempotencyKey !== operationGuid) {
    throw new TypeError("Installment lifecycle identity is invalid.");
  }
  const deviceCode = identity(value.deviceCode, "device code", 128);
  const command = normalizeLifecycleCommand(
    value.command,
    operationGuid,
    deviceCode,
  );
  const installmentGuid = uuid(
    value.installmentGuid,
    "lifecycle installment GUID",
  );
  if (command.installmentGuid !== installmentGuid) {
    throw new TypeError("Installment lifecycle identity is invalid.");
  }
  const kind = lifecycleKind(value.kind);
  if (
    (kind === "void" && !("voidedAtIso" in command)) ||
    (kind === "pickup" && !("confirmedAtIso" in command))
  ) {
    throw new TypeError("Installment lifecycle command kind is invalid.");
  }
  return Object.freeze({
    operationGuid,
    idempotencyKey,
    kind,
    installmentGuid,
    storeCode: identity(value.storeCode, "store code", 50),
    deviceCode,
    originalDeviceCode: identity(
      value.originalDeviceCode,
      "original device code",
      128,
    ),
    command,
    intentFingerprint: sha256Fingerprint(
      value.intentFingerprint,
      "lifecycle intent fingerprint",
    ),
  });
}

function normalizeLifecycleCommand(
  value: unknown,
  expectedOperationGuid: string,
  expectedDeviceCode: string,
): PersistedInstallmentLifecycleAction["command"] {
  if (!record(value)) {
    throw new TypeError("Installment lifecycle command is invalid.");
  }
  const commonKeys = [
    "deviceCode",
    "cashierId",
    "cashierName",
    "installmentGuid",
    "operationGuid",
    "idempotencyKey",
  ] as const;
  const isVoid = Object.prototype.hasOwnProperty.call(value, "voidedAtIso");
  if (
    isVoid
      ? !exact(value, [...commonKeys, "voidedAtIso", "reason"])
      : !exact(value, [...commonKeys, "confirmedAtIso", "note"])
  ) {
    throw new TypeError("Installment lifecycle command is invalid.");
  }
  const operationGuid = uuid(value.operationGuid, "lifecycle operation GUID");
  const idempotencyKey = uuid(
    value.idempotencyKey,
    "lifecycle idempotency key",
  );
  if (
    operationGuid !== expectedOperationGuid ||
    idempotencyKey !== expectedOperationGuid
  ) {
    throw new TypeError("Installment lifecycle command identity is invalid.");
  }
  const common = Object.freeze({
    ...commandIdentity(value, expectedDeviceCode),
    installmentGuid: uuid(
      value.installmentGuid,
      "lifecycle installment GUID",
    ),
    operationGuid,
    idempotencyKey,
  });
  if (isVoid) {
    return Object.freeze({
      ...common,
      voidedAtIso: strictIso(value.voidedAtIso, "void time"),
      reason: confidential(value.reason, "void reason", 1_000),
    });
  }
  return Object.freeze({
    ...common,
    confirmedAtIso: strictIso(value.confirmedAtIso, "pickup time"),
    note: nullableConfidential(value.note, "pickup note", 1_000),
  });
}

function lifecycleKind(value: unknown): "void" | "pickup" {
  if (value !== "void" && value !== "pickup") {
    throw new TypeError("Installment lifecycle action kind is invalid.");
  }
  return value;
}

function normalizeAction(value: unknown): InstallmentPaymentAction {
  if (
    !exact(value, [
      "actionId",
      "idempotencyKey",
      "kind",
      "installmentGuid",
      "paymentGuid",
      "method",
      "amountCents",
    ])
  ) {
    throw new TypeError("Installment payment action is invalid.");
  }
  const actionId = uuid(value.actionId, "action ID");
  const idempotencyKey = uuid(value.idempotencyKey, "idempotency key");
  const installmentGuid = uuid(value.installmentGuid, "installment GUID");
  if (actionId !== idempotencyKey) {
    throw new TypeError("Installment action identity is invalid.");
  }
  if (value.kind === "cancel-refund") {
    if (
      value.paymentGuid !== null ||
      value.method !== null ||
      value.amountCents !== null
    ) {
      throw new TypeError("Installment refund action is invalid.");
    }
    return Object.freeze({
      actionId,
      idempotencyKey,
      kind: "cancel-refund",
      installmentGuid,
      paymentGuid: null,
      method: null,
      amountCents: null,
    });
  }
  if (value.kind !== "create" && value.kind !== "repayment") {
    throw new TypeError("Installment action kind is invalid.");
  }
  return Object.freeze({
    actionId,
    idempotencyKey,
    kind: value.kind,
    installmentGuid,
    paymentGuid: uuid(value.paymentGuid, "payment GUID"),
    method: paymentMethod(value.method),
    amountCents: positive(value.amountCents, "payment amount"),
  });
}

function actionFromRow(row: ActionRow): InstallmentPaymentAction {
  return normalizeAction({
    actionId: row.action_id,
    idempotencyKey: row.idempotency_key,
    kind: row.action_kind,
    installmentGuid: row.installment_guid,
    paymentGuid: row.payment_guid,
    method: row.payment_method,
    amountCents: row.amount_cents,
  });
}

function normalizeCommand(
  value: unknown,
  action: InstallmentPaymentAction,
  deviceCode: string,
): InstallmentActionCommand {
  if (!record(value) || value.kind !== action.kind) {
    throw new TypeError("Installment command kind is invalid.");
  }
  if (value.kind === "create") {
    return createCommand(value, action, deviceCode);
  }
  if (value.kind === "repayment") {
    const legacyKeys = [
      "deviceCode",
      "cashierId",
      "cashierName",
      "kind",
      "installmentGuid",
    ] as const;
    const hasPaymentSelection =
      action.method === "card"
        ? exact(value, [...legacyKeys, "cardProvider"])
        : action.method === "cash"
          ? exact(value, [...legacyKeys, "cashTenderedCents"])
          : false;
    if (!exact(value, legacyKeys) && !hasPaymentSelection) {
      throw new TypeError("Installment repayment command is invalid.");
    }
    return Object.freeze({
      ...commandIdentity(value, deviceCode),
      kind: "repayment",
      installmentGuid: matchingInstallment(value.installmentGuid, action),
      ...(hasPaymentSelection
        ? normalizePaymentSelection(value, action)
        : {}),
    });
  }
  const legacyKeys = [
    "deviceCode",
    "cashierId",
    "cashierName",
    "kind",
    "installmentGuid",
    "cancelledAtIso",
    "reason",
    "idempotencyKey",
  ] as const;
  // 中文注释：精确字段集合只接受三代形状——旧版无指纹、带指纹、带指纹+退款方式；
  // 旧数据缺 refundMode 按原路退处理，不做迁移。
  const hasRefundMode = exact(value, [
    ...legacyKeys,
    "refundPlanFingerprint",
    "refundMode",
  ]);
  const hasRefundPlanFingerprint =
    hasRefundMode ||
    exact(value, [...legacyKeys, "refundPlanFingerprint"]);
  if (!exact(value, legacyKeys) && !hasRefundPlanFingerprint) {
    throw new TypeError("Installment cancel command is invalid.");
  }
  if (
    hasRefundMode &&
    value.refundMode !== "original-route" &&
    value.refundMode !== "voucher"
  ) {
    throw new TypeError("Installment cancel refund mode is invalid.");
  }
  const idempotencyKey = uuid(value.idempotencyKey, "idempotency key");
  if (idempotencyKey !== action.idempotencyKey) {
    throw new TypeError("Installment command identity is invalid.");
  }
  return Object.freeze({
    ...commandIdentity(value, deviceCode),
    kind: "cancel-refund",
    installmentGuid: matchingInstallment(value.installmentGuid, action),
    cancelledAtIso: strictIso(value.cancelledAtIso, "cancellation time"),
    reason: nullableConfidential(value.reason, "reason", 1_000),
    idempotencyKey,
    ...(hasRefundPlanFingerprint
      ? {
          refundPlanFingerprint: sha256Fingerprint(
            value.refundPlanFingerprint,
            "refund plan fingerprint",
          ),
        }
      : {}),
    ...(hasRefundMode
      ? {
          refundMode: value.refundMode as "original-route" | "voucher",
        }
      : {}),
  });
}

function createCommand(
  value: Record<string, unknown>,
  action: InstallmentPaymentAction,
  deviceCode: string,
): Extract<InstallmentActionCommand, { kind: "create" }> {
  const legacyKeys = [
    "deviceCode",
    "cashierId",
    "cashierName",
    "kind",
    "installmentGuid",
    "createdAtIso",
    "totalCents",
    "downPaymentCents",
    "lines",
    "customerName",
    "customerPhone",
    "note",
    "cartFingerprint",
    "draftRevision",
  ] as const;
  const hasPaymentSelection = exact(value, [
    ...legacyKeys,
    ...(action.method === "card"
      ? ["cardProvider" as const]
      : action.method === "cash"
        ? ["cashTenderedCents" as const]
        : []),
  ]) && action.method !== "voucher";
  if (
    (!exact(value, legacyKeys) && !hasPaymentSelection) ||
    !Array.isArray(value.lines) ||
    value.lines.length === 0 ||
    value.lines.length > 1_000
  ) {
    throw new TypeError("Installment create command is invalid.");
  }
  const totalCents = positive(value.totalCents, "total");
  const downPaymentCents = positive(
    value.downPaymentCents,
    "down payment",
  );
  if (downPaymentCents > totalCents) {
    throw new TypeError("Installment down payment is invalid.");
  }
  const seen = new Set<string>();
  const lines = value.lines.map((item) => {
    const line = normalizeLine(item);
    if (seen.has(line.installmentLineGuid)) {
      throw new TypeError("Installment line is duplicate.");
    }
    seen.add(line.installmentLineGuid);
    return line;
  });
  return Object.freeze({
    ...commandIdentity(value, deviceCode),
    kind: "create",
    installmentGuid: matchingInstallment(value.installmentGuid, action),
    createdAtIso: strictIso(value.createdAtIso, "creation time"),
    totalCents,
    downPaymentCents,
    lines: Object.freeze(lines),
    customerName: identity(value.customerName, "customer name", 256),
    customerPhone: identity(value.customerPhone, "customer phone", 128),
    note: nullableConfidential(value.note, "note", 1_000),
    cartFingerprint: confidential(
      value.cartFingerprint,
      "cart fingerprint",
      1_048_576,
    ),
    draftRevision: integer(value.draftRevision, "draft revision"),
    ...(hasPaymentSelection
      ? normalizePaymentSelection(value, action)
      : {}),
  });
}

function normalizePaymentSelection(
  value: Record<string, unknown>,
  action: InstallmentPaymentAction,
): Readonly<{
  cardProvider?: "square" | "linkly-cloud";
  cashTenderedCents?: number;
}> {
  if (
    action.kind === "cancel-refund" ||
    action.method === null ||
    action.amountCents === null
  ) {
    throw new TypeError("Installment payment selection is invalid.");
  }
  if (action.method === "card") {
    if (
      (value.cardProvider !== "square" &&
        value.cardProvider !== "linkly-cloud")
    ) {
      throw new TypeError("Installment payment selection is invalid.");
    }
    return Object.freeze({
      cardProvider: value.cardProvider,
    });
  }
  if (action.method !== "cash") {
    throw new TypeError("Installment payment selection is invalid.");
  }
  const cashTenderedCents = positive(
    value.cashTenderedCents,
    "cash tendered amount",
  );
  if (cashTenderedCents < action.amountCents) {
    throw new TypeError("Installment payment selection is invalid.");
  }
  return Object.freeze({
    cashTenderedCents,
  });
}

function commandIdentity(
  value: Record<string, unknown>,
  expectedDevice: string,
): Readonly<{
  deviceCode: string;
  cashierId: string;
  cashierName: string;
}> {
  const deviceCode = identity(value.deviceCode, "command device", 128);
  if (deviceCode !== expectedDevice) {
    throw new TypeError("Installment command scope is invalid.");
  }
  return Object.freeze({
    deviceCode,
    cashierId: identity(value.cashierId, "cashier ID", 256),
    cashierName: identity(value.cashierName, "cashier name", 256),
  });
}

function matchingInstallment(
  value: unknown,
  action: InstallmentPaymentAction,
): string {
  const result = uuid(value, "command installment GUID");
  if (result !== action.installmentGuid) {
    throw new TypeError("Installment command identity is invalid.");
  }
  return result;
}

function normalizeLine(
  value: unknown,
): Extract<
  InstallmentActionCommand,
  { kind: "create" }
>["lines"][number] {
  if (
    !exact(value, [
      "installmentLineGuid",
      "productCode",
      "referenceCode",
      "displayName",
      "lookupCode",
      "quantity",
      "unitPriceCents",
      "discountCents",
      "actualAmountCents",
      "itemNumber",
    ])
  ) {
    throw new TypeError("Installment line is invalid.");
  }
  return Object.freeze({
    installmentLineGuid: uuid(value.installmentLineGuid, "line GUID"),
    productCode: identity(value.productCode, "product code", 128),
    referenceCode: nullableIdentity(value.referenceCode, "reference", 128),
    displayName: identity(value.displayName, "display name", 512),
    lookupCode: identity(value.lookupCode, "lookup code", 256),
    quantity: quantity(value.quantity),
    unitPriceCents: integer(value.unitPriceCents, "unit price"),
    discountCents: integer(value.discountCents, "discount"),
    actualAmountCents: integer(value.actualAmountCents, "actual amount"),
    itemNumber: nullableIdentity(value.itemNumber, "item number", 128),
  });
}

function normalizeTerminal(value: TerminalScope): TerminalScope {
  if (!record(value)) {
    throw new TypeError("Installment terminal scope is invalid.");
  }
  return Object.freeze({
    storeCode: identity(value.storeCode, "store code", 50),
    deviceCode: identity(value.deviceCode, "device code", 128),
  });
}

function state(value: unknown): InstallmentActionState {
  if (
    value !== "Created" &&
    value !== "ProviderPending" &&
    value !== "Unknown" &&
    value !== "Approved" &&
    value !== "BackendPending"
  ) {
    throw new TypeError("Installment action state is invalid.");
  }
  return value;
}

function allowedTransition(
  current: InstallmentActionState,
  next: InstallmentActionState,
): boolean {
  return (
    (current === "Created" && next === "ProviderPending") ||
    (current === "ProviderPending" &&
      (next === "Unknown" || next === "Approved")) ||
    (current === "Unknown" && next === "Approved") ||
    (current === "Approved" && next === "BackendPending")
  );
}

function paymentMethod(
  value: unknown,
): "cash" | "card" | "voucher" {
  if (value !== "cash" && value !== "card" && value !== "voucher") {
    throw new TypeError("Installment payment method is invalid.");
  }
  return value;
}

function uuid(value: unknown, label: string): string {
  if (
    typeof value !== "string" ||
    !/^[0-9a-f]{8}-[0-9a-f]{4}-[1-5][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/iu.test(
      value,
    )
  ) {
    throw new TypeError(`Installment ${label} is invalid.`);
  }
  return value.toLowerCase();
}

function identity(
  value: unknown,
  label: string,
  max: number,
): string {
  if (
    typeof value !== "string" ||
    value.length === 0 ||
    value.length > max ||
    value.trim() !== value ||
    /[\u0000-\u001f\u007f]/u.test(value)
  ) {
    throw new TypeError(`Installment ${label} is invalid.`);
  }
  return value;
}

function nullableIdentity(
  value: unknown,
  label: string,
  max: number,
): string | null {
  return value === null ? null : identity(value, label, max);
}

function confidential(
  value: unknown,
  label: string,
  max: number,
): string {
  if (
    typeof value !== "string" ||
    value.length === 0 ||
    value.length > max ||
    /[\u0000-\u001f\u007f]/u.test(value)
  ) {
    throw new TypeError(`Installment ${label} is invalid.`);
  }
  return value;
}

function nullableConfidential(
  value: unknown,
  label: string,
  max: number,
): string | null {
  return value === null ? null : confidential(value, label, max);
}

function positive(value: unknown, label: string): number {
  if (
    typeof value !== "number" ||
    !Number.isSafeInteger(value) ||
    value <= 0
  ) {
    throw new TypeError(`Installment ${label} is invalid.`);
  }
  return value;
}

function integer(value: unknown, label: string): number {
  if (
    typeof value !== "number" ||
    !Number.isSafeInteger(value) ||
    value < 0
  ) {
    throw new TypeError(`Installment ${label} is invalid.`);
  }
  return value;
}

function quantity(value: unknown): string {
  if (
    typeof value !== "string" ||
    !/^(?:0|[1-9]\d*)(?:\.\d{1,4})?$/u.test(value) ||
    Number(value) <= 0
  ) {
    throw new TypeError("Installment line quantity is invalid.");
  }
  return value;
}

function strictIso(value: unknown, label: string): string {
  if (
    typeof value !== "string" ||
    !/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z$/u.test(value) ||
    new Date(value).toISOString() !== value
  ) {
    throw new TypeError(`Installment ${label} is invalid.`);
  }
  return value;
}

function sha256Fingerprint(value: unknown, label: string): string {
  if (
    typeof value !== "string" ||
    !/^sha256:[0-9a-f]{64}$/u.test(value)
  ) {
    throw new TypeError(`Installment ${label} is invalid.`);
  }
  return value;
}

function bytes(value: unknown): Uint8Array {
  if (!(value instanceof Uint8Array) || value.length === 0) {
    throw new Error("Persisted installment action ciphertext is invalid.");
  }
  return value;
}

function exact<T extends readonly string[]>(
  value: unknown,
  keys: T,
): value is Record<T[number], unknown> {
  if (!record(value)) return false;
  const actual = Object.keys(value);
  return (
    actual.length === keys.length &&
    keys.every((key) => Object.prototype.hasOwnProperty.call(value, key))
  );
}

function record(value: unknown): value is Record<string, unknown> {
  return (
    typeof value === "object" &&
    value !== null &&
    !Array.isArray(value)
  );
}
