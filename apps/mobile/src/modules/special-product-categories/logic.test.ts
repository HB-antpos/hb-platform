import assert from "node:assert/strict";
import test from "node:test";
import {
  applyLocalOrder,
  buildSlotMap,
  buildSyncRequest,
  canSearchCandidates,
  chunk,
  classifyCandidate,
  computeDropIndex,
  errorCodeKey,
  getErrorCode,
  hasDestructiveChanges,
  isConflictError,
  MAX_BATCH_PRODUCT_CODES,
  MAX_CATEGORIES_PER_STORE,
  moveItem,
  normalizeAssignResult,
  normalizeCandidates,
  normalizeColor,
  normalizeOverview,
  normalizeProductsPage,
  normalizeSyncPreview,
  normalizeSyncResults,
  normalizeSyncTargets,
  pickInitialStore,
  pruneSelection,
  resolveManagedStores,
  sameOrder,
  skippedReasonKey,
  sumSyncCounts,
  summarizeSyncOutcome,
  syncResultErrorKey,
  validateCategoryName,
  willMarkNewSpecialProducts,
} from "./logic";
import type { CandidateProduct, SpecialCategory } from "./types";

const cat = (id: string, name: string, sortOrder = 0): SpecialCategory => ({ id, name, color: null, sortOrder, itemCount: 0 });

test("总览归一化：按 sortOrder 排序、数字字符串与缺字段容忍、颜色只认 #RRGGBB", () => {
  const overview = normalizeOverview(
    {
      success: true,
      data: {
        storeCode: "1042",
        totalCount: "128",
        uncategorizedCount: 6,
        categories: [
          { id: "b", name: "气球", color: "#1677ff", sortOrder: 2, itemCount: 22 },
          { id: "a", name: "购物袋", color: "red", sortOrder: 1, itemCount: "8" },
          { name: "缺 id 的行被丢弃" },
        ],
      },
    },
    "fallback",
  );
  assert.equal(overview.storeCode, "1042");
  assert.equal(overview.totalCount, 128);
  assert.equal(overview.uncategorizedCount, 6);
  assert.deepEqual(overview.categories.map((item) => item.id), ["a", "b"]);
  assert.equal(overview.categories[0].color, null, "非 #RRGGBB 当没有颜色");
  assert.equal(overview.categories[1].color, "#1677FF", "颜色大写化");
  assert.equal(overview.categories[0].itemCount, 8);
  // 空响应不抛错，沿用传入的分店编号。
  const empty = normalizeOverview({ success: true, data: null }, "1042");
  assert.deepEqual([empty.storeCode, empty.totalCount, empty.categories.length], ["1042", 0, 0]);
});

test("商品分页与候选商品归一化", () => {
  const page = normalizeProductsPage({
    success: true,
    data: {
      items: [
        { productCode: "P1", displayName: "Helium Balloon", itemNumber: "BL-1001", barcode: "200", retailPrice: "2.5", productImage: "", categoryId: "c1", categoryName: "气球", sortOrder: 3 },
        { displayName: "没有商品编码的行被丢弃" },
      ],
      page: 2,
      pageSize: 50,
      total: 120,
    },
  });
  assert.equal(page.items.length, 1);
  assert.equal(page.items[0].retailPrice, 2.5);
  assert.equal(page.items[0].productImage, null);
  assert.equal(page.items[0].sortOrder, 3);
  assert.deepEqual([page.page, page.pageSize, page.total], [2, 50, 120]);

  const candidates = normalizeCandidates({ success: true, data: { items: [{ productCode: "P2", displayName: "Pump", isSpecialProduct: true, categoryId: null, retailPrice: null }] } });
  assert.equal(candidates[0].isSpecialProduct, true);
  assert.equal(candidates[0].retailPrice, null);
  assert.equal(normalizeCandidates([{ productCode: "P3", isSpecialProduct: "true" }])[0].isSpecialProduct, true, "兼容直接返回数组");
});

