import { readFileSync } from 'node:fs'
import { resolve } from 'node:path'
import {
  createLatestRequestGuard,
  runLatestGuardedRequest,
} from '../../../utils/latestRequestGuard'

function assert(condition: unknown, message: string): asserts condition {
  if (!condition) throw new Error(message)
}

function assertEqual(actual: unknown, expected: unknown, message: string) {
  if (actual !== expected) throw new Error(`${message}: expected ${String(expected)}, got ${String(actual)}`)
}

function extractBlock(source: string, startMarker: string, endMarker: string, label: string) {
  const start = source.indexOf(startMarker)
  assert(start >= 0, `${label}缺少开始标记`)
  const contentStart = start + startMarker.length
  const end = source.indexOf(endMarker, contentStart)
  assert(end > contentStart, `${label}缺少有效结束标记`)
  const block = source.slice(start, end)
  assert(block.trim().length > startMarker.length, `${label}不得为空`)
  return block
}

function count(source: string, value: string) {
  return source.split(value).length - 1
}

function deferred<T>() {
  let resolvePromise!: (value: T) => void
  let rejectPromise!: (error: unknown) => void
  const promise = new Promise<T>((resolve, reject) => {
    resolvePromise = resolve
    rejectPromise = reject
  })
  return { promise, resolve: resolvePromise, reject: rejectPromise }
}

async function assertLayoutCleanupBlocksReload() {
  const lifecycleGuard = createLatestRequestGuard()
  let mounted = false
  let apiCalls = 0
  let loading = false
  let currentQuery = 'page 1'

  const currentLoader = async () => {
    if (!mounted) return
    await runLatestGuardedRequest(lifecycleGuard, async () => {
      apiCalls += 1
      return currentQuery
    }, {
      onStart: () => { loading = true },
      onSuccess: () => {},
      onSettled: () => { loading = false },
    })
  }
  const commitLayout = () => { mounted = true }
  const cleanupLayout = () => {
    mounted = false
    lifecycleGuard.invalidate()
  }

  const mutation = deferred<void>()
  commitLayout()
  const mutationRun = (async () => {
    await mutation.promise
    await currentLoader()
  })()

  // 当前筛选已提交新 render 后，layout cleanup 必须阻止迟到 mutation 再次 begin。
  currentQuery = 'filtered page 2'
  cleanupLayout()
  mutation.resolve()
  await mutationRun

  assertEqual(apiCalls, 0, 'layout cleanup 后 mutation 不得重新 begin 列表请求')
  assertEqual(loading, false, 'layout cleanup 后不得留下列表 loading')
}

const guard = createLatestRequestGuard()
const state: { data: string; page: number; error: string; loading: boolean } = {
  data: '', page: 0, error: '', loading: false,
}
const run = (request: Promise<string>, page: number) => runLatestGuardedRequest(guard, () => request, {
  onStart: () => { state.loading = true },
  onSuccess: (data) => { state.data = data; state.page = page },
  onError: (error) => { state.error = String(error) },
  onSettled: () => { state.loading = false },
})

const firstEarly = deferred<string>()
const secondPending = deferred<string>()
const firstEarlyRun = run(firstEarly.promise, 1)
const secondPendingRun = run(secondPending.promise, 2)
firstEarly.resolve('stale early')
await firstEarlyRun
assertEqual(state.data, '', 'B 仍在请求时 A 旧成功不得写数据')
assertEqual(state.page, 0, 'B 仍在请求时 A 旧成功不得写页码')
assert(state.loading, 'A 旧 finally 不得关闭 B 的 loading')
secondPending.resolve('latest second')
await secondPendingRun

const firstLate = deferred<string>()
const secondFast = deferred<string>()
const firstLateRun = run(firstLate.promise, 3)
const secondFastRun = run(secondFast.promise, 4)
secondFast.resolve('latest fast')
await secondFastRun
const afterSecondFast = { ...state }
firstLate.resolve('stale late')
await firstLateRun
assertEqual(state.data, afterSecondFast.data, 'B 完成后 A 旧成功不得覆盖数据')
assertEqual(state.page, afterSecondFast.page, 'B 完成后 A 旧成功不得覆盖页码')
assertEqual(state.error, afterSecondFast.error, 'B 完成后 A 旧成功不得改变错误')
assertEqual(state.loading, afterSecondFast.loading, 'B 完成后 A 旧 finally 不得改变 loading')

const firstFailure = deferred<string>()
const secondBeforeFailure = deferred<string>()
const firstFailureRun = run(firstFailure.promise, 5)
const secondBeforeFailureRun = run(secondBeforeFailure.promise, 6)
secondBeforeFailure.resolve('latest before failure')
await secondBeforeFailureRun
const afterSecondBeforeFailure = { ...state }
firstFailure.reject(new Error('stale failure'))
await firstFailureRun
assertEqual(state.data, afterSecondBeforeFailure.data, '旧失败不得覆盖数据')
assertEqual(state.page, afterSecondBeforeFailure.page, '旧失败不得覆盖页码')
assertEqual(state.error, afterSecondBeforeFailure.error, '旧失败不得写错误')
assertEqual(state.loading, afterSecondBeforeFailure.loading, '旧失败 finally 不得改变 loading')

