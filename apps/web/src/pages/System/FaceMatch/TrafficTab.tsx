import { SearchOutlined } from '@ant-design/icons'
import { Alert, Button, Card, Col, DatePicker, Row, Space, Statistic, Table, Tag, Tooltip, Typography } from 'antd'
import type { ColumnsType } from 'antd/es/table'
import dayjs from 'dayjs'
import { useMemo, useState } from 'react'
import { useTranslation } from 'react-i18next'

import { MeasuredTable } from '../../../components/MeasuredTable'
import { getFaceTraffic } from '../../../services/faceMatchService'
import type { FaceTraffic, FaceTrafficCounts } from '../../../types/faceMatch'
import {
  DATE_FORMAT,
  describeFaceMatchError,
  defaultTrafficRange,
  formatHourRange,
  validateTrafficQuery,
  type TrafficRange,
} from './logic'
import { NUMERIC_STYLE, StoreSelect, type FaceMatchTabContext } from './shared'
import { useRemote } from './useRemote'

interface TrafficRow {
  key: string
  label: string
  counts: FaceTrafficCounts
}

interface TrafficQuery extends TrafficRange {
  store: string
}

/** 客流统计页签（Traffic.View）：门店 + 日期范围，按天汇总，单日显示分小时。唯一访客数全程标注「估算」。 */
export default function TrafficTab({ context }: { context: FaceMatchTabContext }) {
  const { t } = useTranslation()
  const { storeOptions, storeOptionsFailed } = context
  const [store, setStore] = useState<string | null>(null)
  const [range, setRange] = useState<TrafficRange>(() => defaultTrafficRange())
  const [query, setQuery] = useState<TrafficQuery | null>(null)
  const [issueKey, setIssueKey] = useState<string | null>(null)
  const [issueParams, setIssueParams] = useState<Record<string, unknown> | undefined>()

  const loader = useMemo(
    () => (query ? (signal: AbortSignal) => getFaceTraffic(query.store, query.from, query.to, signal) : null),
    [query],
  )
  const traffic = useRemote<FaceTraffic>(loader)

  const submit = () => {
    const issue = validateTrafficQuery(store, range)
    setIssueKey(issue?.key ?? null)
    setIssueParams(issue?.params)
    if (!issue && store) setQuery({ store, ...range })
  }

  const data = query ? traffic.data : null
  const estimateLabel = t('faceMatch.traffic.uniqueVisitorEstimate')

  const countColumns = (firstTitle: string): ColumnsType<TrafficRow> => [
    { title: firstTitle, dataIndex: 'label', width: 130, fixed: 'left' },
    { title: t('faceMatch.traffic.captureCount'), key: 'captureCount', align: 'right', width: 100, render: (_, row) => <span style={NUMERIC_STYLE}>{row.counts.captureCount}</span> },
    { title: t('faceMatch.traffic.staffMatchCount'), key: 'staffMatchCount', align: 'right', width: 110, render: (_, row) => <span style={NUMERIC_STYLE}>{row.counts.staffMatchCount}</span> },
    {
      title: (
        <Tooltip title={t('faceMatch.traffic.uniqueVisitorTip')}>
          <span>{estimateLabel}</span>
        </Tooltip>
      ),
      key: 'uniqueVisitorCount',
      align: 'right',
      width: 150,
      render: (_, row) => <span style={NUMERIC_STYLE}>{row.counts.uniqueVisitorCount}</span>,
    },
    { title: t('faceMatch.traffic.repeatCount'), key: 'repeatCount', align: 'right', width: 100, render: (_, row) => <span style={NUMERIC_STYLE}>{row.counts.repeatCount}</span> },
    { title: t('faceMatch.traffic.lowQualityCount'), key: 'lowQualityCount', align: 'right', width: 100, render: (_, row) => <span style={NUMERIC_STYLE}>{row.counts.lowQualityCount}</span> },
    { title: t('faceMatch.traffic.apiFailureCount'), key: 'apiFailureCount', align: 'right', width: 110, render: (_, row) => <span style={NUMERIC_STYLE}>{row.counts.apiFailureCount}</span> },
  ]

  const dayRows: TrafficRow[] = (data?.days ?? []).map((day) => ({ key: day.date, label: day.date, counts: day.counts }))
  const hourRows: TrafficRow[] = (data?.hours ?? []).map((hour) => ({ key: String(hour.hour), label: formatHourRange(hour.hour), counts: hour.counts }))

  const renderTotalRow = (total: FaceTrafficCounts) => (
    <Table.Summary fixed>
      <Table.Summary.Row>
        <Table.Summary.Cell index={0}>
          <Typography.Text strong>{t('faceMatch.traffic.total')}</Typography.Text>
        </Table.Summary.Cell>
        {[total.captureCount, total.staffMatchCount, total.uniqueVisitorCount, total.repeatCount, total.lowQualityCount, total.apiFailureCount].map((value, index) => (
          <Table.Summary.Cell key={index} index={index + 1} align="right">
            <Typography.Text strong style={NUMERIC_STYLE}>{value}</Typography.Text>
          </Table.Summary.Cell>
        ))}
      </Table.Summary.Row>
    </Table.Summary>
  )

  return (
    <Space direction="vertical" size={12} style={{ width: '100%' }}>
      {/* 唯一访客数的口径说明常驻页签顶部：任何截图 / 导出都会带着「估算」二字。 */}
      <Alert type="warning" showIcon message={t('faceMatch.traffic.estimateNotice')} />
      <Space wrap>
        <Typography.Text>{t('faceMatch.common.store')}</Typography.Text>
        <StoreSelect
          options={storeOptions}
          failed={storeOptionsFailed}
          value={store}
          onChange={setStore}
          allowClear={false}
          placeholder={t('faceMatch.traffic.pickStore')}
          style={{ width: 260 }}
        />
        <DatePicker.RangePicker
          allowClear={false}
          format={DATE_FORMAT}
          value={[dayjs(range.from, DATE_FORMAT), dayjs(range.to, DATE_FORMAT)]}
          disabledDate={(date) => date.isAfter(dayjs(), 'day')}
          onChange={(dates) => {
            if (dates?.[0] && dates[1]) setRange({ from: dates[0].format(DATE_FORMAT), to: dates[1].format(DATE_FORMAT) })
          }}
        />
        <Button type="primary" icon={<SearchOutlined />} loading={traffic.loading} onClick={submit}>
          {t('faceMatch.traffic.query')}
        </Button>
      </Space>
      {issueKey ? <Typography.Text type="danger">{t(issueKey, issueParams)}</Typography.Text> : null}
      {traffic.error ? <Alert type="error" showIcon message={describeFaceMatchError(t, traffic.error)} /> : null}

      {data ? (
        <>
          <Typography.Text type="secondary">
            {t('faceMatch.traffic.scope', { store: `${data.storeCode}${data.storeName ? ` · ${data.storeName}` : ''}`, from: data.from, to: data.to, zone: data.timeZoneId || '-' })}
          </Typography.Text>
          <Row gutter={[12, 12]}>
            <Col xs={12} md={8} xl={4}>
              <Card size="small"><Statistic title={t('faceMatch.traffic.captureCount')} value={data.total.captureCount} /></Card>
            </Col>
            <Col xs={12} md={8} xl={4}>
              <Card size="small"><Statistic title={t('faceMatch.traffic.staffMatchCount')} value={data.total.staffMatchCount} /></Card>
            </Col>
            <Col xs={12} md={8} xl={4}>
              <Card size="small">
                <Statistic title={<span>{estimateLabel} <Tag color="gold">{t('faceMatch.traffic.estimateTag')}</Tag></span>} value={data.total.uniqueVisitorCount} />
              </Card>
            </Col>
            <Col xs={12} md={8} xl={4}>
              <Card size="small"><Statistic title={t('faceMatch.traffic.repeatCount')} value={data.total.repeatCount} /></Card>
            </Col>
            <Col xs={12} md={8} xl={4}>
              <Card size="small"><Statistic title={t('faceMatch.traffic.lowQualityCount')} value={data.total.lowQualityCount} /></Card>
            </Col>
            <Col xs={12} md={8} xl={4}>
              <Card size="small"><Statistic title={t('faceMatch.traffic.apiFailureCount')} value={data.total.apiFailureCount} /></Card>
            </Col>
          </Row>
          <MeasuredTable<TrafficRow>
            metricId="system.face-match.traffic-days-table-1"
            rowKey="key"
            size="small"
            dataSource={dayRows}
            columns={countColumns(t('faceMatch.traffic.date'))}
            pagination={false}
            scroll={{ x: 800 }}
            summary={() => renderTotalRow(data.total)}
            locale={{ emptyText: t('faceMatch.traffic.empty') }}
          />
          {hourRows.length > 0 ? (
            <>
              <Typography.Title level={5} style={{ margin: 0 }}>
                {t('faceMatch.traffic.hourlyTitle', { date: data.from })}
              </Typography.Title>
              <MeasuredTable<TrafficRow>
                metricId="system.face-match.traffic-hours-table-1"
                rowKey="key"
                size="small"
                dataSource={hourRows}
                columns={countColumns(t('faceMatch.traffic.hour'))}
                pagination={false}
                scroll={{ x: 800 }}
              />
            </>
          ) : null}
        </>
      ) : !traffic.loading && !traffic.error ? (
        <Typography.Text type="secondary">{t('faceMatch.traffic.pickHint')}</Typography.Text>
      ) : null}
    </Space>
  )
}
