import assert from "node:assert/strict";
import {
  createActivePromotionRequestCoordinator,
  type ActivePromotionRequestContext,
} from "./active-promotion-request";
import type { PromotionListItem } from "./types";

function promotion(id: string): PromotionListItem {
  return {
    id,
    name: id,
    effectiveStart: "",
    effectiveEnd: "",
    isEnabled: true,
    isExclusive: false,
    priority: 0,
    applyQuantity: 2,
    fixedPrice: 10,
    productsCount: 0,
    storesCount: 0,
    products: [],
    stores: [],
    scopeType: null,
    canEditInStoreScope: false,
    canCopyToStore: false,
  };
}

function deferred<T>() {
  let resolve!: (value: T) => void;
  let reject!: (reason?: unknown) => void;
  const promise = new Promise<T>((resolvePromise, rejectPromise) => {
    resolve = resolvePromise;
    reject = rejectPromise;
  });
  return { promise, resolve, reject };
}

async function run() {
  let currentItems: PromotionListItem[] = [];
  const currentIds = () => currentItems.map((item) => item.id);
  const failures: Array<{ error: unknown; context: ActivePromotionRequestContext }> = [];
  const pending = new Map<string, ReturnType<typeof deferred<PromotionListItem[]>>>();
  const coordinator = createActivePromotionRequestCoordinator({
    fetchPromotions: (productCode) => {
      const request = deferred<PromotionListItem[]>();
      pending.set(productCode, request);
      return request.promise;
    },
    applyPromotions: (items) => {
      currentItems = items;
    },
    onFailure: (error, context) => {
      failures.push({ error, context });
    },
  });

  const firstLoad = coordinator.load("P01", "S01");
  const secondLoad = coordinator.load("P02", "S01");
  pending.get("P02")?.resolve([promotion("promo-p02")]);
  assert.deepEqual((await secondLoad)?.map((item) => item.id), ["promo-p02"], "最新请求返回自己的促销，扫码提示音据此判定");
  pending.get("P01")?.resolve([promotion("promo-p01")]);
  assert.equal(await firstLoad, null, "被取代的旧请求返回 null，不能让扫码提示音误报促销");
  assert.deepEqual(currentIds(), ["promo-p02"], "迟到的旧成功响应不得覆盖最新商品");

  const invalidatedLoad = coordinator.load("P03", "S01");
  coordinator.invalidate();
  pending.get("P03")?.resolve([promotion("promo-p03")]);
  assert.equal(await invalidatedLoad, null, "失效后的请求返回 null");
  assert.deepEqual(currentIds(), [], "清空或切店后旧成功响应不得恢复活动");

  const oldFailure = coordinator.load("P04", "S01");
  const latestSuccess = coordinator.load("P05", "S01");
  pending.get("P05")?.resolve([promotion("promo-p05")]);
  await latestSuccess;
  pending.get("P04")?.reject(new Error("旧请求失败"));
  assert.equal(await oldFailure, null, "失败的请求返回 null");
  assert.deepEqual(currentIds(), ["promo-p05"], "旧失败不得清除最新成功结果");

  const latestFailure = coordinator.load("P06", "S02");
  pending.get("P06")?.reject(new Error("最新请求失败"));
  assert.equal(await latestFailure, null, "最新请求失败也返回 null");
  assert.deepEqual(currentIds(), [], "最新请求失败时应隐藏活动");
  assert.equal(failures.length, 1, "仅报告仍然有效的最新请求失败");
  assert.deepEqual(failures[0]?.context, { productCode: "P06", storeCode: "S02" });
}

void run();
