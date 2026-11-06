import {
  INSTALLMENTS_ADD_REPAYMENT_PERMISSION,
  INSTALLMENTS_CANCEL_PERMISSION,
  INSTALLMENTS_CREATE_PERMISSION,
} from "@hb/pos-domain/features/installments/installment-authorization";
import {
  INSTALLMENT_MINIMUM_DOWN_PAYMENT_CENTS,
  INSTALLMENT_MINIMUM_TOTAL_CENTS,
  InstallmentWorkflowError,
  type InstallmentCreateDraftPort,
  type InstallmentWorkflowPort,
} from "./installment-presenter";

import type { Money, PaymentProvider } from "@/core/contracts";
import type { LinklySafeOperatorKey } from "@/features/payments/runtime/linkly-operator-runtime";
import { PAYMENT_PERMISSION } from "@/features/payments/runtime/payment-checkout-runtime";
import type { PaymentProviderAvailability } from "@/features/payments/runtime/payment-provider-registry";
import {
  parseAudInput,
  type PaymentCheckoutPresentation,
  type PaymentConfirmOptions,
  type PaymentPresenterState,
  type PaymentScreenPresenter,
  type PaymentUiMethod,
} from "@/features/payments/ui/payment-presenter";
import type {
  InstallmentCreatePaymentEntry,
  InstallmentRepaymentPaymentEntry,
} from "@/features/payments/ui/unified-payment-entry";

type InstallmentCheckoutEntry =
  | InstallmentCreatePaymentEntry
  | InstallmentRepaymentPaymentEntry;

export type InstallmentCheckoutPresenterOptions = Readonly<{
  entry: InstallmentCheckoutEntry | null;
  createDrafts: InstallmentCreateDraftPort;
  initialOnline: boolean;
  permissions: readonly string[];
  workflow: InstallmentWorkflowPort;
  createTenderId(): string;
  monotonicNowMilliseconds?: () => number;
  performanceRecorder?: InstallmentPresenterMetricRecorder;
}>;

type InstallmentPresenterMetricRecorder = Readonly<{
  record(event: Readonly<{
    name: "presenter-success";
    elapsedMs: number;
    operationHash: string;
    path: "prepare-provider-v1" | "legacy-create-begin" | "recovery";
    outcome: "success" | "recovery" | "failure";
  }>): void | Promise<void>;
}>;

const ZERO_AUD: Money = Object.freeze({ currency: "AUD", cents: 0 });
const EMPTY_PROVIDERS = Object.freeze([
  unavailable("square"),
  unavailable("linkly-cloud"),
  unavailable("voucher"),
]);

/**
 * 分期付款只复用 PaymentScreen 的展示契约，不复用普通订单的 payment attempt
 * 账本。单一 tender 在本 presenter 暂存，确认时才写入分期专用耐久 action。
 */
export class InstallmentCheckoutPresenter implements PaymentScreenPresenter {
  private readonly listeners = new Set<() => void>();
  private readonly granted: ReadonlySet<string>;
  private state: PaymentPresenterState;
  private voucherReference = "";
  private destroyed = false;
  private actionInFlight: Promise<boolean> | null = null;
  private cashOperationHash: string | null = null;
  private cashPath: "prepare-provider-v1" | "legacy-create-begin" | "recovery" = "recovery";
  private cashConfirmationStartedAt: number | null = null;
  private cashSuccessRecordedOperationHash: string | null = null;

  public constructor(
    private readonly options: InstallmentCheckoutPresenterOptions,
  ) {
    this.granted = new Set(options.permissions.map((value) => value.trim()));
    this.state = initialState(
      options.entry,
      this.granted.has(PAYMENT_PERMISSION.takeCash),
    );
  }

  public readonly getState = (): PaymentPresenterState => this.state;

  public readonly subscribe = (listener: () => void): (() => void) => {
    if (this.destroyed) return () => undefined;
    this.listeners.add(listener);
    return () => this.listeners.delete(listener);
  };

