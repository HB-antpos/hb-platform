import { Alert, Input, Modal, Typography } from 'antd'
import { useEffect, useState, type ReactNode } from 'react'
import { isReasonValid, MIN_REASON_LENGTH } from './logic'
import type { Tr } from './parts'

interface ReasonModalProps {
  open: boolean
  title: string
  /** 弹窗正文：说明这次操作的影响。 */
  description: ReactNode
  fieldLabel: string
  placeholder: string
  okText: string
  /** 必填时至少 2 个字；非必填时可留空，但填了也会提交。 */
  required: boolean
  danger?: boolean
  tr: Tr
  /** 提交失败时抛出可展示的文案（Error.message），弹窗保持打开并显示在顶部。 */
  onSubmit: (text: string) => Promise<void>
  onCancel: () => void
}

/**
 * 二次确认 + 填写原因 / 说明：作废与核对标记共用。
 * 提交中不能关闭，避免重复提交；失败信息留在弹窗里，用户改完原因可以直接重试。
 */
export default function ReasonModal({
  open,
  title,
  description,
  fieldLabel,
  placeholder,
  okText,
  required,
  danger = false,
  tr,
  onSubmit,
  onCancel,
}: ReasonModalProps) {
  const [text, setText] = useState('')
  const [touched, setTouched] = useState(false)
  const [submitting, setSubmitting] = useState(false)
  const [submitError, setSubmitError] = useState<string | null>(null)

  useEffect(() => {
    if (!open) return
    // 每次打开都从空白开始，不带上一条记录的原因。
    setText('')
    setTouched(false)
    setSubmitting(false)
    setSubmitError(null)
  }, [open])

  const invalid = required ? !isReasonValid(text) : text.trim().length > 0 && !isReasonValid(text)
  const showInvalid = touched && invalid

  const handleOk = async () => {
    setTouched(true)
    if (invalid || submitting) return
    setSubmitting(true)
    setSubmitError(null)
    try {
      await onSubmit(text.trim())
    } catch (error) {
      setSubmitError(error instanceof Error && error.message ? error.message : tr('errors.unknown'))
    } finally {
      setSubmitting(false)
    }
  }

  return (
    <Modal
      open={open}
      title={title}
      okText={okText}
      cancelText={tr('actions.cancel')}
      okButtonProps={{ danger, loading: submitting, disabled: touched && invalid }}
      cancelButtonProps={{ disabled: submitting }}
      closable={!submitting}
      maskClosable={false}
      keyboard={!submitting}
      destroyOnHidden
      onOk={() => { void handleOk() }}
      onCancel={() => { if (!submitting) onCancel() }}
    >
      <div className="store-cash-reason">
        {submitError ? <Alert type="error" showIcon message={submitError} /> : null}
        <Typography.Paragraph className="store-cash-reason-description">{description}</Typography.Paragraph>
        <label className="store-cash-reason-field">
          <span className="store-cash-field-label">
            {fieldLabel}
            {required ? <span className="store-cash-required" aria-hidden="true">*</span> : null}
          </span>
          <Input.TextArea
            value={text}
            autoFocus
            maxLength={500}
            showCount
            autoSize={{ minRows: 3, maxRows: 6 }}
            placeholder={placeholder}
            status={showInvalid ? 'error' : undefined}
            aria-invalid={showInvalid || undefined}
            aria-required={required || undefined}
            onChange={(event) => setText(event.target.value)}
            onBlur={() => setTouched(true)}
          />
        </label>
        {showInvalid ? (
          <Typography.Text type="danger" className="store-cash-reason-error">
            {tr('reason.tooShort', { min: MIN_REASON_LENGTH })}
          </Typography.Text>
        ) : null}
      </div>
    </Modal>
  )
}