test("assign / 同步响应归一化", () => {
  const assign = normalizeAssignResult({ success: true, data: { assignedCount: 3, markedCount: 2, skipped: [{ productCode: "P9", reason: "NO_STORE_PRICE" }, { reason: "丢弃" }] } });
  assert.deepEqual([assign.assignedCount, assign.markedCount, assign.skipped.length], [3, 2, 1]);

  const targets = normalizeSyncTargets(
    { success: true, data: { source: { storeCode: "1042", categoryCount: 7, productCount: 128 }, targets: [{ storeCode: "1015", storeName: "Sunnybank", categoryCount: 3, specialProductCount: 40 }, { storeName: "无编号被丢弃" }] } },
    "1042",
  );
  assert.deepEqual(targets.source, { storeCode: "1042", categoryCount: 7, productCount: 128 });
  assert.equal(targets.targets.length, 1);

  const preview = normalizeSyncPreview({
    success: true,
    data: { targets: [{ storeCode: "1015", storeName: "Sunnybank", createdCategories: 4, mergedCategories: 3, newlyMarkedProducts: 37, reassignedProducts: 52, unchangedProducts: 31, removedCategories: 1, unmarkedProducts: 2, error: "同步后分类数会超过 50", skipped: [{ productCode: "P1", displayName: "Pump", reason: "NO_STORE_PRICE" }] }] },
  });
  assert.equal(preview[0].createdCategories, 4);
  assert.equal(preview[0].error, "同步后分类数会超过 50", "预览阶段的单店 error 要保留");
  assert.equal(preview[0].skipped[0].displayName, "Pump");

  const results = normalizeSyncResults({ success: true, data: { results: [{ storeCode: "1015", storeName: "Sunnybank", ok: true, skipped: [{ productCode: "P1", reason: "x" }] }, { storeCode: "1021", ok: false, error: "boom" }] } });
  assert.deepEqual(results.map((item) => item.ok), [true, false]);
  assert.equal(results[0].skipped[0].displayName, "P1", "缺 displayName 时用商品编码");
  assert.equal(results[1].error, "boom");
  assert.equal(results[1].storeName, "1021");
  const outcome = summarizeSyncOutcome(results);
  assert.deepEqual([outcome.succeeded.length, outcome.failed.length, outcome.skippedProducts], [1, 1, 1]);
});

test("颜色与分类名校验", () => {
  assert.equal(normalizeColor(" #abcdef "), "#ABCDEF");
  assert.equal(normalizeColor("#abc"), null);
  assert.equal(normalizeColor(null), null);

  const existing = [cat("a", "气球 / 充气"), cat("b", "Cards")];
  assert.deepEqual(validateCategoryName("  新分类 ", existing), { ok: true, name: "新分类" });
  assert.deepEqual(validateCategoryName("   ", existing), { ok: false, issue: "empty" });
  assert.deepEqual(validateCategoryName("x".repeat(31), existing), { ok: false, issue: "tooLong" });
  assert.deepEqual(validateCategoryName("x".repeat(30), existing), { ok: true, name: "x".repeat(30) });
  assert.deepEqual(validateCategoryName("😀".repeat(30), existing), { ok: true, name: "😀".repeat(30) }, "按字符数而不是 UTF-16 单元数");
  assert.deepEqual(validateCategoryName("cards", existing), { ok: false, issue: "duplicate" }, "不区分大小写");
  assert.deepEqual(validateCategoryName("cards", existing, "b"), { ok: true, name: "cards" }, "编辑自己时排除自己");
});

test("moveItem / computeDropIndex / buildSlotMap：拖动后重排", () => {
  const ids = ["a", "b", "c", "d"];
  assert.deepEqual(moveItem(ids, 0, 2), ["b", "c", "a", "d"]);
  assert.deepEqual(moveItem(ids, 3, 0), ["d", "a", "b", "c"]);
  assert.equal(moveItem(ids, 1, 1), ids, "不动返回原数组引用");
  assert.equal(moveItem(ids, 9, 0), ids, "越界返回原数组引用");
  assert.deepEqual(moveItem(ids, 1, 99), ["a", "c", "d", "b"], "目标越界夹到末尾");

  // 行高 56：下拖 30px 不到一半仍在原位，下拖 40px 过半进下一槽；上拖同理；超出夹在两端。
  assert.equal(computeDropIndex(1, 0, 56, 4), 1);
  assert.equal(computeDropIndex(1, 27, 56, 4), 1);
  assert.equal(computeDropIndex(1, 30, 56, 4), 2);
  assert.equal(computeDropIndex(1, -30, 56, 4), 0);
  assert.equal(computeDropIndex(1, 500, 56, 4), 3);
  assert.equal(computeDropIndex(1, -500, 56, 4), 0);
  assert.equal(computeDropIndex(0, 10, 56, 0), 0);

  // 拖 a 到第 3 槽：其余行按原相对顺序补位。
  const slots = buildSlotMap(ids, "a", 2);
  assert.deepEqual([...slots.entries()], [["b", 0], ["c", 1], ["a", 2], ["d", 3]]);
  assert.deepEqual([...buildSlotMap(ids, null, 0).entries()], [["a", 0], ["b", 1], ["c", 2], ["d", 3]]);
});

