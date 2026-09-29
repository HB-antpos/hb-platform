import { readFileSync } from 'node:fs'
import path from 'node:path'
import {
  createLatestRequestGuard,
  runLatestGuardedRequest,
} from '../../../utils/latestRequestGuard'

function assert(condition: unknown, message: string): asserts condition {
  if (!condition) {
    throw new Error(message)
  }
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

function extractSection(source: string, startMarker: string, endMarker: string, label: string) {
  const startIndex = source.indexOf(startMarker)
  assert(startIndex >= 0, `${label}未找到起始标记: ${startMarker}`)

  const endIndex = source.indexOf(endMarker, startIndex + startMarker.length)
  assert(endIndex > startIndex, `${label}未找到结束标记: ${endMarker}`)

  const section = source.slice(startIndex, endIndex).trim()
  assert(section.length > 0, `${label}切片为空`)
  return section
}

function countOccurrences(source: string, pattern: string) {
  return source.split(pattern).length - 1
}

function createLateMutationRefreshHarness() {
  let mounted = true
  let currentQuery = 'page-1'
  let committedLoader: (() => Promise<void>) | null = null
  const startedQueries: string[] = []

  const commit = () => {
    const query = currentQuery
    committedLoader = async () => {
      startedQueries.push(query)
    }
  }

  const directLoad = async (query: string) => {
    startedQueries.push(query)
  }

  const refreshCurrentList = async () => {
    if (!mounted) return
    await committedLoader?.()
  }

  const runMutation = async (mutation: Promise<void>) => {
    await mutation
    await refreshCurrentList()
  }

  const changeQuery = (query: string) => {
    currentQuery = query
  }

  const cleanup = () => {
    mounted = false
  }

  return { changeQuery, cleanup, commit, directLoad, runMutation, startedQueries }
}

interface MainListResult {
  data: string
  page: number
}

async function verifyLatestMainListResult(staleOutcome: 'success' | 'failure') {
  const guard = createLatestRequestGuard()
  const requestA = createDeferred<MainListResult>()
  const requestB = createDeferred<MainListResult>()
  const state = {
    data: 'initial',
    page: 1,
    selectedRowKeys: ['selected-before-b'],
    errorCount: 0,
    loading: false,
  }

  const handlers = {
    onStart: () => {
      state.loading = true
    },
    onSuccess: (result: MainListResult) => {
      state.data = result.data
      state.page = result.page
      state.selectedRowKeys = []
    },
    onError: () => {
      state.errorCount += 1
    },
    onSettled: () => {
      state.loading = false
    },
  }

  const promiseA = runLatestGuardedRequest(guard, () => requestA.promise, handlers)
  const promiseB = runLatestGuardedRequest(guard, () => requestB.promise, handlers)

  requestB.resolve({ data: 'page-3', page: 3 })
  await promiseB
  assert(state.data === 'page-3', '最新请求 B 应先写入数据')
  assert(state.page === 3, '最新请求 B 应先写入页码')
  assert(state.selectedRowKeys.length === 0, '只有最新请求 B 成功时应清空选择')
  assert(state.errorCount === 0, '最新请求 B 成功后不应出现错误')
  assert(!state.loading, '最新请求 B 完成后应关闭 loading')

  state.selectedRowKeys = ['selected-after-b']
  if (staleOutcome === 'success') {
    requestA.resolve({ data: 'page-1', page: 1 })
  } else {
    requestA.reject(new Error('stale request failed'))
  }
  await promiseA

  assert(state.data === 'page-3', `旧请求 A 后${staleOutcome === 'success' ? '成功' : '失败'}不应覆盖数据`)
  assert(state.page === 3, `旧请求 A 后${staleOutcome === 'success' ? '成功' : '失败'}不应覆盖页码`)
  assert(
    state.selectedRowKeys.join(',') === 'selected-after-b',
    `旧请求 A 后${staleOutcome === 'success' ? '成功' : '失败'}不应清空新选择`,
  )
  assert(state.errorCount === 0, '旧请求 A 不应更新错误状态')
  assert(!state.loading, '旧请求 A 的 finally 不应改变最新 loading 状态')
}

function createModalSessionHarness() {
  const listGuard = createLatestRequestGuard()
  let nextSessionId = 0
  let activeSessionId: number | null = null
  const state = {
    listData: '',
    supplierData: '',
    errorCount: 0,
    loading: false,
  }

  const open = (listRequest: Promise<string>, supplierRequest: Promise<string>) => {
    const sessionId = ++nextSessionId
    activeSessionId = sessionId
    listGuard.invalidate()

    const listTask = runLatestGuardedRequest(listGuard, () => listRequest, {
      onStart: () => {
        if (activeSessionId === sessionId) state.loading = true
      },
      onSuccess: (value) => {
        if (activeSessionId !== sessionId) return
        state.listData = value
      },
      onError: () => {
        if (activeSessionId !== sessionId) return
        state.errorCount += 1
      },
      onSettled: () => {
        if (activeSessionId === sessionId) state.loading = false
      },
    })

    const supplierTask = supplierRequest
      .then((value) => {
        if (activeSessionId !== sessionId) return
        state.supplierData = value
      })
      .catch(() => {
        if (activeSessionId !== sessionId) return
        state.errorCount += 1
      })

    return { listTask, supplierTask }
  }

  const close = () => {
    activeSessionId = null
    listGuard.invalidate()
    state.loading = false
  }

  return { open, close, state }
}

function createImportMutationSessionHarness() {
  let nextSessionId = 0
  let activeSessionId: number | null = null
  const state = {
    importing: false,
    successCount: 0,
    errorCount: 0,
    cancelCount: 0,
  }

  const open = () => {
    activeSessionId = ++nextSessionId
    state.importing = false
    return activeSessionId
  }

  const close = () => {
    activeSessionId = null
    state.importing = false
  }

  const startImport = async (request: Promise<boolean>) => {
    const sessionId = activeSessionId
    if (sessionId === null) return

    state.importing = true
    try {
      const success = await request
      if (activeSessionId !== sessionId) return

      if (!success) {
        state.errorCount += 1
        return
      }

      state.successCount += 1
      state.cancelCount += 1
      close()
    } catch {
      if (activeSessionId !== sessionId) return
      state.errorCount += 1
    } finally {
      if (activeSessionId === sessionId) state.importing = false
    }
  }

  return {
    getActiveSessionId: () => activeSessionId,
    open,
    close,
    startImport,
    state,
  }
}

function createDomesticConfirmationSessionHarness() {
  let nextSessionId = 0
  let activeSessionId: number | null = null
  const state = { successCount: 0, cancelCount: 0 }

  const open = () => {
    activeSessionId = ++nextSessionId
    return activeSessionId
  }

  const close = () => {
    activeSessionId = null
  }

  const createConfirmation = async (request: Promise<boolean>) => {
    const sessionId = activeSessionId
    if (sessionId === null) return null

    const success = await request
    if (!success || activeSessionId !== sessionId) return null

    return () => {
      if (activeSessionId !== sessionId) return
      state.successCount += 1
      state.cancelCount += 1
      close()
    }
  }

  return { close, createConfirmation, getActiveSessionId: () => activeSessionId, open, state }
}

function createListSessionEntryHarness() {
  const guard = createLatestRequestGuard()
  let nextSessionId = 0
  let activeSessionId: number | null = null
  let operationCount = 0
  const state = { data: '', loading: false }

  const open = () => {
    activeSessionId = ++nextSessionId
    guard.invalidate()
    return activeSessionId
  }

  const load = (
    sessionId: number | null,
    request: Promise<string>,
  ) => {
    if (sessionId === null || activeSessionId !== sessionId) {
      return Promise.resolve()
    }

    operationCount += 1
    return runLatestGuardedRequest(guard, () => request, {
      onStart: () => {
        state.loading = true
      },
      onSuccess: (value) => {
        state.data = value
      },
      onSettled: () => {
        state.loading = false
      },
    })
  }

  return { getOperationCount: () => operationCount, load, open, state }
}

async function verifyModalSessionIsolation(staleOutcome: 'success' | 'failure') {
  const harness = createModalSessionHarness()
  const session1List = createDeferred<string>()
  const session1Supplier = createDeferred<string>()
  const session1 = harness.open(session1List.promise, session1Supplier.promise)

  harness.close()

  const session2List = createDeferred<string>()
  const session2Supplier = createDeferred<string>()
  const session2 = harness.open(session2List.promise, session2Supplier.promise)

  session2Supplier.resolve('supplier-session-2')
  await session2.supplierTask
  assert(harness.state.supplierData === 'supplier-session-2', '新会话供应商请求应能独立完成')
  assert(harness.state.listData === '', '供应商完成不应提前写入列表结果')
  assert(harness.state.loading, '供应商完成不应取消并行的列表 loading')

  session2List.resolve('list-session-2')
  await session2.listTask
  assert(String(harness.state.listData) === 'list-session-2', '新会话列表请求应能独立完成')
  assert(harness.state.supplierData === 'supplier-session-2', '列表完成不应覆盖供应商结果')
  assert(harness.state.errorCount === 0, '新会话并行请求成功不应产生错误')
  assert(!harness.state.loading, '新会话列表完成后应关闭 loading')

  if (staleOutcome === 'success') {
    session1Supplier.resolve('supplier-session-1')
    session1List.resolve('list-session-1')
  } else {
    session1Supplier.reject(new Error('stale supplier failed'))
    session1List.reject(new Error('stale list failed'))
  }
  await Promise.all([session1.listTask, session1.supplierTask])

  assert(String(harness.state.listData) === 'list-session-2', '旧会话列表晚到不应污染新会话')
  assert(harness.state.supplierData === 'supplier-session-2', '旧会话供应商晚到不应污染新会话')
  assert(harness.state.errorCount === 0, '旧会话失败不应更新错误状态')
  assert(!harness.state.loading, '旧会话结束不应改变新会话 loading')
}

async function verifyImportMutationSessionIsolation(staleOutcome: 'success' | 'failure') {
  const harness = createImportMutationSessionHarness()
  const staleImport = createDeferred<boolean>()
  const firstSession = harness.open()
  const firstTask = harness.startImport(staleImport.promise)

  harness.close()
  const secondSession = harness.open()
  const currentImport = createDeferred<boolean>()
  const currentTask = harness.startImport(currentImport.promise)

  assert(harness.state.importing, '新会话导入开始后 importing 应为 true')
  if (staleOutcome === 'success') {
    staleImport.resolve(true)
  } else {
    staleImport.reject(new Error('stale import failed'))
  }
  await firstTask

  assert(harness.getActiveSessionId() === secondSession, '旧导入结束不得关闭新会话')
  assert(harness.state.importing, '旧导入 finally 不得关闭新会话 importing')
  assert(harness.state.successCount === 0, '旧导入成功不得触发新会话 onSuccess')
  assert(harness.state.cancelCount === 0, '旧导入成功不得关闭新会话')
  assert(harness.state.errorCount === 0, '旧导入失败不得显示错误')
  assert(firstSession !== secondSession, '关闭重开必须创建不同会话')

  currentImport.resolve(true)
  await currentTask
  assert(Number(harness.state.successCount) === 1, '当前会话导入成功应只触发一次 onSuccess')
  assert(Number(harness.state.cancelCount) === 1, '当前会话导入成功应只关闭自身')
  assert(!harness.state.importing, '当前会话结束后 importing 应关闭')
}

async function verifyDomesticConfirmationSessionIsolation() {
  const harness = createDomesticConfirmationSessionHarness()
  const firstSession = harness.open()
  const importRequest = createDeferred<boolean>()
  const confirmationTask = harness.createConfirmation(importRequest.promise)

  importRequest.resolve(true)
  const oldOnOk = await confirmationTask
  assert(oldOnOk, '当前会话导入成功后应创建确认回调')

  harness.close()
  const secondSession = harness.open()
  oldOnOk()

  assert(firstSession !== secondSession, '关闭重开后应进入不同会话')
  assert(harness.getActiveSessionId() === secondSession, '旧确认回调不得关闭新会话')
  assert(harness.state.successCount === 0, '旧确认回调不得触发新会话 onSuccess')
  assert(harness.state.cancelCount === 0, '旧确认回调不得触发新会话 handleCancel')
}

async function verifyExpiredListSessionReturnsBeforeGuardBegin() {
  const harness = createListSessionEntryHarness()
  const expiredSession = harness.open()
  const currentSession = harness.open()
  const currentRequest = createDeferred<string>()
  const currentTask = harness.load(currentSession, currentRequest.promise)

  await harness.load(expiredSession, Promise.resolve('expired-session'))
  assert(harness.getOperationCount() === 1, '过期会话不得执行请求 operation 或开始新的 guard request')
  assert(harness.state.loading, '过期会话不得淘汰当前请求或关闭其 loading')

  currentRequest.resolve('current-session')
  await currentTask
  assert(harness.state.data === 'current-session', '当前请求必须在过期会话调用后正常提交结果')
  assert(!harness.state.loading, '当前请求完成后应正常关闭 loading')
}

const sourceRoot = path.resolve(process.cwd(), 'src/pages')
const domesticProductsSource = readFileSync(
  path.join(sourceRoot, 'DomesticPurchase/DomesticProducts/index.tsx'),
  'utf8',
)
const warehouseProductsSource = readFileSync(
  path.join(sourceRoot, 'Warehouse/Products/index.tsx'),
  'utf8',
)
const storeOrdersSource = readFileSync(
  path.join(sourceRoot, 'Warehouse/StoreOrders/index.tsx'),
  'utf8',
)
const importFromDomesticSource = readFileSync(
  path.join(sourceRoot, 'Warehouse/Products/ImportFromDomesticModal.tsx'),
  'utf8',
)
const importNonHbSource = readFileSync(
  path.join(sourceRoot, 'Warehouse/Products/ImportNonHbModal.tsx'),
  'utf8',
)

async function main() {
  const failures: string[] = []

  const staleSuccessFailure = await runTest('最新 B 先完成后旧 A 成功不能回写任何主列表状态', async () => {
    await verifyLatestMainListResult('success')
  })
  if (staleSuccessFailure) failures.push(staleSuccessFailure)

  const staleFailureFailure = await runTest('最新 B 先完成后旧 A 失败不能回写错误或 loading', async () => {
    await verifyLatestMainListResult('failure')
  })
  if (staleFailureFailure) failures.push(staleFailureFailure)

  const invalidateFailure = await runTest('关闭会话后在途请求的全部回调均失效', async () => {
    const guard = createLatestRequestGuard()
    const request = createDeferred<string>()
    const events: string[] = []

    const pending = runLatestGuardedRequest(guard, () => request.promise, {
      onStart: () => events.push('start'),
      onSuccess: () => events.push('success'),
      onError: () => events.push('error'),
      onSettled: () => events.push('settled'),
    })

    guard.invalidate()
    request.resolve('late result')
    await pending
    assert(events.join(',') === 'start', 'invalidate 后不应再执行 success、error 或 finally')
  })
  if (invalidateFailure) failures.push(invalidateFailure)

  const lateMutationRefreshFailure = await runTest('mutation A 晚完成时只能使用 B 已提交的当前查询', async () => {
    const harness = createLateMutationRefreshHarness()
    harness.commit()
    const mutation = createDeferred<void>()
    const pendingMutation = harness.runMutation(mutation.promise)

    harness.changeQuery('page-3/filter-B')
    harness.commit()
    await harness.directLoad('page-3/filter-B')

    mutation.resolve()
    await pendingMutation

    assert(
      harness.startedQueries.join(',') === 'page-3/filter-B,page-3/filter-B',
      'mutation A 完成后应通过 current loader 重用 B 的已提交查询，不得重发旧查询',
    )
  })
  if (lateMutationRefreshFailure) failures.push(lateMutationRefreshFailure)

  const unmountedLateMutationFailure = await runTest('layout cleanup 后 mutation A 完成不得再 begin 列表请求', async () => {
    const harness = createLateMutationRefreshHarness()
    harness.commit()
    const mutation = createDeferred<void>()
    const pendingMutation = harness.runMutation(mutation.promise)

    harness.cleanup()
    mutation.resolve()
    await pendingMutation

    assert(harness.startedQueries.length === 0, '卸载后的 mutation 续跑不应启动列表请求')
  })
  if (unmountedLateMutationFailure) failures.push(unmountedLateMutationFailure)

  const staleSessionSuccessFailure = await runTest('关闭重开后旧会话列表和供应商成功晚到均无效', async () => {
    await verifyModalSessionIsolation('success')
  })
  if (staleSessionSuccessFailure) failures.push(staleSessionSuccessFailure)

  const staleSessionFailureFailure = await runTest('关闭重开后旧会话列表和供应商失败晚到均无效', async () => {
    await verifyModalSessionIsolation('failure')
  })
  if (staleSessionFailureFailure) failures.push(staleSessionFailureFailure)

  const staleImportSuccessFailure = await runTest('关闭重开后旧导入成功不会关闭或污染新会话', async () => {
    await verifyImportMutationSessionIsolation('success')
  })
  if (staleImportSuccessFailure) failures.push(staleImportSuccessFailure)

  const staleImportFailureFailure = await runTest('关闭重开后旧导入失败和 finally 不会污染新会话', async () => {
    await verifyImportMutationSessionIsolation('failure')
  })
  if (staleImportFailureFailure) failures.push(staleImportFailureFailure)

  const staleConfirmationFailure = await runTest('国内导入旧成功弹窗确认不得关闭或刷新新会话', async () => {
    await verifyDomesticConfirmationSessionIsolation()
  })
  if (staleConfirmationFailure) failures.push(staleConfirmationFailure)

  const expiredListEntryFailure = await runTest('过期会话列表调用必须在 guard begin 前返回', async () => {
    await verifyExpiredListSessionReturnsBeforeGuardBegin()
  })
  if (expiredListEntryFailure) failures.push(expiredListEntryFailure)

  const mainListContractFailure = await runTest('三个主列表均使用独立 guard 并在卸载时失效', () => {
    for (const [name, source, endMarker] of [
      ['国内商品', domesticProductsSource, 'useEffect(() => {'],
      ['仓库商品', warehouseProductsSource, 'const refreshCurrentList ='],
      ['分店进货单', storeOrdersSource, 'const loadUnmatchedStoreGroups = async () =>'],
    ] as const) {
      const loadDataSection = extractSection(source, 'const loadData = async (', endMarker, `${name} loadData`)
      const successSection = extractSection(
        loadDataSection,
        'onSuccess: (result) => {',
        'onError: (error) => {',
        `${name} loadData.onSuccess`,
      )

      assert(source.includes('const listRequestGuardRef = useRef(createLatestRequestGuard())'), `${name}缺少独立列表 guard`)
      assert(loadDataSection.includes('runLatestGuardedRequest('), `${name}列表请求未接入 latest guard`)
      assert(source.includes('listRequestGuardRef.current.invalidate()'), `${name}卸载时未使列表请求失效`)
      assert(loadDataSection.includes('onSettled: () => setLoading(false)'), `${name}的 loading 未受最新请求保护`)
      assert(successSection.includes('setSelectedRowKeys([])'), `${name}仅最新成功响应应清空选择`)
      assert(
        countOccurrences(loadDataSection, 'setSelectedRowKeys([])') === 1,
        `${name} loadData 的清空选择必须只位于 onSuccess`,
      )
    }
  })
  if (mainListContractFailure) failures.push(mainListContractFailure)

  const lateStartLoaderContractFailure = await runTest('仓库主列表仅在 layout commit 发布 current loader，卸载后禁止晚启动', () => {
    for (const [name, source, publishMarker, lifecycleMarker] of [
      [
        '仓库商品',
        warehouseProductsSource,
        'useLayoutEffect(() => {\n        loadDataRef.current = loadData',
        'useLayoutEffect(() => {\n        isMountedRef.current = true',
      ],
      [
        '分店进货单',
        storeOrdersSource,
        'useLayoutEffect(() => {\n    loadDataRef.current = loadData',
        'useLayoutEffect(() => {\n    isMountedRef.current = true',
      ],
    ] as const) {
      const loadDataSection = extractSection(source, 'const loadData = async (', publishMarker, `${name} loadData`)
      const publishSection = extractSection(
        source,
        publishMarker,
        'const refreshCurrentList =',
        `${name} current loader layout publish`,
      )
      const refreshSection = extractSection(
        source,
        'const refreshCurrentList =',
        name === '仓库商品' ? 'const stopBatchUpdateJobPolling' : 'const loadUnmatchedStoreGroups = async () =>',
        `${name} refreshCurrentList`,
      )
      const lifecycleSection = extractSection(
        source,
        lifecycleMarker,
        name === '仓库商品' ? 'useEffect(() => {' : 'const handleStatusToggle =',
        `${name} layout lifecycle`,
      )

      assert(loadDataSection.includes('if (!isMountedRef.current) {'), `${name} loader 未在 begin 前检查 mounted`)
      assert(!loadDataSection.includes('loadDataRef.current = loadData'), `${name} 不得在 render 期间写 current loader ref`)
      assert(publishSection.includes('loadDataRef.current = loadData'), `${name} 未在 layout commit 后发布 current loader`)
      assert(refreshSection.includes('if (!isMountedRef.current) {'), `${name} mutation refresh 缺少 mounted gate`)
      assert(refreshSection.includes('loadDataRef.current?.(overrides)'), `${name} mutation refresh 未调用 current loader ref`)

      const mountedFalseIndex = lifecycleSection.indexOf('isMountedRef.current = false')
      const invalidateIndex = lifecycleSection.indexOf('listRequestGuardRef.current.invalidate()')
      assert(mountedFalseIndex >= 0, `${name} layout cleanup 未标记 unmounted`)
      assert(invalidateIndex > mountedFalseIndex, `${name} layout cleanup 必须先 unmounted 再 invalidate`)
    }
  })
  if (lateStartLoaderContractFailure) failures.push(lateStartLoaderContractFailure)

  const mutationRefreshContractFailure = await runTest('仓库主列表的 post-await 刷新统一走 current loader', () => {
    for (const [name, source, startMarker, endMarker] of [
      ['商品编辑', warehouseProductsSource, 'const handleSave = async () =>', 'const handleBatchToggleActive = async'],
      ['商品批量状态', warehouseProductsSource, 'const handleBatchToggleActive = async', 'const openBatchEdit ='],
      ['商品批量编辑后台任务', warehouseProductsSource, 'const refreshBatchUpdateListWithSelection = useCallback', 'const clearSubmittedBatchUpdateSelection = useCallback'],
      ['商品套装保存', warehouseProductsSource, 'const handleSaveSetItems = async', 'const handleExport = async'],
      ['进货单未匹配分店修复', storeOrdersSource, 'const handleSaveUnmatchedStoreMappings = async', 'const updateColumnFilters ='],
      // 分店订货的 HQ 全量/增量同步已于 2026-09-29 停用，页面不再有对应刷新路径。
      ['进货单状态', storeOrdersSource, 'const handleStatusToggle =', 'const handleBatchStatusChange ='],
      ['进货单批量状态', storeOrdersSource, 'const handleBatchStatusChange =', 'const handleCopyOrderNo ='],
      ['进货单发货', storeOrdersSource, 'const handleConfirmShipping = async', 'const columnDragSensors ='],
      ['进货单删除', storeOrdersSource, 'onConfirm={async () => {', '</Popconfirm>'],
      ['进货单新建', storeOrdersSource, 'onSelect={async (store) => {', '<CopyOrderModal'],
      ['进货单复制', storeOrdersSource, 'onConfirm={async (payload) => {', '<Modal\n        title={t(\'storeOrders.fixStoreGuidTitle\''],
    ] as const) {
      const section = extractSection(source, startMarker, endMarker, name)
      assert(section.includes('refreshCurrentList('), `${name}完成后未通过 current loader 刷新`)
      assert(!section.includes('void loadData('), `${name}完成后仍会调用旧 render loader`)
    }

    const unmatchedSection = extractSection(
      storeOrdersSource,
      'const handleSaveUnmatchedStoreMappings = async',
      'const updateColumnFilters =',
      '进货单未匹配分店 Promise.all',
    )
    assert(unmatchedSection.includes('loadBranches()'), '修复未匹配分店后必须保留分店列表刷新')
    assert(unmatchedSection.includes('loadUnmatchedStoreGroups()'), '修复未匹配分店后必须保留未匹配分组刷新')

    for (const [name, source, marker] of [
      ['商品新建', warehouseProductsSource, '<CreateProductModal'],
      ['国内商品导入', warehouseProductsSource, '<ImportFromDomesticModal'],
      ['非国内商品导入', warehouseProductsSource, '<ImportNonHbModal'],
    ] as const) {
      const section = extractSection(source, marker, '/>', name)
      assert(section.includes('refreshCurrentList('), `${name}完成回调未通过 current loader 刷新`)
      assert(!section.includes('loadData('), `${name}完成回调仍捕获旧 loader`)
    }
  })
  if (mutationRefreshContractFailure) failures.push(mutationRefreshContractFailure)

  const modalSessionContractFailure = await runTest('两个导入弹窗同时隔离打开会话与列表请求', () => {
    for (const [name, source, effectEndMarker] of [
      ['国内导入', importFromDomesticSource, 'const handleSelectCurrentPage = () =>'],
      ['非国内导入', importNonHbSource, 'const columns = useMemo'],
    ] as const) {
      const loadItemsSection = extractSection(
        source,
        'const loadItems = async (',
        'const invalidateListSession = () =>',
        `${name} loadItems`,
      )
      const invalidateSection = extractSection(
        source,
        'const invalidateListSession = () =>',
        'const handleCancel = () =>',
        `${name} invalidateListSession`,
      )
      const cancelSection = extractSection(
        source,
        'const handleCancel = () =>',
        'useEffect(() => {',
        `${name} handleCancel`,
      )
      const sessionEffectSection = extractSection(
        source,
        'useEffect(() => {',
        effectEndMarker,
        `${name} open session effect`,
      )
      const handleImportSection = extractSection(
        source,
        'const handleImport = async () =>',
        '\n\n  return (',
        `${name} handleImport`,
      )

      assert(source.includes('const listRequestGuardRef = useRef(createLatestRequestGuard())'), `${name}缺少列表 guard`)
      assert(source.includes('const activeSessionIdRef = useRef<number | null>(null)'), `${name}缺少打开会话标识`)
      assert(loadItemsSection.includes('runLatestGuardedRequest('), `${name}列表未使用独立 guard`)
      assert(loadItemsSection.includes('activeSessionIdRef.current !== sessionId'), `${name}列表未阻止旧会话写入`)
      assert(
        loadItemsSection.indexOf('if (sessionId === null || activeSessionIdRef.current !== sessionId) {')
          < loadItemsSection.indexOf('runLatestGuardedRequest('),
        `${name}过期会话必须在 guard begin 前返回`,
      )
      assert(invalidateSection.includes('activeSessionIdRef.current = null'), `${name}关闭时未清除活动会话`)
      assert(invalidateSection.includes('listRequestGuardRef.current.invalidate()'), `${name}关闭时未使列表请求失效`)
      assert(invalidateSection.includes('setLoading(false)'), `${name}关闭时未同步结束 loading`)
      assert(invalidateSection.includes('setImporting(false)'), `${name}关闭时未同步结束 importing`)
      assert(cancelSection.includes('invalidateListSession()'), `${name}统一关闭入口未立即失效会话`)
      assert(cancelSection.includes('onCancel()'), `${name}统一关闭入口未通知父组件`)
      assert(sessionEffectSection.includes('if (!open) {'), `${name}缺少外部 open=false 清理`)
      assert(sessionEffectSection.includes('const sessionId = ++nextSessionIdRef.current'), `${name}打开时未创建新会话`)
      assert(sessionEffectSection.includes('activeSessionIdRef.current = sessionId'), `${name}打开时未激活新会话`)
      assert(sessionEffectSection.includes('listRequestGuardRef.current.invalidate()'), `${name}会话切换时未失效旧列表请求`)
      assert(source.includes('onCancel={handleCancel}'), `${name}弹窗未使用受保护的关闭入口`)
      assert(handleImportSection.includes('const sessionId = activeSessionIdRef.current'), `${name}导入未捕获当前打开会话`)
      assert(handleImportSection.includes('if (activeSessionIdRef.current !== sessionId) {'), `${name}旧导入完成后仍可写入新会话`)
      assert(handleImportSection.includes('if (activeSessionIdRef.current === sessionId) {\n        setImporting(false)'), `${name}旧导入 finally 仍可关闭新会话 importing`)
    }
  })
  if (modalSessionContractFailure) failures.push(modalSessionContractFailure)

  const domesticConfirmationContractFailure = await runTest('国内导入成功确认回调继续受创建时会话约束', () => {
    const successModalSection = extractSection(
      importFromDomesticSource,
      'Modal.success({',
      '\n    } catch (error) {',
      '国内导入成功弹窗',
    )
    const onOkSection = extractSection(
      successModalSection,
      'onOk: () => {',
      '\n        },',
      '国内导入成功弹窗 onOk',
    )
    const sessionCheckIndex = onOkSection.indexOf('if (activeSessionIdRef.current !== sessionId) {')
    assert(sessionCheckIndex >= 0, '国内导入成功弹窗 onOk 缺少创建时会话校验')
    assert(sessionCheckIndex < onOkSection.indexOf('onSuccess()'), '国内导入旧 onOk 可能触发新会话 onSuccess')
    assert(sessionCheckIndex < onOkSection.indexOf('handleCancel()'), '国内导入旧 onOk 可能关闭新会话')
  })
  if (domesticConfirmationContractFailure) failures.push(domesticConfirmationContractFailure)

  const supplierIsolationFailure = await runTest('非国内导入的供应商与列表并行且仅共享会话边界', () => {
    const sessionEffectSection = extractSection(
      importNonHbSource,
      'useEffect(() => {',
      'const columns = useMemo',
      '非国内导入 supplier/list session effect',
    )
    assert(
      sessionEffectSection.includes('void Promise.all([') &&
        sessionEffectSection.includes('loadItems({ page: 1 }, sessionId)') &&
        sessionEffectSection.includes('getActiveLocalSuppliers()') &&
        sessionEffectSection.includes('if (activeSessionIdRef.current !== sessionId) return'),
      '供应商和列表应并行加载，并分别校验同一打开会话',
    )
  })
  if (supplierIsolationFailure) failures.push(supplierIsolationFailure)

  if (failures.length > 0) {
    throw new Error(`共有 ${failures.length} 个测试失败\n- ${failures.join('\n- ')}`)
  }

  console.log('remotePaginationRace.logic.test: ok')
}

await main()
