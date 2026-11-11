import assert from "node:assert/strict";
import test from "node:test";
import { createSpecialCategoriesApi, SPECIAL_CATEGORIES_BASE_URL, type SpecialCategoriesTransport } from "./api-contract";

function harness(reply: unknown) {
  const calls: { method: string; url: string; body?: unknown; params?: unknown }[] = [];
  const transport: SpecialCategoriesTransport = {
    async get(url, config) {
      calls.push({ method: "GET", url, params: config?.params });
      return { data: reply };
    },
    async post(url, body, config) {
      calls.push({ method: "POST", url, body, params: config?.params });
      return { data: reply };
    },
    async put(url, body, config) {
      calls.push({ method: "PUT", url, body, params: config?.params });
      return { data: reply };
    },
    async delete(url, config) {
      calls.push({ method: "DELETE", url, params: config?.params });
      return { data: reply };
    },
  };
  return { api: createSpecialCategoriesApi(transport), calls };
}

const base = SPECIAL_CATEGORIES_BASE_URL;

test("基址与契约一致，且不重复 /api 前缀", () => {
  assert.equal(base, "/react/v1/store-special-product-categories");
});

test("分类 CRUD 与排序的路径 / 方法 / 请求体", async () => {
  const overview = harness({ success: true, data: { storeCode: "1042", totalCount: 1, uncategorizedCount: 0, categories: [{ id: "a", name: "A", sortOrder: 1, itemCount: 1 }] } });
  assert.equal((await overview.api.getOverview("1042")).categories[0].id, "a");
  assert.deepEqual(overview.calls[0], { method: "GET", url: base, params: { storeCode: "1042" } });

  const created = harness({ success: true, data: { id: "n", name: "新", color: "#1677FF", sortOrder: 3, itemCount: 0 } });
  assert.equal((await created.api.createCategory({ storeCode: "1042", name: "新", color: "#1677FF" })).id, "n");
  assert.deepEqual(created.calls[0], { method: "POST", url: base, body: { storeCode: "1042", name: "新", color: "#1677FF" }, params: undefined });

  const updated = harness({ success: true, data: { id: "a/b", name: "改", color: null, sortOrder: 1, itemCount: 2 } });
  await updated.api.updateCategory("a/b", { storeCode: "1042", name: "改", color: null });
  assert.equal(updated.calls[0].url, `${base}/a%2Fb`, "分类 id 要做 URL 编码");
  assert.equal(updated.calls[0].method, "PUT");

  const removed = harness({ success: true, data: { removedItemCount: 5 } });
  assert.deepEqual(await removed.api.deleteCategory("a", "1042"), { removedItemCount: 5 });
  assert.deepEqual(removed.calls[0], { method: "DELETE", url: `${base}/a`, params: { storeCode: "1042" } });

  const order = harness({ success: true, data: { categories: [{ id: "b", name: "B", sortOrder: 1, itemCount: 0 }, { id: "a", name: "A", sortOrder: 2, itemCount: 0 }] } });
  const reordered = await order.api.reorderCategories("1042", ["b", "a"]);
  assert.deepEqual(reordered.map((item) => item.id), ["b", "a"]);
  assert.deepEqual(order.calls[0], { method: "PUT", url: `${base}/order`, body: { storeCode: "1042", categoryIds: ["b", "a"] }, params: undefined });
});

test("业务失败（200 + success:false）抛出带原因与错误码的错误", async () => {
  const { api } = harness({ success: false, message: "分类名称已存在", errorCode: "SPECIAL_CATEGORY_NAME_DUPLICATE" });
  await assert.rejects(
    () => api.createCategory({ storeCode: "1042", name: "重复", color: null }),
    (error: Error & { code?: string; apiBusinessError?: boolean }) => error.message === "分类名称已存在" && error.code === "SPECIAL_CATEGORY_NAME_DUPLICATE" && error.apiBusinessError === true,
  );
  const invalid = harness({ success: true, data: {} });
  await assert.rejects(() => invalid.api.createCategory({ storeCode: "1042", name: "x", color: null }), /Category response invalid/);
});

