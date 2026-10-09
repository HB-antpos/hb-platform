import type {
  CartSnapshot,
  Money,
  PricingCartStateSnapshot,
  PromotionDefinition,
  RecallActiveBinding,
} from "@/core/contracts";
import {
  PricingCart,
  type AddCartItemInput,
  type AddOpenItemInput,
  type CartAddDisposition,
  type MergeCompatibleCartLinesResult,
  type RefreshCatalogItemInput,
} from "@/features/sales/domain";

export const ACTIVE_PRICING_CART_BUSY = "ACTIVE_PRICING_CART_BUSY";
export const ACTIVE_PRICING_CART_DEVICE_SCOPE_INVALIDATED =
  "ACTIVE_PRICING_CART_DEVICE_SCOPE_INVALIDATED";
export const ACTIVE_PRICING_CART_STALE_LEASE =
  "ACTIVE_PRICING_CART_STALE_LEASE";
export const ACTIVE_PRICING_CART_STALE_SNAPSHOT =
  "ACTIVE_PRICING_CART_STALE_SNAPSHOT";
export const ACTIVE_PRICING_CART_TERMINAL_RECOVERY_REQUIRED =
  "ACTIVE_PRICING_CART_TERMINAL_RECOVERY_REQUIRED";
export const ACTIVE_PRICING_CART_UPDATE_TRANSITION =
  "ACTIVE_PRICING_CART_UPDATE_TRANSITION";

const DEFAULT_COMMITTED_ORDER_TOMBSTONE_LIMIT = 128;

export type ActivePricingCartSessionSnapshot = Readonly<{
  sessionRevision: number;
  transactionEpoch: number;
  pricingState: PricingCartStateSnapshot;
  cart: CartSnapshot;
  recallBinding: RecallActiveBinding | null;
  terminalRecoveryRequired: boolean;
}>;

export type ActivePricingCartSessionOptions = Readonly<{
  committedOrderTombstoneLimit?: number;
  /** 默认允许；生产组合根在 App 更新切换期间注入同步写门禁。 */
  canStartMutation?: () => boolean;
}>;

/**
 * lease 只在 runExclusive callback 内有效。callback 结束后继续使用会 fail-closed，
 * 防止异步调用方保留旧购物车写能力。
 */
export interface ActivePricingCartLease {
  read(): ActivePricingCartSessionSnapshot;
  isCurrentCartSnapshot(snapshot: CartSnapshot): boolean;
  blockForRecallRecovery(
    recallBinding: RecallActiveBinding,
  ): ActivePricingCartSessionSnapshot;
  replace(
    pricingState: PricingCartStateSnapshot,
    recallBinding: RecallActiveBinding | null,
  ): ActivePricingCartSessionSnapshot;
  setRecallBinding(
    recallBinding: RecallActiveBinding | null,
  ): ActivePricingCartSessionSnapshot;
  clearAfterCommittedOrder(
    orderGuid: string,
  ): ActivePricingCartSessionSnapshot;
  clearAfterRecoveryParked(
    orderGuid: string,
  ): ActivePricingCartSessionSnapshot;
}

/**
 * 唯一持有 PricingCart 的运行时会话。UI adapter、挂单和结账只能经此处读取或变更，
 * 因此整体换车后不会再有旧 adapter 修改旧实例。
 */
export class ActivePricingCartSession {
  private cart: PricingCart;
  private readonly listeners = new Set<() => void>();
  private readonly leaseReleaseWaiters = new Set<() => void>();
  private readonly committedOrderGuids = new Set<string>();
  private readonly committedOrderGuidOrder: string[] = [];
  private readonly committedOrderTombstoneLimit: number;
  private readonly canStartMutation: () => boolean;
  private activeLeaseToken: symbol | null = null;
  private scopeInvalidated = false;
  private pendingRecallRecovery: RecallActiveBinding | null = null;
  private recallBinding: RecallActiveBinding | null = null;
  private sessionRevision = 0;
  private transactionEpoch = 0;
  private current: ActivePricingCartSessionSnapshot;

