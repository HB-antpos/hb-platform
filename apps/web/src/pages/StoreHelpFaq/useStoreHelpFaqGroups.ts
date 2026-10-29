import { useCallback, useEffect, useState } from 'react'

import { loadStoreHelpFaqGroups, type StoreHelpFaqLoadResult } from './faqData'

export type StoreHelpFaqLoadState =
  | { status: 'loading' }
  | ({ status: 'ready' } & StoreHelpFaqLoadResult)
  // 接口与兜底快照都没拿到（例如离线且快照分块未缓存），页面给重试入口
  | { status: 'error' }

/**
 * 加载 FAQ 分组。加载中保持 `loading`，页面据此显示骨架，避免闪出空列表或「没有找到相关问题」。
 * `reload` 用于加载失败后的重试。
 */
export function useStoreHelpFaqGroups() {
  const [attempt, setAttempt] = useState(0)
  const [state, setState] = useState<StoreHelpFaqLoadState>({ status: 'loading' })

  useEffect(() => {
    // 组件卸载或重试时丢弃旧请求的结果
    let cancelled = false
    setState({ status: 'loading' })
    loadStoreHelpFaqGroups().then(
      (result) => {
        if (!cancelled) {
          setState({ status: 'ready', ...result })
        }
      },
      () => {
        if (!cancelled) {
          setState({ status: 'error' })
        }
      },
    )
    return () => {
      cancelled = true
    }
  }, [attempt])

  const reload = useCallback(() => setAttempt((current) => current + 1), [])
  return { state, reload }
}
