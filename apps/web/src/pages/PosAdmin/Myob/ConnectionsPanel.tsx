import { CopyOutlined, DisconnectOutlined, LinkOutlined, ReloadOutlined, SafetyCertificateOutlined } from '@ant-design/icons'
import { Alert, Button, Collapse, Empty, Modal, Space, Tag, Tooltip, message } from 'antd'
import type { ColumnsType } from 'antd/es/table'
import { useMemo, useState } from 'react'
import { useTranslation } from 'react-i18next'

import { MeasuredTable } from '../../../components/MeasuredTable'
import { disconnectMyobConnection, verifyMyobConnection } from '../../../services/myobService'
import type { MyobConnection, MyobConnectionStatus, MyobStoreMappingItem } from '../../../types/myob'
import { copyTextToClipboard } from '../../../utils/clipboard'
import AbnCell from './AbnCell'
import {
  describeMappedStores,
  extractMyobError,
  formatMyobTime,
  splitConnections,
} from './myobLogic'

interface ConnectionsPanelProps {
  connections: MyobConnection[]
  loading: boolean
  /** 加载失败文案；非空时显示错误态（保留已有数据）。 */
  error: string | null
  /** 映射表里的分店，用于「已映射分店」hover 显示名称。 */
  stores: MyobStoreMappingItem[]
  /** 正在跳转授权页（整页跳转前按钮保持禁用）。 */
  authorizing: boolean
  onAuthorize: () => void
  onReload: () => void
  /** 本面板内改动（改 ABN / 验证 / 断开）后回传后端返回的最新单条连接，由父组件按 guid 合并（避免异步回调里用到过期列表）。 */
  onConnectionUpdated: (updated: MyobConnection) => void
  /** 断开会移除映射，需要父组件重新加载分店映射。 */
  onDisconnected: () => void
}

// 状态标签：颜色 + 文案键（字面量，供文案契约测试扫描）
const STATUS_TAG: Record<MyobConnectionStatus, { color: string; labelKey: string }> = {
  Active: { color: 'success', labelKey: 'myob.connections.status.Active' },
  NeedsReauth: { color: 'warning', labelKey: 'myob.connections.status.NeedsReauth' },
  Disconnected: { color: 'default', labelKey: 'myob.connections.status.Disconnected' },
}

