// 「订货汇总」页签的两个数据 hook：
// 1. useSummarySaveQueue：矩阵格子与抽屉输入框共用的逐格保存队列（同一格串行、失败回滚、保存中标记）；
// 2. useSummaryHistory：抽屉里某款商品的去年历史，按商品懒加载并缓存（加载中 / 失败 / 就绪）。
import { App } from 'antd'
import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { useTranslation } from 'react-i18next'

import { getSupplierPreorderAdminItemHistory, saveSupplierPreorderAdminLine } from '../../../services/supplierPreorderService'
import type { SupplierPreorderAdminSaveLineResult, SupplierPreorderItemHistoryDetail } from '../../../types/supplierPreorder'
import { createLineSaveQueue, makeLineKey, type LineSaveQueue } from '../Ordering/lineSaveQueue.logic'

import { formatSydneyDateTime, getErrorText, isAbortError } from './supplierPreorderAdmin.logic'

// ---------- 逐格保存队列 ----------

interface SaveQueueOptions {
  formGuid: string
  /** 每次保存成功（含非最新的）都回调，调用方把服务端结果并进矩阵 */
  onSaved: (result: SupplierPreorderAdminSaveLineResult) => void
  /** 队列从忙变空闲时回调（用来刷新页头统计、操作记录） */
  onIdle?: () => void
}

export interface SummarySaveQueue {
  /** 保存中尚未确认的格子值，键 = makeLineKey(storeGuid, itemGuid) */
  pending: Readonly<Record<string, number>>
  /** 有请求排队或在途的格子 */
  busyKeys: ReadonlySet<string>
  busyCount: number
  /** 最近一次全部保存完成的悉尼时间 HH:mm；还没保存过为 null */
  savedAtText: string | null
  commit: (storeGuid: string, itemGuid: string, cartons: number) => void
}

/**
 * 关键逻辑：
 * - 同一格连续修改只发最后一次、按顺序串行（lineSaveQueue 保证），不同格互不影响；
 * - 保存期间 pending 里保留用户输入的值，界面据此叠加显示（applyPendingEdits）；
 * - 最新一次成功：清掉 pending；最新一次失败：清掉 pending（界面回到服务端值）并提示原因；
 * - 离开页面前把还在防抖里的值立刻发出，保存中关闭 / 刷新标签页会触发浏览器离开确认。
 */
export function useSummarySaveQueue({ formGuid, onSaved, onIdle }: SaveQueueOptions): SummarySaveQueue {
  const { t } = useTranslation()
  const { message } = App.useApp()
  const [pending, setPending] = useState<Record<string, number>>({})
  const [busyKeys, setBusyKeys] = useState<ReadonlySet<string>>(() => new Set())
  const [savedAtText, setSavedAtText] = useState<string | null>(null)

  // 回调与文案放 ref：队列只随订单表创建一次，却总能调到最新的闭包
  const latest = useRef({ onSaved, onIdle, message, t })
  latest.current = { onSaved, onIdle, message, t }
  const busyRef = useRef(new Set<string>())

  const queue = useMemo<LineSaveQueue>(() => {
    return createLineSaveQueue<SupplierPreorderAdminSaveLineResult>({
      delayMs: 0,
      save: (key, value) => {
        const [storeGuid, itemGuid] = splitKey(key)
        return saveSupplierPreorderAdminLine(formGuid, storeGuid, itemGuid, value)
      },
      onSuccess: (key, value, result, outcome) => {
        latest.current.onSaved(result)
        if (outcome.isLatest) {
          setPending((current) => omitKey(current, key, value))
          setSavedAtText(formatSydneyDateTime(new Date().toISOString()).slice(11))
        }
      },
      onError: (key, value, error) => {
        // 回滚：去掉叠加值，格子回到最后一次服务端确认的数量
        setPending((current) => omitKey(current, key, value))
        // 刚失败，不能再显示「全部已保存」
        setSavedAtText(null)
        latest.current.message.error(getErrorText(error, latest.current.t('spAdmin.summary.saveFailed')))
      },
      onBusyChange: (key, busy) => {
        if (busy) busyRef.current.add(key)
        else busyRef.current.delete(key)
        setBusyKeys(new Set(busyRef.current))
        if (!busy && busyRef.current.size === 0) latest.current.onIdle?.()
      },
    })
  }, [formGuid])

  useEffect(() => {
    const currentQueue = queue
    return () => currentQueue.flushAll()
  }, [queue])

  const busyCount = busyKeys.size
  useEffect(() => {
    if (busyCount === 0) return undefined
    const warn = (event: BeforeUnloadEvent) => {
      event.preventDefault()
      event.returnValue = ''
    }
    window.addEventListener('beforeunload', warn)
    return () => window.removeEventListener('beforeunload', warn)
  }, [busyCount])

  const commit = useCallback(
    (storeGuid: string, itemGuid: string, cartons: number) => {
      const key = makeLineKey(storeGuid, itemGuid)
      setPending((current) => ({ ...current, [key]: cartons }))
      queue.schedule(key, cartons)
    },
    [queue],
  )

  return { pending, busyKeys, busyCount, savedAtText, commit }
}

