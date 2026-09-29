import type {
  Money,
  OnlinePaymentPort,
  PaymentAttempt,
  PaymentProvider,
} from "@/core/contracts";
import type {
  ReturnTenderCapacity,
  SqliteReturnCapacityVault,
} from "@/core/db/sqlite-return-capacity-vault";
import {
  PaymentAttemptService,
  type PaymentAttemptExecutionResult,
  type PaymentProviderRegistryPort,
  type StartPaymentAttemptInput,
  type TrustedRefundReferenceSeed,
  type TrustedRefundReferenceSeedHook,
} from "@hb/pos-payments-core/features/payments/payment-attempt-service";
import type { DurableVoucherPreparationService } from "@/features/payments/runtime/voucher-preparation";
import type {
  DurableOnlineReturnRefundPort,
  OnlineReturnRefundInput,
  PreparedOnlineReturnAttempt,
  ReturnAllocationExternalOutcome,
} from "@hb/pos-domain/features/returns/adapters/durable-return-execution-orchestrator";

/** 退款桥只需要 Vault 的只读面；UI 和 route 不会取得受保护 provider context。 */
export interface ReturnCapacityVaultReadPort {
  get(capacityId: string): Promise<ReturnTenderCapacity | null>;
  resolveProtectedContext(
    capacityId: string,
  ): Promise<Readonly<Record<string, unknown>> | null>;
}

export type ProductionReturnRefundAdapterOptions = Readonly<{
  paymentAttempts: Pick<
    PaymentAttemptService,
    "prepareAttempt" | "startAttempt" | "recoverAttempt" | "getAttempt"
  >;
  capacityVault: Pick<
    SqliteReturnCapacityVault,
    "get" | "resolveProtectedContext"
  >;
  providers: PaymentProviderRegistryPort;
  voucherPreparation: Pick<DurableVoucherPreparationService, "prepareRefund">;
}>;

type ResolvedRefund = Readonly<{
  provider: PaymentProvider;
  start: StartPaymentAttemptInput;
}>;

/**
 * 返回退款的唯一 provider 边界：原支付引用始终由 PaymentAttemptService 的 hook
 * 在 Created 落库前从 Vault 读取；本适配器不把 reference 交给 workflow、ledger 或 UI。
 */
