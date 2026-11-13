import { z } from "zod";
import { unwrapApiEnvelope } from "../../shared/api/api-envelope";
import type {
  PackCarrier,
  PackCartonDetail,
  PackCartonList,
  PackCartonSummary,
  PackLocateResult,
  PackOrderOption,
  PackPalletDetail,
  PackPalletList,
  PackProductCandidate,
  PackResolveResult,
} from "./types";

const number = z.number().finite();
const text = z.string();
// 后端统一 WhenWritingNull，空值字段会被省略。
const nullableText = text.nullish().transform((value) => value ?? null);
const nullableNumber = number.nullish().transform((value) => value ?? null);
const count = number.nullish().transform((value) => value ?? 0);

const destinationSchema = z
  .object({
    destinationType: z.number().int().nullish().transform((value) => value ?? 0),
    storeCode: nullableText,
    storeName: nullableText,
    carrierGuid: nullableText,
    carrierName: nullableText,
  })
  .nullish()
  .transform(
    (value) => value ?? { destinationType: 0, storeCode: null, storeName: null, carrierGuid: null, carrierName: null },
  );

const cartonSummaryShape = {
  cartonGuid: text.min(1),
  cartonNo: text.min(1),
  status: z.number().int(),
  originName: text.nullish().transform((value) => value ?? ""),
  destination: destinationSchema,
  skuCount: count,
  pieceCount: count,
  palletGuid: nullableText,
  palletNo: nullableText,
  locationCode: nullableText,
  weightKg: nullableNumber,
  orderGuid: nullableText,
  orderNo: nullableText,
  createdByName: text.nullish().transform((value) => value ?? ""),
  createdAtUtc: text,
  sealedByName: nullableText,
  sealedAtUtc: nullableText,
  updatedAtUtc: text,
};
const cartonSummarySchema = z.object(cartonSummaryShape);

const candidateSchema = z.object({
  productCode: text.min(1),
  itemNumber: nullableText,
  productName: nullableText,
  barcode: nullableText,
  productImage: nullableText,
});

const cartonItemSchema = z.object({
  productCode: text.min(1),
  itemNumber: nullableText,
  productName: nullableText,
  barcode: nullableText,
  productImage: nullableText,
  quantity: number,
  middlePackageQuantity: nullableNumber,
  packingQuantity: nullableNumber,
  orderQuantity: nullableNumber,
  packedInOtherCartons: count,
  updatedAtUtc: text,
});

const cartonDetailSchema = z.object({
  ...cartonSummaryShape,
  remark: nullableText,
  items: z.array(cartonItemSchema).nullish().transform((value) => value ?? []),
  changedProductCode: nullableText,
});

const cartonListSchema = z.object({
  items: z.array(cartonSummarySchema).nullish().transform((value) => value ?? []),
  packingCount: count,
  sealedCount: count,
  looseSealedCount: count,
});

const palletSummaryShape = {
  palletGuid: text.min(1),
  palletNo: text.min(1),
  status: z.number().int(),
  originName: text.nullish().transform((value) => value ?? ""),
  destination: destinationSchema,
  consignmentNo: nullableText,
  locationCode: nullableText,
  weightKg: nullableNumber,
  cartonCount: count,
  skuCount: count,
  pieceCount: count,
  createdByName: text.nullish().transform((value) => value ?? ""),
  createdAtUtc: text,
  sealedByName: nullableText,
  sealedAtUtc: nullableText,
  updatedAtUtc: text,
};

const palletDetailSchema = z.object({
  ...palletSummaryShape,
  remark: nullableText,
  cartons: z.array(cartonSummarySchema).nullish().transform((value) => value ?? []),
});

const palletListSchema = z.object({
  items: z.array(z.object(palletSummaryShape)).nullish().transform((value) => value ?? []),
  buildingCount: count,
  sealedCount: count,
});

const resolveSchema = z.object({
  kind: z.enum(["carton", "pallet"]),
  guid: text.min(1),
  no: text.min(1),
});

