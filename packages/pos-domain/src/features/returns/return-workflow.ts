import {
  ReturnFeatureError,
  buildReturnRefundPlan,
  createNoReceiptDraftLine,
  createReceiptDraftLines,
  selectedLineAmountCents,
  updateReturnLineQuantity,
  validateReceiptReturnContext,
  type NoReceiptReturnItem,
  type OriginalReturnTenderCapacity,
  type ReceiptReturnContext,
  type ReturnCaseKind,
  type ReturnDraftLine,
  type ReturnErrorCode,
  type ReturnRefundPlan,
  type ReturnTenderMethod,
} from "./return-domain";

import {
  normalizeLineSyncProvenance,
  type LineSyncProvenance,
} from "../../core/contracts/line-sync-provenance";

export interface ReturnLookupPort {
  lookupReceipt(query: string): Promise<ReceiptReturnContext | null>;
  lookupNoReceiptProduct(query: string): Promise<NoReceiptReturnItem | null>;
  createNoReceiptOpenItem(input: Readonly<{
    displayName: string;
    unitRefundCents: number;
  }>): Promise<NoReceiptReturnItem | null>;
}

export interface ReturnConnectivityPort {
  isOnline(): Promise<boolean>;
}

export type NoReceiptReturnAuthorization = Readonly<{
  /**
   * 组合根提供的单次退货授权键；执行适配器必须按 actionId 幂等消费。
   * 不得把主管条码、token 或身份资料放入此值。
   */
  authorizationKey: string;
}>;

export interface ReturnSupervisorAuthorizationPort {
  authorizeNoReceiptReturn(): Promise<NoReceiptReturnAuthorization>;
}

/**
 * captureLease 在组合根绑定当前可信收银员 epoch；页面不能用公开 Zustand 状态伪造。
 * assertActive 必须在每个异步边界前后调用。
 */
export interface ReturnSessionGuardPort {
  captureLease(): string;
  assertActive(lease: string): void;
}

export type ReturnExecutionCommand = Readonly<{
  actionId: string;
  plan: ReturnRefundPlan;
  noReceiptAuthorizationKey: string | null;
}>;

export type ReturnExecutionOutcome =
  | Readonly<{
      status: "completed";
      returnOrderGuid: string;
    }>
  | Readonly<{
      status: "declined";
    }>
  | Readonly<{
      status: "unknown";
      /**
       * provider 恢复键可能尚未返回；适配器仍必须能通过 actionId 查询耐久 attempt 绑定。
       */
      recoveryKey: string | null;
    }>;

/**
 * 生产适配器负责把 capacityId 解析为可信原支付引用：
 * - 纯现金分配调用既有 durable cash refund；
 * - card/voucher/installment 分配先持久化 payment attempt，再调用在线退款；
 * - 通信歧义必须返回 unknown，绝不能通过抛错暗示调用方可重试。
 */
export interface ReturnExecutionPort {
  execute(command: ReturnExecutionCommand): Promise<ReturnExecutionOutcome>;
  recover(input: Readonly<{
    actionId: string;
    recoveryKey: string | null;
  }>): Promise<ReturnExecutionOutcome>;
}

export type ReturnWorkflowOptions = Readonly<{
  lookup: ReturnLookupPort;
  connectivity: ReturnConnectivityPort;
  supervisorAuthorization: ReturnSupervisorAuthorizationPort;
  sessionGuard: ReturnSessionGuardPort;
  execution: ReturnExecutionPort;
  createActionId(): string;
}>;

export type ReturnWorkflowStatus =
  | "draft"
  | "submitting"
  | "unknown"
  | "completed"
  | "declined"
  | "failed";

export type ReturnWorkflowSnapshot = Readonly<{
  caseKind: ReturnCaseKind;
  originalOrderGuid: string | null;
  receiptLabel: string | null;
  loadedFrom: "remote" | "local" | null;
  returnRecordsMayBeStale: boolean;
  lines: readonly ReturnDraftLine[];
  tenderCapacities: readonly OriginalReturnTenderCapacity[];
  preferredMethod: ReturnTenderMethod | null;
  selectedTotalCents: number;
  status: ReturnWorkflowStatus;
  completedReturnOrderGuid: string | null;
  lastErrorCode: ReturnErrorCode | null;
}>;

