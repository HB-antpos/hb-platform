import { Input } from 'antd'
import type { TFunction } from 'i18next'
import type { StoreOrderReturnDisposition, StoreOrderReturnReason } from '../../../types/storeOrder'
import { STORE_ORDER_RETURN_NOTE_MAX_LENGTH, STORE_ORDER_RETURN_REASONS } from './storeOrderReturnLogic'

/**
 * 退货原因 / 处置 / 备注的输入控件：「添加退货商品」弹窗与发货格输入负数后的原因气泡共用，
 * 两个入口写入同一套字段、同一套文案。样式在 storeOrderDetail.css（wh-order-return-*）。
 */

/** 原因码 → 文案键。写成字面量映射（而不是拼接键名），页面消息契约测试才能静态核对中英文都存在。 */
export function resolveStoreOrderReturnReasonLabel(t: TFunction, reason: StoreOrderReturnReason | string | null | undefined) {
  switch (reason) {
    case 'damaged':
      return t('warehouseUi.storeOrderDetail.returnReason.damaged')
    case 'wrongItem':
      return t('warehouseUi.storeOrderDetail.returnReason.wrongItem')
    case 'overShipped':
      return t('warehouseUi.storeOrderDetail.returnReason.overShipped')
    case 'quality':
      return t('warehouseUi.storeOrderDetail.returnReason.quality')
    case 'nearExpiry':
      return t('warehouseUi.storeOrderDetail.returnReason.nearExpiry')
    case 'other':
      return t('warehouseUi.storeOrderDetail.returnReason.other')
    default:
      return ''
  }
}

export function resolveStoreOrderReturnDispositionLabel(t: TFunction, disposition: StoreOrderReturnDisposition | null | undefined) {
  if (disposition === 0) {
    return t('warehouseUi.storeOrderDetail.disposition.restock')
  }
  if (disposition === 1) {
    return t('warehouseUi.storeOrderDetail.disposition.scrap')
  }
  return ''
}

interface ReasonChipsProps {
  t: TFunction
  value?: StoreOrderReturnReason | null
  onChange: (value: StoreOrderReturnReason) => void
  disabled?: boolean
  /** 紧凑尺寸：气泡里用。 */
  compact?: boolean
  ariaLabel: string
}

/** 六个原因做成可单选的胶囊按钮（比下拉少一次点击）。 */
export function StoreOrderReturnReasonChips({ t, value, onChange, disabled, compact, ariaLabel }: ReasonChipsProps) {
  return (
    <div className={`wh-order-return-chips${compact ? ' is-compact' : ''}`} role="radiogroup" aria-label={ariaLabel}>
      {STORE_ORDER_RETURN_REASONS.map((reason) => (
        <button
          key={reason}
          type="button"
          role="radio"
          aria-checked={value === reason}
          disabled={disabled}
          className={`wh-order-return-chip${value === reason ? ' is-on' : ''}`}
          onClick={() => onChange(reason)}
        >
          {resolveStoreOrderReturnReasonLabel(t, reason)}
        </button>
      ))}
    </div>
  )
}

interface DispositionSegmentProps {
  t: TFunction
  value: StoreOrderReturnDisposition
  onChange: (value: StoreOrderReturnDisposition) => void
  disabled?: boolean
  ariaLabel: string
}

/** 处置：回仓可售 / 报损不回库（仅作记录）。 */
export function StoreOrderReturnDispositionSegment({ t, value, onChange, disabled, ariaLabel }: DispositionSegmentProps) {
  const options: StoreOrderReturnDisposition[] = [0, 1]
  return (
    <div className="wh-order-return-segment" role="radiogroup" aria-label={ariaLabel}>
      {options.map((option) => (
        <button
          key={option}
          type="button"
          role="radio"
          aria-checked={value === option}
          disabled={disabled}
          className={`wh-order-return-segment-item${value === option ? ' is-on' : ''}`}
          onClick={() => onChange(option)}
        >
          {resolveStoreOrderReturnDispositionLabel(t, option)}
        </button>
      ))}
    </div>
  )
}

interface NoteInputProps {
  value: string
  onChange: (value: string) => void
  placeholder: string
  disabled?: boolean
  rows?: number
}

export function StoreOrderReturnNoteInput({ value, onChange, placeholder, disabled, rows = 2 }: NoteInputProps) {
  return (
    <Input.TextArea
      rows={rows}
      value={value}
      disabled={disabled}
      maxLength={STORE_ORDER_RETURN_NOTE_MAX_LENGTH}
      placeholder={placeholder}
      onChange={(event) => onChange(event.target.value)}
    />
  )
}