const carrierSchema = z.object({
  carrierGuid: text.min(1),
  name: text,
  remark: nullableText,
  isActive: z.boolean().nullish().transform((value) => value ?? true),
});

const orderOptionSchema = z.object({
  orderGuid: text.min(1),
  orderNo: nullableText,
  storeCode: nullableText,
  storeName: nullableText,
  flowStatus: z.number().int().nullish().transform((value) => value ?? 0),
  orderDate: nullableText,
});

const locateSchema = z.object({
  product: candidateSchema,
  middlePackageQuantity: nullableNumber,
  packingQuantity: nullableNumber,
  stockQuantity: nullableNumber,
  packedQuantity: count,
  cartonCount: count,
  palletCount: count,
  rows: z
    .array(
      z.object({
        cartonGuid: text.min(1),
        cartonNo: text.min(1),
        cartonStatus: z.number().int(),
        palletGuid: nullableText,
        palletNo: nullableText,
        locationCode: nullableText,
        originName: text.nullish().transform((value) => value ?? ""),
        destination: destinationSchema,
        quantity: number,
        orderNo: nullableText,
        updatedAtUtc: text,
      }),
    )
    .nullish()
    .transform((value) => value ?? []),
});

function parse<T>(schema: z.ZodType<T>, payload: unknown): T {
  const result = schema.safeParse(unwrapApiEnvelope(payload));
  if (!result.success) {
    throw Object.assign(new Error("Invalid warehouse packing response"), { code: "WAREHOUSE_PACKING_INVALID_RESPONSE" });
  }
  return result.data;
}

export const normalizeCartonList = (payload: unknown): PackCartonList => parse(cartonListSchema, payload);
export const normalizeCartonDetail = (payload: unknown): PackCartonDetail => parse(cartonDetailSchema, payload);
export const normalizePalletList = (payload: unknown): PackPalletList => parse(palletListSchema, payload);
export const normalizePalletDetail = (payload: unknown): PackPalletDetail => parse(palletDetailSchema, payload);
export const normalizeResolve = (payload: unknown): PackResolveResult => parse(resolveSchema, payload);
export const normalizeCarrier = (payload: unknown): PackCarrier => parse(carrierSchema, payload);
export const normalizeCarriers = (payload: unknown): PackCarrier[] => parse(z.array(carrierSchema), payload);
export const normalizeOrderOptions = (payload: unknown): PackOrderOption[] => parse(z.array(orderOptionSchema), payload);
export const normalizeLocate = (payload: unknown): PackLocateResult => parse(locateSchema, payload);

export interface PackingErrorInfo {
  code: string | null;
  status: number | null;
  message: string | null;
  /** 409 CODE_AMBIGUOUS 带回的候选商品。 */
  candidates: PackProductCandidate[];
  /** 加入托盘被拦下时带回的那只箱。 */
  blockedCarton: PackCartonSummary | null;
}

/** 从 axios 错误里取后端 errorCode，以及冲突时附带的候选商品 / 被拦下的箱。 */
export function readPackingError(error: unknown): PackingErrorInfo {
  const response = (error as { response?: { status?: number; data?: unknown } } | null)?.response;
  const body = response?.data as { errorCode?: unknown; message?: unknown; data?: unknown } | undefined;
  const bodyCode = typeof body?.errorCode === "string" ? body.errorCode : null;
  const ownCode = (error as { code?: unknown } | null)?.code;
  const data = (body?.data ?? null) as { candidates?: unknown; blockedCarton?: unknown } | null;
  const candidates = z.array(candidateSchema).safeParse(data?.candidates);
  const blocked = cartonSummarySchema.safeParse(data?.blockedCarton);
  return {
    code: bodyCode ?? (typeof ownCode === "string" && /^[A-Z_]+$/.test(ownCode) ? ownCode : null),
    status: typeof response?.status === "number" ? response.status : null,
    message: typeof body?.message === "string" && body.message.trim() ? body.message.trim() : null,
    candidates: candidates.success ? candidates.data : [],
    blockedCarton: blocked.success ? blocked.data : null,
  };
}
