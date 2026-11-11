import assert from "node:assert/strict";
import test from "node:test";

import {
  SPECIAL_PRODUCT_CATEGORY_ASSIGN_CONFLICT_CODE,
  SPECIAL_PRODUCT_CATEGORY_NOT_FOUND_CODE,
  SPECIAL_PRODUCT_CATEGORY_ORDER_CONFLICT_CODE,
  type SpecialProductCategoriesRemotePort,
  type SpecialProductCategoriesRepositoryPort,
  type SpecialProductCategory,
  type SpecialProductCategoryAssignment,
  type SpecialProductCategoryLocalSnapshot,
  type SpecialProductCategorySnapshot,
} from "@hb/pos-domain/core/contracts/special-product-categories";
import {
  SPECIAL_PRODUCTS_ADD_TO_CART_PERMISSION,
  SPECIAL_PRODUCTS_MANAGE_PERMISSION,
  SPECIAL_PRODUCTS_VIEW_PERMISSION,
} from "@hb/pos-domain/features/special-products/special-products-authorization";
import { HbposApiError } from "@hb/pos-api-client/transport";

import { SpecialProductsPresenter } from "./special-products-presenter";

import type {
  SpecialProductDownloadPage,
  SpecialProductItem,
  SpecialProductsRemotePort,
  SpecialProductsRepositoryPort,
} from "@/core/contracts";

const ALL_PERMISSIONS = [
  SPECIAL_PRODUCTS_VIEW_PERMISSION,
  SPECIAL_PRODUCTS_MANAGE_PERMISSION,
  SPECIAL_PRODUCTS_ADD_TO_CART_PERMISSION,
] as const;

/** 本机顺序 A B C D E；分类 C1（气球）= [C, A]（服务器顺序 C 在前），C2（贺卡）= [B]，D / E 未分类。 */
function fixture() {
  const harness = createHarness({
    items: ["A", "B", "C", "D", "E"].map((code) => product(code)),
    snapshot: snapshot({
      assignments: [
        { categoryId: "C1", productCode: "A", sortOrder: 1 },
        { categoryId: "C1", productCode: "C", sortOrder: 0 },
        { categoryId: "C2", productCode: "B", sortOrder: 0 },
      ],
    }),
  });
  return harness;
}

test("没有任何分类时一切与分类功能上线前一致：列表 = 本机顺序，选中恒为全部", async () => {
  const harness = createHarness({
    items: [product("A"), product("B")],
    snapshot: snapshot({ assignments: [], categories: [] }),
  });
  await harness.presenter.load();

  const state = harness.presenter.getState();
  assert.equal(state.categories.length, 0);
  assert.equal(state.selectedCategoryId, "all");
  assert.equal(state.visibleItems, state.items);
  assert.deepEqual(state.categoryCounts, {
    all: 2,
    byCategory: {},
    uncategorized: 2,
  });

  // 没分类：选中任何分类都被忽略；本机排序照旧（saveOrder，不请求服务器）。
  harness.presenter.selectCategory("C1");
  assert.equal(harness.presenter.getState().selectedCategoryId, "all");
  await harness.presenter.reorder("A", 1);
  assert.deepEqual(harness.repository.saveOrderCalls, [["B", "A"]]);
  assert.equal(harness.categoriesRemote.setOrderCalls.length, 0);
  assert.equal(harness.presenter.getState().statusCode, "reorder-complete");
});

test("不提供 categories 选项时不拉取、不读本机分类，所有分类方法无副作用", async () => {
  const repository = new MemoryItemsRepository([product("A")]);
  const presenter = new SpecialProductsPresenter({
    addToCart: { add: async (item) => ({ kind: "added", lineId: item.productCode }) },
    initialOnline: true,
    permissions: ALL_PERMISSIONS,
    remote: new MemoryItemsRemote(),
    repository,
    storeCode: "S1",
  });
  await presenter.load();
  presenter.selectCategory("C1");
  await presenter.assignToCategory("A", "C1");

  assert.equal(presenter.getState().visibleItems, presenter.getState().items);
  assert.equal(presenter.getState().statusCode, "assign-failed");
});

