import { Alert, Button, Drawer, Form, Input, Select, Spin, Switch, Typography, message } from 'antd'
import { useEffect, useState } from 'react'
import { useTranslation } from 'react-i18next'

import {
  getDeviceRegistrationDetail,
  isDeviceRuntimeOnline,
  updateDeviceRegistration,
} from '../../../services/deviceRegistrationService'
import type {
  DeviceRegistrationDetail,
  DeviceRegistrationItem,
  UpdateDeviceRegistrationPayload,
} from '../../../types/deviceRegistration'

import {
  DeviceStatusPill,
  DeviceTypeTag,
  EMPTY_VALUE,
  OnlineDot,
  RelativeTime,
  formatDateTime,
  formatStoreLabel,
} from './deviceCells'
import { collectDistinctOptions, getDeviceStatusActions, type DeviceStatusAction } from './deviceManagementLogic'
import {
  canEditRegisteredDeviceSystem,
  getRegisteredDeviceSystemEditOptions,
  isEnabledLegacyIosPos,
  supportsTransactionGate,
} from './deviceSystemOptions'

export const DEVICE_TYPE_OPTIONS = ['Mobile', 'PDA', 'POS', 'Admin'] as const

type DeviceEditFormValues = UpdateDeviceRegistrationPayload

interface DeviceDetailDrawerProps {
  /** 列表行：抽屉立即用它渲染，有管理权限时再拉详情填充表单。 */
  device: DeviceRegistrationItem | null
  storeName?: string
  canManage: boolean
  actionLoading: boolean
  onClose: () => void
  onSaved: () => void
  onStatusAction: (device: DeviceRegistrationItem, action: DeviceStatusAction) => void
}

