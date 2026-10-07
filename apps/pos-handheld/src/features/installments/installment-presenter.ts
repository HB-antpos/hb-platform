
import { canCancelInstallmentWithRefund } from "@hb/pos-domain/core/contracts/installments";
import {
  hasInstallmentReprintPermission,
  resolveInstallmentsAccess,
  type InstallmentsAccess,
} from "@hb/pos-domain/features/installments/installment-authorization";
import { isValidInstallmentDateFilter } from "./installment-date-filter";
import type {
  InstallmentCancelRefundMode,
  InstallmentCardProvider,
  InstallmentDateFilter,
  InstallmentDetails,
  InstallmentDeviceScope,
  InstallmentPaymentMethod,
  InstallmentRepaymentCapabilities,
  InstallmentCashRepaymentPreparation,
} from "./installment-models";

import type { InstallmentStatus, InstallmentSummary } from "@/core/contracts";
import type { PaymentProviderAvailability } from "@/features/payments/runtime/payment-provider-registry";

export const INSTALLMENT_MINIMUM_TOTAL_CENTS = 5_000;
export const INSTALLMENT_MINIMUM_DOWN_PAYMENT_CENTS = 2_000;
const INSTALLMENT_HISTORY_PAGE_SIZE = 50;
const INSTALLMENT_HISTORY_REQUEST_SIZE = 51;
const DEFAULT_DATE_FILTER = Object.freeze({
  preset: "all",
  fromDate: null,
  toDate: null,
} satisfies InstallmentDateFilter);

export type InstallmentCreateDraftLine = Readonly<{
  lineKey: string;
  displayName: string;
  quantity: string;
  actualAmountCents: number;
}>;

export type InstallmentCreateDraft = Readonly<{
  revision: number;
  totalCents: number;
  lines: readonly InstallmentCreateDraftLine[];
}>;

export interface InstallmentCreateDraftPort {
  getSnapshot(): InstallmentCreateDraft | null;
  subscribe(listener: () => void): () => void;
}

export interface InstallmentReprintPort {
  canReprint(details: InstallmentDetails): boolean;
  reprintExistingInstallment(installmentGuid: string): Promise<void>;
}

export type InstallmentWorkflowCreateInput = Readonly<{
  draftRevision: number;
  customerName: string;
  customerPhone: string;
  note: string | null;
  downPaymentCents: number;
  method: InstallmentPaymentMethod;
  voucherReference: string | null;
  voucherReservationToken: null;
  /** 旧管理页调用可省略；统一支付页的银行卡必须显式冻结 provider。 */
  cardProvider?: InstallmentCardProvider;
  /** 现金可超付；服务端分期金额仍只接收 downPaymentCents。 */
  cashTenderedCents?: number;
}>;

export type InstallmentWorkflowRepaymentInput = Readonly<{
  installmentGuid: string;
  amountCents: number;
  method: InstallmentPaymentMethod;
  voucherReference: string | null;
  voucherReservationToken: null;
  cardProvider?: InstallmentCardProvider;
  cashTenderedCents?: number;
}>;

/**
 * 组合根实现此 Port，并在每次写操作时重新复核在线状态、设备/收银员 lease、
 * 活动购物车 revision、支付 attempt 与 Unknown 恢复；Presenter 的检查只负责 UX。
 */
export interface InstallmentWorkflowPort {
  /**
   * 只读本地耐久 action；用于区分可安全重试的离线失败与必须恢复的支付事实。
   * 旧测试/管理页实现可省略，调用方会按 fail-closed 处理。
   */
  hasRecoveryRequired?(): Promise<boolean>;
  listPaymentProviderAvailability?(): Promise<
    readonly PaymentProviderAvailability[]
  >;
  /** 旧测试/非生产实现可省略；Presenter 会按未支持失败关闭。 */
  getRepaymentCapabilities?(): Promise<InstallmentRepaymentCapabilities>;
  list(input: Readonly<{
    dateFilter: InstallmentDateFilter;
    deviceScope: InstallmentDeviceScope;
    keyword: string | null;
    online: boolean;
    skip: number;
    status: InstallmentStatus | null;
    take: 51;
  }>): Promise<readonly InstallmentSummary[]>;
  getDetails(input: Readonly<{
    installmentGuid: string;
    online: boolean;
  }>): Promise<InstallmentDetails | null>;
  recoverBlocking(): Promise<InstallmentDetails>;
  create(input: InstallmentWorkflowCreateInput): Promise<InstallmentDetails>;
  addRepayment(
    input: InstallmentWorkflowRepaymentInput,
  ): Promise<InstallmentDetails>;
  prepareCashRepayment?(
    input: InstallmentWorkflowRepaymentInput,
  ): Promise<InstallmentCashRepaymentPreparation>;
  /** 仅恢复原设备已锁定且仍明确为 Prepared 的现金 operation。 */
  inspectPreparedCashRepayment?(): Promise<
    InstallmentCashRepaymentPreparation | null
  >;
  /** 只读确认原设备现金 operation 是否仍可由主管安全取消。 */
  inspectCancellablePreparedCashRepayment?(): Promise<
    InstallmentCashRepaymentPreparation | null
  >;
  confirmPreparedCashRepayment?(): Promise<InstallmentDetails>;
  /** 原设备主管明确确认尚未收现后，安全释放已锁定的现金续付。 */
  cancelPreparedCashRepayment?(): Promise<void>;
  cancelWithRefund(input: Readonly<{
    installmentGuid: string;
    reason: string | null;
    /** 旧实现可省略，按原路退处理。 */
    refundMode?: InstallmentCancelRefundMode;
  }>): Promise<InstallmentDetails>;
  void(input: Readonly<{
    installmentGuid: string;
    reason: string;
  }>): Promise<InstallmentDetails>;
  confirmPickup(input: Readonly<{
    installmentGuid: string;
    note: string | null;
  }>): Promise<InstallmentDetails>;
}

