import { z } from "zod";
import { unwrapApiEnvelope } from "../../shared/api/api-envelope";
import type { ProductBranchSales, StoreProductInsight } from "./types";
import { isValidProductInsightRange } from "./logic";

const number = z.number().finite();
const text = z.string();
// 后端统一使用 WhenWritingNull，合法的空元数据与历史记录会省略字段。
const nullableText = text.nullish().transform((value) => value ?? null);
const range = z
  .object({ startDate: text, endDate: text })
  .refine(isValidProductInsightRange);
const movement = z.object({
  id: text,
  date: text,
  documentNo: text,
  quantity: number,
  supplierName: nullableText,
});
const storeSchema = z.object({
  range,
  generatedAt: text,
  salesStatisticLastUpdatedAt: nullableText,
  store: z.object({ storeCode: text.min(1), storeName: text }),
  product: z.object({
    productCode: text.min(1),
    productName: text,
    itemNumber: nullableText,
    barcode: nullableText,
    productImage: nullableText,
    localSupplierCode: nullableText,
    localSupplierName: nullableText,
  }),
  sourceType: z.enum(["local", "warehouse"]),
  sales: z.object({
    quantity: number,
    amount: number,
    records: z.array(
      z.object({ date: text, quantity: number, amount: number }),
    ),
  }),
  purchases: z.object({
    quantity: number,
    documentCount: z.number().int().nonnegative(),
    records: z.array(movement),
    lastRecord: movement.nullish().transform((value) => value ?? null),
  }),
  warehouse: z.object({
    orderedQuantity: number,
    deliveredQuantity: number,
    orders: z.array(
      z.object({
        id: text,
        date: text,
        documentNo: text,
        quantity: number,
        deliveredQuantity: number,
        deliveryDate: nullableText,
        status: text,
      }),
    ),
    deliveries: z.array(movement),
    lastDelivery: movement.nullish().transform((value) => value ?? null),
  }),
});
const branchesSchema = z.object({
  range,
  generatedAt: text,
  salesStatisticLastUpdatedAt: nullableText,
  productCode: text.min(1),
  scope: z.enum(["all-pos", "authorized-pos"]),
  totalPosStoreCount: z.number().int().nonnegative(),
  includedStoreCount: z.number().int().nonnegative(),
  quantity: number,
  amount: number,
  // 进货字段由新版主 API 返回；旧版缺省时解析为 null，不能当成 0 件进货。
  purchaseSource: z
    .enum(["local", "warehouse"])
    .nullish()
    .transform((value) => value ?? null),
  purchaseQuantity: number.nullish().transform((value) => value ?? null),
  rows: z.array(
    z.object({
      storeCode: text.min(1),
      storeName: text,
      quantity: number,
      amount: number,
      purchaseQuantity: number.nullish().transform((value) => value ?? null),
    }),
  ),
});

function parse<T>(schema: z.ZodType<T>, payload: unknown): T {
  const result = schema.safeParse(unwrapApiEnvelope(payload));
  if (!result.success) {
    // 缺字段或无效金额属于接口失败，不能静默补零伪装成没有业务数据。
    throw Object.assign(new Error("Invalid product insight response"), {
      code: "PRODUCT_INSIGHT_INVALID_RESPONSE",
    });
  }
  return result.data;
}

export function normalizeStoreProductInsight(
  payload: unknown,
): StoreProductInsight {
  return parse(storeSchema, payload);
}

export function normalizeProductBranchSales(
  payload: unknown,
): ProductBranchSales {
  const result = parse(branchesSchema, payload);
  if (
    result.rows.length !== result.includedStoreCount ||
    new Set(result.rows.map((row) => row.storeCode.toLowerCase())).size !==
      result.rows.length ||
    result.includedStoreCount > result.totalPosStoreCount ||
    (result.scope === "all-pos" &&
      result.includedStoreCount !== result.totalPosStoreCount)
  ) {
    throw Object.assign(new Error("Incomplete product branch sales response"), {
      code: "PRODUCT_INSIGHT_INVALID_RESPONSE",
    });
  }
  // 进货数据要么整份可用、要么整份不可用：口径、合计和每行缺一项都视为未返回，避免半份数据被误读成 0。
  const purchaseComplete =
    result.purchaseSource !== null &&
    result.purchaseQuantity !== null &&
    result.rows.every((row) => row.purchaseQuantity !== null);
  if (!purchaseComplete) {
    return {
      ...result,
      purchaseSource: null,
      purchaseQuantity: null,
      rows: result.rows.map((row) => ({ ...row, purchaseQuantity: null })),
    };
  }
  return result;
}
