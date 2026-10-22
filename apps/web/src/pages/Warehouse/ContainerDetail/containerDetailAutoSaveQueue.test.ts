import * as autoSaveQueueModule from './containerDetailAutoSaveQueue'

const {
  buildContainerDetailAutoSaveContextKey,
  createContainerDetailAutoSaveQueue,
  ensureContainerDetailAutoSaveContextSnapshot,
  isContainerDetailAutoSaveContextCurrent,
} = autoSaveQueueModule

function assert(condition: unknown, message: string): asserts condition {
  if (!condition) throw new Error(message)
}

function assertEqual<T>(actual: T, expected: T, message: string) {
  if (actual !== expected) {
    throw new Error(`${message}\nexpected: ${String(expected)}\nactual: ${String(actual)}`)
  }
}

function assertDeepEqual(actual: unknown, expected: unknown, message: string) {
  const actualJson = JSON.stringify(actual)
  const expectedJson = JSON.stringify(expected)
  if (actualJson !== expectedJson) {
    throw new Error(`${message}\nexpected: ${expectedJson}\nactual: ${actualJson}`)
  }
}

function deferred<T>() {
  let resolve!: (value: T | PromiseLike<T>) => void
  let reject!: (reason?: unknown) => void
  const promise = new Promise<T>((resolvePromise, rejectPromise) => {
    resolve = resolvePromise
    reject = rejectPromise
  })
  return { promise, resolve, reject }
}

async function flushMicrotasks() {
  await Promise.resolve()
  await Promise.resolve()
}

function getUnsettledPatches(
  queue: ReturnType<typeof createContainerDetailAutoSaveQueue>,
  contextKey: string,
) {
  return (queue as typeof queue & {
    getUnsettledPatches: (key: string) => Record<string, Record<string, unknown>>
  }).getUnsettledPatches(contextKey)
}

function applyAutoSavePatches(
  rows: Array<Record<string, unknown>>,
  patches: Record<string, Record<string, unknown>>,
) {
  const apply = (autoSaveQueueModule as unknown as {
    applyContainerDetailAutoSavePatches: (
      sourceRows: Array<Record<string, unknown>>,
      latestPatches: Record<string, Record<string, unknown>>,
    ) => Array<Record<string, unknown>>
  }).applyContainerDetailAutoSavePatches
  return apply(rows, patches)
}

type ContainerDetailAutoSaveLifecycleAction = 'attach' | 'discard' | 'none'

function resolveKeepAliveLifecycleAction(active: boolean, contextKey: string): ContainerDetailAutoSaveLifecycleAction {
  const resolver = (autoSaveQueueModule as unknown as {
    resolveContainerDetailAutoSaveLifecycleAction?: (
      isActive: boolean,
      key: string,
    ) => ContainerDetailAutoSaveLifecycleAction
  }).resolveContainerDetailAutoSaveLifecycleAction
  // 未修复版本只要恢复草稿就无条件 attach；保留该行为作为红灯基线。
  return resolver?.(active, contextKey) ?? (contextKey ? 'attach' : 'none')
}

async function testSingleFlightAndLatestWinsMerge() {
  const requests: Array<ReturnType<typeof deferred<void>>> = []
  const batches: Array<Array<{ hguid: string; patch: Record<string, unknown> }>> = []
  let activeRequests = 0
  let maxActiveRequests = 0
  const queue = createContainerDetailAutoSaveQueue({
    sendBatch: async (_contextKey, intents) => {
      batches.push(intents.map((intent) => ({ hguid: intent.hguid, patch: intent.patch })))
      activeRequests += 1
      maxActiveRequests = Math.max(maxActiveRequests, activeRequests)
      const request = deferred<void>()
      requests.push(request)
      try {
        await request.promise
      } finally {
        activeRequests -= 1
      }
      return { validationErrors: [] }
    },
  })

  queue.enqueue('container-a::draft-a', 'row-a', { 单件装箱数: 10 })
  await flushMicrotasks()
  assertEqual(batches.length, 1, '第一项修改应立即开始保存')

  queue.enqueue('container-a::draft-a', 'row-b', { 备注: 'B' })
  queue.enqueue('container-a::draft-a', 'row-a', { 单件装箱数: 20 })
  queue.enqueue('container-a::draft-a', 'row-a', { 单件装箱数: 30, 中包数: 6 })
  await flushMicrotasks()
  assertEqual(batches.length, 1, '首个请求在途时不得启动并发请求')

  requests[0].resolve()
  await flushMicrotasks()
  assertEqual(batches.length, 2, '首个请求完成后应发送合并后的待保存批次')
  assertDeepEqual(
    batches[1],
    [
      { hguid: 'row-b', patch: { 备注: 'B' } },
      { hguid: 'row-a', patch: { 单件装箱数: 30, 中包数: 6 } },
    ],
    '待保存项应按首次入队顺序合并，同一字段只保留最后一次修改',
  )

  requests[1].resolve()
  await queue.drain('container-a::draft-a')
  assertEqual(maxActiveRequests, 1, '同一货柜上下文最多只能有一个请求在途')
  assertEqual(queue.getSnapshot('container-a::draft-a').unsavedFieldCount, 0, '全部保存后不应残留未保存字段')
}

