import assert from "node:assert/strict";
import { createHash, randomBytes } from "node:crypto";
import test from "node:test";
import vm from "node:vm";

import { canUseOrdering, OrderingAuthError, signInOrdering, type OrderingFetch } from "./ordering-auth";
import { isOrderingLoginUrl, isOrderingShopUrl } from "./ordering-config";
import { formatIdleRemaining, resolveDefaultOrderingStore, toOrderingStores } from "./ordering-stores";
import {
  bytesToBase64Url,
  OrderingSupplierTokenSource,
  type OrderingAuthorizer,
  type OrderingCrypto,
} from "./ordering-token-source";
import {
  buildAuthorizeScript,
  ORDERING_BRIDGE_CHANNEL,
  OrderingBridgeError,
  OrderingWebBridge,
} from "./ordering-web-bridge";

import { SupplierAssistantTokenError } from "@/features/supplier-assistant/supplier-assistant-session";

/* ---------------- 网址规则 ---------------- */

test("订货模式只停在 hotbargain.vip/shop 及其子页", () => {
  assert.equal(isOrderingShopUrl("https://hotbargain.vip/shop"), true);
  assert.equal(isOrderingShopUrl("https://hotbargain.vip/shop/orders?x=1"), true);
  assert.equal(isOrderingShopUrl("https://hotbargain.vip/shopping"), false);
  assert.equal(isOrderingShopUrl("https://hotbargain.vip/dashboard"), false);
  assert.equal(isOrderingShopUrl("https://evil.example/shop"), false);
  assert.equal(isOrderingShopUrl("http://hotbargain.vip/shop"), false);
  assert.equal(isOrderingShopUrl(null), false);
  assert.equal(isOrderingLoginUrl("https://hotbargain.vip/login?redirect=%2Fshop"), true);
  assert.equal(isOrderingLoginUrl("https://hotbargain.vip/shop"), false);
});

/* ---------------- 登录 ---------------- */

test("订货权限：管理员或有订货前台权限（权限码值为 OrderFront）", () => {
  assert.equal(canUseOrdering({ roleNames: ["Admin"], permissions: [] }), true);
  assert.equal(canUseOrdering({ roleNames: ["超级管理员"] }), true);
  assert.equal(canUseOrdering({ roleNames: ["StoreManager"], permissions: ["OrderFront"] }), true);
  assert.equal(canUseOrdering({ roleNames: ["Cashier"], permissions: ["OrderFront.BatchProductSales.View"] }), false);
  assert.equal(canUseOrdering({}), false);
});

type Call = Readonly<{ url: string; method: string; body?: string; credentials: string }>;

function fakeFetch(responses: Record<string, () => { status: number; body: unknown }>, calls: Call[]): OrderingFetch {
  return async (url, init) => {
    calls.push({ url, method: init.method, ...(init.body ? { body: init.body } : {}), credentials: init.credentials });
    const path = new URL(url).pathname;
    const handler = responses[path];
    if (!handler) throw new Error(`unexpected ${path}`);
    const { status, body } = handler();
    return { status, ok: status >= 200 && status < 300, json: async () => body };
  };
}

test("登录：Cookie 会话登录 → 读当前员工 → 返回员工信息，全程带 Cookie", async () => {
  const calls: Call[] = [];
  const user = await signInOrdering(
    { username: " sean ", password: "pw" },
    fakeFetch(
      {
        "/api/Auth/session/login": () => ({ status: 200, body: { success: true, data: {} } }),
        "/api/Auth/current": () => ({
          status: 200,
          body: {
            success: true,
            data: { userGUID: "G1", username: "sean", fullName: "Sean Pan", roleNames: [], permissions: ["OrderFront"] },
          },
        }),
      },
      calls,
    ),
  );
  assert.deepEqual(user, { userGuid: "G1", username: "sean", displayName: "Sean Pan" });
  assert.deepEqual(calls.map((call) => call.url), [
    "https://hotbargain.vip/api/Auth/session/login",
    "https://hotbargain.vip/api/Auth/current",
  ]);
  assert.ok(calls.every((call) => call.credentials === "include"));
  // passwordFormat 缺省会被后端当成 clientSha256，新密码哈希的账号必然登录失败。
  assert.deepEqual(JSON.parse(calls[0]!.body!), { username: "sean", password: "pw", passwordFormat: "raw" });
});

test("登录：账号密码不对时带回后端原话", async () => {
  await assert.rejects(
    () =>
      signInOrdering(
        { username: "a", password: "b" },
        fakeFetch({ "/api/Auth/session/login": () => ({ status: 200, body: { success: false, message: "账号已停用" } }) }, []),
      ),
    (error) => error instanceof OrderingAuthError && error.code === "INVALID_CREDENTIALS" && error.serverMessage === "账号已停用",
  );
});

