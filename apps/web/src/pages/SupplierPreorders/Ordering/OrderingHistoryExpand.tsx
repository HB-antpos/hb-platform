// 下单表格展开行：左侧去年日销量 + 进货大图，右侧「去年本店小结」与「各分店去年对比」。
// 数据由页面懒加载（getSupplierPreorderItemHistory），这里只负责展示加载中 / 失败 / 数据三种状态。
import { Button, Spin, Tag } from 'antd'
import type { TFunction } from 'i18next'
import { useMemo, type ReactNode } from 'react'

import type { SupplierPreorderItemHistoryDetail, SupplierPreorderOrderingItem, SupplierPreorderStoreHistory } from '../../../types/supplierPreorder'

import HistoryTrendChart from './HistoryTrendChart'
import { aggregateBatches, expandDaily, formatDateSpan, formatPercent, refDisplayCode, resolveSeriesLength, windowDayCount } from './orderingHistory.logic'
import { HISTORY_COLORS, RefTooltip, sellThroughClassName } from './orderingHistoryCells'
import { formatAud, formatInteger } from './orderingPage.logic'

const NS = 'supplierPreorderOrdering'

/** 展开行的加载状态（按商品缓存在页面里） */
export type HistoryDetailEntry =
  | { status: 'loading' }
  | { status: 'error'; message: string }
  | { status: 'ready'; detail: SupplierPreorderItemHistoryDetail }

interface OrderingHistoryExpandProps {
  t: TFunction
  row: SupplierPreorderOrderingItem
  entry?: HistoryDetailEntry
  storeName: string
  onRetry: () => void
}

export default function OrderingHistoryExpand({ t, row, entry, storeName, onRetry }: OrderingHistoryExpandProps) {
  if (!entry || entry.status === 'loading') {
    return (
      <div className="sp-hist-expand-state">
        <Spin size="small" /> <span className="sp-order-muted">{t(`${NS}.expand.loading`)}</span>
      </div>
    )
  }
  if (entry.status === 'error') {
    return (
      <div className="sp-hist-expand-state">
        <span className="sp-order-muted">{entry.message}</span>
        <Button size="small" onClick={onRetry}>
          {t(`${NS}.expand.retry`)}
        </Button>
      </div>
    )
  }
  return <HistoryDetailView t={t} row={row} detail={entry.detail} storeName={storeName} />
}

function HistoryDetailView({ t, row, detail, storeName }: { t: TFunction; row: SupplierPreorderOrderingItem; detail: SupplierPreorderItemHistoryDetail; storeName: string }) {
  const isRef = detail.source === 'Ref'
  const refCode = refDisplayCode(detail.ref, detail.productCode)
  const store = detail.store?.hasData ? detail.store : null
  const stores = detail.stores ?? null

  const series = useMemo(() => {
    if (!store) return null
    const length = resolveSeriesLength(windowDayCount(detail.windowFrom, detail.windowTo), store.daily)
    const { sales, discount } = expandDaily(store.daily, length)
    const { warehouse, supplier } = aggregateBatches(store.batches, detail.windowFrom, length)
    return { length, sales, discount, warehouse, supplier }
  }, [store, detail.windowFrom, detail.windowTo])

  const title = isRef
    ? t(`${NS}.expand.chartTitleRef`, { code: refCode })
    : t(`${NS}.expand.chartTitleOwn`, { store: storeName, code: row.supplierItemCode })

  return (
    <div className="sp-hist-expand">
      <section className={isRef ? 'sp-hist-card sp-hist-chart-card is-ref' : 'sp-hist-card sp-hist-chart-card'}>
        <header className="sp-hist-card-head">
          <h3>
            {isRef ? (
              <RefTooltip t={t} refItem={detail.ref} ownPrice={row.price} fallbackCode={detail.productCode}>
                <span className="sp-hist-ref-title" tabIndex={0}>
                  {title}
                </span>
              </RefTooltip>
            ) : (
              title
            )}
          </h3>
          <ChartLegend t={t} />
        </header>
        {detail.source === 'None' ? (
          <div className="sp-hist-empty is-large">{t(`${NS}.history.none`)}</div>
        ) : !store || !series ? (
          <div className="sp-hist-empty is-large">{t(`${NS}.history.noStore`)}</div>
        ) : (
          <HistoryTrendChart
            t={t}
            windowFrom={detail.windowFrom}
            length={series.length}
            sales={series.sales}
            discount={series.discount}
            warehouse={series.warehouse}
            supplier={series.supplier}
            keyDates={detail.keyDates}
            discountRanges={store.discountRanges}
          />
        )}
      </section>

      <div className="sp-hist-side">
        <section className="sp-hist-card">
          <header className="sp-hist-card-head">
            <h3>{t(`${NS}.expand.summaryTitle`)}</h3>
          </header>
          {isRef ? <div className="sp-hist-ref-note">{t(`${NS}.expand.summaryRefNote`, { code: refCode })}</div> : null}
          {store ? <StoreSummary t={t} store={store} suggestedCartons={detail.suggestedCartons} isRef={isRef} /> : <div className="sp-order-muted sp-order-small">{t(`${NS}.expand.storeNoData`)}</div>}
        </section>

        {stores && stores.length ? (
          <section className="sp-hist-card">
            <header className="sp-hist-card-head">
              <h3>{t(`${NS}.expand.storesTitle`)}</h3>
            </header>
            <StoresCompareTable t={t} stores={stores} />
          </section>
        ) : null}
      </div>
    </div>
  )
}

