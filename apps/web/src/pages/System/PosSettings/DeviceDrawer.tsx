import { Alert, Button, Drawer, Empty, Skeleton, Space, message } from 'antd'
import type { ColumnsType } from 'antd/es/table'
import { useMemo, useState } from 'react'
import { MeasuredTable } from '../../../components/MeasuredTable'
import { useIsMobile } from '../../../hooks/useIsMobile'
import { getPosSettingDevice, savePosSettingDevice } from '../../../services/posSettingsService'
import type { PosSettingDefinition, PosSettingDeviceStatus, PosSettingStoreSummary } from '../../../types/posSettings'
import ConfirmChangesModal from './ConfirmChangesModal'
import {
  buildSaveValues,
  chooseEffective,
  deviceVisibleDefinitions,
  formatDateTime,
  globalEffective,
  layerEffective,
  reportedState,
  triOf,
} from './logic'
import {
  clientKindLabel,
  DeviceStatusPill,
  EffectiveCell,
  LoadErrorAlert,
  SettingNameCell,
  settingName,
  TriControl,
  ValueTag,
  type Tr,
} from './parts'
import { useRequest } from './useRequest'
import { useSettingsDraft } from './useSettingsDraft'

interface DeviceDrawerProps {
  /** 当前打开的设备；null 表示关闭。seq 每次打开递增，保证每次打开都重新取最新回报。 */
  target: { device: PosSettingDeviceStatus; seq: number } | null
  /** 设备所在门店的覆盖（用来在改动设备覆盖时实时推算「应生效」）。 */
  store: PosSettingStoreSummary | null
  definitions: readonly PosSettingDefinition[]
  globalValues: Readonly<Record<string, boolean>>
  canManage: boolean
  active: boolean
  tr: Tr
  errorText: (error: unknown) => string | null
  onClose: () => void
  /** 保存成功后通知页面刷新设备列表与门店汇总。 */
  onChanged: () => void
}

/**
 * 编辑设备覆盖：每项三态「跟随门店 / 开 / 关」，同时显示「应生效」（服务端三层合并的结果）与
 * 「设备回报」（收银机应用后回传的实际值）；两者不同标「待同步」。
 */
