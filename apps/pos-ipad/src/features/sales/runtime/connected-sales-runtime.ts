import type {
  SalesCapabilities,
  SalesCartPort,
  SalesLookupCandidate,
  SalesLookupSelection,
  SalesPresenterDependencies,
  SalesProductSearchItem,
  SalesWorkflowPort,
} from "../ui/sales-presenter";
import { SalesPresenter } from "../ui/sales-presenter";

import { createAud, type CartSnapshot } from "@/core/contracts";
import type { LocalCatalogMatch } from "@/core/db/catalog-repository";
import type {
  CatalogLookupRevalidationPort,
} from "@/features/catalog/catalog-lookup-revalidation";
import type {
  CashCheckoutInput,
  CashCheckoutResult,
} from "@/features/checkout/cash/cash-checkout-service";
import { PAYMENT_PERMISSION } from "@/features/payments/runtime/payment-checkout-runtime";
import type {
  CartAddDisposition,
  MergeCompatibleCartLinesResult,
} from "@/features/sales/domain";
import {
  ACTIVE_PRICING_CART_STALE_SNAPSHOT,
  ActivePricingCartSession,
} from "@/features/sales/runtime/active-pricing-cart-session";
import {
  AuthorizedSalesOperationExecutor,
  SALES_PERMISSIONS,
  cartMutationRejected,
  quickLineDiscountPermission,
  quickOrderDiscountPermission,
  type SalesOperationSecurity,
} from "@hb/pos-domain/features/sales/runtime/sales-operation-security";
import { scanTiming } from "@/features/sales/runtime/scan-timing";

export const SALES_NEW_TRANSACTIONS_DISABLED =
  "NEW_TRANSACTIONS_DISABLED";
export const SALES_CHECKOUT_PREPARED = "SALES_CHECKOUT_PREPARED";

export type LookupSource = "manual" | "hid" | "camera";

export type LookupOutcome = Readonly<{
  attemptId: string;
  source: LookupSource;
  kind: "added" | "incremented" | "not-found" | "failed-blocked";
  lineId?: string;
  /** HID 时序会话 id；业务逻辑不得读取。 */
  timingId?: string;
}>;

export type LookupAttemptOptions = Readonly<{
  source?: LookupSource;
  /** HID 时序会话 id，仅供 scan-timing 打点使用；业务逻辑不得读取。 */
  timingId?: string;
}>;

type LookupAttempt = {
  readonly attemptId: string;
  readonly source: LookupSource;
  readonly timingId?: string;
  terminalPublished: boolean;
};

export interface LocalCatalogPort {
  findExact(lookupCode: string): Promise<LocalCatalogMatch | null>;
  /**
   * 一码多商品：目录行在首位，其后为仍有效的冲突候选（按商品去重）。
   * 未实现时退回 findExact 单行语义。
   */
  findExactCandidates?(
    lookupCode: string,
  ): Promise<readonly LocalCatalogMatch[]>;
  searchByName(
    query: string,
    limit: number,
    offset?: number,
  ): Promise<readonly LocalCatalogMatch[]>;
}

/** 仅要求 durable 服务兼容 complete 入口，不把 runtime 绑定到具体实现类。 */
export interface DurableCashCheckoutPort {
  complete(input: CashCheckoutInput): Promise<CashCheckoutResult>;
}

export interface SalesHoldPort {
  hold(cart: CartSnapshot): Promise<void>;
}

/**
 * 共享召回购物车的普通 owner release（正常清车路径）。
 * 组合根注入共享挂单 coordinator；失败必须抛错，购物车与 binding 保持不变。
 */
export interface SalesRecalledCartReleasePort {
  releaseRecalledCart(holdGuid: string): Promise<void>;
}

export interface SalesLockPort {
  lock(): Promise<void>;
}

/**
 * 由组合根绑定到创建 presenter 时的可信 cashier lease。401、403、锁屏或换人后，
 * assertActive 必须同步 fail-closed，且 UI 不得构造或替换这个 Port。
 */
export interface ConnectedSalesSessionGuard {
  assertActive(): void;
}

export type ConnectedSalesIdentity = Readonly<{
  storeCode: string;
  deviceCode: string;
  cashierId: string;
  cashierName: string;
  userGuid: string | null;
}>;

export type ConnectedSalesRuntimeDependencies = Readonly<{
  activeCartSession: ActivePricingCartSession;
  catalog?: LocalCatalogPort | undefined;
  catalogRevalidation?: CatalogLookupRevalidationPort | undefined;
  catalogWorkScheduler?: CatalogWorkScheduler | undefined;
  cashCheckout?: DurableCashCheckoutPort | undefined;
  identity: ConnectedSalesIdentity;
  hold?: SalesHoldPort;
  lock?: SalesLockPort;
  releaseRecalledCart?: SalesRecalledCartReleasePort | undefined;
  sessionGuard: ConnectedSalesSessionGuard;
  newTransactionGate: Readonly<{
    canStartNewTransaction(): boolean;
  }>;
  createCheckoutIntentId(): string;
  createLineId(): string;
  operationSecurity: SalesOperationSecurity;
}>;

export interface CatalogWorkScheduler {
  yieldToUi(): Promise<void>;
  waitForTimeout(timeoutMs: number): Promise<void>;
}

const DEFAULT_CATALOG_WORK_SCHEDULER: CatalogWorkScheduler = Object.freeze({
  yieldToUi: () =>
    new Promise<void>((resolve) => {
      setTimeout(resolve, 0);
    }),
  waitForTimeout: (timeoutMs: number) =>
    new Promise<void>((resolve) => {
      setTimeout(resolve, timeoutMs);
    }),
});

class PreparedCheckoutMutationGate {
  private state: "open" | "prepared" | "disposed" = "open";

  public assertMutable(): void {
    if (this.state !== "open") {
      throw Object.assign(
        new Error("Sales cart is frozen for checkout preparation."),
        { code: SALES_CHECKOUT_PREPARED },
      );
    }
  }

  public isMutable(): boolean {
    return this.state === "open";
  }

  public prepare(): void {
    if (this.state === "open") this.state = "prepared";
  }

  public release(): void {
    if (this.state === "prepared") this.state = "open";
  }

  public dispose(): void {
    this.state = "disposed";
  }
}

/**
 * PricingCart 是同步领域对象；这个 adapter 将每次成功变更显式发布给 React presenter。
 * 订单成功前不存在 clear 入口，因此失败/异常路径不能意外清空购物车。
 */
export class PricingCartSalesAdapter implements SalesCartPort {
  private readonly listenerSubscriptions = new Set<() => void>();

  public constructor(
    private readonly activeCart: ActivePricingCartSession,
    private readonly sessionGuard: ConnectedSalesSessionGuard,
    private readonly operations: AuthorizedSalesOperationExecutor,
    private readonly preparedCheckoutGate =
      new PreparedCheckoutMutationGate(),
    private readonly releaseRecalledCart?: SalesRecalledCartReleasePort,
  ) {}

