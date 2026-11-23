import { z } from "zod";
import { apiClient } from "@/shared/api/client";
import { unwrapApiEnvelope } from "@/shared/api/api-envelope";
import { isValidIsoDate } from "./logic";
import type {
  SeasonalLookupResult,
  SeasonalProductInsight,
  SeasonalRanges,
} from "./types";

const number = z.number().finite();
const text = z.string();
// 后端统一使用 WhenWritingNull，空的货号、条码、图片会省略字段。
const nullableText = text.nullish().transform((value) => value ?? null);
const range = z
  .object({ startDate: text, endDate: text })
  .refine((value) => isValidIsoDate(value.startDate) && isValidIsoDate(value.endDate) && value.startDate <= value.endDate);
const ranges = z.object({ inbound: range, sales: range });

const lookupSchema = z.object({
  matchMode: z.enum(["barcode", "itemNumber"]),
  truncated: z.boolean(),
  ranges,
  items: z.array(
    z.object({
      productCode: text.min(1),
      productName: text,
      itemNumber: nullableText,
      barcode: nullableText,
      productImage: nullableText,
      theoreticalStock: number,
      // 进货 / 销量 / 最早进货日是后加字段：旧后端没有时按「未知」处理，不能补成 0 冒充没有进销。
      inboundQuantity: number.nullish().transform((value) => value ?? null),
      salesQuantity: number.nullish().transform((value) => value ?? null),
      firstInboundDate: nullableText,
    }),
  ),
});

const insightSchema = z.object({
  generatedAt: text,
  store: z.object({ storeCode: text.min(1), storeName: text }),
  product: z.object({
    productCode: text.min(1),
    productName: text,
    itemNumber: nullableText,
    barcode: nullableText,
    productImage: nullableText,
    sourceType: z.enum(["local", "warehouse"]),
  }),
  ranges,
  inbound: z.object({
    quantity: number,
    documentCount: z.number().int().nonnegative(),
    records: z.array(z.object({ id: text, date: text, documentNo: text, quantity: number })),
  }),
  sales: z.object({
    quantity: number,
    amount: number,
    daily: z.array(z.object({ date: text, quantity: number, amount: number })),
  }),
  theoreticalStock: number,
  branches: z.array(
    z.object({
      storeCode: text.min(1),
      storeName: text,
      inboundQuantity: number,
      salesQuantity: number,
      theoreticalStock: number,
    }),
  ),
});

function invalid(message: string) {
  return Object.assign(new Error(message), { code: "SEASONAL_INSIGHT_INVALID_RESPONSE" });
}

function parse<T>(schema: z.ZodType<T>, payload: unknown): T {
  const result = schema.safeParse(unwrapApiEnvelope(payload));
  // 缺字段或无效数量属于接口失败，不能静默补零伪装成没有进销数据。
  if (!result.success) throw invalid("Invalid seasonal product response");
  return result.data;
}

/** 未指定区间时由后端按门店时区给默认值（8 月 1 日至门店当地今天）。 */
function rangeParams(value: SeasonalRanges | null) {
  return value
    ? {
        inboundStartDate: value.inbound.startDate,
        inboundEndDate: value.inbound.endDate,
        salesStartDate: value.sales.startDate,
        salesEndDate: value.sales.endDate,
      }
    : {};
}

function sameRanges(requested: SeasonalRanges | null, actual: SeasonalRanges) {
  return (
    !requested ||
    (requested.inbound.startDate === actual.inbound.startDate &&
      requested.inbound.endDate === actual.inbound.endDate &&
      requested.sales.startDate === actual.sales.startDate &&
      requested.sales.endDate === actual.sales.endDate)
  );
}

export async function lookupSeasonalProducts(
  storeCode: string,
  keyword: string,
  requestedRanges: SeasonalRanges | null,
  signal?: AbortSignal,
): Promise<SeasonalLookupResult> {
  const response = await apiClient.get("/react/v1/seasonal-product-insights/lookup", {
    params: { storeCode, keyword, ...rangeParams(requestedRanges) },
    signal,
  });
  const result = parse(lookupSchema, response.data);
  if (!sameRanges(requestedRanges, result.ranges)) throw invalid("Seasonal lookup range mismatch");
  return result;
}

export async function fetchSeasonalProductInsight(
  storeCode: string,
  productCode: string,
  requestedRanges: SeasonalRanges | null,
  signal?: AbortSignal,
): Promise<SeasonalProductInsight> {
  const response = await apiClient.get("/react/v1/seasonal-product-insights/store", {
    params: { storeCode, productCode, ...rangeParams(requestedRanges) },
    signal,
  });
  const result = parse(insightSchema, response.data);
  // 返回的门店、商品或区间与请求不符时视为失败，不能把别的范围的数据显示成当前查询结果。
  if (
    result.store.storeCode.toLowerCase() !== storeCode.toLowerCase() ||
    result.product.productCode.toLowerCase() !== productCode.toLowerCase() ||
    !sameRanges(requestedRanges, result.ranges)
  ) {
    throw invalid("Seasonal product insight scope mismatch");
  }
  return result;
}
