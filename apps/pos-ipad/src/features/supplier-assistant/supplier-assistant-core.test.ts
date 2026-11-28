import assert from "node:assert/strict";
import { createHash } from "node:crypto";
import { readFile } from "node:fs/promises";
import path from "node:path";
import test from "node:test";

import {
  SUPPLIER_ASSISTANT_CONTENT_SOURCE_HASH,
} from "./generated/supplier-assistant-content.generated";
import {
  createSupplierAssistantBackground,
  decideSupplierNavigation,
  originMatchesPattern,
  resolveProfileHomeUrl,
  toProfileLites,
  type LocateRequest,
  type SupplierProfileLite,
} from "./supplier-assistant-background";
import {
  normalizeApiOrigin,
  SupplierAssistantSession,
  type FetchLike,
  type SupplierAssistantToken,
} from "./supplier-assistant-session";

const DATS: SupplierProfileLite = Object.freeze({
  supplierCode: "240",
  displayName: "DATS",
  enabled: true,
  origins: ["https://www.dats.com.au/*"],
  hasSearch: true,
  categoryPassiveEnabled: true,
});

function okResult(data: unknown = {}) {
  return Object.freeze({ httpStatus: 200, ok: true, success: true, data, message: null, errorCode: null });
}

function createBackground(overrides: { profile?: SupplierProfileLite | null; storeCode?: string | null } = {}) {
  const calls: { path: string; body: unknown }[] = [];
  const located: LocateRequest[] = [];
  const background = createSupplierAssistantBackground({
    request: async (requestPath, init) => {
      calls.push({ path: requestPath, body: init.body });
      return okResult({ echoed: true });
    },
    getActiveProfile: () => (overrides.profile === undefined ? DATS : overrides.profile),
    getStoreCode: () => (overrides.storeCode === undefined ? "1042" : overrides.storeCode),
    onLocate: (request) => located.push(request),
  });
  return { background, calls, located };
}

test("摘要请求一律用 App 选定的分店，不信网页传来的分店", async () => {
  const { background, calls } = createBackground();

  const response = await background.handle(
    { type: "SUMMARY_BATCH", storeCode: "1002", supplierCode: "240", itemNumbers: ["VD028"], salesRankingDays: 90 },
    "https://www.dats.com.au/office-stationery",
  );

  assert.equal(response.ok, true);
  assert.equal(calls.length, 1);
  assert.equal(calls[0]!.path, "/api/react/v1/browser-extension/product-purchase-cycle-summary/batch");
  assert.deepEqual(calls[0]!.body, {
    storeCode: "1042",
    supplierCode: "240",
    itemNumbers: ["VD028"],
    salesRankingDays: 90,
  });
});

test("来源网页不是当前供应商时一律拒绝且不发请求", async () => {
  const { background, calls } = createBackground();

  for (const url of ["https://evil.example/", "http://www.dats.com.au/x", "https://dats.com.au.evil.example/"]) {
    const response = await background.handle(
      { type: "BARCODE_LOOKUP", supplierCode: "240", barcode: "9312345678907" },
      url,
    );
    assert.equal(response.ok, false, url);
  }
  assert.equal(calls.length, 0);
});

test("供应商编码与当前供应商不符时拒绝", async () => {
  const { background, calls } = createBackground();

  const response = await background.handle(
    { type: "SUMMARY_BATCH", supplierCode: "236", itemNumbers: ["A1"] },
    "https://www.dats.com.au/",
  );

  assert.equal(response.ok, false);
  assert.equal(calls.length, 0);
});

test("还没拿到分店时摘要请求不发出", async () => {
  const { background, calls } = createBackground({ storeCode: null });

  const response = await background.handle(
    { type: "SUMMARY_BATCH", supplierCode: "240", itemNumbers: ["A1"] },
    "https://www.dats.com.au/",
  );

  assert.equal(response.ok, false);
  assert.equal(calls.length, 0);
});

test("商品按钮与扫码定位都交给 App 打开采购周期面板", async () => {
  const { background, located, calls } = createBackground();

  await background.handle({ type: "LOCATE_ITEM", supplierCode: "240", itemNumber: "VD028" }, "https://www.dats.com.au/a");
  await background.handle({ type: "SCAN_LOCATE_ITEM", supplierCode: "240", itemNumber: "VD029" }, "https://www.dats.com.au/b");

  assert.deepEqual(located, [
    { supplierCode: "240", itemNumber: "VD028", source: "button" },
    { supplierCode: "240", itemNumber: "VD029", source: "scan" },
  ]);
  assert.equal(calls.length, 0);
});