  public constructor(
    initialCart: PricingCart,
    private readonly createEmptyCart: () => PricingCart,
    options: ActivePricingCartSessionOptions = {},
  ) {
    this.committedOrderTombstoneLimit = positiveInteger(
      options.committedOrderTombstoneLimit ??
        DEFAULT_COMMITTED_ORDER_TOMBSTONE_LIMIT,
      "Committed order tombstone limit",
    );
    this.canStartMutation = options.canStartMutation ?? (() => true);
    // 中文注释：克隆传入对象，调用方即使仍持有 initialCart 也无法绕过 session 变更。
    this.cart = cloneCart(initialCart);
    this.current = this.buildSnapshot();
  }

  public read(): ActivePricingCartSessionSnapshot {
    this.assertDeviceScopeValid();
    return this.current;
  }

  public getSnapshot(): CartSnapshot {
    this.assertDeviceScopeValid();
    return this.current.cart;
  }

  public subscribe(listener: () => void): () => void {
    this.listeners.add(listener);
    let subscribed = true;
    return () => {
      if (!subscribed) return;
      subscribed = false;
      this.listeners.delete(listener);
    };
  }

  public isCurrentCartSnapshot(snapshot: CartSnapshot): boolean {
    this.assertDeviceScopeValid();
    return snapshot === this.current.cart;
  }

  /** EAS reload 只能在没有正在提交的 exclusive 操作时执行。 */
  public hasPendingExclusiveOperation(): boolean {
    return this.activeLeaseToken !== null;
  }

  /**
   * 设备凭据已切换到另一 scope 后，旧 runtime 绝不能继续提交旧门店购物车。
   * 此操作允许在 active lease 内同步执行：不会等待或抢占 lease，只令当前及未来写操作失效。
   */
  public invalidateForDeviceScope(): boolean {
    if (this.scopeInvalidated) return false;
    // 中文注释：保留只读快照供 UI 收尾，但 runtime 重建前所有写入和支付租约一律拒绝。
    this.scopeInvalidated = true;
    return true;
  }

  /**
   * 只等待调用时已存在的 lease。transition 已同步封闭新写入后可用此事件式等待，
   * 避免支付或恢复长时间持锁时用零延迟 timer 轮询 JS 线程。
   */
  public waitForExclusiveLeaseRelease(): Promise<void> {
    if (this.activeLeaseToken === null) return Promise.resolve();
    return new Promise((resolve) => {
      this.leaseReleaseWaiters.add(resolve);
    });
  }

  public addItem(input: AddCartItemInput): string {
    return this.addItemWithDisposition(input).lineId;
  }

  public addItemWithDisposition(
    input: AddCartItemInput,
  ): CartAddDisposition {
    this.assertIdle();
    this.assertTerminalRecoveryResolved();
    this.assertCanAdvance();
    const disposition = this.cart.addItemWithDisposition(input);
    this.commitCurrentCartMutation();
    return disposition;
  }

  public addScannedItem(input: AddCartItemInput): string {
    return this.addScannedItemWithDisposition(input).lineId;
  }

  public addScannedItemWithDisposition(
    input: AddCartItemInput,
  ): CartAddDisposition {
    this.assertIdle();
    this.assertTerminalRecoveryResolved();
    this.assertCanAdvance();
    const disposition = this.cart.addScannedItemWithDisposition(input);
    this.commitCurrentCartMutation();
    return disposition;
  }

  public hasMergeCompatibleLines(): boolean {
    this.assertDeviceScopeValid();
    return this.cart.hasMergeCompatibleLines();
  }

  public mergeCompatibleLines(): MergeCompatibleCartLinesResult {
    this.assertIdle();
    this.assertTerminalRecoveryResolved();
    this.assertCanAdvance();
    const result = this.cart.mergeCompatibleLines();
    if (result.removedLineCount > 0) {
      this.commitCurrentCartMutation();
    }
    return result;
  }

