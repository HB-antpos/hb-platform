import type {
  Money,
  PaymentProvider,
  TenderMethod,
} from "@/core/contracts";
import type {
  LinklyOperatorPublicResult,
  LinklyOperatorInteraction,
  LinklyOperatorRuntimePort,
  LinklyOperatorStatus,
  LinklySafeOperatorKey,
} from "@/features/payments/runtime/linkly-operator-runtime";
import {
  PaymentCheckoutRuntimeError,
  type PaymentCheckoutAllowedActions,
  type PaymentCheckoutErrorCode,
  type PaymentCheckoutPublicSnapshot,
  type PaymentCheckoutRuntimePort,
  type PaymentCheckoutStatus,
  type PaymentCheckoutTenderReversalRecovery,
  type PaymentCheckoutTender,
} from "@/features/payments/runtime/payment-checkout-runtime";
import type { PaymentProviderAvailability } from "@/features/payments/runtime/payment-provider-registry";
import type {
  LinklyPaymentTerminalSelectionExpectation,
  LinklyTerminalSelectionPort,
  LinklyTerminalSelectionSnapshot,
} from "@/features/payments/linkly";
import { calculateCashSettlement } from "@/features/sales/domain";

export type PaymentUiMethod = "cash" | PaymentProvider;

export type PaymentUiPhase =
  | "loading"
  | "ready"
  | "submitting"
  | "draft-prepared"
  | "awaiting-terminal"
  | "pending"
  | "offline-cash"
  | "unknown"
  | "recovery-required"
  | "cash-collection-ready"
  | "cash-confirming"
  | "partial"
  | "declined"
  | "cancelled"
  | "success";

export type PaymentFieldIssue =
  | "amount-required"
  | "amount-invalid"
  | "amount-exceeds-remaining"
  | "cash-final-below-due"
  | "installment-customer-required"
  | "installment-down-payment-below-minimum"
  | "installment-total-below-minimum"
  | "voucher-required"
  | "manual-card-confirmation-required"
  | "method-unavailable"
  | "checkout-unavailable";

export type LinklyOperatorErrorCode =
  | "LINKLY_UNKNOWN_REQUIRES_RECOVERY"
  | "LINKLY_OPERATOR_STATE_INVALID"
  | "LINKLY_OPERATOR_KEY_NOT_ALLOWED"
  | "LINKLY_ACKNOWLEDGEMENT_PENDING";

export type PaymentUiRuntimeErrorCode =
  | PaymentCheckoutErrorCode
  | LinklyOperatorErrorCode
  | "INSTALLMENT_CASH_CONFIRMATION_REQUIRED"
  | "INSTALLMENT_CASH_CANCELLATION_FAILED";

export type PaymentPresenterTender = Readonly<{
  tenderGuid: string;
  method: TenderMethod;
  amount: Money;
  reversible: boolean;
  provider?: PaymentProvider | null;
}>;

export type PaymentCheckoutFlow =
  | "regular"
  | "installment-create"
  | "installment-repayment"
  | "installment-recovery";

export type PaymentCheckoutLine = Readonly<{
  lineKey: string;
  displayName: string;
  quantity: string;
  actualAmountCents: number;
}>;

export type InstallmentCustomerPresentation = Readonly<{
  name: string;
  phone: string;
  editable: boolean;
  editorOpen: boolean;
  draftName: string;
  draftPhone: string;
  installmentNumber: string | null;
}>;

export type PaymentCheckoutPresentation = Readonly<{
  flow: PaymentCheckoutFlow;
  lines: readonly PaymentCheckoutLine[];
  installmentCustomer: InstallmentCustomerPresentation | null;
  cash: Readonly<{
    tenderedCents: number;
    appliedCents: number;
    changeCents: number;
  }>;
  canConfirm: boolean;
  fullInstallmentConfirmationRequired: boolean;
  cashRepaymentStatus?: "idle" | "ready" | "confirming";
}>;

export type PaymentPresenterState = Readonly<{
  phase: PaymentUiPhase;
  busy: boolean;
  /** Square 后台轮询单独公开，避免把全局 busy 语义扩散到其他支付动作。 */
  recoveryInFlight?: boolean;
  initialized: boolean;
  providers: readonly PaymentProviderAvailability[];
  /** 当前 cashier 的现金权限；字段缺失一律视为无权限。 */
  cashAvailable?: boolean;
  selectedMethod: PaymentUiMethod | null;
  amountText: string;
  voucherCaptured: boolean;
  manualCardConfirmed?: boolean;
  sensitiveInputRevision: number;
  fieldIssue: PaymentFieldIssue | null;
  runtimeErrorCode: PaymentUiRuntimeErrorCode | null;
  orderGuid: string | null;
  total: Money;
  remaining: Money;
  tenders: readonly PaymentPresenterTender[];
  attemptId: string | null;
  attemptCreatedAtIso: string | null;
  provider: PaymentProvider | null;
  runtimeStatus: PaymentCheckoutStatus | null;
  allowedActions: PaymentCheckoutAllowedActions;
  tenderReversalRecovery: PaymentCheckoutTenderReversalRecovery | null;
  checkout: PaymentCheckoutPresentation;
  linkly: Readonly<{
    status: LinklyOperatorStatus | null;
    errorCode: LinklyOperatorErrorCode | null;
    allowedKeys: readonly LinklySafeOperatorKey[];
    interaction?: LinklyOperatorInteraction | null | undefined;
  }>;
  linklyTerminals?: Readonly<{
    kind: "unavailable" | "loading" | "ready" | "switching" | "failed";
    environment: string | null;
    snapshot: LinklyTerminalSelectionSnapshot | null;
    errorCode: "LINKLY_TERMINAL_SELECTION_FAILED" | null;
  }>;
}>;

/**
 * PaymentScreen 只依赖这层公开 facade。普通 PaymentPresenter 与分期 checkout
 * presenter 都实现同一交互面，页面无需接触任一耐久账本。
 */
