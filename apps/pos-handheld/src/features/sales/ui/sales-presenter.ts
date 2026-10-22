import {
  PendingScanChoiceQueue,
  type PendingScanChoice,
} from "@hb/pos-domain/features/sales/scan/pending-scan-choice-queue";

import { HbposApiError } from "@/core/api/hbpos-api";
import {
  createAud,
  type CartSnapshot,
} from "@/core/contracts";
import type { CashDrawerDisposition } from "@/features/checkout/cash/cash-checkout-service";
import {
  calculateCashSettlement,
  roundCashAmount,
  type MergeCompatibleCartLinesResult,
} from "@/features/sales/domain";
import { scanTiming } from "@/features/sales/runtime/scan-timing";

export const MIN_TOUCH_TARGET = 48;

export type SalesUiPhase =
  | "selling"
  | "verifying-checkout"
  | "cash"
  | "submitting-cash"
  | "success"
  | "locked";

export type SalesSearchStatus = "idle" | "searching" | "ready";

export type SalesErrorCode =
  | "search-required"
  | "search-failed"
  | "product-add-failed"
  | "terminal-recovery-required"
  | "authorization-denied"
  | "cart-update-failed"
  | "invalid-quantity"
  | "invalid-price"
  | "invalid-discount"
  | "empty-cart"
  | "cash-invalid"
  | "cash-insufficient"
  | "cash-failed"
  | "cart-clear-failed"
  | "hold-failed"
  | "lock-failed"
  | "new-transactions-disabled"
  | "runtime-unavailable"
  | "network-unavailable"
  /** 还有一码多商品待选未处理：付款 / 挂单前必须先选定或放弃。 */
  | "lookup-selection-required"
  /** 待选队列已满：这次一码多商品扫码没有被记录。 */
  | "lookup-selection-queue-full";

/**
 * 网络传输失败（断网/服务器未启动/超时）映射为收银页友好错误码；
 * 请求取消与 API 地址配置错误不属于“网络不可用”，不在此列。
 */
export function transportFailureErrorCode(
  error: unknown,
): SalesErrorCode | null {
  if (
    error instanceof HbposApiError &&
    error.kind === "transport" &&
    error.code !== "REQUEST_ABORTED" &&
    error.code !== "UNTRUSTED_API_ORIGIN" &&
    error.code !== "TRANSPORT_UNEXPECTED"
  ) {
    return "network-unavailable";
  }
  return null;
}

export type SalesProductSearchItem = Readonly<{
  productCode: string;
  itemNumber: string | null;
  lookupCode: string;
  displayName: string;
  unitPriceCents: number;
}>;

/**
 * 一码多商品候选只携带展示与身份字段；加购时运行时按查询码 + 商品编码
 * 重新读取本地目录，UI 不能提供或改写价格。
 */
export type SalesLookupCandidate = Readonly<{
  productCode: string;
  itemNumber: string | null;
  barcode: string | null;
  lookupCode: string;
  displayName: string;
  unitPriceCents: number;
  discountRate: number | null;
  priceSource: 0 | 1 | 2 | 3 | 4;
}>;

/** 扫码或精确输入命中多个不同商品时等待收银员选择；不自动加购任何一个。 */
export type SalesLookupSelection = Readonly<{
  selectionId: string;
  lookupCode: string;
  source: "manual" | "hid" | "camera";
  candidates: readonly SalesLookupCandidate[];
}>;

/**
 * 一码多商品待选的生命周期日志（对齐 WPF PosScan pending choice created / resolved /
 * abandoned / rejected）。路由层桥接到中心程序日志；只带查询码末 4 位与长度，不带完整条码。
 */
export type SalesLookupSelectionLogEvent = Readonly<{
  action: "created" | "resolved" | "abandoned" | "rejected";
  /** 队列内编号；rejected 时为 null（未入队）。 */
  choiceId: number | null;
  selectionId: string;
  source: "manual" | "hid" | "camera";
  lookupCodeTail: string;
  lookupCodeLength: number;
  candidateCount: number;
  /** 最多前 5 个候选商品编码，逗号分隔。 */
  candidateProducts: string;
  queueLength: number;
  waitedMs: number | null;
  sameLookupCodeAsPrevious: boolean | null;
  reason: string | null;
  productCode: string | null;
}>;

/** 业务反馈与声音解耦；路由层仅将其映射为本地提示音。 */
export type SalesFeedbackEvent = Readonly<{
  kind:
    | "query-found"
    | "query-empty"
    | "query-error"
    | "added"
    | "incremented"
    | "not-found"
    | "failed-blocked"
    /** 同码远程补查进行中再次扫到：不加购，只提示「正在查询」。 */
    | "lookup-in-progress";
  attemptId?: string;
  source?: "manual" | "hid" | "camera";
  lineId?: string;
  /** HID 时序会话 id；业务和界面状态不得读取。 */
  timingId?: string;
}>;

export type SalesCapabilities = Readonly<{
  catalog: boolean;
  cartEditing: boolean;
  cashCheckout: boolean;
  hold: boolean;
  lock: boolean;
}>;

export type SalesCashCompletion = Readonly<{
  completed: true;
  canClearCart: true;
  orderGuid: string;
  cashDueCents: number;
  changeCents: number;
  postCommit: Readonly<{
    drawerDisposition: CashDrawerDisposition;
  }>;
}>;

export type SalesSuccessState = Readonly<{
  orderGuid: string;
  cashDueCents: number;
  changeCents: number;
  clearCartSignalled: boolean;
  drawerDisposition: CashDrawerDisposition;
}>;

export type SalesPresenterState = Readonly<{
  phase: SalesUiPhase;
  cart: CartSnapshot;
  selectedLineId: string | null;
  revealLineId: string | null;
  revealRevision: number;
  query: string;
  searchStatus: SalesSearchStatus;
  searchResults: readonly SalesProductSearchItem[];
  /** 待选队列队首（最早的一码多商品扫码）；队列为空时为 null。 */
  lookupSelection: SalesLookupSelection | null;
  /** 待选队列长度（含队首）。后续扫码不会顶掉待选，付款 / 挂单前必须先处理完。 */
  lookupSelectionQueueLength: number;
  /** 收银员把待选收起成提示条以便继续扫码；待选仍在队列里，不会消失。 */
  lookupSelectionCollapsed: boolean;
  /** 已点选候选、加购（含主管授权）尚未完成；期间候选弹窗保持打开但不可再选。 */
  lookupSelectionPending: boolean;
  pendingLookupCount: number;
  cashTenderedText: string;
  errorCode: SalesErrorCode | null;
  success: SalesSuccessState | null;
  capabilities: SalesCapabilities;
}>;