export type ReturnRecoveryHydrationLine = Readonly<{
  sourceKind: ReturnDraftLine["sourceKind"];
  itemNumber: string | null;
  displayName: string;
  quantity: number;
  unitRefundCents: number;
  signedAmountCents: number;
  syncProvenance: LineSyncProvenance;
}>;

export type ReturnRecoveryHydration = Readonly<{
  actionId: string;
  sourceKind: ReturnCaseKind;
  totalRefundCents: number;
  lines: readonly ReturnRecoveryHydrationLine[];
}>;

const INITIAL_SNAPSHOT: ReturnWorkflowSnapshot = {
  caseKind: "receipt",
  originalOrderGuid: null,
  receiptLabel: null,
  loadedFrom: null,
  returnRecordsMayBeStale: false,
  lines: [],
  tenderCapacities: [],
  preferredMethod: null,
  selectedTotalCents: 0,
  status: "draft",
  completedReturnOrderGuid: null,
  lastErrorCode: null,
};

/**
 * React 无关退货工作流。它只持有不透明容量键，任何支付引用均留在执行适配器。
 */
export class ReturnWorkflow {
  private snapshot: ReturnWorkflowSnapshot = INITIAL_SNAPSHOT;
  private readonly lease: string;
  private actionId: string | null = null;
  private recoveryKey: string | null = null;
  private recoveryRequired = false;
  private noReceiptAuthorizationKey: string | null = null;
  private confirmInFlight: Promise<ReturnExecutionOutcome> | null = null;
  private recoveryInFlight: Promise<ReturnExecutionOutcome> | null = null;
  private terminalOutcome: ReturnExecutionOutcome | null = null;

  public constructor(private readonly options: ReturnWorkflowOptions) {
    this.lease = options.sessionGuard.captureLease();
  }

  public getSnapshot(): ReturnWorkflowSnapshot {
    return this.snapshot;
  }

  /**
   * 新进程只恢复安全展示投影并绑定原 actionId；不重建 plan，也不允许再次 confirm。
   */
  public hydrateRecovery(
    input: ReturnRecoveryHydration,
  ): ReturnWorkflowSnapshot {
    this.assertSession();
    if (
      this.actionId ||
      this.recoveryRequired ||
      this.terminalOutcome ||
      this.snapshot.status !== "draft" ||
      this.snapshot.lines.length > 0
    ) {
      throw new ReturnFeatureError("RETURN_OPERATION_IN_PROGRESS");
    }
    const actionId = input.actionId.trim();
    if (!actionId) {
      throw new ReturnFeatureError("RETURN_RECOVERY_FAILED");
    }
    const lines = recoveryDraftLines(input);
    this.actionId = actionId;
    this.recoveryKey = null;
    this.recoveryRequired = true;
    this.snapshot = Object.freeze({
      ...INITIAL_SNAPSHOT,
      caseKind: input.sourceKind,
      lines,
      selectedTotalCents: input.totalRefundCents,
      status: "unknown",
      lastErrorCode: "RETURN_UNKNOWN_RECOVERY_REQUIRED",
    });
    return this.snapshot;
  }

  public async loadReceipt(query: string): Promise<ReturnWorkflowSnapshot> {
    this.assertMutable();
    const normalized = query.trim();
    if (!normalized) throw new ReturnFeatureError("RETURN_QUERY_REQUIRED");
    this.assertSession();
    let context: ReceiptReturnContext | null;
    try {
      context = await this.options.lookup.lookupReceipt(normalized);
    } catch (error) {
      if (error instanceof ReturnFeatureError) throw error;
      throw new ReturnFeatureError("RETURN_LOOKUP_FAILED");
    }
    this.assertSession();
    if (!context) throw new ReturnFeatureError("RETURN_ORDER_NOT_FOUND");
    validateReceiptReturnContext(context);
    this.resetExecutionState();
    this.snapshot = {
      caseKind: "receipt",
      originalOrderGuid: context.originalOrderGuid,
      receiptLabel: context.receiptLabel,
      loadedFrom: context.loadedFrom,
      returnRecordsMayBeStale: context.returnRecordsMayBeStale,
      lines: createReceiptDraftLines(context),
      tenderCapacities: [...context.tenderCapacities],
      // 含不能原路退回的额度时默认现金兜底；收银员仍可改选代金券。
      preferredMethod: context.tenderCapacities.some(
        (capacity) => capacity.substituteOnly === true && capacity.remainingCents > 0,
      )
        ? "cash"
        : null,
      selectedTotalCents: 0,
      status: "draft",
      completedReturnOrderGuid: null,
      lastErrorCode: null,
    };
    return this.snapshot;
  }