export interface PaymentScreenPresenter {
  getState(): PaymentPresenterState;
  subscribe(listener: () => void): () => void;
  initialize(): Promise<boolean>;
  destroy(): void;
  selectMethod(method: PaymentUiMethod): boolean;
  selectLinklyTerminal?(terminalId: string): Promise<boolean>;
  setAmountText(value: string): void;
  setManualCardConfirmed?(confirmed: boolean): void;
  setVoucherCode(value: string): void;
  dismissError(): void;
  submitSelected(): Promise<boolean>;
  recover(options?: PaymentRecoverOptions): Promise<boolean>;
  cancel(): Promise<boolean>;
  removeTender(tenderGuid: string): Promise<boolean>;
  sendLinklyKey(key: LinklySafeOperatorKey): Promise<boolean>;
  markLinklyReceiptPrinted(): Promise<boolean>;
  acknowledgeLinkly(): Promise<boolean>;
  confirm?(options?: PaymentConfirmOptions): Promise<boolean>;
  /** React 已提交成功页面后通知 presenter；普通支付无需实现。 */
  recordSuccessRendered?(): void;
  openInstallmentCustomerEditor?(): void;
  setInstallmentCustomerDraftName?(value: string): void;
  setInstallmentCustomerDraftPhone?(value: string): void;
  saveInstallmentCustomer?(): void;
  cancelInstallmentCustomerEditor?(): void;
}

export type PaymentConfirmOptions = Readonly<{
  acknowledgeFullInstallmentPayment?: boolean;
}>;

export type PaymentRecoverOptions = Readonly<{
  background?: boolean;
  signal?: AbortSignal;
  deadlineAtMs?: number;
}>;

export type PaymentCheckoutEntryContext = Readonly<{
  checkoutIntentId: string;
  expectedCartRevision: number;
  total: Money;
  lines?: readonly PaymentCheckoutLine[];
}>;

export type PaymentPresenterDependencies = Readonly<{
  runtime: PaymentCheckoutRuntimePort;
  linklyOperator?: LinklyOperatorRuntimePort;
  linklyTerminals?: Readonly<{
    environment: string;
    port: LinklyTerminalSelectionPort;
  }>;
  entry: PaymentCheckoutEntryContext | null;
  createActionId(): string;
}>;

export const LINKLY_SAFE_OPERATOR_KEYS = Object.freeze([
  "ok",
  "cancel",
  "yes",
  "no",
  "authorise",
] as const satisfies readonly LinklySafeOperatorKey[]);

const EMPTY_ALLOWED_ACTIONS: PaymentCheckoutAllowedActions = Object.freeze({
  start: false,
  changeProvider: true,
  recover: false,
  cancel: false,
  addCash: false,
  removeTender: false,
});

const ZERO_AUD: Money = Object.freeze({ currency: "AUD", cents: 0 });

function emptyRegularCheckout(
  lines: readonly PaymentCheckoutLine[] = [],
): PaymentCheckoutPresentation {
  return Object.freeze({
    flow: "regular",
    lines: Object.freeze(
      lines.map((line) => Object.freeze({ ...line })),
    ),
    installmentCustomer: null,
    cash: Object.freeze({
      tenderedCents: 0,
      appliedCents: 0,
      changeCents: 0,
    }),
    canConfirm: false,
    fullInstallmentConfirmationRequired: false,
    cashRepaymentStatus: "idle",
  });
}

function resetCashPresentation(
  checkout: PaymentCheckoutPresentation,
): PaymentCheckoutPresentation {
  if (
    checkout.cash.tenderedCents === 0 &&
    checkout.cash.appliedCents === 0 &&
    checkout.cash.changeCents === 0
  ) {
    return checkout;
  }
  return Object.freeze({
    ...checkout,
    cash: Object.freeze({
      tenderedCents: 0,
      appliedCents: 0,
      changeCents: 0,
    }),
  });
}

/**
 * Presenter 的公开状态仅保留运行时已经脱敏的本地标识与金额。
 * 券码只短暂保留在私有字段，永远不会出现在 state、日志或错误文案中。
 */
export class PaymentPresenter {
  private state: PaymentPresenterState;
  private readonly listeners = new Set<() => void>();
  private actionInFlight: Promise<boolean> | null = null;
  private voucherCode = "";
  private destroyed = false;
  private lifecycleRevision = 0;
  private readonly linklyTerminalAbort = new AbortController();
  private linklySelectionDesiredId: string | null = null;
  private linklySelectionInFlight: Promise<boolean> | null = null;

  public constructor(private readonly dependencies: PaymentPresenterDependencies) {
    const providers = safeProviderAvailability(dependencies.runtime);
    const cashAvailable = isCashAvailable(dependencies);
    // 默认支付方式：现金可启动时优先现金，否则回退到第一个可用 provider。
    const selectedMethod = resolveInitialMethod(providers, dependencies);
    const total = dependencies.entry
      ? copyMoney(dependencies.entry.total)
      : ZERO_AUD;
    this.state = {
      phase: "loading",
      busy: false,
      recoveryInFlight: false,
      initialized: false,
      providers,
      cashAvailable,
      selectedMethod,
      amountText: defaultAmountText(selectedMethod, total),
      voucherCaptured: false,
      manualCardConfirmed: false,
      sensitiveInputRevision: 0,
      fieldIssue: null,
      runtimeErrorCode: null,
      orderGuid: null,
      total,
      remaining: total,
      tenders: Object.freeze([]),
      attemptId: null,
      attemptCreatedAtIso: null,
      provider: null,
      runtimeStatus: null,
      allowedActions: Object.freeze({
        ...EMPTY_ALLOWED_ACTIONS,
        start: dependencies.entry !== null,
        addCash: cashAvailable,
      }),
      tenderReversalRecovery: null,
      checkout: emptyRegularCheckout(dependencies.entry?.lines),
      linkly: emptyLinklyState(),
      linklyTerminals: dependencies.linklyTerminals
        ? Object.freeze({
            kind: "loading" as const,
            environment: dependencies.linklyTerminals.environment,
            snapshot: null,
            errorCode: null,
          })
        : Object.freeze({
            kind: "unavailable" as const,
            environment: null,
            snapshot: null,
            errorCode: null,
          }),
    };
  }

  public readonly getState = (): PaymentPresenterState => this.state;

  public readonly subscribe = (listener: () => void): (() => void) => {
    if (this.destroyed) return () => undefined;
    this.listeners.add(listener);
    return () => this.listeners.delete(listener);
  };