export interface SalesCartPort {
  getSnapshot(): CartSnapshot;
  subscribe(listener: () => void): () => void;
  hasMergeCompatibleLines(): boolean;
  mergeCompatibleLines(): Promise<MergeCompatibleCartLinesResult>;
  increaseLine(lineId: string): Promise<void>;
  decreaseLine(lineId: string): Promise<void>;
  removeLine(lineId: string): Promise<void>;
  applyLineDiscountBasisPoints(
    lineId: string,
    basisPoints: number,
  ): Promise<void>;
  setLineQuantity(lineId: string, quantity: number): Promise<void>;
  setLineUnitPriceCents(
    lineId: string,
    unitPriceCents: number,
  ): Promise<void>;
  applyLineDiscountAmountCents(
    lineId: string,
    discountCents: number,
  ): Promise<void>;
  applyLineManualDiscountBasisPoints(
    lineId: string,
    basisPoints: number,
  ): Promise<void>;
  applyOrderDiscountAmountCents(discountCents: number): Promise<void>;
  applyOrderManualDiscountBasisPoints(
    basisPoints: number,
  ): Promise<void>;
  applyOrderQuickDiscountBasisPoints(
    basisPoints: number,
  ): Promise<void>;
  clearCart(): Promise<void>;
  /**
   * 该方法是“事务已经提交，UI 现在可以清空”的单向信号。
   * 实现不得在现金服务返回 committed 之前清空购物车。
   */
  clearAfterCommittedOrder(orderGuid: string): Promise<void>;
}

export interface SalesWorkflowPort {
  searchProducts(query: string): Promise<readonly SalesProductSearchItem[]>;
  addProduct(product: SalesProductSearchItem): Promise<void>;
  addByLookupCode(
    lookupCode: string,
    options?: Readonly<{
      source?: "manual" | "hid" | "camera";
      /** HID 时序会话 id，仅供 scan-timing 打点使用。 */
      timingId?: string;
    }>,
  ): Promise<string | null>;
  subscribeLookupOutcome?(
    listener: (outcome: SalesFeedbackEvent) => void,
  ): () => void;
  /** 精确查码命中多个不同商品时发布候选；该次查码不加购，等待收银员选择。 */
  subscribeLookupSelection?(
    listener: (selection: SalesLookupSelection) => void,
  ): () => void;
  /** 收银员选定候选后按查询码 + 商品编码重新读取本地目录并加购。 */
  addLookupCandidate?(
    input: Readonly<{
      lookupCode: string;
      productCode: string;
      source?: "manual" | "hid" | "camera";
    }>,
  ): Promise<string | null>;
  subscribeScanTarget(listener: (lineId: string) => void): () => void;
  addOpenItem(unitPriceCents: number): Promise<void>;
  getPendingCatalogWorkCount(): number;
  subscribePendingCatalogWork(listener: () => void): () => void;
  settlePendingCatalogWork(
    input: Readonly<{ timeoutMs: number }>,
  ): Promise<Readonly<{ timedOut: boolean }>>;
  disposePendingCatalogWork(): void;
  releasePreparedCheckout(): void;
  completeCash(input: Readonly<{
    checkoutIntentId: string;
    cart: CartSnapshot;
    cashTenderedCents: number | null;
  }>): Promise<SalesCashCompletion>;
  holdCart(cart: CartSnapshot): Promise<void>;
  lockTerminal(): Promise<void>;
}

export type SalesPresenterDependencies = Readonly<{
  cart: SalesCartPort;
  workflow: SalesWorkflowPort;
  capabilities: SalesCapabilities;
  createCheckoutIntentId(): string;
  /** 仅约束空购物车开始新交易；已有购物车、补传与支付恢复不得被拦截。 */
  canStartNewTransaction(): boolean;
  /** 待选等待时长的时钟；测试可注入，默认 Date.now。 */
  now?: () => number;
}>;

export type CashDraft = Readonly<{
  cashDueCents: number;
  cashTenderedCents: number | null;
  normalizedTenderedCents: number | null;
  changeCents: number;
  valid: boolean;
  errorCode: "cash-invalid" | "cash-insufficient" | null;
}>;

export const EMPTY_SALE_CART: CartSnapshot = {
  revision: 0,
  mode: "sale",
  lines: [],
  subtotal: createAud(0),
  discount: createAud(0),
  actualAmount: createAud(0),
};

const DISCONNECTED_CAPABILITIES: SalesCapabilities = {
  catalog: false,
  cartEditing: false,
  cashCheckout: false,
  hold: false,
  lock: false,
};

/**
 * 销售 UI 只与这些 feature-private Port 通信，不直接构造目录、账本或外设服务。
 * 因此组合根未接入时可以诚实禁用交易，而不会产生“演示订单”或伪造成功。
 */
export class SalesPresenter {
  private state: SalesPresenterState;
  private readonly listeners = new Set<() => void>();
  private readonly feedbackListeners = new Set<
    (event: SalesFeedbackEvent) => void
  >();
  private readonly unsubscribeCart: () => void;
  private readonly unsubscribePendingCatalogWork: () => void;
  private readonly unsubscribeScanTarget: () => void;
  private readonly unsubscribeLookupOutcome: () => void;
  private readonly unsubscribeLookupSelection: () => void;
  private readonly lookupSelectionLogListeners = new Set<
    (event: SalesLookupSelectionLogEvent) => void
  >();
  // 一码多商品待选队列（对齐 WPF c685663c5）：先进先出，后续扫码不顶掉，选定或放弃才出队。
  private readonly lookupSelectionQueue: PendingScanChoiceQueue<SalesLookupSelection>;
  private cashIntentId: string | null = null;
  private cashSubmission: Promise<boolean> | null = null;
  private checkoutPreparation: Promise<CartSnapshot | null> | null = null;
  private mergeCompatibilityCart: CartSnapshot | null = null;
  private mergeCompatibilityValue = false;
  private searchGeneration = 0;
  private nextPresenterAttemptId = 0;
  private nextScanTimingId = 0;
  private mergeSelectionInProgress = false;
  private destroyed = false;

  public constructor(private readonly dependencies: SalesPresenterDependencies) {
    this.lookupSelectionQueue = new PendingScanChoiceQueue<SalesLookupSelection>(
      dependencies.now ? { now: dependencies.now } : {},
    );
    const initialCart = dependencies.cart.getSnapshot();
    const initialLineId =
      initialCart.lines[initialCart.lines.length - 1]?.lineId ?? null;
    this.state = {
      phase: "selling",
      cart: initialCart,
      selectedLineId: initialLineId,
      revealLineId: initialLineId,
      revealRevision: 0,
      query: "",
      searchStatus: "idle",
      searchResults: [],
      lookupSelection: null,
      lookupSelectionQueueLength: 0,
      lookupSelectionCollapsed: false,
      lookupSelectionPending: false,
      pendingLookupCount: dependencies.workflow.getPendingCatalogWorkCount(),
      cashTenderedText: "",
      errorCode: null,
      success: null,
      capabilities: dependencies.capabilities,
    };
    this.unsubscribeCart = dependencies.cart.subscribe(() => {
      this.applyCartSnapshot(dependencies.cart.getSnapshot());
    });
    this.unsubscribePendingCatalogWork =
      dependencies.workflow.subscribePendingCatalogWork(() => {
        this.patchState({
          pendingLookupCount:
            dependencies.workflow.getPendingCatalogWorkCount(),
        });
      });
    this.unsubscribeScanTarget =
      dependencies.workflow.subscribeScanTarget((lineId) => {
        if (
          this.state.phase === "selling" &&
          this.state.cart.lines.some((line) => line.lineId === lineId)
        ) {
          this.revealLine(lineId);
        }
      });
    this.unsubscribeLookupOutcome =
      dependencies.workflow.subscribeLookupOutcome?.((outcome) => {
        if (!this.destroyed) this.publishFeedback(outcome);
      }) ?? (() => undefined);
    this.unsubscribeLookupSelection =
      dependencies.workflow.subscribeLookupSelection?.((selection) => {
        this.showLookupSelection(selection);
      }) ?? (() => undefined);
  }

