// 抽屉主体：四个数、去年日销量 + 进货 + 折扣期 + 关键日期大图（可切分店）、分店明细表（今年订货可直接改）。
import { Button, Popconfirm, Select, Spin } from 'antd'
import { Fragment, useEffect, useMemo, useState } from 'react'
import { useTranslation } from 'react-i18next'

import type { SupplierPreorderMatrixRow, SupplierPreorderStoreHistory, SupplierPreorderStoreRef } from '../../../types/supplierPreorder'
import HistoryTrendChart from '../Ordering/HistoryTrendChart'
import { makeLineKey } from '../Ordering/lineSaveQueue.logic'
import {
  aggregateBatches,
  expandDaily,
  formatDateSpan,
  formatPercent,
  resolveSeriesLength,
  sellThroughTone,
  windowDayCount,
} from '../Ordering/orderingHistory.logic'
import { HISTORY_COLORS, RefTooltip } from '../Ordering/orderingHistoryCells'
import '../Ordering/orderingMessages'
import '../Ordering/styles.css'

import './registerSpAdminMessages'
import { buildStoreDetailRows, sumStoreDetailRows, suggestFillBlanks, toLastYearCartons, type StoreDetailRow } from './summaryMatrix.logic'
import { SummaryQtyInput } from './SummaryQtyInput'
import { formatCbm, formatInteger, formatMoney } from './supplierPreorderAdmin.logic'
import type { SummaryHistoryEntry } from './useSummaryEditing'

const ORDERING_NS = 'supplierPreorderOrdering'
const ALL_STORES = 'all'

interface SummaryDrawerBodyProps {
  row: SupplierPreorderMatrixRow
  stores: SupplierPreorderStoreRef[]
  entry?: SummaryHistoryEntry
  editable: boolean
  busyKeys: ReadonlySet<string>
  onRetry: () => void
  commit: (storeGuid: string, itemGuid: string, cartons: number) => void
}

const rateClass = (ratio: number | null | undefined) => {
  const tone = sellThroughTone(ratio)
  return tone === 'good' ? 'sp-admin-sum-rate is-good' : tone === 'low' ? 'sp-admin-sum-rate is-low' : 'sp-admin-sum-rate'
}