async function testDrainIncludesWorkEnqueuedWhileWaiting() {
  const contextKey = 'drain-context'
  const requests: Array<ReturnType<typeof deferred<void>>> = []
  const sentValues: number[] = []
  let latestRowValue = 1
  const queue = createContainerDetailAutoSaveQueue({
    sendBatch: async (_contextKey, _intents) => {
      sentValues.push(latestRowValue)
      const request = deferred<void>()
      requests.push(request)
      await request.promise
      return { validationErrors: [] }
    },
  })

  queue.enqueue(contextKey, 'row', { 单件体积: 0.1 })
  await flushMicrotasks()
  const drainPromise = queue.drain(contextKey)
  latestRowValue = 2
  queue.enqueue(contextKey, 'row', { 单件体积: 0.2 })
  requests[0].resolve()
  await flushMicrotasks()
  assertDeepEqual(sentValues, [1, 2], '第二批真正发送时应读取调用方的最新行快照')
  requests[1].resolve()
  await drainPromise
}

async function testFailureRevisionAndRetry() {
  const contextKey = 'failure-revision-context'
  const requests: Array<ReturnType<typeof deferred<'success' | 'fail'>>> = []
  const queue = createContainerDetailAutoSaveQueue({
    sendBatch: async () => {
      const request = deferred<'success' | 'fail'>()
      requests.push(request)
      if (await request.promise === 'fail') throw new Error('network down')
      return { validationErrors: [] }
    },
  })

  queue.enqueue(contextKey, 'row', { 备注: 'old' })
  await flushMicrotasks()
  queue.enqueue(contextKey, 'row', { 备注: 'new' })
  requests[0].resolve('fail')
  await flushMicrotasks()
  assertEqual(queue.getSnapshot(contextKey).failureCount, 0, '旧 revision 失败不得覆盖已排队的新值')
  requests[1].resolve('success')
  await queue.drain(contextKey)

  queue.enqueue(contextKey, 'row', { 中包数: 8 })
  await flushMicrotasks()
  requests[2].resolve('fail')
  await flushMicrotasks()
  const failedSnapshot = queue.getSnapshot(contextKey)
  assertEqual(failedSnapshot.failureCount, 1, '最新 revision 请求失败应保留一个字段级失败')
  assertEqual(failedSnapshot.failures[0]?.message, 'network down', '失败状态应保留可展示的错误消息')

  queue.retryFailed(contextKey)
  await flushMicrotasks()
  requests[3].resolve('success')
  await queue.drain(contextKey)
  assertEqual(queue.getSnapshot(contextKey).failureCount, 0, '显式重试成功后应清除失败状态')
}

async function testCurrentFailurePausesDependentPendingUntilAtomicRetry() {
  const contextKey = 'dependent-failure-context'
  const requests: Array<ReturnType<typeof deferred<'success' | 'fail'>>> = []
  const batches: Array<Array<{ hguid: string; patch: Record<string, unknown> }>> = []
  const queue = createContainerDetailAutoSaveQueue({
    sendBatch: async (_contextKey, intents) => {
      batches.push(intents.map((intent) => ({ hguid: intent.hguid, patch: intent.patch })))
      const request = deferred<'success' | 'fail'>()
      requests.push(request)
      if (await request.promise === 'fail') throw new Error('network down')
      return { validationErrors: [] }
    },
  })

  queue.enqueue(contextKey, 'row', { 单件装箱数: 10 })
  await flushMicrotasks()
  queue.enqueue(contextKey, 'row', { 单件体积: 0.2 })
  requests[0].resolve('fail')
  await flushMicrotasks()

  assertEqual(batches.length, 1, '当前 packing 保存失败后必须暂停依赖它计算的 pending 请求')
  assertEqual(queue.getSnapshot(contextKey).failureCount, 1, '当前 revision 失败应保留失败状态')
  assertDeepEqual(
    getUnsettledPatches(queue, contextKey),
    { row: { 单件装箱数: 10, 单件体积: 0.2 } },
    '暂停期间应同时暴露失败字段和依赖它的 pending 最新值',
  )

  let drainError: unknown
  try {
    await queue.drain(contextKey)
  } catch (error) {
    drainError = error
  }
  assert(drainError instanceof Error, '存在失败时 drain 应停止并返回可处理错误')
  assertEqual(batches.length, 1, 'drain 不得绕过失败继续发送 pending')

  queue.retryFailed(contextKey)
  await flushMicrotasks()
  assertEqual(batches.length, 2, '显式重试才可恢复发送')
  assertEqual(batches[1].length, 1, '同一行失败字段与 pending 字段应合为一个请求意图')
  assertDeepEqual(
    batches[1][0],
    { hguid: 'row', patch: { 单件体积: 0.2, 单件装箱数: 10 } },
    '重试应在启动 transport 前原子合并失败 patch 和既有 pending，保持 latest-wins',
  )

  requests[1].resolve('success')
  await queue.drain(contextKey)
  assertEqual(queue.getSnapshot(contextKey).unsavedFieldCount, 0, '合并重试成功后应清空所有未结算字段')
}

