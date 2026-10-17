import axios, { AxiosError, InternalAxiosRequestConfig } from "axios";
import { router } from "expo-router";
import { SecureStorage } from "@/shared/storage/secure";
import { DeviceStorage } from "@/modules/device/storage";
import { emitUnauthenticatedSession } from "@/modules/auth/auth-session-events";
import { stopAttendanceLocationTracking } from "@/modules/attendance/location-tracking-control";
import { isUnauthenticatedApiPayload } from "@/shared/api/auth-error";
import { buildApiBaseUrl, DEFAULT_API_BASE_URL, getStoredApiHost } from "@/shared/api/config";
import { extractApiErrorMessage } from "@/shared/api/error-message";
import { unwrapApiEnvelope } from "@/shared/api/api-envelope";
import { preserveApiClientError } from "@/shared/api/client-error";
import { isLogCenterIngestUrl } from "@/shared/logging/log-center";
import { reportApplicationLog } from "@/shared/logging/log-center-runtime";
import {
  iosReviewAxiosAdapter,
} from "@/modules/ios-review/transport";
import { isIosReviewSessionActive } from "@/modules/ios-review/session";
import {
  clearAuthSessionMarker,
  getAuthSessionMarker,
} from "@/modules/device-activation/auth-session-marker";
import { DeviceAccountStorage } from "@/modules/device-activation/device-account-storage-runtime";
import { exchangeStoredDeviceAccountToken } from "@/modules/device-activation/device-account-token";
import {
  deriveEffectiveAuthSessionKind,
  isRelativeApiClientUrl,
  removeRequestHeader,
  resolveDeviceAccountRequestPolicy,
} from "@/modules/device-activation/device-account-request-policy";
import { EXPECTED_ACCOUNT_HEADER } from "@/shared/api/account-bound-request-header";
import { toByteArray } from "base64-js";
import {
  getAccountSessionGeneration,
  isAccountSwitchInProgress,
  isStaleSessionGeneration,
  waitForAccountSwitchIdle,
} from "@/modules/account-switch/account-switch-gate";

/** 账号切换期间被打断 / 属于旧账号的请求统一用这个错误结束：不刷新令牌、不触发登出。 */
function createAccountSessionChangedError() {
  return Object.assign(new Error("ACCOUNT_SESSION_CHANGED"), { code: "ACCOUNT_SESSION_CHANGED" });
}

export const apiClient = axios.create({
  baseURL: DEFAULT_API_BASE_URL,
  timeout: 30000,
  headers: { "Content-Type": "application/json" },
});

async function syncApiBaseUrl() {
  const host = await getStoredApiHost();
  const baseURL = buildApiBaseUrl(host);
  apiClient.defaults.baseURL = baseURL;
  return baseURL;
}

let isRefreshing = false;
// 当前这一轮令牌刷新的完成信号：账号切换要等它收尾，才能读到当前账号最新的刷新令牌。
let refreshSettled: Promise<void> | null = null;
let releaseRefreshSettled: (() => void) | null = null;

/** 等正在进行的令牌刷新结束（没有刷新时立即返回）。 */
export async function waitForTokenRefreshIdle(): Promise<void> {
  while (refreshSettled) {
    await refreshSettled;
  }
}
let isRedirectingToLogin = false;
let refreshQueue: Array<{
  resolve: (t: string) => void;
  reject: (e: Error) => void;
}> = [];

function isAuthenticationRequest(config?: InternalAxiosRequestConfig | null) {
  const url = config?.url ?? "";
  return (
    url.includes("/auth/login") ||
    url.includes("/auth/refresh") ||
    url.includes("/mobile/v1/device-session/exchange")
  );
}

function shouldSkipAuthRedirect(config?: InternalAxiosRequestConfig | null) {
  if (!config) {
    return false;
  }

  const rawSkipHeader = config.headers?.["X-Skip-Auth-Redirect"];
  const skipHeaderValue = Array.isArray(rawSkipHeader) ? rawSkipHeader[0] : rawSkipHeader;
  return skipHeaderValue === "1";
}

