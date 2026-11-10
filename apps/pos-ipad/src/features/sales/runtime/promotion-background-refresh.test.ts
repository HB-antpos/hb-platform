import assert from "node:assert/strict";
import test from "node:test";

import {
  PromotionBackgroundRefresher,
  type PromotionBackgroundRefreshPorts,
  type PromotionRefreshLogEvent,
  type RefreshablePromotion,
  type StoredPromotionRules,
} from "./promotion-background-refresh";
import type { PromotionSnapshotLoadResult } from "./promotion-snapshot-loader";

function promo(id: string, version = "v1"): RefreshablePromotion {
  return {
    promotionId: id,
    definitionJson: JSON.stringify({ promotionId: id, version }),
    validFromIso: null,
    validUntilIso: null,
    priority: 0,
  };
}

function stored(
  snapshotId: string,
  promotions: readonly RefreshablePromotion[],
): StoredPromotionRules {
  return {
    snapshotId,
    promotions: promotions.map(({ promotionId, definitionJson }) => ({
      promotionId,
      definitionJson,
    })),
  };
}

const LOADED: PromotionSnapshotLoadResult = {
  status: "loaded",
  snapshotId: "snap-1",
  ruleCount: 1,
};

/** 可控的假端口：每个端口都记录调用，行为由 overrides 注入。 */
function createHarness(
  overrides: Partial<PromotionBackgroundRefreshPorts> & {
    remote?: readonly RefreshablePromotion[];
    /** 逐次返回 loadStored 的结果；用完后重复最后一项。 */
    storedSequence?: readonly (StoredPromotionRules | null)[];
    applyResults?: readonly PromotionSnapshotLoadResult[];
    fetchBudgetMs?: number;
  } = {},
) {
  const calls = {
    fetch: 0,
    replace: [] as { snapshotId: string; ids: string[] }[],
    apply: 0,
    logs: [] as PromotionRefreshLogEvent[],
  };
  const storedSequence = overrides.storedSequence ?? [stored("snap-1", [promo("P1")])];
  let storedCall = 0;
  const applyResults = overrides.applyResults ?? [LOADED];
  let applyCall = 0;

  const ports: PromotionBackgroundRefreshPorts = {
    currentStoreCode: () => "S1",
    isCatalogRefreshRunning: () => false,
    fetchRemote: async () => {
      calls.fetch += 1;
      return overrides.remote ?? [promo("P1")];
    },
    loadStored: async () => {
      const index = Math.min(storedCall, storedSequence.length - 1);
      storedCall += 1;
      return storedSequence[index] ?? null;
    },
    replaceStored: async (snapshotId, promotions) => {
      calls.replace.push({
        snapshotId,
        ids: promotions.map((item) => item.promotionId),
      });
    },
    applyToCart: async () => {
      calls.apply += 1;
      const index = Math.min(applyCall, applyResults.length - 1);
      applyCall += 1;
      return applyResults[index]!;
    },
    tryRunExclusive: async (operation) => ({
      ran: true as const,
      value: await operation(),
    }),
    nowIso: () => "2026-11-11T00:00:00.000Z",
    log: (event) => calls.logs.push(event),
    ...overrides,
  };
  const refresher = new PromotionBackgroundRefresher(ports, {
    ...(overrides.fetchBudgetMs === undefined
      ? {}
      : { fetchBudgetMs: overrides.fetchBudgetMs }),
  });
  return { refresher, calls };
}

test("未登录（没有可信门店）：不发请求", async () => {
  const h = createHarness({ currentStoreCode: () => null });
  assert.deepEqual(await h.refresher.request("timer"), {
    status: "skipped",
    reason: "no-session",
  });
  assert.equal(h.calls.fetch, 0);
});

test("目录下载 / 重置在途：让路，不发请求也不落库", async () => {
  const h = createHarness({ isCatalogRefreshRunning: () => true });
  assert.deepEqual(await h.refresher.request("timer"), {
    status: "skipped",
    reason: "catalog-busy",
  });
  assert.equal(h.calls.fetch, 0);
  assert.equal(h.calls.replace.length, 0);
});

test("远端规则与本机一致：什么都不做，购物车也不会被重算", async () => {
  const h = createHarness({ remote: [promo("P1")] });
  assert.deepEqual(await h.refresher.request("timer"), { status: "unchanged" });
  assert.equal(h.calls.replace.length, 0);
  assert.equal(h.calls.apply, 0);
});

