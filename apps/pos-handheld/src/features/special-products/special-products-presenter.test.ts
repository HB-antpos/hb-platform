import assert from "node:assert/strict";
import test from "node:test";

import {
  SPECIAL_PRODUCTS_ADD_TO_CART_PERMISSION,
  SPECIAL_PRODUCTS_MANAGE_PERMISSION,
  SPECIAL_PRODUCTS_VIEW_PERMISSION,
} from "@hb/pos-domain/features/special-products/special-products-authorization";
import {
  SpecialProductsPresenter,
  type SpecialProductsCartPort,
} from "./special-products-presenter";

import type {
  SpecialProductDownloadPage,
  SpecialProductItem,
  SpecialProductsRemotePort,
  SpecialProductsRepositoryPort,
} from "@/core/contracts";
import type { CartAddDisposition } from "@/features/sales/domain";


const ALL_PERMISSIONS = [
  SPECIAL_PRODUCTS_VIEW_PERMISSION,
  SPECIAL_PRODUCTS_MANAGE_PERMISSION,
  SPECIAL_PRODUCTS_ADD_TO_CART_PERMISSION,
] as const;

test("无 View 权限时不读取本地特殊商品", async () => {
  const repository = new MemorySpecialProductsRepository([product("A")]);
  const presenter = createPresenter({
    repository,
    permissions: [
      SPECIAL_PRODUCTS_MANAGE_PERMISSION,
      SPECIAL_PRODUCTS_ADD_TO_CART_PERMISSION,
    ],
  });

  await presenter.load();

  assert.equal(presenter.getState().kind, "unauthorized");
  assert.equal(repository.listCalls, 0);
});

test("离线仍可浏览本地缓存并按独立 AddToCart 权限加购", async () => {
  const repository = new MemorySpecialProductsRepository([product("A")]);
  const remote = new MemorySpecialProductsRemote();
  const cart = new MemorySpecialProductsCart();
  const presenter = createPresenter({
    repository,
    remote,
    cart,
    online: false,
  });

  await presenter.load();
  await presenter.addToCart("A");
  await presenter.download();
  await presenter.mark("A", false);
  await presenter.reorder("A", 1);

  assert.deepEqual(
    presenter.getState().items.map((item) => item.productCode),
    ["A"],
  );
  assert.deepEqual(cart.added.map((item) => item.productCode), ["A"]);
  assert.equal(remote.pageCalls.length, 0);
  assert.equal(remote.markCalls.length, 0);
  assert.equal(repository.saveOrderCalls.length, 0);
  assert.equal(presenter.getState().statusCode, "online-required");
});

test("跨多个 opaque cursor 完整下载后仅执行一次原子替换", async () => {
  const repository = new MemorySpecialProductsRepository([product("OLD")]);
  const remote = new MemorySpecialProductsRemote();
  remote.pages.set(null, page([product("A")], "cursor-A", true, 3));
  remote.pages.set(
    "cursor-A",
    page([product("B")], "cursor-B", true, 3),
  );
  remote.pages.set("cursor-B", page([product("C")], null, false, 3));
  const presenter = createPresenter({ repository, remote, online: true });

  await presenter.download();

  assert.deepEqual(remote.pageCalls, [null, "cursor-A", "cursor-B"]);
  assert.equal(repository.replaceCalls.length, 1);
  assert.deepEqual(
    repository.replaceCalls[0]?.map((item) => item.productCode),
    ["A", "B", "C"],
  );
  assert.deepEqual(
    presenter.getState().items.map((item) => item.productCode),
    ["A", "B", "C"],
  );
  assert.equal(presenter.getState().statusCode, "download-complete");
});

test("下载列表含重复 productCode（一商品多码）时按商品去重且不中断", async () => {
  const repository = new MemorySpecialProductsRepository([product("OLD")]);
  const remote = new MemorySpecialProductsRemote();
  remote.pages.set(
    null,
    page([product("A"), product("B"), product("A")], null, false, 3),
  );
  const presenter = createPresenter({ repository, remote, online: true });

  await presenter.download();

  assert.deepEqual(
    repository.replaceCalls[0]?.map((item) => item.productCode),
    ["A", "B"],
  );
  assert.deepEqual(
    presenter.getState().items.map((item) => item.productCode),
    ["A", "B"],
  );
  assert.equal(presenter.getState().statusCode, "download-complete");
});