function shouldSkipAuthRecovery(config?: InternalAxiosRequestConfig | null) {
  if (!config) {
    return false;
  }
  const rawValue = config.headers?.["X-Skip-Auth-Recovery"];
  const value = Array.isArray(rawValue) ? rawValue[0] : rawValue;
  return value === "1";
}

function shouldSkipCenterLog(config?: InternalAxiosRequestConfig | null) {
  if (!config) {
    return false;
  }

  const rawSkipHeader = config.headers?.["X-Skip-Center-Log"];
  const skipHeaderValue = Array.isArray(rawSkipHeader) ? rawSkipHeader[0] : rawSkipHeader;
  return skipHeaderValue === "1" || isLogCenterIngestUrl(resolveRequestUrl(config));
}

function resolveRequestUrl(config?: InternalAxiosRequestConfig | null) {
  if (!config?.url) {
    return "";
  }

  if (/^https?:\/\//i.test(config.url)) {
    return config.url;
  }

  const baseURL = config.baseURL ?? apiClient.defaults.baseURL ?? DEFAULT_API_BASE_URL;
  try {
    return new URL(config.url, baseURL).toString();
  } catch {
    return `${baseURL.replace(/\/+$/, "")}/${config.url.replace(/^\/+/, "")}`;
  }
}

function reportApiErrorLog(
  error: unknown,
  config?: InternalAxiosRequestConfig | null,
  options?: {
    responseStatus?: number;
    responseData?: unknown;
    message?: string;
  }
) {
  if (shouldSkipCenterLog(config)) {
    return;
  }

  const responseStatus = options?.responseStatus;
  const retryableConfig = config as (InternalAxiosRequestConfig & { _retry?: boolean }) | null | undefined;
  if (responseStatus === 401 && !retryableConfig?._retry) {
    return;
  }

  const normalizedError = error instanceof Error ? error : new Error(String(error));
  const requestUrl = resolveRequestUrl(config);
  let requestPath = config?.url ?? "";

  if (requestUrl) {
    try {
      requestPath = new URL(requestUrl).pathname;
    } catch {
      requestPath = config?.url ?? requestUrl;
    }
  }

  reportApplicationLog({
    level: responseStatus && responseStatus >= 500 ? "Error" : "Warning",
    message: options?.message ?? "移动端 API 请求失败",
    sourceType: "mobile.api",
    requestPath: requestPath || undefined,
    requestMethod: config?.method?.toUpperCase(),
    statusCode: responseStatus,
    exceptionType: normalizedError.name,
    exceptionMessage: normalizedError.message,
    stackTrace: normalizedError.stack,
    properties: {
      url: requestUrl || undefined,
      axiosCode: (error as { code?: unknown } | undefined)?.code,
      responseData: options?.responseData,
      hasResponse: responseStatus != null,
    },
  });
}

async function invalidateLocalSession(message?: string) {
  // 401/会话失效时必须直接停掉班中后台定位；不能只依赖 UI store 订阅者。
  await stopAttendanceLocationTracking().catch((error) => {
    console.warn("[attendance-location] 会话失效时停止后台定位失败", error);
  });
  await SecureStorage.clearAll();
  await clearAuthSessionMarker();
  emitUnauthenticatedSession({ message });
}

async function redirectToLoginAfterUnauthenticated(message?: string) {
  if (isRedirectingToLogin) {
    return;
  }

  isRedirectingToLogin = true;
  try {
    await invalidateLocalSession(message);
    router.replace("/(auth)/login");
  } finally {
    isRedirectingToLogin = false;
  }
}

