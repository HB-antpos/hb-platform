import {
  EllipsisOutlined,
  InfoCircleOutlined,
  ReloadOutlined,
  SafetyCertificateOutlined,
  SearchOutlined,
} from '@ant-design/icons'
import { Alert, Button, Dropdown, Input, Modal, Segmented, Select, Tooltip, message } from 'antd'
import type { ColumnsType } from 'antd/es/table'
import { useEffect, useMemo, useRef, useState } from 'react'
import { useTranslation } from 'react-i18next'

import ActiveFilterBar from '../../../components/listToolbar/ActiveFilterBar'
import type { ActiveFilterItem } from '../../../components/listToolbar/ActiveFilterBar'
import { MeasuredTable } from '../../../components/MeasuredTable'
import PageContainer from '../../../components/PageContainer'
import { registerPageMessages } from '../../../i18n/registerPageMessages'
import {
  activateDevice,
  disableDevice,
  getDeviceRegistrations,
  getStoreOptions,
  isDeviceRuntimeOnline,
  lockDevice,
} from '../../../services/deviceRegistrationService'
import { useAuthStore } from '../../../store/auth'
import type { DeviceRegistrationItem, StoreOption } from '../../../types/deviceRegistration'
import { P } from '../../../types/permissions'
import { createLatestRequestGuard, runLatestGuardedRequest } from '../../../utils/latestRequestGuard'

import AppUsagePanel from './AppUsagePanel'
import DeviceActivationCodePanel from './DeviceActivationCodePanel'
import EmergencyLoginModal from './EmergencyLoginModal'
import { supportsTransactionGate } from './deviceSystemOptions'
import {
  DeviceStatusPill,
  DeviceTypeTag,
  EMPTY_VALUE,
  OnlineDot,
  RelativeTime,
  StoreCell,
  formatDateTime,
  formatStoreLabel,
} from './deviceCells'
import DeviceDetailDrawer from './DeviceDetailDrawer'
import {
  DEVICE_STATUS_TABS,
  buildCountedOptions,
  compareDateDesc,
  countDevicesByStatus,
  filterDevicesByStatus,
  filterDevicesExceptStatus,
  formatDateOnly,
  getDeviceStatusActions,
  type CountedOption,
  type DeviceOnlineFilter,
  type DeviceStatusAction,
  type DeviceStatusTab,
} from './deviceManagementLogic'
import './deviceManagement.css'
import deviceManagementMessagesEn from './deviceManagementMessages.en.json'
import deviceManagementMessagesZh from './deviceManagementMessages.zh.json'

// 页面级文案随页面代码块懒加载，不进首屏 i18n 包（首屏 gzip 预算很紧）。
registerPageMessages({ zh: deviceManagementMessagesZh, en: deviceManagementMessagesEn })

/**
 * 生产全部门店约 220 台设备：一次拉全量（只把分店交给后端筛选），
 * 状态页签计数、类型/系统/在线/关键字筛选都在本地完成，计数才准确。
 * 旧版固定取 200 条，不选分店时已经在截断。
 */
const REGISTERED_FETCH_SIZE = 1000
const POLL_INTERVAL_MS = 15_000

type DeviceRegistrationViewMode = 'registered' | 'appUsage' | 'activationCodes'

function renderCountedOption(option: CountedOption) {
  return {
    value: option.value,
    // 搜索/回显用纯文本，下拉里右侧显示台数
    title: option.value,
    label: (
      <span className="dev-mgmt-option">
        <span>{option.value}</span>
        <span className="dev-mgmt-option-count">{option.count}</span>
      </span>
    ),
  }
}

