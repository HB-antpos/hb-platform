import type {
  PaymentCartLease,
  PaymentCartLeasePort,
} from "../../features/payments/runtime/payment-checkout-runtime";
import { PaymentCheckoutRuntimeError } from "../../features/payments/runtime/payment-checkout-runtime";
import { PricingCart } from "../../features/sales/domain";
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

/**
 * 冷启动时耐久草稿无法由当前定价引擎精确复现，已改按耐久购物车持有支付 lease。
 * differences 只含字段路径，不含商品或金额值，可直接进入中心日志。
 */
export type PaymentCartDurableRecoveryFallback = Readonly<{
  checkoutIntentId: string;
  differences: readonly string[];
}>;

type HeldPaymentCartLease = {
  readonly publicLease: PaymentCartLease;
  readonly sessionLease: ActivePricingCartLease;
  /** 取得 lease 时销售会话的快照引用；普通 lease 与 publicLease 是同一对象。 */
  readonly sessionCart: CartSnapshot;
  readonly sessionPricingState: PricingCartStateSnapshot;
  release(): void;
  operation: Promise<void> | null;
};

type HeldLeaseMaterial = Readonly<{
  publicLease: PaymentCartLease;
  sessionCart: CartSnapshot;
  sessionPricingState: PricingCartStateSnapshot;
}>;

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
    private readonly onDurableRecoveryFallback: (
      fallback: PaymentCartDurableRecoveryFallback,
    ) => void = () => undefined,
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
      current.cart !== held.sessionCart ||
      current.pricingState !== held.sessionPricingState ||
      current.cart.revision !== held.sessionCart.revision
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

    const lease = await this.restoreRecoveryLease(normalized);
    this.initialized = true;
    return lease;
  }

  /**
   * 把耐久恢复材料恢复为支付 lease。可精确重算时恢复原购物车；否则改按耐久购物车持有 lease，
   * 不抛出让 runtime 初始化失败（门店 1013 设备曾因此永久卡在启动页）。
   */
  private async restoreRecoveryLease(
    normalized: PaymentCartRecoveryMaterial,
  ): Promise<PaymentCartLease> {
    // 先在隔离的 PricingCart 中重算，不可复现时销售会话保持原样，再走耐久降级。
    const differences = replayDifferences(normalized);
    if (differences.length > 0) {
      const lease = await this.acquireDurableRecovery(normalized);
      try {
        this.onDurableRecoveryFallback(Object.freeze({
          checkoutIntentId: normalized.checkoutIntentId,
          differences: Object.freeze(differences),
        }));
      } catch {
        // 诊断旁路故障不能撤销已取得的恢复 lease。
      }
      return lease;
    }

    const restored = this.activeCart.replace(
      normalized.pricingState,
      normalized.recallBinding,
    );
    assertCartValueMatches(restored.cart, normalized.cart);
    return this.acquireExactCore({
      checkoutIntentId: normalized.checkoutIntentId,
      expectedRevision: normalized.cart.revision,
    }, true);
  }

  /**
   * 耐久草稿无法被当前代码精确重算时（例如定价快照新增派生字段、定价引擎跨版本变化），
   * 不能让 runtime 初始化失败把设备永久卡在启动页，也不能把重算出的不同金额展示给收银员。
   * 这里以 SQLCipher 中已与订单行核对过的耐久 cart 作为支付 lease 内容，销售车清空并由该 lease
   * 独占锁住；后续恢复、安全取消、放弃草稿仍由支付运行时按草稿金额逐步交叉核对。
   */
  private acquireDurableRecovery(
    material: PaymentCartRecoveryMaterial,
  ): Promise<PaymentCartLease> {
    return this.acquireHeld((sessionLease) => {
      if (sessionLease.read().cart.lines.length > 0) {
        throw paymentLeaseError(
          ACTIVE_PRICING_CART_BUSY,
          "Payment recovery cannot replace a non-empty active cart.",
        );
      }
      // 空车（与生产组合根的空车工厂一致）承接耐久挂单 binding：解除 RecallActive 围栏后，
      // 完成订单时才能正常清车；不读取可能已不被接受的 pricingState。
      const session = sessionLease.replace(
        new PricingCart().stateSnapshot(),
        material.recallBinding,
      );
      return {
        publicLease: Object.freeze({
          leaseId: requiredText(this.createLeaseId(), "payment lease id"),
          checkoutIntentId: material.checkoutIntentId,
          revision: material.cart.revision,
          total: material.cart.actualAmount,
          cart: material.cart,
          pricingState: material.pricingState,
        }) satisfies PaymentCartLease,
        sessionCart: session.cart,
        sessionPricingState: session.pricingState,
      };
    });
  }

  private acquireNew(input: {
    checkoutIntentId: string;
    expectedRevision: number;
  }, allowRecoveryQuantity: boolean): Promise<PaymentCartLease> {
    return this.acquireHeld((sessionLease) => {
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
      return {
        publicLease: Object.freeze({
          leaseId: requiredText(this.createLeaseId(), "payment lease id"),
          checkoutIntentId: input.checkoutIntentId,
          revision: input.expectedRevision,
          total: snapshot.cart.actualAmount,
          cart: snapshot.cart,
          pricingState: snapshot.pricingState,
        }) satisfies PaymentCartLease,
        sessionCart: snapshot.cart,
        sessionPricingState: snapshot.pricingState,
      };
    });
  }

  /** 在单个 exclusive callback 内准备 lease，并持有到完成或安全取消才释放。 */
  private acquireHeld(
    prepare: (sessionLease: ActivePricingCartLease) => HeldLeaseMaterial,
  ): Promise<PaymentCartLease> {
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
      const prepared = prepare(sessionLease);
      const held: HeldPaymentCartLease = {
        publicLease: prepared.publicLease,
        sessionLease,
        sessionCart: prepared.sessionCart,
        sessionPricingState: prepared.sessionPricingState,
        release: releaseResolve,
        operation: null,
      };
      this.held = held;
      settled = true;
      acquiredResolve(prepared.publicLease);
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

/**
 * 返回耐久 cart 与当前定价引擎重算结果不一致的字段路径；空数组表示可精确复现。
 * 重算本身抛错（定价快照已不被当前代码接受）也视为不可复现，交给耐久降级处理。
 */
function replayDifferences(material: PaymentCartRecoveryMaterial): string[] {
  let replayed: CartSnapshot;
  try {
    replayed = PricingCart.restore(material.pricingState).snapshot();
  } catch {
    return ["pricingState"];
  }
  if (persistedCartValue(replayed) === persistedCartValue(material.cart)) {
    return [];
  }
  const differences: string[] = [];
  const replayedRecord = replayed as unknown as Record<string, unknown>;
  const persistedRecord = material.cart as unknown as Record<string, unknown>;
  for (const key of unionKeys(replayedRecord, persistedRecord)) {
    if (key === "lines") continue;
    if (JSON.stringify(replayedRecord[key]) !== JSON.stringify(persistedRecord[key])) {
      differences.push(key);
    }
  }
  if (replayed.lines.length !== material.cart.lines.length) {
    differences.push("lines.length");
  }
  const lineCount = Math.min(replayed.lines.length, material.cart.lines.length);
  for (let index = 0; index < lineCount; index += 1) {
    const replayedLine = replayed.lines[index] as unknown as Record<string, unknown>;
    const persistedLine = material.cart.lines[index] as unknown as Record<string, unknown>;
    for (const key of unionKeys(replayedLine, persistedLine)) {
      if (key === "discountSource") continue;
      if (JSON.stringify(replayedLine[key]) !== JSON.stringify(persistedLine[key])) {
        differences.push(`lines[${index}].${key}`);
      }
    }
  }
  // 仅字段顺序不同也会让整体字符串不等；此时保留一个可识别的路径而不是空数组。
  return differences.length > 0 ? differences.slice(0, 20) : ["fieldOrder"];
}

function unionKeys(
  left: Record<string, unknown>,
  right: Record<string, unknown>,
): string[] {
  return [...new Set([...Object.keys(left), ...Object.keys(right)])];
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
