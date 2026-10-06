import { readFileSync } from 'node:fs'
import { resolve } from 'node:path'
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

const guard = createLatestRequestGuard()
const state = { data: '', page: 0, loading: false }

function createHandlers(page: number) {
  return {
    onStart: () => { state.loading = true },
    onSuccess: (value: string) => {
      state.data = value
      state.page = page
    },
    onSettled: () => { state.loading = false },
  }
}

const first = createDeferred<string>()
const firstRun = runLatestGuardedRequest(guard, () => first.promise, createHandlers(1))
const second = createDeferred<string>()
const secondRun = runLatestGuardedRequest(guard, () => second.promise, createHandlers(2))

first.resolve('stale page 1')
await firstRun
assertEqual(state.data, '', '旧请求不能覆盖列表')
assertEqual(state.page, 0, '旧请求不能覆盖页码')
assertEqual(state.loading, true, '旧 finally 不能关闭最新请求 loading')

second.resolve('latest page 2')
await secondRun
assertEqual(state.data, 'latest page 2', '最新请求应更新列表')
assertEqual(state.page, 2, '最新请求应更新页码')
assertEqual(state.loading, false, '最新请求应关闭 loading')

const mutationGate = createDeferred<void>()
let mutationMounted = true
const mutationGuard = createLatestRequestGuard()
const mutationRequests: Array<ReturnType<typeof createDeferred<string>>> = []
let desiredQuery = { page: 1, sortField: 'createdate', sortDirection: 'desc' }
let mutationLoading = false
let mutationVisibleQuery = ''
const refreshedQueries: string[] = []

function runList(query: typeof desiredQuery) {
  if (!mutationMounted) return Promise.resolve()
  desiredQuery = query
  const label = `${query.page}:${query.sortField}:${query.sortDirection}`
  refreshedQueries.push(label)
  const request = mutationRequests.shift()
  if (!request) throw new Error(`缺少模拟请求: ${label}`)
  return runLatestGuardedRequest(mutationGuard, () => request.promise, {
    onStart: () => { mutationLoading = true },
    onSuccess: () => { mutationVisibleQuery = label },
    onSettled: () => { mutationLoading = false },
  })
}

function refreshDesiredQuery() {
  return runList({ ...desiredQuery })
}

// A mutation 挂起时，B 已发布 page3/sort 查询但仍在途；A 完成触发的 C 必须沿用 B 的 desired query。
const mutationRun = (async () => {
  await mutationGate.promise
  await refreshDesiredQuery()
})()
const latestLabel = '3:suppliername:asc'
const bRequest = createDeferred<string>()
const cRequest = createDeferred<string>()
mutationRequests.push(bRequest, cRequest)
const bRun = runList({ page: 3, sortField: 'suppliername', sortDirection: 'asc' })
mutationGate.resolve()
await Promise.resolve()
assertEqual(refreshedQueries.join(','), `${latestLabel},${latestLabel}`, 'mutation 刷新必须保留已开始 B 的页码和排序')
bRequest.resolve('B')
await bRun
assertEqual(mutationVisibleQuery, '', 'B 已被 C 淘汰时不得写入列表')
assertEqual(mutationLoading, true, 'B 的旧 finally 不得关闭 C 的 loading')
cRequest.resolve('C')
await mutationRun
assertEqual(mutationVisibleQuery, latestLabel, 'B/C 乱序时只有沿用 desired query 的 C 可以生效')
assertEqual(mutationLoading, false, '最新 C 完成后应关闭 loading')

const unmountGate = createDeferred<void>()
let beginsAfterUnmount = 0
const unmountMutationRun = (async () => {
  await unmountGate.promise
  if (!mutationMounted) {
    return
  }
  beginsAfterUnmount += 1
  await refreshDesiredQuery()
})()
mutationMounted = false
mutationGuard.invalidate()
unmountGate.resolve()
await unmountMutationRun
assertEqual(beginsAfterUnmount, 0, 'layout cleanup 后 mutation 不得重新 begin 列表请求')

// 删除最后一页唯一一条后页面会在 onSuccess 里再发起「回退到最后一页」的查询：
// 首个请求的 finally 不得关闭这个新请求的 loading，且只有新请求可以写入列表。
const overflowGuard = createLatestRequestGuard()
const overflowState = { loading: false, visible: '' }
const overflowFirst = createDeferred<string>()
const overflowSecond = createDeferred<string>()
let overflowSecondRun: Promise<void> | undefined
const overflowFirstRun = runLatestGuardedRequest(overflowGuard, () => overflowFirst.promise, {
  onStart: () => { overflowState.loading = true },
  onSuccess: () => {
    overflowSecondRun = runLatestGuardedRequest(overflowGuard, () => overflowSecond.promise, {
      onStart: () => { overflowState.loading = true },
      onSuccess: (value) => { overflowState.visible = value },
      onSettled: () => { overflowState.loading = false },
    })
  },
  onSettled: () => { overflowState.loading = false },
})
overflowFirst.resolve('empty page 4')
await overflowFirstRun
assertEqual(overflowState.loading, true, '越界回退请求在途时，首个请求的 finally 不得关闭 loading')
assertEqual(overflowState.visible, '', '越界的空页不得写入列表')
overflowSecond.resolve('page 3')
await overflowSecondRun
assertEqual(overflowState.visible, 'page 3', '回退到最后一页的请求应写入列表')
assertEqual(overflowState.loading, false, '回退请求完成后应关闭 loading')

