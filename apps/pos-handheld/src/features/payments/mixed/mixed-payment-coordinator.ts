import type { ApprovedPaymentOrderCompletionService } from "../approved-payment-order-completion";
import {
  PaymentActionBindingConflictError,
  PaymentAttemptBlockedError,
  PaymentAttemptDurabilityError,
  PaymentAttemptOfflineError,
  type PaymentAttemptExecutionResult,
  type PaymentAttemptService,
  type StartPaymentAttemptInput,
} from "@hb/pos-payments-core/features/payments/payment-attempt-service";

import type {
  ApprovedPaymentOrderCommitResult,
  AuditActorSnapshot,
  LocalOrder,
  Money,
  OrderTender,
  PaymentAttempt,
  PaymentProvider,
} from "@/core/contracts";
import { auditActorPayload } from "@/core/contracts";

export type MixedPaymentOrderTruth = Readonly<
  Pick<LocalOrder, "orderGuid" | "state" | "actualAmount" | "tenders">
> &
  Readonly<{
    /** 不可变 reversal 关联；DB 仍须以 sourceTenderGuid 唯一约束作为最终防线。 */
    reversalLinks: readonly MixedTenderReversalLink[];
  }>;

export type MixedTenderReversalLink = Readonly<{
  actionId: string;
  sourceTenderGuid: string;
  reversalTenderGuid: string;
}>;

export interface MixedPaymentOrderTruthPort {
  /** 必须读取当前持久订单及完整 tender（含 reversal），不能返回页面内存快照。 */
  getPaymentTruth(orderGuid: string): Promise<MixedPaymentOrderTruth | null>;
}

export type MixedPaymentAttemptPort = Pick<
  PaymentAttemptService,
  | "startAttempt"
  | "recoverAttempt"
  | "getAttempt"
  | "getBlockingAttempt"
  | "getActionActor"
>;

export type MixedApprovedPaymentCompletionPort = Pick<
  ApprovedPaymentOrderCompletionService,
  "complete"
>;

export type MixedCashTenderCommand = Readonly<{
  actionId: string;
  orderGuid: string;
  actor: AuditActorSnapshot;
  /** 实际计入订单 tender 的金额。 */
  amount: Money;
  /** 新版调用显式冻结顾客实收；旧 action 缺失时等同 amount。 */
  tenderedAmount?: Money;
  /** 必须严格等于 tenderedAmount - amount。 */
  change?: Money;
}>;

export type MixedCashTenderMutation = Readonly<{
  replayed: boolean;
  tenderGuid: string;
  truth: MixedPaymentOrderTruth;
  /** SQLite 已验证的现金结算事实；不得由 UI 反推。 */
  cashSettlement?: MixedCashSettlement | undefined;
}>;

export type MixedCashSettlement = Readonly<{
  tendered: Money;
  applied: Money;
  change: Money;
}>;

/**
 * DB 实现必须以 actionId 幂等，并在一个事务内追加现金 tender、操作审计及订单状态；
 * 不允许协调器先写 tender 再补审计。
 */
export interface MixedCashTenderPort {
  appendCashTenderAtomically(
    command: MixedCashTenderCommand,
  ): Promise<MixedCashTenderMutation>;
}

export type MixedTenderReversalCommand = Readonly<{
  actionId: string;
  orderGuid: string;
  tenderGuid: string;
  actor: AuditActorSnapshot;
}>;

export type MixedTenderReversalMutation = Readonly<{
  state: "reversed" | "pending" | "unknown" | "declined" | "cancelled";
  replayed: boolean;
  reversalTenderGuid: string | null;
  truth: MixedPaymentOrderTruth;
}>;

/**
 * “移除”只能追加不可变 reversal tender 和审计；实现不得 DELETE 原 tender。
 * card/voucher 的 provider 恢复及撤销语义也由此 Port 的后续实现负责。
 */
export interface MixedTenderReversalPort {
  reverseTender(
    command: MixedTenderReversalCommand,
  ): Promise<MixedTenderReversalMutation>;
}

export type MixedPaymentCapability = "available" | "unavailable";

export type MixedPaymentCapabilities = Readonly<{
  mixedCashTender: MixedPaymentCapability;
  tenderReversal: MixedPaymentCapability;
}>;

export type MixedPaymentStatus =
  | "awaiting-terminal"
  | "pending"
  | "unknown"
  | "partial"
  | "completed"
  | "declined"
  | "cancelled"
  | "recovery-required";