  public addOpenItem(input: AddOpenItemInput): string {
    this.assertIdle();
    this.assertTerminalRecoveryResolved();
    this.assertCanAdvance();
    const lineId = this.cart.addOpenItem(input);
    this.commitCurrentCartMutation();
    return lineId;
  }

  /**
   * 远程校准在克隆车上完成全部领域重算，确认仍是扫码开始时的交易后才单次交换。
   */
  public refreshCatalogItem(
    input: RefreshCatalogItemInput,
    expectedTransactionEpoch: number,
  ): readonly string[] {
    this.assertIdle();
    this.assertTerminalRecoveryResolved();
    if (expectedTransactionEpoch !== this.transactionEpoch) return [];
    const replacement = cloneCart(this.cart);
    const updatedLineIds = replacement.refreshCatalogItem(input);
    if (updatedLineIds.length === 0) return [];
    this.swapCart(replacement);
    return updatedLineIds;
  }

  public increaseLine(lineId: string): boolean {
    this.assertIdle();
    this.assertTerminalRecoveryResolved();
    this.assertCanAdvance();
    const changed = this.cart.increaseLine(lineId);
    if (changed) this.commitCurrentCartMutation();
    return changed;
  }

  public decreaseLine(lineId: string): boolean {
    this.assertIdle();
    this.assertTerminalRecoveryResolved();
    this.assertCanAdvance();
    const changed = this.cart.decreaseLine(lineId);
    if (changed) this.commitCurrentCartMutation();
    return changed;
  }

  public removeLine(lineId: string): boolean {
    this.assertIdle();
    this.assertTerminalRecoveryResolved();
    this.assertCanAdvance();
    const changed = this.cart.removeLine(lineId);
    if (changed) this.commitCurrentCartMutation();
    return changed;
  }

  public setLineDiscountPercentBps(
    lineId: string,
    basisPoints: number,
  ): boolean {
    this.assertIdle();
    this.assertTerminalRecoveryResolved();
    this.assertCanAdvance();
    const changed = this.cart.setLineDiscountPercentBps(lineId, basisPoints);
    if (changed) this.commitCurrentCartMutation();
    return changed;
  }

  public setLineQuantity(lineId: string, quantity: number): boolean {
    this.assertIdle();
    this.assertTerminalRecoveryResolved();
    this.assertCanAdvance();
    const changed = this.cart.setLineQuantity(lineId, quantity);
    if (changed) this.commitCurrentCartMutation();
    return changed;
  }

  public setLineUnitPrice(lineId: string, unitPrice: Money): boolean {
    this.assertIdle();
    this.assertTerminalRecoveryResolved();
    this.assertCanAdvance();
    const changed = this.cart.setLineUnitPrice(lineId, unitPrice);
    if (changed) this.commitCurrentCartMutation();
    return changed;
  }

  public setLineDiscountAmount(
    lineId: string,
    discount: Money,
  ): boolean {
    this.assertIdle();
    this.assertTerminalRecoveryResolved();
    this.assertCanAdvance();
    const changed = this.cart.setLineDiscountAmount(lineId, discount);
    if (changed) this.commitCurrentCartMutation();
    return changed;
  }

  public setOrderDiscountAmount(discount: Money): boolean {
    this.assertIdle();
    this.assertTerminalRecoveryResolved();
    this.assertCanAdvance();
    const changed = this.cart.setOrderDiscountAmount(discount);
    if (changed) this.commitCurrentCartMutation();
    return changed;
  }

  public setOrderDiscountPercentBps(basisPoints: number): boolean {
    this.assertIdle();
    this.assertTerminalRecoveryResolved();
    this.assertCanAdvance();
    const changed = this.cart.setOrderDiscountPercentBps(basisPoints);
    if (changed) this.commitCurrentCartMutation();
    return changed;
  }

