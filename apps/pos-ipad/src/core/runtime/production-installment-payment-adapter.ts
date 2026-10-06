import type {
  InstallmentApprovedRefund,
  InstallmentMutationPaymentPort,
  PersistedInstallmentAction,
} from "./production-installment-runtime";
import type {
  PaymentAcknowledgementResult,
  PaymentAcknowledgementRuntimePort,
} from "@hb/pos-payments-core/features/payments/payment-acknowledgement-service";

import {
  canTransitionPaymentAttempt,
  createAud,
  normalizeCardSyncEvidence,
  type CardSyncEvidenceV1,
  type OnlinePaymentPort,
  type PaymentAttempt,
  type PaymentProvider,
  type PaymentProviderReferences,
  type PaymentProviderResult,
} from "@/core/contracts";
import type {
  InstallmentCancelRefundMode,
  InstallmentPaymentCommand,
  InstallmentRefundCommand,
} from "@/features/installments/installment-models";
import type { PaymentProviderRegistryPort } from "@hb/pos-payments-core/features/payments/payment-attempt-service";
import type { PaymentProviderAvailability } from "@/features/payments/runtime/payment-provider-registry";

export type { InstallmentCancelRefundMode };

export type InstallmentCardProvider = Extract<
  PaymentProvider,
  "square" | "linkly-cloud"
>;

export interface InstallmentCardProviderSelectionPort {
  /**
   * 返回当前明确启用的卡 provider。新 attempt 必须恰好得到一个值；
   * 已持久化 attempt 的恢复永远使用原 provider，不随设置切换。
   */
  loadEnabledProviders(): Promise<readonly InstallmentCardProvider[]>;
}

export type InstallmentOriginalTenderEvidence = Readonly<{
  evidenceId: string;
  sourceAttemptId: string;
  sourcePaymentGuid: string;
  installmentGuid: string;
  method: "cash" | "card" | "voucher";
  amountCents: number;
  provider: PaymentProvider | null;
  provenance: "local-approved-attempt" | "hbpos-protected-details";
}>;

export type InstallmentRefundProvenanceSnapshot = Readonly<{
  /** false 代表远端详情或 vault 未完整取得，调用方必须失败关闭。 */
  complete: boolean;
  installmentGuid: string;
  storeCode: string;
  requestingDeviceCode: string;
  paidAmountCents: number;
  tenders: readonly InstallmentOriginalTenderEvidence[];
}>;

/**
 * 实现方可从 Hbpos 受保护详情读取 Reference/CardTransactions，再直接写入 SQLCipher
 * vault；返回给适配器的 snapshot 只包含安全描述符，不得把原卡引用或券码暴露给 UI。
 */
export interface InstallmentRefundProvenanceRemotePort {
  resolveOrImport(input: Readonly<{
    installmentGuid: string;
    storeCode: string;
    requestingDeviceCode: string;
    /**
     * 退代金券模式不需要原卡引用/卡交易证据；缺省按原路退严格校验。
     * vault 以 action 冻结的 refundMode 为准，二者不一致时失败关闭。
     */
    refundMode?: InstallmentCancelRefundMode;
  }>): Promise<InstallmentRefundProvenanceSnapshot>;

  /**
   * 按 evidenceId 从 vault 注入 Square paymentId 或 Linkly RFN。
   * 返回值除 provider references 外必须与输入 attempt 身份完全相同。
   */
  seedRefundAttempt(input: Readonly<{
    evidence: InstallmentOriginalTenderEvidence;
    attempt: PaymentAttempt;
  }>): Promise<PaymentAttempt>;
}

/**
 * Runtime 在候选 action 取得稳定 actionId 后、写入 action ledger 前调用。
 * 实现必须按 actionId bind-or-get，不可覆盖既有 secret；全部字段只存二次加密 BLOB。
 * action ledger 竞争失败时允许留下不可执行的孤儿 intent，后续可按保留策略清理，
 * 但绝不能把 losing candidate 的券材料绑定到 winning action。
 */
export interface InstallmentVoucherIntentVaultPort {
  stage(input: Readonly<{
    actionId: string;
    installmentGuid: string;
    paymentGuid: string;
    storeCode: string;
    deviceCode: string;
    cashierId: string;
    amountCents: number;
    voucherReference: string;
    /**
     * 正常新交易由 Voucher provider 在 query/lock 后生成 token，因此这里通常为 null。
     * 非空值只用于恢复已耐久锁定的旧 intent，不能由普通 UI 手工构造。
     */
    voucherReservationToken: string | null;
  }>): Promise<void>;
}

/**
 * 券输入和 provider 产生的券码/token 都只能由受保护实现处理。
 * prepare 必须幂等，并在 provider 请求前确认 actionId 对应的 UI secret 已耐久化。
 */
export interface InstallmentVoucherMaterialPort {
  prepare(input: Readonly<{
    action: PersistedInstallmentAction;
    record: InstallmentProviderAttemptRecord;
  }>): Promise<void>;
  resolveApproved(input: Readonly<{
    action: PersistedInstallmentAction;
    record: InstallmentProviderAttemptRecord;
    protectedReference: string;
  }>): Promise<
    Readonly<{
      reference: string;
      reservationToken: string | null;
    }>
  >;
}

export type InstallmentApprovedPaymentMaterial =
  | Readonly<{
      kind: "card";
      evidence: CardSyncEvidenceV1;
      receiptText: string | null;
    }>
  | Readonly<{
      kind: "voucher";
      reference: string;
      reservationToken: string | null;
    }>;

export type InstallmentProviderAttemptRecord = Readonly<{
  actionId: string;
  paymentGuid: string;
  sourcePaymentGuid: string | null;
  originalTenderEvidenceId: string;
  sourceAttemptId: string | null;
  sequence: number;
  attempt: PaymentAttempt;
}>;

export type InstallmentCashSettlement = Readonly<{
  actionId: string;
  settlementId: string;
  paymentGuid: string;
  sourcePaymentGuid: string | null;
  originalTenderEvidenceId: string;
  sourceAttemptId: string | null;
  sequence: number;
  operation: "purchase" | "refund";
  amountCents: number;
  idempotencyKey: string;
  state: "Prepared" | "Approved";
}>;

export type InstallmentProviderAttemptPlan = Readonly<{
  actionId: string;
  attempts: readonly InstallmentProviderAttemptRecord[];
  cashSettlements: readonly InstallmentCashSettlement[];
}>;

/**
 * 独立分期支付账本，禁止复用带 local_orders FK 的 PaymentAttemptRepository。
 *
 * 数据库实现要求：
 * - actionId 下原子 bind-or-get 不可变 plan，竞争者只能取得同一 plan；
 * - attempt identity/source provenance/现金 settlement identity 不可改；
 * - provider references、回单、卡证据、券码和 token 只进入二次加密 BLOB；
 * - compareAndUpdateAttempt 是 state+references CAS，Approved material 同事务写入；
 * - purchase Approved 同事务建立 paymentGuid 唯一的原付款证据；
 * - approveCashSettlements 原子 Prepared→Approved，purchase 同事务建立原付款证据。
 */
export interface InstallmentProviderAttemptStorePort {
  loadAction(actionId: string): Promise<PersistedInstallmentAction | null>;
  loadPlan(actionId: string): Promise<InstallmentProviderAttemptPlan | null>;
  bindPlanOrGet(
    candidate: InstallmentProviderAttemptPlan,
  ): Promise<InstallmentProviderAttemptPlan>;
  compareAndUpdateAttempt(input: Readonly<{
    expected: InstallmentProviderAttemptRecord;
    nextAttempt: PaymentAttempt;
    approvedMaterial?: InstallmentApprovedPaymentMaterial;
  }>): Promise<boolean>;
  loadApprovedMaterial(
    attemptId: string,
  ): Promise<InstallmentApprovedPaymentMaterial | null>;
  approveCashSettlements(
    actionId: string,
  ): Promise<readonly InstallmentCashSettlement[]>;
  get?(attemptId: string): Promise<PaymentAttempt | null>;
  findLegacyLinklyAttemptForSession?(
    terminal: Readonly<{ storeCode: string; deviceCode: string }>,
    sessionId: string,
    idempotencyKey: string,
  ): Promise<PaymentAttempt | null>;
  markProviderAcknowledged?(expected: PaymentAttempt, acknowledgedAtIso: string): Promise<boolean>;
  canProviderAcknowledged?(attempt: PaymentAttempt): Promise<boolean>;
  verifyProviderEnvironment?(expected: PaymentAttempt, verifiedEnvironment: string): Promise<boolean>;
}