export default function SummaryDrawerBody({ row, stores, entry, editable, busyKeys, onRetry, commit }: SummaryDrawerBodyProps) {
  const { t } = useTranslation()
  const detail = entry?.status === 'ready' ? entry.detail : null
  const [expandedKey, setExpandedKey] = useState<string | null>(null)
  const [chartStore, setChartStore] = useState<string>(ALL_STORES)

  // 换商品：折叠展开行、图表回到「全部分店」
  useEffect(() => {
    setExpandedKey(null)
    setChartStore(ALL_STORES)
  }, [row.itemGuid])

  const detailRows = useMemo(
    () => buildStoreDetailRows(stores, detail?.stores, row.cartonQty, (storeGuid) => row.storeCartons[storeGuid] ?? 0),
    [stores, detail?.stores, row.cartonQty, row.storeCartons],
  )
  const totals = useMemo(() => sumStoreDetailRows(detailRows, row.cartonQty), [detailRows, row.cartonQty])
  const suggestions = useMemo(() => suggestFillBlanks(detailRows, row.cartonQty), [detailRows, row.cartonQty])

  // 四个数：优先用抽屉历史里的全部分店合计；还没加载完先用矩阵行上的「去年全店」摘要占位
  const allStores = detail?.store?.hasData ? detail.store : null
  const purchaseQty = allStores?.purchaseQty ?? row.lastYear?.purchaseQty ?? null
  const salesQty = allStores?.salesQty ?? row.lastYear?.salesQty ?? null
  const sellThrough = allStores ? allStores.sellThrough : (row.lastYear?.sellThrough ?? null)
  const batchCount = allStores?.purchaseBatches ?? null
  const remaining = purchaseQty !== null && salesQty !== null ? Math.max(0, purchaseQty - salesQty) : null
  const thisYearPieces = row.totalPieces
  const enabledCount = stores.length
  const overall = salesQty ? thisYearPieces / salesQty : null

  // 大图的数据：全部分店合计，或选中的那一家
  const chartHistory: SupplierPreorderStoreHistory | null = useMemo(() => {
    if (!detail) return null
    if (chartStore === ALL_STORES) return detail.store ?? null
    return detail.stores?.find((item) => item.storeGuid === chartStore) ?? null
  }, [detail, chartStore])
  const series = useMemo(() => {
    if (!detail || !chartHistory?.hasData) return null
    const length = resolveSeriesLength(windowDayCount(detail.windowFrom, detail.windowTo), chartHistory.daily)
    const { sales, discount } = expandDaily(chartHistory.daily, length)
    const { warehouse, supplier } = aggregateBatches(chartHistory.batches, detail.windowFrom, length)
    return { length, sales, discount, warehouse, supplier }
  }, [detail, chartHistory])

  const chartOptions = useMemo(
    () => [
      { value: ALL_STORES, label: t('spAdmin.summary.allStores') },
      ...(detail?.stores ?? []).filter((item) => item.storeGuid && item.hasData).map((item) => ({ value: item.storeGuid as string, label: item.storeName })),
    ],
    [detail?.stores, t],
  )

  const isRef = detail?.source === 'Ref'
  const refCode = detail?.ref?.itemNumber?.trim() || detail?.ref?.productCode?.trim() || detail?.productCode || ''

  const applySuggestions = () => suggestions.forEach((item) => commit(item.storeGuid, row.itemGuid, item.cartons))

  const windowFrom = detail?.windowFrom
  const windowTo = detail?.windowTo

  return (
    <div className="sp-admin-sum-dbody">
      <section className="sp-admin-sum-band sp-admin-number" aria-label={t('spAdmin.summary.bandAria')}>
        <div>
          <div className="sp-admin-sum-band-label">{t('spAdmin.summary.lastYearPurchase')}</div>
          <div className="sp-admin-sum-band-value">
            {purchaseQty === null ? '—' : formatInteger(purchaseQty)}
            <small>{t('spAdmin.summary.pcs')}</small>
          </div>
          <div className="sp-admin-sum-band-sub">
            {purchaseQty === null
              ? '—'
              : batchCount === null
                ? t('spAdmin.summary.foldedCartons', { count: toLastYearCartons(purchaseQty, row.cartonQty).toFixed(1) })
                : t('spAdmin.summary.foldedBatches', { count: toLastYearCartons(purchaseQty, row.cartonQty).toFixed(1), batches: batchCount })}
          </div>
        </div>
        <div>
          <div className="sp-admin-sum-band-label">{t('spAdmin.summary.lastYearSales')}</div>
          <div className="sp-admin-sum-band-value">
            {salesQty === null ? '—' : formatInteger(salesQty)}
            <small>{t('spAdmin.summary.pcs')}</small>
          </div>
          <div className="sp-admin-sum-band-sub">{salesQty === null ? '—' : t('spAdmin.summary.foldedCartons', { count: toLastYearCartons(salesQty, row.cartonQty).toFixed(1) })}</div>
        </div>
        <div>
          <div className="sp-admin-sum-band-label">{t('spAdmin.summary.sellThrough')}</div>
          <div className="sp-admin-sum-band-value">
            <span className={rateClass(sellThrough)}>{formatPercent(sellThrough)}</span>
          </div>
          <div className="sp-admin-sum-band-sub">{remaining === null ? '—' : t('spAdmin.summary.remaining', { count: formatInteger(remaining) })}</div>
        </div>
        <div className="is-this-year">
          <div className="sp-admin-sum-band-label">{t('spAdmin.summary.thisYearOrdered', { count: enabledCount })}</div>
          <div className="sp-admin-sum-band-value is-blue">
            {formatInteger(row.totalCartons)}
            <small>{t('spAdmin.summary.cartons')}</small>
          </div>
          <div className="sp-admin-sum-band-sub">
            {overall === null
              ? t('spAdmin.summary.piecesOnly', { count: formatInteger(thisYearPieces) })
              : t('spAdmin.summary.thisYearVsLast', { pieces: formatInteger(thisYearPieces), ratio: formatPercent(overall) })}
          </div>
        </div>
      </section>

      <section className={isRef ? 'sp-admin-sum-card is-ref' : 'sp-admin-sum-card'}>
        <header className="sp-admin-sum-card-head">
          <h3>{t('spAdmin.summary.chartTitle')}</h3>
          {detail && chartOptions.length > 1 ? (
            <Select
              size="small"
              className="sp-admin-sum-chart-select"
              value={chartStore}
              options={chartOptions}
              onChange={setChartStore}
              aria-label={t('spAdmin.summary.chartStoreAria')}
              popupMatchSelectWidth={false}
            />
          ) : null}
          {isRef ? (
            <RefTooltip t={t} refItem={detail?.ref} ownPrice={row.price} fallbackCode={detail?.productCode}>
              <span className="sp-hist-ref-badge" tabIndex={0}>
                {t('spAdmin.summary.refTag', { code: refCode })}
              </span>
            </RefTooltip>
          ) : null}
          <span className="sp-admin-toolbar-spacer" />
          <ChartLegend />
        </header>
        <div className="sp-admin-sum-chart">
          {!entry || entry.status === 'loading' ? (
            <div className="sp-admin-sum-state">
              <Spin size="small" /> <span className="sp-admin-muted">{t('spAdmin.summary.historyLoading')}</span>
            </div>
          ) : entry.status === 'error' ? (
            <div className="sp-admin-sum-state">
              <span className="sp-admin-muted">{entry.message}</span>
              <Button size="small" onClick={onRetry}>
                {t('spAdmin.common.retry')}
              </Button>
            </div>
          ) : entry.detail.source === 'None' ? (
            <div className="sp-admin-sum-state is-empty">{t('spAdmin.summary.historyNone')}</div>
          ) : !series || !chartHistory ? (
            <div className="sp-admin-sum-state is-empty">{t('spAdmin.summary.historyNoStore')}</div>
          ) : (
            <HistoryTrendChart
              t={t}
              windowFrom={entry.detail.windowFrom}
              length={series.length}
              sales={series.sales}
              discount={series.discount}
              warehouse={series.warehouse}
              supplier={series.supplier}
              keyDates={entry.detail.keyDates}
              discountRanges={chartHistory.discountRanges}
            />
          )}
        </div>
      </section>

      <section className="sp-admin-sum-card is-flush">
        <header className="sp-admin-sum-card-head is-pad">
          <h3>{t('spAdmin.summary.storesTitle')}</h3>
          <span className="sp-admin-muted sp-admin-small sp-admin-number">
            {windowFrom && windowTo ? t('spAdmin.summary.windowHint', { from: windowFrom.slice(0, 10), to: windowTo.slice(0, 10) }) : t('spAdmin.summary.expandHint')}
          </span>
          <span className="sp-admin-toolbar-spacer" />
          <Popconfirm
            title={t('spAdmin.summary.fillConfirmTitle', { count: suggestions.length })}
            description={
              <ul className="sp-admin-sum-fill-list sp-admin-number">
                {suggestions.map((item) => (
                  <li key={item.storeGuid}>{t('spAdmin.summary.fillConfirmItem', { store: item.storeName, cartons: item.cartons })}</li>
                ))}
              </ul>
            }
            okText={t('spAdmin.summary.fillConfirmOk')}
            cancelText={t('spAdmin.common.cancel')}
            disabled={!editable || suggestions.length === 0}
            onConfirm={applySuggestions}
          >
            <Button size="small" disabled={!editable || suggestions.length === 0} title={!editable ? t('spAdmin.summary.readOnlyHint') : suggestions.length === 0 ? t('spAdmin.summary.fillNone') : undefined}>
              {t('spAdmin.summary.fillBlanks')}
            </Button>
          </Popconfirm>
        </header>
        <table className="sp-admin-sum-stores sp-admin-number">
          <colgroup>
            <col style={{ width: 150 }} />
            <col style={{ width: 70 }} />
            <col style={{ width: 80 }} />
            <col style={{ width: 62 }} />
            <col style={{ width: 70 }} />
            <col style={{ width: 62 }} />
            <col style={{ width: 90 }} />
            <col />
          </colgroup>
          <thead>
            <tr>
              <th>{t('spAdmin.summary.colStore')}</th>
              <th>{t('spAdmin.summary.colPurchase')}</th>
              <th>{t('spAdmin.summary.colSales')} ↓</th>
              <th>{t('spAdmin.summary.colSellThrough')}</th>
              <th>{t('spAdmin.summary.colDiscount')}</th>
              <th>{t('spAdmin.summary.colFolded')}</th>
              <th>{t('spAdmin.summary.colThisYear')}</th>
              <th>{t('spAdmin.summary.colRatio')}</th>
            </tr>
          </thead>
          <tbody>
            {detailRows.map((item, index) => (
              <Fragment key={item.key}>
                <StoreRow
                  item={item}
                  index={index}
                  itemGuid={row.itemGuid}
                  expanded={expandedKey === item.key}
                  editable={editable}
                  busy={!!item.storeGuid && busyKeys.has(makeLineKey(item.storeGuid, row.itemGuid))}
                  onToggle={() => setExpandedKey((current) => (current === item.key ? null : item.key))}
                  commit={commit}
                />
                {expandedKey === item.key ? (
                  <tr className="is-expand">
                    <td colSpan={8}>
                      <StoreExpand history={item.history} />
                    </td>
                  </tr>
                ) : null}
              </Fragment>
            ))}
            <tr className="is-total">
              <td>{t('spAdmin.summary.totalLabel')}</td>
              <td>{formatInteger(totals.purchaseQty)}</td>
              <td>{formatInteger(totals.salesQty)}</td>
              <td>{formatPercent(totals.sellThrough)}</td>
              <td />
              <td>{totals.foldedCartons.toFixed(1)}</td>
              <td className="is-blue">{t('spAdmin.summary.cartonsValue', { count: formatInteger(totals.thisYear) })}</td>
              <td>{formatPercent(totals.ratio)}</td>
            </tr>
          </tbody>
        </table>
      </section>
    </div>
  )
}