test("进页面：先读本机快照（离线可用），在线后台拉一次并落库", async () => {
  const harness = createHarness({
    items: [product("A"), product("B")],
    localSnapshot: {
      categories: [category("C1", "Old", 0)],
      assignments: [{ categoryId: "C1", productCode: "A", sortOrder: 0 }],
    },
    snapshot: snapshot({
      assignments: [{ categoryId: "C2", productCode: "A", sortOrder: 0 }],
    }),
    online: false,
  });

  await harness.presenter.load();
  // 离线：只有本机快照，不请求服务器。
  assert.deepEqual(
    harness.presenter.getState().categories.map((item) => item.categoryId),
    ["C1"],
  );
  assert.equal(harness.categoriesRemote.getCalls, 0);

  // 路由随后发布真实网络状态：恢复在线 → 后台拉取一次。
  harness.presenter.setOnline(true);
  await harness.settle();
  assert.equal(harness.categoriesRemote.getCalls, 1);
  assert.deepEqual(
    harness.presenter.getState().categories.map((item) => item.categoryId),
    ["C1", "C2"],
  );
  assert.equal(harness.categoriesRepository.replaceCalls.length, 1);

  // 已同步成功：再次 load 不重复拉取；掉线再恢复才会重新拉。
  await harness.presenter.load();
  await harness.settle();
  assert.equal(harness.categoriesRemote.getCalls, 1);
  harness.presenter.setOnline(false);
  harness.presenter.setOnline(true);
  await harness.settle();
  assert.equal(harness.categoriesRemote.getCalls, 2);
});

test("拉取分类失败静默：列表与状态不受影响，下次 load 重试", async () => {
  const harness = createHarness({
    items: [product("A")],
    online: true,
    snapshot: snapshot(),
  });
  harness.categoriesRemote.getError = new Error("network down bearer-secret");

  await harness.presenter.load();
  await harness.settle();

  const state = harness.presenter.getState();
  assert.equal(state.kind, "ready");
  assert.equal(state.statusCode, null);
  assert.deepEqual(state.items.map((item) => item.productCode), ["A"]);
  assert.equal(state.categories.length, 0);
  assert.equal(harness.categoriesRemote.getCalls, 1);

  harness.categoriesRemote.getError = null;
  await harness.presenter.load();
  await harness.settle();
  assert.equal(harness.categoriesRemote.getCalls, 2);
  assert.equal(harness.presenter.getState().categories.length, 2);
});

test("分类视图派生：全部 / 各分类（服务器顺序）/ 未分类（本机顺序），数量只数仍是特殊商品的", async () => {
  const harness = fixture();
  // 本机取消标记过的商品 Z 的归属还在快照里：不计数、不显示。
  harness.categoriesRepository.local = {
    categories: harness.categoriesRemote.snapshot.categories,
    assignments: [
      ...harness.categoriesRemote.snapshot.assignments,
      { categoryId: "C2", productCode: "Z", sortOrder: 5 },
    ],
  };
  await harness.presenter.load();
  await harness.settle();
  const presenter = harness.presenter;
  const codes = () =>
    presenter.getState().visibleItems.map((item) => item.productCode);

  assert.deepEqual(presenter.getState().categoryCounts, {
    all: 5,
    byCategory: { C1: 2, C2: 1 },
    uncategorized: 2,
  });
  assert.deepEqual(codes(), ["A", "B", "C", "D", "E"]);

  presenter.selectCategory("C1");
  assert.equal(presenter.getState().selectedCategoryId, "C1");
  assert.deepEqual(codes(), ["C", "A"]);

  presenter.selectCategory("uncategorized");
  assert.deepEqual(codes(), ["D", "E"]);

  presenter.selectCategory("all");
  assert.deepEqual(codes(), ["A", "B", "C", "D", "E"]);
  // 选中项按分店记在本机。
  assert.deepEqual(harness.categoriesRepository.selectedWrites, [
    "C1",
    "uncategorized",
    "all",
  ]);
});

test("「未分类」清空后回到全部（该行隐藏），选中项记录保留", async () => {
  const harness = createHarness({
    items: [product("A")],
    online: true,
    selected: "uncategorized",
    snapshot: snapshot({
      assignments: [{ categoryId: "C1", productCode: "A", sortOrder: 0 }],
    }),
  });
  await harness.presenter.load();
  await harness.settle();
  assert.equal(harness.presenter.getState().categoryCounts.uncategorized, 0);
  assert.equal(harness.presenter.getState().selectedCategoryId, "all");
});

