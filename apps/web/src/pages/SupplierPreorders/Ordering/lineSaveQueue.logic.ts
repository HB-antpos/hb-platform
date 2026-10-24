// 行级保存队列：本店下单「失焦/步进即保存」时，按行（key）合并与串行化请求。
//
// 规则：
// 1. 防抖合并：同一行在 delayMs 内的连续操作只发最后一次的值（连点「+」三下只发一次）。
// 2. 每行串行：同一行同时最多一个请求在途；在途期间的新值先排队，等在途请求结束再发。
//    这样服务器处理顺序 = 发送顺序，后发的值一定最后落库，不会被先发的覆盖。
// 3. 只采纳最新：每次发送分配递增序号；只有「最新序号且之后没有待发值」的结果才算 isLatest。
//    非最新的成功结果仍会回调（用于更新服务器基准值），但界面不应据此清掉用户正在编辑的值；
//    非最新的失败直接忽略（后面还有更新的值要发）。
// 4. 不同行互不影响，可以并发。

export interface LineSaveQueueTimers {
  set: (callback: () => void, ms: number) => unknown
  clear: (handle: unknown) => void
}

export interface LineSaveOutcome {
  /** 这次结果是否对应该行最后一次操作（之后没有待发/排队的值） */
  isLatest: boolean
  /** 本次请求在该行的序号（从 1 递增） */
  seq: number
}

export interface LineSaveQueueOptions<R> {
  delayMs: number
  save: (key: string, value: number) => Promise<R>
  /** 成功回调：按发送顺序依次触发（每行串行，天然有序） */
  onSuccess: (key: string, value: number, result: R, outcome: LineSaveOutcome) => void
  /** 失败回调：只在最新一次失败时触发，调用方应回滚到服务器值并提示 */
  onError: (key: string, value: number, error: unknown) => void
  /** 行的忙碌状态变化（有待发、排队或在途请求即为忙） */
  onBusyChange?: (key: string, busy: boolean) => void
  timers?: LineSaveQueueTimers
}

interface LineState {
  timer: unknown | null
  /** 防抖中、尚未发出的值 */
  pendingValue: number | null
  /** 在途请求结束后要发的值 */
  queuedValue: number | null
  inFlight: boolean
  seq: number
  busy: boolean
}

export interface LineSaveQueue {
  /** 记录某行的新值，防抖后发送 */
  schedule: (key: string, value: number) => void
  /** 立即发送某行防抖中的值 */
  flush: (key: string) => void
  /** 立即发送所有行防抖中的值（切换分店、离开页面、截止前调用） */
  flushAll: () => void
  isBusy: (key: string) => boolean
  hasBusy: () => boolean
}

const defaultTimers: LineSaveQueueTimers = {
  set: (callback, ms) => setTimeout(callback, ms),
  clear: (handle) => clearTimeout(handle as ReturnType<typeof setTimeout>),
}

export function createLineSaveQueue<R>(options: LineSaveQueueOptions<R>): LineSaveQueue {
  const timers = options.timers ?? defaultTimers
  const lines = new Map<string, LineState>()

  const getLine = (key: string): LineState => {
    let line = lines.get(key)
    if (!line) {
      line = { timer: null, pendingValue: null, queuedValue: null, inFlight: false, seq: 0, busy: false }
      lines.set(key, line)
    }
    return line
  }

  const hasMore = (line: LineState) => line.timer !== null || line.queuedValue !== null

  const refreshBusy = (key: string, line: LineState) => {
    const busy = line.inFlight || hasMore(line)
    if (busy !== line.busy) {
      line.busy = busy
      options.onBusyChange?.(key, busy)
    }
    if (!busy) lines.delete(key)
  }

  const send = (key: string, line: LineState, value: number) => {
    line.inFlight = true
    line.seq += 1
    const seq = line.seq
    refreshBusy(key, line)

    let promise: Promise<R>
    try {
      promise = options.save(key, value)
    } catch (error) {
      promise = Promise.reject(error)
    }

    promise.then(
      (result) => {
        line.inFlight = false
        // 排队的值与刚保存成功的值相同，就不必再发一次
        if (line.queuedValue === value) line.queuedValue = null
        const isLatest = seq === line.seq && !hasMore(line)
        options.onSuccess(key, value, result, { isLatest, seq })
        next(key, line)
      },
      (error: unknown) => {
        line.inFlight = false
        // 还有更新的值要发：这次失败无需提示，最终以最后一次结果为准
        if (seq === line.seq && !hasMore(line)) options.onError(key, value, error)
        next(key, line)
      },
    )
  }

  const next = (key: string, line: LineState) => {
    if (line.queuedValue !== null) {
      const value = line.queuedValue
      line.queuedValue = null
      send(key, line, value)
      return
    }
    refreshBusy(key, line)
  }

  const fire = (key: string, line: LineState) => {
    line.timer = null
    const value = line.pendingValue
    line.pendingValue = null
    if (value === null) {
      refreshBusy(key, line)
      return
    }
    if (line.inFlight) {
      // 在途请求未结束：排队，结束后再发（保证同一行串行）
      line.queuedValue = value
      refreshBusy(key, line)
      return
    }
    send(key, line, value)
  }

  return {
    schedule(key, value) {
      const line = getLine(key)
      if (line.timer !== null) timers.clear(line.timer)
      line.pendingValue = value
      line.timer = timers.set(() => fire(key, line), options.delayMs)
      refreshBusy(key, line)
    },
    flush(key) {
      const line = lines.get(key)
      if (!line || line.timer === null) return
      timers.clear(line.timer)
      fire(key, line)
    },
    flushAll() {
      for (const [key, line] of [...lines.entries()]) {
        if (line.timer === null) continue
        timers.clear(line.timer)
        fire(key, line)
      }
    },
    isBusy(key) {
      return lines.get(key)?.busy ?? false
    },
    hasBusy() {
      for (const line of lines.values()) if (line.busy) return true
      return false
    },
  }
}

/** 行键 = 分店 + 商品，切换分店后旧分店的在途保存仍能正确归属 */
export function makeLineKey(storeGuid: string, itemGuid: string): string {
  return `${storeGuid}|${itemGuid}`
}

export function parseLineKey(key: string): { storeGuid: string; itemGuid: string } {
  const index = key.indexOf('|')
  return index < 0 ? { storeGuid: '', itemGuid: key } : { storeGuid: key.slice(0, index), itemGuid: key.slice(index + 1) }
}
