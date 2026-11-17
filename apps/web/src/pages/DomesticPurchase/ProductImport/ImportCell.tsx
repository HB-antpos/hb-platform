import { WarningOutlined } from '@ant-design/icons'
import { Input, InputNumber, Tooltip } from 'antd'
import { memo } from 'react'
import type { ImportEditableColumn } from './importPasteLogic'

interface ImportCellProps {
  rowId: string
  field: ImportEditableColumn
  kind: 'text' | 'number'
  value: string | number | undefined
  /** 检测 / 入库 / 发送等操作进行中：整张表锁定，不允许编辑。 */
  disabled: boolean
  /** 有变化：浅黄底，并在输入框下方显示库里的原值。 */
  changed?: boolean
  /** 已按当前语言拼好的原值文案，如「原 ¥ 6.50」或「原：空」。 */
  oldLabel?: string
  /** 单元格错误提示（如货号为空）：整格标红。 */
  invalidMessage?: string
  /** 非阻断提示（如非 EAN13 条码）：输入框黄色警示 + 图标 tooltip。 */
  warning?: string
  /**
   * 库内已有值（已按币种 / 小数位格式化）：本格没填时以灰字占位显示，值本身仍为空，所以不影响检测与提交。
   * 与 existingEmpty 二选一；都不传表示不显示（检测前、新商品行、开关关闭等）。
   */
  existingLabel?: string
  /** 库里这一项本来就是空：占位显示「—」。 */
  existingEmpty?: boolean
  /** 屏幕阅读器说明（灰字不是已填的值）；已按当前语言拼好。 */
  existingHint?: string
  precision?: number
  step?: string
  mono?: boolean
  onCommit: (rowId: string, field: ImportEditableColumn, value: string | number | undefined) => void
}

/**
 * 导入表格里的一个可编辑单元格。
 * memo + 全是原始类型的 props：每敲一个字符只有被改的那一格重渲染，不会因为 columns 重建而牵动整张表。
 */
export const ImportCell = memo(function ImportCell({
  rowId,
  field,
  kind,
  value,
  disabled,
  changed,
  oldLabel,
  invalidMessage,
  warning,
  existingLabel,
  existingEmpty,
  existingHint,
  precision,
  step,
  mono,
  onCommit,
}: ImportCellProps) {
  // 库内已有值只用 placeholder 展示：输入框的 value 仍为空，打字即覆盖，清空即恢复灰字（= 这一项不改）
  const placeholder = existingLabel ?? (existingEmpty ? '—' : undefined)
  const className = [
    'pi-cell',
    mono ? 'pi-cell-mono' : '',
    changed ? 'is-changed' : '',
    invalidMessage ? 'is-invalid' : '',
    existingLabel !== undefined ? 'is-existing' : existingEmpty ? 'is-existing-empty' : '',
  ].filter(Boolean).join(' ')

  return (
    // 灰字在窄屏会被省略号截断：悬停时用原生 title 看完整的库内值（错误提示优先）
    <div className={className} data-field={field} title={invalidMessage ?? existingLabel}>
      {kind === 'number' ? (
        <InputNumber
          value={value as number | undefined}
          // 清空数值框时 antd 给 null；统一成 undefined，与粘贴空单元格一致，也避免把 null 发给后端
          onChange={(next) => onCommit(rowId, field, next ?? undefined)}
          controls={false}
          size="small"
          step={step}
          precision={precision}
          disabled={disabled}
          status={invalidMessage ? 'error' : undefined}
          aria-invalid={invalidMessage ? true : undefined}
          placeholder={placeholder}
          aria-description={placeholder ? existingHint : undefined}
        />
      ) : (
        <Input
          value={(value as string | undefined) ?? ''}
          onChange={(event) => onCommit(rowId, field, event.target.value)}
          size="small"
          disabled={disabled}
          status={invalidMessage ? 'error' : warning ? 'warning' : undefined}
          aria-invalid={invalidMessage ? true : undefined}
          placeholder={placeholder}
          aria-description={placeholder ? existingHint : undefined}
          suffix={warning ? (
            <Tooltip title={warning}>
              <WarningOutlined className="pi-cell-warning-icon" />
            </Tooltip>
          ) : undefined}
        />
      )}
      {changed && oldLabel ? <span className="pi-cell-old">{oldLabel}</span> : null}
    </div>
  )
})