test("还原本机记住的分类；分类被删除则回到全部并更新记录", async () => {
  const restored = createHarness({
    items: [product("A"), product("B")],
    online: true,
    selected: "C2",
    snapshot: snapshot({
      assignments: [{ categoryId: "C2", productCode: "B", sortOrder: 0 }],
    }),
    localSnapshot: {
      categories: [category("C1", "Balloons", 0), category("C2", "Cards", 1)],
      assignments: [{ categoryId: "C2", productCode: "B", sortOrder: 0 }],
    },
  });
  await restored.presenter.load();
  assert.equal(restored.presenter.getState().selectedCategoryId, "C2");
  assert.deepEqual(
    restored.presenter.getState().visibleItems.map((item) => item.productCode),
    ["B"],
  );

  // 服务器返回的分类里已没有 C2（被手机删除）→ 回到全部，并把「全部」记到本机。
  const deleted = createHarness({
    items: [product("A"), product("B")],
    online: true,
    selected: "C2",
    snapshot: snapshot({
      assignments: [],
      categories: [category("C1", "Balloons", 0)],
    }),
    localSnapshot: {
      categories: [category("C1", "Balloons", 0), category("C2", "Cards", 1)],
      assignments: [],
    },
  });
  await deleted.presenter.load();
  await deleted.settle();
  assert.equal(deleted.presenter.getState().selectedCategoryId, "all");
  assert.deepEqual(deleted.categoriesRepository.selectedWrites.at(-1), "all");
});

test("搜索在全部特殊商品里过滤，不受当前分类限制；清空回到原分类；与添加弹窗的 searchQuery 独立", async () => {
  const harness = fixture();
  await harness.presenter.load();
  await harness.settle();
  const presenter = harness.presenter;
  presenter.selectCategory("C2"); // 只有 B

  presenter.setSearch("lookup-d");
  assert.deepEqual(
    presenter.getState().visibleItems.map((item) => item.productCode),
    ["D"],
  );
  // 货号 / 条码 / 商品编码也能命中；多个关键词需全部命中。
  presenter.setSearch("ITEM-e");
  assert.deepEqual(
    presenter.getState().visibleItems.map((item) => item.productCode),
    ["E"],
  );
  presenter.setSearch("barcode-a");
  assert.deepEqual(
    presenter.getState().visibleItems.map((item) => item.productCode),
    ["A"],
  );
  presenter.setSearch("product  c   lookup-c");
  assert.deepEqual(
    presenter.getState().visibleItems.map((item) => item.productCode),
    ["C"],
  );
  presenter.setSearch("product zzz");
  assert.deepEqual(presenter.getState().visibleItems, []);
  // 添加弹窗用的 searchQuery 不受影响。
  assert.equal(presenter.getState().searchQuery, "");
  presenter.setSearchQuery("弹窗词");
  assert.equal(presenter.getState().search, "product zzz");

  presenter.setSearch("");
  assert.equal(presenter.getState().selectedCategoryId, "C2");
  assert.deepEqual(
    presenter.getState().visibleItems.map((item) => item.productCode),
    ["B"],
  );

  // 选择分类意味着要浏览该分类：同时清空搜索词。
  presenter.setSearch("lookup-d");
  presenter.selectCategory("C1");
  assert.equal(presenter.getState().search, "");
  assert.deepEqual(
    presenter.getState().visibleItems.map((item) => item.productCode),
    ["C", "A"],
  );
});