  public getSnapshot(): CartSnapshot {
    this.sessionGuard.assertActive();
    return this.activeCart.getSnapshot();
  }

  public subscribe(listener: () => void): () => void {
    this.sessionGuard.assertActive();
    const unsubscribeSession = this.activeCart.subscribe(() => {
      try {
        this.sessionGuard.assertActive();
      } catch {
        // 旧 presenter 失效后静默丢弃共享购物车的新快照，等待 route 卸载订阅。
        return;
      }
      listener();
    });
    let subscribed = true;
    const unsubscribe = () => {
      if (!subscribed) return;
      subscribed = false;
      this.listenerSubscriptions.delete(unsubscribe);
      unsubscribeSession();
    };
    this.listenerSubscriptions.add(unsubscribe);
    return unsubscribe;
  }

  public destroy(): void {
    for (const unsubscribe of [...this.listenerSubscriptions]) {
      unsubscribe();
    }
  }

  public async increaseLine(lineId: string): Promise<void> {
    await this.runMutation(
      SALES_PERMISSIONS.changeQuantity,
      "increase-line",
      "CART_ITEM_QUANTITY_CHANGE",
      () =>
        this.mutate(
          this.activeCart.increaseLine(lineId),
          "Unable to increase cart line.",
        ),
    );
  }

  public async decreaseLine(lineId: string): Promise<void> {
    await this.runMutation(
      SALES_PERMISSIONS.changeQuantity,
      "decrease-line",
      "CART_ITEM_QUANTITY_CHANGE",
      () =>
        this.mutate(
          this.activeCart.decreaseLine(lineId),
          "Unable to decrease cart line.",
        ),
    );
  }

  public async removeLine(lineId: string): Promise<void> {
    await this.runMutation(
      SALES_PERMISSIONS.removeLine,
      "remove-line",
      "CART_ITEM_REMOVE",
      () =>
        this.mutate(
          this.activeCart.removeLine(lineId),
          "Unable to remove cart line.",
        ),
    );
  }

  public hasMergeCompatibleLines(): boolean {
    this.sessionGuard.assertActive();
    return (
      this.preparedCheckoutGate.isMutable() &&
      this.activeCart.hasMergeCompatibleLines()
    );
  }

  public mergeCompatibleLines(): Promise<MergeCompatibleCartLinesResult> {
    this.preparedCheckoutGate.assertMutable();
    return this.operations.runCartMutation({
      permissionCode: SALES_PERMISSIONS.changeQuantity,
      action: "merge-compatible-lines",
      eventType: "CART_ITEM_QUANTITY_CHANGE",
      getCart: () => this.getSnapshot(),
      operation: () => {
        // 授权可能延迟返回，真正合并前再次检查结账围栏。
        this.preparedCheckoutGate.assertMutable();
        return this.activeCart.mergeCompatibleLines();
      },
    });
  }

  public async applyLineDiscountBasisPoints(
    lineId: string,
    basisPoints: number,
  ): Promise<void> {
    const permissionCode =
      basisPoints === 0
        ? SALES_PERMISSIONS.lineManualDiscount
        : quickLineDiscountPermission(basisPoints);
    if (!permissionCode) {
      throw cartMutationRejected(
        "Quick line discount must be 10%, 20%, 30%, 40%, or 50%.",
      );
    }
    await this.runMutation(
      permissionCode,
      basisPoints === 0
        ? "manual-discount-percent"
        : "quick-discount-percent",
      "CART_LINE_DISCOUNT_CHANGE",
      () =>
        this.mutate(
          this.activeCart.setLineDiscountPercentBps(
            lineId,
            basisPoints,
          ),
          "Unable to apply cart discount.",
        ),
    );
  }

  public async setLineQuantity(
    lineId: string,
    quantity: number,
  ): Promise<void> {
    await this.runMutation(
      SALES_PERMISSIONS.changeQuantity,
      "modify-quantity",
      "CART_ITEM_QUANTITY_CHANGE",
      () =>
        this.mutate(
          this.activeCart.setLineQuantity(lineId, quantity),
          "Unable to set cart line quantity.",
        ),
    );
  }

  public async setLineUnitPriceCents(
    lineId: string,
    unitPriceCents: number,
  ): Promise<void> {
    await this.runMutation(
      SALES_PERMISSIONS.changePrice,
      "modify-price",
      "CART_ITEM_PRICE_CHANGE",
      () =>
        this.mutate(
          this.activeCart.setLineUnitPrice(
            lineId,
            createAud(unitPriceCents),
          ),
          "Unable to set cart line price.",
        ),
    );
  }

  public async applyLineDiscountAmountCents(
    lineId: string,
    discountCents: number,
  ): Promise<void> {
    await this.runMutation(
      SALES_PERMISSIONS.lineManualDiscount,
      "manual-discount-amount",
      "CART_LINE_DISCOUNT_CHANGE",
      () =>
        this.mutate(
          this.activeCart.setLineDiscountAmount(
            lineId,
            createAud(discountCents),
          ),
          "Unable to set cart line discount.",
        ),
    );
  }

  public async applyLineManualDiscountBasisPoints(
    lineId: string,
    basisPoints: number,
  ): Promise<void> {
    await this.runMutation(
      SALES_PERMISSIONS.lineManualDiscount,
      "manual-discount-percent",
      "CART_LINE_DISCOUNT_CHANGE",
      () =>
        this.mutate(
          this.activeCart.setLineDiscountPercentBps(
            lineId,
            basisPoints,
          ),
          "Unable to set cart line discount.",
        ),
    );
  }

  public async applyOrderDiscountAmountCents(
    discountCents: number,
  ): Promise<void> {
    await this.runMutation(
      SALES_PERMISSIONS.orderManualDiscount,
      "manual-discount-amount",
      "CART_ORDER_DISCOUNT_CHANGE",
      () =>
        this.mutate(
          this.activeCart.setOrderDiscountAmount(
            createAud(discountCents),
          ),
          "Unable to set order discount.",
        ),
    );
  }

  public async applyOrderManualDiscountBasisPoints(
    basisPoints: number,
  ): Promise<void> {
    await this.runMutation(
      SALES_PERMISSIONS.orderManualDiscount,
      "manual-discount-percent",
      "CART_ORDER_DISCOUNT_CHANGE",
      () =>
        this.mutate(
          this.activeCart.setOrderDiscountPercentBps(basisPoints),
          "Unable to set order discount.",
        ),
    );
  }

  public async applyOrderQuickDiscountBasisPoints(
    basisPoints: number,
  ): Promise<void> {
    const permissionCode = quickOrderDiscountPermission(basisPoints);
    if (!permissionCode) {
      throw cartMutationRejected(
        "Quick order discount must be 10%, 20%, 30%, 40%, or 50%.",
      );
    }
    await this.runMutation(
      permissionCode,
      "quick-discount-percent",
      "CART_ORDER_DISCOUNT_CHANGE",
      () =>
        this.mutate(
          this.activeCart.setOrderDiscountPercentBps(basisPoints),
          "Unable to set order discount.",
        ),
    );
  }

