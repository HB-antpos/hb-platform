import type {
  PaymentCartLease,
  PaymentCartLeasePort,
} from "../../features/payments/runtime/payment-checkout-runtime";
import { PaymentCheckoutRuntimeError } from "../../features/payments/runtime/payment-checkout-runtime";
import {
  ACTIVE_PRICING_CART_BUSY,
  ACTIVE_PRICING_CART_TERMINAL_RECOVERY_REQUIRED,
  type ActivePricingCartLease,
  ActivePricingCartSession,
} from "../../features/sales/runtime";
import type {
  CartSnapshot,
  PricingCartStateSnapshot,
  RecallActiveBinding,
} from "../contracts";

export type PaymentCartRecoveryMaterial = Readonly<{
  checkoutIntentId: string;
  cart: CartSnapshot;
  pricingState: PricingCartStateSnapshot;
  recallBinding: RecallActiveBinding | null;
}>;

export interface PaymentCartRecoveryMaterialPort {
  /**
   * 只返回可信 SQLCipher 恢复材料。没有阻塞支付时返回 null；多个候选必须在
   * persistence 层失败关闭，不能由组合根猜选。
   */
  findBlockingCart(): Promise<PaymentCartRecoveryMaterial | null>;
}

type HeldPaymentCartLease = {
  readonly publicLease: PaymentCartLease;
  readonly sessionLease: ActivePricingCartLease;
  release(): void;
  operation: Promise<void> | null;
};

/**
 * 把跨多个终端请求的支付生命周期映射到 ActivePricingCartSession 的单个
 * exclusive callback。只有订单耐久完成或明确安全取消，callback 才会结束。
 */
