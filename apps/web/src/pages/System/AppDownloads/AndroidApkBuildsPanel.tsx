import { useEffect, useMemo, useRef, useState } from 'react'
import { useTranslation } from 'react-i18next'
import {
  Alert,
  Button,
  Card,
  Descriptions,
  Empty,
  Modal,
  QRCode,
  Segmented,
  Space,
  Tag,
  Typography,
  message,
} from 'antd'
import type { ColumnsType } from 'antd/es/table'
import {
  CopyOutlined,
  LinkOutlined,
  QrcodeOutlined,
  ReloadOutlined,
} from '@ant-design/icons'
import {
  getLatestMobileAppBuild,
  getMobileAppBuilds,
} from '../../../services/mobileAppBuildService'
import type { MobileAppBuild } from '../../../types/mobileAppBuild'
import {
  APP_DOWNLOAD_PROFILES,
  DEFAULT_APP_DOWNLOAD_PROFILE,
  buildAppDownloadQuery,
  normalizeAppDownloadProfile,
  resolveAppDownloadMirrorStatus,
  resolveAppDownloadSource,
  resolveAppDownloadContentState,
  type AppDownloadMirrorStatus,
  type AppDownloadAppKey,
  type AppDownloadProfile,
} from './logic'
import { formatAppDownloadLocalDateTime } from './time'
import { MeasuredTable } from '../../../components/MeasuredTable'

interface AndroidApkBuildsPanelProps {
  /** 所属终端的应用：Mobile 员工端或手持收银端，由所在轨道决定，不再在面板内切换。 */
  appKey: AppDownloadAppKey
  /** 外部刷新信号：数值变化时按当前筛选重新加载。 */
  refreshVersion?: number
}

function formatVersion(build?: MobileAppBuild | null) {
  if (!build) {
    return '--'
  }
  const version = build.appVersion || '--'
  const buildVersion = build.appBuildVersion ? ` (${build.appBuildVersion})` : ''
  return `${version}${buildVersion}`
}

function formatShortCommit(value?: string | null) {
  return value ? value.slice(0, 8) : '--'
}

function getStatusColor(status?: string | null) {
  switch ((status ?? '').toLowerCase()) {
    case 'finished':
    case 'success':
    case 'completed':
      return 'green'
    case 'errored':
    case 'failed':
    case 'canceled':
    case 'cancelled':
      return 'red'
    case 'in-progress':
    case 'running':
    case 'pending':
      return 'processing'
    default:
      return 'default'
  }
}

function getMirrorStatusColor(status: AppDownloadMirrorStatus) {
  switch (status) {
    case 'succeeded':
      return 'green'
    case 'running':
      return 'processing'
    case 'failed':
      return 'orange'
    case 'unsafe':
      return 'red'
    case 'pending':
      return 'default'
    default:
      return 'default'
  }
}

