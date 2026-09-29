import type {
  DurableOfflineCashRefundPort,
  DurableOnlineReturnRefundPort,
  OfflineCashRefundInput,
  OnlineReturnRefundInput,
  PreparedOnlineReturnAttempt,
  ReturnAllocationExternalOutcome,
} from "@hb/pos-domain/features/returns/adapters/durable-return-execution-orchestrator";
import type { SqliteReturnApiAttemptStore } from "@/core/db/sqlite-return-api-attempt-store";

/** 在线现金退款的耐久本地 attempt；账本绑定与完成都要核对这条记录。 */
export type ReturnCashAttemptPort = Pick<
  SqliteReturnApiAttemptStore,
  "prepareOrLoad" | "get" | "compareAndSetState"
>;

export type ProductionReturnOnlineRefundRouterOptions = Readonly<{
  providerRefund: DurableOnlineReturnRefundPort | null;
  cashAttempts: ReturnCashAttemptPort;
  nowIso(): string;
}>;

/**
 * 在线现金退款没有支付 provider 副作用：workflow 已完成联网门禁，现金事实会由
 * return ledger、订单 outbox 与后续钱箱计划同事务落库。因此这里仅把已耐久保存的
 * externalAttemptId 绑定回 allocation，绝不在 prepare/submit/recover 中开钱箱。
 */
export class ProductionReturnOnlineRefundRouter
  implements DurableOnlineReturnRefundPort
{
  public constructor(
    private readonly options: ProductionReturnOnlineRefundRouterOptions,
  ) {}

  public async prepareAttempt(
    input: Omit<
      OnlineReturnRefundInput,
      "attemptKind" | "externalActionId" | "durableAttemptId"
    >,
  ): Promise<PreparedOnlineReturnAttempt> {
    if (input.method !== "cash") {
      return this.requireProvider(input.method).prepareAttempt(input);
    }
    validateCashPreparation(input);
    const attemptId = requiredText(input.externalAttemptId);
    // 账本以 hbpos-api 绑定前会核对 return_api_attempts；此前从未写入，
    // 导致所有在线现金退款在绑定时失败并卡在未知恢复。重放沿用首次创建时间。
    const existing = await this.options.cashAttempts.get(attemptId);
    await this.options.cashAttempts.prepareOrLoad({
      durableAttemptId: attemptId,
      externalAttemptId: attemptId,
      returnOrderGuid: requiredText(input.returnOrderGuid),
      actionId: requiredText(input.actionId),
      allocationId: requiredText(input.allocationId),
      externalActionId: attemptId,
      idempotencyKey: `return-cash:${attemptId}`,
      method: "cash",
      signedAmountCents: input.signedAmountCents,
      protectedContext: null,
      createdAtIso: existing?.createdAtIso ?? this.options.nowIso(),
    });
    return Object.freeze({
      attemptKind: "hbpos-api" as const,
      externalActionId: attemptId,
      durableAttemptId: attemptId,
    });
  }

  public async submit(
    input: OnlineReturnRefundInput,
  ): Promise<ReturnAllocationExternalOutcome> {
    if (input.method !== "cash") {
      return this.requireProvider(input.method).submit(input);
    }
    assertCashBinding(input);
    await this.approveCashAttempt(input);
    return Object.freeze({ status: "completed" as const });
  }

  public async recover(
    input: OnlineReturnRefundInput &
      Readonly<{ protectedRecoveryKey: string | null }>,
  ): Promise<ReturnAllocationExternalOutcome> {
    if (input.method !== "cash") {
      return this.requireProvider(input.method).recover(input);
    }
    assertCashBinding(input);
    if (input.protectedRecoveryKey !== null) {
      throw new ReturnOnlineRefundRouterError(
        "RETURN_CASH_RECOVERY_KEY_INVALID",
      );
    }
    await this.approveCashAttempt(input);
    return Object.freeze({ status: "completed" as const });
  }

  /**
   * 现金由收银员当面退付，没有外部 provider；账本要求 completed 对应 Approved，
   * 这里按合法转换推进（Created→Submitted→Approved），重复提交/恢复保持幂等。
   */
  private async approveCashAttempt(input: OnlineReturnRefundInput): Promise<void> {
    const attemptId = requiredText(input.durableAttemptId);
    const attempt = await this.options.cashAttempts.get(attemptId);
    if (
      !attempt ||
      attempt.method !== "cash" ||
      attempt.actionId !== input.actionId ||
      attempt.allocationId !== input.allocationId ||
      attempt.returnOrderGuid !== input.returnOrderGuid ||
      attempt.signedAmountCents !== input.signedAmountCents
    ) {
      throw new ReturnOnlineRefundRouterError("RETURN_CASH_ATTEMPT_MISMATCH");
    }
    let state = attempt.state;
    if (state === "Created") {
      await this.options.cashAttempts.compareAndSetState({
        durableAttemptId: attemptId,
        expected: "Created",
        next: "Submitted",
        updatedAtIso: this.options.nowIso(),
      });
      state = "Submitted";
    }
    if (state === "Submitted" || state === "Pending" || state === "Unknown") {
      await this.options.cashAttempts.compareAndSetState({
        durableAttemptId: attemptId,
        expected: state,
        next: "Approved",
        updatedAtIso: this.options.nowIso(),
      });
    }
    // 以持久状态为准：并发推进或终态拒绝都不能被报告为 completed。
    const latest = await this.options.cashAttempts.get(attemptId);
    if (latest?.state !== "Approved") {
      throw new ReturnOnlineRefundRouterError("RETURN_CASH_ATTEMPT_NOT_APPROVED");
    }
  }

  private requireProvider(method: OnlineReturnRefundInput["method"]) {
    if (method !== "card" && method !== "voucher") {
      throw new ReturnOnlineRefundRouterError(
        "RETURN_REFUND_METHOD_UNSUPPORTED",
      );
    }
    if (!this.options.providerRefund) {
      throw new ReturnOnlineRefundRouterError(
        "RETURN_PROVIDER_REFUND_UNAVAILABLE",
      );
    }
    return this.options.providerRefund;
  }
}