test("跨页重复 productCode 也按商品去重且不中断下载", async () => {
  const repository = new MemorySpecialProductsRepository([product("OLD")]);
  const remote = new MemorySpecialProductsRemote();
  remote.pages.set(null, page([product("A")], "cursor-A", true, 3));
  remote.pages.set(
    "cursor-A",
    page([product("B"), product("A")], null, false, 3),
  );
  const presenter = createPresenter({ repository, remote, online: true });

  await presenter.download();

  assert.deepEqual(
    repository.replaceCalls[0]?.map((item) => item.productCode),
    ["A", "B"],
  );
  assert.deepEqual(
    presenter.getState().items.map((item) => item.productCode),
    ["A", "B"],
  );
  assert.equal(presenter.getState().statusCode, "download-complete");
});

test("后续游标失败时保留原缓存且错误状态不泄漏底层异常", async () => {
  const repository = new MemorySpecialProductsRepository([product("OLD")]);
  const remote = new MemorySpecialProductsRemote();
  remote.pages.set(null, page([product("A")], "cursor-A", true, 2));
  remote.failCursor = "cursor-A";
  const presenter = createPresenter({ repository, remote, online: true });
  await presenter.load();

  await presenter.download();

  assert.equal(repository.replaceCalls.length, 0);
  assert.deepEqual(
    presenter.getState().items.map((item) => item.productCode),
    ["OLD"],
  );
  assert.equal(presenter.getState().statusCode, "download-failed");
  assert.equal(
    JSON.stringify(presenter.getState()).includes("bearer-secret"),
    false,
  );
});

test("较早的本地读取晚返回时不会覆盖较新的结果", async () => {
  const repository = new MemorySpecialProductsRepository([]);
  const first = deferred<readonly SpecialProductItem[]>();
  repository.listImplementation = (() => {
    let call = 0;
    return async () => {
      call += 1;
      return call === 1 ? first.promise : [product("NEW")];
    };
  })();
  const presenter = createPresenter({ repository });

  const staleLoad = presenter.load();
  await Promise.resolve();
  const currentLoad = presenter.load();
  await currentLoad;
  first.resolve([product("STALE")]);
  await staleLoad;

  assert.deepEqual(
    presenter.getState().items.map((item) => item.productCode),
    ["NEW"],
  );
});

test("销毁期间返回的下载页不再触发原子替换或状态通知", async () => {
  const repository = new MemorySpecialProductsRepository([product("OLD")]);
  const remote = new MemorySpecialProductsRemote();
  const pendingPage = deferred<SpecialProductDownloadPage>();
  remote.getPageImplementation = async () => pendingPage.promise;
  const presenter = createPresenter({ repository, remote, online: true });
  let notifications = 0;
  presenter.subscribe(() => {
    notifications += 1;
  });

  const download = presenter.download();
  await Promise.resolve();
  const notificationsBeforeDestroy = notifications;
  presenter.destroy();
  pendingPage.resolve(page([product("A")], null, false, 1));
  await download;

  assert.equal(repository.replaceCalls.length, 0);
  assert.equal(notifications, notificationsBeforeDestroy);
});

test("在线 Manage 才能标记、取消和保存完整本地排序", async () => {
  const repository = new MemorySpecialProductsRepository([
    product("A"),
    product("B"),
  ]);
  const remote = new MemorySpecialProductsRemote();
  remote.markResult = [withoutSortOrder(product("B"))];
  const presenter = createPresenter({ repository, remote, online: true });
  await presenter.load();

  await presenter.mark("A", false);
  assert.deepEqual(remote.markCalls, [
    { isSpecialProduct: false, productCode: "A", storeCode: "S1" },
  ]);
  assert.equal(repository.applyMarkCalls.length, 1);

  repository.items = [product("A"), product("B")];
  await presenter.load();
  await presenter.reorder("A", 1);
  assert.deepEqual(repository.saveOrderCalls, [["B", "A"]]);
});

