/**
 * EditableCells — 单据编辑内联编辑单元格组件
 *
 * 职责边界：
 * - EditableTextCell：双击编辑文本，Enter 提交 / Esc 取消
 * - EditableNumberCell：双击编辑数字，Enter 提交 / Esc 取消；可选支持上下方向键切换同列行
 * - 纯展示组件，所有保存逻辑由父组件通过 onSave 回调注入
 * - 不接管任何数据加载或业务逻辑
 */

import { Input, InputNumber } from 'antd'
import { useEffect, useRef, useState, type CSSProperties, type FocusEvent, type KeyboardEvent, type ReactNode } from 'react'
import {
  resolveEditableNumberInputWidth,
  shouldSelectEditableNumberTextOnFocus,
} from './editableCellLayout'
import type { InvoiceDetailInlineEditableField, InvoiceDetailInlineNavigationKey } from './inlineEdit'

// ---- 类型 ----

type InlineCellSaveHandler = (
  detailGuid: string,
  field: InvoiceDetailInlineEditableField,
  value: unknown,
) => void

type InlineCellNavigateHandler = (
  detailGuid: string,
  field: InvoiceDetailInlineEditableField,
  key: InvoiceDetailInlineNavigationKey,
) => boolean

// ---- 工具 ----

function formatAmount(value?: number) {
  if (value === undefined || value === null) return '--'
  return value.toFixed(2)
}

// ---- EditableTextCell ----

export function EditableTextCell({
  value,
  detailGuid,
  field,
  onSave,
  display,
  style,
  readOnly = false,
}: {
  value?: string
  detailGuid: string
  field: InvoiceDetailInlineEditableField
  onSave: InlineCellSaveHandler
  display?: ReactNode
  style?: CSSProperties
  /** 只读用户（如店长查看）只展示，不响应双击编辑。 */
  readOnly?: boolean
}) {
  const [editing, setEditing] = useState(false)
  const [inputValue, setInputValue] = useState(value ?? '')

  useEffect(() => {
    setInputValue(value ?? '')
  }, [value])

  const commit = () => {
    onSave(detailGuid, field, inputValue)
    setEditing(false)
  }

  const handleKeyDown = (event: KeyboardEvent<HTMLInputElement>) => {
    if (event.key === 'Enter') {
      event.preventDefault()
      commit()
    }
    if (event.key === 'Escape') {
      setInputValue(value ?? '')
      setEditing(false)
    }
  }

  if (editing) {
    return (
      <Input
        autoFocus
        size="small"
        value={inputValue}
        onChange={(event) => setInputValue(event.target.value)}
        onBlur={commit}
        onKeyDown={handleKeyDown}
      />
    )
  }

  if (readOnly) {
    return <span style={style}>{display ?? value ?? '--'}</span>
  }

  return (
    <span style={{ ...style, cursor: 'pointer' }} onDoubleClick={() => setEditing(true)}>
      {display ?? value ?? '--'}
    </span>
  )
}

// ---- EditableNumberCell ----

export function EditableNumberCell({
  value,
  detailGuid,
  field,
  onSave,
  active,
  onActivate,
  onDeactivate,
  onNavigate,
  displayValue,
  style,
  min = 0,
  max,
  precision = 2,
  addonAfter,
  inputWidth,
  controls,
  selectTextOnFocus,
  readOnly = false,
}: {
  value?: number | null
  detailGuid: string
  field: InvoiceDetailInlineEditableField
  onSave: InlineCellSaveHandler
  active?: boolean
  onActivate?: () => void
  onDeactivate?: () => void
  onNavigate?: InlineCellNavigateHandler
  displayValue?: ReactNode
  style?: CSSProperties
  min?: number
  max?: number
  precision?: number
  addonAfter?: ReactNode
  inputWidth?: number
  controls?: boolean
  selectTextOnFocus?: boolean
  /** 只读用户（如店长查看）只展示，不响应双击编辑。 */
  readOnly?: boolean
}) {
  const [localEditing, setLocalEditing] = useState(false)
  const [inputValue, setInputValue] = useState<number | null>(value ?? null)
  const skipNextBlurCommitRef = useRef(false)
  const isControlled = active !== undefined
  const editing = isControlled ? Boolean(active) : localEditing

  useEffect(() => {
    setInputValue(value ?? null)
  }, [value])

  useEffect(() => {
    if (editing) {
      skipNextBlurCommitRef.current = false
      setInputValue(value ?? null)
    }
  }, [editing, value])

  const setEditing = (nextEditing: boolean) => {
    if (isControlled) {
      if (nextEditing) onActivate?.()
      else onDeactivate?.()
      return
    }
    setLocalEditing(nextEditing)
  }

  const openEditing = () => {
    setInputValue(value ?? null)
    setEditing(true)
  }

  const commit = (source: 'blur' | 'keyboard' = 'keyboard') => {
    if (inputValue != null) {
      onSave(detailGuid, field, inputValue)
    }
    if (source !== 'blur') {
      skipNextBlurCommitRef.current = true
    }
    setEditing(false)
  }

  const handleBlur = () => {
    if (skipNextBlurCommitRef.current) {
      skipNextBlurCommitRef.current = false
      return
    }
    commit('blur')
  }

  const navigate = (key: InvoiceDetailInlineNavigationKey) => {
    if (!onNavigate) return
    skipNextBlurCommitRef.current = true
    // 方向键录入时先提交当前输入值，再由父组件打开相邻行同列编辑框。
    if (inputValue != null) {
      onSave(detailGuid, field, inputValue)
    }
    const moved = onNavigate(detailGuid, field, key)
    if (!moved) {
      skipNextBlurCommitRef.current = false
    }
  }

  const handleKeyDown = (event: KeyboardEvent<HTMLInputElement>) => {
    if ((event.key === 'ArrowUp' || event.key === 'ArrowDown') && onNavigate) {
      event.preventDefault()
      event.stopPropagation()
      navigate(event.key)
      return
    }

    if (event.key === 'Enter') {
      event.preventDefault()
      commit()
    }
    if (event.key === 'Escape') {
      skipNextBlurCommitRef.current = true
      setInputValue(value ?? null)
      setEditing(false)
    }
  }

  const handleFocus = (event: FocusEvent<HTMLInputElement>) => {
    if (!shouldSelectEditableNumberTextOnFocus(selectTextOnFocus)) return
    const inputElement = event.currentTarget
    // 方向键切到目标格时，等 AntD 同步显示值后再全选，方便直接覆盖输入。
    window.setTimeout(() => {
      if (document.contains(inputElement)) {
        inputElement.select()
      }
    }, 0)
  }

  if (editing) {
    return (
      <InputNumber
        autoFocus
        size="small"
        min={min}
        max={max}
        precision={precision}
        addonAfter={addonAfter}
        controls={controls}
        value={inputValue}
        onChange={(nextValue) => setInputValue(nextValue)}
        onBlur={handleBlur}
        onFocus={handleFocus}
        onKeyDown={handleKeyDown}
        style={{ width: resolveEditableNumberInputWidth({ addonAfter, inputWidth }) }}
      />
    )
  }

  if (readOnly) {
    return <span style={style}>{displayValue ?? formatAmount(value ?? undefined)}</span>
  }

  return (
    <span style={{ ...style, cursor: 'pointer' }} onDoubleClick={openEditing}>
      {displayValue ?? formatAmount(value ?? undefined)}
    </span>
  )
}
