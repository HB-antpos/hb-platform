import { ORDERING_EXTENSION_CLIENT_ID, ORDERING_WEB_ORIGIN } from "./ordering-config";
import type { BridgeAuthorizeResult } from "./ordering-web-bridge";

import {
  SupplierAssistantTokenError,
  type SupplierAssistantToken,
  type SupplierAssistantTokenSource,
} from "@/features/supplier-assistant/supplier-assistant-session";

/**
 * 订货模式下「供应商订货助手」的令牌来源：员工网页会话 → 一次性授权码（PKCE）→ 插件范围短期令牌。
 *
 * 中文注释：
 * 1. 与浏览器订货插件走同一套后端接口（/api/Auth/extension/authorize + /extension/token），后端不需要新接口。
 * 2. verifier 在 App 原生层生成并保管，网页只拿到 challenge、只交回授权码。
 * 3. 换来的令牌 token_use=browser_extension，只能调 /api/react/v1/browser-extension/*。
 */
export type OrderingCrypto = Readonly<{
  randomBytes(length: number): Uint8Array;
  /** SHA-256，返回标准 base64。 */
  sha256Base64(text: string): Promise<string>;
}>;

export type OrderingAuthorizer = Readonly<{
  requestAuthorization(
    payload: Readonly<{ codeChallenge: string; state: string; clientId: string }>,
  ): Promise<BridgeAuthorizeResult>;
}>;

type ExchangeFetch = (
  input: string,
  init: Readonly<{ method: "POST"; headers: Record<string, string>; body: string }>,
) => Promise<Readonly<{ status: number; ok: boolean; json(): Promise<unknown> }>>;

/** 后端说「网页会话不在了」的错误码：重试没用，要重新登录。 */
const SESSION_GONE_CODES = new Set(["EXTENSION_AUTH_COOKIE_REQUIRED", "EXTENSION_GRANT_INVALID"]);
/** 令牌到期时间解析不准（时区等）时的上限：最多当它 10 分钟有效，到点提前换。 */
const MAX_ASSUMED_LIFETIME_MS = 10 * 60_000;

const BASE64_ALPHABET = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";

/** 字节转 base64url（无补位）。 */
export function bytesToBase64Url(bytes: Uint8Array): string {
  let output = "";
  for (let index = 0; index < bytes.length; index += 3) {
    const a = bytes[index]!;
    const b = bytes[index + 1];
    const c = bytes[index + 2];
    output += BASE64_ALPHABET[a >> 2];
    output += BASE64_ALPHABET[((a & 3) << 4) | ((b ?? 0) >> 4)];
    if (b !== undefined) output += BASE64_ALPHABET[((b & 15) << 2) | ((c ?? 0) >> 6)];
    if (c !== undefined) output += BASE64_ALPHABET[c & 63];
  }
  return toBase64Url(output);
}

export function toBase64Url(base64: string): string {
  return base64.replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/, "");
}

function envelopeOf(body: unknown): Record<string, unknown> {
  return body && typeof body === "object" ? (body as Record<string, unknown>) : {};
}

export class OrderingSupplierTokenSource implements SupplierAssistantTokenSource {
  public constructor(
    private readonly authorizer: OrderingAuthorizer,
    private readonly crypto: OrderingCrypto,
    private readonly fetchImpl: ExchangeFetch = (input, init) => fetch(input, init),
    private readonly now: () => number = Date.now,
  ) {}

  public async issueToken(): Promise<SupplierAssistantToken> {
    // 32 字节 → 43 位 verifier；16 字节 → 22 位 state，都落在后端校验的长度范围内。
    const codeVerifier = bytesToBase64Url(this.crypto.randomBytes(32));
    const codeChallenge = toBase64Url(await this.crypto.sha256Base64(codeVerifier));
    const state = bytesToBase64Url(this.crypto.randomBytes(16));

    let authorized: BridgeAuthorizeResult;
    try {
      authorized = await this.authorizer.requestAuthorization({
        codeChallenge,
        state,
        clientId: ORDERING_EXTENSION_CLIENT_ID,
      });
    } catch {
      throw new SupplierAssistantTokenError("仓库订货网页还没准备好", "BRIDGE_UNAVAILABLE");
    }
    if (authorized.httpStatus === 401) {
      throw new SupplierAssistantTokenError("网页登录已失效", "SESSION_EXPIRED");
    }
    const authorizeEnvelope = envelopeOf(authorized.body);
    if (authorizeEnvelope.success !== true) {
      const code = typeof authorizeEnvelope.errorCode === "string" ? authorizeEnvelope.errorCode : "";
      throw new SupplierAssistantTokenError(
        "申请订货助手授权失败",
        SESSION_GONE_CODES.has(code) ? "SESSION_EXPIRED" : "AUTHORIZE_FAILED",
      );
    }
    const grant = envelopeOf(authorizeEnvelope.data);
    const code = typeof grant.code === "string" ? grant.code : "";
    if (!code || grant.state !== state) {
      throw new SupplierAssistantTokenError("授权码无效", "AUTHORIZE_FAILED");
    }

    let response;
    try {
      response = await this.fetchImpl(`${ORDERING_WEB_ORIGIN}/api/Auth/extension/token`, {
        method: "POST",
        headers: { Accept: "application/json", "Content-Type": "application/json" },
        body: JSON.stringify({ code, codeVerifier, state, clientId: ORDERING_EXTENSION_CLIENT_ID }),
      });
    } catch {
      throw new SupplierAssistantTokenError("换取订货助手令牌失败", "EXCHANGE_FAILED");
    }
    let body: unknown = null;
    try {
      body = await response.json();
    } catch {
      // 非 JSON 按失败处理
    }
    const exchangeEnvelope = envelopeOf(body);
    const data = envelopeOf(exchangeEnvelope.data);
    const accessToken = typeof data.accessToken === "string" ? data.accessToken : "";
    if (exchangeEnvelope.success !== true || !accessToken) {
      throw new SupplierAssistantTokenError("换取订货助手令牌失败", "EXCHANGE_FAILED");
    }
    const now = this.now();
    const parsedExpiry = Date.parse(typeof data.accessTokenExpiry === "string" ? data.accessTokenExpiry : "");
    const expiresAtMs = Number.isFinite(parsedExpiry)
      ? Math.min(parsedExpiry, now + MAX_ASSUMED_LIFETIME_MS)
      : now + MAX_ASSUMED_LIFETIME_MS;
    return Object.freeze({ accessToken, expiresAtMs, apiOrigin: ORDERING_WEB_ORIGIN });
  }
}