function ChartLegend({ t }: { t: TFunction }) {
  const items: { key: string; label: string; swatch: ReactNode }[] = [
    { key: 'sales', label: t(`${NS}.expand.legendSales`), swatch: <i style={{ background: HISTORY_COLORS.sales }} /> },
    { key: 'warehouse', label: t(`${NS}.expand.legendWarehouse`), swatch: <i style={{ background: HISTORY_COLORS.warehouse }} /> },
    { key: 'supplier', label: t(`${NS}.expand.legendSupplier`), swatch: <i style={{ background: HISTORY_COLORS.supplier }} /> },
    { key: 'discount', label: t(`${NS}.expand.legendDiscount`), swatch: <i style={{ background: HISTORY_COLORS.discount, border: '1px solid #f5b5c4' }} /> },
    { key: 'keyDate', label: t(`${NS}.expand.legendKeyDate`), swatch: <i className="is-dashed" style={{ borderColor: HISTORY_COLORS.keyDate }} /> },
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

function SummaryRow({ label, children }: { label: string; children: ReactNode }) {
  return (
    <div className="sp-hist-summary-row">
      <span className="sp-hist-summary-label">{label}</span>
      <span className="sp-hist-summary-value">{children}</span>
    </div>
  )
}

function StoreSummary({ t, store, suggestedCartons, isRef }: { t: TFunction; store: SupplierPreorderStoreHistory; suggestedCartons?: number | null; isRef: boolean }) {
  const hasRetail = store.retailPrice !== null && store.retailPrice !== undefined
  const hasMarkdown = hasRetail && store.discountPriceMin !== null && store.discountPriceMin !== undefined && store.discountPriceMin < (store.retailPrice as number)
  return (
    <div className="sp-hist-summary sp-order-number">
      <SummaryRow label={t(`${NS}.expand.rowPurchase`)}>
        {store.purchaseBatches > 0 || store.purchaseQty > 0
          ? t(`${NS}.expand.purchaseValue`, {
              batches: formatInteger(store.purchaseBatches),
              qty: formatInteger(store.purchaseQty),
              span: formatDateSpan(store.purchaseFirst, store.purchaseLast),
            })
          : '—'}
      </SummaryRow>
      <SummaryRow label={t(`${NS}.expand.rowSales`)}>
        {store.salesQty > 0
          ? t(`${NS}.expand.salesValue`, { qty: formatInteger(store.salesQty), span: formatDateSpan(store.salesFirst, store.salesLast) })
          : '—'}
      </SummaryRow>
      <SummaryRow label={t(`${NS}.expand.rowSellThrough`)}>
        <b className={sellThroughClassName(store.sellThrough)}>{formatPercent(store.sellThrough)}</b>
      </SummaryRow>
      <SummaryRow label={t(`${NS}.expand.rowDiscount`)}>
        {store.discountStatus === 'unknown' ? (
          <span className="sp-order-muted">{t(`${NS}.history.discountUnknown`)}</span>
        ) : (
          <>
            {store.hasDiscount ? (
              <Tag color="red" className="sp-hist-tag">
                {t(`${NS}.history.hasDiscount`)}
              </Tag>
            ) : (
              <Tag color="green" className="sp-hist-tag">
                {t(`${NS}.history.noDiscount`)}
              </Tag>
            )}
            {store.hasDiscount && store.discountShare !== null && store.discountShare !== undefined
              ? t(`${NS}.history.discountShare`, { rate: formatPercent(store.discountShare) })
              : null}
            {store.hasDiscount && store.discountPriceMin !== null && store.discountPriceMin !== undefined
              ? ` · ${t(`${NS}.expand.discountMin`, { price: formatAud(store.discountPriceMin) })}`
              : null}
            {store.discountStatus === 'partial' ? <span className="sp-hist-sub is-warn"> · {t(`${NS}.history.discountPartial`)}</span> : null}
          </>
        )}
      </SummaryRow>
      <SummaryRow label={t(`${NS}.expand.rowRetail`)}>
        {hasMarkdown
          ? t(`${NS}.expand.retailValue`, { retail: formatAud(store.retailPrice), discount: formatAud(store.discountPriceMin) })
          : hasRetail
            ? formatAud(store.retailPrice)
            : '—'}
      </SummaryRow>
      <SummaryRow label={t(`${NS}.expand.rowSuggested`)}>
        {suggestedCartons !== null && suggestedCartons !== undefined ? (
          <>
            <b>{t(`${NS}.expand.suggestedValue`, { cartons: formatInteger(suggestedCartons) })}</b>
            <span className="sp-hist-summary-note">{isRef ? t(`${NS}.expand.suggestedRefNote`) : t(`${NS}.expand.suggestedNote`)}</span>
          </>
        ) : (
          '—'
        )}
      </SummaryRow>
    </div>
  )
}

function StoresCompareTable({ t, stores }: { t: TFunction; stores: SupplierPreorderStoreHistory[] }) {
  const discountText = (store: SupplierPreorderStoreHistory) => {
    if (!store.hasData) return '—'
    if (store.discountStatus === 'unknown') return t(`${NS}.expand.discountUnknownShort`)
    return store.hasDiscount ? t(`${NS}.expand.yes`) : t(`${NS}.expand.no`)
  }
  return (
    <div className="sp-hist-compare-wrap">
      <table className="sp-hist-compare sp-order-number">
        <thead>
          <tr>
            <th>{t(`${NS}.expand.colStore`)}</th>
            <th>{t(`${NS}.expand.colPurchase`)}</th>
            <th>{t(`${NS}.expand.colSales`)}</th>
            <th>{t(`${NS}.expand.colSellThrough`)}</th>
            <th>{t(`${NS}.expand.colDiscount`)}</th>
            <th>{t(`${NS}.expand.colThisYear`)}</th>
          </tr>
        </thead>
        <tbody>
          {stores.map((store) => (
            <tr key={store.storeCode || store.storeName} className={store.isCurrent ? 'is-current' : undefined}>
              <td>{store.storeName}</td>
              <td>{store.hasData ? formatInteger(store.purchaseQty) : '—'}</td>
              <td>{store.hasData ? formatInteger(store.salesQty) : '—'}</td>
              <td>
                <span className={sellThroughClassName(store.hasData ? store.sellThrough : null)}>{store.hasData ? formatPercent(store.sellThrough) : '—'}</span>
              </td>
              <td>{discountText(store)}</td>
              <td>{store.thisYearCartons ? formatInteger(store.thisYearCartons) : '—'}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  )
}