export type ProductionInstallmentPaymentAdapterOptions = Readonly<{
  store: InstallmentProviderAttemptStorePort;
  providers: PaymentProviderRegistryPort;
  cardProviderSelection: InstallmentCardProviderSelectionPort;
  provenance: InstallmentRefundProvenanceRemotePort;
  voucherMaterials: InstallmentVoucherMaterialPort;
  acknowledgements?: PaymentAcknowledgementRuntimePort | null;
  createId(): string;
  nowIso(): string;
}>;

export type InstallmentPaymentAdapterErrorCode =
  | "INSTALLMENT_ACTION_NOT_FOUND"
  | "INSTALLMENT_ACTION_INVALID"
  | "INSTALLMENT_ATTEMPT_DURABILITY_REQUIRED"
  | "INSTALLMENT_ATTEMPT_PLAN_CONFLICT"
  | "INSTALLMENT_APPROVED_MATERIAL_INVALID"
  | "INSTALLMENT_CARD_PROVIDER_SELECTION_INVALID"
  | "INSTALLMENT_PROVIDER_UNAVAILABLE"
  | "INSTALLMENT_REFUND_PROVENANCE_INVALID"
  | "INSTALLMENT_VOUCHER_MATERIAL_INVALID";

export class InstallmentPaymentAdapterError extends Error {
  public constructor(
    public readonly code: InstallmentPaymentAdapterErrorCode,
    message: string,
  ) {
    super(message);
    this.name = "InstallmentPaymentAdapterError";
  }
}

type PaymentAdapterResult = Awaited<
  ReturnType<InstallmentMutationPaymentPort["beginOrRecover"]>
>;

type AttemptExecution =
  | Readonly<{
      kind: "approved";
      record: InstallmentProviderAttemptRecord;
    }>
  | Readonly<{ kind: "declined"; record: InstallmentProviderAttemptRecord }>
  | Readonly<{ kind: "unknown"; record: InstallmentProviderAttemptRecord }>;

const REFERENCE_KEYS = [
  "checkoutId",
  "paymentId",
  "sessionId",
  "txnRef",
  "rfn",
  "voucherReservationToken",
] as const satisfies readonly (keyof PaymentProviderReferences)[];

/**
 * 分期专用支付编排。所有入口只接受已耐久化 actionId；页面无法传 provider、
 * provider reference、paymentGuid 或退款来源。
 */