  /**
   * 促销快照不能直接改写正在被页面读取的 PricingCart。先在副本上校验并重算，
   * 成功后才一次性交换，故损坏快照不会留下半更新的金额或折扣。
   */
  public applyPromotionSnapshot(
    promotions: readonly PromotionDefinition[],
    asOfIso: string,
  ): ActivePricingCartSessionSnapshot {
    this.assertIdle();
    this.assertTerminalRecoveryResolved();
    const replacement = cloneCart(this.cart);
    replacement.setPromotions(promotions, asOfIso);
    return this.swapCart(replacement);
  }

  /**
   * 手动清车保留当前模式、促销和定价时点；已召回挂单必须先由挂单流程释放，
   * 不能在销售页静默丢失 terminal fence。
   */
  public clearManually(): boolean {
    this.assertIdle();
    this.assertTerminalRecoveryResolved();
    if (this.recallBinding !== null) {
      throw terminalRecoveryError(
        "A recalled cart must be released before it can be cleared.",
      );
    }
    if (this.current.cart.lines.length === 0) return false;
    this.assertCanAdvance();
    const pricingState = this.cart.stateSnapshot();
    if (pricingState.revision >= Number.MAX_SAFE_INTEGER) {
      throw new RangeError("Pricing cart revision is exhausted.");
    }
    const replacement = PricingCart.restore({
      ...pricingState,
      revision: pricingState.revision + 1,
      lines: [],
    });
    this.advanceTransactionEpoch();
    this.cart = replacement;
    this.commitCurrentCartMutation();
    return true;
  }

  /**
   * 先在隔离的新 PricingCart 中完成全部领域验证，再一次性交换并发布。
   * 任何验证异常都不会改变当前车、revision 或订阅者视图。
   */
  public replace(
    pricingState: PricingCartStateSnapshot,
    recallBinding: RecallActiveBinding | null = null,
  ): ActivePricingCartSessionSnapshot {
    this.assertIdle();
    this.assertReplacementBindingAllowed(recallBinding);
    const replacement = PricingCart.restore(pricingState);
    return this.replaceForNewTransaction(replacement, recallBinding);
  }

  /**
   * 启动发现 RecallActive 时只保存不可见的精确 binding 并锁住普通编辑。
   * 冻结购物车必须等主管 recover/release 在 exclusive lease 内显式恢复。
   */
  public blockForRecallRecovery(
    recallBinding: RecallActiveBinding,
  ): ActivePricingCartSessionSnapshot {
    this.assertIdle();
    return this.blockForRecallRecoveryInternal(recallBinding);
  }

  public clearAfterCommittedOrder(
    orderGuid: string,
  ): ActivePricingCartSessionSnapshot {
    this.assertIdle();
    return this.clearAfterCommittedOrderInternal(orderGuid);
  }

  /**
   * exclusive 操作不排队：排队后再执行会基于过期购物车做耐久写，因此忙时直接拒绝。
   * finally 总会释放 lease；callback 已启动后不会伪装成可撤销。
   */
  public runExclusive<T>(
    operation: (lease: ActivePricingCartLease) => T | Promise<T>,
  ): Promise<T> {
    try {
      this.assertMutationAllowed();
    } catch (error) {
      return Promise.reject(error);
    }
    return this.runExclusiveInternal(operation);
  }

  /**
   * 只供生产组合根的 App 更新 transition 使用：全局写门已先关闭普通 mutation，
   * 因此该入口仅绕过外部 guard，仍遵守同一 active lease 与过期 lease 规则。
   */
  public runUpdateTransitionExclusive<T>(
    operation: (lease: ActivePricingCartLease) => T | Promise<T>,
  ): Promise<T> {
    return this.runExclusiveInternal(operation);
  }

