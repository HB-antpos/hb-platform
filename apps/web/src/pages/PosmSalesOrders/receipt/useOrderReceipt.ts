import { useCallback, useEffect, useRef, useState } from 'react'

import {
  getOrCreateReceiptLink,
  getSalesOrderReceipt,
  regenerateReceiptLink,
} from '../../../services/posmSalesOrderService'
import type { PosReceiptShareLink, PosReceiptStaffView } from '../../../types/posmSalesOrder'

interface ReceiptState {
  view: PosReceiptStaffView | null
  loading: boolean
  error: string | null
}

/**
 * 订单详情抽屉里的在线小票：按订单加载纸面与当前分享链接，并提供「生成 / 重新生成链接」。
 * 切换订单或关闭抽屉时取消上一次请求，并用 guid 校验丢弃串单的慢响应。
 */
export function useOrderReceipt(orderGuid: string, enabled: boolean) {
  const [state, setState] = useState<ReceiptState>({ view: null, loading: false, error: null })
  const guidRef = useRef('')
  const abortRef = useRef<AbortController | null>(null)

  const load = useCallback(
    async (silent = false) => {
      abortRef.current?.abort()
      if (!orderGuid || !enabled) {
        guidRef.current = ''
        setState({ view: null, loading: false, error: null })
        return
      }
      const controller = new AbortController()
      abortRef.current = controller
      guidRef.current = orderGuid
      setState((previous) => ({
        view: previous.view?.document.orderGuid === orderGuid ? previous.view : null,
        loading: !silent,
        error: null,
      }))
      try {
        const view = await getSalesOrderReceipt(orderGuid, controller.signal)
        if (guidRef.current !== orderGuid) return
        setState({ view, loading: false, error: null })
      } catch (error) {
        if (controller.signal.aborted || guidRef.current !== orderGuid) return
        setState({ view: null, loading: false, error: error instanceof Error ? error.message : String(error) })
      }
    },
    [orderGuid, enabled],
  )

  useEffect(() => {
    void load()
    return () => abortRef.current?.abort()
  }, [load])

  /** 已有有效链接就复用，没有才新建；新建后静默刷新纸面，让二维码出现在小票里。 */
  const ensureLink = useCallback(async (): Promise<PosReceiptShareLink> => {
    const link = await getOrCreateReceiptLink(orderGuid)
    if (guidRef.current === orderGuid) {
      setState((previous) => (previous.view ? { ...previous, view: { ...previous.view, link } } : previous))
      void load(true)
    }
    return link
  }, [orderGuid, load])

  const regenerate = useCallback(async (): Promise<PosReceiptShareLink> => {
    const link = await regenerateReceiptLink(orderGuid)
    if (guidRef.current === orderGuid) {
      setState((previous) => (previous.view ? { ...previous, view: { ...previous.view, link } } : previous))
      void load(true)
    }
    return link
  }, [orderGuid, load])

  return { ...state, reload: () => load(), ensureLink, regenerate }
}
