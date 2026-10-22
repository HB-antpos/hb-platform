import {
  resolveSpecialProductsAccess,
  type SpecialProductsAccess,
} from "@hb/pos-domain/features/special-products/special-products-authorization";

import type {
  SpecialProductItem,
  SpecialProductsRemotePort,
  SpecialProductsRepositoryPort,
} from "@/core/contracts";
import { normalizeSpecialProductOrder } from "@/core/contracts";
import type { CartAddDisposition } from "@/features/sales/domain";


export interface SpecialProductsCartPort {
  add(item: SpecialProductItem): Promise<CartAddDisposition>;
}

export type SpecialProductsFeedbackEvent = Readonly<{
  kind:
    | "query-found"
    | "query-empty"
    | "query-error"
    | "added"
    | "incremented"
    | "failed-blocked";
  lineId?: string;
}>;

export type SpecialProductsStatusCode =
  | "added-to-cart"
  | "add-to-cart-failed"
  | "download-complete"
  | "download-failed"
  | "load-failed"
  | "mark-complete"
  | "mark-failed"
  | "online-required"
  | "permission-required"
  | "reorder-complete"
  | "reorder-failed"
  | "search-failed";

export type SpecialProductsState = Readonly<{
  access: SpecialProductsAccess;
  busy: boolean;
  candidates: readonly SpecialProductItem[];
  items: readonly SpecialProductItem[];
  kind: "idle" | "loading" | "ready" | "unauthorized" | "failed";
  online: boolean;
  searching: boolean;
  searchQuery: string;
  statusCode: SpecialProductsStatusCode | null;
}>;

export type SpecialProductsPresenterOptions = Readonly<{
  addToCart: SpecialProductsCartPort;
  initialOnline: boolean;
  localPageSize?: number;
  permissions: readonly string[];
  remote: SpecialProductsRemotePort;
  remotePageSize?: number;
  repository: SpecialProductsRepositoryPort;
  storeCode: string;
}>;

export class SpecialProductsPresenter {
  private static readonly DEFAULT_LOCAL_PAGE_SIZE = 100;
  private static readonly DEFAULT_REMOTE_PAGE_SIZE = 200;
  private static readonly MAX_LOCAL_PAGES = 1_000;
  private static readonly MAX_REMOTE_PAGES = 10_000;

  private readonly listeners = new Set<() => void>();
  private readonly feedbackListeners = new Set<
    (event: SpecialProductsFeedbackEvent) => void
  >();
  private readonly options: SpecialProductsPresenterOptions;
  private readonly storeCode: string;
  private state: SpecialProductsState;
  private destroyed = false;
  private loadGeneration = 0;
  private searchGeneration = 0;
  private managementInFlight: Promise<void> | null = null;
  // 点商品加购的防重入锁（对齐 WPF 28f2f0833）：从开始加购到离开本页期间不再接受新的点击，
  // 避免连点或退场动画期间的点击把同一商品加两件；加购失败 / 未授权时立即解锁，允许重试。
  private cartAddLocked = false;

  public constructor(options: SpecialProductsPresenterOptions) {
    this.options = options;
    this.storeCode = requiredText(options.storeCode, "storeCode");
    this.state = {
      access: resolveSpecialProductsAccess(options.permissions),
      busy: false,
      candidates: [],
      items: [],
      kind: "idle",
      online: options.initialOnline,
      searching: false,
      searchQuery: "",
      statusCode: null,
    };
  }

  public readonly getState = (): SpecialProductsState => this.state;

  public readonly subscribe = (listener: () => void): (() => void) => {
    if (this.destroyed) return () => undefined;
    this.listeners.add(listener);
    return () => this.listeners.delete(listener);
  };

  public readonly subscribeFeedback = (
    listener: (event: SpecialProductsFeedbackEvent) => void,
  ): (() => void) => {
    if (this.destroyed) return () => undefined;
    this.feedbackListeners.add(listener);
    return () => this.feedbackListeners.delete(listener);
  };

  public destroy(): void {
    if (this.destroyed) return;
    this.destroyed = true;
    this.loadGeneration += 1;
    this.searchGeneration += 1;
    this.listeners.clear();
    this.feedbackListeners.clear();
  }

