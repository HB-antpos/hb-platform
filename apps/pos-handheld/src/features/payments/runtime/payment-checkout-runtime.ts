import type {
  PaymentProviderAvailability,
  PaymentProviderAvailabilityPort,
  PaymentProviderConfigurationBlocker,
} from "./payment-provider-registry";

import type {
  CartSnapshot,
  LocalOrderState,
  Money,
  PaymentAttempt,
  PaymentProvider,
  PricingCartStateSnapshot,
  TenderMethod,
} from "@/core/contracts";
import type {
  MixedPaymentCoordinator,
  MixedPaymentResult,
  MixedPaymentStatus,
} from "@/features/payments/mixed/mixed-payment-coordinator";
import {
  PaymentAttemptOfflineError,
  type PaymentAttemptExecutionResult,
  type PaymentAttemptService,
} from "@hb/pos-payments-core/features/payments/payment-attempt-service";
import type { DurableVoucherPreparationService } from "@/features/payments/runtime/voucher-preparation";
import type {
  LinklyPaymentTerminalSelectionBindingPort,
  LinklyPaymentTerminalSelectionExpectation,
} from "@/features/payments/linkly";
import { calculateCashSettlement } from "@/features/sales/domain";

export const PAYMENT_PERMISSION = Object.freeze({
  view: "Permissions.PosTerminal.Payment.View",
  takeCash: "Permissions.PosTerminal.Payment.TakeCash",
  takeCard: "Permissions.PosTerminal.Payment.TakeCard",
  takeVoucher: "Permissions.PosTerminal.Payment.TakeVoucher",
  removeTender: "Permissions.PosTerminal.Payment.RemoveTender",
  confirm: "Permissions.PosTerminal.Payment.Confirm",
} as const);

export type PaymentPermissionCode =
  (typeof PAYMENT_PERMISSION)[keyof typeof PAYMENT_PERMISSION];

export interface PaymentPermissionGuard {
  assert(code: PaymentPermissionCode): void | Promise<void>;
  /** 同步、无副作用地读取当前可信会话的权限；缺失时调用方必须 fail closed。 */
  can?(code: PaymentPermissionCode): boolean;
}

export interface PaymentTrustedSessionGuard {
  assertActive(): void | Promise<void>;
}

export type PaymentCartLease = Readonly<{
  leaseId: string;
  checkoutIntentId: string;
  revision: number;
  total: Money;
  /** 完整、不可变的当前购物车；包含行、折扣、退货来源及精确 revision。 */
  cart: CartSnapshot;
  /**
   * 可恢复定价状态；保留 promotion definitions/asOf 与手工折扣语义。
   * 公开支付快照不会暴露该字段。
   */
  pricingState: PricingCartStateSnapshot;
}>;

export interface PaymentCartLeasePort {
  /**
   * 对 checkoutIntentId 幂等获取持久 exclusive lease。lease 未明确 clear/release 前，
   * ActivePricingCartSession 的任何销售页 mutation 必须 fail closed。
   */
  acquireExact(input: {
    checkoutIntentId: string;
    expectedRevision: number;
  }): Promise<PaymentCartLease>;

  /** 每个不可逆异步边界前后都必须确认 lease 仍绑定同一完整 cart revision。 */
  readExact(lease: PaymentCartLease): Promise<PaymentCartLease>;

  /** 仅订单已被 DB 确认为 completed 后清空购物车并释放 lease。 */
  clearAfterCompleted(
    lease: PaymentCartLease,
    orderGuid: string,
  ): Promise<void>;

  /** 仅没有 partial tender 且终端明确 Cancelled 的安全退出可保留购物车并释放 lease。 */
  releaseAfterSafeCancel(
    lease: PaymentCartLease,
    orderGuid: string,
  ): Promise<void>;
}

export type PaymentCheckoutTender = Readonly<{
  tenderGuid: string;
  method: TenderMethod;
  amount: Money;
  reversible: boolean;
}>;

export type PaymentCheckoutDraftState = LocalOrderState | "DraftPrepared";

export type PaymentCheckoutDraft = Readonly<{
  checkoutIntentId: string;
  orderGuid: string;
  cartRevision: number;
  state: PaymentCheckoutDraftState;
  total: Money;
  remaining: Money;
  /** 仅当历史付款全部是已建立不可变 reversal 关联的现金时为 true。 */
  cancellableAfterReversal: boolean;
  /**
   * 仅包含尚未被 reversal 抵消的活动正 tender。不可变原始/reversal ledger 行由
   * DB 内部保留；公开运行时每种 method 最多一行。
   */
  tenders: readonly PaymentCheckoutTender[];
}>;

export type PaymentCheckoutRecoveryRecord = Readonly<{
  draft: PaymentCheckoutDraft;
  /** Draft 已提交但 attempt 尚未创建的崩溃点为 null。 */
  attemptId: string | null;
  /**
   * payment_action_binding 已提交但 attempt 尚未插入时的不可变原动作。
   * DB 必须严格解析 request_signature；不得包含 idempotencyKey、券码或 provider refs。
   */
  preparedAction: PaymentCheckoutPreparedAction | null;
}>;

export type PaymentCheckoutDraftAbandonResult = Readonly<{
  /** resolve 即代表 SQLite 的 abandon/close CAS 已耐久提交。 */
  replayed: boolean;
}>;

export type PaymentCheckoutDraftCancelledCloseResult = Readonly<{
  draft: PaymentCheckoutDraft;
  replayed: boolean;
}>;

export type PaymentCheckoutPreparedAction = Readonly<{
  actionId: string;
  provider: PaymentProvider;
  operation: "purchase";
  amount: Money;
  manualConfirmed?: boolean;
}>;

export interface PaymentCheckoutDraftPort {
  /**
   * 必须按 checkoutIntentId 原子创建或返回同一 OrderGuid，并将 lease.cart 的完整行、
   * lease.pricingState（含 promotion/asOf/手工折扣）、金额、revision 以及从内部可信
   * 会话解析的 store/device/cashier 身份一起耐久保存。
   * 不得只保存 total，也不得允许页面传入或覆盖可信身份。
   */
  createOrReuse(input: {
    checkoutIntentId: string;
    lease: PaymentCartLease;
  }): Promise<PaymentCheckoutDraft>;

  /** 返回 DB 当前订单/tender truth，不得返回页面缓存。 */
  read(orderGuid: string): Promise<PaymentCheckoutDraft | null>;

  /**
   * 仅供 mixed coordinator 已返回 completed 后，对同一 order 与已冻结 scope 做耐久真相核验。
   * 不得用于启动、partial、取消、普通读取，且绝不能借此绕过任何前置权限校验。
   */
  readAfterDurableCompletion(
    orderGuid: string,
  ): Promise<PaymentCheckoutDraft | null>;

  /**
   * 按当前可信 store/device 找出唯一阻塞记录；同时覆盖 DraftPrepared(attemptId=null)
   * 与 Created/Submitted/Pending/Approved/Unknown attempt。
   */
  findBlockingRecovery(): Promise<PaymentCheckoutRecoveryRecord | null>;

  /**
   * 仅允许无支付事实的 Draft/DraftPrepared，或全部现金已建立不可变 reversal
   * 关联且余额恢复全额的 Completing 草稿。DB 必须以 actionId 幂等并用状态
   * CAS 标记放弃；有任何支付歧义时必须拒绝。
   */
  abandonPrepared(input: {
    orderGuid: string;
    actionId: string;
  }): Promise<PaymentCheckoutDraftAbandonResult>;

  /**
   * actionId 必须来自既有 payment_action_binding；实现按该不可变动作幂等关闭
   * 明确 Cancelled、零活动 tender 且 remaining=total 的草稿。
   */
  closeCancelled(input: {
    orderGuid: string;
    actionId: string;
  }): Promise<PaymentCheckoutDraftCancelledCloseResult>;
}

export type PaymentCheckoutStatus = "draft-prepared" | MixedPaymentStatus;

