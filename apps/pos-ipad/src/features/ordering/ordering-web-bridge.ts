import { ORDERING_WEB_ORIGIN } from "./ordering-config";

/**
 * 订货模式的「网页会话桥」：需要用网页 Cookie 会话办的事，都在仓库订货（hotbargain.vip/shop）网页窗口里同源执行。
 *
 * 中文注释：
 * 1. 网页会通过 /api/Auth/session/refresh 自己续期并轮换 Cookie；App 原生层手里那份 Cookie 会变旧。
 *    所以「申请订货助手授权码」「退出」都交给网页窗口去发，用的永远是最新那份 Cookie。
 * 2. 申请授权码沿用浏览器订货插件 shop-bridge.js 的做法：先调 extension/authorize，
 *    遇到 401（access Cookie 过期）就在同源页面里续期一次再试。
 * 3. 网页只把授权码交回 App；PKCE verifier 只在 App 原生层，换令牌也由 App 发。
 * 4. 只认来自 hotbargain.vip 页面、带本次请求编号的回信，别的消息一律忽略。
 */
export const ORDERING_BRIDGE_CHANNEL = "hb-ordering-bridge";

export type BridgeAuthorizeResult = Readonly<{ httpStatus: number; body: unknown }>;

type Pending = Readonly<{
  resolve(value: BridgeAuthorizeResult): void;
  reject(error: Error): void;
  timer: ReturnType<typeof setTimeout>;
}>;

export class OrderingBridgeError extends Error {
  public constructor(public readonly code: "NOT_READY" | "TIMEOUT" | "PAGE_ERROR" | "DETACHED") {
    super(code);
    this.name = "OrderingBridgeError";
  }
}

const REQUEST_TIMEOUT_MS = 20_000;
const READY_TIMEOUT_MS = 30_000;

/** 在网页里执行：带 Cookie 调授权接口，401 时同源续期一次再试，把结果回传给 App。 */
export function buildAuthorizeScript(
  id: string,
  payload: Readonly<{ codeChallenge: string; state: string; clientId: string }>,
): string {
  return `(function(){
  var id = ${JSON.stringify(id)};
  var body = ${JSON.stringify(JSON.stringify(payload))};
  function reply(result){
    try { window.ReactNativeWebView.postMessage(JSON.stringify({ channel: ${JSON.stringify(ORDERING_BRIDGE_CHANNEL)}, id: id, result: result })); } catch (e) {}
  }
  function call(path, data){
    return fetch(path, { method: "POST", credentials: "include", headers: { "Content-Type": "application/json", "Accept": "application/json" }, body: data })
      .then(function(response){
        return response.json().catch(function(){ return null; }).then(function(json){ return { httpStatus: response.status, body: json }; });
      });
  }
  call("/api/Auth/extension/authorize", body)
    .then(function(first){
      if (first.httpStatus !== 401) return first;
      return call("/api/Auth/session/refresh", "{}").then(function(refreshed){
        return refreshed.httpStatus >= 200 && refreshed.httpStatus < 300 && refreshed.body && refreshed.body.success === true
          ? call("/api/Auth/extension/authorize", body)
          : first;
      });
    })
    .then(function(result){ reply({ httpStatus: result.httpStatus, body: result.body }); })
    .catch(function(){ reply({ httpStatus: 0, body: null }); });
})();true;`;
}

/** 在网页里执行：作废网页会话（服务器清 Cookie），再回信。 */
export function buildLogoutScript(id: string): string {
  return `(function(){
  var id = ${JSON.stringify(id)};
  function reply(status){
    try { window.ReactNativeWebView.postMessage(JSON.stringify({ channel: ${JSON.stringify(ORDERING_BRIDGE_CHANNEL)}, id: id, result: { httpStatus: status, body: null } })); } catch (e) {}
  }
  fetch("/api/Auth/session/logout", { method: "POST", credentials: "include", headers: { "Content-Type": "application/json" }, body: "{}" })
    .then(function(response){ reply(response.status); })
    .catch(function(){ reply(0); });
})();true;`;
}

