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

interface InvoiceResult {
  items: string[]
  total: number
}

function createInvoiceListHarness() {
  const guard = createLatestRequestGuard()
  const state = {
    data: ['initial'],
    total: 1,
    selectedRowKeys: ['selected'],
    errorCount: 0,
    loading: false,
  }

  const load = (request: Promise<InvoiceResult>) => runLatestGuardedRequest(guard, () => request, {
    onStart: () => {
      state.loading = true
    },
    onSuccess: (result) => {
      state.data = result.items
      state.total = result.total
    },
    onError: () => {
      state.errorCount += 1
    },
    onSettled: () => {
      state.loading = false
    },
  })

  const skipScope = () => {
    guard.invalidate()
    state.data = []
    state.total = 0
    state.selectedRowKeys = []
    state.loading = false
  }

  return { guard, load, skipScope, state }
}

function createFirstPageRequestHarness(initialPage: number) {
  let page = initialPage
  let mounted = true
  let deferred = false
  let invalidationCount = 0
  let requestCount = 0

  const requestFirstPage = (deferUntilCommitted = false, reloadFromDependencies = page !== 1) => {
    if (!mounted) return
    if (reloadFromDependencies) {
      invalidationCount += 1
      if (page !== 1) page = 1
      return
    }
    if (deferUntilCommitted) {
      deferred = true
      return
    }
    requestCount += 1
  }

  const runPageEffect = () => {
    requestCount += 1
  }

  const runDeferredRequest = () => {
    if (!deferred) return
    deferred = false
    requestCount += 1
  }

  const runLayoutUnmount = () => {
    mounted = false
  }

  return {
    requestFirstPage,
    runPageEffect,
    runDeferredRequest,
    runLayoutUnmount,
    get invalidationCount() {
      return invalidationCount
    },
    get requestCount() {
      return requestCount
    },
  }
}

const pageSource = readFileSync(
  path.resolve(process.cwd(), 'src/pages/PosAdmin/LocalSupplierInvoices/index.tsx'),
  'utf8',
)