/**
 * 只返回编排状态和本地标识。provider references、回单、PAN、token 与券码不会越过此边界。
 */
export type MixedPaymentResult = Readonly<{
  status: MixedPaymentStatus;
  orderGuid: string;
  remaining: Money;
  attemptId: string | null;
  tenderGuid: string | null;
  capability: MixedPaymentCapability;
  errorCode: string | null;
  cashSettlement?: MixedCashSettlement | undefined;
}>;

export type AddMixedOnlineTenderInput = Readonly<{
  manualConfirmed?: boolean;
  actionId: string;
  orderGuid: string;
  provider: PaymentProvider;
  amount: Money;
}>;

export type RecoverMixedOnlineAttemptInput = Readonly<{
  orderGuid: string;
  attemptId: string;
  signal?: AbortSignal;
  deadlineAtMs?: number;
}>;

export type AddMixedCashTenderInput = Omit<MixedCashTenderCommand, "actor">;
export type RemoveMixedTenderInput = Omit<MixedTenderReversalCommand, "actor">;

export type MixedPaymentCoordinatorOptions = Readonly<{
  actor: AuditActorSnapshot;
  orderTruth: MixedPaymentOrderTruthPort;
  paymentAttempts: MixedPaymentAttemptPort;
  approvedCompletion: MixedApprovedPaymentCompletionPort;
  cashTender?: MixedCashTenderPort;
  tenderReversal?: MixedTenderReversalPort;
}>;

export class MixedPaymentValidationError extends Error {
  public constructor(
    public readonly code: string,
    message: string,
  ) {
    super(message);
    this.name = "MixedPaymentValidationError";
  }
}

type PaymentSnapshot = Readonly<{
  truth: MixedPaymentOrderTruth;
  paidCents: number;
  remainingCents: number;
}>;

type InflightMixedAction = Readonly<{
  signature: string;
  promise: Promise<MixedPaymentResult>;
  ownerSignal: AbortSignal | null;
}>;

// 中文注释：多个页面或容器实例共享订单级锁，避免现金、卡、券和 reversal 交叉越过持久阻塞检查。
const sharedMixedActions = new Map<string, InflightMixedAction>();

export class MixedPaymentCoordinator {
  public constructor(private readonly options: MixedPaymentCoordinatorOptions) {}

  public getCapabilities(): MixedPaymentCapabilities {
    return {
      mixedCashTender: this.options.cashTender ? "available" : "unavailable",
      tenderReversal: this.options.tenderReversal ? "available" : "unavailable",
    };
  }

  public addOnlineTender(
    input: AddMixedOnlineTenderInput,
  ): Promise<MixedPaymentResult> {
    assertActionInput(input.actionId, input.orderGuid);
    assertPositiveAud(input.amount);
    assertOnlineProvider(input.provider);
    return this.runOrderAction(
      input.orderGuid,
      [
        "online",
        input.actionId,
        input.provider,
        input.amount.currency,
        String(input.amount.cents),
        actorSignature(this.options.actor),
      ].join("|"),
      () => this.addOnlineTenderOnce(input),
    );
  }

  public recoverOnlineAttempt(
    input: RecoverMixedOnlineAttemptInput,
  ): Promise<MixedPaymentResult> {
    assertRequiredId(input.orderGuid, "orderGuid");
    assertRequiredId(input.attemptId, "attemptId");
    assertRecoveryDeadlineControl(input);
    return this.runOrderAction(
      input.orderGuid,
      `recover|${input.attemptId}`,
      () => this.recoverOnlineAttemptOnce(input),
      input.signal,
    );
  }

  public addCashTender(
    input: AddMixedCashTenderInput,
  ): Promise<MixedPaymentResult> {
    assertActionInput(input.actionId, input.orderGuid);
    assertPositiveAud(input.amount);
    return this.runOrderAction(
      input.orderGuid,
      [
        "cash",
        input.actionId,
        input.amount.currency,
        String(input.amount.cents),
        input.tenderedAmount?.currency ?? "",
        String(input.tenderedAmount?.cents ?? input.amount.cents),
        input.change?.currency ?? "",
        String(input.change?.cents ?? 0),
        actorSignature(this.options.actor),
      ].join("|"),
      () => this.addCashTenderOnce(input),
    );
  }