  public readonly getState = (): SalesPresenterState => this.state;

  public readonly subscribe = (listener: () => void): (() => void) => {
    this.listeners.add(listener);
    return () => this.listeners.delete(listener);
  };

  public readonly subscribeFeedback = (
    listener: (event: SalesFeedbackEvent) => void,
  ): (() => void) => {
    this.feedbackListeners.add(listener);
    return () => this.feedbackListeners.delete(listener);
  };

  public readonly subscribeLookupSelectionLog = (
    listener: (event: SalesLookupSelectionLogEvent) => void,
  ): (() => void) => {
    this.lookupSelectionLogListeners.add(listener);
    return () => this.lookupSelectionLogListeners.delete(listener);
  };

  public destroy(): void {
    if (this.destroyed) return;
    // 页面销毁会放弃全部待选：先记日志再拆订阅，运维才能看到「待选商品没进购物车」。
    this.abandonAllLookupSelections("page-destroyed");
    this.destroyed = true;
    this.searchGeneration += 1;
    try {
      // 先 fence 迟到的目录任务，之后才拆除视图订阅，避免销毁窗口注入购物车。
      this.dependencies.workflow.disposePendingCatalogWork();
    } finally {
      this.unsubscribeCart();
      this.unsubscribePendingCatalogWork();
      this.unsubscribeScanTarget();
      this.unsubscribeLookupOutcome();
      this.unsubscribeLookupSelection();
      this.listeners.clear();
      this.feedbackListeners.clear();
      this.lookupSelectionLogListeners.clear();
    }
  }

  public setQuery(query: string): void {
    this.searchGeneration += 1;
    this.patchState({
      query,
      searchStatus: "idle",
      searchResults: [],
      errorCode:
        this.state.errorCode === "search-required" ? null : this.state.errorCode,
    });
  }

  public dismissError(): void {
    this.patchState({ errorCode: null });
  }

  public selectLine(lineId: string): void {
    if (!this.state.cart.lines.some((line) => line.lineId === lineId)) {
      return;
    }
    this.patchState({ selectedLineId: lineId });
  }

  public canMergeCompatibleLines(): boolean {
    if (
      this.state.phase !== "selling" ||
      !this.dependencies.capabilities.cartEditing
    ) {
      return false;
    }
    if (this.mergeCompatibilityCart !== this.state.cart) {
      this.mergeCompatibilityCart = this.state.cart;
      this.mergeCompatibilityValue =
        this.dependencies.cart.hasMergeCompatibleLines();
    }
    return this.mergeCompatibilityValue;
  }

  public searchProducts(): Promise<boolean> {
    const query = this.state.query.trim();
    if (!query) {
      this.patchState({ errorCode: "search-required" });
      return Promise.resolve(false);
    }
    if (!this.dependencies.capabilities.catalog) {
      this.patchState({ errorCode: "runtime-unavailable" });
      this.publishFeedback({ kind: "query-error" });
      return Promise.resolve(false);
    }

    const generation = ++this.searchGeneration;
    this.patchState({
      searchStatus: "searching",
      errorCode: null,
    });
    return this.dependencies.workflow
      .searchProducts(query)
      .then((results) => {
        if (generation !== this.searchGeneration) {
          return false;
        }
        this.patchState({
          searchStatus: "ready",
          searchResults: [...results],
        });
        this.publishFeedback({
          kind: results.length === 0 ? "query-empty" : "query-found",
        });
        return true;
      })
      .catch((error: unknown) => {
        if (generation === this.searchGeneration) {
          this.patchState({
            searchStatus: "idle",
            errorCode:
              transportFailureErrorCode(error) ??
              (hasErrorCode(
                error,
                "SALES_OPERATION_NOT_AUTHORIZED",
              )
                ? "authorization-denied"
                : "search-failed"),
          });
          this.publishFeedback({ kind: "query-error" });
        }
        return false;
      });
  }

  public addLookupCode(): Promise<boolean> {
    const lookupCode = this.state.query.trim();
    if (!this.dependencies.capabilities.catalog) {
      this.patchState({ errorCode: "runtime-unavailable" });
      this.publishBlockedAddAttempt("manual");
      return Promise.resolve(false);
    }
    if (!lookupCode) {
      this.patchState({ errorCode: "search-required" });
      this.publishBlockedAddAttempt("manual");
      return Promise.resolve(false);
    }
    if (!this.canMutateNewTransaction()) {
      this.publishBlockedAddAttempt("manual");
      return Promise.resolve(false);
    }

    // 扫码输入属于高频主路径：先释放输入框，远程校准和本地 miss 回查在后台完成。
    this.searchGeneration += 1;
    this.patchState({
      query: "",
      searchStatus: "idle",
      searchResults: [],
      errorCode: null,
    });
    const revealRevision = this.state.revealRevision;
    return this.dependencies.workflow
      .addByLookupCode(lookupCode)
      .then((lineId) => {
        const cart = this.dependencies.cart.getSnapshot();
        this.applyCartSnapshot(cart);
        if (
          lineId &&
          !(
            this.state.revealRevision > revealRevision &&
            this.state.revealLineId === lineId
          )
        ) {
          this.revealLine(lineId);
        }
        return true;
      })
      .catch((error: unknown) => {
        if (hasErrorCode(error, "NEW_TRANSACTIONS_DISABLED")) {
          this.patchState({ errorCode: "new-transactions-disabled" });
        } else if (hasErrorCode(error, "SALES_OPERATION_NOT_AUTHORIZED")) {
          this.patchState({ errorCode: "authorization-denied" });
        }
        // 未找到、离线和远程超时均保留当前交易，不用噪声错误打断收银。
        return false;
      });
  }

  public addProduct(product: SalesProductSearchItem): Promise<boolean> {
    if (!this.dependencies.capabilities.catalog) {
      this.patchState({ errorCode: "runtime-unavailable" });
      this.publishBlockedAddAttempt("manual");
      return Promise.resolve(false);
    }
    if (!this.canMutateNewTransaction()) {
      this.publishBlockedAddAttempt("manual");
      return Promise.resolve(false);
    }
    return this.runProductMutation(() =>
      this.dependencies.workflow.addProduct(product),
    );
  }

  /** HID/相机不接管触屏草稿，直接走参数化查码入口。 */
  public addScannedLookupCode(
    lookupCode: string,
    source: "hid" | "camera",
  ): Promise<boolean> {
    const timingId =
      source === "hid"
        ? `scan-${(this.nextScanTimingId += 1)}`
        : undefined;
    scanTiming.beginHid(timingId);
    const completeRejectedHid = () => {
      scanTiming.complete(timingId, "failure");
    };
    // 中文注释：后续扫码不再顶掉尚未选择的一码多商品待选（WPF c685663c5），待选留在队列里。
    if (!this.dependencies.capabilities.catalog) {
      this.patchState({ errorCode: "runtime-unavailable" });
      this.publishBlockedAddAttempt(source);
      completeRejectedHid();
      return Promise.resolve(false);
    }
    if (!lookupCode.trim()) {
      this.publishBlockedAddAttempt(source);
      completeRejectedHid();
      return Promise.resolve(false);
    }
    if (!this.canMutateNewTransaction()) {
      this.publishBlockedAddAttempt(source);
      completeRejectedHid();
      return Promise.resolve(false);
    }
    return this.dependencies.workflow
      .addByLookupCode(
        lookupCode,
        timingId === undefined ? { source } : { source, timingId },
      )
      .then(() => true)
      .catch(() => {
        // runtime 会优先发布权威失败；这里只为失效会话等未发布分支兜底。
        completeRejectedHid();
        return false;
      });
  }