  public initialize(): Promise<boolean> {
    if (this.state.initialized) return Promise.resolve(true);
    return this.runExclusive(async (revision) => {
      this.patchIfCurrent(revision, {
        phase: "loading",
        runtimeErrorCode: null,
        fieldIssue: null,
      });
      const recovery = await this.dependencies.runtime.findRecoveryRequired();
      if (!this.isCurrent(revision)) return false;
      if (recovery) {
        this.applySnapshot(recovery);
        await this.refreshLinklyInteraction(revision);
        const linklyTerminals = await this.loadLinklyTerminals(false);
        this.patchIfCurrent(revision, { linklyTerminals });
      } else {
        const linklyTerminals = await this.loadLinklyTerminals(true);
        if (!this.isCurrent(revision)) return false;
        this.patch({
          phase: "ready",
          initialized: true,
          linklyTerminals,
          allowedActions: Object.freeze({
            ...EMPTY_ALLOWED_ACTIONS,
            start: this.dependencies.entry !== null,
            addCash: isCashAvailable(this.dependencies),
          }),
        });
      }
      return true;
    });
  }

  public load(orderGuid: string): Promise<boolean> {
    return this.runExclusive(async (revision) => {
      this.patchIfCurrent(revision, {
        phase: "loading",
        runtimeErrorCode: null,
        fieldIssue: null,
      });
      const snapshot = await this.dependencies.runtime.read(orderGuid);
      if (!this.isCurrent(revision)) return false;
      this.applySnapshot(snapshot);
      await this.refreshLinklyInteraction(revision);
      return true;
    });
  }

  public destroy(): void {
    if (this.destroyed) return;
    this.destroyed = true;
    this.lifecycleRevision += 1;
    this.linklyTerminalAbort.abort();
    this.linklySelectionDesiredId = null;
    this.voucherCode = "";
    this.state = { ...this.state, manualCardConfirmed: false };
    this.listeners.clear();
  }

  public selectMethod(method: PaymentUiMethod): boolean {
    if (
      this.destroyed ||
      this.state.busy ||
      !canSelectPaymentMethod(this.state, method)
    ) {
      this.patch({ fieldIssue: "method-unavailable" });
      return false;
    }
    if (method !== "voucher") this.clearVoucher();
    this.patch({
      selectedMethod: method,
      manualCardConfirmed: false,
      amountText: defaultAmountText(method, this.state.remaining),
      fieldIssue: null,
      runtimeErrorCode: null,
      checkout:
        method === "cash"
          ? this.state.checkout
          : resetCashPresentation(this.state.checkout),
    });
    return true;
  }

  public selectLinklyTerminal(terminalId: string): Promise<boolean> {
    const normalizedTerminalId = terminalId.trim();
    const resource = this.state.linklyTerminals;
    const terminal = resource?.snapshot?.terminals.find(
      (candidate) => candidate.terminalId === normalizedTerminalId,
    );
    if (
      !this.dependencies.linklyTerminals ||
      !resource?.snapshot ||
      resource.snapshot.mode !== "Active" ||
      !canSwitchLinklyTerminal(this.state) ||
      !terminal ||
      !terminal.isReady ||
      terminal.pairingState !== "Ready"
    ) {
      return Promise.resolve(false);
    }
    if (
      resource.snapshot.selectedTerminalId === normalizedTerminalId &&
      !this.linklySelectionInFlight
    ) {
      return Promise.resolve(true);
    }

    this.linklySelectionDesiredId = normalizedTerminalId;
    if (this.linklySelectionInFlight) return this.linklySelectionInFlight;
    const pending = this.drainLinklyTerminalSelection().finally(() => {
      if (this.linklySelectionInFlight === pending) {
        this.linklySelectionInFlight = null;
      }
    });
    this.linklySelectionInFlight = pending;
    return pending;
  }

  private async loadLinklyTerminals(
    autoSelectSingle: boolean,
  ): Promise<NonNullable<PaymentPresenterState["linklyTerminals"]>> {
    const configuration = this.dependencies.linklyTerminals;
    if (!configuration) {
      return Object.freeze({
        kind: "unavailable",
        environment: null,
        snapshot: null,
        errorCode: null,
      });
    }
    try {
      let snapshot = await configuration.port.readTerminals(
        configuration.environment,
        this.linklyTerminalAbort.signal,
      );
      if (snapshot.environment !== configuration.environment) {
        throw new Error("LINKLY_TERMINAL_ENVIRONMENT_MISMATCH");
      }
      const onlyTerminal = snapshot.terminals[0];
      if (
        autoSelectSingle &&
        snapshot.mode === "Active" &&
        snapshot.selectedTerminalId === null &&
        snapshot.terminals.length === 1 &&
        onlyTerminal?.isReady &&
        onlyTerminal.pairingState === "Ready"
      ) {
        snapshot = await configuration.port.selectTerminal(
          configuration.environment,
          onlyTerminal.terminalId,
          snapshot.selectionRevision,
          this.linklyTerminalAbort.signal,
        );
      }
      return Object.freeze({
        kind: "ready",
        environment: configuration.environment,
        snapshot,
        errorCode: null,
      });
    } catch {
      return Object.freeze({
        kind: "failed",
        environment: configuration.environment,
        snapshot: null,
        errorCode: "LINKLY_TERMINAL_SELECTION_FAILED",
      });
    }
  }

  private async drainLinklyTerminalSelection(): Promise<boolean> {
    const configuration = this.dependencies.linklyTerminals;
    let latest = this.state.linklyTerminals?.snapshot ?? null;
    if (!configuration || !latest || latest.mode !== "Active") return false;

    while (this.linklySelectionDesiredId && !this.destroyed) {
      const targetId = this.linklySelectionDesiredId;
      this.linklySelectionDesiredId = null;
      const target = latest.terminals.find(
        (terminal) => terminal.terminalId === targetId,
      );
      if (
        !target ||
        !target.isReady ||
        target.pairingState !== "Ready" ||
        !canSwitchLinklyTerminal(this.state)
      ) {
        return false;
      }
      this.patch({
        linklyTerminals: Object.freeze({
          kind: "switching",
          environment: configuration.environment,
          snapshot: this.state.linklyTerminals?.snapshot ?? latest,
          errorCode: null,
        }),
      });
      try {
        latest = await configuration.port.selectTerminal(
          configuration.environment,
          targetId,
          latest.selectionRevision,
          this.linklyTerminalAbort.signal,
        );
      } catch {
        try {
          latest = await configuration.port.readTerminals(
            configuration.environment,
            this.linklyTerminalAbort.signal,
          );
        } catch {
          if (!this.destroyed) {
            this.patch({
              linklyTerminals: Object.freeze({
                kind: "failed",
                environment: configuration.environment,
                snapshot: null,
                errorCode: "LINKLY_TERMINAL_SELECTION_FAILED",
              }),
            });
          }
          return false;
        }
        // PUT 结果不明确时只重读，不自动重发同一次选择。
        if (!this.linklySelectionDesiredId) {
          this.patch({
            linklyTerminals: Object.freeze({
              kind: "ready",
              environment: configuration.environment,
              snapshot: latest,
              errorCode: null,
            }),
          });
          return latest.selectedTerminalId === targetId;
        }
      }

      // 新目标已排队时不发布旧目标，使用刚取得的 revision 继续持久切换。
      if (this.linklySelectionDesiredId) continue;
      if (!this.destroyed) {
        this.patch({
          linklyTerminals: Object.freeze({
            kind: "ready",
            environment: configuration.environment,
            snapshot: latest,
            errorCode: null,
          }),
        });
      }
    }
    return !this.destroyed;
  }

