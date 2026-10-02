import { useEffect, useMemo, useRef, useState, type CSSProperties } from 'react'
import { Alert, Button, Image, Input, message, Pagination, Skeleton, Tooltip } from 'antd'
import { DownloadOutlined, InfoCircleOutlined, SearchOutlined } from '@ant-design/icons'
import { useProductImageVersions } from '../../../hooks/useProductImageVersion'
import { toProductImagePreviewUrl, toProductThumbnailUrl } from '../../../utils/productImageThumbnail'
import { MetricPair, useReportText } from '../ReportWorkbench/ReportControls'
import { growth, normalizeKeyword, reportPeriod, type DateSelection } from '../ReportWorkbench/logic'
import { useReportQuery } from '../ReportWorkbench/useReportQuery'
import { ancestorKeys, flattenCategoryTree, nodeKey, resolveCategoryFocus, supplierOpenKeys, type CategorySelection, type CategoryTreeRow } from './categoryLogic'
import { fetchSalesDetailCategoryReport, UNASSIGNED_CATEGORY_KEY, type CategoryMetrics, type CategoryReport, type CategoryReportQuery } from './categoryReportService'
import { MAX_PRODUCT_IMAGE_EXPORT_ROWS } from './logic'
import type { SalesDetailRow } from './reportService'
import styles from './styles.module.css'
import tab from './categoryTab.module.css'

const money = new Intl.NumberFormat('en-AU', { style: 'currency', currency: 'AUD', maximumFractionDigits: 0 })
const moneyCents = new Intl.NumberFormat('en-AU', { style: 'currency', currency: 'AUD', minimumFractionDigits: 2, maximumFractionDigits: 2 })
const percent = new Intl.NumberFormat('en-AU', { style: 'percent', minimumFractionDigits: 1, maximumFractionDigits: 1 })
const integer = new Intl.NumberFormat('en-AU', { maximumFractionDigits: 0 })
/** 分类树请求顺带返回默认节点第一页商品时使用的页大小；改每页条数不应重算分类树。 */
const DEFAULT_PAGE_SIZE = 20

function Growth({ current, previous, compare }: { current: number; previous: number | null; compare: boolean }) {
  const text = useReportText()
  const value = compare ? growth(current, previous) : null
  return <span className={typeof value === 'number' ? value > 0 ? styles.positive : value < 0 ? styles.negative : styles.muted : styles.muted}>
    {value === null ? '—' : value === 'new' ? text('新增', 'New') : `${value > 0 ? '+' : ''}${(value * 100).toFixed(1)}%`}
  </span>
}

/** 毛利率；营业额不为零但成本未补全时与销售明细一致提示「成本待补全」，不显示成 0 或空。 */
function Margin({ metrics }: { metrics: { revenue: number; grossMarginRate: number | null } }) {
  const text = useReportText()
  if (metrics.grossMarginRate != null) return <>{percent.format(metrics.grossMarginRate)}</>
  return metrics.revenue !== 0
    ? <Tooltip title={text('部分商品成本尚未补全，暂不计算毛利', 'Some product costs are still pending')}><span className={tab.costPending}>{text('待补全', 'Pending')}</span></Tooltip>
    : <>—</>
}

function ShareBar({ value, tone = 'normal' }: { value: number | null; tone?: 'supplier' | 'normal' | 'warn' }) {
  return <span className={tab.share}>
    <span className={tab.shareTrack} aria-hidden><span className={tab.shareFill} data-tone={tone}
      style={{ width: `${Math.max(0, Math.min(1, value ?? 0)) * 100}%` } as CSSProperties} /></span>
    <span>{value == null ? '—' : percent.format(value)}</span>
  </span>
}