test("移到分类：需要 Manage 权限 + 在线，成功后用返回的快照覆盖本机", async () => {
  const harness = fixture();
  await harness.presenter.load();
  await harness.settle();
  const presenter = harness.presenter;

  // 离线：锁定，不请求。
  presenter.setOnline(false);
  await presenter.assignToCategory("D", "C2");
  assert.equal(presenter.getState().statusCode, "online-required");
  assert.equal(harness.categoriesRemote.assignCalls.length, 0);
  presenter.setOnline(true);
  await harness.settle();

  harness.categoriesRemote.snapshot = snapshot({
    assignments: [
      { categoryId: "C1", productCode: "A", sortOrder: 1 },
      { categoryId: "C1", productCode: "C", sortOrder: 0 },
      { categoryId: "C2", productCode: "B", sortOrder: 0 },
      { categoryId: "C2", productCode: "D", sortOrder: 1 },
    ],
  });
  await presenter.assignToCategory("D", "C2");

  assert.deepEqual(harness.categoriesRemote.assignCalls, [
    { categoryId: "C2", productCode: "D", storeCode: "S1" },
  ]);
  assert.equal(presenter.getState().statusCode, "assign-complete");
  assert.equal(presenter.getState().busy, false);
  assert.deepEqual(presenter.getState().categoryCounts, {
    all: 5,
    byCategory: { C1: 2, C2: 2 },
    uncategorized: 1,
  });
  // 本机落库用的是服务器返回的快照。
  assert.equal(
    harness.categoriesRepository.replaceCalls.at(-1)?.assignments.length,
    4,
  );

  // 移回「未分类」：categoryId = null。
  harness.categoriesRemote.snapshot = snapshot({
    assignments: [{ categoryId: "C1", productCode: "A", sortOrder: 0 }],
  });
  await presenter.assignToCategory("D", null);
  assert.deepEqual(harness.categoriesRemote.assignCalls.at(-1), {
    categoryId: null,
    productCode: "D",
    storeCode: "S1",
  });
  assert.equal(presenter.getState().categoryCounts.byCategory.C2, 0);
});

test("移到分类：已在目标分类不请求；无 Manage 权限被拒；目标分类 / 商品不存在失败；失败不泄漏底层错误", async () => {
  const harness = fixture();
  await harness.presenter.load();
  await harness.settle();
  const presenter = harness.presenter;

  await presenter.assignToCategory("A", "C1"); // 已在 C1
  assert.equal(harness.categoriesRemote.assignCalls.length, 0);
  assert.equal(presenter.getState().statusCode, "assign-complete");
  await presenter.assignToCategory("D", null); // 本来就未分类
  assert.equal(harness.categoriesRemote.assignCalls.length, 0);

  await presenter.assignToCategory("A", "NOPE");
  assert.equal(presenter.getState().statusCode, "assign-failed");
  await presenter.assignToCategory("MISSING", "C1");
  assert.equal(presenter.getState().statusCode, "assign-failed");
  assert.equal(harness.categoriesRemote.assignCalls.length, 0);

  harness.categoriesRemote.assignError = new Error("POST bearer-secret");
  await presenter.assignToCategory("D", "C2");
  assert.equal(presenter.getState().statusCode, "assign-failed");
  assert.equal(presenter.getState().busy, false);
  assert.doesNotMatch(JSON.stringify(presenter.getState()), /bearer-secret/u);

  const viewOnly = createHarness({
    items: [product("A")],
    online: true,
    permissions: [SPECIAL_PRODUCTS_VIEW_PERMISSION],
    snapshot: snapshot(),
  });
  await viewOnly.presenter.load();
  await viewOnly.presenter.assignToCategory("A", "C1");
  assert.equal(viewOnly.presenter.getState().statusCode, "permission-required");
  assert.equal(viewOnly.categoriesRemote.assignCalls.length, 0);
});

test("移到分类：服务器说分类已不存在（404）时刷新快照", async () => {
  const harness = fixture();
  await harness.presenter.load();
  await harness.settle();
  harness.categoriesRemote.assignError = new HbposApiError("gone", {
    code: SPECIAL_PRODUCT_CATEGORY_NOT_FOUND_CODE,
    kind: "http",
    status: 404,
  });
  harness.categoriesRemote.snapshot = snapshot({
    assignments: [],
    categories: [category("C1", "Balloons", 0)],
  });
  const before = harness.categoriesRemote.getCalls;

  await harness.presenter.assignToCategory("D", "C2");

  assert.equal(harness.presenter.getState().statusCode, "assign-failed");
  assert.equal(harness.categoriesRemote.getCalls, before + 1);
  assert.deepEqual(
    harness.presenter.getState().categories.map((item) => item.categoryId),
    ["C1"],
  );
});