  public setAmountText(value: string): void {
    if (this.destroyed || this.state.busy) return;
    if (this.state.selectedMethod === "manual-card") {
      // 人工确认绑定当前整笔尾款，不接受键盘或外部调用改写金额。
      this.patch({
        amountText: defaultAmountText("manual-card", this.state.remaining),
        manualCardConfirmed: false,
        fieldIssue: null,
      });
      return;
    }
    this.patch({
      amountText: value,
      manualCardConfirmed: false,
      fieldIssue: null,
    });
  }

  public setManualCardConfirmed(confirmed: boolean): void {
    if (this.destroyed || this.state.busy) return;
    const accepted = confirmed && this.state.selectedMethod === "manual-card" &&
      canSelectPaymentMethod(this.state, "manual-card");
    this.patch({ manualCardConfirmed: accepted, fieldIssue: null });
  }

  public setVoucherCode(value: string): void {
    if (this.destroyed || this.state.busy) return;
    this.voucherCode = value.trim();
    this.patch({
      voucherCaptured: this.voucherCode.length > 0,
      fieldIssue:
        this.state.fieldIssue === "voucher-required"
          ? null
          : this.state.fieldIssue,
    });
  }

  public dismissError(): void {
    if (this.destroyed || this.state.busy) return;
    this.patch({
      runtimeErrorCode: null,
      linkly: {
        ...this.state.linkly,
        errorCode: null,
      },
    });
  }

  public submitSelected(): Promise<boolean> {
    const method = this.state.selectedMethod;
    if (method === "manual-card" && this.state.manualCardConfirmed !== true) {
      this.patch({ fieldIssue: "manual-card-confirmation-required" });
      return Promise.resolve(false);
    }
    if (!method || !canSubmitPaymentMethod(this.state, method)) {
      this.patch({ fieldIssue: "method-unavailable" });
      return Promise.resolve(false);
    }
    const amount = method === "manual-card"
      ? copyMoney(this.state.remaining)
      : parseAudInput(this.state.amountText);
    if (!this.state.amountText.trim()) {
      this.patch({ fieldIssue: "amount-required" });
      return Promise.resolve(false);
    }
    if (!amount) {
      this.patch({ fieldIssue: "amount-invalid" });
      return Promise.resolve(false);
    }
    if (
      method !== "cash" &&
      amount.cents > this.state.remaining.cents
    ) {
      this.patch({ fieldIssue: "amount-exceeds-remaining" });
      return Promise.resolve(false);
    }
    if (method === "cash" && cashFinalTenderBelowDue(amount, this.state.remaining)) {
      this.patch({ fieldIssue: "cash-final-below-due" });
      return Promise.resolve(false);
    }
    if (method === "voucher" && !this.voucherCode) {
      this.patch({ fieldIssue: "voucher-required" });
      return Promise.resolve(false);
    }
    const linklyTerminalSelection =
      method === "linkly-cloud"
        ? linklyPaymentSelectionForState(this.state)
        : null;
    if (method === "linkly-cloud" && !linklyTerminalSelection) {
      this.patch({ fieldIssue: "method-unavailable" });
      return Promise.resolve(false);
    }

    return this.runExclusive(async (revision) => {
      this.patchIfCurrent(revision, {
        phase: "submitting",
        runtimeErrorCode: null,
        fieldIssue: null,
      });
      const actionId = this.dependencies.createActionId();
      let snapshot: PaymentCheckoutPublicSnapshot | null;
      try {
        if (method === "cash") {
          const orderGuid = this.state.orderGuid;
          if (!orderGuid) {
            const entry = this.dependencies.entry;
            if (!entry || !this.dependencies.runtime.startCash) {
              this.patchIfCurrent(revision, {
                fieldIssue: "checkout-unavailable",
              });
              return false;
            }
            snapshot = await this.dependencies.runtime.startCash({
              checkoutIntentId: entry.checkoutIntentId,
              expectedCartRevision: entry.expectedCartRevision,
              actionId,
              amount,
            });
          } else {
            snapshot = await this.dependencies.runtime.addCash({
              actionId,
              orderGuid,
              amount,
            });
          }
        } else if (
          this.state.runtimeStatus === "draft-prepared" &&
          this.state.orderGuid
        ) {
          snapshot = await this.dependencies.runtime.resumeCurrent({
            actionId,
            provider: method,
            amount,
            ...(method === "manual-card" ? { manualConfirmed: true } : {}),
            ...(linklyTerminalSelection
              ? { linklyTerminalSelection }
              : {}),
            ...(method === "voucher"
              ? { voucherCode: this.voucherCode }
              : {}),
          });
        } else {
          const entry = this.dependencies.entry;
          if (!entry) {
            this.patchIfCurrent(revision, {
              fieldIssue: "checkout-unavailable",
            });
            return false;
          }
          snapshot = await this.dependencies.runtime.start({
            checkoutIntentId: entry.checkoutIntentId,
            expectedCartRevision: entry.expectedCartRevision,
            actionId,
            provider: method,
            amount,
            ...(method === "manual-card" ? { manualConfirmed: true } : {}),
            ...(linklyTerminalSelection
              ? { linklyTerminalSelection }
              : {}),
            ...(method === "voucher"
              ? { voucherCode: this.voucherCode }
              : {}),
          });
        }
      } finally {
        if (method === "voucher") this.clearVoucher();
        if (method === "manual-card") {
          this.patchIfCurrent(revision, { manualCardConfirmed: false });
        }
      }
      if (!snapshot || !this.isCurrent(revision)) return false;
      this.applySnapshot(snapshot);
      await this.refreshLinklyInteraction(revision);
      if (
        snapshot.errorCode ===
        "LINKLY_CLOUD_TERMINAL_SELECTION_CONFLICT"
      ) {
        const linklyTerminals = await this.loadLinklyTerminals(false);
        if (!this.isCurrent(revision)) return false;
        this.patch({ linklyTerminals });
      }
      if (method === "cash") {
        const settlement = snapshot.cashSettlement;
        this.patchIfCurrent(revision, {
          // 现金实收与找零是耐久 action 事实；缺失时宁可不展示，也不能以输入重算。
          checkout: settlement
            ? Object.freeze({
                ...this.state.checkout,
                cash: Object.freeze({
                  tenderedCents: settlement.tendered.cents,
                  appliedCents: settlement.applied.cents,
                  changeCents: settlement.change.cents,
                }),
              })
            : resetCashPresentation(this.state.checkout),
        });
      }
      return snapshot.status === "completed" || snapshot.status === "partial";
    });
  }