function ChartLegend() {
  const { t } = useTranslation()
  const items = [
    { key: 'sales', label: t(`${ORDERING_NS}.expand.legendSales`), swatch: <i style={{ background: HISTORY_COLORS.sales }} /> },
    { key: 'warehouse', label: t(`${ORDERING_NS}.expand.legendWarehouse`), swatch: <i style={{ background: HISTORY_COLORS.warehouse }} /> },
    { key: 'supplier', label: t(`${ORDERING_NS}.expand.legendSupplier`), swatch: <i style={{ background: HISTORY_COLORS.supplier }} /> },
    { key: 'discount', label: t(`${ORDERING_NS}.expand.legendDiscount`), swatch: <i style={{ background: HISTORY_COLORS.discount, border: '1px solid #f5b5c4' }} /> },
    { key: 'keyDate', label: t(`${ORDERING_NS}.expand.legendKeyDate`), swatch: <i className="is-dashed" style={{ borderColor: HISTORY_COLORS.keyDate }} /> },
  ]
  return (
    <div className="sp-hist-legend">
      {items.map((item) => (
        <span key={item.key}>
          {item.swatch}
          {item.label}
        </span>
      ))}
    </div>
  )
}

interface StoreRowProps {
  item: StoreDetailRow
  index: number
  itemGuid: string
  expanded: boolean
  editable: boolean
  busy: boolean
  onToggle: () => void
  commit: (storeGuid: string, itemGuid: string, cartons: number) => void
}