export default function ConnectionsPanel({
  connections,
  loading,
  error,
  stores,
  authorizing,
  onAuthorize,
  onReload,
  onConnectionUpdated,
  onDisconnected,
}: ConnectionsPanelProps) {
  const { t } = useTranslation()
  // 行级忙碌状态：同一行同时只允许一个请求在途
  const [busy, setBusy] = useState<Record<string, 'verify' | 'disconnect'>>({})
  const { visible, disconnected } = useMemo(() => splitConnections(connections), [connections])

  const setRowBusy = (guid: string, value: 'verify' | 'disconnect' | null) =>
    setBusy((current) => {
      const next = { ...current }
      if (value) {
        next[guid] = value
      } else {
        delete next[guid]
      }
      return next
    })

  const handleVerify = async (record: MyobConnection) => {
    setRowBusy(record.connectionGuid, 'verify')
    try {
      const updated = await verifyMyobConnection(record.connectionGuid)
      onConnectionUpdated(updated)
      message.success(t('myob.actions.verified'))
    } catch (err) {
      message.error(extractMyobError(err, t('myob.actions.verifyFailed')).message)
      // 验证失败后端可能已把状态改成 NeedsReauth 并记录 lastError，刷新一次列表以同步
      onReload()
    } finally {
      setRowBusy(record.connectionGuid, null)
    }
  }

  const handleDisconnect = (record: MyobConnection) => {
    const name = record.businessName || record.businessId
    Modal.confirm({
      title: t('myob.actions.disconnectTitle', { name }),
      content: t('myob.actions.disconnectContent', { count: record.storeCodes.length }),
      okText: t('myob.actions.disconnectOk'),
      cancelText: t('myob.actions.disconnectCancel'),
      okButtonProps: { danger: true },
      // 返回 Promise：确认按钮自带 loading，防止重复点击
      onOk: async () => {
        setRowBusy(record.connectionGuid, 'disconnect')
        try {
          const updated = await disconnectMyobConnection(record.connectionGuid)
          onConnectionUpdated(updated)
          onDisconnected()
          message.success(t('myob.actions.disconnected', { name }))
        } catch (err) {
          message.error(extractMyobError(err, t('myob.actions.disconnectFailed')).message)
          // 抛出让弹窗保持打开，用户可重试或取消
          throw err
        } finally {
          setRowBusy(record.connectionGuid, null)
        }
      },
    })
  }

  const copyBusinessId = (value: string) =>
    void copyTextToClipboard(value, {
      successMessage: t('myob.connections.copyDone'),
      failureMessage: t('myob.connections.copyFailed'),
    })

  const columns: ColumnsType<MyobConnection> = [
    {
      title: t('myob.connections.columns.company'),
      dataIndex: 'businessName',
      key: 'company',
      width: 200,
      render: (value: string) => value || <span className="myob-muted">{t('myob.connections.unknownCompany')}</span>,
    },
    {
      title: t('myob.connections.columns.businessId'),
      dataIndex: 'businessId',
      key: 'businessId',
      width: 260,
      render: (value: string) => (
        <span className="myob-businessid">
          <span className="myob-mono myob-businessid-text" title={value}>
            {value}
          </span>
          <Tooltip title={t('myob.connections.copyBusinessId')}>
            <Button
              type="text"
              size="small"
              icon={<CopyOutlined />}
              aria-label={t('myob.connections.copyBusinessId')}
              onClick={() => copyBusinessId(value)}
            />
          </Tooltip>
        </span>
      ),
    },
    {
      title: t('myob.connections.columns.abn'),
      key: 'abn',
      width: 240,
      render: (_, record) => (
        <AbnCell
          connection={record}
          readOnly={record.status === 'Disconnected'}
          onSaved={onConnectionUpdated}
        />
      ),
    },
    {
      title: t('myob.connections.columns.status'),
      key: 'status',
      width: 190,
      render: (_, record) => {
        const tag = STATUS_TAG[record.status]
        return (
          <div>
            <Tag color={tag.color} style={{ marginInlineEnd: 0 }}>
              {t(tag.labelKey)}
            </Tag>
            {/* 需重新授权 / 有失败原因时附上 lastError，Active 成功后后端会清空 */}
            {record.lastError && record.status !== 'Disconnected' ? (
              <span className="myob-last-error">{record.lastError}</span>
            ) : null}
          </div>
        )
      },
    },
    {
      title: t('myob.connections.columns.authorized'),
      key: 'authorized',
      width: 170,
      render: (_, record) => (
        <>
          <span>{record.authorizedByName || '--'}</span>
          <span className="myob-sub">{formatMyobTime(record.authorizedAt)}</span>
        </>
      ),
    },
    {
      title: t('myob.connections.columns.lastRefreshed'),
      dataIndex: 'lastRefreshedAt',
      key: 'lastRefreshedAt',
      width: 150,
      render: (value: string | null) => formatMyobTime(value),
    },
    {
      title: t('myob.connections.columns.stores'),
      key: 'stores',
      width: 110,
      render: (_, record) => {
        if (record.storeCodes.length === 0) {
          return <span className="myob-muted">{t('myob.connections.noStores')}</span>
        }
        const lines = describeMappedStores(record.storeCodes, stores)
        return (
          <Tooltip
            title={
              <div>
                {lines.map((line) => (
                  <div key={line}>{line}</div>
                ))}
              </div>
            }
          >
            <span style={{ cursor: 'help', borderBottom: '1px dotted currentColor' }}>
              {t('myob.connections.storeCount', { count: record.storeCodes.length })}
            </span>
          </Tooltip>
        )
      },
    },
    {
      title: t('myob.connections.columns.actions'),
      key: 'actions',
      fixed: 'right',
      width: 250,
      render: (_, record) => {
        const rowBusy = busy[record.connectionGuid]
        const isDisconnected = record.status === 'Disconnected'
        return (
          <div className="myob-actions">
            {isDisconnected ? null : (
              <Button
                type="link"
                size="small"
                icon={<SafetyCertificateOutlined />}
                loading={rowBusy === 'verify'}
                disabled={Boolean(rowBusy) || authorizing}
                onClick={() => void handleVerify(record)}
              >
                {t('myob.actions.verify')}
              </Button>
            )}
            {/* 重新授权 = 再次发起授权；授权页由 Administrator 选择公司文件 */}
            <Button
              type="link"
              size="small"
              icon={<LinkOutlined />}
              disabled={Boolean(rowBusy) || authorizing}
              onClick={onAuthorize}
            >
              {t('myob.actions.reauthorize')}
            </Button>
            {isDisconnected ? null : (
              <Button
                type="link"
                size="small"
                danger
                icon={<DisconnectOutlined />}
                loading={rowBusy === 'disconnect'}
                disabled={Boolean(rowBusy) || authorizing}
                onClick={() => handleDisconnect(record)}
              >
                {t('myob.actions.disconnect')}
              </Button>
            )}
          </div>
        )
      },
    },
  ]

  const renderTable = (rows: MyobConnection[], metricId: string, tableLoading = false) => (
    <MeasuredTable<MyobConnection>
      metricId={metricId}
      rowKey="connectionGuid"
      size="middle"
      columns={columns}
      dataSource={rows}
      loading={tableLoading}
      pagination={false}
      // 窄屏横向滚动；操作列固定在右侧
      scroll={{ x: 'max-content' }}
      locale={{ emptyText: <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description={t('myob.connections.empty')} /> }}
    />
  )

  return (
    <section className="myob-card" aria-labelledby="myob-connections-title">
      <div className="myob-card-head">
        <h2 className="myob-card-title" id="myob-connections-title">
          {t('myob.connections.title')}
        </h2>
        <Space wrap>
          <Button icon={<ReloadOutlined />} loading={loading} onClick={onReload}>
            {t('myob.reload')}
          </Button>
          <Button type="primary" icon={<LinkOutlined />} loading={authorizing} onClick={onAuthorize}>
            {t('myob.connections.connect')}
          </Button>
        </Space>
      </div>

      {error ? (
        <Alert
          type="error"
          showIcon
          style={{ marginBottom: 12 }}
          message={t('myob.connections.loadFailed')}
          description={error}
          action={
            <Button size="small" onClick={onReload}>
              {t('myob.connections.retry')}
            </Button>
          }
        />
      ) : null}

      {/* 首次加载且还没有数据时显示加载态；刷新时保留旧数据避免闪空 */}
      {renderTable(visible, 'pos-admin.myob.connections', loading && connections.length === 0)}

      {disconnected.length > 0 ? (
        <Collapse
          className="myob-collapse"
          size="small"
          ghost
          items={[
            {
              key: 'disconnected',
              label: t('myob.connections.disconnectedTitle', { count: disconnected.length }),
              children: renderTable(disconnected, 'pos-admin.myob.connections-disconnected'),
            },
          ]}
        />
      ) : null}
    </section>
  )
}
