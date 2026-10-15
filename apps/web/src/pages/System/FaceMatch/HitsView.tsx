import { CheckOutlined, PictureOutlined, ReloadOutlined } from '@ant-design/icons'
import { Alert, Button, DatePicker, Popconfirm, Segmented, Space, Tag, Tooltip, Typography, message, theme } from 'antd'
import type { ColumnsType } from 'antd/es/table'
import dayjs, { type Dayjs } from 'dayjs'
import { useCallback, useMemo, useState, type ReactNode } from 'react'
import { useTranslation } from 'react-i18next'

import { MeasuredTable } from '../../../components/MeasuredTable'
import { getFaceHitSummary, getFaceHits, markAllFaceHitsRead, markFaceHitRead } from '../../../services/faceMatchService'
import type { FaceHitResult, FaceHitStatus, FaceHitSummary, FaceMatchPage } from '../../../types/faceMatch'
import { formatDateTime } from '../FaceCameras/logic'
import {
  HIT_STATUS_LABEL_KEYS,
  HIT_STATUS_VALUES,
  buildHitSummaryQuery,
  buildHitsQuery,
  collectThumbnailTaskIds,
  defaultHitsRange,
  hitToCaptureResult,
  type HitsFilters,
} from './hitsLogic'
import HitThumbnail from './HitThumbnail'
import {
  BAND_COLORS,
  BAND_LABEL_KEYS,
  PAGE_SIZE,
  REVIEW_STATUS_COLORS,
  REVIEW_STATUS_LABEL_KEYS,
  describeFaceMatchError,
  formatResultPercent,
} from './logic'
import ResultDetailDrawer from './ResultDetailDrawer'
import { NUMERIC_STYLE, NotifyStateCell, Pager, StoreSelect, type FaceMatchTabContext } from './shared'
import { useHitThumbnails } from './useHitThumbnails'
import { useRemote } from './useRemote'

const RANGE_FORMAT = 'YYYY-MM-DD HH:mm'

interface HitsViewProps {
  context: FaceMatchTabContext
  /** 顶部「全部抓拍 / 名单命中」分段切换（由 ResultsTab 提供，放在本视图筛选行最前）。 */
  viewSwitch: ReactNode
  /** 标已读 / 全部标为已读之后通知父组件刷新分段上的红色数字。 */
  onUnreadChanged: () => void
}

/**
 * 「名单命中」视图：以命中任务（FaceMatchTask）为主表，口径与 App 的命中流一致——改版前 Match 期间没有抓拍记录的命中也在这里。
 * 1. 列表只有结构化数据（不含姓名、事件细节、对象键）；命中图缩略图一页一次批量请求（服务端一条审计），原图只在详情抽屉里按需读取；
 * 2. 「我的通知」是当前登录人自己的那条命中通知（未读 / 已读 / 不是收件人）；打开详情即把未读标为已读（与 App 一致）；
 * 3. 「全部标为已读」只标命中通知类型（后端 read-all 带 type），且不受本页门店 / 时间筛选限制；
 * 4. 「复核」只给有复核权限（FaceMatch.Unwelcome.Review）的人，点了切到复核任务页签，能否判断仍由后端 canReview 决定。
 */
