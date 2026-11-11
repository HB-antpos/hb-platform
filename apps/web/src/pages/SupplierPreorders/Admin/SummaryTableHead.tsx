// 订货汇总矩阵表头：序号 / 商品 / 去年全店 / 每家显示的分店 / 「＋ 添加分店」虚线列 / 合计。
// 分店列头三件事：点名字按该店箱数排序（desc → asc → 回序号）；⋮⋮ 把手原生拖动调列序；Alt+←/→ 键盘移一列。
// 拖动只能从把手发起，所以点名字排序与拖动互不冲突。
import type { DragEvent, KeyboardEvent } from 'react'
import { useTranslation } from 'react-i18next'

import type { SupplierPreorderMatrixTotals, SupplierPreorderStoreOrderStatus, SupplierPreorderStoreRef } from '../../../types/supplierPreorder'

import './registerSpAdminMessages'
import { storeSortDirection, type SortState } from './summarySort.logic'
import type { ColumnDrag } from './useColumnDrag'

/** 表格与列头需要的「列控制」：排序、拖拽、键盘移动、新开启高亮、添加分店入口 */
export interface SummaryColumnControls {
  sort: SortState
  onSortSeq: () => void
  onSortStore: (storeGuid: string) => void
  drag: ColumnDrag
  /** 键盘 Alt+←/→ 移动一列（delta = -1 / +1） */
  onMoveBy: (storeGuid: string, delta: number) => void
  /** 刚开启的分店：列头标「新开启」 */
  freshGuid: string | null
  /** 刚开启的分店：整列浅蓝高亮（约 3 秒后淡出，由父组件关掉） */
  freshHighlight: boolean
  /** 已开启但没显示的分店数 */
  hiddenCount: number
  /** 未开启的分店数；还没请求过候选时为 null（不显示这一段） */
  offCount: number | null
  onOpenPicker: () => void
}

/** 单元格 / 列头的列状态：被拖的列淡化，新开启的列浅蓝 */
export function columnTone(controls: SummaryColumnControls, storeGuid: string): 'drag' | 'fresh' | undefined {
  if (controls.drag.guid === storeGuid) return 'drag'
  if (controls.freshGuid === storeGuid && controls.freshHighlight) return 'fresh'
  return undefined
}

interface SummaryTableHeadProps {
  stores: SupplierPreorderStoreRef[]
  storeStatuses: Record<string, SupplierPreorderStoreOrderStatus>
  totals: SupplierPreorderMatrixTotals
  controls: SummaryColumnControls
}

/** 拖动时跟着鼠标的「列头卡片」：用一个临时 DOM 当浏览器的 drag image（文字用 textContent，店名不会被当 HTML） */
function buildDragImage(title: string, meta: string): HTMLElement {
  const ghost = document.createElement('div')
  ghost.className = 'sp-admin-sum-ghost'
  const head = document.createElement('div')
  head.className = 'sp-admin-sum-ghost-title'
  head.textContent = `⋮⋮ ${title}`
  const sub = document.createElement('div')
  sub.className = 'sp-admin-sum-ghost-meta'
  sub.textContent = meta
  ghost.append(head, sub)
  document.body.appendChild(ghost)
  return ghost
}