function splitKey(key: string): [string, string] {
  const index = key.indexOf('|')
  return [key.slice(0, index), key.slice(index + 1)]
}

/** 只在 pending 里的值还是这次保存的值时才移除（防止误删用户随后输入的新值） */
function omitKey(current: Record<string, number>, key: string, value: number): Record<string, number> {
  if (!(key in current) || current[key] !== value) return current
  const next = { ...current }
  delete next[key]
  return next
}

// ---------- 抽屉历史（按商品懒加载 + 缓存） ----------

export type SummaryHistoryEntry =
  | { status: 'loading' }
  | { status: 'error'; message: string }
  | { status: 'ready'; detail: SupplierPreorderItemHistoryDetail }

export interface SummaryHistory {
  entries: Readonly<Record<string, SummaryHistoryEntry>>
  /** 没有缓存（或强制）才发请求；加载中不重复发 */
  load: (itemGuid: string, force?: boolean) => void
}

/** refreshKey 变化（分店开关、修订版）或订单表切换时清空缓存：开启分店变了，分店明细也要变 */
export function useSummaryHistory(formGuid: string, refreshKey: number): SummaryHistory {
  const { t } = useTranslation()
  const [entries, setEntries] = useState<Record<string, SummaryHistoryEntry>>({})
  const entriesRef = useRef(entries)
  entriesRef.current = entries
  const controllers = useRef(new Map<string, AbortController>())
  const fallbackText = t('spAdmin.summary.historyFailed')

  useEffect(() => {
    const active = controllers.current
    setEntries({})
    return () => {
      active.forEach((controller) => controller.abort())
      active.clear()
    }
  }, [formGuid, refreshKey])

  const load = useCallback(
    (itemGuid: string, force = false) => {
      const existing = entriesRef.current[itemGuid]
      if (!force && existing && existing.status !== 'error') return
      controllers.current.get(itemGuid)?.abort()
      const controller = new AbortController()
      controllers.current.set(itemGuid, controller)
      setEntries((current) => ({ ...current, [itemGuid]: { status: 'loading' } }))
      getSupplierPreorderAdminItemHistory(formGuid, itemGuid, controller.signal)
        .then((detail) => {
          if (controller.signal.aborted) return
          setEntries((current) => ({ ...current, [itemGuid]: { status: 'ready', detail } }))
        })
        .catch((error) => {
          if (controller.signal.aborted || isAbortError(error)) return
          setEntries((current) => ({ ...current, [itemGuid]: { status: 'error', message: getErrorText(error, fallbackText) } }))
        })
    },
    [fallbackText, formGuid],
  )

  return { entries, load }
}