export type InstallmentWorkflowErrorCode =
  | "authorization-declined"
  | "claim-review-required"
  | "cash-confirmation-required"
  | "conflict"
  | "online-required"
  | "payment-recovery-required"
  /** 原路退不支持所选原付款（刷卡），需改选"全部退代金券"；未发生任何退款。 */
  | "refund-method-unsupported"
  /** 服务端未按所选退款方式建立取消 claim（旧服务端不支持退代金券）；未发生任何退款。 */
  | "refund-mode-unsupported"
  | "service-unavailable";

export class InstallmentWorkflowError extends Error {
  public constructor(
    public readonly code: InstallmentWorkflowErrorCode,
    message: string,
  ) {
    super(message);
    this.name = "InstallmentWorkflowError";
  }
}

export type InstallmentStatusCode =
  | "action-failed"
  | "authorization-declined"
  | "cancel-complete"
  | "cancel-refund-method-unsupported"
  | "cancel-refund-mode-unsupported"
  | "claim-review-required"
  | "conflict"
  | "create-complete"
  | "details-failed"
  | "details-unavailable"
  | "history-failed"
  | "invalid-date-filter"
  | "invalid-create"
  | "invalid-repayment"
  | "online-required"
  | "payment-recovery-required"
  | "permission-required"
  | "pickup-complete"
  | "repayment-complete"
  | "recovery-complete"
  | "service-unavailable"
  | "void-complete";

export type InstallmentPresenterState = Readonly<{
  access: InstallmentsAccess;
  busy: boolean;
  createDownPayment: string;
  createDraft: InstallmentCreateDraft | null;
  createNote: string;
  createPaymentMethod: InstallmentPaymentMethod;
  createVoucherReference: string;
  customerName: string;
  customerPhone: string;
  details: InstallmentDetails | null;
  detailsLoading: boolean;
  deviceScope: InstallmentDeviceScope;
  dateFilter: InstallmentDateFilter;
  hasMore: boolean;
  kind: "idle" | "loading" | "ready" | "unauthorized" | "failed";
  loadingMore: boolean;
  online: boolean;
  orders: readonly InstallmentSummary[];
  pane: "history" | "create";
  pickupNote: string;
  query: string;
  recoveryRequired: boolean;
  reprint: InstallmentReprintState;
  repaymentAmount: string;
  repaymentMethod: InstallmentPaymentMethod;
  repaymentVoucherReference: string;
  selectedGuid: string | null;
  statusCode: InstallmentStatusCode | null;
  statusFilter: InstallmentStatus | null;
  cancelReason: string;
  /** 用户选择的取消退款方式；有刷卡原付款时实际以 capabilities 的强制退代金券为准。 */
  cancelRefundMode: InstallmentCancelRefundMode;
  voidReason: string;
}>;

export type InstallmentReprintState =
  | Readonly<{ kind: "idle" }>
  | Readonly<{ kind: "submitting"; installmentGuid: string }>
  | Readonly<{ kind: "succeeded"; installmentGuid: string }>
  | Readonly<{ kind: "failed"; installmentGuid: string }>
  | Readonly<{ kind: "unavailable" }>;

export type InstallmentPresenterCapabilities = Readonly<{
  reprint: boolean;
  selectedDetailsCancelRefundable: boolean;
  /** 实际将提交的退款方式：原路退不可用时强制为退代金券。 */
  selectedDetailsCancelRefundMode: InstallmentCancelRefundMode;
  /** 原路退是否可用；有已记录的刷卡原付款时服务端拒绝原路退。 */
  selectedDetailsOriginalRouteRefundable: boolean;
  selectedDetailsPickupConfirmable: boolean;
  selectedDetailsRepayable: boolean;
  selectedDetailsVoidable: boolean;
  selectedDetailsWritable: boolean;
}>;

export type InstallmentPresenterOptions = Readonly<{
  createDrafts: InstallmentCreateDraftPort;
  initialOnline: boolean;
  permissions: readonly string[];
  reprintPort?: InstallmentReprintPort | null;
  trustedDeviceCode: string;
  trustedStoreCode: string;
  workflow: InstallmentWorkflowPort;
}>;

export class InstallmentPresenter {
  private readonly listeners = new Set<() => void>();
  private readonly workflow: InstallmentWorkflowPort;
  private readonly unsubscribeDrafts: () => void;
  private state: InstallmentPresenterState;
  private destroyed = false;
  private loadGeneration = 0;
  private detailGeneration = 0;
  private reprintGeneration = 0;
  private nextSkip = 0;
  private actionInFlight: Promise<void> | null = null;
  private reprintInFlight: Promise<void> | null = null;
  private repaymentClaimsSupported = false;
  private crossDeviceRepaymentEnabled = false;
  private crossDeviceCancelRefundEnabled = false;
  private crossDeviceVoidEnabled = false;
  private crossDevicePickupEnabled = false;