async function testIncompleteBatchCountMismatchBecomesRetryableFailure() {
  const firstResponse = deferred<{
    totalUpdated: number
    totalRequested: number
    validationErrors: never[]
  }>()
  const batches: Array<Record<string, unknown>> = []
  let attempt = 0
  const queue = createContainerDetailAutoSaveQueue({
    sendBatch: async (_contextKey, intents) => {
      attempt += 1
      batches.push({ ...intents[0]?.patch })
      return attempt === 1
        ? firstResponse.promise
        : { totalUpdated: 1, totalRequested: 1, validationErrors: [] }
    },
  })

  queue.enqueue('incomplete-context', 'row', { 备注: '服务端未确认值' })
  await flushMicrotasks()
  queue.enqueue('incomplete-context', 'row', { 中包数: 7 })
  firstResponse.resolve({ totalUpdated: 0, totalRequested: 1, validationErrors: [] })
  await flushMicrotasks()

  const failedSnapshot = queue.getSnapshot('incomplete-context')
  assertEqual(failedSnapshot.failureCount, 1, '静默 count mismatch 必须转成可重试失败')
  assertEqual(failedSnapshot.failures[0]?.code, 'INCOMPLETE_BATCH', '不完整批次应暴露稳定错误码')
  assertEqual(attempt, 1, '静默 count mismatch 后必须暂停随后排队的依赖 patch')
  assertDeepEqual(
    getUnsettledPatches(queue, 'incomplete-context'),
    { row: { 备注: '服务端未确认值', 中包数: 7 } },
    '静默 count mismatch 不得清除当前 revision 或随后 pending 的本地值',
  )

  queue.retryFailed('incomplete-context')
  await queue.drain('incomplete-context')
  assertEqual(attempt, 2, '静默 count mismatch 应支持显式重试')
  assertEqual(batches[1]?.备注, '服务端未确认值', '重试应重新发送未确认 patch')
  assertEqual(batches[1]?.中包数, 7, '重试应与暂停的 pending patch 原子合并')
  assertEqual(queue.getSnapshot('incomplete-context').failureCount, 0, '重试完整成功后应清空失败')
}

async function testExplicitValidationErrorDoesNotGeneralizeCountMismatch() {
  let attempt = 0
  const queue = createContainerDetailAutoSaveQueue({
    sendBatch: async () => {
      attempt += 1
      return attempt === 1
        ? {
            totalUpdated: 0,
            totalRequested: 1,
            validationErrors: [{
              hguid: 'row',
              field: '中包数',
              code: 'INVALID_INNER_PACK',
              message: '中包数无效',
            }],
          }
        : { totalUpdated: 1, totalRequested: 1, validationErrors: [] }
    },
  })

  queue.enqueue('explicit-partial-context', 'row', { 备注: '已成功字段', 中包数: 3 })
  await flushMicrotasks()

  const failedSnapshot = queue.getSnapshot('explicit-partial-context')
  assertEqual(failedSnapshot.failureCount, 1, '明确 validation error 只应留下对应字段失败')
  assertEqual(failedSnapshot.failures[0]?.field, '中包数', '明确字段错误不得泛化到同批其他字段')
  assertEqual(failedSnapshot.failures[0]?.code, 'INVALID_INNER_PACK', '应保留服务端明确错误码')
  assertDeepEqual(
    getUnsettledPatches(queue, 'explicit-partial-context'),
    { row: { 中包数: 3 } },
    '有明确 validationErrors 时不得把同批已成功字段无谓标错',
  )

  queue.retryFailed('explicit-partial-context')
  await queue.drain('explicit-partial-context')
  assertEqual(attempt, 2, '明确字段失败仍应正常重试')
}

async function testIncompleteOlderRevisionDoesNotOverrideNewerValue() {
  const firstResponse = deferred<{
    totalUpdated: number
    totalRequested: number
    validationErrors: never[]
  }>()
  const batches: Array<Record<string, unknown>> = []
  let attempt = 0
  const queue = createContainerDetailAutoSaveQueue({
    sendBatch: async (_contextKey, intents) => {
      attempt += 1
      batches.push({ ...intents[0]?.patch })
      if (attempt === 1) return firstResponse.promise
      return { totalUpdated: 1, totalRequested: 1, validationErrors: [] }
    },
  })

  queue.enqueue('stale-incomplete-context', 'row', { 备注: '旧值' })
  await flushMicrotasks()
  queue.enqueue('stale-incomplete-context', 'row', { 备注: '新值' })
  const drainPromise = queue.drain('stale-incomplete-context')
  firstResponse.resolve({ totalUpdated: 0, totalRequested: 1, validationErrors: [] })
  await drainPromise

  assertDeepEqual(batches, [{ 备注: '旧值' }, { 备注: '新值' }], '旧 revision count mismatch 后应继续发送 newer pending')
  assertEqual(
    queue.getSnapshot('stale-incomplete-context').failureCount,
    0,
    '旧 revision 的 count mismatch 不得覆盖已成功的新 revision',
  )
  assertDeepEqual(getUnsettledPatches(queue, 'stale-incomplete-context'), {}, '新 revision 成功后不应残留旧失败值')
}