const source = readFileSync(resolve('src/pages/DomesticPurchase/ChinaSuppliers/index.tsx'), 'utf8')
const domesticSource = readFileSync(resolve('src/pages/DomesticPurchase/DomesticProducts/index.tsx'), 'utf8')
const tableStart = source.indexOf(
  '<MeasuredTable metricId="domestic-purchase.china-suppliers.table-1"\n          rowKey="guid"',
)
const tableEnd = source.indexOf('\n        />', tableStart)
const tableSource = source.slice(tableStart, tableEnd)
const paginationStart = tableSource.indexOf('pagination={{')
const paginationEnd = tableSource.indexOf('\n          }}', paginationStart)
const paginationSource = tableSource.slice(paginationStart, paginationEnd)

assertIncludes(source, 'createLatestRequestGuard()', '国内供应商主列表应创建最新请求守卫')
assertIncludes(source, 'runLatestGuardedRequest(mainListRequestGuardRef.current', '国内供应商主列表应统一执行受保护请求')
assertIncludes(source, 'mainListRequestGuardRef.current.invalidate()', '国内供应商页面卸载时应使请求失效')
assertIncludes(source, 'const mountedRef = useRef(false)', '国内供应商页面应记录 mounted 状态')
assertIncludes(source, 'const latestLoadDataRef = useRef(loadData)', '国内供应商页面应保存 commit 后最新 loader')
assertIncludes(source, 'latestLoadDataRef.current = loadData', '国内供应商页面应在 layout effect 发布最新 loader')
assertIncludes(source, 'if (!mountedRef.current) {', '国内供应商 loader 应在 begin 前拦截已卸载页面')
assertIncludes(source, 'const desiredListQueryRef = useRef<DesiredChinaSupplierQuery>({', '国内供应商页面应保存已开始请求的 desired query')
assertIncludes(source, 'desiredListQueryRef.current = query', '国内供应商 loader 应在请求开始时发布 desired query')
assertIncludes(source, 'latestLoadDataRef.current({ ...desiredListQueryRef.current, ...overrides })', '国内供应商 mutation 刷新应读取 desired query')
assertIncludes(source, 'const overviewRequestGuardRef = useRef(createLatestRequestGuard())', '页头概览（共 N 家 · 启用 M）应有独立的最新请求守卫')
assertIncludes(source, 'overviewRequestGuardRef.current.invalidate()', '国内供应商页面卸载时应使概览请求失效')
assertIncludes(
  source,
  'const query: DesiredChinaSupplierQuery = {\n      ...desiredListQueryRef.current,\n      ...overrides,\n    }',
  '国内供应商 loader 应以已开始请求的 desired query 为底做对象合并，而不是读渲染闭包里的旧 state 或用默认参数',
)
assertIncludes(source, 'void refreshDesiredList()', '国内供应商更新后应刷新已开始的最新查询')
assertIncludes(source, 'void refreshDesiredList({ page: 1 })', '国内供应商创建后应保持回第一页语义')
assertIncludes(source, "extra.action === 'paginate' ? pagination.current ?? 1 : 1", '只有 paginate 应保留目标页')
assertIncludes(tableSource, 'onChange={handleTableChange}', '主表应只通过 Table.onChange 处理分页和排序')
assertEqual(paginationSource.includes('onChange:'), false, '主表 pagination 不得保留重复请求入口')

assertIncludes(domesticSource, 'const mountedRef = useRef(false)', '国内商品页面应记录 mounted 状态')
assertIncludes(domesticSource, 'const latestLoadDataRef = useRef(loadData)', '国内商品页面应保存 commit 后最新 loader')
assertIncludes(domesticSource, 'latestLoadDataRef.current = loadData', '国内商品页面应在 layout effect 发布最新 loader')
assertIncludes(domesticSource, 'if (!mountedRef.current) {', '国内商品 loader 应在 begin 前拦截已卸载页面')
assertIncludes(domesticSource, 'const desiredGridQueryRef = useRef<DomesticProductGridQuery>({', '国内商品页面应保存完整 desired query')
assertIncludes(domesticSource, 'desiredGridQueryRef.current = query', '国内商品 loader 应在请求开始时发布 desired query')
assertIncludes(domesticSource, 'latestLoadDataRef.current({ ...desiredGridQueryRef.current, ...overrides })', '国内商品 mutation 刷新应读取完整 desired query')
assertIncludes(domesticSource, 'void refreshDesiredGrid()', '国内商品编辑与套装保存后应刷新 desired query')
assertIncludes(domesticSource, 'void refreshDesiredGrid({ page: 1 })', '国内商品创建和删除后应保持回第一页语义')

console.log('ChinaSuppliers paginationRace.test.ts: ok')
