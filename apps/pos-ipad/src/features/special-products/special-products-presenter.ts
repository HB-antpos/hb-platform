import {
  resolveSpecialProductsAccess,
  type SpecialProductsAccess,
} from "@hb/pos-domain/features/special-products/special-products-authorization";

import {
  hasSpecialProductCategoryErrorCode,
  SPECIAL_PRODUCT_CATEGORY_ALL,
  SPECIAL_PRODUCT_CATEGORY_ASSIGN_CONFLICT_CODE,
  SPECIAL_PRODUCT_CATEGORY_NOT_FOUND_CODE,
  SPECIAL_PRODUCT_CATEGORY_ORDER_CONFLICT_CODE,
  SPECIAL_PRODUCT_CATEGORY_UNCATEGORIZED,
  type SpecialProductCategoriesRemotePort,
  type SpecialProductCategoriesRepositoryPort,
  type SpecialProductCategory,
  type SpecialProductCategoryAssignment,
  type SpecialProductCategorySelection,
  type SpecialProductCategorySnapshot,
} from "@hb/pos-domain/core/contracts/special-product-categories";

import {
  applySubsetOrder,
  deriveSpecialProductsCategoryView,
  EMPTY_SPECIAL_PRODUCT_CATEGORY_COUNTS,
  type SpecialProductCategoryCounts,
} from "./special-product-categories-view";

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
  | "assign-complete"
  | "assign-conflict"
  | "assign-failed"
  | "category-order-complete"
  | "download-complete"
  | "download-failed"
  | "load-failed"
  | "mark-complete"
  | "mark-failed"
  | "online-required"
  | "order-conflict"
  | "permission-required"
  | "reorder-complete"
  | "reorder-failed"
  | "search-failed";

export type SpecialProductsState = Readonly<{
  access: SpecialProductsAccess;
  /** 本机保存的分类归属快照（原样）；没有分类时为空数组，页面不显示分类栏。 */
  assignments: readonly SpecialProductCategoryAssignment[];
  busy: boolean;
  candidates: readonly SpecialProductItem[];
  /** 分类栏各行的数量（只数本机仍是特殊商品的）。 */
  categoryCounts: SpecialProductCategoryCounts;
  categories: readonly SpecialProductCategory[];
  /** 本机全部特殊商品（本机顺序）；搜索、加购、取消标记都基于它。 */
  items: readonly SpecialProductItem[];
  kind: "idle" | "loading" | "ready" | "unauthorized" | "failed";
  online: boolean;
  /** 页面搜索框的词（在全部特殊商品里过滤）；与「添加商品」弹窗的 searchQuery 互相独立。 */
  search: string;
  searching: boolean;
  searchQuery: string;
  /** 实际生效的选中项：'all' | 'uncategorized' | 分类 id（已处理各种回退）。 */
  selectedCategoryId: SpecialProductCategorySelection;
  statusCode: SpecialProductsStatusCode | null;
  /** 页面网格要显示的商品（搜索优先，其次按选中分类）；没有分类时等于 items。 */
  visibleItems: readonly SpecialProductItem[];
}>;