  public async clearCart(): Promise<void> {
    const binding = this.activeCart.getRecallBinding();
    if (binding) {
      // 共享召回购物车：普通清车路由到 owner release（服务端 owner-scoped
      // release -> 本地 claim/fence/cart 清理）。未接线端口时 fail-closed。
      if (!this.releaseRecalledCart) {
        throw cartMutationRejected(
          "Unable to clear a recalled cart without a release port.",
        );
      }
      await this.runMutation(
        SALES_PERMISSIONS.clearCart,
        "clear-cart",
        "CART_CLEAR",
        () =>
          this.releaseRecalledCart!.releaseRecalledCart(
            binding.holdId,
          ),
      );
      return;
    }
    await this.runMutation(
      SALES_PERMISSIONS.clearCart,
      "clear-cart",
      "CART_CLEAR",
      () =>
        this.mutate(
          this.activeCart.clearManually(),
          "Unable to clear cart.",
        ),
    );
  }

  public async clearAfterCommittedOrder(orderGuid: string): Promise<void> {
    this.sessionGuard.assertActive();
    this.activeCart.clearAfterCommittedOrder(orderGuid);
  }

  public addCatalogItem(item: LocalCatalogMatch, lineId: string): string {
    return this.addCatalogItemWithDisposition(item, lineId).lineId;
  }

  public addCatalogItemWithDisposition(
    item: LocalCatalogMatch,
    lineId: string,
  ): CartAddDisposition {
    this.preparedCheckoutGate.assertMutable();
    return this.addCatalogItemInternal(item, lineId, false);
  }

  public addScannedCatalogItem(
    item: LocalCatalogMatch,
    lineId: string,
  ): string {
    return this.addScannedCatalogItemWithDisposition(item, lineId).lineId;
  }

  public addScannedCatalogItemWithDisposition(
    item: LocalCatalogMatch,
    lineId: string,
  ): CartAddDisposition {
    this.preparedCheckoutGate.assertMutable();
    return this.addCatalogItemInternal(item, lineId, true);
  }

  public addCatalogItemFromTrustedContinuation(
    item: LocalCatalogMatch,
    lineId: string,
  ): string {
    return this.addCatalogItemFromTrustedContinuationWithDisposition(
      item,
      lineId,
    ).lineId;
  }

  public addCatalogItemFromTrustedContinuationWithDisposition(
    item: LocalCatalogMatch,
    lineId: string,
  ): CartAddDisposition {
    return this.addCatalogItemInternal(item, lineId, false);
  }

  public addScannedCatalogItemFromTrustedContinuation(
    item: LocalCatalogMatch,
    lineId: string,
  ): string {
    return this.addScannedCatalogItemFromTrustedContinuationWithDisposition(
      item,
      lineId,
    ).lineId;
  }

  public addScannedCatalogItemFromTrustedContinuationWithDisposition(
    item: LocalCatalogMatch,
    lineId: string,
  ): CartAddDisposition {
    return this.addCatalogItemInternal(item, lineId, true);
  }

  private addCatalogItemInternal(
    item: LocalCatalogMatch,
    lineId: string,
    scanned: boolean,
  ): CartAddDisposition {
    this.sessionGuard.assertActive();
    const input = {
      lineId: requiredText(lineId, "Cart line id"),
      productCode: item.productCode,
      itemNumber: item.itemNumber,
      lookupCode: item.lookupCode,
      displayName: item.displayName,
      quantity: item.quantityFactor,
      unitPrice: createAud(item.retailPriceCents),
      catalogDiscountBasisPoints: discountRateToBasisPoints(
        item.discountRate,
      ),
      syncProvenance: {
        referenceCode: item.referenceCode,
        priceSource: item.priceSource,
      },
      priceSource: "catalog" as const,
    };
    return scanned
      ? this.activeCart.addScannedItemWithDisposition(input)
      : this.activeCart.addItemWithDisposition(input);
  }

  public refreshCatalogItem(
    expected: Readonly<{
      productCode: string;
      referenceCode: string | null;
      lookupCode: string;
    }>,
    item: LocalCatalogMatch,
    transactionEpoch: number,
  ): readonly string[] {
    this.preparedCheckoutGate.assertMutable();
    return this.refreshCatalogItemInternal(
      expected,
      item,
      transactionEpoch,
    );
  }

  public refreshCatalogItemFromTrustedContinuation(
    expected: Readonly<{
      productCode: string;
      referenceCode: string | null;
      lookupCode: string;
    }>,
    item: LocalCatalogMatch,
    transactionEpoch: number,
  ): readonly string[] {
    return this.refreshCatalogItemInternal(
      expected,
      item,
      transactionEpoch,
    );
  }

  private refreshCatalogItemInternal(
    expected: Readonly<{
      productCode: string;
      referenceCode: string | null;
      lookupCode: string;
    }>,
    item: LocalCatalogMatch,
    transactionEpoch: number,
  ): readonly string[] {
    this.sessionGuard.assertActive();
    return this.activeCart.refreshCatalogItem(
      {
        expected,
        item: {
          productCode: item.productCode,
          referenceCode: item.referenceCode,
          itemNumber: item.itemNumber,
          lookupCode: item.lookupCode,
          displayName: item.displayName,
          retailPriceCents: item.retailPriceCents,
          catalogDiscountBasisPoints: discountRateToBasisPoints(
            item.discountRate,
          ),
          priceSource: item.priceSource,
        },
      },
      transactionEpoch,
    );
  }

  public addOpenCatalogItem(
    item: LocalCatalogMatch,
    lineId: string,
    unitPriceCents: number,
  ): void {
    this.preparedCheckoutGate.assertMutable();
    this.sessionGuard.assertActive();
    this.activeCart.addOpenItem({
      lineId: requiredText(lineId, "Cart line id"),
      productCode: item.productCode,
      itemNumber: item.itemNumber,
      lookupCode: item.lookupCode,
      displayName: item.displayName,
      unitPrice: createAud(unitPriceCents),
      syncProvenance: {
        referenceCode: item.referenceCode,
        priceSource: item.priceSource,
      },
    });
  }

  private runMutation(
    permissionCode: string,
    action: string,
    eventType:
      | "CART_ITEM_REMOVE"
      | "CART_ITEM_QUANTITY_CHANGE"
      | "CART_ITEM_PRICE_CHANGE"
      | "CART_LINE_DISCOUNT_CHANGE"
      | "CART_ORDER_DISCOUNT_CHANGE"
      | "CART_CLEAR",
    operation: () => void | Promise<void>,
  ): Promise<void> {
    this.preparedCheckoutGate.assertMutable();
    return this.operations.runCartMutation({
      permissionCode,
      action,
      eventType,
      getCart: () => this.getSnapshot(),
      operation: () => {
        // 主管授权可能延迟返回；真正写 active cart 前必须再次检查结账围栏。
        this.preparedCheckoutGate.assertMutable();
        return operation();
      },
    });
  }