  public initialize(): Promise<boolean> {
    if (this.state.initialized) return Promise.resolve(true);
    return this.runExclusive(async () => {
      if (!this.options.initialOnline) {
        this.patch({
          initialized: true,
          phase: "recovery-required",
          runtimeErrorCode: "ONLINE_REQUIRED",
        });
        return false;
      }

      const providersPromise = this.loadProviders();
      const entry = this.options.entry;
      if (!entry) {
        const providers = await providersPromise;
        this.patch({
          initialized: true,
          phase: "recovery-required",
          providers,
          allowedActions: allowedActions({ recover: true }),
          checkout: checkoutPresentation("installment-recovery"),
        });
        return true;
      }
      if (entry.kind === "installment-create") {
        const providers = await providersPromise;
        const draft = this.options.createDrafts.getSnapshot();
        if (
          !draft ||
          draft.revision !== entry.expectedCartRevision ||
          draft.lines.length === 0
        ) {
          this.patch({
            initialized: true,
            phase: "recovery-required",
            providers,
            runtimeErrorCode: "PAYMENT_DRAFT_CONFLICT",
          });
          return false;
        }
        if (draft.totalCents < INSTALLMENT_MINIMUM_TOTAL_CENTS) {
          this.patch({
            initialized: true,
            phase: "ready",
            providers,
            total: aud(draft.totalCents),
            remaining: aud(draft.totalCents),
            fieldIssue: "installment-total-below-minimum",
            checkout: Object.freeze({
              ...checkoutPresentation("installment-create"),
              lines: Object.freeze([...draft.lines]),
              installmentCustomer: customerPresentation(true),
            }),
          });
          return false;
        }
        if (!this.canCreate()) {
          return this.initializeForbidden(providers);
        }
        this.patch({
          initialized: true,
          phase: "ready",
          providers,
          selectedMethod: this.firstMethod(providers),
          amountText: centsText(
            Math.min(draft.totalCents, INSTALLMENT_MINIMUM_DOWN_PAYMENT_CENTS),
          ),
          total: aud(draft.totalCents),
          remaining: aud(draft.totalCents),
          allowedActions: allowedActions({
            start: true,
            addCash: this.has(PAYMENT_PERMISSION.takeCash),
          }),
          checkout: Object.freeze({
            ...checkoutPresentation("installment-create"),
            lines: Object.freeze([...draft.lines]),
            installmentCustomer: customerPresentation(true),
          }),
        });
        return true;
      }

      if (!this.canRepay()) return this.initializeForbidden(await providersPromise);
      const [providers, details] = await Promise.all([
        providersPromise,
        this.options.workflow.getDetails({
          installmentGuid: entry.installmentGuid,
          online: true,
        }),
        this.options.workflow.getRepaymentCapabilities?.().catch(() => null),
      ]).then(([loadedProviders, loadedDetails]) => [loadedProviders, loadedDetails] as const);
      if (!details || details.status !== "Active" || details.balanceCents <= 0) {
        this.patch({
          initialized: true,
          phase: "recovery-required",
          providers,
          runtimeErrorCode: "PAYMENT_CHECKOUT_FAILED",
        });
        return false;
      }
      this.patch({
        initialized: true,
        phase: "ready",
        providers,
        selectedMethod: this.firstMethod(providers),
        amountText: centsText(details.balanceCents),
        total: aud(details.balanceCents),
        remaining: aud(details.balanceCents),
        allowedActions: allowedActions({
          start: true,
          addCash: this.has(PAYMENT_PERMISSION.takeCash),
        }),
        checkout: Object.freeze({
          ...checkoutPresentation("installment-repayment"),
          lines: Object.freeze(
            details.lines.map((line) =>
              Object.freeze({
                lineKey: line.installmentLineGuid,
                displayName: line.displayName,
                quantity: line.quantity,
                actualAmountCents: line.actualAmountCents,
              }),
            ),
          ),
          installmentCustomer: Object.freeze({
            ...customerPresentation(false),
            name: details.customerName,
            phone: details.customerPhone ?? "",
            draftName: details.customerName,
            draftPhone: details.customerPhone ?? "",
            installmentNumber: details.installmentNumber,
          }),
        }),
      });
      return true;
    });
  }

  public destroy(): void {
    this.destroyed = true;
    this.voucherReference = "";
    this.listeners.clear();
  }

  public selectMethod(method: PaymentUiMethod): boolean {
    if (!this.methodAvailable(method) || this.state.tenders.length > 0) {
      this.patch({ fieldIssue: "method-unavailable" });
      return false;
    }
    if (method !== "voucher") this.clearVoucher();
    this.patch({
      selectedMethod: method,
      fieldIssue: null,
      checkout: Object.freeze({
        ...this.state.checkout,
        cash:
          method === "cash"
            ? this.state.checkout.cash
            : Object.freeze({
                tenderedCents: 0,
                appliedCents: 0,
                changeCents: 0,
              }),
      }),
    });
    return true;
  }

  public setAmountText(value: string): void {
    if (this.destroyed || this.state.busy || this.state.tenders.length > 0) return;
    this.patch({ amountText: value, fieldIssue: null });
  }

  public setVoucherCode(value: string): void {
    if (this.destroyed || this.state.busy) return;
    this.voucherReference = value.trim();
    this.patch({
      voucherCaptured: this.voucherReference.length > 0,
      fieldIssue: null,
    });
  }

  public dismissError(): void {
    this.patch({ runtimeErrorCode: null });
  }

