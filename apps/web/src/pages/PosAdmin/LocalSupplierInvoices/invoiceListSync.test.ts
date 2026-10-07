import { readFileSync } from 'node:fs'
import { resolve } from 'node:path'

import {
  INVOICE_LIST_REFRESH_DEBOUNCE_MS,
  invoiceChangeAffectsListCounts,
  publishLocalSupplierInvoiceChanged,
  shouldRefreshInvoiceList,
  subscribeLocalSupplierInvoiceChanged,
  type InvoiceListCountKeyRow,
} from './invoiceListSync'

function assert(condition: unknown, message: string): asserts condition {
  if (!condition) throw new Error(message)
}

function assertEqual<T>(actual: T, expected: T, message: string) {
  if (JSON.stringify(actual) !== JSON.stringify(expected)) {
    throw new Error(`${message}。Expected: ${JSON.stringify(expected)}, received: ${JSON.stringify(actual)}`)
  }
}

// 1) 发布订阅：多个订阅者都收到，取消订阅后不再收到，空单号不发布。
const received: string[] = []
const unsubscribeA = subscribeLocalSupplierInvoiceChanged((guid) => received.push(`a:${guid}`))
const unsubscribeB = subscribeLocalSupplierInvoiceChanged((guid) => received.push(`b:${guid}`))
publishLocalSupplierInvoiceChanged('inv-1')
unsubscribeA()
publishLocalSupplierInvoiceChanged('inv-2')
publishLocalSupplierInvoiceChanged(undefined)
publishLocalSupplierInvoiceChanged('')
unsubscribeB()
publishLocalSupplierInvoiceChanged('inv-3')
assertEqual(received, ['a:inv-1', 'b:inv-1', 'b:inv-2'], '订阅、取消订阅与空单号的处理')

// 2) 监听器在回调里取消订阅，不影响同一轮里其它监听器收到通知。
const order: string[] = []
const unsubscribeSelf = subscribeLocalSupplierInvoiceChanged(() => {
  order.push('self')
  unsubscribeSelf()
})
const unsubscribeOther = subscribeLocalSupplierInvoiceChanged(() => order.push('other'))
publishLocalSupplierInvoiceChanged('inv-4')
publishLocalSupplierInvoiceChanged('inv-5')
unsubscribeOther()
assertEqual(order, ['self', 'other', 'other'], '回调内取消订阅不打断本轮遍历')

// 3) 收到过变化且列表可见时跳过防抖立即刷新；隐藏时交给防抖定时器在后台刷新；没变化不刷新。
assert(shouldRefreshInvoiceList(true, true), '过期且可见时立即刷新')
assert(!shouldRefreshInvoiceList(true, false), '过期但隐藏时由防抖定时器后台刷新')
assert(!shouldRefreshInvoiceList(false, true), '未过期不刷新')
assert(INVOICE_LIST_REFRESH_DEBOUNCE_MS > 0 && INVOICE_LIST_REFRESH_DEBOUNCE_MS <= 3000, '防抖要短到返回前就能刷完')