  public setOnline(online: boolean): void {
    if (this.destroyed || this.state.online === online) return;
    this.patch({ online });
  }

  public setSearchQuery(searchQuery: string): void {
    if (this.destroyed) return;
    this.searchGeneration += 1;
    this.patch({
      candidates: [],
      searching: false,
      searchQuery: searchQuery.slice(0, 120),
      statusCode: null,
    });
  }

  public async load(): Promise<void> {
    if (this.destroyed) return;
    if (!this.state.access.canView) {
      this.patch({
        candidates: [],
        items: [],
        kind: "unauthorized",
        statusCode: "permission-required",
      });
      return;
    }

    const generation = ++this.loadGeneration;
    this.patch({ kind: "loading", statusCode: null });
    try {
      const items = await this.listAll();
      if (!this.isCurrentLoad(generation)) return;
      this.patch({
        items,
        kind: "ready",
        statusCode: null,
      });
    } catch {
      if (!this.isCurrentLoad(generation)) return;
      this.patch({
        kind: this.state.items.length > 0 ? "ready" : "failed",
        statusCode: "load-failed",
      });
    }
  }

  public async searchCandidates(): Promise<void> {
    if (this.destroyed) return;
    const query = this.state.searchQuery.trim();
    if (!this.state.access.canManage) {
      this.patch({ statusCode: "permission-required" });
      if (query.length > 0) {
        this.publishFeedback({ kind: "query-error" });
      }
      return;
    }
    const generation = ++this.searchGeneration;
    if (query.length === 0) {
      this.patch({ candidates: [], searching: false, statusCode: null });
      return;
    }

    this.patch({ searching: true, statusCode: null });
    try {
      const candidates = await this.options.repository.searchCandidates(
        this.storeCode,
        query,
        50,
      );
      if (!this.isCurrentSearch(generation)) return;
      this.patch({
        candidates: freezeStoreItems(candidates, this.storeCode),
        searching: false,
      });
      this.publishFeedback({
        kind: candidates.length === 0 ? "query-empty" : "query-found",
      });
    } catch {
      if (!this.isCurrentSearch(generation)) return;
      this.patch({
        candidates: [],
        searching: false,
        statusCode: "search-failed",
      });
      this.publishFeedback({ kind: "query-error" });
    }
  }

  public async addToCart(productCode: string): Promise<void> {
    if (this.destroyed || this.cartAddLocked) return;
    if (!this.state.access.canAddToCart) {
      this.patch({ statusCode: "permission-required" });
      this.publishFeedback({ kind: "failed-blocked" });
      return;
    }
    const item = this.state.items.find(
      (candidate) => candidate.productCode === productCode,
    );
    if (!item) {
      this.patch({ statusCode: "add-to-cart-failed" });
      this.publishFeedback({ kind: "failed-blocked" });
      return;
    }

    this.cartAddLocked = true;
    try {
      const disposition = await this.options.addToCart.add(item);
      if (this.destroyed) return;
      // 成功后保持锁定：路由层收到 added / incremented 会返回收银台，本页实例随之销毁。
      this.patch({ statusCode: "added-to-cart" });
      this.publishFeedback(disposition);
    } catch {
      this.cartAddLocked = false;
      if (!this.destroyed) {
        this.patch({ statusCode: "add-to-cart-failed" });
        this.publishFeedback({ kind: "failed-blocked" });
      }
    }
  }

  public download(): Promise<void> {
    return this.runManagement(
      "download-complete",
      "download-failed",
      async () => {
        const downloaded = await this.downloadAllPages();
        if (this.destroyed) return;
        await this.options.repository.replaceDownloaded(
          this.storeCode,
          downloaded,
        );
      },
    );
  }

  public mark(
    productCode: string,
    isSpecialProduct: boolean,
  ): Promise<void> {
    return this.runManagement("mark-complete", "mark-failed", async () => {
      const normalizedProductCode = requiredText(productCode, "productCode");
      const source = isSpecialProduct
        ? this.state.candidates
        : this.state.items;
      if (
        !source.some(
          (candidate) => candidate.productCode === normalizedProductCode,
        )
      ) {
        throw new Error("Special product mutation target is unavailable.");
      }
      const items = await this.options.remote.mark({
        storeCode: this.storeCode,
        productCode: normalizedProductCode,
        isSpecialProduct,
      });
      if (this.destroyed) return;
      await this.options.repository.applyMark(
        this.storeCode,
        normalizedProductCode,
        isSpecialProduct,
        items,
      );
    });
  }

