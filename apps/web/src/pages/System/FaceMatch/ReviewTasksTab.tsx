import { ReloadOutlined } from '@ant-design/icons'
import { Alert, Button, Select, Space, Tag, Typography } from 'antd'
import type { ColumnsType } from 'antd/es/table'
import { useMemo, useState } from 'react'
import { useTranslation } from 'react-i18next'

import { MeasuredTable } from '../../../components/MeasuredTable'
import { getUnwelcomeTasks } from '../../../services/faceMatchService'
import type { FaceMatchPage, FaceUnwelcomeTask } from '../../../types/faceMatch'
import { formatDateTime } from '../FaceCameras/logic'
import {
  BAND_COLORS,
  BAND_LABEL_KEYS,
  PAGE_SIZE,
  REVIEW_STATUS_COLORS,
  REVIEW_STATUS_FILTER_OPTIONS,
  REVIEW_STATUS_LABEL_KEYS,
  REVIEW_STATUS_PENDING,
  REVIEW_STEP_HINT_KEYS,
  describeFaceMatchError,
  formatSimilarityPercent,
  getReviewStep,
} from './logic'
import { NUMERIC_STYLE, Pager, StoreSelect, type FaceMatchTabContext } from './shared'
import ReviewTaskModal from './ReviewTaskModal'
import { useRemote } from './useRemote'

/**
 * 复核任务页签（Review）：任务列表 + 复核弹框。
 * 列表不含姓名、事件细节，也不含任何图片；图片只在打开复核弹框后按需读取。
 */