export class ProductionInstallmentPaymentAdapter
  implements InstallmentMutationPaymentPort
{
  private readonly inflight = new Map<string, Promise<PaymentAdapterResult>>();

  public constructor(
    private readonly options: ProductionInstallmentPaymentAdapterOptions,
  ) {}

  public beginOrRecover(
    persistedActionId: string,
  ): Promise<PaymentAdapterResult> {
    return this.run(persistedActionId);
  }

  public recoverBlocking(
    persistedActionId: string,
  ): Promise<PaymentAdapterResult> {
    return this.run(persistedActionId);
  }

  /** 中文注释：ACK 恢复绝不触发 submit、refund 或 provider recover。 */
  public async acknowledgeProviderAttempts(
    persistedActionId: string,
  ): Promise<boolean> {
    const acknowledgements = this.options.acknowledgements;
    const plan = await this.options.store.loadPlan(persistedActionId);
    if (!plan) return true;
    const linklyFinalAttempts = plan.attempts.filter(
      (record) =>
        record.attempt.provider === "linkly-cloud" &&
        (record.attempt.state === "Approved" ||
          record.attempt.state === "Declined" ||
          record.attempt.state === "Cancelled"),
    );
    // 没有 ACK 服务时不能把已落账的 Linkly 终态误判为可离开。
    if (!acknowledgements) return linklyFinalAttempts.length === 0;
    const results = await Promise.all(
      linklyFinalAttempts
        .map((record) => acknowledgements.acknowledge(record.attempt.attemptId)),
    );
    return results.every((result: PaymentAcknowledgementResult) => result.acknowledged);
  }

  /**
   * 现金恢复只读检查：Prepared 代表钱箱仍需人工核对，不能借由 recovery
   * 路径隐式批准。若 plan 尚未落盘则直接失败关闭，不在 inspection 中绑定新 plan。
   */
  public async inspectCashSettlement(
    persistedActionId: string,
  ): Promise<InstallmentCashSettlement["state"]> {
    const actionId = requiredText(persistedActionId, "persisted action id");
    const rawAction = await this.options.store.loadAction(actionId);
    if (!rawAction) {
      throw adapterError(
        "INSTALLMENT_ACTION_NOT_FOUND",
        "Persisted installment action was not found.",
      );
    }
    const action = validateAction(rawAction, actionId, { allowCreated: true });
    if (action.action.kind !== "repayment" || action.action.method !== "cash") {
      throw adapterError(
        "INSTALLMENT_ATTEMPT_PLAN_CONFLICT",
        "Cash settlement inspection requires a cash repayment action.",
      );
    }
    const rawPlan = await this.options.store.loadPlan(actionId);
    if (!rawPlan) {
      throw adapterError(
        "INSTALLMENT_ATTEMPT_DURABILITY_REQUIRED",
        "Cash repayment plan is not durably prepared.",
      );
    }
    const plan = validatePlan(rawPlan, action);
    const settlement = plan.cashSettlements[0];
    if (!settlement || plan.cashSettlements.length !== 1) {
      throw adapterError(
        "INSTALLMENT_ATTEMPT_PLAN_CONFLICT",
        "Cash repayment plan is incomplete.",
      );
    }
    return settlement.state;
  }

  /** 只有 runtime 已明确取得现金确认后才能调用此入口。 */
  public confirmCashRepayment(
    persistedActionId: string,
  ): Promise<PaymentAdapterResult> {
    return this.run(persistedActionId);
  }

  public async prepareRepaymentClaim(
    persistedActionId: string,
  ): Promise<Readonly<{ provider: string; providerAttemptId: string }>> {
    const actionId = requiredText(persistedActionId, "persisted action id");
    const rawAction = await this.options.store.loadAction(actionId);
    if (!rawAction) {
      throw adapterError(
        "INSTALLMENT_ACTION_NOT_FOUND",
        "Persisted installment action was not found.",
      );
    }
    // Runtime 在 central claim 已创建后、begin-provider 之前绑定不可变 provider plan。
    // 此时 action 仍是 Created；只允许这个无 provider 副作用的入口读取该状态。
    const action = validateAction(rawAction, actionId, { allowCreated: true });
    if (action.action.kind !== "repayment") {
      throw adapterError(
        "INSTALLMENT_ATTEMPT_PLAN_CONFLICT",
        "Repayment claim binding requires a repayment action.",
      );
    }
    const existingPlan = await this.options.store.loadPlan(actionId);
    if (!existingPlan && action.state !== "Created") {
      // 恢复阶段必须复用首次准备时落盘的 provider identity，绝不能重建 attempt。
      throw adapterError(
        "INSTALLMENT_ATTEMPT_DURABILITY_REQUIRED",
        "Repayment provider plan is not durably prepared.",
      );
    }
    const plan = existingPlan
      ? validatePlan(existingPlan, action)
      : await this.loadOrBindPlan(action);
    if (plan.cashSettlements.length === 1 && plan.attempts.length === 0) {
      const settlement = plan.cashSettlements[0];
      if (!settlement) {
        throw adapterError(
          "INSTALLMENT_ATTEMPT_PLAN_CONFLICT",
          "Cash repayment claim binding is incomplete.",
        );
      }
      return Object.freeze({
        provider: "cash",
        providerAttemptId: settlement.settlementId,
      });
    }
    if (plan.attempts.length === 1 && plan.cashSettlements.length === 0) {
      const attempt = plan.attempts[0]?.attempt;
      if (!attempt) {
        throw adapterError(
          "INSTALLMENT_ATTEMPT_PLAN_CONFLICT",
          "Repayment provider claim binding is incomplete.",
        );
      }
      return Object.freeze({
        provider: attempt.provider,
        providerAttemptId: attempt.attemptId,
      });
    }
    throw adapterError(
      "INSTALLMENT_ATTEMPT_PLAN_CONFLICT",
      "Repayment claim binding must contain exactly one provider attempt.",
    );
  }

  public async listProviderAvailability(): Promise<
    readonly PaymentProviderAvailability[]
  > {
    let enabledCards: readonly InstallmentCardProvider[] = [];
    try {
      enabledCards =
        await this.options.cardProviderSelection.loadEnabledProviders();
    } catch {
      // 配置读取失败时三种在线方式全部失败关闭；UI 不得猜测默认卡通道。
    }
    return Object.freeze(
      (["square", "linkly-cloud", "voucher"] as const).map((provider) => {
        const configured =
          provider === "voucher" || enabledCards.includes(provider);
        const available = configured && this.providerExists(provider);
        return Object.freeze({
          provider,
          available,
          blocker: available ? null : "PAYMENT_PROVIDER_UNKNOWN",
        });
      }),
    );
  }

  private run(rawActionId: string): Promise<PaymentAdapterResult> {
    const actionId = requiredText(rawActionId, "persisted action id");
    const existing = this.inflight.get(actionId);
    if (existing) return existing;

    const promise = this.execute(actionId);
    this.inflight.set(actionId, promise);
    promise.then(
      () => this.deleteInflight(actionId, promise),
      () => this.deleteInflight(actionId, promise),
    );
    return promise;
  }

  private deleteInflight(
    actionId: string,
    expected: Promise<PaymentAdapterResult>,
  ): void {
    if (this.inflight.get(actionId) === expected) {
      this.inflight.delete(actionId);
    }
  }

  private async execute(actionId: string): Promise<PaymentAdapterResult> {
    const rawAction = await this.options.store.loadAction(actionId);
    if (!rawAction) {
      throw adapterError(
        "INSTALLMENT_ACTION_NOT_FOUND",
        "Persisted installment action was not found.",
      );
    }
    const action = validateAction(rawAction, actionId);
    const plan = await this.loadOrBindPlan(action);
    return action.action.kind === "cancel-refund"
      ? this.executeRefundPlan(action, plan)
      : this.executePurchasePlan(action, plan);
  }

  private async loadOrBindPlan(
    action: PersistedInstallmentAction,
  ): Promise<InstallmentProviderAttemptPlan> {
    const existing = await this.options.store.loadPlan(action.action.actionId);
    if (existing) return validatePlan(existing, action);

    const candidate =
      action.action.kind === "cancel-refund"
        ? await this.createRefundPlan(action)
        : await this.createPurchasePlan(action);
    let persisted: InstallmentProviderAttemptPlan;
    try {
      persisted = await this.options.store.bindPlanOrGet(candidate);
    } catch {
      const recovered = await this.options.store.loadPlan(action.action.actionId);
      if (!recovered) {
        throw adapterError(
          "INSTALLMENT_ATTEMPT_DURABILITY_REQUIRED",
          "Installment provider plan could not be durably recovered.",
        );
      }
      persisted = recovered;
    }
    return validatePlan(persisted, action);
  }

  private async createPurchasePlan(
    action: PersistedInstallmentAction,
  ): Promise<InstallmentProviderAttemptPlan> {
    const paymentGuid = requiredText(
      action.action.paymentGuid,
      "payment guid",
    );
    const amountCents = positiveCents(action.action.amountCents);
    const method = action.action.method;
    if (method === "cash") {
      return Object.freeze({
        actionId: action.action.actionId,
        attempts: Object.freeze([]),
        cashSettlements: Object.freeze([
          Object.freeze({
            actionId: action.action.actionId,
            settlementId: generatedId(this.options.createId(), "cash settlement id"),
            paymentGuid,
            sourcePaymentGuid: null,
            originalTenderEvidenceId: generatedId(
              this.options.createId(),
              "original tender evidence id",
            ),
            sourceAttemptId: null,
            sequence: 0,
            operation: "purchase" as const,
            amountCents,
            idempotencyKey: action.action.idempotencyKey,
            state: "Prepared" as const,
          }),
        ]),
      });
    }

    const provider =
      method === "voucher"
        ? "voucher"
        : await this.resolveNewCardProvider(
            action.command.kind === "cancel-refund"
              ? undefined
              : action.command.cardProvider,
          );
    const record = this.createProviderRecord({
      action,
      paymentGuid,
      provider,
      sequence: 0,
      operation: "purchase",
      amountCents,
      originalTenderEvidenceId: generatedId(
        this.options.createId(),
        "original tender evidence id",
      ),
      sourcePaymentGuid: null,
      sourceAttemptId: null,
    });
    return Object.freeze({
      actionId: action.action.actionId,
      attempts: Object.freeze([record]),
      cashSettlements: Object.freeze([]),
    });
  }

  private async createRefundPlan(
    action: PersistedInstallmentAction,
  ): Promise<InstallmentProviderAttemptPlan> {
    const refundMode = actionRefundMode(action);
    const snapshot = validateProvenanceSnapshot(
      await this.options.provenance.resolveOrImport({
        installmentGuid: action.action.installmentGuid,
        storeCode: action.storeCode,
        requestingDeviceCode: action.deviceCode,
        refundMode,
      }),
      action,
    );
    const attempts: InstallmentProviderAttemptRecord[] = [];
    const cashSettlements: InstallmentCashSettlement[] = [];

    for (const [sequence, evidence] of snapshot.tenders.entries()) {
      const paymentGuid = generatedId(
        this.options.createId(),
        "refund payment guid",
      );
      const refundIdempotencyKey = refundStepIdempotencyKey(
        action.action.actionId,
        evidence.sourcePaymentGuid,
      );
      if (refundMode === "voucher") {
        // 中文注释：退代金券模式下现金、刷卡、代金券原付款一律各签发一张同额退款券；
        // 不建现金结算、不调用卡 provider 也不注入原卡引用，幂等键沿用
        // `${operationGuid}:refund:${原paymentGuid}`，服务端据此核验券备注 RefundKey。
        attempts.push(
          this.createProviderRecord({
            action,
            paymentGuid,
            provider: "voucher",
            sequence,
            operation: "refund",
            amountCents: evidence.amountCents,
            originalTenderEvidenceId: evidence.evidenceId,
            sourcePaymentGuid: evidence.sourcePaymentGuid,
            sourceAttemptId: evidence.sourceAttemptId,
            idempotencyKey: refundIdempotencyKey,
          }),
        );
        continue;
      }
      if (evidence.method === "cash") {
        cashSettlements.push(
          Object.freeze({
            actionId: action.action.actionId,
            settlementId: generatedId(
              this.options.createId(),
              "cash refund settlement id",
            ),
            paymentGuid,
            sourcePaymentGuid: evidence.sourcePaymentGuid,
            originalTenderEvidenceId: evidence.evidenceId,
            sourceAttemptId: evidence.sourceAttemptId,
            sequence,
            operation: "refund",
            amountCents: evidence.amountCents,
            idempotencyKey: refundIdempotencyKey,
            state: "Prepared",
          }),
        );
        continue;
      }

      const candidate = this.createProviderRecord({
        action,
        paymentGuid,
        provider: requiredEvidenceProvider(evidence),
        sequence,
        operation: "refund",
        amountCents: evidence.amountCents,
        originalTenderEvidenceId: evidence.evidenceId,
        sourcePaymentGuid: evidence.sourcePaymentGuid,
        sourceAttemptId: evidence.sourceAttemptId,
        idempotencyKey: refundIdempotencyKey,
      });
      const seededAttempt = await this.options.provenance.seedRefundAttempt({
        evidence,
        attempt: candidate.attempt,
      });
      attempts.push(
        Object.freeze({
          ...candidate,
          attempt: validateSeededRefundAttempt(
            candidate.attempt,
            seededAttempt,
            evidence,
          ),
        }),
      );
    }

    return Object.freeze({
      actionId: action.action.actionId,
      attempts: Object.freeze(attempts),
      cashSettlements: Object.freeze(cashSettlements),
    });
  }

  private createProviderRecord(input: Readonly<{
    action: PersistedInstallmentAction;
    paymentGuid: string;
    provider: PaymentProvider;
    sequence: number;
    operation: "purchase" | "refund";
    amountCents: number;
    originalTenderEvidenceId: string;
    sourcePaymentGuid: string | null;
    sourceAttemptId: string | null;
    idempotencyKey?: string;
  }>): InstallmentProviderAttemptRecord {
    const attemptId = generatedId(this.options.createId(), "provider attempt id");
    const nowIso = canonicalIso(this.options.nowIso(), "provider attempt time");
    const providerEnvironment =
      input.provider === "linkly-cloud"
        ? requiredProviderEnvironment(this.requireProvider(input.provider))
        : null;
    return Object.freeze({
      actionId: input.action.action.actionId,
      paymentGuid: input.paymentGuid,
      sourcePaymentGuid: input.sourcePaymentGuid,
      originalTenderEvidenceId: input.originalTenderEvidenceId,
      sourceAttemptId: input.sourceAttemptId,
      sequence: input.sequence,
      attempt: Object.freeze({
        attemptId,
        idempotencyKey:
          input.idempotencyKey ??
          generatedId(this.options.createId(), "provider idempotency key"),
        orderGuid: input.action.action.installmentGuid,
        provider: input.provider,
        operation: input.operation,
        amount: createAud(
          input.operation === "refund"
            ? -positiveCents(input.amountCents)
            : positiveCents(input.amountCents),
        ),
        state: "Created",
        references: emptyReferences(),
        createdAtIso: nowIso,
        updatedAtIso: nowIso,
        lastErrorCode: null,
        ...(providerEnvironment ? { providerEnvironment } : {}),
        receiptText: null,
        responseCode: null,
      }),
    });
  }

  private async resolveNewCardProvider(
    selected: InstallmentCardProvider | undefined,
  ): Promise<InstallmentCardProvider> {
    if (selected !== undefined) {
      if (
        (selected !== "square" && selected !== "linkly-cloud") ||
        !this.providerExists(selected)
      ) {
        throw adapterError(
          "INSTALLMENT_CARD_PROVIDER_SELECTION_INVALID",
          "The frozen installment card provider is unavailable.",
        );
      }
      // 新版 action 已在耐久 envelope 冻结 provider；恢复时不得被后续配置变更改写。
      return selected;
    }

    let configured: readonly InstallmentCardProvider[];
    try {
      configured =
        await this.options.cardProviderSelection.loadEnabledProviders();
    } catch {
      throw adapterError(
        "INSTALLMENT_CARD_PROVIDER_SELECTION_INVALID",
        "Installment card provider configuration could not be loaded.",
      );
    }
    const unique = [
      ...new Set(
        configured.filter(
          (value): value is InstallmentCardProvider =>
            value === "square" || value === "linkly-cloud",
        ),
      ),
    ];
    if (
      unique.length !== 1 ||
      configured.length !== 1 ||
      unique[0] === undefined
    ) {
      throw adapterError(
        "INSTALLMENT_CARD_PROVIDER_SELECTION_INVALID",
        "Exactly one installment card provider must be explicitly enabled.",
      );
    }
    this.requireProvider(unique[0]);
    return unique[0];
  }

  private providerExists(provider: PaymentProvider): boolean {
    try {
      this.requireProvider(provider);
      return true;
    } catch {
      return false;
    }
  }

  private async executePurchasePlan(
    action: PersistedInstallmentAction,
    plan: InstallmentProviderAttemptPlan,
  ): Promise<PaymentAdapterResult> {
    if (plan.cashSettlements.length === 1) {
      const settlements = await this.approveCash(action, plan);
      const cash = settlements[0];
      if (!cash || cash.state !== "Approved") {
        throw adapterError(
          "INSTALLMENT_ATTEMPT_DURABILITY_REQUIRED",
          "Cash installment approval was not durably recorded.",
        );
      }
      return Object.freeze({
        kind: "approved" as const,
        payment: paymentFromCash(action, cash),
      });
    }

    const record = plan.attempts[0];
    if (!record) {
      throw adapterError(
        "INSTALLMENT_ATTEMPT_PLAN_CONFLICT",
        "Installment purchase plan has no payment attempt.",
      );
    }
    const execution = await this.executeProviderAttempt(action, record);
    if (execution.kind === "declined") return Object.freeze({ kind: "declined" });
    if (execution.kind === "unknown") return Object.freeze({ kind: "unknown" });
    return Object.freeze({
      kind: "approved" as const,
      payment: await this.paymentFromApprovedRecord(action, execution.record),
    });
  }

  private async executeRefundPlan(
    action: PersistedInstallmentAction,
    plan: InstallmentProviderAttemptPlan,
  ): Promise<PaymentAdapterResult> {
    let providerRefundApproved = plan.attempts.some(
      (record) => record.attempt.state === "Approved",
    );
    const approvedRecords: InstallmentProviderAttemptRecord[] = [];
    const orderedAttempts = [...plan.attempts].sort(
      (left, right) => left.sequence - right.sequence,
    );

    for (const record of orderedAttempts) {
      const execution = await this.executeProviderAttempt(action, record);
      if (execution.kind === "approved") {
        providerRefundApproved = true;
        approvedRecords.push(execution.record);
        await this.acknowledgeApprovedLinklyRefund(execution.record);
        continue;
      }
      if (execution.kind === "declined") {
        return Object.freeze({
          kind: providerRefundApproved ? "unknown" : "declined",
        });
      }
      return Object.freeze({ kind: "unknown" });
    }

    const cashSettlements = await this.approveCash(action, plan);
    if (cashSettlements.some((settlement) => settlement.state !== "Approved")) {
      throw adapterError(
        "INSTALLMENT_ATTEMPT_DURABILITY_REQUIRED",
        "Cash installment refunds were not durably approved.",
      );
    }
    const refunds = await Promise.all([
      ...approvedRecords.map((record) =>
        this.refundFromApprovedRecord(action, record),
      ),
      ...cashSettlements.map((settlement) =>
        Promise.resolve(refundFromCash(action, settlement)),
      ),
    ]);
    refunds.sort(
      (left, right) =>
        sequenceForRefund(plan, left) - sequenceForRefund(plan, right),
    );
    if (refunds.length === 0) {
      throw adapterError(
        "INSTALLMENT_REFUND_PROVENANCE_INVALID",
        "Installment cancellation has no recorded tender to refund.",
      );
    }
    return Object.freeze({
      kind: "approved" as const,
      refunds: Object.freeze(refunds),
    });
  }

  /**
   * Linkly 服务端会以尚未 ACK 的退款占用终端。取消退款 action 仍被本地阻塞，
   * 但该笔退款已有不可变原付款绑定、耐久 Approved 与加密证据时，必须逐笔确认
   * 才能安全执行下一笔；首付和还款仍只在整个 action 完成后 ACK。
   */
  private async acknowledgeApprovedLinklyRefund(
    record: InstallmentProviderAttemptRecord,
  ): Promise<void> {
    if (record.attempt.provider !== "linkly-cloud") return;
    const acknowledgements = this.options.acknowledgements;
    if (!acknowledgements) {
      throw adapterError(
        "INSTALLMENT_ATTEMPT_DURABILITY_REQUIRED",
        "Linkly refund acknowledgement service is unavailable.",
      );
    }
    const result = await acknowledgements.acknowledge(record.attempt.attemptId);
    if (!result.acknowledged) {
      throw adapterError(
        "INSTALLMENT_ATTEMPT_DURABILITY_REQUIRED",
        "Linkly refund acknowledgement requires recovery.",
      );
    }
  }

  private async approveCash(
    action: PersistedInstallmentAction,
    plan: InstallmentProviderAttemptPlan,
  ): Promise<readonly InstallmentCashSettlement[]> {
    if (plan.cashSettlements.length === 0) return Object.freeze([]);
    if (plan.cashSettlements.every((entry) => entry.state === "Approved")) {
      return plan.cashSettlements;
    }
    let approved: readonly InstallmentCashSettlement[];
    try {
      approved = await this.options.store.approveCashSettlements(
        action.action.actionId,
      );
    } catch {
      throw adapterError(
        "INSTALLMENT_ATTEMPT_DURABILITY_REQUIRED",
        "Cash settlement approval requires recovery.",
      );
    }
    return validateCashSettlements(approved, plan.cashSettlements);
  }

  private async executeProviderAttempt(
    action: PersistedInstallmentAction,
    inputRecord: InstallmentProviderAttemptRecord,
  ): Promise<AttemptExecution> {
    let record = validateAttemptRecord(inputRecord, action);
    if (record.attempt.state === "Approved") {
      return Object.freeze({ kind: "approved", record });
    }
    if (
      record.attempt.state === "Declined" ||
      record.attempt.state === "Cancelled"
    ) {
      return Object.freeze({ kind: "declined", record });
    }

    const provider = this.requireProvider(record.attempt.provider);
    if (record.attempt.provider === "voucher" && record.attempt.state === "Created") {
      try {
        await this.options.voucherMaterials.prepare({ action, record });
      } catch {
        throw adapterError(
          "INSTALLMENT_VOUCHER_MATERIAL_INVALID",
          "Installment voucher intent is not durably prepared.",
        );
      }
    }

    let execute: (attempt: PaymentAttempt) => Promise<PaymentProviderResult>;
    if (record.attempt.state === "Created") {
      record = await this.transitionAttempt(record, "Submitted");
      execute =
        record.attempt.operation === "refund"
          ? (attempt) => provider.refund(attempt)
          : (attempt) => provider.submit(attempt);
    } else if (
      record.attempt.state === "Submitted" ||
      record.attempt.state === "Pending" ||
      record.attempt.state === "Unknown"
    ) {
      execute = (attempt) => provider.recover(attempt);
    } else {
      throw adapterError(
        "INSTALLMENT_ATTEMPT_PLAN_CONFLICT",
        "Installment provider attempt state cannot be recovered.",
      );
    }

    let result: PaymentProviderResult;
    try {
      result = await execute(record.attempt);
    } catch {
      record = await this.persistUnknown(
        record,
        "INSTALLMENT_PROVIDER_TRANSPORT_AMBIGUOUS",
      );
      return Object.freeze({ kind: "unknown", record });
    }

    try {
      record = await this.persistProviderResult(action, record, result);
    } catch (error) {
      if (
        error instanceof InstallmentPaymentAdapterError &&
        error.code === "INSTALLMENT_ATTEMPT_DURABILITY_REQUIRED"
      ) {
        throw error;
      }
      record = await this.persistUnknown(
        record,
        "INSTALLMENT_PROVIDER_RESULT_INVALID",
        result,
      );
    }

    if (record.attempt.state === "Approved") {
      return Object.freeze({ kind: "approved", record });
    }
    if (
      record.attempt.state === "Declined" ||
      record.attempt.state === "Cancelled"
    ) {
      return Object.freeze({ kind: "declined", record });
    }
    return Object.freeze({ kind: "unknown", record });
  }

  private async transitionAttempt(
    record: InstallmentProviderAttemptRecord,
    state: PaymentAttempt["state"],
  ): Promise<InstallmentProviderAttemptRecord> {
    if (!canTransitionPaymentAttempt(record.attempt.state, state)) {
      throw adapterError(
        "INSTALLMENT_ATTEMPT_PLAN_CONFLICT",
        "Installment provider attempt transition is invalid.",
      );
    }
    const nextAttempt: PaymentAttempt = Object.freeze({
      ...record.attempt,
      state,
      updatedAtIso: nextIso(record.attempt.updatedAtIso, this.options.nowIso()),
      lastErrorCode: null,
    });
    return this.compareAndUpdate(record, nextAttempt);
  }

  private async persistProviderResult(
    action: PersistedInstallmentAction,
    record: InstallmentProviderAttemptRecord,
    result: PaymentProviderResult,
  ): Promise<InstallmentProviderAttemptRecord> {
    const references = mergeReferences(
      record.attempt.references,
      result.references,
    );
    if (
      record.attempt.state !== result.state &&
      !canTransitionPaymentAttempt(record.attempt.state, result.state)
    ) {
      throw adapterError(
        "INSTALLMENT_ATTEMPT_PLAN_CONFLICT",
        "Provider returned an invalid installment attempt transition.",
      );
    }

    const nextAttempt: PaymentAttempt = Object.freeze({
      ...record.attempt,
      state: result.state,
      references,
      updatedAtIso: nextIso(record.attempt.updatedAtIso, this.options.nowIso()),
      lastErrorCode:
        result.state === "Declined" || result.state === "Unknown"
          ? normalizedCode(result.responseCode)
          : null,
      receiptText: safeReceipt(result.receiptText),
      responseCode: normalizedCode(result.responseCode),
    });
    const approvedMaterial =
      result.state === "Approved"
        ? await this.approvedMaterial(action, record, nextAttempt, result)
        : undefined;
    if (
      result.state !== "Approved" &&
      result.protectedSyncEvidence !== undefined &&
      result.protectedSyncEvidence !== null
    ) {
      throw adapterError(
        "INSTALLMENT_APPROVED_MATERIAL_INVALID",
        "Only approved card attempts may carry protected evidence.",
      );
    }
    return this.compareAndUpdate(record, nextAttempt, approvedMaterial);
  }

  private async approvedMaterial(
    action: PersistedInstallmentAction,
    record: InstallmentProviderAttemptRecord,
    nextAttempt: PaymentAttempt,
    result: PaymentProviderResult,
  ): Promise<InstallmentApprovedPaymentMaterial> {
    if (
      nextAttempt.provider === "square" ||
      nextAttempt.provider === "linkly-cloud"
    ) {
      if (!result.protectedSyncEvidence) {
        throw adapterError(
          "INSTALLMENT_APPROVED_MATERIAL_INVALID",
          "Approved card attempt is missing protected evidence.",
        );
      }
      const evidence = normalizeCardSyncEvidence(
        result.protectedSyncEvidence,
      );
      if (
        evidence.provider !== nextAttempt.provider ||
        evidence.operation !== nextAttempt.operation ||
        evidence.amountCents !== Math.abs(nextAttempt.amount.cents)
      ) {
        throw adapterError(
          "INSTALLMENT_APPROVED_MATERIAL_INVALID",
          "Approved card evidence does not match the installment attempt.",
        );
      }
      return Object.freeze({
        kind: "card" as const,
        evidence,
        receiptText: safeReceipt(result.receiptText),
      });
    }

    if (
      result.protectedSyncEvidence !== undefined &&
      result.protectedSyncEvidence !== null
    ) {
      throw adapterError(
        "INSTALLMENT_APPROVED_MATERIAL_INVALID",
        "Voucher approval cannot carry card evidence.",
      );
    }
    const protectedReference = requiredText(
      nextAttempt.references.voucherReservationToken,
      "voucher protected reference",
    );
    let material: Readonly<{
      reference: string;
      reservationToken: string | null;
    }>;
    try {
      material = await this.options.voucherMaterials.resolveApproved({
        action,
        record: Object.freeze({ ...record, attempt: nextAttempt }),
        protectedReference,
      });
    } catch {
      throw adapterError(
        "INSTALLMENT_VOUCHER_MATERIAL_INVALID",
        "Approved voucher material could not be resolved.",
      );
    }
    const reference = protectedText(material.reference, "voucher reference");
    const reservationToken =
      material.reservationToken === null
        ? null
        : protectedText(material.reservationToken, "voucher reservation token");
    if (
      (nextAttempt.operation === "purchase" && reservationToken === null) ||
      (nextAttempt.operation === "refund" && reservationToken !== null)
    ) {
      throw adapterError(
        "INSTALLMENT_VOUCHER_MATERIAL_INVALID",
        "Approved voucher material does not match the operation.",
      );
    }
    return Object.freeze({
      kind: "voucher" as const,
      reference,
      reservationToken,
    });
  }

  private async persistUnknown(
    record: InstallmentProviderAttemptRecord,
    code: string,
    result?: PaymentProviderResult,
  ): Promise<InstallmentProviderAttemptRecord> {
    if (
      record.attempt.state !== "Unknown" &&
      !canTransitionPaymentAttempt(record.attempt.state, "Unknown")
    ) {
      throw adapterError(
        "INSTALLMENT_ATTEMPT_PLAN_CONFLICT",
        "Installment attempt cannot enter Unknown.",
      );
    }
    let references = record.attempt.references;
    if (result) {
      try {
        references = mergeReferences(references, result.references);
      } catch {
        // 冲突引用绝不覆盖已持久化真相。
      }
    }
    const nextAttempt: PaymentAttempt = Object.freeze({
      ...record.attempt,
      state: "Unknown",
      references,
      updatedAtIso: nextIso(record.attempt.updatedAtIso, this.options.nowIso()),
      lastErrorCode: code,
      receiptText: safeReceiptOrNull(
        result?.receiptText ?? record.attempt.receiptText ?? null,
      ),
      responseCode: normalizedCode(
        result?.responseCode ?? record.attempt.responseCode ?? null,
      ),
    });
    return this.compareAndUpdate(record, nextAttempt);
  }

  private async compareAndUpdate(
    record: InstallmentProviderAttemptRecord,
    nextAttempt: PaymentAttempt,
    approvedMaterial?: InstallmentApprovedPaymentMaterial,
  ): Promise<InstallmentProviderAttemptRecord> {
    let updated: boolean;
    try {
      updated = await this.options.store.compareAndUpdateAttempt({
        expected: record,
        nextAttempt,
        ...(approvedMaterial ? { approvedMaterial } : {}),
      });
    } catch {
      throw adapterError(
        "INSTALLMENT_ATTEMPT_DURABILITY_REQUIRED",
        "Installment provider result requires durable recovery.",
      );
    }
    if (!updated) {
      throw adapterError(
        "INSTALLMENT_ATTEMPT_DURABILITY_REQUIRED",
        "Installment provider attempt CAS failed; recovery is required.",
      );
    }
    return Object.freeze({ ...record, attempt: nextAttempt });
  }

  private requireProvider(providerName: PaymentProvider): OnlinePaymentPort {
    let provider: OnlinePaymentPort;
    try {
      provider = this.options.providers.get(providerName);
    } catch {
      throw adapterError(
        "INSTALLMENT_PROVIDER_UNAVAILABLE",
        "Persisted installment provider is unavailable.",
      );
    }
    if (provider.provider !== providerName) {
      throw adapterError(
        "INSTALLMENT_PROVIDER_UNAVAILABLE",
        "Installment provider registry returned a mismatched adapter.",
      );
    }
    return provider;
  }

  private async paymentFromApprovedRecord(
    action: PersistedInstallmentAction,
    record: InstallmentProviderAttemptRecord,
  ): Promise<InstallmentPaymentCommand> {
    const material = await this.requireApprovedMaterial(record);
    return paymentCommand(action, record, material);
  }

  private async refundFromApprovedRecord(
    action: PersistedInstallmentAction,
    record: InstallmentProviderAttemptRecord,
  ): Promise<InstallmentApprovedRefund> {
    const material = await this.requireApprovedMaterial(record);
    return approvedRefund(action, record, refundCommand(action, record, material));
  }

  private async requireApprovedMaterial(
    record: InstallmentProviderAttemptRecord,
  ): Promise<InstallmentApprovedPaymentMaterial> {
    let material: InstallmentApprovedPaymentMaterial | null;
    try {
      material = await this.options.store.loadApprovedMaterial(
        record.attempt.attemptId,
      );
    } catch {
      material = null;
    }
    if (!material) {
      throw adapterError(
        "INSTALLMENT_APPROVED_MATERIAL_INVALID",
        "Approved installment attempt has no protected material.",
      );
    }
    return validateStoredMaterial(material, record.attempt);
  }
}

