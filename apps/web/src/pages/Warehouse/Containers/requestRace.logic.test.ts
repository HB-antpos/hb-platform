import { readFileSync } from 'node:fs'
import path from 'node:path'
import { createLatestRequestGuard, runLatestGuardedRequest } from '../../../utils/latestRequestGuard'

function assert(condition: unknown, message: string): asserts condition {
  if (!condition) throw new Error(message)
}

function createDeferred<T>() {
  let resolve!: (value: T) => void
  let reject!: (error: unknown) => void
  const promise = new Promise<T>((resolvePromise, rejectPromise) => {
    resolve = resolvePromise
    reject = rejectPromise
  })
  return { promise, resolve, reject }
}

function extractSection(source: string, startMarker: string, endMarker: string, label: string) {
  const startIndex = source.indexOf(startMarker)
  assert(startIndex >= 0, `${label}未找到起始标记: ${startMarker}`)
  const endIndex = source.indexOf(endMarker, startIndex + startMarker.length)
  assert(endIndex > startIndex, `${label}未找到结束标记: ${endMarker}`)
  const section = source.slice(startIndex, endIndex).trim()
  assert(section.length > 0, `${label}切片为空`)
  return section
}

async function runTest(name: string, execute: () => void | Promise<void>): Promise<string | null> {
  try {
    await execute()
    console.log(`ok - ${name}`)
    return null
  } catch (error) {
    const reason = error instanceof Error ? error.message : String(error)
    console.error(`not ok - ${name}`)
    console.error(reason)
    return `${name}: ${reason}`
  }
}

interface ContainerResult {
  containers: string[]
  totalCount: number
  page: number
  pageSize: number
}

function createContainerListHarness() {
  const guard = createLatestRequestGuard()
  const state = {
    containers: ['initial'],
    total: 1,
    page: 1,
    pageSize: 20,
    errorCount: 0,
    loading: false,
  }

  const load = (request: Promise<ContainerResult>) => runLatestGuardedRequest(guard, () => request, {
    onStart: () => {
      state.loading = true
    },
    onSuccess: (result) => {
      state.containers = result.containers
      state.total = result.totalCount
      state.page = result.page
      state.pageSize = result.pageSize
    },
    onError: () => {
      state.errorCount += 1
    },
    onSettled: () => {
      state.loading = false
    },
  })

  return { guard, load, state }
}

function createFirstPageRequestHarness(initialPage: number) {
  let page = initialPage
  let mounted = true
  let pendingRequest: { options: Record<string, unknown>; resolve: () => void } | null = null
  let activeResolver: (() => void) | null = null
  const requests: Array<{ options: Record<string, unknown>; network: ReturnType<typeof createDeferred<void>> }> = []

  const resolvePendingRequest = () => {
    const pending = pendingRequest
    pendingRequest = null
    pending?.resolve()
  }

  const resolveActiveRequest = (expectedResolver?: () => void) => {
    const active = activeResolver
    if (!active || (expectedResolver && active !== expectedResolver)) return
    activeResolver = null
    active()
  }

  const startRequest = (options: Record<string, unknown>, resolve: () => void) => {
    const network = createDeferred<void>()
    activeResolver = resolve
    requests.push({ options, network })
    void network.promise.finally(() => resolveActiveRequest(resolve))
  }

  const requestFirstPage = (options: Record<string, unknown> = {}): Promise<void> => {
    if (!mounted) return Promise.resolve()

    resolvePendingRequest()
    resolveActiveRequest()
    if (page === 1) {
      return new Promise((resolve) => startRequest(options, resolve))
    }

    return new Promise((resolve) => {
      pendingRequest = { options, resolve }
      page = 1
    })
  }

  const runPageEffect = () => {
    const pending = pendingRequest
    if (page === 1 && pending) {
      pendingRequest = null
      startRequest(pending.options, pending.resolve)
      return
    }

    resolvePendingRequest()
    resolveActiveRequest()
  }

  const runLayoutUnmount = () => {
    mounted = false
    resolvePendingRequest()
    resolveActiveRequest()
  }

  return {
    requestFirstPage,
    runPageEffect,
    runLayoutUnmount,
    requests,
    get hasPendingRequest() {
      return pendingRequest !== null
    },
    get hasActiveResolver() {
      return activeResolver !== null
    },
  }
}

