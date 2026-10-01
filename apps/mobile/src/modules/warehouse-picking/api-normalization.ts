import { z } from "zod";
import { unwrapApiEnvelope } from "../../shared/api/api-envelope";
import type {
  PickCodeLookupResult,
  PickerResolveResult,
  PickLineMutationResult,
  PickOrderList,
  PickProgress,
  PickSheet,
  PickSlipClaim,
  PickSubmitResult,
} from "./types";

const number = z.number().finite();
const text = z.string();
// 后端统一 WhenWritingNull，空值字段会被省略。
const nullableText = text.nullish().transform((value) => value ?? null);
const nullableNumber = number.nullish().transform((value) => value ?? null);

const pickedBySchema = z.object({
  pickerUserGuid: text,
  pickerName: text,
  quantity: number,
});

const sessionSchema = z.object({
  status: z.number().int(),
  startedAtUtc: text,
  startedByName: text.nullish().transform((value) => value ?? ""),
  submittedAtUtc: nullableText,
  submittedByName: nullableText,
});

const participantSchema = z.object({
  pickerUserGuid: text,
  pickerName: text,
  lastActiveAtUtc: text,
  lastDetailGuid: nullableText,
});

// 没有有效标记时后端省略该字段。
const stockoutSchema = z
  .object({
    reason: z.number().int(),
    markedByName: text,
    markedAtUtc: text,
    pickedAtMark: number,
  })
  .nullish()
  .transform((value) => value ?? null);

const lineProgressSchema = z.object({
  detailGuid: text.min(1),
  pickedTotal: number,
  pickedBy: z.array(pickedBySchema).nullish().transform((value) => value ?? []),
  minOrderQuantity: nullableNumber,
  stockout: stockoutSchema,
  assigneeUserGuid: nullableText,
  assigneeName: nullableText,
  assignmentSegmentNo: nullableNumber,
});

const sheetSchema = z.object({
  orderGuid: text.min(1),
  orderNo: nullableText,
  storeCode: nullableText,
  storeName: nullableText,
  orderDate: nullableText,
  flowStatus: z.number().int(),
  session: sessionSchema,
  lines: z.array(
    z.object({
      detailGuid: text.min(1),
      productCode: text,
      itemNumber: nullableText,
      barcode: nullableText,
      productName: nullableText,
      productImage: nullableText,
      locationCode: nullableText,
      orderedQuantity: number,
      minOrderQuantity: nullableNumber,
      isSet: z.boolean(),
      setChildren: z
        .array(
          z.object({
            productCode: text,
            itemNumber: nullableText,
            barcode: nullableText,
            productName: nullableText,
          }),
        )
        .nullish()
        .transform((value) => value ?? []),
      pickedTotal: number,
      pickedBy: z.array(pickedBySchema).nullish().transform((value) => value ?? []),
      stockout: stockoutSchema,
      assigneeUserGuid: nullableText,
      assigneeName: nullableText,
      assignmentSegmentNo: nullableNumber,
    }),
  ),
  codes: z.array(
    z.object({
      code: text,
      target: z.enum(["line", "location"]),
      detailGuids: z.array(text),
      matchedBy: nullableNumber,
      label: nullableText,
    }),
  ),
  participants: z.array(participantSchema).nullish().transform((value) => value ?? []),
  serverTimeUtc: text,
});

const progressSchema = z.object({
  session: sessionSchema,
  lines: z.array(lineProgressSchema),
  participants: z.array(participantSchema).nullish().transform((value) => value ?? []),
  serverTimeUtc: text,
});

const mutationSchema = z.object({
  line: lineProgressSchema,
  appliedDelta: number,
  duplicate: z.boolean().nullish().transform((value) => value ?? false),
});

const orderListSchema = z.object({
  items: z.array(
    z.object({
      orderGuid: text.min(1),
      orderNo: nullableText,
      storeCode: nullableText,
      storeName: nullableText,
      orderDate: nullableText,
      flowStatus: z.number().int(),
      lineCount: number,
      totalQuantity: number,
      pickedLineCount: number,
      sessionStatus: nullableNumber,
      pickers: z
        .array(z.object({ pickerUserGuid: text, pickerName: text }))
        .nullish()
        .transform((value) => value ?? []),
      assignees: z
        .array(z.object({ pickerUserGuid: nullableText, pickerName: nullableText, lineCount: number, segmentNo: number }))
        .nullish()
        .transform((value) => value ?? []),
    }),
  ),
  counts: z.object({ all: number, toPick: number, picking: number, mine: nullableNumber }),
});

const slipClaimSchema = z.object({
  orderGuid: text.min(1),
  orderNo: nullableText,
  segmentNo: number,
  segmentCount: number,
  pickerUserGuid: nullableText,
  pickerName: nullableText,
  lineCount: number,
  claimedByMe: z.boolean().nullish().transform((value) => value ?? false),
  claimedNow: z.boolean().nullish().transform((value) => value ?? false),
});

const pickerResolveSchema = z.object({
  pickerUserGuid: text.min(1),
  pickerName: text,
  roleLabel: nullableText,
  ticket: text.min(1),
  expiresAtUtc: text,
});

const submitSchema = z.object({
  submittedAtUtc: text,
  submittedByName: text,
  lineCount: number,
  shortLineCount: number,
  overLineCount: number,
});

const lookupSchema = z.object({
  productCode: text,
  productName: nullableText,
  itemNumber: nullableText,
  barcode: nullableText,
  locationCode: nullableText,
  productImage: nullableText,
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

export const normalizePickSheet = (payload: unknown): PickSheet => parseOrThrow(sheetSchema, payload, "sheet");
export const normalizePickProgress = (payload: unknown): PickProgress => parseOrThrow(progressSchema, payload, "progress");
export const normalizePickMutation = (payload: unknown): PickLineMutationResult =>
  parseOrThrow(mutationSchema, payload, "mutation");
export const normalizePickOrderList = (payload: unknown): PickOrderList =>
  parseOrThrow(orderListSchema, payload, "order list");
export const normalizePickerResolve = (payload: unknown): PickerResolveResult =>
  parseOrThrow(pickerResolveSchema, payload, "picker");
export const normalizePickSubmit = (payload: unknown): PickSubmitResult => parseOrThrow(submitSchema, payload, "submit");
export const normalizePickLookup = (payload: unknown): PickCodeLookupResult => parseOrThrow(lookupSchema, payload, "lookup");
export const normalizePickSlipClaim = (payload: unknown): PickSlipClaim => parseOrThrow(slipClaimSchema, payload, "slip claim");

/** 从 axios 错误里取后端 errorCode 与冲突时附带的最新数据（409 PICKED_TOTAL_CHANGED 等）。 */
export function readPickingError(error: unknown): { code: string | null; status: number | null; data: unknown } {
  const response = (error as { response?: { status?: number; data?: unknown } } | null)?.response;
  const body = response?.data as { errorCode?: unknown; data?: unknown } | undefined;
  const bodyCode = typeof body?.errorCode === "string" ? body.errorCode : null;
  const ownCode = (error as { code?: unknown } | null)?.code;
  return {
    code: bodyCode ?? (typeof ownCode === "string" && /^[A-Z_]+$/.test(ownCode) ? ownCode : null),
    status: typeof response?.status === "number" ? response.status : null,
    data: body?.data ?? null,
  };
}