  public removeTender(
    input: RemoveMixedTenderInput,
  ): Promise<MixedPaymentResult> {
    assertActionInput(input.actionId, input.orderGuid);
    assertRequiredId(input.tenderGuid, "tenderGuid");
    return this.runOrderAction(
      input.orderGuid,
      `reverse|${input.actionId}|${input.tenderGuid}|${actorSignature(this.options.actor)}`,
      () => this.removeTenderOnce(input),
    );
  }

  private runOrderAction(
    orderGuid: string,
    signature: string,
    operation: () => Promise<MixedPaymentResult>,
    ownerSignal?: AbortSignal,
  ): Promise<MixedPaymentResult> {
    const active = sharedMixedActions.get(orderGuid);
    if (active) {
      if (
        active.signature === signature &&
        active.ownerSignal === (ownerSignal ?? null)
      ) {
        return active.promise;
      }
      return this.inflightConflict(orderGuid);
    }

    const promise = Promise.resolve().then(operation);
    const entry = { signature, promise, ownerSignal: ownerSignal ?? null };
    sharedMixedActions.set(orderGuid, entry);
    promise.then(
      () => deleteMixedActionIfCurrent(orderGuid, entry),
      () => deleteMixedActionIfCurrent(orderGuid, entry),
    );
    return promise;
  }

  private async inflightConflict(orderGuid: string): Promise<MixedPaymentResult> {
    const snapshot = await this.loadSnapshot(orderGuid);
    const blocking = await this.options.paymentAttempts.getBlockingAttempt(orderGuid);
    if (blocking) {
      return Object.freeze({
        ...blockingResult(snapshot, blocking),
        errorCode: "PAYMENT_ACTION_IN_FLIGHT",
      });
    }
    return result("awaiting-terminal", snapshot, {
      errorCode: "PAYMENT_ACTION_IN_FLIGHT",
    });
  }

  private async addOnlineTenderOnce(
    input: AddMixedOnlineTenderInput,
  ): Promise<MixedPaymentResult> {
    const snapshot = await this.loadSnapshot(input.orderGuid);
    const blocking = await this.options.paymentAttempts.getBlockingAttempt(
      input.orderGuid,
    );
    if (blocking) return blockingResult(snapshot, blocking);
    if (snapshot.remainingCents === 0) return completedResult(snapshot);
    assertWithinRemaining(input.amount, snapshot.remainingCents);

    const start: StartPaymentAttemptInput = {
      actionId: input.actionId,
      orderGuid: input.orderGuid,
      provider: input.provider,
      operation: "purchase",
      amount: input.amount,
      actor: this.options.actor,
      ...(input.provider === "manual-card" ? { manualConfirmed: input.manualConfirmed } : {}),
    };
    let execution: PaymentAttemptExecutionResult;
    try {
      execution = await this.options.paymentAttempts.startAttempt(start);
    } catch (error) {
      if (error instanceof PaymentAttemptOfflineError) {
        return result("recovery-required", snapshot, {
          errorCode: "ONLINE_REQUIRED",
        });
      }
      if (error instanceof PaymentAttemptBlockedError) {
        return blockingResult(snapshot, error.blockingAttempt);
      }
      const failedAttemptId =
        error instanceof PaymentAttemptDurabilityError ||
        error instanceof PaymentActionBindingConflictError
          ? error.attemptId
          : null;
      const observed = await safelyFindBlockingAttempt(
        this.options.paymentAttempts,
        input.orderGuid,
      );
      if (observed) {
        if (
          failedAttemptId !== null &&
          observed.attemptId !== failedAttemptId
        ) {
          return result("recovery-required", snapshot, {
            attemptId: failedAttemptId,
            errorCode: "PAYMENT_ATTEMPT_MISMATCH",
          });
        }
        if (!attemptMatchesStart(observed, start)) {
          return result("recovery-required", snapshot, {
            attemptId: observed.attemptId,
            errorCode: "PAYMENT_ATTEMPT_MISMATCH",
          });
        }
        return blockingResult(snapshot, observed);
      }
      return result("recovery-required", snapshot, {
        attemptId: failedAttemptId,
        errorCode: "PAYMENT_START_FAILED",
      });
    }

    if (!executionMatchesStart(execution, start)) {
      return result("recovery-required", snapshot, {
        attemptId: execution.attempt.attemptId,
        errorCode: "PAYMENT_ATTEMPT_MISMATCH",
      });
    }
    return this.settleExecution(snapshot, execution);
  }

