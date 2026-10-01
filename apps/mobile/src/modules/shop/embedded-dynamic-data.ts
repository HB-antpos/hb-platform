import type { StoreOrderDynamicData } from "./types";

type ApiItem = Record<string, unknown>;

/**
 * 解析商品分页响应里顺带返回的动态数据。旧后端不返回该字段时为 undefined，
 * 调用方据此回落到单独的 dynamic-data 请求。
 */
export function normalizeEmbeddedDynamicData(payload: unknown): StoreOrderDynamicData[] | undefined {
  if (!payload || typeof payload !== "object") {
    return undefined;
  }

  const record = payload as { dynamicData?: unknown; DynamicData?: unknown };
  const raw = record.dynamicData ?? record.DynamicData;
  if (!Array.isArray(raw)) {
    return undefined;
  }

  return raw
    .filter((item): item is ApiItem => Boolean(item) && typeof item === "object")
    .map((item) => ({
      productCode: String(item.productCode ?? item.ProductCode ?? ""),
      lastOrderDate:
        item.lastOrderDate != null
          ? String(item.lastOrderDate)
          : item.LastOrderDate != null
            ? String(item.LastOrderDate)
            : undefined,
      lastQuantity:
        item.lastQuantity != null
          ? Number(item.lastQuantity)
          : item.LastQuantity != null
            ? Number(item.LastQuantity)
            : undefined,
      lastAllocQuantity:
        item.lastAllocQuantity != null
          ? Number(item.lastAllocQuantity)
          : item.LastAllocQuantity != null
            ? Number(item.LastAllocQuantity)
            : undefined,
      cartQuantity: Number(item.cartQuantity ?? item.CartQuantity ?? 0),
    }))
    .filter((item) => item.productCode);
}
