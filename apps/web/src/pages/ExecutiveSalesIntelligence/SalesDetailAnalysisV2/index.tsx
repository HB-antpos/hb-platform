import { Fragment, useEffect, useLayoutEffect, useMemo, useRef, useState, type CSSProperties, type ReactNode } from 'react'
import { Alert, Button, Image, Input, message, Pagination, Segmented, Select, Skeleton, Tag, Tooltip, TreeSelect } from 'antd'
import { CloseOutlined, DownloadOutlined, FullscreenExitOutlined, FullscreenOutlined, InfoCircleOutlined, MenuFoldOutlined, MenuUnfoldOutlined, SearchOutlined, ShopOutlined } from '@ant-design/icons'
import { useKeepAliveContext } from 'keepalive-for-react'
import { useLocation, useNavigate } from 'react-router-dom'
import { useIsMobile } from '../../../hooks/useIsMobile'
import { useProductImageVersions } from '../../../hooks/useProductImageVersion'
import { useAuthStore } from '../../../store/auth'
import { toProductImagePreviewUrl, toProductThumbnailUrl } from '../../../utils/productImageThumbnail'
import { MetricPair, ReportControls, useReportText } from '../ReportWorkbench/ReportControls'
import { growth, normalizeKeyword, reportPeriod } from '../ReportWorkbench/logic'
import { useReportQuery, type ReportQueryState } from '../ReportWorkbench/useReportQuery'
import { applyKeyword, clampRailWidth, defaultDetailView, emptySelection, exceedsSalesDetailSelectionLimit, initialDetailState, MAX_CATEGORY_SELECTIONS, MAX_PRODUCT_IMAGE_EXPORT_ROWS, MAX_SUPPLIER_SELECTIONS, parseDetailView, RAIL_DEFAULT_WIDTH, selectDimension, sumProductPage, type CompareView, type DetailTotals, type DetailViewPreference } from './logic'
import ProductBranchDrawer from './ProductBranchDrawer'
import { fetchSalesDetailReport, type ReportSection, type SalesDetailPage, type SalesDetailQuery, type SalesDetailReport, type SalesDetailRow } from './reportService'
import { buildSalesDetailSupplierCategoryTree, fetchSalesDetailCategoryOptions, type SalesDetailCategoryGroup, type SalesDetailCategoryTreeNode } from './categoryOptionsService'
import styles from './styles.module.css'

type MetricKey = 'revenue' | 'grossProfit' | 'grossMarginRate' | 'quantity' | 'averageUnitPrice' | 'share' | 'chinaShare'
type ProductMetric = Exclude<MetricKey, 'share' | 'chinaShare'>
type PanelKey = 'suppliers' | 'branches' | 'products'
type RailPanel = 'suppliers' | 'branches'
type Sort = { key: MetricKey; ascending: boolean }
type SectionState = ReportQueryState<SalesDetailPage> & { data?: SalesDetailPage }
/** 单元格读取的字段：明细行、服务端汇总行和本页合计都满足该形状。 */
type MetricRow = DetailTotals & Partial<Pick<SalesDetailRow, 'share' | 'compareShare' | 'chinaShare' | 'compareChinaShare'>>

// 商品表列宽按百分比分配：名称列吸收剩余宽度（上下/仅本期约 42%，左右约 25%），展开到宽屏时数字列同步变宽。
const STACK_WIDTHS: Record<ProductMetric, string> = { revenue: '11%', quantity: '8.5%', averageUnitPrice: '8.5%', grossProfit: '11%', grossMarginRate: '10%' }
const SIDE_WIDTHS: Record<ProductMetric, [string, string]> = { revenue: ['9%', '8%'], quantity: ['5.5%', '5%'], averageUnitPrice: ['5.5%', '5%'], grossProfit: ['8.5%', '8%'], grossMarginRate: ['6%', '8%'] }
const GROWTH_WIDTH = { stack: '9%', side: '6%' }
const VIEW_STORAGE_KEY = 'hb.sales-detail.view'

// 展示偏好只是本机便利项：存储不可用（隐私模式、被禁用）时按默认展示，不影响查询。
function readViewPreference(): DetailViewPreference {
  try { return parseDetailView(window.localStorage.getItem(VIEW_STORAGE_KEY)) } catch { return defaultDetailView }
}
function saveViewPreference(value: DetailViewPreference) {
  try { window.localStorage.setItem(VIEW_STORAGE_KEY, JSON.stringify(value)) } catch { /* 仅本次会话生效 */ }
}

function projectSection(state: ReportQueryState<SalesDetailReport> & { data?: SalesDetailReport }, section: ReportSection): SectionState {
  const data = state.data?.[section]
  return { ...state, data, snapshot: state.snapshot
    ? { ...state.snapshot, data: data ?? { rows: [], total: 0 } }
    : undefined }
}

function GrowthCell({ current, previous, compare }: { current: number; previous: number | null; compare: boolean }) {
  const text = useReportText()
  const value = compare ? growth(current, previous) : null
  return <span className={typeof value === 'number' ? value > 0 ? styles.positive : value < 0 ? styles.negative : styles.muted : styles.muted}>
    {value === null ? '—' : value === 'new' ? text('新增', 'New') : `${value > 0 ? '+' : ''}${(value * 100).toFixed(1)}%`}
  </span>
}

function Panel({ id, title, hint, count, query, expanded, onExpand, leading, scope, action, filter, notice, children, footer, onRetry }: {
  id: PanelKey; title: string; hint: string; count?: number; query: ReportQueryState<SalesDetailPage>
  expanded: boolean; onExpand: () => void; leading?: ReactNode; scope?: ReactNode; action?: ReactNode; filter: ReactNode; notice?: ReactNode;
  children: ReactNode; footer?: ReactNode; onRetry: () => void
}) {
  const text = useReportText()
  // 标题、筛选与操作合并为一行，把纵向空间留给表格。
  return <section data-panel={id} className={`${styles.panel} ${expanded ? styles.expanded : ''}`} aria-busy={query.loading}>
    <header className={styles.panelHeader}>{leading}
      <h2 title={hint}><span>{id === 'suppliers' ? '01' : id === 'branches' ? '02' : '03'}</span>{title}<small>{count?.toLocaleString('en-AU') ?? '—'}</small></h2>
      {scope}
      <div className={styles.panelFilter}>{filter}</div>
      <div className={styles.panelActions}>{action}
        <Button type="text" size="small" icon={expanded ? <FullscreenExitOutlined /> : <FullscreenOutlined />} onClick={onExpand}
          title={expanded ? text('收起（Esc）', 'Collapse (Esc)') : text('展开', 'Expand')}
          aria-label={expanded ? text(`收起${title}`, `Collapse ${title}`) : text(`展开${title}`, `Expand ${title}`)} /></div></header>
    {notice}
    {query.slow && query.loading && <div className={styles.slow} role="status">{text('查询超过 3 秒，正在读取完整数据…', 'Over 3 seconds. Loading complete data…')}</div>}
    <div className={styles.scroll} tabIndex={0} aria-label={text(`${title}可滚动表格`, `${title} scrollable table`)}>
      {query.error ? <div className={styles.empty}><Alert type="warning" message={query.error} /><Button onClick={onRetry}>{text('重试', 'Retry')}</Button></div>
        : query.loading ? <div className={styles.skeleton}><Skeleton active paragraph={{ rows: 7 }} title={false} /><p>{query.snapshot?.statisticMessage}</p></div>
          : count === 0 ? <div className={styles.empty}>{text('当前条件下没有数据', 'No data for these filters')}</div> : children}
    </div>
    {footer && <footer className={styles.panelFooter}>{footer}</footer>}
  </section>
}