  public submitSelected(): Promise<boolean> {
    const method = this.state.selectedMethod;
    if (
      !method ||
      !this.methodAvailable(method) ||
      this.state.tenders.length > 0
    ) {
      this.patch({ fieldIssue: "method-unavailable" });
      return Promise.resolve(false);
    }
    const tendered = parseAudInput(this.state.amountText);
    if (!this.state.amountText.trim()) {
      this.patch({ fieldIssue: "amount-required" });
      return Promise.resolve(false);
    }
    if (!tendered) {
      this.patch({ fieldIssue: "amount-invalid" });
      return Promise.resolve(false);
    }
    if (method !== "cash" && tendered.cents > this.state.remaining.cents) {
      this.patch({ fieldIssue: "amount-exceeds-remaining" });
      return Promise.resolve(false);
    }
    if (method === "voucher" && !this.voucherReference) {
      this.patch({ fieldIssue: "voucher-required" });
      return Promise.resolve(false);
    }
    const appliedCents =
      method === "cash"
        ? Math.min(tendered.cents, this.state.remaining.cents)
        : tendered.cents;
    if (
      this.state.checkout.flow === "installment-create" &&
      appliedCents < INSTALLMENT_MINIMUM_DOWN_PAYMENT_CENTS
    ) {
      this.patch({
        fieldIssue: "installment-down-payment-below-minimum",
      });
      return Promise.resolve(false);
    }

    if (
      this.state.checkout.flow === "installment-repayment" &&
      method === "cash"
    ) {
      return this.prepareCashRepayment(tendered.cents);
    }

    const tenderMethod =
      method === "cash" ? "cash" : method === "voucher" ? "voucher" : "card";
    this.patch({
      tenders: Object.freeze([
        Object.freeze({
          tenderGuid: this.options.createTenderId(),
          method: tenderMethod,
          amount: aud(appliedCents),
          reversible: true,
          provider:
            method === "square" || method === "linkly-cloud" ? method : null,
        }),
      ]),
      remaining: aud(Math.max(0, this.state.total.cents - appliedCents)),
      allowedActions: allowedActions({ removeTender: true }),
      checkout: Object.freeze({
        ...this.state.checkout,
        canConfirm: true,
        fullInstallmentConfirmationRequired:
          this.state.checkout.flow === "installment-create" &&
          appliedCents === this.state.total.cents,
        cash: Object.freeze({
          tenderedCents: method === "cash" ? tendered.cents : 0,
          appliedCents: method === "cash" ? appliedCents : 0,
          changeCents:
            method === "cash" ? Math.max(0, tendered.cents - appliedCents) : 0,
        }),
      }),
      fieldIssue: null,
    });
    return Promise.resolve(true);
  }

  public confirm(options?: PaymentConfirmOptions): Promise<boolean> {
    const entry = this.options.entry;
    const tender = this.state.tenders[0];
    const preparedCashConfirmation =
      tender?.method === "cash" &&
      this.state.tenders.length === 1 &&
      this.state.checkout.canConfirm &&
      this.state.checkout.flow === "installment-repayment" &&
      this.state.checkout.cashRepaymentStatus === "ready";
    if (preparedCashConfirmation) {
      return this.confirmPreparedCashRepayment();
    }
    if (
      !entry ||
      !tender ||
      this.state.tenders.length !== 1 ||
      !this.state.checkout.canConfirm
    ) {
      return Promise.resolve(false);
    }
    const method = tender.method;
    const cardProvider =
      tender.provider === "square" || tender.provider === "linkly-cloud"
        ? tender.provider
        : undefined;
    const paymentSelection =
      method === "card" && cardProvider
        ? Object.freeze({ cardProvider })
        : method === "cash"
          ? Object.freeze({
              cashTenderedCents:
                this.state.checkout.cash.tenderedCents,
            })
          : Object.freeze({});
    const customer = this.state.checkout.installmentCustomer;
    if (
      entry.kind === "installment-create" &&
      (!customer?.name.trim() || !customer.phone.trim())
    ) {
      this.patch({ fieldIssue: "installment-customer-required" });
      return Promise.resolve(false);
    }
    if (
      entry.kind === "installment-create" &&
      this.state.checkout.fullInstallmentConfirmationRequired &&
      options?.acknowledgeFullInstallmentPayment !== true
    ) {
      return Promise.resolve(false);
    }
    const voucherReference =
      method === "voucher" ? this.voucherReference : null;
    if (method === "voucher") this.clearVoucher();

    return this.runExclusive(async () => {
      try {
        const details =
          entry.kind === "installment-create"
            ? await this.options.workflow.create({
                draftRevision: entry.expectedCartRevision,
                customerName: customer!.name.trim(),
                customerPhone: customer!.phone.trim(),
                note: null,
                downPaymentCents: tender.amount.cents,
                method,
                voucherReference,
                voucherReservationToken: null,
                ...paymentSelection,
              })
            : await this.options.workflow.addRepayment({
                installmentGuid: entry.installmentGuid,
                amountCents: tender.amount.cents,
                method,
                voucherReference,
                voucherReservationToken: null,
                ...paymentSelection,
              });
        this.patch({
          phase: "success",
          orderGuid: details.installmentGuid,
          remaining: aud(Math.max(0, details.balanceCents)),
          allowedActions: allowedActions({}),
          checkout: Object.freeze({
            ...this.state.checkout,
            canConfirm: false,
            fullInstallmentConfirmationRequired: false,
            cashRepaymentStatus: "idle",
          }),
        });
        return true;
      } catch (error) {
        const failureCode = repaymentSubmitFailureCode(error);
        let recoveryRequired = true;
        try {
          recoveryRequired =
            (await this.options.workflow.hasRecoveryRequired?.()) ??
            true;
        } catch {
          // 本地耐久事实无法核实时必须 fail closed，禁止切换账本。
        }
        if (!recoveryRequired) {
          this.patch({
            phase: "ready",
            runtimeErrorCode: failureCode,
          });
          return false;
        }
        this.patch({
          phase: "recovery-required",
          runtimeErrorCode: failureCode,
          allowedActions: allowedActions({ recover: true }),
        });
        return false;
      }
    });
  }