test("移到分类：两台收银机同时首次归类（409 ASSIGN_CONFLICT）时刷新快照并提示 assign-conflict", async () => {
  const harness = fixture();
  await harness.presenter.load();
  await harness.settle();
  harness.categoriesRemote.assignError = new HbposApiError("conflict", {
    apiEnvelope: true,
    code: SPECIAL_PRODUCT_CATEGORY_ASSIGN_CONFLICT_CODE,
    kind: "http",
    status: 409,
  });
  // 别的收银机刚把 D 归到了 C2。
  harness.categoriesRemote.snapshot = snapshot({
    assignments: [
      { categoryId: "C1", productCode: "A", sortOrder: 1 },
      { categoryId: "C1", productCode: "C", sortOrder: 0 },
      { categoryId: "C2", productCode: "B", sortOrder: 0 },
      { categoryId: "C2", productCode: "D", sortOrder: 1 },
    ],
  });
  const before = harness.categoriesRemote.getCalls;

  await harness.presenter.assignToCategory("D", "C1");

  assert.equal(harness.presenter.getState().statusCode, "assign-conflict");
  assert.equal(harness.presenter.getState().busy, false);
  assert.equal(harness.categoriesRemote.getCalls, before + 1);
  assert.equal(harness.presenter.getState().categoryCounts.byCategory.C2, 2);
});

test("选中真实分类时 ↑ ↓ / moveTo 走服务器 order 接口（整个分类的新排列），成功用返回快照覆盖", async () => {
  const harness = fixture();
  await harness.presenter.load();
  await harness.settle();
  const presenter = harness.presenter;
  presenter.selectCategory("C1"); // 视图 [C, A]

  harness.categoriesRemote.snapshot = snapshot({
    assignments: [
      { categoryId: "C1", productCode: "A", sortOrder: 0 },
      { categoryId: "C1", productCode: "C", sortOrder: 1 },
      { categoryId: "C2", productCode: "B", sortOrder: 0 },
    ],
  });
  await presenter.reorder("C", 1);

  assert.deepEqual(harness.categoriesRemote.setOrderCalls, [
    { categoryId: "C1", productCodes: ["A", "C"], storeCode: "S1" },
  ]);
  // 分类内顺序归服务器：不动本机排序。
  assert.equal(harness.repository.saveOrderCalls.length, 0);
  assert.equal(presenter.getState().statusCode, "category-order-complete");
  assert.deepEqual(
    presenter.getState().visibleItems.map((item) => item.productCode),
    ["A", "C"],
  );

  // moveTo：把 C 移回第 0 位。
  harness.categoriesRemote.snapshot = fixtureSnapshot();
  await presenter.moveTo("C", 0);
  assert.deepEqual(harness.categoriesRemote.setOrderCalls.at(-1), {
    categoryId: "C1",
    productCodes: ["C", "A"],
    storeCode: "S1",
  });
  assert.deepEqual(
    presenter.getState().visibleItems.map((item) => item.productCode),
    ["C", "A"],
  );

  // 越界目标：失败，不请求服务器。
  const calls = harness.categoriesRemote.setOrderCalls.length;
  await presenter.moveTo("C", 9);
  assert.equal(presenter.getState().statusCode, "reorder-failed");
  assert.equal(harness.categoriesRemote.setOrderCalls.length, calls);
});

test("分类内顺序冲突（409）：刷新快照并提示 order-conflict", async () => {
  const harness = fixture();
  await harness.presenter.load();
  await harness.settle();
  const presenter = harness.presenter;
  presenter.selectCategory("C1");
  harness.categoriesRemote.setOrderError = new HbposApiError("conflict", {
    apiEnvelope: true,
    code: SPECIAL_PRODUCT_CATEGORY_ORDER_CONFLICT_CODE,
    kind: "http",
    status: 409,
  });
  // 别的设备刚把 E 放进了 C1。
  harness.categoriesRemote.snapshot = snapshot({
    assignments: [
      { categoryId: "C1", productCode: "A", sortOrder: 1 },
      { categoryId: "C1", productCode: "C", sortOrder: 0 },
      { categoryId: "C1", productCode: "E", sortOrder: 2 },
      { categoryId: "C2", productCode: "B", sortOrder: 0 },
    ],
  });
  const before = harness.categoriesRemote.getCalls;

  await presenter.reorder("C", 1);

  assert.equal(presenter.getState().statusCode, "order-conflict");
  assert.equal(presenter.getState().busy, false);
  assert.equal(harness.categoriesRemote.getCalls, before + 1);
  assert.deepEqual(
    presenter.getState().visibleItems.map((item) => item.productCode),
    ["C", "A", "E"],
  );
});