async function testDetachedFailureSurvivesAttachAndKeepsContextIsolation() {
  const contextARequests: Array<ReturnType<typeof deferred<'success' | 'fail'>>> = []
  const snapshots: string[] = []
  const disposedContexts: string[] = []
  const queue = createContainerDetailAutoSaveQueue({
    sendBatch: async (contextKey, intents) => {
      if (contextKey === 'A') {
        const request = deferred<'success' | 'fail'>()
        contextARequests.push(request)
        if (await request.promise === 'fail') {
          throw new Error(`A:${String(intents[0]?.patch.备注)}`)
        }
      }
      return { validationErrors: [] }
    },
    onSnapshotChange: (contextKey, snapshot) => {
      snapshots.push(`${contextKey}:${snapshot.unsavedFieldCount}`)
    },
    onContextDisposed: (contextKey) => disposedContexts.push(contextKey),
  })

  queue.enqueue('A', 'row-a', { 备注: 'A failed value' })
  await flushMicrotasks()
  queue.discardContext('A')
  const snapshotCountAfterDiscard = snapshots.length
  queue.enqueue('B', 'row-b', { 备注: 'B saved value' })
  await queue.drain('B')
  contextARequests[0].resolve('fail')
  await flushMicrotasks()

  assertDeepEqual(
    getUnsettledPatches(queue, 'A'),
    { 'row-a': { 备注: 'A failed value' } },
    'detach 后 transport 失败仍应保留 A 的最新输入值',
  )
  assertEqual(disposedContexts.includes('A'), false, 'detach 后存在失败的上下文不得被清理')
  assertEqual(queue.getSnapshot('B').failureCount, 0, 'A 的失败不得污染 B 上下文')
  assert(
    snapshots.slice(snapshotCountAfterDiscard).every((entry) => !entry.startsWith('A:')),
    '上下文 detach 后旧请求完成不得触发旧页面状态回调',
  )

  const restored = queue.attachContext('A')
  assertEqual(restored.failureCount, 1, '返回 A 时应恢复失败状态和重试入口')
  assertEqual(restored.unsavedFieldCount, 1, '返回 A 时应恢复失败输入的未保存计数')
  queue.retryFailed('A')
  await flushMicrotasks()
  contextARequests[1].resolve('success')
  await queue.drain('A')
  assertEqual(queue.getSnapshot('A').failureCount, 0, '返回 A 重试成功后应清除失败')
  assertDeepEqual(getUnsettledPatches(queue, 'A'), {}, '重试成功后不应再覆盖服务器新响应')

  queue.discardContext('A')
  assertEqual(disposedContexts.includes('A'), true, '只有 clean drain 后再次 detach 才可清理上下文')
}

async function testLoadOverlayUsesLatestRunningPendingAndFailureValues() {
  const requests: Array<ReturnType<typeof deferred<'success' | 'fail'>>> = []
  const queue = createContainerDetailAutoSaveQueue({
    sendBatch: async () => {
      const request = deferred<'success' | 'fail'>()
      requests.push(request)
      if (await request.promise === 'fail') throw new Error('save failed')
      return { validationErrors: [] }
    },
  })
  const serverRows = [{ hguid: 'row', 单件装箱数: 1, 单件体积: 0.01, 备注: 'server' }]

  queue.enqueue('A', 'row', { 单件装箱数: 10 })
  await flushMicrotasks()
  let overlaid = applyAutoSavePatches(serverRows, getUnsettledPatches(queue, 'A'))
  assertEqual(overlaid[0].单件装箱数, 10, 'reset 响应不得覆盖 running 的最新值')

  queue.enqueue('A', 'row', { 单件装箱数: 30, 单件体积: 0.2 })
  overlaid = applyAutoSavePatches(serverRows, getUnsettledPatches(queue, 'A'))
  assertEqual(overlaid[0].单件装箱数, 30, 'pending 同字段更高 revision 应覆盖 running 旧值')
  assertEqual(overlaid[0].单件体积, 0.2, 'reset 响应不得覆盖 pending 的最新值')

  requests[0].resolve('success')
  await flushMicrotasks()
  requests[1].resolve('fail')
  await flushMicrotasks()
  overlaid = applyAutoSavePatches(serverRows, getUnsettledPatches(queue, 'A'))
  assertEqual(overlaid[0].单件装箱数, 30, 'reset 响应不得覆盖 failure 的最新数值')
  assertEqual(overlaid[0].单件体积, 0.2, '同一失败批次的另一最新字段也必须保留')
  assertEqual(overlaid[0].备注, 'server', '没有未结算 patch 的字段应继续采用服务器响应')
}

