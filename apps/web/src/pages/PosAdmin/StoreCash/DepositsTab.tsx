import { Empty, Switch, Tooltip } from 'antd'
import type { ColumnsType } from 'antd/es/table'
import { useCallback, useMemo, useRef, useState } from 'react'
import { MeasuredTable } from '../../../components/MeasuredTable'
import StatusPill from '../../../components/listToolbar/StatusPill'
import { listCashDeposits } from '../../../services/storeCashService'
import type { CashDepositListItem, CashPaged } from '../../../types/storeCash'
import { buildDepositsCsv } from './csv'
import DepositDrawer from './DepositDrawer'
import { buildCashExportFileName, EMPTY_CELL, EXPORT_MAX_ROWS, formatStoreDateTime, isVoided, timeZoneOf } from './logic'
import { fetchAllPages, PAGE_FETCH_LIMIT } from './paging'
import { Amount, ExportButton, LoadErrorAlert, RangeFilter, RefreshButton, StoreSelect } from './parts'
import type { CashTabProps } from './tabProps'
import { useCashRequest } from './useCashRequest'
import { useCsvExport } from './useCsvExport'

const DEFAULT_PAGE_SIZE = 20

/**
 * 存款：单店 + 区间 + 含作废开关，表格按服务端分页；点行打开详情抽屉看存单照片、作废。
 * 导出按 limit=200 循环取完当前筛选下的全部记录（上限 5000 行）。
 */
