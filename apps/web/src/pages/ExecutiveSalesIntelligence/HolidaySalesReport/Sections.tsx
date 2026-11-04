import { Drawer } from 'antd'
import { useEffect, useMemo, useRef, useState, type FC, type MouseEvent } from 'react'
import type {
  HolidayBenchmark,
  HolidayCategory,
  HolidayCategoryMap,
  HolidayProduct,
  HolidaySalesReport,
  HolidayStore,
} from '../../../services/holidaySalesReportService'
import {
  BENCHMARK_LABEL_KEY,
  benchmarkMetrics,
  buildChart,
  buildInsights,
  CATEGORY_COLOR,
  CATEGORY_LABEL_KEY,
  categoryPointDiff,
  compareToAverage,
  formatCount,
  formatMetric,
  formatMoney,
  formatMonthDay,
  formatPercent,
  formatSellThrough,
  hasNoPurchase,
  INSIGHT_KEY,
  nearestPoint,
  sellThroughTone,
  supplierShares,
  TONE_COLOR,
  VERDICT_KEY,
} from './logic'
import styles from './styles.module.css'

export type Tr = (key: string, params?: Record<string, string | number>) => string

const SUPPLIER_TAG: Record<string, string> = { '257': 'KFC', '243': 'Brazco' }

function supplierTag(code: string): { label: string; className: string } {
  return code === '243' ? { label: SUPPLIER_TAG[code], className: styles.tagBrazco } : { label: SUPPLIER_TAG[code] ?? code, className: styles.tagKfc }
}

// ---------- 顶部 6 个指标 ----------

export const KpiRow: FC<{ report: HolidaySalesReport; single: boolean; tr: Tr; allStoresSellThrough: number | null }> = ({
  report,
  single,
  tr,
  allStoresSellThrough,
}) => {
  const t = report.totals
  const benchmark = report.benchmark
  const amountSub = single && benchmark
    ? tr('kpi.amountStore', { rank: benchmark.rank, total: benchmark.storeCount })
    : tr('kpi.amountAll', { count: report.stores.length })
  const sellThroughSub = single
    ? tr('kpi.sellThroughStore', { value: formatSellThrough(allStoresSellThrough) })
    : tr('kpi.sellThroughAll', { qty: formatCount(t.purchaseQuantity), cost: formatMoney(t.purchaseCost) })
  const earlySub = single ? tr('kpi.earlyStore') : tr('kpi.earlyAll', { count: formatCount(t.earlySoldOutCount) })
  return (
    <section className={styles.kpis} aria-label={tr('title')}>
      <div className={styles.kpi}>
        <span className={styles.kpiLabel}>{tr('kpi.amount')}</span>
        <strong className={styles.kpiValue}>{formatMoney(t.amount)}</strong>
        <span className={styles.kpiSub}>{amountSub}</span>
      </div>
      <div className={styles.kpi}>
        <span className={styles.kpiLabel}>{tr('kpi.quantity')}</span>
        <strong className={styles.kpiValue}>{formatCount(t.quantity)}</strong>
        <span className={styles.kpiSub}>{tr('kpi.quantitySub', { count: formatCount(t.skuCount) })}</span>
      </div>
      <div className={styles.kpi}>
        <span className={styles.kpiLabel}>{tr('kpi.sellThrough')}</span>
        <strong className={styles.kpiValue} style={{ color: TONE_COLOR[sellThroughTone(t.sellThrough)] }}>
          {formatSellThrough(t.sellThrough)}
        </strong>
        <span className={styles.kpiSub}>{sellThroughSub}</span>
      </div>
      <div className={`${styles.kpi} ${styles.kpiAlert}`}>
        <span className={styles.kpiLabel}>{tr('kpi.early')}</span>
        <strong className={styles.kpiValue}>{formatCount(single ? t.earlySoldOutCount : t.earlySoldOutProductCount)}</strong>
        <span className={styles.kpiSub}>{earlySub}</span>
      </div>
      <div className={`${styles.kpi} ${styles.kpiAlert}`}>
        <span className={styles.kpiLabel}>{tr('kpi.lost')}</span>
        <strong className={styles.kpiValue}>{formatMoney(t.estimatedLostAmount)}</strong>
        <span className={styles.kpiSub}>{tr('kpi.lostSub')}</span>
      </div>
      <div className={`${styles.kpi} ${styles.kpiWarn}`}>
        <span className={styles.kpiLabel}>{tr('kpi.left')}</span>
        <strong className={styles.kpiValue}>{tr('kpi.leftValue', { qty: formatCount(t.leftoverQuantity) })}</strong>
        <span className={styles.kpiSub}>{tr('kpi.leftSub', { cost: formatMoney(t.leftoverCost) })}</span>
      </div>
    </section>
  )
}

