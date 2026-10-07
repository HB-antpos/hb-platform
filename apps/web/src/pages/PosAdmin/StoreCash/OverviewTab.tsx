import { InfoCircleOutlined } from '@ant-design/icons'
import { Empty, Select, Tooltip } from 'antd'
import type { ColumnsType } from 'antd/es/table'
import { useMemo } from 'react'
import { MeasuredTable } from '../../../components/MeasuredTable'
import StatusPill from '../../../components/listToolbar/StatusPill'
import { getCashOverview } from '../../../services/storeCashService'
import type { CashExpenseCategory, CashOverview, CashOverviewRow } from '../../../types/storeCash'
import { buildOverviewCsv } from './csv'
import {
  buildCashExportFileName,
  categoryAmount,
  compareNullableNumber,
  DEFAULT_T2_VISIBLE_DAYS,
  EMPTY_CELL,
  formatCount,
} from './logic'
import { Amount, ExportButton, KpiTile, LoadErrorAlert, RangeFilter, RefreshButton, storeOptionLabel } from './parts'
import type { CashTabProps } from './tabProps'
import { useCashRequest } from './useCashRequest'
import { useCsvExport } from './useCsvExport'

const CATEGORY_COLUMNS: { category: CashExpenseCategory; titleKey: string }[] = [
  { category: 'Salary', titleKey: 'storeCash.category.Salary' },
  { category: 'Purchase', titleKey: 'storeCash.category.Purchase' },
  { category: 'T2', titleKey: 'storeCash.category.T2' },
  { category: 'Other', titleKey: 'storeCash.category.Other' },
]

/**
 * 总览：多店现金池与区间统计。
 * 「现金池余额、未存天数、最近存款日」截止各店今天；「日结现金、存款、支出」只统计所选区间。
 * 日结未接入时现金池余额、日结现金、日结差异为 null，显示「—」，合计同样不可算。
 */
