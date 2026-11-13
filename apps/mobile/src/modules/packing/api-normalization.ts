import { z } from "zod";
import { unwrapApiEnvelope } from "../../shared/api/api-envelope";
import type {
  PackCarrier,
  PackCartonDetail,
  PackCartonList,
  PackCartonSummary,
  PackIncoming,
  PackLocateResult,
  PackLocation,
  PackOwner,
  PackPalletDetail,
  PackPalletList,
  PackProductCandidate,
  PackReceiveLookup,
  PackResolveResult,
  PackScope,
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
    code: nullableText,
    name: nullableText,
    carrierGuid: nullableText,
    carrierName: nullableText,
  })
  .nullish()
  .transform((value) => value ?? { destinationType: 0, code: null, name: null, carrierGuid: null, carrierName: null });

const emptyText = text.nullish().transform((value) => value ?? "");

const cartonSummaryShape = {
  cartonGuid: text.min(1),
  cartonNo: text.min(1),
  status: z.number().int(),
  ownerCode: emptyText,
  ownerName: emptyText,
  originCode: emptyText,
  originName: emptyText,
  destination: destinationSchema,
  skuCount: count,
  pieceCount: count,
  palletGuid: nullableText,
  palletNo: nullableText,
  locationCode: nullableText,
  weightKg: nullableNumber,
  orderGuid: nullableText,
  orderNo: nullableText,
  createdByName: emptyText,
  createdAtUtc: text,
  sealedByName: nullableText,
  sealedAtUtc: nullableText,
  dispatchedByName: nullableText,
  dispatchedAtUtc: nullableText,
  receivedByName: nullableText,
  receivedAtUtc: nullableText,
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
  storedCount: count,
  outgoingCount: count,
  incomingCount: count,
});

const palletSummaryShape = {
  palletGuid: text.min(1),
  palletNo: text.min(1),
  status: z.number().int(),
  ownerCode: emptyText,
  ownerName: emptyText,
  originCode: emptyText,
  originName: emptyText,
  destination: destinationSchema,
  consignmentNo: nullableText,
  locationCode: nullableText,
  weightKg: nullableNumber,
  cartonCount: count,
  inTransitCartonCount: count,
  skuCount: count,
  pieceCount: count,
  createdByName: emptyText,
  createdAtUtc: text,
  sealedByName: nullableText,
  sealedAtUtc: nullableText,
  dispatchedByName: nullableText,
  dispatchedAtUtc: nullableText,
  receivedByName: nullableText,
  receivedAtUtc: nullableText,
  updatedAtUtc: text,
};

const palletDetailSchema = z.object({
  ...palletSummaryShape,
  remark: nullableText,
  cartons: z.array(cartonSummarySchema).nullish().transform((value) => value ?? []),
  actualInTransitCartonCount: nullableNumber,
});

const palletListSchema = z.object({
  items: z.array(z.object(palletSummaryShape)).nullish().transform((value) => value ?? []),
  buildingCount: count,
  storedCount: count,
});

const resolveSchema = z.object({
  kind: z.enum(["carton", "pallet"]),
  guid: text.min(1),
  no: text.min(1),
  palletGuid: nullableText,
});

// 账号能操作的地点，以及「可发往的地点」候选，结构相同：{ code, name, isWarehouse }。
const ownerSchema = z.object({
  code: text.min(1),
  name: emptyText,
  isWarehouse: z.boolean().nullish().transform((value) => value ?? false),
});

const scopeSchema = z.object({
  owners: z
    .array(ownerSchema)
    .nullish()
    .transform((value) => value ?? []),
  isManager: z.boolean().nullish().transform((value) => value ?? false),
  isAdmin: z.boolean().nullish().transform((value) => value ?? false),
});

const locationSchema = z.object({
  locationGuid: text.min(1),
  ownerCode: emptyText,
  name: text,
  barcode: emptyText,
  isActive: z.boolean().nullish().transform((value) => value ?? true),
});

const incomingItemSchema = z.object({
  kind: z.enum(["pallet", "carton"]),
  guid: text.min(1),
  no: text.min(1),
  originCode: emptyText,
  originName: emptyText,
  carrierName: nullableText,
  consignmentNo: nullableText,
  cartonCount: count,
  receivedCartonCount: count,
  pieceCount: count,
  dispatchedAtUtc: nullableText,
  dispatchedByName: nullableText,
  receivedAtUtc: nullableText,
  receivedByName: nullableText,
});