export class ActivePricingCartPaymentLeaseCoordinator
implements PaymentCartLeasePort {
  private held: HeldPaymentCartLease | null = null;
  private acquireInFlight: Promise<PaymentCartLease> | null = null;
  private initializeInFlight: Promise<PaymentCartLease | null> | null = null;
  private initialized = false;

  public constructor(
    private readonly activeCart: ActivePricingCartSession,
    private readonly recovery: PaymentCartRecoveryMaterialPort,
    private readonly createLeaseId: () => string,
  ) {}

  /**
   * 必须在公开 sales/payment facade 前调用。崩溃遗留草稿先恢复完整定价状态，
   * 随即取得长期 exclusive lease，普通销售页面没有短暂可写窗口。
   */
  public initializeRecovery(): Promise<PaymentCartLease | null> {
    if (this.initialized) {
      return Promise.resolve(this.held?.publicLease ?? null);
    }
    if (this.initializeInFlight) return this.initializeInFlight;

    const operation = this.initializeRecoveryOnce().finally(() => {
      if (this.initializeInFlight === operation) {
        this.initializeInFlight = null;
      }
    });
    this.initializeInFlight = operation;
    return operation;
  }

  public async acquireExact(input: {
    checkoutIntentId: string;
    expectedRevision: number;
  }): Promise<PaymentCartLease> {
    return this.acquireExactCore(input, false);
  }

  private async acquireExactCore(input: {
    checkoutIntentId: string;
    expectedRevision: number;
  }, allowRecoveryQuantity: boolean): Promise<PaymentCartLease> {
    const expected = normalizeAcquisition(input);
    if (this.held) {
      return Promise.resolve(assertHeldMatches(this.held, expected));
    }
    if (this.acquireInFlight) {
      return this.acquireInFlight.then((lease) =>
        assertPublicLeaseMatches(lease, expected),
      );
    }

    const operation = this.acquireNew(expected, allowRecoveryQuantity).finally(() => {
      if (this.acquireInFlight === operation) {
        this.acquireInFlight = null;
      }
    });
    this.acquireInFlight = operation;
    return operation;
  }

  public async readExact(lease: PaymentCartLease): Promise<PaymentCartLease> {
    const held = this.requireHeld(lease);
    const current = held.sessionLease.read();
    if (
      current.cart !== held.publicLease.cart ||
      current.pricingState !== held.publicLease.pricingState ||
      current.cart.revision !== held.publicLease.revision
    ) {
      throw paymentLeaseError(
        "PAYMENT_CART_LEASE_CONFLICT",
        "Payment cart changed while its exclusive lease was active.",
      );
    }
    return held.publicLease;
  }

  public async clearAfterCompleted(
    lease: PaymentCartLease,
    orderGuid: string,
  ): Promise<void> {
    const held = this.requireHeld(lease);
    const normalizedOrderGuid = requiredText(orderGuid, "order guid");
    // clear 失败时保持 lease，不允许 UI 把未确认完成的订单当作安全退出。
    held.sessionLease.clearAfterCommittedOrder(normalizedOrderGuid);
    await this.releaseHeld(held);
  }

  public async releaseAfterSafeCancel(
    lease: PaymentCartLease,
    orderGuid: string,
  ): Promise<void> {
    const held = this.requireHeld(lease);
    requiredText(orderGuid, "order guid");
    // DB adapter 已确认该草稿可关闭并耐久完成 CAS；这里仅释放内存写锁。
    await this.releaseHeld(held);
  }

  private async initializeRecoveryOnce(): Promise<PaymentCartLease | null> {
    const material = await this.recovery.findBlockingCart();
    if (!material) {
      this.initialized = true;
      return null;
    }

    const normalized = normalizeRecoveryMaterial(material);
    const current = this.activeCart.read();
    if (
      current.terminalRecoveryRequired !==
        (normalized.recallBinding !== null)
    ) {
      throw paymentLeaseError(
        ACTIVE_PRICING_CART_TERMINAL_RECOVERY_REQUIRED,
        "Payment recovery does not match the active held-order fence.",
      );
    }
    if (current.cart.lines.length > 0) {
      throw paymentLeaseError(
        ACTIVE_PRICING_CART_BUSY,
        "Payment recovery cannot replace a non-empty active cart.",
      );
    }

    const restored = this.activeCart.replace(
      normalized.pricingState,
      normalized.recallBinding,
    );
    assertCartValueMatches(restored.cart, normalized.cart);
    const lease = await this.acquireExactCore({
      checkoutIntentId: normalized.checkoutIntentId,
      expectedRevision: normalized.cart.revision,
    }, true);
    this.initialized = true;
    return lease;
  }

  private acquireNew(input: {
    checkoutIntentId: string;
    expectedRevision: number;
  }, allowRecoveryQuantity: boolean): Promise<PaymentCartLease> {
    let acquiredResolve!: (lease: PaymentCartLease) => void;
    let acquiredReject!: (error: unknown) => void;
    let settled = false;
    const acquired = new Promise<PaymentCartLease>((resolve, reject) => {
      acquiredResolve = resolve;
      acquiredReject = reject;
    });
    let releaseResolve!: () => void;
    const releaseGate = new Promise<void>((resolve) => {
      releaseResolve = resolve;
    });

    const operation = this.activeCart.runExclusive(async (sessionLease) => {
      const snapshot = sessionLease.read();
      if (
        snapshot.cart.revision !== input.expectedRevision ||
        snapshot.pricingState.revision !== input.expectedRevision ||
        snapshot.cart.lines.length === 0
      ) {
        throw paymentLeaseError(
          "PAYMENT_CART_LEASE_CONFLICT",
          "Payment checkout no longer matches the active cart revision.",
        );
      }
      if (!allowRecoveryQuantity) {
        // 此处仍在短暂独占回调内；拒绝小数时回调退出即可释放写锁，收银员能修改数量。
        let itemCount = 0;
        for (const line of snapshot.cart.lines) {
          const quantity = Number(line.quantity);
          itemCount += quantity;
          if (!Number.isSafeInteger(quantity) || quantity <= 0 ||
              quantity > 2_147_483_647 || itemCount > 2_147_483_647) {
            throw new PaymentCheckoutRuntimeError("PAYMENT_QUANTITY_UNSUPPORTED");
          }
        }
        if (snapshot.pricingState.lines.some(line =>
          !Number.isSafeInteger(line.quantity) || line.quantity <= 0 ||
          line.quantity > 2_147_483_647
        )) {
          throw new PaymentCheckoutRuntimeError("PAYMENT_QUANTITY_UNSUPPORTED");
        }
      }
      const publicLease = Object.freeze({
        leaseId: requiredText(this.createLeaseId(), "payment lease id"),
        checkoutIntentId: input.checkoutIntentId,
        revision: input.expectedRevision,
        total: snapshot.cart.actualAmount,
        cart: snapshot.cart,
        pricingState: snapshot.pricingState,
      }) satisfies PaymentCartLease;
      const held: HeldPaymentCartLease = {
        publicLease,
        sessionLease,
        release: releaseResolve,
        operation: null,
      };
      this.held = held;
      settled = true;
      acquiredResolve(publicLease);
      await releaseGate;
    });

    const held = this.held;
    if (held) held.operation = operation;
    void operation.catch((error: unknown) => {
      if (!settled) acquiredReject(error);
      if (this.held === held) this.held = null;
    });
    return acquired;
  }

  private requireHeld(lease: PaymentCartLease): HeldPaymentCartLease {
    const held = this.held;
    if (
      !held ||
      held.publicLease !== lease ||
      held.publicLease.leaseId !== lease.leaseId
    ) {
      throw paymentLeaseError(
        "PAYMENT_CART_LEASE_CONFLICT",
        "Payment cart lease is stale or belongs to another checkout.",
      );
    }
    return held;
  }

  private async releaseHeld(held: HeldPaymentCartLease): Promise<void> {
    if (this.held !== held) {
      throw paymentLeaseError(
        "PAYMENT_CART_LEASE_CONFLICT",
        "Payment cart lease was already released.",
      );
    }
    held.release();
    await held.operation;
    if (this.held === held) this.held = null;
  }
}