test("applyLocalOrder：乐观顺序覆盖服务器顺序，服务器多出来的排末尾，已删除的忽略", () => {
  const server = [cat("a", "A"), cat("b", "B"), cat("c", "C")];
  assert.deepEqual(applyLocalOrder(server, null).map((item) => item.id), ["a", "b", "c"]);
  assert.deepEqual(applyLocalOrder(server, ["c", "a", "b"]).map((item) => item.id), ["c", "a", "b"]);
  assert.deepEqual(applyLocalOrder(server, ["c", "gone", "a"]).map((item) => item.id), ["c", "a", "b"]);
  assert.equal(sameOrder(["a", "b"], ["a", "b"]), true);
  assert.equal(sameOrder(["a", "b"], ["b", "a"]), false);
});

test("错误码提取、映射与冲突判定", () => {
  assert.equal(getErrorCode({ code: "special_category_name_duplicate" }), "SPECIAL_CATEGORY_NAME_DUPLICATE", "unwrapApiEnvelope 抛出的 error.code");
  assert.equal(getErrorCode({ response: { status: 409, data: { success: false, errorCode: "SPECIAL_CATEGORY_ORDER_CONFLICT" } } }), "SPECIAL_CATEGORY_ORDER_CONFLICT", "HTTP 错误体");
  assert.equal(getErrorCode(new Error("x")), null);
  assert.equal(errorCodeKey({ response: { data: { errorCode: "SPECIAL_CATEGORY_NAME_DUPLICATE" } } }), "nameDuplicate");
  assert.equal(errorCodeKey({ response: { data: { errorCode: "SPECIAL_CATEGORY_LIMIT_REACHED" } } }), "limitReached");
  assert.equal(errorCodeKey({ response: { status: 409, data: { errorCode: "SPECIAL_CATEGORY_ASSIGN_CONFLICT" } } }), "assignConflict");
  assert.equal(isConflictError({ response: { status: 409, data: { errorCode: "SPECIAL_CATEGORY_ASSIGN_CONFLICT" } } }), true, "同时首次归类冲突要触发重新加载");
  assert.equal(syncResultErrorKey("SYNC_STORE_FAILED"), "storeFailedRolledBack");
  assert.equal(syncResultErrorKey("sync_store_failed"), "storeFailedRolledBack");
  assert.equal(syncResultErrorKey("分类数超过 50"), "storeFailed", "其它可读文案原样显示");
  assert.equal(syncResultErrorKey(null), "unknownError");
  assert.equal(errorCodeKey({ response: { data: { errorCode: "STORE_OUT_OF_SCOPE" } } }), "forbidden");
  assert.equal(errorCodeKey({ response: { data: { errorCode: "VALIDATION_ERROR" } } }), null, "校验错误显示后端原因");

  assert.equal(isConflictError({ response: { status: 409, data: { errorCode: "SPECIAL_CATEGORY_ORDER_CONFLICT" } } }), true);
  assert.equal(isConflictError({ code: "SPECIAL_CATEGORY_ORDER_CONFLICT" }), true);
  assert.equal(isConflictError({ response: { status: 409 } }), true, "没有业务码的 409 当作冲突");
  // 同名 / 数量上限也是 HTTP 409，但不是「被别人改过」。
  assert.equal(isConflictError({ response: { status: 409, data: { errorCode: "SPECIAL_CATEGORY_NAME_DUPLICATE" } } }), false);
  assert.equal(isConflictError({ response: { status: 409, data: { errorCode: "SPECIAL_CATEGORY_LIMIT_REACHED" } } }), false);
  assert.equal(isConflictError({ response: { status: 500 } }), false);
  assert.equal(isConflictError(new Error("network")), false);
  assert.equal(isConflictError(null), false);
});

test("批量分块：超过 500 个编码按顺序切块", () => {
  assert.deepEqual(chunk([1, 2, 3, 4, 5], 2), [[1, 2], [3, 4], [5]]);
  assert.deepEqual(chunk([], 2), []);
  assert.equal(MAX_BATCH_PRODUCT_CODES, 500);
  assert.equal(MAX_CATEGORIES_PER_STORE, 50);
});

