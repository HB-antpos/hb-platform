import { useCallback, useEffect, useState } from 'react'

interface RemoteState<T> {
  data: T | null
  loading: boolean
  error: unknown
}

/**
 * 通用读取：loader 变化（依赖变化）或调用 reload 时重新请求；上一次请求会被中止，乱序响应与卸载后的响应一律丢弃。
 * loader 传 null 表示暂不请求（例如还没选门店），此时状态清空。
 * 调用方必须用 useCallback 稳定 loader，否则每次渲染都会重新请求。
 */
export function useRemote<T>(loader: ((signal: AbortSignal) => Promise<T>) | null) {
  const [state, setState] = useState<RemoteState<T>>({ data: null, loading: false, error: null })
  const [tick, setTick] = useState(0)

  useEffect(() => {
    if (!loader) {
      setState({ data: null, loading: false, error: null })
      return
    }
    const controller = new AbortController()
    // 保留上一份数据直到新数据到达，列表刷新时不闪空白。
    setState((current) => ({ data: current.data, loading: true, error: null }))
    loader(controller.signal)
      .then((data) => {
        if (!controller.signal.aborted) setState({ data, loading: false, error: null })
      })
      .catch((error: unknown) => {
        if (!controller.signal.aborted) setState((current) => ({ data: current.data, loading: false, error }))
      })
    return () => controller.abort()
  }, [loader, tick])

  const reload = useCallback(() => setTick((value) => value + 1), [])
  return { ...state, reload }
}