test("moveTo 把商品移到任意目标索引并持久化完整排列", async () => {
  const repository = new MemorySpecialProductsRepository([
    product("A"),
    product("B"),
    product("C"),
    product("D"),
  ]);
  const presenter = createPresenter({ repository, online: true });
  await presenter.load();

  await presenter.moveTo("A", 2);
  assert.deepEqual(repository.saveOrderCalls, [["B", "C", "A", "D"]]);

  // 目标索引越界时拒绝并保留原排列
  await presenter.moveTo("D", 99);
  assert.equal(presenter.getState().statusCode, "reorder-failed");
  assert.equal(repository.saveOrderCalls.length, 1);
});

test("搜索候选的陈旧响应不会覆盖最新查询", async () => {
  const repository = new MemorySpecialProductsRepository([]);
  const first = deferred<readonly SpecialProductItem[]>();
  repository.searchImplementation = async (_storeCode, query) =>
    query === "alpha" ? first.promise : [product("BETA")];
  const presenter = createPresenter({ repository });

  presenter.setSearchQuery("alpha");
  const staleSearch = presenter.searchCandidates();
  await Promise.resolve();
  presenter.setSearchQuery("beta");
  await presenter.searchCandidates();
  first.resolve([product("ALPHA")]);
  await staleSearch;

  assert.equal(presenter.getState().searchQuery, "beta");
  assert.deepEqual(
    presenter.getState().candidates.map((item) => item.productCode),
    ["BETA"],
  );
});

test("候选查询与加购发布结构化反馈，不由状态文本推断声音", async () => {
  const cart = new MemorySpecialProductsCart();
  const presenter = createPresenter({
    cart,
    repository: new MemorySpecialProductsRepository([product("A")]),
  });
  const events: unknown[] = [];
  presenter.subscribeFeedback((event) => events.push(event));

  await presenter.load();
  presenter.setSearchQuery("tea");
  await presenter.searchCandidates();
  await presenter.addToCart("A");

  assert.deepEqual(events.map((event: any) => event.kind), [
    "query-empty",
    "added",
  ]);
});

test("非空候选查询被权限前置拒绝时只发布一次 query-error，空查询不伪造结果", async () => {
  const presenter = createPresenter({
    permissions: [
      SPECIAL_PRODUCTS_VIEW_PERMISSION,
      SPECIAL_PRODUCTS_ADD_TO_CART_PERMISSION,
    ],
  });
  const events: { kind: string }[] = [];
  presenter.subscribeFeedback((event) => events.push(event));

  presenter.setSearchQuery("tea");
  await presenter.searchCandidates();
  assert.deepEqual(events.map((event) => event.kind), ["query-error"]);

  presenter.setSearchQuery("");
  await presenter.searchCandidates();
  assert.deepEqual(events.map((event) => event.kind), ["query-error"]);
});

test("特殊商品权限或目标前置失败时各发布一次 failed-blocked", async () => {
  const denied = createPresenter({
    permissions: [
      SPECIAL_PRODUCTS_VIEW_PERMISSION,
      SPECIAL_PRODUCTS_MANAGE_PERMISSION,
    ],
    repository: new MemorySpecialProductsRepository([product("A")]),
  });
  const deniedEvents: { kind: string }[] = [];
  denied.subscribeFeedback((event) => deniedEvents.push(event));
  await denied.load();
  await denied.addToCart("A");
  assert.deepEqual(deniedEvents.map((event) => event.kind), [
    "failed-blocked",
  ]);

  const missing = createPresenter({
    repository: new MemorySpecialProductsRepository([product("A")]),
  });
  const missingEvents: { kind: string }[] = [];
  missing.subscribeFeedback((event) => missingEvents.push(event));
  await missing.load();
  await missing.addToCart("MISSING");
  assert.deepEqual(missingEvents.map((event) => event.kind), [
    "failed-blocked",
  ]);
});

