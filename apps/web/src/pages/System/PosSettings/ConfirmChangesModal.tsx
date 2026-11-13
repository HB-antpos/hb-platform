import { Alert, Modal } from 'antd'
import { useEffect, useState } from 'react'
import type { SettingChange, SettingLayer } from './logic'
import { settingName, triLabel, type Tr } from './parts'

interface ConfirmChangesModalProps {
  open: boolean
  title: string
  layer: SettingLayer
  changes: readonly SettingChange[]
  tr: Tr
  /** 提交失败时抛出可展示的文案（Error.message）：弹窗保持打开并显示在顶部，改完可以直接重试。 */
  onConfirm: () => Promise<void>
  onCancel: () => void
}

/**
 * 保存前的二次确认：逐项列出「旧 → 新」。提交中不能关闭，避免重复提交。
 * 设置会下发到真实收银机，所以一律先确认，不提供「不再提示」。
 */
export default function ConfirmChangesModal({
  open,
  title,
  layer,
  changes,
  tr,
  onConfirm,
  onCancel,
}: ConfirmChangesModalProps) {
  const [submitting, setSubmitting] = useState(false)
  const [submitError, setSubmitError] = useState<string | null>(null)

  useEffect(() => {
    if (!open) return
    setSubmitting(false)
    setSubmitError(null)
  }, [open])

  const handleOk = async () => {
    if (submitting) return
    setSubmitting(true)
    setSubmitError(null)
    try {
      await onConfirm()
    } catch (error) {
      setSubmitError(error instanceof Error ? error.message : tr('errors.unknown'))
      setSubmitting(false)
    }
  }

  return (
    <Modal
      open={open}
      title={title}
      okText={tr('confirm.okText')}
      cancelText={tr('actions.cancel')}
      confirmLoading={submitting}
      cancelButtonProps={{ disabled: submitting }}
      closable={!submitting}
      maskClosable={false}
      keyboard={!submitting}
      onOk={() => void handleOk()}
      onCancel={() => { if (!submitting) onCancel() }}
      destroyOnClose
    >
      {submitError ? <Alert type="error" showIcon message={submitError} style={{ marginBottom: 12 }} /> : null}
      <p className="pos-settings-confirm-desc">{tr('confirm.description', { count: changes.length })}</p>
      <ul className="pos-settings-confirm-list">
        {changes.map((change) => (
          <li key={change.key}>
            <span className="pos-settings-confirm-name">{settingName(change.key, tr)}</span>
            <span className="pos-settings-confirm-change">
              <span className="pos-settings-confirm-from">{triLabel(layer, change.from, tr)}</span>
              <span aria-hidden="true">{tr('confirm.arrow')}</span>
              <strong>{triLabel(layer, change.to, tr)}</strong>
            </span>
          </li>
        ))}
      </ul>
    </Modal>
  )
}