  public constructor(private readonly options: InstallmentPresenterOptions) {
    this.workflow = options.workflow;
    const createDraft = readDraft(options.createDrafts);
    this.state = {
      access: resolveInstallmentsAccess(options.permissions),
      busy: false,
      createDownPayment: defaultDownPayment(createDraft),
      createDraft,
      createNote: "",
      createPaymentMethod: "cash",
      createVoucherReference: "",
      customerName: "",
      customerPhone: "",
      details: null,
      detailsLoading: false,
      deviceScope: "store",
      dateFilter: DEFAULT_DATE_FILTER,
      hasMore: false,
      kind: "idle",
      loadingMore: false,
      online: options.initialOnline,
      orders: [],
      pane: "history",
      pickupNote: "",
      query: "",
      recoveryRequired: false,
      reprint: { kind: "idle" },
      repaymentAmount: "",
      repaymentMethod: "cash",
      repaymentVoucherReference: "",
      selectedGuid: null,
      statusCode: null,
      statusFilter: null,
      cancelReason: "",
      cancelRefundMode: "original-route",
      voidReason: "",
    };
    this.unsubscribeDrafts = options.createDrafts.subscribe(() => {
      if (this.destroyed) return;
      const nextDraft = readDraft(options.createDrafts);
      const currentDownPayment = parseAud(this.state.createDownPayment);
      this.patch({
        createDraft: nextDraft,
        createDownPayment:
          currentDownPayment === null ||
          !nextDraft ||
          currentDownPayment > nextDraft.totalCents
            ? defaultDownPayment(nextDraft)
            : this.state.createDownPayment,
      });
    });
  }

  public readonly getState = (): InstallmentPresenterState => this.state;

  public get capabilities(): InstallmentPresenterCapabilities {
    return Object.freeze({
      reprint: this.reprintableDetails() !== null,
      selectedDetailsCancelRefundable:
        this.selectedDetailsCancelRefundable(),
      selectedDetailsCancelRefundMode: this.effectiveCancelRefundMode(),
      selectedDetailsOriginalRouteRefundable:
        this.selectedDetailsOriginalRouteRefundable(),
      selectedDetailsPickupConfirmable:
        this.selectedDetailsPickupConfirmable(),
      selectedDetailsRepayable: this.selectedDetailsRepayable(),
      selectedDetailsVoidable: this.selectedDetailsVoidable(),
      selectedDetailsWritable: this.selectedDetailsWritable(),
    });
  }

  public readonly subscribe = (listener: () => void): (() => void) => {
    if (this.destroyed) return () => undefined;
    this.listeners.add(listener);
    return () => this.listeners.delete(listener);
  };

  public destroy(): void {
    if (this.destroyed) return;
    this.destroyed = true;
    this.loadGeneration += 1;
    this.detailGeneration += 1;
    this.reprintGeneration += 1;
    this.unsubscribeDrafts();
    this.listeners.clear();
  }

  public setOnline(online: boolean): void {
    if (this.destroyed || this.state.online === online) return;
    if (!online) {
      this.repaymentClaimsSupported = false;
      this.crossDeviceRepaymentEnabled = false;
      this.crossDeviceCancelRefundEnabled = false;
      this.crossDeviceVoidEnabled = false;
      this.crossDevicePickupEnabled = false;
      this.loadGeneration += 1;
      this.detailGeneration += 1;
      this.reprintGeneration += 1;
      this.nextSkip = 0;
      this.patch({
        details: null,
        detailsLoading: false,
        hasMore: false,
        kind: "failed",
        loadingMore: false,
        online,
        orders: [],
        reprint: { kind: "idle" },
        selectedGuid: null,
        statusCode: "online-required",
      });
      return;
    }
    this.patch({ online, statusCode: null });
  }

  public setSearchQuery(query: string): void {
    if (this.destroyed) return;
    this.nextSkip = 0;
    this.detailGeneration += 1;
    this.reprintGeneration += 1;
    this.patch({
      details: null,
      detailsLoading: false,
      hasMore: false,
      query: query.slice(0, 120),
      reprint: { kind: "idle" },
      selectedGuid: null,
      statusCode: null,
    });
  }

  public async setStatusFilter(
    status: InstallmentStatus | null,
  ): Promise<void> {
    if (this.destroyed) return;
    this.patch({ statusFilter: status, statusCode: null });
    await this.load();
  }

  public async setDeviceScope(scope: InstallmentDeviceScope): Promise<void> {
    if (this.destroyed) return;
    if (scope !== "store" && scope !== "device") {
      this.patch({ statusCode: "history-failed" });
      return;
    }
    this.patch({ deviceScope: scope, statusCode: null });
    await this.load();
  }

  public async setDateFilter(filter: InstallmentDateFilter): Promise<void> {
    if (this.destroyed) return;
    if (!isValidInstallmentDateFilter(filter)) {
      this.patch({ statusCode: "invalid-date-filter" });
      return;
    }
    this.patch({
      dateFilter: Object.freeze({ ...filter }),
      statusCode: null,
    });
    await this.load();
  }

  public showHistory(): void {
    if (this.destroyed) return;
    this.patch({ pane: "history", statusCode: null });
  }

  public showCreate(): void {
    if (this.destroyed) return;
    if (!this.state.access.canCreate) {
      this.patch({ statusCode: "permission-required" });
      return;
    }
    this.patch({ pane: "create", statusCode: null });
  }

  public setCustomerName(value: string): void {
    this.patchText("customerName", value, 256);
  }

  public setCustomerPhone(value: string): void {
    this.patchText("customerPhone", value, 128);
  }

  public setCreateNote(value: string): void {
    this.patchText("createNote", value, 2_000);
  }

  public setCreateDownPayment(value: string): void {
    this.patchText("createDownPayment", value, 32);
  }

