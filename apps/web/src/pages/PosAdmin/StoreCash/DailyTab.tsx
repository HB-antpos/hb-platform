import { InfoCircleOutlined, WarningOutlined } from '@ant-design/icons'
import { Alert, Empty, Tooltip } from 'antd'
import type { ColumnsType } from 'antd/es/table'
import { useMemo } from 'react'
import { MeasuredTable } from '../../../components/MeasuredTable'
import StatusPill from '../../../components/listToolbar/StatusPill'
import { getCashDaily } from '../../../services/storeCashService'
import type { CashCloseArchive, CashDaily, CashDailyDevice, CashDailyRow, CashPosMovement } from '../../../types/storeCash'
import { buildDailyCsv } from './csv'
import {
  buildCashExportFileName,
  dailyInflow,
  EMPTY_CELL,
  formatStoreDateTime,
  selectionModeKey,
  sortDailyRows,
  summarizeDaily,
  timeZoneOf,
  weekdayKeyOf,
} from './logic'
import { Amount, ExportButton, LoadErrorAlert, RangeFilter, RefreshButton, RegisterSelect, StoreSelect, VarianceAmount, type Tr } from './parts'
import type { CashTabProps } from './tabProps'
import { useCashRequest } from './useCashRequest'
import { useCsvExport } from './useCsvExport'

/**
 * 按日明细：单店逐日一行（日结现金、有无日结、是否已被存款覆盖、当天支出），
 * 展开看每台设备的日结存档与纳入情况。Web 端只读，纳入哪几份日结在员工 App 里手选。
 */
