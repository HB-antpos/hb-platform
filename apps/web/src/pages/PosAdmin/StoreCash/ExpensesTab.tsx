import { InfoCircleOutlined } from '@ant-design/icons'
import { Alert, Empty, Select, Switch, Tooltip } from 'antd'
import type { ColumnsType } from 'antd/es/table'
import { useCallback, useMemo, useRef, useState } from 'react'
import { MeasuredTable } from '../../../components/MeasuredTable'
import StatusPill from '../../../components/listToolbar/StatusPill'
import { listCashExpenses } from '../../../services/storeCashService'
import {
  CASH_EXPENSE_CATEGORIES,
  type CashExpenseListItem,
  type CashExpenseListQuery,
} from '../../../types/storeCash'
import { buildExpensesCsv } from './csv'
import ExpenseDrawer from './ExpenseDrawer'
import {
  buildCashExportFileName,
  CATEGORY_LABEL_KEYS,
  categoryLabelKey,
  DEFAULT_T2_VISIBLE_DAYS,
  EMPTY_CELL,
  EXPORT_MAX_ROWS,
  formatCount,
  formatStoreDateTime,
  isExpenseCategory,
  isReviewStatus,
  isVoided,
  reviewLabelKey,
  reviewTone,
  summarizeExpenses,
  timeZoneOf,
} from './logic'
import { fetchAllPages, PAGE_FETCH_LIMIT, type FetchAllResult } from './paging'
import { Amount, ExportButton, LoadErrorAlert, RangeFilter, RefreshButton, RegisterSelect, StoreSelect } from './parts'
import type { CashTabProps } from './tabProps'
import { useCashRequest } from './useCashRequest'
import { useCsvExport } from './useCsvExport'

const DEFAULT_PAGE_SIZE = 20
const ALL = '__all__'

/**
 * 支出：单店 + 区间 + 类别 + 核对状态 + 含作废开关。
 * 类别合计条要覆盖「当前筛选下的全部数据」，所以列表按 limit=200 循环取完（上限 5000 行），表格在前端分页；
 * 导出同口径（重新取一遍最新数据）。T2 的可见范围由服务端过滤，前端不再额外过滤。
 * 存疑行整行标红；核对标记是财务事后标记，不影响支出生效。
 */
