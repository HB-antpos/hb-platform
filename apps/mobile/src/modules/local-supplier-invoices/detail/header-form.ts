import type { LocalSupplierInvoice, UpdateInvoiceHeaderRequest } from "@/modules/local-supplier-invoices/types";

/**
 * 进货单表头编辑（与 Web InvoiceEdit/invoiceHeaderForm.ts 对齐）。
 * 可编辑字段只有 storeCode / supplierCode / orderDate / inboundDate / remarks；
 * 日期在手机端用 "YYYY-MM-DD" 字符串（无 dayjs），保存时只提交日期部分。
 */
export interface InvoiceHeaderFormValues {
  storeCode: string;
  supplierCode: string;
  orderDate: string;
  inboundDate: string;
  remarks: string;
}

export const INVOICE_HEADER_EDITABLE_FIELDS = [
  "storeCode",
  "supplierCode",
  "orderDate",
  "inboundDate",
  "remarks",
] as const;

type HeaderSource = Pick<LocalSupplierInvoice, "storeCode" | "supplierCode" | "orderDate" | "inboundDate" | "remarks">;

const DATE_PREFIX = /^(\d{4})-(\d{2})-(\d{2})/;

/** 是否为真实存在的 "YYYY-MM-DD"（拒绝 2026-02-30 这类）。 */
export function isValidDateOnly(value: string): boolean {
  const match = /^(\d{4})-(\d{2})-(\d{2})$/.exec(value);
  if (!match) return false;
  const year = Number(match[1]);
  const month = Number(match[2]);
  const day = Number(match[3]);
  const date = new Date(Date.UTC(year, month - 1, day));
  return date.getUTCFullYear() === year && date.getUTCMonth() === month - 1 && date.getUTCDate() === day;
}

/** 后端日期 ("2026-07-18T00:00:00Z" 等) → "YYYY-MM-DD"；空值或无法识别返回空串。 */
export function normalizeDateOnly(value: string | null | undefined): string {
  const match = DATE_PREFIX.exec((value ?? "").trim());
  return match ? `${match[1]}-${match[2]}-${match[3]}` : "";
}

export function buildHeaderFormValues(invoice: HeaderSource): InvoiceHeaderFormValues {
  return {
    storeCode: invoice.storeCode ?? "",
    supplierCode: invoice.supplierCode ?? "",
    orderDate: normalizeDateOnly(invoice.orderDate),
    inboundDate: normalizeDateOnly(invoice.inboundDate),
    remarks: invoice.remarks ?? "",
  };
}

/**
 * 保存 payload：文本 trim，空串省略（undefined）；日期只取 "YYYY-MM-DD"，无法识别的省略。
 * 与 Web 一致，空值 = 本次不提交该字段（因此无法通过表头把已有日期 / 备注清空）。
 */
export function buildHeaderSavePayload(values: Partial<InvoiceHeaderFormValues>): UpdateInvoiceHeaderRequest {
  return {
    storeCode: values.storeCode?.trim() || undefined,
    supplierCode: values.supplierCode?.trim() || undefined,
    orderDate: normalizeDateOnly(values.orderDate) || undefined,
    inboundDate: normalizeDateOnly(values.inboundDate) || undefined,
    remarks: values.remarks?.trim() || undefined,
  };
}

/**
 * 表头是否有未保存修改：把当前表单值和服务端订单头都换算成保存 payload 再逐项比较，
 * 这样日期格式、首尾空格、空备注这类不影响落库的差异不会被误报成修改。
 */
export function isHeaderDirty(
  values: Partial<InvoiceHeaderFormValues> | undefined,
  invoice: HeaderSource | null | undefined
): boolean {
  if (!invoice || !values) return false;
  const current = buildHeaderSavePayload(values);
  const original = buildHeaderSavePayload(buildHeaderFormValues(invoice));
  return INVOICE_HEADER_EDITABLE_FIELDS.some((field) => (current[field] ?? undefined) !== (original[field] ?? undefined));
}
