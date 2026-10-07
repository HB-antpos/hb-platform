import { readFileSync } from 'node:fs'
import path from 'node:path'

import { INVOICE_EDIT_ROUTE_PATH, resolveInvoiceEditGuid } from './invoiceEditRoute'

function assert(condition: unknown, message: string): asserts condition {
  if (!condition) throw new Error(message)
}

function assertEqual<T>(actual: T, expected: T, message: string) {
  if (actual !== expected) {
    throw new Error(`${message}。Expected: ${String(expected)}, received: ${String(actual)}`)
  }
}

const edit = (id: string) => ({ path: INVOICE_EDIT_ROUTE_PATH, params: { id } })

assertEqual(resolveInvoiceEditGuid(undefined, edit('inv-1')), 'inv-1', '首次进入明细页取地址里的单号')
// 复现 10-07 线上问题：检测提交后切到列表页，隐藏的明细页不能把单号变成空值
assertEqual(resolveInvoiceEditGuid('inv-1', { path: '/pos-admin/local-supplier-invoices', params: {} }), 'inv-1', '切到列表页时保留原单号')
assertEqual(resolveInvoiceEditGuid('inv-1', null), 'inv-1', '路由解析不到时保留原单号')
assertEqual(
  resolveInvoiceEditGuid('inv-1', { path: '/pos-admin/local-supplier-invoices/:id/sales-analysis', params: { id: 'inv-2' } }),
  'inv-1',
  '别的带 :id 的页面（如销量分析）不改变明细页单号',
)
assertEqual(resolveInvoiceEditGuid('inv-1', edit('inv-1')), 'inv-1', '切回同一张单单号不变，不触发任务重置')
assertEqual(resolveInvoiceEditGuid('inv-1', edit('inv-3')), 'inv-3', '本页确实切换到另一张单时才更新')
assertEqual(resolveInvoiceEditGuid('inv-1', edit('  ')), 'inv-1', '空单号不覆盖')

// 路由常量必须与路由表一致，否则明细页永远拿不到单号
const routesSource = readFileSync(path.resolve(process.cwd(), 'src/router/routes.tsx'), 'utf8')
assert(routesSource.includes(`path: '${INVOICE_EDIT_ROUTE_PATH}'`), '路由表中应存在明细页路由')

const pageSource = readFileSync(path.resolve(process.cwd(), 'src/pages/PosAdmin/LocalSupplierInvoices/InvoiceEdit/index.tsx'), 'utf8')
assert(pageSource.includes('resolveInvoiceEditGuid(invoiceGuidRef.current, route)'), '明细页应通过 resolveInvoiceEditGuid 取单号')
assert(!pageSource.includes('const invoiceGuid = route?.params.id'), '明细页不应直接跟随全局路由取单号')

console.log('invoiceEditRoute tests passed')
