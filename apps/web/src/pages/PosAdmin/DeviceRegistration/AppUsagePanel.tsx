import { ReloadOutlined, SearchOutlined } from '@ant-design/icons'
import { Button, Input, Select, Tag, Tooltip, Typography, message } from 'antd'
import type { ColumnsType } from 'antd/es/table'
import { useEffect, useMemo, useRef, useState } from 'react'
import { useTranslation } from 'react-i18next'

import { MeasuredTable } from '../../../components/MeasuredTable'
import { getAppDeviceStatuses, getAppDeviceStatusSummary } from '../../../services/deviceRegistrationService'
import type {
  AppDeviceOnlineState,
  AppDeviceStatus,
  AppDeviceStatusSummary,
  StoreOption,
} from '../../../types/deviceRegistration'
import { createLatestRequestGuard, runLatestGuardedRequest } from '../../../utils/latestRequestGuard'

import { EMPTY_VALUE, OnlineDot, RelativeTime, StoreCell } from './deviceCells'
import { APP_DEVICE_SYSTEM_OPTIONS } from './deviceSystemOptions'

const APP_USAGE_PAGE_SIZE = 200

const APP_UPDATE_SOURCE_COLOR_MAP: Record<string, string> = {
  ota: 'green',
  embedded: 'blue',
  unknown: 'default',
}

const EMPTY_SUMMARY: AppDeviceStatusSummary = {
  total: 0,
  online: 0,
  offline: 0,
  android: 0,
  ios: 0,
  unknownSystem: 0,
}

function getUpdateTail(updateId: string) {
  return updateId.length <= 10 ? updateId : `…${updateId.slice(-10)}`
}

function getAppPackageVersion(item: AppDeviceStatus) {
  if (item.appVersion && item.appBuildVersion) {
    return `${item.appVersion} (${item.appBuildVersion})`
  }
  return item.appVersion || item.appBuildVersion || EMPTY_VALUE
}

interface AppUsagePanelProps {
  stores: StoreOption[]
  storeNameMap: Record<string, string>
  selectedStoreCode?: string
  onStoreChange: (storeCode?: string) => void
}