export type PaymentCheckoutErrorCode =
  | PaymentProviderConfigurationBlocker
  | "PAYMENT_ACTION_IN_FLIGHT"
  | "PAYMENT_ATTEMPT_IDENTITY_MISMATCH"
  | "PAYMENT_ATTEMPT_MISMATCH"
  | "PAYMENT_ATTEMPT_NOT_FOUND"
  | "PAYMENT_ATTEMPT_ORDER_MISMATCH"
  | "PAYMENT_RECOVERY_FAILED"
  | "PAYMENT_RECOVERY_MISMATCH"
  | "PAYMENT_START_FAILED"
  | "PAYMENT_CANCEL_FAILED"
  | "PAYMENT_STATUS_UNKNOWN"
  | "PAYMENT_TERMINAL_AWAITED"
  | "APPROVED_COMPLETION_REQUIRED"
  | "APPROVED_COMPLETION_FAILED"
  | "APPROVED_COMPLETION_MISMATCH"
  | "APPROVED_TRUTH_MISMATCH"
  | "BLOCKING_ATTEMPT_ORDER_MISMATCH"
  | "MIXED_CASH_COMMIT_FAILED"
  | "MIXED_CASH_UNAVAILABLE"
  | "ONLINE_REQUIRED"
  | "TENDER_REVERSAL_FAILED"
  | "TENDER_REVERSAL_TRUTH_MISMATCH"
  | "TENDER_REVERSAL_UNAVAILABLE"
  | "TENDER_REVERSAL_UNKNOWN"
  | "TENDER_REVERSAL_RECOVERY_REQUIRED"
  | "TENDER_REVERSAL_BLOCKED"
  | "ZERO_BALANCE_ORDER_NOT_COMPLETED"
  | "PAYMENT_DRAFT_NOT_FOUND"
  | "PAYMENT_DRAFT_CONFLICT"
  | "PAYMENT_DRAFT_ABANDON_FORBIDDEN"
  | "PAYMENT_CART_LEASE_CONFLICT"
  | "PAYMENT_QUANTITY_UNSUPPORTED"
  | "PAYMENT_TENDER_METHOD_ALREADY_ACTIVE"
  | "PAYMENT_CHECKOUT_FAILED"
  | "PAYMENT_PREPARED_ACTION_RECOVERY_REQUIRED"
  | "LINKLY_CLOUD_TERMINAL_SELECTION_CONFLICT"
  | "SQUARE_SANDBOX_AMOUNT_LIMIT_EXCEEDED"
  | "VOUCHER_CONTEXT_NOT_PREPARED"
  | "LINKLY_ACKNOWLEDGEMENT_PENDING"
  | "RETURN_RECOVERY_REQUIRED"
  | "MANUAL_CARD_CONFIRMATION_REQUIRED"
  | "MANUAL_CARD_FULL_BALANCE_REQUIRED"
  | "MANUAL_CARD_SALE_ONLY";

export type PaymentCheckoutAllowedActions = Readonly<{
  start: boolean;
  changeProvider: boolean;
  recover: boolean;
  cancel: boolean;
  addCash: boolean;
  removeTender: boolean;
}>;

/**
 * 礼券 tender 撤销的公开恢复指针。只暴露本地 tender 标识和稳定状态；
 * 耐久 actionId、礼券码、provider token 及幂等材料始终留在数据库层。
 */
export type PaymentCheckoutTenderReversalRecovery = Readonly<{
  tenderGuid: string;
  status: "pending" | "unknown" | "blocked";
}>;

/**
 * Route/UI 唯一可见的支付快照。没有 receipt、provider references、token、
 * voucherCode、authorization code 或完整 cashier/device 凭据。
 */
export type PaymentCheckoutPublicSnapshot = Readonly<{
  orderGuid: string;
  total: Money;
  remaining: Money;
  tenders: readonly PaymentCheckoutTender[];
  attemptId: string | null;
  /** 仅投影耐久 PaymentAttempt.createdAtIso；无 attempt 时必须为 null。 */
  attemptCreatedAtIso: string | null;
  provider: PaymentProvider | null;
  status: PaymentCheckoutStatus;
  errorCode: PaymentCheckoutErrorCode | null;
  allowedActions: PaymentCheckoutAllowedActions;
  tenderReversalRecovery?: PaymentCheckoutTenderReversalRecovery;
  /** 仅在刚完成的现金动作中返回，数值来自持久化 cash action。 */
  cashSettlement?: PaymentCheckoutCashSettlement;
}>;

export type PaymentCheckoutCashSettlement = Readonly<{
  tendered: Money;
  applied: Money;
  change: Money;
}>;

export type StartPaymentCheckoutInput = Readonly<{
  checkoutIntentId: string;
  expectedCartRevision: number;
  actionId: string;
  provider: PaymentProvider;
  amount: Money;
  manualConfirmed?: boolean;
  /** 仅 provider=voucher 时允许；返回快照永远不会包含该字段。 */
  voucherCode?: string;
  /** Linkly 只接收支付页已经展示并确认的安全终端选择快照。 */
  linklyTerminalSelection?: LinklyPaymentTerminalSelectionExpectation;
}>;

export type ResumePreparedPaymentInput = Readonly<{
  actionId: string;
  provider: PaymentProvider;
  amount: Money;
  manualConfirmed?: boolean;
  voucherCode?: string;
  linklyTerminalSelection?: LinklyPaymentTerminalSelectionExpectation;
}>;

export type RecoverPaymentCheckoutInput = Readonly<{
  orderGuid: string;
  attemptId: string;
  signal?: AbortSignal;
  deadlineAtMs?: number;
}>;

export type CancelPaymentCheckoutInput = RecoverPaymentCheckoutInput;

export type AddPaymentCashInput = Readonly<{
  orderGuid: string;
  actionId: string;
  /** 顾客实收；运行时按可信 remaining 计算入账与找零。 */
  amount: Money;
}>;

export type StartCashPaymentCheckoutInput = Readonly<{
  checkoutIntentId: string;
  expectedCartRevision: number;
  actionId: string;
  /** 顾客实收；运行时在可信 draft 上按 min(实收, remaining) 原子入账。 */
  amount: Money;
}>;

export type RemovePaymentTenderInput = Readonly<{
  orderGuid: string;
  actionId: string;
  tenderGuid: string;
}>;

export interface PaymentCheckoutRuntimePort {
  listProviderAvailability(): readonly PaymentProviderAvailability[];
  /** 当前可信收银员是否具备现金收款权限；缺失或异常均视为不可用。 */
  canTakeCash?(): boolean;
  read(orderGuid: string): Promise<PaymentCheckoutPublicSnapshot>;
  findRecoveryRequired(): Promise<PaymentCheckoutPublicSnapshot | null>;
  resumeCurrent(
    preparedInput?: ResumePreparedPaymentInput,
  ): Promise<PaymentCheckoutPublicSnapshot | null>;
  start(input: StartPaymentCheckoutInput): Promise<PaymentCheckoutPublicSnapshot>;
  startCash?(
    input: StartCashPaymentCheckoutInput,
  ): Promise<PaymentCheckoutPublicSnapshot>;
  recover(
    input: RecoverPaymentCheckoutInput,
  ): Promise<PaymentCheckoutPublicSnapshot>;
  retryTenderReversal?(input: {
    orderGuid: string;
    tenderGuid: string;
  }): Promise<PaymentCheckoutPublicSnapshot>;
  cancel(input: CancelPaymentCheckoutInput): Promise<PaymentCheckoutPublicSnapshot>;
  abandonPrepared(input: {
    orderGuid: string;
    actionId: string;
  }): Promise<PaymentCheckoutPublicSnapshot>;
  addCash(input: AddPaymentCashInput): Promise<PaymentCheckoutPublicSnapshot>;
  removeTender(
    input: RemovePaymentTenderInput,
  ): Promise<PaymentCheckoutPublicSnapshot>;
}

export type PaymentCheckoutMixedCoordinatorPort = Pick<
  MixedPaymentCoordinator,
  | "addOnlineTender"
  | "recoverOnlineAttempt"
  | "addCashTender"
  | "removeTender"
>;

export type PaymentCheckoutAttemptPort = Pick<
  PaymentAttemptService,
  "getAttempt" | "getBlockingAttempt" | "cancelAttempt"
>;

export type PaymentCheckoutVoucherPreparationPort = Pick<
  DurableVoucherPreparationService,
  "preparePurchase"
>;

export type PaymentCheckoutRuntimeOptions = Readonly<{
  mixed: PaymentCheckoutMixedCoordinatorPort;
  attempts: PaymentCheckoutAttemptPort;
  drafts: PaymentCheckoutDraftPort;
  cartLease: PaymentCartLeasePort;
  providers: PaymentProviderAvailabilityPort;
  trustedSession: PaymentTrustedSessionGuard;
  permissions: PaymentPermissionGuard;
  voucherPreparation?: PaymentCheckoutVoucherPreparationPort;
  linklyPaymentSelection?: LinklyPaymentTerminalSelectionBindingPort;
}>;

export class PaymentCheckoutRuntimeError extends Error {
  public constructor(public readonly code: PaymentCheckoutErrorCode) {
    super(code);
    this.name = "PaymentCheckoutRuntimeError";
  }
}

type CheckoutFlight = Readonly<{
  signature: string;
  promise: Promise<PaymentCheckoutPublicSnapshot>;
}>;

/**
 * 支付页运行时只编排已耐久的 draft/attempt/tender。所有 provider 调用均由
 * PaymentAttemptService/MixedPaymentCoordinator 执行，运行时不会接触 provider secret。
 */
export class PaymentCheckoutRuntime implements PaymentCheckoutRuntimePort {
  private readonly checkoutFlights = new Map<string, CheckoutFlight>();

  public constructor(private readonly options: PaymentCheckoutRuntimeOptions) {}

  public listProviderAvailability(): readonly PaymentProviderAvailability[] {
    return this.options.providers.listAvailability();
  }

  public canTakeCash(): boolean {
    return this.options.permissions.can?.(PAYMENT_PERMISSION.takeCash) === true;
  }

