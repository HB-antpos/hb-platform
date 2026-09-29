(() => {
  var __defProp = Object.defineProperty;
  var __getOwnPropNames = Object.getOwnPropertyNames;
  var __esm = (fn, res, err) => function __init() {
    if (err) throw err[0];
    try {
      return fn && (res = (0, fn[__getOwnPropNames(fn)[0]])(fn = 0)), res;
    } catch (e) {
      throw err = [e], e;
    }
  };
  var __export = (target, all) => {
    for (var name in all)
      __defProp(target, name, { get: all[name], enumerable: true });
  };

  // hb-safari-config:config.js
  var config_exports = {};
  __export(config_exports, {
    API_BASE: () => API_BASE,
    BUILD_TARGET: () => BUILD_TARGET,
    EXTENSION_VERSION: () => EXTENSION_VERSION,
    HB_API_ORIGIN: () => HB_API_ORIGIN,
    HB_WEB_ORIGIN: () => HB_WEB_ORIGIN
  });
  var EXTENSION_VERSION, HB_API_ORIGIN, HB_WEB_ORIGIN, BUILD_TARGET, API_BASE;
  var init_config = __esm({
    "hb-safari-config:config.js"() {
      EXTENSION_VERSION = "1.5.0";
      HB_API_ORIGIN = "https://hotbargain.vip";
      HB_WEB_ORIGIN = "https://hotbargain.vip";
      BUILD_TARGET = "safari";
      API_BASE = HB_API_ORIGIN;
    }
  });

  // src/lib/handshake.js
  var handshake_exports = {};
  __export(handshake_exports, {
    EXTENSION_MESSAGE_SOURCE: () => EXTENSION_MESSAGE_SOURCE,
    NONCE_PATTERN: () => NONCE_PATTERN,
    OPEN_MESSAGE_TYPE: () => OPEN_MESSAGE_TYPE,
    OPEN_RESULT_MESSAGE_TYPE: () => OPEN_RESULT_MESSAGE_TYPE,
    PING_MESSAGE_TYPE: () => PING_MESSAGE_TYPE,
    PLATFORM_MESSAGE_SOURCE: () => PLATFORM_MESSAGE_SOURCE,
    STATUS_MESSAGE_TYPE: () => STATUS_MESSAGE_TYPE,
    buildBridgeResponse: () => buildBridgeResponse,
    detectBrowser: () => detectBrowser,
    generateNonce: () => generateNonce,
    isValidNonce: () => isValidNonce,
    validateBridgeMessage: () => validateBridgeMessage
  });
  function isValidNonce(nonce) {
    return typeof nonce === "string" && NONCE_PATTERN.test(nonce);
  }
  function bytesToBase64Url(bytes) {
    let binary = "";
    for (let i = 0; i < bytes.length; i++) binary += String.fromCharCode(bytes[i]);
    if (typeof btoa === "function") {
      return btoa(binary).replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/, "");
    }
    return Buffer.from(bytes).toString("base64url");
  }
  function generateNonce(bytes = 32) {
    const arr = new Uint8Array(bytes);
    const c = globalThis.crypto;
    if (c && typeof c.getRandomValues === "function") {
      c.getRandomValues(arr);
    } else {
      for (let i = 0; i < bytes; i++) arr[i] = Math.floor(Math.random() * 256);
    }
    return bytesToBase64Url(arr);
  }
  function detectBrowser(userAgent) {
    const ua = String(userAgent || "");
    if (/Edg\//.test(ua)) return "edge";
    if (/Chrome\//.test(ua)) return "chrome";
    if (/Version\//.test(ua) && /Safari\//.test(ua)) return "safari";
    return "unknown";
  }
  function validateBridgeMessage({ source, sourceWindow, origin, expectedOrigin, message }) {
    if (source !== sourceWindow) return { ok: false, reason: "invalid-source" };
    if (origin !== expectedOrigin) return { ok: false, reason: "invalid-origin" };
    if (!message || typeof message !== "object") return { ok: false, reason: "invalid-message" };
    if (message.source !== PLATFORM_MESSAGE_SOURCE) return { ok: false, reason: "invalid-message-source" };
    if (message.type !== PING_MESSAGE_TYPE && message.type !== OPEN_MESSAGE_TYPE) {
      return { ok: false, reason: "invalid-type" };
    }
    if (!isValidNonce(message.nonce)) return { ok: false, reason: "invalid-nonce" };
    return { ok: true };
  }
  function buildBridgeResponse({ kind, nonce, version, browser, installed = true, ok, error }) {
    const resp = {
      source: EXTENSION_MESSAGE_SOURCE,
      type: kind === "OPEN_RESULT" ? OPEN_RESULT_MESSAGE_TYPE : STATUS_MESSAGE_TYPE,
      nonce,
      installed,
      version,
      browser
    };
    if (kind === "OPEN_RESULT") {
      resp.ok = ok === true;
      if (error) resp.error = error;
    }
    return resp;
  }
  var PLATFORM_MESSAGE_SOURCE, EXTENSION_MESSAGE_SOURCE, PING_MESSAGE_TYPE, OPEN_MESSAGE_TYPE, STATUS_MESSAGE_TYPE, OPEN_RESULT_MESSAGE_TYPE, NONCE_PATTERN;
  var init_handshake = __esm({
    "src/lib/handshake.js"() {
      PLATFORM_MESSAGE_SOURCE = "hb-platform";
      EXTENSION_MESSAGE_SOURCE = "hb-supplier-ordering-extension";
      PING_MESSAGE_TYPE = "HB_SUPPLIER_ASSISTANT_PING";
      OPEN_MESSAGE_TYPE = "HB_SUPPLIER_ASSISTANT_OPEN";
      STATUS_MESSAGE_TYPE = "HB_SUPPLIER_ASSISTANT_STATUS";
      OPEN_RESULT_MESSAGE_TYPE = "HB_SUPPLIER_ASSISTANT_OPEN_RESULT";
      NONCE_PATTERN = /^[A-Za-z0-9_-]{16,64}$/;
    }
  });

  // src/lib/session-handoff.js
  var session_handoff_exports = {};
  __export(session_handoff_exports, {
    WEBSITE_SESSION_CLIENT_ID: () => WEBSITE_SESSION_CLIENT_ID,
    authorizeWithCookieSessionRefresh: () => authorizeWithCookieSessionRefresh,
    coordinateWebsiteOpen: () => coordinateWebsiteOpen,
    createAccessRequestExecutor: () => createAccessRequestExecutor,
    createPkceMaterial: () => createPkceMaterial,
    createSingleFlight: () => createSingleFlight,
    parseAuthorizeResponse: () => parseAuthorizeResponse,
    parseTokenResponse: () => parseTokenResponse,
    validateWebsiteSessionContext: () => validateWebsiteSessionContext
  });
  function base64Url(bytes) {
    let binary = "";
    for (const byte of bytes) binary += String.fromCharCode(byte);
    return btoa(binary).replaceAll("+", "-").replaceAll("/", "_").replace(/=+$/u, "");
  }
  async function createPkceMaterial({ cryptoImpl = globalThis.crypto } = {}) {
    if (!cryptoImpl?.getRandomValues || !cryptoImpl?.subtle?.digest) {
      throw new Error("\u5F53\u524D\u6D4F\u89C8\u5668\u4E0D\u652F\u6301\u5B89\u5168\u7684\u7F51\u7AD9\u4F1A\u8BDD\u6388\u6743");
    }
    const verifierBytes = cryptoImpl.getRandomValues(new Uint8Array(32));
    const stateBytes = cryptoImpl.getRandomValues(new Uint8Array(32));
    const codeVerifier = base64Url(verifierBytes);
    const digest = await cryptoImpl.subtle.digest(
      "SHA-256",
      new TextEncoder().encode(codeVerifier)
    );
    return {
      codeVerifier,
      codeChallenge: base64Url(new Uint8Array(digest)),
      state: base64Url(stateBytes)
    };
  }
  function parseOrigin(value) {
    try {
      const url = new URL(value);
      if (url.username || url.password) return null;
      return url.origin;
    } catch {
      return null;
    }
  }
  function validateWebsiteSessionContext({
    pageUrl,
    webOrigin,
    apiOrigin,
    isTopLevel
  }) {
    if (!isTopLevel) return { ok: false, reason: "NOT_TOP_LEVEL" };
    let page;
    try {
      page = new URL(pageUrl);
    } catch {
      return { ok: false, reason: "INVALID_PAGE_URL" };
    }
    const trustedWebOrigin = parseOrigin(webOrigin);
    const trustedApiOrigin = parseOrigin(apiOrigin);
    if (!trustedWebOrigin || page.origin !== trustedWebOrigin) {
      return { ok: false, reason: "UNTRUSTED_PAGE" };
    }
    if (page.pathname !== "/shop") return { ok: false, reason: "NOT_SHOP_PAGE" };
    if (!trustedApiOrigin || trustedApiOrigin !== page.origin) {
      return { ok: false, reason: "API_ORIGIN_MISMATCH" };
    }
    return { ok: true, origin: page.origin };
  }
  function failure(body, fallbackReason) {
    return {
      ok: false,
      reason: body?.errorCode || fallbackReason,
      error: body?.message || fallbackReason
    };
  }
  function parseAuthorizeResponse({ httpOk, body, expectedState }) {
    if (!httpOk || body?.success !== true) return failure(body, "WEBSITE_LOGIN_REQUIRED");
    const data = body.data;
    if (!data || typeof data.code !== "string" || !data.code || typeof data.state !== "string" || data.state !== expectedState || typeof data.expiresAtUtc !== "string") {
      return failure(body, data?.state !== expectedState ? "STATE_MISMATCH" : "INVALID_AUTHORIZE_RESPONSE");
    }
    return {
      ok: true,
      code: data.code,
      state: data.state,
      expiresAtUtc: data.expiresAtUtc
    };
  }
  function parseTokenResponse({ httpOk, body, nowMs = Date.now() }) {
    if (!httpOk || body?.success !== true) return failure(body, "TOKEN_EXCHANGE_FAILED");
    const data = body.data;
    const expiryMs = Date.parse(data?.accessTokenExpiry || "");
    if (!data || typeof data.accessToken !== "string" || !data.accessToken || typeof data.accessTokenExpiry !== "string" || !Number.isFinite(expiryMs) || expiryMs <= nowMs + 5e3 || data.refreshToken != null || typeof data.userGuid !== "string" || !data.userGuid.trim() || !(typeof data.username === "string" && data.username.trim() || typeof data.fullName === "string" && data.fullName.trim())) {
      return failure(body, "INVALID_TOKEN_RESPONSE");
    }
    const user = {
      userGuid: data.userGuid.trim(),
      ...typeof data.username === "string" && data.username.trim() ? { username: data.username.trim() } : {},
      ...typeof data.fullName === "string" && data.fullName.trim() ? { fullName: data.fullName.trim() } : {}
    };
    return {
      ok: true,
      accessToken: data.accessToken,
      accessTokenExpiry: data.accessTokenExpiry,
      user
    };
  }
  function createSingleFlight(task) {
    let pending = null;
    return (...args) => {
      if (pending) return pending;
      let result;
      try {
        result = task(...args);
      } catch (error) {
        result = Promise.reject(error);
      }
      const current = Promise.resolve(result);
      const wrapped = current.finally(() => {
        if (pending === wrapped) pending = null;
      });
      pending = wrapped;
      return wrapped;
    };
  }
  async function authorizeWithCookieSessionRefresh({
    authorizeRequest,
    refreshSession
  }) {
    const first = await authorizeRequest();
    if (first?.httpStatus !== 401) return first;
    const refreshed = await refreshSession();
    return refreshed ? authorizeRequest() : first;
  }
  function invokeAndSettle(task) {
    try {
      return Promise.resolve(task()).then(
        (value) => ({ ok: true, value }),
        (error) => ({ ok: false, error })
      );
    } catch (error) {
      return Promise.resolve({ ok: false, error });
    }
  }
  async function coordinateWebsiteOpen({ buildTarget, authorize, openPanel }) {
    const earlyOpen = buildTarget === "safari" ? null : invokeAndSettle(openPanel);
    const handoff = await authorize();
    if (!handoff?.ok) {
      if (earlyOpen) await earlyOpen;
      return { handoff, openResult: null };
    }
    if (!earlyOpen) {
      return { handoff, openResult: await openPanel() };
    }
    const opened = await earlyOpen;
    if (!opened.ok) throw opened.error;
    return { handoff, openResult: opened.value };
  }
  function createAccessRequestExecutor({ isAuthFailure, clearAccessSession }) {
    return async (request) => {
      const response = await request();
      if (isAuthFailure(response)) await clearAccessSession();
      return response;
    };
  }
  var WEBSITE_SESSION_CLIENT_ID;
  var init_session_handoff = __esm({
    "src/lib/session-handoff.js"() {
      WEBSITE_SESSION_CLIENT_ID = "hb-supplier-order";
    }
  });

  // src/content/shop-bridge.js
  (async () => {
    if (window !== window.top || location.pathname !== "/shop") return;
    const [
      {
        API_BASE: API_BASE2,
        EXTENSION_VERSION: EXTENSION_VERSION2,
        BUILD_TARGET: BUILD_TARGET2,
        HB_WEB_ORIGIN: HB_WEB_ORIGIN2
      },
      {
        OPEN_MESSAGE_TYPE: OPEN_MESSAGE_TYPE2,
        PING_MESSAGE_TYPE: PING_MESSAGE_TYPE2,
        validateBridgeMessage: validateBridgeMessage2,
        buildBridgeResponse: buildBridgeResponse2,
        detectBrowser: detectBrowser2
      },
      {
        authorizeWithCookieSessionRefresh: authorizeWithCookieSessionRefresh2,
        coordinateWebsiteOpen: coordinateWebsiteOpen2,
        createPkceMaterial: createPkceMaterial2,
        createSingleFlight: createSingleFlight2,
        parseAuthorizeResponse: parseAuthorizeResponse2,
        validateWebsiteSessionContext: validateWebsiteSessionContext2,
        WEBSITE_SESSION_CLIENT_ID: WEBSITE_SESSION_CLIENT_ID2
      }
    ] = await Promise.all([
      Promise.resolve().then(() => (init_config(), config_exports)),
      Promise.resolve().then(() => (init_handshake(), handshake_exports)),
      Promise.resolve().then(() => (init_session_handoff(), session_handoff_exports))
    ]);
    if (location.origin !== HB_WEB_ORIGIN2) return;
    const version = EXTENSION_VERSION2;
    const browser = BUILD_TARGET2 && BUILD_TARGET2 !== "unknown" ? BUILD_TARGET2 : detectBrowser2(navigator.userAgent);
    const expectedOrigin = location.origin;
    async function getConfiguredApiOrigin() {
      try {
        const result = await chrome.runtime.sendMessage({ type: "GET_API_ORIGIN" });
        return result?.ok && result.apiOrigin ? result.apiOrigin : API_BASE2;
      } catch {
        return API_BASE2;
      }
    }
    const authorizeAndExchange = createSingleFlight2(async ({ apiOrigin = API_BASE2 } = {}) => {
      const context = validateWebsiteSessionContext2({
        pageUrl: location.href,
        webOrigin: HB_WEB_ORIGIN2,
        apiOrigin,
        isTopLevel: window === window.top
      });
      if (!context.ok) {
        return {
          ok: false,
          reason: context.reason,
          error: context.reason === "API_ORIGIN_MISMATCH" ? "\u5F53\u524D\u63A5\u53E3\u4E0E HB SHOP \u7F51\u9875\u4E0D\u540C\u6E90" : "\u7F51\u7AD9\u4F1A\u8BDD\u6388\u6743\u9875\u9762\u65E0\u6548"
        };
      }
      const { codeVerifier, codeChallenge, state } = await createPkceMaterial2();
      let authorization;
      try {
        const authorizeRequest = async () => {
          const response = await fetch(`${apiOrigin}/api/Auth/extension/authorize`, {
            method: "POST",
            credentials: "include",
            headers: {
              "Content-Type": "application/json",
              "X-HB-Extension-Version": version
            },
            body: JSON.stringify({
              codeChallenge,
              state,
              clientId: WEBSITE_SESSION_CLIENT_ID2
            })
          });
          let body = null;
          try {
            body = await response.json();
          } catch {
          }
          return { httpStatus: response.status, httpOk: response.ok, body };
        };
        const refreshSession = async () => {
          const response = await fetch(`${apiOrigin}/api/Auth/session/refresh`, {
            method: "POST",
            credentials: "include",
            headers: {
              "Content-Type": "application/json",
              "X-HB-Extension-Version": version
            },
            body: JSON.stringify({})
          });
          let body = null;
          try {
            body = await response.json();
          } catch {
          }
          return response.ok && body?.success === true;
        };
        authorization = await authorizeWithCookieSessionRefresh2({
          authorizeRequest,
          refreshSession
        });
      } catch (error) {
        return {
          ok: false,
          reason: "AUTHORIZE_UNAVAILABLE",
          error: String(error?.message || error)
        };
      }
      const authorized = parseAuthorizeResponse2({
        httpOk: authorization.httpOk,
        body: authorization.body,
        expectedState: state
      });
      if (!authorized.ok) return authorized;
      return chrome.runtime.sendMessage({
        type: "WEBSITE_SESSION_GRANT",
        code: authorized.code,
        codeVerifier,
        state: authorized.state,
        clientId: WEBSITE_SESSION_CLIENT_ID2
      });
    });
    chrome.runtime.onMessage.addListener((message, _sender, sendResponse) => {
      if (message?.type !== "REQUEST_WEBSITE_SESSION") return false;
      authorizeAndExchange({ apiOrigin: message.apiOrigin }).then(sendResponse).catch((error) => sendResponse({
        ok: false,
        reason: "WEBSITE_SESSION_HANDOFF_FAILED",
        error: String(error?.message || error)
      }));
      return true;
    });
    window.addEventListener("message", async (event) => {
      const check = validateBridgeMessage2({
        source: event.source,
        sourceWindow: window,
        origin: event.origin,
        expectedOrigin,
        message: event.data
      });
      if (!check.ok) return;
      const msg = event.data;
      if (msg.type === PING_MESSAGE_TYPE2) {
        event.source.postMessage(
          buildBridgeResponse2({ kind: "STATUS", nonce: msg.nonce, version, browser, installed: true }),
          expectedOrigin
        );
        return;
      }
      if (msg.type === OPEN_MESSAGE_TYPE2) {
        try {
          const { handoff, openResult: result } = await coordinateWebsiteOpen2({
            buildTarget: BUILD_TARGET2,
            // Chrome/Edge 会在本次调用先触发侧栏；Safari 仍先授权再打开完整助手页。
            openPanel: () => chrome.runtime.sendMessage({ type: "OPEN_SIDE_PANEL" }),
            authorize: async () => authorizeAndExchange({
              apiOrigin: await getConfiguredApiOrigin()
            })
          });
          if (!handoff?.ok) {
            event.source.postMessage(
              buildBridgeResponse2({
                kind: "OPEN_RESULT",
                nonce: msg.nonce,
                version,
                browser,
                installed: true,
                ok: false,
                error: handoff?.reason || "website-session-required"
              }),
              expectedOrigin
            );
            return;
          }
          const ok = !!result?.ok;
          event.source.postMessage(
            buildBridgeResponse2({
              kind: "OPEN_RESULT",
              nonce: msg.nonce,
              version,
              browser,
              installed: true,
              ok,
              error: ok ? void 0 : result?.error || "open-failed"
            }),
            expectedOrigin
          );
        } catch (error) {
          event.source.postMessage(
            buildBridgeResponse2({
              kind: "OPEN_RESULT",
              nonce: msg.nonce,
              version,
              browser,
              installed: true,
              ok: false,
              error: String(error?.message || error)
            }),
            expectedOrigin
          );
        }
      }
    });
    try {
      const ready = await chrome.runtime.sendMessage({ type: "SHOP_BRIDGE_READY" });
      if (ready?.ok && ready.shouldAuthorize) {
        void authorizeAndExchange({ apiOrigin: ready.apiOrigin });
      }
    } catch {
    }
  })();
})();
