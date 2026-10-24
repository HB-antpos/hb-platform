// 客流统计接口调用（HTTP 客户端由外部注入：生产用 apiClient，测试用假客户端抓取请求）。
// 只读计数接口，响应不含任何生物信息；请求带 no-store，避免系统缓存留下过期的「今天」。
import type { AxiosRequestConfig } from "axios";
import { unwrapApiEnvelope } from "@/shared/api/api-envelope";
import { normalizeTrafficOverview } from "./normalize";
import type { TrafficOverview, TrafficOverviewQuery } from "./types";

export const TRAFFIC_OVERVIEW_PATH = "/react/v1/face-match/traffic/overview";

export interface TrafficHttp {
  get(url: string, config?: AxiosRequestConfig): Promise<{ data: unknown }>;
}

export function createTrafficStatsApi(http: TrafficHttp) {
  return {
    async fetchOverview(query: TrafficOverviewQuery, signal?: AbortSignal): Promise<TrafficOverview> {
      const params: Record<string, string> = { store: query.storeCode, period: query.period };
      // anchor 缺省 = 门店本地今天，由服务端按门店时区解释，客户端不传「今天」。
      if (query.anchor) params.anchor = query.anchor;
      const response = await http.get(TRAFFIC_OVERVIEW_PATH, {
        params,
        signal,
        headers: { "Cache-Control": "no-store", Pragma: "no-cache" },
      });
      return normalizeTrafficOverview(unwrapApiEnvelope<unknown>(response.data), query.period);
    },
  };
}