  public async read(orderGuid: string): Promise<PaymentCheckoutPublicSnapshot> {
    await this.assertView();
    const draft = await this.requireDraft(orderGuid);
    await this.assertView();
    const attempt = await this.options.attempts.getBlockingAttempt(orderGuid);
    await this.assertView();
    return publicSnapshot(draft, attempt, statusForAttempt(attempt));
  }

  public async findRecoveryRequired(): Promise<PaymentCheckoutPublicSnapshot | null> {
    await this.assertView();
    const recovery = await this.options.drafts.findBlockingRecovery();
    await this.assertView();
    if (!recovery) return null;
    const attempt = await this.attemptForRecovery(recovery);
    await this.assertView();
    return publicSnapshot(
      recovery.draft,
      attempt,
      attempt || !recovery.preparedAction
        ? statusForAttempt(attempt)
        : "recovery-required",
      attempt || !recovery.preparedAction
        ? undefined
        : "PAYMENT_PREPARED_ACTION_RECOVERY_REQUIRED",
      recovery.preparedAction,
    );
  }

  public async resumeCurrent(
    preparedInput?: ResumePreparedPaymentInput,
  ): Promise<PaymentCheckoutPublicSnapshot | null> {
    await this.assertView();
    const recovery = await this.options.drafts.findBlockingRecovery();
    await this.assertView();
    if (!recovery) return null;
    const attempt = await this.attemptForRecovery(recovery);
    await this.assertView();
    if (attempt) {
      return this.recover({
        orderGuid: recovery.draft.orderGuid,
        attemptId: attempt.attemptId,
      });
    }
    if (recovery.preparedAction) {
      const lease = await this.acquireDraftLease(recovery.draft);
      return this.startPreparedDraft(
        recovery.draft,
        lease,
        recovery.preparedAction,
        { voucherContextAlreadyPrepared: true },
      );
    }
    if (!preparedInput) {
      return publicSnapshot(recovery.draft, null, "draft-prepared");
    }
    const lease = await this.acquireDraftLease(recovery.draft);
    return this.startPreparedDraft(recovery.draft, lease, preparedInput);
  }

  public start(
    input: StartPaymentCheckoutInput,
  ): Promise<PaymentCheckoutPublicSnapshot> {
    const checkoutIntentId = requiredText(
      input.checkoutIntentId,
      "PAYMENT_DRAFT_CONFLICT",
    );
    const signature = startSignature(input);
    const existing = this.checkoutFlights.get(checkoutIntentId);
    if (existing) {
      if (existing.signature === signature) return existing.promise;
      return Promise.reject(
        new PaymentCheckoutRuntimeError("PAYMENT_ACTION_IN_FLIGHT"),
      );
    }

    const promise = Promise.resolve().then(() => this.startOnce(input));
    const flight = { signature, promise };
    this.checkoutFlights.set(checkoutIntentId, flight);
    promise.then(
      () => this.deleteFlight(checkoutIntentId, flight),
      () => this.deleteFlight(checkoutIntentId, flight),
    );
    return promise;
  }

  public startCash(
    input: StartCashPaymentCheckoutInput,
  ): Promise<PaymentCheckoutPublicSnapshot> {
    const checkoutIntentId = requiredText(
      input.checkoutIntentId,
      "PAYMENT_DRAFT_CONFLICT",
    );
    const signature = JSON.stringify({
      kind: "cash",
      checkoutIntentId,
      expectedCartRevision: input.expectedCartRevision,
      actionId: input.actionId,
      amount: input.amount,
    });
    const existing = this.checkoutFlights.get(checkoutIntentId);
    if (existing) {
      if (existing.signature === signature) return existing.promise;
      return Promise.reject(
        new PaymentCheckoutRuntimeError("PAYMENT_ACTION_IN_FLIGHT"),
      );
    }
    const promise = Promise.resolve().then(async () => {
      await this.assertCashAction();
      assertPositiveAud(input.amount);
      const lease = await this.options.cartLease.acquireExact({
        checkoutIntentId,
        expectedRevision: input.expectedCartRevision,
      });
      assertLease(lease, checkoutIntentId, input.expectedCartRevision, true);
      await this.assertCashAction();
      await this.assertExact(lease, true);
      const draft = await this.options.drafts.createOrReuse({
        checkoutIntentId,
        lease,
      });
      await this.assertCashAction();
      await this.assertExact(lease, true);
      assertDraftMatchesLease(draft, lease);
      assertNoActiveTenderMethod(draft, "cash");
      const cashSettlement = cashSettlementForDraft(draft, input.amount);
      const result = await this.options.mixed.addCashTender({
        actionId: input.actionId,
        orderGuid: draft.orderGuid,
        amount: cashSettlement.applied,
        tenderedAmount: cashSettlement.tendered,
        change: cashSettlement.change,
      });
      if (result.status !== "completed") {
        await this.assertCashAction();
        await this.assertExact(lease);
      }
      return this.finishMixedResult(draft, lease, result, null);
    });
    const flight = { signature, promise };
    this.checkoutFlights.set(checkoutIntentId, flight);
    promise.then(
      () => this.deleteFlight(checkoutIntentId, flight),
      () => this.deleteFlight(checkoutIntentId, flight),
    );
    return promise;
  }

  public async recover(
    input: RecoverPaymentCheckoutInput,
  ): Promise<PaymentCheckoutPublicSnapshot> {
    const draft = await this.requireDraft(input.orderGuid);
    const attempt = await this.requireAttempt(input.attemptId, input.orderGuid);
    await this.assertProviderAction(attempt.provider);
    const lease = await this.acquireDraftLease(draft);
    const durableManualCardAction =
      attempt.provider === "manual-card" && attempt.state === "Created"
        ? await this.cancelPreparedActionOrNull(draft, attempt)
        : null;
    await this.assertExact(
      lease,
      attempt.state === "Created" && durableManualCardAction?.manualConfirmed !== true,
    );

    const mixed = await this.options.mixed.recoverOnlineAttempt({
      orderGuid: draft.orderGuid,
      attemptId: attempt.attemptId,
      ...recoveryDeadlineFields(input),
    });
    if (mixed.status !== "completed") {
      await this.assertProviderAction(attempt.provider);
      await this.assertExact(lease);
    }
    return this.finishMixedResult(draft, lease, mixed, attempt.provider);
  }

  public async cancel(
    input: CancelPaymentCheckoutInput,
  ): Promise<PaymentCheckoutPublicSnapshot> {
    const draft = await this.requireDraft(input.orderGuid);
    const attempt = await this.requireAttempt(input.attemptId, input.orderGuid);
    await this.assertProviderAction(attempt.provider);
    const lease = await this.acquireDraftLease(draft);
    await this.assertExact(lease);

    if (attempt.state === "Unknown" || attempt.state === "Approved") {
      const refreshed = await this.refreshDraft(draft.orderGuid);
      await this.assertProviderAction(attempt.provider);
      await this.assertExact(lease);
      return publicSnapshot(
        refreshed,
        attempt,
        statusForAttempt(attempt),
      );
    }

    const preparedAction = await this.cancelPreparedActionOrNull(
      draft,
      attempt,
    );
    await this.assertProviderAction(attempt.provider);
    await this.assertExact(lease);
    if (!preparedAction) {
      return cancelCloseRecoverySnapshot(draft, attempt);
    }

    let resolvedAttempt = attempt;
    if (attempt.state !== "Cancelled") {
      let execution: PaymentAttemptExecutionResult;
      try {
        execution = await this.options.attempts.cancelAttempt(attempt.attemptId);
      } catch (error) {
        await this.assertProviderAction(attempt.provider);
        await this.assertExact(lease);
        const refreshed = await this.refreshDraft(draft.orderGuid);
        await this.assertProviderAction(attempt.provider);
        await this.assertExact(lease);
        return publicSnapshot(
          refreshed,
          attempt,
          "recovery-required",
          error instanceof PaymentAttemptOfflineError
            ? "ONLINE_REQUIRED"
            : "PAYMENT_CANCEL_FAILED",
        );
      }
      await this.assertProviderAction(attempt.provider);
      await this.assertExact(lease);
      assertSameAttempt(execution.attempt, attempt);
      resolvedAttempt = execution.attempt;

      if (resolvedAttempt.state === "Approved") {
        const mixed = await this.options.mixed.recoverOnlineAttempt({
          orderGuid: draft.orderGuid,
          attemptId: attempt.attemptId,
        });
        if (mixed.status !== "completed") {
          await this.assertProviderAction(attempt.provider);
          await this.assertExact(lease);
        }
        return this.finishMixedResult(draft, lease, mixed, attempt.provider);
      }
    }

    const refreshed = await this.refreshDraft(draft.orderGuid);
    await this.assertProviderAction(attempt.provider);
    await this.assertExact(lease);
    if (
      resolvedAttempt.state === "Cancelled" &&
      refreshed.tenders.length === 0 &&
      refreshed.remaining.cents === refreshed.total.cents
    ) {
      return this.closeCancelledAttempt(
        refreshed,
        lease,
        resolvedAttempt,
        preparedAction,
      );
    }
    return publicSnapshot(
      refreshed,
      resolvedAttempt,
      statusForAttempt(resolvedAttempt),
    );
  }