function validateAction(
  action: PersistedInstallmentAction,
  expectedActionId: string,
  options: Readonly<{ allowCreated?: boolean }> = {},
): PersistedInstallmentAction {
  const payment = action.action;
  const allowedStates = options.allowCreated
    ? ["Created", "ProviderPending", "Unknown", "Approved", "BackendPending"]
    : ["ProviderPending", "Unknown", "Approved", "BackendPending"];
  if (
    payment.actionId !== expectedActionId ||
    payment.idempotencyKey !== payment.actionId ||
    payment.installmentGuid !== action.command.installmentGuid ||
    payment.kind !== action.command.kind ||
    action.deviceCode !== action.command.deviceCode ||
    !allowedStates.includes(action.state)
  ) {
    throw adapterError(
      "INSTALLMENT_ACTION_INVALID",
      "Persisted installment action identity is invalid.",
    );
  }
  requiredText(action.storeCode, "action store code");
  requiredText(action.deviceCode, "action device code");
  requiredText(action.command.cashierId, "action cashier id");
  requiredText(action.command.cashierName, "action cashier name");
  requiredText(payment.installmentGuid, "action installment guid");
  if (payment.kind === "cancel-refund") {
    if (
      payment.paymentGuid !== null ||
      payment.method !== null ||
      payment.amountCents !== null
    ) {
      throw adapterError(
        "INSTALLMENT_ACTION_INVALID",
        "Cancel/refund action contains a new tender.",
      );
    }
  } else if (
    payment.paymentGuid === null ||
    payment.method === null ||
    payment.amountCents === null
  ) {
    throw adapterError(
      "INSTALLMENT_ACTION_INVALID",
      "Installment payment action is incomplete.",
    );
  } else {
    requiredText(payment.paymentGuid, "action payment guid");
    positiveCents(payment.amountCents);
  }
  return action;
}

