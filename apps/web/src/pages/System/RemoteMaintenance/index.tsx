import {
  CopyOutlined,
  LinkOutlined,
  ReloadOutlined,
  SafetyCertificateOutlined,
} from '@ant-design/icons'
import {
  Alert,
  Button,
  Card,
  Empty,
  Input,
  Modal,
  Select,
  Space,
  Tag,
  Typography,
  message,
} from 'antd'
import type { ColumnsType } from 'antd/es/table'
import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { MeasuredTable } from '../../../components/MeasuredTable'
import PageContainer from '../../../components/PageContainer'
import {
  getRemoteMaintenanceCredential,
  getRemoteMaintenanceDevices,
} from '../../../services/remoteMaintenanceService'
import { getActiveStores, type StoreOption } from '../../../services/storeService'
import type {
  RemoteMaintenanceDevice,
  RemoteMaintenanceDevicePage,
  RemoteOnlineStatus,
  RemoteServiceStatus,
} from '../../../types/remoteMaintenance'
import { useAuthStore } from '../../../store/auth'
import {
  buildRemoteMaintenanceQuery,
  createRemoteMaintenanceRequestGate,
  createRemoteMaintenancePollScheduler,
  formatRemoteMaintenanceDateTime,
  getRemoteOnlineStatusColor,
  getRemoteServiceStatusColor,
  getRustDeskVersionDisplay,
  isRemoteMaintenanceVisible,
} from './remoteMaintenanceLogic'
import './remoteMaintenance.css'

function rustDeskUri(rustdeskId: string) {
  return `rustdesk://connection/new/${encodeURIComponent(rustdeskId)}`
}

function copySecret(value: string) {
  return navigator.clipboard.writeText(value)
}

