import type { LocalSupplierInvoiceItemDto } from '../../../../types/localSupplierInvoice'
import { DetailAction } from '../../../../types/localSupplierInvoice'

export type PriceFilter = 'all' | 'up' | 'down'
export type ProductStatusFilter = 'notDetected' | 'exists' | 'notExists'
export type BarcodeStatusFilter = 'notDetected' | 'normal' | 'noMatch' | 'multiMatch'
/** 筛选专用的组合值：条码异常 = 无匹配 + 多匹配，供工作台「条码异常」快捷筛选使用。 */
export type BarcodeStatusFilterValue = StatusFilterValue<BarcodeStatusFilter> | 'abnormal'
export type ActionTypeFilter =
  | DetailAction.None
  | DetailAction.CreateProduct
  | DetailAction.UpdatePurchasePrice
  | DetailAction.WaitForOperation
  | DetailAction.UpdateItemNumber
  | DetailAction.AddMultiCode
export type StatusFilterValue<T extends string | number> = 'all' | T
export type ActionTypeFilterValue = 'all' | ActionTypeFilter

export interface InvoiceDetailFilters {
  searchText: string
  priceFilter: PriceFilter
  productStatusFilter: StatusFilterValue<ProductStatusFilter>
  barcodeStatusFilter: BarcodeStatusFilterValue
  actionTypeFilter?: ActionTypeFilterValue
  rowActions?: Record<string, number>
}

export interface DetailStatusStats {
  product: Record<ProductStatusFilter, number>
  barcode: Record<BarcodeStatusFilter, number>
  action: Record<ActionTypeFilter, number>
}

export const actionTypeFilters = [
  DetailAction.None,
  DetailAction.CreateProduct,
  DetailAction.UpdatePurchasePrice,
  DetailAction.WaitForOperation,
  DetailAction.UpdateItemNumber,
  DetailAction.AddMultiCode,
] as const

export function getProductStatusFilter(detail: LocalSupplierInvoiceItemDto): ProductStatusFilter {
  const count = detail.existingProductCount
  if (count === undefined || count === null) return 'notDetected'
  if (count > 0) return 'exists'
  return 'notExists'
}

export function getBarcodeStatusFilter(detail: LocalSupplierInvoiceItemDto): BarcodeStatusFilter {
  const status = detail.barcodeStatus
  const count = detail.barcodeMatchCount ?? 0
  if (status === undefined || status === null || status === 0) return 'notDetected'
  if (status === 1) return 'normal'
  if (count === 0) return 'noMatch'
  return 'multiMatch'
}

export function isBarcodeStatusAbnormal(detail: LocalSupplierInvoiceItemDto) {
  const status = getBarcodeStatusFilter(detail)
  return status === 'noMatch' || status === 'multiMatch'
}

export function getActionTypeFilter(
  detail: LocalSupplierInvoiceItemDto,
  rowActions: Record<string, number> = {},
): ActionTypeFilter {
  const action = rowActions[detail.detailGUID] ?? detail.activityType ?? DetailAction.None
  return isActionTypeFilter(action) ? action : DetailAction.None
}

export function getDetailStatusStats(
  details: LocalSupplierInvoiceItemDto[],
  rowActions: Record<string, number> = {},
): DetailStatusStats {
  const stats: DetailStatusStats = {
    product: {
      notDetected: 0,
      exists: 0,
      notExists: 0,
    },
    barcode: {
      notDetected: 0,
      normal: 0,
      noMatch: 0,
      multiMatch: 0,
    },
    action: {
      [DetailAction.None]: 0,
      [DetailAction.CreateProduct]: 0,
      [DetailAction.UpdatePurchasePrice]: 0,
      [DetailAction.WaitForOperation]: 0,
      [DetailAction.UpdateItemNumber]: 0,
      [DetailAction.AddMultiCode]: 0,
    },
  }

  details.forEach((item) => {
    stats.product[getProductStatusFilter(item)] += 1
    stats.barcode[getBarcodeStatusFilter(item)] += 1
    stats.action[getActionTypeFilter(item, rowActions)] += 1
  })

  return stats
}

export function toggleStatusFilter<T extends string | number>(
  currentFilter: StatusFilterValue<T>,
  nextFilter: T,
): StatusFilterValue<T> {
  return currentFilter === nextFilter ? 'all' : nextFilter
}

export function filterInvoiceDetails(
  details: LocalSupplierInvoiceItemDto[],
  filters: InvoiceDetailFilters,
): LocalSupplierInvoiceItemDto[] {
  let result = details

  const keyword = filters.searchText.trim().toLowerCase()
  if (keyword) {
    result = result.filter(
      (item) =>
        item.productCode?.toLowerCase().includes(keyword) ||
        item.itemNumber?.toLowerCase().includes(keyword) ||
        item.barcode?.toLowerCase().includes(keyword) ||
        item.productName?.toLowerCase().includes(keyword) ||
        item.storeProductCode?.toLowerCase().includes(keyword),
    )
  }

  if (filters.priceFilter === 'up') {
    result = result.filter((item) => hasPurchasePriceChanged(item, 'up'))
  } else if (filters.priceFilter === 'down') {
    result = result.filter((item) => hasPurchasePriceChanged(item, 'down'))
  }

  // 状态过滤与搜索、涨跌筛选按 AND 叠加。
  if (filters.productStatusFilter !== 'all') {
    result = result.filter((item) => getProductStatusFilter(item) === filters.productStatusFilter)
  }

  if (filters.barcodeStatusFilter === 'abnormal') {
    result = result.filter((item) => isBarcodeStatusAbnormal(item))
  } else if (filters.barcodeStatusFilter !== 'all') {
    result = result.filter((item) => getBarcodeStatusFilter(item) === filters.barcodeStatusFilter)
  }

  if (filters.actionTypeFilter !== undefined && filters.actionTypeFilter !== 'all') {
    result = result.filter((item) => getActionTypeFilter(item, filters.rowActions) === filters.actionTypeFilter)
  }

  return result
}

function isActionTypeFilter(value: number): value is ActionTypeFilter {
  return (actionTypeFilters as readonly number[]).includes(value)
}

function hasPurchasePriceChanged(item: LocalSupplierInvoiceItemDto, direction: 'up' | 'down') {
  if (
    item.lastPurchasePrice === undefined ||
    item.lastPurchasePrice === null ||
    item.lastPurchasePrice <= 0 ||
    item.purchasePrice === undefined ||
    item.purchasePrice === null
  ) {
    return false
  }

  return direction === 'up'
    ? item.purchasePrice > item.lastPurchasePrice
    : item.purchasePrice < item.lastPurchasePrice
}