export default function DepositsTab({ context, filters, query, active, tr, tf, errorText, onQueryChange }: CashTabProps) {
  const storeCode = filters.storeCode
  const timeZone = timeZoneOf(context.stores, storeCode)
  const includeVoided = query.voided
  const filterKey = JSON.stringify(['deposits', storeCode, filters.from, filters.to, includeVoided])
  // 页码跟着筛选走：筛选一变就回到第 1 页（不额外发一次旧页码的请求）。
  const [paging, setPaging] = useState({ filterKey, page: 1, pageSize: DEFAULT_PAGE_SIZE })
  const page = paging.filterKey === filterKey ? paging.page : 1
  const pageSize = paging.pageSize
  const [drawer, setDrawer] = useState<{ depositGuid: string; seq: number } | null>(null)
  const drawerSeqRef = useRef(0)

  const listRequest = useCashRequest<CashPaged<CashDepositListItem>>(
    storeCode ? JSON.stringify([filterKey, page, pageSize]) : null,
    (signal) => listCashDeposits({
      storeCode: storeCode as string,
      from: filters.from,
      to: filters.to,
      includeVoided,
      limit: pageSize,
      offset: (page - 1) * pageSize,
    }, signal),
    active,
  )
  const list = listRequest.data
  const loading = listRequest.loading
  const loadError = listRequest.error ? errorText(listRequest.error) : null
  const exporter = useCsvExport(tr, errorText)

  // 每次打开都换一个序号，抽屉据此重新取详情（图片签名地址几分钟就过期）。
  const openDrawer = useCallback((depositGuid: string) => {
    drawerSeqRef.current += 1
    setDrawer({ depositGuid, seq: drawerSeqRef.current })
  }, [])

  const handleExport = () => {
    if (!storeCode) return
    const code = storeCode
    void exporter.run(async (signal, onProgress) => {
      const result = await fetchAllPages<CashDepositListItem>(
        (limit, offset, pageSignal) => listCashDeposits({
          storeCode: code,
          from: filters.from,
          to: filters.to,
          includeVoided,
          limit,
          offset,
        }, pageSignal),
        { pageSize: PAGE_FETCH_LIMIT, maxRows: EXPORT_MAX_ROWS, signal, onProgress, keyOf: (item) => item.depositGuid },
      )
      if (result.status === 'tooMany') return result
      if (result.items.length === 0) return { status: 'empty' }
      const { content, rowCount } = buildDepositsCsv(result.items, tf, timeZone)
      return {
        status: 'ok',
        content,
        rowCount,
        fileName: buildCashExportFileName(tr('export.prefix.deposits'), filters.from, filters.to, code),
      }
    })
  }

  const columns = useMemo<ColumnsType<CashDepositListItem>>(() => [
    {
      title: tr('deposits.columns.depositDate'),
      key: 'depositDate',
      width: 116,
      // 日期做成按钮：键盘也能打开详情（整行点击只服务鼠标）。
      render: (_, item) => (
        <button
          type="button"
          className="store-cash-link"
          onClick={(event) => {
            event.stopPropagation()
            openDrawer(item.depositGuid)
          }}
        >
          {item.depositDate}
        </button>
      ),
    },
    {
      title: tr('deposits.columns.covered'),
      key: 'covered',
      width: 190,
      render: (_, item) => (item.coveredFromDate && item.coveredToDate
        ? tr('deposits.coveredRange', { from: item.coveredFromDate, to: item.coveredToDate })
        : <span className="store-cash-empty">{EMPTY_CELL}</span>),
    },
    {
      title: tr('deposits.columns.total'),
      key: 'total',
      align: 'right',
      width: 130,
      render: (_, item) => <Amount value={item.totalAmount} strong />,
    },
    {
      title: tr('deposits.columns.slips'),
      key: 'slips',
      align: 'right',
      width: 80,
      render: (_, item) => tr('deposits.slipCount', { count: item.slipCount }),
    },
    {
      title: tr('deposits.columns.images'),
      key: 'images',
      align: 'right',
      width: 80,
      render: (_, item) => item.imageCount,
    },
    {
      title: tr('deposits.columns.createdBy'),
      key: 'createdBy',
      width: 120,
      render: (_, item) => item.createdByName || <span className="store-cash-empty">{EMPTY_CELL}</span>,
    },
    {
      title: tr('deposits.columns.createdAt'),
      key: 'createdAt',
      width: 150,
      render: (_, item) => formatStoreDateTime(item.createdAtUtc, timeZone),
    },
    {
      title: tr('deposits.columns.status'),
      key: 'status',
      width: 96,
      render: (_, item) => (isVoided(item)
        ? <StatusPill tone="gray">{tr('status.Voided')}</StatusPill>
        : <StatusPill tone="green">{tr('status.Active')}</StatusPill>),
    },
    {
      title: tr('deposits.columns.note'),
      key: 'note',
      width: 220,
      render: (_, item) => (item.note
        ? <Tooltip title={item.note}><span className="store-cash-ellipsis">{item.note}</span></Tooltip>
        : <span className="store-cash-empty">{EMPTY_CELL}</span>),
    },
  ], [openDrawer, timeZone, tr])

  return (
    <div className="store-cash-stack">
      <div className="store-cash-toolbar">
        <StoreSelect stores={context.stores} value={storeCode} tr={tr} onChange={(code) => onQueryChange({ store: code })} />
        <RangeFilter from={filters.from} to={filters.to} today={filters.today} tr={tr} onChange={(range) => onQueryChange(range)} />
        <label className="store-cash-switch">
          <Switch size="small" checked={includeVoided} onChange={(checked) => onQueryChange({ voided: checked })} />
          {tr('filters.includeVoided')}
        </label>
        <div className="store-cash-toolbar-actions">
          <RefreshButton loading={loading} tr={tr} onClick={listRequest.reload} />
          <ExportButton
            running={exporter.running}
            progress={exporter.progress}
            disabled={!storeCode || (list?.total ?? 0) === 0}
            tr={tr}
            onClick={handleExport}
          />
        </div>
      </div>

      {loadError ? <LoadErrorAlert message={loadError} tr={tr} onRetry={listRequest.reload} /> : null}

      <section className="store-cash-panel" aria-label={tr('deposits.tableLabel')}>
        <div className="store-cash-panel-head">
          <h2 className="store-cash-panel-title">{tr('deposits.tableTitle')}</h2>
          <span className="store-cash-note">{tr('deposits.rowHint')}</span>
        </div>
        <MeasuredTable<CashDepositListItem>
          metricId="pos-admin.store-cash.deposits-table"
          className="store-cash-table"
          rowKey={(item) => item.depositGuid}
          size="small"
          loading={loading}
          columns={columns}
          dataSource={list?.items ?? []}
          scroll={{ x: 1180 }}
          locale={{ emptyText: <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description={tr('deposits.empty')} /> }}
          rowClassName={(item) => (isVoided(item) ? 'store-cash-row-clickable store-cash-row-voided' : 'store-cash-row-clickable')}
          onRow={(item) => ({ onClick: () => openDrawer(item.depositGuid) })}
          pagination={{
            current: page,
            pageSize,
            total: list?.total ?? 0,
            showSizeChanger: true,
            pageSizeOptions: [20, 50, 100, 200],
            showTotal: (count) => tr('pagination.total', { count }),
            onChange: (nextPage, nextPageSize) => setPaging({
              filterKey,
              page: nextPageSize !== pageSize ? 1 : nextPage,
              pageSize: nextPageSize,
            }),
          }}
        />
      </section>

      <DepositDrawer
        target={drawer}
        timeZone={timeZone}
        active={active}
        tr={tr}
        errorText={errorText}
        onClose={() => setDrawer(null)}
        onChanged={listRequest.reload}
      />
    </div>
  )
}
