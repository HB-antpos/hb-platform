import { readFileSync } from 'node:fs'
import { resolve } from 'node:path'
import type { LatestRequestGuard } from '../../../utils/latestRequestGuard'
import {
  createLatestRequestGuard,
  runLatestGuardedRequest,
} from '../../../utils/latestRequestGuard'

function assertEqual(actual: unknown, expected: unknown, label: string) {
  if (actual !== expected) {
    throw new Error(`${label}: expected ${String(expected)}, got ${String(actual)}`)
  }
}

function assertIncludes(source: string, expected: string, label: string) {
  if (!source.includes(expected)) {
    throw new Error(`${label}. Missing: ${expected}`)
  }
}

function createDeferred<T>() {
  let resolvePromise!: (value: T) => void
  const promise = new Promise<T>((resolvePromiseValue) => {
    resolvePromise = resolvePromiseValue
  })
  return { promise, resolve: resolvePromise }
}

// 每个前缀一个独立守卫：对应生产代码里「每个展开行是一个 PrefixProductsPanel 实例，各自持有守卫」。
const guards = new Map<string, LatestRequestGuard>()
const state: Record<string, { data: string; page: number; loading: boolean; expanded: boolean }> = {}

function load(prefixCode: string, page: number, request: Promise<string>) {
  let guard = guards.get(prefixCode)
  if (!guard) {
    guard = createLatestRequestGuard()
    guards.set(prefixCode, guard)
  }

  state[prefixCode] ??= { data: '', page: 0, loading: false, expanded: true }
  return runLatestGuardedRequest(guard, () => request, {
    onStart: () => { state[prefixCode].loading = true },
    onSuccess: (data) => {
      state[prefixCode].data = data
      state[prefixCode].page = page
    },
    onSettled: () => { state[prefixCode].loading = false },
  })
}

const aPage1 = createDeferred<string>()
const aPage1Run = load('A', 1, aPage1.promise)
const bPage1 = createDeferred<string>()
const bPage1Run = load('B', 1, bPage1.promise)
const aPage2 = createDeferred<string>()
const aPage2Run = load('A', 2, aPage2.promise)

aPage1.resolve('stale A page 1')
await aPage1Run
assertEqual(state.A.data, '', '同一前缀旧页不能覆盖新页')
assertEqual(state.A.loading, true, '同一前缀旧 finally 不能关闭新页 loading')

bPage1.resolve('B page 1')
await bPage1Run
assertEqual(state.B.data, 'B page 1', '不同前缀请求应独立完成')
assertEqual(state.B.page, 1, '不同前缀页码应独立更新')

aPage2.resolve('A page 2')
await aPage2Run
assertEqual(state.A.data, 'A page 2', '同一前缀最新页应胜出')
assertEqual(state.A.page, 2, '同一前缀最新页码应胜出')

const collapsed = createDeferred<string>()
const collapsedRun = load('A', 3, collapsed.promise)
guards.get('A')?.invalidate()
state.A.expanded = false
state.A.loading = false
collapsed.resolve('ignored after collapse')
await collapsedRun
assertEqual(state.A.data, 'A page 2', '收起后的旧响应不能覆盖商品')
assertEqual(state.A.expanded, false, '收起后的旧响应不能重新展开行')
assertEqual(state.A.loading, false, '收起后应立即结束 loading')

const mutationGate = createDeferred<void>()
let mutationMounted = true
const mutationListGuard = createLatestRequestGuard()
const mutationRequests: Array<ReturnType<typeof createDeferred<string>>> = []
let desiredListQuery = { page: 1, supplierCode: 'OLD', sortField: 'prefixCode', sortDirection: 'desc' }
let listLoading = false
let visibleListQuery = ''
const listRefreshes: string[] = []

function runPrefixList(query: typeof desiredListQuery) {
  if (!mutationMounted) return Promise.resolve()
  desiredListQuery = query
  const label = `${query.page}:${query.supplierCode}:${query.sortField}:${query.sortDirection}`
  listRefreshes.push(label)
  const request = mutationRequests.shift()
  if (!request) throw new Error(`缺少模拟请求: ${label}`)
  return runLatestGuardedRequest(mutationListGuard, () => request.promise, {
    onStart: () => { listLoading = true },
    onSuccess: () => { visibleListQuery = label },
    onSettled: () => { listLoading = false },
  })
}

function refreshDesiredPrefixList() {
  return runPrefixList({ ...desiredListQuery })
}

// B 已发布 page3/supplier/sort 但未完成时，A mutation 完成触发的 C 仍必须使用 B 的 desired query。
const mutationRun = (async () => {
  await mutationGate.promise
  await refreshDesiredPrefixList()
})()
const latestLabel = '3:NEW:createdAt:asc'
const bRequest = createDeferred<string>()
const cRequest = createDeferred<string>()
mutationRequests.push(bRequest, cRequest)
const bRun = runPrefixList({ page: 3, supplierCode: 'NEW', sortField: 'createdAt', sortDirection: 'asc' })
mutationGate.resolve()
await Promise.resolve()
assertEqual(listRefreshes.join(','), `${latestLabel},${latestLabel}`, '前缀 mutation 刷新必须保留 B 已开始的分页、筛选和排序')
bRequest.resolve('B')
await bRun
assertEqual(visibleListQuery, '', 'B 被 C 淘汰后不得更新前缀主列表')
assertEqual(listLoading, true, 'B 的旧 finally 不得关闭 C 的 loading')
cRequest.resolve('C')
await mutationRun
assertEqual(visibleListQuery, latestLabel, 'B/C 乱序时只有沿用 desired query 的 C 可以更新前缀列表')
assertEqual(listLoading, false, '最新 C 完成后应关闭 loading')