  public recover(): Promise<boolean> {
    if (!this.state.allowedActions.recover) return Promise.resolve(false);
    return this.runExclusive(async () => {
      const workflow = this.options.workflow;
      try {
        const details = await workflow.recoverBlocking();
        this.patch({
          phase: "success",
          orderGuid: details.installmentGuid,
          total: aud(details.totalCents),
          remaining: aud(Math.max(0, details.balanceCents)),
          allowedActions: allowedActions({}),
        });
        return true;
      } catch (error) {
        if (
          error instanceof InstallmentWorkflowError &&
          error.code === "cash-confirmation-required"
        ) {
          let prepared = null;
          try {
            prepared = await workflow.inspectPreparedCashRepayment?.();
          } catch {
            // 当前 cashier 无法恢复确认时，仍可继续检查主管取消边界。
          }
          if (prepared) {
            const appliedCents = prepared.amountCents;
            this.cashOperationHash = prepared.operationHash;
            this.cashPath = prepared.path ?? "recovery";
            this.patch({
              phase: "recovery-required",
              runtimeErrorCode: "INSTALLMENT_CASH_CONFIRMATION_REQUIRED",
              allowedActions: allowedActions({
                recover: true,
                cancel: this.canCancelPreparedCashRepayment(),
              }),
              total: aud(appliedCents),
              remaining: ZERO_AUD,
              tenders: Object.freeze([
                Object.freeze({
                  tenderGuid: this.options.createTenderId(),
                  method: "cash",
                  amount: aud(appliedCents),
                  reversible: false,
                  provider: null,
                }),
              ]),
              checkout: Object.freeze({
                ...this.state.checkout,
                flow: "installment-repayment",
                canConfirm: true,
                cashRepaymentStatus: "ready",
                cash: Object.freeze({
                  tenderedCents: appliedCents,
                  appliedCents,
                  changeCents: 0,
                }),
              }),
            });
            return false;
          }
        }

        if (
          this.canCancelPreparedCashRepayment() &&
          workflow.inspectCancellablePreparedCashRepayment
        ) {
          try {
            const prepared =
              await workflow.inspectCancellablePreparedCashRepayment();
            if (prepared) {
              const appliedCents = prepared.amountCents;
              this.cashOperationHash = prepared.operationHash;
              this.cashPath = prepared.path ?? "recovery";
              this.patch({
                phase: "recovery-required",
                runtimeErrorCode: "PAYMENT_RECOVERY_FAILED",
                allowedActions: allowedActions({
                  changeProvider: false,
                  cancel: true,
                }),
                total: aud(appliedCents),
                remaining: ZERO_AUD,
                tenders: Object.freeze([
                  Object.freeze({
                    tenderGuid: this.options.createTenderId(),
                    method: "cash",
                    amount: aud(appliedCents),
                    reversible: false,
                    provider: null,
                  }),
                ]),
                checkout: Object.freeze({
                  ...this.state.checkout,
                  flow: "installment-repayment",
                  canConfirm: false,
                  fullInstallmentConfirmationRequired: false,
                  cashRepaymentStatus: "idle",
                  cash: Object.freeze({
                    tenderedCents: appliedCents,
                    appliedCents,
                    changeCents: 0,
                  }),
                }),
              });
              return false;
            }
          } catch {
            // 主管取消探测失败时保留恢复阻断，不伪造可取消事实。
          }
        }
        this.patch({
          phase: "recovery-required",
          runtimeErrorCode:
            error instanceof InstallmentWorkflowError &&
            error.code === "online-required"
              ? "ONLINE_REQUIRED"
              : "PAYMENT_RECOVERY_FAILED",
          allowedActions: allowedActions({ recover: true }),
          checkout: Object.freeze({
            ...this.state.checkout,
            canConfirm: false,
            cashRepaymentStatus: "idle",
          }),
        });
        return false;
      }
    });
  }