  public recover(options: PaymentRecoverOptions = {}): Promise<boolean> {
    // 恢复调用必须独占本次生命周期，不能加入带有其他 signal 的在途 Promise。
    if (this.actionInFlight) return Promise.resolve(false);
    const orderGuid = this.state.orderGuid;
    const tenderReversalRecovery = this.state.tenderReversalRecovery;
    const retryTenderReversal =
      this.dependencies.runtime.retryTenderReversal?.bind(
        this.dependencies.runtime,
      );
    if (
      !this.state.allowedActions.recover ||
      !orderGuid ||
      tenderReversalRecovery?.status === "blocked" ||
      (tenderReversalRecovery !== null && !retryTenderReversal)
    ) {
      return Promise.resolve(false);
    }
    return this.runExclusive(async (revision) => {
      if (!options.background) {
        this.patchIfCurrent(revision, {
          phase: "submitting",
          runtimeErrorCode: null,
        });
      }
      const snapshot = tenderReversalRecovery
        ? await retryTenderReversal!({
            orderGuid,
            tenderGuid: tenderReversalRecovery.tenderGuid,
          })
        : this.state.attemptId
          ? await this.dependencies.runtime.recover({
              orderGuid,
              attemptId: this.state.attemptId,
              ...paymentRecoveryDeadlineFields(options),
            })
          : await this.dependencies.runtime.resumeCurrent();
      if (!snapshot || !this.isCurrent(revision)) return false;
      this.applySnapshot(snapshot);
      await this.refreshLinklyInteraction(revision, options);
      return snapshot.status === "completed" || snapshot.status === "partial";
    }, { background: options.background === true });
  }

  public cancel(): Promise<boolean> {
    if (
      !this.state.allowedActions.cancel ||
      !this.state.orderGuid
    ) {
      return Promise.resolve(false);
    }
    return this.runExclusive(async (revision) => {
      this.patchIfCurrent(revision, {
        phase: "submitting",
        runtimeErrorCode: null,
      });
      const snapshot = this.state.attemptId
        ? await this.dependencies.runtime.cancel({
            orderGuid: this.state.orderGuid!,
            attemptId: this.state.attemptId,
          })
        : await this.dependencies.runtime.abandonPrepared({
            orderGuid: this.state.orderGuid!,
            actionId: this.dependencies.createActionId(),
          });
      if (!this.isCurrent(revision)) return false;
      this.applySnapshot(snapshot);
      await this.refreshLinklyInteraction(revision);
      return snapshot.status === "cancelled";
    });
  }

  public removeTender(tenderGuid: string): Promise<boolean> {
    if (
      !this.state.allowedActions.removeTender ||
      !this.state.orderGuid ||
      !this.state.tenders.some(
        (tender) =>
          tender.tenderGuid === tenderGuid && tender.reversible,
      )
    ) {
      return Promise.resolve(false);
    }
    return this.runExclusive(async (revision) => {
      this.patchIfCurrent(revision, {
        phase: "submitting",
        runtimeErrorCode: null,
      });
      const snapshot = await this.dependencies.runtime.removeTender({
        orderGuid: this.state.orderGuid!,
        actionId: this.dependencies.createActionId(),
        tenderGuid,
      });
      if (!this.isCurrent(revision)) return false;
      this.applySnapshot(snapshot);
      return snapshot.errorCode === null;
    });
  }

  public sendLinklyKey(key: LinklySafeOperatorKey): Promise<boolean> {
    if (
      !this.dependencies.linklyOperator ||
      this.state.provider !== "linkly-cloud" ||
      !this.state.attemptId ||
      !LINKLY_SAFE_OPERATOR_KEYS.includes(key)
    ) {
      return Promise.resolve(false);
    }
    return this.runExclusive(async (revision) => {
      const result = await this.dependencies.linklyOperator!.sendKey({
        attemptId: this.state.attemptId!,
        key,
      });
      if (!this.isCurrent(revision)) return false;
      this.applyLinklyResult(result);
      if (
        result.status === "completed" ||
        result.status === "cancelled"
      ) {
        const snapshot = await this.dependencies.runtime.recover({
          orderGuid: this.state.orderGuid!,
          attemptId: result.attemptId,
        });
        if (!this.isCurrent(revision)) return false;
        this.applySnapshot(snapshot);
      }
      return result.errorCode === null;
    });
  }

  public markLinklyReceiptPrinted(): Promise<boolean> {
    return this.runLinklyConfirmation("printed");
  }

  public acknowledgeLinkly(): Promise<boolean> {
    return this.runLinklyConfirmation("acknowledged");
  }

  private runLinklyConfirmation(
    action: "printed" | "acknowledged",
  ): Promise<boolean> {
    if (
      !this.dependencies.linklyOperator ||
      this.state.provider !== "linkly-cloud" ||
      !this.state.attemptId
    ) {
      return Promise.resolve(false);
    }
    return this.runExclusive(async (revision) => {
      const result =
        action === "printed"
          ? await this.dependencies.linklyOperator!.markReceiptPrinted(
              this.state.attemptId!,
            )
          : await this.dependencies.linklyOperator!.acknowledge(
              this.state.attemptId!,
            );
      if (!this.isCurrent(revision)) return false;
      this.applyLinklyResult(result);
      if (action === "acknowledged" && result.errorCode === null) {
        // ACK 成功后由 checkout runtime 执行 ACK-only recovery/业务状态刷新；
        // 不能再次调用 operator.read，避免把 ACK 入口变成额外终端查询。
        const snapshot = await this.dependencies.runtime.recover({
          orderGuid: this.state.orderGuid!,
          attemptId: result.attemptId,
        });
        if (!this.isCurrent(revision)) return false;
        this.applySnapshot(snapshot);
        return snapshot.errorCode === null;
      }
      return result.errorCode === null;
    });
  }