export default function DeviceDrawer({
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
}: DeviceDrawerProps) {
  const isMobile = useIsMobile()
  const deviceCode = target?.device.deviceCode ?? null
  const requestKey = target ? `${target.device.deviceCode}:${target.seq}` : null
  const detailRequest = useRequest(
    requestKey,
    (signal) => getPosSettingDevice(deviceCode as string, signal),
    active && target !== null,
  )
  const detail = detailRequest.data
  // 先用列表里的状态占位，详情到达后用详情里的（更新更准）。
  const status = detail?.status ?? target?.device ?? null
  // 依赖项必须稳定：visible 每次渲染都换新数组会让草稿 effect 反复重置，造成死循环。
  const clientKind = status?.clientKind ?? null
  const detailEffective = detail?.effective
  const visible = useMemo(
    () => (clientKind === null ? [] : deviceVisibleDefinitions(definitions, clientKind, detailEffective ?? {})),
    [clientKind, definitions, detailEffective],
  )
  const { original, draft, changes, setTri, reset } = useSettingsDraft(visible, detail?.overrides)
  const [confirmOpen, setConfirmOpen] = useState(false)
  const loadError = detailRequest.error ? errorText(detailRequest.error) : null

  const save = async () => {
    if (!deviceCode) return
    try {
      await savePosSettingDevice(deviceCode, { values: buildSaveValues(changes) })
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
      title: tr('deviceDrawer.colSetting'),
      key: 'setting',
      render: (_, definition) => <SettingNameCell definition={definition} tr={tr} showClients={false} />,
    },
    {
      title: tr('deviceDrawer.colOwn'),
      key: 'own',
      width: 230,
      render: (_, definition) => (
        <TriControl
          layer="device"
          value={draft[definition.key] ?? 'inherit'}
          onChange={(next) => setTri(definition.key, next)}
          disabled={!canManage}
          label={settingName(definition.key, tr)}
          tr={tr}
        />
      ),
    },
    {
      title: tr('deviceDrawer.colEffective'),
      key: 'effective',
      width: 170,
      render: (_, definition) => <EffectiveCell effective={effectiveOf(definition)} tr={tr} />,
    },
    {
      title: tr('deviceDrawer.colReported'),
      key: 'reported',
      width: 130,
      render: (_, definition) => {
        const { state, value } = reportedState(effectiveOf(definition), detail?.reported ?? {}, definition.key)
        if (state === 'pending') {
          return (
            <span className="pos-settings-reported">
              {value === undefined ? <span className="pos-settings-muted">{tr('deviceDrawer.notReported')}</span> : <ValueTag value={value} tr={tr} />}
              <span className="pos-settings-pending">{tr('deviceDrawer.pendingTag')}</span>
            </span>
          )
        }
        if (state === 'unreported') return <span className="pos-settings-muted">{tr('deviceDrawer.notReported')}</span>
        return <ValueTag value={value} tr={tr} />
      },
    },
  ]

  /** 应生效：没改的项用服务端三层合并的值；改了的项按「草稿 + 门店覆盖 + 全局」实时推算。 */
  function effectiveOf(definition: PosSettingDefinition) {
    const tri = draft[definition.key] ?? 'inherit'
    const storeLevel = layerEffective(
      triOf(store?.overrides[definition.key]),
      'store',
      globalEffective(definition, globalValues),
    )
    return chooseEffective(
      tri,
      original[definition.key] ?? 'inherit',
      detail?.effective[definition.key],
      layerEffective(tri, 'device', storeLevel),
    )
  }

  const bannerType = status?.status === 'applied' ? 'success' : status?.status === 'pending' ? 'warning' : 'info'
  const bannerText = status?.status === 'applied'
    ? tr('deviceDrawer.bannerApplied', { time: formatDateTime(status.appliedAtUtc) })
    : status?.status === 'pending' ? tr('deviceDrawer.bannerPending') : tr('deviceDrawer.bannerNever')

  return (
    <>
      <Drawer
        open={target !== null}
        width={isMobile ? '100%' : 860}
        title={tr('deviceDrawer.title', { code: deviceCode ?? '' })}
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
          {status ? (
            <div className="pos-settings-device-meta">
              <DeviceStatusPill status={status.status} tr={tr} />
              <span className="pos-settings-muted">
                {tr('deviceDrawer.meta', {
                  store: detail?.storeName || store?.storeName || status.storeCode,
                  client: clientKindLabel(status.clientKind, tr),
                  version: status.appVersion ?? '',
                }).trim()}
              </span>
            </div>
          ) : null}
          {loadError ? <LoadErrorAlert message={loadError} tr={tr} onRetry={detailRequest.reload} /> : null}
          {!detail && detailRequest.loading ? <Skeleton active paragraph={{ rows: 8 }} /> : null}
          {detail && status ? (
            <>
              <Alert type={bannerType} showIcon message={bannerText} />
              {status.offlineOverrideCount > 0 ? (
                <Alert
                  type="warning"
                  showIcon
                  message={tr('deviceDrawer.offline', {
                    count: status.offlineOverrideCount,
                    time: formatDateTime(status.lastOfflineOverrideAtUtc),
                  })}
                />
              ) : null}
              <MeasuredTable<PosSettingDefinition>
                metricId="system.pos-settings.device-drawer-table"
                rowKey={(definition) => definition.key}
                size="small"
                pagination={false}
                columns={columns}
                dataSource={visible}
                scroll={{ x: 640 }}
                locale={{ emptyText: <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description={tr('deviceDrawer.noSettings')} /> }}
                rowClassName={(definition) => (definition.isNew ? 'pos-settings-row-new' : '')}
              />
              <p className="pos-settings-footer">{tr('deviceDrawer.footnote')}</p>
            </>
          ) : null}
        </div>
      </Drawer>
      <ConfirmChangesModal
        open={confirmOpen}
        title={tr('confirm.titleDevice', { code: deviceCode ?? '' })}
        layer="device"
        changes={changes}
        tr={tr}
        onConfirm={save}
        onCancel={() => setConfirmOpen(false)}
      />
    </>
  )
}