test("登录：没有订货权限时退出刚建的会话再报错", async () => {
  const calls: Call[] = [];
  await assert.rejects(
    () =>
      signInOrdering(
        { username: "a", password: "b" },
        fakeFetch(
          {
            "/api/Auth/session/login": () => ({ status: 200, body: { success: true } }),
            "/api/Auth/current": () => ({
              status: 200,
              body: { success: true, data: { userGUID: "G", username: "a", roleNames: ["Cashier"], permissions: [] } },
            }),
            "/api/Auth/session/logout": () => ({ status: 200, body: { success: true } }),
          },
          calls,
        ),
      ),
    (error) => error instanceof OrderingAuthError && error.code === "NOT_ALLOWED",
  );
  assert.equal(calls.at(-1)!.url, "https://hotbargain.vip/api/Auth/session/logout");
});

test("登录：连不上服务器报网络错误", async () => {
  await assert.rejects(
    () => signInOrdering({ username: "a", password: "b" }, async () => Promise.reject(new Error("offline"))),
    (error) => error instanceof OrderingAuthError && error.code === "NETWORK",
  );
});

/* ---------------- 网页会话桥 ---------------- */

test("网页桥：等网页就绪后注入授权脚本，只收 hotbargain.vip 页面带编号的回信", async () => {
  const bridge = new OrderingWebBridge();
  const injected: string[] = [];
  const pending = bridge.requestAuthorization({ codeChallenge: "c".repeat(43), state: "s".repeat(22), clientId: "hb-supplier-order" });
  assert.equal(injected.length, 0);
  bridge.attach((script) => injected.push(script));
  await new Promise((resolve) => setTimeout(resolve, 0));
  assert.equal(injected.length, 1);
  const id = /var id = "([^"]+)"/.exec(injected[0]!)![1]!;

  const reply = JSON.stringify({ channel: ORDERING_BRIDGE_CHANNEL, id, result: { httpStatus: 200, body: { success: true } } });
  // 别的网站发来的同样内容不算数（但会被吞掉，不交给别的处理器）。
  assert.equal(bridge.handleMessage(reply, "https://evil.example/shop"), true);
  assert.equal(bridge.handleMessage("not json", "https://hotbargain.vip/shop"), false);
  assert.equal(bridge.handleMessage(JSON.stringify({ channel: "other" }), "https://hotbargain.vip/shop"), false);
  assert.equal(bridge.handleMessage(reply, "https://hotbargain.vip/shop/orders"), true);
  assert.deepEqual(await pending, { httpStatus: 200, body: { success: true } });
});

test("网页桥：网页窗口卸载时等待中的请求立即失败；没挂网页时退出返回 false", async () => {
  const bridge = new OrderingWebBridge();
  bridge.attach(() => undefined);
  const pending = bridge.requestAuthorization({ codeChallenge: "c", state: "s", clientId: "x" });
  await new Promise((resolve) => setTimeout(resolve, 0));
  bridge.attach(null);
  await assert.rejects(pending, (error) => error instanceof OrderingBridgeError && error.code === "DETACHED");
  assert.equal(await bridge.logout(), false);
});

/** 在沙箱里真跑一遍注入脚本：模拟网页的 fetch 与 ReactNativeWebView。 */
async function runAuthorizeScript(statuses: number[]): Promise<{ paths: string[]; reply: Record<string, unknown> }> {
  const paths: string[] = [];
  let resolveReply: (value: Record<string, unknown>) => void = () => undefined;
  const replied = new Promise<Record<string, unknown>>((resolve) => {
    resolveReply = resolve;
  });
  const queue = [...statuses];
  const context = vm.createContext({
    fetch: async (path: string) => {
      paths.push(path);
      const status = queue.shift() ?? 500;
      return { status, json: async () => ({ success: status === 200, data: { code: "X", state: "S" } }) };
    },
    window: {
      ReactNativeWebView: { postMessage: (data: string) => resolveReply(JSON.parse(data) as Record<string, unknown>) },
    },
  });
  vm.runInContext(buildAuthorizeScript("id-1", { codeChallenge: "c", state: "S", clientId: "hb-supplier-order" }), context);
  return { paths, reply: await replied };
}

test("授权脚本：401 时同源续期一次再申请；续期失败就照实回 401", async () => {
  const ok = await runAuthorizeScript([401, 200, 200]);
  assert.deepEqual(ok.paths, ["/api/Auth/extension/authorize", "/api/Auth/session/refresh", "/api/Auth/extension/authorize"]);
  assert.equal(ok.reply.id, "id-1");
  assert.equal((ok.reply.result as { httpStatus: number }).httpStatus, 200);

  const expired = await runAuthorizeScript([401, 401]);
  assert.deepEqual(expired.paths, ["/api/Auth/extension/authorize", "/api/Auth/session/refresh"]);
  assert.equal((expired.reply.result as { httpStatus: number }).httpStatus, 401);
});

/* ---------------- PKCE 与插件令牌 ---------------- */

