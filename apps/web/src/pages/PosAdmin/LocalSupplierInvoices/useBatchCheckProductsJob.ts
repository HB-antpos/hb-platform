import { useCallback, useEffect, useLayoutEffect, useRef, useState } from 'react'
import {
  cancelBatchCheckProductsJob,
  getBatchCheckProductsJob,
  startBatchCheckProductsJob,
} from '../../../services/localSupplierInvoiceService'
import type { BatchCheckProductsJobDto } from '../../../types/localSupplierInvoice'
import { RequestError } from '../../../utils/request'
import {
  BATCH_CHECK_POLL_INTERVAL_MS,
  isBatchJobActive,
  readStoredBatchJobId,
  shouldRefreshListForBatch,
  writeStoredBatchJobId,
  type BatchListRefreshState,
} from './batchCheck'

/** 连续查询失败这么多次才放弃轮询，网络抖动不打断进度显示。 */
const MAX_CONSECUTIVE_POLL_FAILURES = 5

function getSessionStorage() {
  try {
    return typeof window === 'undefined' ? undefined : window.sessionStorage
  } catch {
    return undefined
  }
}

interface UseBatchCheckProductsJobOptions {
  /** 有新处理完的单时调用（已节流），由页面静默刷新列表与计数。 */
  onListShouldRefresh: () => void
  /** 轮询彻底失败（任务丢失除外）时提示。 */
  onPollError?: (error: unknown) => void
}

/**
 * 列表批量商品检测任务：提交、轮询进度、停止剩余、关闭进度条。
 * 任务在后端执行，页面只负责显示；jobId 存 sessionStorage，离开再回来接着显示。
 */
export function useBatchCheckProductsJob({ onListShouldRefresh, onPollError }: UseBatchCheckProductsJobOptions) {
  const [job, setJob] = useState<BatchCheckProductsJobDto | null>(null)
  const [submitting, setSubmitting] = useState(false)
  const [cancelling, setCancelling] = useState(false)
  // 连续查询失败后停止轮询：进度条改为「进度获取失败」并允许关闭，后台任务不受影响。
  const [pollStopped, setPollStopped] = useState(false)
  const jobIdRef = useRef<string | null>(null)
  const timerRef = useRef<ReturnType<typeof setTimeout> | null>(null)
  const failuresRef = useRef(0)
  const refreshStateRef = useRef<BatchListRefreshState>({ refreshedProcessed: 0, lastRefreshAt: 0 })
  const callbacksRef = useRef({ onListShouldRefresh, onPollError })
  useLayoutEffect(() => {
    callbacksRef.current = { onListShouldRefresh, onPollError }
  })

  const clearTimer = () => {
    if (timerRef.current) {
      clearTimeout(timerRef.current)
      timerRef.current = null
    }
  }

  const applyJob = useCallback((next: BatchCheckProductsJobDto) => {
    // 关键位置：只接受当前跟踪任务的结果，避免关闭进度条或换任务后旧轮询回写。
    if (jobIdRef.current !== next.jobId) return
    setJob(next)
    const now = Date.now()
    if (shouldRefreshListForBatch(next, refreshStateRef.current, now)) {
      refreshStateRef.current = { refreshedProcessed: next.processed, lastRefreshAt: now }
      callbacksRef.current.onListShouldRefresh()
    }
  }, [])

  const poll = useCallback(
    (jobId: string) => {
      clearTimer()
      timerRef.current = setTimeout(async () => {
        timerRef.current = null
        try {
          const next = await getBatchCheckProductsJob(jobId)
          if (jobIdRef.current !== jobId) return
          failuresRef.current = 0
          applyJob(next)
          if (isBatchJobActive(next)) poll(jobId)
        } catch (error) {
          if (jobIdRef.current !== jobId) return
          if (error instanceof RequestError && error.status === 404) {
            // 任务过期或 API 重启：已检测完的单结果已落库，刷新一次列表后结束跟踪。
            jobIdRef.current = null
            writeStoredBatchJobId(getSessionStorage(), null)
            setJob(null)
            callbacksRef.current.onListShouldRefresh()
            return
          }
          failuresRef.current += 1
          if (failuresRef.current >= MAX_CONSECUTIVE_POLL_FAILURES) {
            setPollStopped(true)
            callbacksRef.current.onPollError?.(error)
            return
          }
          poll(jobId)
        }
      }, BATCH_CHECK_POLL_INTERVAL_MS)
    },
    [applyJob],
  )

  const track = useCallback(
    (next: BatchCheckProductsJobDto, refreshedProcessed: number) => {
      jobIdRef.current = next.jobId
      failuresRef.current = 0
      setPollStopped(false)
      refreshStateRef.current = { refreshedProcessed, lastRefreshAt: Date.now() }
      writeStoredBatchJobId(getSessionStorage(), next.jobId)
      setJob(next)
      if (isBatchJobActive(next)) poll(next.jobId)
    },
    [poll],
  )

  // 回到页面时恢复上次提交的任务进度。
  useEffect(() => {
    const storedJobId = readStoredBatchJobId(getSessionStorage())
    if (!storedJobId) return
    jobIdRef.current = storedJobId
    getBatchCheckProductsJob(storedJobId)
      .then((restored) => {
        if (jobIdRef.current !== storedJobId) return
        // 恢复时列表本身就是最新加载的，只有之后新处理完的单才需要再刷新。
        track(restored, restored.processed)
      })
      .catch(() => {
        if (jobIdRef.current !== storedJobId) return
        jobIdRef.current = null
        writeStoredBatchJobId(getSessionStorage(), null)
      })
  }, [track])

  useEffect(() => () => {
    clearTimer()
    jobIdRef.current = null
  }, [])

  const start = useCallback(
    async (invoiceGuids: string[]) => {
      setSubmitting(true)
      try {
        const started = await startBatchCheckProductsJob(invoiceGuids)
        track(started, 0)
        return started
      } finally {
        setSubmitting(false)
      }
    },
    [track],
  )

  const cancel = useCallback(async () => {
    const jobId = jobIdRef.current
    if (!jobId) return
    setCancelling(true)
    try {
      const next = await cancelBatchCheckProductsJob(jobId)
      applyJob(next)
    } finally {
      setCancelling(false)
    }
  }, [applyJob])

  /** 关闭进度条：任务结束或进度获取失败后可用，清掉行内的失败 / 跳过标记。 */
  const dismiss = useCallback(() => {
    clearTimer()
    jobIdRef.current = null
    writeStoredBatchJobId(getSessionStorage(), null)
    setPollStopped(false)
    setJob(null)
  }, [])

  return { job, submitting, cancelling, pollStopped, start, cancel, dismiss }
}