test("远端有新促销：落在当前 active 快照上并装入购物车", async () => {
  const h = createHarness({
    remote: [promo("P1"), promo("P2")],
    storedSequence: [stored("snap-1", [promo("P1")])],
    applyResults: [{ status: "loaded", snapshotId: "snap-1", ruleCount: 2 }],
  });
  assert.deepEqual(await h.refresher.request("timer"), {
    status: "applied",
    ruleCount: 2,
  });
  assert.deepEqual(h.calls.replace, [{ snapshotId: "snap-1", ids: ["P1", "P2"] }]);
  assert.equal(h.calls.apply, 1);
});

test("远端规则内容变化（同 id、定义改了）也算变化", async () => {
  const h = createHarness({
    remote: [promo("P1", "v2")],
    storedSequence: [stored("snap-1", [promo("P1", "v1")])],
  });
  assert.equal((await h.refresher.request("login")).status, "applied");
  assert.equal(h.calls.replace.length, 1);
});

test("总部停用 / 删除了全部促销（远端为空）：落库为空并装入，折扣随之消失", async () => {
  const h = createHarness({
    remote: [],
    storedSequence: [stored("snap-1", [promo("P1")])],
    applyResults: [{ status: "loaded", snapshotId: "snap-1", ruleCount: 0 }],
  });
  assert.deepEqual(await h.refresher.request("timer"), {
    status: "applied",
    ruleCount: 0,
  });
  assert.deepEqual(h.calls.replace, [{ snapshotId: "snap-1", ids: [] }]);
});

test("本机还没有 active 目录快照：没有可挂靠的地方，不落库", async () => {
  const h = createHarness({ storedSequence: [null] });
  assert.deepEqual(await h.refresher.request("timer"), {
    status: "skipped",
    reason: "no-active-snapshot",
  });
  assert.equal(h.calls.replace.length, 0);
});

test("拉取失败：不落库不装入，记一条失败日志，下一轮恢复正常", async () => {
  let fail = true;
  const h = createHarness({
    remote: [promo("P1"), promo("P2")],
    fetchRemote: async () => {
      if (fail) throw new Error("offline");
      return [promo("P1"), promo("P2")];
    },
  });
  assert.deepEqual(await h.refresher.request("timer"), { status: "failed" });
  assert.equal(h.calls.replace.length, 0);
  assert.equal(h.calls.apply, 0);
  assert.equal(h.calls.logs[0]?.kind, "failed");

  fail = false;
  assert.equal((await h.refresher.request("timer")).status, "applied");
});

test("单飞：上一轮还没结束时其余触发直接跳过，结束后可再次触发", async () => {
  let release: (() => void) | undefined;
  const gate = new Promise<void>((resolve) => {
    release = resolve;
  });
  const h = createHarness({
    fetchRemote: async () => {
      await gate;
      return [promo("P1")];
    },
  });
  const first = h.refresher.request("timer");
  assert.deepEqual(await h.refresher.request("foreground"), {
    status: "skipped",
    reason: "in-flight",
  });
  release?.();
  assert.equal((await first).status, "unchanged");
  assert.equal((await h.refresher.request("timer")).status, "unchanged");
});

test("购物车忙（付款中）：规则已落库但延后装入，下一轮即使远端没变也会重试装入", async () => {
  const h = createHarness({
    remote: [promo("P1"), promo("P2")],
    // 第一轮：本机只有 P1；第二轮起本机已是 P1+P2（上一轮已落库）。
    storedSequence: [
      stored("snap-1", [promo("P1")]),
      stored("snap-1", [promo("P1")]),
      stored("snap-1", [promo("P1"), promo("P2")]),
    ],
    applyResults: [
      { status: "fallback" },
      { status: "loaded", snapshotId: "snap-1", ruleCount: 2 },
    ],
  });

  assert.deepEqual(await h.refresher.request("timer"), { status: "deferred" });
  assert.equal(h.calls.replace.length, 1);
  assert.equal(h.calls.logs.at(-1)?.kind, "deferred");

  // 第二轮：远端与本机一致，但还有待装入的规则 → 重新装入，不再重复落库。
  assert.deepEqual(await h.refresher.request("timer"), {
    status: "applied",
    ruleCount: 2,
  });
  assert.equal(h.calls.replace.length, 1);
  assert.equal(h.calls.apply, 2);

  // 第三轮：都装好了 → 什么都不做。
  assert.deepEqual(await h.refresher.request("timer"), { status: "unchanged" });
  assert.equal(h.calls.apply, 2);
});

test("目录刷新抢先拿到互斥门闩：本轮让路，不落库", async () => {
  const h = createHarness({
    remote: [promo("P1"), promo("P2")],
    tryRunExclusive: async () => ({ ran: false as const }),
  });
  assert.deepEqual(await h.refresher.request("timer"), {
    status: "skipped",
    reason: "catalog-busy",
  });
  assert.equal(h.calls.replace.length, 0);
});