test("选中「全部 / 未分类」时保持本机排序逻辑：不请求服务器 order，未分类只重排自己的商品", async () => {
  const harness = fixture();
  await harness.presenter.load();
  await harness.settle();
  const presenter = harness.presenter;

  await presenter.reorder("A", 1); // 全部：A、B 互换
  assert.deepEqual(harness.repository.saveOrderCalls.at(-1), [
    "B",
    "A",
    "C",
    "D",
    "E",
  ]);
  assert.equal(presenter.getState().statusCode, "reorder-complete");

  presenter.selectCategory("uncategorized"); // [D, E]
  await presenter.reorder("E", -1);
  // D、E 占的位置互换，A B C 的相对位置不变。
  assert.deepEqual(harness.repository.saveOrderCalls.at(-1), [
    "B",
    "A",
    "C",
    "E",
    "D",
  ]);
  assert.equal(harness.categoriesRemote.setOrderCalls.length, 0);
  assert.deepEqual(
    presenter.getState().visibleItems.map((item) => item.productCode),
    ["E", "D"],
  );
});

test("搜索中不能排序（视图只是子集合）；移到分类仍可用", async () => {
  const harness = fixture();
  await harness.presenter.load();
  await harness.settle();
  const presenter = harness.presenter;
  presenter.setSearch("product");

  await presenter.reorder("A", 1);
  assert.equal(presenter.getState().statusCode, "reorder-failed");
  assert.equal(harness.repository.saveOrderCalls.length, 0);
  assert.equal(harness.categoriesRemote.setOrderCalls.length, 0);

  harness.categoriesRemote.snapshot = fixtureSnapshot();
  await presenter.assignToCategory("D", "C2");
  assert.equal(presenter.getState().statusCode, "assign-complete");
});

test("下载更新时一并拉取分类；分类拉取失败不让商品下载失败", async () => {
  const harness = createHarness({
    items: [product("OLD")],
    online: true,
    snapshot: snapshot({
      assignments: [{ categoryId: "C1", productCode: "A", sortOrder: 0 }],
    }),
  });
  harness.itemsRemote.pages = [product("A"), product("B")];
  await harness.presenter.load();
  await harness.settle();
  const before = harness.categoriesRemote.getCalls;
  harness.categoriesRemote.snapshot = snapshot({
    assignments: [{ categoryId: "C2", productCode: "B", sortOrder: 0 }],
  });

  await harness.presenter.download();

  assert.equal(harness.presenter.getState().statusCode, "download-complete");
  assert.equal(harness.categoriesRemote.getCalls, before + 1);
  assert.deepEqual(harness.presenter.getState().categoryCounts.byCategory, {
    C1: 0,
    C2: 1,
  });

  harness.categoriesRemote.getError = new Error("categories endpoint missing");
  await harness.presenter.download();
  assert.equal(harness.presenter.getState().statusCode, "download-complete");
  assert.deepEqual(
    harness.presenter.getState().items.map((item) => item.productCode),
    ["A", "B"],
  );
});

test("慢返回的旧分类读取不会盖掉更新的在线调整结果", async () => {
  const harness = fixture();
  await harness.presenter.load();
  await harness.settle();
  const presenter = harness.presenter;

  // 一次慢的后台拉取（重新在线触发）还没回来……
  const slow = deferred<SpecialProductCategorySnapshot>();
  harness.categoriesRemote.getImplementation = () => slow.promise;
  presenter.setOnline(false);
  presenter.setOnline(true);
  await harness.settle();

  // ……期间在线调整成功，应用了更新的快照。
  const adjusted = snapshot({
    assignments: [{ categoryId: "C2", productCode: "D", sortOrder: 0 }],
  });
  harness.categoriesRemote.snapshot = adjusted;
  await presenter.assignToCategory("D", "C2");
  assert.equal(presenter.getState().categoryCounts.byCategory.C2, 1);

  // 旧读取此时才返回（内容是调整前的数据）：必须被丢弃。
  slow.resolve(fixtureSnapshot());
  await harness.settle();
  assert.equal(presenter.getState().categoryCounts.byCategory.C2, 1);
  assert.equal(harness.categoriesRepository.replaceCalls.at(-1)?.assignments.length, 1);
});

