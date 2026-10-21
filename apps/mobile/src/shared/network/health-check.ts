/**
 * 通用后端可达性检查（健康探测）。
 *
 * 复用考勤模块 verifyAttendanceNetworkReachability 的既有模式（GET /health + 超时中止），
 * 作为通用基础设施对外暴露，供网络恢复补传、页面刷新等场景调用。
 * 所有副作用依赖均可注入，便于单元测试与在 iOS 审核态下保持一致行为。
 */
import { reviewAwareFetch } from "@/modules/ios-review/network";
import { isIosReviewSessionActive } from "@/modules/ios-review/session";
import { resolveEffectiveApiHost } from "@/shared/api/effective-api-host";
import { probeWithTransportFallback } from "@/shared/api/transport";

/** 健康探测默认超时（毫秒），与考勤模块一致。 */
export const NETWORK_CHECK_TIMEOUT_MS = 5000;

/** 后端可达性检查结果：ok 表示后端可达（health 端点正常响应）。 */
export type BackendReachability = Readonly<{
  ok: boolean;
  checkedAtIso: string;
}>;

export type BackendHealthCheckOptions = {
  /** 探测超时（毫秒），默认 5000。 */
  timeoutMs?: number;
  /** iOS 审核态判定函数；默认读取 ios-review session 状态。 */
  isReviewActive?: () => boolean;
  /** 请求执行器；默认 reviewAwareFetch（审核态外等同全局 fetch）。 */
  fetchImpl?: typeof fetch;
  /** 当前 API 基础地址来源；默认与 apiClient 请求拦截器同一套解析。 */
  getApiBaseUrl?: () => Promise<string>;
  /** 时间戳来源，便于测试固定时间。 */
  nowIso?: () => string;
};

/**
 * 由 API 基础地址推导健康检查 URL。
 *
 * 后端 HealthController 的路由是 `api/health`（[Route("api/[controller]")]），
 * 因此必须拼在 `/api` 之后。生产域名经 Nginx 代理时 `/health` 与 `/api/health` 均可用，
 * 但本地或局域网直连 5002 时只有 `/api/health` 存在；早期实现去掉 `/api` 会在开发环境
 * 恒定返回 404，使离线恢复探测永远判定“后端不可达”。
 */
export function buildHealthUrl(apiBaseUrl: string): string {
  const normalized = apiBaseUrl.replace(/\/+$/, "");
  return normalized.endsWith("/api") ? `${normalized}/health` : `${normalized}/api/health`;
}

/**
 * 检查后端是否可达。
 *
 * 行为约定：
 * - iOS 审核态：不发起真实网络请求，直接视为可达（离线 Demo 由本地 adapter 处理）。
 * - 普通态：GET /health，超时或任何异常一律返回 { ok: false }，不向上抛错，
 *   保证调用方无需处理异常分支。
 */
export async function checkBackendReachable(
  options: BackendHealthCheckOptions = {},
): Promise<BackendReachability> {
  const nowIso = options.nowIso ?? (() => new Date().toISOString());
  const isReviewActive =
    options.isReviewActive ?? isIosReviewSessionActive;
  if (isReviewActive()) {
    // 审核态下健康检查不触网，直接报告可用，避免审核环境产生真实请求。
    return { ok: true, checkedAtIso: nowIso() };
  }

  const fetcher = options.fetchImpl ?? reviewAwareFetch;
  const timeoutMs = options.timeoutMs ?? NETWORK_CHECK_TIMEOUT_MS;
  // 每次探测尝试都有独立的超时控制器：首选入口耗尽超时后，备用入口的探测不能共用已中止的控制器。
  const probeOnce = async (apiBaseUrl: string) => {
    const controller = new AbortController();
    const timeoutId = setTimeout(() => controller.abort(), timeoutMs);
    try {
      return await fetcher(buildHealthUrl(apiBaseUrl), {
        method: "GET",
        headers: { Accept: "application/json" },
        signal: controller.signal,
      });
    } finally {
      clearTimeout(timeoutId);
    }
  };

  try {
    const response = options.getApiBaseUrl
      ? await probeOnce(await options.getApiBaseUrl())
      // 默认路径：必须与业务请求解析出同一个 host（设备账号会话下以绑定 host 为准），
      // 否则绑定 host 与偏好 host 不一致时，探测会稳定成功而业务请求稳定失败，
      // 把离线态拖进「判定恢复→重跑失败→再判定恢复」的循环。
      // 同时探测也要和业务请求一样走传输入口选择：当前入口不通就顺带试备用入口，
      // 否则线路只对某个入口故障时，App 会被误判为离线，或探测通、业务却不通。
      : await probeWithTransportFallback(
          await resolveEffectiveApiHost(),
          probeOnce,
          (candidate) => candidate.ok === true,
        );
    // 只有 2xx（response.ok）才算后端真正可达；5xx/4xx 视为不可达。
    return { ok: response.ok === true, checkedAtIso: nowIso() };
  } catch {
    // 超时、网络不可达或地址解析失败统一视为不可达，不暴露底层异常。
    return { ok: false, checkedAtIso: nowIso() };
  }
}
