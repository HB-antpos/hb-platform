import type { LocalSupplierInvoiceItemDto } from '../../../../types/localSupplierInvoice'
import type { InvoiceDetailInlineEditableField } from './inlineEdit'

/**
 * 「未保存修改」只比较可在页面上编辑、要靠「保存修改」落库的字段。
 * 操作类型（activityType）改了会立即落库，商品检测结果由后端写回，都不算未保存。
 */
export const INVOICE_DETAIL_SAVED_FIELDS: readonly InvoiceDetailInlineEditableField[] = [
  'itemNumber',
  'barcode',
  'productName',
  'quantity',
  'purchasePrice',
  'retailPrice',
  'pricingFloatRate',
  'newAutoRetailPrice',
  'autoPricing',
  'isSpecialProduct',
  'discountRate',
]

export type InvoiceDetailSnapshotIndex = ReadonlyMap<string, LocalSupplierInvoiceItemDto>

export function buildInvoiceDetailSnapshotIndex(
  snapshot: readonly LocalSupplierInvoiceItemDto[],
): InvoiceDetailSnapshotIndex {
  return new Map(snapshot.map((item) => [item.detailGUID, item]))
}

function normalizeComparableValue(value: unknown) {
  // 空字符串、null、undefined 在落库语义上一致，比较前统一成 undefined。
  if (value === null || value === undefined || value === '') return undefined
  if (typeof value === 'number') return Math.round(value * 10000) / 10000
  return value
}

export function isInvoiceDetailFieldEdited(
  detail: LocalSupplierInvoiceItemDto,
  snapshotIndex: InvoiceDetailSnapshotIndex,
  field: InvoiceDetailInlineEditableField,
) {
  const original = snapshotIndex.get(detail.detailGUID)
  // 快照里没有的行只会来自服务端刷新的竞态，这里不当作用户修改。
  if (!original) return false
  return normalizeComparableValue(detail[field]) !== normalizeComparableValue(original[field])
}

export function getEditedInvoiceDetailFields(
  detail: LocalSupplierInvoiceItemDto,
  snapshotIndex: InvoiceDetailSnapshotIndex,
) {
  return INVOICE_DETAIL_SAVED_FIELDS.filter((field) => isInvoiceDetailFieldEdited(detail, snapshotIndex, field))
}

export function countEditedInvoiceDetailRows(
  details: readonly LocalSupplierInvoiceItemDto[],
  snapshotIndex: InvoiceDetailSnapshotIndex,
) {
  return details.reduce(
    (count, detail) => (getEditedInvoiceDetailFields(detail, snapshotIndex).length ? count + 1 : count),
    0,
  )
}