  /**
   * 收银员从待选队首的一码多商品候选中选定商品。运行时按查询码 + 商品编码重新读取本地目录，
   * 经与扫码相同的授权、审计与结账围栏加购。与搜索抽屉一致，加购完成前弹窗保持打开
   * （主管授权弹窗叠在其上，避免与关闭动画竞争），期间忽略重复点选。
   * 选定成功才出队并自动轮到下一个；加购失败时条目保留，收银员可改选或放弃（对齐 WPF）。
   */
  public chooseLookupCandidate(productCode: string): Promise<boolean> {
    const entry = this.lookupSelectionQueue.current;
    const selection = entry?.payload ?? null;
    const candidate = selection?.candidates.find(
      (current) => current.productCode === productCode,
    );
    if (
      !entry ||
      !selection ||
      !candidate ||
      this.state.lookupSelectionPending
    ) {
      return Promise.resolve(false);
    }
    const workflow = this.dependencies.workflow;
    if (!this.dependencies.capabilities.catalog || !workflow.addLookupCandidate) {
      // 运行时已断开：待选无法再入车，全部放弃并留日志，避免卡住付款。
      this.abandonAllLookupSelections("runtime-unavailable");
      this.patchState({ errorCode: "runtime-unavailable" });
      this.publishBlockedAddAttempt(selection.source);
      return Promise.resolve(false);
    }
    if (!this.canMutateNewTransaction()) {
      // 条目保留：策略恢复后仍可选定，或由收银员主动放弃。
      this.publishBlockedAddAttempt(selection.source);
      return Promise.resolve(false);
    }
    this.patchState({ lookupSelectionPending: true });
    const revealRevision = this.state.revealRevision;
    return workflow
      .addLookupCandidate({
        lookupCode: candidate.lookupCode,
        productCode: candidate.productCode,
        source: selection.source,
      })
      .then((lineId) => {
        this.resolveLookupSelection(entry, candidate.productCode);
        this.applyCartSnapshot(this.dependencies.cart.getSnapshot());
        if (
          lineId &&
          !(
            this.state.revealRevision > revealRevision &&
            this.state.revealLineId === lineId
          )
        ) {
          this.revealLine(lineId);
        }
        this.patchState({ errorCode: null });
        return true;
      })
      .catch((error: unknown) => {
        this.patchState({
          lookupSelectionPending: false,
          errorCode: productMutationErrorCode(error),
        });
        return false;
      });
  }

  /**
   * 放弃队首待选：这一次扫码不加入任何商品，自动轮到下一个。
   * 界面须先让收银员二次确认（对齐 WPF「放弃需再点一次确认」）。
   */
  public cancelLookupSelection(): void {
    const entry = this.lookupSelectionQueue.current;
    if (!entry || this.state.lookupSelectionPending) return;
    this.removeLookupSelection(entry, "abandoned", "cashier-abandon", null);
  }

  /**
   * 取单等会替换当前购物车的入口先调用：有待选时拒绝并展开待选弹窗，返回 false。
   */
  public requireNoPendingLookupSelections(): boolean {
    return !this.blockForPendingLookupSelections();
  }

  /** 收起待选弹窗，继续扫码；待选保留在队列里，提示条常驻直到处理完。 */
  public collapseLookupSelection(): void {
    if (
      this.lookupSelectionQueue.size === 0 ||
      this.state.lookupSelectionPending ||
      this.state.lookupSelectionCollapsed
    ) {
      return;
    }
    this.patchState({ lookupSelectionCollapsed: true });
  }

  /** 从提示条重新展开待选弹窗。 */
  public expandLookupSelection(): void {
    if (this.lookupSelectionQueue.size === 0) return;
    if (this.state.lookupSelectionCollapsed) {
      this.patchState({ lookupSelectionCollapsed: false });
    }
  }

  public addOpenItem(unitPriceCents: number): Promise<boolean> {
    if (!Number.isSafeInteger(unitPriceCents) || unitPriceCents <= 0) {
      this.patchState({ errorCode: "invalid-price" });
      this.publishBlockedAddAttempt("manual");
      return Promise.resolve(false);
    }
    if (!this.dependencies.capabilities.catalog) {
      this.patchState({ errorCode: "runtime-unavailable" });
      this.publishBlockedAddAttempt("manual");
      return Promise.resolve(false);
    }
    if (!this.canMutateNewTransaction()) {
      this.publishBlockedAddAttempt("manual");
      return Promise.resolve(false);
    }
    return this.runProductMutation(() =>
      this.dependencies.workflow.addOpenItem(unitPriceCents),
    );
  }

  public increaseLine(lineId: string): Promise<boolean> {
    return this.runCartMutation(() =>
      this.dependencies.cart.increaseLine(lineId),
    );
  }

  public decreaseLine(lineId: string): Promise<boolean> {
    return this.runCartMutation(() =>
      this.dependencies.cart.decreaseLine(lineId),
    );
  }

  public removeLine(lineId: string): Promise<boolean> {
    return this.runCartMutation(() =>
      this.dependencies.cart.removeLine(lineId),
    );
  }

  public mergeCompatibleLines(): Promise<boolean> {
    if (!this.canMergeCompatibleLines()) {
      return Promise.resolve(false);
    }
    const selectedLineId = this.state.selectedLineId;
    this.mergeSelectionInProgress = true;
    return this.dependencies.cart
      .mergeCompatibleLines()
      .then((result) => {
        const cart = this.dependencies.cart.getSnapshot();
        this.applyCartSnapshot(cart);
        const mappedSelection = selectedLineId
          ? result.groups.find((group) =>
              group.removedLineIds.includes(selectedLineId),
            )?.keptLineId
          : null;
        const nextSelectedLineId =
          mappedSelection ??
          (selectedLineId &&
          cart.lines.some((line) => line.lineId === selectedLineId)
            ? selectedLineId
            : cart.lines[cart.lines.length - 1]?.lineId ?? null);
        if (nextSelectedLineId !== this.state.selectedLineId) {
          this.revealLine(nextSelectedLineId);
        }
        this.patchState({ errorCode: null });
        return result.removedLineCount > 0;
      })
      .catch((error: unknown) => {
        this.patchState({
          errorCode:
            transportFailureErrorCode(error) ??
            (hasErrorCode(
              error,
              "SALES_OPERATION_NOT_AUTHORIZED",
            )
              ? "authorization-denied"
              : "cart-update-failed"),
        });
        return false;
      })
      .finally(() => {
        this.mergeSelectionInProgress = false;
      });
  }