  public setCreatePaymentMethod(method: InstallmentPaymentMethod): void {
    if (this.destroyed) return;
    this.patch({
      createPaymentMethod: method,
      createVoucherReference:
        method === "voucher" ? this.state.createVoucherReference : "",
      statusCode: null,
    });
  }

  public setCreateVoucherReference(value: string): void {
    this.patchText("createVoucherReference", value, 512);
  }

  public setRepaymentAmount(value: string): void {
    this.patchText("repaymentAmount", value, 32);
  }

  public setRepaymentMethod(method: InstallmentPaymentMethod): void {
    if (this.destroyed) return;
    this.patch({
      repaymentMethod: method,
      repaymentVoucherReference:
        method === "voucher"
          ? this.state.repaymentVoucherReference
          : "",
      statusCode: null,
    });
  }

  public setRepaymentVoucherReference(value: string): void {
    this.patchText("repaymentVoucherReference", value, 512);
  }

  public setCancelReason(value: string): void {
    this.patchText("cancelReason", value, 1_000);
  }

  public setCancelRefundMode(mode: InstallmentCancelRefundMode): void {
    if (this.destroyed) return;
    if (mode !== "original-route" && mode !== "voucher") return;
    if (
      mode === "original-route" &&
      !this.selectedDetailsOriginalRouteRefundable()
    ) {
      // 中文注释：刷卡原付款无法原路退回，保持退代金券并提示原因。
      this.patch({
        cancelRefundMode: "voucher",
        statusCode: "cancel-refund-method-unsupported",
      });
      return;
    }
    this.patch({ cancelRefundMode: mode, statusCode: null });
  }

  public setVoidReason(value: string): void {
    this.patchText("voidReason", value, 1_000);
  }

  public setPickupNote(value: string): void {
    this.patchText("pickupNote", value, 1_000);
  }

  public async load(): Promise<void> {
    if (this.destroyed) return;
    if (!this.state.access.canView) {
      this.reprintGeneration += 1;
      this.patch({
        details: null,
        detailsLoading: false,
        hasMore: false,
        kind: "unauthorized",
        loadingMore: false,
        orders: [],
        reprint: { kind: "idle" },
        selectedGuid: null,
        statusCode: "permission-required",
      });
      return;
    }
    const generation = ++this.loadGeneration;
    // 每一轮加载都先失败关闭；只有本轮可信服务端快照成功才开放跨机续付。
    this.repaymentClaimsSupported = false;
    this.crossDeviceRepaymentEnabled = false;
    this.crossDeviceCancelRefundEnabled = false;
    this.crossDeviceVoidEnabled = false;
    this.crossDevicePickupEnabled = false;
    this.detailGeneration += 1;
    this.reprintGeneration += 1;
    this.nextSkip = 0;
    this.patch({
      details: null,
      detailsLoading: false,
      hasMore: false,
      kind: "loading",
      loadingMore: false,
      orders: [],
      reprint: { kind: "idle" },
      selectedGuid: null,
      statusCode: null,
    });
    try {
      const [capabilitiesResult, ordersResult] = await Promise.allSettled([
        this.workflow.getRepaymentCapabilities?.() ??
          Promise.reject(new Error("Repayment capabilities unavailable.")),
        this.workflow.list(this.historyInput(0)),
      ]);
      if (!this.isCurrentLoad(generation)) return;
      if (capabilitiesResult.status === "fulfilled") {
        this.repaymentClaimsSupported =
          capabilitiesResult.value.repaymentClaimsSupported;
        this.crossDeviceRepaymentEnabled =
          capabilitiesResult.value.repaymentClaimsSupported &&
          capabilitiesResult.value.crossDeviceRepaymentEnabled;
        this.crossDeviceCancelRefundEnabled =
          capabilitiesResult.value.cancelClaimsSupported === true &&
          capabilitiesResult.value.crossDeviceCancelRefundEnabled;
        this.crossDeviceVoidEnabled =
          capabilitiesResult.value.crossDeviceVoidEnabled;
        this.crossDevicePickupEnabled =
          capabilitiesResult.value.crossDevicePickupEnabled;
      }
      if (ordersResult.status === "rejected") throw ordersResult.reason;
      const orders = ordersResult.value;
      const page = orders.slice(0, INSTALLMENT_HISTORY_PAGE_SIZE);
      this.nextSkip = INSTALLMENT_HISTORY_PAGE_SIZE;
      this.patch({
        hasMore: orders.length > INSTALLMENT_HISTORY_PAGE_SIZE,
        kind: "ready",
        orders: uniqueSummaries([], page),
      });
    } catch (error) {
      if (!this.isCurrentLoad(generation)) return;
      this.applyHistoryFailure(error, false);
    }
  }

  public async loadMore(): Promise<void> {
    if (
      this.destroyed ||
      !this.state.access.canView ||
      this.state.loadingMore ||
      !this.state.hasMore ||
      this.state.kind !== "ready"
    ) {
      return;
    }
    const generation = this.loadGeneration;
    const skip = this.nextSkip;
    this.patch({ loadingMore: true, statusCode: null });
    try {
      const orders = await this.workflow.list(this.historyInput(skip));
      if (!this.isCurrentLoad(generation)) return;
      const page = orders.slice(0, INSTALLMENT_HISTORY_PAGE_SIZE);
      this.nextSkip = skip + INSTALLMENT_HISTORY_PAGE_SIZE;
      this.patch({
        hasMore: orders.length > INSTALLMENT_HISTORY_PAGE_SIZE,
        loadingMore: false,
        orders: uniqueSummaries(this.state.orders, page),
      });
    } catch (error) {
      if (!this.isCurrentLoad(generation)) return;
      this.applyHistoryFailure(error, true);
    }
  }