// ---------- 每日销售曲线 ----------

export const DailyChart: FC<{ report: HolidaySalesReport; tr: Tr }> = ({ report, tr }) => {
  const chart = useMemo(() => buildChart(report.daily, report.holidayDate), [report.daily, report.holidayDate])
  const [hover, setHover] = useState<{ x: number; y: number; date: string; amount: number } | null>(null)
  const wrapRef = useRef<HTMLDivElement>(null)

  // 窄屏下曲线可横向滚动，节日在最右端，默认停在右端而不是 8 月初的空白段。
  useEffect(() => {
    const wrap = wrapRef.current
    if (wrap) wrap.scrollLeft = wrap.scrollWidth
  }, [chart])

  const onMove = (event: MouseEvent<SVGSVGElement>) => {
    const box = event.currentTarget.getBoundingClientRect()
    if (box.width <= 0) return
    setHover(nearestPoint(chart, ((event.clientX - box.left) * chart.width) / box.width))
  }

  return (
    <section className={styles.card}>
      <h2 className={styles.cardTitle}>
        {tr('chart.title')}
        <span className={styles.hint}>{tr('chart.hint')}</span>
      </h2>
      <div className={styles.chartWrap} ref={wrapRef}>
      <svg
        className={styles.chart}
        viewBox={`0 0 ${chart.width} ${chart.height}`}
        role="img"
        aria-label={tr('chart.aria', { from: formatMonthDay(report.salesFrom), through: formatMonthDay(report.salesThrough) })}
        onMouseMove={onMove}
        onMouseLeave={() => setHover(null)}
      >
        {chart.yTicks.map((tick) => (
          <g key={tick.value}>
            <line x1={52} x2={chart.width - 12} y1={tick.y} y2={tick.y} className={styles.gridLine} />
            <text x={46} y={tick.y + 4} textAnchor="end" className={styles.axisText}>
              {tick.value >= 1000 ? `$${tick.value / 1000}k` : `$${tick.value}`}
            </text>
          </g>
        ))}
        {chart.weekBand && <rect x={chart.weekBand.x} y={14} width={chart.weekBand.width} height={chart.height - 40} className={styles.weekBand} />}
        {chart.area && <polygon points={chart.area} className={styles.chartArea} />}
        {chart.line && <polyline points={chart.line} className={styles.chartLine} />}
        {chart.holiday && (
          <g>
            <line x1={chart.holiday.x} x2={chart.holiday.x} y1={14} y2={chart.height - 26} className={styles.holidayLine} />
            <circle cx={chart.holiday.x} cy={chart.holiday.y} r={4} className={styles.holidayDot} />
            <text
              x={(chart.weekBand?.x ?? chart.holiday.x) - 8}
              y={Math.max(chart.holiday.y + 10, 46)}
              textAnchor="end"
              className={styles.holidayText}
            >
              {tr('chart.holiday', { date: formatMonthDay(report.holidayDate), amount: formatMoney(chart.holiday.amount) })}
            </text>
            {chart.weekBand && (
              <text x={chart.weekBand.x - 4} y={26} textAnchor="end" className={styles.weekText}>
                {tr('chart.lastWeek')} ▸
              </text>
            )}
          </g>
        )}
        {chart.xTicks.map((tick) => (
          <text key={tick.label + tick.x} x={tick.x} y={chart.height - 8} textAnchor="middle" className={styles.axisText}>
            {tick.label}
          </text>
        ))}
        {hover && (
          <g pointerEvents="none">
            <line x1={hover.x} x2={hover.x} y1={14} y2={chart.height - 26} className={styles.hoverLine} />
            <circle cx={hover.x} cy={hover.y} r={3.5} className={styles.hoverDot} />
          </g>
        )}
      </svg>
      </div>
      <div className={styles.chartReadout} aria-live="polite">
        {hover ? tr('chart.hover', { date: formatMonthDay(hover.date), amount: formatMoney(hover.amount) }) : ' '}
      </div>
    </section>
  )
}