async function testFailureSurvivesAcrossQueueInstancesWithoutLeakingOldCallbacks() {
  const firstRequest = deferred<'success' | 'fail'>()
  let firstTransportCount = 0
  let firstSnapshotCount = 0
  let firstDisposeCount = 0
  let secondTransportCount = 0
  let secondSnapshotCount = 0
  const disposedContexts: string[] = []
  const firstQueue = createContainerDetailAutoSaveQueue({
    sendBatch: async () => {
      firstTransportCount += 1
      if (await firstRequest.promise === 'fail') throw new Error('first instance failed')
      return { validationErrors: [] }
    },
    onSnapshotChange: () => {
      firstSnapshotCount += 1
    },
    onContextDisposed: () => {
      firstDisposeCount += 1
    },
  })

  firstQueue.enqueue('remount-context', 'row', { 备注: '卸载前失败值' })
  await flushMicrotasks()
  firstQueue.discardContext('remount-context')
  const firstSnapshotCountAfterDiscard = firstSnapshotCount
  firstRequest.resolve('fail')
  await flushMicrotasks()

  const secondQueue = createContainerDetailAutoSaveQueue({
    sendBatch: async (_contextKey, intents) => {
      secondTransportCount += 1
      assertEqual(intents[0]?.patch.备注, '卸载前失败值', '新实例应使用旧实例保留的失败值重试')
      return { validationErrors: [] }
    },
    onSnapshotChange: () => {
      secondSnapshotCount += 1
    },
    onContextDisposed: (contextKey) => disposedContexts.push(contextKey),
  })

  const restored = secondQueue.attachContext('remount-context')
  assertEqual(restored.failureCount, 1, '真正 unmount/remount 后新 queue 实例应恢复失败状态')
  assertEqual(firstDisposeCount, 0, '旧实例 detach 后的失败完成不得调用旧 dispose callback')
  assertDeepEqual(
    getUnsettledPatches(secondQueue, 'remount-context'),
    { row: { 备注: '卸载前失败值' } },
    '真正 unmount/remount 后新实例应恢复失败字段值',
  )
  firstQueue.discardContext('remount-context')
  assertEqual(
    secondQueue.getSnapshot('remount-context').failureCount,
    1,
    '旧实例晚到的 discard 不得 detach 已由新实例接管的 context',
  )
  assertEqual(firstDisposeCount, 0, '新实例接管后旧实例晚到 discard 仍不得调用旧 dispose callback')
  secondQueue.retryFailed('remount-context')
  await secondQueue.drain('remount-context')

  assertEqual(firstTransportCount, 1, '新实例重试不得复用旧实例 transport')
  assertEqual(secondTransportCount, 1, '新实例应接管 transport 并完成一次重试')
  assertEqual(
    firstSnapshotCount,
    firstSnapshotCountAfterDiscard,
    '旧实例 discard 后不得再收到 snapshot 回调',
  )
  assert(secondSnapshotCount > 0, '新实例 attach 后应接收自己的 snapshot 回调')
  secondQueue.discardContext('remount-context')
  assertEqual(
    disposedContexts.includes('remount-context'),
    true,
    '重试成功并 clean discard 后应释放共享 context 的 transport 和快照',
  )
  assertEqual(firstDisposeCount, 0, 'clean dispose 只能调用当前 owner callback，不能回调旧组件')
}

async function testLateEnqueueCannotReclaimSharedContextOwnership() {
  const contextKey = 'owner-handoff-context'
  let firstTransportCount = 0
  let firstSnapshotCount = 0
  const firstQueue = createContainerDetailAutoSaveQueue({
    sendBatch: async () => {
      firstTransportCount += 1
      throw new Error('first owner failed')
    },
    onSnapshotChange: () => {
      firstSnapshotCount += 1
    },
  })

  firstQueue.attachContext(contextKey)
  firstQueue.enqueue(contextKey, 'row', { 备注: '应由新 owner 重试' })
  await flushMicrotasks()
  assertEqual(firstQueue.getSnapshot(contextKey).failureCount, 1, '旧 owner 应先留下一个可重试失败')

  let secondTransportCount = 0
  const secondBatches: Array<Record<string, unknown>> = []
  const secondQueue = createContainerDetailAutoSaveQueue({
    sendBatch: async (_contextKey, intents) => {
      secondTransportCount += 1
      secondBatches.push({ ...intents[0]?.patch })
      return { validationErrors: [] }
    },
  })
  const restored = secondQueue.attachContext(contextKey)
  assertEqual(restored.failureCount, 1, '新 owner attach 后应接管旧失败')
  const firstSnapshotCountAfterHandoff = firstSnapshotCount

  firstQueue.enqueue(contextKey, 'row', { 中包数: 99 })
  await flushMicrotasks()

  assertEqual(
    secondQueue.getSnapshot(contextKey).failureCount,
    1,
    '旧 owner 的迟到 enqueue 不得抢回共享 context owner',
  )
  assertDeepEqual(
    getUnsettledPatches(secondQueue, contextKey),
    { row: { 备注: '应由新 owner 重试' } },
    '旧 owner 的迟到 enqueue 不得修改新 owner 接管的未结算值',
  )
  assertEqual(firstSnapshotCount, firstSnapshotCountAfterHandoff, '旧 owner 不得重新绑定自己的 snapshot callback')

  secondQueue.retryFailed(contextKey)
  await secondQueue.drain(contextKey)
  assertEqual(firstTransportCount, 1, '新 owner 重试不得重新调用旧 transport')
  assertEqual(secondTransportCount, 1, '新 owner retry 应使用新 transport')
  assertDeepEqual(secondBatches[0], { 备注: '应由新 owner 重试' }, '重试请求不得混入旧 owner 的迟到 patch')

  secondQueue.enqueue(contextKey, 'row', { 中包数: 6 })
  await secondQueue.drain(contextKey)
  assertEqual(secondTransportCount, 2, '当前 owner 的正常 enqueue 应继续工作')
  assertDeepEqual(secondBatches[1], { 中包数: 6 }, '当前 owner 应发送自己的最新 patch')
  secondQueue.discardContext(contextKey)
}

