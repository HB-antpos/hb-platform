// 抽屉分店明细里「今年订货(箱)」的输入框：失焦 / Enter 保存（与矩阵共用同一条保存队列），Esc 撤销。
import { App } from 'antd'
import { useRef, useState, type KeyboardEvent } from 'react'
import { useTranslation } from 'react-i18next'

import './registerSpAdminMessages'
import { parseCartonInput, sanitizeCartonDraft } from './summaryMatrix.logic'

interface SummaryQtyInputProps {
  /** 当前显示的今年箱数（已叠加保存中的值） */
  value: number
  disabled: boolean
  busy: boolean
  ariaLabel: string
  /** 输入框序号：Enter 后跳到序号 + 1 的输入框，方便从上到下连续录入 */
  'data-qty-index': number
  onCommit: (cartons: number) => void
}

export function SummaryQtyInput({ value, disabled, busy, ariaLabel, onCommit, 'data-qty-index': index }: SummaryQtyInputProps) {
  const { t } = useTranslation()
  const { message } = App.useApp()
  // 聚焦期间显示用户正在输入的草稿；失焦后始终显示外部传入的值（保存结果 / 回滚都能直接反映）
  const [draft, setDraft] = useState<string | null>(null)
  // Enter / Esc 结束后紧接着会触发 blur：用标记保证同一次编辑只提交一次，下次聚焦再重置
  const finishedRef = useRef(true)

  const finish = (commitValue: boolean) => {
    if (finishedRef.current) return
    finishedRef.current = true
    const text = draft
    setDraft(null)
    if (!commitValue || text === null) return
    const parsed = parseCartonInput(text)
    if (!parsed.ok) {
      if (parsed.reason === 'invalid') message.warning(t('spAdmin.summary.invalidCartons'))
      return
    }
    if (parsed.value !== value) onCommit(parsed.value)
  }

  const onKeyDown = (event: KeyboardEvent<HTMLInputElement>) => {
    if (event.nativeEvent.isComposing) return
    const input = event.currentTarget
    if (event.key === 'Enter') {
      event.preventDefault()
      finish(true)
      const next = input.closest('table')?.querySelector<HTMLInputElement>(`input[data-qty-index="${index + 1}"]`)
      if (next && !next.disabled) next.focus()
      else input.blur()
    } else if (event.key === 'Escape') {
      event.preventDefault()
      event.stopPropagation()
      finish(false)
      input.blur()
    }
  }

  return (
    <span className="sp-admin-sum-qty-wrap">
      <input
        className={value === 0 && draft === null ? 'sp-admin-sum-qty is-empty sp-admin-number' : 'sp-admin-sum-qty sp-admin-number'}
        inputMode="numeric"
        autoComplete="off"
        maxLength={4}
        data-qty-index={index}
        aria-label={ariaLabel}
        aria-busy={busy || undefined}
        disabled={disabled}
        value={draft ?? String(value)}
        onFocus={(event) => {
          finishedRef.current = false
          setDraft(String(value))
          event.currentTarget.select()
        }}
        onChange={(event) => setDraft(sanitizeCartonDraft(event.target.value))}
        onBlur={() => finish(true)}
        onKeyDown={onKeyDown}
      />
      {busy ? <i className="sp-admin-sum-qty-spin" aria-hidden="true" /> : null}
    </span>
  )
}
