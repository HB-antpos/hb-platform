// 订货汇总矩阵的分店格：大字 = 今年箱数（0 显示淡色点），小字 =「去年 X.X」（该店去年销量折合箱数）。
// 单击进入编辑（选中全部文字）；Enter / Tab / 方向键保存并移动，Esc 撤销，失焦自动保存。
import { Tooltip } from 'antd'
import { memo, useEffect, useRef, useState, type KeyboardEvent, type MouseEvent, type ReactNode } from 'react'
import { useTranslation } from 'react-i18next'

import type { SupplierPreorderLineMark, SupplierPreorderMatrixRow, SupplierPreorderStoreRef } from '../../../types/supplierPreorder'

import './registerSpAdminMessages'
import {
  formatLastYearCartons,
  getLastYearCellValue,
  isMissedCell,
  keyToMove,
  sanitizeCartonDraft,
  toLastYearCartons,
  toMonthDayTime,
  type CellMove,
} from './summaryMatrix.logic'
import { formatMatrixValue, formatSydneyDateTime, type MatrixUnit } from './supplierPreorderAdmin.logic'

export interface SummaryCellProps {
  row: SupplierPreorderMatrixRow
  rowIndex: number
  store: SupplierPreorderStoreRef
  colIndex: number
  /** 当前显示的今年箱数（已叠加保存中的值） */
  value: number
  unit: MatrixUnit
  compare: boolean
  editable: boolean
  editing: boolean
  /** 直接敲数字进入编辑时的首个字符 */
  seed?: string
  busy: boolean
  /** 键盘焦点所在格（roving tabindex：整张表只有一个 Tab 停靠点） */
  active: boolean
  onStart: (rowIndex: number, colIndex: number, seed?: string) => void
  /** text = null 表示撤销；move 不为空时调用方要把编辑移到下一格 */
  onDone: (rowIndex: number, colIndex: number, text: string | null, move: CellMove | null) => void
  onFocusCell: (rowIndex: number, colIndex: number) => void
  onNavigate: (rowIndex: number, colIndex: number, move: CellMove) => void
}

export const SummaryCell = memo(function SummaryCell(props: SummaryCellProps) {
  const { t } = useTranslation()
  const { row, rowIndex, store, colIndex, value, unit, compare, editable, editing, busy, active } = props
  const mark: SupplierPreorderLineMark | undefined = row.storeMarks[store.storeGuid]
  const sales = row.storeLastYearSales[store.storeGuid]
  const cartonsMode = unit === 'cartons'
  const lastYearText = !compare || !row.lastYear ? null : cartonsMode ? formatLastYearCartons(sales, row.cartonQty) : formatLastYearValue(row, store.storeGuid, unit)
  const missed = compare && cartonsMode && isMissedCell(value, toLastYearCartons(sales, row.cartonQty))
  const unitLabel = t(`spAdmin.summary.unit.${unit}`)
  const ariaLabel = t('spAdmin.summary.cellAria', { store: store.storeName, item: row.supplierItemCode, value: formatMatrixValue(value, unit), unit: unitLabel })

  const className = ['sp-admin-sum-td', 'sp-admin-sum-store-td', mark ? 'has-mark' : '', busy ? 'is-saving' : '', editing ? 'is-editing' : ''].filter(Boolean).join(' ')

  const body = editing ? (
    <CellEditor
      initial={props.seed ?? String(value)}
      selectAll={props.seed === undefined}
      ariaLabel={ariaLabel}
      onDone={(text, move) => props.onDone(rowIndex, colIndex, text, move)}
    >
      {compare && row.lastYear ? <span className={missed ? 'sp-admin-sum-h is-miss' : 'sp-admin-sum-h'}>{lastYearLabel(t, lastYearText)}</span> : null}
    </CellEditor>
  ) : (
    <div
      className={editable ? 'sp-admin-sum-cell is-editable' : 'sp-admin-sum-cell'}
      role="button"
      tabIndex={active ? 0 : -1}
      aria-label={ariaLabel}
      data-cell={`${rowIndex}:${colIndex}`}
      onFocus={() => props.onFocusCell(rowIndex, colIndex)}
      onClick={(event: MouseEvent<HTMLDivElement>) => {
        // 可编辑时点格子只进入编辑，不触发整行的「打开抽屉」
        if (!editable) return
        event.stopPropagation()
        props.onStart(rowIndex, colIndex)
      }}
      onKeyDown={(event: KeyboardEvent<HTMLDivElement>) => {
        if (event.metaKey || event.ctrlKey || event.altKey) return
        const move = keyToMove(event.key)
        if (move && event.key.startsWith('Arrow')) {
          event.preventDefault()
          props.onNavigate(rowIndex, colIndex, move)
          return
        }
        if (!editable) return
        if (event.key === 'Enter' || event.key === 'F2') {
          event.preventDefault()
          props.onStart(rowIndex, colIndex)
        } else if (/^\d$/.test(event.key)) {
          event.preventDefault()
          props.onStart(rowIndex, colIndex, event.key)
        }
      }}
    >
      <span className={value ? 'sp-admin-sum-q' : 'sp-admin-sum-q is-zero'}>{value ? formatMatrixValue(value, unit) : '·'}</span>
      {compare && row.lastYear ? <span className={missed ? 'sp-admin-sum-h is-miss' : 'sp-admin-sum-h'}>{lastYearLabel(t, lastYearText)}</span> : null}
    </div>
  )

  return (
    <td className={className} role="gridcell">
      {mark ? (
        <Tooltip
          placement="bottom"
          title={
            <div className="sp-admin-sum-mark-tip sp-admin-number">
              <b>{t('spAdmin.summary.markTitle')}</b>
              <span>{t('spAdmin.summary.markChange', { store: store.storeName, from: mark.originalCartons, to: value })}</span>
              <span className="sp-admin-sum-mark-by">
                {t('spAdmin.summary.markBy', { by: mark.adjustedBy || '--', time: toMonthDayTime(formatSydneyDateTime(mark.adjustedAtUtc)) })}
              </span>
            </div>
          }
        >
          {body}
        </Tooltip>
      ) : (
        body
      )}
    </td>
  )
})