  public beginNoReceipt(): ReturnWorkflowSnapshot {
    this.assertMutable();
    this.assertSession();
    this.resetExecutionState();
    this.snapshot = {
      ...INITIAL_SNAPSHOT,
      caseKind: "no-receipt",
    };
    return this.snapshot;
  }

  public async addNoReceiptProduct(
    query: string,
  ): Promise<ReturnWorkflowSnapshot> {
    this.assertMutable();
    this.assertNoReceiptMode();
    const normalized = query.trim();
    if (!normalized) throw new ReturnFeatureError("RETURN_QUERY_REQUIRED");
    await this.authorizeNoReceipt();
    let item: NoReceiptReturnItem | null;
    try {
      item = await this.options.lookup.lookupNoReceiptProduct(normalized);
    } catch (error) {
      if (error instanceof ReturnFeatureError) throw error;
      throw new ReturnFeatureError("RETURN_LOOKUP_FAILED");
    }
    this.assertSession();
    if (!item) throw new ReturnFeatureError("RETURN_PRODUCT_NOT_FOUND");
    if (item.sourceKind !== "no-receipt-product") {
      throw new ReturnFeatureError("RETURN_SOURCE_MISMATCH");
    }
    return this.appendNoReceiptLine(item);
  }

  public async addNoReceiptOpenItem(input: Readonly<{
    displayName: string;
    unitRefundCents: number;
  }>): Promise<ReturnWorkflowSnapshot> {
    this.assertMutable();
    this.assertNoReceiptMode();
    if (
      !input.displayName.trim() ||
      !Number.isSafeInteger(input.unitRefundCents) ||
      input.unitRefundCents <= 0
    ) {
      throw new ReturnFeatureError("RETURN_OPEN_ITEM_INVALID");
    }
    await this.authorizeNoReceipt();
    let item: NoReceiptReturnItem | null;
    try {
      item = await this.options.lookup.createNoReceiptOpenItem({
        displayName: input.displayName.trim(),
        unitRefundCents: input.unitRefundCents,
      });
    } catch (error) {
      if (error instanceof ReturnFeatureError) throw error;
      throw new ReturnFeatureError("RETURN_LOOKUP_FAILED");
    }
    this.assertSession();
    if (!item || item.sourceKind !== "no-receipt-open-item") {
      throw new ReturnFeatureError("RETURN_OPEN_ITEM_INVALID");
    }
    return this.appendNoReceiptLine(item);
  }

  public setQuantity(
    selectionKey: string,
    quantity: number,
  ): ReturnWorkflowSnapshot {
    this.assertMutable();
    this.assertSession();
    const lines = updateReturnLineQuantity(
      this.snapshot.lines,
      selectionKey,
      quantity,
    );
    this.snapshot = this.patchLines(lines);
    return this.snapshot;
  }

  public setPreferredMethod(
    method: ReturnTenderMethod,
  ): ReturnWorkflowSnapshot {
    this.assertMutable();
    this.assertSession();
    this.snapshot = {
      ...this.snapshot,
      preferredMethod: method,
      lastErrorCode: null,
    };
    return this.snapshot;
  }

  public async previewPlan(): Promise<ReturnRefundPlan> {
    this.assertMutable();
    this.assertSession();
    const online = await this.options.connectivity.isOnline();
    this.assertSession();
    return this.buildPlan(online);
  }

  public confirm(): Promise<ReturnExecutionOutcome> {
    try {
      this.assertSession();
      if (this.snapshot.status === "unknown" || this.recoveryRequired) {
        return Promise.reject(
          new ReturnFeatureError("RETURN_UNKNOWN_RECOVERY_REQUIRED"),
        );
      }
      if (this.confirmInFlight) return this.confirmInFlight;
      if (this.terminalOutcome) return Promise.resolve(this.terminalOutcome);
      if (this.snapshot.status !== "draft") {
        return Promise.reject(
          new ReturnFeatureError(
            this.snapshot.lastErrorCode ?? "RETURN_OPERATION_IN_PROGRESS",
          ),
        );
      }
    } catch (error) {
      return Promise.reject(error);
    }

    const operation = this.confirmOnce().finally(() => {
      if (this.confirmInFlight === operation) this.confirmInFlight = null;
    });
    this.confirmInFlight = operation;
    return operation;
  }