  public applyLineDiscount(
    lineId: string,
    basisPoints: number,
  ): Promise<boolean> {
    if (
      !Number.isSafeInteger(basisPoints) ||
      basisPoints < 0 ||
      basisPoints > 10_000
    ) {
      this.patchState({ errorCode: "cart-update-failed" });
      return Promise.resolve(false);
    }
    return this.runCartMutation(() =>
      this.dependencies.cart.applyLineDiscountBasisPoints(
        lineId,
        basisPoints,
      ),
    );
  }

  public setLineQuantity(
    lineId: string,
    quantity: number,
  ): Promise<boolean> {
    if (!Number.isSafeInteger(quantity) || quantity <= 0) {
      this.patchState({ errorCode: "invalid-quantity" });
      return Promise.resolve(false);
    }
    return this.runCartMutation(() =>
      this.dependencies.cart.setLineQuantity(lineId, quantity),
    );
  }

  public setLineUnitPriceCents(
    lineId: string,
    unitPriceCents: number,
  ): Promise<boolean> {
    if (!Number.isSafeInteger(unitPriceCents) || unitPriceCents < 0) {
      this.patchState({ errorCode: "invalid-price" });
      return Promise.resolve(false);
    }
    return this.runCartMutation(() =>
      this.dependencies.cart.setLineUnitPriceCents(
        lineId,
        unitPriceCents,
      ),
    );
  }

  public applyLineDiscountAmountCents(
    lineId: string,
    discountCents: number,
  ): Promise<boolean> {
    if (!Number.isSafeInteger(discountCents) || discountCents < 0) {
      this.patchState({ errorCode: "invalid-discount" });
      return Promise.resolve(false);
    }
    return this.runCartMutation(() =>
      this.dependencies.cart.applyLineDiscountAmountCents(
        lineId,
        discountCents,
      ),
    );
  }

  public applyLineManualDiscountBasisPoints(
    lineId: string,
    basisPoints: number,
  ): Promise<boolean> {
    if (!validBasisPoints(basisPoints)) {
      this.patchState({ errorCode: "invalid-discount" });
      return Promise.resolve(false);
    }
    return this.runCartMutation(() =>
      this.dependencies.cart.applyLineManualDiscountBasisPoints(
        lineId,
        basisPoints,
      ),
    );
  }

  public applyOrderDiscountAmountCents(
    discountCents: number,
  ): Promise<boolean> {
    if (!Number.isSafeInteger(discountCents) || discountCents < 0) {
      this.patchState({ errorCode: "invalid-discount" });
      return Promise.resolve(false);
    }
    return this.runCartMutation(() =>
      this.dependencies.cart.applyOrderDiscountAmountCents(
        discountCents,
      ),
    );
  }

  public applyOrderManualDiscountBasisPoints(
    basisPoints: number,
  ): Promise<boolean> {
    if (!validBasisPoints(basisPoints)) {
      this.patchState({ errorCode: "invalid-discount" });
      return Promise.resolve(false);
    }
    return this.runCartMutation(() =>
      this.dependencies.cart.applyOrderManualDiscountBasisPoints(
        basisPoints,
      ),
    );
  }

  public applyOrderQuickDiscount(
    basisPoints: number,
  ): Promise<boolean> {
    if (
      basisPoints !== 1_000 &&
      basisPoints !== 2_000 &&
      basisPoints !== 3_000 &&
      basisPoints !== 4_000 &&
      basisPoints !== 5_000
    ) {
      this.patchState({ errorCode: "invalid-discount" });
      return Promise.resolve(false);
    }
    return this.runCartMutation(() =>
      this.dependencies.cart.applyOrderQuickDiscountBasisPoints(
        basisPoints,
      ),
    );
  }

  public clearCart(): Promise<boolean> {
    if (this.state.phase !== "selling") {
      return Promise.resolve(false);
    }
    if (this.state.cart.lines.length === 0) {
      this.patchState({ errorCode: "empty-cart" });
      return Promise.resolve(false);
    }
    return this.runCartMutation(() =>
      this.dependencies.cart.clearCart(),
    ).then((cleared) => {
      // 清空购物车 = 放弃这一单，残留待选一并放弃并记日志（对齐 WPF）。
      if (cleared) this.abandonAllLookupSelections("cart-cleared");
      return cleared;
    });
  }

  public async openCash(): Promise<boolean> {
    if (!this.dependencies.capabilities.cashCheckout) {
      this.patchState({ errorCode: "runtime-unavailable" });
      return false;
    }
    const cart = await this.prepareCheckout();
    if (!cart) {
      return false;
    }

    this.cashIntentId = this.dependencies.createCheckoutIntentId();
    this.patchState({
      cart,
      phase: "cash",
      cashTenderedText: "",
      errorCode: null,
      success: null,
    });
    return true;
  }

  public prepareOnlineCheckout(): Promise<CartSnapshot | null> {
    return this.prepareCheckout();
  }

  public releasePreparedCheckout(): void {
    if (this.destroyed) return;
    this.dependencies.workflow.releasePreparedCheckout();
    if (this.state.phase === "verifying-checkout") {
      this.patchState({
        phase: "selling",
        pendingLookupCount:
          this.dependencies.workflow.getPendingCatalogWorkCount(),
      });
    }
  }

  public closeCash(): boolean {
    if (this.state.phase === "submitting-cash") {
      return false;
    }
    this.releasePreparedCheckout();
    this.cashIntentId = null;
    this.patchState({
      phase: "selling",
      cashTenderedText: "",
      errorCode: null,
    });
    return true;
  }

  public setCashTenderedText(value: string): void {
    if (this.state.phase !== "cash") {
      return;
    }
    this.patchState({
      cashTenderedText: value,
      errorCode:
        this.state.errorCode === "cash-invalid" ||
        this.state.errorCode === "cash-insufficient"
          ? null
          : this.state.errorCode,
    });
  }

  public setExactCash(): void {
    if (this.state.phase !== "cash") {
      return;
    }
    const cashDueCents = getCashDueCents(this.state.cart);
    this.setCashTenderedText(formatCashInput(cashDueCents));
  }

  /**
   * 非 async 包装保证重复点击拿到同一个 Promise；底层 completeCash 只调用一次。
   */
  public submitCash(): Promise<boolean> {
    if (this.cashSubmission) {
      return this.cashSubmission;
    }
    if (this.state.phase !== "cash") {
      return Promise.resolve(false);
    }

    const draft = deriveCashDraft(
      this.state.cart,
      this.state.cashTenderedText,
    );
    if (!draft.valid) {
      this.patchState({ errorCode: draft.errorCode });
      return Promise.resolve(false);
    }

    const checkoutIntentId =
      this.cashIntentId ?? this.dependencies.createCheckoutIntentId();
    this.cashIntentId = checkoutIntentId;
    const cartAtConfirmation = this.state.cart;
    this.patchState({
      phase: "submitting-cash",
      errorCode: null,
    });

    const submission = this.dependencies.workflow
      .completeCash({
        checkoutIntentId,
        cart: cartAtConfirmation,
        cashTenderedCents: draft.cashTenderedCents,
      })
      .then(
        async (result) => {
          if (!result.completed || !result.canClearCart) {
            this.patchState({
              phase: "cash",
              errorCode: "cash-failed",
            });
            return false;
          }

          let clearCartSignalled = true;
          let cartAfterCommit = cartAtConfirmation;
          try {
            await this.dependencies.cart.clearAfterCommittedOrder(
              result.orderGuid,
            );
            cartAfterCommit = this.dependencies.cart.getSnapshot();
            if (cartAfterCommit.lines.length > 0) {
              clearCartSignalled = false;
            }
          } catch {
            clearCartSignalled = false;
          }

          this.cashIntentId = null;
          this.patchState({
            phase: "success",
            cart: cartAfterCommit,
            cashTenderedText: "",
            errorCode: clearCartSignalled ? null : "cart-clear-failed",
            success: {
              orderGuid: result.orderGuid,
              cashDueCents: result.cashDueCents,
              changeCents: result.changeCents,
              clearCartSignalled,
              drawerDisposition: result.postCommit.drawerDisposition,
            },
          });
          return true;
        },
        () => {
          this.patchState({
            phase: "cash",
            errorCode: "cash-failed",
          });
          return false;
        },
      )
      .finally(() => {
        if (this.cashSubmission === submission) {
          this.cashSubmission = null;
        }
      });

    this.cashSubmission = submission;
    return submission;
  }