  private async recoverOnlineAttemptOnce(
    input: RecoverMixedOnlineAttemptInput,
  ): Promise<MixedPaymentResult> {
    const snapshot = await this.loadSnapshot(input.orderGuid);
    const observed = await this.options.paymentAttempts.getAttempt(input.attemptId);
    if (!observed || observed.orderGuid !== input.orderGuid) {
      return result("recovery-required", snapshot, {
        attemptId: input.attemptId,
        errorCode: observed ? "PAYMENT_ATTEMPT_ORDER_MISMATCH" : "PAYMENT_ATTEMPT_NOT_FOUND",
      });
    }
    if (!isValidRecoverablePurchaseIdentity(observed, input, snapshot)) {
      return result("recovery-required", snapshot, {
        attemptId: observed.attemptId,
        errorCode: "PAYMENT_ATTEMPT_IDENTITY_MISMATCH",
      });
    }
    if (observed.state === "Declined" || observed.state === "Cancelled") {
      return blockingResult(snapshot, observed);
    }

    let execution: PaymentAttemptExecutionResult;
    try {
      execution = await this.options.paymentAttempts.recoverAttempt(
        observed.attemptId,
        input.signal && input.deadlineAtMs !== undefined
          ? {
              signal: input.signal,
              deadlineAtMs: input.deadlineAtMs,
            }
          : undefined,
      );
    } catch (error) {
      if (error instanceof PaymentAttemptOfflineError) {
        return blockingResult(snapshot, observed);
      }
      return result("recovery-required", snapshot, {
        attemptId: observed.attemptId,
        errorCode: "PAYMENT_RECOVERY_FAILED",
      });
    }
    if (!sameImmutableAttemptIdentity(execution.attempt, observed)) {
      return result("recovery-required", snapshot, {
        attemptId: observed.attemptId,
        errorCode: "PAYMENT_RECOVERY_MISMATCH",
      });
    }
    return this.settleExecution(snapshot, execution);
  }

  private async settleExecution(
    before: PaymentSnapshot,
    execution: PaymentAttemptExecutionResult,
  ): Promise<MixedPaymentResult> {
    const { attempt } = execution;
    if (attempt.state !== "Approved") {
      return blockingResult(before, attempt);
    }

    let committed: ApprovedPaymentOrderCommitResult;
    try {
      const actor = await this.options.paymentAttempts.getActionActor(
        attempt.attemptId,
        attempt.orderGuid,
      );
      committed = await this.options.approvedCompletion.complete(
        execution,
        actor,
      );
    } catch {
      return result("recovery-required", before, {
        attemptId: attempt.attemptId,
        errorCode: "APPROVED_COMPLETION_FAILED",
      });
    }
    if (
      committed.orderGuid !== attempt.orderGuid ||
      committed.signedTenderAmountCents !== attempt.amount.cents ||
      !committed.tenderGuid.trim()
    ) {
      return result("recovery-required", before, {
        attemptId: attempt.attemptId,
        errorCode: "APPROVED_COMPLETION_MISMATCH",
      });
    }

    let after: PaymentSnapshot;
    try {
      after = await this.loadSnapshot(attempt.orderGuid);
      assertPersistedTender(
        after.truth.tenders,
        committed.tenderGuid,
        attempt.provider === "voucher" ? "voucher" : "card",
        attempt.amount.cents,
      );
      const status = classifyCompletedMutation(after);
      if (committed.completed !== (status === "completed")) {
        throw new Error("Approved completion flag does not match persisted truth.");
      }
      if (
        !committed.replayed &&
        after.paidCents !== before.paidCents + attempt.amount.cents
      ) {
        throw new Error("Approved tender amount does not match persisted truth.");
      }
      return result(status, after, {
        attemptId: attempt.attemptId,
        tenderGuid: committed.tenderGuid,
      });
    } catch {
      return result("recovery-required", before, {
        attemptId: attempt.attemptId,
        tenderGuid: committed.tenderGuid,
        errorCode: "APPROVED_TRUTH_MISMATCH",
      });
    }
  }