export class ProductionReturnRefundAdapter
  implements DurableOnlineReturnRefundPort
{
  public readonly trustedRefundReferenceSeed: TrustedRefundReferenceSeedHook =
    async (input) => {
      const capacity = await this.requireCapacity(input.capacity.capacityId);
      const amountCents = refundMagnitude(input.capacity.amount);
      if (
        input.operation !== "refund" ||
        capacity.method !== "card" ||
        capacity.remainingAmountCents < amountCents
      ) {
        throw new ReturnRefundAdapterError("REFUND_CAPACITY_MISMATCH");
      }
      const context = await this.requireContext(capacity.capacityId);
      const provider = providerFromContext(context);
      if (provider !== input.provider) {
        throw new ReturnRefundAdapterError("REFUND_PROVIDER_MISMATCH");
      }
      return seedFromContext(context, provider);
    };

  public constructor(
    private readonly options: ProductionReturnRefundAdapterOptions,
  ) {}

  public async prepareAttempt(
    input: Omit<
      OnlineReturnRefundInput,
      "attemptKind" | "externalActionId" | "durableAttemptId"
    >,
  ): Promise<PreparedOnlineReturnAttempt> {
    const resolved = await this.resolveRefund(input);
    if (resolved.provider === "voucher") {
      await this.options.voucherPreparation.prepareRefund({
        actionId: resolved.start.actionId,
        orderGuid: resolved.start.orderGuid,
        // 退款编排没有 UI 原因字段；使用固定受信任分类，不接收用户自由文本。
        refundReason: "RETURN_REFUND",
      });
    }
    const prepared = await this.options.paymentAttempts.prepareAttempt(
      resolved.start,
    );
    assertAttemptMatches(prepared.attempt, resolved);
    return preparedBinding(input.externalAttemptId, prepared.attempt);
  }

  public async submit(
    input: OnlineReturnRefundInput,
  ): Promise<ReturnAllocationExternalOutcome> {
    const resolved = await this.resolveBoundRefund(input);
    const outcome = await this.options.paymentAttempts.startAttempt(resolved.start);
    assertAttemptMatches(outcome.attempt, resolved);
    return mapAttemptOutcome(outcome);
  }

  public async recover(
    input: OnlineReturnRefundInput & Readonly<{ protectedRecoveryKey: string | null }>,
  ): Promise<ReturnAllocationExternalOutcome> {
    const resolved = await this.resolveBoundRefund(input);
    // recoveryKey 由 durable return ledger 管理；provider 恢复永远只认绑定的 attemptId。
    void input.protectedRecoveryKey;
    const outcome = await this.options.paymentAttempts.recoverAttempt(
      input.durableAttemptId,
    );
    assertAttemptMatches(outcome.attempt, resolved);
    return mapAttemptOutcome(outcome);
  }

  private async resolveBoundRefund(
    input: OnlineReturnRefundInput,
  ): Promise<ResolvedRefund> {
    if (
      input.attemptKind !== "payment-provider" ||
      input.externalActionId !== input.externalAttemptId
    ) {
      throw new ReturnRefundAdapterError("REFUND_ATTEMPT_BINDING_MISMATCH");
    }
    const resolved = await this.resolveRefund(input);
    const attempt = await this.options.paymentAttempts.getAttempt(
      requiredText(input.durableAttemptId),
    );
    if (!attempt) throw new ReturnRefundAdapterError("REFUND_ATTEMPT_NOT_FOUND");
    assertAttemptMatches(attempt, resolved);
    if (attempt.attemptId !== input.durableAttemptId) {
      throw new ReturnRefundAdapterError("REFUND_ATTEMPT_BINDING_MISMATCH");
    }
    return resolved;
  }

  private async resolveRefund(
    input: Omit<
      OnlineReturnRefundInput,
      "attemptKind" | "externalActionId" | "durableAttemptId"
    >,
  ): Promise<ResolvedRefund> {
    if (input.method !== "card" && input.method !== "voucher") {
      throw new ReturnRefundAdapterError("REFUND_METHOD_UNSUPPORTED");
    }
    const originalOrderGuid = requiredText(input.originalOrderGuid);
    const capacityId = requiredText(input.capacityId);
    const signedAmountCents = requireRefundSignedCents(input.signedAmountCents);
    const amountCents = -signedAmountCents;
    const capacity = await this.requireCapacity(capacityId);
    if (
      capacity.originalOrderGuid !== originalOrderGuid ||
      !isRefundMethodAllowedForCapacity(input.method, capacity.method) ||
      capacity.remainingAmountCents < amountCents
    ) {
      throw new ReturnRefundAdapterError("REFUND_CAPACITY_MISMATCH");
    }
    const provider = await this.resolveProvider(input.method, capacity);
    assertRegisteredProvider(this.options.providers, provider);
    return {
      provider,
      start: {
        actionId: requiredText(input.externalAttemptId),
        orderGuid: requiredText(input.returnOrderGuid),
        provider,
        operation: "refund",
        // PaymentAttempt 是账本模型：退款必须保留负数，provider 再换算正 magnitude。
        amount: { currency: "AUD", cents: signedAmountCents },
        actor: input.actor,
        refundCapacityId: capacityId,
      },
    };
  }

  /**
   * 原路退款按受保护 context 解析 provider；礼券代替刷卡/现金额度一律签发新券，
   * 不读取原支付引用（手工刷卡、现金额度本就没有可用的集成凭据）。
   */
  private async resolveProvider(
    method: "card" | "voucher",
    capacity: ReturnTenderCapacity,
  ): Promise<PaymentProvider> {
    if (method === "voucher" && capacity.method !== "voucher") {
      return "voucher";
    }
    const provider = providerFromContext(
      await this.requireContext(capacity.capacityId),
    );
    if (
      (method === "card" && provider === "voucher") ||
      (method === "voucher" && provider !== "voucher")
    ) {
      throw new ReturnRefundAdapterError("REFUND_PROVIDER_MISMATCH");
    }
    return provider;
  }

  private async requireCapacity(
    capacityId: string,
  ): Promise<ReturnTenderCapacity> {
    const capacity = await this.options.capacityVault.get(requiredText(capacityId));
    if (!capacity) throw new ReturnRefundAdapterError("REFUND_CAPACITY_NOT_FOUND");
    return capacity;
  }

  private async requireContext(
    capacityId: string,
  ): Promise<Readonly<Record<string, unknown>>> {
    const context = await this.options.capacityVault.resolveProtectedContext(
      requiredText(capacityId),
    );
    if (!context) throw new ReturnRefundAdapterError("REFUND_CONTEXT_MISSING");
    return context;
  }
}

export class ReturnRefundAdapterError extends Error {
  public constructor(public readonly code: string) {
    super(`Return refund provider bridge rejected (${code}).`);
    this.name = "ReturnRefundAdapterError";
  }
}

/**
 * 退款方式与原额度方式：刷卡只能原路退回刷卡额度；礼券既可退回原礼券额度，
 * 也可代替刷卡/现金额度（额度仍按原支付扣减，由账本预留保证不超额）。
 */
function isRefundMethodAllowedForCapacity(
  method: "card" | "voucher",
  capacityMethod: ReturnTenderCapacity["method"],
): boolean {
  if (method === "card") return capacityMethod === "card";
  return (
    capacityMethod === "voucher" ||
    capacityMethod === "card" ||
    capacityMethod === "cash"
  );
}

