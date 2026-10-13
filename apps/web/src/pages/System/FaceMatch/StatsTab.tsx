import { ReloadOutlined } from '@ant-design/icons'
import { Alert, Button, Card, Col, Progress, Row, Space, Statistic, Tag, Typography } from 'antd'
import type { ColumnsType } from 'antd/es/table'
import { useCallback, useMemo } from 'react'
import { useTranslation } from 'react-i18next'

import { MeasuredTable } from '../../../components/MeasuredTable'
import { getUnwelcomeStats } from '../../../services/faceMatchService'
import type { FaceUnwelcomeStats, FaceUnwelcomeStatsBucket, FaceUnwelcomeStatsEntry } from '../../../types/faceMatch'
import {
  describeFaceMatchError,
  computeDismissRate,
  formatRate,
  formatScoreBucket,
  toRatio,
} from './logic'
import { NUMERIC_STYLE } from './shared'
import { useRemote } from './useRemote'

const STATUS_NAME_KEYS: Record<string, string> = {
  pending: 'faceMatch.unwelcome.status.pending',
  active: 'faceMatch.unwelcome.status.active',
  expired: 'faceMatch.unwelcome.status.expired',
  withdrawn: 'faceMatch.unwelcome.status.withdrawn',
}

/** 驳回率进度条：越高越醒目（驳回率高 = 误报多）。 */
function RateBar({ value }: { value: number | null | undefined }) {
  const ratio = toRatio(value)
  if (ratio === null) return <span>-</span>
  return (
    <Space size={8} style={{ width: '100%' }}>
      <Progress percent={Math.round(ratio * 100)} size="small" showInfo={false} status={ratio >= 0.8 ? 'exception' : 'normal'} style={{ width: 90, margin: 0 }} />
      <span style={NUMERIC_STYLE}>{formatRate(ratio)}</span>
    </Space>
  )
}

/**
 * 误报统计页签（任一名单权限）：按条目与相似度区间看驳回率，用于判断某个条目是否该出名单、阈值是否要调。
 * 只有计数与比率，不含任何图片、姓名或事件细节。
 */