  public reorder(productCode: string, delta: -1 | 1): Promise<void> {
    return this.reorderWithinCurrentItems((productCodes) => {
      const normalizedProductCode = requiredText(productCode, "productCode");
      const currentIndex = productCodes.indexOf(normalizedProductCode);
      const nextIndex = currentIndex + delta;
      if (
        currentIndex < 0 ||
        (delta !== -1 && delta !== 1) ||
        nextIndex < 0 ||
        nextIndex >= productCodes.length
      ) {
        throw new Error("Special product reorder target is unavailable.");
      }
      const swapped = [...productCodes];
      [swapped[currentIndex], swapped[nextIndex]] = [
        swapped[nextIndex]!,
        swapped[currentIndex]!,
      ];
      return swapped;
    });
  }

  /** 拖拽排序：把商品移到任意目标索引（与 reorder 共用持久化与门禁）。 */
  public moveTo(productCode: string, toIndex: number): Promise<void> {
    return this.reorderWithinCurrentItems((productCodes) => {
      const normalizedProductCode = requiredText(productCode, "productCode");
      const currentIndex = productCodes.indexOf(normalizedProductCode);
      const normalizedToIndex = Math.trunc(toIndex);
      if (
        currentIndex < 0 ||
        !Number.isInteger(normalizedToIndex) ||
        normalizedToIndex < 0 ||
        normalizedToIndex >= productCodes.length
      ) {
        throw new Error("Special product reorder target is unavailable.");
      }
      if (normalizedToIndex === currentIndex) return productCodes;
      const moved = [...productCodes];
      const [item] = moved.splice(currentIndex, 1);
      moved.splice(normalizedToIndex, 0, item!);
      return moved;
    });
  }

  /** 在当前商品列表内执行一次重排并持久化（canManage/online 门禁在 runManagement）。 */
  private reorderWithinCurrentItems(
    move: (productCodes: string[]) => string[],
  ): Promise<void> {
    return this.runManagement(
      "reorder-complete",
      "reorder-failed",
      async () => {
        const productCodes = this.state.items.map((item) => item.productCode);
        const reordered = move(productCodes);
        const normalizedOrder = normalizeSpecialProductOrder(
          reordered,
          new Set(productCodes),
        );
        if (this.destroyed) return;
        await this.options.repository.saveOrder(
          this.storeCode,
          normalizedOrder,
        );
      },
    );
  }

  private runManagement(
    successCode: SpecialProductsStatusCode,
    failureCode: SpecialProductsStatusCode,
    operation: () => Promise<void>,
  ): Promise<void> {
    if (this.destroyed) return Promise.resolve();
    if (!this.state.access.canManage) {
      this.patch({ statusCode: "permission-required" });
      return Promise.resolve();
    }
    if (!this.state.online) {
      this.patch({ statusCode: "online-required" });
      return Promise.resolve();
    }
    if (this.managementInFlight) return this.managementInFlight;

    const generation = ++this.loadGeneration;
    this.patch({ busy: true, statusCode: null });
    const running = (async () => {
      try {
        await operation();
        if (!this.isCurrentLoad(generation)) return;
        const items = await this.listAll();
        if (!this.isCurrentLoad(generation)) return;
        this.patch({
          busy: false,
          items,
          kind: "ready",
          statusCode: successCode,
        });
      } catch {
        if (!this.isCurrentLoad(generation)) return;
        this.patch({
          busy: false,
          kind: this.state.items.length > 0 ? "ready" : "failed",
          statusCode: failureCode,
        });
      }
    })().finally(() => {
      if (this.managementInFlight === running) {
        this.managementInFlight = null;
      }
    });
    this.managementInFlight = running;
    return running;
  }