test("特殊商品连点加购只加一件：加购进行中与成功后都锁定（WPF 28f2f0833）", async () => {
  let releaseAdd: (() => void) | null = null;
  const added: string[] = [];
  const cart: SpecialProductsCartPort = {
    async add(item) {
      added.push(item.productCode);
      await new Promise<void>((resolve) => {
        releaseAdd = resolve;
      });
      return { lineId: item.productCode, kind: "added" };
    },
  };
  const presenter = createPresenter({
    cart,
    repository: new MemorySpecialProductsRepository([product("A"), product("B")]),
  });
  const events: { kind: string }[] = [];
  presenter.subscribeFeedback((event) => events.push(event));
  await presenter.load();

  const first = presenter.addToCart("A");
  // 第一次加购还没回来时连点（同一商品或别的商品）都被忽略。
  await presenter.addToCart("A");
  await presenter.addToCart("B");
  (releaseAdd as (() => void) | null)?.();
  await first;
  // 成功后路由层会返回收银台；退场期间的点击同样被忽略。
  await presenter.addToCart("A");

  assert.deepEqual(added, ["A"]);
  assert.deepEqual(events.map((event) => event.kind), ["added"]);
});

test("特殊商品加购失败立即解锁，留在本页可重试", async () => {
  let fail = true;
  const added: string[] = [];
  const cart: SpecialProductsCartPort = {
    async add(item) {
      if (fail) throw new Error("cart unavailable");
      added.push(item.productCode);
      return { lineId: item.productCode, kind: "added" };
    },
  };
  const presenter = createPresenter({
    cart,
    repository: new MemorySpecialProductsRepository([product("A")]),
  });
  await presenter.load();

  await presenter.addToCart("A");
  assert.equal(presenter.getState().statusCode, "add-to-cart-failed");
  fail = false;
  await presenter.addToCart("A");

  assert.deepEqual(added, ["A"]);
  assert.equal(presenter.getState().statusCode, "added-to-cart");
});

function createPresenter(
  overrides: Partial<{
    cart: SpecialProductsCartPort;
    online: boolean;
    permissions: readonly string[];
    remote: SpecialProductsRemotePort;
    repository: SpecialProductsRepositoryPort;
  }> = {},
) {
  return new SpecialProductsPresenter({
    addToCart: overrides.cart ?? new MemorySpecialProductsCart(),
    initialOnline: overrides.online ?? false,
    permissions: overrides.permissions ?? ALL_PERMISSIONS,
    remote: overrides.remote ?? new MemorySpecialProductsRemote(),
    repository:
      overrides.repository ?? new MemorySpecialProductsRepository([]),
    storeCode: "S1",
  });
}

class MemorySpecialProductsCart implements SpecialProductsCartPort {
  public readonly added: SpecialProductItem[] = [];

  public async add(item: SpecialProductItem): Promise<CartAddDisposition> {
    this.added.push(item);
    return { lineId: item.productCode, kind: "added" };
  }
}

class MemorySpecialProductsRemote implements SpecialProductsRemotePort {
  public readonly markCalls: {
    storeCode: string;
    productCode: string;
    isSpecialProduct: boolean;
  }[] = [];
  public readonly pageCalls: (string | null)[] = [];
  public readonly pages = new Map<string | null, SpecialProductDownloadPage>();
  public failCursor: string | null | undefined;
  public getPageImplementation:
    | ((input: {
        storeCode: string;
        cursor: string | null;
        pageSize: number;
      }) => Promise<SpecialProductDownloadPage>)
    | null = null;
  public markResult: readonly Omit<SpecialProductItem, "sortOrder">[] = [];

  public async getPage(input: {
    storeCode: string;
    cursor: string | null;
    pageSize: number;
  }): Promise<SpecialProductDownloadPage> {
    this.pageCalls.push(input.cursor);
    if (this.getPageImplementation) {
      return this.getPageImplementation(input);
    }
    if (this.failCursor === input.cursor) {
      throw new Error("GET /special-products bearer-secret");
    }
    const result = this.pages.get(input.cursor);
    if (!result) {
      return page([], null, false, 0);
    }
    return result;
  }

  public async mark(input: {
    storeCode: string;
    productCode: string;
    isSpecialProduct: boolean;
  }): Promise<readonly Omit<SpecialProductItem, "sortOrder">[]> {
    this.markCalls.push(input);
    return this.markResult;
  }
}

