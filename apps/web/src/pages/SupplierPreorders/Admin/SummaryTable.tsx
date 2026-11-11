// 订货汇总矩阵表：行 = 商品，列 =「去年全店」+ 每家开启分店 + 右侧合计。
// 商品列与右侧合计列固定（分店多时横向滚动），表尾合计取后端 totals（覆盖全部页）。
// 编辑状态（哪一格在编辑、键盘焦点在哪一格）只放在这里；保存与数据由父组件通过 commit / pending 提供。
import { App, Spin } from 'antd'
import { useCallback, useEffect, useMemo, useRef, useState, type DragEvent } from 'react'
import { useTranslation } from 'react-i18next'

import type { SupplierPreorderMatrix, SupplierPreorderMatrixRow } from '../../../types/supplierPreorder'
import { toProductThumbnailUrl } from '../../../utils/productImageThumbnail'
import { makeLineKey } from '../Ordering/lineSaveQueue.logic'
import { isOrderingHistoryPending, windowDayCount } from '../Ordering/orderingHistory.logic'

import './registerSpAdminMessages'
import { SummaryCell } from './SummaryCell'
import { insertIndexAtX, previewDrop } from './summaryColumns.logic'
import { SummaryLastYearCell, lastYearCellClassName } from './SummaryLastYearCell'
import { moveCell, parseCartonInput, type CellMove, type CellPos } from './summaryMatrix.logic'
import SummaryTableHead, { columnTone, type SummaryColumnControls } from './SummaryTableHead'
import {
  formatCbm,
  formatInteger,
  formatMatrixValue,
  formatMoney,
  getMatrixCellValue,
  getMatrixStoreTotal,
  type MatrixUnit,
} from './supplierPreorderAdmin.logic'

// 列宽（px）：与 summary.css 里的固定列偏移量保持一致
const COL_THUMB = 50
const COL_SEQ = 44
const COL_PRODUCT = 236
const COL_LAST_YEAR = 150
const COL_ADD = 148
const COL_STORE = 88
const COL_STORE_AMOUNT = 104
const COL_TOTAL_CARTONS = 68
const COL_TOTAL_AMOUNT = 96
const COL_TOTAL_CBM = 62
// 拖动列时的自动横向滚动：鼠标进入「固定列之外的可视区」左右各 64px 内开始滚，每次 dragover 滚 24px
const STICKY_LEFT_WIDTH = COL_THUMB + COL_SEQ + COL_PRODUCT
const STICKY_RIGHT_WIDTH = COL_TOTAL_CARTONS + COL_TOTAL_AMOUNT + COL_TOTAL_CBM
const AUTO_SCROLL_EDGE = 64
const AUTO_SCROLL_STEP = 24

interface EditingCell {
  itemGuid: string
  storeGuid: string
  seed?: string
}

interface SummaryTableProps {
  /** 已叠加保存中的值（applyPendingEdits） */
  matrix: SupplierPreorderMatrix
  unit: MatrixUnit
  compare: boolean
  /** 只有「箱」单位且订单表不是草稿才可编辑 */
  editable: boolean
  loading: boolean
  selectedGuid: string | null
  busyKeys: ReadonlySet<string>
  /** 分页 / 筛选变化时递增：放弃正在进行的编辑 */
  resetKey: string
  /** 分店列的排序 / 拖拽 / 高亮 / 添加入口；stores 已经是「显示的列」，按显示顺序传入 */
  controls: SummaryColumnControls
  onSelectRow: (itemGuid: string) => void
  commit: (storeGuid: string, itemGuid: string, cartons: number) => void
}