export default function SupplierCategoryTab({ active, allowed, userGuid, branches, dates, supplierCodes, selectedBranchCode, refresh, onLoadingChange, onSelectSuppliers }: {
  active: boolean; allowed: boolean; userGuid?: string; branches?: string[]; dates: DateSelection
  supplierCodes: string[]; selectedBranchCode?: string; refresh: number
  onLoadingChange: (loading: boolean) => void; onSelectSuppliers: () => void
}) {
  const text = useReportText()
  const period = useMemo(() => reportPeriod(dates), [dates])
  const [selected, setSelected] = useState<CategorySelection>()
  const [expanded, setExpanded] = useState<ReadonlySet<string>>(() => new Set())
  const [showAll, setShowAll] = useState<ReadonlySet<string>>(() => new Set())
  const [filter, setFilter] = useState('')
  const [keywordDraft, setKeywordDraft] = useState('')
  const [keyword, setKeyword] = useState('')
  const [page, setPage] = useState(1)
  const [pageSize, setPageSize] = useState(DEFAULT_PAGE_SIZE)
  const [exporting, setExporting] = useState(false)
  const exportAbort = useRef<AbortController | null>(null)
  useEffect(() => () => exportAbort.current?.abort(), [])

  // 所选供应商变化时回到第一个供应商的全部商品；它与分类树同一次请求返回，首屏只等一次查询。
  const defaultSelection = useMemo<CategorySelection | undefined>(() => supplierCodes[0] ? { supplierCode: supplierCodes[0] } : undefined, [supplierCodes])
  useEffect(() => {
    setSelected(current => current && supplierCodes.includes(current.supplierCode) ? current : defaultSelection)
  }, [supplierCodes, defaultSelection])
  useEffect(() => { setPage(1) }, [selected, keyword, dates, selectedBranchCode, supplierCodes])
  useEffect(() => {
    const timer = window.setTimeout(() => setKeyword(normalizeKeyword(keywordDraft)), 300)
    return () => window.clearTimeout(timer)
  }, [keywordDraft])

  const base: CategoryReportQuery = { ...period, supplierCodes, branchCodes: branches, selectedBranchCode }
  const enabled = allowed && supplierCodes.length > 0
  const treeQuery: CategoryReportQuery = { ...base, nodeSupplierCode: defaultSelection?.supplierCode, pageIndex: 1, pageSize: DEFAULT_PAGE_SIZE, includeTree: true }
  const tree = useReportQuery<CategoryReport>(`sales-detail:category-tree:${JSON.stringify([userGuid, treeQuery])}`,
    signal => fetchSalesDetailCategoryReport(treeQuery, signal), { active, enabled, refresh, metricId: 'sales-detail-category-tree' })
  const report = tree.snapshot?.data
  // 默认节点的第一页已随分类树返回；其余节点、翻页或搜索才单独请求商品，且不再重复汇总分类树。
  const usesTreeProducts = !!selected && !selected.categoryGuid && selected.supplierCode === defaultSelection?.supplierCode && page === 1 && pageSize === DEFAULT_PAGE_SIZE && !keyword
  const productQuery: CategoryReportQuery = { ...base, nodeSupplierCode: selected?.supplierCode, nodeCategoryGuid: selected?.categoryGuid,
    search: keyword || undefined, pageIndex: page, pageSize, includeTree: false }
  const products = useReportQuery<CategoryReport>(`sales-detail:category-products:${JSON.stringify([userGuid, productQuery])}`,
    signal => fetchSalesDetailCategoryReport(productQuery, signal),
    { active, enabled: enabled && !!selected && !!report && !usesTreeProducts, refresh, metricId: 'sales-detail-category-products' })
  const productPage = usesTreeProducts ? report?.products : products.snapshot?.data.products
  const productsLoading = usesTreeProducts ? tree.loading : products.loading
  const productsError = usesTreeProducts ? tree.error : products.error
  useEffect(() => { onLoadingChange(tree.loading || (!usesTreeProducts && products.loading)) }, [tree.loading, products.loading, usesTreeProducts, onLoadingChange])

  const labels = { all: text('全部', 'All'), unassigned: text('未归类', 'Uncategorised') }
  const focus = report ? resolveCategoryFocus(report, selected, labels) : undefined
  useEffect(() => {
    if (!report) return
    // 换了日期或供应商后当前分类可能已无销售：回到默认节点。少量供应商时默认展开，方便直接看到一级分类。
    if (selected && !resolveCategoryFocus(report, selected, labels)) setSelected(defaultSelection)
    setExpanded(current => current.size || report.suppliers.length > 3 ? current : new Set(report.suppliers.flatMap(item => supplierOpenKeys(report, item.supplierCode))))
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [report])

  const rows = report ? flattenCategoryTree(report, { expanded, showAll, filter }) : []
  const select = (next: CategorySelection) => {
    setSelected(next)
    if (report) setExpanded(current => new Set([...current, ...ancestorKeys(report, next)]))
  }
  const toggle = (row: CategoryTreeRow) => setExpanded(current => {
    const next = new Set(current)
    if (next.has(row.key)) next.delete(row.key)
    else (row.kind === 'supplier' && report ? supplierOpenKeys(report, row.supplierCode) : [row.key]).forEach(key => next.add(key))
    return next
  })
  const allKeys = () => {
    const keys: string[] = []
    const walk = (supplier: string, nodes: CategoryReport['suppliers'][number]['categories']) => nodes.forEach(node => {
      if (node.children.length) { keys.push(nodeKey(supplier, node.categoryGuid)); walk(supplier, node.children) }
    })
    report?.suppliers.forEach(supplier => { keys.push(nodeKey(supplier.supplierCode)); walk(supplier.supplierCode, supplier.categories) })
    return keys
  }
  const fullyExpanded = rows.length > 0 && allKeys().every(key => expanded.has(key))
  const isSelected = (row: CategoryTreeRow) => !!selected && row.kind !== 'more' && row.supplierCode === selected.supplierCode
    && (row.kind === 'supplier' ? !selected.categoryGuid : row.categoryGuid?.toLowerCase() === selected.categoryGuid?.toLowerCase())

  const pageRows = useMemo(() => [...(productPage?.rows ?? [])].sort((left, right) => right.revenue - left.revenue
    || (right.compareRevenue ?? 0) - (left.compareRevenue ?? 0) || left.code.localeCompare(right.code)), [productPage])
  const imageVersion = useProductImageVersions(pageRows.map(row => row.productImage))
  const nodeName = focus ? focus.node ? focus.node.categoryGuid === UNASSIGNED_CATEGORY_KEY ? labels.unassigned : focus.node.name
    : text(`${focus.supplier.supplierName || focus.supplier.supplierCode} · 全部商品`, `${focus.supplier.supplierName || focus.supplier.supplierCode} · All products`) : ''
  const pageTotal = pageRows.reduce((sum, row) => ({ revenue: sum.revenue + row.revenue, compare: sum.compare + (row.compareRevenue ?? 0), quantity: sum.quantity + row.quantity }),
    { revenue: 0, compare: 0, quantity: 0 })

  const runExport = async () => {
    if (!productPage?.rows.length || exportAbort.current || !focus) return
    const controller = new AbortController()
    exportAbort.current = controller
    setExporting(true)
    try {
      const { exportSalesDetailRows } = await import('./export')
      const path = focus.path.slice(1).map(item => item.label).join('_').replace(/[\\/:*?"<>|]+/g, '-')
      const result = await exportSalesDetailRows(pageRows, `${text('澳洲供应商分类', 'AU_Supplier_Category')}_${path}_${dates.startDate}_${dates.endDate}_page-${page}.xlsx`, {
        compare: dates.compare, english: text('zh', 'en') === 'en', startDate: dates.startDate, endDate: dates.endDate, signal: controller.signal,
      })
      message.success(text(`已导出 ${result.count} 件商品${result.failedImages ? `，${result.failedImages} 张图片读取失败` : ''}`,
        `Exported ${result.count} products${result.failedImages ? `; ${result.failedImages} images unavailable` : ''}`))
    } catch (error) {
      if (!controller.signal.aborted) message.error(error instanceof Error ? error.message : text('导出失败', 'Export failed'))
    } finally {
      exportAbort.current = null
      setExporting(false)
    }
  }

  if (!supplierCodes.length)
    return <section className={tab.emptyState} aria-live="polite">
      <strong>{text('先选择要查看的澳洲供应商', 'Choose Australian suppliers to start')}</strong>
      <span>{text('可多选，Hot Bargain（200）按仓库分类汇总，其他供应商按各自网站分类汇总。', 'Select one or more. Hot Bargain (200) uses warehouse categories; other suppliers use their own website categories.')}</span>
      <Button type="primary" onClick={onSelectSuppliers}>{text('选择供应商', 'Choose suppliers')}</Button>
    </section>

  const summary = report?.summary
  const kpi = (label: string, field: keyof CategoryMetrics, previousField: keyof CategoryMetrics | undefined, format: 'money' | 'integer') =>
    <div className={styles.kpi}>
      <span className={styles.kpiLabel}>{label}{summary && dates.compare && previousField && <span className={styles.kpiGrowth}>
        <Growth current={summary[field] as number} previous={summary[previousField] as number | null} compare /></span>}</span>
      <div className={styles.kpiValue}>{summary ? <MetricPair current={summary[field] as number} previous={previousField ? summary[previousField] as number | null : null}
        compare={dates.compare && !!previousField} format={format} /> : <strong className={styles.pendingTotal}>—</strong>}</div>
    </div>
  const unassignedShare = summary && summary.revenue > 0 ? (report!.unassigned.revenue / summary.revenue) : null
  const biggestUnassigned = report?.suppliers.filter(item => item.unassigned).sort((a, b) => b.unassigned!.revenue - a.unassigned!.revenue)[0]
  const colSpan = dates.compare ? 6 : 4

  return <>
    {tree.error && <Alert type="warning" showIcon message={tree.error} />}
    {tree.snapshot?.statisticMessage && <Alert type="warning" showIcon message={tree.snapshot.statisticMessage} />}
    <section className={styles.summary} aria-label={text('所选供应商汇总', 'Selected supplier totals')}
      style={{ '--previous-label': JSON.stringify(text('同期 ', 'Prev ')) } as CSSProperties}>
      {kpi(text('所选供应商营业额', 'Selected supplier revenue'), 'revenue', 'compareRevenue', 'money')}
      {kpi(text('商品数量', 'Product quantity'), 'quantity', 'compareQuantity', 'integer')}
      <div className={styles.kpi}>
        <span className={styles.kpiLabel}>{text('毛利 · 毛利率', 'Gross profit · margin')}</span>
        <div className={tab.kpiPlain}>{summary && summary.grossProfit == null && summary.revenue !== 0
          ? <strong className={tab.costPending}>{text('成本待补全', 'Cost pending')}</strong>
          : <><strong>{summary?.grossProfit == null ? '—' : money.format(summary.grossProfit)}</strong>
            <small>{summary?.grossMarginRate == null ? '' : percent.format(summary.grossMarginRate)}</small></>}</div>
      </div>
      {kpi(text('动销商品', 'Products sold'), 'productCount', undefined, 'integer')}
      <div className={`${styles.kpi} ${tab.kpiWarn}`}>
        <span className={styles.kpiLabel}>{text('未归类营业额', 'Uncategorised revenue')}
          <Tooltip title={text('商品没有供应商分类，或所属分类已删除、归属的供应商与商品当前供应商不一致', 'No supplier category, the category was deleted, or the assignment belongs to another supplier')}>
            <InfoCircleOutlined /></Tooltip></span>
        <div className={tab.kpiPlain}><strong>{report ? money.format(report.unassigned.revenue) : '—'}</strong>
          <small>{unassignedShare == null ? '' : percent.format(unassignedShare)}</small>
          {biggestUnassigned && <Button type="link" size="small" onClick={() => select({ supplierCode: biggestUnassigned.supplierCode, categoryGuid: UNASSIGNED_CATEGORY_KEY })}>
            {text('只看未归类', 'View')}</Button>}</div>
      </div>
      <div className={styles.scopeBlock}><span>{dates.startDate === dates.endDate ? dates.startDate : `${dates.startDate} — ${dates.endDate}`}
        {dates.compare && period.compareStartDate ? ` · ${text('同期', 'Previous')} ${period.compareStartDate} — ${period.compareEndDate}` : ''} · AUD</span></div>
    </section>

    <div className={tab.split}>
      <section className={tab.panel} aria-label={text('分类汇总', 'Category totals')} aria-busy={tree.loading}>
        <header className={tab.panelHeader}>
          <h2><span>01</span>{text('分类汇总', 'Category totals')}</h2>
          <Input size="small" allowClear prefix={<SearchOutlined />} className={tab.filter} value={filter} onChange={event => setFilter(event.target.value)}
            placeholder={text('筛选分类名', 'Filter categories')} aria-label={text('筛选分类名', 'Filter categories')} />
          <Button size="small" disabled={!report} onClick={() => setExpanded(fullyExpanded ? new Set() : new Set(allKeys()))}>
            {fullyExpanded ? text('全部收起', 'Collapse all') : text('全部展开', 'Expand all')}</Button>
        </header>
        {tree.slow && tree.loading && <div className={styles.slow} role="status">{text('查询超过 3 秒，正在读取完整数据…', 'Over 3 seconds. Loading complete data…')}</div>}
        <div className={tab.scroll}>
          {tree.loading && !report ? <div className={styles.skeleton}><Skeleton active paragraph={{ rows: 8 }} title={false} /></div>
            : !rows.length ? <div className={styles.empty}>{filter ? text('没有匹配的分类', 'No matching categories') : text('当前条件下没有数据', 'No data for these filters')}</div>
              : <table className={tab.treeTable} data-compare={dates.compare}>
                <colgroup><col /><col className={tab.moneyCol} />{dates.compare && <><col className={tab.compareCol} /><col className={tab.growthCol} /></>}
                  <col className={tab.shareCol} /><col className={tab.marginCol} /></colgroup>
                <thead><tr><th>{text('供应商 / 分类', 'Supplier / category')}</th><th>{text('营业额', 'Revenue')} ↓</th>
                  {dates.compare && <><th>{text('同期', 'Previous')}</th><th>{text('增长率', 'Growth')}</th></>}
                  <th><Tooltip title={text('供应商占所选供应商合计；一级分类占所属供应商；子分类占父分类', 'Supplier vs selected total; category vs its parent')}>
                    <span>{text('占上级', 'Of parent')}</span></Tooltip></th><th>{text('毛利率', 'Margin')}</th></tr></thead>
                <tbody>{rows.map(row => row.kind === 'more'
                  ? <tr key={row.key} className={tab.moreRow}><td colSpan={colSpan}>
                    <button type="button" style={{ paddingLeft: row.depth * 16 + 18 }} onClick={() => setShowAll(current => new Set(current).add(row.parentKey!))}>
                      {text(`显示其余 ${row.hiddenCount} 个分类 · ${money.format(row.hiddenRevenue ?? 0)}`, `Show ${row.hiddenCount} more · ${money.format(row.hiddenRevenue ?? 0)}`)}</button></td></tr>
                  : <tr key={row.key} data-kind={row.kind} className={isSelected(row) ? tab.selected : undefined}>
                    <td><div className={tab.nameCell} style={{ paddingLeft: row.depth * 16 }}>
                      {row.expandable ? <button type="button" className={tab.caret} aria-expanded={row.expanded}
                        aria-label={row.expanded ? text(`收起${row.name}`, `Collapse ${row.name}`) : text(`展开${row.name}`, `Expand ${row.name}`)}
                        onClick={() => toggle(row)}>{row.expanded ? '▾' : '▸'}</button> : <span className={tab.caretSpacer} />}
                      <button type="button" className={tab.nodeButton} aria-pressed={isSelected(row)}
                        title={row.kind === 'supplier' ? `${row.name} · ${row.supplierCode}${row.source === 'warehouse' ? ` · ${text('按仓库分类', 'Warehouse categories')}` : ''}` : row.name}
                        onClick={() => select({ supplierCode: row.supplierCode, categoryGuid: row.kind === 'supplier' ? undefined : row.categoryGuid })}>
                        <span>{row.kind === 'unassigned' ? labels.unassigned : row.name}</span>
                        {row.kind === 'supplier' && <small>{row.supplierCode}</small>}
                        {row.inactive && <small>{text('已停用', 'Inactive')}</small>}
                      </button>
                      {row.kind === 'supplier' && !!row.unassignedShare && row.unassignedShare >= 0.05 && <Tooltip
                        title={text(`该供应商 ${percent.format(row.unassignedShare)} 的营业额没有分类`, `${percent.format(row.unassignedShare)} of this supplier's revenue is uncategorised`)}>
                        <span className={tab.warnTag} aria-label={text(`未归类 ${percent.format(row.unassignedShare)}`, `${percent.format(row.unassignedShare)} uncategorised`)}>
                          {percent.format(row.unassignedShare)}</span></Tooltip>}
                    </div></td>
                    <td>{money.format(row.metrics!.revenue)}</td>
                    {dates.compare && <><td className={styles.muted}>{row.metrics!.compareRevenue == null ? '—' : money.format(row.metrics!.compareRevenue)}</td>
                      <td><Growth current={row.metrics!.revenue} previous={row.metrics!.compareRevenue} compare /></td></>}
                    <td><ShareBar value={row.share} tone={row.kind === 'supplier' ? 'supplier' : row.kind === 'unassigned' ? 'warn' : 'normal'} /></td>
                    <td className={row.metrics!.grossMarginRate != null && row.metrics!.grossMarginRate < 0 ? styles.negative : undefined}>
                      <Margin metrics={row.metrics!} /></td>
                  </tr>)}</tbody>
              </table>}
        </div>
      </section>

      <section className={`${tab.panel} ${tab.detailPanel}`} aria-label={text('分类商品明细', 'Category products')} aria-busy={productsLoading}>
        <header className={tab.detailHeader}>
          <nav aria-label={text('当前分类', 'Current category')} className={tab.crumbs}>
            <span className={tab.panelNo}>02</span>
            {(focus?.path ?? []).map((item, index, path) => index === path.length - 1 || !item.selection && index > 0
              ? <span key={index} aria-current={index === path.length - 1 ? 'page' : undefined}>{item.label}</span>
              : <span key={index} className={tab.crumbLink}>
                <button type="button" onClick={() => item.selection ? select(item.selection) : setSelected(defaultSelection)}>{item.label}</button><span aria-hidden>›</span></span>)}
          </nav>
          <div className={tab.detailTitle}>
            <h2 title={nodeName}>{nodeName || '—'}</h2>
            {focus && <dl className={tab.stats}>
              <div><dt>{text('商品', 'Products')}</dt><dd>{integer.format(focus.metrics.productCount)}</dd></div>
              <div><dt>{text('营业额', 'Revenue')}</dt><dd>{money.format(focus.metrics.revenue)}{dates.compare && <small> · {text('同期', 'Prev')} {focus.metrics.compareRevenue == null ? '—' : money.format(focus.metrics.compareRevenue)}{' '}
                <Growth current={focus.metrics.revenue} previous={focus.metrics.compareRevenue} compare /></small>}</dd></div>
              <div><dt>{text('数量', 'Quantity')}</dt><dd>{integer.format(focus.metrics.quantity)}{dates.compare && focus.metrics.compareQuantity != null && <small> · {text('同期', 'Prev')} {integer.format(focus.metrics.compareQuantity)}</small>}</dd></div>
              <div><dt>{text('毛利率', 'Margin')}</dt><dd><Margin metrics={focus.metrics} /></dd></div>
              <div><dt>{text('占上级', 'Of parent')}</dt><dd>{focus.share == null ? '—' : percent.format(focus.share)}</dd></div>
            </dl>}
          </div>
          <div className={tab.detailTools}>
            <Input size="small" allowClear prefix={<SearchOutlined />} value={keywordDraft} onChange={event => setKeywordDraft(event.target.value)}
              placeholder={text('货号 / 条码 / 品名', 'Item / barcode / name')} aria-label={text('搜索本分类商品', 'Search products in this category')} />
            <Button size="small" icon={<DownloadOutlined />} loading={exporting} disabled={!pageRows.length || productsLoading}
              onClick={() => { void runExport() }}>{text('导出本页 Excel', 'Export page Excel')}</Button>
          </div>
        </header>
        <div className={tab.scroll}>
          {productsError ? <div className={styles.empty}><Alert type="warning" message={productsError} /></div>
            : productsLoading || !productPage ? <div className={styles.skeleton}><Skeleton active paragraph={{ rows: 8 }} title={false} /></div>
              : !pageRows.length ? <div className={styles.empty}>{keyword ? text('没有匹配的商品', 'No matching products') : text('当前分类没有销售', 'No sales in this category')}</div>
                : <table className={tab.productTable}>
                  <colgroup><col className={tab.rankCol} /><col /><col className={tab.priceCol} /><col className={tab.qtyCol} /><col className={tab.moneyCol} />
                    {dates.compare && <><col className={tab.compareCol} /><col className={tab.growthCol} /></>}<col className={tab.marginCol} /></colgroup>
                  <thead><tr><th>#</th><th>{text('货号 / 商品名称', 'Item / product')}</th><th>{text('均价', 'Unit price')}</th><th>{text('数量', 'Qty')}</th>
                    <th><Tooltip title={text('第二行为占本分类营业额', 'Second line: share of this category')}><span>{text('营业额', 'Revenue')} ↓</span></Tooltip></th>{dates.compare && <><th>{text('同期', 'Previous')}</th><th>{text('增长率', 'Growth')}</th></>}
                    <th>{text('毛利率', 'Margin')}</th></tr></thead>
                  <tbody>{pageRows.map((row: SalesDetailRow, index) => <tr key={row.code}>
                    <td className={styles.muted}>{(page - 1) * pageSize + index + 1}</td>
                    <td><div className={tab.productCell}>
                      {row.productImage ? <Image className={tab.thumb} width={32} height={32} alt=""
                        src={toProductThumbnailUrl(row.productImage, undefined, imageVersion(row.productImage))}
                        preview={{ src: toProductImagePreviewUrl(row.productImage, imageVersion(row.productImage)) }} />
                        : <span className={styles.imagePlaceholder}>▦</span>}
                      <span className={tab.productText} title={`${row.name} · ${row.code}`}><small>{row.itemNumber || row.code}</small><strong>{row.name || row.code}</strong></span>
                    </div></td>
                    <td>{row.averageUnitPrice == null ? '—' : moneyCents.format(row.averageUnitPrice)}</td>
                    <td><span className={tab.stack}><span>{integer.format(row.quantity)}</span>
                      {dates.compare && <small>{row.compareQuantity == null ? '—' : integer.format(row.compareQuantity)}</small>}</span></td>
                    <td><span className={tab.stack}><strong>{moneyCents.format(row.revenue)}</strong>
                      <small>{focus && focus.metrics.revenue > 0 ? percent.format(row.revenue / focus.metrics.revenue) : '—'}</small></span></td>
                    {dates.compare && <><td className={styles.muted}>{row.compareRevenue == null ? '—' : moneyCents.format(row.compareRevenue)}</td>
                      <td><Growth current={row.revenue} previous={row.compareRevenue} compare /></td></>}
                    <td><Margin metrics={row} /></td>
                  </tr>)}</tbody>
                </table>}
        </div>
        <footer className={tab.detailFooter}>
          <span>{productPage && productPage.total > 0 && text(
            `第 ${(page - 1) * pageSize + 1}–${Math.min(productPage.total, page * pageSize)} 件 · 共 ${integer.format(productPage.total)} 件 ｜ 本页合计 ${moneyCents.format(pageTotal.revenue)}${dates.compare ? ` · 同期 ${moneyCents.format(pageTotal.compare)}` : ''}`,
            `${(page - 1) * pageSize + 1}–${Math.min(productPage.total, page * pageSize)} of ${integer.format(productPage.total)} | Page total ${moneyCents.format(pageTotal.revenue)}${dates.compare ? ` · prev ${moneyCents.format(pageTotal.compare)}` : ''}`)}</span>
          <Pagination size="small" showSizeChanger showLessItems current={page} pageSize={pageSize} total={productPage?.total ?? 0}
            pageSizeOptions={[10, 20, 50, 100, 200, MAX_PRODUCT_IMAGE_EXPORT_ROWS]} disabled={productsLoading}
            onChange={(next, size) => { if (size !== pageSize) { setPageSize(size); setPage(1) } else setPage(next) }} />
        </footer>
      </section>
    </div>
  </>
}