async function testOnlyActiveKeepAliveInstanceOwnsAndEnqueuesAutoSaveContext() {
  const contextKey = 'keep-alive-shared-context'
  assertEqual(resolveKeepAliveLifecycleAction(true, contextKey), 'attach', 'active 实例应 attach 自动保存 context')
  assertEqual(resolveKeepAliveLifecycleAction(false, contextKey), 'discard', 'inactive 实例应 discard 且不得接管 context')
  assertEqual(resolveKeepAliveLifecycleAction(true, ''), 'none', '缺少 context key 时不得触发队列生命周期操作')
  const visibleBatches: Array<Record<string, unknown>> = []
  const hiddenBatches: Array<Record<string, unknown>> = []
  const visibleQueue = createContainerDetailAutoSaveQueue({
    sendBatch: async (_contextKey, intents) => {
      visibleBatches.push({ ...intents[0]?.patch })
      return { totalUpdated: 1, totalRequested: 1, validationErrors: [] }
    },
  })
  const hiddenQueue = createContainerDetailAutoSaveQueue({
    sendBatch: async (_contextKey, intents) => {
      hiddenBatches.push({ ...intents[0]?.patch })
      return { totalUpdated: 1, totalRequested: 1, validationErrors: [] }
    },
  })
  const syncLifecycle = (
    queue: ReturnType<typeof createContainerDetailAutoSaveQueue>,
    active: boolean,
  ) => {
    const action = resolveKeepAliveLifecycleAction(active, contextKey)
    if (action === 'attach') queue.attachContext(contextKey)
    if (action === 'discard') queue.discardContext(contextKey)
  }
  const blurAndEnqueue = (
    queue: ReturnType<typeof createContainerDetailAutoSaveQueue>,
    active: boolean,
    remark: string,
  ) => {
    if (resolveKeepAliveLifecycleAction(active, contextKey) !== 'attach') return
    queue.enqueue(contextKey, 'row', { 备注: remark })
  }

  syncLifecycle(visibleQueue, true)
  syncLifecycle(hiddenQueue, false)
  blurAndEnqueue(hiddenQueue, false, '隐藏实例迟到 blur')
  blurAndEnqueue(visibleQueue, true, '可见实例保存值')
  await visibleQueue.drain(contextKey)

  assertDeepEqual(visibleBatches, [{ 备注: '可见实例保存值' }], 'inactive 实例不得 attach 抢走 visible blur 的 queue owner')
  assertDeepEqual(hiddenBatches, [], 'inactive 实例的迟到 blur 不得发送 transport')

  syncLifecycle(visibleQueue, false)
  syncLifecycle(hiddenQueue, true)
  blurAndEnqueue(visibleQueue, false, '旧实例迟到值')
  blurAndEnqueue(hiddenQueue, true, '新可见实例值')
  await hiddenQueue.drain(contextKey)

  assertDeepEqual(visibleBatches, [{ 备注: '可见实例保存值' }], '失活后的旧实例不得重新创建或抢回 context')
  assertDeepEqual(hiddenBatches, [{ 备注: '新可见实例值' }], '新可见实例 blur 必须进入其 queue transport')
  hiddenQueue.discardContext(contextKey)
}

async function testDiscardedOwnerCannotRebindThroughLateEnqueue() {
  const contextKey = 'discarded-owner-context'
  const firstRequest = deferred<void>()
  let transportCount = 0
  const queue = createContainerDetailAutoSaveQueue({
    sendBatch: async () => {
      transportCount += 1
      if (transportCount === 1) await firstRequest.promise
      return { validationErrors: [] }
    },
  })

  queue.enqueue(contextKey, 'row', { 备注: '卸载前值' })
  await flushMicrotasks()
  queue.discardContext(contextKey)
  queue.enqueue(contextKey, 'row', { 中包数: 12 })

  assertDeepEqual(
    getUnsettledPatches(queue, contextKey),
    { row: { 备注: '卸载前值' } },
    '已经 discard 的 owner 不得通过迟到 enqueue 重新激活 context',
  )
  firstRequest.resolve()
  await flushMicrotasks()
  assertEqual(transportCount, 1, 'discard 后迟到 patch 不得启动额外 transport')
}