  private applySnapshot(snapshot: PaymentCheckoutPublicSnapshot): void {
    if (this.destroyed) return;
    const tenders = copyTenders(snapshot.tenders);
    const tenderReversalRecovery = copyTenderReversalRecovery(
      snapshot.tenderReversalRecovery,
    );
    const activeMethods = tenders.map((tender) => tender.method);
    const currentMethod = this.state.selectedMethod;
    const selectedMethod =
      currentMethod &&
      isMethodSelectableFromSnapshot(
        currentMethod,
        snapshot,
        this.state.providers,
        this.state.cashAvailable === true,
        activeMethods,
      )
        ? currentMethod
        : firstAvailableMethod(
            snapshot,
            this.state.providers,
            this.state.cashAvailable === true,
            activeMethods,
          );
    this.state = {
      ...this.state,
      phase: phaseForSnapshot(snapshot),
      initialized: true,
      selectedMethod,
      manualCardConfirmed: false,
      amountText: defaultAmountText(selectedMethod, snapshot.remaining),
      fieldIssue: null,
      runtimeErrorCode: snapshot.errorCode,
      orderGuid: snapshot.orderGuid,
      total: copyMoney(snapshot.total),
      remaining: copyMoney(snapshot.remaining),
      tenders,
      attemptId: snapshot.attemptId,
      attemptCreatedAtIso: snapshot.attemptCreatedAtIso,
      provider: snapshot.provider,
      runtimeStatus: snapshot.status,
      allowedActions: Object.freeze({
        ...snapshot.allowedActions,
        ...(this.state.cashAvailable ? {} : { addCash: false }),
        ...(tenderReversalRecovery?.status === "blocked"
          ? { recover: false }
          : {}),
      }),
      tenderReversalRecovery,
      checkout: tenders.some((tender) => tender.method === "cash")
        ? this.state.checkout
        : resetCashPresentation(this.state.checkout),
      linkly: linklyStateForSnapshot(snapshot, this.state.linkly),
    };
    this.emit();
  }

  private applyLinklyResult(result: LinklyOperatorPublicResult): void {
    if (this.destroyed || result.attemptId !== this.state.attemptId) return;
    const errorCode = stableLinklyError(result.errorCode);
    this.patch({
      runtimeErrorCode: errorCode ?? this.state.runtimeErrorCode,
      linkly: Object.freeze({
        status: result.status,
        errorCode,
        allowedKeys: Object.freeze([...result.allowedKeys]),
        interaction: copyLinklyInteraction(result.interaction),
      }),
      ...(result.status === "recovery-required"
        ? { phase: "recovery-required" as const }
        : {}),
    });
  }

  private async refreshLinklyInteraction(
    revision: number,
    control: PaymentRecoverOptions = {},
  ): Promise<void> {
    const attemptId = this.state.attemptId;
    if (
      !attemptId ||
      this.state.provider !== "linkly-cloud" ||
      !this.dependencies.linklyOperator ||
      !this.isCurrent(revision)
    ) {
      return;
    }
    if (control.signal?.aborted) return;
    const result = await this.dependencies.linklyOperator.read({
      attemptId,
      ...(control.signal ? { signal: control.signal } : {}),
      ...(control.deadlineAtMs !== undefined
        ? { deadlineAtMs: control.deadlineAtMs }
        : {}),
    });
    if (!this.isCurrent(revision)) return;
    this.applyLinklyResult(result);
  }

  private clearVoucher(): void {
    this.voucherCode = "";
    if (this.destroyed) return;
    this.patch({
      voucherCaptured: false,
      sensitiveInputRevision: this.state.sensitiveInputRevision + 1,
    });
  }

  private runExclusive(
    operation: (revision: number) => Promise<boolean>,
    options: Readonly<{ background?: boolean }> = {},
  ): Promise<boolean> {
    if (this.destroyed) return Promise.resolve(false);
    if (this.actionInFlight) return this.actionInFlight;
    const revision = ++this.lifecycleRevision;
    if (options.background) {
      this.patch({ recoveryInFlight: true });
    } else {
      this.patch({ busy: true });
    }
    const pending = Promise.resolve()
      .then(() => operation(revision))
      .catch((error: unknown) => {
        if (this.isCurrent(revision)) {
          this.patch({
            runtimeErrorCode: runtimeErrorCode(error),
          });
        }
        return false;
      })
      .finally(() => {
        if (this.actionInFlight === pending) {
          this.actionInFlight = null;
          if (this.isCurrent(revision)) {
            this.patch(
              options.background
                ? { recoveryInFlight: false }
                : { busy: false },
            );
          }
        }
      });
    this.actionInFlight = pending;
    return pending;
  }

  private patchIfCurrent(
    revision: number,
    patch: Partial<PaymentPresenterState>,
  ): void {
    if (this.isCurrent(revision)) this.patch(patch);
  }

  private isCurrent(revision: number): boolean {
    return !this.destroyed && revision === this.lifecycleRevision;
  }

  private patch(patch: Partial<PaymentPresenterState>): void {
    if (this.destroyed) return;
    this.state = { ...this.state, ...patch };
    this.emit();
  }

  private emit(): void {
    for (const listener of this.listeners) listener();
  }
}

export function parseAudInput(value: string): Money | null {
  const normalized = value.trim();
  if (!/^(?:0|[1-9]\d*)(?:\.\d{1,2})?$/.test(normalized)) {
    return null;
  }
  const [dollars = "", fraction = ""] = normalized.split(".");
  const cents =
    Number(dollars) * 100 + Number(fraction.padEnd(2, "0") || "0");
  if (!Number.isSafeInteger(cents) || cents <= 0) return null;
  return Object.freeze({ currency: "AUD", cents });
}