// ---------- 供应商构成 + 关键结论 ----------

export const InsightCard: FC<{ report: HolidaySalesReport; tr: Tr }> = ({ report, tr }) => {
  const shares = supplierShares(report)
  const insights = useMemo(
    () => buildInsights(report, (key) => tr(CATEGORY_LABEL_KEY[key])),
    // tr 随语言变化重建，report 随查询变化
    [report, tr],
  )
  return (
    <section className={styles.card}>
      <h2 className={styles.cardTitle}>{tr('supplier.title')}</h2>
      <div className={styles.shareBar} aria-hidden="true">
        {shares.map((share) => (
          <b key={share.code} style={{ width: `${share.share * 100}%` }} className={share.code === '243' ? styles.shareSecond : styles.shareFirst} />
        ))}
      </div>
      <div className={styles.shareLegend}>
        {shares.map((share) => {
          const tag = supplierTag(share.code)
          return (
            <span key={share.code}>
              <i className={`${styles.tag} ${tag.className}`}>{tag.label}</i> {formatMoney(share.amount)} · {formatPercent(share.share)}
            </span>
          )
        })}
      </div>
      <h2 className={styles.cardTitle}>
        {tr('insights.title')}
        <span className={styles.hint}>{tr('insights.auto')}</span>
      </h2>
      <ul className={styles.insights}>
        {insights.map((insight) => (
          <li key={insight.id}>
            <i style={{ background: insight.color }} aria-hidden="true" />
            <span>{tr(INSIGHT_KEY[insight.id], insight.params)}</span>
          </li>
        ))}
      </ul>
    </section>
  )
}

// ---------- 大类表 ----------

export const CategoryTable: FC<{ report: HolidaySalesReport; single: boolean; tr: Tr; benchmark: HolidayBenchmark | null; onOpenMap: () => void }> = ({
  report,
  single,
  tr,
  benchmark,
  onOpenMap,
}) => {
  const max = report.categories.reduce((current, category) => Math.max(current, category.amount), 0)
  return (
    <section className={styles.card}>
      <h2 className={styles.cardTitle}>
        {tr('cat.title')}
        <span className={styles.hint}>{tr('cat.hint')}</span>
        <button type="button" className={styles.linkButton} onClick={onOpenMap}>
          {tr('actions.categoryMap')} ›
        </button>
      </h2>
      <div className={styles.tableScroll}>
        <table className={styles.table}>
          <thead>
            <tr>
              <th>{tr('cat.colName')}</th>
              <th className={styles.wide}>{tr('cat.colAmount')}</th>
              <th className={styles.num}>{tr('cat.colShare')}</th>
              <th className={styles.num}>{tr('cat.colQuantity')}</th>
              <th className={styles.num}>{tr('cat.colSkus')}</th>
              <th className={styles.wide}>{tr('cat.colSellThrough')}</th>
              <th className={styles.num}>{tr('cat.colLost')}</th>
              <th className={styles.num}>{tr('cat.colLeft')}</th>
              {single && <th className={styles.num}>{tr('cat.colVsAll')}</th>}
            </tr>
          </thead>
          <tbody>
            {report.categories.map((category) => (
              <CategoryRow key={category.key} category={category} report={report} max={max} single={single} tr={tr} benchmark={benchmark} />
            ))}
          </tbody>
        </table>
      </div>
    </section>
  )
}