// 3.1) 分段/分店计数是否受影响：筛选相关字段没变就跳过重算，成员关系可能变化时保守重算。
const row = (overrides: Partial<InvoiceListCountKeyRow> = {}): InvoiceListCountKeyRow => ({
  invoiceGUID: 'inv-1',
  storeCode: '1002',
  supplierCode: 'S1',
  invoiceNo: 'A-100',
  orderDate: '2026-10-07',
  createdAt: '2026-10-06T01:00:00Z',
  isProductChecked: true,
  ...overrides,
})
assert(
  !invoiceChangeAffectsListCounts(['inv-1'], [row()], [row()]),
  '金额/明细数/备注等不在比对字段里的变化不重算计数',
)
assert(
  !invoiceChangeAffectsListCounts(['inv-1'], [row({ isProductChecked: undefined })], [row({ isProductChecked: false })]),
  '是否检测的空值与 false 等价，不算变化',
)
assert(
  invoiceChangeAffectsListCounts(['inv-1'], [row({ isProductChecked: false })], [row({ isProductChecked: true })]),
  '检测完成（待检测 → 已检测）影响待检测计数与分店计数',
)
assert(
  invoiceChangeAffectsListCounts(['inv-1'], [row({ isProductChecked: true })], [row({ isProductChecked: false })]),
  '粘贴新明细（已检测 → 待检测）影响待检测计数',
)
assert(invoiceChangeAffectsListCounts(['inv-1'], [row()], [row({ storeCode: '1005' })]), '换分店要重算分店单数')
assert(invoiceChangeAffectsListCounts(['inv-1'], [row()], [row({ orderDate: '2026-01-01' })]), '改订单日期可能移出日期区间')
assert(invoiceChangeAffectsListCounts(['inv-1'], [row()], [row({ supplierCode: 'S2' })]), '换供应商可能移出供应商筛选')
assert(invoiceChangeAffectsListCounts(['inv-1'], [row()], [row({ invoiceNo: 'B-1' })]), '改单号可能移出单号筛选')
assert(invoiceChangeAffectsListCounts(['inv-1'], [row()], []), '刷新后不在当前页（被筛选排除或排序挪页）要重算')
assert(invoiceChangeAffectsListCounts(['inv-1'], [], [row()]), '刷新前不在当前页无法比较，保守重算')
assert(
  invoiceChangeAffectsListCounts(['inv-1', 'inv-2'], [row(), row({ invoiceGUID: 'inv-2' })], [row(), row({ invoiceGUID: 'inv-2', storeCode: '9' })]),
  '多张变化的单只要有一张影响计数就重算',
)
assert(!invoiceChangeAffectsListCounts([], [row()], [row({ storeCode: '9' })]), '没有变化的单号不触发重算')

// 4) 源码约束：明细页商品检测成功后要发布变化（异步任务与同步兜底两条路径）；
//    列表的变化刷新必须静默（不转圈）、只在计数受影响时才清空两组计数缓存并补算。
const editSource = readFileSync(resolve('src/pages/PosAdmin/LocalSupplierInvoices/InvoiceEdit/index.tsx'), 'utf8')
const checkStart = editSource.indexOf('const handleCheckProducts')
const checkEnd = editSource.indexOf('// ---- 行操作类型变更 ----', checkStart)
assert(checkStart > 0 && checkEnd > checkStart, '找不到明细页商品检测处理函数')
const checkSection = editSource.slice(checkStart, checkEnd)
assertEqual(
  checkSection.split('publishLocalSupplierInvoiceChanged(submittedInvoiceGuid)').length - 1,
  2,
  '商品检测的异步任务与同步兜底都要在成功后通知列表',
)

const listSource = readFileSync(resolve('src/pages/PosAdmin/LocalSupplierInvoices/index.tsx'), 'utf8')
const subscribeIndex = listSource.indexOf('subscribeLocalSupplierInvoiceChanged(')
assert(subscribeIndex > 0, '列表页必须订阅进货单变化')
const flushStart = listSource.indexOf('const flushInvoiceListRefresh')
assert(flushStart > 0 && flushStart < subscribeIndex, '找不到列表的变化刷新函数')
const flushSection = listSource.slice(flushStart, subscribeIndex)
assert(flushSection.includes('silent: true'), '变化触发的刷新必须静默，不能转圈挡住列表')
assert(flushSection.includes('invoiceChangeAffectsListCounts('), '计数只在受影响时才重算')
assert(flushSection.includes('countsOnly: true'), '补算计数时不重复请求当前页')
assert(flushSection.includes('lastCountFilterKeyRef.current = null'), '补算前要清空分段计数缓存')
assert(flushSection.includes('lastStoreCountFilterKeyRef.current = null'), '补算前要清空分店计数缓存')
assert(
  flushSection.indexOf('invoiceChangeAffectsListCounts(') < flushSection.indexOf('lastCountFilterKeyRef.current = null'),
  '清空计数缓存必须在判定受影响之后，不能一刷新就强制重算',
)
assert(flushSection.includes('latestLoadDataRef.current('), '刷新要走统一的受保护加载入口')
const subscribeSection = listSource.slice(subscribeIndex, subscribeIndex + 600)
assert(subscribeSection.includes('INVOICE_LIST_REFRESH_DEBOUNCE_MS'), '后台刷新要防抖，连续操作合并成一次')

console.log('LocalSupplierInvoices invoiceListSync tests: ok')
