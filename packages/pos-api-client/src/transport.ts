export * from "./transport-contracts";
export { isDeviceRevocationCode } from "./core/api/forbidden-response";
import { AxiosError, isAxiosError, type AxiosInstance, type AxiosRequestConfig, type InternalAxiosRequestConfig } from "axios";

import { isDeviceRevocationCode } from "./core/api/forbidden-response";
import { HbposApiError, type HbposTransport, type HbposTransportRequest, type HbposTransportResponse } from "./transport-contracts";

export type HbposRequestCredentials = Readonly<{
  device?: Readonly<{
    authorizationCode: string;
    deviceCode: string;
    storeCode: string;
    hardwareId: string;
  }>;
  cashierAuthorization?: string;
}>;

export interface HbposRequestCredentialProvider {
  getCredentials(): Promise<HbposRequestCredentials>;
}

/** 认证失败只驱动本地安全状态变化，HTTP 错误仍由调用方按原语义处理。 */
export interface HbposAuthenticationFailureHandler {
  onUnauthorized(): Promise<void>;
  /** 仅由明确的设备撤销错误码触发，普通 403 不得持久锁机。 */
  onForbidden(): Promise<void>;
}

export type HbposTransportPolicy = Readonly<{
  cashierHeader: "preserve-explicit" | "override-explicit";
  responseHeaders: "omit" | "normalize";
}>;

export function createAxiosHbposTransport(
  baseUrl: string,
  credentialProvider: HbposRequestCredentialProvider,
  instance: AxiosInstance,
  authenticationFailureHandler: HbposAuthenticationFailureHandler | undefined,
  policy: HbposTransportPolicy,
): HbposTransport {
  const trustedOrigin = new URL(baseUrl).origin;
  instance.interceptors.request.use(async (config: InternalAxiosRequestConfig) => {
    // 有限正超时从进入 interceptor 起只计算一次，凭据读取和 HTTP 派发共用同一预算。
    const timeoutDeadline = freezeRequestTimeoutDeadline(config.timeout);
    const requestUrl = new URL(
      config.url ?? "",
      config.baseURL ?? baseUrl,
    );
    if (requestUrl.origin !== trustedOrigin) {
      throw new HbposApiError(
        "Hbpos request origin is not trusted.",
        { kind: "transport", code: "UNTRUSTED_API_ORIGIN" },
      );
    }
    const credentials = await getRequestCredentials(credentialProvider, config, timeoutDeadline);
    const remainingTimeout = getRemainingTimeoutMs(timeoutDeadline);
    if (timeoutDeadline !== undefined) {
      if (remainingTimeout === undefined) {
        throw createRequestTimeoutError(config);
      }
      // 适配器只能获得尚未消耗的整数毫秒，不能重新获得完整 timeout 预算。
      config.timeout = remainingTimeout;
    }
    config.headers.set("Accept", "application/json");
    if (credentials.device) {
      config.headers.set("Authorization", `Bearer ${credentials.device.authorizationCode}`);
      config.headers.set("X-HBPOS-Device-Code", credentials.device.deviceCode);
      config.headers.set("X-HBPOS-Store-Code", credentials.device.storeCode);
      config.headers.set("X-HBPOS-Hardware-Id", credentials.device.hardwareId);
    }
    if (
      credentials.cashierAuthorization &&
      (policy.cashierHeader === "override-explicit" ||
        !config.headers.has("X-HBPOS-Cashier-Authorization"))
    ) {
      config.headers.set("X-HBPOS-Cashier-Authorization", credentials.cashierAuthorization);
    }
    return config;
  });

  return {
    async request<T>(request: HbposTransportRequest): Promise<HbposTransportResponse<T>> {
      try {
        const response = await instance.request<T>(toAxiosRequest(request));
        const base = { status: response.status, data: response.data };
        return policy.responseHeaders === "normalize"
          ? { ...base, headers: normalizeResponseHeaders(response.headers) }
          : base;
      } catch (error: unknown) {
        if (error instanceof HbposApiError) {
          throw error;
        }
        if (!isAxiosError(error)) {
          // 非 axios 异常（如 interceptor 凭证读取失败等内部故障）：抛独立错误码，
          // 避免被上层误判为“网络不可用”；文案保持中性（不暗示网络故障）。
          throw new HbposApiError("请求未能完成，请重试。", {
            kind: "transport",
            code: "TRANSPORT_UNEXPECTED",
          });
        }

        if (error.code === "ERR_CANCELED") {
          throw new HbposApiError("Hbpos request was cancelled.", {
            kind: "transport",
            code: "REQUEST_ABORTED",
          });
        }
        const payload = error.response?.data as { errorCode?: string; message?: string } | undefined;
        if (!error.response) {
          // 无 HTTP 响应（网络断开 / 连接拒绝 / 超时）：按底层错误码给出可读提示，
          // 并把原始网络错误码交给上层（UI 可据此展示更精确的引导文案）。
          throw new HbposApiError(transportFailureMessage(error.code), {
            kind: "transport",
            code: "NO_HTTP_RESPONSE",
            // exactOptionalPropertyTypes 下需条件展开，避免 undefined 赋给可选字段。
            ...(error.code ? { networkCode: error.code } : {}),
          });
        }
        const code = payload?.errorCode;
        const apiError = new HbposApiError(
          payload?.message ?? error.message ?? "Hbpos API request failed.",
          code
            ? { kind: "http", status: error.response.status, code }
            : { kind: "http", status: error.response.status }
        );
        if (
          (apiError.status === 401 || apiError.status === 403) &&
          isDeviceRevocationCode(apiError.code)
        ) {
          await notifyAuthenticationFailure(authenticationFailureHandler?.onForbidden);
        } else if (apiError.status === 401 && !suppressesCashierLoginFailure(request, apiError)) {
          await notifyAuthenticationFailure(authenticationFailureHandler?.onUnauthorized);
        }
        throw apiError;
      }
    }
  };
}