export class ProductionReturnCashRefundAdapter
  implements DurableOfflineCashRefundPort
{
  public async submit(
    input: OfflineCashRefundInput,
  ): Promise<ReturnAllocationExternalOutcome> {
    validateOfflineCashProof(input);
    return Object.freeze({ status: "completed" as const });
  }

  public async recover(
    input: OfflineCashRefundInput &
      Readonly<{ protectedRecoveryKey: string | null }>,
  ): Promise<ReturnAllocationExternalOutcome> {
    validateOfflineCashProof(input);
    if (input.protectedRecoveryKey !== null) {
      throw new ReturnOnlineRefundRouterError(
        "RETURN_CASH_RECOVERY_KEY_INVALID",
      );
    }
    return Object.freeze({ status: "completed" as const });
  }
}

export class ReturnOnlineRefundRouterError extends Error {
  public constructor(public readonly code: string) {
    super(code);
    this.name = "ReturnOnlineRefundRouterError";
  }
}

function validateCashPreparation(
  input: Omit<
    OnlineReturnRefundInput,
    "attemptKind" | "externalActionId" | "durableAttemptId"
  >,
): void {
  requiredText(input.actionId);
  requiredText(input.allocationId);
  requiredText(input.externalAttemptId);
  requiredText(input.returnOrderGuid);
  if (
    !Number.isSafeInteger(input.signedAmountCents) ||
    input.signedAmountCents >= 0
  ) {
    throw new ReturnOnlineRefundRouterError(
      "RETURN_CASH_AMOUNT_INVALID",
    );
  }
  const capacityId = nullableText(input.capacityId);
  const originalOrderGuid = nullableText(input.originalOrderGuid);
  if ((capacityId === null) !== (originalOrderGuid === null)) {
    throw new ReturnOnlineRefundRouterError(
      "RETURN_CASH_SOURCE_MISMATCH",
    );
  }
}

function assertCashBinding(input: OnlineReturnRefundInput): void {
  validateCashPreparation(input);
  const expected = requiredText(input.externalAttemptId);
  if (
    input.attemptKind !== "hbpos-api" ||
    requiredText(input.externalActionId) !== expected ||
    requiredText(input.durableAttemptId) !== expected
  ) {
    throw new ReturnOnlineRefundRouterError(
      "RETURN_CASH_ATTEMPT_MISMATCH",
    );
  }
}

function validateOfflineCashProof(input: OfflineCashRefundInput): void {
  requiredText(input.actionId);
  requiredText(input.allocationId);
  requiredText(input.returnOrderGuid);
  const originalOrderGuid = requiredText(input.originalOrderGuid);
  const capacityId = requiredText(input.capacityId);
  const proof = input.offlineCashProof;
  const magnitude = -input.signedAmountCents;
  if (
    !Number.isSafeInteger(input.signedAmountCents) ||
    input.signedAmountCents >= 0 ||
    !Number.isSafeInteger(magnitude) ||
    requiredText(proof.evidenceId).length === 0 ||
    requiredText(proof.capacityId) !== capacityId ||
    requiredText(proof.originalOrderGuid) !== originalOrderGuid ||
    !Number.isSafeInteger(proof.remainingCents) ||
    proof.remainingCents < magnitude
  ) {
    throw new ReturnOnlineRefundRouterError(
      "RETURN_OFFLINE_CASH_PROOF_MISMATCH",
    );
  }
}

function requiredText(value: unknown): string {
  if (typeof value !== "string" || value.trim().length === 0) {
    throw new ReturnOnlineRefundRouterError(
      "RETURN_CASH_ATTEMPT_MISMATCH",
    );
  }
  return value.trim();
}

function nullableText(value: unknown): string | null {
  if (value === null) return null;
  return requiredText(value);
}