const CategoryRow: FC<{
  category: HolidayCategory
  report: HolidaySalesReport
  max: number
  single: boolean
  tr: Tr
  benchmark: HolidayBenchmark | null
}> = ({ category, report, max, single, tr, benchmark }) => {
  const tone = sellThroughTone(category.sellThrough)
  const diff = single ? categoryPointDiff(category, benchmark) : null
  return (
    <tr>
      <td>
        <i className={styles.swatch} style={{ background: CATEGORY_COLOR[category.key] }} aria-hidden="true" />
        {tr(CATEGORY_LABEL_KEY[category.key])}
      </td>
      <td>
        <div className={styles.barCell}>
          <div className={styles.bar}>
            <b style={{ width: `${max > 0 ? (category.amount / max) * 100 : 0}%`, background: CATEGORY_COLOR[category.key] }} />
          </div>
          <span className={styles.barValue}>{formatMoney(category.amount)}</span>
        </div>
      </td>
      <td className={styles.num}>{report.totals.amount > 0 ? formatPercent(category.amount / report.totals.amount) : '—'}</td>
      <td className={styles.num}>{formatCount(category.quantity)}</td>
      <td className={styles.num}>{formatCount(category.skuCount)}</td>
      <td>
        <div className={styles.barCell}>
          <div className={styles.bar}>
            <b style={{ width: `${Math.min(category.sellThrough ?? 0, 1) * 100}%`, background: TONE_COLOR[tone] }} />
          </div>
          <span className={styles.barValueSm}>{formatSellThrough(category.sellThrough)}</span>
        </div>
      </td>
      <td className={`${styles.num} ${category.estimatedLostAmount > 500 ? styles.bad : ''}`}>{formatMoney(category.estimatedLostAmount)}</td>
      <td className={`${styles.num} ${category.leftoverCost > 3000 ? styles.warnText : ''}`}>{formatMoney(category.leftoverCost)}</td>
      {single && (
        <td className={styles.num}>
          {diff === null ? '—' : diff >= 0 ? <span className={`${styles.pill} ${styles.pillGood}`}>{tr('cat.higher', { points: diff })}</span> : <span className={`${styles.pill} ${styles.pillBad}`}>{tr('cat.lower', { points: -diff })}</span>}
        </td>
      )}
    </tr>
  )
}

// ---------- 商品四张榜 ----------

const ProductImage: FC<{ src: string | null; label: string }> = ({ src, label }) => {
  const [failed, setFailed] = useState(false)
  if (!src || failed) return <div className={`${styles.thumb} ${styles.thumbEmpty}`}>{label}</div>
  return <img className={styles.thumb} src={src} alt="" loading="lazy" onError={() => setFailed(true)} />
}

type BoardKind = 'best' | 'early' | 'slow' | 'over'