  private async addCashTenderOnce(
    input: AddMixedCashTenderInput,
  ): Promise<MixedPaymentResult> {
    const before = await this.loadSnapshot(input.orderGuid);
    const blocking = await this.options.paymentAttempts.getBlockingAttempt(
      input.orderGuid,
    );
    if (blocking) return blockingResult(before, blocking);
    if (before.remainingCents === 0) return completedResult(before);
    assertWithinRemaining(input.amount, before.remainingCents);

    const cashTender = this.options.cashTender;
    if (!cashTender) {
      return result("recovery-required", before, {
        capability: "unavailable",
        errorCode: "MIXED_CASH_UNAVAILABLE",
      });
    }

    let mutation: MixedCashTenderMutation;
    try {
      mutation = await cashTender.appendCashTenderAtomically({
        ...input,
        actor: this.options.actor,
      });
      const after = snapshotFromTruth(mutation.truth, input.orderGuid);
      assertPersistedTender(
        after.truth.tenders,
        mutation.tenderGuid,
        "cash",
        input.amount.cents,
      );
      if (
        (!mutation.replayed &&
          after.paidCents !== before.paidCents + input.amount.cents) ||
        (mutation.replayed && after.paidCents !== before.paidCents)
      ) {
        throw new Error("Cash tender amount does not match persisted truth.");
      }
      return result(classifyCompletedMutation(after), after, {
        tenderGuid: mutation.tenderGuid,
        cashSettlement: mutation.cashSettlement,
      });
    } catch {
      return result("recovery-required", before, {
        errorCode: "MIXED_CASH_COMMIT_FAILED",
      });
    }
  }

  private async removeTenderOnce(
    input: RemoveMixedTenderInput,
  ): Promise<MixedPaymentResult> {
    const before = await this.loadSnapshot(input.orderGuid);
    const blocking = await this.options.paymentAttempts.getBlockingAttempt(
      input.orderGuid,
    );
    if (blocking) return blockingResult(before, blocking);
    if (before.truth.state !== "Completing") {
      throw new MixedPaymentValidationError(
        "ORDER_NOT_COMPLETING",
        "Tender reversal is only allowed while the order is Completing.",
      );
    }

    const source = before.truth.tenders.find(
      (tender) => tender.tenderGuid === input.tenderGuid,
    );
    if (!source || source.amount.cents <= 0) {
      throw new MixedPaymentValidationError(
        "TENDER_NOT_REVERSIBLE",
        "The selected tender does not exist or is already a reversal.",
      );
    }
    const existingReversal = before.truth.reversalLinks.find(
      (link) => link.sourceTenderGuid === source.tenderGuid,
    );
    if (existingReversal) {
      if (existingReversal.actionId !== input.actionId) {
        throw new MixedPaymentValidationError(
          "TENDER_ALREADY_REVERSED",
          "The selected tender already has an immutable reversal.",
        );
      }
      return result(classifyCompletedMutation(before), before, {
        tenderGuid: existingReversal.reversalTenderGuid,
      });
    }
    const reversal = this.options.tenderReversal;
    if (!reversal) {
      return result("recovery-required", before, {
        capability: "unavailable",
        errorCode: "TENDER_REVERSAL_UNAVAILABLE",
      });
    }

    let mutation: MixedTenderReversalMutation;
    try {
      mutation = await reversal.reverseTender({
        ...input,
        actor: this.options.actor,
      });
    } catch {
      return result("recovery-required", before, {
        errorCode: "TENDER_REVERSAL_FAILED",
      });
    }
    if (mutation.state !== "reversed") {
      const status =
        mutation.state === "unknown"
          ? "unknown"
          : mutation.state === "pending"
            ? "pending"
            : mutation.state;
      return result(status, before, {
        tenderGuid: input.tenderGuid,
        errorCode:
          mutation.state === "unknown" ? "TENDER_REVERSAL_UNKNOWN" : null,
      });
    }

    try {
      if (!mutation.reversalTenderGuid?.trim()) {
        throw new Error("Missing reversal tender id.");
      }
      const after = snapshotFromTruth(mutation.truth, input.orderGuid);
      assertPersistedTender(
        after.truth.tenders,
        source.tenderGuid,
        source.method,
        source.amount.cents,
      );
      assertPersistedTender(
        after.truth.tenders,
        mutation.reversalTenderGuid,
        source.method,
        -source.amount.cents,
      );
      assertPersistedReversalLink(
        after.truth.reversalLinks,
        input.actionId,
        source.tenderGuid,
        mutation.reversalTenderGuid,
      );
      if (
        !mutation.replayed &&
        after.paidCents !== before.paidCents - source.amount.cents
      ) {
        throw new Error("Tender reversal amount does not match persisted truth.");
      }
      return result(classifyCompletedMutation(after), after, {
        tenderGuid: mutation.reversalTenderGuid,
      });
    } catch {
      return result("recovery-required", before, {
        tenderGuid: mutation.reversalTenderGuid,
        errorCode: "TENDER_REVERSAL_TRUTH_MISMATCH",
      });
    }
  }

