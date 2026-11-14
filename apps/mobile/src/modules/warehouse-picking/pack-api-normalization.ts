import { z } from "zod";
import { unwrapApiEnvelope } from "../../shared/api/api-envelope";
import { normalizeCartonDetail } from "../packing/api-normalization";
import type { CartonSync, PackMismatch, PackOpenCarton, PackProgress, PackSetItemResult, PackSubmitBlock } from "./pack-types";

const number = z.number().finite();
const text = z.string();
// 后端统一 WhenWritingNull，空值字段会被省略。
const nullableText = text.nullish().transform((value) => value ?? null);
const nullableNumber = number.nullish().transform((value) => value ?? null);
const count = number.nullish().transform((value) => value ?? 0);
const emptyText = text.nullish().transform((value) => value ?? "");
const flag = z.boolean().nullish().transform((value) => value ?? false);

export const cartonSyncSchema = z.object({
  cartonGuid: text.min(1),
  cartonNo: text.min(1),
  skuCount: count,
  pieceCount: count,
  itemPieces: count,
});

const progressSchema = z.object({
  orderGuid: text.min(1),
  orderNo: nullableText,
  storeCode: nullableText,
  storeName: nullableText,
  orderDate: nullableText,
  flowStatus: z.number().int(),
  sessionStatus: nullableNumber,
  lines: z
    .array(
      z.object({
        productCode: text.min(1),
        productName: nullableText,
        itemNumber: nullableText,
        orderedPieces: count,
        pickedPieces: count,
        packedPieces: count,
      }),
    )
    .nullish()
    .transform((value) => value ?? []),
  cartons: z
    .array(
      z.object({
        cartonGuid: text.min(1),
        cartonNo: text.min(1),
        status: z.number().int(),
        skuCount: count,
        pieceCount: count,
        palletNo: nullableText,
        createdByName: emptyText,
        createdAtUtc: nullableText,
        isMine: flag,
        // 只有装箱中的箱带；[]（空箱）与缺失（null）要区分开，所以不能把缺失补成 []。
        items: z
          .array(z.object({ productCode: text.min(1), quantity: count }))
          .nullish()
          .transform((value) => value ?? null),
      }),
    )
    .nullish()
    .transform((value) => value ?? []),
  openCartonCount: count,
  emptyCartonCount: count,
  sealedCartonCount: count,
  reconciled: flag,
});

const setItemSchema = z.object({
  carton: z.unknown(),
  overPacked: flag,
  overPackedBy: count,
});

const openCartonSchema = z.object({
  cartonGuid: text.min(1),
  cartonNo: text.min(1),
  skuCount: count,
  pieceCount: count,
});

const mismatchSchema = z.object({
  productCode: text.min(1),
  productName: nullableText,
  itemNumber: nullableText,
  detailGuids: z.array(text).nullish().transform((value) => value ?? []),
  picked: count,
  packed: count,
  diff: number.nullish(),
});

function parseOrThrow<T>(schema: z.ZodType<T>, payload: unknown, label: string): T {
  const result = schema.safeParse(unwrapApiEnvelope(payload));
  if (!result.success) {
    // 结构不符按接口失败处理：宁可报错，也不能把缺字段当成 0 件显示给拣货员。
    throw Object.assign(new Error(`Invalid warehouse picking ${label} response`), {
      code: "WAREHOUSE_PICKING_INVALID_RESPONSE",
    });
  }
  return result.data;
}

export const normalizePackProgress = (payload: unknown): PackProgress => parseOrThrow(progressSchema, payload, "pack progress");

export function normalizeSetCartonItem(payload: unknown): PackSetItemResult {
  const parsed = parseOrThrow(setItemSchema, payload, "carton item");
  return {
    carton: normalizeCartonDetail(parsed.carton),
    overPacked: parsed.overPacked,
    overPackedBy: parsed.overPackedBy,
  };
}

/** 边拣边装写入响应里的 carton；没有（不装箱的请求）或格式不对都按「没有」处理，不影响拣货结果。 */
export function readCartonSync(value: unknown): CartonSync | null {
  const parsed = cartonSyncSchema.safeParse(value);
  return parsed.success ? parsed.data : null;
}

/**
 * 提交被装箱拦下（409 PACKING_OPEN_CARTONS / PACKING_NOT_RECONCILED）时，从错误的 data 里取出明细。
 * 返回 null 表示这不是装箱拦截。diff 缺失时按 picked - packed 补。
 */
export function readPackSubmitBlock(code: string | null, data: unknown): PackSubmitBlock | null {
  const body = (data ?? {}) as { openCartons?: unknown; mismatches?: unknown };
  if (code === "PACKING_OPEN_CARTONS") {
    const parsed = z.array(openCartonSchema).safeParse(body.openCartons);
    const openCartons: PackOpenCarton[] = parsed.success ? parsed.data : [];
    return { kind: "openCartons", openCartons };
  }
  if (code === "PACKING_NOT_RECONCILED") {
    const parsed = z.array(mismatchSchema).safeParse(body.mismatches);
    const mismatches: PackMismatch[] = parsed.success
      ? parsed.data.map((item) => ({ ...item, diff: item.diff ?? item.picked - item.packed }))
      : [];
    return { kind: "notReconciled", mismatches };
  }
  return null;
}
