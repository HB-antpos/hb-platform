import { unwrapApiEnvelope } from "@/shared/api/api-envelope";
import { apiClient } from "@/shared/api/client";
import { normalizeDailyCloseDetail, normalizeDailyCloseListPage } from "./api-normalization";
import type { DailyCloseDetail, DailyCloseListPage, DailyCloseListParams } from "./types";

// apiClient 的 baseURL 已带 /api，业务路径与其他 mobile API 一致，不再加 /api 前缀。
const BASE_URL = "/react/v1/pos-daily-closes";

/** 日结记录列表：分店范围由后端按账号收口（管理员看全部，店长只看自己关联的分店）。 */
export async function fetchDailyCloses(params: DailyCloseListParams, signal?: AbortSignal): Promise<DailyCloseListPage> {
  const response = await apiClient.get(BASE_URL, { params, signal });
  return normalizeDailyCloseListPage(unwrapApiEnvelope(response.data));
}

/** 日结明细：不存在与无分店权限都是 404（后端不泄露存在性）。 */
export async function fetchDailyCloseDetail(dailyCloseGuid: string, signal?: AbortSignal): Promise<DailyCloseDetail> {
  const response = await apiClient.get(`${BASE_URL}/${encodeURIComponent(dailyCloseGuid)}`, { signal });
  const detail = normalizeDailyCloseDetail(unwrapApiEnvelope(response.data));
  // 返回的编号和请求的不一致说明串台，宁可报错也不要把别的记录显示在当前详情页。
  if (!detail || detail.dailyCloseGuid.toLowerCase() !== dailyCloseGuid.toLowerCase()) {
    throw Object.assign(new Error("Daily close detail response invalid"), { code: "DAILY_CLOSE_INVALID_RESPONSE" });
  }
  return detail;
}
