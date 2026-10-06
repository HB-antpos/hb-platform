import type { LocalSupplierInvoiceItemDto } from '../../../../types/localSupplierInvoice'
import { discountRateToPercent } from '../../../../utils/discountRate'
import type { InvoiceDetailInlineEditableField } from './inlineEdit'

export interface PricingEditorChange {
  field: InvoiceDetailInlineEditableField
  value: unknown
}

function isSameNumber(left: number | null | undefined, right: number | null | undefined) {
  // null 与 undefined 都表示「没有值」，视为相同。
  if (left == null || right == null) return (left ?? null) === (right ?? null)
  return Math.abs(left - right) < 0.0001
}

/**
 * 只返回真正改动过的字段；数字留空表示「不改」，不能把空值兜底成 0 落库。
 * 折扣率以百分比交给行内编辑的规整逻辑，由它换算成小数。
 */
export function buildPricingEditorChanges(
  detail: LocalSupplierInvoiceItemDto,
  draft: {
    autoPricing: boolean
    pricingFloatRate: number | null
    newAutoRetailPrice: number | null
    isSpecialProduct: boolean
    discountPercent: number | null
  },
): PricingEditorChange[] {
  const changes: PricingEditorChange[] = []
  if (draft.autoPricing !== Boolean(detail.autoPricing)) {
    changes.push({ field: 'autoPricing', value: draft.autoPricing })
  }
  if (draft.pricingFloatRate != null && !isSameNumber(draft.pricingFloatRate, detail.pricingFloatRate)) {
    changes.push({ field: 'pricingFloatRate', value: draft.pricingFloatRate })
  }
  if (draft.newAutoRetailPrice != null && !isSameNumber(draft.newAutoRetailPrice, detail.newAutoRetailPrice)) {
    changes.push({ field: 'newAutoRetailPrice', value: draft.newAutoRetailPrice })
  }
  if (draft.isSpecialProduct !== Boolean(detail.isSpecialProduct)) {
    changes.push({ field: 'isSpecialProduct', value: draft.isSpecialProduct })
  }
  if (draft.discountPercent != null && !isSameNumber(draft.discountPercent, discountRateToPercent(detail.discountRate))) {
    changes.push({ field: 'discountRate', value: draft.discountPercent })
  }
  return changes
}
