import { Empty, Select } from 'antd'
import type { ColumnsType } from 'antd/es/table'
import { useMemo, useRef, useState } from 'react'
import { MeasuredTable } from '../../../components/MeasuredTable'
import { listPosSettingDevices } from '../../../services/posSettingsService'
import type { PosSettingDefinition, PosSettingDeviceStatus, PosSettingStoreSummary } from '../../../types/posSettings'
import DeviceDrawer from './DeviceDrawer'
import { EMPTY_CELL, formatDateTime } from './logic'
import { clientKindLabel, DeviceStatusPill, LoadErrorAlert, type Tr } from './parts'
import { useRequest } from './useRequest'

interface DevicesTabProps {
  definitions: readonly PosSettingDefinition[]
  globalValues: Readonly<Record<string, boolean>>
  stores: readonly PosSettingStoreSummary[]
  canManage: boolean
  active: boolean
  tr: Tr
  errorText: (error: unknown) => string | null
  /** 设备覆盖保存成功后刷新门店汇总（设备覆盖数、已应用进度）。 */
  onChanged: () => void
}

/** 「设备」页签：先选门店（默认第一个可见门店），列出该店设备；点一行打开右侧抽屉编辑设备覆盖。 */
export default function DevicesTab({
  definitions,
  globalValues,
  stores,
  canManage,
  active,
  tr,
  errorText,
  onChanged,
}: DevicesTabProps) {
  const [selected, setSelected] = useState<string | null>(null)
  // 选中的门店不在可见列表里（权限变化、列表刷新）就回到第一个。
  const storeCode = selected && stores.some((store) => store.storeCode === selected) ? selected : (stores[0]?.storeCode ?? null)
  const store = stores.find((item) => item.storeCode === storeCode) ?? null
  const [drawer, setDrawer] = useState<{ device: PosSettingDeviceStatus; seq: number } | null>(null)
  const seqRef = useRef(0)

  const listRequest = useRequest(
    storeCode,
    (signal) => listPosSettingDevices(storeCode as string, signal),
    active && storeCode !== null,
  )
  const devices = listRequest.data ?? []
  const loadError = listRequest.error ? errorText(listRequest.error) : null
  const storeOptions = useMemo(
    () => stores.map((item) => ({ value: item.storeCode, label: `${item.storeName} (${item.storeCode})` })),
    [stores],
  )

  const columns: ColumnsType<PosSettingDeviceStatus> = [
    { title: tr('devices.colCode'), dataIndex: 'deviceCode', key: 'deviceCode', render: (code: string) => <strong>{code}</strong> },
    { title: tr('devices.colClient'), key: 'client', width: 100, render: (_, device) => clientKindLabel(device.clientKind, tr) },
    { title: tr('devices.colVersion'), key: 'version', width: 100, render: (_, device) => device.appVersion ?? EMPTY_CELL },
    { title: tr('devices.colStatus'), key: 'status', width: 130, render: (_, device) => <DeviceStatusPill status={device.status} tr={tr} /> },
    {
      title: tr('devices.colOverride'),
      key: 'override',
      width: 100,
      render: (_, device) => (device.hasDeviceOverride
        ? <span className="pos-settings-override-flag">{tr('devices.overridden')}</span>
        : <span className="pos-settings-muted">{EMPTY_CELL}</span>),
    },
    {
      title: tr('devices.colOffline'),
      key: 'offline',
      width: 150,
      render: (_, device) => (device.offlineOverrideCount > 0
        ? (
          <span className="pos-settings-cell-stack">
            <span>{tr('devices.offlineTimes', { count: device.offlineOverrideCount })}</span>
            <span className="pos-settings-muted">{tr('devices.offlineLast', { time: formatDateTime(device.lastOfflineOverrideAtUtc) })}</span>
          </span>
        )
        : <span className="pos-settings-muted">{EMPTY_CELL}</span>),
    },
    {
      title: tr('devices.colAppliedAt'),
      key: 'appliedAt',
      width: 150,
      render: (_, device) => <span className="pos-settings-muted">{formatDateTime(device.appliedAtUtc)}</span>,
    },
  ]

  if (stores.length === 0) {
    return <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description={tr('devices.noStores')} />
  }

  return (
    <div className="pos-settings-stack">
      <div className="pos-settings-toolbar">
        <label className="pos-settings-field">
          <span className="pos-settings-field-label">{tr('devices.storeLabel')}</span>
          <Select
            className="pos-settings-store-select"
            showSearch
            optionFilterProp="label"
            placeholder={tr('devices.storePlaceholder')}
            value={storeCode}
            options={storeOptions}
            onChange={(value) => setSelected(value)}
          />
        </label>
      </div>
      {loadError ? <LoadErrorAlert message={loadError} tr={tr} onRetry={listRequest.reload} /> : null}
      <section className="pos-settings-panel" aria-label={tr('devices.tableLabel')}>
        <div className="pos-settings-panel-head">
          <h2 className="pos-settings-panel-title">{tr('devices.title')}</h2>
          <span className="pos-settings-note">{tr('devices.subtitle')}</span>
        </div>
        <MeasuredTable<PosSettingDeviceStatus>
          metricId="system.pos-settings.devices-table"
          className="pos-settings-clickable-table"
          rowKey={(device) => device.deviceCode}
          size="small"
          pagination={false}
          loading={listRequest.loading}
          columns={columns}
          dataSource={devices}
          scroll={{ x: 820 }}
          locale={{ emptyText: <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description={tr('devices.empty')} /> }}
          onRow={(device) => ({
            onClick: () => {
              // 每次打开都用新的 seq，保证抽屉重新取最新回报。
              seqRef.current += 1
              setDrawer({ device, seq: seqRef.current })
            },
          })}
        />
      </section>
      <DeviceDrawer
        target={drawer}
        store={store}
        definitions={definitions}
        globalValues={globalValues}
        canManage={canManage}
        active={active}
        tr={tr}
        errorText={errorText}
        onClose={() => setDrawer(null)}
        onChanged={() => {
          listRequest.reload()
          onChanged()
        }}
      />
    </div>
  )
}
