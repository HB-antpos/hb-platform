import { apiClient } from "@/shared/api/client";
import { unwrapApiEnvelope } from "@/shared/api/api-envelope";
import type { ContainerNewProductsResponse } from "./types";
import { getContainerNewProductsErrorCode } from "./errors";
import { buildContainerNewProductsParams } from "./request-params";
import { containerNewProductsResponseSchema } from "./response-schema";

// 工作台角标只要新商品（includeExisting=false），页面要连同已有商品一起取来在前端筛选，两者分开缓存；
// 键都以 "container-new-products" 打头，工作台下拉刷新按前缀一起失效
export function containerNewProductsQueryKey(storeCode: string | null, includeExisting = false) {
  return ["container-new-products", storeCode, includeExisting ? "with-existing" : "new-only"] as const;
}

export async function getContainerNewProducts(storeCode: string, includeExisting = false): Promise<ContainerNewProductsResponse> {
  try {
    // apiClient 已将 /api 作为 baseURL 前缀，业务路径保持与其他 mobile API 一致。
    // 参数见 buildContainerNewProductsParams：始终带 windowBasis=warehouse（页面与角标同一窗口），includeExisting 只在需要时带
    const response = await apiClient.get("/react/v1/container-new-products", {
      params: buildContainerNewProductsParams(storeCode, includeExisting),
    });
    const parsed = containerNewProductsResponseSchema.safeParse(unwrapApiEnvelope(response.data));
    if (!parsed.success) {
      throw new Error("Invalid container new products response");
    }
    return parsed.data;
  } catch (error) {
    const code = getContainerNewProductsErrorCode(error);
    if (code === "STORE_STATE_UNKNOWN") {
      throw Object.assign(new Error("Store state is unknown"), { code });
    }
    throw error;
  }
}
