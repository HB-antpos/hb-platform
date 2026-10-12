import { CopyOutlined, EyeInvisibleOutlined, EyeOutlined } from '@ant-design/icons'
import { Alert, Button, Input, Modal, Space, Typography, message } from 'antd'
import type { TextAreaRef } from 'antd/es/input/TextArea'
import { useRef, useState } from 'react'
import { useTranslation } from 'react-i18next'

import { copyTextToClipboard, maskCameraUrl, type SecretRevealState } from './logic'

interface SecretRevealModalProps {
  /** 只在 state.open 为 true 时由父组件挂载；关闭时父组件把状态置回已清空并卸载本组件。 */
  state: Extract<SecretRevealState, { open: true }>
  onClose: () => void
}

/**
 * 一次性展示新增 / 轮换后的摄像机地址（含 token）。
 * 约定：地址只存在于父组件的局部 state 和本组件的 props 里；这里不写 localStorage / sessionStorage / 地址栏 / console，
 * 不把地址放进 message / 通知文案；关闭由父组件清空 state。
 */
export default function SecretRevealModal({ state, onClose }: SecretRevealModalProps) {
  const { t } = useTranslation()
  const inputRef = useRef<TextAreaRef>(null)
  const [hidden, setHidden] = useState(false)

  const handleCopy = async () => {
    const copied = await copyTextToClipboard(state.cameraUrl)
    if (copied) {
      void message.success(t('faceCameras.secret.copied'))
      return
    }
    // 降级：剪贴板不可用时取消隐藏并选中全文，提示用户手动复制。
    setHidden(false)
    window.setTimeout(() => inputRef.current?.resizableTextArea?.textArea.select(), 0)
    void message.warning(t('faceCameras.secret.copyFailed'))
  }

  return (
    <Modal
      open
      width={640}
      title={state.kind === 'created' ? t('faceCameras.secret.titleCreated') : t('faceCameras.secret.titleRotated')}
      // 必须点「我已保存」才能关闭：避免误点遮罩 / 按 Esc 把只显示一次的地址丢掉。
      closable={false}
      maskClosable={false}
      keyboard={false}
      destroyOnHidden
      cancelButtonProps={{ style: { display: 'none' } }}
      okText={t('faceCameras.secret.close')}
      onOk={onClose}
    >
      <Space direction="vertical" size={12} style={{ width: '100%', marginTop: 8 }}>
        <Alert type="warning" showIcon message={t('faceCameras.secret.onceOnly')} />
        {state.kind === 'rotated' ? (
          <Typography.Text type="secondary">{t('faceCameras.secret.rotatedNote', { cameraId: state.cameraId })}</Typography.Text>
        ) : (
          <Typography.Text type="secondary">{t('faceCameras.secret.createdNote', { cameraId: state.cameraId })}</Typography.Text>
        )}
        <div>
          <Typography.Text strong>{t('faceCameras.secret.urlLabel')}</Typography.Text>
          {/* 地址约 90 个字符：用自适应高度的只读文本域完整显示（含末尾 token），方便核对与手动复制。 */}
          <Input.TextArea
            ref={inputRef}
            readOnly
            autoSize={{ minRows: 2, maxRows: 4 }}
            // 隐藏时文本域里只放掩码文本，真实地址仅在复制按钮的闭包里。
            value={hidden ? maskCameraUrl(state.cameraUrl) : state.cameraUrl}
            onFocus={(event) => event.target.select()}
            autoComplete="off"
            spellCheck={false}
            aria-label={t('faceCameras.secret.urlLabel')}
            style={{ marginTop: 4, fontFamily: 'ui-monospace, SFMono-Regular, Menlo, monospace', resize: 'none' }}
          />
          <Space style={{ marginTop: 8 }}>
            <Button type="primary" icon={<CopyOutlined />} onClick={() => void handleCopy()}>
              {t('faceCameras.secret.copy')}
            </Button>
            <Button icon={hidden ? <EyeOutlined /> : <EyeInvisibleOutlined />} onClick={() => setHidden((value) => !value)}>
              {hidden ? t('faceCameras.secret.show') : t('faceCameras.secret.hide')}
            </Button>
          </Space>
        </div>
        <Typography.Text type="secondary">{t('faceCameras.secret.usage')}</Typography.Text>
      </Space>
    </Modal>
  )
}
