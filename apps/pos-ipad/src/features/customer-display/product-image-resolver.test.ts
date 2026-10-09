import assert from "node:assert/strict";
import test from "node:test";

import {
  CustomerDisplayProductImageResolver,
  createCatalogProductImageUrlResolver,
} from "./product-image-resolver";

const ROOT = "file:///cache/customer-display-product-images/";

function deferred<T>() {
  let resolve!: (value: T) => void;
  let reject!: (reason?: unknown) => void;
  const promise = new Promise<T>((res, rej) => {
    resolve = res;
    reject = rej;
  });
  return { promise, resolve, reject };
}

async function settle(): Promise<void> {
  for (let index = 0; index < 8; index += 1) await Promise.resolve();
  await new Promise((resolve) => setImmediate(resolve));
}

class FakeCache {
  public readonly fetches: string[] = [];
  public readonly gates = new Map<string, ReturnType<typeof deferred<string>>>();
  public readonly evicted = new Set<string>();
  public failUrls = new Set<string>();

  public touch(uri: string): boolean {
    return !this.evicted.has(uri);
  }

  public async fetch(url: string): Promise<string> {
    this.fetches.push(url);
    if (this.failUrls.has(url)) throw new Error("download failed");
    const gate = this.gates.get(url);
    if (gate) return gate.promise;
    return `${ROOT}${encodeURIComponent(url)}.jpg`;
  }
}

function setup(
  overrides: Partial<
    ConstructorParameters<typeof CustomerDisplayProductImageResolver>[0]
  > = {},
) {
  const cache = new FakeCache();
  const ready: number[] = [];
  let clock = 10_000;
  const urls = new Map<string, string | null>();
  const resolver = new CustomerDisplayProductImageResolver({
    cache,
    resolveRemoteUrl: async (productCode) =>
      urls.get(productCode) ?? null,
    onImageReady: () => {
      ready.push(clock);
    },
    now: () => clock,
    ...overrides,
  });
  return {
    cache,
    ready,
    urls,
    resolver,
    advance: (ms: number) => {
      clock += ms;
    },
  };
}

const line = (productCode: string, lookupCode = `L-${productCode}`) => ({
  productCode,
  lookupCode,
});

test("peek 未命中返回 null 并后台下载；就绪后命中本地 URI 并回调 onImageReady", async () => {
  const { resolver, urls, cache, ready } = setup();
  urls.set("P1", "https://cdn.example.com/p1.jpg");

  assert.equal(resolver.peek(line("P1")), null);
  await settle();

  assert.deepEqual(cache.fetches, ["https://cdn.example.com/p1.jpg"]);
  assert.equal(ready.length, 1);
  const uri = resolver.peek(line("P1"));
  assert.equal(
    uri,
    `${ROOT}${encodeURIComponent("https://cdn.example.com/p1.jpg")}.jpg`,
  );
  // 命中后不会再触发下载或回调。
  await settle();
  assert.equal(cache.fetches.length, 1);
  assert.equal(ready.length, 1);
});

test("同一商品在途期间重复 peek 只触发一次解析与下载", async () => {
  const { resolver, urls, cache } = setup();
  urls.set("P1", "https://cdn.example.com/p1.jpg");
  const gate = deferred<string>();
  cache.gates.set("https://cdn.example.com/p1.jpg", gate);

  for (let index = 0; index < 5; index += 1) resolver.peek(line("P1"));
  await settle();
  resolver.peek(line("P1"));
  assert.equal(cache.fetches.length, 1);

  gate.resolve(`${ROOT}done.jpg`);
  await settle();
  assert.equal(resolver.peek(line("P1")), `${ROOT}done.jpg`);
});

test("并发上限为 2，其余排队，空位出现后依次继续", async () => {
  const { resolver, urls, cache, ready } = setup();
  const gates = ["P1", "P2", "P3", "P4"].map((code) => {
    const url = `https://cdn.example.com/${code}.jpg`;
    urls.set(code, url);
    const gate = deferred<string>();
    cache.gates.set(url, gate);
    return { code, gate };
  });

  for (const { code } of gates) resolver.peek(line(code));
  await settle();
  assert.equal(cache.fetches.length, 2);

  gates[0]!.gate.resolve(`${ROOT}1.jpg`);
  await settle();
  assert.equal(cache.fetches.length, 3);
  assert.equal(ready.length, 1);

  gates[1]!.gate.resolve(`${ROOT}2.jpg`);
  gates[2]!.gate.resolve(`${ROOT}3.jpg`);
  await settle();
  assert.equal(cache.fetches.length, 4);
  gates[3]!.gate.resolve(`${ROOT}4.jpg`);
  await settle();
  assert.equal(ready.length, 4);
});

test("下载失败后在退避期内不重试，到期后才会再试", async () => {
  const { resolver, urls, cache, advance, ready } = setup({
    failureRetryMs: 60_000,
  });
  urls.set("P1", "https://cdn.example.com/p1.jpg");
  cache.failUrls.add("https://cdn.example.com/p1.jpg");

  resolver.peek(line("P1"));
  await settle();
  assert.equal(cache.fetches.length, 1);

  // 退避期内反复 peek（模拟连续发布）不会形成重试风暴。
  for (let index = 0; index < 10; index += 1) resolver.peek(line("P1"));
  await settle();
  assert.equal(cache.fetches.length, 1);

  advance(60_001);
  cache.failUrls.clear();
  resolver.peek(line("P1"));
  await settle();
  assert.equal(cache.fetches.length, 2);
  assert.equal(ready.length, 1);
});