export default function SummaryTable({ matrix, unit, compare, editable, loading, selectedGuid, busyKeys, resetKey, controls, onSelectRow, commit }: SummaryTableProps) {
  const { t } = useTranslation()
  const { message } = App.useApp()
  const rows = matrix.rows.items
  const stores = matrix.stores
  const [editing, setEditing] = useState<EditingCell | null>(null)
  const [active, setActive] = useState<CellPos>({ row: 0, col: 0 })
  const tableRef = useRef<HTMLTableElement>(null)
  const scrollRef = useRef<HTMLDivElement>(null)
  const innerRef = useRef<HTMLDivElement>(null)
  const tipRef = useRef<HTMLDivElement>(null)
  const pointerRef = useRef({ x: 0, y: 0 })
  // 落点竖线相对表格内容区的横坐标（随内容一起滚动，所以自动滚动时线始终贴着缝）
  const [dropLeft, setDropLeft] = useState<number | null>(null)

  // 用 ref 保存最新的行 / 店 / 编辑状态：传给 memo 单元格的回调保持引用稳定，输入时只有被编辑的那一格重渲染
  const latest = useRef({ rows, stores, commit })
  latest.current = { rows, stores, commit }

  useEffect(() => {
    setEditing(null)
    setActive({ row: 0, col: 0 })
  }, [resetKey])

  // 用 J / K 翻看商品时，让选中的行保持在可视范围内
  useEffect(() => {
    if (selectedGuid) tableRef.current?.querySelector('tr.is-selected')?.scrollIntoView({ block: 'nearest' })
  }, [selectedGuid])

  const windowDays = useMemo(() => windowDayCount(matrix.history.windowFrom, matrix.history.windowTo), [matrix.history.windowFrom, matrix.history.windowTo])
  const historyPending = isOrderingHistoryPending(matrix.history.status)

  const focusCell = useCallback((pos: CellPos) => {
    setActive(pos)
    // 等当前这一轮渲染（编辑框卸载、格子按钮出现）之后再聚焦
    requestAnimationFrame(() => tableRef.current?.querySelector<HTMLElement>(`[data-cell="${pos.row}:${pos.col}"]`)?.focus())
  }, [])

  const startEdit = useCallback((rowIndex: number, colIndex: number, seed?: string) => {
    const { rows: currentRows, stores: currentStores } = latest.current
    const row = currentRows[rowIndex]
    const store = currentStores[colIndex]
    if (!row || !store) return
    setActive({ row: rowIndex, col: colIndex })
    setEditing({ itemGuid: row.itemGuid, storeGuid: store.storeGuid, seed })
  }, [])

  const handleDone = useCallback(
    (rowIndex: number, colIndex: number, text: string | null, move: CellMove | null) => {
      const { rows: currentRows, stores: currentStores, commit: doCommit } = latest.current
      const row = currentRows[rowIndex]
      const store = currentStores[colIndex]
      if (row && store && text !== null) {
        const parsed = parseCartonInput(text)
        if (!parsed.ok) {
          // 空串按「没改」；其余非法输入提示范围后回到原值
          if (parsed.reason === 'invalid') message.warning(t('spAdmin.summary.invalidCartons'))
        } else if (parsed.value !== (row.storeCartons[store.storeGuid] ?? 0)) {
          doCommit(store.storeGuid, row.itemGuid, parsed.value)
        }
      }
      const target = move ? moveCell({ row: rowIndex, col: colIndex }, move, currentRows.length, currentStores.length) : null
      if (target) {
        const nextRow = currentRows[target.row]
        const nextStore = currentStores[target.col]
        setActive(target)
        setEditing({ itemGuid: nextRow.itemGuid, storeGuid: nextStore.storeGuid })
      } else {
        setEditing(null)
        // Esc / 到边界：焦点回到这一格，继续可用键盘移动；失焦保存（move 为空且 text 非空）不抢焦点
        if (text === null || move) focusCell({ row: rowIndex, col: colIndex })
      }
    },
    [focusCell, message, t],
  )

  const handleNavigate = useCallback(
    (rowIndex: number, colIndex: number, move: CellMove) => {
      const target = moveCell({ row: rowIndex, col: colIndex }, move, latest.current.rows.length, latest.current.stores.length)
      if (target) focusCell(target)
    },
    [focusCell],
  )

  const handleFocusCell = useCallback((rowIndex: number, colIndex: number) => setActive({ row: rowIndex, col: colIndex }), [])

  const storeWidth = unit === 'amount' ? COL_STORE_AMOUNT : COL_STORE
  const tableWidth = COL_THUMB + COL_SEQ + COL_PRODUCT + COL_LAST_YEAR + stores.length * storeWidth + COL_ADD + COL_TOTAL_CARTONS + COL_TOTAL_AMOUNT + COL_TOTAL_CBM
  const totals = matrix.totals

  // ---------- 拖动分店列（原生 HTML5 拖放）：表格负责算落点、画落点线、自动横向滚动 ----------
  const { drag } = controls
  const preview = drag.guid !== null && drag.insertIndex !== null ? previewDrop(stores.map((store) => store.storeGuid), drag.guid, drag.insertIndex) : null
  const showDropLine = !!preview?.changed && dropLeft !== null
  const handleDragOver = (event: DragEvent<HTMLDivElement>) => {
    if (drag.guid === null) return
    event.preventDefault()
    event.dataTransfer.dropEffect = 'move'
    pointerRef.current = { x: event.clientX, y: event.clientY }
    if (tipRef.current) {
      tipRef.current.style.left = `${event.clientX + 14}px`
      tipRef.current.style.top = `${event.clientY - 40}px`
    }
    const container = scrollRef.current
    const inner = innerRef.current
    if (!container || !inner) return
    const box = container.getBoundingClientRect()
    if (event.clientX < box.left + STICKY_LEFT_WIDTH + AUTO_SCROLL_EDGE) container.scrollLeft -= AUTO_SCROLL_STEP
    else if (event.clientX > box.right - STICKY_RIGHT_WIDTH - AUTO_SCROLL_EDGE) container.scrollLeft += AUTO_SCROLL_STEP
    // 滚动之后再量列头位置，落点才准
    const rects = [...(tableRef.current?.querySelectorAll<HTMLElement>('th[data-store-th]') ?? [])].map((th) => th.getBoundingClientRect())
    if (rects.length === 0) return
    const insertIndex = insertIndexAtX(rects, event.clientX)
    const edge = insertIndex < rects.length ? rects[insertIndex].left : rects[rects.length - 1].right
    setDropLeft(edge - inner.getBoundingClientRect().left)
    drag.hover(insertIndex)
  }
  const handleDragLeave = (event: DragEvent<HTMLDivElement>) => {
    // 只有真正离开表格区域才清落点（在子元素间移动也会触发 dragleave）
    if (drag.guid !== null && !event.currentTarget.contains(event.relatedTarget as Node | null)) drag.hover(null)
  }
  const handleDrop = (event: DragEvent<HTMLDivElement>) => {
    if (drag.guid === null) return
    event.preventDefault()
    drag.drop()
  }
  const dropTip = preview?.changed
    ? preview.beforeGuid
      ? t('spAdmin.summary.dropTip', { store: stores.find((store) => store.storeGuid === preview.beforeGuid)?.storeName ?? '', position: preview.position })
      : t('spAdmin.summary.dropTipEnd', { position: preview.position })
    : null

  return (
    <Spin spinning={loading}>
      <div ref={scrollRef} className="sp-admin-sum-scroll" onDragOver={handleDragOver} onDragLeave={handleDragLeave} onDrop={handleDrop}>
        <div ref={innerRef} className="sp-admin-sum-inner">
          {showDropLine ? <div className="sp-admin-sum-dropline" style={{ left: (dropLeft ?? 0) - 1.5 }} aria-hidden="true" /> : null}
          <table ref={tableRef} className="sp-admin-sum-table" role="grid" aria-label={t('spAdmin.summary.tableAria')} style={{ width: '100%', minWidth: tableWidth }}>
            <colgroup>
              <col style={{ width: COL_THUMB }} />
              <col style={{ width: COL_SEQ }} />
              <col style={{ width: COL_PRODUCT }} />
              <col style={{ width: COL_LAST_YEAR }} />
              {stores.map((store) => (
                <col key={store.storeGuid} style={{ width: storeWidth }} />
              ))}
              <col style={{ width: COL_ADD }} />
              {/* 填充列：容器比表格宽时由它吃掉多余宽度，右侧固定的合计列始终贴着右边缘、偏移量不变 */}
              <col />
              <col style={{ width: COL_TOTAL_CARTONS }} />
              <col style={{ width: COL_TOTAL_AMOUNT }} />
              <col style={{ width: COL_TOTAL_CBM }} />
            </colgroup>
            <SummaryTableHead stores={stores} storeStatuses={matrix.storeStatuses} totals={totals} controls={controls} />
            <tbody>
              {rows.length === 0 ? (
                <tr>
                  <td className="sp-admin-sum-empty" colSpan={stores.length + 9}>
                    {t('spAdmin.summary.empty')}
                  </td>
                </tr>
              ) : null}
              {rows.map((row, rowIndex) => (
                <tr
                  key={row.itemGuid}
                  className={row.itemGuid === selectedGuid ? 'sp-admin-sum-row is-selected' : 'sp-admin-sum-row'}
                  onClick={() => onSelectRow(row.itemGuid)}
                >
                  <td className="sp-admin-sum-td is-sticky-l1">
                    <ProductThumb url={row.imageUrl} />
                  </td>
                  <td className="sp-admin-sum-td is-sticky-lseq sp-admin-number sp-admin-sum-seq">{row.seq}</td>
                  <td className="sp-admin-sum-td l is-sticky-l2 is-edge-l">
                    <ProductCell row={row} onOpen={onSelectRow} />
                  </td>
                  <td className={`sp-admin-sum-td l ${lastYearCellClassName(row.lastYear)}`}>
                    <SummaryLastYearCell lastYear={row.lastYear} windowDays={windowDays} pending={historyPending} />
                  </td>
                  {stores.map((store, colIndex) => {
                    const isEditing = editing?.itemGuid === row.itemGuid && editing.storeGuid === store.storeGuid
                    return (
                      <SummaryCell
                        key={store.storeGuid}
                        row={row}
                        rowIndex={rowIndex}
                        store={store}
                        colIndex={colIndex}
                        value={getMatrixCellValue(row, store.storeGuid, unit)}
                        unit={unit}
                        compare={compare}
                        editable={editable}
                        editing={isEditing}
                        seed={isEditing ? editing?.seed : undefined}
                        busy={busyKeys.has(makeLineKey(store.storeGuid, row.itemGuid))}
                        active={active.row === rowIndex && active.col === colIndex}
                        tone={columnTone(controls, store.storeGuid)}
                        onStart={startEdit}
                        onDone={handleDone}
                        onFocusCell={handleFocusCell}
                        onNavigate={handleNavigate}
                      />
                    )
                  })}
                  <td className="sp-admin-sum-td sp-admin-sum-addcol-td" />
                  <td className="sp-admin-sum-td sp-admin-sum-filler" />
                  <td className="sp-admin-sum-td is-total is-sticky-r3 is-edge-r sp-admin-number sp-admin-strong">{formatInteger(row.totalCartons)}</td>
                  <td className="sp-admin-sum-td is-total is-sticky-r2 is-edge-r sp-admin-number">{formatMoney(row.totalAmount)}</td>
                  <td className="sp-admin-sum-td is-total is-sticky-r1 is-edge-r sp-admin-number">{formatCbm(row.totalCbm)}</td>
                </tr>
              ))}
            </tbody>
            <tfoot>
              <tr className="sp-admin-sum-foot">
                <td className="sp-admin-sum-td is-sticky-l1" />
                <td className="sp-admin-sum-td is-sticky-lseq" />
                <td className="sp-admin-sum-td l is-sticky-l2 is-edge-l sp-admin-strong">{t('spAdmin.summary.totalRow', { count: totals.orderedItemCount })}</td>
                <td className="sp-admin-sum-td" />
                {stores.map((store) => (
                  <td
                    key={store.storeGuid}
                    className={['sp-admin-sum-td', 'is-store-total', 'sp-admin-number', 'sp-admin-strong', columnTone(controls, store.storeGuid) ? `is-tone-${columnTone(controls, store.storeGuid)}` : ''].filter(Boolean).join(' ')}
                  >
                    {formatMatrixValue(getMatrixStoreTotal(totals, store.storeGuid, unit), unit)}
                  </td>
                ))}
                <td className="sp-admin-sum-td sp-admin-sum-addcol-td" />
                <td className="sp-admin-sum-td sp-admin-sum-filler" />
                <td className="sp-admin-sum-td is-total is-sticky-r3 is-edge-r sp-admin-number sp-admin-strong">{formatInteger(totals.totalCartons)}</td>
                <td className="sp-admin-sum-td is-total is-sticky-r2 is-edge-r sp-admin-number sp-admin-strong">{formatMoney(totals.totalAmount)}</td>
                <td className="sp-admin-sum-td is-total is-sticky-r1 is-edge-r sp-admin-number sp-admin-strong">{formatCbm(totals.totalCbm)}</td>
              </tr>
            </tfoot>
          </table>
        </div>
      </div>
      {dropTip ? (
        <div ref={tipRef} className="sp-admin-sum-droptip" style={{ left: pointerRef.current.x + 14, top: pointerRef.current.y - 40 }} aria-hidden="true">
          {dropTip}
        </div>
      ) : null}
    </Spin>
  )
}