  public async abandonPrepared(input: {
    orderGuid: string;
    actionId: string;
  }): Promise<PaymentCheckoutPublicSnapshot> {
    await this.assertPreparedAbandon();
    const recovery = await this.options.drafts.findBlockingRecovery();
    await this.assertPreparedAbandon();
    if (
      !recovery ||
      recovery.draft.orderGuid !== input.orderGuid ||
      recovery.attemptId !== null ||
      recovery.preparedAction?.provider === "manual-card" ||
      recovery.draft.tenders.length !== 0 ||
      !canAbandonDraft(recovery.draft)
    ) {
      throw new PaymentCheckoutRuntimeError(
        "PAYMENT_DRAFT_ABANDON_FORBIDDEN",
      );
    }
    const lease = await this.acquireDraftLease(recovery.draft);
    await this.assertExact(lease);
    await this.options.drafts.abandonPrepared({
      orderGuid: recovery.draft.orderGuid,
      actionId: requiredText(
        input.actionId,
        "PAYMENT_DRAFT_ABANDON_FORBIDDEN",
      ),
    });
    // durable close 已是唯一提交屏障；会话可在 await 期间过期，仍必须马上解锁购物车。
    await this.options.cartLease.releaseAfterSafeCancel(
      lease,
      recovery.draft.orderGuid,
    );
    return abandonedSnapshot(recovery.draft);
  }

  public async addCash(
    input: AddPaymentCashInput,
  ): Promise<PaymentCheckoutPublicSnapshot> {
    const draft = await this.requireDraft(input.orderGuid);
    await this.assertCashAction();
    assertPositiveAud(input.amount);
    assertNoActiveTenderMethod(draft, "cash");
    const lease = await this.acquireDraftLease(draft);
    await this.assertExact(lease, true);
    const cashSettlement = cashSettlementForDraft(draft, input.amount);
    const result = await this.options.mixed.addCashTender({
      actionId: input.actionId,
      orderGuid: draft.orderGuid,
      amount: cashSettlement.applied,
      tenderedAmount: cashSettlement.tendered,
      change: cashSettlement.change,
    });
    if (result.status !== "completed") {
      await this.assertCashAction();
      await this.assertExact(lease);
    }
    return this.finishMixedResult(draft, lease, result, null);
  }

  public async removeTender(
    input: RemovePaymentTenderInput,
  ): Promise<PaymentCheckoutPublicSnapshot> {
    const draft = await this.requireDraft(input.orderGuid);
    await this.assertRemoveTender();
    const lease = await this.acquireDraftLease(draft);
    await this.assertExact(lease);
    const selected = draft.tenders.find(
      (tender) => tender.tenderGuid === input.tenderGuid,
    );
    if (!selected?.reversible) {
      throw new PaymentCheckoutRuntimeError("TENDER_REVERSAL_UNAVAILABLE");
    }
    const result = await this.options.mixed.removeTender({
      actionId: input.actionId,
      orderGuid: draft.orderGuid,
      tenderGuid: input.tenderGuid,
    });
    if (result.status !== "completed") {
      await this.assertRemoveTender();
      await this.assertExact(lease);
    }
    return this.finishMixedResult(draft, lease, result, null);
  }

  private async startOnce(
    input: StartPaymentCheckoutInput,
  ): Promise<PaymentCheckoutPublicSnapshot> {
    await this.assertProviderAction(input.provider);
    this.assertProviderAvailable(input.provider);
    assertPositiveAud(input.amount);
    assertManualConfirmation(input);
    const lease = await this.options.cartLease.acquireExact({
      checkoutIntentId: input.checkoutIntentId,
      expectedRevision: input.expectedCartRevision,
    });
    assertLease(lease, input.checkoutIntentId, input.expectedCartRevision, true);
    await this.assertProviderAction(input.provider);
    await this.assertExact(lease, true);
    const draft = await this.options.drafts.createOrReuse({
      checkoutIntentId: input.checkoutIntentId,
      lease,
    });
    await this.assertProviderAction(input.provider);
    await this.assertExact(lease, true);
    assertDraftMatchesLease(draft, lease);
    return this.startPreparedDraft(draft, lease, input);
  }

  private async startPreparedDraft(
    draft: PaymentCheckoutDraft,
    lease: PaymentCartLease,
    input: ResumePreparedPaymentInput,
    recovery: Readonly<{ voucherContextAlreadyPrepared: boolean }> = {
      voucherContextAlreadyPrepared: false,
    },
  ): Promise<PaymentCheckoutPublicSnapshot> {
    await this.assertProviderAction(input.provider);
    if (!recovery.voucherContextAlreadyPrepared) {
      this.assertProviderAvailable(input.provider);
    }
    assertPositiveAud(input.amount);
    assertManualConfirmation(input);
    if (input.provider === "manual-card") {
      if (lease.cart.mode !== "sale" || lease.cart.lines.some((line) => line.kind !== "sale")) {
        throw new PaymentCheckoutRuntimeError("MANUAL_CARD_SALE_ONLY");
      }
      if (input.amount.cents !== draft.remaining.cents) {
        throw new PaymentCheckoutRuntimeError("MANUAL_CARD_FULL_BALANCE_REQUIRED");
      }
    }
    assertWithinRemaining(input.amount, draft.remaining);
    assertNoActiveTenderMethod(
      draft,
      input.provider === "voucher" ? "voucher" : "card",
    );
    const durableManualCardRecovery =
      recovery.voucherContextAlreadyPrepared &&
      input.provider === "manual-card" &&
      input.manualConfirmed === true;
    await this.assertExact(lease, !durableManualCardRecovery);

    if (input.provider === "voucher") {
      if (!recovery.voucherContextAlreadyPrepared) {
        if (!this.options.voucherPreparation) {
          throw new PaymentCheckoutRuntimeError(
            "VOUCHER_CONTEXT_NOT_PREPARED",
          );
        }
        await this.options.voucherPreparation.preparePurchase({
          actionId: input.actionId,
          orderGuid: draft.orderGuid,
          voucherCode: requiredText(
            input.voucherCode ?? "",
            "VOUCHER_CONTEXT_NOT_PREPARED",
          ),
        });
        await this.assertProviderAction(input.provider);
        await this.assertExact(lease);
      }
    } else if (input.voucherCode !== undefined) {
      throw new PaymentCheckoutRuntimeError("PAYMENT_DRAFT_CONFLICT");
    }

    // provider 边界前最后一次复核，确保券上下文、draft、lease 和可信会话仍一致。
    await this.assertProviderAction(input.provider);
    await this.assertExact(lease);
    const submit = () => this.options.mixed.addOnlineTender({
      actionId: input.actionId,
      orderGuid: draft.orderGuid,
      provider: input.provider,
      amount: input.amount,
      ...(input.manualConfirmed === undefined ? {} : { manualConfirmed: input.manualConfirmed }),
    });
    const result =
      input.provider === "linkly-cloud" &&
      input.linklyTerminalSelection &&
      this.options.linklyPaymentSelection
        ? await this.options.linklyPaymentSelection.runWithSelection(
            draft.orderGuid,
            input.linklyTerminalSelection,
            submit,
          )
        : await submit();
    if (result.status !== "completed") {
      await this.assertProviderAction(input.provider);
      await this.assertExact(lease);
    }
    return this.finishMixedResult(draft, lease, result, input.provider);
  }

