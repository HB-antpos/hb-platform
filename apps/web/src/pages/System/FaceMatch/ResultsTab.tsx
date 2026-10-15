import { PictureOutlined, ReloadOutlined } from '@ant-design/icons'
import { Alert, Badge, Button, Checkbox, DatePicker, Input, Segmented, Select, Space, Tag, Typography, theme } from 'antd'
import type { ColumnsType } from 'antd/es/table'
import dayjs, { type Dayjs } from 'dayjs'
import { useCallback, useMemo, useState } from 'react'
import { useTranslation } from 'react-i18next'

import { MeasuredTable } from '../../../components/MeasuredTable'
import { getFaceCameras } from '../../../services/faceCameraService'
import { getFaceCaptureResults, getFaceHitSummary, markFaceHitRead } from '../../../services/faceMatchService'
import type { FaceCamera } from '../../../types/faceCamera'
import type { FaceCaptureResult, FaceHitSummary, FaceMatchPage } from '../../../types/faceMatch'
import { formatDateTime } from '../FaceCameras/logic'
import {
  BAND_COLORS,
  BAND_LABEL_KEYS,
  PAGE_SIZE,
  RESULT_DECISION_LABEL_KEYS,
  RESULT_DECISION_VALUES,
  buildResultsQuery,
  defaultResultsRange,
  describeFaceMatchError,
  formatResultPercent,
  resolveResultDecision,
  type ResultsFilters,
  type ResultsTimeRange,
} from './logic'
import HitsView from './HitsView'
import ResultDetailDrawer from './ResultDetailDrawer'
import { NUMERIC_STYLE, NotifyStateCell, Pager, StoreSelect, type FaceMatchTabContext } from './shared'
import { useRemote } from './useRemote'

const RANGE_FORMAT = 'YYYY-MM-DD HH:mm'

type ResultsView = 'all' | 'hits'

// 未读命中汇总（不带门店 / 时间条件 = 当前登录人在自己门店范围内的全部未读命中通知，口径同 App 红色数字）。
const loadUnreadSummary = (signal: AbortSignal) => getFaceHitSummary({}, signal)

/**
 * 抓拍与比对页签（FaceCapture.Images.View），顶部分段切换两个视图：
 * 1. 全部抓拍：Capture 与 Match 模式留存的抓拍 + 评估分数 + 名单命中与通知发送情况。
 *    列表里没有任何真实照片（缩略图位置只是占位图标）：点「查看」打开详情抽屉后才按需读取图片，每次读取服务端都写审计。
 * 2. 名单命中：以命中任务为主表（改版前 Match 期间没有抓拍记录的命中也在），见 HitsView。
 */
