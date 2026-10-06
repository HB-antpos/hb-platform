import dayjs, { type Dayjs } from 'dayjs'

/** 列表日期筛选按哪个日期：创建日期（默认）或订单日期。 */
export type InvoiceListDateField = 'createdAt' | 'orderDate'
export type InvoiceListDateRange = [Dayjs, Dayjs]

export const DEFAULT_INVOICE_LIST_DATE_FIELD: InvoiceListDateField = 'createdAt'
export const DEFAULT_INVOICE_LIST_DATE_RANGE_DAYS = 90

/** 默认近 90 天：含今天在内往前数 90 个自然日。 */
export function getDefaultInvoiceListDateRange(today: Dayjs = dayjs()): InvoiceListDateRange {
  const end = today.startOf('day')
  return [end.subtract(DEFAULT_INVOICE_LIST_DATE_RANGE_DAYS - 1, 'day'), end]
}

export function isDefaultInvoiceListDateFilter(
  field: InvoiceListDateField,
  range: InvoiceListDateRange | null,
  today: Dayjs = dayjs(),
) {
  if (field !== DEFAULT_INVOICE_LIST_DATE_FIELD || !range) return false
  const [defaultStart, defaultEnd] = getDefaultInvoiceListDateRange(today)
  return range[0].isSame(defaultStart, 'day') && range[1].isSame(defaultEnd, 'day')
}

/**
 * 转成 grid 的 filterModel 条目；未选日期（全部时间）返回 null。
 * - 订单日期是业务日期，直接按天传，后端包含结束日整天。
 * - 创建日期在库里存 UTC，按本地日期的起止换算成带时区的时刻传给后端，结束取次日零点（开区间），
 *   这样悉尼 10-06 当天 00:00–24:00 创建的单都落在 10-06，不会因为时差错到前一天。
 */
export function buildInvoiceListDateFilter(
  field: InvoiceListDateField,
  range: InvoiceListDateRange | null,
): { key: InvoiceListDateField; value: Record<string, string> } | null {
  if (!range) return null
  const [start, end] = range
  if (field === 'orderDate') {
    return {
      key: 'orderDate',
      value: { filterType: 'date', type: 'inRange', filter: start.format('YYYY-MM-DD'), filterTo: end.format('YYYY-MM-DD') },
    }
  }
  return {
    key: 'createdAt',
    value: {
      filterType: 'date',
      type: 'inRange',
      filter: start.startOf('day').toISOString(),
      filterTo: end.add(1, 'day').startOf('day').toISOString(),
    },
  }
}