function providerFromContext(
  context: Readonly<Record<string, unknown>>,
): PaymentProvider {
  if (matchesContext(context, { version: 1, provider: "square", paymentId: "" })) {
    requireTextField(context, "paymentId");
    return "square";
  }
  if (matchesContext(context, {
    version: 1,
    provider: "linkly-cloud",
    rfn: "",
    originalReference: "",
  })) {
    requireTextField(context, "rfn");
    requireTextField(context, "originalReference");
    return "linkly-cloud";
  }
  if (matchesContext(context, { version: 1, provider: "voucher" })) {
    return "voucher";
  }
  throw new ReturnRefundAdapterError("REFUND_CONTEXT_INVALID");
}

function seedFromContext(
  context: Readonly<Record<string, unknown>>,
  provider: Exclude<PaymentProvider, "voucher">,
): TrustedRefundReferenceSeed {
  switch (provider) {
    case "square":
      return { provider, paymentId: requireTextField(context, "paymentId") };
    case "linkly-cloud":
      return { provider, rfn: requireTextField(context, "rfn") };
    case "manual-card":
      throw new ReturnRefundAdapterError("REFUND_CONTEXT_INVALID");
  }
}

function matchesContext(
  context: Readonly<Record<string, unknown>>,
  expected: Readonly<Record<string, unknown>>,
): boolean {
  const keys = Object.keys(context).sort();
  const expectedKeys = Object.keys(expected).sort();
  return keys.length === expectedKeys.length
    && keys.every((key, index) => key === expectedKeys[index])
    && context.version === 1
    && context.provider === expected.provider;
}

function preparedBinding(
  externalActionId: string,
  attempt: PaymentAttempt,
): PreparedOnlineReturnAttempt {
  return {
    attemptKind: "payment-provider",
    externalActionId: requiredText(externalActionId),
    durableAttemptId: attempt.attemptId,
  };
}

function assertAttemptMatches(
  attempt: PaymentAttempt,
  resolved: ResolvedRefund,
): void {
  const expected = resolved.start;
  if (
    attempt.orderGuid !== expected.orderGuid ||
    attempt.provider !== expected.provider ||
    attempt.operation !== "refund" ||
    attempt.amount.currency !== "AUD" ||
    attempt.amount.cents !== expected.amount.cents
  ) {
    throw new ReturnRefundAdapterError("REFUND_ATTEMPT_MISMATCH");
  }
}

function mapAttemptOutcome(
  outcome: PaymentAttemptExecutionResult,
): ReturnAllocationExternalOutcome {
  switch (outcome.attempt.state) {
    case "Approved":
      return { status: "completed" };
    case "Declined":
    case "Cancelled":
      return { status: "declined" };
    case "Created":
    case "Submitted":
    case "Pending":
    case "Unknown":
      // 不根据 Unknown/Pending 另选 provider 或重新 refund；只能恢复同一 durable attempt。
      return { status: "unknown", protectedRecoveryKey: null };
    default:
      throw new ReturnRefundAdapterError("REFUND_ATTEMPT_STATE_INVALID");
  }
}

function assertRegisteredProvider(
  providers: PaymentProviderRegistryPort,
  provider: PaymentProvider,
): void {
  const registered: OnlinePaymentPort = providers.get(provider);
  if (registered.provider !== provider) {
    throw new ReturnRefundAdapterError("REFUND_PROVIDER_MISMATCH");
  }
}

function requireRefundSignedCents(signedAmountCents: number): number {
  if (!Number.isSafeInteger(signedAmountCents) || signedAmountCents >= 0) {
    throw new ReturnRefundAdapterError("REFUND_AMOUNT_INVALID");
  }
  if (signedAmountCents === Number.MIN_SAFE_INTEGER) {
    throw new ReturnRefundAdapterError("REFUND_AMOUNT_INVALID");
  }
  return signedAmountCents;
}

function refundMagnitude(amount: Money): number {
  if (
    amount.currency !== "AUD" ||
    !Number.isSafeInteger(amount.cents) ||
    amount.cents >= 0 ||
    amount.cents === Number.MIN_SAFE_INTEGER
  ) {
    throw new ReturnRefundAdapterError("REFUND_AMOUNT_INVALID");
  }
  return -amount.cents;
}

function requireTextField(
  context: Readonly<Record<string, unknown>>,
  key: string,
): string {
  const value = context[key];
  if (typeof value !== "string") {
    throw new ReturnRefundAdapterError("REFUND_CONTEXT_INVALID");
  }
  return requiredText(value);
}

function requiredText(value: string | null): string {
  const normalized = value?.trim();
  if (!normalized) throw new ReturnRefundAdapterError("REFUND_INPUT_INVALID");
  return normalized;
}