export default function DeviceRegistrationPage() {
  const { t } = useTranslation()
  const access = useAuthStore((state) => state.access)
  const canManageActivationCodes = access.hasPermission(
    P.DeviceRegistration.ActivationCodesManage,
  )
  const canManageMobileActivationCodes = access.canManageMobileDeviceActivationCodes
  const canManageAnyActivationCodes =
    canManageActivationCodes || canManageMobileActivationCodes
  const canViewLegacyDeviceRegistration =
    access.isAdmin ||
    access.hasPermission(P.DeviceRegistration.View) ||
    access.hasPermission(P.DeviceRegistration.Manage)
  const canManage = access.canManageDeviceRegistration
  const canIssueEmergencyLogin = canManage && access.canManageSystemSettings

  const [viewMode, setViewMode] = useState<DeviceRegistrationViewMode>(() =>
    canViewLegacyDeviceRegistration ? 'registered' : 'activationCodes',
  )
  const [stores, setStores] = useState<StoreOption[]>([])
  const [selectedStoreCode, setSelectedStoreCode] = useState<string>()

  const [items, setItems] = useState<DeviceRegistrationItem[]>([])
  const [serverTotal, setServerTotal] = useState(0)
  const [loading, setLoading] = useState(false)
  const [statusTab, setStatusTab] = useState<DeviceStatusTab>('all')
  const [keyword, setKeyword] = useState('')
  const [deviceType, setDeviceType] = useState<string>()
  const [deviceSystem, setDeviceSystem] = useState<string>()
  const [onlineFilter, setOnlineFilter] = useState<DeviceOnlineFilter>()
  const [pagination, setPagination] = useState({ current: 1, pageSize: 50 })

  const [actionDeviceId, setActionDeviceId] = useState<number | null>(null)
  const [drawerDeviceId, setDrawerDeviceId] = useState<number | null>(null)
  const [drawerSnapshot, setDrawerSnapshot] = useState<DeviceRegistrationItem | null>(null)
  const [emergencyOpen, setEmergencyOpen] = useState(false)
  const listRequestGuardRef = useRef(createLatestRequestGuard())
  // 列定义被 useMemo 缓存，里面的操作按钮闭包可能是旧渲染的；刷新列表时从 ref 读当前分店，
  // 否则切换分店后点「启用」会按旧分店重新拉取并覆盖列表。
  const selectedStoreCodeRef = useRef(selectedStoreCode)
  selectedStoreCodeRef.current = selectedStoreCode

  async function loadStores() {
    try {
      setStores(await getStoreOptions())
    } catch (error) {
      console.error(t('posAdmin.devices.loadStoresFailed'), error)
      message.error(t('posAdmin.devices.loadStoresFailed'))
    }
  }

  function loadDevices(showLoading = true) {
    return runLatestGuardedRequest(
      listRequestGuardRef.current,
      () =>
        getDeviceRegistrations({
          page: 1,
          pageSize: REGISTERED_FETCH_SIZE,
          storeCode: selectedStoreCodeRef.current,
        }),
      {
        onStart: () => {
          if (showLoading) {
            setLoading(true)
          }
        },
        onSuccess: (result) => {
          setItems(result.devices)
          setServerTotal(result.total)
        },
        onError: (error) => {
          console.error(t('posAdmin.devices.loadFailed'), error)
          // 后台轮询失败不弹窗，避免网络抖动时每 15 秒刷一条错误
          if (showLoading) {
            message.error(t('posAdmin.devices.loadFailed'))
          }
        },
        onSettled: () => setLoading(false),
      },
    )
  }

  useEffect(() => {
    if (canViewLegacyDeviceRegistration) {
      void loadStores()
    }
  }, [canViewLegacyDeviceRegistration])

  useEffect(() => {
    if (!canViewLegacyDeviceRegistration && viewMode !== 'activationCodes') {
      setViewMode('activationCodes')
    }
  }, [canViewLegacyDeviceRegistration, viewMode])

  useEffect(() => {
    if (viewMode !== 'registered') {
      return
    }

    void loadDevices()
    const intervalId = window.setInterval(() => {
      void loadDevices(false)
    }, POLL_INTERVAL_MS)

    return () => window.clearInterval(intervalId)
  }, [viewMode, selectedStoreCode])

  useEffect(() => () => listRequestGuardRef.current.invalidate(), [])

  // 任一筛选变化都回到第一页，避免停在超出范围的空页
  useEffect(() => {
    setPagination((current) => (current.current === 1 ? current : { ...current, current: 1 }))
  }, [selectedStoreCode, statusTab, keyword, deviceType, deviceSystem, onlineFilter])

  const storeNameMap = useMemo(
    () =>
      stores.reduce<Record<string, string>>((accumulator, store) => {
        accumulator[store.storeCode] = store.storeName
        return accumulator
      }, {}),
    [stores]
  )

  const getStoreName = (storeCode?: string | null) =>
    storeCode ? storeNameMap[storeCode] : undefined

  const baseFiltered = useMemo(
    () =>
      filterDevicesExceptStatus(
        items,
        { keyword, deviceType, deviceSystem, online: onlineFilter },
        (item) => isDeviceRuntimeOnline(item),
        getStoreName,
      ),
    [items, keyword, deviceType, deviceSystem, onlineFilter, storeNameMap]
  )
  const statusCounts = useMemo(() => countDevicesByStatus(baseFiltered), [baseFiltered])
  const visibleItems = useMemo(() => filterDevicesByStatus(baseFiltered, statusTab), [baseFiltered, statusTab])
  const scopeCounts = useMemo(
    () => ({
      total: items.length,
      online: items.filter((item) => isDeviceRuntimeOnline(item)).length,
      pending: countDevicesByStatus(items).pending,
    }),
    [items]
  )

  // 选项与台数取自当前分店范围的已加载设备（不受其他筛选影响），保证每个选项都能筛出数据
  const deviceTypeOptions = useMemo(
    () => buildCountedOptions(items.map((item) => item.deviceType), deviceType),
    [items, deviceType]
  )
  const deviceSystemOptions = useMemo(
    () => buildCountedOptions(items.map((item) => item.deviceSystem), deviceSystem),
    [items, deviceSystem]
  )

  async function executeStatusAction(item: DeviceRegistrationItem, action: DeviceStatusAction) {
    setActionDeviceId(item.id)
    try {
      if (action === 'activate') {
        await activateDevice(item.id)
        message.success(t('message.deviceEnabled', { deviceNo: item.systemDeviceNumber }))
      } else if (action === 'disable') {
        await disableDevice(item.id)
        message.success(t('message.deviceDisabled', { deviceNo: item.systemDeviceNumber }))
      } else {
        await lockDevice(item.id)
        message.success(t('message.deviceLocked', { deviceNo: item.systemDeviceNumber }))
      }

      await loadDevices()
    } catch (error) {
      console.error(t('message.deviceStatusFailed'), error)
      message.error(t('message.deviceStatusFailed'))
    } finally {
      setActionDeviceId(null)
    }
  }

  /** 启用直接执行；禁用、锁定会让门店设备立即不可用，先二次确认。 */
  function runAction(item: DeviceRegistrationItem, action: DeviceStatusAction) {
    if (!canManage) {
      return
    }
    if (action === 'activate') {
      void executeStatusAction(item, action)
      return
    }

    const deviceNo = item.systemDeviceNumber || item.hardwareId
    Modal.confirm({
      title: t(
        action === 'lock' ? 'posAdmin.devices.mgmt.confirmLockTitle' : 'posAdmin.devices.mgmt.confirmDisableTitle',
        { deviceNo },
      ),
      content: t(
        action === 'lock' ? 'posAdmin.devices.mgmt.confirmLockContent' : 'posAdmin.devices.mgmt.confirmDisableContent',
      ),
      okText: t(action === 'lock' ? 'posAdmin.devices.lock' : 'posAdmin.devices.disable'),
      okButtonProps: { danger: true },
      cancelText: t('common.cancel'),
      onOk: () => executeStatusAction(item, action),
    })
  }

  function openDrawer(item: DeviceRegistrationItem) {
    setDrawerDeviceId(item.id)
    setDrawerSnapshot(item)
  }

  function closeDrawer() {
    setDrawerDeviceId(null)
    setDrawerSnapshot(null)
  }

  // 抽屉跟随轮询/操作后的最新行，行被筛掉时退回打开时的快照
  const drawerDevice = drawerDeviceId === null
    ? null
    : items.find((item) => item.id === drawerDeviceId) ?? drawerSnapshot

  const statusActionLabel: Record<DeviceStatusAction, string> = {
    activate: t('posAdmin.devices.enable'),
    disable: t('posAdmin.devices.disable'),
    lock: t('posAdmin.devices.lock'),
  }

  const columns = useMemo<ColumnsType<DeviceRegistrationItem>>(() => {
    const baseColumns: ColumnsType<DeviceRegistrationItem> = [
      {
        title: t('posAdmin.devices.mgmt.columns.device'),
        key: 'device',
        width: 220,
        fixed: 'left',
        render: (_value, record) => (
          <div className="dev-mgmt-two">
            <span className="dev-mgmt-strong dev-mgmt-mono">{record.systemDeviceNumber || EMPTY_VALUE}</span>
            <span className="dev-mgmt-sub dev-mgmt-mono dev-mgmt-ellipsis" title={record.hardwareId}>
              {record.hardwareId || EMPTY_VALUE}
            </span>
          </div>
        ),
      },
      {
        title: t('column.store'),
        dataIndex: 'storeCode',
        width: 170,
        render: (value: string | null | undefined, record) => (
          <StoreCell storeCode={value} storeName={getStoreName(value) ?? record.storeName} />
        ),
      },
      {
        title: t('posAdmin.devices.mgmt.columns.typeSystem'),
        key: 'typeSystem',
        width: 170,
        render: (_value, record) => (
          <span className="dev-mgmt-inline">
            <DeviceTypeTag value={record.deviceType} />
            <span className="dev-mgmt-sub">{record.deviceSystem || EMPTY_VALUE}</span>
          </span>
        ),
      },
      {
        title: t('posAdmin.devices.mgmt.columns.runtime'),
        key: 'runtime',
        width: 180,
        render: (_value, record) => {
          const online = isDeviceRuntimeOnline(record)
          return (
            <div className="dev-mgmt-two">
              <span className="dev-mgmt-inline">
                <OnlineDot online={online} t={t} />
                <span className="dev-mgmt-sub">
                  <RelativeTime value={record.lastHeartbeatAt} t={t} empty={t('posAdmin.devices.mgmt.heartbeatNever')} />
                </span>
              </span>
              {online && record.currentCashierName ? (
                <Tooltip title={formatDateTime(record.cashierLoginAt)}>
                  <span className="dev-mgmt-sub dev-mgmt-ellipsis">
                    {t('posAdmin.devices.mgmt.cashier', { name: record.currentCashierName })}
                  </span>
                </Tooltip>
              ) : null}
            </div>
          )
        },
      },
      {
        title: t('column.status'),
        dataIndex: 'status',
        width: 100,
        render: (_value: number, record) => (
          <DeviceStatusPill status={record.status} description={record.statusDescription} t={t} />
        ),
      },
      {
        title: t('posAdmin.devices.mgmt.columns.transactions'),
        dataIndex: 'allowTransactions',
        width: 80,
        render: (value: boolean, record) =>
          supportsTransactionGate(record.deviceSystem, record.deviceType) ? (
            <span className={value ? 'dev-mgmt-txn-on' : 'dev-mgmt-txn-off'}>
              {t(value ? 'posAdmin.devices.transactionsAllowed' : 'posAdmin.devices.transactionsBlocked')}
            </span>
          ) : (
            <Tooltip title={t('posAdmin.devices.mgmt.transactionsNotApplicableHint')}>
              <span className="dev-mgmt-faint">{EMPTY_VALUE}</span>
            </Tooltip>
          ),
      },
      {
        title: t('column.remarks'),
        dataIndex: 'remark',
        width: 160,
        render: (value: string | null | undefined) =>
          value ? (
            <span className="dev-mgmt-ellipsis" title={value}>{value}</span>
          ) : (
            <span className="dev-mgmt-faint">{EMPTY_VALUE}</span>
          ),
      },
      {
        title: t('posAdmin.devices.mgmt.columns.registeredAt'),
        dataIndex: 'createdAt',
        width: 120,
        sorter: (left, right) => compareDateDesc(right.createdAt, left.createdAt),
        render: (value: string | undefined) => (
          <Tooltip title={formatDateTime(value)}>
            <span className="dev-mgmt-mono">{formatDateOnly(value) ?? EMPTY_VALUE}</span>
          </Tooltip>
        ),
      },
    ]

    if (!canManage) {
      return baseColumns
    }

    return [
      ...baseColumns,
      {
        title: t('column.action'),
        key: 'actions',
        width: 150,
        fixed: 'right',
        align: 'right',
        render: (_value, record) => {
          const { primary, secondary } = getDeviceStatusActions(record.status)
          return (
            // 操作区点击不能冒泡到行点击（行点击会打开详情抽屉）
            <span className="dev-mgmt-actions" onClick={(event) => event.stopPropagation()}>
              <Button
                type="link"
                size="small"
                loading={actionDeviceId === record.id}
                onClick={() => runAction(record, primary)}
              >
                {statusActionLabel[primary]}
              </Button>
              <Button type="link" size="small" onClick={() => openDrawer(record)}>
                {t('common.edit')}
              </Button>
              <Dropdown
                trigger={['click']}
                menu={{
                  items: secondary.map((action) => ({
                    key: action,
                    label: statusActionLabel[action],
                    danger: action === 'lock',
                  })),
                  onClick: ({ key }) => runAction(record, key as DeviceStatusAction),
                }}
              >
                <Button type="text" size="small" icon={<EllipsisOutlined />} aria-label={t('common.more')} />
              </Dropdown>
            </span>
          )
        },
      },
    ]
  }, [canManage, actionDeviceId, storeNameMap, t])

  const storeOptions = stores.map((store) => ({
    label: `${store.storeCode} / ${store.storeName}`,
    value: store.storeCode,
  }))

  const activeFilterItems: ActiveFilterItem[] = [
    ...(selectedStoreCode
      ? [{
          key: 'store',
          label: t('column.store'),
          value: formatStoreLabel(selectedStoreCode, getStoreName(selectedStoreCode)),
          source: 'toolbar' as const,
          onRemove: () => setSelectedStoreCode(undefined),
        }]
      : []),
    ...(keyword.trim()
      ? [{
          key: 'keyword',
          label: t('common.search'),
          value: keyword.trim(),
          source: 'toolbar' as const,
          onRemove: () => setKeyword(''),
        }]
      : []),
    ...(deviceType
      ? [{
          key: 'deviceType',
          label: t('posAdmin.devices.deviceType'),
          value: deviceType,
          source: 'toolbar' as const,
          onRemove: () => setDeviceType(undefined),
        }]
      : []),
    ...(deviceSystem
      ? [{
          key: 'deviceSystem',
          label: t('posAdmin.devices.deviceSystem'),
          value: deviceSystem,
          source: 'toolbar' as const,
          onRemove: () => setDeviceSystem(undefined),
        }]
      : []),
    ...(onlineFilter
      ? [{
          key: 'online',
          label: t('posAdmin.devices.onlineStatus'),
          value: t(onlineFilter === 'online' ? 'posAdmin.devices.online' : 'posAdmin.devices.offline'),
          source: 'toolbar' as const,
          onRemove: () => setOnlineFilter(undefined),
        }]
      : []),
  ]

  function clearFilters() {
    setSelectedStoreCode(undefined)
    setKeyword('')
    setDeviceType(undefined)
    setDeviceSystem(undefined)
    setOnlineFilter(undefined)
    setStatusTab('all')
  }

  const viewOptions = [
    ...(canViewLegacyDeviceRegistration
      ? [
          { label: t('posAdmin.devices.viewRegistered'), value: 'registered' as const },
          { label: t('posAdmin.devices.viewAppUsage'), value: 'appUsage' as const },
        ]
      : []),
    ...(canManageAnyActivationCodes
      ? [{ label: t('posAdmin.devices.activation.view'), value: 'activationCodes' as const }]
      : []),
  ]

  return (
    <PageContainer
      compact
      title={t('posAdmin.devices.title')}
      subtitle={
        viewMode === 'registered' && items.length
          ? t('posAdmin.devices.mgmt.subtitle', { total: scopeCounts.total, online: scopeCounts.online })
          : undefined
      }
      extra={
        viewOptions.length > 1 ? (
          <Segmented<DeviceRegistrationViewMode>
            value={viewMode}
            onChange={(nextViewMode) => setViewMode(nextViewMode)}
            options={viewOptions}
          />
        ) : null
      }
    >
      {viewMode === 'activationCodes' ? (
        <div className="dev-mgmt-card">
          <DeviceActivationCodePanel
            canManage={canManageActivationCodes}
            canManageMobile={canManageMobileActivationCodes}
          />
        </div>
      ) : viewMode === 'appUsage' ? (
        <AppUsagePanel
          stores={stores}
          storeNameMap={storeNameMap}
          selectedStoreCode={selectedStoreCode}
          onStoreChange={setSelectedStoreCode}
        />
      ) : (
        <div className="dev-mgmt-card">
          <div className="dev-mgmt-toolbar">
            <Input
              allowClear
              prefix={<SearchOutlined className="dev-mgmt-faint" />}
              placeholder={t('posAdmin.devices.mgmt.searchPlaceholder')}
              style={{ width: 280 }}
              value={keyword}
              onChange={(event) => setKeyword(event.target.value)}
            />
            <Select
              allowClear
              showSearch
              optionFilterProp="label"
              placeholder={t('posAdmin.devices.filterByStore')}
              style={{ width: 220 }}
              value={selectedStoreCode}
              onChange={(value) => setSelectedStoreCode(value)}
              options={storeOptions}
            />
            <Select
              allowClear
              placeholder={t('posAdmin.devices.filterByDeviceType')}
              style={{ width: 150 }}
              value={deviceType}
              onChange={(value) => setDeviceType(value)}
              options={deviceTypeOptions.map(renderCountedOption)}
            />
            <Select
              allowClear
              placeholder={t('posAdmin.devices.filterByDeviceSystem')}
              style={{ width: 140 }}
              value={deviceSystem}
              onChange={(value) => setDeviceSystem(value)}
              options={deviceSystemOptions.map(renderCountedOption)}
            />
            <Select<DeviceOnlineFilter>
              allowClear
              placeholder={t('posAdmin.devices.filterByOnline')}
              style={{ width: 130 }}
              value={onlineFilter}
              onChange={(value) => setOnlineFilter(value)}
              options={[
                { label: t('posAdmin.devices.online'), value: 'online' },
                { label: t('posAdmin.devices.offline'), value: 'offline' },
              ]}
            />
            <span className="dev-mgmt-toolbar-spacer" />
            {canIssueEmergencyLogin ? (
              <Tooltip title={!selectedStoreCode ? t('posAdmin.devices.emergencySelectStoreFirst') : undefined}>
                <Button
                  danger
                  icon={<SafetyCertificateOutlined />}
                  disabled={!selectedStoreCode}
                  onClick={() => setEmergencyOpen(true)}
                >
                  {t('posAdmin.devices.emergencyAction')}
                </Button>
              </Tooltip>
            ) : null}
            <Tooltip title={t('common.refresh')}>
              <Button
                icon={<ReloadOutlined spin={loading} />}
                aria-label={t('common.refresh')}
                onClick={() => void loadDevices()}
              />
            </Tooltip>
          </div>

          <div className="dev-mgmt-status-tabs" role="tablist">
            {DEVICE_STATUS_TABS.map((tab) => (
              <button
                key={tab}
                type="button"
                role="tab"
                aria-selected={statusTab === tab}
                className={`dev-mgmt-status-tab ${statusTab === tab ? 'dev-mgmt-status-tab-active' : ''}`}
                onClick={() => setStatusTab(tab)}
              >
                {t(`posAdmin.devices.mgmt.statusTabs.${tab}`)}
                <span
                  className={`dev-mgmt-status-count ${
                    tab === 'pending' && statusCounts.pending > 0 ? 'dev-mgmt-status-count-alert' : ''
                  }`}
                >
                  {statusCounts[tab]}
                </span>
              </button>
            ))}
            <Tooltip title={t('posAdmin.devices.deviceNote')} placement="topRight">
              <InfoCircleOutlined className="dev-mgmt-status-tab-info" aria-label={t('posAdmin.devices.deviceNote')} />
            </Tooltip>
          </div>

          {scopeCounts.pending > 0 && statusTab !== 'pending' ? (
            <div className="dev-mgmt-pending">
              <span className="dev-mgmt-pending-title">
                {t('posAdmin.devices.mgmt.pendingBanner', { count: scopeCounts.pending })}
              </span>
              <span className="dev-mgmt-pending-text">{t('posAdmin.devices.deviceNote')}</span>
              <Button size="small" onClick={() => setStatusTab('pending')}>
                {t('posAdmin.devices.mgmt.pendingBannerAction')}
              </Button>
            </div>
          ) : null}

          {serverTotal > items.length || activeFilterItems.length ? (
            <div className="dev-mgmt-bars">
              {serverTotal > items.length ? (
                <Alert
                  type="warning"
                  showIcon
                  message={t('posAdmin.devices.mgmt.truncated', { loaded: items.length, total: serverTotal })}
                />
              ) : null}
              {activeFilterItems.length ? (
                <ActiveFilterBar items={activeFilterItems} onClearAll={clearFilters} />
              ) : null}
            </div>
          ) : null}

          <MeasuredTable<DeviceRegistrationItem>
            metricId="pos-admin.device-registration.table-2"
            className="dev-mgmt-table"
            rowKey="id"
            size="middle"
            loading={loading}
            columns={columns}
            dataSource={visibleItems}
            scroll={{ x: canManage ? 1350 : 1200 }}
            rowClassName={() => 'dev-mgmt-row-clickable'}
            onRow={(record) => ({ onClick: () => openDrawer(record) })}
            locale={{
              emptyText: activeFilterItems.length || statusTab !== 'all' ? (
                <div style={{ padding: '24px 0' }}>
                  <div className="dev-mgmt-sub">{t('posAdmin.devices.mgmt.emptyFiltered')}</div>
                  <Button type="link" onClick={clearFilters}>{t('posAdmin.devices.mgmt.clearFilters')}</Button>
                </div>
              ) : undefined,
            }}
            pagination={{
              current: pagination.current,
              pageSize: pagination.pageSize,
              showSizeChanger: true,
              pageSizeOptions: [20, 50, 100, 200],
              showTotal: (total) => t('common.totalCount', { count: total }),
              hideOnSinglePage: visibleItems.length <= 20,
              onChange: (current, pageSize) => setPagination({ current, pageSize }),
            }}
          />
        </div>
      )}

      <DeviceDetailDrawer
        device={drawerDevice}
        storeName={getStoreName(drawerDevice?.storeCode)}
        canManage={canManage}
        actionLoading={drawerDevice !== null && actionDeviceId === drawerDevice.id}
        onClose={closeDrawer}
        onSaved={() => {
          closeDrawer()
          void loadDevices()
        }}
        onStatusAction={runAction}
      />

      <EmergencyLoginModal
        open={emergencyOpen}
        storeCode={selectedStoreCode}
        storeLabel={formatStoreLabel(selectedStoreCode, getStoreName(selectedStoreCode))}
        onClose={() => setEmergencyOpen(false)}
      />
    </PageContainer>
  )
}
