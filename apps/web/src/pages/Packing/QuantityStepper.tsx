import { MinusOutlined, PlusOutlined } from '@ant-design/icons'
import { Button, InputNumber } from 'antd'
import { useEffect, useState } from 'react'

interface QuantityStepperProps {
  value: number
  disabled?: boolean
  /** 装多了：数字变橙色提醒。 */
  warning?: boolean
  max?: number
  /** 提交新的总数量（≥ 1；删除走单独的删除按钮）。 */
  onCommit: (quantity: number) => void
}

/** 本箱数量：可点加减，也可以直接键盘输入（回车或失焦提交）。 */
export default function QuantityStepper({ value, disabled, warning, max = 100000, onCommit }: QuantityStepperProps) {
  const [draft, setDraft] = useState<number | null>(value)

  useEffect(() => {
    setDraft(value)
  }, [value])

  const commit = (next: number | null) => {
    const quantity = Math.floor(Number(next))
    if (!Number.isFinite(quantity) || quantity < 1 || quantity > max) {
      // 无效输入还原成当前数量。
      setDraft(value)
      return
    }
    if (quantity !== value) {
      onCommit(quantity)
    }
  }

  return (
    <span className={warning ? 'wh-packing-qty wh-packing-qty-warning' : 'wh-packing-qty'}>
      <Button
        size="small"
        icon={<MinusOutlined />}
        disabled={disabled || value <= 1}
        onClick={() => commit(value - 1)}
        aria-label="-1"
      />
      <InputNumber
        size="small"
        controls={false}
        min={1}
        max={max}
        precision={0}
        disabled={disabled}
        value={draft}
        onChange={(next) => setDraft(typeof next === 'number' ? next : null)}
        onBlur={() => commit(draft)}
        onPressEnter={(event) => (event.target as HTMLInputElement).blur()}
      />
      <Button
        size="small"
        icon={<PlusOutlined />}
        disabled={disabled || value >= max}
        onClick={() => commit(value + 1)}
        aria-label="+1"
      />
    </span>
  )
}