/** Android APK：最新可下载包 + 构建历史（原 App 下载页的两块卡片）。 */
export default function AndroidApkBuildsPanel({ appKey, refreshVersion = 0 }: AndroidApkBuildsPanelProps) {
  const { t } = useTranslation()
  const [latest, setLatest] = useState<MobileAppBuild | null>(null)
  const [items, setItems] = useState<MobileAppBuild[]>([])
  const [buildLoading, setBuildLoading] = useState(false)
  const [page, setPage] = useState(1)
  const [pageSize, setPageSize] = useState(10)
  const [total, setTotal] = useState(0)
  const [profile, setProfile] = useState<AppDownloadProfile>(DEFAULT_APP_DOWNLOAD_PROFILE)
  const [loadFailed, setLoadFailed] = useState(false)
  const [qrBuild, setQrBuild] = useState<MobileAppBuild | null>(null)
  const buildLoadRequestIdRef = useRef(0)
  const profileRef = useRef(profile)
  const pageSizeRef = useRef(pageSize)
  profileRef.current = profile
  pageSizeRef.current = pageSize

  async function copyText(
    value: string | null | undefined,
    successMessage: string,
    failedMessage: string,
  ) {
    if (!value) {
      return
    }

    try {
      await navigator.clipboard.writeText(value)
      message.success(successMessage)
    } catch (error) {
      console.error(failedMessage, error)
      message.error(failedMessage)
    }
  }

  async function copyLink(url?: string | null) {
    await copyText(
      url,
      t('system.appDownloads.copySuccess'),
      t('system.appDownloads.copyFailed'),
    )
  }

  function openLink(url?: string | null) {
    if (!url) {
      return
    }
    window.open(url, '_blank', 'noopener,noreferrer')
  }

  async function loadBuildData(
    nextPage: number,
    nextPageSize: number,
    nextProfile: AppDownloadProfile,
  ) {
    const query = buildAppDownloadQuery(nextProfile, nextPage, nextPageSize, appKey)
    const requestId = buildLoadRequestIdRef.current + 1
    buildLoadRequestIdRef.current = requestId
    setBuildLoading(true)
    setLoadFailed(false)
    try {
      const [latestBuild, history] = await Promise.all([
        getLatestMobileAppBuild(query.profile, query.appKey),
        getMobileAppBuilds(query),
      ])
      if (requestId !== buildLoadRequestIdRef.current) {
        return
      }
      setLatest(latestBuild)
      setItems(history.items)
      setTotal(history.total)
      setPage(history.page)
      setPageSize(history.pageSize)
      setProfile(query.profile)
    } catch (error) {
      if (requestId !== buildLoadRequestIdRef.current) {
        return
      }
      console.error(t('system.appDownloads.loadFailed'), error)
      setLatest(null)
      setItems([])
      setTotal(0)
      setPage(1)
      setLoadFailed(true)
      message.error(t('system.appDownloads.loadFailed'))
    } finally {
      // 只允许最后一次请求收尾，避免旧请求晚返回时关闭新请求的 loading 或覆盖状态。
      if (requestId === buildLoadRequestIdRef.current) {
        setBuildLoading(false)
      }
    }
  }

  useEffect(() => {
    // 首次挂载与外部刷新都回到第一页，保留当前 profile 与每页条数。
    void loadBuildData(1, pageSizeRef.current, profileRef.current)
    return () => {
      // 卸载后让在途请求失效，避免切换轨道后旧请求回写。
      buildLoadRequestIdRef.current += 1
    }
    // loadBuildData 每次渲染都会重建；这里只应在切换应用或外部刷新时重新加载。
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [appKey, refreshVersion])

  function handleProfileChange(value: string | number) {
    const nextProfile = normalizeAppDownloadProfile(value)
    setProfile(nextProfile)
    void loadBuildData(1, pageSize, nextProfile)
  }

  const profileOptions = useMemo(
    () =>
      APP_DOWNLOAD_PROFILES.map((value) => ({
        label: t(`system.appDownloads.profiles.${value}`),
        value,
      })),
    [t],
  )

  const columns = useMemo<ColumnsType<MobileAppBuild>>(
    () => [
      {
        title: t('system.appDownloads.profile'),
        dataIndex: 'buildProfile',
        width: 130,
        render: (value: string | null | undefined) => value || '--',
      },
      {
        title: t('system.appDownloads.versionBuild'),
        key: 'version',
        width: 160,
        render: (_value, record) => formatVersion(record),
      },
      {
        title: t('system.appDownloads.status'),
        dataIndex: 'status',
        width: 130,
        render: (value: string | null | undefined) => (
          <Tag color={getStatusColor(value)}>{value || '--'}</Tag>
        ),
      },
      {
        title: t('system.appDownloads.completedAt'),
        dataIndex: 'completedAt',
        width: 190,
        render: (value: string | null | undefined) => formatAppDownloadLocalDateTime(value),
      },
      {
        title: t('system.appDownloads.expirationDate'),
        dataIndex: 'expirationDate',
        width: 190,
        render: (value: string | null | undefined) => formatAppDownloadLocalDateTime(value),
      },
      {
        title: t('system.appDownloads.downloadSource'),
        key: 'downloadSource',
        width: 130,
        render: (_value, record) => (
          <Tag>{t(`system.appDownloads.downloadSources.${resolveAppDownloadSource(record)}`)}</Tag>
        ),
      },
      {
        title: t('system.appDownloads.mirrorStatus'),
        key: 'mirrorStatus',
        width: 140,
        render: (_value, record) => {
          const mirrorStatus = resolveAppDownloadMirrorStatus(record)
          return (
            <Tag color={getMirrorStatusColor(mirrorStatus)}>
              {t(`system.appDownloads.mirrorStatuses.${mirrorStatus}`)}
            </Tag>
          )
        },
      },
      {
        title: t('system.appDownloads.mirrorError'),
        dataIndex: 'cosMirrorError',
        width: 220,
        render: (value: string | null | undefined) => (
          <Typography.Text
            type={value ? 'danger' : undefined}
            ellipsis={{ tooltip: value || undefined }}
            style={{ maxWidth: 200 }}
          >
            {value || '--'}
          </Typography.Text>
        ),
      },
      {
        title: t('system.appDownloads.commit'),
        dataIndex: 'gitCommitHash',
        width: 160,
        render: (value: string | null | undefined, record) => (
          <Typography.Text title={record.gitCommitMessage || undefined}>
            {formatShortCommit(value)}
          </Typography.Text>
        ),
      },
      {
        title: t('column.action'),
        key: 'actions',
        width: 280,
        fixed: 'right',
        render: (_value, record) => (
          <Space wrap>
            <Button
              size="small"
              icon={<LinkOutlined />}
              disabled={!record.artifactUrl}
              onClick={() => openLink(record.artifactUrl)}
            >
              {t('system.appDownloads.openDownload')}
            </Button>
            <Button
              size="small"
              icon={<CopyOutlined />}
              disabled={!record.artifactUrl}
              onClick={() => void copyLink(record.artifactUrl)}
            >
              {t('system.appDownloads.copyLink')}
            </Button>
            <Button
              size="small"
              icon={<QrcodeOutlined />}
              disabled={!record.artifactUrl}
              onClick={() => setQrBuild(record)}
            >
              {t('system.appDownloads.viewQrCode')}
            </Button>
          </Space>
        ),
      },
    ],
    [t],
  )

  const latestActions = (
    <Space wrap>
      <Segmented
        size="small"
        value={profile}
        options={profileOptions}
        onChange={handleProfileChange}
      />
      <Button
        icon={<CopyOutlined />}
        disabled={!latest?.artifactUrl}
        onClick={() => void copyLink(latest?.artifactUrl)}
      >
        {t('system.appDownloads.copyLink')}
      </Button>
      <Button
        type="primary"
        icon={<LinkOutlined />}
        disabled={!latest?.artifactUrl}
        onClick={() => openLink(latest?.artifactUrl)}
      >
        {t('system.appDownloads.openDownload')}
      </Button>
      <Button
        disabled={!latest?.buildDetailsPageUrl}
        onClick={() => openLink(latest?.buildDetailsPageUrl)}
      >
        {t('system.appDownloads.buildDetails')}
      </Button>
      <Button icon={<ReloadOutlined />} loading={buildLoading} onClick={() => void loadBuildData(1, pageSize, profile)}>
        {t('common.refresh')}
      </Button>
    </Space>
  )

  const contentState = resolveAppDownloadContentState(
    loadFailed,
    Boolean(latest?.artifactUrl),
    items.length,
  )

  return (
    <Space direction="vertical" size={16} style={{ width: '100%' }}>
      <Card title={t('system.appDownloads.latestTitle')} extra={latestActions} loading={buildLoading}>
        {contentState === 'error' ? (
          <Alert
            type="error"
            showIcon
            message={t('system.appDownloads.loadFailed')}
            description={t('system.appDownloads.loadFailedDescription')}
          />
        ) : latest?.artifactUrl ? (
          <Space align="start" size={24} wrap>
            <QRCode value={latest.artifactUrl} size={180} />
            <Descriptions column={2} bordered size="small" style={{ minWidth: 520 }}>
              <Descriptions.Item label={t('system.appDownloads.version')}>
                {formatVersion(latest)}
              </Descriptions.Item>
              <Descriptions.Item label={t('system.appDownloads.profile')}>
                {latest.buildProfile || profile}
              </Descriptions.Item>
              <Descriptions.Item label={t('system.appDownloads.channel')}>
                {latest.channel || '--'}
              </Descriptions.Item>
              <Descriptions.Item label={t('system.appDownloads.runtime')}>
                {latest.runtimeVersion || '--'}
              </Descriptions.Item>
              <Descriptions.Item label={t('system.appDownloads.completedAt')}>
                {formatAppDownloadLocalDateTime(latest.completedAt)}
              </Descriptions.Item>
              <Descriptions.Item label={t('system.appDownloads.expirationDate')}>
                {formatAppDownloadLocalDateTime(latest.expirationDate)}
              </Descriptions.Item>
              <Descriptions.Item label={t('system.appDownloads.downloadSource')}>
                <Tag>{t(`system.appDownloads.downloadSources.${resolveAppDownloadSource(latest)}`)}</Tag>
              </Descriptions.Item>
              <Descriptions.Item label={t('system.appDownloads.mirrorStatus')}>
                {(() => {
                  const mirrorStatus = resolveAppDownloadMirrorStatus(latest)
                  return (
                    <Tag color={getMirrorStatusColor(mirrorStatus)}>
                      {t(`system.appDownloads.mirrorStatuses.${mirrorStatus}`)}
                    </Tag>
                  )
                })()}
              </Descriptions.Item>
              <Descriptions.Item label={t('system.appDownloads.mirrorError')} span={2}>
                <Typography.Text
                  type={latest.cosMirrorError ? 'danger' : undefined}
                  ellipsis={{ tooltip: latest.cosMirrorError || undefined }}
                  style={{ maxWidth: 480 }}
                >
                  {latest.cosMirrorError || '--'}
                </Typography.Text>
              </Descriptions.Item>
              <Descriptions.Item label={t('system.appDownloads.commit')}>
                {formatShortCommit(latest.gitCommitHash)}
              </Descriptions.Item>
            </Descriptions>
          </Space>
        ) : (
          <Empty description={t('system.appDownloads.empty')} />
        )}
      </Card>

      <Card title={t('system.appDownloads.historyTitle')}>
        <MeasuredTable<MobileAppBuild>
          metricId="system.app-downloads.table-1"
          rowKey="id"
          loading={buildLoading}
          columns={columns}
          dataSource={items}
          scroll={{ x: 1730 }}
          locale={{
            emptyText: (
              <Empty
                description={
                  loadFailed
                    ? t('system.appDownloads.loadFailed')
                    : t('system.appDownloads.empty')
                }
              />
            ),
          }}
          pagination={{
            current: page,
            pageSize,
            total,
            showSizeChanger: true,
            onChange: (nextPage, nextPageSize) => void loadBuildData(nextPage, nextPageSize, profile),
          }}
        />
      </Card>

      <Modal
        open={!!qrBuild}
        title={t('system.appDownloads.qrCodeTitle')}
        footer={null}
        onCancel={() => setQrBuild(null)}
        destroyOnHidden
      >
        {qrBuild?.artifactUrl ? (
          <Space direction="vertical" size={16} style={{ width: '100%', alignItems: 'center' }}>
            <QRCode value={qrBuild.artifactUrl} size={220} />
            <Typography.Text copyable>{qrBuild.artifactUrl}</Typography.Text>
          </Space>
        ) : null}
      </Modal>
    </Space>
  )
}