  private async finishMixedResult(
    before: PaymentCheckoutDraft,
    lease: PaymentCartLease,
    result: MixedPaymentResult,
    requestedProvider: PaymentProvider | null,
  ): Promise<PaymentCheckoutPublicSnapshot> {
    const completed = result.status === "completed";
    if (result.orderGuid !== before.orderGuid) {
      throw new PaymentCheckoutRuntimeError("PAYMENT_DRAFT_CONFLICT");
    }
    const draft = completed
      ? await this.readAfterDurableCompletion(before.orderGuid)
      : await this.refreshDraft(before.orderGuid);
    if (!completed) {
      await this.assertActive();
      await this.assertExact(lease);
    }
    const attempt = completed
      ? await this.persistedAttemptOrNull(result.attemptId, draft.orderGuid)
      : await this.attemptOrNull(result.attemptId, draft.orderGuid);
    if (
      draft.remaining.currency !== result.remaining.currency ||
      draft.remaining.cents !== result.remaining.cents
    ) {
      return publicSnapshot(
        draft,
        attempt,
        "recovery-required",
        "APPROVED_TRUTH_MISMATCH",
      );
    }
    if (!completed) {
      await this.assertActive();
      await this.assertExact(lease);
    }
    if (
      !completed &&
      attempt &&
      requestedProvider !== null &&
      attempt.provider !== requestedProvider
    ) {
      throw new PaymentCheckoutRuntimeError(
        "PAYMENT_ATTEMPT_IDENTITY_MISMATCH",
      );
    }
    if (
      result.status === "cancelled" &&
      attempt?.state === "Cancelled" &&
      draft.tenders.length === 0 &&
      draft.remaining.cents === draft.total.cents
    ) {
      // 终端已确认零扣款取消：只沿用原 immutable action 做本地耐久收尾，禁止再次请求 provider。
      const preparedAction = await this.cancelPreparedActionOrNull(
        draft,
        attempt,
      );
      await this.assertProviderAction(attempt.provider);
      await this.assertExact(lease);
      return this.closeCancelledAttempt(
        draft,
        lease,
        attempt,
        preparedAction,
      );
    }
    const snapshot = publicSnapshot(
      draft,
      attempt,
      result.status === "partial" && draft.cancellableAfterReversal
        ? "draft-prepared"
        : result.status,
      stableMixedError(result.errorCode),
    );
    if (result.status === "completed") {
      const alreadyCompleted =
        isDurablyCompletedOrderState(before.state) &&
        before.remaining.cents === 0;
      // completed 后只能读取冻结 scope 的耐久投影；缺少任一证明都保留原 lease 供恢复。
      if (
        !matchesCompletedCheckoutScope(before, draft, lease) ||
        !isDurablyCompletedOrderState(draft.state) ||
        draft.remaining.cents !== 0 ||
        (!alreadyCompleted &&
          !matchesNewCompletedAction(
            before,
            draft,
            result,
            attempt,
            requestedProvider,
          ))
      ) {
        return publicSnapshot(
          draft,
          attempt,
          "recovery-required",
          "APPROVED_TRUTH_MISMATCH",
        );
      }
      // 不可逆完成已由耐久 draft/attempt/tender 真相确认；只能沿用原 lease 清车，绝不能改用新收银员。
      await this.options.cartLease.clearAfterCompleted(lease, draft.orderGuid);
    }
    return attachCashSettlement(snapshot, result.cashSettlement);
  }

  private async acquireDraftLease(
    draft: PaymentCheckoutDraft,
  ): Promise<PaymentCartLease> {
    const lease = await this.options.cartLease.acquireExact({
      checkoutIntentId: draft.checkoutIntentId,
      expectedRevision: draft.cartRevision,
    });
    assertLease(lease, draft.checkoutIntentId, draft.cartRevision);
    assertDraftMatchesLease(draft, lease);
    await this.assertActive();
    return lease;
  }

  private async assertExact(lease: PaymentCartLease, requireSupportedQuantity = false): Promise<void> {
    const current = await this.options.cartLease.readExact(lease);
    if (
      current.leaseId !== lease.leaseId ||
      current.checkoutIntentId !== lease.checkoutIntentId ||
      current.revision !== lease.revision ||
      current.cart !== lease.cart ||
      current.pricingState !== lease.pricingState
    ) {
      throw new PaymentCheckoutRuntimeError("PAYMENT_CART_LEASE_CONFLICT");
    }
    assertLease(current, lease.checkoutIntentId, lease.revision, requireSupportedQuantity);
  }

  private assertProviderAvailable(provider: PaymentProvider): void {
    const availability = this.options.providers.getAvailability(provider);
    if (!availability.available) {
      throw new PaymentCheckoutRuntimeError(
        availability.blocker ?? "PAYMENT_PROVIDER_UNKNOWN",
      );
    }
  }

  private async assertView(): Promise<void> {
    await this.assertActive();
    await this.options.permissions.assert(PAYMENT_PERMISSION.view);
    await this.assertActive();
  }

  private async assertProviderAction(provider: PaymentProvider): Promise<void> {
    await this.assertView();
    await this.options.permissions.assert(
      provider === "voucher"
        ? PAYMENT_PERMISSION.takeVoucher
        : PAYMENT_PERMISSION.takeCard,
    );
    await this.options.permissions.assert(PAYMENT_PERMISSION.confirm);
    await this.assertActive();
  }

  private async assertCashAction(): Promise<void> {
    await this.assertView();
    await this.options.permissions.assert(PAYMENT_PERMISSION.takeCash);
    await this.options.permissions.assert(PAYMENT_PERMISSION.confirm);
    await this.assertActive();
  }

  private async assertRemoveTender(): Promise<void> {
    await this.assertView();
    await this.options.permissions.assert(PAYMENT_PERMISSION.removeTender);
    await this.assertActive();
  }

  private async assertPreparedAbandon(): Promise<void> {
    await this.assertView();
    await this.options.permissions.assert(PAYMENT_PERMISSION.confirm);
    await this.assertActive();
  }

  private async assertActive(): Promise<void> {
    await this.options.trustedSession.assertActive();
  }

  private async requireDraft(orderGuid: string): Promise<PaymentCheckoutDraft> {
    await this.assertView();
    const draft = await this.options.drafts.read(
      requiredText(orderGuid, "PAYMENT_DRAFT_NOT_FOUND"),
    );
    await this.assertView();
    if (!draft) throw new PaymentCheckoutRuntimeError("PAYMENT_DRAFT_NOT_FOUND");
    validateDraft(draft);
    return draft;
  }

  private async refreshDraft(orderGuid: string): Promise<PaymentCheckoutDraft> {
    const draft = await this.options.drafts.read(orderGuid);
    if (!draft) throw new PaymentCheckoutRuntimeError("PAYMENT_DRAFT_NOT_FOUND");
    validateDraft(draft);
    return draft;
  }

  private async readAfterDurableCompletion(
    orderGuid: string,
  ): Promise<PaymentCheckoutDraft> {
    const draft = await this.options.drafts.readAfterDurableCompletion(orderGuid);
    if (!draft) throw new PaymentCheckoutRuntimeError("PAYMENT_DRAFT_NOT_FOUND");
    // completed 也必须基于耐久投影重验，不能把 coordinator 返回值当作草稿真相。
    validateDraft(draft);
    return draft;
  }

  private async requireAttempt(
    attemptId: string,
    orderGuid: string,
  ): Promise<PaymentAttempt> {
    const attempt = await this.options.attempts.getAttempt(
      requiredText(attemptId, "PAYMENT_ATTEMPT_NOT_FOUND"),
    );
    await this.assertActive();
    if (!attempt) {
      throw new PaymentCheckoutRuntimeError("PAYMENT_ATTEMPT_NOT_FOUND");
    }
    if (attempt.orderGuid !== orderGuid) {
      throw new PaymentCheckoutRuntimeError(
        "PAYMENT_ATTEMPT_ORDER_MISMATCH",
      );
    }
    return attempt;
  }

  private async attemptOrNull(
    attemptId: string | null,
    orderGuid: string,
  ): Promise<PaymentAttempt | null> {
    if (!attemptId) return null;
    return this.requireAttempt(attemptId, orderGuid);
  }

  private async persistedAttemptOrNull(
    attemptId: string | null,
    orderGuid: string,
  ): Promise<PaymentAttempt | null> {
    if (!attemptId) return null;
    const attempt = await this.options.attempts.getAttempt(
      requiredText(attemptId, "PAYMENT_ATTEMPT_NOT_FOUND"),
    );
    if (!attempt) {
      throw new PaymentCheckoutRuntimeError("PAYMENT_ATTEMPT_NOT_FOUND");
    }
    if (attempt.orderGuid !== orderGuid) {
      throw new PaymentCheckoutRuntimeError(
        "PAYMENT_ATTEMPT_ORDER_MISMATCH",
      );
    }
    return attempt;
  }

  private async attemptForRecovery(
    recovery: PaymentCheckoutRecoveryRecord,
  ): Promise<PaymentAttempt | null> {
    validateDraft(recovery.draft);
    if (recovery.preparedAction) {
      validatePreparedAction(recovery.preparedAction, recovery.draft);
    }
    const blocking = await this.options.attempts.getBlockingAttempt(
      recovery.draft.orderGuid,
    );
    await this.assertActive();
    if (recovery.attemptId === null) return blocking;
    const attempt = await this.requireAttempt(
      recovery.attemptId,
      recovery.draft.orderGuid,
    );
    if (
      recovery.preparedAction &&
      (attempt.provider !== recovery.preparedAction.provider ||
        attempt.operation !== recovery.preparedAction.operation ||
        attempt.amount.currency !== recovery.preparedAction.amount.currency ||
        attempt.amount.cents !== recovery.preparedAction.amount.cents)
    ) {
      throw new PaymentCheckoutRuntimeError(
        "PAYMENT_ATTEMPT_IDENTITY_MISMATCH",
      );
    }
    if (blocking && blocking.attemptId !== attempt.attemptId) {
      throw new PaymentCheckoutRuntimeError(
        "PAYMENT_ATTEMPT_IDENTITY_MISMATCH",
      );
    }
    return attempt;
  }