function normalizeResponseHeaders(
  input: unknown,
): Readonly<Record<string, string>> {
  if (!input || typeof input !== "object" || Array.isArray(input)) {
    return Object.freeze({});
  }
  const candidate = input as Readonly<{ toJSON?: () => unknown }>;
  const source = typeof candidate.toJSON === "function"
    ? candidate.toJSON()
    : input;
  if (!source || typeof source !== "object" || Array.isArray(source)) {
    return Object.freeze({});
  }

  const normalized: Record<string, string> = {};
  for (const [rawName, rawValue] of Object.entries(source)) {
    if (rawValue === null || rawValue === undefined) continue;
    const name = rawName.trim().toLowerCase();
    if (!name) continue;
    normalized[name] = Array.isArray(rawValue)
      ? rawValue.map(String).join(", ")
      : String(rawValue);
  }
  return Object.freeze(normalized);
}

function getRequestCredentials(
  credentialProvider: HbposRequestCredentialProvider,
  config: InternalAxiosRequestConfig,
  timeoutDeadline: number | undefined,
): Promise<HbposRequestCredentials> {
  const { signal } = config;
  // 已取消的请求不能再触碰 Keychain，避免恢复流程已结束后仍启动凭据读取。
  if (signal?.aborted) {
    return Promise.reject(new AxiosError("Hbpos request was cancelled.", "ERR_CANCELED", config));
  }
  if (!signal && timeoutDeadline === undefined) {
    return credentialProvider.getCredentials();
  }

  return new Promise((resolve, reject) => {
    let settled = false;
    let timer: ReturnType<typeof setTimeout> | undefined;
    const onAbort = () => {
      settleReject(new AxiosError("Hbpos request was cancelled.", "ERR_CANCELED", config));
    };
    const cleanup = () => {
      signal?.removeEventListener?.("abort", onAbort);
      if (timer !== undefined) {
        clearTimeout(timer);
        timer = undefined;
      }
    };
    const settleResolve = (credentials: HbposRequestCredentials) => {
      if (settled) {
        return;
      }
      settled = true;
      cleanup();
      resolve(credentials);
    };
    const settleReject = (error: unknown) => {
      if (settled) {
        return;
      }
      settled = true;
      cleanup();
      reject(error);
    };

    signal?.addEventListener?.("abort", onAbort, { once: true });
    // 注册监听后再次检查，覆盖注册期间发生取消的竞态，且仍不读取凭据。
    if (signal?.aborted) {
      onAbort();
      return;
    }
    const remainingTimeout = getRemainingTimeoutMs(timeoutDeadline);
    if (timeoutDeadline !== undefined) {
      if (remainingTimeout === undefined) {
        settleReject(createRequestTimeoutError(config));
        return;
      }
      // Axios 默认使用 ECONNABORTED 表示超时，复用既有无 HTTP 响应映射。
      timer = setTimeout(() => {
        settleReject(createRequestTimeoutError(config));
      }, remainingTimeout);
    }

    try {
      // 始终登记成功与失败处理：race 已结束后凭据晚到不会未处理，也不会继续派发请求。
      credentialProvider.getCredentials().then((credentials) => {
        if (timeoutDeadline !== undefined && getRemainingTimeoutMs(timeoutDeadline) === undefined) {
          settleReject(createRequestTimeoutError(config));
          return;
        }
        settleResolve(credentials);
      }, settleReject);
    } catch (error) {
      settleReject(error);
    }
  });
}