  private runExclusiveInternal<T>(
    operation: (lease: ActivePricingCartLease) => T | Promise<T>,
  ): Promise<T> {
    this.assertDeviceScopeValid();
    if (this.activeLeaseToken !== null) {
      return Promise.reject(
        codedError(
          ACTIVE_PRICING_CART_BUSY,
          "Active pricing cart is busy.",
        ),
      );
    }
    const token = Symbol("active-pricing-cart-lease");
    this.activeLeaseToken = token;
    const lease = this.createLease(token);

    let result: T | Promise<T>;
    try {
      result = operation(lease);
    } catch (error) {
      this.releaseLease(token);
      return Promise.reject(error);
    }

    return Promise.resolve(result).finally(() => {
      this.releaseLease(token);
    });
  }

  private createLease(token: symbol): ActivePricingCartLease {
    return Object.freeze({
      read: () => {
        this.assertLease(token);
        return this.current;
      },
      isCurrentCartSnapshot: (snapshot: CartSnapshot) => {
        this.assertLease(token);
        return snapshot === this.current.cart;
      },
      blockForRecallRecovery: (recallBinding: RecallActiveBinding) => {
        this.assertLease(token);
        return this.blockForRecallRecoveryInternal(recallBinding);
      },
      replace: (
        pricingState: PricingCartStateSnapshot,
        recallBinding: RecallActiveBinding | null,
      ) => {
        this.assertLease(token);
        this.assertReplacementBindingAllowed(recallBinding);
        const replacement = PricingCart.restore(pricingState);
        return this.replaceForNewTransaction(replacement, recallBinding);
      },
      setRecallBinding: (recallBinding: RecallActiveBinding | null) => {
        this.assertLease(token);
        return this.setRecallBindingInternal(recallBinding);
      },
      clearAfterCommittedOrder: (orderGuid: string) => {
        // 中文注释：设备 scope 切换后，只有仍由本次支付持有的原 lease
        // 可为已耐久完成订单收尾；其他 lease 操作仍必须先通过 scope 校验。
        this.assertActiveLeaseToken(token);
        return this.clearAfterCommittedOrderInternal(orderGuid);
      },
      clearAfterRecoveryParked: (orderGuid: string) => {
        this.assertActiveLeaseToken(token);
        requiredText(orderGuid, "Parked recovery order guid");
        return this.clearCurrentCartWithoutCompletionTombstone();
      },
    });
  }

  private clearAfterCommittedOrderInternal(
    orderGuid: string,
  ): ActivePricingCartSessionSnapshot {
    this.assertTerminalRecoveryResolved();
    const normalizedOrderGuid = requiredText(
      orderGuid,
      "Committed order guid",
    ).toLowerCase();
    if (this.committedOrderGuids.has(normalizedOrderGuid)) {
      return this.current;
    }

    const next = this.clearCurrentCartWithoutCompletionTombstone();
    this.rememberCommittedOrder(normalizedOrderGuid);
    return next;
  }

  /** 移交恢复中心只清当前车，不可污染订单完成幂等墓碑。 */
  private clearCurrentCartWithoutCompletionTombstone(): ActivePricingCartSessionSnapshot {
    const emptyCart = cloneCart(this.createEmptyCart());
    if (emptyCart.snapshot().lines.length > 0) {
      throw new Error("Empty cart factory must return a cart without lines.");
    }

    this.assertCanAdvance();
    this.advanceTransactionEpoch();
    this.cart = emptyCart;
    this.recallBinding = null;
    this.commitCurrentCartMutation();
    return this.current;
  }

  private swapCart(
    replacement: PricingCart,
    recallBinding: RecallActiveBinding | null = this.recallBinding,
  ): ActivePricingCartSessionSnapshot {
    const nextBinding = cloneRecallBinding(recallBinding);
    this.assertReplacementBindingAllowed(nextBinding);
    this.assertCanAdvance();
    this.cart = replacement;
    this.recallBinding = nextBinding;
    this.pendingRecallRecovery = null;
    this.commitCurrentCartMutation();
    return this.current;
  }

  private replaceForNewTransaction(
    replacement: PricingCart,
    recallBinding: RecallActiveBinding | null,
  ): ActivePricingCartSessionSnapshot {
    const previousEpoch = this.transactionEpoch;
    this.advanceTransactionEpoch();
    try {
      return this.swapCart(replacement, recallBinding);
    } catch (error) {
      this.transactionEpoch = previousEpoch;
      throw error;
    }
  }

