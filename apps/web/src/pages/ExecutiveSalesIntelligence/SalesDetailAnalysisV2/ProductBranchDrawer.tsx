import { Alert, Button, Drawer, Segmented, Skeleton, Tooltip } from 'antd'
import { Fragment, useEffect, useMemo, useState } from 'react'
import { MetricPair, useReportText } from '../ReportWorkbench/ReportControls'
import { growth } from '../ReportWorkbench/logic'
import { useReportQuery } from '../ReportWorkbench/useReportQuery'
import {
  batchOffset, buildDailyPoints, buildMonthlyPoints, drawerWindow, monthDay, normalizeBranchKey, purchaseOnlyStores,
  summarizeStorePurchases, weekdayOf, type StorePurchase,
} from './drawerLogic'
import { productBranchDrawerQuery } from './logic'
import ProductTrendChart, { type TrendGranularity } from './ProductTrendChart'
import {
  fetchProductPurchases, fetchProductTrend, fetchSalesDetailReport, normalizeSalesDetailRow,
  type ProductPurchases, type ProductTrend, type SalesDetailQuery, type SalesDetailRow,
} from './reportService'
import styles from './styles.module.css'

interface ProductBranchDrawerProps {
  open: boolean
  product: SalesDetailRow | null
  baseQuery: SalesDetailQuery
  onClose: () => void
}

const integer = new Intl.NumberFormat('en-AU', { maximumFractionDigits: 0 })
const WEEKDAYS_ZH = ['周日', '周一', '周二', '周三', '周四', '周五', '周六']
const WEEKDAYS_EN = ['Sun', 'Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat']

function GrowthCell({ current, previous, compare }: { current: number; previous: number | null; compare: boolean }) {
  const text = useReportText()
  const value = compare ? growth(current, previous) : null
  return <span className={typeof value === 'number' ? value > 0 ? styles.positive : value < 0 ? styles.negative : styles.muted : styles.muted}>
    {value === null ? '—' : value === 'new' ? text('新增', 'New') : `${value > 0 ? '+' : ''}${(value * 100).toFixed(1)}%`}
  </span>
}

function Chevron({ open }: { open: boolean }) {
  return <svg className={styles.buyChevron} viewBox="0 0 10 10" aria-hidden="true">
    <path d={open ? 'M1.5 6.5 5 3l3.5 3.5' : 'M1.5 3.5 5 7l3.5-3.5'} fill="none" stroke="currentColor" strokeWidth="1.6" strokeLinecap="round" strokeLinejoin="round" />
  </svg>
}

interface DrawerRow { row: SalesDetailRow; purchaseOnly: boolean }

/**
 * 商品反查：销售曲线（日 / 月）、进货明细与分店表现各自独立请求，关闭或切换商品时由 useReportQuery 取消在途请求。
 * 进货窗口 = 销售开始日前 60 天 ~ 销售结束日，曲线与进货列共用；点分店行可让曲线只看该店。
 */
