import { SearchOutlined } from '@ant-design/icons'
import { Alert, Button, DatePicker, Input, Space, Typography } from 'antd'
import type { ColumnsType } from 'antd/es/table'
import dayjs from 'dayjs'
import { useMemo, useState } from 'react'
import { useTranslation } from 'react-i18next'

import { MeasuredTable } from '../../../components/MeasuredTable'
import { getFaceAudit, type FaceAuditQuery } from '../../../services/faceMatchService'
import type { FaceAuditItem, FaceMatchPage } from '../../../types/faceMatch'
import { formatDateTime } from '../FaceCameras/logic'
import { DATE_FORMAT, PAGE_SIZE, auditQueryFromFilters, describeFaceMatchError, type TrafficRange } from './logic'
import { NUMERIC_STYLE, Pager, StoreSelect, type FaceMatchTabContext } from './shared'
import { useRemote } from './useRemote'

/** 审计页签（Manage）：按门店 / 动作 / 日期范围查询，上一页 / 下一页翻页（后端没有总数）。 */
export default function AuditTab({ context }: { context: FaceMatchTabContext }) {
  const { t } = useTranslation()
  const { storeOptions, storeOptionsFailed } = context
  const [store, setStore] = useState<string | null>(null)
  const [action, setAction] = useState('')
  const [range, setRange] = useState<Partial<TrafficRange>>({})
  // 点击「查询」后才生效的过滤条件，避免每敲一个字就请求。
  const [applied, setApplied] = useState(() => auditQueryFromFilters({}))
  const [page, setPage] = useState(1)

  const loader = useMemo(() => {
    const query: FaceAuditQuery = { ...applied, page, pageSize: PAGE_SIZE }
    return (signal: AbortSignal) => getFaceAudit(query, signal)
  }, [applied, page])
  const audit = useRemote<FaceMatchPage<FaceAuditItem>>(loader)

  const columns = useMemo<ColumnsType<FaceAuditItem>>(
    () => [
      { title: t('faceMatch.audit.time'), dataIndex: 'atUtc', width: 170, render: (value: string) => <span style={NUMERIC_STYLE}>{formatDateTime(value)}</span> },
      { title: t('faceMatch.audit.actor'), dataIndex: 'actorName', width: 160, ellipsis: true },
      { title: t('faceMatch.audit.action'), dataIndex: 'action', width: 220, ellipsis: true },
      { title: t('faceMatch.common.store'), dataIndex: 'storeCode', width: 100, render: (value: string | null) => value ?? '-' },
      { title: t('faceMatch.audit.target'), dataIndex: 'targetRef', width: 200, ellipsis: true, render: (value: string | null) => value ?? '-' },
      { title: t('faceMatch.audit.result'), dataIndex: 'result', width: 160, ellipsis: true },
    ],
    [t],
  )

  const apply = () => {
    setPage(1)
    setApplied(auditQueryFromFilters({ store, action, range }))
  }

  return (
    <Space direction="vertical" size={12} style={{ width: '100%' }}>
      <Typography.Text type="secondary">{t('faceMatch.audit.intro')}</Typography.Text>
      <Space wrap>
        <StoreSelect
          options={storeOptions}
          failed={storeOptionsFailed}
          value={store}
          onChange={setStore}
          placeholder={t('faceMatch.audit.allStores')}
          style={{ width: 220 }}
        />
        <Input
          allowClear
          style={{ width: 200 }}
          placeholder={t('faceMatch.audit.actionPlaceholder')}
          maxLength={64}
          value={action}
          onChange={(event) => setAction(event.target.value)}
          onPressEnter={apply}
        />
        <DatePicker.RangePicker
          format={DATE_FORMAT}
          value={range.from && range.to ? [dayjs(range.from, DATE_FORMAT), dayjs(range.to, DATE_FORMAT)] : null}
          disabledDate={(date) => date.isAfter(dayjs(), 'day')}
          onChange={(dates) => setRange(dates?.[0] && dates[1] ? { from: dates[0].format(DATE_FORMAT), to: dates[1].format(DATE_FORMAT) } : {})}
        />
        <Button type="primary" icon={<SearchOutlined />} loading={audit.loading} onClick={apply}>
          {t('faceMatch.common.query')}
        </Button>
      </Space>
      {audit.error ? <Alert type="error" showIcon message={describeFaceMatchError(t, audit.error)} action={<Button size="small" onClick={audit.reload}>{t('faceMatch.common.retry')}</Button>} /> : null}
      <MeasuredTable<FaceAuditItem>
        metricId="system.face-match.audit-table-1"
        rowKey="auditId"
        size="small"
        loading={audit.loading && !audit.data}
        dataSource={audit.data?.items ?? []}
        columns={columns}
        pagination={false}
        scroll={{ x: 1000 }}
        locale={{ emptyText: t('faceMatch.audit.empty') }}
      />
      <Pager page={page} hasMore={audit.data?.hasMore === true} loading={audit.loading} onChange={setPage} />
    </Space>
  )
}