  public async select(installmentGuid: string): Promise<void> {
    if (this.destroyed || !this.state.access.canView) return;
    const generation = ++this.detailGeneration;
    this.reprintGeneration += 1;
    this.patch({
      details: null,
      detailsLoading: true,
      reprint: { kind: "idle" },
      selectedGuid: installmentGuid,
      statusCode: null,
    });
    try {
      const details = await this.workflow.getDetails({
        installmentGuid,
        online: this.state.online,
      });
      if (!this.isCurrentDetail(generation, installmentGuid)) return;
      this.patch({
        details,
        detailsLoading: false,
        statusCode: details ? null : "details-unavailable",
      });
    } catch (error) {
      if (!this.isCurrentDetail(generation, installmentGuid)) return;
      const statusCode = workflowFailureCode(error);
      if (
        statusCode === "online-required" ||
        statusCode === "authorization-declined"
      ) {
        this.invalidateOnlineHistory(statusCode);
        return;
      }
      this.patch({
        details: null,
        detailsLoading: false,
        statusCode:
          statusCode === "service-unavailable"
            ? "service-unavailable"
            : "details-failed",
      });
    }
  }

  public async retryDetails(): Promise<void> {
    const installmentGuid = this.state.selectedGuid;
    if (this.destroyed || !installmentGuid) return;
    await this.select(installmentGuid);
  }

  public reprintSelected(): Promise<void> {
    if (this.destroyed) return Promise.resolve();
    if (this.reprintInFlight) return this.reprintInFlight;
    const details = this.reprintableDetails();
    const port = this.options.reprintPort;
    if (!details || !port) {
      this.patch({ reprint: { kind: "unavailable" } });
      return Promise.resolve();
    }

    const installmentGuid = details.installmentGuid;
    const generation = ++this.reprintGeneration;
    this.patch({
      busy: true,
      reprint: { kind: "submitting", installmentGuid },
    });
    let running!: Promise<void>;
    running = (async () => {
      try {
        await port.reprintExistingInstallment(installmentGuid);
        if (!this.isCurrentReprint(generation, installmentGuid)) return;
        this.patch({
          reprint: { kind: "succeeded", installmentGuid },
        });
      } catch {
        if (!this.isCurrentReprint(generation, installmentGuid)) return;
        this.patch({
          reprint: { kind: "failed", installmentGuid },
        });
      } finally {
        if (this.reprintInFlight === running) {
          this.reprintInFlight = null;
          if (!this.destroyed) {
            // 中文注释：重打与分期写动作共用 busy 门禁；打印退出后再统一开放所有写入口。
            this.patch({ busy: false, reprint: this.state.reprint });
          }
        }
      }
    })();
    this.reprintInFlight = running;
    return running;
  }

  public create(): Promise<void> {
    const guard = this.guardWrite(this.state.access.canCreate);
    if (guard) return guard;
    const draft = this.state.createDraft;
    const downPaymentCents = parseAud(this.state.createDownPayment);
    const customerName = requiredTrimmed(this.state.customerName);
    const customerPhone = requiredTrimmed(this.state.customerPhone);
    const voucherReference = optionalTrimmed(
      this.state.createVoucherReference,
    );
    if (
      !draft ||
      draft.lines.length === 0 ||
      draft.totalCents < INSTALLMENT_MINIMUM_TOTAL_CENTS ||
      downPaymentCents === null ||
      downPaymentCents < INSTALLMENT_MINIMUM_DOWN_PAYMENT_CENTS ||
      downPaymentCents > draft.totalCents ||
      !customerName ||
      !customerPhone ||
      (this.state.createPaymentMethod === "voucher" && !voucherReference)
    ) {
      this.patch({ statusCode: "invalid-create" });
      return Promise.resolve();
    }
    const input: InstallmentWorkflowCreateInput = {
      customerName,
      customerPhone,
      downPaymentCents,
      draftRevision: draft.revision,
      method: this.state.createPaymentMethod,
      note: optionalTrimmed(this.state.createNote),
      voucherReference:
        this.state.createPaymentMethod === "voucher"
          ? voucherReference
          : null,
      voucherReservationToken: null,
    };
    // 中文注释：券码只在本次调用闭包中短暂保留；workflow 开始前先从公开状态清除，
    // 即使在线 query/lock 失败也不得恢复到可观察 state。
    this.patch({ createVoucherReference: "" });
    return this.runAction(
      () => this.workflow.create(input),
      "create-complete",
      true,
    );
  }

  public recoverBlocking(): Promise<void> {
    if (this.destroyed) return Promise.resolve();
    if (!this.state.online) {
      this.patch({ statusCode: "online-required" });
      return Promise.resolve();
    }
    return this.runAction(
      () => this.workflow.recoverBlocking(),
      "recovery-complete",
    );
  }

  public addRepayment(): Promise<void> {
    const guard = this.guardSelectedRepayment(
      this.state.access.canAddRepayment,
    );
    if (guard) return guard;
    const details = this.state.details;
    const amountCents = parseAud(this.state.repaymentAmount);
    const voucherReference = optionalTrimmed(
      this.state.repaymentVoucherReference,
    );
    if (
      !details ||
      details.status !== "Active" ||
      details.balanceCents <= 0 ||
      amountCents === null ||
      amountCents <= 0 ||
      amountCents > details.balanceCents ||
      (this.state.repaymentMethod === "voucher" && !voucherReference)
    ) {
      this.patch({ statusCode: "invalid-repayment" });
      return Promise.resolve();
    }
    const input: InstallmentWorkflowRepaymentInput = {
      amountCents,
      installmentGuid: details.installmentGuid,
      method: this.state.repaymentMethod,
      voucherReference:
        this.state.repaymentMethod === "voucher"
          ? voucherReference
          : null,
      voucherReservationToken: null,
    };
    this.patch({ repaymentVoucherReference: "" });
    return this.runAction(
      () => this.workflow.addRepayment(input),
      "repayment-complete",
    );
  }