const ProductLine: FC<{ product: HolidayProduct; index: number; kind: BoardKind; single: boolean; tr: Tr }> = ({ product, index, kind, single, tr }) => {
  const tag = supplierTag(product.supplierCode)
  const rate = formatSellThrough(product.sellThrough)
  let head: string
  let lines: string[]
  let headClass = styles.valueHead
  if (kind === 'best') {
    head = formatMoney(product.amount)
    lines = [
      tr('product.soldRate', { qty: formatCount(product.quantity), rate }),
      [single ? '' : tr('product.stores', { count: product.storeCount }), product.soldOutStoreCount > 0 ? (single ? tr('product.soldOutSelf') : tr('product.soldOutStores', { count: product.soldOutStoreCount })) : '']
        .filter(Boolean)
        .join(' · '),
    ]
  } else if (kind === 'early') {
    head = tr('product.earlyDays', { days: product.daysEarly ?? 0 })
    headClass = `${styles.valueHead} ${styles.bad}`
    lines = [
      single && product.soldOutDate
        ? tr('product.earlyStore', { date: formatMonthDay(product.soldOutDate), qty: formatCount(product.purchaseQuantity) })
        : tr('product.earlyAll', { out: product.soldOutStoreCount, stocked: product.storeCount }),
      tr('product.lost', { amount: formatMoney(product.estimatedLostAmount) }),
    ]
  } else if (kind === 'slow') {
    head = tr('product.slowHead', { rate })
    headClass = `${styles.valueHead} ${styles.bad}`
    lines = [
      tr('product.slowBought', { bought: formatCount(product.purchaseQuantity), sold: formatCount(product.quantity) }),
      tr('product.slowLeft', { qty: formatCount(product.leftoverQuantity), cost: formatMoney(product.leftoverCost) }),
    ]
  } else {
    head = tr('product.overHead', { qty: formatCount(product.leftoverQuantity) })
    headClass = `${styles.valueHead} ${styles.warnText}`
    lines = [
      tr('product.overCost', { cost: formatMoney(product.leftoverCost), rate }),
      tr('product.overSold', { qty: formatCount(product.quantity), amount: formatMoney(product.amount) }),
    ]
  }
  return (
    <li className={styles.product}>
      <span className={styles.rank}>{index + 1}</span>
      <ProductImage src={product.imageUrl} label={tr('product.noImage')} />
      <div className={styles.productMain}>
        <div className={styles.productName} title={product.productName}>{product.productName}</div>
        <div className={styles.productMeta}>
          <span className={styles.catTag} style={{ color: CATEGORY_COLOR[product.categoryKey], borderColor: `${CATEGORY_COLOR[product.categoryKey]}66` }}>
            {tr(CATEGORY_LABEL_KEY[product.categoryKey])}
          </span>
          <i className={`${styles.tag} ${tag.className}`}>{tag.label}</i>
          <span>{product.itemNumber}</span>
        </div>
      </div>
      <div className={styles.productValue}>
        <div className={headClass}>{head}</div>
        {lines.filter(Boolean).map((line) => (
          <div key={line} className={styles.valueSub}>{line}</div>
        ))}
      </div>
    </li>
  )
}

const Board: FC<{
  kind: BoardKind
  icon: string
  iconClass: string
  title: string
  desc: string
  products: HolidayProduct[]
  single: boolean
  tr: Tr
}> = ({ kind, icon, iconClass, title, desc, products, single, tr }) => {
  const [expanded, setExpanded] = useState(false)
  const shown = expanded ? products : products.slice(0, 5)
  return (
    <section className={styles.card}>
      <div className={styles.boardHead}>
        <span className={`${styles.boardIcon} ${iconClass}`} aria-hidden="true">{icon}</span>
        <span className={styles.boardTitle}>{title}</span>
        <span className={styles.hint}>{desc}</span>
      </div>
      {shown.length === 0 ? (
        <div className={styles.empty}>{tr('board.empty')}</div>
      ) : (
        <ol className={styles.productList}>
          {shown.map((product, index) => (
            <ProductLine key={product.productCode} product={product} index={index} kind={kind} single={single} tr={tr} />
          ))}
        </ol>
      )}
      {products.length > 5 && (
        <button type="button" className={styles.moreButton} onClick={() => setExpanded((current) => !current)}>
          {expanded ? tr('board.less') : `${tr('board.more')} (${products.length - 5})`}
        </button>
      )}
    </section>
  )
}

export const ProductBoards: FC<{ report: HolidaySalesReport; single: boolean; tr: Tr }> = ({ report, single, tr }) => (
  <div className={styles.quad}>
    <Board kind="best" icon="↑" iconClass={styles.iconGood} title={tr('board.best.title')} desc={tr('board.best.desc')} products={report.bestSellers} single={single} tr={tr} />
    <Board
      kind="early"
      icon="!"
      iconClass={styles.iconBad}
      title={tr('board.early.title')}
      desc={single ? tr('board.early.descStore') : tr('board.early.descAll')}
      products={report.earlySoldOut}
      single={single}
      tr={tr}
    />
    <Board kind="slow" icon="↓" iconClass={styles.iconGray} title={tr('board.slow.title')} desc={tr('board.slow.desc')} products={report.slowSellers} single={single} tr={tr} />
    <Board kind="over" icon="▣" iconClass={styles.iconWarn} title={tr('board.over.title')} desc={tr('board.over.desc')} products={report.overstock} single={single} tr={tr} />
  </div>
)

// ---------- 分店排行（合计视图） ----------