/** 小字：有去年记录显示「去年 6.6」，没有显示「去年 —」 */
function lastYearLabel(t: (key: string, options?: Record<string, unknown>) => string, text: string | null): string {
  return t('spAdmin.summary.lastYearCell', { value: text ?? '—' })
}

/** 非「箱」单位下小字的去年数（件 / 金额） */
function formatLastYearValue(row: SupplierPreorderMatrixRow, storeGuid: string, unit: MatrixUnit): string | null {
  const value = getLastYearCellValue(row, storeGuid, unit)
  return value === null ? null : formatMatrixValue(value, unit)
}

// ---------- 编辑框 ----------

interface CellEditorProps {
  initial: string
  selectAll: boolean
  ariaLabel: string
  onDone: (text: string | null, move: CellMove | null) => void
  children?: ReactNode
}

/**
 * 编辑框：挂载即聚焦；Enter / Tab / 方向键 = 保存并移动，Esc = 撤销，失焦 = 保存。
 * 左右方向键只在光标已在最左 / 最右（含全选状态）时才移动格子，其余时候保持输入框自己的光标移动。
 */
function CellEditor({ initial, selectAll, ariaLabel, onDone, children }: CellEditorProps) {
  const [draft, setDraft] = useState(initial)
  const inputRef = useRef<HTMLInputElement>(null)
  // 保存 / 撤销后输入框会被卸载，浏览器随后可能再触发一次 blur：用标记保证只结束一次
  const doneRef = useRef(false)

  useEffect(() => {
    const input = inputRef.current
    if (!input) return
    input.focus()
    if (selectAll) input.select()
    else input.setSelectionRange(input.value.length, input.value.length)
  }, [selectAll])

  const finish = (text: string | null, move: CellMove | null) => {
    if (doneRef.current) return
    doneRef.current = true
    onDone(text, move)
  }

  const onKeyDown = (event: KeyboardEvent<HTMLInputElement>) => {
    if (event.nativeEvent.isComposing) return
    if (event.key === 'Escape') {
      event.preventDefault()
      event.stopPropagation()
      finish(null, null)
      return
    }
    const move = keyToMove(event.key, event.shiftKey)
    if (!move) return
    const input = event.currentTarget
    if (move === 'left' && input.selectionStart !== 0) return
    if (move === 'right' && input.selectionEnd !== input.value.length) return
    event.preventDefault()
    finish(draft, move)
  }

  return (
    <div className="sp-admin-sum-cell is-editing" onClick={(event) => event.stopPropagation()}>
      <input
        ref={inputRef}
        className="sp-admin-sum-input sp-admin-number"
        inputMode="numeric"
        autoComplete="off"
        maxLength={4}
        value={draft}
        aria-label={ariaLabel}
        onChange={(event) => setDraft(sanitizeCartonDraft(event.target.value))}
        onKeyDown={onKeyDown}
        onBlur={() => finish(draft, null)}
      />
      {children}
    </div>
  )
}
