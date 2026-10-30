import { apiClient } from "@/shared/api/client";
import { useDeviceStore } from "@/store/device-store";
import {
  normalizeProductBranchSales,
  normalizeStoreProductInsight,
} from "./api-normalization";
import type { ProductInsightRange } from "./types";

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
) {
  // 不传 range 时默认日期交由后端按门店时区计算，避免设备处于其他时区时偏移一天；
  // 其他周期由页面以后端返回的结束日为锚点推算后显式传入。
  const response = await apiClient.get("/react/v1/product-insights/store", {
    params: { storeCode, productCode, ...range },
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