export default function RemoteMaintenancePage() {
  const { t } = useTranslation()
  const isAdmin = useAuthStore((state) => state.access.isAdmin)
  const [pageResult, setPageResult] = useState<RemoteMaintenanceDevicePage>({
    items: [],
    total: 0,
    page: 1,
    pageSize: 10,
    serverTimeUtc: null,
  })
  const [page, setPage] = useState(1)
  const [pageSize, setPageSize] = useState(10)
  const [keyword, setKeyword] = useState('')
  const [storeCode, setStoreCode] = useState('')
  // 分店下拉与列表里的分店名称共用同一份启用分店列表（label=分店名称，value=分店代码）。
  const [storeOptions, setStoreOptions] = useState<StoreOption[]>([])
  const [onlineStatus, setOnlineStatus] = useState<RemoteOnlineStatus>()
  const [serviceStatus, setServiceStatus] = useState<RemoteServiceStatus>()
  const [loading, setLoading] = useState(false)
  const [loadError, setLoadError] = useState<string | null>(null)
  const [credentialDevice, setCredentialDevice] = useState<RemoteMaintenanceDevice | null>(null)
  const [credentialPassword, setCredentialPassword] = useState<string | null>(null)
  const [credentialLoading, setCredentialLoading] = useState(false)
  const [credentialError, setCredentialError] = useState<string | null>(null)
  const requestGateRef = useRef(createRemoteMaintenanceRequestGate())
  const credentialGateRef = useRef(createRemoteMaintenanceRequestGate())
  const deviceAbortControllerRef = useRef<AbortController | null>(null)
  const credentialAbortControllerRef = useRef<AbortController | null>(null)

  const filters = useMemo(() => ({ keyword, storeCode, onlineStatus, serviceStatus }), [
    keyword,
    onlineStatus,
    serviceStatus,
    storeCode,
  ])

  const loadDevices = useCallback(async (nextPage = page, nextPageSize = pageSize) => {
    const requestId = requestGateRef.current.begin()
    deviceAbortControllerRef.current?.abort()
    const controller = new AbortController()
    deviceAbortControllerRef.current = controller
    setLoading(true)
    try {
      const result = await getRemoteMaintenanceDevices(
        buildRemoteMaintenanceQuery(nextPage, nextPageSize, filters),
        controller.signal,
      )
      if (!requestGateRef.current.isCurrent(requestId)) return
      setPageResult(result)
      setPage(result.page)
      setPageSize(result.pageSize)
      setLoadError(null)
    } catch (error) {
      if (!requestGateRef.current.isCurrent(requestId)) return
      if (error instanceof DOMException && error.name === 'AbortError') return
      if (error && typeof error === 'object' && 'name' in error && error.name === 'AbortError') return
      setLoadError(error instanceof Error ? error.message : t('system.remoteMaintenance.loadFailed'))
    } finally {
      if (deviceAbortControllerRef.current === controller) {
        deviceAbortControllerRef.current = null
      }
      if (requestGateRef.current.isCurrent(requestId)) setLoading(false)
    }
  }, [filters, page, pageSize, t])

  useEffect(() => {
    if (!isAdmin) return

    const scheduler = createRemoteMaintenancePollScheduler({
      isVisible: isRemoteMaintenanceVisible,
      refresh: () => loadDevices(page, pageSize),
      onInvalidate: () => {
        requestGateRef.current.invalidate()
        deviceAbortControllerRef.current?.abort()
      },
      setTimer: (callback, delayMs) => window.setTimeout(callback, delayMs),
      clearTimer: (timer) => window.clearTimeout(timer),
    })

    scheduler.start()
    document.addEventListener('visibilitychange', scheduler.handleVisibilityChange)
    return () => {
      scheduler.dispose()
      deviceAbortControllerRef.current = null
      document.removeEventListener('visibilitychange', scheduler.handleVisibilityChange)
    }
  }, [isAdmin, loadDevices, page, pageSize])

  useEffect(() => {
    if (!isAdmin) return
    let disposed = false
    void getActiveStores()
      .then((stores) => {
        if (!disposed) setStoreOptions(stores)
      })
      .catch((error) => {
        // 分店列表只用于显示名称和下拉；失败不影响设备列表，名称退回显示分店代码。
        console.error('Failed to load stores for remote maintenance', error)
        if (!disposed) message.warning(t('system.remoteMaintenance.storesLoadFailed'))
      })
    return () => {
      disposed = true
    }
  }, [isAdmin, t])

  const storeNameByCode = useMemo(
    () => new Map(storeOptions.map((option) => [option.value, option.label])),
    [storeOptions],
  )

  const resetFilters = () => {
    requestGateRef.current.invalidate()
    setKeyword('')
    setStoreCode('')
    setOnlineStatus(undefined)
    setServiceStatus(undefined)
    setPage(1)
  }

  const closeCredential = () => {
    credentialGateRef.current.invalidate()
    credentialAbortControllerRef.current?.abort()
    credentialAbortControllerRef.current = null
    setCredentialDevice(null)
    setCredentialPassword(null)
    setCredentialLoading(false)
    setCredentialError(null)
  }

  const openCredential = async (device: RemoteMaintenanceDevice) => {
    const requestId = credentialGateRef.current.begin()
    credentialAbortControllerRef.current?.abort()
    const controller = new AbortController()
    credentialAbortControllerRef.current = controller
    setCredentialDevice(device)
    setCredentialPassword(null)
    setCredentialError(null)
    setCredentialLoading(true)
    try {
      const password = await getRemoteMaintenanceCredential(device.id, controller.signal)
      if (credentialGateRef.current.isCurrent(requestId)) setCredentialPassword(password)
    } catch (error) {
      if (credentialGateRef.current.isCurrent(requestId)) {
        if (error instanceof DOMException && error.name === 'AbortError') return
        if (error && typeof error === 'object' && 'name' in error && error.name === 'AbortError') return
        setCredentialError(error instanceof Error ? error.message : t('system.remoteMaintenance.credentialLoadFailed'))
      }
    } finally {
      if (credentialAbortControllerRef.current === controller) {
        credentialAbortControllerRef.current = null
      }
      if (credentialGateRef.current.isCurrent(requestId)) setCredentialLoading(false)
    }
  }

  const handleCopyCredential = async () => {
    if (!credentialPassword) return
    try {
      await copySecret(credentialPassword)
      message.success(t('system.remoteMaintenance.credentialCopied'))
    } catch {
      message.error(t('system.remoteMaintenance.credentialCopyFailed'))
    }
  }

  const columns = useMemo<ColumnsType<RemoteMaintenanceDevice>>(() => [
    {
      title: t('system.remoteMaintenance.store'),
      key: 'store',
      fixed: 'left',
      width: 150,
      render: (_value, record) => (
        <Space direction="vertical" size={0}>
          <Typography.Text strong>
            {storeNameByCode.get(record.storeCode) || record.storeCode || '--'}
          </Typography.Text>
          <Typography.Text type="secondary">
            {[record.storeCode, record.deviceCode].filter(Boolean).join(' · ') || '--'}
          </Typography.Text>
        </Space>
      ),
    },
    {
      title: t('system.remoteMaintenance.computer'),
      dataIndex: 'computerName',
      width: 180,
      render: (value: string) => value || '--',
    },
    {
      title: t('system.remoteMaintenance.rustdeskId'),
      dataIndex: 'rustdeskId',
      width: 150,
      render: (value: string) => value || '--',
    },
    {
      title: t('system.remoteMaintenance.clientVersion'),
      dataIndex: 'clientVersion',
      width: 150,
      // 显示设备上实际的版本；偏离下发版本或程序文件缺失时标红，与“最近上报”列的文本 + 标签写法一致。
      render: (_: string, record) => {
        const display = getRustDeskVersionDisplay(record)
        return (
          <Space direction="vertical" size={0}>
            <span>{display.version}</span>
            {display.tagKey ? (
              <Tag color="red">{t(`system.remoteMaintenance.rustDeskVersionStatuses.${display.tagKey}`)}</Tag>
            ) : null}
          </Space>
        )
      },
    },
    {
      title: t('system.remoteMaintenance.agentVersion'),
      dataIndex: 'agentVersion',
      width: 130,
      render: (value: string) => value || '--',
    },
    {
      title: t('system.remoteMaintenance.onlineStatus'),
      dataIndex: 'onlineStatus',
      width: 120,
      render: (value: RemoteOnlineStatus) => (
        <Tag color={getRemoteOnlineStatusColor(value)}>{t(`system.remoteMaintenance.onlineStatuses.${value}`)}</Tag>
      ),
    },
    {
      title: t('system.remoteMaintenance.serviceStatus'),
      dataIndex: 'serviceStatus',
      width: 150,
      render: (value: RemoteServiceStatus, record) => (
        <Space direction="vertical" size={0}>
          <Tag color={getRemoteServiceStatusColor(value)}>{t(`system.remoteMaintenance.serviceStatuses.${value}`)}</Tag>
          {record.onlineStatus === 'offline' ? (
            <Typography.Text type="secondary" className="remote-maintenance-hint">
              {t('system.remoteMaintenance.lastReportedStatus')}
            </Typography.Text>
          ) : null}
        </Space>
      ),
    },
    {
      title: t('system.remoteMaintenance.lastSeen'),
      dataIndex: 'lastSeenAtUtc',
      width: 180,
      render: (value: string | null, record) => (
        <Space direction="vertical" size={0}>
          <span>{formatRemoteMaintenanceDateTime(value)}</span>
          {record.isStale ? <Tag color="orange">{t('system.remoteMaintenance.stale')}</Tag> : null}
        </Space>
      ),
    },
    {
      title: t('system.remoteMaintenance.registeredAt'),
      dataIndex: 'registeredAtUtc',
      width: 180,
      render: (value: string | null) => formatRemoteMaintenanceDateTime(value),
    },
    {
      title: t('column.action'),
      key: 'actions',
      fixed: 'right',
      width: 250,
      render: (_value, record) => (
        <Space wrap>
          <Button
            size="small"
            icon={<LinkOutlined />}
            disabled={!record.rustdeskId}
            href={record.rustdeskId ? rustDeskUri(record.rustdeskId) : undefined}
            onClick={() => message.info(t('system.remoteMaintenance.rustdeskInstallHint'))}
          >
            {t('system.remoteMaintenance.openRustDesk')}
          </Button>
          <Button
            size="small"
            icon={<SafetyCertificateOutlined />}
            onClick={() => void openCredential(record)}
          >
            {t('system.remoteMaintenance.viewCredential')}
          </Button>
        </Space>
      ),
    },
  ], [openCredential, storeNameByCode, t])

  useEffect(() => {
    if (isAdmin) return
    credentialGateRef.current.invalidate()
    credentialAbortControllerRef.current?.abort()
    credentialAbortControllerRef.current = null
    setCredentialDevice(null)
    setCredentialPassword(null)
    setCredentialLoading(false)
    setCredentialError(null)
  }, [isAdmin])

  if (!isAdmin) {
    return <Alert type="error" showIcon message={t('forbidden.title')} description={t('system.remoteMaintenance.adminOnly')} />
  }

  return (
    <PageContainer
      title={t('system.remoteMaintenance.title')}
      subtitle={t('system.remoteMaintenance.subtitle')}
      extra={(
        <Button icon={<ReloadOutlined />} loading={loading} onClick={() => void loadDevices(1, pageSize)}>
          {t('common.refresh')}
        </Button>
      )}
    >
      <Card className="remote-maintenance-filters">
        <Space wrap size={[12, 12]}>
          <Input
            allowClear
            value={keyword}
            placeholder={t('system.remoteMaintenance.keywordPlaceholder')}
            onChange={(event) => { setKeyword(event.target.value); setPage(1) }}
            onPressEnter={() => void loadDevices(1, pageSize)}
          />
          <Select
            allowClear
            showSearch
            optionFilterProp="label"
            value={storeCode || undefined}
            placeholder={t('system.remoteMaintenance.storePlaceholder')}
            options={storeOptions}
            // 名称或分店代码都能搜到（输入 1042 也能命中）。
            filterOption={(input, option) => {
              const keyword = input.trim().toLowerCase()
              return !keyword
                || String(option?.label ?? '').toLowerCase().includes(keyword)
                || String(option?.value ?? '').toLowerCase().includes(keyword)
            }}
            onChange={(value: string | undefined) => { setStoreCode(value ?? ''); setPage(1) }}
          />
          <Select
            allowClear
            value={onlineStatus}
            placeholder={t('system.remoteMaintenance.onlineStatus')}
            options={(['online', 'offline', 'never'] as RemoteOnlineStatus[]).map((value) => ({
              value,
              label: t(`system.remoteMaintenance.onlineStatuses.${value}`),
            }))}
            onChange={(value: RemoteOnlineStatus | undefined) => { setOnlineStatus(value); setPage(1) }}
          />
          <Select
            allowClear
            value={serviceStatus}
            placeholder={t('system.remoteMaintenance.serviceStatus')}
            options={(['notInstalled', 'running', 'stopped', 'starting', 'stopping', 'checkFailed'] as RemoteServiceStatus[]).map((value) => ({
              value,
              label: t(`system.remoteMaintenance.serviceStatuses.${value}`),
            }))}
            onChange={(value: RemoteServiceStatus | undefined) => { setServiceStatus(value); setPage(1) }}
          />
          <Button onClick={resetFilters}>{t('system.remoteMaintenance.resetFilters')}</Button>
        </Space>
      </Card>

      {loadError ? (
        <Alert
          type="warning"
          showIcon
          message={t('system.remoteMaintenance.notRefreshed')}
          description={loadError}
          action={<Button size="small" onClick={() => void loadDevices(page, pageSize)}>{t('system.remoteMaintenance.retry')}</Button>}
        />
      ) : null}

      <Card
        title={t('system.remoteMaintenance.devicesTitle')}
        extra={pageResult.serverTimeUtc ? `${t('system.remoteMaintenance.serverTime')}: ${formatRemoteMaintenanceDateTime(pageResult.serverTimeUtc)}` : undefined}
      >
        <MeasuredTable<RemoteMaintenanceDevice>
          metricId="system.remote-maintenance.devices"
          rowKey="id"
          loading={loading}
          columns={columns}
          dataSource={pageResult.items}
          scroll={{ x: 1640 }}
          locale={{ emptyText: <Empty description={t('system.remoteMaintenance.empty')} /> }}
          pagination={{
            current: page,
            pageSize,
            total: pageResult.total,
            showSizeChanger: true,
            onChange: (nextPage, nextPageSize) => void loadDevices(nextPage, nextPageSize),
          }}
        />
      </Card>

      <Modal
        open={!!credentialDevice}
        title={credentialDevice ? t('system.remoteMaintenance.credentialTitle', { computer: credentialDevice.computerName || credentialDevice.deviceCode }) : undefined}
        onCancel={closeCredential}
        footer={null}
        destroyOnHidden
      >
        {credentialError ? <Alert type="error" showIcon message={credentialError} /> : null}
        <Space direction="vertical" size={16} style={{ width: '100%' }}>
          <Typography.Text type="secondary">{t('system.remoteMaintenance.credentialWarning')}</Typography.Text>
          <Input.Password
            readOnly
            value={credentialPassword ?? ''}
            visibilityToggle={false}
            placeholder={credentialLoading ? t('system.remoteMaintenance.credentialLoading') : t('system.remoteMaintenance.credentialUnavailable')}
          />
          <Button
            block
            icon={<CopyOutlined />}
            disabled={!credentialPassword}
            onClick={() => void handleCopyCredential()}
          >
            {t('system.remoteMaintenance.copyCredential')}
          </Button>
        </Space>
      </Modal>
    </PageContainer>
  )
}