function isFromOrderingOrigin(url: string | null | undefined): boolean {
  if (!url) return false;
  try {
    return new URL(url).origin === ORDERING_WEB_ORIGIN;
  } catch {
    return false;
  }
}

export class OrderingWebBridge {
  private inject: ((script: string) => void) | null = null;
  private readyWaiters: (() => void)[] = [];
  private readonly pending = new Map<string, Pending>();
  private sequence = 0;

  /** 网页窗口加载完 hotbargain.vip 页面后登记注入函数；卸载时传 null。 */
  public attach(inject: ((script: string) => void) | null): void {
    this.inject = inject;
    if (!inject) {
      for (const [id, entry] of this.pending) {
        clearTimeout(entry.timer);
        entry.reject(new OrderingBridgeError("DETACHED"));
        this.pending.delete(id);
      }
      return;
    }
    const waiters = this.readyWaiters;
    this.readyWaiters = [];
    for (const waiter of waiters) waiter();
  }

  public get attached(): boolean {
    return this.inject !== null;
  }

  /** 处理网页窗口发来的消息；是本桥的回信就返回 true。 */
  public handleMessage(data: string, senderUrl: string | null | undefined): boolean {
    let envelope: Record<string, unknown>;
    try {
      envelope = JSON.parse(data) as Record<string, unknown>;
    } catch {
      return false;
    }
    if (envelope.channel !== ORDERING_BRIDGE_CHANNEL) return false;
    // 只有 hotbargain.vip 页面的回信才算数（网页窗口被拦回 /shop 前的一瞬间也不会误收）。
    if (!isFromOrderingOrigin(senderUrl) || typeof envelope.id !== "string") return true;
    const entry = this.pending.get(envelope.id);
    if (!entry) return true;
    this.pending.delete(envelope.id);
    clearTimeout(entry.timer);
    const result = (envelope.result ?? {}) as Record<string, unknown>;
    entry.resolve(
      Object.freeze({
        httpStatus: typeof result.httpStatus === "number" ? result.httpStatus : 0,
        body: result.body ?? null,
      }),
    );
    return true;
  }

  public async requestAuthorization(
    payload: Readonly<{ codeChallenge: string; state: string; clientId: string }>,
  ): Promise<BridgeAuthorizeResult> {
    const id = this.nextId();
    return this.run(id, buildAuthorizeScript(id, payload), REQUEST_TIMEOUT_MS);
  }

  /** 在网页里退出；网页窗口不在时直接返回 false，由调用方走原生兜底。 */
  public async logout(): Promise<boolean> {
    if (!this.inject) return false;
    const id = this.nextId();
    try {
      const result = await this.run(id, buildLogoutScript(id), 5_000);
      return result.httpStatus >= 200 && result.httpStatus < 300;
    } catch {
      return false;
    }
  }

  private nextId(): string {
    this.sequence += 1;
    return `b${Date.now().toString(36)}${this.sequence}`;
  }

  private async waitUntilAttached(): Promise<void> {
    if (this.inject) return;
    await new Promise<void>((resolve, reject) => {
      const timer = setTimeout(() => {
        this.readyWaiters = this.readyWaiters.filter((item) => item !== done);
        reject(new OrderingBridgeError("NOT_READY"));
      }, READY_TIMEOUT_MS);
      const done = () => {
        clearTimeout(timer);
        resolve();
      };
      this.readyWaiters.push(done);
    });
  }

  private async run(id: string, script: string, timeoutMs: number): Promise<BridgeAuthorizeResult> {
    await this.waitUntilAttached();
    return new Promise<BridgeAuthorizeResult>((resolve, reject) => {
      const timer = setTimeout(() => {
        this.pending.delete(id);
        reject(new OrderingBridgeError("TIMEOUT"));
      }, timeoutMs);
      this.pending.set(id, { resolve, reject, timer });
      try {
        this.inject?.(script);
      } catch {
        clearTimeout(timer);
        this.pending.delete(id);
        reject(new OrderingBridgeError("PAGE_ERROR"));
      }
    });
  }
}
