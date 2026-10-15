import { Alert, Input, Modal, Space, Typography } from 'antd'
import { useEffect, useState } from 'react'
import { useTranslation } from 'react-i18next'

import { CANCEL_NOTE_MAX_LENGTH } from './logic'

const I18N = 'warehouse.priceUpdateTasks.cancel'

interface CancelTasksModalProps {
  open: boolean
  /** 本次撤销范围的说明（几个任务 / 哪个商品几家分店）。 */
  description: string
  loading: boolean
  onConfirm: (note: string) => void
  onClose: () => void
}

/** 撤销未完成任务的确认框：写明「只取消提醒、不改价格、不可恢复」，并收集可选备注。 */
export default function CancelTasksModal({ open, description, loading, onConfirm, onClose }: CancelTasksModalProps) {
  const { t } = useTranslation()
  const [note, setNote] = useState('')

  // 每次重新打开都从空备注开始，避免带上上一次的内容。
  useEffect(() => {
    if (open) setNote('')
  }, [open])

  return (
    <Modal
      open={open}
      title={t(`${I18N}.modalTitle`)}
      okText={t(`${I18N}.confirm`)}
      okButtonProps={{ danger: true, loading }}
      cancelText={t('common.cancel', '取消')}
      cancelButtonProps={{ disabled: loading }}
      maskClosable={false}
      keyboard={!loading}
      onOk={() => onConfirm(note)}
      onCancel={onClose}
    >
      <Space direction="vertical" size={12} style={{ width: '100%' }}>
        <Typography.Text>{description}</Typography.Text>
        <Alert type="warning" showIcon message={t(`${I18N}.warning`)} />
        <div>
          <Typography.Text type="secondary">{t(`${I18N}.noteLabel`)}</Typography.Text>
          <Input.TextArea
            value={note}
            maxLength={CANCEL_NOTE_MAX_LENGTH}
            showCount
            autoSize={{ minRows: 2, maxRows: 4 }}
            placeholder={t(`${I18N}.notePlaceholder`)}
            onChange={(event) => setNote(event.target.value)}
          />
        </div>
      </Space>
    </Modal>
  )
}
