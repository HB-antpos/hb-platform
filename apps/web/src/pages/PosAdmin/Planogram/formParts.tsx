import { Form, InputNumber } from 'antd'
import type { FormInstance, Rule } from 'antd/es/form'
import type { NamePath } from 'antd/es/form/interface'
import { useRef, type ReactNode } from 'react'
import { useTranslation } from 'react-i18next'
import { createOpenKeyAllocator, planogramErrorText, resolvePlanogramError } from './planogramLogic'

/** 抽屉每次「打开」得到一个新编号，当作表单 key 使用（原因见 createOpenKeyAllocator）。 */
export function useOpenKey(target: object | null): number {
  const allocatorRef = useRef<ReturnType<typeof createOpenKeyAllocator>>()
  if (!allocatorRef.current) allocatorRef.current = createOpenKeyAllocator()
  return allocatorRef.current(target)
}

interface NumberFieldProps {
  name: NamePath
  label: ReactNode
  /** 必填：留空时提示「请填写」。 */
  required?: boolean
  /** 下限（含）；allowZero 为 false 时同时要求严格大于 0。 */
  min?: number
  max?: number
  /** 小数位数：0 = 只能填整数（包装尺寸 / 孔数）；不传 = 允许任意小数（孔距 25.4 等）。 */
  precision?: number
  /** 输入框右侧的单位，默认 mm。 */
  unit?: string
  /** 允许填 0（如臂端预留）；默认必须 > 0。 */
  allowZero?: boolean
  /** 一个单元格里并排两个输入框时用，覆盖外层样式。 */
  inline?: boolean
  placeholder?: string
  /** 输入时回调（只在用户改动时触发，程序 setFieldValue 不触发）。 */
  onChange?: (value: number | null) => void
  autoFocus?: boolean
  className?: string
  /** 单独禁用（如没有价签臂时的价签尺寸）；整个表单只读时由 Form 的 disabled 统一处理。 */
  disabled?: boolean
  /** 跨字段校验（如节宽必须大于立柱宽）；dependencies 里的字段变化时重新校验。 */
  extraRules?: Rule[]
  dependencies?: NamePath[]
}

/**
 * 带单位后缀的数字输入。设计图里单位（mm / g / kg）写在输入框右侧，用 InputNumber 的 suffix 实现；
 * 不显示加减按钮（controls=false），与设计图一致。
 */
export function NumberField({
  name, label, required = true, min, max, precision, unit = 'mm', allowZero = false, inline = false,
  placeholder, onChange, autoFocus, className, disabled, extraRules, dependencies,
}: NumberFieldProps) {
  const { t } = useTranslation()
  // 禁用的字段（如没有价签臂时的价签尺寸）不参与校验，否则会被一个看得见改不了的字段卡住保存
  const rules: Rule[] = []
  if (required && !disabled) rules.push({ required: true, message: t('planogram.validation.required') })
  if (!disabled) rules.push({
    validator: (_rule, value: unknown) => {
      if (value === null || value === undefined || value === '') return Promise.resolve()
      if (typeof value !== 'number' || Number.isNaN(value)) return Promise.reject(new Error(t('planogram.validation.number')))
      if (!allowZero && value <= 0) return Promise.reject(new Error(t('planogram.validation.positive')))
      if (allowZero && value < 0) return Promise.reject(new Error(t('planogram.validation.nonNegative')))
      return Promise.resolve()
    },
  })
  if (extraRules && !disabled) rules.push(...extraRules)
  return (
    <Form.Item
      name={name}
      label={label}
      rules={rules}
      dependencies={dependencies}
      className={className}
      style={inline ? { marginBottom: 0 } : undefined}
    >
      <InputNumber
        className="planogram-number"
        controls={false}
        min={min}
        max={max}
        precision={precision}
        suffix={unit ? <span className="planogram-unit">{unit}</span> : undefined}
        placeholder={placeholder}
        onChange={(value) => onChange?.(typeof value === 'number' ? value : null)}
        autoFocus={autoFocus}
        disabled={disabled}
      />
    </Form.Item>
  )
}

/** 分组标题：粗体小标题 + 灰色说明。 */
export function SectionTitle({ title, hint }: { title: string; hint?: string }) {
  return (
    <div className="planogram-section-title">
      <span>{title}</span>
      {hint ? <span className="planogram-section-hint">{hint}</span> : null}
    </div>
  )
}

/** 只读的自动换算格：灰底，右侧注明「自动」。 */
export function AutoCell({ label, value, note }: { label: ReactNode; value: ReactNode; note: ReactNode }) {
  return (
    <Form.Item label={label}>
      <div className="planogram-auto-cell" role="textbox" aria-readonly="true">
        <span>{value}</span>
        <span className="planogram-unit">{note}</span>
      </div>
    </Form.Item>
  )
}

/**
 * 把保存失败的原因落到界面上，返回「还需要弹出来的提示文案」（已经落到具体字段上的就不再弹）：
 * - PLANOGRAM_CODE_DUPLICATE：标在「编码」字段上；
 * - PLANOGRAM_VALIDATION 的 details 里带字段名的：标在对应字段上（按字段名大小写不敏感匹配）；
 * - 其余：返回文案交给调用方 message.error。
 */
export function applyPlanogramFormError(
  form: FormInstance,
  error: unknown,
  fieldNames: readonly string[],
  language: string,
  translate: (key: string) => string,
): string | null {
  const resolved = resolvePlanogramError(error)
  if (resolved.kind === 'abort') return null
  const text = planogramErrorText(resolved, language, translate)
  const byLower = new Map(fieldNames.map((field) => [field.toLowerCase(), field]))

  if (resolved.code === 'PLANOGRAM_CODE_DUPLICATE' && byLower.has('code')) {
    form.setFields([{ name: 'code', errors: [text] }])
    return null
  }

  const mapped = resolved.fieldErrors
    .map((item) => ({ field: item.field ? byLower.get(item.field.toLowerCase()) : undefined, message: item.message }))
  const matched = mapped.filter((item): item is { field: string; message: string } => Boolean(item.field))
  if (matched.length > 0) {
    form.setFields(matched.map((item) => ({ name: item.field, errors: [item.message] })))
    const unmatched = mapped.filter((item) => !item.field).map((item) => item.message)
    return unmatched.length > 0 ? unmatched.join('; ') : null
  }
  return text
}