  private confirmPreparedCashRepayment(): Promise<boolean> {
    return this.runExclusive(async () => {
      this.cashConfirmationStartedAt = this.monotonicNowMilliseconds();
      const workflow = this.options.workflow;
      try {
        if (!workflow.confirmPreparedCashRepayment) {
          throw new InstallmentWorkflowError(
            "cash-confirmation-required",
            "Cash confirmation is unavailable.",
          );
        }
        this.patch({
          phase: "cash-confirming",
          checkout: Object.freeze({
            ...this.state.checkout,
            cashRepaymentStatus: "confirming",
          }),
        });
        const details = await workflow.confirmPreparedCashRepayment();
        const appliedCents = this.state.tenders[0]?.amount.cents ?? 0;
        this.patch({
          phase: "success",
          orderGuid: details.installmentGuid,
          total: aud(appliedCents + Math.max(0, details.balanceCents)),
          remaining: aud(Math.max(0, details.balanceCents)),
          allowedActions: allowedActions({}),
          checkout: Object.freeze({
            ...this.state.checkout,
            canConfirm: false,
            cashRepaymentStatus: "idle",
          }),
        });
        return true;
      } catch (error) {
        // 中文注释：一旦操作员点击“已收现金”，任何后续失败都只能恢复原
        // operation，绝不能再次展示收款确认按钮，避免人工重复收现。
        this.patch({
          phase: "recovery-required",
          runtimeErrorCode:
            error instanceof InstallmentWorkflowError &&
            error.code === "online-required"
              ? "ONLINE_REQUIRED"
              : "PAYMENT_CHECKOUT_FAILED",
          allowedActions: allowedActions({ recover: true }),
          checkout: Object.freeze({
            ...this.state.checkout,
            canConfirm: false,
            cashRepaymentStatus: "idle",
          }),
        });
        return false;
      }
    });
  }

  public cancel(): Promise<boolean> {
    const workflow = this.options.workflow;
    const preparedCashTender =
      this.state.checkout.flow === "installment-repayment" &&
      this.state.tenders.length === 1 &&
      this.state.tenders[0]?.method === "cash" &&
      this.state.tenders[0].reversible === false;
    if (
      !preparedCashTender ||
      !this.state.allowedActions.cancel ||
      !workflow.cancelPreparedCashRepayment
    ) {
      return Promise.resolve(false);
    }
    return this.runExclusive(async () => {
      try {
        await workflow.cancelPreparedCashRepayment!();
        const canRestart =
          this.options.entry?.kind === "installment-repayment";
        this.cashOperationHash = null;
        this.cashConfirmationStartedAt = null;
        this.cashSuccessRecordedOperationHash = null;
        this.patch({
          phase: canRestart ? "ready" : "cancelled",
          runtimeErrorCode: null,
          fieldIssue: null,
          orderGuid: null,
          remaining: aud(this.state.total.cents),
          tenders: Object.freeze([]),
          allowedActions: canRestart
            ? allowedActions({
                start: true,
                addCash: this.has(PAYMENT_PERMISSION.takeCash),
              })
            : allowedActions({}),
          checkout: Object.freeze({
            ...this.state.checkout,
            canConfirm: false,
            fullInstallmentConfirmationRequired: false,
            cashRepaymentStatus: "idle",
            cash: Object.freeze({
              tenderedCents: 0,
              appliedCents: 0,
              changeCents: 0,
            }),
          }),
        });
        return true;
      } catch {
        // 取消结果不明确时保留原现金 fence，只允许用同一 operation 重试
        // cancel；runtime 会先 GET 并对已 Released 结果完成本地幂等收口。
        this.patch({
          phase: "recovery-required",
          runtimeErrorCode: "INSTALLMENT_CASH_CANCELLATION_FAILED",
          allowedActions: allowedActions({
            changeProvider: false,
            cancel: true,
          }),
          checkout: Object.freeze({
            ...this.state.checkout,
            canConfirm: false,
            fullInstallmentConfirmationRequired: false,
            cashRepaymentStatus: "idle",
          }),
        });
        return false;
      }
    });
  }