export default function OverviewTab({ context, filters, active, tr, tf, errorText, onQueryChange }: CashTabProps) {
  const t2Days = context.t2VisibleDays || DEFAULT_T2_VISIBLE_DAYS
  const storeCodes = useMemo(() => [...filters.storeCodes].sort(), [filters.storeCodes])
  const requestKey = JSON.stringify(['overview', filters.from, filters.to, storeCodes])
  const overviewRequest = useCashRequest<CashOverview>(
    requestKey,
    (signal) => getCashOverview({ from: filters.from, to: filters.to, storeCodes }, signal),
    active,
  )
  const overview = overviewRequest.data
  const loading = overviewRequest.loading
  const loadError = overviewRequest.error ? errorText(overviewRequest.error) : null
  const exporter = useCsvExport(tr, errorText)

  const openingMissingCount = overview?.rows.filter((row) => row.openingMissing).length ?? 0
  const connected = overview?.dailyCloseConnected ?? context.dailyCloseConnected
  const t2Restricted = overview?.t2Restricted ?? !context.capabilities.canViewAllStores
  const t2Note = tr('t2Note', { days: t2Days })

  const openDaily = (storeCode: string) => onQueryChange({ tab: 'daily', store: storeCode })

  const handleExport = () => {
    if (!overview) return
    void exporter.run(async () => {
      if (overview.rows.length === 0) return { status: 'empty' }
      const { content, rowCount } = buildOverviewCsv(overview, tf, t2Days)
      return {
        status: 'ok',
        content,
        rowCount,
        fileName: buildCashExportFileName(tr('export.prefix.overview'), overview.from, overview.to),
      }
    })
  }

  const columns = useMemo<ColumnsType<CashOverviewRow>>(() => [
    {
      title: tr('overview.columns.store'),
      key: 'store',
      fixed: 'left',
      width: 180,
      render: (_, row) => (
        <div className="store-cash-cell-stack">
          <button
            type="button"
            className="store-cash-link"
            title={tr('overview.openDaily')}
            onClick={(event) => {
              event.stopPropagation()
              openDaily(row.storeCode)
            }}
          >
            {row.storeName || row.storeCode}
          </button>
          <span className="store-cash-sub">{row.storeCode}</span>
        </div>
      ),
    },
    {
      title: tr('overview.columns.poolBalance'),
      key: 'poolBalance',
      align: 'right',
      width: 140,
      sorter: (left, right) => compareNullableNumber(left.poolBalance, right.poolBalance),
      render: (_, row) => (
        <div className="store-cash-cell-stack store-cash-cell-stack-end">
          <Amount value={row.poolBalance} strong />
          {row.openingMissing ? (
            <Tooltip title={tr('overview.openingMissingHint')}>
              <span><StatusPill tone="orange">{tr('overview.openingMissing')}</StatusPill></span>
            </Tooltip>
          ) : null}
        </div>
      ),
    },
    {
      title: tr('overview.columns.uncovered'),
      key: 'uncovered',
      width: 160,
      sorter: (left, right) => left.uncoveredDayCount - right.uncoveredDayCount,
      render: (_, row) => {
        if (!connected) return <span className="store-cash-empty">{EMPTY_CELL}</span>
        if (row.uncoveredDayCount === 0) return <span className="store-cash-muted">{tr('overview.allDeposited')}</span>
        return (
          <div className="store-cash-cell-stack">
            <span className="store-cash-pills">
              <span>{tr('overview.uncoveredDays', { count: row.uncoveredDayCount })}</span>
              {row.depositOverdue ? <StatusPill tone="red">{tr('overview.overdue')}</StatusPill> : null}
            </span>
            {row.oldestUncoveredDate ? (
              <span className="store-cash-sub">{tr('overview.oldestUncovered', { date: row.oldestUncoveredDate })}</span>
            ) : null}
          </div>
        )
      },
    },
    {
      title: tr('overview.columns.inflowCash'),
      key: 'inflowCash',
      align: 'right',
      width: 130,
      render: (_, row) => <Amount value={row.inflowCash} />,
    },
    {
      title: (
        <Tooltip title={tr('overview.varianceHint')}>
          <span>{tr('overview.columns.closeVariance')} <InfoCircleOutlined /></span>
        </Tooltip>
      ),
      key: 'closeVariance',
      align: 'right',
      width: 120,
      sorter: (left, right) => compareNullableNumber(left.closeVariance, right.closeVariance),
      render: (_, row) => <Amount value={row.closeVariance} signed />,
    },
    {
      title: tr('overview.columns.closeDays'),
      key: 'closeDays',
      width: 120,
      render: (_, row) => {
        if (!connected) return <span className="store-cash-empty">{EMPTY_CELL}</span>
        return (
          <div className="store-cash-cell-stack">
            <span>{tr('overview.closeDays', { count: row.closeDayCount })}</span>
            {row.missingCloseDayCount > 0 ? (
              <Tooltip title={tr('overview.missingCloseHint')}>
                <span className="store-cash-sub store-cash-note-warning">
                  {tr('overview.missingCloseDays', { count: row.missingCloseDayCount })}
                </span>
              </Tooltip>
            ) : null}
          </div>
        )
      },
    },
    {
      title: tr('overview.columns.deposits'),
      key: 'deposits',
      align: 'right',
      width: 130,
      render: (_, row) => (
        <div className="store-cash-cell-stack store-cash-cell-stack-end">
          <Amount value={row.depositTotal} />
          <span className="store-cash-sub">{tr('overview.depositCount', { count: row.depositCount })}</span>
        </div>
      ),
    },
    {
      title: tr('overview.columns.expenseGroup'),
      key: 'expenses',
      children: [
        {
          title: tr('overview.columns.expenseTotal'),
          key: 'expenseTotal',
          align: 'right',
          width: 120,
          render: (_, row) => <Amount value={row.expenseTotal} strong />,
        },
        ...CATEGORY_COLUMNS.map(({ category, titleKey }) => ({
          title: category === 'T2' && t2Restricted ? (
            <Tooltip title={t2Note}>
              <span>{tf(titleKey)} <InfoCircleOutlined /></span>
            </Tooltip>
          ) : tf(titleKey),
          key: `category-${category}`,
          align: 'right' as const,
          width: 112,
          render: (_: unknown, row: CashOverviewRow) => <Amount value={categoryAmount(row.expenseByCategory, category)} />,
        })),
      ],
    },
    {
      title: tr('overview.columns.flagged'),
      key: 'flagged',
      align: 'right',
      width: 96,
      sorter: (left, right) => left.flaggedExpenseCount - right.flaggedExpenseCount,
      render: (_, row) => (row.flaggedExpenseCount > 0
        ? <StatusPill tone="red">{formatCount(row.flaggedExpenseCount)}</StatusPill>
        : <span className="store-cash-muted">0</span>),
    },
    {
      title: tr('overview.columns.lastDepositDate'),
      key: 'lastDepositDate',
      width: 120,
      render: (_, row) => row.lastDepositDate ?? <span className="store-cash-empty">{EMPTY_CELL}</span>,
    },
  // openDaily 只依赖 onQueryChange；随文案与口径变化重建列即可。
  // eslint-disable-next-line react-hooks/exhaustive-deps
  ], [connected, t2Note, t2Restricted, tf, tr])

  const totals = overview?.totals
  const showStorePicker = context.stores.length > 1

  return (
    <div className="store-cash-stack">
      <div className="store-cash-toolbar">
        <RangeFilter
          from={filters.from}
          to={filters.to}
          today={filters.today}
          tr={tr}
          onChange={(range) => onQueryChange(range)}
        />
        {showStorePicker ? (
          <label className="store-cash-field">
            <span className="store-cash-field-label">{tr('filters.stores')}</span>
            <Select
              className="store-cash-stores-select"
              mode="multiple"
              allowClear
              maxTagCount="responsive"
              aria-label={tr('filters.stores')}
              placeholder={tr('filters.allStores')}
              value={filters.storeCodes}
              optionFilterProp="label"
              options={context.stores.map((store) => ({ value: store.storeCode, label: storeOptionLabel(store) }))}
              onChange={(codes: string[]) => onQueryChange({ stores: codes })}
            />
          </label>
        ) : null}
        <div className="store-cash-toolbar-actions">
          <RefreshButton loading={loading} tr={tr} onClick={overviewRequest.reload} />
          <ExportButton
            running={exporter.running}
            progress={exporter.progress}
            disabled={!overview || loading}
            tr={tr}
            onClick={handleExport}
          />
        </div>
      </div>

      {loadError ? <LoadErrorAlert message={loadError} tr={tr} onRetry={overviewRequest.reload} /> : null}

      <section className="store-cash-kpis" aria-label={tr('kpi.region')} aria-busy={loading}>
        <KpiTile
          label={tr('kpi.pool')}
          loading={loading && !overview}
          value={<Amount value={totals?.poolBalance ?? null} />}
          sub={!connected
            ? tr('kpi.dailyCloseMissing')
            : openingMissingCount > 0
              ? tr('kpi.openingMissing', { count: openingMissingCount })
              : tr('kpi.poolSub')}
        />
        <KpiTile
          label={tr('kpi.inflow')}
          loading={loading && !overview}
          value={<Amount value={totals?.inflowCash ?? null} />}
          sub={connected ? (
            <span>
              {tr('kpi.variance')} <Amount value={totals?.closeVariance ?? null} signed />
            </span>
          ) : tr('kpi.dailyCloseMissing')}
        />
        <KpiTile
          label={tr('kpi.deposits')}
          loading={loading && !overview}
          value={<Amount value={totals?.depositTotal ?? null} />}
          sub={totals ? tr('kpi.depositCount', { count: totals.depositCount }) : undefined}
        />
        <KpiTile
          label={tr('kpi.expenses')}
          loading={loading && !overview}
          value={<Amount value={totals?.expenseTotal ?? null} />}
          sub={t2Restricted ? t2Note : tr('kpi.expenseSub')}
        />
        <KpiTile
          label={tr('kpi.overdue')}
          loading={loading && !overview}
          tone={totals && totals.overdueStoreCount > 0 ? 'danger' : 'default'}
          value={connected ? formatCount(totals?.overdueStoreCount) : EMPTY_CELL}
          sub={connected
            ? tr('kpi.overdueSub', { days: context.depositOverdueDays, count: totals?.uncoveredDayCount ?? 0 })
            : tr('kpi.dailyCloseMissing')}
        />
        <KpiTile
          label={tr('kpi.flagged')}
          loading={loading && !overview}
          tone={totals && totals.flaggedExpenseCount > 0 ? 'warning' : 'default'}
          value={formatCount(totals?.flaggedExpenseCount)}
          sub={tr('kpi.flaggedSub')}
        />
      </section>

      <section className="store-cash-panel" aria-label={tr('overview.tableLabel')}>
        <div className="store-cash-panel-head">
          <h2 className="store-cash-panel-title">{tr('overview.tableTitle')}</h2>
          <span className="store-cash-note">
            {tr('overview.rangeNote', { from: overview?.from ?? filters.from, to: overview?.to ?? filters.to })}
          </span>
          {t2Restricted ? (
            <span className="store-cash-note">
              <InfoCircleOutlined aria-hidden="true" />
              {t2Note}
            </span>
          ) : null}
          <span className="store-cash-note">{tr('overview.rowHint')}</span>
        </div>
        <MeasuredTable<CashOverviewRow>
          metricId="pos-admin.store-cash.overview-table"
          className="store-cash-table"
          rowKey={(row) => row.storeCode}
          size="small"
          bordered
          loading={loading}
          columns={columns}
          dataSource={overview?.rows ?? []}
          pagination={false}
          scroll={{ x: 1640 }}
          locale={{ emptyText: <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description={tr('overview.empty')} /> }}
          rowClassName={() => 'store-cash-row-clickable'}
          onRow={(row) => ({ onClick: () => openDaily(row.storeCode) })}
          summary={() => (totals && overview && overview.rows.length > 1 ? (
            <MeasuredTable.Summary fixed="bottom">
              <MeasuredTable.Summary.Row>
                <MeasuredTable.Summary.Cell index={0}>
                  {tr('overview.totalRow', { count: overview.rows.length })}
                </MeasuredTable.Summary.Cell>
                <MeasuredTable.Summary.Cell index={1} align="right"><Amount value={totals.poolBalance} strong /></MeasuredTable.Summary.Cell>
                <MeasuredTable.Summary.Cell index={2}>
                  {connected ? tr('overview.uncoveredDays', { count: totals.uncoveredDayCount }) : EMPTY_CELL}
                </MeasuredTable.Summary.Cell>
                <MeasuredTable.Summary.Cell index={3} align="right"><Amount value={totals.inflowCash} /></MeasuredTable.Summary.Cell>
                <MeasuredTable.Summary.Cell index={4} align="right"><Amount value={totals.closeVariance} signed /></MeasuredTable.Summary.Cell>
                <MeasuredTable.Summary.Cell index={5} />
                <MeasuredTable.Summary.Cell index={6} align="right">
                  <div className="store-cash-cell-stack store-cash-cell-stack-end">
                    <Amount value={totals.depositTotal} />
                    <span className="store-cash-sub">{tr('overview.depositCount', { count: totals.depositCount })}</span>
                  </div>
                </MeasuredTable.Summary.Cell>
                <MeasuredTable.Summary.Cell index={7} align="right"><Amount value={totals.expenseTotal} strong /></MeasuredTable.Summary.Cell>
                {CATEGORY_COLUMNS.map(({ category }, offset) => (
                  <MeasuredTable.Summary.Cell key={category} index={8 + offset} align="right">
                    <Amount value={categoryAmount(totals.expenseByCategory, category)} />
                  </MeasuredTable.Summary.Cell>
                ))}
                <MeasuredTable.Summary.Cell index={12} align="right">{formatCount(totals.flaggedExpenseCount)}</MeasuredTable.Summary.Cell>
                <MeasuredTable.Summary.Cell index={13} />
              </MeasuredTable.Summary.Row>
            </MeasuredTable.Summary>
          ) : null)}
        />
      </section>
    </div>
  )
}