const unmountGate = createDeferred<void>()
let beginsAfterUnmount = 0
const unmountMutationRun = (async () => {
  await unmountGate.promise
  if (!mutationMounted) {
    return
  }
  beginsAfterUnmount += 1
  await refreshDesiredPrefixList()
})()
mutationMounted = false
mutationListGuard.invalidate()
unmountGate.resolve()
await unmountMutationRun
assertEqual(beginsAfterUnmount, 0, '前缀 layout cleanup 后 mutation 不得重新 begin 列表请求')

const source = readFileSync(resolve('src/pages/DomesticPurchase/ProductPrefixCodeManagement/index.tsx'), 'utf8')
// 展开商品面板已拆成独立组件（每个前缀一个实例，各自持有分页状态与最新请求守卫），其源码契约在 panelSource 上检查。
const panelSource = readFileSync(resolve('src/pages/DomesticPurchase/ProductPrefixCodeManagement/PrefixProductsPanel.tsx'), 'utf8')
const tableStart = source.lastIndexOf('metricId="domestic-purchase.product-prefix-code-management.table-2"')
const tableEnd = source.indexOf('\n          />', tableStart)
const tableSource = source.slice(tableStart, tableEnd)
const paginationStart = tableSource.indexOf('pagination={{')
const paginationEnd = tableSource.indexOf('\n            }}', paginationStart)
const paginationSource = tableSource.slice(paginationStart, paginationEnd)

assertEqual(tableStart > 0 && tableEnd > tableStart, true, '应能在源码里定位到前缀主表')
assertEqual(paginationStart > 0 && paginationEnd > paginationStart, true, '应能在主表里定位到 pagination 配置')

assertIncludes(source, 'createLatestRequestGuard()', '前缀主列表应创建最新请求守卫')
assertIncludes(source, 'runLatestGuardedRequest(mainListRequestGuardRef.current', '前缀主列表应统一执行受保护请求')
assertIncludes(source, 'const mountedRef = useRef(false)', '前缀页面应记录 mounted 状态')
assertIncludes(source, 'const latestLoadListRef = useRef(loadList)', '前缀页面应保存 commit 后最新 loader')
assertIncludes(source, 'latestLoadListRef.current = loadList', '前缀页面应在 layout effect 发布最新 loader')
assertIncludes(source, 'if (!mountedRef.current) {', '前缀主列表应在 begin 前拦截已卸载页面')
assertIncludes(source, 'const desiredListQueryRef = useRef<DesiredPrefixListQuery>({', '前缀页面应保存已开始请求的 desired query')
assertIncludes(source, 'desiredListQueryRef.current = query', '前缀 loader 应在请求开始时发布 desired query')
assertIncludes(source, 'latestLoadListRef.current({ ...desiredListQueryRef.current, ...overrides })', '前缀 mutation 刷新应读取 desired query')
assertIncludes(source, 'void refreshDesiredList()', '前缀编辑、删除、切换状态后应刷新 desired query')
assertIncludes(source, 'void refreshDesiredList({ page: 1 })', '前缀创建后应保持回第一页语义')
assertIncludes(source, "extra.action === 'paginate' ? pagination.current ?? 1 : 1", '只有 paginate 应保留目标页')
assertIncludes(tableSource, 'onChange={handleTableChange}', '主表应只通过 Table.onChange 处理分页和排序')
assertEqual(paginationSource.includes('onChange:'), false, '主表 pagination 不得保留重复请求入口')
assertEqual((source.match(/onChange: \(nextPage, nextPageSize\)/g) ?? []).length, 0, '主页面不应再持有展开子表的分页入口')

// 展开商品：每个前缀一个独立面板实例，守卫随实例隔离；收起 / 被换页卸载时作废在途请求。
assertIncludes(source, '<PrefixProductsPanel key={record.prefixCode}', '展开行应按前缀编码为 key 渲染独立面板，保证每个前缀独立分页与守卫')
assertEqual(source.includes('productRequestGuardsRef'), false, '主页面不应再用共享 Map 持有展开商品的守卫')
assertIncludes(panelSource, 'const requestGuardRef = useRef(createLatestRequestGuard())', '每个展开面板应持有自己的最新请求守卫')
assertIncludes(panelSource, 'runLatestGuardedRequest(requestGuardRef.current', '展开商品应通过自己的守卫执行受保护请求')
assertIncludes(panelSource, 'return () => requestGuardRef.current.invalidate()', '收起或卸载时应作废在途展开请求，晚到的响应不能写已卸载面板')
assertEqual((panelSource.match(/onChange: \(nextPage, nextPageSize\)/g) ?? []).length, 1, '只应保留展开子表的 pagination.onChange')

console.log('ProductPrefixCodeManagement paginationRace.test.ts: ok')