  private mutate(success: boolean, failureMessage: string): void {
    if (!success) {
      throw cartMutationRejected(failureMessage);
    }
  }
}

/**
 * 生产组合的最窄边界：外层只能给出已认证的 identity、真实本地目录和 durable cash port。
 * SalesWorkflowPort 不接收身份字段，从而无法由 UI 伪造门店、设备或收银员。
 */
export function createConnectedSalesDependencies(
  input: ConnectedSalesRuntimeDependencies,
): SalesPresenterDependencies {
  assertIdentity(input.identity);
  input.sessionGuard.assertActive();
  const operations = new AuthorizedSalesOperationExecutor(
    input.operationSecurity,
    input.identity,
    input.sessionGuard,
  );
  const preparedCheckoutGate = new PreparedCheckoutMutationGate();
  const cart = new PricingCartSalesAdapter(
    input.activeCartSession,
    input.sessionGuard,
    operations,
    preparedCheckoutGate,
    input.releaseRecalledCart,
  );
  const workflow = new ConnectedSalesWorkflow(
    cart,
    input.activeCartSession,
    input.catalog,
    input.catalogRevalidation,
    input.catalogWorkScheduler ?? DEFAULT_CATALOG_WORK_SCHEDULER,
    input.cashCheckout,
    input.identity,
    input.hold,
    input.lock,
    input.sessionGuard,
    input.newTransactionGate,
    input.createLineId,
    operations,
    preparedCheckoutGate,
  );

  return {
    cart,
    workflow,
    capabilities: deriveCapabilities(input),
    createCheckoutIntentId: input.createCheckoutIntentId,
    canStartNewTransaction:
      input.newTransactionGate.canStartNewTransaction,
  };
}

export function createConnectedSalesPresenter(
  input: ConnectedSalesRuntimeDependencies,
): SalesPresenter {
  return new SalesPresenter(createConnectedSalesDependencies(input));
}

class ConnectedSalesWorkflow implements SalesWorkflowPort {
  private readonly pendingCatalogWork = new Set<Promise<void>>();
  private readonly pendingCatalogWorkListeners = new Set<() => void>();
  private readonly scanTargetListeners = new Set<(lineId: string) => void>();
  private readonly lookupOutcomeListeners = new Set<
    (outcome: LookupOutcome) => void
  >();
  private readonly lookupSelectionListeners = new Set<
    (selection: SalesLookupSelection) => void
  >();
  private checkoutFence = 0;
  private catalogWorkDisposed = false;
  private nextLookupAttemptId = 0;

  public constructor(
    private readonly cart: PricingCartSalesAdapter,
    private readonly activeCart: ActivePricingCartSession,
    private readonly catalog: LocalCatalogPort | undefined,
    private readonly catalogRevalidation:
      | CatalogLookupRevalidationPort
      | undefined,
    private readonly catalogWorkScheduler: CatalogWorkScheduler,
    private readonly cashCheckout: DurableCashCheckoutPort | undefined,
    private readonly identity: ConnectedSalesIdentity,
    private readonly hold: SalesHoldPort | undefined,
    private readonly lock: SalesLockPort | undefined,
    private readonly sessionGuard: ConnectedSalesSessionGuard,
    private readonly newTransactionGate: Readonly<{
      canStartNewTransaction(): boolean;
    }>,
    private readonly createLineId: () => string,
    private readonly operations: AuthorizedSalesOperationExecutor,
    private readonly preparedCheckoutGate: PreparedCheckoutMutationGate,
  ) {}

  public async searchProducts(
    query: string,
  ): Promise<readonly SalesProductSearchItem[]> {
    return this.operations.runRead(
      SALES_PERMISSIONS.view,
      "search-products",
      async () => {
        const exactCandidates = await this.findSearchExactCandidates(query);
        this.sessionGuard.assertActive();
        if (exactCandidates.length > 1) {
          // 与 WPF 一致：输入精确命中一码多商品时直接列出全部候选，每个商品一次，
          // 避免关键词搜索只列目录胜出项（单结果会被自动加购）或被结果上限截断。
          return exactCandidates.map(toSearchItem);
        }
        const matches = await this.requireCatalog().searchByName(
          query,
          50,
          0,
        );
        this.sessionGuard.assertActive();
        return deduplicateProductSearchMatches(
          matches.filter(
            (match) => match.storeCode === this.identity.storeCode,
          ),
        )
          .map(toSearchItem);
      },
    );
  }

  public async addProduct(product: SalesProductSearchItem): Promise<void> {
    const attempt = this.createLookupAttempt("manual");
    try {
      this.sessionGuard.assertActive();
      this.preparedCheckoutGate.assertMutable();
      this.assertCanStartOrContinueTransaction();
      await this.operations.runCartMutation({
      permissionCode: SALES_PERMISSIONS.addItem,
      action: "add-selected",
      eventType: "CART_ITEM_ADD",
      getCart: () => this.cart.getSnapshot(),
      operation: async () => {
        // 一码多商品时搜索结果可能是冲突候选：按商品编码在同码候选中取回本地目录行。
        const candidates = await this.requireExactCatalogCandidates(
          product.lookupCode,
        );
        const match =
          candidates.find(
            (candidate) => candidate.productCode === product.productCode,
          ) ?? candidates[0]!;
        this.assertCanStartOrContinueTransaction();
        if (
          match.productCode !== product.productCode ||
          match.lookupCode !== product.lookupCode
        ) {
          throw new Error(
            "Catalog product identity does not match the active local snapshot.",
          );
        }
        const disposition = this.cart.addCatalogItemWithDisposition(
          match,
          this.createLineId(),
        );
        this.completeLookupAttempt(attempt, disposition);
      },
      });
    } catch (error) {
      this.notifyFailedLookupAttempt(attempt, error);
      throw error;
    }
  }

