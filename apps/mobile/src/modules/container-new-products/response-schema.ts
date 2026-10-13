import { z } from "zod";
import { normalizeWarehouseArrivalDate } from "./warehouse-date";

// 接口响应的解析与归一化。独立成纯模块（不依赖 apiClient / Expo），便于直接单测；api.ts 只负责请求。
export const containerNewProductsResponseSchema = z.object({
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
    // 货柜到仓库日期：basis = actual 时是实际到货日期，estimated 时是预计到岸日期。
    // 旧版后端不返回该字段（undefined）→ null，页面不显示仓库日期行；日期只取前 10 位
    warehouseArrivalDate: z.string().nullable().optional().transform((value) => normalizeWarehouseArrivalDate(value)),
    basis: z.enum(["actual", "estimated"]),
    isNewProduct: z.boolean().nullable().optional().transform((value) => value ?? true),
  })),
});