  private async cancelPreparedActionOrNull(
    draft: PaymentCheckoutDraft,
    attempt: PaymentAttempt,
  ): Promise<PaymentCheckoutPreparedAction | null> {
    const recovery = await this.options.drafts.findBlockingRecovery();
    await this.assertActive();
    if (
      !recovery ||
      !recovery.preparedAction ||
      recovery.draft.orderGuid !== draft.orderGuid ||
      recovery.draft.checkoutIntentId !== draft.checkoutIntentId ||
      recovery.draft.cartRevision !== draft.cartRevision ||
      recovery.draft.total.currency !== draft.total.currency ||
      recovery.draft.total.cents !== draft.total.cents ||
      (recovery.attemptId !== attempt.attemptId &&
        !(attempt.state === "Cancelled" && recovery.attemptId === null))
    ) {
      return null;
    }
    try {
      validateDraft(recovery.draft);
      validatePreparedAction(recovery.preparedAction, recovery.draft);
    } catch {
      return null;
    }
    if (
      recovery.preparedAction.provider !== attempt.provider ||
      recovery.preparedAction.operation !== attempt.operation ||
      recovery.preparedAction.amount.currency !== attempt.amount.currency ||
      recovery.preparedAction.amount.cents !== attempt.amount.cents
    ) {
      return null;
    }
    return recovery.preparedAction;
  }

  private async closeCancelledAttempt(
    draft: PaymentCheckoutDraft,
    lease: PaymentCartLease,
    attempt: PaymentAttempt,
    preparedAction: PaymentCheckoutPreparedAction | null,
  ): Promise<PaymentCheckoutPublicSnapshot> {
    if (
      attempt.state !== "Cancelled" ||
      draft.tenders.length !== 0 ||
      draft.remaining.cents !== draft.total.cents ||
      preparedAction === null
    ) {
      return cancelCloseRecoverySnapshot(draft, attempt);
    }

    let closed: PaymentCheckoutDraftCancelledCloseResult;
    try {
      await this.assertProviderAction(attempt.provider);
      await this.assertExact(lease);
      closed = await this.options.drafts.closeCancelled({
        orderGuid: draft.orderGuid,
        actionId: preparedAction.actionId,
      });
      assertCancelledCloseProjection(draft, closed.draft);
    } catch {
      await this.assertProviderAction(attempt.provider);
      await this.assertExact(lease);
      return cancelCloseRecoverySnapshot(draft, attempt);
    }

    await this.assertProviderAction(attempt.provider);
    await this.assertExact(lease);
    await this.options.cartLease.releaseAfterSafeCancel(
      lease,
      draft.orderGuid,
    );
    await this.assertProviderAction(attempt.provider);
    return abandonedSnapshot(closed.draft);
  }

  private deleteFlight(checkoutIntentId: string, flight: CheckoutFlight): void {
    if (this.checkoutFlights.get(checkoutIntentId) === flight) {
      this.checkoutFlights.delete(checkoutIntentId);
    }
  }
}

function publicSnapshot(
  draft: PaymentCheckoutDraft,
  attempt: PaymentAttempt | null,
  status: PaymentCheckoutStatus,
  errorCode: PaymentCheckoutErrorCode | null | undefined = defaultError(attempt),
  preparedAction: PaymentCheckoutPreparedAction | null = null,
): PaymentCheckoutPublicSnapshot {
  validateDraft(draft);
  const settledApprovedTender =
    attempt?.state === "Approved" &&
    (status === "partial" || status === "completed");
  const blocking =
    (isBlockingAttempt(attempt) && !settledApprovedTender) ||
    preparedAction !== null;
  const completed = status === "completed";
  const terminal = attempt?.state === "Declined" || attempt?.state === "Cancelled";
  const canCloseCancelled =
    status === "cancelled" &&
    attempt?.state === "Cancelled" &&
    draft.tenders.length === 0 &&
    draft.remaining.cents === draft.total.cents;
  const canStart =
    !completed &&
    draft.remaining.cents > 0 &&
    (!attempt || terminal || settledApprovedTender) &&
    preparedAction === null;
  return Object.freeze({
    orderGuid: draft.orderGuid,
    total: copyMoney(draft.total),
    remaining: copyMoney(draft.remaining),
    tenders: Object.freeze(
      draft.tenders.map((tender) =>
        Object.freeze({
          tenderGuid: tender.tenderGuid,
          method: tender.method,
          amount: copyMoney(tender.amount),
          reversible: tender.reversible,
        }),
      ),
    ),
    attemptId: attempt?.attemptId ?? null,
    attemptCreatedAtIso: attempt?.createdAtIso ?? null,
    provider: attempt?.provider ?? preparedAction?.provider ?? null,
    status,
    errorCode: errorCode ?? null,
    allowedActions: Object.freeze({
      start: canStart,
      changeProvider: canStart,
      recover:
        !completed &&
        (preparedAction !== null ||
          (attempt !== null &&
            !settledApprovedTender &&
            ["Created", "Submitted", "Pending", "Unknown", "Approved"].includes(
              attempt.state,
            ))),
      cancel:
        !completed &&
        ((attempt !== null &&
          attempt.provider !== "manual-card" &&
          (["Created", "Submitted", "Pending"].includes(attempt.state) ||
            canCloseCancelled)) ||
          (attempt === null &&
            (status === "draft-prepared" || preparedAction !== null) &&
            preparedAction?.provider !== "manual-card" &&
            draft.tenders.length === 0 &&
            canAbandonDraft(draft))),
      addCash: !completed && !blocking && draft.remaining.cents > 0,
      removeTender:
        !completed &&
        !blocking &&
        draft.tenders.some((tender) => tender.reversible),
    }),
  });
}

function recoveryDeadlineFields(
  input: Pick<RecoverPaymentCheckoutInput, "signal" | "deadlineAtMs">,
): Readonly<{ signal: AbortSignal; deadlineAtMs: number }> | Record<string, never> {
  if (input.signal === undefined && input.deadlineAtMs === undefined) return {};
  if (
    input.signal === undefined ||
    input.deadlineAtMs === undefined ||
    !Number.isFinite(input.deadlineAtMs)
  ) {
    throw new PaymentCheckoutRuntimeError("PAYMENT_RECOVERY_FAILED");
  }
  return {
    signal: input.signal,
    deadlineAtMs: input.deadlineAtMs,
  };
}

function abandonedSnapshot(
  draft: PaymentCheckoutDraft,
): PaymentCheckoutPublicSnapshot {
  const snapshot = publicSnapshot(draft, null, "cancelled", null);
  return Object.freeze({
    ...snapshot,
    allowedActions: Object.freeze({
      start: false,
      changeProvider: false,
      recover: false,
      cancel: false,
      addCash: false,
      removeTender: false,
    }),
  });
}

function cancelCloseRecoverySnapshot(
  draft: PaymentCheckoutDraft,
  attempt: PaymentAttempt,
): PaymentCheckoutPublicSnapshot {
  const snapshot = publicSnapshot(
    draft,
    attempt,
    "recovery-required",
    "PAYMENT_CANCEL_FAILED",
  );
  return Object.freeze({
    ...snapshot,
    allowedActions: Object.freeze({
      start: false,
      changeProvider: false,
      recover: false,
      cancel: true,
      addCash: false,
      removeTender: false,
    }),
  });
}

function assertCancelledCloseProjection(
  before: PaymentCheckoutDraft,
  closed: PaymentCheckoutDraft,
): void {
  validateDraft(closed);
  if (
    closed.orderGuid !== before.orderGuid ||
    closed.checkoutIntentId !== before.checkoutIntentId ||
    closed.cartRevision !== before.cartRevision ||
    closed.total.currency !== before.total.currency ||
    closed.total.cents !== before.total.cents ||
    closed.remaining.currency !== closed.total.currency ||
    closed.remaining.cents !== closed.total.cents ||
    closed.tenders.length !== 0
  ) {
    throw new PaymentCheckoutRuntimeError("PAYMENT_DRAFT_CONFLICT");
  }
}

function statusForAttempt(
  attempt: PaymentAttempt | null,
): PaymentCheckoutStatus {
  if (!attempt) return "draft-prepared";
  switch (attempt.state) {
    case "Created":
    case "Submitted":
      return "awaiting-terminal";
    case "Pending":
      return "pending";
    case "Approved":
      return "recovery-required";
    case "Unknown":
      return "unknown";
    case "Declined":
      return "declined";
    case "Cancelled":
      return "cancelled";
  }
}

function defaultError(
  attempt: PaymentAttempt | null,
): PaymentCheckoutErrorCode | null {
  if (attempt?.state === "Unknown") return "PAYMENT_STATUS_UNKNOWN";
  if (attempt?.state === "Approved") return "APPROVED_COMPLETION_REQUIRED";
  if (attempt?.state === "Created" || attempt?.state === "Submitted") {
    return "PAYMENT_TERMINAL_AWAITED";
  }
  return null;
}

function isBlockingAttempt(attempt: PaymentAttempt | null): boolean {
  return (
    attempt !== null &&
    ["Created", "Submitted", "Pending", "Unknown", "Approved"].includes(
      attempt.state,
    )
  );
}