test("进入独占区后 active 快照已被目录下载换新：写入落在新快照上，不写旧快照", async () => {
  const h = createHarness({
    remote: [promo("P1"), promo("P2")],
    storedSequence: [
      stored("snap-old", [promo("P1")]),
      stored("snap-new", [promo("P1")]),
    ],
  });
  assert.equal((await h.refresher.request("timer")).status, "applied");
  assert.deepEqual(h.calls.replace, [
    { snapshotId: "snap-new", ids: ["P1", "P2"] },
  ]);
});

test("拉取超过预算：中止请求并记为失败，不会一直挂着", async () => {
  const h = createHarness({
    fetchBudgetMs: 20,
    fetchRemote: ({ signal }) =>
      new Promise((_resolve, reject) => {
        signal.addEventListener("abort", () => reject(new Error("aborted")));
      }),
  });
  assert.deepEqual(await h.refresher.request("timer"), { status: "failed" });
  assert.equal(h.calls.replace.length, 0);
});

test("日志端口抛错不影响刷新结果", async () => {
  const h = createHarness({
    remote: [promo("P1"), promo("P2")],
    log: () => {
      throw new Error("log sink down");
    },
  });
  assert.equal((await h.refresher.request("timer")).status, "applied");
});

test("同一条规则只是商品明细顺序不同：视为没变化，不落库也不重算购物车", async () => {
  const definition = (products: readonly string[]) =>
    JSON.stringify({
      promotionId: "P1",
      name: "2 for 1.50",
      applyQuantity: 2,
      products: products.map((productCode) => ({ productCode, unitWeight: 1 })),
    });
  const remote: RefreshablePromotion = {
    promotionId: "P1",
    definitionJson: definition(["B", "A"]),
    validFromIso: null,
    validUntilIso: null,
    priority: 0,
  };
  const h = createHarness({
    remote: [remote],
    storedSequence: [
      {
        snapshotId: "snap-1",
        promotions: [{ promotionId: "P1", definitionJson: definition(["A", "B"]) }],
      },
    ],
  });
  assert.deepEqual(await h.refresher.request("timer"), { status: "unchanged" });
  assert.equal(h.calls.replace.length, 0);
  assert.equal(h.calls.apply, 0);
});

test("定义 JSON 损坏时退回原文比较：有差异就刷新，不会因解析失败而漏掉变化", async () => {
  const h = createHarness({
    remote: [promo("P1", "v2")],
    storedSequence: [
      { snapshotId: "snap-1", promotions: [{ promotionId: "P1", definitionJson: "{not json" }] },
    ],
  });
  assert.equal((await h.refresher.request("timer")).status, "applied");
});

test("日志防刷屏：连续失败只记第一条，恢复正常后再失败会重新记；规则变化每次都记", async () => {
  let fail = true;
  let remote: readonly RefreshablePromotion[] = [promo("P1")];
  const h = createHarness({
    fetchRemote: async () => {
      if (fail) throw new Error("offline");
      return remote;
    },
    storedSequence: [stored("snap-1", [promo("P1")])],
  });

  await h.refresher.request("timer");
  await h.refresher.request("timer");
  await h.refresher.request("timer");
  assert.deepEqual(h.calls.logs.map((event) => event.kind), ["failed"]);

  // 恢复且规则有变化：记 applied，并重新武装失败日志。
  fail = false;
  remote = [promo("P1"), promo("P2")];
  assert.equal((await h.refresher.request("timer")).status, "applied");
  fail = true;
  await h.refresher.request("timer");
  await h.refresher.request("timer");
  assert.deepEqual(h.calls.logs.map((event) => event.kind), [
    "failed",
    "applied",
    "failed",
  ]);
});

test("日志防刷屏：购物车长时间忙（延后装入）只在进入该状态时记一条", async () => {
  const h = createHarness({
    remote: [promo("P1"), promo("P2")],
    storedSequence: [
      stored("snap-1", [promo("P1")]),
      stored("snap-1", [promo("P1"), promo("P2")]),
    ],
    applyResults: [{ status: "fallback" }],
  });
  assert.equal((await h.refresher.request("timer")).status, "deferred");
  assert.equal((await h.refresher.request("timer")).status, "deferred");
  assert.equal((await h.refresher.request("timer")).status, "deferred");
  assert.deepEqual(h.calls.logs.map((event) => event.kind), ["deferred"]);
});