export default function ReviewTasksTab({ context }: { context: FaceMatchTabContext }) {
  const { t } = useTranslation()
  const { storeOptions, storeOptionsFailed, writeBlocked } = context
  const [status, setStatus] = useState<number | undefined>(REVIEW_STATUS_PENDING)
  const [storeCode, setStoreCode] = useState<string | null>(null)
  const [page, setPage] = useState(1)
  const [target, setTarget] = useState<FaceUnwelcomeTask | null>(null)

  const loader = useMemo(
    () => (signal: AbortSignal) => getUnwelcomeTasks({ status, storeCode: storeCode ?? undefined, page, pageSize: PAGE_SIZE }, signal),
    [status, storeCode, page],
  )
  const tasks = useRemote<FaceMatchPage<FaceUnwelcomeTask>>(loader)

  const columns = useMemo<ColumnsType<FaceUnwelcomeTask>>(
    () => [
      {
        title: t('faceMatch.review.columns.capturedAt'),
        dataIndex: 'capturedAtUtc',
        width: 170,
        render: (value: string) => <span style={NUMERIC_STYLE}>{formatDateTime(value)}</span>,
      },
      {
        title: t('faceMatch.common.store'),
        key: 'store',
        width: 200,
        ellipsis: true,
        render: (_: unknown, record) => (
          <span>
            <Typography.Text strong>{record.storeCode}</Typography.Text>
            {record.storeName ? <Typography.Text type="secondary"> · {record.storeName}</Typography.Text> : null}
          </span>
        ),
      },
      {
        // 列头直接写明口径：这是相似度，不等于是同一个人的概率。
        title: t('faceMatch.review.columns.similarity'),
        key: 'similarity',
        width: 190,
        render: (_: unknown, record) => (
          <Space size={6}>
            <Typography.Text strong style={NUMERIC_STYLE}>{formatSimilarityPercent(record)}%</Typography.Text>
            <Tag color={BAND_COLORS[record.band]} style={{ marginInlineEnd: 0 }}>{t(BAND_LABEL_KEYS[record.band])}</Tag>
          </Space>
        ),
      },
      {
        title: t('faceMatch.common.status'),
        key: 'status',
        width: 200,
        render: (_: unknown, record) => (
          <Space size={4} wrap>
            <Tag color={REVIEW_STATUS_COLORS[record.reviewStatus] ?? 'default'} style={{ marginInlineEnd: 0 }}>
              {REVIEW_STATUS_LABEL_KEYS[record.reviewStatus] ? t(REVIEW_STATUS_LABEL_KEYS[record.reviewStatus]) : record.reviewStatusName}
            </Tag>
            {!record.imageAvailable ? <Tag style={{ marginInlineEnd: 0 }}>{t('faceMatch.review.imageDeleted')}</Tag> : null}
          </Space>
        ),
      },
      {
        title: t('faceMatch.review.columns.nextStep'),
        key: 'nextStep',
        width: 240,
        ellipsis: true,
        render: (_: unknown, record) => t(REVIEW_STEP_HINT_KEYS[getReviewStep(record)]),
      },
      {
        title: t('faceMatch.review.columns.reviewers'),
        key: 'reviewers',
        width: 220,
        render: (_: unknown, record) =>
          record.firstReviewerName ? (
            <div>
              <div>{t('faceMatch.review.reviewerFirst', { name: record.firstReviewerName })}</div>
              {record.secondReviewerName ? <div>{t('faceMatch.review.reviewerSecond', { name: record.secondReviewerName })}</div> : null}
            </div>
          ) : (
            '-'
          ),
      },
      {
        title: t('faceMatch.common.actions'),
        key: 'actions',
        width: 110,
        fixed: 'right',
        render: (_: unknown, record) => (
          <Button type="link" size="small" disabled={!record.imageAvailable} onClick={() => setTarget(record)}>
            {record.canReview && !writeBlocked ? t('faceMatch.review.actions.review') : t('faceMatch.review.actions.view')}
          </Button>
        ),
      },
    ],
    [t, writeBlocked],
  )

  return (
    <Space direction="vertical" size={12} style={{ width: '100%' }}>
      <Alert type="warning" showIcon message={t('faceMatch.review.intro')} />
      <Space wrap>
        <Select
          allowClear
          style={{ width: 160 }}
          placeholder={t('faceMatch.review.allStatuses')}
          value={status}
          options={REVIEW_STATUS_FILTER_OPTIONS.map((option) => ({ value: option.value, label: t(option.labelKey) }))}
          onChange={(value: number | undefined) => {
            setStatus(value)
            setPage(1)
          }}
        />
        <StoreSelect
          options={storeOptions}
          failed={storeOptionsFailed}
          value={storeCode}
          onChange={(value) => {
            setStoreCode(value)
            setPage(1)
          }}
          placeholder={t('faceMatch.review.allStores')}
          style={{ width: 220 }}
        />
        <Button icon={<ReloadOutlined />} loading={tasks.loading} onClick={tasks.reload}>
          {t('faceMatch.common.refresh')}
        </Button>
      </Space>
      {tasks.error ? (
        <Alert type="error" showIcon message={describeFaceMatchError(t, tasks.error)} action={<Button size="small" onClick={tasks.reload}>{t('faceMatch.common.retry')}</Button>} />
      ) : null}
      <MeasuredTable<FaceUnwelcomeTask>
        metricId="system.face-match.review-tasks-table-1"
        rowKey="taskId"
        size="small"
        loading={tasks.loading && !tasks.data}
        dataSource={tasks.data?.items ?? []}
        columns={columns}
        pagination={false}
        scroll={{ x: 1300 }}
        locale={{ emptyText: t('faceMatch.review.empty') }}
      />
      <Pager page={page} hasMore={tasks.data?.hasMore === true} loading={tasks.loading} onChange={setPage} />

      {/* 仅点击「复核」后挂载；以 taskId 作 key，切换任务即重新挂载，旧图片 URL 随之撤销。 */}
      {target ? (
        <ReviewTaskModal
          key={target.taskId}
          task={target}
          writeBlocked={writeBlocked}
          onReviewed={tasks.reload}
          onClose={() => setTarget(null)}
        />
      ) : null}
    </Space>
  )
}