  private blockForRecallRecoveryInternal(
    recallBinding: RecallActiveBinding,
  ): ActivePricingCartSessionSnapshot {
    const next = cloneRecallBinding(recallBinding);
    if (this.pendingRecallRecovery) {
      if (sameRecallBinding(this.pendingRecallRecovery, next)) {
        return this.current;
      }
      throw terminalRecoveryError(
        "Another terminal recall recovery is already pending.",
      );
    }
    if (this.recallBinding) {
      if (sameRecallBinding(this.recallBinding, next)) {
        return this.current;
      }
      throw terminalRecoveryError(
        "Another recalled cart is already active.",
      );
    }
    if (
      this.current.cart.lines.length > 0 ||
      this.current.pricingState.lines.length > 0
    ) {
      throw terminalRecoveryError(
        "Terminal recall recovery requires an empty active cart.",
      );
    }

    this.assertCanAdvance();
    // 中文注释：pending binding 只保存在私有字段，公开快照仅暴露阻断布尔值。
    this.pendingRecallRecovery = next;
    this.commitCurrentCartMutation();
    return this.current;
  }

  private setRecallBindingInternal(
    recallBinding: RecallActiveBinding | null,
  ): ActivePricingCartSessionSnapshot {
    this.assertTerminalRecoveryResolved();
    const next = cloneRecallBinding(recallBinding);
    if (next && !sameRecallBinding(this.recallBinding, next)) {
      throw terminalRecoveryError(
        "Recall binding can only be activated by an exact pending recovery.",
      );
    }
    if (sameRecallBinding(this.recallBinding, next)) {
      return this.current;
    }
    this.assertCanAdvance();
    this.recallBinding = next;
    this.commitCurrentCartMutation();
    return this.current;
  }

  private commitCurrentCartMutation(): void {
    this.sessionRevision += 1;
    this.current = this.buildSnapshot();
    this.publish();
  }

  private buildSnapshot(): ActivePricingCartSessionSnapshot {
    return deepFreeze({
      sessionRevision: this.sessionRevision,
      transactionEpoch: this.transactionEpoch,
      pricingState: this.cart.stateSnapshot(),
      cart: this.cart.snapshot(),
      recallBinding: cloneRecallBinding(this.recallBinding),
      terminalRecoveryRequired: this.pendingRecallRecovery !== null,
    });
  }

  private publish(): void {
    for (const listener of [...this.listeners]) {
      try {
        listener();
      } catch {
        // 中文注释：一个页面卸载或监听器异常不能阻断其他页面刷新，更不能回滚领域变更。
      }
    }
  }

  private rememberCommittedOrder(orderGuid: string): void {
    this.committedOrderGuids.add(orderGuid);
    this.committedOrderGuidOrder.push(orderGuid);
    while (
      this.committedOrderGuidOrder.length >
      this.committedOrderTombstoneLimit
    ) {
      const evicted = this.committedOrderGuidOrder.shift();
      if (evicted !== undefined) this.committedOrderGuids.delete(evicted);
    }
  }

  private assertIdle(): void {
    this.assertMutationAllowed();
    if (this.activeLeaseToken !== null) {
      throw codedError(
        ACTIVE_PRICING_CART_BUSY,
        "Active pricing cart is busy.",
      );
    }
  }

  private assertMutationAllowed(): void {
    this.assertDeviceScopeValid();
    if (!this.canStartMutation()) {
      throw codedError(
        ACTIVE_PRICING_CART_UPDATE_TRANSITION,
        "Active pricing cart is blocked by an app update transition.",
      );
    }
  }

  private assertLease(token: symbol): void {
    this.assertDeviceScopeValid();
    this.assertActiveLeaseToken(token);
  }