const incomingSchema = z.object({
  pending: z.array(incomingItemSchema).nullish().transform((value) => value ?? []),
  recent: z.array(incomingItemSchema).nullish().transform((value) => value ?? []),
});

const receiveLookupSchema = z.object({
  kind: z.enum(["pallet", "carton"]),
  pallet: palletDetailSchema.nullish().transform((value) => value ?? null),
  carton: cartonDetailSchema.nullish().transform((value) => value ?? null),
  scannedCartonGuid: nullableText,
  scannedCartonNo: nullableText,
  viaCartonLabel: z.boolean().nullish().transform((value) => value ?? false),
  inTransitCartonCount: count,
  canReceive: z.boolean().nullish().transform((value) => value ?? false),
  blockReason: z
    .enum(["NOT_IN_TRANSIT", "NOT_FOR_YOU"])
    .nullish()
    .transform((value) => value ?? null),
});

const carrierSchema = z.object({
  carrierGuid: text.min(1),
  name: text,
  remark: nullableText,
  isActive: z.boolean().nullish().transform((value) => value ?? true),
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
        ownerCode: emptyText,
        ownerName: emptyText,
        originName: emptyText,
        destination: destinationSchema,
        quantity: number,
        orderNo: nullableText,
        updatedAtUtc: text,
        group: z.enum(["stored", "transit", "elsewhere", "other"]).catch("other"),
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
export const normalizeScope = (payload: unknown): PackScope => parse(scopeSchema, payload);
export const normalizeDestinations = (payload: unknown): PackOwner[] => parse(z.array(ownerSchema), payload);
export const normalizeLocations = (payload: unknown): PackLocation[] => parse(z.array(locationSchema), payload);
export const normalizeLocation = (payload: unknown): PackLocation => parse(locationSchema, payload);
export const normalizeIncoming = (payload: unknown): PackIncoming => parse(incomingSchema, payload);
export const normalizeReceiveLookup = (payload: unknown): PackReceiveLookup => parse(receiveLookupSchema, payload);
export const normalizeLocate = (payload: unknown): PackLocateResult => parse(locateSchema, payload);

export interface PackingErrorInfo {
  code: string | null;
  status: number | null;
  message: string | null;
  /** 409 CODE_AMBIGUOUS 带回的候选商品。 */
  candidates: PackProductCandidate[];
  /** 加入托盘被拦下时带回的那只箱。 */
  blockedCarton: PackCartonSummary | null;
  /** 整托签收数量对不上（COUNT_MISMATCH）时，托盘上实际还在路上的箱数。 */
  actualInTransitCartonCount: number | null;
}

/** 从 axios 错误里取后端 errorCode，以及冲突时附带的候选商品 / 被拦下的箱。 */
export function readPackingError(error: unknown): PackingErrorInfo {
  const response = (error as { response?: { status?: number; data?: unknown } } | null)?.response;
  const body = response?.data as { errorCode?: unknown; message?: unknown; data?: unknown } | undefined;
  const bodyCode = typeof body?.errorCode === "string" ? body.errorCode : null;
  const ownCode = (error as { code?: unknown } | null)?.code;
  const data = (body?.data ?? null) as {
    candidates?: unknown;
    blockedCarton?: unknown;
    actualInTransitCartonCount?: unknown;
  } | null;
  const candidates = z.array(candidateSchema).safeParse(data?.candidates);
  const blocked = cartonSummarySchema.safeParse(data?.blockedCarton);
  return {
    code: bodyCode ?? (typeof ownCode === "string" && /^[A-Z_]+$/.test(ownCode) ? ownCode : null),
    status: typeof response?.status === "number" ? response.status : null,
    message: typeof body?.message === "string" && body.message.trim() ? body.message.trim() : null,
    candidates: candidates.success ? candidates.data : [],
    blockedCarton: blocked.success ? blocked.data : null,
    actualInTransitCartonCount:
      typeof data?.actualInTransitCartonCount === "number" ? data.actualInTransitCartonCount : null,
  };
}