function validatePlan(
  plan: InstallmentProviderAttemptPlan,
  action: PersistedInstallmentAction,
): InstallmentProviderAttemptPlan {
  if (plan.actionId !== action.action.actionId) {
    throw planConflict();
  }
  const sequences = new Set<number>();
  for (const record of plan.attempts) {
    validateAttemptRecord(record, action);
    if (sequences.has(record.sequence)) throw planConflict();
    sequences.add(record.sequence);
  }
  for (const cash of plan.cashSettlements) {
    validateCashSettlement(cash, action);
    if (sequences.has(cash.sequence)) throw planConflict();
    sequences.add(cash.sequence);
  }

  if (action.action.kind !== "cancel-refund") {
    if (plan.attempts.length + plan.cashSettlements.length !== 1) {
      throw planConflict();
    }
    const record = plan.attempts[0];
    const cash = plan.cashSettlements[0];
    if (action.action.method === "cash") {
      if (!cash || record) throw planConflict();
    } else {
      if (!record || cash) throw planConflict();
      if (
        (action.action.method === "voucher") !==
        (record.attempt.provider === "voucher")
      ) {
        throw planConflict();
      }
    }
  } else if (plan.attempts.length + plan.cashSettlements.length === 0) {
    throw planConflict();
  } else if (
    actionRefundMode(action) === "voucher" &&
    (plan.cashSettlements.length > 0 ||
      plan.attempts.some((record) => record.attempt.provider !== "voucher"))
  ) {
    // 中文注释：退代金券模式的耐久计划只能由退款券 attempt 组成。
    throw planConflict();
  }
  return plan;
}