  public recoverUnknown(): Promise<ReturnExecutionOutcome> {
    try {
      this.assertSession();
      if (!this.actionId || !this.recoveryRequired) {
        return Promise.reject(
          new ReturnFeatureError("RETURN_UNKNOWN_RECOVERY_REQUIRED"),
        );
      }
      if (this.recoveryInFlight) return this.recoveryInFlight;
    } catch (error) {
      return Promise.reject(error);
    }

    const operation = this.recoverOnce().finally(() => {
      if (this.recoveryInFlight === operation) this.recoveryInFlight = null;
    });
    this.recoveryInFlight = operation;
    return operation;
  }

  public reset(): ReturnWorkflowSnapshot {
    this.assertSession();
    if (this.confirmInFlight || this.recoveryInFlight) {
      throw new ReturnFeatureError("RETURN_OPERATION_IN_PROGRESS");
    }
    if (this.recoveryRequired || this.snapshot.status === "unknown") {
      throw new ReturnFeatureError("RETURN_UNKNOWN_RECOVERY_REQUIRED");
    }
    this.resetExecutionState();
    this.snapshot = INITIAL_SNAPSHOT;
    return this.snapshot;
  }

  private async confirmOnce(): Promise<ReturnExecutionOutcome> {
    this.snapshot = {
      ...this.snapshot,
      status: "submitting",
      lastErrorCode: null,
    };
    try {
      const online = await this.options.connectivity.isOnline();
      this.assertSession();
      const plan = this.buildPlan(online);
      const actionId = this.actionId ?? this.options.createActionId();
      if (!actionId.trim()) {
        throw new ReturnFeatureError("RETURN_EXECUTION_FAILED");
      }
      this.actionId = actionId;
      const noReceiptAuthorizationKey =
        plan.sourceKind === "no-receipt"
          ? this.noReceiptAuthorizationKey
          : null;
      if (
        plan.sourceKind === "no-receipt" &&
        !noReceiptAuthorizationKey
      ) {
        throw new ReturnFeatureError("RETURN_SUPERVISOR_REQUIRED");
      }

      let outcome: ReturnExecutionOutcome;
      try {
        outcome = await this.options.execution.execute({
          actionId,
          plan,
          noReceiptAuthorizationKey,
        });
        validateExecutionOutcome(outcome);
      } catch (error) {
        // action 已越过支付边界后，任何未归类异常都按 Unknown 冻结；只允许按 actionId 恢复。
        this.recoveryRequired = true;
        this.recoveryKey = null;
        this.snapshot = {
          ...this.snapshot,
          status: "unknown",
          lastErrorCode:
            error instanceof ReturnFeatureError
              ? error.code
              : "RETURN_EXECUTION_FAILED",
        };
        throw error instanceof ReturnFeatureError
          ? error
          : new ReturnFeatureError("RETURN_EXECUTION_FAILED");
      }

      // 先冻结耐久结果，再检查旧页面 lease；即使换班也绝不能再次退款。
      this.applyExecutionOutcome(outcome);
      this.assertSession();
      return outcome;
    } catch (error) {
      if (this.snapshot.status === "submitting") {
        const code = returnErrorCode(error, "RETURN_EXECUTION_FAILED");
        this.snapshot = {
          ...this.snapshot,
          status: "failed",
          lastErrorCode: code,
        };
      }
      throw error;
    }
  }

  private async recoverOnce(): Promise<ReturnExecutionOutcome> {
    const actionId = this.actionId;
    if (!actionId || !this.recoveryRequired) {
      throw new ReturnFeatureError("RETURN_UNKNOWN_RECOVERY_REQUIRED");
    }
    const online = await this.options.connectivity.isOnline();
    this.assertSession();
    if (!online) throw new ReturnFeatureError("RETURN_ONLINE_REQUIRED");
    this.snapshot = {
      ...this.snapshot,
      status: "submitting",
      lastErrorCode: null,
    };

    let outcome: ReturnExecutionOutcome;
    try {
      outcome = await this.options.execution.recover({
        actionId,
        // provider key 只从受保护账本解析，跨进程恢复不把它带回 UI。
        recoveryKey: null,
      });
      validateExecutionOutcome(outcome);
    } catch (error) {
      this.snapshot = {
        ...this.snapshot,
        status: "unknown",
        lastErrorCode: "RETURN_RECOVERY_FAILED",
      };
      throw error instanceof ReturnFeatureError
        ? error
        : new ReturnFeatureError("RETURN_RECOVERY_FAILED");
    }
    this.applyExecutionOutcome(outcome);
    this.assertSession();
    return outcome;
  }

