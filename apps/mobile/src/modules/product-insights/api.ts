import { apiClient } from "@/shared/api/client";
import { useDeviceStore } from "@/store/device-store";
import {
  normalizeProductBranchSales,
  normalizeStoreProductInsight,
} from "./api-normalization";
import {
  buildPresetRange,
  countRangeDays,
  DEFAULT_PRODUCT_INSIGHT_PRESET,
  type ProductInsightPresetDays,
} from "./movement-summary";
import type { ProductInsightRange, StoreProductInsight } from "./types";

function deviceHeaders() {
  const session = useDeviceStore.getState().session;
  return session?.hardwareId && session.authCode
    ? { "X-Device-Id": session.hardwareId, "X-Auth-Code": session.authCode }
    : {};
}

export async function fetchStoreProductInsight(
  storeCode: string,
  productCode: string,
  signal?: AbortSignal,
  range?: ProductInsightRange,
  presetDays: ProductInsightPresetDays = DEFAULT_PRODUCT_INSIGHT_PRESET,
): Promise<StoreProductInsight> {
  // 不传 range 时只带 days，日期交由后端按门店时区计算，避免设备处于其他时区时偏移一天；
  // 其他周期由页面以后端返回的结束日为锚点推算后显式传入。
  const response = await apiClient.get("/react/v1/product-insights/store", {
    params: { storeCode, productCode, ...(range ?? { days: presetDays }) },
    headers: deviceHeaders(),
    signal,
  });
  const result = normalizeStoreProductInsight(response.data);
  if (
    result.store.storeCode.toLowerCase() !== storeCode.toLowerCase() ||
    result.product.productCode.toLowerCase() !== productCode.toLowerCase() ||
    (range &&
      (result.range.startDate !== range.startDate ||
        result.range.endDate !== range.endDate))
  ) {
    throw Object.assign(new Error("Product insight scope mismatch"), {
      code: "PRODUCT_INSIGHT_INVALID_RESPONSE",
    });
  }
  // 兼容未部署 days 参数的旧后端：它固定返回 90 天。以其结束日（门店今天）为锚点，
  // 按所选预设天数显式重查一次，保证页面显示的周期与数据一致。
  if (!range && countRangeDays(result.range) !== presetDays) {
    return fetchStoreProductInsight(
      storeCode,
      productCode,
      signal,
      buildPresetRange(result.range.endDate, presetDays),
    );
  }
  return result;
}

export async function fetchProductInsightBranchSales(
  productCode: string,
  range: ProductInsightRange,
  signal?: AbortSignal,
) {
  const response = await apiClient.get("/react/v1/product-insights/branches", {
    params: { productCode, ...range },
    signal,
  });
  const result = normalizeProductBranchSales(response.data);
  if (
    result.productCode.toLowerCase() !== productCode.toLowerCase() ||
    result.range.startDate !== range.startDate ||
    result.range.endDate !== range.endDate
  ) {
    throw Object.assign(new Error("Product branch sales scope mismatch"), {
      code: "PRODUCT_INSIGHT_INVALID_RESPONSE",
    });
  }
  return result;
}