test("商品列表 / 候选 / assign / unmark / 分类内排序", async () => {
  const products = harness({ success: true, data: { items: [], page: 1, pageSize: 100, total: 0 } });
  await products.api.getProducts({ storeCode: "1042", categoryId: "uncategorized", keyword: " bal ", page: 1, pageSize: 100 });
  assert.deepEqual(products.calls[0], { method: "GET", url: `${base}/products`, params: { storeCode: "1042", categoryId: "uncategorized", keyword: "bal", page: 1, pageSize: 100 } });
  await products.api.getProducts({ storeCode: "1042", page: 2, pageSize: 50 });
  assert.deepEqual(products.calls[1].params, { storeCode: "1042", categoryId: undefined, keyword: undefined, page: 2, pageSize: 50 }, "省略 categoryId = 全部");

  const candidates = harness({ success: true, data: { items: [] } });
  await candidates.api.getCandidates({ storeCode: "1042", keyword: " balloon " });
  assert.deepEqual(candidates.calls[0].params, { storeCode: "1042", keyword: "balloon", limit: 30 });

  const assign = harness({ success: true, data: { assignedCount: 2, markedCount: 1, skipped: [] } });
  const result = await assign.api.assignProducts({ storeCode: "1042", productCodes: ["P1", "P2"], categoryId: null, markAsSpecial: false });
  assert.equal(result.assignedCount, 2);
  assert.deepEqual(assign.calls[0], { method: "POST", url: `${base}/assign`, body: { storeCode: "1042", productCodes: ["P1", "P2"], categoryId: null, markAsSpecial: false }, params: undefined });

  const unmark = harness({ success: true, data: { unmarkedCount: 3 } });
  assert.deepEqual(await unmark.api.unmarkProducts("1042", ["P1"]), { unmarkedCount: 3 });
  assert.equal(unmark.calls[0].url, `${base}/unmark`);

  const productOrder = harness({ success: true, data: { ok: true } });
  await productOrder.api.reorderCategoryProducts("c1", "1042", ["P2", "P1"]);
  assert.deepEqual(productOrder.calls[0], { method: "PUT", url: `${base}/c1/products/order`, body: { storeCode: "1042", productCodes: ["P2", "P1"] }, params: undefined });
});

test("assign / unmark 超过 500 个编码时分批提交并汇总", async () => {
  const codes = Array.from({ length: 1201 }, (_, index) => `P${index}`);
  const assign = harness({ success: true, data: { assignedCount: 500, markedCount: 1, skipped: [{ productCode: "P1", reason: "NO_STORE_PRICE" }] } });
  const result = await assign.api.assignProducts({ storeCode: "1042", productCodes: codes, categoryId: "c1", markAsSpecial: true });
  assert.equal(assign.calls.length, 3);
  assert.deepEqual(assign.calls.map((call) => (call.body as { productCodes: string[] }).productCodes.length), [500, 500, 201]);
  assert.equal((assign.calls[1].body as { productCodes: string[] }).productCodes[0], "P500", "保持传入顺序");
  assert.deepEqual([result.assignedCount, result.markedCount, result.skipped.length], [1500, 3, 3]);

  const unmark = harness({ success: true, data: { unmarkedCount: 500 } });
  assert.equal((await unmark.api.unmarkProducts("1042", codes)).unmarkedCount, 1500);
  assert.equal(unmark.calls.length, 3);
});

test("同步：targets / preview / execute 的路径与请求体", async () => {
  const targets = harness({ success: true, data: { source: { storeCode: "1042", categoryCount: 1, productCount: 2 }, targets: [] } });
  await targets.api.getSyncTargets("1042");
  assert.deepEqual(targets.calls[0], { method: "GET", url: `${base}/sync/targets`, params: { sourceStoreCode: "1042" } });

  const request = { sourceStoreCode: "1042", targetStoreCodes: ["1015"], scope: "categories-and-products" as const, mode: "replace" as const };
  const preview = harness({ success: true, data: { targets: [{ storeCode: "1015", removedCategories: 2 }] } });
  assert.equal((await preview.api.previewSync(request))[0].removedCategories, 2);
  assert.deepEqual(preview.calls[0], { method: "POST", url: `${base}/sync/preview`, body: request, params: undefined });

  const execute = harness({ success: true, data: { results: [{ storeCode: "1015", ok: true }] } });
  const results = await execute.api.executeSync({ ...request, confirmReplace: true });
  assert.equal(results[0].ok, true);
  assert.deepEqual(execute.calls[0].body, { ...request, confirmReplace: true });
  assert.equal(execute.calls[0].url, `${base}/sync/execute`);
});