  public startNewSale(): boolean {
    if (this.state.phase !== "success") {
      return false;
    }
    if (this.state.success?.clearCartSignalled !== true) {
      this.patchState({ errorCode: "cart-clear-failed" });
      return false;
    }
    const cart = this.dependencies.cart.getSnapshot();
    if (cart.lines.length > 0) {
      this.patchState({ errorCode: "cart-clear-failed" });
      return false;
    }
    if (!this.dependencies.canStartNewTransaction()) {
      this.patchState({ errorCode: "new-transactions-disabled" });
      return false;
    }
    this.releasePreparedCheckout();
    this.patchState({
      phase: "selling",
      query: "",
      searchStatus: "idle",
      searchResults: [],
      cashTenderedText: "",
      errorCode: null,
      success: null,
      cart,
    });
    return true;
  }

  public holdCart(): Promise<boolean> {
    if (this.state.phase !== "selling") {
      return Promise.resolve(false);
    }
    if (!this.dependencies.capabilities.hold) {
      this.patchState({ errorCode: "runtime-unavailable" });
      return Promise.resolve(false);
    }
    if (this.state.cart.lines.length === 0) {
      this.patchState({ errorCode: "empty-cart" });
      return Promise.resolve(false);
    }
    if (this.blockForPendingLookupSelections()) {
      return Promise.resolve(false);
    }

    return this.dependencies.workflow
      .holdCart(this.state.cart)
      .then(() => {
        this.patchState({
          cart: this.dependencies.cart.getSnapshot(),
          errorCode: null,
        });
        return true;
      })
      .catch(() => {
        this.patchState({ errorCode: "hold-failed" });
        return false;
      });
  }

  public lockTerminal(): Promise<boolean> {
    if (!this.dependencies.capabilities.lock) {
      this.patchState({ errorCode: "runtime-unavailable" });
      return Promise.resolve(false);
    }
    return this.dependencies.workflow
      .lockTerminal()
      .then(() => {
        // 锁屏不放弃待选（对齐 WPF「锁屏换收银员保留」）；锁屏态界面不展示待选弹窗。
        this.patchState({
          phase: "locked",
          errorCode: null,
          lookupSelectionPending: false,
        });
        return true;
      })
      .catch(() => {
        this.patchState({ errorCode: "lock-failed" });
        return false;
      });
  }

  private runProductMutation(operation: () => Promise<void>): Promise<boolean> {
    const cartBefore = this.state.cart;
    const revealRevision = this.state.revealRevision;
    return operation()
      .then(() => {
        const cart = this.dependencies.cart.getSnapshot();
        this.applyCartSnapshot(cart);
        const targetLineId = findProductMutationTarget(cartBefore, cart);
        this.searchGeneration += 1;
        this.patchState({
          query: "",
          searchStatus: "idle",
          searchResults: [],
          errorCode: null,
        });
        if (
          targetLineId &&
          !(
            this.state.revealRevision > revealRevision &&
            this.state.revealLineId === targetLineId
          )
        ) {
          this.revealLine(targetLineId);
        }
        return true;
      })
      .catch((error: unknown) => {
        this.patchState({ errorCode: productMutationErrorCode(error) });
        return false;
      });
  }

  private prepareCheckout(): Promise<CartSnapshot | null> {
    if (
      this.destroyed ||
      this.state.phase !== "selling" ||
      this.checkoutPreparation
    ) {
      return Promise.resolve(null);
    }
    const pendingLookupCount =
      this.dependencies.workflow.getPendingCatalogWorkCount();
    if (
      this.state.cart.lines.length === 0 &&
      pendingLookupCount <= 0 &&
      this.lookupSelectionQueue.size === 0
    ) {
      this.patchState({ errorCode: "empty-cart" });
      return Promise.resolve(null);
    }
    // 付款前必须先处理完一码多商品待选，否则选定的商品会漏收（WPF c685663c5）。
    if (this.blockForPendingLookupSelections()) {
      return Promise.resolve(null);
    }

    this.patchState({
      phase: "verifying-checkout",
      pendingLookupCount,
      cashTenderedText: "",
      errorCode: null,
    });
    const preparation = Promise.resolve()
      .then(() =>
        this.dependencies.workflow.settlePendingCatalogWork({
          timeoutMs: 2_000,
        }),
      )
      .catch(() => ({ timedOut: true }))
      .then(() => {
        const cart = this.dependencies.cart.getSnapshot();
        if (this.destroyed || this.state.phase !== "verifying-checkout") {
          return null;
        }
        if (this.lookupSelectionQueue.size > 0) {
          // 结账准备期间迟到的远程/本地查码命中了一码多商品：退回收银态先处理待选。
          this.releasePreparedCheckout();
          this.patchState({
            cart,
            pendingLookupCount:
              this.dependencies.workflow.getPendingCatalogWorkCount(),
          });
          this.blockForPendingLookupSelections();
          return null;
        }
        if (cart.lines.length === 0) {
          this.releasePreparedCheckout();
          this.patchState({
            phase: "selling",
            cart,
            pendingLookupCount:
              this.dependencies.workflow.getPendingCatalogWorkCount(),
            errorCode: "empty-cart",
          });
          return null;
        }
        this.patchState({
          cart,
          pendingLookupCount:
            this.dependencies.workflow.getPendingCatalogWorkCount(),
        });
        return cart;
      })
      .finally(() => {
        if (this.checkoutPreparation === preparation) {
          this.checkoutPreparation = null;
        }
      });
    this.checkoutPreparation = preparation;
    return preparation;
  }

  private canMutateNewTransaction(): boolean {
    if (this.state.phase !== "selling") {
      return false;
    }
    if (
      this.state.cart.lines.length === 0 &&
      !this.dependencies.canStartNewTransaction()
    ) {
      this.patchState({ errorCode: "new-transactions-disabled" });
      return false;
    }
    return true;
  }

  private runCartMutation(operation: () => Promise<void>): Promise<boolean> {
    if (this.state.phase !== "selling") {
      return Promise.resolve(false);
    }
    if (!this.dependencies.capabilities.cartEditing) {
      this.patchState({ errorCode: "runtime-unavailable" });
      return Promise.resolve(false);
    }
    return operation()
      .then(() => {
        this.applyCartSnapshot(this.dependencies.cart.getSnapshot());
        this.patchState({ errorCode: null });
        return true;
      })
      .catch((error: unknown) => {
        this.patchState({
          errorCode:
            transportFailureErrorCode(error) ??
            (hasErrorCode(
              error,
              "SALES_OPERATION_NOT_AUTHORIZED",
            )
              ? "authorization-denied"
              : "cart-update-failed"),
        });
        return false;
      });
  }

