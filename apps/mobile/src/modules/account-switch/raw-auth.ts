// 多账号用的「裸」认证请求：直接用 axios，不经过 apiClient 的拦截器。
//
// 为什么不能用 apiClient：
//   - 切换 / 添加账号时，目标账号的刷新、核对身份、登录都不能带上当前账号的令牌；
//   - apiClient 的 401 处理会刷新令牌甚至登出当前账号，而这里的 401 只代表「目标账号的凭据不对」；
//   - 账号切换闸门会挡住 apiClient 的请求，裸请求不受影响。
// 注意：服务器拒绝刷新令牌时返回的是「HTTP 200 + success:false」，响应解析见 raw-auth-parse.ts。

import axios from "axios";
import type { CurrentUser, TokenResponse, LoginRequest } from "@/modules/auth/types";
import { normalizeCurrentUser } from "@/modules/auth/api";
import { isUnauthenticatedApiPayload } from "@/shared/api/auth-error";
import { unwrapApiEnvelope } from "@/shared/api/api-envelope";
import { buildApiBaseUrl, getStoredApiHost } from "@/shared/api/config";
import { buildTransportApiBaseUrl, ensureApiTransportHydrated } from "@/shared/api/transport";
import { assertCurrentUserAuthorized, parseRefreshResponse, type TokenPair } from "./raw-auth-parse";

const RAW_AUTH_TIMEOUT_MS = 20_000;

// 真实发请求的地址：走当前选中的传输入口（边缘或直连，同一后端），线路故障时才不会只有账号切换登录失败。
async function authBaseUrl(): Promise<string> {
  const host = await getStoredApiHost();
  await ensureApiTransportHydrated();
  // 这里主要是登录 / 刷新令牌 / 登出这类写请求，不拿它们去试探冷却结束后的首选入口（probe:false）。
  return buildTransportApiBaseUrl(host, undefined, { probe: false });
}

/**
 * 当前使用的 API 主机（保险库按它区分账号来自哪个服务器）。
 * 这是账号的「身份」标识，必须保持稳定，绝不能随传输入口（边缘/直连）变化，所以不走传输选择。
 */
export async function currentApiHost(): Promise<string> {
  return buildApiBaseUrl(await getStoredApiHost());
}

export async function rawRefresh(refreshToken: string): Promise<TokenPair> {
  const res = await axios.post(
    `${await authBaseUrl()}/auth/refresh`,
    { refreshToken },
    { timeout: RAW_AUTH_TIMEOUT_MS }
  );
  return parseRefreshResponse(res.data);
}

/** 用显式 Bearer 令牌读取该令牌对应的用户。 */
export async function rawFetchCurrentUser(accessToken: string): Promise<CurrentUser> {
  const res = await axios.get(`${await authBaseUrl()}/auth/current`, {
    timeout: RAW_AUTH_TIMEOUT_MS,
    headers: { Authorization: `Bearer ${accessToken}` },
  });
  assertCurrentUserAuthorized(res.data, isUnauthenticatedApiPayload);
  return normalizeCurrentUser(unwrapApiEnvelope(res.data));
}

export async function rawLogin(payload: LoginRequest): Promise<TokenResponse> {
  const res = await axios.post(`${await authBaseUrl()}/auth/login`, payload, { timeout: RAW_AUTH_TIMEOUT_MS });
  return unwrapApiEnvelope<TokenResponse>(res.data);
}

/** 只需要刷新令牌就能让服务器作废该会话；失败不抛错（尽力而为）。 */
export async function rawLogout(refreshToken: string): Promise<void> {
  try {
    await axios.post(`${await authBaseUrl()}/auth/logout`, { refreshToken }, { timeout: 8_000 });
  } catch {
    // 作废失败只意味着这个会话多活几天（7 天后自然过期），不影响本机。
  }
}