  private async loadSnapshot(orderGuid: string): Promise<PaymentSnapshot> {
    const truth = await this.options.orderTruth.getPaymentTruth(orderGuid);
    if (!truth) {
      throw new MixedPaymentValidationError(
        "ORDER_NOT_FOUND",
        `Persisted payment order was not found: ${orderGuid}`,
      );
    }
    return snapshotFromTruth(truth, orderGuid);
  }
}

function snapshotFromTruth(
  truth: MixedPaymentOrderTruth,
  expectedOrderGuid: string,
): PaymentSnapshot {
  if (truth.orderGuid !== expectedOrderGuid) {
    throw new Error("Persisted payment truth belongs to another order.");
  }
  assertAudInteger(truth.actualAmount, "Order actual amount");
  if (truth.actualAmount.cents < 0) {
    throw new Error("Mixed purchase order amount cannot be negative.");
  }

  let paidCents = 0;
  const tenderIds = new Set<string>();
  for (const tender of truth.tenders) {
    assertAudInteger(tender.amount, "Tender amount");
    if (!tender.tenderGuid.trim() || tenderIds.has(tender.tenderGuid)) {
      throw new Error("Persisted tender identity is invalid.");
    }
    tenderIds.add(tender.tenderGuid);
    paidCents = safeAdd(paidCents, tender.amount.cents);
  }
  assertPersistedReversalLinks(truth);
  if (paidCents < 0 || paidCents > truth.actualAmount.cents) {
    throw new Error("Persisted tenders exceed the order payment bounds.");
  }
  return {
    truth,
    paidCents,
    remainingCents: truth.actualAmount.cents - paidCents,
  };
}

function classifyCompletedMutation(
  snapshot: PaymentSnapshot,
): "partial" | "completed" {
  if (snapshot.remainingCents === 0) {
    if (isCompletedOrderState(snapshot.truth.state)) return "completed";
    throw new Error("Zero-balance order has not been durably completed.");
  }
  if (snapshot.truth.state !== "Completing") {
    throw new Error("Partial tender order must remain in Completing.");
  }
  return "partial";
}

function completedResult(snapshot: PaymentSnapshot): MixedPaymentResult {
  if (
    snapshot.remainingCents !== 0 ||
    !isCompletedOrderState(snapshot.truth.state)
  ) {
    return result("recovery-required", snapshot, {
      errorCode: "ZERO_BALANCE_ORDER_NOT_COMPLETED",
    });
  }
  return result("completed", snapshot);
}

function blockingResult(
  snapshot: PaymentSnapshot,
  attempt: PaymentAttempt,
): MixedPaymentResult {
  if (attempt.orderGuid !== snapshot.truth.orderGuid) {
    return result("recovery-required", snapshot, {
      attemptId: attempt.attemptId,
      errorCode: "BLOCKING_ATTEMPT_ORDER_MISMATCH",
    });
  }
  switch (attempt.state) {
    case "Created":
    case "Submitted":
    case "Approved":
      return result("awaiting-terminal", snapshot, {
        attemptId: attempt.attemptId,
        errorCode:
          attempt.state === "Approved"
            ? "APPROVED_COMPLETION_REQUIRED"
            : "PAYMENT_TERMINAL_AWAITED",
      });
    case "Pending":
      return result("pending", snapshot, {
        attemptId: attempt.attemptId,
      });
    case "Unknown":
      return result("unknown", snapshot, {
        attemptId: attempt.attemptId,
        errorCode: "PAYMENT_STATUS_UNKNOWN",
      });
    case "Declined":
      return result("declined", snapshot, {
        attemptId: attempt.attemptId,
        errorCode:
          (attempt.provider === "square" &&
            attempt.lastErrorCode === "SQUARE_SANDBOX_AMOUNT_LIMIT_EXCEEDED") ||
          (attempt.provider === "linkly-cloud" &&
            attempt.lastErrorCode ===
              "LINKLY_CLOUD_TERMINAL_SELECTION_CONFLICT")
            ? attempt.lastErrorCode
            : null,
      });
    case "Cancelled":
      return result("cancelled", snapshot, {
        attemptId: attempt.attemptId,
      });
  }
}