function StoreRow({ item, index, itemGuid, expanded, editable, busy, onToggle, commit }: StoreRowProps) {
  const { t } = useTranslation()
  const history = item.history
  const discountCell = !item.hasData || !history ? (
    '—'
  ) : history.discountStatus === 'unknown' ? (
    <span className="sp-admin-muted">{t('spAdmin.summary.discountUnknown')}</span>
  ) : history.hasDiscount ? (
    <span className="sp-admin-sum-pill is-red">{t('spAdmin.summary.discountYes')}</span>
  ) : (
    <span className="sp-admin-sum-pill is-green">{t('spAdmin.summary.discountNo')}</span>
  )
  const ratioText = item.flag === 'missed' ? t('spAdmin.summary.notOrdered') : item.ratio === null ? '—' : formatPercent(item.ratio)
  return (
    <tr className={expanded ? 'is-open' : undefined}>
      <td>
        <div className="sp-admin-sum-name-cell">
          <button type="button" className="sp-admin-sum-expand-btn" aria-expanded={expanded} disabled={!item.hasData} onClick={onToggle}>
            <span className={expanded ? 'sp-admin-sum-caret is-down' : 'sp-admin-sum-caret'} aria-hidden="true" />
            <span className="sp-admin-ellipsis" title={item.storeName}>
              {item.storeName}
            </span>
          </button>
          {!item.enabled ? <span className="sp-admin-sum-pill is-gray">{t('spAdmin.summary.storeOff')}</span> : null}
        </div>
      </td>
      <td>{item.hasData ? formatInteger(item.purchaseQty) : '—'}</td>
      <td className="sp-admin-strong">{item.hasData ? formatInteger(item.salesQty) : '—'}</td>
      <td>{item.hasData ? <span className={rateClass(item.sellThrough)}>{formatPercent(item.sellThrough)}</span> : '—'}</td>
      <td>{discountCell}</td>
      <td>{item.hasData ? item.foldedCartons.toFixed(1) : '—'}</td>
      <td>
        {item.storeGuid ? (
          <SummaryQtyInput
            value={item.thisYear}
            disabled={!editable}
            busy={busy}
            data-qty-index={index}
            ariaLabel={t('spAdmin.summary.qtyAria', { store: item.storeName })}
            onCommit={(cartons) => commit(item.storeGuid as string, itemGuid, cartons)}
          />
        ) : (
          <span className="sp-admin-faint">—</span>
        )}
      </td>
      <td>
        <span className={item.flag === 'missed' ? 'sp-admin-sum-ratio is-miss' : item.flag === 'high' ? 'sp-admin-sum-ratio is-high' : 'sp-admin-sum-ratio'}>{ratioText}</span>
      </td>
    </tr>
  )
}

