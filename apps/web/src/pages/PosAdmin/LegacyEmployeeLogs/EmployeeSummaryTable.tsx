import { AlertOutlined } from '@ant-design/icons'
import { Alert, Button, Empty, Progress, Tag, Typography, theme } from 'antd'
import type { ColumnsType } from 'antd/es/table'
import { useMemo } from 'react'
import { useTranslation } from 'react-i18next'

import { MeasuredTable } from '../../../components/MeasuredTable'
import type { LegacyEmployeeLogEmployeeSummary, LegacyEmployeeLogEmployeeSummaryResult } from '../../../types/legacyEmployeeLog'

import { dangerRate, formatStoreLabel, orderRuleCounts } from './legacyEmployeeLogsLogic'

interface EmployeeSummaryTableProps {
  data: LegacyEmployeeLogEmployeeSummaryResult | null
  loading: boolean
  error: string | null
  storeNames: ReadonlyMap<string, string>
  onRetry: () => void
  onViewEmployee: (row: LegacyEmployeeLogEmployeeSummary) => void
}

/** 按员工汇总：危险占比高于整体均值标红；排序在前端做（员工数最多几十人）。 */
export default function EmployeeSummaryTable({ data, loading, error, storeNames, onRetry, onViewEmployee }: EmployeeSummaryTableProps) {
  const { t } = useTranslation()
  const { token } = theme.useToken()
  const average = data ? dangerRate(data.dangerTotal, data.total) : 0
  const maxDanger = Math.max(1, ...(data?.employees ?? []).map((row) => row.dangerCount))

  const columns = useMemo<ColumnsType<LegacyEmployeeLogEmployeeSummary>>(
    () => [
      {
        title: t('legacyEmployeeLogs.employees.employee'),
        key: 'employee',
        width: 150,
        render: (_, row) => (
          <div>
            <div style={{ fontWeight: 600 }}>{row.employeeName || row.employeeId || '-'}</div>
            <Typography.Text type="secondary" style={{ fontSize: 12 }}>{row.deviceCodes.join('、') || '-'}</Typography.Text>
          </div>
        ),
      },
      {
        title: t('legacyEmployeeLogs.employees.stores'),
        key: 'stores',
        width: 190,
        render: (_, row) => (
          <Typography.Text ellipsis={{ tooltip: true }} style={{ maxWidth: 180 }}>
            {row.storeCodes.map((code) => formatStoreLabel(code, storeNames)).join('、') || '-'}
          </Typography.Text>
        ),
      },
      {
        title: t('legacyEmployeeLogs.employees.total'),
        dataIndex: 'total',
        key: 'total',
        width: 100,
        align: 'right',
        sorter: (a, b) => a.total - b.total,
        render: (value: number) => <span style={{ fontVariantNumeric: 'tabular-nums' }}>{value.toLocaleString('en-US')}</span>,
      },
      {
        title: t('legacyEmployeeLogs.employees.danger'),
        dataIndex: 'dangerCount',
        key: 'dangerCount',
        width: 170,
        sorter: (a, b) => a.dangerCount - b.dangerCount,
        render: (value: number) => (
          <div style={{ display: 'flex', alignItems: 'center', gap: 8 }}>
            <span style={{ width: 36, fontVariantNumeric: 'tabular-nums' }}>{value}</span>
            <Progress percent={(value / maxDanger) * 100} showInfo={false} size="small" strokeColor={token.colorError} style={{ flex: 1, margin: 0 }} />
          </div>
        ),
      },
      {
        title: (
          <span>
            {t('legacyEmployeeLogs.employees.dangerRate')}
            <Typography.Text type="secondary" style={{ fontSize: 12, fontWeight: 400 }}>
              {t('legacyEmployeeLogs.employees.dangerRateAvg', { percent: (average * 100).toFixed(1) })}
            </Typography.Text>
          </span>
        ),
        key: 'dangerRate',
        width: 150,
        sorter: (a, b) => dangerRate(a.dangerCount, a.total) - dangerRate(b.dangerCount, b.total),
        render: (_, row) => {
          const rate = dangerRate(row.dangerCount, row.total)
          return (
            <span style={{ fontVariantNumeric: 'tabular-nums', color: rate > average ? token.colorError : undefined, fontWeight: rate > average ? 600 : undefined }}>
              {(rate * 100).toFixed(1)}%
            </span>
          )
        },
      },
      {
        title: t('legacyEmployeeLogs.employees.abnormal'),
        key: 'abnormal',
        sorter: (a, b) => a.abnormalCount - b.abnormalCount || a.pendingReview - b.pendingReview,
        render: (_, row) => row.abnormalCount > 0 ? (
          <div style={{ display: 'flex', flexWrap: 'wrap', alignItems: 'center', gap: 6 }}>
            <Tag color="warning" icon={<AlertOutlined />} style={{ marginInlineEnd: 0 }}>
              {t('legacyEmployeeLogs.employees.abnormalTag', { count: row.abnormalCount, pending: row.pendingReview })}
            </Tag>
            <Typography.Text type="secondary" style={{ fontSize: 12 }}>
              {orderRuleCounts(row.abnormalByRule)
                .sort((a, b) => b.count - a.count)
                .slice(0, 2)
                .map((rule) => `${t(`legacyEmployeeLogs.rules.${rule.ruleCode}.label`)} ${rule.count}`)
                .join(' · ')}
            </Typography.Text>
          </div>
        ) : (
          <Typography.Text type="secondary" style={{ fontSize: 12 }}>{t('legacyEmployeeLogs.employees.noAbnormal')}</Typography.Text>
        ),
      },
      {
        title: t('legacyEmployeeLogs.employees.amount'),
        dataIndex: 'amountImpact',
        key: 'amountImpact',
        width: 120,
        align: 'right',
        sorter: (a, b) => a.amountImpact - b.amountImpact,
        render: (value: number) => value > 0 ? (
          <span style={{ color: token.colorError, fontWeight: 600, fontVariantNumeric: 'tabular-nums' }}>−{value.toFixed(2)}</span>
        ) : '-',
      },
      {
        key: 'action',
        width: 100,
        fixed: 'right',
        render: (_, row) => (
          <Button type="link" size="small" disabled={!row.employeeId} onClick={() => onViewEmployee(row)}>
            {t('legacyEmployeeLogs.employees.view')}
          </Button>
        ),
      },
    ],
    [average, maxDanger, onViewEmployee, storeNames, t, token],
  )

  if (error) {
    return (
      <Alert
        type="error"
        showIcon
        style={{ margin: 16 }}
        message={t('legacyEmployeeLogs.employees.loadFailed')}
        description={error}
        action={<Button onClick={onRetry}>{t('legacyEmployeeLogs.retry')}</Button>}
      />
    )
  }

  return (
    <>
      <MeasuredTable<LegacyEmployeeLogEmployeeSummary>
        metricId="pos-admin.legacy-employee-logs.table-2"
        rowKey={(row) => row.employeeId ?? `name:${row.employeeName ?? ''}`}
        size="middle"
        loading={loading}
        columns={columns}
        dataSource={data?.employees ?? []}
        scroll={{ x: 1080 }}
        pagination={false}
        locale={{ emptyText: <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description={t('legacyEmployeeLogs.employees.empty')} /> }}
      />
      <Typography.Paragraph type="secondary" style={{ fontSize: 12, margin: '12px 16px' }}>
        {t('legacyEmployeeLogs.employees.footnote')}
      </Typography.Paragraph>
    </>
  )
}