/** 旧 action 没有 refundMode，一律按原路退。 */
function actionRefundMode(
  action: PersistedInstallmentAction,
): InstallmentCancelRefundMode {
  return action.command.kind === "cancel-refund"
    ? action.command.refundMode ?? "original-route"
    : "original-route";
}

function validateAttemptRecord(
  record: InstallmentProviderAttemptRecord,
  action: PersistedInstallmentAction,
): InstallmentProviderAttemptRecord {
  const attempt = record.attempt;
  if (
    record.actionId !== action.action.actionId ||
    attempt.orderGuid !== action.action.installmentGuid ||
    !Number.isSafeInteger(record.sequence) ||
    record.sequence < 0 ||
    !attempt.attemptId.trim() ||
    !attempt.idempotencyKey.trim() ||
    attempt.amount.currency !== "AUD" ||
    !Number.isSafeInteger(attempt.amount.cents) ||
    attempt.amount.cents === 0
  ) {
    throw planConflict();
  }
  requiredText(record.paymentGuid, "planned payment guid");
  requiredText(
    record.originalTenderEvidenceId,
    "original tender evidence id",
  );
  if (action.action.kind === "cancel-refund") {
    if (
      attempt.operation !== "refund" ||
      attempt.amount.cents >= 0 ||
      !record.sourcePaymentGuid ||
      !record.sourceAttemptId ||
      attempt.idempotencyKey !==
        refundStepIdempotencyKey(
          action.action.actionId,
          record.sourcePaymentGuid,
        )
    ) {
      throw planConflict();
    }
  } else if (
    attempt.operation !== "purchase" ||
    attempt.amount.cents <= 0 ||
    record.paymentGuid !== action.action.paymentGuid ||
    record.sourcePaymentGuid !== null ||
    record.sourceAttemptId !== null ||
    attempt.amount.cents !== action.action.amountCents
  ) {
    throw planConflict();
  }
  if (
    attempt.provider === "voucher" &&
    action.action.kind !== "cancel-refund" &&
    action.action.method !== "voucher"
  ) {
    throw planConflict();
  }
  return record;
}

function validateCashSettlement(
  settlement: InstallmentCashSettlement,
  action: PersistedInstallmentAction,
): InstallmentCashSettlement {
  if (
    settlement.actionId !== action.action.actionId ||
    !settlement.settlementId.trim() ||
    !settlement.paymentGuid.trim() ||
    !settlement.originalTenderEvidenceId.trim() ||
    !Number.isSafeInteger(settlement.sequence) ||
    settlement.sequence < 0 ||
    positiveCents(settlement.amountCents) !== settlement.amountCents ||
    (settlement.state !== "Prepared" && settlement.state !== "Approved")
  ) {
    throw planConflict();
  }
  if (action.action.kind === "cancel-refund") {
    if (
      settlement.operation !== "refund" ||
      !settlement.sourcePaymentGuid ||
      !settlement.sourceAttemptId ||
      settlement.idempotencyKey !==
        refundStepIdempotencyKey(
          action.action.actionId,
          settlement.sourcePaymentGuid,
        )
    ) {
      throw planConflict();
    }
  } else if (
    action.action.method !== "cash" ||
    settlement.operation !== "purchase" ||
    settlement.paymentGuid !== action.action.paymentGuid ||
    settlement.amountCents !== action.action.amountCents ||
    settlement.idempotencyKey !== action.action.idempotencyKey ||
    settlement.sourcePaymentGuid !== null ||
    settlement.sourceAttemptId !== null
  ) {
    throw planConflict();
  }
  return settlement;
}

function validateCashSettlements(
  actual: readonly InstallmentCashSettlement[],
  expected: readonly InstallmentCashSettlement[],
): readonly InstallmentCashSettlement[] {
  if (actual.length !== expected.length) throw planConflict();
  const byId = new Map(actual.map((entry) => [entry.settlementId, entry]));
  return Object.freeze(
    expected.map((prior) => {
      const current = byId.get(prior.settlementId);
      if (
        !current ||
        current.actionId !== prior.actionId ||
        current.paymentGuid !== prior.paymentGuid ||
        current.sourcePaymentGuid !== prior.sourcePaymentGuid ||
        current.originalTenderEvidenceId !== prior.originalTenderEvidenceId ||
        current.sourceAttemptId !== prior.sourceAttemptId ||
        current.sequence !== prior.sequence ||
        current.operation !== prior.operation ||
        current.amountCents !== prior.amountCents ||
        current.idempotencyKey !== prior.idempotencyKey ||
        current.state !== "Approved"
      ) {
        throw planConflict();
      }
      return current;
    }),
  );
}