export default function ResultsTab({ context }: { context: FaceMatchTabContext }) {
  const { t } = useTranslation()
  const { token } = theme.useToken()
  const { storeOptions, storeOptionsFailed, goToReview, canViewTarget } = context
  const [view, setView] = useState<ResultsView>('all')
  const unreadSummary = useRemote<FaceHitSummary>(loadUnreadSummary)
  const unreadCount = unreadSummary.data?.unread ?? 0
  const reloadUnread = unreadSummary.reload
  const [filters, setFilters] = useState<ResultsFilters>(() => ({
    storeCode: null,
    cameraId: null,
    decision: null,
    hitOnly: false,
    range: defaultResultsRange(),
  }))
  // 摄像机编号手填时的草稿：失焦 / 回车才生效，避免每敲一个字就发一次请求。
  const [cameraDraft, setCameraDraft] = useState('')
  const [page, setPage] = useState(1)
  const [target, setTarget] = useState<FaceCaptureResult | null>(null)

  // 改任何筛选条件都回到第一页。
  const patchFilters = useCallback((patch: Partial<ResultsFilters>) => {
    setFilters((current) => ({ ...current, ...patch }))
    setPage(1)
  }, [])

  const loader = useMemo(
    () => (signal: AbortSignal) => getFaceCaptureResults(buildResultsQuery(filters, page, PAGE_SIZE), signal),
    [filters, page],
  )
  const results = useRemote<FaceMatchPage<FaceCaptureResult>>(view === 'all' ? loader : null)
  const items = useMemo(() => results.data?.items ?? [], [results.data])

  // 打开详情：未读的命中顺手标为已读（与 App 打开详情一致），成功后刷新分段上的红色数字；失败静默。
  const openDetail = useCallback(
    (record: FaceCaptureResult) => {
      setTarget(record)
      if (record.hit?.unread) {
        markFaceHitRead(record.hit.taskId).then(reloadUnread, () => undefined)
      }
    },
    [reloadUnread],
  )

  // 摄像机下拉：摄像机列表需要摄像机管理权限，拿不到（403 等）时降级成手填摄像机编号。
  const cameraLoader = useCallback((signal: AbortSignal) => getFaceCameras(signal), [])
  const cameras = useRemote(cameraLoader)
  const cameraListFailed = cameras.error !== null && !cameras.data
  const cameraOptions = useMemo(
    () =>
      (cameras.data?.items ?? [])
        .filter((camera: FaceCamera) => !filters.storeCode || camera.storeCode === filters.storeCode)
        .map((camera: FaceCamera) => ({ value: camera.cameraId, label: camera.cameraId })),
    [cameras.data, filters.storeCode],
  )

  const presets = useMemo(
    () => [
      { label: t('faceMatch.results.filters.presetToday'), value: () => rangeTuple(defaultResultsRange()) },
      { label: t('faceMatch.results.filters.presetLast24h'), value: () => [dayjs().subtract(24, 'hour'), dayjs()] as [Dayjs, Dayjs] },
      { label: t('faceMatch.results.filters.presetLast7d'), value: () => [dayjs().subtract(7, 'day').startOf('day'), dayjs()] as [Dayjs, Dayjs] },
    ],
    [t],
  )

  const columns = useMemo<ColumnsType<FaceCaptureResult>>(() => {
    const base: ColumnsType<FaceCaptureResult> = [
      {
        title: t('faceMatch.results.columns.photo'),
        key: 'photo',
        width: 76,
        // 占位图标：列表不加载真实照片，点「查看」才会读取（并写审计）。
        render: () => (
          <div
            title={t('faceMatch.results.photoPlaceholder')}
            style={{ width: 40, height: 40, borderRadius: token.borderRadius, background: token.colorFillTertiary, color: token.colorTextTertiary, display: 'flex', alignItems: 'center', justifyContent: 'center' }}
          >
            <PictureOutlined style={{ fontSize: 20 }} />
          </div>
        ),
      },
      {
        title: t('faceMatch.results.columns.capturedAt'),
        dataIndex: 'capturedAtUtc',
        width: 170,
        render: (value: string) => <span style={NUMERIC_STYLE}>{formatDateTime(value)}</span>,
      },
      {
        title: t('faceMatch.results.columns.storeCamera'),
        key: 'storeCamera',
        width: 210,
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
        title: t('faceMatch.results.columns.decision'),
        key: 'decision',
        width: 140,
        render: (_: unknown, record) => {
          const decision = resolveResultDecision(record)
          return (
            <Space size={4} wrap>
              <Tag color={decision.color} style={{ marginInlineEnd: 0 }}>{t(decision.labelKey)}</Tag>
              {record.isDuplicate ? <Tag style={{ marginInlineEnd: 0 }}>{t('faceMatch.results.duplicateTag')}</Tag> : null}
            </Space>
          )
        },
      },
      {
        title: t('faceMatch.results.columns.staffScore'),
        key: 'staffScore',
        width: 110,
        render: (_: unknown, record) => <span style={NUMERIC_STYLE}>{formatResultPercent(record.staffScorePercent)}</span>,
      },
      {
        title: t('faceMatch.results.columns.visitScore'),
        key: 'visitScore',
        width: 110,
        render: (_: unknown, record) => <span style={NUMERIC_STYLE}>{formatResultPercent(record.visitScorePercent)}</span>,
      },
      {
        title: t('faceMatch.results.columns.hit'),
        key: 'hit',
        width: 130,
        render: (_: unknown, record) =>
          record.hit ? (
            <Space size={6}>
              <Typography.Text strong style={NUMERIC_STYLE}>{formatResultPercent(record.hit.scorePercent)}</Typography.Text>
              {/* 档位只认后端给出的值：接口没给就不显示，页面不自己用阈值划档。 */}
              {record.hit.band ? <Tag color={BAND_COLORS[record.hit.band]} style={{ marginInlineEnd: 0 }}>{t(BAND_LABEL_KEYS[record.hit.band])}</Tag> : null}
            </Space>
          ) : (
            '—'
          ),
      },
      {
        title: t('faceMatch.results.columns.notify'),
        key: 'notify',
        width: 230,
        render: (_: unknown, record) =>
          record.hit ? (
            <NotifyStateCell state={record.hit.notifyState} recipients={record.hit.notifyRecipientCount} read={record.hit.notifyReadCount} />
          ) : (
            '—'
          ),
      },
      {
        title: t('faceMatch.common.actions'),
        key: 'actions',
        width: 80,
        fixed: 'right',
        render: (_: unknown, record) => (
          <Button type="link" size="small" onClick={() => openDetail(record)}>
            {t('faceMatch.results.view')}
          </Button>
        ),
      },
    ]
    // 命中行淡红底：逐格设背景（固定列也跟着变色）。
    return base.map((column) => ({
      ...column,
      onCell: (record: FaceCaptureResult) => (record.hit ? { style: { background: token.colorErrorBg } } : {}),
    }))
  }, [t, token, openDetail])

  const viewSwitch = (
    <Segmented<ResultsView>
      value={view}
      onChange={setView}
      options={[
        { value: 'all', label: t('faceMatch.results.views.all') },
        {
          value: 'hits',
          label: (
            <Space size={6}>
              <span>{t('faceMatch.results.views.hits')}</span>
              {unreadCount > 0 ? <Badge count={unreadCount} size="small" overflowCount={99} title={t('faceMatch.results.views.unreadTitle', { count: unreadCount })} /> : null}
            </Space>
          ),
        },
      ]}
    />
  )

  if (view === 'hits') {
    return (
      <Space direction="vertical" size={12} style={{ width: '100%' }}>
        <Alert type="info" showIcon message={t('faceMatch.results.intro')} />
        <HitsView context={context} viewSwitch={viewSwitch} onUnreadChanged={reloadUnread} />
      </Space>
    )
  }

  return (
    <Space direction="vertical" size={12} style={{ width: '100%' }}>
      <Alert type="info" showIcon message={t('faceMatch.results.intro')} />
      <Space wrap>
        {viewSwitch}
        <StoreSelect
          options={storeOptions}
          failed={storeOptionsFailed}
          value={filters.storeCode}
          onChange={(value) => {
            // 换门店时摄像机筛选作废（摄像机属于某个门店）。
            setCameraDraft('')
            patchFilters({ storeCode: value, cameraId: null })
          }}
          placeholder={t('faceMatch.results.filters.allStores')}
          style={{ width: 200 }}
        />
        {cameraListFailed ? (
          <Input
            allowClear
            style={{ width: 180 }}
            maxLength={64}
            placeholder={t('faceMatch.results.filters.allCameras')}
            value={cameraDraft}
            onChange={(event) => {
              setCameraDraft(event.target.value)
              if (!event.target.value) patchFilters({ cameraId: null })
            }}
            onBlur={() => patchFilters({ cameraId: cameraDraft.trim() || null })}
            onPressEnter={() => patchFilters({ cameraId: cameraDraft.trim() || null })}
          />
        ) : (
          <Select
            allowClear
            showSearch
            style={{ width: 180 }}
            loading={cameras.loading && !cameras.data}
            placeholder={t('faceMatch.results.filters.allCameras')}
            value={filters.cameraId ?? undefined}
            options={cameraOptions}
            onChange={(value: string | undefined) => patchFilters({ cameraId: value ?? null })}
          />
        )}
        <Select
          allowClear
          style={{ width: 150 }}
          placeholder={t('faceMatch.results.filters.allDecisions')}
          value={filters.decision ?? undefined}
          options={RESULT_DECISION_VALUES.map((value) => ({ value, label: t(RESULT_DECISION_LABEL_KEYS[value]) }))}
          onChange={(value: string | undefined) => patchFilters({ decision: value ?? null })}
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
        <Checkbox checked={filters.hitOnly} onChange={(event) => patchFilters({ hitOnly: event.target.checked })}>
          {t('faceMatch.results.filters.hitOnly')}
        </Checkbox>
        <Button icon={<ReloadOutlined />} loading={results.loading} onClick={results.reload}>
          {t('faceMatch.common.refresh')}
        </Button>
      </Space>
      {results.error ? (
        <Alert type="error" showIcon message={describeFaceMatchError(t, results.error)} action={<Button size="small" onClick={results.reload}>{t('faceMatch.common.retry')}</Button>} />
      ) : null}
      <MeasuredTable<FaceCaptureResult>
        metricId="system.face-match.results-table-1"
        rowKey="captureId"
        size="small"
        loading={results.loading && !results.data}
        dataSource={items}
        columns={columns}
        pagination={false}
        scroll={{ x: 1250 }}
        locale={{ emptyText: t('faceMatch.results.empty') }}
      />
      <Pager page={page} hasMore={results.data?.hasMore === true} loading={results.loading} onChange={setPage} />
      <Typography.Text type="secondary" style={{ fontSize: 12 }}>
        {t('faceMatch.results.footnote')}
      </Typography.Text>

      {/* 仅点击「查看」后挂载；以 captureId 作 key，切换抓拍即重新挂载，旧图片 URL 随之撤销。 */}
      {target ? (
        <ResultDetailDrawer
          key={target.captureId}
          result={target}
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

function rangeTuple(range: ResultsTimeRange): [Dayjs, Dayjs] {
  return [range.start, range.end]
}