  public async addByLookupCode(
    lookupCode: string,
    options: LookupAttemptOptions = {},
  ): Promise<string | null> {
    const attempt = this.createLookupAttempt(
      options.source ?? "manual",
      options.timingId,
    );
    try {
      this.sessionGuard.assertActive();
      this.preparedCheckoutGate.assertMutable();
      this.assertCanStartOrContinueTransaction();
    if (!this.catalogRevalidation) {
      const disposition = await this.operations.runCartMutation({
        permissionCode: SALES_PERMISSIONS.addItem,
        action: "scan-add-item",
        eventType: "CART_ITEM_ADD",
        getCart: () => this.cart.getSnapshot(),
        operation: async () => {
          const candidates = await this.requireExactCatalogCandidates(
            lookupCode,
          );
          this.assertCanStartOrContinueTransaction();
          if (candidates.length > 1) {
            this.preparedCheckoutGate.assertMutable();
            this.publishLookupSelection(attempt, candidates);
            return null;
          }
          const result = this.cart.addScannedCatalogItemWithDisposition(
            candidates[0]!,
            this.createLineId(),
          );
          this.notifyScanTarget(result.lineId);
          this.completeLookupAttempt(attempt, result);
          return result;
        },
      });
      return disposition?.lineId ?? null;
    }

    const normalizedLookupCode = normalizeLookupCode(
      requiredText(lookupCode, "Catalog lookup code"),
    );
    const checkoutFence = this.checkoutFence;
    const transactionEpoch = this.activeCart.read().transactionEpoch;
    let anchor: CatalogIdentity | null = null;
    let addedLineId: string | null = null;
    let selectionRequested = false;
    await this.operations.runCartMutation({
      permissionCode: SALES_PERMISSIONS.addItem,
      action: "scan-add-item",
      eventType: "CART_ITEM_ADD",
      getCart: () => this.cart.getSnapshot(),
      operation: async () => {
        let candidates: readonly LocalCatalogMatch[] = [];
        try {
          candidates = await this.findLocalCatalogCandidates(
            normalizedLookupCode,
          );
        } catch {
          // 本地目录暂不可读仍可进入在线校准；失败结果保持当前购物车。
        }
        this.sessionGuard.assertActive();
        // 授权或本地查询可能跨越结账准备边界，回调真正继续前必须再次检查。
        this.preparedCheckoutGate.assertMutable();
        this.assertCanStartOrContinueTransaction();
        candidates = candidates.filter(
          (candidate) =>
            candidate.storeCode === this.identity.storeCode &&
            normalizeLookupCode(candidate.lookupCodeNormalized) ===
              normalizedLookupCode,
        );
        if (candidates.length > 1) {
          // 一码多商品：不自动加购，等待收银员在候选中选择。
          selectionRequested = true;
          this.publishLookupSelection(attempt, candidates);
          return;
        }
        const match = candidates[0] ?? null;
        anchor = match === null ? null : catalogIdentity(match);
        if (match !== null) {
          const disposition = this.cart.addScannedCatalogItemWithDisposition(
            match,
            this.createLineId(),
          );
          addedLineId = disposition.lineId;
          this.notifyScanTarget(addedLineId);
          this.completeLookupAttempt(attempt, disposition);
        }
      },
    });
    if (selectionRequested) {
      // 服务端单品回查只知道目录胜出项；候选待选时启动远程续作会把胜出项自动加购。
      return null;
    }
    if (!this.preparedCheckoutGate.isMutable()) {
      // 本地命中与 ADD 审计已经完成，但冻结后不得再登记新的目录续作。
      return addedLineId;
    }
    // 本地授权和 ADD 审计完成后才启动远程任务，避免同一价格差异被两类事件重复覆盖。
    const task = this.applyRemoteScanResult({
      lookupCode: normalizedLookupCode,
      anchor,
      checkoutFence,
      transactionEpoch,
      attempt,
    });
    this.trackCatalogWork(task);
    return addedLineId;
    } catch (error) {
      if (this.isCurrentLookupAttempt(attempt)) {
        this.completeLookupAttempt(attempt, {
          kind: hasErrorCode(error, "CATALOG_LOOKUP_NOT_FOUND")
            ? "not-found"
            : "failed-blocked",
        });
      }
      throw error;
    }
  }

  /**
   * 收银员从一码多商品候选中选定商品。与扫码同一加购权限、审计、结账围栏与定位；
   * 按与 WPF 选择弹窗一致的全局合并口径加购（同码且同商品才并入已有行），不同商品分行。
   * 远程回查只返回目录胜出项，锚点为所选商品，身份不同则绝不改写该行。
   */
  public async addLookupCandidate(
    input: Readonly<{
      lookupCode: string;
      productCode: string;
      source?: LookupSource;
    }>,
  ): Promise<string | null> {
    const attempt = this.createLookupAttempt(input.source ?? "manual");
    try {
      this.sessionGuard.assertActive();
      this.preparedCheckoutGate.assertMutable();
      this.assertCanStartOrContinueTransaction();
      const normalizedLookupCode = normalizeLookupCode(
        requiredText(input.lookupCode, "Catalog lookup code"),
      );
      const productKey = normalizeProductKey(
        requiredText(input.productCode, "Catalog product code"),
      );
      const checkoutFence = this.checkoutFence;
      const transactionEpoch = this.activeCart.read().transactionEpoch;
      const added: {
        anchor: CatalogIdentity | null;
        lineId: string | null;
      } = { anchor: null, lineId: null };
      await this.operations.runCartMutation({
        permissionCode: SALES_PERMISSIONS.addItem,
        action: "select-match",
        eventType: "CART_ITEM_ADD",
        getCart: () => this.cart.getSnapshot(),
        operation: async () => {
          const candidates = await this.requireExactCatalogCandidates(
            normalizedLookupCode,
          );
          this.preparedCheckoutGate.assertMutable();
          this.assertCanStartOrContinueTransaction();
          const match = candidates.find(
            (candidate) =>
              normalizeLookupCode(candidate.lookupCodeNormalized) ===
                normalizedLookupCode &&
              normalizeProductKey(candidate.productCode) === productKey,
          );
          if (!match) {
            throw Object.assign(
              new Error("Selected catalog candidate is no longer available."),
              { code: "CATALOG_LOOKUP_NOT_FOUND" },
            );
          }
          const disposition = this.cart.addCatalogItemWithDisposition(
            match,
            this.createLineId(),
          );
          added.anchor = catalogIdentity(match);
          added.lineId = disposition.lineId;
          this.notifyScanTarget(disposition.lineId);
          this.completeLookupAttempt(attempt, disposition);
        },
      });
      if (
        added.anchor === null ||
        !this.catalogRevalidation ||
        !this.preparedCheckoutGate.isMutable()
      ) {
        return added.lineId;
      }
      const task = this.applyRemoteScanResult({
        lookupCode: normalizedLookupCode,
        anchor: added.anchor,
        checkoutFence,
        transactionEpoch,
        attempt,
      });
      this.trackCatalogWork(task);
      return added.lineId;
    } catch (error) {
      this.notifyFailedLookupAttempt(attempt, error);
      throw error;
    }
  }

  public subscribeLookupSelection(
    listener: (selection: SalesLookupSelection) => void,
  ): () => void {
    this.lookupSelectionListeners.add(listener);
    let subscribed = true;
    return () => {
      if (!subscribed) return;
      subscribed = false;
      this.lookupSelectionListeners.delete(listener);
    };
  }

  public getPendingCatalogWorkCount(): number {
    return this.pendingCatalogWork.size;
  }

  public subscribePendingCatalogWork(listener: () => void): () => void {
    this.pendingCatalogWorkListeners.add(listener);
    let subscribed = true;
    return () => {
      if (!subscribed) return;
      subscribed = false;
      this.pendingCatalogWorkListeners.delete(listener);
    };
  }