export default function DailyTab({ context, filters, query, active, tr, tf, errorText, onQueryChange }: CashTabProps) {
  const storeCode = filters.storeCode
  const timeZone = timeZoneOf(context.stores, storeCode)
  const requestKey = storeCode ? JSON.stringify(['daily', storeCode, filters.from, filters.to]) : null
  const dailyRequest = useCashRequest<CashDaily>(
    requestKey,
    (signal) => getCashDaily({ storeCode: storeCode as string, from: filters.from, to: filters.to }, signal),
    active,
  )
  const daily = dailyRequest.data
  const loading = dailyRequest.loading
  const loadError = dailyRequest.error ? errorText(dailyRequest.error) : null
  const connected = daily?.dailyCloseConnected ?? context.dailyCloseConnected
  const rows = useMemo(() => (daily ? sortDailyRows(daily.rows) : []), [daily])
  const summary = useMemo(() => summarizeDaily(rows, connected), [connected, rows])
  const exporter = useCsvExport(tr, errorText)

  const handleExport = () => {
    if (!daily) return
    void exporter.run(async () => {
      if (daily.rows.length === 0) return { status: 'empty' }
      const { content, rowCount } = buildDailyCsv(daily, tf)
      return {
        status: 'ok',
        content,
        rowCount,
        fileName: buildCashExportFileName(tr('export.prefix.daily'), filters.from, filters.to, daily.storeCode || storeCode),
      }
    })
  }

  const columns = useMemo<ColumnsType<CashDailyRow>>(() => [
    {
      title: tr('daily.columns.businessDate'),
      key: 'businessDate',
      width: 130,
      render: (_, row) => (
        <span>
          {row.businessDate}
          <span className="store-cash-muted"> {tf(weekdayKeyOf(row.businessDate))}</span>
        </span>
      ),
    },
    {
      title: tr('daily.columns.inflowCash'),
      key: 'inflowCash',
      align: 'right',
      width: 130,
      render: (_, row) => <Amount value={dailyInflow(row, connected)} strong />,
    },
    {
      title: tr('daily.columns.close'),
      key: 'close',
      width: 110,
      render: (_, row) => {
        if (!connected) return <span className="store-cash-empty">{EMPTY_CELL}</span>
        return row.hasClose
          ? <StatusPill tone="green">{tr('daily.hasClose')}</StatusPill>
          : <StatusPill tone="gray">{tr('daily.noClose')}</StatusPill>
      },
    },
    {
      title: tr('daily.columns.covered'),
      key: 'covered',
      width: 110,
      render: (_, row) => {
        if (!connected || !row.hasClose) return <span className="store-cash-empty">{EMPTY_CELL}</span>
        return row.covered
          ? <StatusPill tone="green">{tr('daily.covered')}</StatusPill>
          : <StatusPill tone="orange">{tr('daily.uncovered')}</StatusPill>
      },
    },
    {
      title: tr('daily.columns.expenseTotal'),
      key: 'expenseTotal',
      align: 'right',
      width: 120,
      render: (_, row) => <Amount value={row.expenseTotal} muted={row.expenseTotal === 0} />,
    },
    {
      title: tr('daily.columns.devices'),
      key: 'devices',
      width: 220,
      render: (_, row) => {
        if (row.devices.length === 0) return <span className="store-cash-empty">{EMPTY_CELL}</span>
        const manual = row.devices.filter((device) => device.selectionMode === 'Manual').length
        const stale = row.devices.filter((device) => device.selectionStale).length
        const overlap = row.devices.filter((device) => device.selectionOverlapWarning).length
        return (
          <span className="store-cash-pills">
            <span>{tr('daily.deviceCount', { count: row.devices.length })}</span>
            {manual > 0 ? <StatusPill tone="blue">{tr('daily.manualCount', { count: manual })}</StatusPill> : null}
            {stale > 0 ? <StatusPill tone="red">{tr('daily.staleCount', { count: stale })}</StatusPill> : null}
            {overlap > 0 ? <StatusPill tone="orange">{tr('daily.overlapCount', { count: overlap })}</StatusPill> : null}
          </span>
        )
      },
    },
  ], [connected, tf, tr])

  return (
    <div className="store-cash-stack">
      <div className="store-cash-toolbar">
        {context.stores.length > 1 ? (
          <RegisterSelect value={query.register} tr={tr} onChange={(register) => onQueryChange({ register })} />
        ) : null}
        <StoreSelect stores={filters.storeOptions} value={storeCode} tr={tr} onChange={(code) => onQueryChange({ store: code })} />
        <RangeFilter from={filters.from} to={filters.to} today={filters.today} tr={tr} onChange={(range) => onQueryChange(range)} />
        <div className="store-cash-toolbar-actions">
          <RefreshButton loading={loading} tr={tr} onClick={dailyRequest.reload} />
          <ExportButton
            running={exporter.running}
            progress={exporter.progress}
            disabled={!daily || loading}
            tr={tr}
            onClick={handleExport}
          />
        </div>
      </div>

      {loadError ? <LoadErrorAlert message={loadError} tr={tr} onRetry={dailyRequest.reload} /> : null}

      <div className="store-cash-summary" aria-live="polite">
        <span className="store-cash-chip store-cash-chip-total">
          {tr('daily.summary.inflow')} <Amount value={summary.inflow} />
        </span>
        <span className="store-cash-chip">
          {tr('daily.summary.closeDays')} <strong>{connected ? summary.closeDays : EMPTY_CELL}</strong>
        </span>
        <span className="store-cash-chip">
          {tr('daily.summary.uncoveredDays')}{' '}
          <strong className={summary.uncoveredDays > 0 ? 'store-cash-note-warning' : undefined}>
            {connected ? summary.uncoveredDays : EMPTY_CELL}
          </strong>
        </span>
        <span className="store-cash-chip">
          {tr('daily.summary.expense')} <Amount value={summary.expenseTotal} />
        </span>
        <span className="store-cash-summary-notes">
          <span className="store-cash-note">
            <InfoCircleOutlined aria-hidden="true" />
            {tr('daily.readOnlyHint')}
          </span>
        </span>
      </div>

      <section className="store-cash-panel" aria-label={tr('daily.tableLabel')}>
        <MeasuredTable<CashDailyRow>
          metricId="pos-admin.store-cash.daily-table"
          className="store-cash-table"
          rowKey={(row) => row.businessDate}
          size="small"
          loading={loading}
          columns={columns}
          dataSource={rows}
          pagination={false}
          scroll={{ x: 860 }}
          locale={{ emptyText: <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description={tr('daily.empty')} /> }}
          expandable={{
            rowExpandable: (row) => row.devices.length > 0 || (row.posMovements?.length ?? 0) > 0,
            expandedRowRender: (row) => (
              <>
                <DeviceList devices={row.devices} timeZone={timeZone} tr={tr} tf={tf} />
                {row.posMovements && row.posMovements.length > 0 ? (
                  <PosMovementList movements={row.posMovements} timeZone={timeZone} tr={tr} tf={tf} />
                ) : null}
              </>
            ),
          }}
        />
      </section>
    </div>
  )
}

