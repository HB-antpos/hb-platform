import { z } from "zod";
import { apiClient } from "@/shared/api/client";
import { unwrapApiEnvelope } from "@/shared/api/api-envelope";
import type { ContainerNewProductsResponse } from "./types";
import { getContainerNewProductsErrorCode } from "./errors";

const responseSchema = z.object({
  storeCode: z.string().min(1),
  stateCode: z.string().nullable().optional().transform((value) => value ?? null),
  localToday: z.string().nullable().optional().transform((value) => value?.slice(0, 10) || null),
  items: z.array(z.object({
    productCode: z.string().min(1),
    hbProductNo: z.string().nullable().optional().transform((value) => value?.trim() || null),
    quantity: z.number().nullable().optional().transform((value) => value ?? null),
    imageUrl: z.string().nullable().optional().transform((value) => value ?? null),
    barcode: z.string().nullable().optional().transform((value) => value?.trim() || null),
    // 0 或负数按「没有零售价」处理，与后端口径一致
    retailPrice: z.number().nullable().optional().transform((value) => (value != null && value > 0 ? value : null)),
    containerNumber: z.string().nullable().optional().transform((value) => value ?? null),
    containerCode: z.string(),
    estimatedStoreArrivalDate: z.string().min(1),
    estimatedStoreArrivalDateEnd: z.string().nullable().optional().transform((value) => value || null),
    basis: z.enum(["actual", "estimated"]),
  })),
});

// 页面与外壳（工作台角标）共用同一缓存键：工作台取到的数据点进 HB新品 可直接复用
export function containerNewProductsQueryKey(storeCode: string | null) {
  return ["container-new-products", storeCode] as const;
}

export async function getContainerNewProducts(storeCode: string): Promise<ContainerNewProductsResponse> {
  try {
    // apiClient 已将 /api 作为 baseURL 前缀，业务路径保持与其他 mobile API 一致。
    const response = await apiClient.get("/react/v1/container-new-products", {
      params: { storeCode },
    });
    const parsed = responseSchema.safeParse(unwrapApiEnvelope(response.data));
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