export default function SummaryTableHead({ stores, storeStatuses, totals, controls }: SummaryTableHeadProps) {
  const { t } = useTranslation()
  const { sort, drag } = controls

  const handleDragStart = (event: DragEvent<HTMLElement>, store: SupplierPreorderStoreRef) => {
    const status = storeStatuses[store.storeGuid] ?? 'None'
    const meta = `${t(`spAdmin.common.orderStatus.${status}`)} · ${t('spAdmin.summary.ghostMeta', { cartons: totals.storeCartons[store.storeGuid] ?? 0 })}`
    const ghost = buildDragImage(store.storeName, meta)
    event.dataTransfer.effectAllowed = 'move'
    // Firefox 必须 setData 才会开始拖动
    event.dataTransfer.setData('text/plain', store.storeName)
    event.dataTransfer.setDragImage(ghost, 18, 18)
    // drag image 在本次事件结束时已截取完，下一个 tick 移除临时节点
    window.setTimeout(() => ghost.remove(), 0)
    drag.begin(store.storeGuid)
  }

  const handleKeyDown = (event: KeyboardEvent<HTMLElement>, storeGuid: string) => {
    if (!event.altKey || event.ctrlKey || event.metaKey) return
    if (event.key !== 'ArrowLeft' && event.key !== 'ArrowRight') return
    event.preventDefault()
    controls.onMoveBy(storeGuid, event.key === 'ArrowLeft' ? -1 : 1)
  }

  const addSubParts = [
    controls.hiddenCount > 0 ? t('spAdmin.summary.addColHidden', { count: controls.hiddenCount }) : null,
    controls.offCount !== null && controls.offCount > 0 ? t('spAdmin.summary.addColOff', { count: controls.offCount }) : null,
  ].filter(Boolean)

  return (
    <thead>
      <tr>
        <th className="sp-admin-sum-th l is-sticky-l1" />
        <th className="sp-admin-sum-th l is-sticky-lseq" aria-sort={sort.sort === 'seq' ? 'ascending' : undefined}>
          <button type="button" className="sp-admin-sum-sortbtn is-seq" onClick={controls.onSortSeq} title={t('spAdmin.summary.sortBySeq')}>
            {t('spAdmin.summary.colSeq')}
            {sort.sort === 'seq' ? <i className="sp-admin-sum-arrow is-on" aria-hidden="true">▲</i> : null}
          </button>
        </th>
        <th className="sp-admin-sum-th l is-sticky-l2 is-edge-l">{t('spAdmin.summary.colProduct')}</th>
        <th className="sp-admin-sum-th l">
          {t('spAdmin.summary.colLastYear')}
          <span className="sp-admin-sum-th-sub">{t('spAdmin.summary.colLastYearSub')}</span>
        </th>
        {stores.map((store) => {
          const status = storeStatuses[store.storeGuid] ?? 'None'
          const direction = storeSortDirection(sort, store.storeGuid)
          const tone = columnTone(controls, store.storeGuid)
          const fresh = controls.freshGuid === store.storeGuid
          return (
            <th
              key={store.storeGuid}
              className={['sp-admin-sum-th', 'is-store', tone ? `is-tone-${tone}` : ''].filter(Boolean).join(' ')}
              data-store-th={store.storeGuid}
              aria-sort={direction === 'desc' ? 'descending' : direction === 'asc' ? 'ascending' : undefined}
              onKeyDown={(event) => handleKeyDown(event, store.storeGuid)}
            >
              {tone === 'fresh' ? <span className="sp-admin-sum-fresh-bar" aria-hidden="true" /> : null}
              <span className="sp-admin-sum-store-top">
                <span
                  className="sp-admin-sum-grip"
                  draggable
                  role="button"
                  tabIndex={-1}
                  aria-label={t('spAdmin.summary.gripAria', { store: store.storeName })}
                  title={t('spAdmin.summary.gripTitle')}
                  onDragStart={(event) => handleDragStart(event, store)}
                  onDragEnd={drag.end}
                  onClick={(event) => event.stopPropagation()}
                >
                  ⋮⋮
                </span>
                <button
                  type="button"
                  className="sp-admin-sum-sortbtn is-store"
                  title={`${store.storeName} · ${t('spAdmin.summary.sortByStoreAria', { store: store.storeName })}`}
                  aria-label={t('spAdmin.summary.sortByStoreAria', { store: store.storeName })}
                  aria-keyshortcuts="Alt+ArrowLeft Alt+ArrowRight"
                  onClick={() => controls.onSortStore(store.storeGuid)}
                >
                  <span className="sp-admin-sum-store-name">{store.storeName}</span>
                  {direction ? (
                    <i className="sp-admin-sum-arrow is-on" aria-hidden="true">
                      {direction === 'desc' ? '▼' : '▲'}
                    </i>
                  ) : null}
                </button>
              </span>
              <span className="sp-admin-sum-store-meta">
                {fresh ? <span className="sp-admin-sum-fresh-tag">{t('spAdmin.summary.freshTag')}</span> : null}
                <i className={`sp-admin-sum-dot is-${status}`} aria-hidden="true" />
                {t(`spAdmin.common.orderStatus.${status}`)}
              </span>
            </th>
          )
        })}
        <th className="sp-admin-sum-th sp-admin-sum-addcol-th">
          <button type="button" className="sp-admin-sum-addcol" onClick={controls.onOpenPicker}>
            <span>＋ {t('spAdmin.summary.addStore')}</span>
            {addSubParts.length > 0 ? <small>{addSubParts.join(' · ')}</small> : null}
          </button>
        </th>
        <th className="sp-admin-sum-th sp-admin-sum-filler" />
        <th className="sp-admin-sum-th is-sticky-r3 is-edge-r">{t('spAdmin.summary.colTotalCartons')}</th>
        <th className="sp-admin-sum-th is-sticky-r2 is-edge-r">{t('spAdmin.summary.colTotalAmount')}</th>
        <th className="sp-admin-sum-th is-sticky-r1 is-edge-r">m³</th>
      </tr>
    </thead>
  )
}
