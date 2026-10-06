import dayjs from 'dayjs'

import {
  DEFAULT_INVOICE_LIST_DATE_FIELD,
  buildInvoiceListDateFilter,
  getDefaultInvoiceListDateRange,
  isDefaultInvoiceListDateFilter,
} from './listDateFilter'

function assert(condition: unknown, message: string): asserts condition {
  if (!condition) throw new Error(message)
}

function assertEqual<T>(actual: T, expected: T, message: string) {
  if (actual !== expected) {
    throw new Error(`${message}。Expected: ${String(expected)}, received: ${String(actual)}`)
  }
}

const today = dayjs('2026-10-06T15:30:00')
const [defaultStart, defaultEnd] = getDefaultInvoiceListDateRange(today)
assertEqual(defaultStart.format('YYYY-MM-DD'), '2026-07-09', '默认区间从今天往前数 90 个自然日（含今天）')
assertEqual(defaultEnd.format('YYYY-MM-DD'), '2026-10-06', '默认区间结束于今天')
assertEqual(DEFAULT_INVOICE_LIST_DATE_FIELD, 'createdAt', '默认按创建日期筛选')

assert(isDefaultInvoiceListDateFilter('createdAt', [defaultStart, defaultEnd], today), '默认日期条件不算额外筛选')
assert(!isDefaultInvoiceListDateFilter('orderDate', [defaultStart, defaultEnd], today), '切到订单日期算改过筛选')
assert(!isDefaultInvoiceListDateFilter('createdAt', null, today), '清空日期（全部时间）算改过筛选')
assert(
  !isDefaultInvoiceListDateFilter('createdAt', [defaultStart.add(1, 'day'), defaultEnd], today),
  '改过起始日算改过筛选',
)

assertEqual(buildInvoiceListDateFilter('createdAt', null), null, '未选日期不加日期条件')

const orderFilter = buildInvoiceListDateFilter('orderDate', [dayjs('2026-09-01'), dayjs('2026-09-30')])
assertEqual(orderFilter?.key, 'orderDate', '订单日期条件的列名')
assertEqual(orderFilter?.value.type, 'inRange', '订单日期按区间筛选')
assertEqual(orderFilter?.value.filter, '2026-09-01', '订单日期按天传起始日')
assertEqual(orderFilter?.value.filterTo, '2026-09-30', '订单日期按天传结束日，由后端包含整天')

// 创建时间存 UTC：传本地日期起点与「结束日次日零点」的时刻，区间 [起, 止)。
const createdStart = dayjs('2026-09-01')
const createdEnd = dayjs('2026-09-30')
const createdFilter = buildInvoiceListDateFilter('createdAt', [createdStart, createdEnd])
assertEqual(createdFilter?.key, 'createdAt', '创建日期条件的列名')
assertEqual(createdFilter?.value.filter, createdStart.startOf('day').toISOString(), '起点是本地起始日零点的时刻')
assertEqual(
  createdFilter?.value.filterTo,
  createdEnd.add(1, 'day').startOf('day').toISOString(),
  '终点是本地结束日次日零点的时刻（开区间，结束日整天都包含）',
)
assert(createdFilter?.value.filter.endsWith('Z'), '时刻按 UTC 序列化，后端据此换算')

console.log('LocalSupplierInvoices listDateFilter tests: ok')