function validateProvenanceSnapshot(
  snapshot: InstallmentRefundProvenanceSnapshot,
  action: PersistedInstallmentAction,
): InstallmentRefundProvenanceSnapshot {
  if (
    snapshot.complete !== true ||
    snapshot.installmentGuid !== action.action.installmentGuid ||
    snapshot.storeCode !== action.storeCode ||
    snapshot.requestingDeviceCode !== action.deviceCode ||
    !Number.isSafeInteger(snapshot.paidAmountCents) ||
    snapshot.paidAmountCents <= 0 ||
    snapshot.tenders.length === 0
  ) {
    throw provenanceInvalid();
  }
  const paymentGuids = new Set<string>();
  const evidenceIds = new Set<string>();
  const sourceAttemptIds = new Set<string>();
  let sum = 0;
  for (const evidence of snapshot.tenders) {
    if (
      evidence.installmentGuid !== snapshot.installmentGuid ||
      !isUuid(evidence.sourcePaymentGuid) ||
      !evidence.evidenceId.trim() ||
      !evidence.sourceAttemptId.trim() ||
      evidence.provenance !== "local-approved-attempt" &&
        evidence.provenance !== "hbpos-protected-details"
    ) {
      throw provenanceInvalid();
    }
    if (
      paymentGuids.has(evidence.sourcePaymentGuid) ||
      evidenceIds.has(evidence.evidenceId) ||
      sourceAttemptIds.has(evidence.sourceAttemptId)
    ) {
      throw provenanceInvalid();
    }
    paymentGuids.add(evidence.sourcePaymentGuid);
    evidenceIds.add(evidence.evidenceId);
    sourceAttemptIds.add(evidence.sourceAttemptId);
    const amount = positiveCents(evidence.amountCents);
    sum += amount;
    if (!Number.isSafeInteger(sum)) throw provenanceInvalid();
    if (
      (evidence.method === "cash" && evidence.provider !== null) ||
      (evidence.method === "voucher" && evidence.provider !== "voucher") ||
      (evidence.method === "card" &&
        evidence.provider !== "square" &&
        evidence.provider !== "linkly-cloud")
    ) {
      throw provenanceInvalid();
    }
  }
  if (sum !== snapshot.paidAmountCents) throw provenanceInvalid();
  return snapshot;
}

function validateSeededRefundAttempt(
  original: PaymentAttempt,
  seeded: PaymentAttempt,
  evidence: InstallmentOriginalTenderEvidence,
): PaymentAttempt {
  if (
    seeded.attemptId !== original.attemptId ||
    seeded.idempotencyKey !== original.idempotencyKey ||
    seeded.orderGuid !== original.orderGuid ||
    seeded.provider !== original.provider ||
    seeded.operation !== original.operation ||
    seeded.amount.currency !== original.amount.currency ||
    seeded.amount.cents !== original.amount.cents ||
    seeded.state !== original.state ||
    seeded.createdAtIso !== original.createdAtIso ||
    seeded.updatedAtIso !== original.updatedAtIso
  ) {
    throw provenanceInvalid();
  }
  const references = seeded.references;
  if (evidence.provider === "square") {
    if (
      !references.paymentId ||
      references.checkoutId ||
      references.sessionId ||
      references.txnRef ||
      references.rfn ||
      references.voucherReservationToken
    ) {
      throw provenanceInvalid();
    }
  } else if (evidence.provider === "linkly-cloud") {
    if (
      !references.rfn ||
      references.checkoutId ||
      references.paymentId ||
      references.sessionId ||
      references.txnRef ||
      references.voucherReservationToken
    ) {
      throw provenanceInvalid();
    }
  } else if (
    evidence.provider === "voucher" &&
    REFERENCE_KEYS.some((key) => references[key] !== null)
  ) {
    throw provenanceInvalid();
  }
  return Object.freeze({
    ...seeded,
    references: Object.freeze({ ...references }),
  });
}

function requiredEvidenceProvider(
  evidence: InstallmentOriginalTenderEvidence,
): PaymentProvider {
  if (
    (evidence.method === "card" &&
      (evidence.provider === "square" ||
        evidence.provider === "linkly-cloud")) ||
    (evidence.method === "voucher" && evidence.provider === "voucher")
  ) {
    return evidence.provider;
  }
  throw provenanceInvalid();
}

function paymentFromCash(
  action: PersistedInstallmentAction,
  settlement: InstallmentCashSettlement,
): InstallmentPaymentCommand {
  return Object.freeze({
    paymentGuid: settlement.paymentGuid,
    method: "cash" as const,
    amountCents: settlement.amountCents,
    reference: null,
    reservationToken: null,
    cardTransactions: Object.freeze([]),
    idempotencyKey: action.action.idempotencyKey,
  });
}

function paymentCommand(
  action: PersistedInstallmentAction,
  record: InstallmentProviderAttemptRecord,
  material: InstallmentApprovedPaymentMaterial,
): InstallmentPaymentCommand {
  if (record.attempt.operation !== "purchase") throw planConflict();
  if (material.kind === "voucher") {
    return Object.freeze({
      paymentGuid: record.paymentGuid,
      method: "voucher" as const,
      amountCents: positiveCents(record.attempt.amount.cents),
      reference: material.reference,
      reservationToken: requiredText(
        material.reservationToken,
        "voucher reservation token",
      ),
      cardTransactions: Object.freeze([]),
      idempotencyKey: action.action.idempotencyKey,
    });
  }
  return Object.freeze({
    paymentGuid: record.paymentGuid,
    method: "card" as const,
    amountCents: positiveCents(record.attempt.amount.cents),
    reference: cardReference(material.evidence),
    reservationToken: null,
    cardTransactions: Object.freeze([
      cardTransaction(material.evidence, material.receiptText),
    ]),
    idempotencyKey: action.action.idempotencyKey,
  });
}

function refundCommand(
  _action: PersistedInstallmentAction,
  record: InstallmentProviderAttemptRecord,
  material: InstallmentApprovedPaymentMaterial,
): InstallmentRefundCommand {
  if (record.attempt.operation !== "refund") throw planConflict();
  if (material.kind === "voucher") {
    return Object.freeze({
      paymentGuid: record.paymentGuid,
      method: "voucher" as const,
      amountCents: positiveCents(-record.attempt.amount.cents),
      // 中文注释：服务端按 VOUCHER_REFUND:{券码} 前缀查找新签发的退款券并核验 RefundKey。
      reference: voucherRefundReference(material.reference),
      cardTransactions: Object.freeze([]),
      idempotencyKey: record.attempt.idempotencyKey,
    });
  }
  return Object.freeze({
    paymentGuid: record.paymentGuid,
    method: "card" as const,
    amountCents: positiveCents(-record.attempt.amount.cents),
    reference: cardReference(material.evidence),
    cardTransactions: Object.freeze([
      cardTransaction(material.evidence, material.receiptText),
    ]),
    idempotencyKey: record.attempt.idempotencyKey,
  });
}

function voucherRefundReference(voucherCode: string): string {
  return `VOUCHER_REFUND:${protectedText(voucherCode, "voucher refund code").trim()}`;
}

function refundFromCash(
  action: PersistedInstallmentAction,
  settlement: InstallmentCashSettlement,
): InstallmentApprovedRefund {
  if (
    settlement.operation !== "refund" ||
    !settlement.sourcePaymentGuid ||
    !settlement.sourceAttemptId
  ) {
    throw planConflict();
  }
  return Object.freeze({
    refund: Object.freeze({
      paymentGuid: settlement.paymentGuid,
      method: "cash" as const,
      amountCents: settlement.amountCents,
      reference: null,
      cardTransactions: Object.freeze([]),
      idempotencyKey: settlement.idempotencyKey,
    }),
    originalTenderEvidenceId: settlement.originalTenderEvidenceId,
    refundAttemptId: settlement.settlementId,
    sourceAttemptId: settlement.sourceAttemptId,
    sourcePaymentGuid: settlement.sourcePaymentGuid,
  });
}

function approvedRefund(
  action: PersistedInstallmentAction,
  record: InstallmentProviderAttemptRecord,
  refund: InstallmentRefundCommand,
): InstallmentApprovedRefund {
  if (!record.sourcePaymentGuid || !record.sourceAttemptId) {
    throw planConflict();
  }
  if (refund.idempotencyKey !== record.attempt.idempotencyKey) {
    throw planConflict();
  }
  return Object.freeze({
    refund,
    originalTenderEvidenceId: record.originalTenderEvidenceId,
    refundAttemptId: record.attempt.attemptId,
    sourceAttemptId: record.sourceAttemptId,
    sourcePaymentGuid: record.sourcePaymentGuid,
  });
}