export default function ExpensesTab({ context, filters, query, active, tr, tf, errorText, onQueryChange }: CashTabProps) {
  const storeCode = filters.storeCode
  const timeZone = timeZoneOf(context.stores, storeCode)
  const t2Days = context.t2VisibleDays || DEFAULT_T2_VISIBLE_DAYS
  // 没有全部分店权限的账号，服务端只返回最近 N 天的 T2。
  const t2Restricted = !context.capabilities.canViewAllStores
  const listQuery = useMemo<CashExpenseListQuery | null>(() => (storeCode ? {
    storeCode,
    from: filters.from,
    to: filters.to,
    category: query.category,
    reviewStatus: query.review,
    includeVoided: query.voided,
  } : null), [filters.from, filters.to, query.category, query.review, query.voided, storeCode])
  const filterKey = JSON.stringify(['expenses', listQuery])
  const [paging, setPaging] = useState({ filterKey, page: 1, pageSize: DEFAULT_PAGE_SIZE })
  const page = paging.filterKey === filterKey ? paging.page : 1
  const [drawer, setDrawer] = useState<{ expenseGuid: string; seq: number } | null>(null)
  const drawerSeqRef = useRef(0)

  const loadAll = useCallback((signal?: AbortSignal, onProgress?: (progress: { fetched: number; total: number }) => void) => {
    const base = listQuery as CashExpenseListQuery
    return fetchAllPages<CashExpenseListItem>(
      (limit, offset, pageSignal) => listCashExpenses({ ...base, limit, offset }, pageSignal),
      { pageSize: PAGE_FETCH_LIMIT, maxRows: EXPORT_MAX_ROWS, signal, onProgress, keyOf: (item) => item.expenseGuid },
    )
  }, [listQuery])

  const listRequest = useCashRequest<FetchAllResult<CashExpenseListItem>>(
    listQuery ? filterKey : null,
    (signal) => loadAll(signal),
    active,
  )
  const result = listRequest.data
  const items = useMemo(() => (result?.status === 'ok' ? result.items : []), [result])
  const summary = useMemo(() => summarizeExpenses(items), [items])
  const loading = listRequest.loading
  const loadError = listRequest.error ? errorText(listRequest.error) : null
  const exporter = useCsvExport(tr, errorText)

  const openDrawer = useCallback((expenseGuid: string) => {
    drawerSeqRef.current += 1
    setDrawer({ expenseGuid, seq: drawerSeqRef.current })
  }, [])

  const handleExport = () => {
    if (!listQuery) return
    const code = listQuery.storeCode
    void exporter.run(async (signal, onProgress) => {
      const all = await loadAll(signal, onProgress)
      if (all.status === 'tooMany') return all
      if (all.items.length === 0) return { status: 'empty' }
      const { content, rowCount } = buildExpensesCsv(all.items, tf, timeZone)
      return {
        status: 'ok',
        content,
        rowCount,
        fileName: buildCashExportFileName(tr('export.prefix.expenses'), filters.from, filters.to, code),
      }
    })
  }

  const columns = useMemo<ColumnsType<CashExpenseListItem>>(() => [
    {
      title: tr('expenses.columns.expenseDate'),
      key: 'expenseDate',
      width: 116,
      render: (_, item) => (
        <button
          type="button"
          className="store-cash-link"
          onClick={(event) => {
            event.stopPropagation()
            openDrawer(item.expenseGuid)
          }}
        >
          {item.expenseDate}
        </button>
      ),
    },
    {
      title: tr('expenses.columns.category'),
      key: 'category',
      width: 110,
      render: (_, item) => {
        const key = categoryLabelKey(item.category)
        return key ? tf(key) : item.category
      },
    },
    {
      title: tr('expenses.columns.amount'),
      key: 'amount',
      align: 'right',
      width: 120,
      render: (_, item) => <Amount value={item.amount} strong />,
    },
    {
      title: tr('expenses.columns.payee'),
      key: 'payee',
      width: 140,
      render: (_, item) => item.payeeName || <span className="store-cash-empty">{EMPTY_CELL}</span>,
    },
    {
      title: tr('expenses.columns.note'),
      key: 'note',
      width: 200,
      render: (_, item) => (item.note
        ? <Tooltip title={item.note}><span className="store-cash-ellipsis">{item.note}</span></Tooltip>
        : <span className="store-cash-empty">{EMPTY_CELL}</span>),
    },
    {
      title: tr('expenses.columns.review'),
      key: 'review',
      width: 130,
      render: (_, item) => (
        <Tooltip title={item.reviewNote || undefined}>
          <span className="store-cash-pills">
            <StatusPill tone={reviewTone(item.reviewStatus)}>{tf(reviewLabelKey(item.reviewStatus))}</StatusPill>
            {item.reviewNote ? <InfoCircleOutlined aria-label={item.reviewNote} /> : null}
          </span>
        </Tooltip>
      ),
    },
    {
      title: tr('expenses.columns.createdBy'),
      key: 'createdBy',
      width: 150,
      render: (_, item) => (
        <div className="store-cash-cell-stack">
          <span>{item.createdByName || EMPTY_CELL}</span>
          <span className="store-cash-sub">{formatStoreDateTime(item.createdAtUtc, timeZone)}</span>
        </div>
      ),
    },
    {
      title: tr('expenses.columns.status'),
      key: 'status',
      width: 96,
      render: (_, item) => (isVoided(item)
        ? <StatusPill tone="gray">{tr('status.Voided')}</StatusPill>
        : <StatusPill tone="green">{tr('status.Active')}</StatusPill>),
    },
    {
      title: tr('expenses.columns.images'),
      key: 'images',
      align: 'right',
      width: 72,
      render: (_, item) => item.imageCount,
    },
  ], [openDrawer, tf, timeZone, tr])

  const tooMany = result?.status === 'tooMany' ? result : null

  return (
    <div className="store-cash-stack">
      <div className="store-cash-toolbar">
        {context.stores.length > 1 ? (
          <RegisterSelect value={query.register} tr={tr} onChange={(register) => onQueryChange({ register })} />
        ) : null}
        <StoreSelect stores={filters.storeOptions} value={storeCode} tr={tr} onChange={(code) => onQueryChange({ store: code })} />
        <RangeFilter from={filters.from} to={filters.to} today={filters.today} tr={tr} onChange={(range) => onQueryChange(range)} />
        <label className="store-cash-field">
          <span className="store-cash-field-label">{tr('filters.category')}</span>
          <Select
            className="store-cash-small-select"
            aria-label={tr('filters.category')}
            value={query.category ?? ALL}
            options={[
              { value: ALL, label: tr('filters.allCategories') },
              ...CASH_EXPENSE_CATEGORIES.map((category) => ({ value: category, label: tf(CATEGORY_LABEL_KEYS[category]) })),
            ]}
            onChange={(value: string) => onQueryChange({ category: isExpenseCategory(value) ? value : undefined })}
          />
        </label>
        <label className="store-cash-field">
          <span className="store-cash-field-label">{tr('filters.review')}</span>
          <Select
            className="store-cash-small-select"
            aria-label={tr('filters.review')}
            value={query.review ?? ALL}
            options={[
              { value: ALL, label: tr('filters.allReview') },
              { value: 'None', label: tr('review.None') },
              { value: 'Reviewed', label: tr('review.Reviewed') },
              { value: 'Flagged', label: tr('review.Flagged') },
            ]}
            onChange={(value: string) => onQueryChange({ review: isReviewStatus(value) ? value : undefined })}
          />
        </label>
        <label className="store-cash-switch">
          <Switch size="small" checked={query.voided} onChange={(checked) => onQueryChange({ voided: checked })} />
          {tr('filters.includeVoided')}
        </label>
        <div className="store-cash-toolbar-actions">
          <RefreshButton loading={loading} tr={tr} onClick={listRequest.reload} />
          <ExportButton
            running={exporter.running}
            progress={exporter.progress}
            disabled={!listQuery || loading || Boolean(tooMany) || items.length === 0}
            tr={tr}
            onClick={handleExport}
          />
        </div>
      </div>

      {loadError ? <LoadErrorAlert message={loadError} tr={tr} onRetry={listRequest.reload} /> : null}
      {tooMany ? (
        <Alert
          type="warning"
          showIcon
          message={tr('expenses.tooMany', {
            total: tooMany.total.toLocaleString('en-US'),
            max: tooMany.maxRows.toLocaleString('en-US'),
          })}
        />
      ) : null}

      <div className="store-cash-summary" aria-label={tr('expenses.summary.region')} aria-live="polite" aria-busy={loading}>
        <span className="store-cash-chip store-cash-chip-total">
          {tr('expenses.summary.total')} <Amount value={tooMany ? null : summary.total} />
          <span>{tr('expenses.summary.count', { count: formatCount(summary.count) })}</span>
        </span>
        {CASH_EXPENSE_CATEGORIES.map((category) => (
          <span key={category} className="store-cash-chip">
            {tf(CATEGORY_LABEL_KEYS[category])} <Amount value={tooMany ? null : summary.byCategory[category]} />
          </span>
        ))}
        <span className="store-cash-summary-notes">
          {t2Restricted ? (
            <span className="store-cash-note">
              <InfoCircleOutlined aria-hidden="true" />
              {tr('t2Note', { days: t2Days })}
            </span>
          ) : null}
          {summary.voidedCount > 0 ? (
            <span className="store-cash-note">{tr('expenses.summary.voidedExcluded', { count: summary.voidedCount })}</span>
          ) : null}
          {summary.flaggedCount > 0 ? (
            <span className="store-cash-note store-cash-note-warning">{tr('expenses.summary.flagged', { count: summary.flaggedCount })}</span>
          ) : null}
        </span>
      </div>

      <section className="store-cash-panel" aria-label={tr('expenses.tableLabel')}>
        <div className="store-cash-panel-head">
          <h2 className="store-cash-panel-title">{tr('expenses.tableTitle')}</h2>
          <span className="store-cash-note">{tr('expenses.rowHint')}</span>
        </div>
        <MeasuredTable<CashExpenseListItem>
          metricId="pos-admin.store-cash.expenses-table"
          className="store-cash-table"
          rowKey={(item) => item.expenseGuid}
          size="small"
          loading={loading}
          columns={columns}
          dataSource={items}
          scroll={{ x: 1150 }}
          locale={{ emptyText: <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description={tr('expenses.empty')} /> }}
          rowClassName={(item) => {
            if (isVoided(item)) return 'store-cash-row-clickable store-cash-row-voided'
            return item.reviewStatus === 'Flagged' ? 'store-cash-row-clickable store-cash-row-flagged' : 'store-cash-row-clickable'
          }}
          onRow={(item) => ({ onClick: () => openDrawer(item.expenseGuid) })}
          pagination={{
            current: page,
            pageSize: paging.pageSize,
            total: items.length,
            showSizeChanger: true,
            pageSizeOptions: [20, 50, 100, 200],
            showTotal: (count) => tr('pagination.total', { count }),
            onChange: (nextPage, nextPageSize) => setPaging({
              filterKey,
              page: nextPageSize !== paging.pageSize ? 1 : nextPage,
              pageSize: nextPageSize,
            }),
          }}
        />
      </section>

      <ExpenseDrawer
        target={drawer}
        timeZone={timeZone}
        active={active}
        tr={tr}
        tf={tf}
        errorText={errorText}
        onClose={() => setDrawer(null)}
        onChanged={listRequest.reload}
      />
    </div>
  )
}