function startSignature(
  input: StartPaymentCheckoutInput,
): string {
  // 故意不把 voucherCode 放入诊断或共享锁签名，避免敏感券码进入可观察内存键。
  return [
    input.actionId,
    input.provider,
    input.amount.currency,
    input.amount.cents,
    input.expectedCartRevision,
    input.manualConfirmed === true ? "confirmed" : "",
    input.linklyTerminalSelection?.environment ?? "",
    input.linklyTerminalSelection?.mode ?? "",
    input.linklyTerminalSelection?.mode === "Active"
      ? input.linklyTerminalSelection.terminalId
      : "",
    input.linklyTerminalSelection?.mode === "Active"
      ? input.linklyTerminalSelection.selectionRevision
      : "",
  ].join("|");
}

function assertManualConfirmation(input: ResumePreparedPaymentInput): void {
  if (input.provider === "manual-card" && input.manualConfirmed !== true) {
    throw new PaymentCheckoutRuntimeError("MANUAL_CARD_CONFIRMATION_REQUIRED");
  }
  if (input.provider !== "manual-card" && input.manualConfirmed !== undefined) {
    throw new PaymentCheckoutRuntimeError("PAYMENT_DRAFT_CONFLICT");
  }
}

function assertLease(
  lease: PaymentCartLease,
  checkoutIntentId: string,
  revision: number,
  requireSupportedQuantity = false,
): void {
  if (
    !lease.leaseId.trim() ||
    lease.checkoutIntentId !== checkoutIntentId ||
    lease.revision !== revision ||
    lease.cart.revision !== revision ||
    lease.pricingState.revision !== revision ||
    lease.pricingState.mode !== lease.cart.mode ||
    lease.total.currency !== "AUD" ||
    lease.cart.actualAmount.currency !== "AUD" ||
    lease.total.cents !== lease.cart.actualAmount.cents
  ) {
    throw new PaymentCheckoutRuntimeError("PAYMENT_CART_LEASE_CONFLICT");
  }
  validateCart(lease.cart, requireSupportedQuantity);
  validatePricingState(lease.pricingState, lease.cart, requireSupportedQuantity);
}

function assertDraftMatchesLease(
  draft: PaymentCheckoutDraft,
  lease: PaymentCartLease,
): void {
  validateDraft(draft);
  if (
    draft.checkoutIntentId !== lease.checkoutIntentId ||
    draft.cartRevision !== lease.revision ||
    draft.total.currency !== lease.total.currency ||
    draft.total.cents !== lease.total.cents
  ) {
    throw new PaymentCheckoutRuntimeError("PAYMENT_DRAFT_CONFLICT");
  }
}

function isDurablyCompletedOrderState(
  state: PaymentCheckoutDraftState,
): boolean {
  return (
    state === "CompletedLocal" ||
    state === "PendingSync" ||
    state === "Syncing" ||
    state === "Synced" ||
    state === "Blocked403" ||
    state === "Rejected"
  );
}

function matchesCompletedCheckoutScope(
  before: PaymentCheckoutDraft,
  after: PaymentCheckoutDraft,
  lease: PaymentCartLease,
): boolean {
  return (
    before.checkoutIntentId === lease.checkoutIntentId &&
    before.cartRevision === lease.revision &&
    sameMoney(before.total, lease.total) &&
    after.checkoutIntentId === before.checkoutIntentId &&
    after.orderGuid === before.orderGuid &&
    after.cartRevision === before.cartRevision &&
    sameMoney(after.total, before.total)
  );
}

function matchesNewCompletedAction(
  before: PaymentCheckoutDraft,
  after: PaymentCheckoutDraft,
  result: MixedPaymentResult,
  attempt: PaymentAttempt | null,
  requestedProvider: PaymentProvider | null,
): boolean {
  const tender = result.tenderGuid
    ? after.tenders.find((candidate) => candidate.tenderGuid === result.tenderGuid)
    : undefined;
  if (!tender) return false;

  if (requestedProvider !== null) {
    return (
      result.attemptId !== null &&
      attempt !== null &&
      attempt.attemptId === result.attemptId &&
      attempt.state === "Approved" &&
      attempt.orderGuid === before.orderGuid &&
      attempt.provider === requestedProvider &&
      attempt.operation === "purchase" &&
      tender.method === (requestedProvider === "voucher" ? "voucher" : "card") &&
      sameMoney(tender.amount, attempt.amount)
    );
  }

  const settlement = result.cashSettlement;
  if (
    tender.method !== "cash" ||
    settlement === undefined ||
    !isNonNegativeAud(settlement.tendered)
  ) {
    return false;
  }
  let expected: PaymentCheckoutCashSettlement;
  try {
    // 以冻结 draft 和顾客实收重新执行确定性五分规则，coordinator 返回值只作为待核验材料。
    expected = cashSettlementForDraft(before, settlement.tendered);
  } catch {
    return false;
  }
  return (
    sameMoney(settlement.tendered, expected.tendered) &&
    sameMoney(settlement.applied, expected.applied) &&
    sameMoney(settlement.change, expected.change) &&
    sameMoney(tender.amount, expected.applied)
  );
}

function sameMoney(left: Money, right: Money): boolean {
  return left.currency === right.currency && left.cents === right.cents;
}

function isNonNegativeAud(value: Money): boolean {
  return (
    value.currency === "AUD" &&
    Number.isSafeInteger(value.cents) &&
    value.cents >= 0
  );
}

function validateCart(cart: CartSnapshot, requireSupportedQuantity: boolean): void {
  if (
    cart.mode !== "sale" ||
    cart.lines.length === 0 ||
    cart.actualAmount.currency !== "AUD" ||
    !Number.isSafeInteger(cart.actualAmount.cents) ||
    cart.actualAmount.cents <= 0
  ) {
    throw new PaymentCheckoutRuntimeError("PAYMENT_CART_LEASE_CONFLICT");
  }
  let itemCount = 0;
  for (const line of cart.lines) {
    // POSM 明细数量是整数；先阻止收款，避免付款完成后上传时丢失小数件数。
    const quantity = Number(line.quantity);
    itemCount += quantity;
    if (requireSupportedQuantity && (
      !Number.isSafeInteger(quantity) || quantity <= 0 ||
      quantity > 2_147_483_647 || itemCount > 2_147_483_647
    )) {
      throw new PaymentCheckoutRuntimeError("PAYMENT_QUANTITY_UNSUPPORTED");
    }
    if (
      !line.lineId.trim() ||
      !line.lookupCode.trim() ||
      !line.displayName.trim() ||
      line.actualAmount.currency !== "AUD" ||
      !Number.isSafeInteger(line.actualAmount.cents)
    ) {
      throw new PaymentCheckoutRuntimeError("PAYMENT_CART_LEASE_CONFLICT");
    }
  }
}

function validatePricingState(
  pricingState: PricingCartStateSnapshot,
  cart: CartSnapshot,
  requireSupportedQuantity: boolean,
): void {
  if (
    pricingState.revision !== cart.revision ||
    pricingState.mode !== cart.mode ||
    !Number.isFinite(Date.parse(pricingState.asOfIso)) ||
    pricingState.lines.length !== cart.lines.length
  ) {
    throw new PaymentCheckoutRuntimeError("PAYMENT_CART_LEASE_CONFLICT");
  }
  const cartLineIds = new Set(cart.lines.map((line) => line.lineId));
  for (const line of pricingState.lines) {
    if (requireSupportedQuantity && (
      !Number.isSafeInteger(line.quantity) || line.quantity <= 0 ||
      line.quantity > 2_147_483_647
    )) {
      throw new PaymentCheckoutRuntimeError("PAYMENT_QUANTITY_UNSUPPORTED");
    }
    if (
      !cartLineIds.has(line.lineId) ||
      !line.productCode.trim() ||
      !line.lookupCode.trim() ||
      !line.displayName.trim() ||
      !Number.isFinite(line.quantity) ||
      !Number.isSafeInteger(line.unitPriceCents)
    ) {
      throw new PaymentCheckoutRuntimeError("PAYMENT_CART_LEASE_CONFLICT");
    }
  }
}

