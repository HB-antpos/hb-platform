import { useCallback, useEffect, useRef, useState } from 'react'

export interface CashRequestState<T> {
  data: T | null
  error: unknown
  loading: boolean
  reload: () => void
}

interface InternalState<T> {
  data: T | null
  error: unknown
  loading: boolean
}

/**
 * 按「查询键」取数：键变了才重新请求，旧请求中止、旧结果不会覆盖新结果。
 * - enabled 为 false（页面被保活隐藏或页签未激活）时不发请求，也不丢已有数据；
 *   重新激活时同一个键已取过就不重复请求，切回来不闪烁；
 * - 换键时保留上一份数据直到新数据到达（表格不清空，只显示加载中）；失败时清空数据并给出错误，
 *   避免旧数据被误当成新条件下的结果。
 */
export function useCashRequest<T>(
  key: string | null,
  load: (signal: AbortSignal) => Promise<T>,
  enabled: boolean,
): CashRequestState<T> {
  const loadRef = useRef(load)
  loadRef.current = load
  const [nonce, setNonce] = useState(0)
  const [state, setState] = useState<InternalState<T>>({ data: null, error: null, loading: false })
  const doneRef = useRef<{ key: string; nonce: number } | null>(null)

  useEffect(() => {
    if (!enabled || key === null) return undefined
    if (doneRef.current?.key === key && doneRef.current.nonce === nonce) return undefined
    const controller = new AbortController()
    setState((current) => ({ ...current, loading: true, error: null }))
    loadRef.current(controller.signal).then(
      (data) => {
        if (controller.signal.aborted) return
        doneRef.current = { key, nonce }
        setState({ data, error: null, loading: false })
      },
      (error: unknown) => {
        if (controller.signal.aborted) return
        doneRef.current = { key, nonce }
        setState({ data: null, error, loading: false })
      },
    )
    return () => controller.abort()
  }, [enabled, key, nonce])

  const reload = useCallback(() => setNonce((value) => value + 1), [])
  return { ...state, reload }
}