  private assertActiveLeaseToken(token: symbol): void {
    if (this.activeLeaseToken !== token) {
      throw codedError(
        ACTIVE_PRICING_CART_STALE_LEASE,
        "Active pricing cart lease is no longer valid.",
      );
    }
  }

  private assertDeviceScopeValid(): void {
    if (this.scopeInvalidated) {
      throw codedError(
        ACTIVE_PRICING_CART_DEVICE_SCOPE_INVALIDATED,
        "Active pricing cart is invalidated by a device scope change.",
      );
    }
  }

  private assertCanAdvance(): void {
    if (this.sessionRevision >= Number.MAX_SAFE_INTEGER) {
      throw new RangeError("Active pricing cart revision is exhausted.");
    }
  }

  private advanceTransactionEpoch(): void {
    if (this.transactionEpoch >= Number.MAX_SAFE_INTEGER) {
      throw new RangeError("Active cart transaction epoch is exhausted.");
    }
    this.transactionEpoch += 1;
  }

  private assertReplacementBindingAllowed(
    recallBinding: RecallActiveBinding | null,
  ): void {
    const requested = cloneRecallBinding(recallBinding);
    if (this.pendingRecallRecovery) {
      if (!sameRecallBinding(this.pendingRecallRecovery, requested)) {
        throw terminalRecoveryError(
          "Exact pending recall binding is required to restore the cart.",
        );
      }
      return;
    }
    if (!sameRecallBinding(this.recallBinding, requested)) {
      throw terminalRecoveryError(
        "Recall binding transition requires a pending recovery.",
      );
    }
  }

  private assertTerminalRecoveryResolved(): void {
    if (this.pendingRecallRecovery) {
      throw terminalRecoveryError(
        "Terminal recall recovery must be resolved before editing the cart.",
      );
    }
  }

  private releaseLease(token: symbol): void {
    if (this.activeLeaseToken === token) {
      this.activeLeaseToken = null;
      for (const resolve of this.leaseReleaseWaiters) resolve();
      this.leaseReleaseWaiters.clear();
    }
  }
}

function cloneCart(cart: PricingCart): PricingCart {
  return PricingCart.restore(cart.stateSnapshot());
}

function cloneRecallBinding(
  binding: RecallActiveBinding | null,
): RecallActiveBinding | null {
  if (!binding) return null;
  return {
    kind: "recalled",
    holdId: requiredText(binding.holdId, "Held order id"),
    recallAttemptId: requiredText(
      binding.recallAttemptId,
      "Recall attempt id",
    ),
    scope: {
      storeCode: requiredText(binding.scope.storeCode, "Recall store code"),
      deviceCode: requiredText(binding.scope.deviceCode, "Recall device code"),
    },
  };
}

function sameRecallBinding(
  left: RecallActiveBinding | null,
  right: RecallActiveBinding | null,
): boolean {
  return (
    left === right ||
    (left !== null &&
      right !== null &&
      left.holdId === right.holdId &&
      left.recallAttemptId === right.recallAttemptId &&
      left.scope.storeCode === right.scope.storeCode &&
      left.scope.deviceCode === right.scope.deviceCode)
  );
}

function codedError(code: string, message: string): Error {
  return Object.assign(new Error(message), { code });
}

function terminalRecoveryError(message: string): Error {
  return codedError(
    ACTIVE_PRICING_CART_TERMINAL_RECOVERY_REQUIRED,
    message,
  );
}

function positiveInteger(value: number, label: string): number {
  if (!Number.isSafeInteger(value) || value <= 0) {
    throw new RangeError(`${label} must be a positive integer.`);
  }
  return value;
}

function requiredText(value: string, label: string): string {
  const normalized = value.trim();
  if (!normalized) {
    throw new Error(`${label} is required.`);
  }
  return normalized;
}

function deepFreeze<T>(value: T): T {
  if (value === null || typeof value !== "object" || Object.isFrozen(value)) {
    return value;
  }
  for (const nested of Object.values(value)) {
    deepFreeze(nested);
  }
  return Object.freeze(value);
}
