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
    isNewProduct: z.boolean().nullable().optional().transform((value) => value ?? true),
  })),
});

// 工作台角标只要新商品（includeExisting=false），页面要连同已有商品一起取来在前端筛选，两者分开缓存；
// 键都以 "container-new-products" 打头，工作台下拉刷新按前缀一起失效
export function containerNewProductsQueryKey(storeCode: string | null, includeExisting = false) {
  return ["container-new-products", storeCode, includeExisting ? "with-existing" : "new-only"] as const;
}

export async function getContainerNewProducts(storeCode: string, includeExisting = false): Promise<ContainerNewProductsResponse> {
  try {
    // apiClient 已将 /api 作为 baseURL 前缀，业务路径保持与其他 mobile API 一致。
    // 只在需要时带 includeExisting，角标请求与旧接口完全一致
    const response = await apiClient.get("/react/v1/container-new-products", {
      params: includeExisting ? { storeCode, includeExisting: true } : { storeCode },
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