function freezeRequestTimeoutDeadline(timeout: unknown): number | undefined {
  if (typeof timeout !== "number" || !Number.isFinite(timeout) || timeout <= 0) {
    return undefined;
  }
  return Date.now() + timeout;
}

function getRemainingTimeoutMs(timeoutDeadline: number | undefined): number | undefined {
  if (timeoutDeadline === undefined) {
    return undefined;
  }
  const remaining = timeoutDeadline - Date.now();
  // Date.now 精度为毫秒；只要绝对 deadline 尚未到达，就至少交给下游 1ms。
  return remaining > 0 ? Math.ceil(remaining) : undefined;
}

function createRequestTimeoutError(config: InternalAxiosRequestConfig): AxiosError {
  return new AxiosError("Hbpos request timed out.", "ECONNABORTED", config);
}

function suppressesCashierLoginFailure(
  request: HbposTransportRequest,
  error: HbposApiError,
): boolean {
  // 条码不存在是当前输入的业务拒绝；任何无错误码或其他 401 均可能代表设备/会话失效，必须清理。
  return request.authenticationFailurePolicy === "suppress-unauthorized"
    && error.code === "CASHIER_LOGIN_FAILED";
}

async function notifyAuthenticationFailure(action: (() => Promise<void>) | undefined): Promise<void> {
  try {
    await action?.();
  } catch {
    // Keychain 故障不能把原始 401/403 覆盖为本地错误；调用方仍需按服务器拒绝停止交易。
  }
}

/**
 * 把底层网络错误码映射为收银员可读的中文提示。
 * - ERR_NETWORK：设备网络断开（无网络 / Wi-Fi 异常）
 * - ECONNREFUSED：服务器未启动或端口不可达
 * - ETIMEDOUT / ECONNABORTED：请求超时（网络缓慢或服务器无响应）
 * - 其他未知码：统一给出“检查网络”的通用引导
 */
function transportFailureMessage(networkCode: string | undefined): string {
  switch (networkCode) {
    case "ERR_NETWORK":
      return "网络连接失败，请检查设备网络连接。";
    case "ECONNREFUSED":
      return "无法连接服务器，请确认服务器已启动。";
    case "ETIMEDOUT":
    case "ECONNABORTED":
      return "连接服务器超时，请检查网络或稍后重试。";
    default:
      return "无法连接服务器，请检查网络后重试。";
  }
}

function toAxiosRequest(request: HbposTransportRequest): AxiosRequestConfig {
  const acceptedStatuses = new Set(request.acceptedStatuses ?? []);
  return {
    method: request.method,
    url: request.url,
    ...("data" in request ? { data: request.data } : {}),
    ...(request.params ? { params: request.params } : {}),
    ...(request.headers ? { headers: request.headers } : {}),
    ...(request.signal ? { signal: request.signal } : {}),
    ...(request.timeoutMs === undefined ? {} : { timeout: request.timeoutMs }),
    ...(request.responseType === "arraybuffer" ? { responseType: "arraybuffer" as const } : {}),
    ...(acceptedStatuses.size > 0
      ? {
          validateStatus: (status: number) =>
            (status >= 200 && status < 300) ||
            acceptedStatuses.has(status),
        }
      : {}),
  };
}