export default function HitsView({ context, viewSwitch, onUnreadChanged }: HitsViewProps) {
  const { t } = useTranslation()
  const { token } = theme.useToken()
  const { storeOptions, storeOptionsFailed, goToReview, canViewTarget, config } = context
  const canReviewPermission = config.permissions.unwelcomeReview
  const [filters, setFilters] = useState<HitsFilters>(() => ({ storeCode: null, status: 'all', range: defaultHitsRange() }))
  const [page, setPage] = useState(1)
  const [target, setTarget] = useState<FaceHitResult | null>(null)
  // 本页里刚被标为已读的任务（不必为了一个标记重新拉整页）。
  const [readIds, setReadIds] = useState<ReadonlySet<string>>(() => new Set())
  const [markingAll, setMarkingAll] = useState(false)

  const patchFilters = useCallback((patch: Partial<HitsFilters>) => {
    setFilters((current) => ({ ...current, ...patch }))
    setPage(1)
  }, [])

  const loader = useMemo(
    () => (signal: AbortSignal) => getFaceHits(buildHitsQuery(filters, page, PAGE_SIZE), signal),
    [filters, page],
  )
  const hits = useRemote<FaceMatchPage<FaceHitResult>>(loader)
  const summaryFilters = useMemo(() => buildHitSummaryQuery(filters), [filters])
  const summaryLoader = useCallback((signal: AbortSignal) => getFaceHitSummary(summaryFilters, signal), [summaryFilters])
  const summary = useRemote<FaceHitSummary>(summaryLoader)

  const items = useMemo(() => hits.data?.items ?? [], [hits.data])
  const thumbnailIds = useMemo(() => collectThumbnailTaskIds(items.filter((item) => item.imageAvailable).map((item) => item.taskId)), [items])
  const thumbnails = useHitThumbnails(thumbnailIds)

  const isUnread = useCallback((record: FaceHitResult) => record.unread && !readIds.has(record.taskId), [readIds])

  const reloadAll = useCallback(() => {
    setReadIds(new Set())
    hits.reload()
    summary.reload()
  }, [hits, summary])

  const openDetail = useCallback(
    (record: FaceHitResult) => {
      setTarget(record)
      if (!record.unread || readIds.has(record.taskId)) return
      markFaceHitRead(record.taskId).then(
        () => {
          setReadIds((current) => new Set(current).add(record.taskId))
          summary.reload()
          onUnreadChanged()
        },
        () => undefined,
      )
    },
    [readIds, summary, onUnreadChanged],
  )

  const handleMarkAll = async () => {
    setMarkingAll(true)
    try {
      await markAllFaceHitsRead()
      void message.success(t('faceMatch.hits.markAllDone'))
      reloadAll()
      onUnreadChanged()
    } catch (error) {
      void message.error(describeFaceMatchError(t, error))
    } finally {
      setMarkingAll(false)
    }
  }

  const presets = useMemo(
    () => [
      { label: t('faceMatch.results.filters.presetToday'), value: () => [dayjs().startOf('day'), dayjs().endOf('day')] as [Dayjs, Dayjs] },
      { label: t('faceMatch.results.filters.presetLast7d'), value: () => [dayjs().subtract(6, 'day').startOf('day'), dayjs().endOf('day')] as [Dayjs, Dayjs] },
    ],
    [t],
  )

  const columns = useMemo<ColumnsType<FaceHitResult>>(() => {
    const base: ColumnsType<FaceHitResult> = [
      {
        title: t('faceMatch.hits.columns.image'),
        key: 'image',
        width: 76,
        render: (_: unknown, record) =>
          record.imageAvailable ? (
            <HitThumbnail state={thumbnails.get(record.taskId)} />
          ) : (
            // 命中图已过期删除：与缩略图同尺寸的虚线占位，文字放悬停提示，避免标签溢出到相邻列。
            <Tooltip title={t('faceMatch.results.hitImageGone')}>
              <div
                aria-label={t('faceMatch.results.hitImageGone')}
                style={{
                  width: 40,
                  height: 40,
                  borderRadius: token.borderRadius,
                  border: `1px dashed ${token.colorBorder}`,
                  color: token.colorTextQuaternary,
                  display: 'flex',
                  alignItems: 'center',
                  justifyContent: 'center',
                }}
              >
                <PictureOutlined style={{ fontSize: 16 }} />
              </div>
            </Tooltip>
          ),
      },
      {
        title: t('faceMatch.results.columns.capturedAt'),
        dataIndex: 'capturedAtUtc',
        width: 170,
        // 未读不在这里另挂标签：整行淡红底 +「我的通知」列已表示未读，再挂标签会把时间挤成两行。
        render: (value: string) => <span style={NUMERIC_STYLE}>{formatDateTime(value)}</span>,
      },
      {
        title: t('faceMatch.results.columns.storeCamera'),
        key: 'storeCamera',
        width: 180,
        render: (_: unknown, record) => (
          <div>
            <div>
              <Typography.Text strong>{record.storeCode}</Typography.Text>
              {record.storeName ? <Typography.Text type="secondary"> · {record.storeName}</Typography.Text> : null}
            </div>
            <Typography.Text type="secondary" style={{ fontSize: 12 }}>{record.cameraId}</Typography.Text>
          </div>
        ),
      },
      {
        title: t('faceMatch.hits.columns.similarity'),
        key: 'similarity',
        width: 120,
        render: (_: unknown, record) => (
          <Space size={6}>
            <Typography.Text strong style={NUMERIC_STYLE}>{formatResultPercent(record.scorePercent)}</Typography.Text>
            {/* 档位只认后端给出的值：接口没给就不显示，页面不自己用阈值划档。 */}
            {record.band ? <Tag color={BAND_COLORS[record.band]} style={{ marginInlineEnd: 0 }}>{t(BAND_LABEL_KEYS[record.band])}</Tag> : null}
          </Space>
        ),
      },
      {
        title: t('faceMatch.hits.columns.reviewStatus'),
        key: 'reviewStatus',
        width: 150,
        render: (_: unknown, record) => (
          <Space size={4} wrap>
            <Tag color={REVIEW_STATUS_COLORS[record.reviewStatus] ?? 'default'} style={{ marginInlineEnd: 0 }}>
              {REVIEW_STATUS_LABEL_KEYS[record.reviewStatus] ? t(REVIEW_STATUS_LABEL_KEYS[record.reviewStatus]) : record.reviewStatusName}
            </Tag>
            {record.autoConfirmed ? <Tag style={{ marginInlineEnd: 0 }}>{t('faceMatch.review.autoConfirmedTag')}</Tag> : null}
          </Space>
        ),
      },
      {
        title: t('faceMatch.hits.columns.myNotification'),
        key: 'myNotification',
        width: 100,
        // 只看当前登录人自己的那条通知：不是收件人显示「—」。
        render: (_: unknown, record) =>
          record.notificationId === null ? (
            '—'
          ) : isUnread(record) ? (
            <Typography.Text type="danger">{t('faceMatch.hits.myUnread')}</Typography.Text>
          ) : (
            <Typography.Text type="secondary">{t('faceMatch.hits.myRead')}</Typography.Text>
          ),
      },
      {
        title: t('faceMatch.results.columns.notify'),
        key: 'notify',
        width: 210,
        render: (_: unknown, record) => (
          <NotifyStateCell state={record.notifyState} recipients={record.notifyRecipientCount} read={record.notifyReadCount} />
        ),
      },
      {
        title: t('faceMatch.common.actions'),
        key: 'actions',
        width: 120,
        fixed: 'right',
        render: (_: unknown, record) => (
          <Space size={0}>
            <Button type="link" size="small" onClick={() => openDetail(record)}>
              {t('faceMatch.results.view')}
            </Button>
            {/* 「复核」只给有复核权限的人；命中图到期删除后不能再复核。 */}
            {canReviewPermission && goToReview ? (
              <Button type="link" size="small" disabled={!record.imageAvailable} onClick={goToReview}>
                {t('faceMatch.hits.review')}
              </Button>
            ) : null}
          </Space>
        ),
      },
    ]
    return base.map((column) => ({
      ...column,
      onCell: (record: FaceHitResult) => (isUnread(record) ? { style: { background: token.colorErrorBg } } : {}),
    }))
  }, [t, token, thumbnails, isUnread, openDetail, canReviewPermission, goToReview])

  return (
    <Space direction="vertical" size={12} style={{ width: '100%' }}>
      <Space wrap>
        {viewSwitch}
        <Segmented<FaceHitStatus>
          value={filters.status}
          onChange={(status) => patchFilters({ status })}
          options={HIT_STATUS_VALUES.map((status) => {
            const count = summary.data ? (status === 'all' ? summary.data.total : summary.data[status]) : null
            return {
              value: status,
              label: (
                <span>
                  {t(HIT_STATUS_LABEL_KEYS[status])}
                  {count !== null ? <span style={{ ...NUMERIC_STYLE, marginInlineStart: 4 }}>{count}</span> : null}
                </span>
              ),
            }
          })}
        />
        <StoreSelect
          options={storeOptions}
          failed={storeOptionsFailed}
          value={filters.storeCode}
          onChange={(value) => patchFilters({ storeCode: value })}
          placeholder={t('faceMatch.results.filters.allStores')}
          style={{ width: 200 }}
        />
        <DatePicker.RangePicker
          showTime={{ format: 'HH:mm' }}
          format={RANGE_FORMAT}
          allowClear
          presets={presets}
          placeholder={[t('faceMatch.results.filters.rangeStart'), t('faceMatch.results.filters.rangeEnd')]}
          value={filters.range ? [filters.range.start, filters.range.end] : null}
          onChange={(values) => patchFilters({ range: values && values[0] && values[1] ? { start: values[0], end: values[1] } : null })}
        />
        <Popconfirm title={t('faceMatch.hits.markAllConfirm')} okText={t('faceMatch.hits.markAll')} onConfirm={handleMarkAll}>
          <Button icon={<CheckOutlined />} loading={markingAll}>
            {t('faceMatch.hits.markAll')}
          </Button>
        </Popconfirm>
        <Button icon={<ReloadOutlined />} loading={hits.loading} onClick={reloadAll}>
          {t('faceMatch.common.refresh')}
        </Button>
      </Space>
      {hits.error ? (
        <Alert type="error" showIcon message={describeFaceMatchError(t, hits.error)} action={<Button size="small" onClick={hits.reload}>{t('faceMatch.common.retry')}</Button>} />
      ) : null}
      <MeasuredTable<FaceHitResult>
        metricId="system.face-match.hits-table-1"
        rowKey="taskId"
        size="small"
        loading={hits.loading && !hits.data}
        dataSource={items}
        columns={columns}
        pagination={false}
        scroll={{ x: 1126 }}
        locale={{ emptyText: t('faceMatch.hits.empty') }}
      />
      <Pager page={page} hasMore={hits.data?.hasMore === true} loading={hits.loading} onChange={setPage} />
      <Typography.Text type="secondary" style={{ fontSize: 12 }}>
        {t('faceMatch.hits.footnote')}
      </Typography.Text>

      {/* 仅点击「查看」后挂载；以任务编号作 key，切换命中即重新挂载，旧图片 URL 随之撤销。 */}
      {target ? (
        <ResultDetailDrawer
          key={target.taskId}
          result={hitToCaptureResult(target)}
          origin="hits"
          canViewTarget={canViewTarget}
          onGoReview={
            goToReview
              ? () => {
                  setTarget(null)
                  goToReview()
                }
              : undefined
          }
          onClose={() => setTarget(null)}
        />
      ) : null}
    </Space>
  )
}