async function main() {
  const failures: string[] = []

  const staleFirstFailure = await runTest('旧 A 先完成时不能覆盖 B 或提前关闭 loading', async () => {
    const harness = createInvoiceListHarness()
    const requestA = createDeferred<InvoiceResult>()
    const requestB = createDeferred<InvoiceResult>()
    const promiseA = harness.load(requestA.promise)
    const promiseB = harness.load(requestB.promise)

    requestA.resolve({ items: ['old'], total: 10 })
    await promiseA
    assert(harness.state.data.join(',') === 'initial', '旧 A 先完成不应写入列表')
    assert(harness.state.total === 1, '旧 A 先完成不应写入总数')
    assert(harness.state.selectedRowKeys.join(',') === 'selected', '旧 A 不应改变选择')
    assert(harness.state.errorCount === 0, '旧 A 不应改变错误状态')
    assert(harness.state.loading, '旧 A 的 finally 不应关闭 B 的 loading')

    requestB.resolve({ items: ['latest'], total: 30 })
    await promiseB
    assert(harness.state.data.join(',') === 'latest', '最新 B 应写入列表')
    assert(Number(harness.state.total) === 30, '最新 B 应写入总数')
    assert(harness.state.selectedRowKeys.join(',') === 'selected', '最新列表响应应保留现有选择语义')
    assert(!harness.state.loading, '最新 B 完成后应关闭 loading')
  })
  if (staleFirstFailure) failures.push(staleFirstFailure)

  const staleAfterLatestFailure = await runTest('B 先完成后 A 成功或失败均不能回写', async () => {
    for (const staleOutcome of ['success', 'failure'] as const) {
      const harness = createInvoiceListHarness()
      const requestA = createDeferred<InvoiceResult>()
      const requestB = createDeferred<InvoiceResult>()
      const promiseA = harness.load(requestA.promise)
      const promiseB = harness.load(requestB.promise)

      requestB.resolve({ items: ['latest'], total: 30 })
      await promiseB
      if (staleOutcome === 'success') requestA.resolve({ items: ['old'], total: 10 })
      else requestA.reject(new Error('stale failure'))
      await promiseA

      assert(harness.state.data.join(',') === 'latest', `旧 A ${staleOutcome} 不应覆盖列表`)
      assert(harness.state.total === 30, `旧 A ${staleOutcome} 不应覆盖总数`)
      assert(harness.state.selectedRowKeys.join(',') === 'selected', `旧 A ${staleOutcome} 不应改变选择`)
      assert(harness.state.errorCount === 0, `旧 A ${staleOutcome} 不应写入错误`)
      assert(!harness.state.loading, `旧 A ${staleOutcome} 不应改变 loading`)
    }
  })
  if (staleAfterLatestFailure) failures.push(staleAfterLatestFailure)

  const scopeSkipFailure = await runTest('scope skip 立即清空并使成功或失败晚到均无效', async () => {
    for (const staleOutcome of ['success', 'failure'] as const) {
      const harness = createInvoiceListHarness()
      const request = createDeferred<InvoiceResult>()
      const pending = harness.load(request.promise)

      harness.skipScope()
      if (staleOutcome === 'success') request.resolve({ items: ['old'], total: 10 })
      else request.reject(new Error('stale failure'))
      await pending

      assert(harness.state.data.length === 0, 'scope skip 后列表必须保持为空')
      assert(harness.state.total === 0, 'scope skip 后总数必须保持为零')
      assert(harness.state.selectedRowKeys.length === 0, 'scope skip 后必须清空选择')
      assert(harness.state.errorCount === 0, 'scope skip 后旧失败不得写入错误')
      assert(!harness.state.loading, 'scope skip 后旧 finally 不得改变 loading')
    }
  })
  if (scopeSkipFailure) failures.push(scopeSkipFailure)

  const firstPageRequestCountFailure = await runTest('查询和重置在任意当前页都只发一次请求', () => {
    const searchFromFirstPage = createFirstPageRequestHarness(1)
    searchFromFirstPage.requestFirstPage()
    assert(searchFromFirstPage.requestCount === 1, '第一页查询应立即且只请求一次')

    const searchFromLaterPage = createFirstPageRequestHarness(3)
    searchFromLaterPage.requestFirstPage()
    assert(searchFromLaterPage.requestCount === 0, '后续页查询不应使用旧页立即请求')
    assert(searchFromLaterPage.invalidationCount === 1, '后续页查询应先使旧请求失效')
    searchFromLaterPage.runPageEffect()
    assert(Number(searchFromLaterPage.requestCount) === 1, '页码提交后的 effect 应且只应请求一次')

    const resetFromFirstPage = createFirstPageRequestHarness(1)
    resetFromFirstPage.requestFirstPage(true)
    assert(resetFromFirstPage.requestCount === 0, '第一页重置应等待新筛选状态提交')
    resetFromFirstPage.runDeferredRequest()
    assert(Number(resetFromFirstPage.requestCount) === 1, '第一页重置提交后应且只应请求一次')

    const resetWithActiveSort = createFirstPageRequestHarness(1)
    resetWithActiveSort.requestFirstPage(true, true)
    assert(resetWithActiveSort.requestCount === 0, '第一页非默认排序重置不应安排 timer 请求')
    assert(resetWithActiveSort.invalidationCount === 1, '第一页非默认排序重置应先使旧请求失效')
    resetWithActiveSort.runDeferredRequest()
    assert(resetWithActiveSort.requestCount === 0, '排序依赖将触发 effect 时不得再执行 deferred 请求')
    resetWithActiveSort.runPageEffect()
    assert(Number(resetWithActiveSort.requestCount) === 1, '第一页非默认排序重置只应由依赖 effect 请求一次')

    const resetFromLaterPage = createFirstPageRequestHarness(3)
    resetFromLaterPage.requestFirstPage(true)
    assert(resetFromLaterPage.requestCount === 0, '后续页重置不应额外安排旧页请求')
    resetFromLaterPage.runDeferredRequest()
    assert(resetFromLaterPage.requestCount === 0, '后续页重置不应同时触发 deferred 请求')
    resetFromLaterPage.runPageEffect()
    assert(Number(resetFromLaterPage.requestCount) === 1, '后续页重置只应由 page effect 请求一次')
  })
  if (firstPageRequestCountFailure) failures.push(firstPageRequestCountFailure)

  const latestLoaderRefFailure = await runTest('异步 mutation 完成后必须调用当前 scope 的 loader', async () => {
    const mutation = createDeferred<void>()
    const calls: string[] = []
    const latestLoadDataRef = {
      current: async () => {
        calls.push('old-scope')
      },
    }
    const continuation = (async () => {
      await mutation.promise
      await latestLoadDataRef.current()
    })()

    latestLoadDataRef.current = async () => {
      calls.push('current-scope')
    }
    mutation.resolve()
    await continuation

    assert(calls.join(',') === 'current-scope', 'mutation continuation 不应调用发起时的旧 scope loader')
  })
  if (latestLoaderRefFailure) failures.push(latestLoaderRefFailure)

  const mutationAfterUnmountFailure = await runTest('layout cleanup 后、passive cleanup 前不得重新开始列表请求', async () => {
    const mutation = createDeferred<void>()
    let mounted = true
    let requestCount = 0
    const latestLoadDataRef = {
      current: async () => {
        if (!mounted) return
        requestCount += 1
      },
    }
    const continuation = (async () => {
      await mutation.promise
      await latestLoadDataRef.current()
    })()

    // React 在卸载 commit 阶段先执行 layout cleanup；这里刻意不等待 passive effect。
    mounted = false
    mutation.resolve()
    await continuation

    assert(requestCount === 0, '卸载后的 mutation continuation 不得开始列表请求')
  })
  if (mutationAfterUnmountFailure) failures.push(mutationAfterUnmountFailure)

  const sourceContractFailure = await runTest('发票列表 guard 与 scope skip 接线位于 loadData 内', () => {
    const loadDataSection = extractSection(pageSource, 'const loadData = async () => {', 'useLayoutEffect(() => {', 'invoice loadData')
    const committedLoaderSection = extractSection(
      pageSource,
      'useLayoutEffect(() => {\n    latestLoadDataRef.current = loadData',
      'useEffect(() => {',
      'invoice committed loader ref',
    )
    const cleanupSection = extractSection(
      pageSource,
      'useLayoutEffect(() => {\n    mountedRef.current = true',
      'useLayoutEffect(() => {\n    latestLoadDataRef.current = loadData',
      'invoice request cleanup',
    )
    const skipSection = extractSection(
      loadDataSection,
      'if (shouldSkipScopedStoreQuery(managedStoreCodes)) {',
      'const startRow =',
      'invoice scope skip',
    )
    const successSection = extractSection(loadDataSection, 'onSuccess: (result) => {', 'onError:', 'invoice onSuccess')

    assert(pageSource.includes('const listRequestGuardRef = useRef(createLatestRequestGuard())'), '发票列表缺少独立 guard')
    assert(pageSource.includes('const mountedRef = useRef(false)'), '发票列表缺少 mounted guard')
    assert(pageSource.includes('const latestLoadDataRef = useRef<() => Promise<void>>'), '发票列表缺少 current-loader ref')
    assert(loadDataSection.includes('if (!mountedRef.current) return'), 'loader 在卸载后仍可能开始请求')
    assert(committedLoaderSection.includes('latestLoadDataRef.current = loadData'), 'current-loader ref 未在 commit 后更新')
    assert(!loadDataSection.includes('latestLoadDataRef.current = loadData'), 'current-loader ref 不应在 render 期间直接赋值')
    assert(
      loadDataSection.includes('runLatestGuardedRequest(') && loadDataSection.includes('listRequestGuardRef.current'),
      '发票列表未接入 guarded request',
    )
    assert(successSection.includes('setData(result?.items ?? [])'), '最新成功未写入列表')
    assert(successSection.includes('setTotal(result?.total ?? 0)'), '最新成功未写入总数')
    assert(loadDataSection.includes('onSettled: () => setLoading(false)'), 'loading 未受最新请求保护')
    const mountedFalseIndex = cleanupSection.indexOf('mountedRef.current = false')
    const invalidateIndex = cleanupSection.indexOf('listRequestGuardRef.current.invalidate()')
    assert(mountedFalseIndex >= 0 && mountedFalseIndex < invalidateIndex, '卸载清理应先标记 unmounted，再 invalidate')
    assert(cleanupSection.includes('return () => {'), '卸载保护必须使用 layout cleanup')
    assert(!pageSource.includes('useEffect(() => () => {\n    mountedRef.current = false'), '不得把 mounted 清理推迟到 passive effect')

    const orderedStatements = [
      'listRequestGuardRef.current.invalidate()',
      'setData([])',
      'setTotal(0)',
      'setSelectedRowKeys([])',
      'setLoading(false)',
      'return',
    ]
    let previousIndex = -1
    orderedStatements.forEach((statement) => {
      const index = skipSection.indexOf(statement)
      assert(index > previousIndex, `scope skip 接线顺序错误或缺少 ${statement}`)
      previousIndex = index
    })

    const firstPageSection = extractSection(
      pageSource,
      'const requestFirstPage = (deferUntilCommitted = false, reloadFromDependencies = page !== 1) => {',
      'const handleSearch = () => {',
      'invoice first-page entry',
    )
    const searchSection = extractSection(pageSource, 'const handleSearch = () => {', 'const handleReset = () => {', 'invoice search')
    const resetSection = extractSection(pageSource, 'const handleReset = () => {', 'const handleDelete = async', 'invoice reset')
    assert(firstPageSection.includes('if (reloadFromDependencies) {'), '依赖将变化时未分流到 effect 唯一请求')
    assert(firstPageSection.includes("if (page !== 1) setPage(1)") && firstPageSection.includes('return'), '后续页请求应只切换第一页')
    assert(firstPageSection.includes('if (deferUntilCommitted) {'), '第一页重置缺少提交后请求分支')
    assert(searchSection.includes('requestFirstPage()'), '查询未统一走 first-page 入口')
    assert(
      resetSection.includes("const reloadFromDependencies = page !== 1 || sortBy !== 'createdAt' || sortOrder !== 'descend'"),
      '重置未预判页码或排序依赖变化',
    )
    assert(resetSection.includes('requestFirstPage(true, reloadFromDependencies)'), '重置未统一走依赖感知的 first-page 入口')
    assert(!searchSection.includes('setPage(1)') && !searchSection.includes('latestLoadDataRef.current()'), '查询仍存在双请求入口')
    assert(!resetSection.includes('setPage(1)') && !resetSection.includes('latestLoadDataRef.current()'), '重置仍存在双请求入口')
    const deleteSection = extractSection(pageSource, 'const handleDelete = async', 'const handleCreate = async', 'invoice delete')
    const importedSection = extractSection(
      pageSource,
      'const handleImportedInvoiceCreated = async',
      'const baseColumns: ColumnsType<LocalSupplierInvoiceListDto>',
      'invoice imported continuation',
    )
    // 「从HQ同步」已于 2026-09-29 停用，列表页只剩删除与导入两个需要刷新列表的后续动作。
    assert(!pageSource.includes('const handleSyncFromHq = async'), '列表页不应再保留 HQ 同步后续刷新逻辑')
    for (const [name, section] of [
      ['delete', deleteSection],
      ['imported', importedSection],
    ] as const) {
      assert(section.includes('latestLoadDataRef.current()'), `${name} 刷新未调用当前 loader ref`)
      assert(!section.includes('loadData()'), `${name} 仍可能调用旧闭包 loader`)
    }
  })
  if (sourceContractFailure) failures.push(sourceContractFailure)

  if (failures.length) throw new Error(`共有 ${failures.length} 个测试失败\n- ${failures.join('\n- ')}`)
  console.log('LocalSupplierInvoices.requestRace.logic.test: ok')
}

await main()
