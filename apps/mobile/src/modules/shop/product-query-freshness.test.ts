import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { QueryClient, QueryObserver } from "@tanstack/react-query";
import { isSameKeywordResubmit } from "./home-filters";
import {
  SHOP_BROWSE_PRODUCTS_STALE_TIME_MS,
  buildShopProductsQueryKey,
  resolveShopProductsStaleTime,
} from "./product-query-key";
import type { StoreOrderProductQuery } from "./types";

interface ProductListStub {
  items: { itemNumber: string }[];
  total: number;
}

const browseQuery: StoreOrderProductQuery = {
  storeCode: "S001",
  pageNumber: 1,
  pageSize: 18,
  sortBy: "Default",
};
const keywordQuery: StoreOrderProductQuery = { ...browseQuery, itemNumber: "ME542-6" };
const emptyResult: ProductListStub = { items: [], total: 0 };
const listedResult: ProductListStub = { items: [{ itemNumber: "ME542-6" }], total: 1 };

// ---- 新鲜期规则 ----
assert.equal(resolveShopProductsStaleTime(keywordQuery), 0, "货号/条码关键字检索不得设置客户端新鲜期");
assert.equal(
  resolveShopProductsStaleTime({ ...browseQuery, productName: "Mug" }),
  0,
  "商品名关键字检索同样不得设置客户端新鲜期",
);
assert.equal(
  resolveShopProductsStaleTime({ ...browseQuery, itemNumber: "   " }),
  SHOP_BROWSE_PRODUCTS_STALE_TIME_MS,
  "空白关键字等同浏览，沿用浏览缓存",
);
assert.equal(resolveShopProductsStaleTime(browseQuery), 2 * 60 * 1000, "首页浏览缓存与后端 2 分钟对齐");
assert.equal(
  resolveShopProductsStaleTime({ ...browseQuery, categoryGUID: "C-1" }),
  SHOP_BROWSE_PRODUCTS_STALE_TIME_MS,
  "分类浏览缓存与后端 2 分钟对齐",
);

// ---- 同词重搜识别 ----
assert.equal(isSameKeywordResubmit("ME542-6", " ME542-6 "), true, "再次提交当前关键词（含首尾空白）应识别为同词重搜");
assert.equal(isSameKeywordResubmit("ME542-6", "ME542-7"), false, "换词会换查询键，无需额外重取");
assert.equal(isSameKeywordResubmit("", "  "), false, "空白提交是清空搜索，不是重搜");
assert.equal(isSameKeywordResubmit("", "ME542-6"), false, "首次进入搜索会换查询键，无需额外重取");

// ---- 用与 useQuery 相同的 QueryObserver 验证缓存行为 ----
async function waitForIdle(observer: QueryObserver<ProductListStub>) {
  for (let attempt = 0; attempt < 50; attempt += 1) {
    if (!observer.getCurrentResult().isFetching) {
      return;
    }
    await new Promise((resolveTimer) => setTimeout(resolveTimer, 0));
  }
  throw new Error("商品查询未在预期时间内结束");
}

function createClient() {
  // gcTime 设为 Infinity：不注册垃圾回收定时器，测试结束后进程能立即退出。
  return new QueryClient({ defaultOptions: { queries: { gcTime: Infinity, retry: false } } });
}

/**
 * 场景：门店在上架前搜过某关键字（缓存了空结果），随后回到浏览，再搜同一关键字。
 * 返回查询函数被调用的次数与最终展示的结果。
 */
async function searchAgainAfterEmptyResult(staleTimeFor: (query: StoreOrderProductQuery) => number) {
  const client = createClient();
  client.setQueryData(buildShopProductsQueryKey(keywordQuery, false), emptyResult);
  client.setQueryData(buildShopProductsQueryKey(browseQuery, false), listedResult);

  let fetchCount = 0;
  const optionsFor = (query: StoreOrderProductQuery) => ({
    queryKey: buildShopProductsQueryKey(query, false),
    staleTime: staleTimeFor(query),
    queryFn: async () => {
      fetchCount += 1;
      return listedResult;
    },
  });

  const observer = new QueryObserver<ProductListStub>(client, optionsFor(browseQuery));
  const unsubscribe = observer.subscribe(() => undefined);
  await waitForIdle(observer);
  const browseFetchCount = fetchCount;

  observer.setOptions(optionsFor(keywordQuery));
  await waitForIdle(observer);
  const result = observer.getCurrentResult().data;

  unsubscribe();
  client.clear();
  return { browseFetchCount, keywordFetchCount: fetchCount - browseFetchCount, result };
}

async function run() {
  const current = await searchAgainAfterEmptyResult(resolveShopProductsStaleTime);
  assert.equal(current.browseFetchCount, 0, "2 分钟内回到首页浏览应直接复用缓存");
  assert.equal(current.keywordFetchCount, 1, "再次搜索同一关键字必须重新请求");
  assert.deepEqual(current.result, listedResult, "关键字查询不得复用上架前缓存的空结果");

  // 对照：改动前统一 5 分钟新鲜期时，同一场景会直接展示旧的空结果（即 ME542-6 “上架了却搜不到”）。
  const legacy = await searchAgainAfterEmptyResult(() => 5 * 60 * 1000);
  assert.equal(legacy.keywordFetchCount, 0, "对照组：旧配置下关键字查询不会重新请求");
  assert.deepEqual(legacy.result, emptyResult, "对照组：旧配置下会展示缓存的空结果");

  console.log("product-query-freshness.test.ts: ok");
}

// ---- 源码契约：hook 与首页必须接入上述规则 ----
const currentDirectory = dirname(fileURLToPath(import.meta.url));
const useProductsSource = readFileSync(resolve(currentDirectory, "use-products.ts"), "utf8");
assert.match(
  useProductsSource,
  /staleTime: resolveShopProductsStaleTime\(query\),/,
  "useProducts 必须按查询类型决定新鲜期，不能回退为固定值",
);

const homeSource = readFileSync(resolve(currentDirectory, "../../../app/(shell)/home.tsx"), "utf8");
assert.match(
  homeSource,
  /const submitSearchKeyword = useCallback\([\s\S]*?if \(isSameKeywordResubmit\(keyword, input\)\) \{\s*void refetchProducts\(\);/,
  "同词重搜必须显式重新请求商品列表",
);
assert.match(
  homeSource,
  /const handleApplySearch = useCallback\([\s\S]*?submitSearchKeyword\(searchInput\);/,
  "搜索框提交必须经过同词重搜判断",
);
assert.match(
  homeSource,
  /const handleSupplyOrder = useCallback\([\s\S]*?submitSearchKeyword\(code\);/,
  "恢复订货后按货号搜索必须经过同词重搜判断",
);

void run().catch((error) => {
  console.error(error);
  process.exitCode = 1;
});