function result(
  status: MixedPaymentStatus,
  snapshot: PaymentSnapshot,
  overrides: Partial<
    Pick<
      MixedPaymentResult,
      | "attemptId"
      | "tenderGuid"
      | "capability"
      | "errorCode"
      | "cashSettlement"
    >
  > = {},
): MixedPaymentResult {
  return {
    status,
    orderGuid: snapshot.truth.orderGuid,
    remaining: { currency: "AUD", cents: snapshot.remainingCents },
    attemptId: null,
    tenderGuid: null,
    capability: "available",
    errorCode: null,
    ...overrides,
  };
}

function executionMatchesStart(
  execution: PaymentAttemptExecutionResult,
  input: StartPaymentAttemptInput,
): boolean {
  const { attempt } = execution;
  return (
    attempt.attemptId.trim().length > 0 &&
    attempt.idempotencyKey.trim().length > 0 &&
    attempt.orderGuid === input.orderGuid &&
    attempt.provider === input.provider &&
    attempt.operation === input.operation &&
    attempt.amount.currency === input.amount.currency &&
    attempt.amount.cents === input.amount.cents
  );
}

function attemptMatchesStart(
  attempt: PaymentAttempt,
  input: StartPaymentAttemptInput,
): boolean {
  return executionMatchesStart(
    { attempt, receiptText: null, responseCode: null },
    input,
  );
}

function isValidRecoverablePurchaseIdentity(
  attempt: PaymentAttempt,
  input: RecoverMixedOnlineAttemptInput,
  snapshot: PaymentSnapshot,
): boolean {
  return (
    attempt.attemptId === input.attemptId &&
    attempt.attemptId.trim().length > 0 &&
    attempt.idempotencyKey.trim().length > 0 &&
    attempt.orderGuid === input.orderGuid &&
    attempt.operation === "purchase" &&
    (attempt.provider === "square" ||
      attempt.provider === "linkly-cloud" ||
      attempt.provider === "voucher" ||
      attempt.provider === "manual-card") &&
    attempt.amount.currency === "AUD" &&
    Number.isSafeInteger(attempt.amount.cents) &&
    attempt.amount.cents > 0 &&
    attempt.amount.cents <= snapshot.truth.actualAmount.cents &&
    Number.isFinite(Date.parse(attempt.createdAtIso))
  );
}

function sameImmutableAttemptIdentity(
  left: PaymentAttempt,
  right: PaymentAttempt,
): boolean {
  return (
    left.attemptId === right.attemptId &&
    left.idempotencyKey === right.idempotencyKey &&
    left.orderGuid === right.orderGuid &&
    left.provider === right.provider &&
    left.operation === right.operation &&
    left.amount.currency === right.amount.currency &&
    left.amount.cents === right.amount.cents &&
    left.createdAtIso === right.createdAtIso
  );
}

function assertPersistedTender(
  tenders: readonly OrderTender[],
  tenderGuid: string,
  method: OrderTender["method"],
  amountCents: number,
): void {
  const tender = tenders.find((candidate) => candidate.tenderGuid === tenderGuid);
  if (
    !tender ||
    tender.method !== method ||
    tender.amount.currency !== "AUD" ||
    tender.amount.cents !== amountCents
  ) {
    throw new Error("Persisted tender does not match the durable mutation result.");
  }
}

function assertPersistedReversalLink(
  links: readonly MixedTenderReversalLink[],
  actionId: string,
  sourceTenderGuid: string,
  reversalTenderGuid: string,
): void {
  if (
    links.some(
      (link) =>
        link.actionId === actionId &&
        link.sourceTenderGuid === sourceTenderGuid &&
        link.reversalTenderGuid === reversalTenderGuid,
    )
  ) {
    return;
  }
  throw new Error("Persisted reversal link does not match the durable mutation result.");
}

