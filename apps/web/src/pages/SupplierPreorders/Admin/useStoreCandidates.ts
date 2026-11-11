// 订货汇总「添加分店」下拉里的未开启分店：打开下拉时才请求，结果缓存到分店开关变化 / 开启成功为止。
import { useCallback, useEffect, useRef, useState } from 'react'

import { getSupplierPreorderStoreCandidates } from '../../../services/supplierPreorderService'
import type { SupplierPreorderStoreCandidate } from '../../../types/supplierPreorder'

import { isAbortError } from './supplierPreorderAdmin.logic'

export interface StoreCandidatesState {
  /** null = 还没请求过（或缓存已失效） */
  candidates: SupplierPreorderStoreCandidate[] | null
  loading: boolean
  failed: boolean
  /** 没有缓存时请求；已有缓存直接返回（forceReload 为 true 时强制重拉，用于「重试」） */
  load: (forceReload?: boolean) => void
  /** 开启成功后把这家店从候选里移掉，不必重新请求 */
  remove: (storeGuid: string) => void
}

/** refreshKey 变化（分店开关 / 修订版应用）时缓存作废：开启状态可能已经在别处改过 */
export function useStoreCandidates(formGuid: string, refreshKey: number): StoreCandidatesState {
  const [candidates, setCandidates] = useState<SupplierPreorderStoreCandidate[] | null>(null)
  const [loading, setLoading] = useState(false)
  const [failed, setFailed] = useState(false)
  const controllerRef = useRef<AbortController | null>(null)
  // 用 ref 记「是否已有缓存 / 正在请求」，让 load 的引用保持稳定
  const stateRef = useRef({ cached: false, inflight: false })

  useEffect(() => {
    controllerRef.current?.abort()
    stateRef.current = { cached: false, inflight: false }
    setCandidates(null)
    setLoading(false)
    setFailed(false)
    return () => controllerRef.current?.abort()
  }, [formGuid, refreshKey])

  const load = useCallback(
    (forceReload = false) => {
      const state = stateRef.current
      if (state.inflight || (state.cached && !forceReload)) return
      const controller = new AbortController()
      controllerRef.current = controller
      state.inflight = true
      setLoading(true)
      setFailed(false)
      getSupplierPreorderStoreCandidates(formGuid, controller.signal)
        .then((result) => {
          if (controller.signal.aborted) return
          state.cached = true
          setCandidates(result)
        })
        .catch((error) => {
          if (controller.signal.aborted || isAbortError(error)) return
          setFailed(true)
        })
        .finally(() => {
          if (controller.signal.aborted) return
          state.inflight = false
          setLoading(false)
        })
    },
    [formGuid],
  )

  const remove = useCallback((storeGuid: string) => {
    setCandidates((current) => (current ? current.filter((item) => item.storeGuid !== storeGuid) : current))
  }, [])

  return { candidates, loading, failed, load, remove }
}