  public removeTender(tenderGuid: string): Promise<boolean> {
    const tender = this.state.tenders[0];
    if (
      !tender ||
      tender.tenderGuid !== tenderGuid ||
      !tender.reversible ||
      this.state.busy
    ) {
      return Promise.resolve(false);
    }
    this.patch({
      tenders: Object.freeze([]),
      remaining: aud(this.state.total.cents),
      allowedActions: allowedActions({
        start: true,
        addCash: this.has(PAYMENT_PERMISSION.takeCash),
      }),
      checkout: Object.freeze({
        ...this.state.checkout,
        canConfirm: false,
        fullInstallmentConfirmationRequired: false,
        cash: Object.freeze({
          tenderedCents: 0,
          appliedCents: 0,
          changeCents: 0,
        }),
      }),
    });
    return Promise.resolve(true);
  }

  public sendLinklyKey(_key: LinklySafeOperatorKey): Promise<boolean> {
    return Promise.resolve(false);
  }

  public markLinklyReceiptPrinted(): Promise<boolean> {
    return Promise.resolve(false);
  }

  public acknowledgeLinkly(): Promise<boolean> {
    return Promise.resolve(false);
  }

  public openInstallmentCustomerEditor(): void {
    const customer = this.state.checkout.installmentCustomer;
    if (!customer?.editable || this.state.busy) return;
    this.patchCustomer({ editorOpen: true });
  }

  public setInstallmentCustomerDraftName(value: string): void {
    this.patchCustomer({ draftName: value.slice(0, 256) });
  }

  public setInstallmentCustomerDraftPhone(value: string): void {
    this.patchCustomer({ draftPhone: value.slice(0, 128) });
  }

  public saveInstallmentCustomer(): void {
    const customer = this.state.checkout.installmentCustomer;
    if (!customer?.editable) return;
    const name = customer.draftName.trim();
    const phone = customer.draftPhone.trim();
    if (!name || !phone) {
      this.patch({ fieldIssue: "installment-customer-required" });
      return;
    }
    this.patchCustomer({ name, phone, editorOpen: false });
    this.patch({ fieldIssue: null });
  }

  public cancelInstallmentCustomerEditor(): void {
    const customer = this.state.checkout.installmentCustomer;
    if (!customer?.editable) return;
    this.patchCustomer({
      draftName: customer.name,
      draftPhone: customer.phone,
      editorOpen: false,
    });
  }

  private initializeForbidden(
    providers: readonly PaymentProviderAvailability[],
  ): false {
    this.patch({
      initialized: true,
      phase: "ready",
      providers,
      fieldIssue: "checkout-unavailable",
      allowedActions: allowedActions({}),
    });
    return false;
  }

  private prepareCashRepayment(tenderedCents: number): Promise<boolean> {
    const entry = this.options.entry;
    if (!entry || entry.kind !== "installment-repayment") {
      return Promise.resolve(false);
    }
    return this.runExclusive(async () => {
      const workflow = this.options.workflow;
      if (!workflow.prepareCashRepayment) {
        this.patch({
          phase: "recovery-required",
          runtimeErrorCode: "INSTALLMENT_CASH_CONFIRMATION_REQUIRED",
        });
        return false;
      }
      this.patch({ phase: "submitting", allowedActions: allowedActions({}) });
      try {
        const prepared = await workflow.prepareCashRepayment({
          installmentGuid: entry.installmentGuid,
          amountCents: Math.min(tenderedCents, this.state.remaining.cents),
          method: "cash",
          voucherReference: null,
          voucherReservationToken: null,
          cashTenderedCents: tenderedCents,
        });
        this.cashOperationHash = prepared.operationHash;
        this.cashPath = prepared.path ?? "recovery";
        const appliedCents = Math.min(prepared.amountCents, this.state.remaining.cents);
        this.patch({
          phase: "cash-collection-ready",
          tenders: Object.freeze([
            Object.freeze({
              tenderGuid: this.options.createTenderId(),
              method: "cash",
              amount: aud(appliedCents),
              reversible: false,
              provider: null,
            }),
          ]),
          remaining: aud(Math.max(0, this.state.total.cents - appliedCents)),
          allowedActions: allowedActions({
            cancel: this.canCancelPreparedCashRepayment(),
          }),
          checkout: Object.freeze({
            ...this.state.checkout,
            canConfirm: true,
            cashRepaymentStatus: "ready",
            cash: Object.freeze({
              tenderedCents,
              appliedCents,
              changeCents: Math.max(0, tenderedCents - appliedCents),
            }),
          }),
          fieldIssue: null,
        });
        return true;
      } catch (error) {
        const runtimeErrorCode =
          error instanceof InstallmentWorkflowError &&
          error.code === "online-required"
            ? "ONLINE_REQUIRED"
            : "PAYMENT_CHECKOUT_FAILED";
        let recoveryRequired = true;
        try {
          recoveryRequired =
            (await workflow.hasRecoveryRequired?.()) ?? true;
        } catch {
          // 本地耐久事实无法核实时必须 fail closed，禁止开始新操作。
        }
        if (!recoveryRequired) {
          this.patch({
            phase: "ready",
            runtimeErrorCode,
            allowedActions: allowedActions({
              start: true,
              addCash: this.has(PAYMENT_PERMISSION.takeCash),
            }),
            checkout: Object.freeze({
              ...this.state.checkout,
              canConfirm: false,
              cashRepaymentStatus: "idle",
            }),
          });
          return false;
        }
        this.patch({
          phase: "recovery-required",
          runtimeErrorCode,
          allowedActions: allowedActions({ recover: true }),
        });
        return false;
      }
    });
  }