export type SpecialProductsPresenterOptions = Readonly<{
  addToCart: SpecialProductsCartPort;
  /**
   * 分店级特殊商品分类（可选）：不提供时一切行为与分类功能上线前完全一致。
   * 独立于 remote / repository 两个既有端口，手持 POS 共用的端口因此保持不变。
   */
  categories?: Readonly<{
    remote: SpecialProductCategoriesRemotePort;
    repository: SpecialProductCategoriesRepositoryPort;
  }>;
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
  // —— 分类状态（仅在提供 options.categories 时使用）——
  // 用户请求的选中项（持久化到本机）；对外暴露的 state.selectedCategoryId 是它经回退后的生效值。
  private requestedSelection: SpecialProductCategorySelection =
    SPECIAL_PRODUCT_CATEGORY_ALL;
  // 本机记住的选中项只在首次 load 读取一次；用户在读取完成前已手动选择则以用户为准。
  private selectionRestored = false;
  private selectionTouched = false;
  // 分类快照的版本号：任何一次「应用新快照」都会自增，用来丢弃慢返回的旧读取，
  // 防止后台拉取 / 本机读取晚到后把更新的快照（例如刚在线调整的结果）盖回去。
  private categoryGeneration = 0;
  private categoriesSynced = false;
  private categoryRefreshInFlight = false;

  public constructor(options: SpecialProductsPresenterOptions) {
    this.options = options;
    this.storeCode = requiredText(options.storeCode, "storeCode");
    this.state = {
      access: resolveSpecialProductsAccess(options.permissions),
      assignments: [],
      busy: false,
      candidates: [],
      categories: [],
      categoryCounts: EMPTY_SPECIAL_PRODUCT_CATEGORY_COUNTS,
      items: [],
      kind: "idle",
      online: options.initialOnline,
      search: "",
      searching: false,
      searchQuery: "",
      selectedCategoryId: SPECIAL_PRODUCT_CATEGORY_ALL,
      statusCode: null,
      visibleItems: [],
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
    if (!online) {
      // 掉线后分类可能在别处被改，恢复在线时要重新拉一次。
      this.categoriesSynced = false;
    } else {
      this.requestCategoryRefresh();
    }
  }

  /** 页面搜索框：在「全部」特殊商品里按名称 / 货号 / 条码 / 商品编码过滤，纯本地、不受当前分类限制。 */
  public setSearch(search: string): void {
    if (this.destroyed) return;
    this.patch({ search: search.slice(0, 120) });
  }

  /**
   * 选中分类栏的一行（'all' | 'uncategorized' | 分类 id）：记在本机（按分店），下次进入还原。
   * 选择分类意味着要浏览该分类，所以同时清空搜索词；不存在的分类 id 忽略。
   */
  public selectCategory(selection: SpecialProductCategorySelection): void {
    if (this.destroyed || !this.options.categories) return;
    const valid =
      selection === SPECIAL_PRODUCT_CATEGORY_ALL ||
      selection === SPECIAL_PRODUCT_CATEGORY_UNCATEGORIZED ||
      this.state.categories.some(
        (category) => category.categoryId === selection,
      );
    if (!valid) return;
    this.selectionTouched = true;
    this.requestedSelection = selection;
    this.persistSelection(selection);
    this.patch({ search: "", statusCode: null }, true);
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
      // 本机分类快照离线可用；读取失败不影响商品列表显示。
      const local = await this.readLocalCategories();
      if (!this.isCurrentLoad(generation)) return;
      this.patch(
        {
          items,
          kind: "ready",
          statusCode: null,
          ...(local ?? {}),
        },
        true,
      );
      // 进页面（在线）后台拉一次分类快照；失败静默。
      this.requestCategoryRefresh();
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
        // 下载更新时一并拉取分类；分类拉取失败不让已成功的商品下载变成失败，下次进页面会再拉。
        await this.refreshCategories().catch(() => undefined);
      },
    );
  }

  /**
   * 把商品移到某分类（categoryId = null 表示回到「未分类」）。
   * 与其它管理写操作同一把锁（需 Manage 权限 + 在线 + 非 busy）；成功后用服务端返回的快照覆盖本机。
   */
  public assignToCategory(
    productCode: string,
    categoryId: string | null,
  ): Promise<void> {
    return this.runManagement("assign-complete", "assign-failed", async () => {
      const categories = this.options.categories;
      if (!categories) throw new Error("Special product categories are unavailable.");
      const normalizedProductCode = requiredText(productCode, "productCode");
      if (
        !this.state.items.some(
          (item) => item.productCode === normalizedProductCode,
        )
      ) {
        throw new Error("Special product category target is unavailable.");
      }
      const target =
        categoryId === null ? null : requiredText(categoryId, "categoryId");
      if (
        target !== null &&
        !this.state.categories.some((category) => category.categoryId === target)
      ) {
        throw new Error("Special product category is unavailable.");
      }
      const current =
        this.state.assignments.find(
          (assignment) => assignment.productCode === normalizedProductCode,
        )?.categoryId ?? null;
      // 已经在目标分类里：无需请求服务端。
      if (current === target) return;
      try {
        const snapshot = await categories.remote.assign({
          storeCode: this.storeCode,
          productCode: normalizedProductCode,
          categoryId: target,
        });
        if (this.destroyed) return;
        await this.commitCategorySnapshot(snapshot, null);
      } catch (error) {
        // 两台收银机同时首次归类同一商品：先刷新快照，再提示重新操作。
        if (
          hasSpecialProductCategoryErrorCode(
            error,
            SPECIAL_PRODUCT_CATEGORY_ASSIGN_CONFLICT_CODE,
          )
        ) {
          await this.refreshCategories().catch(() => undefined);
          throw new SpecialProductsStatusError("assign-conflict");
        }
        // 分类刚被删除：本机列表已陈旧，先刷新快照再按失败提示。
        if (
          hasSpecialProductCategoryErrorCode(
            error,
            SPECIAL_PRODUCT_CATEGORY_NOT_FOUND_CODE,
          )
        ) {
          await this.refreshCategories().catch(() => undefined);
        }
        throw error;
      }
    });
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
    return this.reorderInView((productCodes) => {
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
    return this.reorderInView((productCodes) => {
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

  /**
   * 在当前视图（页面实际显示的列表）内执行一次重排并持久化（canManage/online 门禁在 runManagement）：
   * - 选中真实分类：走服务器 order 接口，productCodes 为该分类当前全部商品的新排列，
   *   成功用返回的快照覆盖本机；409 顺序冲突 → 刷新快照并提示 order-conflict；
   * - 「全部」「未分类」：沿用本机排序（saveOrder）。「全部」视图 = 完整列表，行为与分类上线前一致；
   *   「未分类」只重排它自己的商品，其余商品相对位置不变；
   * - 搜索词非空时视图只是子集合，重排没有明确含义，一律拒绝（页面上 ↑ ↓ / 拖动也已禁用）。
   */
  private reorderInView(
    move: (productCodes: string[]) => string[],
  ): Promise<void> {
    const categoryOrder = this.isCategoryOrderView();
    return this.runManagement(
      categoryOrder ? "category-order-complete" : "reorder-complete",
      "reorder-failed",
      async () => {
        if (this.state.search.trim().length > 0) {
          throw new Error("Special product reorder is unavailable while searching.");
        }
        const viewCodes = this.state.visibleItems.map((item) => item.productCode);
        const reordered = move(viewCodes);
        if (categoryOrder) {
          const categoryId = this.state.selectedCategoryId;
          const normalizedOrder = normalizeSpecialProductOrder(
            reordered,
            new Set(viewCodes),
          );
          if (this.destroyed) return;
          await this.saveCategoryOrder(categoryId, normalizedOrder);
          return;
        }
        const fullCodes = this.state.items.map((item) => item.productCode);
        const normalizedOrder = normalizeSpecialProductOrder(
          applySubsetOrder(fullCodes, reordered),
          new Set(fullCodes),
        );
        if (this.destroyed) return;
        await this.options.repository.saveOrder(
          this.storeCode,
          normalizedOrder,
        );
      },
    );
  }

  /** 当前视图是否是「某个真实分类」（此时分类内顺序由服务器统一）。 */
  private isCategoryOrderView(): boolean {
    const selected = this.state.selectedCategoryId;
    return (
      this.options.categories !== undefined &&
      selected !== SPECIAL_PRODUCT_CATEGORY_ALL &&
      selected !== SPECIAL_PRODUCT_CATEGORY_UNCATEGORIZED
    );
  }

  private async saveCategoryOrder(
    categoryId: string,
    productCodes: readonly string[],
  ): Promise<void> {
    const categories = this.options.categories;
    if (!categories) throw new Error("Special product categories are unavailable.");
    try {
      const snapshot = await categories.remote.setOrder({
        storeCode: this.storeCode,
        categoryId,
        productCodes,
      });
      if (this.destroyed) return;
      await this.commitCategorySnapshot(snapshot, null);
    } catch (error) {
      if (
        hasSpecialProductCategoryErrorCode(
          error,
          SPECIAL_PRODUCT_CATEGORY_ORDER_CONFLICT_CODE,
        )
      ) {
        // 别的收银机或手机刚改过这个分类：先刷新快照，再提示重新排序。
        await this.refreshCategories().catch(() => undefined);
        throw new SpecialProductsStatusError("order-conflict");
      }
      throw error;
    }
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
      } catch (error) {
        if (!this.isCurrentLoad(generation)) return;
        this.patch({
          busy: false,
          kind: this.state.items.length > 0 ? "ready" : "failed",
          // 操作可以指定更具体的失败状态（例如分类内顺序冲突）。
          statusCode:
            error instanceof SpecialProductsStatusError
              ? error.statusCode
              : failureCode,
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

  /** 分类相关的私有流程：拉取、落库、应用快照与选中项回退。 */
  private requestCategoryRefresh(): void {
    if (
      this.destroyed ||
      !this.options.categories ||
      !this.state.online ||
      !this.state.access.canView ||
      this.state.kind === "idle" ||
      this.state.kind === "unauthorized" ||
      this.categoriesSynced ||
      this.categoryRefreshInFlight
    ) {
      return;
    }
    this.categoryRefreshInFlight = true;
    // 后台拉取：失败静默，不影响现有列表；未同步成功前下次 load / 重新在线会再试。
    void this.refreshCategories()
      .catch(() => undefined)
      .finally(() => {
        this.categoryRefreshInFlight = false;
      });
  }

  /** 从服务器拉取整店分类快照，整体覆盖本机并应用到页面（失败抛出，由调用方决定是否静默）。 */
  private async refreshCategories(): Promise<void> {
    const categories = this.options.categories;
    if (!categories || this.destroyed) return;
    // 读取开始时记下版本号：期间若有更新的快照被应用（例如刚在线调整），这次读取作废。
    const ticket = this.categoryGeneration;
    const snapshot = await categories.remote.getCategories({
      storeCode: this.storeCode,
    });
    if (await this.commitCategorySnapshot(snapshot, ticket)) {
      this.categoriesSynced = true;
    }
  }

  /**
   * 落库并应用服务端快照。ticket 为 null = 在线调整的返回结果，一定应用；
   * 否则仅当期间没有别的快照被应用过时才应用，返回是否已应用。
   * 服务端 revision 在删除行时可能不变甚至回退，所以这里不比较 revision，一律整体覆盖。
   */
  private async commitCategorySnapshot(
    snapshot: SpecialProductCategorySnapshot,
    ticket: number | null,
  ): Promise<boolean> {
    const categories = this.options.categories;
    if (!categories || this.destroyed) return false;
    if (snapshot.storeCode !== this.storeCode) {
      throw new Error("Special product category store scope is invalid.");
    }
    if (ticket !== null && ticket !== this.categoryGeneration) return false;
    await categories.repository.replaceSnapshot(snapshot);
    if (this.destroyed) return false;
    if (ticket !== null && ticket !== this.categoryGeneration) return false;
    this.categoryGeneration += 1;
    this.reconcileSelection(snapshot.categories, true);
    this.patch(
      {
        assignments: snapshot.assignments,
        categories: snapshot.categories,
      },
      true,
    );
    return true;
  }

  /** 读取本机快照与（首次）记住的选中项；任何失败都返回 null，调用方保持现状。 */
  private async readLocalCategories(): Promise<Partial<SpecialProductsState> | null> {
    const categories = this.options.categories;
    if (!categories) return null;
    const ticket = this.categoryGeneration;
    try {
      const snapshot = await categories.repository.readSnapshot(this.storeCode);
      let stored: SpecialProductCategorySelection | null = null;
      if (!this.selectionRestored) {
        stored = await categories.repository.readSelectedCategory(
          this.storeCode,
        );
      }
      // 读取期间已有更新的快照（后台拉取）被应用：本机旧数据作废。
      if (this.destroyed || ticket !== this.categoryGeneration) return null;
      if (!this.selectionRestored) {
        this.selectionRestored = true;
        if (!this.selectionTouched && stored !== null) {
          this.requestedSelection = stored;
        }
      }
      this.reconcileSelection(snapshot.categories, false);
      return {
        assignments: snapshot.assignments,
        categories: snapshot.categories,
      };
    } catch {
      return null;
    }
  }

  /**
   * 记住的分类已被删除 → 回到「全部」并更新本机记录。
   * 只有拿到权威分类列表（服务器刚返回）或本机快照非空时才判定「被删除」：
   * 本机还没有快照（从没拉成功过）时保留记录，等第一次拉取成功后再判断。
   */
  private reconcileSelection(
    categories: readonly SpecialProductCategory[],
    authoritative: boolean,
  ): void {
    const selection = this.requestedSelection;
    if (
      selection === SPECIAL_PRODUCT_CATEGORY_ALL ||
      selection === SPECIAL_PRODUCT_CATEGORY_UNCATEGORIZED ||
      categories.some((category) => category.categoryId === selection) ||
      (categories.length === 0 && !authoritative)
    ) {
      return;
    }
    this.requestedSelection = SPECIAL_PRODUCT_CATEGORY_ALL;
    this.persistSelection(SPECIAL_PRODUCT_CATEGORY_ALL);
  }

  private persistSelection(selection: SpecialProductCategorySelection): void {
    const categories = this.options.categories;
    if (!categories) return;
    // 记录选中项只是体验优化，失败不影响页面。
    void categories.repository
      .saveSelectedCategory(this.storeCode, selection)
      .catch(() => undefined);
  }

  /**
   * 合并状态并通知订阅者。items / categories / assignments / search 任一变化（或 rederive）时
   * 重新派生分类视图（生效的选中项、网格列表、分类栏数量）。
   */
  private patch(
    patch: Partial<SpecialProductsState>,
    rederive = false,
  ): void {
    if (this.destroyed) return;
    let next: SpecialProductsState = { ...this.state, ...patch };
    if (
      rederive ||
      "items" in patch ||
      "categories" in patch ||
      "assignments" in patch ||
      "search" in patch
    ) {
      const view = deriveSpecialProductsCategoryView({
        items: next.items,
        categories: next.categories,
        assignments: next.assignments,
        selection: this.requestedSelection,
        search: next.search,
      });
      next = {
        ...next,
        categoryCounts: view.counts,
        selectedCategoryId: view.selectedCategoryId,
        visibleItems: view.visibleItems,
      };
    }
    this.state = next;
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

/** 让 runManagement 的失败分支使用更具体的状态码（例如 order-conflict）。 */
class SpecialProductsStatusError extends Error {
  public constructor(public readonly statusCode: SpecialProductsStatusCode) {
    super(statusCode);
    this.name = "SpecialProductsStatusError";
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
