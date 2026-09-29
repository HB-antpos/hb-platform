import { readFileSync } from 'node:fs'
import path from 'node:path'
import {
  StoreOrderSyncPollingCancelledError,
  StoreOrderSyncPollingTimeoutError,
  createStoreOrderSyncJobPoller,
} from './syncJobPolling'
import type { StoreOrderSyncJobResult } from '../../../types/storeOrder'

function assert(condition: unknown, message: string): asserts condition {
  if (!condition) {
    throw new Error(message)
  }
}

function assertEqual<T>(actual: T, expected: T, message: string) {
  if (actual !== expected) {
    throw new Error(`${message}。Expected: ${String(expected)}, received: ${String(actual)}`)
  }
}

function assertDeepEqual(actual: unknown, expected: unknown, message: string) {
  const actualJson = JSON.stringify(actual)
  const expectedJson = JSON.stringify(expected)

  if (actualJson !== expectedJson) {
    throw new Error(`${message}。Expected: ${expectedJson}, received: ${actualJson}`)
  }
}

async function assertRejects(
  execute: () => Promise<unknown>,
  expectedError: new (...args: any[]) => Error,
  message: string,
) {
  try {
    await execute()
  } catch (error) {
    assert(error instanceof expectedError, message)
    return
  }

  throw new Error(`${message}。Expected promise to reject`)
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

type TimeoutTask = {
  id: number
  execute: () => void
  delay: number
  dueAt: number
}

function createFakeTimer() {
  let sequence = 0
  let now = 0
  const tasks = new Map<number, TimeoutTask>()

  return {
    setTimeout: (callback: () => void, delay: number) => {
      const id = sequence + 1
      sequence = id
      tasks.set(id, { id, execute: callback, delay, dueAt: now + delay })
      return id
    },
    clearTimeout: (id: unknown) => {
      if (typeof id === 'number') {
        tasks.delete(id)
      }
    },
    flushNext: () => {
      const next = Array.from(tasks.values()).sort((left, right) => {
        if (left.dueAt !== right.dueAt) {
          return left.dueAt - right.dueAt
        }
        return left.id - right.id
      })[0]
      if (!next) {
        throw new Error('没有可执行的定时任务')
      }
      tasks.delete(next.id)
      now = next.dueAt
      next.execute()
      return next.delay
    },
    pendingCount: () => tasks.size,
  }
}

const pageFile = path.resolve(process.cwd(), 'src/pages/Warehouse/StoreOrders/index.tsx')
const pageSource = readFileSync(pageFile, 'utf8')

async function main() {
  const failures: string[] = []

  // 分店订货 HQ → HBweb 的全量/增量同步已于 2026-09-29 停用（后端返回 410），列表页不得再接线同步流程；
  // 本文件其余用例继续验证 syncJobPolling 轮询器本身。
  const pageSourceFailure = await runTest('列表页不再接线 HQ 全量/增量同步流程', () => {
    for (const removed of [
      'createStoreOrderFullHqSyncJob',
      'createStoreOrderIncrementalHqSyncJob',
      'getStoreOrderHqSyncJob',
      'createStoreOrderSyncJobPoller',
      'stopSyncPollingRef',
      'runStoreOrderHqSync',
      'handleFullHqSync',
      'handleOpenIncrementalHqSync',
      'incrementalConflictStrategy',
      "t('storeOrders.syncFullOrders')",
      "t('storeOrders.syncIncrementalOrders')",
    ]) {
      assert(!pageSource.includes(removed), `列表页不应再包含 HQ 同步代码：${removed}`)
    }
  })
  if (pageSourceFailure) failures.push(pageSourceFailure)

  const successPollingFailure = await runTest('轮询器应每次等待定时器后继续请求直到成功', async () => {
    const timer = createFakeTimer()
    const statuses: StoreOrderSyncJobResult[] = [
      { jobId: 'job-1', status: 'Queued', message: '排队中' },
      { jobId: 'job-1', status: 'Running', message: '同步中' },
      {
        jobId: 'job-1',
        status: 'Succeeded',
        message: '同步完成',
        conflictStrategy: 'LatestWins',
        ordersSynced: 3,
        detailsSynced: 5,
        ordersUpdated: 1,
        detailsUpdated: 2,
        skippedOrdersBecauseLocalNewer: 4,
        skippedDetailsBecauseLocalNewer: 6,
      },
    ]
    const requestedJobIds: string[] = []

    const poller = createStoreOrderSyncJobPoller({
      jobId: 'job-1',
      pollIntervalMs: 2000,
      timeoutMs: 30000,
      getJob: async (jobId) => {
        requestedJobIds.push(jobId)
        return statuses.shift()!
      },
      setTimeoutFn: timer.setTimeout,
      clearTimeoutFn: timer.clearTimeout,
    })

    const firstDelay = timer.flushNext()
    await Promise.resolve()
    const secondDelay = timer.flushNext()
    await Promise.resolve()
    const thirdDelay = timer.flushNext()
    await Promise.resolve()
    const result = await poller.promise

    assertEqual(firstDelay, 2000, '首次轮询应等待 2 秒')
    assertEqual(secondDelay, 2000, '后续轮询也应等待 2 秒')
    assertEqual(thirdDelay, 2000, '成功前的最后一次轮询也应等待 2 秒')
    assertDeepEqual(requestedJobIds, ['job-1', 'job-1', 'job-1'], '轮询应持续查询同一个 job')
    assertDeepEqual(
      result,
      {
        jobId: 'job-1',
        status: 'Succeeded',
        message: '同步完成',
        conflictStrategy: 'LatestWins',
        ordersSynced: 3,
        detailsSynced: 5,
        ordersUpdated: 1,
        detailsUpdated: 2,
        skippedOrdersBecauseLocalNewer: 4,
        skippedDetailsBecauseLocalNewer: 6,
      },
      '轮询成功后应返回最终摘要',
    )
  })
  if (successPollingFailure) failures.push(successPollingFailure)

  const failedPollingFailure = await runTest('轮询器应把 Failed 作为最终状态返回而不是本地报错', async () => {
    const timer = createFakeTimer()
    const poller = createStoreOrderSyncJobPoller({
      jobId: 'job-2',
      pollIntervalMs: 2000,
      timeoutMs: 30000,
      getJob: async () => ({
        jobId: 'job-2',
        status: 'Failed',
        message: '后端同步失败：测试错误',
      }),
      setTimeoutFn: timer.setTimeout,
      clearTimeoutFn: timer.clearTimeout,
    })

    timer.flushNext()
    const result = await poller.promise

    assertDeepEqual(
      result,
      {
        jobId: 'job-2',
        status: 'Failed',
        message: '后端同步失败：测试错误',
      },
      'Failed 应作为后端最终结果透传',
    )
  })
  if (failedPollingFailure) failures.push(failedPollingFailure)

  const timeoutFailure = await runTest('轮询器超时应抛出本地 timeout，而不是伪装成后端失败', async () => {
    const timer = createFakeTimer()
    const poller = createStoreOrderSyncJobPoller({
      jobId: 'job-3',
      pollIntervalMs: 2000,
      timeoutMs: 2000,
      getJob: async () => ({
        jobId: 'job-3',
        status: 'Running',
        message: '同步中',
      }),
      setTimeoutFn: timer.setTimeout,
      clearTimeoutFn: timer.clearTimeout,
    })

    timer.flushNext()
    await Promise.resolve()
    timer.flushNext()

    await assertRejects(
      () => poller.promise,
      StoreOrderSyncPollingTimeoutError,
      '轮询超时应抛出本地超时错误',
    )
  })
  if (timeoutFailure) failures.push(timeoutFailure)

  const cancelFailure = await runTest('停止轮询应清理挂起定时器并抛出取消错误', async () => {
    const timer = createFakeTimer()
    const poller = createStoreOrderSyncJobPoller({
      jobId: 'job-4',
      pollIntervalMs: 2000,
      timeoutMs: 30000,
      getJob: async () => ({
        jobId: 'job-4',
        status: 'Running',
        message: '同步中',
      }),
      setTimeoutFn: timer.setTimeout,
      clearTimeoutFn: timer.clearTimeout,
    })

    poller.stop()

    assertEqual(timer.pendingCount(), 0, '停止轮询后不应残留定时器')
    await assertRejects(
      () => poller.promise,
      StoreOrderSyncPollingCancelledError,
      '停止轮询后应抛出取消错误',
    )
  })
  if (cancelFailure) failures.push(cancelFailure)

  if (failures.length > 0) {
    throw new Error(`共有 ${failures.length} 个测试失败\n- ${failures.join('\n- ')}`)
  }

  console.log('syncJobPolling.test: ok')
}

await main()
