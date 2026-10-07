import { ReloadOutlined } from '@ant-design/icons'
import { Alert, Button, Drawer, Tag, Tooltip, Typography } from 'antd'
import type { ColumnsType } from 'antd/es/table'
import type { TFunction } from 'i18next'
import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { MeasuredTable } from '../../../components/MeasuredTable'
import { getStoreReceiptProfileDevices } from '../../../services/storeReceiptProfileService'
import type {
  StoreReceiptProfileClientKind,
  StoreReceiptProfileDevice,
  StoreReceiptProfileDevicesResult,
} from '../../../types/storeReceiptProfile'
import { createLatestRequestGuard } from '../listPagination'
import {
  formatHeartbeatAt,
  formatPublishedAt,
  isReceiptProfileEndpointMissing,
  sortReceiptProfileDevices,
} from './receiptProfileLogic'

export interface ReceiptProfileDevicesTarget {
  storeGuid: string
  storeName: string
  storeCode: string
}

interface ReceiptProfileDevicesDrawerProps {
  /** 要查看的分店；为 null 时抽屉关闭。 */
  target: ReceiptProfileDevicesTarget | null
  onClose: () => void
}

function clientKindLabel(kind: StoreReceiptProfileClientKind, t: TFunction) {
  switch (kind) {
    case 'wpf':
      return t('system.stores.receiptProfile.kindWpf')
    case 'handheld':
      return t('system.stores.receiptProfile.kindHandheld')
    case 'ipad':
      return t('system.stores.receiptProfile.kindIpad')
    default:
      return t('system.stores.receiptProfile.kindOther')
  }
}

/** 单台设备的应用状态：已最新 / 待更新（已应用旧版本）/ 尚未应用（从未回执）。 */
function DeviceApplyState({ device }: { device: StoreReceiptProfileDevice }) {
  const { t } = useTranslation()
  if (device.upToDate) {
    return <span className="sys-store-rp-status sys-store-rp-status-synced">{t('system.stores.receiptProfile.deviceUpToDate')}</span>
  }
  return (
    <span className="sys-store-rp-status sys-store-rp-status-pending">
      {device.appliedVersion == null
        ? t('system.stores.receiptProfile.deviceNeverApplied')
        : t('system.stores.receiptProfile.deviceOutdated')}
    </span>
  )
}

/**
 * 「设备应用情况」抽屉：该分店启用的收银设备（WPF / 手持 / iPad）各自已应用到哪个下发版本。
 * 打开时请求 GET api/stores/receipt-profile/{storeGuid}/devices，可手动刷新。
 */
