import { readFileSync } from 'node:fs'
import path from 'node:path'

import { resolveOwnRoute } from './stableRouteContext'

function assert(condition: unknown, message: string): asserts condition {
  if (!condition) throw new Error(message)
}

function assertSame<T>(actual: T, expected: T, message: string) {
  if (actual !== expected) {
    throw new Error(`${message}。Expected: ${JSON.stringify(expected)}, received: ${JSON.stringify(actual)}`)
  }
}

interface TestRoute {
  path: string
  params: Record<string, string>
}

const STORE_ORDER_DETAIL = '/warehouse/store-order/detail/:id'
const STORE_ORDER_INVOICE = '/warehouse/store-order/invoice/:id'
const CONTAINER_DETAIL = '/warehouse/container/detail/:containerGuid'
const CONTAINER_ALLOCATION_SALES = '/warehouse/container/allocation-sales/:containerGuid'
const LOCAL_SUPPLIER_INVOICE = '/pos-admin/local-supplier-invoices/:id'

const route = (routePath: string, params: Record<string, string> = {}): TestRoute => ({ path: routePath, params })

const orderA = route(STORE_ORDER_DETAIL, { id: 'order-a' })

// 订货单明细提交粘贴替换后切到列表：隐藏实例必须仍是 order-a，否则 [id] effect 会停掉后台任务轮询
assertSame(
  resolveOwnRoute(STORE_ORDER_DETAIL, orderA, route('/warehouse/store-orders')),
  orderA,
  '切到列表页时保留本页路由',
)
assertSame(resolveOwnRoute(STORE_ORDER_DETAIL, orderA, null), orderA, '地址解析不到路由时保留本页路由')

// 发票页隐藏时打开别的 :id 页面，不能拿别人的 id 去拉订货单（会弹「订单不存在」或把标签标题改成别的单号）
const invoiceA = route(STORE_ORDER_INVOICE, { id: 'order-a' })
assertSame(
  resolveOwnRoute(STORE_ORDER_INVOICE, invoiceA, route(STORE_ORDER_DETAIL, { id: 'order-b' })),
  invoiceA,
  '别的订货单明细页的 id 不影响发票页',
)
assertSame(
  resolveOwnRoute(STORE_ORDER_INVOICE, invoiceA, route(LOCAL_SUPPLIER_INVOICE, { id: 'supplier-invoice-1' })),
  invoiceA,
  '同名参数 :id 的进货单页不影响发票页',
)

// 货柜明细隐藏时打开另一个货柜：不能把草稿命名空间切到别的货柜
const containerA = route(CONTAINER_DETAIL, { containerGuid: 'container-a' })
assertSame(
  resolveOwnRoute(CONTAINER_DETAIL, containerA, route(CONTAINER_ALLOCATION_SALES, { containerGuid: 'container-b' })),
  containerA,
  '分配销售页的 containerGuid 不影响货柜明细',
)

// 本页路由的地址变化照常跟随
const orderAAgain = route(STORE_ORDER_DETAIL, { id: 'order-a' })
assertSame(resolveOwnRoute(STORE_ORDER_DETAIL, orderA, orderAAgain), orderAAgain, '切回本页时采用新解析的路由')
const orderB = route(STORE_ORDER_DETAIL, { id: 'order-b' })
assertSame(resolveOwnRoute(STORE_ORDER_DETAIL, orderA, orderB), orderB, '同一路由模式下参数变化照常更新')

// 挂载时解析不到路由则无法判定身份，保持原有跟随行为
const listRoute = route('/warehouse/store-orders')
assertSame(resolveOwnRoute(undefined, null, listRoute), listRoute, '无本页身份时跟随当前地址')
assertSame(resolveOwnRoute(undefined, orderA, null), null, '无本页身份时地址解析不到即为空')

// 测试里用到的路由模式必须与路由表一致，否则上面的场景失去意义
const routesSource = readFileSync(path.resolve(process.cwd(), 'src/router/routes.tsx'), 'utf8')
for (const routePath of [STORE_ORDER_DETAIL, STORE_ORDER_INVOICE, CONTAINER_DETAIL, CONTAINER_ALLOCATION_SALES, LOCAL_SUPPLIER_INVOICE]) {
  assert(routesSource.includes(`path: '${routePath}'`), `路由表中应存在 ${routePath}`)
}
// 保活缓存键按 pathname 区分实例，是「实例参数不会合法变化」的前提
assert(routesSource.includes('key: route.meta.affix ? route.path : pathname'), '非固定标签页的缓存键应为 pathname')

const hookSource = readFileSync(path.resolve(process.cwd(), 'src/hooks/useStableRouteContext.ts'), 'utf8')
assert(
  hookSource.includes('resolveOwnRoute(ownRoutePathRef.current, routeRef.current, resolveRoute(pathnameRef.current))'),
  'useStableRouteContext 应只接受本页路由模式的新路由',
)
assert(hookSource.includes('const ownRoutePathRef = useRef(routeRef.current?.path)'), '本页路由模式应在挂载时确定')

console.log('stableRouteContext tests passed')