function sequenceForRefund(
  plan: InstallmentProviderAttemptPlan,
  refund: InstallmentApprovedRefund,
): number {
  const provider = plan.attempts.find(
    (record) => record.attempt.attemptId === refund.refundAttemptId,
  );
  if (provider) return provider.sequence;
  const cash = plan.cashSettlements.find(
    (settlement) => settlement.settlementId === refund.refundAttemptId,
  );
  if (cash) return cash.sequence;
  throw planConflict();
}

function validateStoredMaterial(
  material: InstallmentApprovedPaymentMaterial,
  attempt: PaymentAttempt,
): InstallmentApprovedPaymentMaterial {
  if (attempt.state !== "Approved") {
    throw adapterError(
      "INSTALLMENT_APPROVED_MATERIAL_INVALID",
      "Protected material belongs to a non-approved attempt.",
    );
  }
  if (material.kind === "card") {
    if (attempt.provider !== "square" && attempt.provider !== "linkly-cloud") {
      throw materialInvalid();
    }
    const evidence = normalizeCardSyncEvidence(material.evidence);
    if (
      evidence.provider !== attempt.provider ||
      evidence.operation !== attempt.operation ||
      evidence.amountCents !== Math.abs(attempt.amount.cents)
    ) {
      throw materialInvalid();
    }
    return Object.freeze({
      kind: "card",
      evidence,
      receiptText: safeReceipt(material.receiptText),
    });
  }
  if (attempt.provider !== "voucher") throw materialInvalid();
  const reference = protectedText(material.reference, "voucher reference");
  const reservationToken =
    material.reservationToken === null
      ? null
      : protectedText(material.reservationToken, "voucher reservation token");
  if (
    (attempt.operation === "purchase" && reservationToken === null) ||
    (attempt.operation === "refund" && reservationToken !== null)
  ) {
    throw materialInvalid();
  }
  return Object.freeze({
    kind: "voucher",
    reference,
    reservationToken,
  });
}

function cardTransaction(
  evidence: CardSyncEvidenceV1,
  receiptText: string | null,
): Readonly<{
  processor: string | null;
  txnRef: string | null;
  authCode: string | null;
  cardType: string | null;
  cardBin: number | null;
  maskedCardNumber: string | null;
  merchantId: string | null;
  responseCode: string | null;
  responseText: string | null;
  stan: string | null;
  bankDateTime: string | null;
  amount: number;
  receiptText: string | null;
  refundReference: string | null;
}> {
  return Object.freeze({
    processor: evidence.processor,
    txnRef: evidence.txnRef,
    authCode: evidence.authCode,
    cardType: evidence.cardType,
    cardBin: evidence.cardBin,
    maskedCardNumber: evidence.maskedCardNumber,
    merchantId: evidence.merchantId,
    responseCode: evidence.responseCode,
    responseText: evidence.responseText,
    stan: evidence.stan,
    bankDateTime: evidence.bankDateTimeIso,
    amount: evidence.amountCents / 100,
    receiptText,
    refundReference: evidence.refundReference,
  });
}

function cardReference(evidence: CardSyncEvidenceV1): string | null {
  return evidence.operation === "refund"
    ? evidence.txnRef
    : evidence.refundReference ?? evidence.txnRef;
}

function mergeReferences(
  current: PaymentProviderReferences,
  incoming: PaymentProviderReferences,
): PaymentProviderReferences {
  const merged = { ...current };
  for (const key of REFERENCE_KEYS) {
    const previous = current[key];
    const next = incoming[key];
    if (previous !== null && next !== null && previous !== next) {
      throw adapterError(
        "INSTALLMENT_ATTEMPT_PLAN_CONFLICT",
        "Provider returned a conflicting protected reference.",
      );
    }
    if (next !== null) merged[key] = next;
  }
  return Object.freeze(merged);
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

function nextIso(previousIso: string, candidateIso: string): string {
  const previous = Date.parse(previousIso);
  const candidate = Date.parse(candidateIso);
  if (!Number.isFinite(previous) || !Number.isFinite(candidate)) {
    throw adapterError(
      "INSTALLMENT_ACTION_INVALID",
      "Installment attempt timestamp is invalid.",
    );
  }
  return new Date(Math.max(candidate, previous + 1)).toISOString();
}

function canonicalIso(value: string, label: string): string {
  const milliseconds = Date.parse(value);
  if (!Number.isFinite(milliseconds)) {
    throw adapterError(
      "INSTALLMENT_ACTION_INVALID",
      `${label} is invalid.`,
    );
  }
  return new Date(milliseconds).toISOString();
}

function positiveCents(value: number | null): number {
  if (!Number.isSafeInteger(value) || (value as number) <= 0) {
    throw adapterError(
      "INSTALLMENT_ACTION_INVALID",
      "Installment amount must use positive integer cents.",
    );
  }
  return value as number;
}

function normalizedCode(value: string | null | undefined): string | null {
  if (typeof value !== "string") return null;
  const normalized = value.trim();
  if (
    normalized.length === 0 ||
    normalized.length > 128 ||
    /[\u0000-\u001f\u007f]/u.test(normalized)
  ) {
    return null;
  }
  return normalized;
}

function safeReceipt(value: string | null | undefined): string | null {
  if (typeof value !== "string") return null;
  if (value.length > 32_768 || value.includes("\u0000")) {
    throw adapterError(
      "INSTALLMENT_APPROVED_MATERIAL_INVALID",
      "Provider receipt is invalid.",
    );
  }
  return value;
}

function safeReceiptOrNull(value: string | null | undefined): string | null {
  try {
    return safeReceipt(value);
  } catch {
    // provider 返回的非法回单不可进入 Unknown 恢复材料，也不传播原文。
    return null;
  }
}

function requiredText(value: string | null | undefined, label: string): string {
  if (typeof value !== "string" || value.trim().length === 0) {
    throw adapterError(
      "INSTALLMENT_ACTION_INVALID",
      `${label} is required.`,
    );
  }
  return value;
}

function requiredProviderEnvironment(provider: OnlinePaymentPort): string {
  const value = (provider as Readonly<{ environment?: unknown }>).environment;
  if (typeof value !== "string") {
    throw adapterError("INSTALLMENT_PROVIDER_UNAVAILABLE", "Linkly installment provider environment is unavailable.");
  }
  const environment = value.trim();
  if (!environment || environment.length > 64 || /[\u0000-\u001f\u007f]/u.test(environment)) {
    throw adapterError("INSTALLMENT_PROVIDER_UNAVAILABLE", "Linkly installment provider environment is invalid.");
  }
  return environment;
}

function protectedText(value: string, label: string): string {
  if (
    typeof value !== "string" ||
    value.trim().length === 0 ||
    value.length > 512 ||
    value.includes("\u0000")
  ) {
    throw adapterError(
      "INSTALLMENT_VOUCHER_MATERIAL_INVALID",
      `${label} is invalid.`,
    );
  }
  return value;
}

function generatedId(value: string, label: string): string {
  const normalized = requiredText(value, label);
  if (normalized.length > 256 || normalized.includes("\u0000")) {
    throw adapterError(
      "INSTALLMENT_ACTION_INVALID",
      `${label} is invalid.`,
    );
  }
  return normalized;
}

function refundStepIdempotencyKey(
  operationGuid: string,
  originalPaymentGuid: string,
): string {
  if (!isUuid(operationGuid) || !isUuid(originalPaymentGuid)) {
    throw adapterError(
      "INSTALLMENT_ACTION_INVALID",
      "Refund provenance identifiers are invalid.",
    );
  }
  return `${operationGuid.toLowerCase()}:refund:${originalPaymentGuid.toLowerCase()}`;
}

function isUuid(value: string): boolean {
  return /^[0-9a-f]{8}-[0-9a-f]{4}-[1-5][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/iu.test(
    value,
  );
}

function adapterError(
  code: InstallmentPaymentAdapterErrorCode,
  message: string,
): InstallmentPaymentAdapterError {
  return new InstallmentPaymentAdapterError(code, message);
}

function planConflict(): InstallmentPaymentAdapterError {
  return adapterError(
    "INSTALLMENT_ATTEMPT_PLAN_CONFLICT",
    "Persisted installment provider plan conflicts with the frozen action.",
  );
}

function provenanceInvalid(): InstallmentPaymentAdapterError {
  return adapterError(
    "INSTALLMENT_REFUND_PROVENANCE_INVALID",
    "Complete installment refund provenance could not be proven.",
  );
}

function materialInvalid(): InstallmentPaymentAdapterError {
  return adapterError(
    "INSTALLMENT_APPROVED_MATERIAL_INVALID",
    "Stored installment approval material is invalid.",
  );
}