function paymentRecoveryDeadlineFields(
  options: PaymentRecoverOptions,
): Readonly<{ signal: AbortSignal; deadlineAtMs: number }> | Record<string, never> {
  if (options.signal === undefined && options.deadlineAtMs === undefined) return {};
  if (
    options.signal === undefined ||
    options.deadlineAtMs === undefined ||
    !Number.isFinite(options.deadlineAtMs)
  ) {
    throw new PaymentCheckoutRuntimeError("PAYMENT_RECOVERY_FAILED");
  }
  return {
    signal: options.signal,
    deadlineAtMs: options.deadlineAtMs,
  };
}

export function canSelectPaymentMethod(
  state: PaymentPresenterState,
  method: PaymentUiMethod,
): boolean {
  if (state.busy) return false;
  if (method === "manual-card" &&
      (state.checkout.flow !== "regular" || state.remaining.cents <= 0)) return false;
  if (method === "linkly-cloud" && !linklyTerminalReady(state)) return false;
  const activeMethods = state.tenders.map((tender) => tender.method);
  if (state.checkout.flow !== "regular") {
    if (!state.allowedActions.start || activeMethods.length > 0) return false;
    return method === "cash"
      ? state.cashAvailable === true && state.allowedActions.addCash
      : providerAvailable(state.providers, method);
  }
  if (method === "cash") {
    return (
      state.cashAvailable === true &&
      state.allowedActions.addCash &&
      !activeMethods.includes("cash")
    );
  }
  if (
    !providerAvailable(state.providers, method) ||
    !state.allowedActions.changeProvider ||
    activeMethods.includes(tenderMethodForProvider(method))
  ) {
    return false;
  }
  return state.allowedActions.start;
}

function linklyTerminalReady(state: PaymentPresenterState): boolean {
  const resource = state.linklyTerminals;
  // 非 Linkly 组合（例如分期 presenter）保留既有 provider 可用性判断。
  if (!resource) return true;
  if (resource.kind !== "ready" || !resource.snapshot) return false;
  if (resource.snapshot.mode !== "Active") return true;
  const selected = resource.snapshot.terminals.find(
    (terminal) => terminal.terminalId === resource.snapshot?.selectedTerminalId,
  );
  return Boolean(
    selected &&
      selected.isReady &&
      !selected.isBusy &&
      selected.pairingState === "Ready",
  );
}

function linklyPaymentSelectionForState(
  state: PaymentPresenterState,
): LinklyPaymentTerminalSelectionExpectation | null {
  const resource = state.linklyTerminals;
  const snapshot = resource?.kind === "ready" ? resource.snapshot : null;
  if (!snapshot) return null;
  if (snapshot.mode !== "Active") {
    return Object.freeze({
      environment: snapshot.environment,
      mode: snapshot.mode,
    });
  }
  const selected = snapshot.terminals.find(
    (terminal) => terminal.terminalId === snapshot.selectedTerminalId,
  );
  if (!selected) return null;
  return Object.freeze({
    environment: snapshot.environment,
    mode: "Active",
    terminalId: selected.terminalId,
    selectionRevision: snapshot.selectionRevision,
  });
}

export function canSwitchLinklyTerminal(state: PaymentPresenterState): boolean {
  if (state.busy || state.recoveryInFlight) return false;
  return (
    state.phase === "ready" ||
    state.phase === "partial" ||
    state.phase === "declined" ||
    state.phase === "cancelled"
  );
}

export function canSubmitPaymentMethod(
  state: PaymentPresenterState,
  method: PaymentUiMethod,
): boolean {
  if (method === "manual-card" && state.manualCardConfirmed !== true) return false;
  return canSelectPaymentMethod(
    { ...state, busy: false },
    method,
  );
}

function phaseForStatus(status: PaymentCheckoutStatus): PaymentUiPhase {
  switch (status) {
    case "draft-prepared":
      return "draft-prepared";
    case "awaiting-terminal":
      return "awaiting-terminal";
    case "pending":
      return "pending";
    case "unknown":
      return "unknown";
    case "recovery-required":
      return "recovery-required";
    case "partial":
      return "partial";
    case "completed":
      return "success";
    case "declined":
      return "declined";
    case "cancelled":
      return "cancelled";
  }
}

function phaseForSnapshot(
  snapshot: PaymentCheckoutPublicSnapshot,
): PaymentUiPhase {
  if (
    snapshot.status === "recovery-required" &&
    snapshot.errorCode === "ONLINE_REQUIRED" &&
    snapshot.allowedActions.addCash
  ) {
    return "offline-cash";
  }
  return phaseForStatus(snapshot.status);
}

function safeProviderAvailability(
  runtime: PaymentCheckoutRuntimePort,
): readonly PaymentProviderAvailability[] {
  try {
    return Object.freeze(
      runtime.listProviderAvailability().map((entry) =>
        Object.freeze({ ...entry }),
      ),
    );
  } catch {
    return Object.freeze([]);
  }
}

function firstAvailableMethod(
  snapshot: PaymentCheckoutPublicSnapshot,
  providers: readonly PaymentProviderAvailability[],
  cashAvailable: boolean,
  activeMethods: readonly TenderMethod[],
): PaymentUiMethod | null {
  if (
    cashAvailable &&
    snapshot.allowedActions.addCash &&
    !activeMethods.includes("cash")
  ) {
    return "cash";
  }
  if (!snapshot.allowedActions.start) return null;
  return firstAvailableProvider(providers, activeMethods);
}

/**
 * 支付页面默认支付方式：现金可用时优先现金，否则回退到第一个可用 provider。
 * 判定条件与初始 allowedActions.addCash 共用 isCashAvailable，保持一致；
 * 用户手动选择后由 selectMethod 覆盖，applySnapshot 按快照可选择性保留当前
 * 选择（现金不可选时回退 firstAvailableMethod），不重复执行默认解析。
 */
function resolveInitialMethod(
  providers: readonly PaymentProviderAvailability[],
  dependencies: PaymentPresenterDependencies,
): PaymentUiMethod | null {
  if (isCashAvailable(dependencies)) return "cash";
  return firstAvailableProvider(providers, []) ?? null;
}

/**
 * 现金结账是否可用：存在结账入口，且当前可信收银员具备 TakeCash 权限。
 * 默认支付方式与初始 addCash 权限（含无恢复时的 ready 分支）共用同一判定，
 * 避免失配。
 */
function isCashAvailable(dependencies: PaymentPresenterDependencies): boolean {
  try {
    return (
      dependencies.entry !== null &&
      typeof dependencies.runtime.startCash === "function" &&
      dependencies.runtime.canTakeCash?.() === true
    );
  } catch {
    // 当前可信会话不可读取时不暴露现金入口，避免 UI 与服务端权限失配。
    return false;
  }
}