async function testSuccessfulSaveInvalidatesStaleItemQueryBeforeClearingRunningOverlay() {
  const staleQuery = deferred<string>()
  const queryController = new AbortController()
  let currentRequestId = 7
  const staleQueryRequestId = currentRequestId
  let visibleRemark = '自动保存新值'
  let loading = true
  let errorToastCount = 0
  const settledContexts: string[] = []
  const activeContextKey = 'race-context-A'
  const staleQueryTask = staleQuery.promise
    .then((remark) => {
      if (!queryController.signal.aborted && staleQueryRequestId === currentRequestId) {
        visibleRemark = remark
      }
    })
    .catch(() => {
      if (!queryController.signal.aborted) errorToastCount += 1
    })
    .finally(() => {
      loading = false
    })

  const queue = createContainerDetailAutoSaveQueue({
    sendBatch: async () => ({ validationErrors: [] }),
    onBatchSuccess: (contextKey: string) => {
      settledContexts.push(contextKey)
      if (contextKey !== activeContextKey) return
      queryController.abort()
      currentRequestId += 1
    },
  } as Parameters<typeof createContainerDetailAutoSaveQueue>[0] & {
    onBatchSuccess: (contextKey: string) => void
  })

  // 旧 query 已读到服务器旧值；保存成功必须先让其失效，再清除 running overlay。
  queue.enqueue(activeContextKey, 'row', { 备注: '自动保存新值' })
  await queue.drain(activeContextKey)
  assertDeepEqual(getUnsettledPatches(queue, activeContextKey), {}, '保存成功后 queue 可正常清空未结算 patch')
  staleQuery.resolve('服务器旧值')
  await staleQueryTask

  assertDeepEqual(settledContexts, [activeContextKey], '成功屏障应携带准确 context key')
  assertEqual(visibleRemark, '自动保存新值', '旧 query 回包不得覆盖已成功自动保存的新值')
  assertEqual(errorToastCount, 0, '主动失效旧 query 不应弹加载错误')
  assertEqual(loading, false, '主动失效旧 query 后 loading 必须正常结束')

  const contextBController = new AbortController()
  const queueForDetachedA = createContainerDetailAutoSaveQueue({
    sendBatch: async () => ({ validationErrors: [] }),
    onBatchSuccess: (contextKey: string) => {
      if (contextKey === 'race-context-B') contextBController.abort()
    },
  } as Parameters<typeof createContainerDetailAutoSaveQueue>[0] & {
    onBatchSuccess: (contextKey: string) => void
  })
  queueForDetachedA.enqueue('race-context-detached-A', 'row-a', { 备注: 'A' })
  await queueForDetachedA.drain('race-context-detached-A')
  assertEqual(contextBController.signal.aborted, false, 'A 保存成功不得失效 B 的 item query')
}

async function testDiscardedContextDrainsPendingBatchWithOriginalPersistenceMetadata() {
  const firstRequest = deferred<void>()
  const contexts = new Map([
    ['context-A', {
      userGuid: 'user-A',
      draftIdentity: 'draft-A',
      fieldVersions: { 'row-a:备注': 'version-A' },
      fieldBaselineTokens: { 'row-a:备注': 'token-A' },
    }],
    ['context-B', {
      userGuid: 'user-B',
      draftIdentity: 'draft-B',
      fieldVersions: { 'row-b:备注': 'version-B' },
      fieldBaselineTokens: { 'row-b:备注': 'token-B' },
    }],
  ])
  let activeUserGuid = 'user-A'
  const batches: {
    contextKey: string
    userGuid: string
    fieldVersion?: string
    baselineToken?: string
    activeUserGuid: string
  }[] = []
  const queue = createContainerDetailAutoSaveQueue({
    sendBatch: async (contextKey) => {
      const context = contexts.get(contextKey)
      assert(context, `缺少 ${contextKey} 的持久化上下文`)
      batches.push({
        contextKey,
        userGuid: context.userGuid,
        fieldVersion: context.fieldVersions['row-a:备注'],
        baselineToken: context.fieldBaselineTokens['row-a:备注'],
        activeUserGuid,
      })
      if (batches.length === 1) await firstRequest.promise
      return { validationErrors: [] }
    },
  })

  queue.enqueue('context-A', 'row-a', { 备注: 'A 请求 1' })
  await flushMicrotasks()
  queue.enqueue('context-A', 'row-a', { 备注: 'A 请求 2' })
  queue.discardContext('context-A')
  activeUserGuid = 'user-B'
  firstRequest.resolve()
  for (let index = 0; index < 10 && batches.length < 2; index += 1) {
    await flushMicrotasks()
  }

  assertEqual(batches.length, 2, 'discard 只断开 UI，A 的 pending 批次仍应完成')
  assertDeepEqual(
    batches.map(({ contextKey, userGuid, fieldVersion, baselineToken }) => ({
      contextKey,
      userGuid,
      fieldVersion,
      baselineToken,
    })),
    [
      { contextKey: 'context-A', userGuid: 'user-A', fieldVersion: 'version-A', baselineToken: 'token-A' },
      { contextKey: 'context-A', userGuid: 'user-A', fieldVersion: 'version-A', baselineToken: 'token-A' },
    ],
    'A 的后续批次必须继续使用 A 固定的用户、版本和服务器基线',
  )
  assertEqual(batches[1].activeUserGuid, 'user-B', '回归场景必须确实已切换到 B 用户上下文')
}

