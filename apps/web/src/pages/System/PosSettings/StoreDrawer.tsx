import { Alert, Button, Drawer, Empty, Skeleton, Space, Typography, message } from 'antd'
import type { ColumnsType } from 'antd/es/table'
import { useState } from 'react'
import { MeasuredTable } from '../../../components/MeasuredTable'
import { useIsMobile } from '../../../hooks/useIsMobile'
import { getPosSettingStore, savePosSettingStore } from '../../../services/posSettingsService'
import type { PosSettingDefinition, PosSettingDeviceStatus, PosSettingStoreSummary } from '../../../types/posSettings'
import ConfirmChangesModal from './ConfirmChangesModal'
import {
  buildSaveValues,
  chooseEffective,
  formatDateTime,
  globalEffective,
  layerEffective,
} from './logic'
import {
  clientKindLabel,
  DeviceStatusPill,
  EffectiveCell,
  LoadErrorAlert,
  SettingNameCell,
  settingName,
  TriControl,
  type Tr,
} from './parts'
import { useRequest } from './useRequest'
import { useSettingsDraft } from './useSettingsDraft'

interface StoreDrawerProps {
  /** 当前打开的门店；null 表示关闭。seq 每次打开递增，保证每次打开都重新取最新数据。 */
  target: { storeCode: string; seq: number } | null
  store: PosSettingStoreSummary | null
  definitions: readonly PosSettingDefinition[]
  globalValues: Readonly<Record<string, boolean>>
  canManage: boolean
  active: boolean
  tr: Tr
  errorText: (error: unknown) => string | null
  onClose: () => void
  /** 保存成功后通知页面刷新门店列表。 */
  onChanged: () => void
}

/**
 * 编辑门店覆盖：每项三态「跟随全局 / 开 / 关」，右侧实时显示这家店的最终值与来源；
 * 下方列出本店设备的应用情况（保存后能看到是否已应用）。
 */
export default function StoreDrawer({
  target,
  store,
  definitions,
  globalValues,
  canManage,
  active,
  tr,
  errorText,
  onClose,
  onChanged,
}: StoreDrawerProps) {
  const isMobile = useIsMobile()
  const requestKey = target ? `${target.storeCode}:${target.seq}` : null
  const detailRequest = useRequest(
    requestKey,
    (signal) => getPosSettingStore(target?.storeCode as string, signal),
    active && target !== null,
  )
  const detail = detailRequest.data
  const { original, draft, changes, setTri, reset } = useSettingsDraft(definitions, detail?.overrides)
  const [confirmOpen, setConfirmOpen] = useState(false)
  const loadError = detailRequest.error ? errorText(detailRequest.error) : null
  const storeName = detail?.storeName || store?.storeName || target?.storeCode || ''

  const save = async () => {
    if (!target) return
    try {
      await savePosSettingStore(target.storeCode, { values: buildSaveValues(changes) })
    } catch (error) {
      throw new Error(errorText(error) ?? tr('errors.unknown'))
    }
    message.success(tr('saved'))
    setConfirmOpen(false)
    detailRequest.reload()
    onChanged()
  }

  const columns: ColumnsType<PosSettingDefinition> = [
    {
      title: tr('storeDrawer.colSetting'),
      key: 'setting',
      render: (_, definition) => <SettingNameCell definition={definition} tr={tr} />,
    },
    {
      title: tr('storeDrawer.colOwn'),
      key: 'own',
      width: 230,
      render: (_, definition) => (
        <TriControl
          layer="store"
          value={draft[definition.key] ?? 'inherit'}
          onChange={(next) => setTri(definition.key, next)}
          disabled={!canManage}
          label={settingName(definition.key, tr)}
          tr={tr}
        />
      ),
    },
    {
      title: tr('storeDrawer.colEffective'),
      key: 'effective',
      width: 170,
      render: (_, definition) => {
        const tri = draft[definition.key] ?? 'inherit'
        // 没改的项用服务端算好的值；改了的项按草稿 + 当前全局值实时推算。
        const computed = layerEffective(tri, 'store', globalEffective(definition, globalValues))
        return (
          <EffectiveCell
            effective={chooseEffective(tri, original[definition.key] ?? 'inherit', detail?.effective[definition.key], computed)}
            tr={tr}
          />
        )
      },
    },
  ]

  const deviceColumns: ColumnsType<PosSettingDeviceStatus> = [
    { title: tr('storeDrawer.colDevice'), dataIndex: 'deviceCode', key: 'deviceCode' },
    {
      title: tr('storeDrawer.colType'),
      key: 'type',
      render: (_, device) => `${clientKindLabel(device.clientKind, tr)} ${device.appVersion ?? ''}`.trim(),
    },
    {
      title: tr('storeDrawer.colStatus'),
      key: 'status',
      render: (_, device) => <DeviceStatusPill status={device.status} tr={tr} />,
    },
    {
      title: tr('storeDrawer.colAppliedAt'),
      key: 'appliedAt',
      render: (_, device) => <span className="pos-settings-muted">{formatDateTime(device.appliedAtUtc)}</span>,
    },
  ]

  return (
    <>
      <Drawer
        open={target !== null}
        width={isMobile ? '100%' : 820}
        title={tr('storeDrawer.title', { name: storeName, code: target?.storeCode ?? '' })}
        onClose={onClose}
        destroyOnClose
        footer={canManage ? (
          <Space className="pos-settings-drawer-footer">
            <Button onClick={changes.length > 0 ? reset : onClose}>
              {changes.length > 0 ? tr('actions.reset') : tr('actions.cancel')}
            </Button>
            <Button type="primary" disabled={changes.length === 0 || !detail} onClick={() => setConfirmOpen(true)}>
              {tr('actions.save')}
            </Button>
          </Space>
        ) : undefined}
      >
        <div className="pos-settings-drawer-body">
          {loadError ? <LoadErrorAlert message={loadError} tr={tr} onRetry={detailRequest.reload} /> : null}
          {!detail && detailRequest.loading ? <Skeleton active paragraph={{ rows: 8 }} /> : null}
          {detail ? (
            <>
              <Alert type="info" showIcon message={tr('storeDrawer.info')} />
              {detail.updatedAtUtc ? (
                <Typography.Text type="secondary">
                  {tr('storeDrawer.updatedBy', { time: formatDateTime(detail.updatedAtUtc), name: detail.updatedByName ?? '—' })}
                </Typography.Text>
              ) : null}
              <MeasuredTable<PosSettingDefinition>
                metricId="system.pos-settings.store-drawer-table"
                rowKey={(definition) => definition.key}
                size="small"
                pagination={false}
                columns={columns}
                dataSource={[...definitions]}
                scroll={{ x: 560 }}
                rowClassName={(definition) => (definition.isNew ? 'pos-settings-row-new' : '')}
              />
              <h3 className="pos-settings-section-title">{tr('storeDrawer.devicesTitle')}</h3>
              <MeasuredTable<PosSettingDeviceStatus>
                metricId="system.pos-settings.store-drawer-devices"
                rowKey={(device) => device.deviceCode}
                size="small"
                pagination={false}
                columns={deviceColumns}
                dataSource={detail.devices}
                scroll={{ x: 480 }}
                locale={{ emptyText: <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description={tr('storeDrawer.noDevices')} /> }}
              />
            </>
          ) : null}
        </div>
      </Drawer>
      <ConfirmChangesModal
        open={confirmOpen}
        title={tr('confirm.titleStore', { name: storeName })}
        layer="store"
        changes={changes}
        tr={tr}
        onConfirm={save}
        onCancel={() => setConfirmOpen(false)}
      />
    </>
  )
}