  private buildPlan(online: boolean): ReturnRefundPlan {
    return buildReturnRefundPlan({
      sourceKind: this.snapshot.caseKind,
      originalOrderGuid: this.snapshot.originalOrderGuid,
      lines: this.snapshot.lines,
      capacities: this.snapshot.tenderCapacities,
      online,
      preferredMethod: this.snapshot.preferredMethod,
    });
  }

  private applyExecutionOutcome(outcome: ReturnExecutionOutcome): void {
    if (outcome.status === "unknown") {
      this.recoveryRequired = true;
      this.recoveryKey = null;
      this.snapshot = {
        ...this.snapshot,
        status: "unknown",
        lastErrorCode: "RETURN_UNKNOWN_RECOVERY_REQUIRED",
      };
      return;
    }

    this.recoveryRequired = false;
    this.recoveryKey = null;
    this.terminalOutcome = outcome;
    this.snapshot = {
      ...this.snapshot,
      status: outcome.status === "completed" ? "completed" : "declined",
      completedReturnOrderGuid:
        outcome.status === "completed" ? outcome.returnOrderGuid : null,
      lastErrorCode:
        outcome.status === "declined" ? "RETURN_EXECUTION_DECLINED" : null,
    };
  }

  private appendNoReceiptLine(
    item: NoReceiptReturnItem,
  ): ReturnWorkflowSnapshot {
    const line = createNoReceiptDraftLine(item);
    if (
      this.snapshot.lines.some(
        (existing) =>
          existing.selectionKey === line.selectionKey ||
          existing.returnSourceKey === line.returnSourceKey,
      )
    ) {
      throw new ReturnFeatureError("RETURN_SOURCE_MISMATCH");
    }
    this.snapshot = this.patchLines([...this.snapshot.lines, line]);
    return this.snapshot;
  }

  private patchLines(
    lines: readonly ReturnDraftLine[],
  ): ReturnWorkflowSnapshot {
    const selectedTotalCents = lines.reduce((total, line) => {
      const next =
        total + selectedLineAmountCents(line, line.selectedQuantity);
      if (!Number.isSafeInteger(next)) {
        throw new ReturnFeatureError("RETURN_AMOUNT_EXCEEDED");
      }
      return next;
    }, 0);
    return {
      ...this.snapshot,
      lines,
      selectedTotalCents,
      lastErrorCode: null,
    };
  }

  private async authorizeNoReceipt(): Promise<void> {
    this.assertSession();
    const online = await this.options.connectivity.isOnline();
    this.assertSession();
    if (!online) throw new ReturnFeatureError("RETURN_ONLINE_REQUIRED");
    if (this.noReceiptAuthorizationKey) return;
    try {
      const authorization =
        await this.options.supervisorAuthorization.authorizeNoReceiptReturn();
      if (!authorization.authorizationKey.trim()) {
        throw new ReturnFeatureError("RETURN_SUPERVISOR_REQUIRED");
      }
      this.noReceiptAuthorizationKey = authorization.authorizationKey;
    } catch (error) {
      if (error instanceof ReturnFeatureError) throw error;
      throw new ReturnFeatureError("RETURN_SUPERVISOR_REQUIRED");
    }
    this.assertSession();
  }

  private assertNoReceiptMode(): void {
    if (this.snapshot.caseKind !== "no-receipt") {
      throw new ReturnFeatureError("RETURN_SOURCE_MISMATCH");
    }
  }

  private assertMutable(): void {
    if (this.confirmInFlight || this.recoveryInFlight) {
      throw new ReturnFeatureError("RETURN_OPERATION_IN_PROGRESS");
    }
    if (this.snapshot.status === "unknown" || this.recoveryRequired) {
      throw new ReturnFeatureError("RETURN_UNKNOWN_RECOVERY_REQUIRED");
    }
    if (this.terminalOutcome || this.snapshot.status !== "draft") {
      throw new ReturnFeatureError("RETURN_OPERATION_IN_PROGRESS");
    }
  }

