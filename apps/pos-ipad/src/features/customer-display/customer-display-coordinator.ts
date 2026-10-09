import type {
  CustomerDisplayFrame,
  CustomerDisplayPublishResult,
} from "./customer-display-publisher";

import type {
  CartSnapshot,
  CustomerDisplaySnapshot,
} from "@/core/contracts";

export interface CustomerDisplayCartPort {
  getSnapshot(): CartSnapshot;
  subscribe(listener: () => void): () => void;
}

/**
 * 广告位：将要展示的购物车有商品行 = checkout（收银右侧位），否则 idle（空闲全屏位）。
 * 付款/找零/成功沿用最后一个非空购物车，因此仍是 checkout。
 */
export type CustomerDisplayAdvertSlot = "idle" | "checkout";

export type CustomerDisplayAdvertSlotListener = (
  slot: CustomerDisplayAdvertSlot,
) => void;

export interface CustomerDisplayPublisherPort {
  publish(frame: CustomerDisplayFrame): Promise<CustomerDisplayPublishResult>;
}

/**
 * 客显状态仅跟随共享购物车和显式的支付阶段，不持有支付引用或顾客资料。
 * 支付完成清车后仍保留最后一个非空快照，直到主屏明确开始下一笔交易。
 */
export class CustomerDisplayCoordinator {
  private advert: CustomerDisplaySnapshot["advert"] = null;
  private advertSlot: CustomerDisplayAdvertSlot | null = null;
  private advertSlotListener: CustomerDisplayAdvertSlotListener | null = null;
  private changeCents = 0;
  private destroyed = false;
  private initialized = false;
  private lastNonEmptyCart: CartSnapshot | null = null;
  private mode: CustomerDisplaySnapshot["mode"] = "idle";
  private unsubscribeCart: (() => void) | null = null;

  public constructor(
    private readonly cart: CustomerDisplayCartPort,
    private readonly publisher: CustomerDisplayPublisherPort,
  ) {}

  public async initialize(): Promise<void> {
    this.assertAlive();
    if (this.initialized) return;
    this.initialized = true;
    this.unsubscribeCart = this.cart.subscribe(() => {
      this.onCartChanged();
    });
    const current = this.readCart();
    this.mode = current.lines.length > 0 ? "cart" : "idle";
    await this.publish(current);
  }

  public showCart(): Promise<CustomerDisplayPublishResult> {
    this.assertReady();
    const current = this.readCart();
    this.changeCents = 0;
    this.mode = current.lines.length > 0 ? "cart" : "idle";
    return this.publish(current);
  }

  public showPayment(): Promise<CustomerDisplayPublishResult> {
    this.assertReady();
    this.changeCents = 0;
    this.mode = "payment";
    return this.publish(this.transactionCart());
  }

  public showChange(changeCents: number): Promise<CustomerDisplayPublishResult> {
    this.assertReady();
    this.changeCents = changeCents;
    this.mode = "change";
    return this.publish(this.transactionCart());
  }

  public showSuccess(
    changeCents: number,
  ): Promise<CustomerDisplayPublishResult> {
    this.assertReady();
    this.changeCents = changeCents;
    this.mode = "success";
    return this.publish(this.transactionCart());
  }

  public setAdvert(
    advert: CustomerDisplaySnapshot["advert"],
  ): Promise<CustomerDisplayPublishResult> {
    this.assertReady();
    this.advert = advert;
    return this.publish(
      this.mode === "idle" || this.mode === "cart"
        ? this.readCart()
        : this.transactionCart(),
    );
  }

  /**
   * 注册广告位变化监听（用 setter 而不是构造参数，避免与播放层互相依赖的构造顺序问题）。
   * 首次发布与之后每次位置变化都会通知；传 null 取消。
   */
  public setAdvertSlotListener(
    listener: CustomerDisplayAdvertSlotListener | null,
  ): void {
    this.advertSlotListener = listener;
    // 新监听器还没收到过任何位置，下次发布时需要重新通知一次当前位置。
    this.advertSlot = null;
  }

  /**
   * 按当前状态重发一次画面。商品缩略图等异步素材就绪后用它刷新，不改变 mode。
   * 协调器已销毁或尚未初始化时静默忽略，避免晚到的回调打断退出流程。
   */
  public refresh(): Promise<CustomerDisplayPublishResult> | null {
    if (this.destroyed || !this.initialized) return null;
    return this.publish(
      this.mode === "idle" || this.mode === "cart"
        ? this.readCart()
        : this.transactionCart(),
    );
  }

  /**
   * 锁屏、设备拒绝与 runtime 退出都必须主动覆盖公共外屏；
   * 不能依赖下一笔购物车变更来清除上一位顾客的交易。
   */
  public clearSensitiveContent(): Promise<CustomerDisplayPublishResult> {
    this.assertReady();
    this.lastNonEmptyCart = null;
    this.changeCents = 0;
    this.advert = null;
    this.mode = "idle";
    return this.publish(null);
  }

  public destroy(): void {
    if (this.destroyed) return;
    this.destroyed = true;
    this.unsubscribeCart?.();
    this.unsubscribeCart = null;
  }

  private onCartChanged(): void {
    if (this.destroyed) return;
    const current = this.readCart();
    if (this.mode !== "idle" && this.mode !== "cart") {
      return;
    }
    this.mode = current.lines.length > 0 ? "cart" : "idle";
    void this.publish(current).catch(() => {
      // 客显验证或桥接异常不能传播到共享购物车的主交易通知。
    });
  }

  private readCart(): CartSnapshot {
    const current = this.cart.getSnapshot();
    if (current.lines.length > 0) {
      this.lastNonEmptyCart = current;
    }
    return current;
  }

  private transactionCart(): CartSnapshot | null {
    const current = this.readCart();
    return current.lines.length > 0 ? current : this.lastNonEmptyCart;
  }

  private publish(
    cart: CartSnapshot | null,
  ): Promise<CustomerDisplayPublishResult> {
    const result = this.publisher.publish({
      mode: this.mode,
      cart,
      changeCents: this.changeCents,
      advert: this.advert,
    });
    // 先发起发布再通知广告位，且不等待播放层，避免位置切换产生的广告帧抢在本帧之前。
    this.notifyAdvertSlot(cart);
    return result;
  }

  private notifyAdvertSlot(cart: CartSnapshot | null): void {
    const slot: CustomerDisplayAdvertSlot =
      cart !== null && cart.lines.length > 0 ? "checkout" : "idle";
    if (slot === this.advertSlot) return;
    this.advertSlot = slot;
    try {
      this.advertSlotListener?.(slot);
    } catch {
      // 播放层异常不能传播到客显发布或共享购物车的主交易通知。
    }
  }

  private assertAlive(): void {
    if (this.destroyed) {
      throw new Error("Customer display coordinator is destroyed.");
    }
  }

  private assertReady(): void {
    this.assertAlive();
    if (!this.initialized) {
      throw new Error("Customer display coordinator is not initialized.");
    }
  }
}