  public recordSuccessRendered(): void {
    const startedAt = this.cashConfirmationStartedAt;
    const operationHash = this.cashOperationHash;
    if (
      this.state.phase !== "success" ||
      startedAt === null ||
      operationHash === null ||
      this.cashSuccessRecordedOperationHash === operationHash
    ) {
      return;
    }
    // 先标记再写旁路指标，确保重复 render 或指标异常都不会重复上报。
    this.cashSuccessRecordedOperationHash = operationHash;
    this.cashConfirmationStartedAt = null;
    this.recordPresenterSuccess(startedAt);
  }

  private monotonicNowMilliseconds(): number {
    try {
      const injected = this.options.monotonicNowMilliseconds?.();
      if (injected !== undefined && Number.isFinite(injected)) return injected;
    } catch {
      // 性能时钟是旁路；原生 uptime 不可用时不能中断现金确认。
    }
    const performanceNow = globalThis.performance?.now();
    return performanceNow !== undefined && Number.isFinite(performanceNow)
      ? performanceNow
      : Date.now();
  }

  private recordPresenterSuccess(startedAt: number): void {
    try {
      const result = this.options.performanceRecorder?.record({
        name: "presenter-success",
        elapsedMs: Math.max(0, this.monotonicNowMilliseconds() - startedAt),
        operationHash: this.cashOperationHash ?? "sha256:unknown",
        path: this.cashPath,
        outcome: "success",
      });
      if (result && typeof (result as Promise<void>).catch === "function") {
        void (result as Promise<void>).catch(() => undefined);
      }
    } catch {
      // 指标失败不得改变支付结果。
    }
  }

  private async loadProviders(): Promise<readonly PaymentProviderAvailability[]> {
    if (!this.options.workflow.listPaymentProviderAvailability) {
      return EMPTY_PROVIDERS;
    }
    try {
      return Object.freeze(
        (await this.options.workflow.listPaymentProviderAvailability()).map(
          (entry) =>
            this.providerPermission(entry.provider)
              ? Object.freeze({ ...entry })
              : Object.freeze({
                  provider: entry.provider,
                  available: false,
                  blocker: "PAYMENT_PROVIDER_UNKNOWN" as const,
                }),
        ),
      );
    } catch {
      return EMPTY_PROVIDERS;
    }
  }

  private firstMethod(
    providers: readonly PaymentProviderAvailability[],
  ): PaymentUiMethod | null {
    if (this.has(PAYMENT_PERMISSION.takeCash)) return "cash";
    for (const provider of providers) {
      if (provider.available && this.providerPermission(provider.provider)) {
        return provider.provider;
      }
    }
    return null;
  }

  private methodAvailable(method: PaymentUiMethod): boolean {
    if (
      this.destroyed ||
      this.state.busy ||
      !this.state.allowedActions.start
    ) {
      return false;
    }
    if (method === "cash") return this.has(PAYMENT_PERMISSION.takeCash);
    return (
      this.providerPermission(method) &&
      this.state.providers.some(
        (entry) => entry.provider === method && entry.available,
      )
    );
  }

  private providerPermission(provider: PaymentProvider): boolean {
    return this.has(
      provider === "voucher"
        ? PAYMENT_PERMISSION.takeVoucher
        : PAYMENT_PERMISSION.takeCard,
    );
  }

  private canCreate(): boolean {
    return (
      this.has(INSTALLMENTS_CREATE_PERMISSION) &&
      this.has(PAYMENT_PERMISSION.view) &&
      this.has(PAYMENT_PERMISSION.confirm)
    );
  }

  private canRepay(): boolean {
    return (
      this.has(INSTALLMENTS_ADD_REPAYMENT_PERMISSION) &&
      this.has(PAYMENT_PERMISSION.view) &&
      this.has(PAYMENT_PERMISSION.confirm)
    );
  }

  private has(permission: string): boolean {
    return this.granted.has(permission);
  }

  private canCancelPreparedCashRepayment(): boolean {
    return (
      this.has(INSTALLMENTS_CANCEL_PERMISSION) &&
      Boolean(this.options.workflow.cancelPreparedCashRepayment)
    );
  }