apiClient.interceptors.request.use(
  async (config: InternalAxiosRequestConfig) => {
    const guardedConfig = config as InternalAxiosRequestConfig & {
      _expectedAccountGuid?: string;
      _sessionGeneration?: number;
    };
    // 账号切换期间暂停发请求，等新会话就位再发；会话代号在放行后盖章，
    // 重试沿用首次的代号——这样「旧账号发出的请求」不会被换上新账号的令牌重放。
    await waitForAccountSwitchIdle();
    const sessionStamp = guardedConfig._sessionGeneration ?? getAccountSessionGeneration();
    guardedConfig._sessionGeneration = sessionStamp;
    const rawExpectedAccountGuid = config.headers?.[EXPECTED_ACCOUNT_HEADER];
    const headerExpectedAccountGuid = Array.isArray(rawExpectedAccountGuid)
      ? rawExpectedAccountGuid[0]
      : rawExpectedAccountGuid;
    if (typeof headerExpectedAccountGuid === "string" && headerExpectedAccountGuid.trim()) {
      guardedConfig._expectedAccountGuid = headerExpectedAccountGuid.trim();
    }
    // 内部 metadata 会随 401 重试保留；header 在首次读取后立即删除，绝不发送给服务端。
    const expectedAccountGuid = guardedConfig._expectedAccountGuid;
    if (config.headers) removeRequestHeader(config.headers, EXPECTED_ACCOUNT_HEADER);

    if (isIosReviewSessionActive()) {
      if (expectedAccountGuid) {
        throw Object.assign(new Error("ACCOUNT_SESSION_CHANGED"), { code: "ACCOUNT_SESSION_CHANGED" });
      }
      // 关键位置：审核会话在任何 base URL、token、设备认证读取之前强制切到本地 adapter。
      config.adapter = iosReviewAxiosAdapter;
      config.baseURL = undefined;
      if (config.headers) {
        removeRequestHeader(config.headers, "Authorization");
        removeRequestHeader(config.headers, "X-Device-Id");
        removeRequestHeader(config.headers, "X-Auth-Code");
      }
      return config;
    }

    if (!isRelativeApiClientUrl(config.url)) {
      throw new Error("ABSOLUTE_API_URL_NOT_ALLOWED");
    }

    const rawApiHost = config.headers?.["X-Client-Api-Host"];
    const apiHost = Array.isArray(rawApiHost) ? rawApiHost[0] : rawApiHost;
    const rawSkipAuthentication = config.headers?.["X-Client-Skip-Authentication"];
    const skipAuthentication =
      (Array.isArray(rawSkipAuthentication)
        ? rawSkipAuthentication[0]
        : rawSkipAuthentication) === "1";
    if (config.headers) {
      removeRequestHeader(config.headers, "X-Client-Api-Host");
      removeRequestHeader(config.headers, "X-Client-Skip-Authentication");
    }
    const requestedApiHost =
      typeof apiHost === "string" && apiHost
        ? apiHost
        : await getStoredApiHost();

    if (skipAuthentication) {
      const requestPolicy = resolveDeviceAccountRequestPolicy({
        requestedApiHost,
        bindingApiHost: null,
        sessionKind: null,
        skipAuthentication: true,
      });
      config.baseURL = buildApiBaseUrl(requestPolicy.apiHost);
      if (config.headers) {
        removeRequestHeader(config.headers, "Authorization");
        removeRequestHeader(config.headers, "X-Device-Id");
        removeRequestHeader(config.headers, "X-Auth-Code");
      }
      return config;
    }

    const suppliedDeviceId = config.headers?.get("X-Device-Id");
    const suppliedAuthCode = config.headers?.get("X-Auth-Code");
    const deviceSessionPrefetch = config.url === "/react/v1/store-product-maintenance/scan-label"
      && typeof suppliedDeviceId === "string" && Boolean(suppliedDeviceId)
      && typeof suppliedAuthCode === "string" && Boolean(suppliedAuthCode)
      ? Promise.resolve().then(() => DeviceStorage.peekSession()).then(
          (value) => ({ ok: true as const, value }),
          (error: unknown) => ({ ok: false as const, error }),
        )
      : null;
    // 预读只触碰只读存储；拒绝也立即收敛，后续 policy 不需要设备会话时不会留下未处理异常。

    const [token, refreshToken, persistedSessionKind, accountBinding] = await Promise.all([
      SecureStorage.getToken(),
      SecureStorage.getRefreshToken(),
      getAuthSessionMarker(),
      DeviceAccountStorage.loadBinding().catch(() => null),
    ]);

    // 存储读取期间用户可能切换到审核会话；账号绑定请求必须在异步边界后再次确认。
    if (expectedAccountGuid && isIosReviewSessionActive()) {
      throw Object.assign(new Error("ACCOUNT_SESSION_CHANGED"), { code: "ACCOUNT_SESSION_CHANGED" });
    }
    if (typeof expectedAccountGuid === "string" && expectedAccountGuid.trim()) {
      const tokenParts = token?.split(".") ?? [];
      let tokenSubject = "";
      try {
        const encoded = (tokenParts[1] ?? "").replace(/-/g, "+").replace(/_/g, "/");
        const padded = encoded.padEnd(Math.ceil(encoded.length / 4) * 4, "=");
        const payload = JSON.parse(new TextDecoder().decode(toByteArray(padded))) as Record<string, unknown>;
        tokenSubject = typeof payload.sub === "string" ? payload.sub : "";
      } catch {
        tokenSubject = "";
      }
      if (!tokenSubject || tokenSubject.toLowerCase() !== expectedAccountGuid.trim().toLowerCase()) {
        throw Object.assign(new Error("ACCOUNT_SESSION_CHANGED"), { code: "ACCOUNT_SESSION_CHANGED" });
      }
    }
    const sessionKind = deriveEffectiveAuthSessionKind({
      persistedKind: persistedSessionKind,
      hasAccessToken: Boolean(token),
      hasRefreshToken: Boolean(refreshToken),
      hasBinding: Boolean(accountBinding),
    });
    if (expectedAccountGuid && sessionKind !== "account") {
      throw Object.assign(new Error("ACCOUNT_SESSION_CHANGED"), { code: "ACCOUNT_SESSION_CHANGED" });
    }
    const requestPolicy = resolveDeviceAccountRequestPolicy({
      requestedApiHost,
      bindingApiHost: accountBinding?.apiHost,
      sessionKind,
      skipAuthentication: false,
    });
    config.baseURL = buildApiBaseUrl(requestPolicy.apiHost);
    if (!apiHost && requestPolicy.apiHost === requestedApiHost) {
      apiClient.defaults.baseURL = config.baseURL;
    }

    if (!requestPolicy.allowDeviceHeaders && config.headers) {
      // 即使调用方显式传入，绑定凭据也不能跨 host 发送。
      removeRequestHeader(config.headers, "X-Device-Id");
      removeRequestHeader(config.headers, "X-Auth-Code");
    }
    if (!requestPolicy.allowBearerToken) {
      if (config.headers) {
        removeRequestHeader(config.headers, "Authorization");
      }
      throw new Error("DEVICE_ACCOUNT_BINDING_NOT_FOUND");
    }

    if (token && config.headers) {
      if (!config.headers.has("Authorization")) {
        config.headers.set("Authorization", `Bearer ${token}`);
      }
      if (sessionKind !== "deviceAccount") {
        // 读令牌期间发生了账号切换：这个请求属于旧账号，作废，免得带着新账号的令牌发出去。
        if (isAccountSwitchInProgress() || isStaleSessionGeneration(sessionStamp)) {
          throw createAccountSessionChangedError();
        }
        return config;
      }
    }

    let deviceSession = null;
    if (requestPolicy.allowDeviceHeaders) {
      const prefetched = await deviceSessionPrefetch;
      if (prefetched && !prefetched.ok) throw prefetched.error;
      // 旧格式留给原读取路径迁移；预读与调用方设备头不符时重读，避免覆盖较新的凭据。
      deviceSession = prefetched?.ok
        && !prefetched.value.requiresMigration
        && DeviceStorage.isSessionSnapshotCurrent(prefetched.value)
        && prefetched.value.session?.hardwareId === suppliedDeviceId
        && prefetched.value.session.authCode === suppliedAuthCode
        ? prefetched.value.session
        : await DeviceStorage.getSession();
    }
    if (deviceSession?.hardwareId && deviceSession.authCode && config.headers) {
      config.headers.set("X-Device-Id", deviceSession.hardwareId);
      config.headers.set("X-Auth-Code", deviceSession.authCode);
    }

    if (isAccountSwitchInProgress() || isStaleSessionGeneration(sessionStamp)) {
      throw createAccountSessionChangedError();
    }
    return config;
  },
  (error) => Promise.reject(error)
);

