import { readFileSync } from 'node:fs'
import { resolve } from 'node:path'

import {
  publishLocalSupplierInvoiceChanged,
  shouldRefreshInvoiceList,
  subscribeLocalSupplierInvoiceChanged,
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

// 3) 只有「收到过变化」且「列表可见」时才刷新；隐藏时等切回再刷新。
assert(shouldRefreshInvoiceList(true, true), '过期且可见时立即刷新')
assert(!shouldRefreshInvoiceList(true, false), '过期但隐藏时等切回')
assert(!shouldRefreshInvoiceList(false, true), '未过期不刷新')

// 4) 源码约束：明细页商品检测成功后要发布变化（异步任务与同步兜底两条路径），列表订阅后要清空两组计数缓存再刷新。
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
const refreshSection = listSource.slice(subscribeIndex, subscribeIndex + 800)
assert(refreshSection.includes('lastCountFilterKeyRef.current = null'), '刷新前要清空分段计数缓存')
assert(refreshSection.includes('lastStoreCountFilterKeyRef.current = null'), '刷新前要清空分店计数缓存')
assert(refreshSection.includes('latestLoadDataRef.current()'), '刷新要走统一的受保护加载入口')

console.log('LocalSupplierInvoices invoiceListSync tests: ok')