  private assertSession(): void {
    try {
      this.options.sessionGuard.assertActive(this.lease);
    } catch {
      throw new ReturnFeatureError("RETURN_SESSION_EXPIRED");
    }
  }

  private resetExecutionState(): void {
    this.actionId = null;
    this.recoveryKey = null;
    this.recoveryRequired = false;
    this.noReceiptAuthorizationKey = null;
    this.confirmInFlight = null;
    this.recoveryInFlight = null;
    this.terminalOutcome = null;
  }
}

function recoveryDraftLines(
  input: ReturnRecoveryHydration,
): readonly ReturnDraftLine[] {
  if (
    !Number.isSafeInteger(input.totalRefundCents) ||
    input.totalRefundCents <= 0 ||
    input.lines.length === 0
  ) {
    throw new ReturnFeatureError("RETURN_RECOVERY_FAILED");
  }
  let total = 0;
  const lines = input.lines.map((line, index): ReturnDraftLine => {
    const amountCents = -line.signedAmountCents;
    if (
      !line.displayName.trim() ||
      !Number.isSafeInteger(line.quantity) ||
      line.quantity <= 0 ||
      !Number.isSafeInteger(line.unitRefundCents) ||
      line.unitRefundCents <= 0 ||
      !Number.isSafeInteger(amountCents) ||
      amountCents <= 0 ||
      (input.sourceKind === "receipt") !==
        (line.sourceKind === "receipt") ||
      (line.sourceKind !== "receipt" &&
        line.unitRefundCents * line.quantity !== amountCents)
    ) {
      throw new ReturnFeatureError("RETURN_RECOVERY_FAILED");
    }
    total += amountCents;
    if (!Number.isSafeInteger(total)) {
      throw new ReturnFeatureError("RETURN_RECOVERY_FAILED");
    }
    const safeIndex = index + 1;
    const base = {
      selectionKey: `recovery-line-${safeIndex}`,
      returnSourceKey: `recovery-source-${safeIndex}`,
      productCode: `recovery-product-${safeIndex}`,
      itemNumber: line.itemNumber,
      lookupCode: `recovery-lookup-${safeIndex}`,
      displayName: line.displayName.trim(),
      availableQuantity: line.quantity,
      selectedQuantity: line.quantity,
      unitRefundCents: line.unitRefundCents,
      remainingAmountCents: amountCents,
      syncProvenance: normalizeRecoverySyncProvenance(
        line.syncProvenance,
      ),
    };
    return Object.freeze(
      line.sourceKind === "receipt"
        ? {
            ...base,
            sourceKind: "receipt" as const,
            originalOrderGuid: `recovery-original-${safeIndex}`,
            originalOrderDetailGuid: `recovery-detail-${safeIndex}`,
          }
        : {
            ...base,
            sourceKind: line.sourceKind,
            originalOrderGuid: null,
            originalOrderDetailGuid: null,
          },
    );
  });
  if (total !== input.totalRefundCents) {
    throw new ReturnFeatureError("RETURN_RECOVERY_FAILED");
  }
  return Object.freeze(lines);
}

function validateExecutionOutcome(outcome: ReturnExecutionOutcome): void {
  if (
    outcome.status === "completed" &&
    !outcome.returnOrderGuid.trim()
  ) {
    throw new ReturnFeatureError("RETURN_EXECUTION_FAILED");
  }
  if (
    outcome.status === "unknown" &&
    outcome.recoveryKey !== null &&
    !outcome.recoveryKey.trim()
  ) {
    throw new ReturnFeatureError("RETURN_EXECUTION_FAILED");
  }
}

export function returnErrorCode(
  error: unknown,
  fallback: ReturnErrorCode,
): ReturnErrorCode {
  return error instanceof ReturnFeatureError ? error.code : fallback;
}

function normalizeRecoverySyncProvenance(
  input: unknown,
): LineSyncProvenance {
  try {
    return normalizeLineSyncProvenance(input);
  } catch {
    throw new ReturnFeatureError("RETURN_RECOVERY_FAILED");
  }
}