function DeviceList({ devices, timeZone, tr, tf }: { devices: CashDailyDevice[]; timeZone: string; tr: Tr; tf: Tr }) {
  return (
    <div className="store-cash-devices">
      {devices.map((device) => (
        <div key={device.deviceCode} className="store-cash-device">
          <div className="store-cash-device-head">
            <span className="store-cash-device-code">{device.deviceCode}</span>
            <StatusPill tone={device.selectionMode === 'Manual' ? 'blue' : 'gray'}>{tf(selectionModeKey(device.selectionMode))}</StatusPill>
            <span className="store-cash-device-meta">
              {tr('daily.device.included')} <Amount value={device.includedCash} strong />
            </span>
            {device.selectionMode === 'Manual' && (device.selectedByName || device.selectedAtUtc) ? (
              <span className="store-cash-device-meta">
                {tr('daily.device.selectedBy', {
                  name: device.selectedByName ?? EMPTY_CELL,
                  time: formatStoreDateTime(device.selectedAtUtc, timeZone),
                })}
              </span>
            ) : null}
          </div>
          {device.selectionStale ? (
            <Alert className="store-cash-device-alert" type="error" showIcon icon={<WarningOutlined />} message={tr('daily.device.stale')} />
          ) : null}
          {device.selectionOverlapWarning ? (
            <Alert className="store-cash-device-alert" type="warning" showIcon message={tr('daily.device.overlap')} />
          ) : null}
          {device.selectionMode === 'Manual' && device.selectionReason ? (
            <p className="store-cash-device-meta">{tr('daily.device.reason', { reason: device.selectionReason })}</p>
          ) : null}
          <ArchiveTable archives={device.archives} timeZone={timeZone} tr={tr} />
        </div>
      ))}
    </div>
  )
}

function ArchiveTable({ archives, timeZone, tr }: { archives: CashCloseArchive[]; timeZone: string; tr: Tr }) {
  const columns: ColumnsType<CashCloseArchive> = [
    {
      title: tr('daily.archive.savedAt'),
      key: 'savedAt',
      width: 150,
      render: (_, archive) => formatStoreDateTime(archive.savedAtUtc, timeZone),
    },
    {
      title: tr('daily.archive.period'),
      key: 'period',
      width: 260,
      render: (_, archive) => (
        <Tooltip title={tr('daily.archive.periodHint')}>
          <span>{formatStoreDateTime(archive.periodFromUtc, timeZone)} ~ {formatStoreDateTime(archive.periodToUtc, timeZone)}</span>
        </Tooltip>
      ),
    },
    {
      title: tr('daily.archive.counted'),
      key: 'counted',
      align: 'right',
      width: 110,
      render: (_, archive) => <Amount value={archive.countedCash} />,
    },
    {
      title: tr('daily.archive.expected'),
      key: 'expected',
      align: 'right',
      width: 110,
      render: (_, archive) => <Amount value={archive.expectedCash} />,
    },
    {
      title: tr('daily.archive.variance'),
      key: 'variance',
      align: 'right',
      width: 160,
      render: (_, archive) => <VarianceAmount value={archive.variance} tr={tr} />,
    },
    {
      title: tr('daily.archive.cashOut'),
      key: 'cashOut',
      align: 'right',
      width: 110,
      // 旧后端不返回这三列时按 0 显示，与旧口径一致。
      render: (_, archive) => <Amount value={archive.cashOut ?? 0} muted={!archive.cashOut} />,
    },
    {
      title: tr('daily.archive.cashIn'),
      key: 'cashIn',
      align: 'right',
      width: 110,
      render: (_, archive) => <Amount value={archive.cashIn ?? 0} muted={!archive.cashIn} />,
    },
    {
      title: (
        <Tooltip title={tr('daily.archive.poolInflowHint')}>
          <span>{tr('daily.archive.poolInflow')} <InfoCircleOutlined aria-hidden="true" /></span>
        </Tooltip>
      ),
      key: 'poolInflow',
      align: 'right',
      width: 120,
      render: (_, archive) => <Amount value={archive.poolInflow ?? archive.countedCash} strong />,
    },
    {
      title: tr('daily.archive.included'),
      key: 'included',
      width: 100,
      render: (_, archive) => (archive.included
        ? <StatusPill tone="green">{tr('daily.archive.yes')}</StatusPill>
        : <StatusPill tone="gray">{tr('daily.archive.no')}</StatusPill>),
    },
  ]
  return (
    <MeasuredTable<CashCloseArchive>
      metricId="pos-admin.store-cash.daily-archive-table"
      rowKey={(archive) => archive.closeId}
      size="small"
      columns={columns}
      dataSource={archives}
      pagination={false}
      scroll={{ x: 1170 }}
      locale={{ emptyText: tr('daily.archive.empty') }}
    />
  )
}