  private async downloadAllPages(): Promise<
    readonly Omit<SpecialProductItem, "sortOrder">[]
  > {
    const downloaded: Omit<SpecialProductItem, "sortOrder">[] = [];
    const seenProductCodes = new Set<string>();
    const seenCursors = new Set<string>();
    let cursor: string | null = null;

    for (
      let pageNumber = 0;
      pageNumber < SpecialProductsPresenter.MAX_REMOTE_PAGES;
      pageNumber += 1
    ) {
      if (this.destroyed) return [];
      const page = await this.options.remote.getPage({
        storeCode: this.storeCode,
        cursor,
        pageSize:
          this.options.remotePageSize ??
          SpecialProductsPresenter.DEFAULT_REMOTE_PAGE_SIZE,
      });
      if (this.destroyed) return [];

      for (const item of page.items) {
        const productCode = requiredText(item.productCode, "productCode");
        if (item.storeCode !== this.storeCode) {
          // 门店串号属于数据损坏信号，必须中止而不是静默丢弃。
          throw new Error("Special product download page is invalid.");
        }
        if (seenProductCodes.has(productCode)) {
          // 后端按 lookup_code 组织商品（一商品多码），同一 productCode 可能
          // 在列表中重复出现；本地列表按商品去重，重复条目跳过而不中断下载。
          continue;
        }
        seenProductCodes.add(productCode);
        downloaded.push({ ...item, productCode });
      }

      if (!page.hasMore) return Object.freeze(downloaded);
      const nextCursor = page.nextCursor;
      if (
        typeof nextCursor !== "string" ||
        nextCursor.length === 0 ||
        nextCursor.length > 2_048 ||
        seenCursors.has(nextCursor)
      ) {
        throw new Error("Special product download cursor is invalid.");
      }
      seenCursors.add(nextCursor);
      cursor = nextCursor;
    }

    throw new Error("Special product download page limit exceeded.");
  }

  private async listAll(): Promise<readonly SpecialProductItem[]> {
    const pageSize =
      this.options.localPageSize ??
      SpecialProductsPresenter.DEFAULT_LOCAL_PAGE_SIZE;
    if (!Number.isSafeInteger(pageSize) || pageSize <= 0 || pageSize > 1_000) {
      throw new TypeError("Special product local page size is invalid.");
    }

    const items: SpecialProductItem[] = [];
    for (
      let pageNumber = 0;
      pageNumber < SpecialProductsPresenter.MAX_LOCAL_PAGES;
      pageNumber += 1
    ) {
      const page = await this.options.repository.list(
        this.storeCode,
        pageSize,
        items.length,
      );
      if (this.destroyed) return [];
      items.push(...freezeStoreItems(page, this.storeCode));
      if (page.length < pageSize) {
        return Object.freeze(
          [...items].sort(
            (left, right) =>
              left.sortOrder - right.sortOrder ||
              left.productCode.localeCompare(right.productCode),
          ),
        );
      }
    }
    throw new Error("Special product local page limit exceeded.");
  }

  private isCurrentLoad(generation: number): boolean {
    return !this.destroyed && generation === this.loadGeneration;
  }

  private isCurrentSearch(generation: number): boolean {
    return !this.destroyed && generation === this.searchGeneration;
  }

  private patch(patch: Partial<SpecialProductsState>): void {
    if (this.destroyed) return;
    this.state = { ...this.state, ...patch };
    for (const listener of [...this.listeners]) {
      try {
        listener();
      } catch {
        // 已卸载页面的订阅异常不能阻止其余观察者接收脱敏状态。
      }
    }
  }

  private publishFeedback(event: SpecialProductsFeedbackEvent): void {
    if (this.destroyed) return;
    for (const listener of [...this.feedbackListeners]) {
      try {
        listener(event);
      } catch {
        // 提示层不得影响特殊商品写入或查询的权威结果。
      }
    }
  }
}

function freezeStoreItems(
  items: readonly SpecialProductItem[],
  storeCode: string,
): readonly SpecialProductItem[] {
  if (items.some((item) => item.storeCode !== storeCode)) {
    throw new Error("Special product store scope is invalid.");
  }
  return Object.freeze(items.map((item) => Object.freeze({ ...item })));
}

function requiredText(value: unknown, label: string): string {
  if (typeof value !== "string") {
    throw new TypeError(`${label} is invalid.`);
  }
  const normalized = value.trim();
  if (
    normalized.length === 0 ||
    normalized.length > 128 ||
    /[\u0000-\u001f\u007f]/u.test(normalized)
  ) {
    throw new TypeError(`${label} is invalid.`);
  }
  return normalized;
}