test("没有图片地址时按较长间隔重查，且不下载", async () => {
  const { resolver, cache, advance, urls } = setup({
    missingRetryMs: 300_000,
  });
  resolver.peek(line("P9"));
  await settle();
  resolver.peek(line("P9"));
  await settle();
  assert.equal(cache.fetches.length, 0);

  // 退避期内不再解析：把地址补上也要等到期。
  urls.set("P9", "https://cdn.example.com/p9.jpg");
  advance(299_999);
  resolver.peek(line("P9"));
  await settle();
  assert.equal(cache.fetches.length, 0);

  advance(2);
  resolver.peek(line("P9"));
  await settle();
  assert.equal(cache.fetches.length, 1);
});

test("缓存文件被淘汰后丢弃索引并重新下载", async () => {
  const { resolver, urls, cache } = setup();
  urls.set("P1", "https://cdn.example.com/p1.jpg");
  resolver.peek(line("P1"));
  await settle();
  const uri = resolver.peek(line("P1"))!;
  assert.ok(uri);

  cache.evicted.add(uri);
  assert.equal(resolver.peek(line("P1")), null);
  await settle();
  assert.equal(cache.fetches.length, 2);
});

test("解析器自身抛错、onImageReady 抛错都被吞掉；dispose 后不再回调", async () => {
  const throwing = setup({
    resolveRemoteUrl: async () => {
      throw new Error("catalog down");
    },
  });
  assert.equal(throwing.resolver.peek(line("P1")), null);
  await settle();
  assert.equal(throwing.cache.fetches.length, 0);

  let readyCalls = 0;
  const noisy = setup({
    onImageReady: () => {
      readyCalls += 1;
      throw new Error("refresh exploded");
    },
  });
  noisy.urls.set("P1", "https://cdn.example.com/p1.jpg");
  noisy.resolver.peek(line("P1"));
  await settle();
  assert.equal(readyCalls, 1);
  assert.ok(noisy.resolver.peek(line("P1")));

  const disposed = setup();
  disposed.urls.set("P2", "https://cdn.example.com/p2.jpg");
  const gate = deferred<string>();
  disposed.cache.gates.set("https://cdn.example.com/p2.jpg", gate);
  disposed.resolver.peek(line("P2"));
  await settle();
  disposed.resolver.dispose();
  gate.resolve(`${ROOT}2.jpg`);
  await settle();
  assert.equal(disposed.ready.length, 0);
  assert.equal(disposed.resolver.peek(line("P2")), null);
  assert.equal(disposed.resolver.peek(line("P3")), null);
});

test("空商品编码直接忽略", async () => {
  const { resolver, cache } = setup();
  assert.equal(resolver.peek({ productCode: "  ", lookupCode: "x" }), null);
  await settle();
  assert.equal(cache.fetches.length, 0);
});

test("目录解析：按商品编码与查询码核对目录项，仅返回受信任的图片地址", async () => {
  const resolve = createCatalogProductImageUrlResolver({
    apiBaseUrl: "https://api.example.com/",
    findExact: async (code) =>
      code === "L1"
        ? { productCode: "P1", lookupCode: "L1", productImage: "/img/p1.jpg" }
        : null,
  });
  assert.equal(
    await resolve("P1", "L1"),
    "https://api.example.com/img/p1.jpg",
  );
  assert.equal(await resolve("P2", "L1"), null); // 商品编码不一致
  assert.equal(await resolve("P1", "L2"), null); // 目录无此码
  assert.equal(await resolve("", "L1"), null);

  const untrusted = createCatalogProductImageUrlResolver({
    apiBaseUrl: "https://api.example.com/",
    findExact: async () => ({
      productCode: "P1",
      lookupCode: "L1",
      productImage: "http://evil.example.net/p.jpg",
    }),
  });
  assert.equal(await untrusted("P1", "L1"), null);

  const noImage = createCatalogProductImageUrlResolver({
    apiBaseUrl: "https://api.example.com/",
    findExact: async () => ({
      productCode: "P1",
      lookupCode: "L1",
      productImage: null,
    }),
  });
  assert.equal(await noImage("P1", "L1"), null);
});

test("一码多商品时在同码候选中按商品编码取图", async () => {
  const resolve = createCatalogProductImageUrlResolver({
    apiBaseUrl: "https://api.example.com/",
    findExact: async () => {
      throw new Error("must use candidates");
    },
    findExactCandidates: async () => [
      { productCode: "P1", lookupCode: "L1", productImage: "https://cdn.example.com/a.jpg" },
      { productCode: "P2", lookupCode: "L1", productImage: "https://cdn.example.com/b.jpg" },
    ],
  });
  assert.equal(await resolve("P2", "L1"), "https://cdn.example.com/b.jpg");
  assert.equal(await resolve("P3", "L1"), null);
});
