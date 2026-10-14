// 推送设备接口调用（HTTP 客户端由外部注入：生产用 apiClient，测试用假客户端抓取请求）。
//
//   POST   /api/react/v1/notifications/push-devices  { expoPushToken, platform, appVersion?, runtimeVersion? } → { pushDeviceId }
//   DELETE /api/react/v1/notifications/push-devices  { expoPushToken }                                          → 200（幂等，只撤销自己的）
//
// 约定：
// 1. 令牌只放请求体，绝不拼进 URL / 查询串（URL 会进代理与访问日志）；
// 2. 请求头带 X-Skip-Center-Log：失败（含后端尚未上线时的 404）不进中心日志，保持静默；
// 3. 请求绑定发起时的账号（X-Client-Expected-Account-Guid）：期间如果切换了账号，请求在本地被拒绝，
//    不会把上一个账号取到的令牌挂到新账号头上，也不会触发 401 自动刷新 / 踢登录；
// 4. 本文件不写日志、不落盘。
import type { AxiosRequestConfig } from "axios";
import { accountBoundRequestConfig } from "@/modules/auth/account-bound-request";
import { PUSH_DEVICES_PATH, PUSH_REQUEST_HEADERS, type PushPlatform } from "./push-types";

export interface PushDevicesHttp {
  post(url: string, data?: unknown, config?: AxiosRequestConfig): Promise<{ data: unknown }>;
  delete(url: string, config?: AxiosRequestConfig): Promise<{ data: unknown }>;
}

export interface PushDevicePayload {
  expoPushToken: string;
  platform: PushPlatform;
  appVersion?: string;
  runtimeVersion?: string;
}

export interface PushRequestOptions {
  signal?: AbortSignal;
  /** 单次请求的超时（毫秒）；不传沿用 apiClient 默认值。 */
  timeoutMs?: number;
}

function buildConfig(accountGuid: string, options?: PushRequestOptions): AxiosRequestConfig {
  const bound = accountBoundRequestConfig(accountGuid, options?.signal);
  return {
    ...bound,
    ...(options?.timeoutMs ? { timeout: options.timeoutMs } : {}),
    headers: { ...bound.headers, ...PUSH_REQUEST_HEADERS },
  };
}

export function createPushDevicesApi(http: PushDevicesHttp) {
  return {
    /** 上报（upsert）本机令牌；同一令牌换账号时后端会把它转给当前账号。失败直接抛出，由调用方静默处理。 */
    async register(payload: PushDevicePayload, accountGuid: string, options?: PushRequestOptions): Promise<void> {
      const body: PushDevicePayload = { expoPushToken: payload.expoPushToken, platform: payload.platform };
      if (payload.appVersion) body.appVersion = payload.appVersion;
      if (payload.runtimeVersion) body.runtimeVersion = payload.runtimeVersion;
      await http.post(PUSH_DEVICES_PATH, body, buildConfig(accountGuid, options));
    },

    /** 注销本机令牌（幂等）。DELETE 带请求体：令牌不进 URL。 */
    async unregister(expoPushToken: string, accountGuid: string, options?: PushRequestOptions): Promise<void> {
      await http.delete(PUSH_DEVICES_PATH, { ...buildConfig(accountGuid, options), data: { expoPushToken } });
    },
  };
}

export type PushDevicesApi = ReturnType<typeof createPushDevicesApi>;
