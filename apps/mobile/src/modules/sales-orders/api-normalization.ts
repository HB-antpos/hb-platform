import { z } from "zod";
import { unwrapApiEnvelope } from "@/shared/api/api-envelope";
import { validateSalesOrderRange } from "./logic";
import type {
  SalesOrderBranchCatalog,
  SalesOrderDetail,
  SalesOrderListPage,
  SalesOrderReceiptLink,
  SalesOrderReceiptView,
} from "./types";

const number = z.number().finite();
const text = z.string();
// 后端统一使用 WhenWritingNull，合法的空字段会被省略。
const nullableText = text.nullish().transform((value) => value ?? null);
const nullableNumber = number.nullish().transform((value) => value ?? null);
const scope = z.enum(["all-stores", "authorized-stores"]);

const orderSchema = z.object({
  orderGuid: text.min(1),
  branchCode: nullableText,
  branchName: nullableText,
  deviceCode: nullableText,
  orderTime: nullableText,
  skuCount: nullableNumber,
  itemCount: nullableNumber,
  totalAmount: nullableNumber,
  discountAmount: nullableNumber,
  actualAmount: nullableNumber,
  status: nullableNumber,
  // 旧后端不返回该字段，缺省按不含退款商品。
  hasRefundItems: z.boolean().nullish().transform((value) => value ?? false),
});

const listItemSchema = orderSchema.extend({
  quantityTotal: nullableNumber,
  matchedProducts: z
    .array(
      z.object({
        productCode: text,
        itemNumber: nullableText,
        productName: nullableText,
        barcode: nullableText,
        quantity: number,
      }),
    )
    .nullish()
    .transform((value) => value ?? []),
});

const listPageSchema = z.object({
  items: z.array(listItemSchema),
  total: z.number().int().nonnegative(),
  pageNumber: z.number().int().positive(),
  pageSize: z.number().int().positive(),
  scope,
  range: z
    .object({ startDate: text, endDate: text, dayCount: z.number().int() })
    .refine((value) => validateSalesOrderRange(value).ok),
  sortDirection: z.enum(["asc", "desc"]),
});

const branchesSchema = z.object({
  scope,
  branches: z.array(z.object({ storeCode: text.min(1), storeName: nullableText })),
});

const detailSchema = z.object({
  order: orderSchema,
  orderDetails: z
    .array(
      z.object({
        productImage: nullableText,
        productCode: nullableText,
        itemNumber: nullableText,
        productName: nullableText,
        quantity: nullableNumber,
        unitPrice: nullableNumber,
        discountAmount: nullableNumber,
        actualAmount: nullableNumber,
      }),
    )
    .nullish()
    .transform((value) => value ?? []),
  paymentDetails: z
    .array(
      z.object({
        paymentTime: nullableText,
        paymentMethod: nullableNumber,
        paymentMethodName: nullableText,
        amount: nullableNumber,
      }),
    )
    .nullish()
    .transform((value) => value ?? []),
});

function invalid(message: string) {
  return Object.assign(new Error(message), { code: "SALES_ORDER_INVALID_RESPONSE" });
}

export function normalizeSalesOrderListPage(payload: unknown): SalesOrderListPage {
  const parsed = listPageSchema.safeParse(unwrapApiEnvelope(payload));
  if (!parsed.success) throw invalid("Sales order list payload is invalid");
  return parsed.data;
}

export function normalizeSalesOrderBranchCatalog(payload: unknown): SalesOrderBranchCatalog {
  const parsed = branchesSchema.safeParse(unwrapApiEnvelope(payload));
  if (!parsed.success) throw invalid("Sales order branch payload is invalid");
  return {
    scope: parsed.data.scope,
    branches: parsed.data.branches.map((branch) => ({
      storeCode: branch.storeCode,
      // 门店名缺失时退回编码，筛选面板不能出现空白 chip。
      storeName: branch.storeName?.trim() || branch.storeCode,
    })),
  };
}

export function normalizeSalesOrderDetail(payload: unknown): SalesOrderDetail {
  const parsed = detailSchema.safeParse(unwrapApiEnvelope(payload));
  if (!parsed.success) throw invalid("Sales order detail payload is invalid");
  return {
    order: { ...parsed.data.order, quantityTotal: null },
    lines: parsed.data.orderDetails,
    payments: parsed.data.paymentDetails,
  };
}

const receiptLineSchema = z.object({
  kind: z.enum(["text", "separator", "blank", "barcode", "qr"]),
  // 空行的 text 是空串，后端 WhenWritingNull 也可能省略。
  text: text.nullish().transform((value) => value ?? ""),
  align: z.enum(["left", "center"]).nullish().transform((value) => value ?? "left"),
  bold: z.boolean().nullish().transform((value) => value ?? false),
});

const receiptLinkSchema = z.object({
  url: text.min(1),
  expiresAtUtc: text,
  createdAtUtc: text,
  openCount: z.number().int().nonnegative(),
});

const receiptViewSchema = z.object({
  document: z.object({
    orderGuid: text.min(1),
    shortCode: text,
    statusKind: z.enum(["paid", "refund"]),
    lines: z.array(receiptLineSchema),
  }),
  link: receiptLinkSchema.nullish().transform((value) => value ?? null),
});

/** 员工端小票：完整纸面 + 当前有效链接（没有则为 null）。 */
export function normalizeSalesOrderReceiptView(payload: unknown): SalesOrderReceiptView {
  const parsed = receiptViewSchema.safeParse(unwrapApiEnvelope(payload));
  if (!parsed.success) throw invalid("Sales order receipt payload is invalid");
  return parsed.data;
}

/** 生成 / 重新生成链接后的响应。 */
export function normalizeSalesOrderReceiptLink(payload: unknown): SalesOrderReceiptLink {
  const parsed = receiptLinkSchema.safeParse(unwrapApiEnvelope(payload));
  if (!parsed.success) throw invalid("Sales order receipt link payload is invalid");
  return parsed.data;
}
