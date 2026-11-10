import assert from 'node:assert/strict'
import { existsSync, readFileSync } from 'node:fs'
import { resolve } from 'node:path'

// routes 初始化 i18n 时会读取浏览器语言；测试只提供最小只读存根，不写入持久化存储。
Object.defineProperty(globalThis, 'localStorage', {
  configurable: true,
  value: { getItem: () => null },
})

// 必须晚于上面的存根加载（静态 import 会被提升到存根之前）
const { resolveRoute } = await import('./routes')

// 回归：「供应商节日预订单」订单表详情页点开后整页空白。
// 根因：后台页由 AdminLayout（挂在通配 splat 路由上）手工渲染，不在带 :formGuid 的 <Route> 里，
// 页面里的 useParams() 只能拿到 { '*': 'supplier-preorders/forms/<guid>' }，formGuid 恒为 ''，详情页因此不发请求、也不报错。
// 后台带动态参数的页面必须通过 useStableRouteContext()（底层是 resolveRoute 的 matchPath）取参数。
// 需在 apps/web 目录下运行（与其他 web 单测一致）。

function stripComments(source: string) {
  return source.replace(/\/\*[\s\S]*?\*\//g, '').replace(/(^|[^:'"`])\/\/[^\n]*/g, '$1')
}

// ---------- 1. 路由层：两个供应商节日预订单页能从地址解析出 formGuid ----------
const GUID = '3f2a9c1e-7b64-4d0a-9e51-8c2d4a6b1f70'

const detailRoute = resolveRoute(`/supplier-preorders/forms/${GUID}`)
assert.ok(detailRoute, '订单表详情地址必须能解析到路由')
assert.equal(detailRoute.path, '/supplier-preorders/forms/:formGuid')
assert.equal(detailRoute.params.formGuid, GUID, '订单表详情必须解析出 formGuid')

const orderingRoute = resolveRoute(`/supplier-preorder-ordering/${GUID}`)
assert.ok(orderingRoute, '下单页地址必须能解析到路由')
assert.equal(orderingRoute.path, '/supplier-preorder-ordering/:formGuid')
assert.equal(orderingRoute.params.formGuid, GUID, '下单页必须解析出 formGuid')

// 导入页是 /supplier-preorders/import，不能被当成 forms/:formGuid；列表页没有参数
assert.equal(resolveRoute('/supplier-preorders/import')?.path, '/supplier-preorders/import')
assert.deepEqual(resolveRoute('/supplier-preorders')?.params, {})

// ---------- 2. 守卫：后台所有带动态参数的路由页都不得使用 useParams ----------
const routeSource = readFileSync(resolve('src/router/routes.tsx'), 'utf8')
const lazyImports = new Map(
  [...routeSource.matchAll(/const (\w+) = lazy\(\(\) => import\('([^']+)'\)\)/g)].map((match) => [match[1], match[2]] as const),
)
// path 里带 :param 的叶子路由，取其后（下一个 path: 之前）的 element: <Component />
const dynamicRoutes = [...routeSource.matchAll(/path: '([^']*:[^']*)'((?:(?!path: )[\s\S])*?)element: <(\w+)[ />]/g)].map((match) => ({
  path: match[1],
  component: match[3],
}))
assert.ok(dynamicRoutes.length >= 10, `只扫描到 ${dynamicRoutes.length} 个带动态参数的后台路由，扫描规则可能已失效`)
assert.ok(
  dynamicRoutes.some((item) => item.path === '/supplier-preorders/forms/:formGuid'),
  '扫描结果必须包含订单表详情路由',
)

const offenders: string[] = []
let scannedPages = 0
for (const { path, component } of dynamicRoutes) {
  const importPath = lazyImports.get(component)
  if (!importPath) continue // 例如只做跳转的 LegacyInvoiceDetailRedirect（直接 matchPath，不是 lazy 页面）
  const base = resolve('src/router', importPath)
  const file = [`${base}.tsx`, `${base}/index.tsx`].find((candidate) => existsSync(candidate))
  assert.ok(file, `找不到 ${path} 对应的页面源码：${importPath}`)
  scannedPages += 1
  if (/\buseParams\s*[<(]/.test(stripComments(readFileSync(file, 'utf8')))) offenders.push(`${path} → ${importPath}`)
}
assert.ok(scannedPages >= 10, `只核对了 ${scannedPages} 个页面源码，扫描规则可能已失效`)
assert.deepEqual(offenders, [], '这些后台页由 AdminLayout 手工渲染，useParams 拿不到动态参数，必须改用 useStableRouteContext')

// ---------- 3. 两个页面确实改用稳定路由上下文取 formGuid ----------
for (const file of ['src/pages/SupplierPreorders/Admin/FormDetail.tsx', 'src/pages/SupplierPreorders/Ordering/OrderForm.tsx']) {
  const source = stripComments(readFileSync(resolve(file), 'utf8'))
  assert.ok(source.includes("useStableRouteContext()?.params.formGuid ?? ''"), `${file} 必须从 useStableRouteContext 取 formGuid`)
}

// ---------- 4. 详情页没有编号时给出明确提示，不能静默空白 ----------
const detailSource = readFileSync(resolve('src/pages/SupplierPreorders/Admin/FormDetail.tsx'), 'utf8')
assert.ok(
  detailSource.includes("t('spAdmin.detail.invalidAddress')"),
  '详情页在地址缺少订单表编号时必须显示 invalidAddress 提示',
)
assert.ok(!detailSource.includes('<Spin spinning={loading} />'), '没有详情时不得渲染 spinning=false 的空 Spin（会呈现为空白）')

console.log('adminDynamicRouteParams.test.ts passed')