  private applyCartSnapshot(cart: CartSnapshot): void {
    const previousCart = this.state.cart;
    const previousLineIds = new Set(
      previousCart.lines.map((line) => line.lineId),
    );
    const latestAddedLine = [...cart.lines]
      .reverse()
      .find((line) => !previousLineIds.has(line.lineId));
    if (latestAddedLine) {
      this.patchState({ cart });
      this.revealLine(latestAddedLine.lineId);
      return;
    }
    if (cart.lines.length === 0) {
      this.patchState({
        cart,
        selectedLineId: null,
        revealLineId: null,
      });
      return;
    }
    if (
      !this.mergeSelectionInProgress &&
      (!this.state.selectedLineId ||
        !cart.lines.some(
          (line) => line.lineId === this.state.selectedLineId,
        ))
    ) {
      this.patchState({ cart });
      this.revealLine(cart.lines[cart.lines.length - 1]!.lineId);
      return;
    }
    this.patchState({ cart });
  }

  private revealLine(lineId: string | null): void {
    this.patchState({
      selectedLineId: lineId,
      revealLineId: lineId,
      revealRevision: this.state.revealRevision + 1,
    });
  }

  private publishFeedback(event: SalesFeedbackEvent): void {
    if (this.destroyed) return;
    for (const listener of [...this.feedbackListeners]) {
      try {
        listener(event);
      } catch {
        // 提示层故障不能影响已确认的查询或购物车变更。
      }
    }
  }

  private showLookupSelection(selection: SalesLookupSelection): void {
    if (this.destroyed) return;
    if (
      (this.state.phase !== "selling" &&
        this.state.phase !== "verifying-checkout") ||
      selection.candidates.length < 2
    ) {
      // 中文注释：现金页、成功页或锁屏期间迟到的候选不入队，仅给出失败反馈。
      this.publishFeedback({
        attemptId: selection.selectionId,
        source: selection.source,
        kind: "failed-blocked",
      });
      return;
    }
    const wasEmpty = this.lookupSelectionQueue.size === 0;
    const entry = this.lookupSelectionQueue.tryEnqueue(
      selection.lookupCode,
      selection,
    );
    if (!entry) {
      // 队列满了拒收并报错误音，不静默丢（正常一单只会有个位数待选）。
      this.emitLookupSelectionLog({
        action: "rejected",
        choiceId: null,
        selection,
        queueLength: this.lookupSelectionQueue.size,
        waitedMs: null,
        sameLookupCodeAsPrevious: null,
        reason: "queue-full",
        productCode: null,
      });
      this.patchState({ errorCode: "lookup-selection-queue-full" });
      this.publishFeedback({
        attemptId: selection.selectionId,
        source: selection.source,
        kind: "failed-blocked",
      });
      return;
    }
    this.emitLookupSelectionLog({
      action: "created",
      choiceId: entry.id,
      selection,
      queueLength: this.lookupSelectionQueue.size,
      waitedMs: null,
      sameLookupCodeAsPrevious: entry.sameLookupCodeAsPrevious,
      reason: null,
      productCode: null,
    });
    // 队列从空到有才自动展开；收银员已收起继续扫码时只累加提示条计数，不强行弹出。
    this.syncLookupSelectionState(
      wasEmpty ? { lookupSelectionCollapsed: false } : {},
    );
    this.publishFeedback({
      attemptId: selection.selectionId,
      source: selection.source,
      kind: "query-found",
    });
  }

  /** 付款 / 挂单前有待选：拒绝并展开待选弹窗，提示收银员先处理。 */
  private blockForPendingLookupSelections(): boolean {
    if (this.lookupSelectionQueue.size === 0) return false;
    this.patchState({
      errorCode: "lookup-selection-required",
      lookupSelectionCollapsed: false,
    });
    return true;
  }

  /** 选定入车成功：只移除这次点选所属的条目（期间别的条目不受影响）。 */
  private resolveLookupSelection(
    entry: PendingScanChoice<SalesLookupSelection>,
    productCode: string,
  ): void {
    this.removeLookupSelection(entry, "resolved", null, productCode);
  }

  private removeLookupSelection(
    entry: PendingScanChoice<SalesLookupSelection>,
    action: "resolved" | "abandoned",
    reason: string | null,
    productCode: string | null,
  ): void {
    const removed = this.lookupSelectionQueue.remove(entry.id);
    if (removed) {
      this.emitLookupSelectionLog({
        action,
        choiceId: removed.id,
        selection: removed.payload,
        queueLength: this.lookupSelectionQueue.size,
        waitedMs: this.lookupSelectionQueue.waitedMs(removed),
        sameLookupCodeAsPrevious: removed.sameLookupCodeAsPrevious,
        reason,
        productCode,
      });
    }
    this.syncLookupSelectionState({ lookupSelectionPending: false });
  }

  /** 放弃全部待选（清空购物车、页面销毁、运行时断开），逐条记日志。 */
  private abandonAllLookupSelections(reason: string): void {
    const removed = this.lookupSelectionQueue.clear();
    for (const entry of removed) {
      this.emitLookupSelectionLog({
        action: "abandoned",
        choiceId: entry.id,
        selection: entry.payload,
        queueLength: 0,
        waitedMs: this.lookupSelectionQueue.waitedMs(entry),
        sameLookupCodeAsPrevious: entry.sameLookupCodeAsPrevious,
        reason,
        productCode: null,
      });
    }
    if (removed.length > 0) {
      this.syncLookupSelectionState({ lookupSelectionPending: false });
    }
  }

  /** 把队列状态投影到界面状态；队列清空时收起标记一并复位。 */
  private syncLookupSelectionState(
    patch: Partial<SalesPresenterState> = {},
  ): void {
    const head = this.lookupSelectionQueue.current;
    this.patchState({
      ...patch,
      lookupSelection: head?.payload ?? null,
      lookupSelectionQueueLength: this.lookupSelectionQueue.size,
      ...(head === null ? { lookupSelectionCollapsed: false } : {}),
    });
  }

  private emitLookupSelectionLog(
    input: Readonly<{
      action: SalesLookupSelectionLogEvent["action"];
      choiceId: number | null;
      selection: SalesLookupSelection;
      queueLength: number;
      waitedMs: number | null;
      sameLookupCodeAsPrevious: boolean | null;
      reason: string | null;
      productCode: string | null;
    }>,
  ): void {
    const lookupCode = input.selection.lookupCode.trim();
    const event: SalesLookupSelectionLogEvent = Object.freeze({
      action: input.action,
      choiceId: input.choiceId,
      selectionId: input.selection.selectionId,
      source: input.selection.source,
      lookupCodeTail: lookupCode.slice(-4),
      lookupCodeLength: lookupCode.length,
      candidateCount: input.selection.candidates.length,
      candidateProducts: input.selection.candidates
        .slice(0, 5)
        .map((candidate) => candidate.productCode)
        .join(","),
      queueLength: input.queueLength,
      waitedMs: input.waitedMs,
      sameLookupCodeAsPrevious: input.sameLookupCodeAsPrevious,
      reason: input.reason,
      productCode: input.productCode,
    });
    for (const listener of [...this.lookupSelectionLogListeners]) {
      try {
        listener(event);
      } catch {
        // 日志是旁路，不能影响待选与购物车。
      }
    }
  }

