import {
  unwrapHbposEnvelope,
  type HbposEnvelope,
  type HbposTransport,
} from "@/core/api/hbpos-api";

/**
 * 大屏收银 App 内「订货助手」的令牌与请求。
 *
 * 中文注释：
 * 1. 令牌由 POS API 按设备身份签发（设备绑定哪家分店就是哪家），App 不需要登录 HB 网站账号。
 * 2. 令牌只留在 App 原生层：网页窗口里的插件脚本拿不到它，所有接口请求都由这里代发。
 * 3. 订货助手接口在中心（主 API）：/api/react/v1/browser-extension/*，中心地址由 POS API 一起下发。
 */
export type SupplierAssistantToken = Readonly<{
  accessToken: string;
  expiresAtMs: number;
  storeCode: string;
  deviceCode: string;
  apiOrigin: string;
}>;

export interface SupplierAssistantTokenSource {
  issueToken(signal?: AbortSignal): Promise<SupplierAssistantToken>;
}

type TokenResponseDto = Readonly<{
  accessToken?: string | null;
  expiresAtUtc?: string | null;
  storeCode?: string | null;
  deviceCode?: string | null;
  apiOrigin?: string | null;
}>;

export class SupplierAssistantTokenError extends Error {
  public constructor(
    message: string,
    public readonly code: string,
  ) {
    super(message);
    this.name = "SupplierAssistantTokenError";
  }
}

/** POS API：POST /api/v1/supplier-assistant/token（设备认证头由 transport 自动加）。 */
export class HbposSupplierAssistantApi implements SupplierAssistantTokenSource {
  public constructor(private readonly transport: HbposTransport) {}

  public async issueToken(signal?: AbortSignal): Promise<SupplierAssistantToken> {
    const response = await this.transport.request<HbposEnvelope<TokenResponseDto>>({
      method: "POST",
      url: "/api/v1/supplier-assistant/token",
      data: {},
      ...(signal ? { signal } : {}),
    });
    return parseSupplierAssistantToken(unwrapHbposEnvelope(response.data));
  }
}

export function parseSupplierAssistantToken(
  data: TokenResponseDto | null | undefined,
): SupplierAssistantToken {
  const accessToken = data?.accessToken?.trim() ?? "";
  const storeCode = data?.storeCode?.trim() ?? "";
  const deviceCode = data?.deviceCode?.trim() ?? "";
  const expiresAtMs = Date.parse(data?.expiresAtUtc ?? "");
  const apiOrigin = normalizeApiOrigin(data?.apiOrigin);
  if (!accessToken || !storeCode || !deviceCode || !Number.isFinite(expiresAtMs) || !apiOrigin) {
    throw new SupplierAssistantTokenError("订货助手令牌响应无效", "TOKEN_RESPONSE_INVALID");
  }
  return Object.freeze({ accessToken, expiresAtMs, storeCode, deviceCode, apiOrigin });
}

/** 只接受 https 的 origin（协议 + 主机 + 端口），其余一律拒绝，免得令牌被发到别处。 */
export function normalizeApiOrigin(value: string | null | undefined): string | null {
  const raw = value?.trim();
  if (!raw) return null;
  const match = /^https:\/\/([a-z0-9.-]+)(:\d{1,5})?\/?$/i.exec(raw);
  if (!match) return null;
  return `https://${match[1]!.toLowerCase()}${match[2] ?? ""}`;
}

export type AssistantApiResult = Readonly<{
  httpStatus: number;
  ok: boolean;
  success: boolean;
  data: unknown;
  message: string | null;
  errorCode: string | null;
}>;

export type FetchLike = (
  input: string,
  init: Readonly<{
    method: string;
    headers: Record<string, string>;
    body?: string;
    signal?: AbortSignal;
  }>,
) => Promise<Readonly<{ status: number; ok: boolean; json(): Promise<unknown> }>>;

// 令牌五分钟有效；剩不到一分钟就提前换，避免请求途中过期。
const REFRESH_MARGIN_MS = 60_000;
const EXTENSION_API_PREFIX = "/api/react/v1/browser-extension/";

export class SupplierAssistantSession {
  private token: SupplierAssistantToken | null = null;
  private inflight: Promise<SupplierAssistantToken> | null = null;

  public constructor(
    private readonly source: SupplierAssistantTokenSource,
    private readonly fetchImpl: FetchLike,
    private readonly extensionVersion: string,
    private readonly now: () => number = Date.now,
  ) {}

  /** 拿一个有效令牌；并发调用共用同一次签发。 */
  public async getToken(): Promise<SupplierAssistantToken> {
    if (this.token && this.token.expiresAtMs - this.now() > REFRESH_MARGIN_MS) {
      return this.token;
    }
    if (!this.inflight) {
      this.inflight = this.source
        .issueToken()
        .then((token) => {
          this.token = token;
          return token;
        })
        .finally(() => {
          this.inflight = null;
        });
    }
    return this.inflight;
  }

  public get storeCode(): string | null {
    return this.token?.storeCode ?? null;
  }

  /**
   * 代发订货助手接口请求。path 必须在 /api/react/v1/browser-extension/ 之下；
   * 令牌失效（401）时换一次令牌重试，仍失败就照实返回。
   */
  public async request(
    path: string,
    init: Readonly<{ method: "GET" | "POST"; body?: unknown; signal?: AbortSignal }>,
  ): Promise<AssistantApiResult> {
    if (!path.startsWith(EXTENSION_API_PREFIX) || path.includes("..")) {
      throw new SupplierAssistantTokenError("只允许访问订货助手接口", "PATH_NOT_ALLOWED");
    }
    let result = await this.send(await this.getToken(), path, init);
    if (result.httpStatus === 401) {
      this.token = null;
      result = await this.send(await this.getToken(), path, init);
    }
    return result;
  }

  private async send(
    token: SupplierAssistantToken,
    path: string,
    init: Readonly<{ method: "GET" | "POST"; body?: unknown; signal?: AbortSignal }>,
  ): Promise<AssistantApiResult> {
    const headers: Record<string, string> = {
      Authorization: `Bearer ${token.accessToken}`,
      Accept: "application/json",
      // 后端按这个版本过滤供应商配置，和插件 1.7.x 走同一套。
      "X-HB-Extension-Version": this.extensionVersion,
    };
    if (init.body !== undefined) headers["Content-Type"] = "application/json";
    const response = await this.fetchImpl(`${token.apiOrigin}${path}`, {
      method: init.method,
      headers,
      ...(init.body !== undefined ? { body: JSON.stringify(init.body) } : {}),
      ...(init.signal ? { signal: init.signal } : {}),
    });
    let body: unknown = null;
    try {
      body = await response.json();
    } catch {
      // 非 JSON 响应按业务失败处理
    }
    const envelope = (body && typeof body === "object" ? body : {}) as Record<string, unknown>;
    return Object.freeze({
      httpStatus: response.status,
      ok: response.ok,
      success: envelope.success === true,
      data: envelope.data ?? null,
      message: typeof envelope.message === "string" ? envelope.message : null,
      errorCode: typeof envelope.errorCode === "string" ? envelope.errorCode : null,
    });
  }
}