function testContextSnapshotOwnership() {
  const contextA = buildContainerDetailAutoSaveContextKey('container-a', 'draft-a')
  assertEqual(
    isContainerDetailAutoSaveContextCurrent(contextA, 'container-a', 'draft-a'),
    true,
    '同一货柜及草稿身份可以刷新其自动保存快照',
  )
  assertEqual(
    isContainerDetailAutoSaveContextCurrent(contextA, 'container-b', 'draft-a'),
    false,
    '路由切换首帧不得用新货柜页面状态覆盖旧货柜自动保存快照',
  )
  assertEqual(
    isContainerDetailAutoSaveContextCurrent(contextA, 'container-a', 'draft-b'),
    false,
    '用户草稿身份变化时不得复用旧自动保存快照',
  )
}

// 回归：保存完成、队列空闲时切走标签会释放 context 并删掉页面快照；切回来再编辑必须仍能发送。
async function testIdleTabSwitchRebuildsSnapshotBeforeNextEdit() {
  const contextKey = buildContainerDetailAutoSaveContextKey('container-tab', 'draft-tab')
  const snapshots = new Map<string, { rows: string[] }>([[contextKey, { rows: ['初始行'] }]])
  const sentRows: string[][] = []
  const queue = createContainerDetailAutoSaveQueue({
    sendBatch: async (key) => {
      const snapshot = snapshots.get(key)
      if (!snapshot) throw new Error('货柜明细保存上下文已失效')
      sentRows.push(snapshot.rows)
    },
    onContextDisposed: (key) => {
      snapshots.delete(key)
    },
  })
  const activate = () => {
    ensureContainerDetailAutoSaveContextSnapshot(snapshots, contextKey, () => ({ rows: ['重建行'] }))
    queue.attachContext(contextKey)
  }

  activate()
  queue.enqueue(contextKey, 'row-1', { 调整浮率: 1.5 })
  await queue.drain(contextKey)
  assertEqual(sentRows.length, 1, '切走标签前的编辑应正常保存')

  // active=false：队列空闲，context 立即释放，页面回调删除快照。
  queue.discardContext(contextKey)
  assertEqual(snapshots.has(contextKey), false, '空闲 discard 后页面快照会被释放（这是该回归的前提）')

  // active=true：重新 attach 前补齐快照，再编辑不能报上下文失效。
  activate()
  queue.enqueue(contextKey, 'row-2', { 调整浮率: 2 })
  await queue.drain(contextKey)
  assertEqual(sentRows.length, 2, '切回标签后的编辑必须能发出保存请求')
  assertDeepEqual(sentRows[1], ['重建行'], '切回标签后应使用重建的快照')
  assertEqual(queue.getSnapshot(contextKey).failureCount, 0, '切回标签后的编辑不应产生保存失败')

  // 快照仍存在时不得覆盖：避免冲掉正在使用的行与草稿元数据。
  assertEqual(
    ensureContainerDetailAutoSaveContextSnapshot(snapshots, contextKey, () => ({ rows: ['不应覆盖'] })),
    false,
    '已有快照时 ensure 不应重建',
  )
  assertDeepEqual(snapshots.get(contextKey)?.rows, ['重建行'], '已有快照内容必须保持')
  queue.discardContext(contextKey)
}

async function run() {
  await testSingleFlightAndLatestWinsMerge()
  await testDrainIncludesWorkEnqueuedWhileWaiting()
  await testFailureRevisionAndRetry()
  await testCurrentFailurePausesDependentPendingUntilAtomicRetry()
  await testIncompleteBatchCountMismatchBecomesRetryableFailure()
  await testExplicitValidationErrorDoesNotGeneralizeCountMismatch()
  await testIncompleteOlderRevisionDoesNotOverrideNewerValue()
  await testDetachedFailureSurvivesAttachAndKeepsContextIsolation()
  await testLoadOverlayUsesLatestRunningPendingAndFailureValues()
  await testFailureSurvivesAcrossQueueInstancesWithoutLeakingOldCallbacks()
  await testLateEnqueueCannotReclaimSharedContextOwnership()
  await testOnlyActiveKeepAliveInstanceOwnsAndEnqueuesAutoSaveContext()
  await testDiscardedOwnerCannotRebindThroughLateEnqueue()
  await testSuccessfulSaveInvalidatesStaleItemQueryBeforeClearingRunningOverlay()
  await testDiscardedContextDrainsPendingBatchWithOriginalPersistenceMetadata()
  testContextSnapshotOwnership()
  await testIdleTabSwitchRebuildsSnapshotBeforeNextEdit()
  console.log('containerDetailAutoSaveQueue tests passed')
}

void run().catch((error) => {
  console.error(error)
  process.exitCode = 1
})