export default function DeviceDetailDrawer({
  device,
  storeName,
  canManage,
  actionLoading,
  onClose,
  onSaved,
  onStatusAction,
}: DeviceDetailDrawerProps) {
  const { t } = useTranslation()
  const [editForm] = Form.useForm<DeviceEditFormValues>()
  const editingDeviceType = Form.useWatch('deviceType', editForm)
  const editingDeviceSystem = Form.useWatch('deviceSystem', editForm)
  const editingSupportsTransactionGate = supportsTransactionGate(editingDeviceSystem, editingDeviceType)
  const [detail, setDetail] = useState<DeviceRegistrationDetail | null>(null)
  const [detailLoading, setDetailLoading] = useState(false)
  const [saving, setSaving] = useState(false)
  const deviceId = device?.id

  // 详情接口要求 Manage 权限；只读用户直接展示列表行，不发请求以免 403。
  useEffect(() => {
    setDetail(null)
    if (deviceId === undefined || !canManage) {
      return
    }

    let cancelled = false
    setDetailLoading(true)
    getDeviceRegistrationDetail(deviceId)
      .then((nextDetail) => {
        if (cancelled) {
          return
        }
        setDetail(nextDetail)
        editForm.setFieldsValue({
          deviceType: nextDetail.deviceType,
          deviceSystem: nextDetail.deviceSystem,
          allowTransactions: nextDetail.allowTransactions,
          remark: nextDetail.remark ?? '',
        })
      })
      .catch((error) => {
        console.error(t('posAdmin.devices.loadDetailFailed'), error)
        message.error(t('posAdmin.devices.loadDetailFailed'))
      })
      .finally(() => {
        if (!cancelled) {
          setDetailLoading(false)
        }
      })

    return () => {
      cancelled = true
    }
  }, [deviceId, canManage])

  // 状态动作执行后列表会刷新，抽屉头部的状态跟随最新列表行；表单字段以详情为准。
  const view: DeviceRegistrationItem | null = device
    ? { ...device, ...(detail ?? {}), status: device.status, statusDescription: device.statusDescription }
    : null

  async function submit() {
    if (!detail) {
      return
    }

    try {
      const values = await editForm.validateFields()
      setSaving(true)
      await updateDeviceRegistration(detail.id, {
        deviceType: values.deviceType,
        deviceSystem: values.deviceSystem,
        allowTransactions: values.allowTransactions,
        remark: values.remark ?? '',
      })
      message.success(t('posAdmin.devices.updateSuccess'))
      onSaved()
    } catch (error) {
      if (typeof error === 'object' && error !== null && 'errorFields' in error) {
        return
      }
      console.error(t('posAdmin.devices.updateFailed'), error)
      message.error(t('posAdmin.devices.updateFailed'))
    } finally {
      setSaving(false)
    }
  }

  const online = view ? isDeviceRuntimeOnline(view) : false
  const statusActions = view ? getDeviceStatusActions(view.status) : null
  const statusActionLabel: Record<DeviceStatusAction, string> = {
    activate: t('posAdmin.devices.enable'),
    disable: t('posAdmin.devices.disable'),
    lock: t('posAdmin.devices.lock'),
  }

  return (
    <Drawer
      open={Boolean(device)}
      onClose={onClose}
      width={520}
      className="dev-mgmt-drawer"
      destroyOnHidden
      closable={false}
      title={
        view ? (
          <div className="dev-mgmt-drawer-head">
            <span className="dev-mgmt-drawer-title dev-mgmt-mono">
              {view.systemDeviceNumber || view.hardwareId || EMPTY_VALUE}
            </span>
            <span className="dev-mgmt-drawer-meta">
              <DeviceStatusPill status={view.status} description={view.statusDescription} t={t} />
              <OnlineDot online={online} t={t} />
              <Typography.Text type="secondary" style={{ fontSize: 12 }}>
                {formatStoreLabel(view.storeCode, storeName ?? view.storeName)}
              </Typography.Text>
            </span>
          </div>
        ) : null
      }
      extra={<Button type="text" onClick={onClose}>{t('common.close')}</Button>}
      footer={
        view && canManage && statusActions ? (
          <div className="dev-mgmt-drawer-footer">
            {[statusActions.primary, ...statusActions.secondary].map((action) => (
              <Button
                key={action}
                danger={action === 'lock'}
                // 「启用」是待确认设备的关键动作，用描边主色突出；与右侧「保存」区分
                type={action === 'activate' ? 'primary' : 'default'}
                ghost={action === 'activate'}
                loading={actionLoading}
                onClick={() => onStatusAction(view, action)}
              >
                {statusActionLabel[action]}
              </Button>
            ))}
            <span className="dev-mgmt-drawer-footer-spacer" />
            <Button onClick={onClose}>{t('common.cancel')}</Button>
            <Button
              type="primary"
              loading={saving}
              disabled={detailLoading || !detail}
              onClick={() => void submit()}
            >
              {t('common.save')}
            </Button>
          </div>
        ) : null
      }
    >
      {view ? (
        <>
          <section className="dev-mgmt-section">
            <div className="dev-mgmt-section-title">{t('posAdmin.devices.mgmt.drawer.sectionBasic')}</div>
            <dl className="dev-mgmt-kv">
              <dt>{t('posAdmin.devices.hardwareId')}</dt>
              <dd>
                {view.hardwareId ? (
                  <Typography.Text className="dev-mgmt-mono" copyable>{view.hardwareId}</Typography.Text>
                ) : EMPTY_VALUE}
              </dd>
              <dt>{t('posAdmin.devices.deviceType')}</dt>
              <dd><DeviceTypeTag value={view.deviceType} /></dd>
              <dt>{t('posAdmin.devices.deviceSystem')}</dt>
              <dd>{view.deviceSystem || EMPTY_VALUE}</dd>
              <dt>{t('posAdmin.devices.allowTransactions')}</dt>
              <dd>
                {supportsTransactionGate(view.deviceSystem, view.deviceType)
                  ? t(view.allowTransactions ? 'posAdmin.devices.transactionsAllowed' : 'posAdmin.devices.transactionsBlocked')
                  : <span className="dev-mgmt-faint">{t('posAdmin.devices.transactionControlNotApplicable')}</span>}
              </dd>
              <dt>{t('column.remarks')}</dt>
              <dd>{view.remark || <span className="dev-mgmt-faint">{EMPTY_VALUE}</span>}</dd>
            </dl>
          </section>

          <section className="dev-mgmt-section">
            <div className="dev-mgmt-section-title">{t('posAdmin.devices.mgmt.drawer.sectionRuntime')}</div>
            <dl className="dev-mgmt-kv">
              <dt>{t('posAdmin.devices.onlineStatus')}</dt>
              <dd><OnlineDot online={online} t={t} /></dd>
              <dt>{t('posAdmin.devices.mgmt.drawer.lastHeartbeat')}</dt>
              <dd>
                <RelativeTime value={view.lastHeartbeatAt} t={t} empty={t('posAdmin.devices.mgmt.heartbeatNever')} />
              </dd>
              <dt>{t('posAdmin.devices.currentCashier')}</dt>
              <dd>
                {online && view.currentCashierName
                  ? view.currentCashierName
                  : <span className="dev-mgmt-faint">{t('posAdmin.devices.cashierNotLoggedIn')}</span>}
              </dd>
              {online && view.currentCashierName ? (
                <>
                  <dt>{t('posAdmin.devices.mgmt.drawer.cashierLoginAt')}</dt>
                  <dd>{formatDateTime(view.cashierLoginAt)}</dd>
                </>
              ) : null}
            </dl>
          </section>

          <section className="dev-mgmt-section">
            <div className="dev-mgmt-section-title">{t('posAdmin.devices.mgmt.drawer.sectionRecord')}</div>
            <dl className="dev-mgmt-kv">
              <dt>{t('column.createTime')}</dt>
              <dd>
                {formatDateTime(view.createdAt)}
                {view.createdBy ? <span className="dev-mgmt-sub"> · {view.createdBy}</span> : null}
              </dd>
              <dt>{t('posAdmin.devices.lastModified')}</dt>
              <dd>
                {formatDateTime(view.lastModified)}
                {view.lastModifiedBy ? <span className="dev-mgmt-sub"> · {view.lastModifiedBy}</span> : null}
              </dd>
            </dl>
          </section>

          {canManage ? (
            <section className="dev-mgmt-section">
              <div className="dev-mgmt-section-title">{t('posAdmin.devices.mgmt.drawer.sectionSettings')}</div>
              <Spin spinning={detailLoading}>
                <Form form={editForm} layout="vertical" requiredMark={false} disabled={!detail}>
                  <div className="dev-mgmt-form-row">
                    <Form.Item
                      name="deviceType"
                      label={t('posAdmin.devices.deviceType')}
                      rules={[{ required: true, message: t('posAdmin.devices.deviceTypeRequired') }]}
                    >
                      <Select
                        disabled={
                          !detail ||
                          isEnabledLegacyIosPos(detail.status, detail.deviceSystem, detail.deviceType)
                        }
                        // 生产里还有 StorePDA、WarehousePDA 等历史类型，当前值不在候选里时补进来，避免显示成裸值
                        options={collectDistinctOptions([detail?.deviceType], DEVICE_TYPE_OPTIONS).map((deviceType) => ({
                          label: <DeviceTypeTag value={deviceType} />,
                          value: deviceType,
                        }))}
                      />
                    </Form.Item>
                    <Form.Item
                      name="deviceSystem"
                      label={t('posAdmin.devices.deviceSystem')}
                      rules={[{ required: true, message: t('posAdmin.devices.deviceSystemRequired') }]}
                    >
                      <Select
                        disabled={
                          !detail ||
                          !canEditRegisteredDeviceSystem(detail.status, detail.deviceSystem, detail.deviceType)
                        }
                        options={(detail
                          ? getRegisteredDeviceSystemEditOptions(detail.status, detail.deviceSystem, detail.deviceType)
                          : []
                        ).map((deviceSystem) => ({ label: deviceSystem, value: deviceSystem }))}
                      />
                    </Form.Item>
                  </div>
                  <Form.Item
                    name="allowTransactions"
                    label={t('posAdmin.devices.allowTransactions')}
                    valuePropName="checked"
                    extra={t(
                      editingSupportsTransactionGate
                        ? 'posAdmin.devices.allowTransactionsHint'
                        : 'posAdmin.devices.allowTransactionsUnsupportedHint'
                    )}
                  >
                    <Switch disabled={!editingSupportsTransactionGate} />
                  </Form.Item>
                  <Form.Item name="remark" label={t('column.remarks')}>
                    <Input.TextArea
                      rows={3}
                      maxLength={500}
                      showCount
                      placeholder={t('posAdmin.devices.remarkPlaceholder')}
                    />
                  </Form.Item>
                </Form>
              </Spin>
            </section>
          ) : (
            <Alert type="info" showIcon message={t('posAdmin.devices.mgmt.drawer.readOnlyHint')} style={{ marginTop: 12 }} />
          )}
        </>
      ) : null}
    </Drawer>
  )
}