  private clearVoucher(): void {
    this.voucherReference = "";
    this.patch({
      voucherCaptured: false,
      sensitiveInputRevision: this.state.sensitiveInputRevision + 1,
    });
  }

  private patchCustomer(
    patch: Partial<NonNullable<PaymentCheckoutPresentation["installmentCustomer"]>>,
  ): void {
    const customer = this.state.checkout.installmentCustomer;
    if (!customer) return;
    this.patch({
      checkout: Object.freeze({
        ...this.state.checkout,
        installmentCustomer: Object.freeze({ ...customer, ...patch }),
      }),
    });
  }

  private runExclusive(
    operation: () => Promise<boolean>,
  ): Promise<boolean> {
    if (this.destroyed) return Promise.resolve(false);
    if (this.actionInFlight) return Promise.resolve(false);
    this.patch({ busy: true });
    const pending = Promise.resolve()
      .then(operation)
      .catch(() => {
        this.patch({ runtimeErrorCode: "PAYMENT_CHECKOUT_FAILED" });
        return false;
      })
      .finally(() => {
        if (this.actionInFlight === pending) {
          this.actionInFlight = null;
          this.patch({ busy: false });
        }
      });
    this.actionInFlight = pending;
    return pending;
  }

  private patch(patch: Partial<PaymentPresenterState>): void {
    if (this.destroyed) return;
    this.state = { ...this.state, ...patch };
    for (const listener of this.listeners) listener();
  }
}

function initialState(
  entry: InstallmentCheckoutEntry | null,
  cashAvailable: boolean,
): PaymentPresenterState {
  return {
    phase: "loading",
    busy: false,
    initialized: false,
    providers: EMPTY_PROVIDERS,
    cashAvailable,
    selectedMethod: null,
    amountText: "",
    voucherCaptured: false,
    sensitiveInputRevision: 0,
    fieldIssue: null,
    runtimeErrorCode: null,
    orderGuid: null,
    total: ZERO_AUD,
    remaining: ZERO_AUD,
    tenders: Object.freeze([]),
    attemptId: null,
    // 分期没有 PaymentAttempt 窗口身份，但仍必须履行统一公开状态契约。
    attemptCreatedAtIso: null,
    provider: null,
    runtimeStatus: null,
    allowedActions: allowedActions({}),
    tenderReversalRecovery: null,
    checkout: checkoutPresentation(
      entry?.kind ?? "installment-recovery",
    ),
    linkly: Object.freeze({
      status: null,
      errorCode: null,
      allowedKeys: Object.freeze([]),
    }),
  };
}

function checkoutPresentation(
  flow: PaymentCheckoutPresentation["flow"],
): PaymentCheckoutPresentation {
  return Object.freeze({
    flow,
    lines: Object.freeze([]),
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

/**
 * 续付提交失败时支付页展示的错误码。占用中与通道未放开都发生在任何 provider 调用之前、
 * 本地 action 已终结，明确告诉收银员「本次未收款」；其余失败保持原来的通用提示。
 */
function repaymentSubmitFailureCode(
  error: unknown,
):
  | "ONLINE_REQUIRED"
  | "INSTALLMENT_REPAYMENT_BUSY"
  | "INSTALLMENT_REPAYMENT_METHOD_UNSUPPORTED"
  | "PAYMENT_CHECKOUT_FAILED" {
  if (!(error instanceof InstallmentWorkflowError)) {
    return "PAYMENT_CHECKOUT_FAILED";
  }
  switch (error.code) {
    case "online-required":
      return "ONLINE_REQUIRED";
    case "claim-busy":
      return "INSTALLMENT_REPAYMENT_BUSY";
    case "payment-method-unsupported":
      return "INSTALLMENT_REPAYMENT_METHOD_UNSUPPORTED";
    default:
      return "PAYMENT_CHECKOUT_FAILED";
  }
}

function customerPresentation(
  editable: boolean,
): NonNullable<PaymentCheckoutPresentation["installmentCustomer"]> {
  return Object.freeze({
    name: "",
    phone: "",
    editable,
    editorOpen: false,
    draftName: "",
    draftPhone: "",
    installmentNumber: null,
  });
}

function allowedActions(
  input: Partial<PaymentPresenterState["allowedActions"]>,
): PaymentPresenterState["allowedActions"] {
  return Object.freeze({
    start: false,
    changeProvider: true,
    recover: false,
    cancel: false,
    addCash: false,
    removeTender: false,
    ...input,
  });
}

function unavailable(
  provider: PaymentProvider,
): PaymentProviderAvailability {
  return Object.freeze({
    provider,
    available: false,
    blocker: "PAYMENT_PROVIDER_UNKNOWN",
  });
}

function aud(cents: number): Money {
  return Object.freeze({ currency: "AUD", cents });
}

function centsText(cents: number): string {
  return (cents / 100).toFixed(2);
}