function ProductThumb({ url }: { url?: string | null }) {
  const src = toProductThumbnailUrl(url)
  if (!src) return <span className="sp-admin-sum-thumb is-empty" aria-hidden="true" />
  return <img className="sp-admin-sum-thumb" src={src} alt="" width={36} height={36} loading="lazy" />
}

/** 商品格：名称（点击打开抽屉的键盘入口）、货号、进价（改价时现价红色）、箱规、「新品」「改价」标签 */
function ProductCell({ row, onOpen }: { row: SupplierPreorderMatrixRow; onOpen: (itemGuid: string) => void }) {
  const { t } = useTranslation()
  const priceChanged = row.prevPrice !== null && row.prevPrice !== undefined && row.prevPrice !== row.price
  return (
    <>
      <button
        type="button"
        className="sp-admin-sum-name"
        title={row.name}
        onClick={(event) => {
          event.stopPropagation()
          onOpen(row.itemGuid)
        }}
      >
        {row.name}
      </button>
      <span className="sp-admin-sum-meta sp-admin-number">
        <span className="sp-admin-mono">{row.supplierItemCode}</span>
        <span className={priceChanged ? 'sp-admin-sum-price is-changed' : undefined} title={priceChanged ? t('spAdmin.summary.prevPrice', { price: formatMoney(row.prevPrice) }) : undefined}>
          {formatMoney(row.price)}
        </span>
        <span>{t('spAdmin.summary.perCarton', { count: row.cartonQty })}</span>
        {row.isNew ? <span className="sp-admin-sum-tag is-new">{t('spAdmin.summary.tagNew')}</span> : null}
        {priceChanged ? <span className="sp-admin-sum-tag is-price">{t('spAdmin.summary.tagPriceChanged')}</span> : null}
      </span>
    </>
  )
}