test("添加商品的三种情况与搜索门槛", () => {
  const base: CandidateProduct = { productCode: "P", displayName: "N", itemNumber: "", barcode: "", retailPrice: 1, productImage: null, isSpecialProduct: false, categoryId: null, categoryName: null };
  assert.equal(classifyCandidate(base, "c1"), "mark", "还不是特殊商品 → 标记并放进来");
  assert.equal(classifyCandidate({ ...base, isSpecialProduct: true, categoryId: "c2", categoryName: "别的" }, "c1"), "move", "在别的分类 → 移过来");
  assert.equal(classifyCandidate({ ...base, isSpecialProduct: true, categoryId: "c1" }, "c1"), "already", "已在本分类 → 置灰");
  assert.equal(classifyCandidate({ ...base, isSpecialProduct: true, categoryId: null }, "c1"), "fromUncategorized");
  // 取消标记后归属行还在、但不再是特殊商品：按「还不是特殊商品」处理，重新标记走 mark。
  assert.equal(classifyCandidate({ ...base, isSpecialProduct: false, categoryId: "c1" }, "c1"), "mark");
  assert.equal(canSearchCandidates("b"), false);
  assert.equal(canSearchCandidates(" ba "), true);
  assert.equal(canSearchCandidates("气"), false);
  assert.equal(canSearchCandidates("气球"), true);
});

test("同步预览汇总、破坏性变更与改价提示", () => {
  const counts = (overrides: Partial<ReturnType<typeof sumSyncCounts>> & { skipped?: number } = {}) => ({
    createdCategories: 0,
    mergedCategories: 0,
    removedCategories: overrides.removedCategories ?? 0,
    newlyMarkedProducts: overrides.newlyMarkedProducts ?? 0,
    reassignedProducts: 0,
    unchangedProducts: 0,
    unmarkedProducts: overrides.unmarkedProducts ?? 0,
    skipped: Array.from({ length: overrides.skipped ?? 0 }, (_, index) => ({ productCode: `P${index}`, displayName: "", reason: "NO_STORE_PRICE" })),
  });
  const list = [counts({ newlyMarkedProducts: 37, skipped: 8 }), counts({ newlyMarkedProducts: 96, skipped: 5, removedCategories: 2 })];
  const totals = sumSyncCounts(list);
  assert.deepEqual([totals.storeCount, totals.newlyMarkedProducts, totals.skippedProducts, totals.removedCategories], [2, 133, 13, 2]);
  assert.equal(hasDestructiveChanges(list), true);
  assert.equal(hasDestructiveChanges([counts({ newlyMarkedProducts: 5 })]), false);
  assert.equal(hasDestructiveChanges([counts({ unmarkedProducts: 1 })]), true, "只有取消标记也算破坏性");
  assert.equal(willMarkNewSpecialProducts("categories-and-products", list), true);
  assert.equal(willMarkNewSpecialProducts("categories", list), false, "只同步分类不会新标记，不显示改价提示");
  assert.equal(willMarkNewSpecialProducts("categories-and-products", [counts()]), false);
});

test("同步请求去重、勾选裁剪、跳过原因码映射", () => {
  assert.deepEqual(buildSyncRequest({ sourceStoreCode: "1", targetStoreCodes: ["2", "3", "2"], scope: "categories", mode: "merge" }).targetStoreCodes, ["2", "3"]);
  assert.deepEqual(pruneSelection(["2", "9"], ["2", "3"]), ["2"]);
  assert.equal(skippedReasonKey("NO_STORE_PRICE"), "noStorePrice");
  assert.equal(skippedReasonKey("no-store-price"), "noStorePrice");
  assert.equal(skippedReasonKey("SOMETHING_NEW"), null);
});

test("管理的分店：不限分店 = 全部，受限 = 主分店；默认分店取偏好或第一家", () => {
  const stores = [
    { storeCode: "1021", storeName: "Inala" },
    { storeCode: "1015", storeName: "Sunnybank" },
    { storeCode: "1033", storeName: "Logan" },
  ];
  assert.deepEqual(resolveManagedStores(stores, null).map((item) => item.storeCode), ["1021", "1033", "1015"], "按名称排序");
  assert.deepEqual(resolveManagedStores(stores, ["1015", "9999"]).map((item) => item.storeCode), ["1015"]);
  assert.deepEqual(resolveManagedStores(stores, []), []);
  assert.equal(pickInitialStore(stores, "1033"), "1033");
  assert.equal(pickInitialStore(stores, "9999"), "1021");
  assert.equal(pickInitialStore([], "1033"), null);
});