test("销毁后不再落库、不再通知", async () => {
  const harness = fixture();
  const slow = deferred<SpecialProductCategorySnapshot>();
  harness.categoriesRemote.getImplementation = () => slow.promise;
  await harness.presenter.load();
  await harness.settle();
  harness.presenter.destroy();
  slow.resolve(fixtureSnapshot());
  await harness.settle();
  assert.equal(harness.categoriesRepository.replaceCalls.length, 0);
});

// ───────────────────────── 测试替身 ─────────────────────────

function createHarness(
  options: Readonly<{
    items: readonly SpecialProductItem[];
    snapshot: SpecialProductCategorySnapshot;
    localSnapshot?: SpecialProductCategoryLocalSnapshot;
    online?: boolean;
    permissions?: readonly string[];
    selected?: string;
  }>,
) {
  const repository = new MemoryItemsRepository([...options.items]);
  const itemsRemote = new MemoryItemsRemote();
  const categoriesRemote = new MemoryCategoriesRemote(options.snapshot);
  const categoriesRepository = new MemoryCategoriesRepository(
    options.localSnapshot ?? { assignments: [], categories: [] },
    options.selected ?? null,
  );
  const presenter = new SpecialProductsPresenter({
    addToCart: {
      add: async (item) => ({ kind: "added", lineId: item.productCode }),
    },
    categories: { remote: categoriesRemote, repository: categoriesRepository },
    initialOnline: options.online ?? true,
    permissions: options.permissions ?? ALL_PERMISSIONS,
    remote: itemsRemote,
    repository,
    storeCode: "S1",
  });
  return {
    categoriesRemote,
    categoriesRepository,
    itemsRemote,
    presenter,
    repository,
    /** 让后台（不被 await 的）拉取与落库跑完。 */
    settle: async () => {
      for (let index = 0; index < 10; index += 1) {
        await new Promise((resolve) => setImmediate(resolve));
      }
    },
  };
}

class MemoryCategoriesRemote implements SpecialProductCategoriesRemotePort {
  public getCalls = 0;
  public getError: Error | null = null;
  public getImplementation:
    | (() => Promise<SpecialProductCategorySnapshot>)
    | null = null;
  public assignError: Error | null = null;
  public setOrderError: Error | null = null;
  public readonly assignCalls: {
    storeCode: string;
    productCode: string;
    categoryId: string | null;
  }[] = [];
  public readonly setOrderCalls: {
    storeCode: string;
    categoryId: string;
    productCodes: readonly string[];
  }[] = [];

  public constructor(public snapshot: SpecialProductCategorySnapshot) {}

  public async getCategories(): Promise<SpecialProductCategorySnapshot> {
    this.getCalls += 1;
    if (this.getError) throw this.getError;
    if (this.getImplementation) return this.getImplementation();
    return this.snapshot;
  }

  public async assign(input: {
    storeCode: string;
    productCode: string;
    categoryId: string | null;
  }): Promise<SpecialProductCategorySnapshot> {
    this.assignCalls.push(input);
    if (this.assignError) throw this.assignError;
    return this.snapshot;
  }

  public async setOrder(input: {
    storeCode: string;
    categoryId: string;
    productCodes: readonly string[];
  }): Promise<SpecialProductCategorySnapshot> {
    this.setOrderCalls.push({ ...input });
    if (this.setOrderError) throw this.setOrderError;
    return this.snapshot;
  }
}