  public cancelWithRefund(): Promise<void> {
    const guard = this.guardSelectedAction(
      this.state.access.canCancel,
      this.selectedDetailsCancelRefundable(),
    );
    if (guard) return guard;
    const details = this.state.details;
    if (
      !details ||
      !canCancelInstallmentWithRefund(details.status, details.balanceCents)
    ) {
      this.patch({ statusCode: "action-failed" });
      return Promise.resolve();
    }
    const refundMode = this.effectiveCancelRefundMode();
    return this.runAction(
      () =>
        this.workflow.cancelWithRefund({
          installmentGuid: details.installmentGuid,
          reason: optionalTrimmed(this.state.cancelReason),
          refundMode,
        }),
      "cancel-complete",
    );
  }

  public voidSelected(): Promise<void> {
    const guard = this.guardSelectedAction(
      this.state.access.canCancel,
      this.selectedDetailsVoidable(),
    );
    if (guard) return guard;
    const details = this.state.details;
    if (
      !details ||
      details.status !== "Active" ||
      details.balanceCents <= 0
    ) {
      this.patch({ statusCode: "action-failed" });
      return Promise.resolve();
    }
    return this.runAction(
      () =>
        this.workflow.void({
          installmentGuid: details.installmentGuid,
          reason:
            optionalTrimmed(this.state.voidReason) ?? "作废分期单",
        }),
      "void-complete",
    );
  }

  public confirmPickup(): Promise<void> {
    const guard = this.guardSelectedAction(
      this.state.access.canConfirmPickup,
      this.selectedDetailsPickupConfirmable(),
    );
    if (guard) return guard;
    const details = this.state.details;
    if (!details || details.status !== "PaidOff") {
      this.patch({ statusCode: "action-failed" });
      return Promise.resolve();
    }
    return this.runAction(
      () =>
        this.workflow.confirmPickup({
          installmentGuid: details.installmentGuid,
          note: optionalTrimmed(this.state.pickupNote),
        }),
      "pickup-complete",
    );
  }

  private guardWrite(hasPermission: boolean): Promise<void> | null {
    if (this.destroyed) return Promise.resolve();
    if (
      this.reprintInFlight ||
      this.state.reprint.kind === "submitting"
    ) {
      // 中文注释：重打已冻结服务端详情；完成前不得并发改变付款、余额或生命周期状态。
      return this.reprintInFlight ?? Promise.resolve();
    }
    if (!hasPermission) {
      this.patch({ statusCode: "permission-required" });
      return Promise.resolve();
    }
    if (!this.state.online) {
      this.patch({ statusCode: "online-required" });
      return Promise.resolve();
    }
    if (this.state.recoveryRequired) {
      this.patch({ statusCode: "payment-recovery-required" });
      return Promise.resolve();
    }
    if (this.actionInFlight) return this.actionInFlight;
    return null;
  }

  private guardSelectedAction(
    hasPermission: boolean,
    scopeAllowed: boolean,
  ): Promise<void> | null {
    const guard = this.guardWrite(hasPermission);
    if (guard) return guard;
    if (this.state.details && !scopeAllowed) {
      // 中文注释：跨机生命周期动作必须由本轮可信 capability 逐项放行；跨店始终拒绝。
      this.patch({ statusCode: "conflict" });
      return Promise.resolve();
    }
    return null;
  }

  private guardSelectedRepayment(
    hasPermission: boolean,
  ): Promise<void> | null {
    const guard = this.guardWrite(hasPermission);
    if (guard) return guard;
    if (
      this.state.details &&
      !this.selectedDetailsRepaymentScopeAllowed(this.state.details)
    ) {
      // 中文注释：跨机续付只能由可信服务端 capability 放行；跨店或非 Active 始终拒绝。
      this.patch({ statusCode: "conflict" });
      return Promise.resolve();
    }
    return null;
  }

  private runAction(
    operation: () => Promise<InstallmentDetails>,
    successCode: InstallmentStatusCode,
    returnToHistory = false,
  ): Promise<void> {
    if (this.actionInFlight) return this.actionInFlight;
    let running!: Promise<void>;
    running = (async () => {
      this.reprintGeneration += 1;
      this.patch({
        busy: true,
        reprint: { kind: "idle" },
        statusCode: null,
      });
      try {
        const details = await operation();
        if (this.destroyed) return;
        const orders = upsertSummary(this.state.orders, details);
        this.patch({
          busy: false,
          details,
          kind: "ready",
          orders,
          pane: returnToHistory ? "history" : this.state.pane,
          recoveryRequired: false,
          selectedGuid: details.installmentGuid,
          statusCode: successCode,
        });
      } catch (error) {
        if (this.destroyed) return;
        const statusCode = workflowFailureCode(error);
        this.patch({
          busy: false,
          recoveryRequired:
            this.state.recoveryRequired ||
            statusCode === "payment-recovery-required",
          statusCode,
        });
      } finally {
        if (this.actionInFlight === running) {
          this.actionInFlight = null;
        }
      }
    })();
    this.actionInFlight = running;
    return running;
  }

