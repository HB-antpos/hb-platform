// 订货汇总的「视图状态」：分店列显示 / 顺序 + 商品排序，按订单表记在 localStorage（读写都容错），以及键盘移动列后的 aria-live 播报。
// 只管客户端视图；矩阵数据与合计仍来自后端，不受这里影响。
import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { useTranslation } from 'react-i18next'

import type { SupplierPreorderStoreRef } from '../../../types/supplierPreorder'

import {
  hideColumn,
  loadSummaryView,
  moveVisibleBy,
  prunePrefs,
  reorderByDrop,
  resetColumns,
  resolveColumns,
  saveSummaryView,
  showColumn,
  showOnlyOrdered,
  type ColumnPrefs,
  type ResolvedColumns,
  type StoredSummaryView,
} from './summaryColumns.logic'
import { normalizeSort, type SortState } from './summarySort.logic'

export interface SummaryColumnsController {
  resolved: ResolvedColumns
  sort: SortState
  setSort: (next: SortState) => void
  /** 拖拽松手：在显示列的第 insertIndex 列之前插入 */
  reorder: (guid: string, insertIndex: number) => void
  /** 键盘 Alt+←/→；返回是否真的移动了（到头了为 false） */
  moveBy: (guid: string, delta: number) => boolean
  show: (guid: string) => void
  hide: (guid: string) => void
  showOnlyOrdered: () => void
  reset: () => void
  /** 给屏幕阅读器的播报文字（aria-live 区域渲染它） */
  announcement: string
}

export function useSummaryColumns(formGuid: string, stores: SupplierPreorderStoreRef[] | undefined, orderedGuids: ReadonlySet<string>): SummaryColumnsController {
  const { t } = useTranslation()
  const [view, setView] = useState<StoredSummaryView>(() => loadSummaryView(formGuid))
  const [announce, setAnnounce] = useState({ text: '', tick: 0 })

  // 持久化（失败静默：视图仍在内存里生效）
  useEffect(() => {
    saveSummaryView(formGuid, view)
  }, [formGuid, view])

  // 订单表里分店增减后保持自洽：丢弃已不存在的 guid（记住的店排序对应的店没了也回默认序号）
  useEffect(() => {
    if (!stores) return
    const valid = new Set(stores.map((store) => store.storeGuid))
    setView((current) => {
      const columns = prunePrefs(current.columns, valid)
      const sort = normalizeSort(current.sort, valid)
      return columns === current.columns && sort === current.sort ? current : { columns, sort }
    })
  }, [stores])

  const resolved = useMemo(() => resolveColumns(stores ?? [], orderedGuids, view.columns), [orderedGuids, stores, view.columns])

  // 操作函数读 ref 里的最新设置：开启分店等异步流程回来时不会拿到过期的闭包，函数引用也保持稳定
  const latest = useRef({ resolved, columns: view.columns })
  latest.current = { resolved, columns: view.columns }

  const setColumns = useCallback((next: ColumnPrefs | null) => {
    if (next) setView((current) => ({ ...current, columns: next }))
  }, [])

  const setSort = useCallback((next: SortState) => setView((current) => ({ ...current, sort: next })), [])
  const reorder = useCallback((guid: string, insertIndex: number) => setColumns(reorderByDrop(latest.current.columns, latest.current.resolved, guid, insertIndex)), [setColumns])

  const moveBy = useCallback(
    (guid: string, delta: number) => {
      const { columns, resolved: current } = latest.current
      const result = moveVisibleBy(columns, current, guid, delta)
      if (!result) return false
      setColumns(result.prefs)
      const name = current.visible.find((store) => store.storeGuid === guid)?.storeName ?? ''
      setAnnounce((state) => ({ text: t('spAdmin.summary.moveAnnounce', { store: name, position: result.position }), tick: state.tick + 1 }))
      return true
    },
    [setColumns, t],
  )

  const show = useCallback((guid: string) => setColumns(showColumn(latest.current.columns, latest.current.resolved, guid)), [setColumns])
  const hide = useCallback((guid: string) => setColumns(hideColumn(latest.current.columns, guid)), [setColumns])
  const onlyOrdered = useCallback(() => setColumns(showOnlyOrdered(latest.current.columns)), [setColumns])
  const reset = useCallback(() => setColumns(resetColumns()), [setColumns])

  // 相同文字连续播报两次时，屏幕阅读器不会重读：交替追加零宽空格让内容有变化
  const announcement = announce.text ? announce.text + (announce.tick % 2 ? '' : '​') : ''

  return { resolved, sort: view.sort, setSort, reorder, moveBy, show, hide, showOnlyOrdered: onlyOrdered, reset, announcement }
}