  public subscribeScanTarget(
    listener: (lineId: string) => void,
  ): () => void {
    this.scanTargetListeners.add(listener);
    let subscribed = true;
    return () => {
      if (!subscribed) return;
      subscribed = false;
      this.scanTargetListeners.delete(listener);
    };
  }

  public subscribeLookupOutcome(
    listener: (outcome: LookupOutcome) => void,
  ): () => void {
    this.lookupOutcomeListeners.add(listener);
    let subscribed = true;
    return () => {
      if (!subscribed) return;
      subscribed = false;
      this.lookupOutcomeListeners.delete(listener);
    };
  }

  public async settlePendingCatalogWork(input: Readonly<{
    timeoutMs: number;
  }>): Promise<Readonly<{ timedOut: boolean }>> {
    if (
      !Number.isSafeInteger(input.timeoutMs) ||
      input.timeoutMs < 0
    ) {
      throw new RangeError(
        "Catalog settlement timeout must be non-negative milliseconds.",
      );
    }
    // 方法调用同步执行到首个 await 前即冻结新写入；成功和超时都保持冻结。
    this.preparedCheckoutGate.prepare();
    const pending = [...this.pendingCatalogWork];
    if (pending.length === 0) {
      this.checkoutFence += 1;
      return { timedOut: false };
    }

    const completed = Promise.allSettled(pending).then(() => false);
    const timeout = Promise.resolve()
      .then(() =>
        this.catalogWorkScheduler.waitForTimeout(input.timeoutMs),
      )
      .then(() => true);
    let timedOut: boolean;
    try {
      timedOut = await Promise.race([completed, timeout]);
    } catch {
      timedOut = true;
    } finally {
      // 已捕获 pending 在等待期内可正常收敛；返回结账 revision 前统一换代，
      // 令任何未被本次 settlement 捕获的迟到任务都不能再改购物车。
      this.checkoutFence += 1;
    }
    return { timedOut };
  }

  public disposePendingCatalogWork(): void {
    this.checkoutFence += 1;
    this.catalogWorkDisposed = true;
    this.preparedCheckoutGate.dispose();
    this.pendingCatalogWork.clear();
    this.pendingCatalogWorkListeners.clear();
    this.scanTargetListeners.clear();
    this.lookupOutcomeListeners.clear();
    this.lookupSelectionListeners.clear();
  }

  public releasePreparedCheckout(): void {
    this.preparedCheckoutGate.release();
  }

  public async addOpenItem(unitPriceCents: number): Promise<void> {
    const attempt = this.createLookupAttempt("manual");
    try {
      if (!Number.isSafeInteger(unitPriceCents) || unitPriceCents <= 0) {
        throw cartMutationRejected(
          "Open item price must be positive AUD cents.",
        );
      }
      this.sessionGuard.assertActive();
      this.preparedCheckoutGate.assertMutable();
      this.assertCanStartOrContinueTransaction();
      await this.operations.runCartMutation({
      permissionCode: SALES_PERMISSIONS.addOpenItem,
      action: "add-open-item",
      eventType: "CART_ITEM_ADD",
      getCart: () => this.cart.getSnapshot(),
      operation: async () => {
        const match = await this.requireExactCatalogItem("OPENITEM");
        this.assertCanStartOrContinueTransaction();
        const lineId = this.createLineId();
        this.cart.addOpenCatalogItem(
          match,
          lineId,
          unitPriceCents,
        );
        this.completeLookupAttempt(attempt, { lineId, kind: "added" });
      },
      });
    } catch (error) {
      this.notifyFailedLookupAttempt(attempt, error);
      throw error;
    }
  }

  public async completeCash(input: Readonly<{
    checkoutIntentId: string;
    cart: CartSnapshot;
    cashTenderedCents: number | null;
  }>): Promise<CashCheckoutResult> {
    this.sessionGuard.assertActive();
    const checkout = this.cashCheckout;
    if (!checkout) {
      return Promise.reject(new Error("Durable cash checkout is unavailable."));
    }
    return this.activeCart.runExclusive(async (lease) => {
      this.sessionGuard.assertActive();
      if (!lease.isCurrentCartSnapshot(input.cart)) {
        throw Object.assign(
          new Error("Cash checkout cart snapshot is stale."),
          { code: ACTIVE_PRICING_CART_STALE_SNAPSHOT },
        );
      }

      const trustedCart = lease.read().cart;
      const completeAuthorizedCash = () =>
        this.operations.runRead(
          PAYMENT_PERMISSION.confirm,
          "confirm-cash-checkout",
          () => {
            // 中文注释：身份只来自已认证组合根，UI 仅能传交易内容与 intent。
            return checkout.complete({ ...input, ...this.identity });
          },
        );
      const result = await this.operations.runRead(
        PAYMENT_PERMISSION.view,
        "view-payment-checkout",
        () =>
          trustedCart.actualAmount.cents === 0
            ? completeAuthorizedCash()
            : this.operations.runRead(
                PAYMENT_PERMISSION.takeCash,
                "take-cash-checkout",
                completeAuthorizedCash,
              ),
      );
      try {
        // 先在同一 lease 内清车，阻止另一个 presenter 在 durable commit 后插入商品。
        lease.clearAfterCommittedOrder(result.orderGuid);
      } catch {
        // 已提交订单不能降级成“现金失败”；Presenter 会再次发出 clear 信号并显示明确告警。
      }
      return result;
    });
  }

  public async holdCart(cart: CartSnapshot): Promise<void> {
    this.sessionGuard.assertActive();
    if (!this.hold) {
      throw new Error("Sales hold is unavailable.");
    }
    await this.hold.hold(cart);
  }

  public async lockTerminal(): Promise<void> {
    this.sessionGuard.assertActive();
    if (!this.lock) {
      throw new Error("Terminal lock is unavailable.");
    }
    await this.lock.lock();
  }

  private requireCatalog(): LocalCatalogPort {
    if (!this.catalog) {
      throw new Error("Local catalog is unavailable.");
    }
    return this.catalog;
  }

  private assertCanStartOrContinueTransaction(): void {
    if (
      this.activeCart.getSnapshot().lines.length === 0 &&
      !this.newTransactionGate.canStartNewTransaction()
    ) {
      throw Object.assign(
        new Error("New transactions are disabled by the iPad policy gate."),
        { code: SALES_NEW_TRANSACTIONS_DISABLED },
      );
    }
  }

  private async findLocalCatalogCandidates(
    lookupCode: string,
  ): Promise<readonly LocalCatalogMatch[]> {
    const catalog = this.requireCatalog();
    if (catalog.findExactCandidates) {
      return catalog.findExactCandidates(lookupCode);
    }
    const match = await catalog.findExact(lookupCode);
    return match === null ? [] : [match];
  }