export default function ReceiptProfileDevicesDrawer({ target, onClose }: ReceiptProfileDevicesDrawerProps) {
  const { t } = useTranslation()
  const [loading, setLoading] = useState(false)
  const [result, setResult] = useState<StoreReceiptProfileDevicesResult | null>(null)
  const [error, setError] = useState<unknown>(null)
  const guardRef = useRef(createLatestRequestGuard())
  const targetGuid = target?.storeGuid

  const load = useCallback(async (storeGuid: string) => {
    const requestId = guardRef.current.begin()
    setLoading(true)
    setError(null)
    try {
      const data = await getStoreReceiptProfileDevices(storeGuid)
      if (guardRef.current.isLatest(requestId)) {
        setResult(data)
      }
    } catch (loadError) {
      if (guardRef.current.isLatest(requestId)) {
        console.error(loadError)
        setError(loadError)
      }
    } finally {
      if (guardRef.current.isLatest(requestId)) {
        setLoading(false)
      }
    }
  }, [])

  useEffect(() => {
    const guard = guardRef.current
    if (!targetGuid) {
      return undefined
    }
    setResult(null)
    void load(targetGuid)
    return () => {
      // 关闭或切换分店后，迟到的响应不能再写入。
      guard.invalidate()
    }
  }, [targetGuid, load])

  const devices = useMemo(() => sortReceiptProfileDevices(result?.devices ?? []), [result])
  const latestVersion = result?.latestVersion ?? 0
  const upToDateCount = devices.filter((device) => device.upToDate).length

  const columns: ColumnsType<StoreReceiptProfileDevice> = [
    {
      title: t('system.stores.receiptProfile.deviceCode'),
      dataIndex: 'deviceCode',
      render: (value: string) => <span className="sys-store-mono">{value}</span>,
    },
    {
      title: t('system.stores.receiptProfile.deviceKind'),
      dataIndex: 'clientKind',
      width: 104,
      render: (value: StoreReceiptProfileClientKind, record) => (
        <Tooltip title={record.deviceSystem || undefined}>
          <Tag bordered={false} style={{ marginInlineEnd: 0 }}>{clientKindLabel(value, t)}</Tag>
        </Tooltip>
      ),
    },
    {
      title: t('system.stores.receiptProfile.deviceOnline'),
      dataIndex: 'isOnline',
      width: 72,
      render: (value: boolean) => (
        <span className={value ? 'sys-store-status sys-store-status-on' : 'sys-store-status'}>
          {value ? t('system.stores.receiptProfile.online') : t('system.stores.receiptProfile.offline')}
        </span>
      ),
    },
    {
      title: t('system.stores.receiptProfile.lastHeartbeat'),
      dataIndex: 'lastHeartbeatAt',
      width: 128,
      render: (value: string | null) => {
        const text = formatHeartbeatAt(value)
        return text ? <span className="sys-store-sub">{text}</span> : <span className="sys-store-faint">--</span>
      },
    },
    {
      title: t('system.stores.receiptProfile.appliedVersion'),
      dataIndex: 'appliedVersion',
      width: 104,
      render: (value: number | null, record) => {
        if (value == null) {
          return <span className="sys-store-faint">--</span>
        }
        const appliedAt = formatPublishedAt(record.appliedAtUtc)
        return (
          <Tooltip title={appliedAt ? t('system.stores.receiptProfile.appliedAt', { time: appliedAt.local }) : undefined}>
            <span className="sys-store-mono">v{value}</span>
          </Tooltip>
        )
      },
    },
    {
      title: t('system.stores.receiptProfile.deviceState'),
      key: 'state',
      width: 96,
      render: (_, record) => <DeviceApplyState device={record} />,
    },
  ]

  return (
    <Drawer
      rootClassName="sys-store-drawer"
      title={(
        <div className="sys-store-drawer-head">
          <span className="sys-store-drawer-title">{t('system.stores.receiptProfile.devicesTitle')}</span>
          {target ? (
            <div className="sys-store-drawer-meta">
              <span>{target.storeName}</span>
              <Tag bordered={false} className="sys-store-mono" style={{ marginInlineEnd: 0 }}>{target.storeCode}</Tag>
            </div>
          ) : null}
        </div>
      )}
      extra={(
        <Tooltip title={t('common.refresh')}>
          <Button
            icon={<ReloadOutlined />}
            aria-label={t('common.refresh')}
            disabled={!targetGuid || loading}
            onClick={() => targetGuid && void load(targetGuid)}
          />
        </Tooltip>
      )}
      width={720}
      open={Boolean(target)}
      onClose={onClose}
      destroyOnHidden
      closable={{ placement: 'end' }}
    >
      {error ? (
        <Alert
          type="error"
          showIcon
          message={isReceiptProfileEndpointMissing(error)
            ? t('system.stores.receiptProfile.statusUnavailable')
            : t('system.stores.receiptProfile.devicesLoadFailed')}
          action={<Button size="small" onClick={() => targetGuid && void load(targetGuid)}>{t('common.retry')}</Button>}
        />
      ) : (
        <>
          <div className="sys-store-rp-summary">
            {result ? (
              latestVersion > 0 ? (
                <>
                  <strong>{t('system.stores.receiptProfile.devicesSummary', { version: latestVersion, applied: upToDateCount, total: devices.length })}</strong>
                  <Typography.Text type="secondary">{t('system.stores.receiptProfile.devicesHint')}</Typography.Text>
                </>
              ) : (
                <Typography.Text type="secondary">{t('system.stores.receiptProfile.devicesNeverPublished')}</Typography.Text>
              )
            ) : null}
          </div>
          <MeasuredTable<StoreReceiptProfileDevice>
            metricId="system.stores.receipt-devices.table-1"
            size="small"
            rowKey="deviceCode"
            loading={loading}
            columns={columns}
            dataSource={devices}
            pagination={false}
            locale={{ emptyText: result ? t('system.stores.receiptProfile.devicesEmpty') : ' ' }}
            scroll={{ x: 'max-content' }}
          />
        </>
      )}
    </Drawer>
  )
}