export default function SalesDetailAnalysisV2() {
  const text = useReportText()
  const location = useLocation()
  const navigate = useNavigate()
  const { active: cachedActive } = useKeepAliveContext()
  const isMobile = useIsMobile()
  // 手机布局直接渲染页面，没有 KeepAlive；使用与布局相同的判断，避免默认 false 阻止查询。
  const active = isMobile || cachedActive
  const { access, currentUser } = useAuthStore()
  const initial = useMemo(() => initialDetailState(location.search), [location.search])
  const [dates, setDates] = useState(initial.dates)
  const [kind, setKind] = useState(initial.kind)
  const [selection, setSelection] = useState(initial.selection)
  const [keywordDraft, setKeywordDraft] = useState('')
  const [composing, setComposing] = useState(false)
  const [supplierSearch, setSupplierSearch] = useState('')
  const [categoryGroups, setCategoryGroups] = useState<SalesDetailCategoryGroup[]>()
  const [categoryLoading, setCategoryLoading] = useState(false)
  const [categoryError, setCategoryError] = useState<string>()
  const [expanded, setExpanded] = useState<PanelKey | null>(null)
  const [view, setView] = useState<DetailViewPreference>(readViewPreference)
  const [railWidth, setRailWidth] = useState(RAIL_DEFAULT_WIDTH)
  const [bundleRefresh, setBundleRefresh] = useState(0)
  const [drawerProduct, setDrawerProduct] = useState<SalesDetailRow | null>(null)
  // 大图预览按商品编码记录：翻页、筛选后该商品不在本页时预览自动关闭，不会错位到别的商品。
  const [previewCode, setPreviewCode] = useState<string | null>(null)
  const [brokenImages, setBrokenImages] = useState<ReadonlySet<string>>(() => new Set())
  const [exporting, setExporting] = useState(false)
  const [exportFinalizing, setExportFinalizing] = useState(false)
  const [exportProgress, setExportProgress] = useState('')
  const exportAbort = useRef<AbortController | null>(null)
  const [sorts, setSorts] = useState<Record<RailPanel, Sort>>({ suppliers: { key: 'revenue', ascending: false }, branches: { key: 'revenue', ascending: false } })
  const page = useRef<HTMLElement>(null)
  const [pageOffset, setPageOffset] = useState<number>()
  const workspace = useRef<HTMLDivElement>(null)
  const drag = useRef<{ startX: number; railWidth: number; width: number }>()
  const selectedNames = useRef<Record<string, string>>({})
  const searchClear = useRef(false)
  const appliedSearch = useRef(location.search)

  useEffect(() => {
    // 抽屉与大图预览都挂载在 body；页面切换或账号变化时关闭，避免覆盖其他保活页面。
    setDrawerProduct(null)
    setPreviewCode(null)
  }, [active, currentUser?.userGUID])
  useEffect(() => () => exportAbort.current?.abort(), [])
  useLayoutEffect(() => {
    const element = page.current
    if (!active || !element) return
    // 桌面端让页面恰好填满视口：量出页面在文档中的起点（顶栏、标签栏）加内容区底部留白，外壳高度变化时仍然准确。
    const measure = () => {
      const rect = element.getBoundingClientRect()
      if (!rect.width) return
      const container = element.closest('.admin-content')
      const bottom = container ? parseFloat(getComputedStyle(container).paddingBottom) || 0 : 0
      setPageOffset(Math.round(rect.top + window.scrollY + bottom))
    }
    measure()
    window.addEventListener('resize', measure)
    return () => window.removeEventListener('resize', measure)
  }, [active])
  useEffect(() => { saveViewPreference(view) }, [view])
  useEffect(() => {
    // KeepAlive 隐藏期间不消费其他页面的 URL；返回相同地址时保留三栏筛选。
    if (!active || !location.pathname.endsWith('/sales-detail-v2') || appliedSearch.current === location.search) return
    appliedSearch.current = location.search
    setDates(initial.dates); setKind(initial.kind); setSelection(initial.selection); setKeywordDraft(''); setSupplierSearch('')
  }, [active, initial, location.pathname, location.search])
  useEffect(() => {
    if (composing) return
    const timer = window.setTimeout(() => {
      if (searchClear.current) { searchClear.current = false; return }
      setSelection(value => applyKeyword(value, keywordDraft))
    }, 300)
    return () => window.clearTimeout(timer)
  }, [keywordDraft, composing])
  useEffect(() => {
    // 大图预览打开时 Esc 只关闭预览，不连带收起已展开的商品表。
    const handle = (event: KeyboardEvent) => { if (event.key === 'Escape' && previewCode === null) setExpanded(null) }
    window.addEventListener('keydown', handle)
    return () => window.removeEventListener('keydown', handle)
  }, [previewCode])
  // 销售明细和商品搜索共用全部关联分店范围。
  const branches = useMemo(() => access.visibleStoreCodes() ?? undefined, [access])
  const allowed = !!currentUser && (branches === undefined || branches.length > 0)
  const period = useMemo(() => reportPeriod(dates), [dates])
  const query: SalesDetailQuery = { ...period, kind, branchCodes: branches, selectedBranchCode: selection.branch,
    selectedSupplierCode: selection.supplier, selectedSupplierCodes: selection.supplierCodes.length ? selection.supplierCodes : undefined,
    supplierCategoryGuids: selection.supplierCategoryGuids.length ? selection.supplierCategoryGuids : undefined,
    warehouseCategoryGuids: selection.warehouseCategoryGuids.length ? selection.warehouseCategoryGuids : undefined,
    selectedProductCode: selection.product, search: selection.keyword || undefined,
    pageIndex: selection.page, pageSize: selection.pageSize }
  useEffect(() => {
    const controller = new AbortController()
    setCategoryGroups(undefined)
    setCategoryLoading(true); setCategoryError(undefined)
    const selectedCategoryGuids = kind === 'australia' ? selection.supplierCategoryGuids : selection.warehouseCategoryGuids
    if (exceedsSalesDetailSelectionLimit(selectedCategoryGuids, MAX_CATEGORY_SELECTIONS)) {
      setCategoryError(text(`已选择 ${selectedCategoryGuids.length} 个分类，超过 ${MAX_CATEGORY_SELECTIONS} 项上限，请减少选择后重试`, `You selected ${selectedCategoryGuids.length} categories, exceeding the ${MAX_CATEGORY_SELECTIONS}-item limit. Reduce the selection and try again.`))
    }
    if (exceedsSalesDetailSelectionLimit(selection.supplierCodes, MAX_SUPPLIER_SELECTIONS)) {
      setCategoryGroups([])
      setCategoryLoading(false)
      setCategoryError(text(`供应商分类最多支持 ${MAX_SUPPLIER_SELECTIONS} 个供应商，请减少选择后重试`, `Supplier categories support up to ${MAX_SUPPLIER_SELECTIONS} suppliers. Reduce the selection and try again.`))
      return () => controller.abort()
    }
    fetchSalesDetailCategoryOptions(kind, selection.supplierCodes, controller.signal)
      .then(groups => {
        if (controller.signal.aborted) return
        const count = groups.reduce((total, group) => total + group.options.length, 0)
        setCategoryGroups(groups)
        if (count > MAX_CATEGORY_SELECTIONS) setCategoryError(text(`分类选项共 ${count} 项，超过 ${MAX_CATEGORY_SELECTIONS} 项上限，请缩小供应商范围`, `There are ${count} category options, exceeding the ${MAX_CATEGORY_SELECTIONS}-item limit. Reduce the supplier scope.`))
      })
      .catch(error => { if (!controller.signal.aborted) { setCategoryError(error instanceof Error ? error.message : '分类选项加载失败 / Failed to load categories') } })
      .finally(() => { if (!controller.signal.aborted) setCategoryLoading(false) })
    return () => controller.abort()
  }, [kind, selection.supplierCodes])
  useEffect(() => {
    if (!categoryGroups) return
    const valid = new Set(categoryGroups.flatMap(group => group.options.map(option => option.guid)))
    setSelection(value => {
      const supplierCategoryGuids = value.supplierCategoryGuids.filter(guid => valid.has(guid))
      const warehouseCategoryGuids = value.warehouseCategoryGuids.filter(guid => valid.has(guid))
      return supplierCategoryGuids.length === value.supplierCategoryGuids.length && warehouseCategoryGuids.length === value.warehouseCategoryGuids.length
        ? value : { ...value, supplierCategoryGuids, warehouseCategoryGuids, page: 1 }
    })
  }, [categoryGroups])
  // 分页也读取四栏同一次快照，避免供应商归属变更后将新商品页拼到旧汇总上。
  const bundleKey = JSON.stringify([currentUser?.userGUID, branches, query])
  const bundle = useReportQuery<SalesDetailReport>(
    `sales-detail:bundle:${bundleKey}`,
    signal => fetchSalesDetailReport(query, signal),
    { active, enabled: allowed, refresh: bundleRefresh, metricId: 'sales-detail-whole-page' },
  )
  const suppliers = projectSection(bundle, 'suppliers')
  const stores = projectSection(bundle, 'branches')
  const summary = projectSection(bundle, 'summary')
  const products = projectSection(bundle, 'products')
  const loading = bundle.loading
  const retrySection = (_section: ReportSection) => setBundleRefresh(value => value + 1)
  const refreshAll = () => setBundleRefresh(value => value + 1)
  // 顶部销量与均价使用服务端全量筛选汇总，不能由当前商品页或各行均价推算。
  const total = summary.data?.summary ?? summary.data?.rows[0]
  const pageTotal: MetricRow | undefined = products.data ? products.data.summary ?? sumProductPage(products.data.rows) : undefined
  useEffect(() => {
    if (products.data && selection.page > Math.max(1, Math.ceil(products.data.total / selection.pageSize)))
      setSelection(value => ({ ...value, page: Math.max(1, Math.ceil(products.data!.total / value.pageSize)) }))
  }, [products.data, selection.page, selection.pageSize])
  // 未开启同期对比时只有本期可看，商品表固定为单行。
  const compareView: CompareView = dates.compare ? view.compareView : 'current'

  const labels: Record<MetricKey, string> = { revenue: text('营业额', 'Revenue'), grossProfit: text('毛利额', 'Gross profit'), grossMarginRate: text('毛利率', 'Margin'),
    quantity: text('数量', 'Quantity'),
    averageUnitPrice: text('均价', 'Unit price'), share: text('营业额占比', 'Revenue share'), chinaShare: text('中国货占比', 'China share') }
  const previous: Record<MetricKey, keyof MetricRow> = { revenue: 'compareRevenue', grossProfit: 'compareGrossProfit', grossMarginRate: 'compareGrossMarginRate',
    quantity: 'compareQuantity', averageUnitPrice: 'compareAverageUnitPrice', share: 'compareShare', chinaShare: 'compareChinaShare' }
  const metrics = (panel: PanelKey): (MetricKey | 'growth')[] => {
    const sales: MetricKey[] = ['revenue', 'quantity', 'averageUnitPrice']
    const profit: MetricKey[] = ['grossProfit', 'grossMarginRate']
    const shares: MetricKey[] = panel === 'suppliers' ? ['share', ...(kind === 'china' ? ['chinaShare' as const] : [])] : []
    // 表头与数据共用列顺序，毛利额和毛利率固定放在增长率之后。
    return [...sales, ...shares, 'growth', ...profit]
  }
  const metricLabel = (panel: PanelKey | 'summary', field: MetricKey) => panel !== 'products' && field === 'quantity'
    ? text('商品数量', 'Product quantity')
    : panel !== 'products' && field === 'averageUnitPrice'
      ? text('商品均价', 'Average product price')
      : labels[field]
  const formatOf = (field: MetricKey) => field.includes('Share') || field === 'share' || field === 'grossMarginRate' ? 'rate' as const : field === 'quantity' ? 'integer' as const : 'money' as const
  const isCost = (field: MetricKey) => field === 'grossMarginRate' || field === 'grossProfit'
  const metric = (row: MetricRow, field: MetricKey) => <MetricPair current={row[field]} previous={row[previous[field]] ?? null}
    compare={dates.compare} revenue={row.revenue} compareRevenue={row.compareRevenue} costMetric={isCost(field)} format={formatOf(field)} />
  // 左右对比的同期列：同期值放在主位置显示，“成本待补全”改按同期营业额判断。
  const previousMetric = (row: MetricRow, field: MetricKey) => <MetricPair current={row[previous[field]] ?? null} compare={false}
    revenue={row.compareRevenue ?? undefined} costMetric={isCost(field)} format={formatOf(field)} />
  const pick = (dimension: 'supplier' | 'branch' | 'product', row: SalesDetailRow) => {
    selectedNames.current[`${dimension}:${row.code}`] = row.name
    setSelection(value => selectDimension(value, dimension, row.code))
  }
  const selectedName = (dimension: 'supplier' | 'branch' | 'product') => {
    if (dimension === 'supplier') {
      const codes = selection.supplierCodes
      if (!codes.length) return undefined
      return codes.map(code => selectedNames.current[`supplier:${code}`] || code).join('、')
    }
    const code = selection[dimension]
    return code ? selectedNames.current[`${dimension}:${code}`] || code : undefined
  }
  const clear = () => { setSelection({ ...emptySelection, pageSize: selection.pageSize }); setKeywordDraft(''); setSupplierSearch('') }
  const switchKind = (value: typeof kind) => {
    if (value === kind) return
    setKind(value); clear()
    // 类别切换会清空三维筛选，日期与对比设置保持；刷新页面仍回到当前类别。
    const params = new URLSearchParams({ kind: value, startDate: dates.startDate, endDate: dates.endDate,
      compare: String(dates.compare), compareMode: dates.compareMode })
    appliedSearch.current = `?${params}`
    navigate({ pathname: location.pathname, search: appliedSearch.current }, { replace: true })
  }
  const toggleRail = () => setView(value => ({ ...value, railCollapsed: !value.railCollapsed }))
  const sortedRows = (panel: RailPanel, rows: SalesDetailRow[]) => [...rows].sort((a, b) => {
    const { key, ascending } = sorts[panel]
    if (a[key] == null) return b[key] == null ? a.code.localeCompare(b.code) : 1
    if (b[key] == null) return -1
    return (a[key]! - b[key]!) * (ascending ? 1 : -1) || a.code.localeCompare(b.code)
  })
  const supplierRows = sortedRows('suppliers', (suppliers.data?.rows ?? []).filter(row => `${row.name} ${row.code}`.toLowerCase().includes(supplierSearch.trim().toLowerCase())))
  const supplierOptions = (suppliers.data?.rows ?? []).map(row => ({ value: row.code, label: `${row.name || row.code} · ${row.code}` }))
  const supplierCategoryTree = buildSalesDetailSupplierCategoryTree(categoryGroups ?? [], code =>
    supplierOptions.find(option => option.value === code)?.label || code || text('供应商分类', 'Supplier categories'))
  // 复用分类父子关系，仓库树直接从分类根节点展示，不添加供应商分组。
  const warehouseCategoryTree = supplierCategoryTree.flatMap(group => group.children ?? [])
  const changeCategories = (values: string[]) => {
    if (exceedsSalesDetailSelectionLimit(values, MAX_CATEGORY_SELECTIONS)) {
      message.warning(text(`最多选择 ${MAX_CATEGORY_SELECTIONS} 个分类`, `Select up to ${MAX_CATEGORY_SELECTIONS} categories.`))
      return
    }
    setSelection(value => kind === 'australia' ? ({ ...value, supplierCategoryGuids: values, page: 1 }) : ({ ...value, warehouseCategoryGuids: values, page: 1 }))
  }
  const storeRows = sortedRows('branches', stores.data?.rows ?? [])
  // 后端保证全量分页顺序；前端再排序当前页，兼容缓存或旧接口返回的非确定顺序。
  const productRows = [...(products.data?.rows ?? [])].sort((left, right) => right.quantity - left.quantity
    || (right.compareQuantity ?? 0) - (left.compareQuantity ?? 0)
    || left.code.localeCompare(right.code))
  // COS 上按原文件名换过的图带版本号，绕开缩略图 30 天强缓存；没换过的图地址不变。
  const imageVersion = useProductImageVersions(productRows.map(row => row.productImage))
  // 预览组只收本页能正常显示的图片，按表格顺序排列，预览中左右键即可逐个翻看本页商品。
  const previewRows = productRows.filter(row => row.productImage && !brokenImages.has(row.productImage))
  const previewIndex = previewCode === null ? -1 : previewRows.findIndex(row => row.code === previewCode)
  const markImageBroken = (url: string) => setBrokenImages(value => value.has(url) ? value : new Set(value).add(url))
  // 缩略图处理失败（桶未开通图片处理、格式不支持等）时退回原图再试一次，原图也失败才换成占位符。
  const handleThumbnailError = (image: HTMLImageElement, original: string) => {
    // 退回的原图同样带版本号，避免换过图后退回到浏览器缓存里的旧原图。
    const originalSrc = toProductImagePreviewUrl(original, imageVersion(original))
    if (image.dataset.fallback !== 'original' && image.getAttribute('src') !== originalSrc) {
      image.dataset.fallback = 'original'
      image.src = originalSrc
      return
    }
    markImageBroken(original)
  }
  const exportUnavailable = !allowed || products.loading || !products.data?.rows.length || exporting
    || composing || normalizeKeyword(keywordDraft) !== selection.keyword
  const runExport = async () => {
    if (!products.data || exportAbort.current || exportUnavailable) return
    const controller = new AbortController()
    exportAbort.current = controller
    setExporting(true)
    setExportFinalizing(false)
    setExportProgress(text('正在准备导出…', 'Preparing export…'))
    try {
      const { exportSalesDetailProducts } = await import('./export')
      const result = await exportSalesDetailProducts(query, { ...products.data, rows: productRows }, {
        compare: dates.compare, english: text('zh', 'en') === 'en', startDate: dates.startDate, endDate: dates.endDate,
        signal: controller.signal, onProgress: setExportProgress, onFinalize: () => setExportFinalizing(true),
      })
      message.success(text(`已导出 ${result.count} 件商品${result.failedImages ? `，${result.failedImages} 张图片读取失败` : ''}`,
        `Exported ${result.count} products${result.failedImages ? `; ${result.failedImages} images unavailable` : ''}`))
    } catch (error) {
      if (!controller.signal.aborted) message.error(error instanceof Error ? error.message : text('导出失败', 'Export failed'))
    } finally {
      if (exportAbort.current === controller) exportAbort.current = null
      setExporting(false)
      setExportFinalizing(false)
      setExportProgress('')
    }
  }

  // ---------- 供应商 / 分店：左栏单行紧凑表，展开后显示全部指标 ----------
  const dimensionOf = (panel: RailPanel) => panel === 'suppliers' ? 'supplier' as const : 'branch' as const
  const toggleSort = (panel: RailPanel, field: MetricKey) => setSorts(value => ({ ...value, [panel]: { key: field, ascending: value[panel].key === field ? !value[panel].ascending : false } }))
  const sortMark = (panel: RailPanel, field: MetricKey) => sorts[panel].key === field ? sorts[panel].ascending ? '↑' : '↓' : '↕'
  const ariaSort = (panel: RailPanel, field: MetricKey) => sorts[panel].key === field ? sorts[panel].ascending ? 'ascending' as const : 'descending' as const : undefined
  const isDimensionSelected = (dimension: 'supplier' | 'branch', code: string) => dimension === 'supplier'
    ? selection.supplierCodes.includes(code)
    : selection.branch === code
  const shareTip = (field: MetricKey) => field === 'share' ? text(kind === 'china' ? '分母：所选分店的国内供应商全量营业额，不受商品选择影响' : '分母：所选分店的全部营业额，不受商品选择影响', 'Denominator: all revenue in the selected store scope; not narrowed by product selection')
    : field === 'chinaShare' ? text('分母：所选分店的全部营业额', 'Denominator: all revenue in the selected store scope') : undefined
  const railNameButton = (panel: RailPanel, row: SalesDetailRow, index: number) => {
    const dimension = dimensionOf(panel)
    const selected = isDimensionSelected(dimension, row.code)
    return <button data-code={row.code} aria-pressed={selected} className={styles.nameButton}
      onClick={() => pick(dimension, row)} title={`${row.name} · ${row.code}`}>
      <span className={styles.rank}>{String(index + 1).padStart(2, '0')}</span>
      <span className={styles.railName}><strong>{row.name || row.code}</strong>{panel === 'suppliers' && <small>{row.code}</small>}</span>
    </button>
  }
  const railTable = (panel: RailPanel, rows: SalesDetailRow[]) => {
    const dimension = dimensionOf(panel)
    const sortable = (field: MetricKey, label: string, title?: string) => <th aria-sort={ariaSort(panel, field)}>
      <button type="button" title={title} onClick={() => toggleSort(panel, field)}>{label} {sortMark(panel, field)}</button></th>
    return <table className={`${styles.table} ${styles.railTable}`}>
      <colgroup><col /><col className={styles.railMoneyCol} /><col className={styles.railGrowthCol} /><col className={styles.railQuantityCol} /></colgroup>
      <thead><tr><th>{panel === 'suppliers' ? text('供应商 / 编码', 'Supplier / Code') : text('分店名称', 'Store')}</th>
        {sortable('revenue', labels.revenue)}<th>{text('增长率', 'Growth')}</th>{sortable('quantity', labels.quantity, metricLabel(panel, 'quantity'))}</tr></thead>
      <tbody>{rows.map((row, index) => <tr key={row.code} className={isDimensionSelected(dimension, row.code) ? styles.selected : ''}>
        <td>{railNameButton(panel, row, index)}</td>
        <td data-metric="revenue">{metric(row, 'revenue')}</td>
        <td data-metric="growth"><GrowthCell current={row.revenue} previous={row.compareRevenue} compare={dates.compare} /></td>
        <td data-metric="quantity">{metric(row, 'quantity')}</td>
      </tr>)}</tbody></table>
  }
  const fullTable = (panel: RailPanel, rows: SalesDetailRow[]) => {
    const dimension = dimensionOf(panel)
    return <table className={`${styles.table} ${styles.fullTable}`}><colgroup><col className={styles.fullNameCol} /></colgroup>
      <thead><tr><th>{panel === 'suppliers' ? text('供应商 / 编码', 'Supplier / Code') : text('分店名称', 'Store')}</th>
        {metrics(panel).map(field => field === 'growth' ? <th key={field}>{text('增长率', 'Growth')}</th> : <th key={field} aria-sort={ariaSort(panel, field)}>
          <Tooltip title={shareTip(field)}><button type="button" onClick={() => toggleSort(panel, field)}>{metricLabel(panel, field)} {sortMark(panel, field)}</button></Tooltip></th>)}</tr></thead>
      <tbody>{rows.map((row, index) => <tr key={row.code} className={isDimensionSelected(dimension, row.code) ? styles.selected : ''}>
        <td>{railNameButton(panel, row, index)}</td>{metrics(panel).map(field => <td key={field} data-metric={field}>{field === 'growth'
          ? <GrowthCell current={row.revenue} previous={row.compareRevenue} compare={dates.compare} />
          : metric(row, field)}</td>)}
      </tr>)}</tbody></table>
  }

  // ---------- 商品明细：上下对比 / 仅本期 / 左右对比 ----------
  const productFields = metrics('products') as (ProductMetric | 'growth')[]
  const productHead = (field: ProductMetric) => field === 'quantity'
    ? <Tooltip title={text('商品按本期数量降序排列', 'Sorted by current quantity, descending')}><span className={styles.sortedHead}>{labels.quantity} ↓</span></Tooltip>
    : labels[field]
  const productTable = (rows: SalesDetailRow[]) => {
    const side = compareView === 'side'
    const rowStart = (selection.page - 1) * selection.pageSize
    const cells = (row: MetricRow) => productFields.map(field => field === 'growth'
      ? <td key={field} data-metric="growth" className={side ? styles.groupStart : undefined}><GrowthCell current={row.revenue} previous={row.compareRevenue} compare={dates.compare} /></td>
      : side
        ? <Fragment key={field}><td data-metric={field} className={styles.groupStart}>{metric(row, field)}</td>
          <td data-metric={field} className={styles.previousCell}>{previousMetric(row, field)}</td></Fragment>
        : <td key={field} data-metric={field} className={field === 'grossProfit' ? styles.groupStart : undefined}>{metric(row, field)}</td>)
    const totalRow = (key: 'page' | 'all', label: string, note: string, totals: MetricRow) => <tr key={key} className={key === 'all' ? styles.allTotal : undefined}>
      <td><span className={styles.totalLabel}><strong>{label}</strong><small>{note}</small></span></td>{cells(totals)}</tr>
    const listFiltered = !!(selection.supplierCodes.length || selection.branch || selection.keyword)
    return <table className={`${styles.table} ${styles.productTable}`} data-view={compareView}>
      <colgroup><col />{productFields.map(field => field === 'growth' ? <col key={field} style={{ width: side ? GROWTH_WIDTH.side : GROWTH_WIDTH.stack }} />
        : side ? <Fragment key={field}><col style={{ width: SIDE_WIDTHS[field][0] }} /><col style={{ width: SIDE_WIDTHS[field][1] }} /></Fragment>
          : <col key={field} style={{ width: STACK_WIDTHS[field] }} />)}</colgroup>
      <thead>{side ? <>
        <tr><th rowSpan={2}>{text('货号 / 商品名称', 'Item / Product')}</th>{productFields.map(field => field === 'growth'
          ? <th key={field} rowSpan={2} className={styles.groupStart}>{text('增长率', 'Growth')}</th>
          : <th key={field} colSpan={2} className={`${styles.groupHead} ${styles.groupStart}`}>{productHead(field)}</th>)}</tr>
        <tr>{productFields.map(field => field !== 'growth' && <Fragment key={field}>
          <th className={styles.groupStart}>{text('本期', 'Current')}</th><th>{text('同期', 'Previous')}</th></Fragment>)}</tr>
      </> : <tr><th>{text('货号 / 商品名称', 'Item / Product')}</th>{productFields.map(field => <th key={field} className={field === 'grossProfit' ? styles.groupStart : undefined}>
        {field === 'growth' ? text('增长率', 'Growth') : productHead(field)}</th>)}</tr>}</thead>
      <tbody>{rows.map((row, index) => <tr key={row.code} className={selection.product === row.code ? styles.selected : ''}>
        <td><div className={styles.productName}>
          <span className={styles.rank}>{rowStart + index + 1}</span>
          {/* 缩略图是独立按钮：点击看大图，不触发商品联动筛选；读取失败的图片退回占位符且不可预览。
              表格里只加载 COS 缩略图（单张约 2KB），点开大图才加载原图；key 随原图地址变化，重置退回原图的标记。 */}
          {row.productImage && !brokenImages.has(row.productImage)
            ? <button type="button" className={styles.thumbButton} onClick={() => setPreviewCode(row.code)}
              title={text('查看大图', 'View larger image')} aria-label={text(`查看${row.name || row.code}的大图`, `View larger image of ${row.name || row.code}`)}>
              <img key={`${row.productImage}|${imageVersion(row.productImage) ?? ''}`}
                src={toProductThumbnailUrl(row.productImage, undefined, imageVersion(row.productImage))} alt="" loading="lazy" decoding="async"
                onError={event => handleThumbnailError(event.currentTarget, row.productImage!)} /></button>
            : <span className={styles.imagePlaceholder}>▦</span>}
          {/* 点击商品与供应商、分店一样作为联动筛选；分店分布抽屉改由行尾图标打开。 */}
          <button data-code={row.code} aria-pressed={selection.product === row.code} className={styles.nameButton}
            onClick={() => pick('product', row)} title={`${row.name} · ${row.code}`}>
            <span className={styles.nameText}><small>{row.itemNumber || row.code}</small><strong>{row.name || row.code}</strong></span>
          </button>
          <Tooltip title={text('查看分店分布', 'Store breakdown')}>
            <Button type="text" size="small" className={styles.drawerButton} icon={<ShopOutlined />}
              aria-label={text(`查看${row.name || row.code}的分店分布`, `Store breakdown for ${row.name || row.code}`)} onClick={() => setDrawerProduct(row)} />
          </Tooltip>
        </div></td>{cells(row)}
      </tr>)}</tbody>
      {pageTotal && <tfoot>
        {totalRow('page', text('本页合计', 'Page total'), text(`${rows.length} 件`, `${rows.length} items`), pageTotal)}
        {/* 展开后顶部汇总被遮住，补一行全量合计；选中商品时汇总只含该商品，不能冒充列表合计。 */}
        {expanded === 'products' && !selection.product && total && totalRow('all', listFiltered ? text('当前筛选合计', 'Filtered total') : text('全部合计', 'All products'),
          text(`${(products.data?.total ?? 0).toLocaleString('en-AU')} 件`, `${(products.data?.total ?? 0).toLocaleString('en-AU')} items`), total)}
      </tfoot>}
    </table>
  }

  const hasFilters = !!(selection.supplierCodes.length || selection.supplierCategoryGuids.length || selection.warehouseCategoryGuids.length || selection.branch || selection.product || selection.keyword)
  const railScope = [selectedName('supplier'), selectedName('product')].filter(Boolean).join(' · ')
  const scopeLine = [dates.startDate === dates.endDate ? dates.startDate : `${dates.startDate} — ${dates.endDate}`,
    kind === 'china' ? text('国内供应商', 'China suppliers') : text('澳洲供应商', 'Australian suppliers'),
    [selectedName('supplier'), selectedName('branch'), selectedName('product'), selection.keyword && `“${selection.keyword}”`].filter(Boolean).join(' · ')
      || text('全部供应商 / 全部分店', 'All suppliers / stores')].join(' · ')
  const legend = !dates.compare ? text('未开启同期对比', 'Comparison off')
    : compareView === 'side' ? text('本期在左 · 同期在右', 'Current left · Previous right')
      : compareView === 'current' ? text('商品表仅显示本期', 'Products show current only')
        : text('本期在上 · 同期在下', 'Current above · Previous below')
  const firstRow = products.data?.total ? (selection.page - 1) * selection.pageSize + 1 : 0
  const lastRow = Math.min(products.data?.total ?? 0, selection.page * selection.pageSize)
  const railLabel = view.railCollapsed ? text('展开供应商与分店栏', 'Show suppliers and stores') : text('收起供应商与分店栏', 'Hide suppliers and stores')
  const resizer = <div role="separator" tabIndex={0} aria-orientation="vertical" aria-label={text('调整左栏宽度', 'Resize side panels')}
    aria-valuemin={20} aria-valuemax={46} aria-valuenow={Math.round(railWidth)} className={styles.resizer}
    onDoubleClick={() => setRailWidth(RAIL_DEFAULT_WIDTH)}
    onKeyDown={event => { if (event.key === 'ArrowLeft' || event.key === 'ArrowRight') { event.preventDefault(); setRailWidth(value => clampRailWidth(value + (event.key === 'ArrowLeft' ? -2 : 2))) } }}
    onPointerDown={event => { event.currentTarget.setPointerCapture(event.pointerId); drag.current = { startX: event.clientX, railWidth, width: workspace.current?.clientWidth || 1 } }}
    onPointerMove={event => { if (drag.current) setRailWidth(clampRailWidth(drag.current.railWidth + (event.clientX - drag.current.startX) / drag.current.width * 100)) }}
    onPointerUp={() => { drag.current = undefined }} onPointerCancel={() => { drag.current = undefined }} />

  return <main ref={page} className={styles.page} data-report="sales-detail"
    style={pageOffset === undefined ? undefined : { '--page-offset': `${pageOffset}px` } as CSSProperties}>
    <div className={styles.topBar}>
      <div className={styles.titleGroup}>
        <h1 title={text('供应商、分店与商品双向联动，从任意一栏开始分析。', 'Explore from any supplier, store or product.')}>{text('销售明细', 'Sales detail')}</h1>
        <div role="tablist" aria-label={text('供应商类别', 'Supplier type')} className={styles.tabs}>
          {(['australia', 'china'] as const).map(value => <button role="tab" key={value} aria-selected={kind === value} onClick={() => switchKind(value)}>{value === 'china' ? text('HB 仓库 · 国内供应商', 'HB warehouse · China') : text('澳洲供应商', 'Australian suppliers')}</button>)}
        </div>
      </div>
      <div className={styles.headerControls}>
        <Select mode="multiple" allowClear showSearch optionFilterProp="label" maxTagCount="responsive" size="small"
          aria-label={text('选择供应商', 'Select suppliers')} placeholder={text('供应商（可多选）', 'Suppliers (multiple)')}
          style={{ minWidth: 210, maxWidth: 340 }} value={selection.supplierCodes} options={supplierOptions}
          onChange={values => {
            if (exceedsSalesDetailSelectionLimit(values, MAX_SUPPLIER_SELECTIONS)) {
              message.warning(text(`最多选择 ${MAX_SUPPLIER_SELECTIONS} 个供应商`, `Select up to ${MAX_SUPPLIER_SELECTIONS} suppliers.`))
              return
            }
            setSelection(value => ({ ...value, supplier: values[0], supplierCodes: values, supplierCategoryGuids: [], page: 1 }))
          }} />
        {kind === 'australia' ? <TreeSelect treeCheckable multiple allowClear showSearch treeDefaultExpandAll
          showCheckedStrategy={TreeSelect.SHOW_PARENT} maxTagCount={0} size="small"
          maxTagPlaceholder={() => text(`已选 ${selection.supplierCategoryGuids.length} 项`, `${selection.supplierCategoryGuids.length} selected`)}
          aria-label={text('选择供应商分类', 'Select supplier categories')}
          placeholder={text('供应商分类（可多选）', 'Supplier categories (multiple)')}
          style={{ minWidth: 220, maxWidth: 360 }} popupClassName={styles.categoryTreePopup}
          loading={categoryLoading} disabled={!selection.supplierCodes.length}
          value={selection.supplierCategoryGuids} treeData={supplierCategoryTree}
          filterTreeNode={(input, node) => ((node as SalesDetailCategoryTreeNode).searchText || '').toLocaleLowerCase().includes(input.trim().toLocaleLowerCase())}
          notFoundContent={categoryError || text('暂无分类', 'No categories')}
          popupRender={menu => <><div className={styles.categoryTreeHint}>{text('按所选供应商分类筛选；选择父级包含子分类。', 'Filter by the selected suppliers’ categories. A parent includes its subcategories.')}</div>{menu}</>}
          onChange={values => changeCategories(Array.isArray(values) ? values.map(String) : [])} />
          : <TreeSelect treeCheckable multiple allowClear showSearch
            showCheckedStrategy={TreeSelect.SHOW_PARENT} maxTagCount={0} size="small"
            maxTagPlaceholder={() => text(`已选 ${selection.warehouseCategoryGuids.length} 项`, `${selection.warehouseCategoryGuids.length} selected`)}
            aria-label={text('选择仓库分类', 'Select warehouse categories')}
            placeholder={text('仓库分类（可多选）', 'Warehouse categories (multiple)')}
            style={{ minWidth: 220, maxWidth: 360 }} popupClassName={styles.categoryTreePopup} loading={categoryLoading}
            value={selection.warehouseCategoryGuids} treeData={warehouseCategoryTree}
            filterTreeNode={(input, node) => ((node as SalesDetailCategoryTreeNode).searchText || '').toLocaleLowerCase().includes(input.trim().toLocaleLowerCase())}
            notFoundContent={categoryError || text('暂无分类', 'No categories')}
            popupRender={menu => <><div className={styles.categoryTreeHint}>{text('选择父级包含子分类，可展开选择具体分类。', 'A parent includes its subcategories. Expand to select individual categories.')}</div>{menu}</>}
            onChange={values => changeCategories(Array.isArray(values) ? values.map(String) : [])} />}
        <ReportControls value={dates} onChange={value => { setDates(value); setSelection(current => ({ ...current, page: 1 })) }} onRefresh={refreshAll} loading={loading} />
        {access.canViewSalesData && <Button onClick={() => navigate(`/executive-sales-intelligence/overview?branch=${encodeURIComponent(selection.branch ?? '')}&startDate=${dates.startDate}&endDate=${dates.endDate}&compare=${dates.compare}&compareMode=${dates.compareMode}`)}>{text('营业额报告', 'Revenue report')}</Button>}
      </div>
    </div>
    {categoryError && <Alert type="warning" showIcon message={categoryError} />}
    {!allowed && <Alert type="warning" message={text('当前账号没有可查询的分店范围', 'No stores are available for this account')} />}
    {summary.error && <Alert type="warning" message={summary.error} action={<Button onClick={() => retrySection('summary')}>{text('重试汇总', 'Retry totals')}</Button>} />}
    {bundle.data && bundle.snapshot?.statisticMessage && <Alert type="warning" showIcon message={bundle.snapshot.statisticMessage} />}
    <section className={styles.summary} aria-label={text('全量筛选汇总', 'All matching totals')} data-testid="detail-summary"
      style={{ '--previous-label': JSON.stringify(text('同期 ', 'Prev ')) } as CSSProperties}>
      {(['revenue', 'quantity', 'averageUnitPrice', 'grossProfit', 'grossMarginRate'] as MetricKey[]).map(field => {
        const current = total?.[field]
        // 毛利受成本补全影响，汇总条只给销售类指标显示增长率。
        const showGrowth = total && dates.compare && current != null && (field === 'revenue' || field === 'quantity' || field === 'averageUnitPrice')
        // 增长率放在标签行，数值行只放本期与同期，汇总条保持单行不折行。
        return <div key={field} className={styles.kpi}>
          <span className={styles.kpiLabel}>{field === 'revenue' ? text(hasFilters ? '当前筛选营业额' : '当前标签营业额', 'Matching revenue') : metricLabel('summary', field)}
            {showGrowth && <span className={styles.kpiGrowth}><GrowthCell current={current} previous={total[previous[field]] ?? null} compare /></span>}</span>
          <div className={styles.kpiValue}>{total ? metric(total, field) : <strong className={styles.pendingTotal}>—</strong>}</div>
        </div>
      })}
      <div className={styles.scopeBlock}>
        <div className={styles.chips}><span>{text('筛选', 'Filters')}</span>
          {selection.supplierCodes.length > 0 && <Tag closable onClose={() => setSelection(value => ({ ...value, supplier: undefined, supplierCodes: [], supplierCategoryGuids: [], page: 1 }))}>{text('供应商', 'Suppliers')}: {selection.supplierCodes.length}</Tag>}
          {selection.supplierCategoryGuids.length > 0 && <Tag closable onClose={() => setSelection(value => ({ ...value, supplierCategoryGuids: [], page: 1 }))}>{text('澳洲分类', 'AU categories')}: {selection.supplierCategoryGuids.length}</Tag>}
          {selection.warehouseCategoryGuids.length > 0 && <Tag closable onClose={() => setSelection(value => ({ ...value, warehouseCategoryGuids: [], page: 1 }))}>{text('仓库分类', 'Warehouse categories')}: {selection.warehouseCategoryGuids.length}</Tag>}
          {(['branch', 'product'] as const).map(dimension => selection[dimension] && <Tag closable key={dimension} onClose={() => setSelection(value => ({ ...value, [dimension]: undefined, page: dimension === 'product' ? value.page : 1 }))}>
            {text(dimension === 'branch' ? '分店' : '商品', dimension)}: {selectedName(dimension)}</Tag>)}
          {selection.keyword && <Tag closable onClose={() => { searchClear.current = true; setKeywordDraft(''); setSelection(value => ({ ...value, keyword: '', page: 1 })) }}>{text('关键字', 'Keyword')}: {selection.keyword}</Tag>}
          {hasFilters ? <Button type="link" size="small" onClick={clear}>{text('清除全部', 'Clear all')}</Button> : <span className={styles.chipsAll}>{text('全部供应商 · 全部分店 · 全部商品', 'All suppliers · stores · products')}</span>}
        </div>
        <div className={styles.legend} title={[legend, dates.compare ? `${text('同期', 'Previous')} ${period.compareStartDate} — ${period.compareEndDate}` : '', 'AUD'].filter(Boolean).join(' | ')}><span>{legend}</span>
          {dates.compare && <><span aria-hidden>|</span><span>{text('同期', 'Previous')} {period.compareStartDate} — {period.compareEndDate}</span></>}
          <span aria-hidden>|</span><span>AUD</span>
          <Tooltip title={text('移动端报告同源统计 · 商品数量含退货抵减 · 商品均价 = 营业额 ÷ 商品数量（数量 ≤ 0 时显示 —）', 'Mobile report statistics · Product quantity is net of returns · Average product price = revenue ÷ product quantity (— when quantity ≤ 0)')}>
            <button type="button" className={styles.infoButton} aria-label={text('统计口径说明', 'Calculation notes')}><InfoCircleOutlined /></button>
          </Tooltip>
        </div>
      </div>
    </section>
    <div ref={workspace} className={`${styles.workspace} ${view.railCollapsed ? styles.railCollapsed : ''} ${expanded ? styles.hasExpanded : ''}`}
      style={{ '--rail-width': `${railWidth}%` } as CSSProperties}>
      {/* 左栏收起后只留一条竖向入口，商品明细吃满整行宽度。 */}
      <nav className={styles.railStrip} aria-label={text('供应商与分店（已收起）', 'Suppliers and stores (collapsed)')}>
        <Button type="text" size="small" icon={<MenuUnfoldOutlined />} aria-label={text('展开供应商与分店栏', 'Show suppliers and stores')} onClick={toggleRail} />
        <button type="button" className={styles.railStripItem} onClick={toggleRail}><span>01</span> {text('供应商', 'Suppliers')} · {selectedName('supplier') ?? text('全部', 'All')}</button>
        <button type="button" className={styles.railStripItem} onClick={toggleRail}><span>02</span> {text('分店', 'Stores')} · {selectedName('branch') ?? text('全部', 'All')}</button>
      </nav>
      <div className={styles.rail}>
        <Panel id="suppliers" title={text('供应商', 'Suppliers')} hint={text('点击供应商，联动分店与商品；再次点击取消', 'Select a supplier to filter stores and products; click again to clear')} count={suppliers.data ? supplierRows.length : undefined} query={suppliers}
          expanded={expanded === 'suppliers'} onExpand={() => setExpanded(value => value === 'suppliers' ? null : 'suppliers')} onRetry={() => retrySection('suppliers')}
          scope={expanded === 'suppliers' && <span className={styles.panelScope}>{scopeLine}</span>}
          filter={<Input size="small" prefix={<SearchOutlined />} allowClear placeholder={text('名称 / 编码', 'Name / code')} aria-label={text('搜索供应商', 'Search suppliers')} value={supplierSearch} onChange={event => setSupplierSearch(event.target.value)} />}>
          {expanded === 'suppliers' ? fullTable('suppliers', supplierRows) : railTable('suppliers', supplierRows)}</Panel>
        <Panel id="branches" title={text('分店表现', 'Store performance')} hint={text('点击分店，反查供应商与商品；再次点击取消', 'Select a store to filter suppliers and products; click again to clear')} count={stores.data?.total} query={stores}
          expanded={expanded === 'branches'} onExpand={() => setExpanded(value => value === 'branches' ? null : 'branches')} onRetry={() => retrySection('branches')}
          scope={expanded === 'branches' && <span className={styles.panelScope}>{scopeLine}</span>}
          filter={<span>{text('范围', 'Scope')} · <strong>{railScope || text('当前标签全部供应商', 'All suppliers in this tab')}</strong></span>}>
          {expanded === 'branches' ? fullTable('branches', storeRows) : railTable('branches', storeRows)}</Panel>
      </div>
      {resizer}
      <Panel id="products" title={text('商品明细', 'Product detail')} hint={text('全量关键字过滤；点击商品联动供应商与分店，再次点击取消', 'Search all products; select one to filter suppliers and stores')} count={products.data?.total} query={products}
        expanded={expanded === 'products'} onExpand={() => setExpanded(value => value === 'products' ? null : 'products')} onRetry={() => retrySection('products')}
        leading={expanded !== 'products' && <Tooltip title={railLabel}><Button size="small" className={styles.railToggle} icon={view.railCollapsed ? <MenuUnfoldOutlined /> : <MenuFoldOutlined />}
          aria-label={railLabel} aria-expanded={!view.railCollapsed} onClick={toggleRail} /></Tooltip>}
        scope={expanded === 'products' && <span className={styles.panelScope}>{scopeLine}</span>}
        action={<>
          <Segmented size="small" value={compareView} disabled={!dates.compare} aria-label={text('同期显示方式', 'Comparison layout')}
            onChange={value => setView(current => ({ ...current, compareView: value as CompareView }))}
            options={[{ value: 'stack', label: text('上下对比', 'Stacked'), title: text('本期在上、同期在下', 'Current above, previous below') },
              { value: 'current', label: text('仅本期', 'Current'), title: text('只看本期，行高最低', 'Current period only, densest rows') },
              { value: 'side', label: text('左右对比', 'Side by side'), title: text('本期与同期左右并排，适合宽屏或收起左栏', 'Current and previous side by side; best on wide screens') }]} />
          <Button size="small" icon={<DownloadOutlined />} disabled={exportUnavailable}
            aria-label={text('导出当前页商品明细 Excel', 'Export current product page to Excel')}
            onClick={() => { void runExport() }}>{text('导出本页 Excel', 'Export page Excel')}</Button></>}
        filter={<><Input prefix={<SearchOutlined />} value={keywordDraft} placeholder={text('名称 / 货号 / 条码 / 供应商', 'Name / item / barcode / supplier')} aria-label={text('商品关键字', 'Product keyword')}
          onChange={event => { searchClear.current = false; setKeywordDraft(event.target.value) }} onCompositionStart={() => setComposing(true)} onCompositionEnd={() => setComposing(false)} />
          {keywordDraft && <Button type="text" size="small" icon={<CloseOutlined />} aria-label={text('清除商品关键字', 'Clear product keyword')} onClick={() => { searchClear.current = true; setKeywordDraft(''); setSelection(value => ({ ...value, keyword: '', page: 1 })) }} />}</>}
        notice={exporting && <div className={styles.exportStatus} role="status"><span>{exportProgress}</span>
          {!exportFinalizing && <Button type="link" size="small" onClick={() => exportAbort.current?.abort()}>{text('取消', 'Cancel')}</Button>}</div>}
        footer={<div className={styles.productFooter}>
          <span className={styles.pageInfo}>{!!products.data?.total && <span>{text(`第 ${firstRow.toLocaleString('en-AU')}–${lastRow.toLocaleString('en-AU')} 件 · 共 ${products.data.total.toLocaleString('en-AU')} 件`,
            `${firstRow.toLocaleString('en-AU')}–${lastRow.toLocaleString('en-AU')} of ${products.data.total.toLocaleString('en-AU')}`)}</span>}
            <span>{summary.snapshot?.statisticUpdatedAt ? `${text('统计水位', 'Snapshot')} ${new Date(summary.snapshot.statisticUpdatedAt).toLocaleString()}` : text('按完整统计快照读取', 'Reading complete snapshots')}</span></span>
          <Pagination size="small" showSizeChanger showQuickJumper showLessItems pageSizeOptions={[10, 20, 50, 100, 200, MAX_PRODUCT_IMAGE_EXPORT_ROWS]} current={selection.page} pageSize={selection.pageSize} total={products.data?.total ?? 0} disabled={products.loading}
            onChange={(page, pageSize) => setSelection(value => ({ ...value, page: value.pageSize === pageSize ? page : 1, pageSize }))} />
        </div>}>{productTable(productRows)}</Panel>
    </div>
    <ProductBranchDrawer key={currentUser?.userGUID ?? 'anonymous'} open={active && allowed && !!drawerProduct}
      product={drawerProduct} baseQuery={query} onClose={() => setDrawerProduct(null)} />
    {/* 受控预览组：不渲染子元素，只由缩略图按钮打开；页脚显示序号、货号与商品名，方便翻看时对照。 */}
    <Image.PreviewGroup items={previewRows.map(row => ({ src: toProductImagePreviewUrl(row.productImage!, imageVersion(row.productImage)), alt: row.name || row.code }))}
      preview={{ visible: active && previewIndex >= 0, current: Math.max(previewIndex, 0),
        onVisibleChange: visible => { if (!visible) setPreviewCode(null) },
        onChange: current => setPreviewCode(previewRows[current]?.code ?? null),
        countRender: (current, total) => {
          const row = previewRows[current - 1]
          return <span className={styles.previewCaption}><span>{current} / {total}</span>
            {row && <><small>{row.itemNumber || row.code}</small><strong>{row.name || row.code}</strong></>}</span>
        } }} />
  </main>
}