function firstAvailableProvider(
  providers: readonly PaymentProviderAvailability[],
  activeMethods: readonly TenderMethod[],
): PaymentProvider | null {
  return (
    providers.find(
      (entry) =>
        entry.available &&
        !activeMethods.includes(tenderMethodForProvider(entry.provider)),
    )?.provider ?? null
  );
}

function isMethodSelectableFromSnapshot(
  method: PaymentUiMethod,
  snapshot: PaymentCheckoutPublicSnapshot,
  providers: readonly PaymentProviderAvailability[],
  cashAvailable: boolean,
  activeMethods: readonly TenderMethod[],
): boolean {
  if (method === "cash") {
    return (
      cashAvailable &&
      snapshot.allowedActions.addCash &&
      !activeMethods.includes("cash")
    );
  }
  return (
    snapshot.allowedActions.start &&
    snapshot.allowedActions.changeProvider &&
    providerAvailable(providers, method) &&
    !activeMethods.includes(tenderMethodForProvider(method))
  );
}

function providerAvailable(
  providers: readonly PaymentProviderAvailability[],
  provider: PaymentProvider,
): boolean {
  return providers.some(
    (entry) => entry.provider === provider && entry.available,
  );
}

function tenderMethodForProvider(provider: PaymentProvider): TenderMethod {
  return provider === "voucher" ? "voucher" : "card";
}

function copyTenders(
  tenders: readonly PaymentCheckoutTender[],
): readonly PaymentPresenterTender[] {
  return Object.freeze(
    tenders.map((tender) =>
      Object.freeze({
        tenderGuid: tender.tenderGuid,
        method: tender.method,
        amount: copyMoney(tender.amount),
        reversible: tender.reversible,
      }),
    ),
  );
}

function copyTenderReversalRecovery(
  recovery: PaymentCheckoutTenderReversalRecovery | undefined,
): PaymentCheckoutTenderReversalRecovery | null {
  if (!recovery) return null;
  return Object.freeze({
    tenderGuid: recovery.tenderGuid,
    status: recovery.status,
  });
}

function copyMoney(value: Money): Money {
  return Object.freeze({
    currency: value.currency,
    cents: value.cents,
  });
}

function centsToAmountInput(cents: number): string {
  return (cents / 100).toFixed(2);
}

function defaultAmountText(
  method: PaymentUiMethod | null,
  remaining: Money,
): string {
  if (remaining.cents <= 0) return "";
  const cashDue = method === "cash" ? cashDueCents(remaining) : null;
  // 1/2 分尾款的五分应收为零，但输入框必须保留可提交的正数原始余额。
  return centsToAmountInput(cashDue && cashDue > 0 ? cashDue : remaining.cents);
}

function cashFinalTenderBelowDue(amount: Money, remaining: Money): boolean {
  return amount.cents >= remaining.cents && amount.cents < cashDueCents(remaining);
}

function cashDueCents(remaining: Money): number {
  return calculateCashSettlement({
    actualAmount: remaining,
    cashTendered: ZERO_AUD,
  }).cashDue.cents;
}

function emptyLinklyState(): PaymentPresenterState["linkly"] {
  return Object.freeze({
    status: null,
    errorCode: null,
    allowedKeys: Object.freeze([]),
    interaction: null,
  });
}

function linklyStateForSnapshot(
  snapshot: PaymentCheckoutPublicSnapshot,
  previous: PaymentPresenterState["linkly"],
): PaymentPresenterState["linkly"] {
  if (snapshot.provider !== "linkly-cloud" || !snapshot.attemptId) {
    return emptyLinklyState();
  }
  if (
    snapshot.status === "awaiting-terminal" ||
    snapshot.status === "pending"
  ) {
    return Object.freeze({
      status: "in-progress",
      errorCode: null,
      // 未读到当前终端旗标前不得展示任何按键，避免将 Key=0 误作取消。
      allowedKeys: previous.status === "in-progress" ? previous.allowedKeys : [],
      interaction: previous.interaction,
    });
  }
  if (snapshot.status === "unknown") {
    return Object.freeze({
      status: "recovery-required",
      errorCode: "LINKLY_UNKNOWN_REQUIRES_RECOVERY",
      allowedKeys: Object.freeze([]),
      interaction: previous.interaction,
    });
  }
  if (snapshot.status === "cancelled") {
    return Object.freeze({
      status: "cancelled",
      errorCode: null,
      allowedKeys: Object.freeze([]),
      interaction: previous.interaction,
    });
  }
  if (snapshot.status === "declined") {
    return Object.freeze({
      status: "declined",
      errorCode: null,
      allowedKeys: Object.freeze([]),
      interaction: previous.interaction,
    });
  }
  if (snapshot.status === "completed" || snapshot.status === "partial") {
    return Object.freeze({
      status: "completed",
      errorCode: null,
      allowedKeys: Object.freeze([]),
      interaction: previous.interaction,
    });
  }
  return previous;
}

function copyLinklyInteraction(
  interaction: LinklyOperatorInteraction | undefined,
): LinklyOperatorInteraction | null {
  if (!interaction) return null;
  return Object.freeze({
    displayText: interaction.displayText,
    displayLines: Object.freeze([...interaction.displayLines]),
    inputType: interaction.inputType,
    graphicCode: interaction.graphicCode,
    recoveryAction: interaction.recoveryAction,
  });
}

function runtimeErrorCode(error: unknown): PaymentUiRuntimeErrorCode {
  if (error instanceof PaymentCheckoutRuntimeError) return error.code;
  return "PAYMENT_CHECKOUT_FAILED";
}

function stableLinklyError(
  value: string | null,
): LinklyOperatorErrorCode | null {
  if (value === null) return null;
  return LINKLY_OPERATOR_ERRORS.has(value as LinklyOperatorErrorCode)
    ? (value as LinklyOperatorErrorCode)
    : null;
}

const LINKLY_OPERATOR_ERRORS = new Set<LinklyOperatorErrorCode>([
  "LINKLY_UNKNOWN_REQUIRES_RECOVERY",
  "LINKLY_OPERATOR_STATE_INVALID",
  "LINKLY_OPERATOR_KEY_NOT_ALLOWED",
  "LINKLY_ACKNOWLEDGEMENT_PENDING",
]);
