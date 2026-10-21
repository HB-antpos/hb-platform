/**
 * ContainerDetailFillHandle — 货柜明细单元格「拖拽填充」的展示层
 *
 * 职责边界：
 * - 只负责在可编辑单元格右下角画填充柄，并在拖拽过程中高亮被覆盖的目标单元格
 * - 拖拽状态（源单元格、目标行）与最终写入由页面层通过 Context 提供，这里不碰保存队列
 * - 填充柄靠 CSS :focus-within 显示：输入框聚焦时出现；柄的 mousedown 会 preventDefault，
 *   因此按下柄不会让输入框失焦，拖拽期间柄也不会消失
 */

import { createContext, useContext, type MouseEvent as ReactMouseEvent, type ReactNode } from 'react'
import type { ContainerDetailColumnPasteKey } from './containerDetailColumnPaste'

export interface ContainerDetailFillDragState {
  sourceRowKey: string
  columnKey: ContainerDetailColumnPasteKey
  /** 当前拖到的范围内、会被覆盖的行（不含源行）。 */
  targetRowKeys: ReadonlySet<string>
}

export interface ContainerDetailFillContextValue {
  drag: ContainerDetailFillDragState | null
  onFillStart: (rowKey: string, columnKey: ContainerDetailColumnPasteKey) => void
}

export const ContainerDetailFillContext = createContext<ContainerDetailFillContextValue | null>(null)

interface FillableCellProps {
  rowKey: string
  columnKey: ContainerDetailColumnPasteKey
  /** 文本列的输入框撑满单元格宽度。 */
  block?: boolean
  children: ReactNode
}

/** 包住一个可编辑输入框：右下角出现填充柄，拖拽范围内的单元格高亮。 */
export function FillableCell({ rowKey, columnKey, block, children }: FillableCellProps) {
  const context = useContext(ContainerDetailFillContext)
  const drag = context?.drag ?? null
  const isSource = drag?.columnKey === columnKey && drag.sourceRowKey === rowKey
  const isTarget = drag?.columnKey === columnKey && drag.targetRowKeys.has(rowKey)
  const className = [
    'container-detail-fillable',
    block ? 'container-detail-fillable-block' : '',
    isSource ? 'container-detail-fillable-source' : '',
    isTarget ? 'container-detail-fill-target' : '',
  ].filter(Boolean).join(' ')

  const handleHandleMouseDown = (event: ReactMouseEvent<HTMLSpanElement>) => {
    if (event.button !== 0 || !context) return
    // 阻止默认行为 = 阻止输入框因点击柄而失焦，源单元格正在输入的值保持原样。
    event.preventDefault()
    event.stopPropagation()
    context.onFillStart(rowKey, columnKey)
  }

  return (
    <span className={className} data-fill-row-key={rowKey} data-fill-column-key={columnKey}>
      {children}
      {context ? (
        <span
          className="container-detail-fill-handle"
          role="presentation"
          aria-hidden="true"
          onMouseDown={handleHandleMouseDown}
        />
      ) : null}
    </span>
  )
}