  private publishBlockedAddAttempt(
    source: "manual" | "hid" | "camera",
  ): void {
    this.nextPresenterAttemptId += 1;
    this.publishFeedback({
      attemptId: `presenter-blocked-${this.nextPresenterAttemptId}`,
      source,
      kind: "failed-blocked",
    });
  }

  private patchState(patch: Partial<SalesPresenterState>): void {
    if (this.destroyed) return;
    this.state = {
      ...this.state,
      ...patch,
    };
    for (const listener of this.listeners) {
      try {
        listener();
      } catch {
        // 视图订阅者故障不能反向改变“现金订单已提交”的业务结果。
      }
    }
  }
}

function productMutationErrorCode(error: unknown): SalesErrorCode {
  return (
    transportFailureErrorCode(error) ??
    (hasErrorCode(error, "NEW_TRANSACTIONS_DISABLED")
      ? "new-transactions-disabled"
      : hasErrorCode(error, "ACTIVE_PRICING_CART_TERMINAL_RECOVERY_REQUIRED")
        ? "terminal-recovery-required"
        : hasErrorCode(error, "SALES_OPERATION_NOT_AUTHORIZED")
          ? "authorization-denied"
          : "product-add-failed")
  );
}

function findProductMutationTarget(
  before: CartSnapshot,
  after: CartSnapshot,
): string | null {
  const previousById = new Map(
    before.lines.map((line) => [line.lineId, line] as const),
  );
  for (let index = after.lines.length - 1; index >= 0; index -= 1) {
    const line = after.lines[index]!;
    const previous = previousById.get(line.lineId);
    if (!previous || cartLineDisplayChanged(previous, line)) {
      return line.lineId;
    }
  }
  return null;
}

function cartLineDisplayChanged(
  before: CartSnapshot["lines"][number],
  after: CartSnapshot["lines"][number],
): boolean {
  return (
    before.quantity !== after.quantity ||
    before.unitPrice.cents !== after.unitPrice.cents ||
    before.discount.cents !== after.discount.cents ||
    before.actualAmount.cents !== after.actualAmount.cents ||
    before.priceSource !== after.priceSource
  );
}

export function getCashDueCents(cart: CartSnapshot): number {
  return roundCashAmount(cart.actualAmount).cents;
}

export function deriveCashDraft(
  cart: CartSnapshot,
  cashTenderedText: string,
): CashDraft {
  const cashDueCents = getCashDueCents(cart);
  if (cashDueCents === 0) {
    return {
      cashDueCents,
      cashTenderedCents: null,
      normalizedTenderedCents: null,
      changeCents: 0,
      valid: true,
      errorCode: null,
    };
  }

  const cashTenderedCents = parseCashInput(cashTenderedText);
  if (cashTenderedCents === null) {
    return {
      cashDueCents,
      cashTenderedCents: null,
      normalizedTenderedCents: null,
      changeCents: 0,
      valid: false,
      errorCode: "cash-invalid",
    };
  }

  const settlement = calculateCashSettlement({
    actualAmount: cart.actualAmount,
    cashTendered: createAud(cashTenderedCents),
  });
  const enough =
    cart.actualAmount.cents >= 0
      ? settlement.normalizedCashTendered.cents >= settlement.cashDue.cents
      : settlement.normalizedCashTendered.cents <= settlement.cashDue.cents;

  return {
    cashDueCents: settlement.cashDue.cents,
    cashTenderedCents,
    normalizedTenderedCents: settlement.normalizedCashTendered.cents,
    changeCents: settlement.change.cents,
    valid: enough,
    errorCode: enough ? null : "cash-insufficient",
  };
}

export function parseCashInput(value: string): number | null {
  const normalized = value.trim().replace(/[$,\s]/g, "");
  const match = /^(\d+)(?:\.(\d{0,2}))?$/.exec(normalized);
  if (!match) {
    return null;
  }
  const whole = Number(match[1]);
  const cents = Number((match[2] ?? "").padEnd(2, "0"));
  const result = whole * 100 + cents;
  return Number.isSafeInteger(result) ? result : null;
}

export function formatCashInput(cents: number): string {
  const sign = cents < 0 ? "-" : "";
  const absolute = Math.abs(cents);
  return `${sign}${Math.floor(absolute / 100)}.${String(absolute % 100).padStart(2, "0")}`;
}

export function formatAud(cents: number, locale: "en" | "zh"): string {
  return new Intl.NumberFormat(locale === "zh" ? "zh-AU" : "en-AU", {
    style: "currency",
    currency: "AUD",
    currencyDisplay: "narrowSymbol",
  }).format(cents / 100);
}

export function getAvailableTenderMethods(
  connectivity: "checking" | "online" | "offline",
): readonly ("cash" | "card" | "voucher")[] {
  return connectivity === "online"
    ? ["cash", "card", "voucher"]
    : ["cash"];
}

export function createDisconnectedSalesPresenter(): SalesPresenter {
  const unavailable = async (): Promise<never> => {
    throw new Error("Sales runtime is not connected.");
  };
  const cart: SalesCartPort = {
    getSnapshot: () => EMPTY_SALE_CART,
    subscribe: () => () => undefined,
    hasMergeCompatibleLines: () => false,
    mergeCompatibleLines: unavailable,
    increaseLine: unavailable,
    decreaseLine: unavailable,
    removeLine: unavailable,
    applyLineDiscountBasisPoints: unavailable,
    setLineQuantity: unavailable,
    setLineUnitPriceCents: unavailable,
    applyLineDiscountAmountCents: unavailable,
    applyLineManualDiscountBasisPoints: unavailable,
    applyOrderDiscountAmountCents: unavailable,
    applyOrderManualDiscountBasisPoints: unavailable,
    applyOrderQuickDiscountBasisPoints: unavailable,
    clearCart: unavailable,
    clearAfterCommittedOrder: unavailable,
  };
  const workflow: SalesWorkflowPort = {
    searchProducts: unavailable,
    addProduct: unavailable,
    addByLookupCode: unavailable,
    subscribeScanTarget: () => () => undefined,
    addOpenItem: unavailable,
    getPendingCatalogWorkCount: () => 0,
    subscribePendingCatalogWork: () => () => undefined,
    settlePendingCatalogWork: async () => ({ timedOut: false }),
    disposePendingCatalogWork: () => undefined,
    releasePreparedCheckout: () => undefined,
    completeCash: unavailable,
    holdCart: unavailable,
    lockTerminal: unavailable,
  };
  return new SalesPresenter({
    cart,
    workflow,
    capabilities: DISCONNECTED_CAPABILITIES,
    createCheckoutIntentId: () => "unavailable",
    canStartNewTransaction: () => false,
  });
}

function hasErrorCode(error: unknown, code: string): boolean {
  return (
    typeof error === "object" &&
    error !== null &&
    "code" in error &&
    error.code === code
  );
}

function validBasisPoints(value: number): boolean {
  return (
    Number.isSafeInteger(value) && value >= 0 && value <= 10_000
  );
}
