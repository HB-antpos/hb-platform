// 位置请求接口调用（HTTP 客户端由外部注入：生产用 apiClient，测试用假客户端抓取请求）。
//
//   GET  /api/react/v1/attendance/my/location-requests/pending            → AttendanceLocationRequestDto[]（只含未过期、仍在班中的待响应请求）
//   POST /api/react/v1/attendance/my/location-requests/{requestGuid}/respond  body: RespondLocationRequestBody → AttendanceLocationRequestDto
//
// apiClient 的响应拦截器会把统一信封 ApiResponse<T> 解包：业务失败（HTTP 200 + success:false）直接抛错，
// 错误对象带 code（= errorCode）与 apiBusinessError:true；本文件的归一化函数对未解包的信封同样能处理。
//
// 约定：
// 1. 请求绑定发起时的账号（X-Client-Expected-Account-Guid）：期间如果切换了账号，请求在本地被拒绝，
//    不会把上一个账号取到的位置挂到新账号头上，也不会触发 401 自动刷新 / 踢登录；
// 2. X-Skip-Auth-Redirect：这是自动触发的请求，遇到 401 只清理本地会话，不由请求自己拉起登录页；
// 3. 拉取待办带 X-Skip-Center-Log：它是每次回前台都会发的轻量只读请求，失败（含后端尚未上线时的 404）不进中心日志；
//    respond 不带，失败有排查价值；中心日志只记响应，不含请求体里的坐标；
// 4. 本文件不写日志、不落盘。
import type { AxiosRequestConfig } from "axios";
import { accountBoundRequestConfig } from "@/modules/auth/account-bound-request";
import {
  LOCATION_REQUEST_PENDING_PATH,
  locationRequestRespondPath,
  normalizeLocationRequest,
  normalizeLocationRequestList,
  type AttendanceLocationRequest,
  type RespondLocationRequestBody,
} from "./location-request-logic";

export interface LocationRequestHttp {
  get(url: string, config?: AxiosRequestConfig): Promise<{ data: unknown }>;
  post(url: string, data?: unknown, config?: AxiosRequestConfig): Promise<{ data: unknown }>;
}

/** 单次请求的超时：比 apiClient 默认的 30 秒短，免得消费锁被一次慢请求占太久。 */
export const LOCATION_REQUEST_HTTP_TIMEOUT_MS = 15_000;

export interface LocationRequestApiOptions {
  signal?: AbortSignal;
  timeoutMs?: number;
}

function buildConfig(accountGuid: string, options: LocationRequestApiOptions | undefined, extraHeaders: Record<string, string>): AxiosRequestConfig {
  const bound = accountBoundRequestConfig(accountGuid, options?.signal);
  return {
    ...bound,
    timeout: options?.timeoutMs ?? LOCATION_REQUEST_HTTP_TIMEOUT_MS,
    headers: { ...bound.headers, "X-Skip-Auth-Redirect": "1", ...extraHeaders },
  };
}

export function createLocationRequestApi(http: LocationRequestHttp) {
  return {
    /** 拉取我的待响应位置请求。响应不是列表时抛 LocationRequestResponseError（不会被当成「没有待办」）。 */
    async listPending(accountGuid: string, options?: LocationRequestApiOptions): Promise<AttendanceLocationRequest[]> {
      const response = await http.get(LOCATION_REQUEST_PENDING_PATH, buildConfig(accountGuid, options, { "X-Skip-Center-Log": "1" }));
      return normalizeLocationRequestList(response.data);
    },

    /** 响应一条位置请求。重复响应服务端幂等（alreadyResponded=true）。失败直接抛出，由调用方按错误码分类。 */
    async respond(
      accountGuid: string,
      requestGuid: string,
      body: RespondLocationRequestBody,
      options?: LocationRequestApiOptions,
    ): Promise<AttendanceLocationRequest> {
      const response = await http.post(locationRequestRespondPath(requestGuid), body, buildConfig(accountGuid, options, {}));
      return normalizeLocationRequest(response.data);
    },
  };
}

export type LocationRequestApi = ReturnType<typeof createLocationRequestApi>;