const pageSource = readFileSync(
  path.resolve(process.cwd(), 'src/pages/Warehouse/Containers/index.tsx'),
  'utf8',
)

async function main() {
  const failures: string[] = []

  const staleFirstFailure = await runTest('旧 A 先失败时不能报错或提前关闭 B loading', async () => {
    const harness = createContainerListHarness()
    const requestA = createDeferred<ContainerResult>()
    const requestB = createDeferred<ContainerResult>()
    const promiseA = harness.load(requestA.promise)
    const promiseB = harness.load(requestB.promise)

    requestA.reject(new Error('stale failure'))
    await promiseA
    assert(harness.state.containers.join(',') === 'initial', '旧失败不应覆盖货柜')
    assert(harness.state.errorCount === 0, '旧失败不应显示错误')
    assert(harness.state.loading, '旧 finally 不应关闭 B loading')

    requestB.resolve({ containers: ['page-2'], totalCount: 50, page: 2, pageSize: 20 })
    await promiseB
    assert(harness.state.containers.join(',') === 'page-2', '最新 B 应写入货柜')
    assert(harness.state.total === 50, '最新 B 应写入总数')
    assert(harness.state.page === 2, '最新 B 应写入页码')
    assert(harness.state.pageSize === 20, '最新 B 应写入页长')
    assert(!harness.state.loading, '最新 B 完成后应关闭 loading')
  })
  if (staleFirstFailure) failures.push(staleFirstFailure)

  const latestFirstFailure = await runTest('B 先完成后旧 A 成功不能覆盖任何分页状态', async () => {
    const harness = createContainerListHarness()
    const requestA = createDeferred<ContainerResult>()
    const requestB = createDeferred<ContainerResult>()
    const promiseA = harness.load(requestA.promise)
    const promiseB = harness.load(requestB.promise)

    requestB.resolve({ containers: ['page-3'], totalCount: 70, page: 3, pageSize: 50 })
    await promiseB
    requestA.resolve({ containers: ['page-1'], totalCount: 10, page: 1, pageSize: 20 })
    await promiseA

    assert(harness.state.containers.join(',') === 'page-3', '旧成功不应覆盖货柜')
    assert(harness.state.total === 70, '旧成功不应覆盖总数')
    assert(harness.state.page === 3, '旧成功不应覆盖页码')
    assert(harness.state.pageSize === 50, '旧成功不应覆盖页长')
    assert(harness.state.errorCount === 0, '旧成功不应改变错误状态')
    assert(!harness.state.loading, '旧 finally 不应改变最新 loading')
  })
  if (latestFirstFailure) failures.push(latestFirstFailure)

  const firstPageRequestCountFailure = await runTest('回第一页入口在任意当前页都只发一次请求', async () => {
    const fromFirstPage = createFirstPageRequestHarness(1)
    const firstPagePromise = fromFirstPage.requestFirstPage({ filter: 'current' })
    assert(fromFirstPage.requests.length === 1, '第一页直接刷新应只请求一次')
    assert(fromFirstPage.requests[0]?.options.filter === 'current', '第一页直接刷新应保留 options')
    fromFirstPage.requests[0]?.network.resolve()
    await firstPagePromise

    const fromLaterPage = createFirstPageRequestHarness(3)
    const laterPagePromise = fromLaterPage.requestFirstPage({ filter: 'pending' })
    assert(fromLaterPage.requests.length === 0, '非第一页调用入口时不应立即请求')
    fromLaterPage.runPageEffect()
    assert(Number(fromLaterPage.requests.length) === 1, '切换到第一页后 effect 应只请求一次')
    assert(fromLaterPage.requests[0]?.options.filter === 'pending', 'page effect 应消费 pending options')
    fromLaterPage.requests[0]?.network.resolve()
    await laterPagePromise
  })
  if (firstPageRequestCountFailure) failures.push(firstPageRequestCountFailure)

  const activeResolverUnmountFailure = await runTest('page effect 启动的网络挂起时，layout 卸载必须立即结束等待', async () => {
    const harness = createFirstPageRequestHarness(3)
    let completed = false
    const firstPagePromise = harness.requestFirstPage({ filter: 'hung-on-unmount' }).then(() => {
      completed = true
    })
    harness.runPageEffect()
    assert(harness.requests.length === 1 && harness.hasActiveResolver, 'page effect 应把 pending resolver 转为 active')

    // 网络仍挂起；layout cleanup 必须先于 passive effect 同步释放调用者。
    harness.runLayoutUnmount()
    await Promise.resolve()
    assert(completed, '卸载不应等待挂起网络才结束 first-page Promise')
    assert(!harness.hasActiveResolver && !harness.hasPendingRequest, '卸载后 resolver 必须清空')

    harness.requests[0]?.network.resolve()
    await firstPagePromise
    assert(!harness.hasActiveResolver, '旧网络 finally 应幂等，不得恢复已清理 resolver')
  })
  if (activeResolverUnmountFailure) failures.push(activeResolverUnmountFailure)

  const activeResolverSupersedeFailure = await runTest('挂起网络被新请求替代时旧等待立即结束且旧 finally 不影响新请求', async () => {
    const harness = createFirstPageRequestHarness(3)
    let oldCompleted = false
    let latestCompleted = false
    const oldPromise = harness.requestFirstPage({ filter: 'old' }).then(() => {
      oldCompleted = true
    })
    harness.runPageEffect()
    const oldNetwork = harness.requests[0]?.network

    const latestPromise = harness.requestFirstPage({ filter: 'latest' }).then(() => {
      latestCompleted = true
    })
    await Promise.resolve()
    assert(oldCompleted, '新请求应同步释放被替代的旧等待')
    assert(!latestCompleted, '最新请求网络未结束前不应完成')
    assert(harness.requests.length === 2, '替代请求应只新增一次网络请求')
    assert(harness.requests[1]?.options.filter === 'latest', '替代请求应保留最新 options')

    oldNetwork?.resolve()
    await oldPromise
    await Promise.resolve()
    assert(!latestCompleted && harness.hasActiveResolver, '旧网络 finally 不得结束最新请求')

    harness.requests[1]?.network.resolve()
    await latestPromise
    assert(latestCompleted && !harness.hasActiveResolver, '最新网络完成后应且只应结束最新等待')
  })
  if (activeResolverSupersedeFailure) failures.push(activeResolverSupersedeFailure)

  const mutationAfterUnmountFailure = await runTest('mutation 在 layout cleanup 后、passive cleanup 前不得重新请求第一页', async () => {
    const mutation = createDeferred<void>()
    const harness = createFirstPageRequestHarness(3)
    const continuation = (async () => {
      await mutation.promise
      await harness.requestFirstPage({ filter: 'stale-session' })
    })()

    harness.runLayoutUnmount()
    mutation.resolve()
    await continuation

    assert(Number(harness.requests.length) === 0, '卸载后的 mutation continuation 不得开始列表请求')
  })
  if (mutationAfterUnmountFailure) failures.push(mutationAfterUnmountFailure)

  const currentMutationLoaderFailure = await runTest('异步 mutation 使用当前页长和筛选快照', async () => {
    const mutation = createDeferred<void>()
    const requests: string[] = []
    const latestRequestFirstPageRef = {
      current: async () => {
        requests.push('20:old-filter')
      },
    }
    const continuation = (async () => {
      await mutation.promise
      await latestRequestFirstPageRef.current()
    })()

    latestRequestFirstPageRef.current = async () => {
      requests.push('50:current-filter')
    }
    mutation.resolve()
    await continuation

    assert(requests.join(',') === '50:current-filter', 'mutation continuation 不应使用旧 pageSize/filter 闭包')
  })
  if (currentMutationLoaderFailure) failures.push(currentMutationLoaderFailure)

  const sourceContractFailure = await runTest('货柜列表全部状态提交均位于 guarded loader', () => {
    const loadDataSection = extractSection(
      pageSource,
      'const loadData = async (',
      'const requestFirstPage =',
      'containers loadData',
    )
    const successSection = extractSection(loadDataSection, 'onSuccess: (result) => {', 'onError:', 'containers onSuccess')
    const firstPageSection = extractSection(
      pageSource,
      'const requestFirstPage = (options: LoadDataOptions = {}): Promise<void> => {',
      'useLayoutEffect(() => {',
      'containers requestFirstPage',
    )
    const committedLoaderSection = extractSection(
      pageSource,
      'useLayoutEffect(() => {\n    latestLoadDataRef.current = loadData',
      'useEffect(() => {',
      'containers committed loader refs',
    )
    const pageEffectSection = extractSection(
      pageSource,
      'useEffect(() => {\n    const pendingRequest = pendingFirstPageRequestRef.current',
      'const handleCreate = async',
      'containers page effect',
    )
    const cleanupSection = extractSection(
      pageSource,
      'useLayoutEffect(() => {\n    mountedRef.current = true',
      'useLayoutEffect(() => {\n    latestLoadDataRef.current = loadData',
      'containers request cleanup',
    )

    assert(pageSource.includes('const listRequestGuardRef = useRef(createLatestRequestGuard())'), '货柜列表缺少独立 guard')
    assert(pageSource.includes('const mountedRef = useRef(false)'), '货柜列表缺少 mounted guard')
    assert(pageSource.includes('const latestLoadDataRef = useRef<('), '货柜列表缺少 current loadData ref')
    assert(pageSource.includes('const activeFirstPageResolverRef = useRef<(() => void) | null>(null)'), '货柜列表缺少 active resolver')
    assert(pageSource.includes('const latestRequestFirstPageRef = useRef<'), '货柜列表缺少 current first-page ref')
    assert(loadDataSection.includes('if (!mountedRef.current) return'), 'loader 在卸载后仍可能开始请求')
    assert(firstPageSection.includes('if (!mountedRef.current) return Promise.resolve()'), '回第一页入口在卸载后仍可能开始请求')
    assert(committedLoaderSection.includes('latestLoadDataRef.current = loadData'), 'current loadData ref 未在 commit 后更新')
    assert(committedLoaderSection.includes('latestRequestFirstPageRef.current = requestFirstPage'), 'current first-page ref 未在 commit 后更新')
    assert(!loadDataSection.includes('latestLoadDataRef.current = loadData'), 'current loadData ref 不应在 render 期间直接赋值')
    assert(!firstPageSection.includes('latestRequestFirstPageRef.current = requestFirstPage'), 'current first-page ref 不应在 render 期间直接赋值')
    assert(
      loadDataSection.includes('runLatestGuardedRequest(') && loadDataSection.includes('listRequestGuardRef.current'),
      '货柜列表未接入 guarded request',
    )
    assert(successSection.includes('setContainers(result.containers)'), '最新成功未写入货柜')
    assert(successSection.includes('setTotal(result.totalCount)'), '最新成功未写入总数')
    assert(successSection.includes('setPage(result.page)'), '最新成功未写入页码')
    assert(successSection.includes('setPageSize(result.pageSize)'), '最新成功未写入页长')
    assert(loadDataSection.includes('onError: (error) => {'), '错误提示未受最新请求保护')
    assert(loadDataSection.includes('onSettled: () => setLoading(false)'), 'loading 未受最新请求保护')
    assert(cleanupSection.includes('listRequestGuardRef.current.invalidate()'), '卸载时未 invalidate')
    assert(cleanupSection.includes('resolvePendingFirstPageRequest()'), '卸载时未结束 pending 刷新')
    assert(cleanupSection.includes('resolveActiveFirstPageRequest()'), '卸载时未同步结束 active 刷新')
    assert(cleanupSection.includes('return () => {'), '卸载保护必须使用 layout cleanup')
    assert(!pageSource.includes('useEffect(() => () => {\n    mountedRef.current = false'), '不得把 mounted 清理推迟到 passive effect')
    const mountedFalseIndex = cleanupSection.indexOf('mountedRef.current = false')
    const pendingResolveIndex = cleanupSection.indexOf('resolvePendingFirstPageRequest()')
    const activeResolveIndex = cleanupSection.indexOf('resolveActiveFirstPageRequest()')
    const invalidateIndex = cleanupSection.indexOf('listRequestGuardRef.current.invalidate()')
    assert(
      mountedFalseIndex >= 0 &&
        mountedFalseIndex < pendingResolveIndex &&
        pendingResolveIndex < activeResolveIndex &&
        activeResolveIndex < invalidateIndex,
      '卸载清理应先标记 unmounted，再结束 pending/active 请求并 invalidate',
    )
    assert(pageSource.includes('const [pageSize, setPageSize] = useState(20)'), '默认页长被意外修改')
    assert(firstPageSection.includes('if (page === 1) {'), '第一页刷新未直接走单请求')
    assert(firstPageSection.includes('pendingFirstPageRequestRef.current = { options, resolve }'), '非第一页未保存 pending options')
    assert(firstPageSection.includes('setPage(1)'), '非第一页未只切换页码')
    assert(pageEffectSection.includes('pendingFirstPageRequestRef.current = null'), 'page effect 未消费 pending 请求')
    assert(pageEffectSection.includes('pendingRequest.options'), 'page effect 未传递 pending options')
    assert(pageEffectSection.includes('startFirstPageRequest(pendingRequest.options, pendingRequest.resolve)'), 'pending resolver 未转为 active 网络等待')
    assert(pageSource.includes('activeFirstPageResolverRef.current = resolve'), '网络开始时未登记 active resolver')
    assert(pageSource.includes('resolveActiveFirstPageRequest(resolve)'), '网络 finally 未幂等收口 active resolver')

    // HQ 同步入口已于 2026-09-29 停用，只剩创建成功后刷新第一页。
    assert(pageSource.split('await latestRequestFirstPageRef.current()').length - 1 === 1, '创建成功后应统一使用 current first-page ref')
    // 重设计后去掉「查询/重置」按钮：更多筛选、关键字、状态页签、日期类型与区间、清空全部都即时生效，
    // 但仍必须统一走 first-page 入口，不能绕过竞态守卫直接请求。
    assert(pageSource.includes('void requestFirstPage({ columnFilters: nextFilters })'), '更多筛选未统一走 first-page 入口')
    const applyKeywordSection = extractSection(pageSource, 'const applyKeyword = (value: string) => {', 'useLayoutEffect(() => {', 'containers applyKeyword')
    assert(applyKeywordSection.includes('void requestFirstPage({ itemNumberFilter: nextKeyword })'), '关键字未统一走 first-page 入口')
    assert(pageSource.includes('onPressEnter={() => applyKeyword(keywordDraft)}'), '回车查询未走关键字入口')
    assert(
      pageSource.includes('window.setTimeout(() => latestApplyKeywordRef.current(value), 300)'),
      '关键字防抖应在 300ms 后通过 commit 后的最新入口生效，避免旧闭包带旧筛选',
    )
    assert(committedLoaderSection.includes('latestApplyKeywordRef.current = applyKeyword'), '关键字入口 ref 未在 commit 后更新')
    assert(pageSource.includes('void requestFirstPage({ statusTab: key })'), '状态页签未统一走 first-page 入口')
    assert(pageSource.includes('void requestFirstPage({ dateType: value })'), '日期类型未统一走 first-page 入口')
    assert(pageSource.includes('void requestFirstPage({ dateRange: value })'), '日期区间未统一走 first-page 入口')
    assert(
      pageSource.includes("void requestFirstPage({ dateRange: null, itemNumberFilter: '', columnFilters: {} })"),
      '清空全部未统一走 first-page 入口',
    )
    assert(!pageSource.includes('loadData(1, pageSize'), '页面仍存在绕过单一入口的第一页请求')

    // 计数与到岸周历是额外请求，各用独立 guard，卸载时一并作废，防抖定时器也要清掉。
    assert(pageSource.includes('const countsRequestGuardRef = useRef(createLatestRequestGuard())'), '计数请求缺少独立 guard')
    assert(pageSource.includes('const overviewRequestGuardRef = useRef(createLatestRequestGuard())'), '到岸周历请求缺少独立 guard')
    assert(pageSource.includes('countsRequestGuardRef.current,') && pageSource.includes('overviewRequestGuardRef.current,'), '计数与周历请求未接入 guarded request')
    assert(
      cleanupSection.includes('countsRequestGuardRef.current.invalidate()') &&
        cleanupSection.includes('overviewRequestGuardRef.current.invalidate()') &&
        cleanupSection.includes('window.clearTimeout(keywordTimerRef.current)'),
      '卸载时未作废计数/周历请求或未清理关键字防抖',
    )
  })
  if (sourceContractFailure) failures.push(sourceContractFailure)

  if (failures.length) throw new Error(`共有 ${failures.length} 个测试失败\n- ${failures.join('\n- ')}`)
  console.log('Containers.requestRace.logic.test: ok')
}

await main()