  /** 精确查码的全部本地候选（仅本店）；没有任何候选时按“商品不存在”失败。 */
  private async requireExactCatalogCandidates(
    lookupCode: string,
  ): Promise<readonly LocalCatalogMatch[]> {
    this.sessionGuard.assertActive();
    const candidates = await this.findLocalCatalogCandidates(lookupCode);
    this.sessionGuard.assertActive();
    const scoped = candidates.filter(
      (candidate) => candidate.storeCode === this.identity.storeCode,
    );
    if (scoped.length === 0) {
      throw Object.assign(
        new Error("Catalog item is unavailable for this store."),
        { code: "CATALOG_LOOKUP_NOT_FOUND" },
      );
    }
    return scoped;
  }

  /** 搜索框输入恰为冲突码时的精确候选；只作增强，读取失败时回到普通关键词搜索。 */
  private async findSearchExactCandidates(
    query: string,
  ): Promise<readonly LocalCatalogMatch[]> {
    const catalog = this.requireCatalog();
    const normalizedQuery = normalizeLookupCode(query);
    if (!normalizedQuery || !catalog.findExactCandidates) return [];
    try {
      const candidates = await catalog.findExactCandidates(normalizedQuery);
      return candidates.filter(
        (candidate) =>
          candidate.storeCode === this.identity.storeCode &&
          normalizeLookupCode(candidate.lookupCodeNormalized) ===
            normalizedQuery,
      );
    } catch {
      return [];
    }
  }

  /**
   * 候选待选不是加购结果：本次查码不再发布加购/失败终态（提示音由页面按候选给出），
   * 也不计入 scan-to-cart 时序。没有页面订阅候选时按失败收口，绝不静默或自动加购任一商品。
   */
  private publishLookupSelection(
    attempt: LookupAttempt,
    candidates: readonly LocalCatalogMatch[],
  ): void {
    if (this.lookupSelectionListeners.size === 0) {
      this.completeLookupAttempt(attempt, { kind: "failed-blocked" });
      return;
    }
    if (attempt.terminalPublished) return;
    attempt.terminalPublished = true;
    try {
      scanTiming.discard(attempt.timingId);
    } catch {
      // timing 是旁路；候选发布不能因指标异常中断。
    }
    const selection: SalesLookupSelection = Object.freeze({
      selectionId: attempt.attemptId,
      lookupCode: candidates[0]!.lookupCode,
      source: attempt.source,
      candidates: Object.freeze(candidates.map(toLookupCandidate)),
    });
    for (const listener of [...this.lookupSelectionListeners]) {
      try {
        listener(selection);
      } catch {
        // 已卸载页面的监听器不能阻断其他订阅者。
      }
    }
  }

  private async requireExactCatalogItem(
    lookupCode: string,
  ): Promise<LocalCatalogMatch> {
    this.sessionGuard.assertActive();
    const match = await this.requireCatalog().findExact(lookupCode);
    this.sessionGuard.assertActive();
    if (!match || match.storeCode !== this.identity.storeCode) {
      throw Object.assign(
        new Error("Catalog item is unavailable for this store."),
        { code: "CATALOG_LOOKUP_NOT_FOUND" },
      );
    }
    return match;
  }

  private async applyRemoteScanResult(input: Readonly<{
    lookupCode: string;
    anchor: CatalogIdentity | null;
    checkoutFence: number;
    transactionEpoch: number;
    attempt: LookupAttempt;
  }>): Promise<void> {
    const revalidation = this.catalogRevalidation;
    if (!revalidation) return;
    try {
      await this.catalogWorkScheduler.yieldToUi();
      const result = await revalidation.revalidate(input.lookupCode);
      await this.catalogWorkScheduler.yieldToUi();
      if (!this.isCurrentRemoteLookupAttempt(input)) return;
      if (result.kind === "not-found") {
        if (
          !(await revalidation.isCurrentBaseSnapshot(
            result.baseSnapshotId,
          )) ||
          !this.isCurrentRemoteLookupAttempt(input)
        ) {
          return;
        }
        this.completeLookupAttempt(input.attempt, { kind: "not-found" });
        return;
      }
      if (result.kind === "unavailable") {
        this.completeLookupAttempt(input.attempt, {
          kind: "failed-blocked",
        });
        return;
      }
      if (result.kind !== "found") {
        this.completeLookupAttempt(input.attempt, {
          kind: "failed-blocked",
        });
        return;
      }
      if (
        !(await revalidation.isCurrentBaseSnapshot(
          result.baseSnapshotId,
        )) ||
        !this.isCurrentRemoteLookupAttempt(input)
      ) {
        return;
      }
      this.sessionGuard.assertActive();
      if (
        input.checkoutFence !== this.checkoutFence ||
        input.transactionEpoch !==
          this.activeCart.read().transactionEpoch
      ) {
        return;
      }
      if (
        result.item.storeCode !== this.identity.storeCode ||
        normalizeLookupCode(result.item.lookupCodeNormalized) !==
        input.lookupCode
      ) {
        this.completeLookupAttempt(input.attempt, {
          kind: "failed-blocked",
        });
        return;
      }

      const anchor = input.anchor;
      if (anchor === null) {
        await this.operations.runTrustedCartMutation({
          permissionCode: SALES_PERMISSIONS.addItem,
          action: "catalog-revalidation-auto-add",
          eventType: "CART_ITEM_ADD",
          getCart: () => this.cart.getSnapshot(),
          operation: () => {
            const disposition =
              this.cart.addScannedCatalogItemFromTrustedContinuationWithDisposition(
              result.item,
              this.createLineId(),
              );
            this.notifyScanTarget(disposition.lineId);
            this.completeLookupAttempt(input.attempt, disposition);
            return disposition.lineId;
          },
        });
        return;
      }
      if (!hasSameCatalogIdentity(anchor, result.item)) return;
      await this.operations.runTrustedCartMutation({
        permissionCode: SALES_PERMISSIONS.addItem,
        action: "catalog-revalidation",
        eventType: "CART_ITEM_PRICE_CHANGE",
        getCart: () => this.cart.getSnapshot(),
        operation: () =>
          this.cart.refreshCatalogItemFromTrustedContinuation(
            anchor,
            result.item,
            input.transactionEpoch,
          ),
      });
    } catch {
      // 在线失败、锁屏、换收银员或支付 exclusive lease 均只保留本地结果。
      if (this.isCurrentRemoteLookupAttempt(input)) {
        this.completeLookupAttempt(input.attempt, {
          kind: "failed-blocked",
        });
      }
    }
  }

  private trackCatalogWork(task: Promise<void>): void {
    if (this.catalogWorkDisposed) {
      void task.catch(() => undefined);
      return;
    }
    this.pendingCatalogWork.add(task);
    this.notifyPendingCatalogWork();
    void task.then(
      () => this.completeCatalogWork(task),
      () => this.completeCatalogWork(task),
    );
  }

  private completeCatalogWork(task: Promise<void>): void {
    if (!this.pendingCatalogWork.delete(task)) return;
    this.notifyPendingCatalogWork();
  }