  private patchText<
    K extends
      | "cancelReason"
      | "createDownPayment"
      | "createNote"
      | "createVoucherReference"
      | "customerName"
      | "customerPhone"
      | "pickupNote"
      | "repaymentAmount"
      | "repaymentVoucherReference"
      | "voidReason",
  >(key: K, value: string, maxLength: number): void {
    if (this.destroyed) return;
    this.patch({
      [key]: value.slice(0, maxLength),
      statusCode: null,
    } as Pick<InstallmentPresenterState, K | "statusCode">);
  }

  private isCurrentLoad(generation: number): boolean {
    return !this.destroyed && generation === this.loadGeneration;
  }

  private reprintableDetails(): InstallmentDetails | null {
    const details = this.state.details;
    const port = this.options.reprintPort;
    if (
      this.destroyed ||
      !hasInstallmentReprintPermission(this.options.permissions) ||
      !this.state.online ||
      this.state.busy ||
      this.state.recoveryRequired ||
      this.state.detailsLoading ||
      !details ||
      this.state.selectedGuid !== details.installmentGuid ||
      !this.selectedDetailsWritable() ||
      (this.reprintInFlight !== null &&
        this.state.reprint.kind !== "submitting") ||
      !port
    ) {
      return null;
    }
    try {
      return port.canReprint(details) ? details : null;
    } catch {
      return null;
    }
  }

  private selectedDetailsWritable(): boolean {
    const details = this.state.details;
    return Boolean(
      details &&
        this.selectedDetailsSameStore(details) &&
        details.deviceCode === this.options.trustedDeviceCode,
    );
  }

  private selectedDetailsRepayable(): boolean {
    const details = this.state.details;
    if (
      !details ||
      !this.selectedDetailsSameStore(details) ||
      details.status !== "Active" ||
      details.balanceCents <= 0
    ) {
      return false;
    }
    return this.selectedDetailsRepaymentScopeAllowed(details);
  }

  private selectedDetailsCancelRefundable(): boolean {
    const details = this.state.details;
    return Boolean(
      details &&
        canCancelInstallmentWithRefund(details.status, details.balanceCents) &&
        this.selectedDetailsActionScopeAllowed(
          details,
          this.crossDeviceCancelRefundEnabled,
        ),
    );
  }

  private selectedDetailsOriginalRouteRefundable(): boolean {
    const details = this.state.details;
    // 中文注释：服务端原路退对刷卡原付款一律拒绝（无服务端可核验的卡退款回执）。
    return !details?.payments.some(
      (payment) =>
        payment.status === "Recorded" &&
        payment.amountCents > 0 &&
        payment.method === "card",
    );
  }

  private effectiveCancelRefundMode(): InstallmentCancelRefundMode {
    return this.selectedDetailsOriginalRouteRefundable()
      ? this.state.cancelRefundMode
      : "voucher";
  }

  private selectedDetailsVoidable(): boolean {
    const details = this.state.details;
    return Boolean(
      details &&
        details.status === "Active" &&
        details.balanceCents > 0 &&
        this.selectedDetailsActionScopeAllowed(
          details,
          this.crossDeviceVoidEnabled,
        ),
    );
  }

  private selectedDetailsPickupConfirmable(): boolean {
    const details = this.state.details;
    return Boolean(
      details &&
        details.status === "PaidOff" &&
        this.selectedDetailsActionScopeAllowed(
          details,
          this.crossDevicePickupEnabled,
        ),
    );
  }

  private selectedDetailsActionScopeAllowed(
    details: InstallmentDetails,
    crossDeviceEnabled: boolean,
  ): boolean {
    return (
      this.selectedDetailsSameStore(details) &&
      (details.deviceCode === this.options.trustedDeviceCode ||
        crossDeviceEnabled)
    );
  }

  private selectedDetailsRepaymentScopeAllowed(
    details: InstallmentDetails,
  ): boolean {
    return (
      this.repaymentClaimsSupported &&
      this.selectedDetailsSameStore(details) &&
      (details.deviceCode === this.options.trustedDeviceCode ||
        this.crossDeviceRepaymentEnabled)
    );
  }

  private selectedDetailsSameStore(details: InstallmentDetails): boolean {
    return details.storeCode === this.options.trustedStoreCode;
  }

  private historyInput(skip: number): Parameters<InstallmentWorkflowPort["list"]>[0] {
    return {
      dateFilter: this.state.dateFilter,
      deviceScope: this.state.deviceScope,
      keyword: optionalTrimmed(this.state.query),
      online: this.state.online,
      skip,
      status: this.state.statusFilter,
      take: INSTALLMENT_HISTORY_REQUEST_SIZE,
    };
  }

  private applyHistoryFailure(error: unknown, loadingMore: boolean): void {
    const failureCode = workflowFailureCode(error);
    const recoveryRequired = failureCode === "payment-recovery-required";
    if (
      failureCode === "online-required" ||
      failureCode === "authorization-declined"
    ) {
      this.invalidateOnlineHistory(failureCode);
      return;
    }
    this.patch({
      hasMore: loadingMore ? this.state.hasMore : false,
      kind: loadingMore && this.state.orders.length > 0 ? "ready" : "failed",
      loadingMore: false,
      recoveryRequired: this.state.recoveryRequired || recoveryRequired,
      statusCode: recoveryRequired
        ? "payment-recovery-required"
        : failureCode === "service-unavailable"
          ? "service-unavailable"
          : "history-failed",
    });
  }

