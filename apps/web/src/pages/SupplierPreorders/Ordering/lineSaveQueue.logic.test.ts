import assert from 'node:assert/strict'

import { createLineSaveQueue, makeLineKey, parseLineKey, type LineSaveQueueTimers } from './lineSaveQueue.logic'

// ---------- 测试工具：手动推进的计时器 + 可控的请求 ----------

function createFakeTimers() {
  let nextId = 1
  const pending = new Map<number, () => void>()
  const timers: LineSaveQueueTimers = {
    set: (callback) => {
      const id = nextId++
      pending.set(id, callback)
      return id
    },
    clear: (handle) => {
      pending.delete(handle as number)
    },
  }
  return {
    timers,
    /** 触发所有已到期（这里不区分时间，全部触发）的计时器 */
    runAll() {
      const callbacks = [...pending.values()]
      pending.clear()
      callbacks.forEach((callback) => callback())
    },
    count: () => pending.size,
  }
}

interface Deferred {
  key: string
  value: number
  resolve: (cartons: number) => void
  reject: (error: Error) => void
}

const flushMicrotasks = async () => {
  for (let index = 0; index < 5; index += 1) await Promise.resolve()
}

function setup() {
  const clock = createFakeTimers()
  const requests: Deferred[] = []
  const successes: { key: string; value: number; result: number; isLatest: boolean; seq: number }[] = []
  const errors: { key: string; value: number; message: string }[] = []
  const busyEvents: [string, boolean][] = []
  const queue = createLineSaveQueue<number>({
    delayMs: 400,
    timers: clock.timers,
    save: (key, value) =>
      new Promise<number>((resolve, reject) => {
        requests.push({ key, value, resolve, reject })
      }),
    onSuccess: (key, value, result, outcome) => successes.push({ key, value, result, ...outcome }),
    onError: (key, value, error) => errors.push({ key, value, message: (error as Error).message }),
    onBusyChange: (key, busy) => busyEvents.push([key, busy]),
  })
  return { clock, requests, successes, errors, busyEvents, queue }
}

async function run() {
  // 1. 防抖合并：同一行连续 3 次操作只发最后一次
  {
    const { clock, requests, successes, queue, busyEvents } = setup()
    queue.schedule('A', 1)
    queue.schedule('A', 2)
    queue.schedule('A', 3)
    assert.equal(clock.count(), 1, '同一行只保留一个防抖计时器')
    assert.equal(requests.length, 0, '防抖期内不发请求')
    assert.equal(queue.isBusy('A'), true, '防抖期内即视为忙碌')
    clock.runAll()
    assert.deepEqual(requests.map((r) => r.value), [3])
    requests[0].resolve(3)
    await flushMicrotasks()
    assert.deepEqual(successes, [{ key: 'A', value: 3, result: 3, isLatest: true, seq: 1 }])
    assert.equal(queue.isBusy('A'), false)
    assert.equal(queue.hasBusy(), false)
    assert.deepEqual(busyEvents, [['A', true], ['A', false]], '忙碌状态只在变化时通知')
  }

  // 2. 每行串行：在途期间的新值排队，旧结果不是最新，新结果才是最新
  {
    const { clock, requests, successes, queue } = setup()
    queue.schedule('A', 1)
    clock.runAll()
    queue.schedule('A', 5)
    clock.runAll()
    assert.equal(requests.length, 1, '同一行在途时不并发发第二个请求')
    requests[0].resolve(1)
    await flushMicrotasks()
    assert.equal(successes[0].isLatest, false, '先发请求的结果不能当成最新')
    assert.equal(requests.length, 2, '在途结束后立刻发排队的值')
    assert.equal(requests[1].value, 5)
    requests[1].resolve(5)
    await flushMicrotasks()
    assert.deepEqual(successes.map((s) => [s.value, s.isLatest, s.seq]), [[1, false, 1], [5, true, 2]])
  }

  // 3. 在途期间又有防抖中的值：在途结果也不是最新
  {
    const { clock, requests, successes, queue } = setup()
    queue.schedule('A', 2)
    clock.runAll()
    queue.schedule('A', 3)
    requests[0].resolve(2)
    await flushMicrotasks()
    assert.equal(successes[0].isLatest, false, '还有防抖中的值时不采纳在途结果')
    clock.runAll()
    assert.equal(requests[1].value, 3)
  }

  // 4. 失败：只有最新一次失败才回调；非最新失败忽略
  {
    const { clock, requests, errors, successes, queue } = setup()
    queue.schedule('A', 4)
    clock.runAll()
    queue.schedule('A', 6)
    clock.runAll()
    requests[0].reject(new Error('旧请求失败'))
    await flushMicrotasks()
    assert.deepEqual(errors, [], '非最新失败不提示')
    requests[1].reject(new Error('已过截止时间'))
    await flushMicrotasks()
    assert.deepEqual(errors, [{ key: 'A', value: 6, message: '已过截止时间' }])
    assert.equal(successes.length, 0)
    assert.equal(queue.isBusy('A'), false, '失败后行恢复空闲')
  }

  // 5. 排队值与刚保存成功的值相同：不重复发送
  {
    const { clock, requests, successes, queue } = setup()
    queue.schedule('A', 2)
    clock.runAll()
    queue.schedule('A', 2)
    clock.runAll()
    requests[0].resolve(2)
    await flushMicrotasks()
    assert.equal(requests.length, 1, '相同值不再发')
    assert.equal(successes[0].isLatest, true)
  }

  // 6. 不同行互不影响，可以并发
  {
    const { clock, requests, queue } = setup()
    queue.schedule('A', 1)
    queue.schedule('B', 2)
    clock.runAll()
    assert.deepEqual(requests.map((r) => [r.key, r.value]), [['A', 1], ['B', 2]])
  }

  // 7. flush / flushAll：立即发送防抖中的值
  {
    const { clock, requests, queue } = setup()
    queue.schedule('A', 1)
    queue.schedule('B', 2)
    queue.flush('A')
    assert.deepEqual(requests.map((r) => r.key), ['A'])
    queue.flushAll()
    assert.deepEqual(requests.map((r) => r.key), ['A', 'B'])
    assert.equal(clock.count(), 0, 'flush 后不留计时器')
    queue.flush('不存在的行')
  }

  // 8. save 同步抛异常也按失败处理
  {
    const clock = createFakeTimers()
    const errors: string[] = []
    const queue = createLineSaveQueue<number>({
      delayMs: 400,
      timers: clock.timers,
      save: () => {
        throw new Error('同步异常')
      },
      onSuccess: () => assert.fail('不应成功'),
      onError: (_key, _value, error) => errors.push((error as Error).message),
    })
    queue.schedule('A', 1)
    clock.runAll()
    await flushMicrotasks()
    assert.deepEqual(errors, ['同步异常'])
    assert.equal(queue.hasBusy(), false)
  }

  // 9. 行键编解码
  assert.equal(makeLineKey('s1', 'i1'), 's1|i1')
  assert.deepEqual(parseLineKey('s1|i1'), { storeGuid: 's1', itemGuid: 'i1' })
  assert.deepEqual(parseLineKey('i1'), { storeGuid: '', itemGuid: 'i1' })
}

await run()