apiClient.interceptors.response.use(
  async (response) => {
    if (
      isUnauthenticatedApiPayload(response.data) &&
      !isAuthenticationRequest(response.config as InternalAxiosRequestConfig)
    ) {
      // 切换前发出的旧账号请求在切换期间 / 之后收到「未登录」信封：不能因此把新账号登出。
      const stamp = (response.config as InternalAxiosRequestConfig & { _sessionGeneration?: number })
        ._sessionGeneration;
      if (isAccountSwitchInProgress() || isStaleSessionGeneration(stamp)) {
        throw createAccountSessionChangedError();
      }
      const message = extractApiErrorMessage(response.data, "Unauthorized");
      if (shouldSkipAuthRecovery(response.config as InternalAxiosRequestConfig)) {
        throw new Error(message);
      }
      if (shouldSkipAuthRedirect(response.config as InternalAxiosRequestConfig)) {
        await invalidateLocalSession(message);
      } else {
        await redirectToLoginAfterUnauthenticated(message);
      }
      throw new Error(message);
    }

    try {
      response.data = unwrapApiEnvelope(response.data);
    } catch (error) {
      reportApiErrorLog(error, response.config as InternalAxiosRequestConfig, {
        responseStatus: response.status,
        responseData: response.data,
        message: "移动端 API 返回业务失败响应",
      });
      throw error;
    }
    return response;
  },
  async (error: AxiosError) => {
    const original = error.config as InternalAxiosRequestConfig & {
      _retry?: boolean;
      _expectedAccountGuid?: string;
      _sessionGeneration?: number;
    };
    const skipAuthRedirect = shouldSkipAuthRedirect(original);
    const skipAuthRecovery = shouldSkipAuthRecovery(original);

    if (
      error.response?.status === 401
      && !isAuthenticationRequest(original)
      && (isAccountSwitchInProgress() || isStaleSessionGeneration(original?._sessionGeneration))
    ) {
      // 旧账号的请求在切换期间/之后才收到 401：既不能用新账号的令牌去刷新，也不能因此把新账号登出。
      return Promise.reject(createAccountSessionChangedError());
    }

    if (
      error.response?.status === 401
      && original?._expectedAccountGuid
      && !isAuthenticationRequest(original)
    ) {
      // 权限管理写请求不能在 401 后自动重放：迟到的刷新结果可能覆盖用户刚建立的新会话。
      // 此处也不能清理全局会话；旧响应与新登录并发时，异步清理可能误删新账号凭据。
      return Promise.reject(
        Object.assign(new Error("ACCOUNT_SESSION_CHANGED"), {
          code: "ACCOUNT_SESSION_CHANGED",
          cause: preserveApiClientError(error),
        })
      );
    }

    if (error.response?.status === 401 && skipAuthRecovery) {
      return Promise.reject(preserveApiClientError(error));
    }

    if (error.response?.status === 401 && original?._retry && !isAuthenticationRequest(original)) {
      const message = extractApiErrorMessage(error, error.message);
      if (!skipAuthRedirect) {
        await redirectToLoginAfterUnauthenticated(message);
      } else {
        await invalidateLocalSession(message);
      }
      error.message = message;
      return Promise.reject(preserveApiClientError(error));
    }

    if (error.response?.status === 401 && !original?._retry && !isAuthenticationRequest(original)) {
      if (isRefreshing) {
        return new Promise((resolve, reject) => {
          refreshQueue.push({
            resolve: (t) => {
              original.headers.set("Authorization", `Bearer ${t}`);
              resolve(apiClient(original));
            },
            reject,
          });
        });
      }
      original._retry = true;
      isRefreshing = true;
      refreshSettled = new Promise<void>((resolve) => {
        releaseRefreshSettled = resolve;
      });
      const refreshGeneration = getAccountSessionGeneration();
      try {
        const [persistedSessionKind, accountBinding, currentToken, refreshToken] =
          await Promise.all([
            getAuthSessionMarker(),
            DeviceAccountStorage.loadBinding().catch(() => null),
            SecureStorage.getToken(),
            SecureStorage.getRefreshToken(),
          ]);
        const sessionKind = deriveEffectiveAuthSessionKind({
          persistedKind: persistedSessionKind,
          hasAccessToken: Boolean(currentToken),
          hasRefreshToken: Boolean(refreshToken),
          hasBinding: Boolean(accountBinding),
        });
        let accessToken: string;
        if (sessionKind === "deviceAccount") {
          const exchanged = await exchangeStoredDeviceAccountToken();
          accessToken = exchanged.accessToken;
          await SecureStorage.setToken(accessToken);
          await SecureStorage.removeRefreshToken();
        } else {
          const baseURL = await syncApiBaseUrl();
          const rt = await SecureStorage.getRefreshToken();
          if (!rt) throw new Error("No refresh token");
          const res = await axios.post(`${baseURL}/auth/refresh`, {
            refreshToken: rt,
          });
          const refreshed = res.data.data ?? res.data;
          // 刷新期间账号已经切换完成：这份令牌属于旧账号，丢弃，绝不能写进新账号的槽位。
          // 只看会话代号、不看「切换进行中」：激活新会话之前槽位仍属于这个账号，且账号切换流程会等本次刷新收尾后
          // 再回写最新令牌——此时丢弃会让保险库里留下已被服务器轮转作废的旧令牌。
          if (isStaleSessionGeneration(refreshGeneration)) {
            throw createAccountSessionChangedError();
          }
          accessToken = refreshed.accessToken;
          await SecureStorage.setToken(accessToken);
          await SecureStorage.setRefreshToken(refreshed.refreshToken);
        }
        refreshQueue.forEach((cb) => cb.resolve(accessToken));
        refreshQueue = [];
        original.headers.set("Authorization", `Bearer ${accessToken}`);
        return apiClient(original);
      } catch (refreshErr) {
        refreshQueue.forEach((cb) => cb.reject(refreshErr as Error));
        refreshQueue = [];
        if ((refreshErr as { code?: unknown } | null)?.code === "ACCOUNT_SESSION_CHANGED") {
          // 账号切换打断的刷新不是登录失效：不清会话、不跳登录页。
          return Promise.reject(refreshErr);
        }
        if (!skipAuthRedirect) {
          await redirectToLoginAfterUnauthenticated(
            refreshErr instanceof Error ? refreshErr.message : undefined
          );
        } else {
          await invalidateLocalSession(
            refreshErr instanceof Error ? refreshErr.message : undefined
          );
        }
        return Promise.reject(refreshErr);
      } finally {
        isRefreshing = false;
        const release = releaseRefreshSettled;
        refreshSettled = null;
        releaseRefreshSettled = null;
        release?.();
      }
    }
    reportApiErrorLog(error, original, {
      responseStatus: error.response?.status,
      responseData: error.response?.data,
    });
    return Promise.reject(preserveApiClientError(error));
  }
);