function validateDraft(draft: PaymentCheckoutDraft): void {
  if (
    !draft.checkoutIntentId.trim() ||
    !draft.orderGuid.trim() ||
    !Number.isSafeInteger(draft.cartRevision) ||
    draft.cartRevision < 0 ||
    draft.total.currency !== "AUD" ||
    draft.remaining.currency !== "AUD" ||
    !Number.isSafeInteger(draft.total.cents) ||
    !Number.isSafeInteger(draft.remaining.cents) ||
    draft.total.cents <= 0 ||
    draft.remaining.cents < 0 ||
    draft.remaining.cents > draft.total.cents ||
    typeof draft.cancellableAfterReversal !== "boolean"
  ) {
    throw new PaymentCheckoutRuntimeError("PAYMENT_DRAFT_CONFLICT");
  }
  const tenderIds = new Set<string>();
  let paid = 0;
  for (const tender of draft.tenders) {
    if (
      !tender.tenderGuid.trim() ||
      tenderIds.has(tender.tenderGuid) ||
      tender.amount.currency !== "AUD" ||
      !Number.isSafeInteger(tender.amount.cents) ||
      tender.amount.cents <= 0
    ) {
      throw new PaymentCheckoutRuntimeError("PAYMENT_DRAFT_CONFLICT");
    }
    tenderIds.add(tender.tenderGuid);
    paid = safeAdd(paid, tender.amount.cents);
  }
  if (draft.total.cents - paid !== draft.remaining.cents) {
    throw new PaymentCheckoutRuntimeError("PAYMENT_DRAFT_CONFLICT");
  }
  if (
    draft.cancellableAfterReversal &&
    (draft.state !== "Completing" ||
      draft.tenders.length !== 0 ||
      draft.remaining.cents !== draft.total.cents)
  ) {
    throw new PaymentCheckoutRuntimeError("PAYMENT_DRAFT_CONFLICT");
  }
  const methods = new Set<TenderMethod>();
  for (const tender of draft.tenders) {
    if (methods.has(tender.method)) {
      throw new PaymentCheckoutRuntimeError(
        "PAYMENT_TENDER_METHOD_ALREADY_ACTIVE",
      );
    }
    methods.add(tender.method);
  }
}

function canAbandonDraft(draft: PaymentCheckoutDraft): boolean {
  return (
    draft.tenders.length === 0 &&
    draft.remaining.currency === draft.total.currency &&
    draft.remaining.cents === draft.total.cents &&
    ((draft.cancellableAfterReversal &&
      draft.state === "Completing") ||
      (!draft.cancellableAfterReversal &&
        (draft.state === "Draft" ||
          draft.state === "DraftPrepared")))
  );
}

function assertNoActiveTenderMethod(
  draft: PaymentCheckoutDraft,
  method: TenderMethod,
): void {
  if (draft.tenders.some((tender) => tender.method === method)) {
    throw new PaymentCheckoutRuntimeError(
      "PAYMENT_TENDER_METHOD_ALREADY_ACTIVE",
    );
  }
}

function validatePreparedAction(
  action: PaymentCheckoutPreparedAction,
  draft: PaymentCheckoutDraft,
): void {
  requiredText(action.actionId, "PAYMENT_DRAFT_CONFLICT");
  assertPositiveAud(action.amount);
  assertWithinRemaining(action.amount, draft.remaining);
  if (action.operation !== "purchase") {
    throw new PaymentCheckoutRuntimeError("PAYMENT_DRAFT_CONFLICT");
  }
}

function assertPositiveAud(amount: Money): void {
  if (
    amount.currency !== "AUD" ||
    !Number.isSafeInteger(amount.cents) ||
    amount.cents <= 0
  ) {
    throw new PaymentCheckoutRuntimeError("PAYMENT_DRAFT_CONFLICT");
  }
}

function cashSettlementForDraft(
  draft: PaymentCheckoutDraft,
  cashTendered: Money,
): PaymentCheckoutCashSettlement {
  let nonCashCents = 0;
  for (const tender of draft.tenders) {
    // 调用方已拒绝活动现金 tender；这里再校验一次，确保舍入只基于耐久非现金事实。
    if (tender.method === "cash") {
      throw new PaymentCheckoutRuntimeError("PAYMENT_DRAFT_CONFLICT");
    }
    nonCashCents = safeAdd(nonCashCents, tender.amount.cents);
  }
  if (nonCashCents !== draft.total.cents - draft.remaining.cents) {
    throw new PaymentCheckoutRuntimeError("PAYMENT_DRAFT_CONFLICT");
  }
  const settlement = calculateCashSettlement({
    actualAmount: draft.total,
    nonCashAmount: Object.freeze({ currency: "AUD", cents: nonCashCents }),
    cashTendered,
  });
  // 五分上调时，原始实收虽覆盖余额却仍不足应收，绝不能伪造为已收足或写入 partial。
  if (
    cashTendered.cents >= draft.remaining.cents &&
    cashTendered.cents < settlement.cashDue.cents
  ) {
    throw new PaymentCheckoutRuntimeError("MIXED_CASH_COMMIT_FAILED");
  }
  // 现金终态以五分应收为边界；向下取整时允许实收小于账面余额但完整结清。
  const finalCash = cashTendered.cents >= settlement.cashDue.cents;
  const appliedCents = finalCash
    ? draft.remaining.cents
    : cashTendered.cents;
  // 仅 1/2 分的最终舍入会得到 0 实收；部分现金仍必须真实地追加正数 tender。
  if (
    appliedCents <= 0 &&
    !(finalCash && settlement.cashDue.cents === 0 && draft.remaining.cents > 0)
  ) {
    throw new PaymentCheckoutRuntimeError("PAYMENT_DRAFT_CONFLICT");
  }
  return Object.freeze({
    tendered: copyMoney(
      finalCash ? settlement.normalizedCashTendered : cashTendered,
    ),
    applied: Object.freeze({ currency: "AUD", cents: appliedCents }),
    change: Object.freeze({
      currency: "AUD",
      cents: finalCash ? settlement.change.cents : 0,
    }),
  });
}

function attachCashSettlement(
  snapshot: PaymentCheckoutPublicSnapshot,
  settlement: MixedPaymentResult["cashSettlement"],
): PaymentCheckoutPublicSnapshot {
  if (!settlement) return snapshot;
  return Object.freeze({
    ...snapshot,
    cashSettlement: Object.freeze({
      tendered: copyMoney(settlement.tendered),
      applied: copyMoney(settlement.applied),
      change: copyMoney(settlement.change),
    }),
  });
}

function assertWithinRemaining(amount: Money, remaining: Money): void {
  if (
    amount.currency !== remaining.currency ||
    amount.cents > remaining.cents
  ) {
    throw new PaymentCheckoutRuntimeError("PAYMENT_DRAFT_CONFLICT");
  }
}

function assertSameAttempt(
  actual: PaymentAttempt,
  expected: PaymentAttempt,
): void {
  if (
    actual.attemptId !== expected.attemptId ||
    actual.idempotencyKey !== expected.idempotencyKey ||
    actual.orderGuid !== expected.orderGuid ||
    actual.provider !== expected.provider ||
    actual.operation !== expected.operation ||
    actual.amount.currency !== expected.amount.currency ||
    actual.amount.cents !== expected.amount.cents ||
    actual.createdAtIso !== expected.createdAtIso
  ) {
    throw new PaymentCheckoutRuntimeError(
      "PAYMENT_ATTEMPT_IDENTITY_MISMATCH",
    );
  }
}

function stableMixedError(
  value: string | null,
): PaymentCheckoutErrorCode | null {
  if (value === null) return null;
  return STABLE_MIXED_ERRORS.has(value as PaymentCheckoutErrorCode)
    ? (value as PaymentCheckoutErrorCode)
    : "PAYMENT_CHECKOUT_FAILED";
}

function requiredText(
  value: string,
  code: PaymentCheckoutErrorCode,
): string {
  const normalized = value.trim();
  if (!normalized) throw new PaymentCheckoutRuntimeError(code);
  return normalized;
}

function copyMoney(value: Money): Money {
  return Object.freeze({ currency: value.currency, cents: value.cents });
}

function safeAdd(left: number, right: number): number {
  const value = left + right;
  if (!Number.isSafeInteger(value)) {
    throw new PaymentCheckoutRuntimeError("PAYMENT_DRAFT_CONFLICT");
  }
  return value;
}

const STABLE_MIXED_ERRORS = new Set<PaymentCheckoutErrorCode>([
  "PAYMENT_ACTION_IN_FLIGHT",
  "PAYMENT_ATTEMPT_IDENTITY_MISMATCH",
  "PAYMENT_ATTEMPT_MISMATCH",
  "PAYMENT_ATTEMPT_NOT_FOUND",
  "PAYMENT_ATTEMPT_ORDER_MISMATCH",
  "PAYMENT_RECOVERY_FAILED",
  "PAYMENT_RECOVERY_MISMATCH",
  "PAYMENT_START_FAILED",
  "PAYMENT_STATUS_UNKNOWN",
  "PAYMENT_TERMINAL_AWAITED",
  "LINKLY_CLOUD_TERMINAL_SELECTION_CONFLICT",
  "SQUARE_SANDBOX_AMOUNT_LIMIT_EXCEEDED",
  "APPROVED_COMPLETION_REQUIRED",
  "APPROVED_COMPLETION_FAILED",
  "APPROVED_COMPLETION_MISMATCH",
  "APPROVED_TRUTH_MISMATCH",
  "BLOCKING_ATTEMPT_ORDER_MISMATCH",
  "MIXED_CASH_COMMIT_FAILED",
  "MIXED_CASH_UNAVAILABLE",
  "ONLINE_REQUIRED",
  "TENDER_REVERSAL_FAILED",
  "TENDER_REVERSAL_TRUTH_MISMATCH",
  "TENDER_REVERSAL_UNAVAILABLE",
  "TENDER_REVERSAL_UNKNOWN",
  "ZERO_BALANCE_ORDER_NOT_COMPLETED",
]);