export default function ProductBranchDrawer({ open, product, baseQuery, onClose }: ProductBranchDrawerProps) {
  const text = useReportText()
  const [refresh, setRefresh] = useState(0)
  const [trendRefresh, setTrendRefresh] = useState(0)
  const [purchaseRefresh, setPurchaseRefresh] = useState(0)
  const [granularity, setGranularity] = useState<TrendGranularity>('day')
  const [branchFilter, setBranchFilter] = useState<string | null>(null)
  const [expanded, setExpanded] = useState<Set<string>>(() => new Set())
  // 换商品或重新打开：回到默认「日」、取消单店筛选与展开。
  useEffect(() => { setGranularity('day'); setBranchFilter(null); setExpanded(new Set()) }, [product?.code, open])

  const drawerQuery = useMemo(() => product ? productBranchDrawerQuery(baseQuery, product.code) : baseQuery,
    [baseQuery, product])
  const queryKey = JSON.stringify([drawerQuery, product?.code])
  const branches = useReportQuery(
    `sales-detail:product-branches:${queryKey}`,
    signal => fetchSalesDetailReport(drawerQuery, signal, ['branches']),
    { active: open, enabled: open && !!product, refresh, metricId: 'sales-detail-product-branches' },
  )
  const rangeKey = JSON.stringify([baseQuery.startDate, baseQuery.endDate, baseQuery.branchCodes, product?.code])
  const filterKey = normalizeBranchKey(branchFilter)
  const trend = useReportQuery<ProductTrend>(
    `sales-detail:product-trend:${rangeKey}:${filterKey}`,
    signal => fetchProductTrend(baseQuery, product!.code, signal, branchFilter ?? undefined),
    { active: open, enabled: open && !!product, refresh: trendRefresh, metricId: 'sales-detail-product-trend' },
  )
  const purchases = useReportQuery<ProductPurchases>(
    `sales-detail:product-purchases:${rangeKey}`,
    signal => fetchProductPurchases(baseQuery, product!.code, signal),
    { active: open, enabled: open && !!product, refresh: purchaseRefresh, metricId: 'sales-detail-product-purchases' },
  )

  const page = branches.data?.branches
  const compare = !!baseQuery.compareStartDate
  const period = baseQuery.compareStartDate && baseQuery.compareEndDate
    ? `${text('同期', 'Previous')} ${baseQuery.compareStartDate} — ${baseQuery.compareEndDate}`
    : undefined
  const window = useMemo(() => drawerWindow(baseQuery.startDate, baseQuery.endDate), [baseQuery.startDate, baseQuery.endDate])
  const batches = purchases.data?.batches
  const points = useMemo(() => buildDailyPoints(window, trend.data?.daily ?? [], batches ?? [], filterKey || null), [window, trend.data, batches, filterKey])
  const months = useMemo(() => buildMonthlyPoints(points, baseQuery.startDate, baseQuery.endDate), [points, baseQuery.startDate, baseQuery.endDate])
  const summaries = useMemo(() => summarizeStorePurchases(batches ?? [], baseQuery.startDate, baseQuery.endDate), [batches, baseQuery.startDate, baseQuery.endDate])
  const emptySales = !!trend.data && points.every(point => point.sales === 0)

  const baseRows = useMemo(() => page?.rows ?? [], [page])
  // 窗口内有进货、销售期与同期都没有销量的分店：追加在表格末尾。
  const extraStores = useMemo(() => page && purchases.data ? purchaseOnlyStores(baseRows.map(row => row.code), purchases.data.stores, summaries) : [], [page, purchases.data, baseRows, summaries])
  const rows = useMemo<DrawerRow[]>(() => [
    ...baseRows.map(row => ({ row, purchaseOnly: false })),
    ...extraStores.map(store => ({
      row: normalizeSalesDetailRow({ code: store.code, name: store.name, revenue: 0, quantity: 0, compareRevenue: compare ? 0 : null, compareQuantity: compare ? 0 : null }),
      purchaseOnly: true,
    })),
  ], [baseRows, extraStores, compare])
  const filterName = useMemo(() => {
    if (!filterKey) return ''
    const row = rows.find(item => normalizeBranchKey(item.row.code) === filterKey)?.row
    return row?.name || row?.code || branchFilter || ''
  }, [filterKey, rows, branchFilter])

  const selectBranch = (code: string) => setBranchFilter(current => normalizeBranchKey(current) === normalizeBranchKey(code) ? null : code.trim())
  const toggleExpanded = (key: string) => setExpanded(current => {
    const next = new Set(current)
    if (!next.delete(key)) next.add(key)
    return next
  })
  const weekday = (date: string) => text(WEEKDAYS_ZH[weekdayOf(date)], WEEKDAYS_EN[weekdayOf(date)])
  const sourceName = (source: string) => source === 'supplier' ? text('供应商进货单', 'Supplier invoice') : text('总仓配货', 'Warehouse')
  const windowLabel = `${monthDay(window.start)} ~ ${monthDay(window.end)}`

  const purchaseCell = (summary: StorePurchase | undefined) => {
    if (!summary) {
      if (purchases.loading) return <span className={styles.buySkeleton} aria-label={text('进货加载中', 'Loading purchases')}><i /><i /></span>
      return <span className={styles.muted}>—</span>
    }
    const isOpen = expanded.has(summary.key)
    return <Tooltip title={`${text('总仓配货', 'Warehouse')} ${integer.format(summary.warehouse)} · ${text('供应商进货单', 'Supplier')} ${integer.format(summary.supplier)}`}>
      <button type="button" className={styles.buyButton} aria-expanded={isOpen} aria-controls={`purchase-detail-${summary.key}`}
        onClick={event => { event.stopPropagation(); toggleExpanded(summary.key) }} onKeyDown={event => event.stopPropagation()}>
        <span className={styles.buyTop}>{integer.format(summary.quantity)}<Chevron open={isOpen} /></span>
        <span className={styles.buySub}>{text('最近', 'Latest')} <em className={summary.latestInSalesPeriod ? styles.buyHot : undefined}>{monthDay(summary.latestDate)}</em> · {summary.batchCount} {text('批', summary.batchCount === 1 ? 'batch' : 'batches')}</span>
      </button>
    </Tooltip>
  }

  return <Drawer
    className={styles.productDrawer}
    open={open}
    placement="right"
    width="min(880px, 100vw)"
    destroyOnHidden
    onClose={onClose}
    title={product && <div className={styles.drawerTitle}>
      <strong>{product.name || product.code}</strong>
      <span>{text('货号', 'Item')} {product.itemNumber || product.code}</span>
      <small>{baseQuery.startDate} — {baseQuery.endDate}{period ? ` · ${period}` : ''}</small>
      <small className={styles.drawerWindow}><i />{text('进货窗口', 'Purchase window')} {window.start} — {window.end}（{text(`销售开始日前 60 天起`, '60 days before the sales start')}）</small>
    </div>}
  >
    {!product ? null : <div className={styles.drawerBody} data-testid="product-branch-drawer">
      <section className={styles.trendCard} aria-label={text('销售曲线', 'Sales curve')}>
        <div className={styles.trendHead}>
          <h3>{text('销售曲线', 'Sales curve')}
            {filterName
              ? <span className={styles.trendChip}>{filterName}<button type="button" aria-label={text('取消只看这家店', 'Show all stores again')} onClick={() => setBranchFilter(null)}>✕</button></span>
              : <small>{text('全部分店合计', 'All stores')} · {window.start} ~ {window.end}</small>}
          </h3>
          <Segmented size="small" value={granularity} onChange={value => setGranularity(value as TrendGranularity)}
            options={[{ value: 'day', label: text('日', 'Day') }, { value: 'month', label: text('月', 'Month') }]} aria-label={text('曲线粒度', 'Curve granularity')} />
        </div>
        <div className={styles.trendLegend}>
          <span><i className={styles.legendLine} />{text(filterName ? '销量（该店，件）' : '销量（全部分店合计，件）', filterName ? 'Sold (this store, pcs)' : 'Sold (all stores, pcs)')}</span>
          <span><i className={styles.legendWarehouse} />{text('总仓配货', 'Warehouse')}</span>
          <span><i className={styles.legendSupplier} />{text('供应商进货单', 'Supplier invoice')}</span>
          {granularity === 'day' && <span><i className={styles.legendBand} />{text(`本期销售期 ${monthDay(baseQuery.startDate)} ~ ${monthDay(baseQuery.endDate)}`, `Sales period ${monthDay(baseQuery.startDate)} ~ ${monthDay(baseQuery.endDate)}`)}</span>}
          {purchases.loading && <span className={styles.muted}>{text('进货加载中…', 'Loading purchases…')}</span>}
          {purchases.error && <span className={styles.trendError}>{text('进货加载失败', 'Purchases failed to load')} <Button type="link" size="small" onClick={() => setPurchaseRefresh(value => value + 1)}>{text('重试', 'Retry')}</Button></span>}
        </div>
        {trend.error ? <div className={styles.trendState}>
          <Alert type="warning" showIcon message={`${text('曲线加载失败', 'Curve failed to load')}：${trend.error}`} />
          <Button size="small" onClick={() => setTrendRefresh(value => value + 1)}>{text('重试', 'Retry')}</Button>
        </div> : trend.loading || !trend.data ? <div className={styles.trendSkeleton} aria-busy="true"><Skeleton.Node active style={{ width: '100%', height: 150 }}>{' '}</Skeleton.Node><Skeleton.Node active style={{ width: '70%', height: 60 }}>{' '}</Skeleton.Node></div>
          : <ProductTrendChart points={points} months={months} granularity={granularity} salesStart={baseQuery.startDate} salesEnd={baseQuery.endDate} emptySales={emptySales} text={text} />}
      </section>
      <div className={styles.drawerIntro}>
        <span>{text('商品在各分店的表现', 'Product performance by store')}</span>
        {page && <small>{page.total + extraStores.length} {text('家分店', 'stores')}</small>}
      </div>
      <div className={styles.drawerLegend}>
        <span>{period ? text('销售列：本期在上 · 同期在下', 'Sales: current above · previous below') : text('销售列：当前期间', 'Sales: current period')} · AUD</span>
        <span><b>{text('进货列', 'Purchase')}</b>{text('：窗口内进货量在上 · 最近进货日与批数在下（点击展开明细）', ': window total above · latest date and batches below (click to expand)')}</span>
      </div>
      {branches.error ? <div className={styles.drawerEmpty}>
        <Alert type="warning" message={branches.error} />
        <Button onClick={() => setRefresh(value => value + 1)}>{text('重试', 'Retry')}</Button>
      </div> : branches.loading ? <div className={styles.drawerSkeleton}>
        <Skeleton active paragraph={{ rows: 8 }} title={false} />
      </div> : !page || rows.length === 0 ? <div className={styles.drawerEmpty}>
        {text('当前条件下没有分店数据', 'No store data for these filters')}
      </div> : <div className={styles.drawerScroll} tabIndex={0} aria-label={text('商品分店数据可滚动表格', 'Product store data scrollable table')}>
        <table className={styles.drawerTable}>
          <thead><tr>
            <th>{text('分店名称 / 编码', 'Store / Code')}</th>
            <th>{text('营业额', 'Revenue')}</th>
            <th>{text('商品数量', 'Product quantity')}</th>
            <th className={styles.buyHead}>{text('进货量', 'Purchased')}<small>{windowLabel}</small></th>
            <th>{text('均价', 'Average price')}</th>
            <th>{text('同比', 'Growth')}</th>
            <th>{text('毛利额', 'Gross profit')}</th>
            <th>{text('毛利率', 'Margin')}</th>
          </tr></thead>
          <tbody>{rows.map(({ row, purchaseOnly }) => {
            const key = normalizeBranchKey(row.code)
            const summary = summaries.get(key)
            const selected = key === filterKey
            const isOpen = !!summary && expanded.has(key)
            return <Fragment key={`${purchaseOnly ? 'p' : 'r'}-${row.code}`}>
              <tr className={[selected ? styles.drawerRowActive : '', isOpen ? styles.drawerRowOpen : ''].filter(Boolean).join(' ')}
                aria-selected={selected} tabIndex={0} onClick={() => selectBranch(row.code)}
                onKeyDown={event => { if (event.key === 'Enter' || event.key === ' ') { event.preventDefault(); selectBranch(row.code) } }}
                title={selected ? text('再点一次取消只看这家店', 'Click again to show all stores') : text('点击只看这家店的销售曲线', 'Click to show this store only')}>
                <td><strong>{row.name || row.code}{purchaseOnly && <span className={styles.onlyTag}>{text('仅进货', 'Purchases only')}</span>}</strong><small>{row.code}</small></td>
                <td><MetricPair current={row.revenue} previous={row.compareRevenue} compare={compare} /></td>
                <td><MetricPair current={row.quantity} previous={row.compareQuantity} compare={compare} format="integer" /></td>
                <td className={styles.buyCell}>{purchaseCell(summary)}</td>
                <td><MetricPair current={row.averageUnitPrice} previous={row.compareAverageUnitPrice} compare={compare} /></td>
                <td><GrowthCell current={row.revenue} previous={row.compareRevenue} compare={compare} /></td>
                <td><MetricPair current={row.grossProfit} previous={row.compareGrossProfit} compare={compare} costMetric revenue={row.revenue} compareRevenue={row.compareRevenue} /></td>
                <td><MetricPair current={row.grossMarginRate} previous={row.compareGrossMarginRate} compare={compare} format="rate" costMetric revenue={row.revenue} compareRevenue={row.compareRevenue} /></td>
              </tr>
              {isOpen && summary && <tr className={styles.drawerDetailRow}><td colSpan={8}>
                <div className={styles.batchBox} id={`purchase-detail-${key}`}>
                  <div className={styles.batchHead}>
                    <span>{row.name || row.code} · {text('进货明细', 'Purchase detail')}（{summary.batchCount} {text('批', 'batches')}，{text('共', 'total')} {integer.format(summary.quantity)} {text('件', 'pcs')}）</span>
                    <small>{text('总仓配货', 'Warehouse')} {integer.format(summary.warehouse)} + {text('供应商进货单', 'Supplier')} {integer.format(summary.supplier)}</small>
                  </div>
                  <table className={styles.batchTable}>
                    <thead><tr><th>{text('进货日期', 'Date')}</th><th>{text('来源', 'Source')}</th><th>{text('数量', 'Qty')}</th><th>{text('单号', 'Document')}</th><th /></tr></thead>
                    <tbody>{summary.batches.map((batch, index) => {
                      const offset = batchOffset(batch.date, baseQuery.startDate, baseQuery.endDate)
                      return <tr key={`${batch.date}-${batch.source}-${batch.documentNo}-${index}`}>
                        <td>{batch.date} {weekday(batch.date)}</td>
                        <td><span className={batch.source === 'supplier' ? styles.chipSupplier : styles.chipWarehouse}>{sourceName(batch.source)}</span></td>
                        <td>{integer.format(batch.quantity)}</td>
                        <td className={styles.batchNo}>{batch.documentNo || '—'}</td>
                        <td><span className={offset.inPeriod ? styles.tagIn : styles.tagBefore}>{offset.inPeriod ? text('销售期内', 'In sales period') : text(`销售前 ${offset.daysBefore} 天`, `${offset.daysBefore}d before sales`)}</span></td>
                      </tr>
                    })}</tbody>
                  </table>
                </div>
              </td></tr>}
            </Fragment>
          })}</tbody>
        </table>
      </div>}
    </div>}
  </Drawer>
}