/** 展开一家店：每批进货（日期 / 来源 / 数量 / 单号）+ 销售期、零售价、期末剩余 */
function StoreExpand({ history }: { history: SupplierPreorderStoreHistory | null }) {
  const { t } = useTranslation()
  if (!history) return null
  const batches = [...history.batches].sort((a, b) => a.date.localeCompare(b.date))
  const remaining = Math.max(0, history.purchaseQty - history.salesQty)
  const hasRetail = history.retailPrice !== null && history.retailPrice !== undefined
  const hasMarkdown = hasRetail && history.discountPriceMin !== null && history.discountPriceMin !== undefined && history.discountPriceMin < (history.retailPrice as number)
  return (
    <div className="sp-admin-sum-expand sp-admin-number">
      <div>
        <h4>{t('spAdmin.summary.batchesTitle', { batches: history.purchaseBatches || batches.length, qty: formatInteger(history.purchaseQty) })}</h4>
        {batches.length === 0 ? <div className="sp-admin-muted">{t('spAdmin.summary.noBatches')}</div> : null}
        {batches.map((batch, index) => (
          <div key={`${batch.date}-${batch.documentNo ?? ''}-${index}`} className="sp-admin-sum-batch">
            <span>{batch.date.slice(0, 10)}</span>
            <span className="sp-admin-sum-src">
              <i style={{ background: batch.source === 'supplier' ? HISTORY_COLORS.supplier : HISTORY_COLORS.warehouse }} />
              {batch.source === 'supplier' ? t('spAdmin.summary.sourceSupplier') : t('spAdmin.summary.sourceWarehouse')}
            </span>
            <span className="is-qty">{t('spAdmin.summary.lastYearPieces', { count: formatInteger(batch.quantity) })}</span>
            <span className="sp-admin-mono sp-admin-muted sp-admin-ellipsis" title={batch.documentNo ?? undefined}>
              {batch.documentNo ?? ''}
            </span>
          </div>
        ))}
      </div>
      <div>
        <h4>{t('spAdmin.summary.salesTitle')}</h4>
        <div className="sp-admin-sum-kv">
          <span className="sp-admin-muted">{t('spAdmin.summary.salesPeriod')}</span>
          <span>{t('spAdmin.summary.salesPeriodValue', { span: formatDateSpan(history.salesFirst, history.salesLast), qty: formatInteger(history.salesQty) })}</span>
        </div>
        <div className="sp-admin-sum-kv">
          <span className="sp-admin-muted">{t('spAdmin.summary.retailPrice')}</span>
          <span>
            {hasMarkdown
              ? t('spAdmin.summary.retailMarkdown', { retail: formatMoney(history.retailPrice), discount: formatMoney(history.discountPriceMin) })
              : hasRetail
                ? formatMoney(history.retailPrice)
                : '—'}
          </span>
        </div>
        <div className="sp-admin-sum-kv">
          <span className="sp-admin-muted">{t('spAdmin.summary.endStock')}</span>
          <span>{t('spAdmin.summary.lastYearPieces', { count: formatInteger(remaining) })}</span>
        </div>
      </div>
    </div>
  )
}

/** 抽屉头部 / 底部用到的行金额与体积文案 */
export function formatRowAmountVolume(row: Pick<SupplierPreorderMatrixRow, 'totalAmount' | 'totalCbm'>): { amount: string; cbm: string } {
  return { amount: formatMoney(row.totalAmount), cbm: formatCbm(row.totalCbm) }
}