test("base64url 与 Node 实现一致", () => {
  for (const length of [0, 1, 2, 3, 16, 31, 32]) {
    const bytes = randomBytes(length);
    assert.equal(bytesToBase64Url(bytes), bytes.toString("base64url"));
  }
});

const nodeCrypto: OrderingCrypto = {
  randomBytes: (length) => new Uint8Array(randomBytes(length)),
  sha256Base64: async (text) => createHash("sha256").update(text).digest("base64"),
};

function authorizer(
  respond: (payload: { codeChallenge: string; state: string; clientId: string }) => { httpStatus: number; body: unknown },
  seen: { codeChallenge: string; state: string; clientId: string }[] = [],
): OrderingAuthorizer {
  return {
    requestAuthorization: async (payload) => {
      seen.push(payload);
      return respond(payload);
    },
  };
}

test("插件令牌：challenge = SHA256(verifier)，换令牌带 verifier 与 state，到期时间有上限", async () => {
  const seen: { codeChallenge: string; state: string; clientId: string }[] = [];
  const exchanges: Record<string, unknown>[] = [];
  const source = new OrderingSupplierTokenSource(
    authorizer((payload) => ({ httpStatus: 200, body: { success: true, data: { code: "C".repeat(43), state: payload.state } } }), seen),
    nodeCrypto,
    async (url, init) => {
      assert.equal(url, "https://hotbargain.vip/api/Auth/extension/token");
      exchanges.push(JSON.parse(init.body) as Record<string, unknown>);
      return {
        status: 200,
        ok: true,
        json: async () => ({ success: true, data: { accessToken: "ext-jwt", accessTokenExpiry: "2099-01-01T00:00:00Z" } }),
      };
    },
    () => 1_000,
  );
  const token = await source.issueToken();
  assert.equal(token.accessToken, "ext-jwt");
  assert.equal(token.apiOrigin, "https://hotbargain.vip");
  assert.equal(token.expiresAtMs, 1_000 + 10 * 60_000);
  const verifier = exchanges[0]!.codeVerifier as string;
  assert.match(verifier, /^[A-Za-z0-9_-]{43}$/);
  assert.match(seen[0]!.state, /^[A-Za-z0-9_-]{22}$/);
  assert.equal(seen[0]!.codeChallenge, createHash("sha256").update(verifier).digest("base64url"));
  assert.equal(seen[0]!.clientId, "hb-supplier-order");
  assert.deepEqual(exchanges[0], {
    code: "C".repeat(43),
    codeVerifier: verifier,
    state: seen[0]!.state,
    clientId: "hb-supplier-order",
  });
});

test("插件令牌：网页会话没了（401 / Cookie 缺失）报 SESSION_EXPIRED，state 不符报 AUTHORIZE_FAILED", async () => {
  const neverExchange = async () => {
    throw new Error("不应换令牌");
  };
  for (const response of [
    { httpStatus: 401, body: null },
    { httpStatus: 200, body: { success: false, errorCode: "EXTENSION_AUTH_COOKIE_REQUIRED" } },
  ]) {
    await assert.rejects(
      () => new OrderingSupplierTokenSource(authorizer(() => response), nodeCrypto, neverExchange).issueToken(),
      (error) => error instanceof SupplierAssistantTokenError && error.code === "SESSION_EXPIRED",
    );
  }
  await assert.rejects(
    () =>
      new OrderingSupplierTokenSource(
        authorizer(() => ({ httpStatus: 200, body: { success: true, data: { code: "C", state: "other" } } })),
        nodeCrypto,
        neverExchange,
      ).issueToken(),
    (error) => error instanceof SupplierAssistantTokenError && error.code === "AUTHORIZE_FAILED",
  );
});

/* ---------------- 分店与倒计时 ---------------- */

test("订货助手分店：去重去空；默认本机分店，否则单店直接用，多店让员工选", () => {
  const stores = toOrderingStores({
    stores: [
      { storeCode: "1042", storeName: "Orion" },
      { storeCode: "1042", storeName: "重复" },
      { storeCode: " ", storeName: "空" },
      { storeCode: "1012", storeName: "" },
      null,
    ],
  });
  assert.deepEqual(stores, [
    { storeCode: "1042", storeName: "Orion" },
    { storeCode: "1012", storeName: "1012" },
  ]);
  assert.equal(resolveDefaultOrderingStore(stores, "1012")?.storeCode, "1012");
  assert.equal(resolveDefaultOrderingStore(stores, "9999"), null);
  assert.equal(resolveDefaultOrderingStore(stores, null), null);
  assert.equal(resolveDefaultOrderingStore([stores[0]!], null)?.storeCode, "1042");
  assert.deepEqual(toOrderingStores(null), []);
});

test("闲置倒计时显示分:秒，不出现负数", () => {
  assert.equal(formatIdleRemaining(15 * 60_000), "15:00");
  assert.equal(formatIdleRemaining(61_001), "1:02");
  assert.equal(formatIdleRemaining(-5), "0:00");
});