// 原因码 → 文案键：写成字面量，便于文案契约测试静态核对。
const POS_REASON_KEYS: Record<string, string> = {
  ChangeTopUp: 'storeCash.daily.pos.reasons.ChangeTopUp',
  OtherIn: 'storeCash.daily.pos.reasons.OtherIn',
  SafeDrop: 'storeCash.daily.pos.reasons.SafeDrop',
  PaidOut: 'storeCash.daily.pos.reasons.PaidOut',
}

/**
 * 当天收银端现金存入 / 取出明细（只读）。冲正记录与被冲正的原记录都保留，
 * 原记录标「已冲正」，两条金额互相抵消；小额支出提示已自动生成支出。
 */
function PosMovementList({ movements, timeZone, tr, tf }: { movements: CashPosMovement[]; timeZone: string; tr: Tr; tf: Tr }) {
  const columns: ColumnsType<CashPosMovement> = [
    {
      title: tr('daily.pos.time'),
      key: 'time',
      width: 150,
      render: (_, item) => formatStoreDateTime(item.occurredAtUtc, timeZone),
    },
    {
      title: tr('daily.pos.device'),
      key: 'device',
      width: 120,
      render: (_, item) => (
        <span>
          {item.deviceCode}
          {item.displayNumber ? <span className="store-cash-muted"> {item.displayNumber}</span> : null}
        </span>
      ),
    },
    {
      title: tr('daily.pos.type'),
      key: 'type',
      width: 190,
      render: (_, item) => {
        const label = POS_REASON_KEYS[item.reasonCode] ? tf(POS_REASON_KEYS[item.reasonCode]) : item.reasonCode
        return (
          <span className="store-cash-pills">
            <span>{label}</span>
            {item.voidsMovementGuid ? <StatusPill tone="gray">{tr('daily.pos.reversal')}</StatusPill> : null}
            {item.isVoided ? <StatusPill tone="gray">{tr('daily.pos.voided')}</StatusPill> : null}
            {item.reasonCode === 'PaidOut' && !item.voidsMovementGuid && !item.isVoided
              ? <StatusPill tone="blue">{tr('daily.pos.paidOutHint')}</StatusPill>
              : null}
          </span>
        )
      },
    },
    {
      title: tr('daily.pos.amount'),
      key: 'amount',
      align: 'right',
      width: 110,
      // 存入为正、取出为负，与收银端日结的加减方向一致。
      render: (_, item) => <Amount value={item.direction === 'In' ? item.amount : -item.amount} signed muted={item.isVoided} />,
    },
    {
      title: tr('daily.pos.cashier'),
      key: 'cashier',
      width: 160,
      render: (_, item) => (item.authorizedByName ? `${item.cashierName} / ${item.authorizedByName}` : item.cashierName || EMPTY_CELL),
    },
    {
      title: tr('daily.pos.note'),
      key: 'note',
      render: (_, item) => (item.note
        ? <Tooltip title={item.note}><span className="store-cash-ellipsis">{item.note}</span></Tooltip>
        : <span className="store-cash-empty">{EMPTY_CELL}</span>),
    },
  ]
  return (
    <div className="store-cash-device">
      <div className="store-cash-device-head">
        <span className="store-cash-device-code">{tr('daily.pos.title', { count: movements.length })}</span>
      </div>
      <MeasuredTable<CashPosMovement>
        metricId="pos-admin.store-cash.daily-pos-movement-table"
        rowKey={(item) => item.movementGuid}
        size="small"
        columns={columns}
        dataSource={movements}
        pagination={false}
        scroll={{ x: 900 }}
      />
    </div>
  )
}