function normalizeAcquisition(input: {
  checkoutIntentId: string;
  expectedRevision: number;
}): Readonly<{ checkoutIntentId: string; expectedRevision: number }> {
  if (!Number.isSafeInteger(input.expectedRevision) || input.expectedRevision < 0) {
    throw paymentLeaseError(
      "PAYMENT_CART_LEASE_CONFLICT",
      "Payment cart revision is invalid.",
    );
  }
  return {
    checkoutIntentId: requiredText(
      input.checkoutIntentId,
      "checkout intent id",
    ),
    expectedRevision: input.expectedRevision,
  };
}

function normalizeRecoveryMaterial(
  material: PaymentCartRecoveryMaterial,
): PaymentCartRecoveryMaterial {
  const checkoutIntentId = requiredText(
    material.checkoutIntentId,
    "checkout intent id",
  );
  if (
    material.cart.revision !== material.pricingState.revision ||
    material.cart.mode !== material.pricingState.mode ||
    material.cart.lines.length === 0
  ) {
    throw paymentLeaseError(
      "PAYMENT_CART_LEASE_CONFLICT",
      "Payment recovery material has inconsistent cart state.",
    );
  }
  return {
    checkoutIntentId,
    cart: material.cart,
    pricingState: material.pricingState,
    recallBinding: normalizeRecallBinding(material.recallBinding),
  };
}

function normalizeRecallBinding(
  input: RecallActiveBinding | null,
): RecallActiveBinding | null {
  if (input === null) return null;
  if (!input || typeof input !== "object" || input.kind !== "recalled") {
    throw paymentLeaseError(
      "PAYMENT_CART_LEASE_CONFLICT",
      "Payment recovery recall binding is invalid.",
    );
  }
  return Object.freeze({
    kind: "recalled",
    scope: Object.freeze({
      storeCode: requiredText(input.scope?.storeCode, "recall store code"),
      deviceCode: requiredText(input.scope?.deviceCode, "recall device code"),
    }),
    holdId: requiredText(input.holdId, "recall hold id"),
    recallAttemptId: requiredText(
      input.recallAttemptId,
      "recall attempt id",
    ),
  });
}

function assertHeldMatches(
  held: HeldPaymentCartLease,
  expected: Readonly<{ checkoutIntentId: string; expectedRevision: number }>,
): PaymentCartLease {
  return assertPublicLeaseMatches(held.publicLease, expected);
}

function assertPublicLeaseMatches(
  lease: PaymentCartLease,
  expected: Readonly<{ checkoutIntentId: string; expectedRevision: number }>,
): PaymentCartLease {
  if (
    lease.checkoutIntentId !== expected.checkoutIntentId ||
    lease.revision !== expected.expectedRevision
  ) {
    throw paymentLeaseError(
      "PAYMENT_CART_LEASE_CONFLICT",
      "Another checkout already owns the active cart.",
    );
  }
  return lease;
}

function assertCartValueMatches(
  actual: CartSnapshot,
  expected: CartSnapshot,
): void {
  if (persistedCartValue(actual) !== persistedCartValue(expected)) {
    throw paymentLeaseError(
      "PAYMENT_CART_LEASE_CONFLICT",
      "Recovered pricing state does not reproduce the persisted cart.",
    );
  }
}

function persistedCartValue(cart: CartSnapshot): string {
  return JSON.stringify({
    ...cart,
    lines: cart.lines.map((line) => {
      // discountSource 是 PricingCart 重算生成的展示字段，草稿存储的 normalizeLine 不持久化它；
      // 原先整体比较会让任何"收款中途重启"的冷启动恢复必然失败、设备卡在启动页（门店 1013）。
      // 仅排除此字段，商品、数量、金额、折扣和来源仍必须与耐久快照完全一致（与平板端 #130 同一修法）。
      const persisted = { ...line } as typeof line & { discountSource?: unknown };
      delete persisted.discountSource;
      return persisted;
    }),
  });
}

function requiredText(value: string, label: string): string {
  const normalized = value.trim();
  if (!normalized) {
    throw paymentLeaseError(
      "PAYMENT_CART_LEASE_CONFLICT",
      `Payment ${label} is required.`,
    );
  }
  return normalized;
}

function paymentLeaseError(code: string, message: string): Error & {
  code: string;
} {
  return Object.assign(new Error(message), { code });
}