  private invalidateOnlineHistory(
    statusCode: "authorization-declined" | "online-required",
  ): void {
    this.detailGeneration += 1;
    this.reprintGeneration += 1;
    this.nextSkip = 0;
    this.patch({
      details: null,
      detailsLoading: false,
      hasMore: false,
      kind: statusCode === "authorization-declined" ? "unauthorized" : "failed",
      loadingMore: false,
      orders: [],
      reprint: { kind: "idle" },
      selectedGuid: null,
      statusCode,
    });
  }

  private isCurrentDetail(
    generation: number,
    installmentGuid: string,
  ): boolean {
    return (
      !this.destroyed &&
      generation === this.detailGeneration &&
      this.state.selectedGuid === installmentGuid
    );
  }

  private isCurrentReprint(
    generation: number,
    installmentGuid: string,
  ): boolean {
    return (
      !this.destroyed &&
      generation === this.reprintGeneration &&
      this.state.selectedGuid === installmentGuid &&
      this.state.details?.installmentGuid === installmentGuid
    );
  }

  private patch(
    patch: Partial<InstallmentPresenterState>,
  ): void {
    if (this.destroyed) return;
    this.state = Object.freeze({ ...this.state, ...patch });
    for (const listener of this.listeners) listener();
  }
}

function readDraft(
  port: InstallmentCreateDraftPort,
): InstallmentCreateDraft | null {
  try {
    const draft = port.getSnapshot();
    if (
      !draft ||
      !Number.isSafeInteger(draft.revision) ||
      draft.revision < 0 ||
      !Number.isSafeInteger(draft.totalCents) ||
      draft.totalCents < 0 ||
      !Array.isArray(draft.lines)
    ) {
      return null;
    }
    const lines = draft.lines.map((line) => {
      if (
        typeof line.lineKey !== "string" ||
        line.lineKey.length === 0 ||
        typeof line.displayName !== "string" ||
        line.displayName.length === 0 ||
        typeof line.quantity !== "string" ||
        line.quantity.length === 0 ||
        !Number.isSafeInteger(line.actualAmountCents) ||
        line.actualAmountCents < 0
      ) {
        throw new Error("Invalid installment draft line.");
      }
      return Object.freeze({ ...line });
    });
    return Object.freeze({
      revision: draft.revision,
      totalCents: draft.totalCents,
      lines: Object.freeze(lines),
    });
  } catch {
    return null;
  }
}

function defaultDownPayment(
  draft: InstallmentCreateDraft | null,
): string {
  if (!draft || draft.totalCents <= 0) return "";
  return formatInputCents(
    Math.min(
      draft.totalCents,
      INSTALLMENT_MINIMUM_DOWN_PAYMENT_CENTS,
    ),
  );
}

function parseAud(value: string): number | null {
  const normalized = value.trim();
  if (!/^(?:0|[1-9]\d*)(?:\.\d{1,2})?$/u.test(normalized)) {
    return null;
  }
  const [dollars, decimals = ""] = normalized.split(".");
  const cents = Number(dollars) * 100 + Number(decimals.padEnd(2, "0"));
  return Number.isSafeInteger(cents) ? cents : null;
}

function formatInputCents(cents: number): string {
  return `${Math.floor(cents / 100)}.${String(cents % 100).padStart(2, "0")}`;
}

function optionalTrimmed(value: string): string | null {
  const normalized = value.trim();
  return normalized.length === 0 ? null : normalized;
}

function requiredTrimmed(value: string): string | null {
  return optionalTrimmed(value);
}

function workflowFailureCode(error: unknown): InstallmentStatusCode {
  if (error instanceof InstallmentWorkflowError) {
    if (error.code === "authorization-declined") {
      return "authorization-declined";
    }
    if (error.code === "conflict") return "conflict";
    if (error.code === "claim-review-required") {
      return "claim-review-required";
    }
    if (error.code === "online-required") return "online-required";
    if (error.code === "payment-recovery-required") {
      return "payment-recovery-required";
    }
    if (error.code === "refund-method-unsupported") {
      return "cancel-refund-method-unsupported";
    }
    if (error.code === "refund-mode-unsupported") {
      return "cancel-refund-mode-unsupported";
    }
    return "service-unavailable";
  }
  return "action-failed";
}

function upsertSummary(
  orders: readonly InstallmentSummary[],
  details: InstallmentDetails,
): readonly InstallmentSummary[] {
  const summary = summaryFromDetails(details);
  const index = orders.findIndex(
    (order) => order.installmentGuid === details.installmentGuid,
  );
  if (index < 0) return Object.freeze([summary, ...orders]);
  const next = [...orders];
  next[index] = summary;
  return Object.freeze(next);
}

function uniqueSummaries(
  existing: readonly InstallmentSummary[],
  incoming: readonly InstallmentSummary[],
): readonly InstallmentSummary[] {
  const seen = new Set(existing.map((order) => order.installmentGuid));
  const next = [...existing];
  for (const order of incoming) {
    if (seen.has(order.installmentGuid)) continue;
    seen.add(order.installmentGuid);
    next.push(order);
  }
  return Object.freeze(next);
}

function summaryFromDetails(
  details: InstallmentDetails,
): InstallmentSummary {
  return Object.freeze({
    installmentGuid: details.installmentGuid,
    installmentNumber: details.installmentNumber,
    storeCode: details.storeCode,
    deviceCode: details.deviceCode,
    cashierName: details.cashierName,
    customerName: details.customerName,
    customerPhone: details.customerPhone,
    createdAtIso: details.createdAtIso,
    totalCents: details.totalCents,
    downPaymentCents: details.downPaymentCents,
    paidCents: details.paidCents,
    balanceCents: details.balanceCents,
    status: details.status,
    updatedAtIso: details.updatedAtIso,
  });
}