  private notifyPendingCatalogWork(): void {
    for (const listener of [...this.pendingCatalogWorkListeners]) {
      try {
        listener();
      } catch {
        // 一个已卸载页面的监听器不能阻断其他订阅者或扫码校准。
      }
    }
  }

  private notifyScanTarget(lineId: string): void {
    for (const listener of [...this.scanTargetListeners]) {
      try {
        listener(lineId);
      } catch {
        // 已卸载页面的监听器不能阻断扫码写入或后续目录任务。
      }
    }
  }

  private createLookupAttempt(
    source: LookupSource,
    timingId?: string,
  ): LookupAttempt {
    this.nextLookupAttemptId += 1;
    return {
      attemptId: `lookup-${this.nextLookupAttemptId}`,
      source,
      ...(timingId === undefined ? {} : { timingId }),
      terminalPublished: false,
    };
  }

  private isCurrentLookupAttempt(input: LookupAttempt): boolean {
    void input;
    if (this.catalogWorkDisposed || !this.preparedCheckoutGate.isMutable()) {
      return false;
    }
    try {
      this.sessionGuard.assertActive();
      return true;
    } catch {
      return false;
    }
  }

  private isCurrentRemoteLookupAttempt(input: Readonly<{
    checkoutFence: number;
    transactionEpoch: number;
    attempt: LookupAttempt;
  }>): boolean {
    if (
      this.catalogWorkDisposed ||
      input.checkoutFence !== this.checkoutFence
    ) {
      return false;
    }
    try {
      this.sessionGuard.assertActive();
      return (
        input.transactionEpoch ===
        this.activeCart.read().transactionEpoch
      );
    } catch {
      return false;
    }
  }

  private completeLookupAttempt(
    attempt: LookupAttempt,
    terminal: Readonly<{
      kind: LookupOutcome["kind"];
      lineId?: string;
    }>,
  ): boolean {
    if (attempt.terminalPublished) return false;
    attempt.terminalPublished = true;
    this.notifyLookupOutcome({
      attemptId: attempt.attemptId,
      source: attempt.source,
      ...(attempt.timingId === undefined
        ? {}
        : { timingId: attempt.timingId }),
      ...terminal,
    });
    try {
      scanTiming.complete(
        attempt.timingId,
        terminal.kind === "added" || terminal.kind === "incremented"
          ? "success"
          : "failure",
      );
    } catch {
      // timing 是旁路；权威购物车结果已发布，指标异常不得反转业务结果。
    }
    return true;
  }

  private notifyLookupOutcome(outcome: LookupOutcome): void {
    for (const listener of [...this.lookupOutcomeListeners]) {
      try {
        listener(outcome);
      } catch {
        // 已卸载的提示桥不能中断本次交易的权威结果。
      }
    }
  }

  private notifyFailedLookupAttempt(
    attempt: LookupAttempt,
    error: unknown,
  ): void {
    if (!this.isCurrentLookupAttempt(attempt)) return;
    this.completeLookupAttempt(attempt, {
      kind: hasErrorCode(error, "CATALOG_LOOKUP_NOT_FOUND")
        ? "not-found"
        : "failed-blocked",
    });
  }
}

type CatalogIdentity = Readonly<{
  productCode: string;
  referenceCode: string | null;
  lookupCode: string;
}>;

function catalogIdentity(item: LocalCatalogMatch): CatalogIdentity {
  return {
    productCode: item.productCode,
    referenceCode: item.referenceCode,
    lookupCode: item.lookupCode,
  };
}

function hasErrorCode(error: unknown, code: string): boolean {
  return (
    typeof error === "object" &&
    error !== null &&
    "code" in error &&
    (error as Readonly<{ code?: unknown }>).code === code
  );
}

function hasSameCatalogIdentity(
  expected: CatalogIdentity,
  item: LocalCatalogMatch,
): boolean {
  return (
    normalizeLookupCode(expected.lookupCode) ===
      normalizeLookupCode(item.lookupCodeNormalized) &&
    expected.productCode === item.productCode &&
    expected.referenceCode === item.referenceCode
  );
}

function deriveCapabilities(
  input: ConnectedSalesRuntimeDependencies,
): SalesCapabilities {
  return {
    catalog: input.catalog !== undefined,
    cartEditing: true,
    cashCheckout: input.cashCheckout !== undefined,
    hold: input.hold !== undefined,
    lock: input.lock !== undefined,
  };
}

function toSearchItem(item: LocalCatalogMatch): SalesProductSearchItem {
  return {
    productCode: item.productCode,
    itemNumber: item.itemNumber,
    barcode: item.barcode,
    lookupCode: item.lookupCode,
    displayName: item.displayName,
    unitPriceCents: item.retailPriceCents,
    discountRate: item.discountRate,
  };
}

function toLookupCandidate(item: LocalCatalogMatch): SalesLookupCandidate {
  return Object.freeze({
    productCode: item.productCode,
    itemNumber: item.itemNumber,
    barcode: item.barcode,
    lookupCode: item.lookupCode,
    displayName: item.displayName,
    unitPriceCents: item.retailPriceCents,
    discountRate: item.discountRate,
    priceSource: item.priceSource,
  });
}

function normalizeProductKey(value: string): string {
  return value.trim().toUpperCase();
}

function deduplicateProductSearchMatches(
  matches: readonly LocalCatalogMatch[],
): readonly LocalCatalogMatch[] {
  const seenProductPrices = new Set<string>();
  return matches.filter((match) => {
    // 套装码必须保留为独立售卖选择；门店套装价会使用 multi-code 来源。
    if (isProductSetMatch(match)) return true;
    const key = `${match.productCode.trim().toUpperCase()}\u0000${match.retailPriceCents}`;
    if (seenProductPrices.has(key)) return false;
    seenProductPrices.add(key);
    return true;
  });
}

function isProductSetMatch(match: LocalCatalogMatch): boolean {
  const sourceLabel = match.priceSourceLabel.trim().toLowerCase();
  return match.priceSource === 2 || sourceLabel === "set" || sourceLabel.startsWith("set-");
}

function assertIdentity(identity: ConnectedSalesIdentity): void {
  requiredText(identity.storeCode, "Store code");
  requiredText(identity.deviceCode, "Device code");
  requiredText(identity.cashierId, "Cashier id");
  requiredText(identity.cashierName, "Cashier name");
  if (identity.userGuid !== null) {
    requiredText(identity.userGuid, "Cashier user guid");
  }
}

function requiredText(value: string, label: string): string {
  if (!value.trim()) {
    throw new Error(`${label} is required.`);
  }
  return value;
}

function normalizeLookupCode(value: string): string {
  return value.trim().toUpperCase();
}

/** 目录接口使用 0..1 小数；购物车合同统一保存整数基点，避免把 sale 价伪装成 unitPrice。 */
function discountRateToBasisPoints(
  discountRate: number | null,
): number {
  if (discountRate === null || !Number.isFinite(discountRate)) {
    return 0;
  }
  return Math.min(10_000, Math.max(0, Math.round(discountRate * 10_000)));
}