test("被动分类采集只接受同源页面、passive 模式，并本地限流", async () => {
  let now = 1_000;
  const calls: string[] = [];
  const background = createSupplierAssistantBackground({
    request: async (requestPath) => {
      calls.push(requestPath);
      return okResult();
    },
    getActiveProfile: () => DATS,
    getStoreCode: () => "1042",
    onLocate: () => undefined,
    now: () => now,
  });
  const capture = (pageUrl: string, mode = "passive") => ({
    type: "CATEGORY_CAPTURE",
    payload: { supplierCode: "240", pageUrl, mode, categoryPath: [], itemNumbers: ["A1"], capturedAt: "2026-11-26T00:00:00Z" },
  });

  assert.equal((await background.handle(capture("https://evil.example/x"), "https://www.dats.com.au/x")).ok, false);
  assert.equal((await background.handle(capture("https://www.dats.com.au/x", "crawl"), "https://www.dats.com.au/x")).ok, false);
  for (let index = 0; index < 20; index += 1) {
    assert.equal((await background.handle(capture("https://www.dats.com.au/x"), "https://www.dats.com.au/x")).ok, true);
  }
  const limited = await background.handle(capture("https://www.dats.com.au/x"), "https://www.dats.com.au/x");
  assert.equal(limited.errorCode, "LOCAL_RATE_LIMITED");
  assert.equal(calls.length, 20);
  now += 61_000;
  assert.equal((await background.handle(capture("https://www.dats.com.au/x"), "https://www.dats.com.au/x")).ok, true);
});

test("主动整站采集在 App 里不支持", async () => {
  const { background, calls } = createBackground();

  const response = await background.handle({ type: "CATEGORY_TREE_SNAPSHOT", payload: {} }, "https://www.dats.com.au/");

  assert.equal(response.errorCode, "CRAWL_NOT_SUPPORTED");
  assert.equal(calls.length, 0);
});

test("origin 规则：精确主机与 *. 子域", () => {
  assert.equal(originMatchesPattern("https://www.dats.com.au/*", "https://www.dats.com.au"), true);
  assert.equal(originMatchesPattern("https://www.dats.com.au/*", "https://dats.com.au"), false);
  assert.equal(originMatchesPattern("https://*.example.com/*", "https://shop.example.com"), true);
  assert.equal(originMatchesPattern("https://*.example.com/*", "https://example.com"), false);
  assert.equal(originMatchesPattern("https://*.example.com/*", "https://evilexample.com"), false);
  assert.equal(originMatchesPattern("http://txkorders.inzantsales.com/*", "http://txkorders.inzantsales.com"), true);
});

test("后端供应商配置只保留启用且字段齐全的条目", () => {
  const lites = toProfileLites({
    profiles: [
      { supplierCode: "240", displayName: "DATS", enabled: true, origins: ["https://www.dats.com.au/*"], search: { urlTemplate: "x" }, category: { enabled: true, passiveEnabled: true } },
      { supplierCode: "236", displayName: "Off", enabled: false, origins: ["https://off.example/*"] },
      { supplierCode: "", displayName: "Bad", enabled: true, origins: ["https://bad.example/*"] },
    ],
  });

  assert.deepEqual(lites.map((item) => item.supplierCode), ["240"]);
  assert.equal(lites[0]!.hasSearch, true);
  assert.equal(lites[0]!.categoryPassiveEnabled, true);
  assert.equal(resolveProfileHomeUrl(lites[0]!), "https://www.dats.com.au/");
});

test("供应商网页跳转：本站 http 改走 https，整站 http 放行交给 ATS，其余 http 与外部协议拦下", () => {
  const profile = (origins: string[]): SupplierProfileLite =>
    Object.freeze({ ...DATS, supplierCode: "x", displayName: "x", origins });
  const brazco = profile(["https://www.brazcoint.com.au/*"]);
  const yatsal = profile(["https://yatsal.com.au/*", "https://www.yatsal.com.au/*"]);
  const txk = profile(["http://txkorders.inzantsales.com/*"]);

  assert.deepEqual(decideSupplierNavigation(brazco, "https://www.brazcoint.com.au/"), { action: "allow" });
  // 网站自己的 301：https → http://本站/home.html，App 跳过明文那一跳。
  assert.deepEqual(decideSupplierNavigation(brazco, "http://www.brazcoint.com.au/home.html"), {
    action: "upgrade",
    url: "https://www.brazcoint.com.au/home.html",
  });
  assert.deepEqual(decideSupplierNavigation(yatsal, "http://yatsal.com.au/"), {
    action: "upgrade",
    url: "https://yatsal.com.au/",
  });
  assert.deepEqual(decideSupplierNavigation(txk, "http://txkorders.inzantsales.com/shop"), { action: "allow" });
  assert.deepEqual(decideSupplierNavigation(brazco, "http://evil.example/"), { action: "block" });
  assert.deepEqual(decideSupplierNavigation(brazco, "http://www.brazcoint.com.au.evil.example/"), { action: "block" });
  assert.deepEqual(decideSupplierNavigation(brazco, "mailto:sales@brazcoint.com.au"), { action: "block" });
  assert.deepEqual(decideSupplierNavigation(brazco, "about:blank"), { action: "allow" });
  // 外站 https 维持原样放行（与改动前一致）。
  assert.deepEqual(decideSupplierNavigation(brazco, "https://www.google.com/maps"), { action: "allow" });
});