export const StoreTable: FC<{ report: HolidaySalesReport; tr: Tr; onOpen: (branchCode: string) => void }> = ({ report, tr, onOpen }) => {
  const max = report.stores.reduce((current, store) => Math.max(current, store.amount), 0)
  const withoutSales = report.storesWithoutSales
  return (
    <section className={styles.card}>
      <h2 className={styles.cardTitle}>
        {tr('stores.title')}
        <span className={styles.hint}>{tr('stores.hint')}</span>
      </h2>
      <div className={styles.tableScroll}>
        <table className={styles.table}>
          <thead>
            <tr>
              <th>#</th>
              <th>{tr('stores.colStore')}</th>
              <th className={styles.wide}>{tr('stores.colAmount')}</th>
              <th className={styles.num}>{tr('stores.colQuantity')}</th>
              <th className={styles.num}>{tr('stores.colPurchase')}</th>
              <th className={styles.wide}>{tr('stores.colSellThrough')}</th>
              <th className={styles.num}>{tr('stores.colEarly')}</th>
              <th className={styles.num}>{tr('stores.colLost')}</th>
              <th className={styles.num}>{tr('stores.colLeft')}</th>
              <th className={styles.num}>{tr('stores.colAfter')}</th>
              <th />
            </tr>
          </thead>
          <tbody>
            {report.stores.map((store, index) => (
              <StoreRow key={store.branchCode} store={store} index={index} max={max} tr={tr} onOpen={onOpen} />
            ))}
          </tbody>
        </table>
      </div>
      <p className={styles.note}>
        {tr('stores.noPurchaseNote')}
        {withoutSales.length > 0 && (
          <>
            {' '}
            {tr('stores.withoutSales', {
              names: withoutSales.map((store) => `${store.branchCode} ${store.branchName} (${formatCount(store.purchaseQuantity)})`).join(', '),
            })}
          </>
        )}
      </p>
    </section>
  )
}

const StoreRow: FC<{ store: HolidayStore; index: number; max: number; tr: Tr; onOpen: (branchCode: string) => void }> = ({ store, index, max, tr, onOpen }) => {
  const tone = sellThroughTone(store.sellThrough)
  const open = () => onOpen(store.branchCode)
  return (
    <tr
      className={styles.clickRow}
      tabIndex={0}
      onClick={open}
      onKeyDown={(event) => {
        if (event.key === 'Enter' || event.key === ' ') {
          event.preventDefault()
          open()
        }
      }}
    >
      <td className={styles.rank}>{index + 1}</td>
      <td>
        {store.branchCode} {store.branchName} {hasNoPurchase(store) && <span className={styles.pill}>{tr('stores.noPurchase')}</span>}
      </td>
      <td>
        <div className={styles.barCell}>
          <div className={styles.bar}>
            <b style={{ width: `${max > 0 ? (store.amount / max) * 100 : 0}%`, background: '#1677ff' }} />
          </div>
          <span className={styles.barValue}>{formatMoney(store.amount)}</span>
        </div>
      </td>
      <td className={styles.num}>{formatCount(store.quantity)}</td>
      <td className={styles.num}>{formatCount(store.purchaseQuantity)}</td>
      <td>
        <div className={styles.barCell}>
          <div className={styles.bar}>
            <b style={{ width: `${Math.min(store.sellThrough ?? 0, 1) * 100}%`, background: TONE_COLOR[tone] }} />
          </div>
          <span className={styles.barValueSm}>{formatSellThrough(store.sellThrough)}</span>
        </div>
      </td>
      <td className={`${styles.num} ${store.earlySoldOutCount > 0 ? styles.bad : ''}`}>{formatCount(store.earlySoldOutCount)}</td>
      <td className={styles.num}>{formatMoney(store.estimatedLostAmount)}</td>
      <td className={`${styles.num} ${store.leftoverCost > 4000 ? styles.warnText : ''}`}>{formatMoney(store.leftoverCost)}</td>
      <td className={styles.num}>{formatMoney(store.afterHolidayAmount)}</td>
      <td className={styles.openText}>{tr('stores.open')} ›</td>
    </tr>
  )
}

// ---------- 单店基准 ----------