const mutationGate = deferred<void>()
const mutationLoaderCalls: number[] = []
const latestLoaderRef = { current: async () => { mutationLoaderCalls.push(1) } }
const mutationRun = (async () => {
  await mutationGate.promise
  await latestLoaderRef.current()
})()
latestLoaderRef.current = async () => { mutationLoaderCalls.push(3) }
mutationGate.resolve()
await mutationRun
assertEqual(mutationLoaderCalls.join(','), '3', 'mutation 完成后应使用查询变化后的当前 loader 参数')

const unmountGate = deferred<void>()
let mutationMounted = true
let loaderCallsAfterUnmount = 0
const unmountRun = (async () => {
  await unmountGate.promise
  if (mutationMounted) loaderCallsAfterUnmount += 1
})()
mutationMounted = false
unmountGate.resolve()
await unmountRun
assertEqual(loaderCallsAfterUnmount, 0, 'mutation 挂起期间卸载后不得重新调用列表 loader')

await assertLayoutCleanupBlocksReload()

const source = readFileSync(resolve('src/pages/PosAdmin/SupplierManagement/index.tsx'), 'utf8')
assert(source.includes('listRequestGuardRef = useRef(createLatestRequestGuard())'), 'SupplierManagement 应创建主列表 guard')
const loader = extractBlock(source, 'const loadData = async () => {', '\n\n  const latestLoadDataRef', 'SupplierManagement loadData')
assert(loader.includes('runLatestGuardedRequest(listRequestGuardRef.current'), 'SupplierManagement loadData 应使用 latest guard')
assert(loader.includes('if (!mountedRef.current) {\n      return\n    }'), 'SupplierManagement loadData 在 begin 前应确认页面仍挂载')
assert(loader.includes('onSettled: () => setLoading(false)'), 'SupplierManagement loading 只能由最新 finally 关闭')
assert(source.includes('const latestLoadDataRef = useRef(loadData)'), 'SupplierManagement 应保存当前 render 的 loader')
assert(source.includes('const mountedRef = useRef(false)'), 'SupplierManagement 应记录组件挂载状态')
const latestLoaderLayoutEffect = extractBlock(source, 'useLayoutEffect(() => {\n    latestLoadDataRef.current = loadData', '\n\n  useLayoutEffect(() => {\n    mountedRef.current = true', 'SupplierManagement 当前 loader layout effect')
assertEqual(count(source, 'latestLoadDataRef.current = loadData'), 1, 'SupplierManagement loader ref 只能在 layout effect 中更新')
assert(latestLoaderLayoutEffect.includes('latestLoadDataRef.current = loadData'), 'SupplierManagement 应在每次 commit 后更新 loader ref')
const mountLifecycle = extractBlock(source, 'useLayoutEffect(() => {\n    mountedRef.current = true', '\n\n  useEffect(() => {\n    void loadData()', 'SupplierManagement 挂载生命周期')
assert(mountLifecycle.includes('useLayoutEffect'), 'SupplierManagement 应在 layout cleanup 阶段关闭 session')
assert(mountLifecycle.indexOf('mountedRef.current = false') < mountLifecycle.indexOf('listRequestGuardRef.current.invalidate()'), 'SupplierManagement 卸载时应先标记 unmounted 再 invalidate')
// HQ → HBweb 的「从 HQ 同步」已于 2026-09-29 停用，页面不应再提供该入口；「同步所选到 HQ」保留。
assert(!source.includes('handleSyncFromHq') && !source.includes('syncLocalSuppliers('), 'SupplierManagement 不应再提供从 HQ 同步入口')
assert(source.includes('syncLocalSuppliersToHq(selectedRowKeys.map(String))'), 'SupplierManagement 应保留同步所选到 HQ')
const createMutation = extractBlock(source, 'const handleCreate = async () => {', '\n\n  const openEdit', 'SupplierManagement 创建 mutation')
const editMutation = extractBlock(source, 'const handleEdit = async () => {', '\n\n  return (', 'SupplierManagement 编辑 mutation')
for (const [label, block] of [['创建', createMutation], ['编辑', editMutation]] as const) {
  assert(block.includes('await latestLoadDataRef.current()'), `SupplierManagement ${label}完成后应调用当前 loader`)
  assert(block.includes('if (mountedRef.current)'), `SupplierManagement ${label}完成后应先确认组件仍挂载`)
  assert(!block.includes('await loadData()'), `SupplierManagement ${label}不得调用旧 render loader`)
}
const listEffect = extractBlock(source, 'useEffect(() => {\n    void loadData()', '\n\n  useLayoutEffect', 'SupplierManagement 列表 effect')
assertEqual(count(listEffect, 'loadData('), 1, 'SupplierManagement effect 每次只能加载一次')
assert(listEffect.includes('listRequestGuardRef.current.invalidate()'), 'SupplierManagement effect 清理时应使旧请求失效')
assertEqual(count(source, 'await latestLoadDataRef.current()'), 2, 'SupplierManagement mutation 后仍应显式刷新列表')

console.log('SupplierManagement/requestRace.test.ts: ok')
