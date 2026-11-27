/**
 * 员工移动端「订货助手」的接口代发。
 *
 * 中文注释：
 * 1. 与大屏收银 App 不同：员工已经用自己的账号登录，订货助手接口（/api/react/v1/browser-extension/*）
 *    直接认员工登录令牌，后端 BrowserExtensionAccessService 按「管理员 / OrderFront.View / 可管理分店」放行，
 *    不需要再换设备令牌或插件短期令牌。
 * 2. 令牌只在 App 原生层：所有请求经 apiClient 代发（自动带令牌、401 自动刷新、线路故障自动换入口），
 *    网页窗口里的插件脚本只能发消息，拿不到令牌。
 * 3. 只允许订货助手接口这一个前缀，免得网页借道访问员工账号的其他接口。
 * 4. 本文件不 import React Native，便于 tsx 直接测；真正的 apiClient 由页面注入。
 */
export type AssistantApiResult = Readonly<{
  httpStatus: number;
  ok: boolean;
  success: boolean;
  data: unknown;
  message: string | null;
  errorCode: string | null;
}>;

export type SupplierAssistantHttpRequest = Readonly<{
  method: "GET" | "POST";
  /** apiClient 的相对路径（baseURL 已含 /api）。 */
  url: string;
  data?: unknown;
  headers: Record<string, string>;
  signal?: AbortSignal;
}>;

/** 与 axios 实例的 request 同形；失败时抛出带 response 的错误（AxiosError）。 */
export type SupplierAssistantHttp = (
  request: SupplierAssistantHttpRequest,
) => Promise<Readonly<{ status: number; data: unknown }>>;

export type SupplierAssistantRequest = (
  path: string,
  init: Readonly<{ method: "GET" | "POST"; body?: unknown; signal?: AbortSignal }>,
) => Promise<AssistantApiResult>;

export class SupplierAssistantRequestError extends Error {
  public constructor(
    message: string,
    public readonly code: string,
  ) {
    super(message);
    this.name = "SupplierAssistantRequestError";
  }
}

export const SUPPLIER_ASSISTANT_API_PREFIX = "/api/react/v1/browser-extension/";
// apiClient 的 baseURL 以 /api 结尾，代发时去掉这一段。
const API_BASE_SEGMENT = "/api";

export function createSupplierAssistantRequest(
  http: SupplierAssistantHttp,
  extensionVersion: string,
): SupplierAssistantRequest {
  return async (path, init) => {
    if (!isAllowedSupplierAssistantPath(path)) {
      throw new SupplierAssistantRequestError("只允许访问订货助手接口", "PATH_NOT_ALLOWED");
    }
    const headers: Record<string, string> = {
      Accept: "application/json",
      // 后端按这个版本过滤供应商配置，和插件 1.7.x、大屏收银 App 走同一套。
      "X-HB-Extension-Version": extensionVersion,
    };
    try {
      const response = await http({
        method: init.method,
        url: path.slice(API_BASE_SEGMENT.length),
        headers,
        ...(init.body !== undefined ? { data: init.body } : {}),
        ...(init.signal ? { signal: init.signal } : {}),
      });
      return toAssistantApiResult(response.status, response.data);
    } catch (error) {
      // 有 HTTP 响应（403 / 429 / 500 等）照实交给插件脚本处理；断网等没有响应的错误继续往外抛。
      const response = readErrorResponse(error);
      if (response) return toAssistantApiResult(response.status, response.data);
      throw error;
    }
  };
}

export function isAllowedSupplierAssistantPath(path: string): boolean {
  return (
    typeof path === "string"
    && path.startsWith(SUPPLIER_ASSISTANT_API_PREFIX)
    && !path.includes("..")
    && !path.includes("//", SUPPLIER_ASSISTANT_API_PREFIX.length - 1)
    && !/[\s?#\\]/.test(path)
  );
}

export function toAssistantApiResult(status: number, body: unknown): AssistantApiResult {
  const envelope = (body && typeof body === "object" ? body : {}) as Record<string, unknown>;
  return Object.freeze({
    httpStatus: status,
    ok: status >= 200 && status < 300,
    success: envelope.success === true,
    data: envelope.data ?? null,
    message: typeof envelope.message === "string" ? envelope.message : null,
    errorCode: typeof envelope.errorCode === "string" ? envelope.errorCode : null,
  });
}

function readErrorResponse(error: unknown): Readonly<{ status: number; data: unknown }> | null {
  if (!error || typeof error !== "object") return null;
  // 账号切换中断（ACCOUNT_SESSION_CHANGED）的错误把原始 AxiosError 放在 cause 里，也不当成接口响应。
  const response = (error as { response?: unknown }).response;
  if (!response || typeof response !== "object") return null;
  const status = (response as { status?: unknown }).status;
  if (typeof status !== "number") return null;
  return Object.freeze({ status, data: (response as { data?: unknown }).data ?? null });
}
