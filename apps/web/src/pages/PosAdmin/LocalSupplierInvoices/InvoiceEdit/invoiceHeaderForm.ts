import dayjs, { type Dayjs } from 'dayjs'
import type {
  LocalSupplierInvoiceDetailDto,
  UpdateInvoiceRequest,
} from '../../../../types/localSupplierInvoice'

export interface InvoiceHeaderFormValues {
  invoiceNo?: string
  storeCode?: string
  supplierCode?: string
  orderDate?: Dayjs
  inboundDate?: Dayjs
  totalAmount: string
  remarks?: string
}

export interface InvoiceHeaderSelectOption {
  label: string
  value: string
  disabled?: boolean
}

function formatAmount(value?: number) {
  if (value === undefined || value === null) return '--'
  return value.toFixed(2)
}

export function buildInvoiceHeaderFormValues(data: LocalSupplierInvoiceDetailDto): InvoiceHeaderFormValues {
  return {
    invoiceNo: data.invoiceNo,
    storeCode: data.storeCode,
    supplierCode: data.supplierCode,
    orderDate: data.orderDate ? dayjs(data.orderDate) : undefined,
    inboundDate: data.inboundDate ? dayjs(data.inboundDate) : undefined,
    totalAmount: formatAmount(data.totalAmount),
    remarks: data.remarks,
  }
}

export function buildInvoiceHeaderSavePayload(values: InvoiceHeaderFormValues): UpdateInvoiceRequest {
  return {
    storeCode: values.storeCode?.trim() || undefined,
    supplierCode: values.supplierCode?.trim() || undefined,
    orderDate: values.orderDate?.format('YYYY-MM-DD'),
    inboundDate: values.inboundDate?.format('YYYY-MM-DD'),
    remarks: values.remarks?.trim() || undefined,
  }
}

export function includeCurrentInvoiceHeaderOption(
  options: InvoiceHeaderSelectOption[],
  currentCode: string | undefined,
  currentName: string | undefined,
  disabled = false,
): InvoiceHeaderSelectOption[] {
  const code = currentCode?.trim()
  if (!code || options.some((option) => option.value === code)) {
    return options
  }

  const name = currentName?.trim()
  // 当前订单可能引用已停用或本轮选项加载失败的数据，保留只用于回显的兜底项。
  return [
    {
      value: code,
      label: name ? `${code} - ${name}` : code,
      disabled,
    },
    ...options,
  ]
}

const INVOICE_HEADER_SAVED_FIELDS = ['storeCode', 'supplierCode', 'orderDate', 'inboundDate', 'remarks'] as const

/**
 * 表头是否有未保存修改：把当前表单值和服务端订单头都换算成保存 payload 再逐项比较，
 * 这样日期格式、首尾空格、空备注这类不影响落库的差异不会被误报成修改。
 */
export function isInvoiceHeaderDirty(
  values: Partial<InvoiceHeaderFormValues> | undefined,
  invoice: LocalSupplierInvoiceDetailDto | null,
) {
  if (!invoice || !values) return false
  const current = buildInvoiceHeaderSavePayload({ totalAmount: '', ...values })
  const original = buildInvoiceHeaderSavePayload(buildInvoiceHeaderFormValues(invoice))
  return INVOICE_HEADER_SAVED_FIELDS.some((field) => (current[field] ?? undefined) !== (original[field] ?? undefined))
}