class MemoryCategoriesRepository
  implements SpecialProductCategoriesRepositoryPort
{
  public readonly replaceCalls: SpecialProductCategorySnapshot[] = [];
  public readonly selectedWrites: string[] = [];

  public constructor(
    public local: SpecialProductCategoryLocalSnapshot,
    public selected: string | null,
  ) {}

  public async readSnapshot(): Promise<SpecialProductCategoryLocalSnapshot> {
    return this.local;
  }

  public async replaceSnapshot(
    snapshotValue: SpecialProductCategorySnapshot,
  ): Promise<void> {
    this.replaceCalls.push(snapshotValue);
    this.local = {
      assignments: snapshotValue.assignments,
      categories: snapshotValue.categories,
    };
  }

  public async listCategoryProducts(): Promise<readonly SpecialProductItem[]> {
    return [];
  }

  public async readSelectedCategory(): Promise<string | null> {
    return this.selected;
  }

  public async saveSelectedCategory(
    _storeCode: string,
    selection: string,
  ): Promise<void> {
    this.selected = selection;
    this.selectedWrites.push(selection);
  }
}

class MemoryItemsRemote implements SpecialProductsRemotePort {
  public pages: readonly SpecialProductItem[] = [];

  public async getPage(): Promise<SpecialProductDownloadPage> {
    return {
      hasMore: false,
      items: this.pages.map(({ sortOrder: _sortOrder, ...rest }) => rest),
      nextCursor: null,
      totalCount: this.pages.length,
    };
  }

  public async mark(): Promise<readonly Omit<SpecialProductItem, "sortOrder">[]> {
    return [];
  }
}

class MemoryItemsRepository implements SpecialProductsRepositoryPort {
  public readonly saveOrderCalls: string[][] = [];

  public constructor(public items: SpecialProductItem[]) {}

  public async list(
    _storeCode: string,
    limit: number,
    offset: number,
  ): Promise<readonly SpecialProductItem[]> {
    return this.items.slice(offset, offset + limit);
  }

  public async searchCandidates(): Promise<readonly SpecialProductItem[]> {
    return [];
  }

  public async replaceDownloaded(
    _storeCode: string,
    items: readonly Omit<SpecialProductItem, "sortOrder">[],
  ): Promise<void> {
    this.items = items.map((item, sortOrder) => ({ ...item, sortOrder }));
  }

  public async applyMark(): Promise<void> {}

  public async saveOrder(
    _storeCode: string,
    orderedProductCodes: readonly string[],
  ): Promise<void> {
    this.saveOrderCalls.push([...orderedProductCodes]);
    const byCode = new Map(this.items.map((item) => [item.productCode, item]));
    this.items = orderedProductCodes.map((productCode, sortOrder) => ({
      ...byCode.get(productCode)!,
      sortOrder,
    }));
  }
}

function category(
  categoryId: string,
  name: string,
  sortOrder: number,
): SpecialProductCategory {
  return { categoryId, color: null, name, sortOrder };
}

function snapshot(
  overrides: Partial<{
    assignments: readonly SpecialProductCategoryAssignment[];
    categories: readonly SpecialProductCategory[];
  }> = {},
): SpecialProductCategorySnapshot {
  return {
    assignments: overrides.assignments ?? [],
    categories: overrides.categories ?? [
      category("C1", "Balloons", 0),
      category("C2", "Cards", 1),
    ],
    generatedAt: "2026-11-11T00:00:00.000Z",
    revision: "1",
    storeCode: "S1",
  };
}

function fixtureSnapshot(): SpecialProductCategorySnapshot {
  return snapshot({
    assignments: [
      { categoryId: "C1", productCode: "A", sortOrder: 1 },
      { categoryId: "C1", productCode: "C", sortOrder: 0 },
      { categoryId: "C2", productCode: "B", sortOrder: 0 },
    ],
  });
}

function product(productCode: string): SpecialProductItem {
  return {
    barcode: `barcode-${productCode.toLowerCase()}`,
    discountRate: null,
    displayName: `Product ${productCode}`,
    itemNumber: `item-${productCode}`,
    lookupCode: `lookup-${productCode.toLowerCase()}`,
    priceSource: 0,
    productCode,
    productImage: null,
    quantityFactor: 1,
    referenceCode: null,
    retailPriceCents: 1_250,
    sortOrder: 0,
    storeCode: "S1",
  };
}

function deferred<T>() {
  let resolve!: (value: T) => void;
  const promise = new Promise<T>((resolvePromise) => {
    resolve = resolvePromise;
  });
  return { promise, resolve };
}