test("供应商首页优先 www（裸域名常先 301 到 http），没有 www 就用第一条", () => {
  const profile = (origins: string[]): SupplierProfileLite =>
    Object.freeze({ ...DATS, supplierCode: "x", displayName: "x", origins });
  assert.equal(
    resolveProfileHomeUrl(profile(["https://yatsal.com.au/*", "https://www.yatsal.com.au/*"])),
    "https://www.yatsal.com.au/",
  );
  assert.equal(resolveProfileHomeUrl(profile(["http://txkorders.inzantsales.com/*"])), "http://txkorders.inzantsales.com/");
  assert.equal(resolveProfileHomeUrl(profile(["https://*.example.com/*"])), null);
});

test("中心地址只接受 https origin", () => {
  assert.equal(normalizeApiOrigin("https://HotBargain.vip/"), "https://hotbargain.vip");
  assert.equal(normalizeApiOrigin("http://hotbargain.vip"), null);
  assert.equal(normalizeApiOrigin("https://hotbargain.vip/api"), null);
});

function token(overrides: Partial<SupplierAssistantToken> = {}): SupplierAssistantToken {
  return Object.freeze({
    accessToken: "jwt-1",
    expiresAtMs: 10 * 60_000,
    apiOrigin: "https://hotbargain.vip",
    ...overrides,
  });
}

test("会话：并发请求共用一次签发，快过期时提前换令牌", async () => {
  let now = 0;
  let issued = 0;
  const seenAuth: string[] = [];
  const fetchImpl: FetchLike = async (_url, init) => {
    seenAuth.push(init.headers.Authorization!);
    return { status: 200, ok: true, json: async () => ({ success: true, data: {} }) };
  };
  const session = new SupplierAssistantSession(
    {
      issueToken: async () => {
        issued += 1;
        return token({ accessToken: `jwt-${issued}`, expiresAtMs: now + 5 * 60_000 });
      },
    },
    fetchImpl,
    "1.7.1",
    () => now,
  );

  await Promise.all([
    session.request("/api/react/v1/browser-extension/stores", { method: "GET" }),
    session.request("/api/react/v1/browser-extension/stores", { method: "GET" }),
  ]);
  assert.equal(issued, 1);
  now += 4 * 60_000 + 1;
  await session.request("/api/react/v1/browser-extension/stores", { method: "GET" });
  assert.equal(issued, 2);
  assert.deepEqual(seenAuth, ["Bearer jwt-1", "Bearer jwt-1", "Bearer jwt-2"]);
});

test("会话：401 时换一次令牌重试；只允许订货助手接口", async () => {
  let issued = 0;
  let calls = 0;
  const session = new SupplierAssistantSession(
    {
      issueToken: async () => {
        issued += 1;
        return token({ accessToken: `jwt-${issued}` });
      },
    },
    async () => {
      calls += 1;
      return calls === 1
        ? { status: 401, ok: false, json: async () => ({ success: false, errorCode: "EXTENSION_TOKEN_EXPIRED" }) }
        : { status: 200, ok: true, json: async () => ({ success: true, data: { ok: 1 } }) };
    },
    "1.7.1",
    () => 0,
  );

  const result = await session.request("/api/react/v1/browser-extension/stores", { method: "GET" });
  assert.equal(result.success, true);
  assert.equal(issued, 2);
  await assert.rejects(() => session.request("/api/users", { method: "GET" }));
  await assert.rejects(() => session.request("/api/react/v1/browser-extension/../../users", { method: "GET" }));
});

test("生成的注入脚本与插件源码一致（插件改了要重跑生成脚本）", async () => {
  // 与 scripts/build-supplier-assistant-content.mjs 的 computeSourceHash 同算法。
  const appRoot = path.resolve(__dirname, "../../..");
  const extensionRoot = path.resolve(appRoot, "../supplier-order-extension/src");
  const contentDir = path.join(appRoot, "src/features/supplier-assistant/content");
  const { readdir } = await import("node:fs/promises");
  const libFiles = (await readdir(path.join(extensionRoot, "lib")))
    .filter((name) => name.endsWith(".js"))
    .map((name) => path.join(extensionRoot, "lib", name));
  const files = [
    path.join(extensionRoot, "content/list.js"),
    ...libFiles,
    path.join(contentDir, "chrome-shim.js"),
    path.join(contentDir, "entry.js"),
  ].sort();
  const hash = createHash("sha256");
  for (const file of files) {
    hash.update(path.relative(appRoot, file).split(path.sep).join("/"));
    hash.update("\0");
    hash.update(await readFile(file));
    hash.update("\0");
  }

  assert.equal(
    hash.digest("hex"),
    SUPPLIER_ASSISTANT_CONTENT_SOURCE_HASH,
    "插件源码已变，请运行 node apps/pos-ipad/scripts/build-supplier-assistant-content.mjs",
  );
});
