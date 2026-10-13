import { Alert, Form, Input, Modal } from 'antd'
import { useState } from 'react'
import { useTranslation } from 'react-i18next'

import { REASON_MAX_LENGTH, validateRequiredText } from './logic'

interface ReasonModalProps {
  title: string
  /** 说明这次操作的后果（撤回同意会同时撤回登记并删除腾讯云侧人脸等）。 */
  description: string
  okText: string
  /** 提交原因；失败时抛出，由本组件把错误文案显示在弹框里（调用方负责翻译）。 */
  onSubmit: (reason: string) => Promise<void>
  describeError: (error: unknown) => string
  onClose: () => void
  /** 为 true 时原因可留空（用于备注类输入）；默认必填。 */
  optional?: boolean
  maxLength?: number
  initialValue?: string
  /** 确定按钮是否用危险色（撤回类操作为 true；写备注类为 false）。 */
  danger?: boolean
  /** 输入框标签；默认「原因」。 */
  label?: string
}

/**
 * 需要填写原因的二次确认弹框（撤回同意 / 撤回登记 / 撤回名单条目 / 填写申诉备注）。
 * 父组件按需挂载本组件（挂载即打开，关闭即卸载），输入的原因不会残留。
 */
export default function ReasonModal({
  title,
  description,
  okText,
  onSubmit,
  describeError,
  onClose,
  optional = false,
  maxLength = REASON_MAX_LENGTH,
  initialValue = '',
  danger = true,
  label,
}: ReasonModalProps) {
  const { t } = useTranslation()
  const [reason, setReason] = useState(initialValue)
  const [submitting, setSubmitting] = useState(false)
  const [touched, setTouched] = useState(false)
  const [errorText, setErrorText] = useState<string | null>(null)

  const issue = optional ? null : validateRequiredText(reason, maxLength)
  const invalid = touched && issue !== null

  const submit = async () => {
    setTouched(true)
    if (issue) return
    setSubmitting(true)
    setErrorText(null)
    try {
      await onSubmit(reason.trim())
      onClose()
    } catch (error) {
      setErrorText(describeError(error))
    } finally {
      setSubmitting(false)
    }
  }

  return (
    <Modal
      open
      title={title}
      okText={okText}
      cancelText={t('faceMatch.common.cancel')}
      okButtonProps={{ danger }}
      confirmLoading={submitting}
      cancelButtonProps={{ disabled: submitting }}
      maskClosable={false}
      keyboard={!submitting}
      closable={!submitting}
      destroyOnHidden
      onOk={() => void submit()}
      onCancel={() => {
        if (!submitting) onClose()
      }}
    >
      <Alert type={danger ? 'warning' : 'info'} showIcon message={description} style={{ marginBottom: 16 }} />
      <Form layout="vertical" autoComplete="off">
        <Form.Item
          label={label ?? t('faceMatch.common.reason')}
          required={!optional}
          validateStatus={invalid ? 'error' : undefined}
          help={invalid && issue ? t(issue.key, issue.params) : undefined}
        >
          <Input.TextArea
            rows={3}
            value={reason}
            maxLength={maxLength}
            showCount
            onChange={(event) => setReason(event.target.value)}
          />
        </Form.Item>
      </Form>
      {errorText ? <Alert type="error" showIcon message={errorText} /> : null}
    </Modal>
  )
}