function assertPersistedReversalLinks(truth: MixedPaymentOrderTruth): void {
  const actionIds = new Set<string>();
  const sourceIds = new Set<string>();
  const reversalIds = new Set<string>();
  for (const link of truth.reversalLinks) {
    if (
      !link.actionId.trim() ||
      !link.sourceTenderGuid.trim() ||
      !link.reversalTenderGuid.trim() ||
      actionIds.has(link.actionId) ||
      sourceIds.has(link.sourceTenderGuid) ||
      reversalIds.has(link.reversalTenderGuid)
    ) {
      throw new Error("Persisted tender reversal identity is invalid.");
    }
    const source = truth.tenders.find(
      (candidate) => candidate.tenderGuid === link.sourceTenderGuid,
    );
    const reversal = truth.tenders.find(
      (candidate) => candidate.tenderGuid === link.reversalTenderGuid,
    );
    if (
      !source ||
      source.amount.cents <= 0 ||
      !reversal ||
      reversal.method !== source.method ||
      reversal.amount.currency !== source.amount.currency ||
      reversal.amount.cents !== -source.amount.cents
    ) {
      throw new Error("Persisted tender reversal link is inconsistent.");
    }
    actionIds.add(link.actionId);
    sourceIds.add(link.sourceTenderGuid);
    reversalIds.add(link.reversalTenderGuid);
  }
}

async function safelyFindBlockingAttempt(
  attempts: MixedPaymentAttemptPort,
  orderGuid: string,
): Promise<PaymentAttempt | null> {
  try {
    return await attempts.getBlockingAttempt(orderGuid);
  } catch {
    return null;
  }
}

function assertActionInput(actionId: string, orderGuid: string): void {
  assertRequiredId(actionId, "actionId");
  assertRequiredId(orderGuid, "orderGuid");
}

function assertRecoveryDeadlineControl(
  input: RecoverMixedOnlineAttemptInput,
): void {
  if (input.signal === undefined && input.deadlineAtMs === undefined) return;
  if (
    input.signal === undefined ||
    input.deadlineAtMs === undefined ||
    !Number.isFinite(input.deadlineAtMs)
  ) {
    throw new MixedPaymentValidationError(
      "RECOVERY_CONTROL_INVALID",
      "Recovery signal and absolute deadline must be supplied together.",
    );
  }
}

function actorSignature(actor: AuditActorSnapshot): string {
  return JSON.stringify(
    auditActorPayload({
      cashierId: actor.cashierId,
      cashierName: actor.cashierName,
      userGuid: actor.userGuid,
    }),
  );
}

function assertRequiredId(value: string, label: string): void {
  if (!value.trim()) {
    throw new MixedPaymentValidationError(
      "IDENTITY_REQUIRED",
      `${label} is required.`,
    );
  }
}

function assertPositiveAud(amount: Money): void {
  assertAudInteger(amount, "Selected payment amount");
  if (amount.cents <= 0) {
    throw new MixedPaymentValidationError(
      "INVALID_PAYMENT_AMOUNT",
      "Selected payment amount must be positive.",
    );
  }
}

function assertAudInteger(amount: Money, label: string): void {
  if (amount.currency !== "AUD" || !Number.isSafeInteger(amount.cents)) {
    throw new MixedPaymentValidationError(
      "INVALID_PAYMENT_AMOUNT",
      `${label} must be AUD integer cents.`,
    );
  }
}

function assertWithinRemaining(amount: Money, remainingCents: number): void {
  if (amount.cents > remainingCents) {
    throw new MixedPaymentValidationError(
      "PAYMENT_EXCEEDS_REMAINING",
      "Selected payment amount exceeds the persisted remaining balance.",
    );
  }
}

function assertOnlineProvider(provider: PaymentProvider): void {
  if (
    provider !== "square" &&
    provider !== "linkly-cloud" &&
    provider !== "voucher" &&
    provider !== "manual-card"
  ) {
    throw new MixedPaymentValidationError(
      "UNSUPPORTED_PAYMENT_PROVIDER",
      "Mixed online tender provider is unsupported.",
    );
  }
}

function safeAdd(left: number, right: number): number {
  const value = left + right;
  if (!Number.isSafeInteger(value)) {
    throw new Error("Persisted tender total exceeds safe integer bounds.");
  }
  return value;
}

function isCompletedOrderState(state: LocalOrder["state"]): boolean {
  return (
    state === "CompletedLocal" ||
    state === "PendingSync" ||
    state === "Syncing" ||
    state === "Synced" ||
    state === "Blocked403" ||
    state === "Rejected"
  );
}

function deleteMixedActionIfCurrent(
  orderGuid: string,
  expected: InflightMixedAction,
): void {
  if (sharedMixedActions.get(orderGuid) === expected) {
    sharedMixedActions.delete(orderGuid);
  }
}