export default function AppUsagePanel({ stores, storeNameMap, selectedStoreCode, onStoreChange }: AppUsagePanelProps) {
  const { t } = useTranslation()
  const [items, setItems] = useState<AppDeviceStatus[]>([])
  const [summary, setSummary] = useState<AppDeviceStatusSummary>(EMPTY_SUMMARY)
  const [loading, setLoading] = useState(false)
  const [deviceSystem, setDeviceSystem] = useState<string>()
  const [onlineState, setOnlineState] = useState<AppDeviceOnlineState>('all')
  const [keywordInput, setKeywordInput] = useState('')
  const [keyword, setKeyword] = useState('')
  const requestGuardRef = useRef(createLatestRequestGuard())

  function load() {
    return runLatestGuardedRequest(
      requestGuardRef.current,
      () =>
        Promise.all([
          getAppDeviceStatuses({
            page: 1,
            pageSize: APP_USAGE_PAGE_SIZE,
            storeCode: selectedStoreCode,
            deviceSystem,
            onlineState,
            keyword,
          }),
          getAppDeviceStatusSummary({ storeCode: selectedStoreCode, deviceSystem, keyword }),
        ]),
      {
        onStart: () => setLoading(true),
        onSuccess: ([list, nextSummary]) => {
          setItems(list.devices)
          setSummary(nextSummary)
        },
        onError: (error) => {
          console.error(t('posAdmin.devices.appUsageLoadFailed'), error)
          message.error(t('posAdmin.devices.appUsageLoadFailed'))
        },
        onSettled: () => setLoading(false),
      },
    )
  }

  useEffect(() => {
    void load()
  }, [selectedStoreCode, deviceSystem, onlineState, keyword])

  useEffect(() => () => requestGuardRef.current.invalidate(), [])

  const columns = useMemo<ColumnsType<AppDeviceStatus>>(() => [
    {
      title: t('posAdmin.devices.mgmt.columns.device'),
      key: 'device',
      width: 220,
      render: (_value, record) => (
        <div className="dev-mgmt-two">
          <span className="dev-mgmt-strong dev-mgmt-mono">{record.systemDeviceNumber || record.hardwareId}</span>
          {record.systemDeviceNumber ? (
            <span className="dev-mgmt-sub dev-mgmt-mono dev-mgmt-ellipsis" title={record.hardwareId}>
              {record.hardwareId}
            </span>
          ) : null}
        </div>
      ),
    },
    {
      title: t('column.store'),
      dataIndex: 'storeCode',
      width: 170,
      render: (value: string | undefined) => <StoreCell storeCode={value} storeName={value ? storeNameMap[value] : undefined} />,
    },
    {
      title: t('posAdmin.devices.mgmt.columns.runtime'),
      key: 'runtime',
      width: 150,
      render: (_value, record) => (
        <div className="dev-mgmt-two">
          <OnlineDot online={record.isOnline} t={t} />
          <span className="dev-mgmt-sub">
            <RelativeTime value={record.lastSeenAtUtc} t={t} />
          </span>
        </div>
      ),
    },
    {
      title: t('posAdmin.devices.deviceSystem'),
      key: 'system',
      width: 100,
      render: (_value, record) => record.deviceSystem || record.platform || <span className="dev-mgmt-faint">{EMPTY_VALUE}</span>,
    },
    {
      title: t('posAdmin.devices.mgmt.columns.version'),
      key: 'version',
      width: 190,
      render: (_value, record) => (
        <div className="dev-mgmt-two">
          <span className="dev-mgmt-mono">{getAppPackageVersion(record)}</span>
          {record.runtimeVersion || record.channel ? (
            <span className="dev-mgmt-sub dev-mgmt-ellipsis">
              {[record.runtimeVersion, record.channel].filter(Boolean).join(' · ')}
            </span>
          ) : null}
        </div>
      ),
    },
    {
      title: t('posAdmin.devices.mgmt.columns.update'),
      key: 'update',
      width: 180,
      render: (_value, record) => {
        const source = record.updateSource?.trim()
        const normalized = source?.toLowerCase()
        const updateId = record.updateId?.trim()
        if (!source && !updateId) {
          return <span className="dev-mgmt-faint">{EMPTY_VALUE}</span>
        }
        return (
          <span className="dev-mgmt-inline">
            {source ? (
              <Tag color={normalized ? APP_UPDATE_SOURCE_COLOR_MAP[normalized] ?? 'default' : 'default'}>
                {normalized && normalized in APP_UPDATE_SOURCE_COLOR_MAP
                  ? t(`posAdmin.devices.appUpdateSources.${normalized}`)
                  : source}
              </Tag>
            ) : null}
            {updateId ? (
              <Tooltip title={updateId}>
                <Typography.Text className="dev-mgmt-mono dev-mgmt-sub" copyable={{ text: updateId }}>
                  {getUpdateTail(updateId)}
                </Typography.Text>
              </Tooltip>
            ) : null}
          </span>
        )
      },
    },
    {
      title: t('posAdmin.devices.mgmt.columns.lastUser'),
      key: 'lastUser',
      width: 170,
      render: (_value, record) => {
        const user = record.lastSeenUserFullName || record.lastSeenUsername || record.lastSeenUserGuid
        if (!user) {
          return <span className="dev-mgmt-faint">{t('posAdmin.devices.appNoRecentUser')}</span>
        }
        return (
          <div className="dev-mgmt-two">
            <span className="dev-mgmt-ellipsis" title={user}>{user}</span>
            {record.lastAuthMode ? <span className="dev-mgmt-sub">{record.lastAuthMode}</span> : null}
          </div>
        )
      },
    },
  ], [storeNameMap, t])

  const stats = [
    { key: 'total', label: t('posAdmin.devices.appSummaryTotal'), value: summary.total },
    { key: 'online', label: t('posAdmin.devices.appSummaryOnline'), value: summary.online },
    { key: 'android', label: t('posAdmin.devices.appSummaryAndroid'), value: summary.android },
    { key: 'ios', label: t('posAdmin.devices.appSummaryIos'), value: summary.ios },
    { key: 'unknown', label: t('posAdmin.devices.mgmt.appSummaryUnknown'), value: summary.unknownSystem },
  ]

  return (
    <div className="dev-mgmt-card">
      <div className="dev-mgmt-toolbar">
        <Input
          allowClear
          prefix={<SearchOutlined className="dev-mgmt-faint" />}
          placeholder={t('posAdmin.devices.appSearchPlaceholder')}
          style={{ width: 240 }}
          value={keywordInput}
          onChange={(event) => {
            setKeywordInput(event.target.value)
            if (!event.target.value) {
              setKeyword('')
            }
          }}
          onPressEnter={() => setKeyword(keywordInput.trim())}
        />
        <Select
          allowClear
          showSearch
          optionFilterProp="label"
          placeholder={t('posAdmin.devices.filterByStore')}
          style={{ width: 220 }}
          value={selectedStoreCode}
          onChange={(value) => onStoreChange(value)}
          options={stores.map((store) => ({ label: `${store.storeCode} / ${store.storeName}`, value: store.storeCode }))}
        />
        <Select
          allowClear
          placeholder={t('posAdmin.devices.filterByDeviceSystem')}
          style={{ width: 140 }}
          value={deviceSystem}
          onChange={(value) => setDeviceSystem(value)}
          options={APP_DEVICE_SYSTEM_OPTIONS.map((value) => ({ label: value, value }))}
        />
        <Select<AppDeviceOnlineState>
          style={{ width: 120 }}
          value={onlineState}
          onChange={(value) => setOnlineState(value)}
          options={(['all', 'online', 'offline'] as const).map((value) => ({
            label: t(`posAdmin.devices.appOnlineFilters.${value}`),
            value,
          }))}
        />
        <span className="dev-mgmt-toolbar-spacer" />
        <Button icon={<ReloadOutlined />} loading={loading} onClick={() => void load()}>
          {t('common.refresh')}
        </Button>
      </div>

      <div className="dev-mgmt-stats">
        {stats.map((stat) => (
          <div key={stat.key} className={`dev-mgmt-stat dev-mgmt-stat-${stat.key}`}>
            <div className="dev-mgmt-stat-label">{stat.label}</div>
            <div className="dev-mgmt-stat-value">{stat.value}</div>
          </div>
        ))}
      </div>
      <div className="dev-mgmt-note">{t('posAdmin.devices.appUsageNote')}</div>

      <MeasuredTable<AppDeviceStatus>
        metricId="pos-admin.device-registration.table-1"
        className="dev-mgmt-table"
        rowKey={(record) => record.id || record.hardwareId}
        loading={loading}
        columns={columns}
        dataSource={items}
        scroll={{ x: 1180 }}
        pagination={false}
        size="middle"
      />
    </div>
  )
}
