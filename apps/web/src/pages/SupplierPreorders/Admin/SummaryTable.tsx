// 订货汇总矩阵表：行 = 商品，列 =「去年全店」+ 每家开启分店 + 右侧合计。
// 商品列与右侧合计列固定（分店多时横向滚动），表尾合计取后端 totals（覆盖全部页）。
// 编辑状态（哪一格在编辑、键盘焦点在哪一格）只放在这里；保存与数据由父组件通过 commit / pending 提供。
import { App, Spin } from 'antd'
import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { useTranslation } from 'react-i18next'

import type { SupplierPreorderMatrix, SupplierPreorderMatrixRow } from '../../../types/supplierPreorder'
import { toProductThumbnailUrl } from '../../../utils/productImageThumbnail'
import { makeLineKey } from '../Ordering/lineSaveQueue.logic'
import { isOrderingHistoryPending, windowDayCount } from '../Ordering/orderingHistory.logic'

import './registerSpAdminMessages'
import { SummaryCell } from './SummaryCell'
import { SummaryLastYearCell, lastYearCellClassName } from './SummaryLastYearCell'
import { moveCell, parseCartonInput, type CellMove, type CellPos } from './summaryMatrix.logic'
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
const COL_PRODUCT = 236
const COL_LAST_YEAR = 150
const COL_STORE = 82
const COL_STORE_AMOUNT = 104
const COL_TOTAL_CARTONS = 68
const COL_TOTAL_AMOUNT = 96
const COL_TOTAL_CBM = 62

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
  onSelectRow: (itemGuid: string) => void
  commit: (storeGuid: string, itemGuid: string, cartons: number) => void
}

export default function SummaryTable({ matrix, unit, compare, editable, loading, selectedGuid, busyKeys, resetKey, onSelectRow, commit }: SummaryTableProps) {
  const { t } = useTranslation()
  const { message } = App.useApp()
  const rows = matrix.rows.items
  const stores = matrix.stores
  const [editing, setEditing] = useState<EditingCell | null>(null)
  const [active, setActive] = useState<CellPos>({ row: 0, col: 0 })
  const tableRef = useRef<HTMLTableElement>(null)

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
  const tableWidth = COL_THUMB + COL_PRODUCT + COL_LAST_YEAR + stores.length * storeWidth + COL_TOTAL_CARTONS + COL_TOTAL_AMOUNT + COL_TOTAL_CBM
  const totals = matrix.totals

  return (
    <Spin spinning={loading}>
      <div className="sp-admin-sum-scroll">
        <table ref={tableRef} className="sp-admin-sum-table" role="grid" aria-label={t('spAdmin.summary.tableAria')} style={{ width: '100%', minWidth: tableWidth }}>
          <colgroup>
            <col style={{ width: COL_THUMB }} />
            <col style={{ width: COL_PRODUCT }} />
            <col style={{ width: COL_LAST_YEAR }} />
            {stores.map((store) => (
              <col key={store.storeGuid} style={{ width: storeWidth }} />
            ))}
            {/* 填充列：容器比表格宽时由它吃掉多余宽度，右侧固定的合计列始终贴着右边缘、偏移量不变 */}
            <col />
            <col style={{ width: COL_TOTAL_CARTONS }} />
            <col style={{ width: COL_TOTAL_AMOUNT }} />
            <col style={{ width: COL_TOTAL_CBM }} />
          </colgroup>
          <thead>
            <tr>
              <th className="sp-admin-sum-th l is-sticky-l1" />
              <th className="sp-admin-sum-th l is-sticky-l2 is-edge-l">{t('spAdmin.summary.colProduct')}</th>
              <th className="sp-admin-sum-th l">
                {t('spAdmin.summary.colLastYear')}
                <span className="sp-admin-sum-th-sub">{t('spAdmin.summary.colLastYearSub')}</span>
              </th>
              {stores.map((store) => {
                const status = matrix.storeStatuses[store.storeGuid] ?? 'None'
                return (
                  <th key={store.storeGuid} className="sp-admin-sum-th is-store">
                    <span className="sp-admin-sum-store-name" title={store.storeName}>
                      {store.storeName}
                    </span>
                    <span className="sp-admin-sum-store-meta">
                      <i className={`sp-admin-sum-dot is-${status}`} aria-hidden="true" />
                      {t(`spAdmin.common.orderStatus.${status}`)}
                    </span>
                  </th>
                )
              })}
              <th className="sp-admin-sum-th sp-admin-sum-filler" />
              <th className="sp-admin-sum-th is-sticky-r3 is-edge-r">{t('spAdmin.summary.colTotalCartons')}</th>
              <th className="sp-admin-sum-th is-sticky-r2 is-edge-r">{t('spAdmin.summary.colTotalAmount')}</th>
              <th className="sp-admin-sum-th is-sticky-r1 is-edge-r">m³</th>
            </tr>
          </thead>
          <tbody>
            {rows.length === 0 ? (
              <tr>
                <td className="sp-admin-sum-empty" colSpan={stores.length + 7}>
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
                      onStart={startEdit}
                      onDone={handleDone}
                      onFocusCell={handleFocusCell}
                      onNavigate={handleNavigate}
                    />
                  )
                })}
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
              <td className="sp-admin-sum-td l is-sticky-l2 is-edge-l sp-admin-strong">{t('spAdmin.summary.totalRow', { count: totals.orderedItemCount })}</td>
              <td className="sp-admin-sum-td" />
              {stores.map((store) => (
                <td key={store.storeGuid} className="sp-admin-sum-td is-store-total sp-admin-number sp-admin-strong">
                  {formatMatrixValue(getMatrixStoreTotal(totals, store.storeGuid, unit), unit)}
                </td>
              ))}
              <td className="sp-admin-sum-td sp-admin-sum-filler" />
              <td className="sp-admin-sum-td is-total is-sticky-r3 is-edge-r sp-admin-number sp-admin-strong">{formatInteger(totals.totalCartons)}</td>
              <td className="sp-admin-sum-td is-total is-sticky-r2 is-edge-r sp-admin-number sp-admin-strong">{formatMoney(totals.totalAmount)}</td>
              <td className="sp-admin-sum-td is-total is-sticky-r1 is-edge-r sp-admin-number sp-admin-strong">{formatCbm(totals.totalCbm)}</td>
            </tr>
          </tfoot>
        </table>
      </div>
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
