import { unwrapApiEnvelope } from "@/shared/api/api-envelope";
import { apiClient } from "@/shared/api/client";
import { buildGridRequest, normalizeDetail, normalizeEvaluate, normalizeListPage } from "./logic";
import type { PricingEvaluateResult, PricingListFilters, PricingStrategyDetail, PricingStrategyListPage, PricingStrategySavePayload } from "./types";

// apiClient 的 baseURL 已带 /api；与 Web /api/react/v1/pricing-strategies 是同一组接口。
// 读（grid / 详情 / 试算）要 PricingStrategy.View，写（新建 / 修改 / 删除）要 PricingStrategy.Edit。
const BASE_URL = "/react/v1/pricing-strategies";

export async function fetchPricingStrategies(filters: PricingListFilters, signal?: AbortSignal): Promise<PricingStrategyListPage> {
  const response = await apiClient.post(`${BASE_URL}/grid`, buildGridRequest(filters), { signal });
  return normalizeListPage(unwrapApiEnvelope(response.data));
}

export async function fetchPricingStrategy(id: string, signal?: AbortSignal): Promise<PricingStrategyDetail> {
  const response = await apiClient.get(`${BASE_URL}/${encodeURIComponent(id)}`, { signal });
  const detail = normalizeDetail(unwrapApiEnvelope(response.data));
  // 返回的编号和请求的不一致说明串台，宁可报错也不要把别的策略显示在编辑页。
  if (!detail || detail.id.toLowerCase() !== id.toLowerCase()) {
    throw Object.assign(new Error("Pricing strategy response invalid"), { code: "PRICING_STRATEGY_INVALID_RESPONSE" });
  }
  return detail;
}

export async function createPricingStrategy(payload: PricingStrategySavePayload): Promise<void> {
  const response = await apiClient.post(BASE_URL, payload);
  // 后端校验失败返回 200 + success:false（带中文原因），unwrapApiEnvelope 会抛出带原因的错误。
  unwrapApiEnvelope(response.data);
}

export async function updatePricingStrategy(id: string, payload: PricingStrategySavePayload): Promise<void> {
  const response = await apiClient.put(`${BASE_URL}/${encodeURIComponent(id)}`, payload);
  unwrapApiEnvelope(response.data);
}

export async function deletePricingStrategy(id: string): Promise<void> {
  const response = await apiClient.delete(`${BASE_URL}/${encodeURIComponent(id)}`);
  unwrapApiEnvelope(response.data);
}

export async function evaluatePricing(
  params: { purchasePrice: number; storeCode: string | null; supplierCode: string | null },
  signal?: AbortSignal,
): Promise<PricingEvaluateResult> {
  const response = await apiClient.post(
    `${BASE_URL}/evaluate`,
    {
      purchasePrice: params.purchasePrice,
      ...(params.storeCode ? { storeCode: params.storeCode } : {}),
      ...(params.supplierCode ? { supplierCode: params.supplierCode } : {}),
    },
    { signal },
  );
  return normalizeEvaluate(unwrapApiEnvelope(response.data), params.purchasePrice);
}