export const BenchmarkCard: FC<{ report: HolidaySalesReport; benchmark: HolidayBenchmark; tr: Tr }> = ({ report, benchmark, tr }) => {
  const metrics = benchmarkMetrics(report.totals, benchmark)
  return (
    <section className={styles.card}>
      <h2 className={styles.cardTitle}>
        {tr('benchmark.title')}
        <span className={styles.hint}>{tr('benchmark.hint', { count: benchmark.storeCount })}</span>
      </h2>
      <div className={styles.benchGrid}>
        {metrics.map((metric) => {
          const verdict = metric.value !== null && metric.average !== null ? compareToAverage(metric.value, metric.average, metric.higherIsBetter) : null
          return (
            <div key={metric.id} className={styles.benchItem}>
              <span className={styles.kpiLabel}>{tr(BENCHMARK_LABEL_KEY[metric.id])}</span>
              <strong className={styles.benchValue}>{formatMetric(metric.kind, metric.value)}</strong>
              <span className={styles.kpiSub}>
                {tr('benchmark.average', { value: formatMetric(metric.kind, metric.average) })}{' '}
                {verdict && (
                  <span className={`${styles.pill} ${verdict === 'better' ? styles.pillGood : verdict === 'worse' ? styles.pillWarn : ''}`}>
                    {tr(VERDICT_KEY[verdict])}
                  </span>
                )}
              </span>
            </div>
          )
        })}
      </div>
    </section>
  )
}

// ---------- 分类对照抽屉 ----------

export const CategoryMapDrawer: FC<{ open: boolean; onClose: () => void; map: HolidayCategoryMap | undefined; loading: boolean; error?: string; tr: Tr }> = ({
  open,
  onClose,
  map,
  loading,
  error,
  tr,
}) => (
  <Drawer open={open} onClose={onClose} width="min(1040px, 100vw)" title={tr('map.title')} destroyOnClose>
    <p className={styles.note}>{tr('map.desc')}</p>
    {loading && <div className={styles.empty}>{tr('state.loading')}</div>}
    {error && !loading && <div className={styles.errorBox}>{tr('state.error', { message: error })}</div>}
    {map && (
      <div className={styles.mapGrid}>
        {map.groups.map((group) => (
          <section key={group.key} className={styles.card}>
            <h3 className={styles.mapTitle}>
              <i className={styles.swatch} style={{ background: CATEGORY_COLOR[group.key] }} aria-hidden="true" />
              {tr(CATEGORY_LABEL_KEY[group.key])}
              <span className={styles.hint}>{formatMoney(group.amount)}</span>
            </h3>
            {group.entries.slice(0, 12).map((entry) => {
              const tag = supplierTag(entry.supplierCode)
              return (
                <div key={`${entry.supplierCode}|${entry.supplierCategory ?? ''}`} className={styles.mapItem}>
                  <span className={styles.mapName}>
                    <i className={`${styles.tag} ${tag.className}`}>{tag.label}</i> {entry.supplierCategory ?? tr('map.noCategory')}
                  </span>
                  <span className={styles.mapValue}>
                    {entry.skuCount} · {formatMoney(entry.amount)}
                  </span>
                </div>
              )
            })}
            {group.entries.length > 12 && <div className={styles.note}>{tr('map.more', { count: group.entries.length - 12 })}</div>}
          </section>
        ))}
        <section className={`${styles.card} ${styles.note}`}>
          <strong>{tr('map.rulesTitle')}</strong>
          <p>{tr('map.rules')}</p>
        </section>
      </div>
    )}
  </Drawer>
)

// ---------- 口径说明 ----------

export const Footer: FC<{ report: HolidaySalesReport; tr: Tr }> = ({ report, tr }) => (
  <section className={`${styles.card} ${styles.note}`}>
    <strong>{tr('footer.title')}</strong>
    <p>{tr('footer.scope', { count: formatCount(report.productCount) })}</p>
    <p>{tr('footer.purchase')}</p>
    <p>{tr('footer.sellThrough')}</p>
    <p>{tr('footer.soldOut')}</p>
  </section>
)
