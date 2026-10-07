import { useCallback, useEffect, useRef, useState } from 'react'
import { getStoreReceiptProfileStatuses } from '../../../services/storeReceiptProfileService'
import type { StoreReceiptProfileStatusItem } from '../../../types/storeReceiptProfile'
import { applyStatusResponse, createStatusIssueTracker } from './receiptProfileLogic'

interface UseReceiptProfileStatusesOptions {
  /** 每个页面实例只会在第一次失败时回调一次（用于一次性轻提示），之后的失败静默。 */
  onFirstFailure?: (error: unknown) => void
}

/**
 * 分店列表的「小票下发状态」：异步补充，不阻塞列表首屏。
 * - refresh(guids)：请求这些分店的状态并合并进状态表，旧值在新值到达前继续显示（不闪骨架）。
 * - 同一分店被多次请求时，只有最近一次发起的响应会写入（见 createStatusIssueTracker）。
 * - 失败不抛出：failed=true，状态列显示占位，列表其余功能照常。
 */
export function useReceiptProfileStatuses({ onFirstFailure }: UseReceiptProfileStatusesOptions = {}) {
  const [statusByGuid, setStatusByGuid] = useState<Record<string, StoreReceiptProfileStatusItem>>({})
  const [loadingCount, setLoadingCount] = useState(0)
  const [failed, setFailed] = useState(false)
  const trackerRef = useRef(createStatusIssueTracker())
  const mountedRef = useRef(true)
  const warnedRef = useRef(false)
  // 回调放进 ref：refresh 保持稳定引用，不会因为父组件重渲染而变化。
  const onFirstFailureRef = useRef(onFirstFailure)
  onFirstFailureRef.current = onFirstFailure

  useEffect(() => {
    mountedRef.current = true
    return () => {
      mountedRef.current = false
    }
  }, [])

  const refresh = useCallback(async (storeGuids: readonly string[]) => {
    const guids = Array.from(new Set(storeGuids.filter(Boolean)))
    if (guids.length === 0) {
      return
    }

    const token = trackerRef.current.begin(guids)
    setLoadingCount((count) => count + 1)
    try {
      const items = await getStoreReceiptProfileStatuses(guids)
      if (!mountedRef.current) {
        return
      }
      setStatusByGuid((previous) => applyStatusResponse(
        previous,
        guids,
        items,
        (guid) => trackerRef.current.isCurrent(token, guid),
      ))
      setFailed(false)
    } catch (error) {
      if (!mountedRef.current) {
        return
      }
      console.error(error)
      setFailed(true)
      if (!warnedRef.current) {
        warnedRef.current = true
        onFirstFailureRef.current?.(error)
      }
    } finally {
      if (mountedRef.current) {
        setLoadingCount((count) => count - 1)
      }
    }
  }, [])

  return { statusByGuid, loading: loadingCount > 0, failed, refresh }
}