export default function StatsTab() {
  const { t } = useTranslation()
  const loader = useCallback((signal: AbortSignal) => getUnwelcomeStats(signal), [])
  const stats = useRemote<FaceUnwelcomeStats>(loader)
  const data = stats.data

  const entryColumns = useMemo<ColumnsType<FaceUnwelcomeStatsEntry>>(
    () => [
      {
        title: t('faceMatch.stats.entry'),
        dataIndex: 'entryId',
        width: 150,
        render: (value: string) => <Typography.Text copyable={{ text: value }} style={{ fontFamily: 'ui-monospace, SFMono-Regular, Menlo, monospace' }}>{value.slice(0, 8)}</Typography.Text>,
      },
      {
        title: t('faceMatch.common.status'),
        dataIndex: 'statusName',
        width: 100,
        render: (value: string) => (STATUS_NAME_KEYS[value.toLowerCase()] ? t(STATUS_NAME_KEYS[value.toLowerCase()]) : value),
      },
      { title: t('faceMatch.stats.taskCount'), dataIndex: 'taskCount', align: 'right', width: 90, render: (value: number) => <span style={NUMERIC_STYLE}>{value}</span> },
      { title: t('faceMatch.stats.pendingCount'), dataIndex: 'pendingCount', align: 'right', width: 90, render: (value: number) => <span style={NUMERIC_STYLE}>{value}</span> },
      { title: t('faceMatch.stats.dismissedCount'), dataIndex: 'dismissedCount', align: 'right', width: 90, render: (value: number) => <span style={NUMERIC_STYLE}>{value}</span> },
      { title: t('faceMatch.stats.confirmedCount'), dataIndex: 'confirmedCount', align: 'right', width: 100, render: (value: number) => <span style={NUMERIC_STYLE}>{value}</span> },
      { title: t('faceMatch.stats.autoConfirmedCount'), dataIndex: 'autoConfirmedCount', align: 'right', width: 100, render: (value: number) => <span style={NUMERIC_STYLE}>{value}</span> },
      { title: t('faceMatch.stats.dismissRate'), dataIndex: 'dismissRate', width: 190, render: (value: number | null) => <RateBar value={value} /> },
      {
        title: t('faceMatch.stats.suggestion'),
        dataIndex: 'suggestReview',
        width: 220,
        render: (value: boolean) => (value ? <Tag color="red">{t('faceMatch.stats.suggestReview')}</Tag> : '-'),
      },
    ],
    [t],
  )

  const bucketColumns = useMemo<ColumnsType<FaceUnwelcomeStatsBucket>>(
    () => [
      { title: t('faceMatch.stats.bucket'), key: 'bucket', width: 120, render: (_: unknown, record) => <span style={NUMERIC_STYLE}>{formatScoreBucket(record)}</span> },
      { title: t('faceMatch.stats.bucketTotal'), dataIndex: 'total', align: 'right', width: 100, render: (value: number) => <span style={NUMERIC_STYLE}>{value}</span> },
      { title: t('faceMatch.stats.dismissedCount'), dataIndex: 'dismissedCount', align: 'right', width: 100, render: (value: number) => <span style={NUMERIC_STYLE}>{value}</span> },
      { title: t('faceMatch.stats.confirmedCount'), dataIndex: 'confirmedCount', align: 'right', width: 110, render: (value: number) => <span style={NUMERIC_STYLE}>{value}</span> },
      { title: t('faceMatch.stats.autoConfirmedCount'), dataIndex: 'autoConfirmedCount', align: 'right', width: 100, render: (value: number) => <span style={NUMERIC_STYLE}>{value}</span> },
      {
        title: t('faceMatch.stats.dismissRate'),
        key: 'rate',
        width: 190,
        render: (_: unknown, record) => <RateBar value={computeDismissRate(record.dismissedCount, record.confirmedCount)} />,
      },
    ],
    [t],
  )

  const suggested = (data?.entries ?? []).filter((entry) => entry.suggestReview)

  return (
    <Space direction="vertical" size={12} style={{ width: '100%' }}>
      <Alert type="info" showIcon message={t('faceMatch.stats.intro')} />
      <Space>
        <Button icon={<ReloadOutlined />} loading={stats.loading} onClick={stats.reload}>
          {t('faceMatch.common.refresh')}
        </Button>
      </Space>
      {stats.error ? <Alert type="error" showIcon message={describeFaceMatchError(t, stats.error)} /> : null}
      {suggested.length > 0 ? <Alert type="error" showIcon message={t('faceMatch.stats.suggestBanner', { count: suggested.length })} /> : null}
      {data ? (
        <>
          <Row gutter={[12, 12]}>
            <Col xs={12} md={8} xl={4}><Card size="small"><Statistic title={t('faceMatch.stats.taskCount')} value={data.taskCount} /></Card></Col>
            <Col xs={12} md={8} xl={4}><Card size="small"><Statistic title={t('faceMatch.stats.pendingCount')} value={data.pendingCount} /></Card></Col>
            <Col xs={12} md={8} xl={4}><Card size="small"><Statistic title={t('faceMatch.stats.dismissedCount')} value={data.dismissedCount} /></Card></Col>
            <Col xs={12} md={8} xl={4}><Card size="small"><Statistic title={t('faceMatch.stats.confirmedCount')} value={data.confirmedCount} /></Card></Col>
            <Col xs={12} md={8} xl={4}><Card size="small"><Statistic title={t('faceMatch.stats.autoConfirmedCount')} value={data.autoConfirmedCount} /></Card></Col>
            <Col xs={12} md={8} xl={4}><Card size="small"><Statistic title={t('faceMatch.stats.dismissRate')} value={formatRate(data.dismissRate)} /></Card></Col>
          </Row>
          <Typography.Title level={5} style={{ margin: 0 }}>{t('faceMatch.stats.byEntry')}</Typography.Title>
          <MeasuredTable<FaceUnwelcomeStatsEntry>
            metricId="system.face-match.stats-entries-table-1"
            rowKey="entryId"
            size="small"
            dataSource={data.entries}
            columns={entryColumns}
            pagination={false}
            scroll={{ x: 1100 }}
            // 建议复核的条目整行高亮（同时有文字标签，不只靠颜色）。
            rowClassName={(record) => (record.suggestReview ? 'ant-table-row-selected' : '')}
            locale={{ emptyText: t('faceMatch.stats.empty') }}
          />
          <Typography.Title level={5} style={{ margin: 0 }}>{t('faceMatch.stats.byScore')}</Typography.Title>
          <Typography.Text type="secondary">{t('faceMatch.stats.byScoreHint')}</Typography.Text>
          <MeasuredTable<FaceUnwelcomeStatsBucket>
            metricId="system.face-match.stats-buckets-table-1"
            rowKey={(record) => `${record.from}-${record.to}`}
            size="small"
            dataSource={data.scoreBuckets}
            columns={bucketColumns}
            pagination={false}
            scroll={{ x: 740 }}
            locale={{ emptyText: t('faceMatch.stats.empty') }}
          />
        </>
      ) : null}
    </Space>
  )
}
