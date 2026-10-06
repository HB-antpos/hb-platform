import { Alert, Button, Descriptions, Form, Input, Modal, Popconfirm, QRCode, Space, Spin, Tag, Typography, message } from 'antd'
import { useEffect, useState } from 'react'
import { useTranslation } from 'react-i18next'

import {
  createEmergencyLoginGrant,
  getEmergencyLoginGrant,
  revokeEmergencyLoginGrant,
} from '../../../services/deviceRegistrationService'
import type { EmergencyLoginGrantSummary } from '../../../types/deviceRegistration'

import { EMPTY_VALUE, formatDateTime } from './deviceCells'

type EmergencyGrantFormValues = { reason: string }

interface EmergencyLoginModalProps {
  open: boolean
  storeCode?: string
  storeLabel: string
  onClose: () => void
}

/** 门店紧急登录授权弹窗：行为与旧版一致，只是从设备管理页主体拆出。 */
export default function EmergencyLoginModal({ open, storeCode, storeLabel, onClose }: EmergencyLoginModalProps) {
  const { t } = useTranslation()
  const [emergencyForm] = Form.useForm<EmergencyGrantFormValues>()
  const [emergencyLoading, setEmergencyLoading] = useState(false)
  const [emergencySaving, setEmergencySaving] = useState(false)
  const [emergencyRevoking, setEmergencyRevoking] = useState(false)
  const [emergencyGrant, setEmergencyGrant] = useState<EmergencyLoginGrantSummary | null>(null)
  const [emergencyToken, setEmergencyToken] = useState<string | null>(null)

  useEffect(() => {
    if (!open || !storeCode) {
      return
    }

    let cancelled = false
    setEmergencyLoading(true)
    setEmergencyGrant(null)
    setEmergencyToken(null)
    emergencyForm.resetFields()
    getEmergencyLoginGrant(storeCode)
      .then((grant) => {
        if (!cancelled) {
          setEmergencyGrant(grant)
        }
      })
      .catch((error) => {
        console.error(t('posAdmin.devices.emergencyLoadFailed'), error)
        message.error(t('posAdmin.devices.emergencyLoadFailed'))
      })
      .finally(() => {
        if (!cancelled) {
          setEmergencyLoading(false)
        }
      })

    return () => {
      cancelled = true
    }
  }, [open, storeCode])

  function close() {
    setEmergencyGrant(null)
    setEmergencyToken(null)
    emergencyForm.resetFields()
    onClose()
  }

  async function submitEmergencyGrant() {
    if (!storeCode) {
      return
    }

    try {
      const values = await emergencyForm.validateFields()
      setEmergencySaving(true)
      const result = await createEmergencyLoginGrant(storeCode, values.reason)
      setEmergencyGrant(result.grant)
      setEmergencyToken(result.token)
      message.success(t('posAdmin.devices.emergencyCreateSuccess'))
    } catch (error) {
      if (typeof error === 'object' && error !== null && 'errorFields' in error) {
        return
      }
      console.error(t('posAdmin.devices.emergencyCreateFailed'), error)
      message.error(t('posAdmin.devices.emergencyCreateFailed'))
    } finally {
      setEmergencySaving(false)
    }
  }

  async function revokeEmergencyGrant() {
    if (!emergencyGrant) {
      return
    }

    try {
      setEmergencyRevoking(true)
      const revoked = await revokeEmergencyLoginGrant(
        emergencyGrant.grantId,
        t('posAdmin.devices.emergencyRevokeReason')
      )
      setEmergencyGrant(revoked)
      setEmergencyToken(null)
      message.success(t('posAdmin.devices.emergencyRevokeSuccess'))
    } catch (error) {
      console.error(t('posAdmin.devices.emergencyRevokeFailed'), error)
      message.error(t('posAdmin.devices.emergencyRevokeFailed'))
    } finally {
      setEmergencyRevoking(false)
    }
  }

  async function copyEmergencyToken() {
    if (!emergencyToken) {
      return
    }
    try {
      await navigator.clipboard.writeText(emergencyToken)
      message.success(t('posAdmin.devices.emergencyCopySuccess'))
    } catch (error) {
      console.error(t('posAdmin.devices.emergencyCopyFailed'), error)
      message.error(t('posAdmin.devices.emergencyCopyFailed'))
    }
  }

  function downloadEmergencyQrCode() {
    const canvas = document.querySelector<HTMLCanvasElement>('#emergency-login-qr canvas')
    if (!canvas || !emergencyGrant) {
      message.error(t('posAdmin.devices.emergencyDownloadFailed'))
      return
    }

    const link = document.createElement('a')
    link.download = `hbpos-emergency-${emergencyGrant.storeCode}-${emergencyGrant.businessDate}.png`
    link.href = canvas.toDataURL('image/png')
    link.click()
  }

  return (
    <Modal
      open={open}
      title={t('posAdmin.devices.emergencyTitle')}
      onCancel={close}
      destroyOnHidden
      width={680}
      footer={
        emergencyToken ? (
          <Space>
            <Button onClick={() => void copyEmergencyToken()}>
              {t('posAdmin.devices.emergencyCopy')}
            </Button>
            <Button onClick={downloadEmergencyQrCode}>
              {t('posAdmin.devices.emergencyDownload')}
            </Button>
            <Button type="primary" onClick={close}>
              {t('common.close')}
            </Button>
          </Space>
        ) : emergencyGrant?.status === 'Active' ? (
          <Space>
            <Popconfirm
              title={t('posAdmin.devices.emergencyRevokeConfirm')}
              onConfirm={() => void revokeEmergencyGrant()}
            >
              <Button danger loading={emergencyRevoking}>
                {t('posAdmin.devices.emergencyRevoke')}
              </Button>
            </Popconfirm>
            <Button onClick={close}>{t('common.close')}</Button>
          </Space>
        ) : (
          <Space>
            <Button onClick={close}>{t('common.cancel')}</Button>
            <Button
              danger
              type="primary"
              loading={emergencySaving}
              disabled={emergencyLoading}
              onClick={() => void submitEmergencyGrant()}
            >
              {t('posAdmin.devices.emergencyCreate')}
            </Button>
          </Space>
        )
      }
    >
      <Spin spinning={emergencyLoading}>
        <Space direction="vertical" size={16} style={{ width: '100%' }}>
          <Alert
            type="warning"
            showIcon
            message={t('posAdmin.devices.emergencyWarningTitle')}
            description={t('posAdmin.devices.emergencyWarningDescription')}
          />
          <Descriptions bordered column={1} size="small">
            <Descriptions.Item label={t('column.store')}>
              {storeCode ? storeLabel : EMPTY_VALUE}
            </Descriptions.Item>
            {emergencyGrant ? (
              <>
                <Descriptions.Item label={t('posAdmin.devices.emergencyStatus')}>
                  <Tag color={emergencyGrant.status === 'Active' ? 'red' : 'default'}>
                    {t(`posAdmin.devices.emergencyStatuses.${emergencyGrant.status}`)}
                  </Tag>
                </Descriptions.Item>
                <Descriptions.Item label={t('posAdmin.devices.emergencyGrantId')}>
                  <Typography.Text copyable>{emergencyGrant.grantId}</Typography.Text>
                </Descriptions.Item>
                <Descriptions.Item label={t('posAdmin.devices.emergencyExpiresAt')}>
                  {formatDateTime(emergencyGrant.expiresAtUtc)}
                </Descriptions.Item>
                <Descriptions.Item label={t('posAdmin.devices.emergencyReason')}>
                  {emergencyGrant.reason || EMPTY_VALUE}
                </Descriptions.Item>
              </>
            ) : null}
          </Descriptions>

          {emergencyToken ? (
            <Space direction="vertical" align="center" size={12} style={{ width: '100%' }}>
              <div id="emergency-login-qr">
                <QRCode value={emergencyToken} size={320} errorLevel="M" />
              </div>
              <Alert
                type="info"
                showIcon
                message={t('posAdmin.devices.emergencyTokenOneTime')}
              />
            </Space>
          ) : emergencyGrant?.status === 'Active' ? (
            <Alert type="info" showIcon message={t('posAdmin.devices.emergencyActiveSummary')} />
          ) : (
            <Form form={emergencyForm} layout="vertical">
              <Form.Item
                name="reason"
                label={t('posAdmin.devices.emergencyReason')}
                rules={[
                  { required: true, message: t('posAdmin.devices.emergencyReasonRequired') },
                  { max: 200, message: t('posAdmin.devices.emergencyReasonTooLong') },
                ]}
              >
                <Input.TextArea rows={3} maxLength={200} showCount />
              </Form.Item>
            </Form>
          )}
        </Space>
      </Spin>
    </Modal>
  )
}