class MemorySpecialProductsRepository
  implements SpecialProductsRepositoryPort
{
  public readonly applyMarkCalls: {
    storeCode: string;
    productCode: string;
    isSpecialProduct: boolean;
    items: readonly Omit<SpecialProductItem, "sortOrder">[];
  }[] = [];
  public readonly replaceCalls: (readonly Omit<SpecialProductItem, "sortOrder">[])[] = [];
  public readonly saveOrderCalls: string[][] = [];
  public listCalls = 0;
  public listImplementation:
    | ((
        storeCode: string,
        limit: number,
        offset: number,
      ) => Promise<readonly SpecialProductItem[]>)
    | null = null;
  public searchImplementation:
    | ((
        storeCode: string,
        query: string,
        limit: number,
      ) => Promise<readonly SpecialProductItem[]>)
    | null = null;

  public constructor(public items: SpecialProductItem[]) {}

  public async list(
    storeCode: string,
    limit: number,
    offset: number,
  ): Promise<readonly SpecialProductItem[]> {
    this.listCalls += 1;
    if (this.listImplementation) {
      return this.listImplementation(storeCode, limit, offset);
    }
    return this.items.slice(offset, offset + limit);
  }

  public async searchCandidates(
    storeCode: string,
    query: string,
    limit: number,
  ): Promise<readonly SpecialProductItem[]> {
    if (this.searchImplementation) {
      return this.searchImplementation(storeCode, query, limit);
    }
    return [];
  }

  public async replaceDownloaded(
    _storeCode: string,
    items: readonly Omit<SpecialProductItem, "sortOrder">[],
  ): Promise<void> {
    this.replaceCalls.push(items);
    this.items = items.map((item, sortOrder) => ({ ...item, sortOrder }));
  }

  public async applyMark(
    storeCode: string,
    productCode: string,
    isSpecialProduct: boolean,
    items: readonly Omit<SpecialProductItem, "sortOrder">[],
  ): Promise<void> {
    this.applyMarkCalls.push({
      storeCode,
      productCode,
      isSpecialProduct,
      items,
    });
    if (isSpecialProduct) {
      const existing = new Map(
        this.items.map((item) => [item.productCode, item] as const),
      );
      for (const item of items) {
        existing.set(item.productCode, {
          ...item,
          sortOrder: existing.get(item.productCode)?.sortOrder ?? existing.size,
        });
      }
      this.items = [...existing.values()];
    } else {
      this.items = this.items.filter(
        (item) => item.productCode !== productCode,
      );
    }
  }

  public async saveOrder(
    _storeCode: string,
    orderedProductCodes: readonly string[],
  ): Promise<void> {
    this.saveOrderCalls.push([...orderedProductCodes]);
    const byCode = new Map(
      this.items.map((item) => [item.productCode, item] as const),
    );
    this.items = orderedProductCodes.map((productCode, sortOrder) => ({
      ...byCode.get(productCode)!,
      sortOrder,
    }));
  }
}

function product(
  productCode: string,
  storeCode = "S1",
): SpecialProductItem {
  return {
    barcode: `barcode-${productCode}`,
    discountRate: null,
    displayName: `Product ${productCode}`,
    itemNumber: `item-${productCode}`,
    lookupCode: `lookup-${productCode}`,
    priceSource: 0,
    productCode,
    productImage: null,
    quantityFactor: 1,
    referenceCode: null,
    retailPriceCents: 1_250,
    sortOrder: 0,
    storeCode,
  };
}

function withoutSortOrder(
  item: SpecialProductItem,
): Omit<SpecialProductItem, "sortOrder"> {
  const { sortOrder: _sortOrder, ...result } = item;
  return result;
}

function page(
  items: readonly SpecialProductItem[],
  nextCursor: string | null,
  hasMore: boolean,
  totalCount: number,
): SpecialProductDownloadPage {
  return {
    hasMore,
    items: items.map(withoutSortOrder),
    nextCursor,
    totalCount,
  };
}

function deferred<T>() {
  let resolve!: (value: T) => void;
  const promise = new Promise<T>((resolvePromise) => {
    resolve = resolvePromise;
  });
  return { promise, resolve };
}
