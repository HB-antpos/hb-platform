import { CloseOutlined, DownloadOutlined, DownOutlined, InfoCircleOutlined, ReloadOutlined, SearchOutlined, UpOutlined } from '@ant-design/icons'
import { Alert, Button, DatePicker, Dropdown, Image, Input, message, Pagination, Segmented, Tooltip } from 'antd'
import type { MenuProps, TableProps } from 'antd'
import type { ColumnsType } from 'antd/es/table'
import dayjs, { type Dayjs } from 'dayjs'
import { useKeepAliveContext } from 'keepalive-for-react'
import React, { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { useAuthStore } from '../../../store/auth'
import { MeasuredTable } from '../../../components/MeasuredTable'
import {
  getCompactSalesBoard,
  type CompactSalesBoard,
  type CompactSalesBoardChinaSupplier,
  type CompactSalesBoardProduct,
  type CompactSalesBoardRequest,
  type CompactSalesBoardStore,
  type DateRange,
} from '../../../services/salesDashboardService'
import styles from './styles.module.css'
import { MAX_REPORT_DAYS, quickDateSelection, type QuickRange as ReportQuickRange } from '../ReportWorkbench/logic'
import { MAX_PRODUCT_IMAGE_EXPORT_ROWS } from '../SalesDetailAnalysisV2/logic'
import { readCompactBoardCache, writeCompactBoardCache, type CompactBoardCacheEntry } from './cache'
import type { CompactBoardExportProgress } from './export'
import {
  buildPanelKeys,
  buildRequestKey,
  defaultProductSort,
  describeExportFilters,
  describeProductSort,
  formatShare,
  matchesSupplierSearch,
  pageRange,
  readStoredFlag,
  resolveProductSort,
  resolveSupplierToggle,
  shareOf,
  shouldHandleEscape,
  toAntdSortOrder,
  toggleSelection,
  writeStoredFlag,
  type BoardFilterState,
  type BoardSelection,
  type PanelKeys,
  type ProductSort,
} from './logic'

const { RangePicker } = DatePicker

type QuickRange = Exclude<ReportQuickRange, 'custom'>
type CacheState = 'cached' | 'fresh' | 'refreshing' | 'error'
type ExportMode = 'page' | 'all'

const quickRangeOptions: { label: string; value: QuickRange }[] = [
  { label: '今天', value: 'today' },
  { label: '昨天', value: 'yesterday' },
  { label: '本周', value: 'thisWeek' },
  { label: '上周', value: 'lastWeek' },
  { label: '本月', value: 'thisMonth' },
  { label: '上月', value: 'lastMonth' },
]

const compactCurrencyFormatter = new Intl.NumberFormat('en-AU', { style: 'currency', currency: 'AUD', maximumFractionDigits: 0 })
const priceFormatter = new Intl.NumberFormat('en-AU', { style: 'currency', currency: 'AUD', minimumFractionDigits: 2, maximumFractionDigits: 2 })
const integerFormatter = new Intl.NumberFormat('en-AU')
const compactBoardClientCacheMs = 30_000
// 中文注释：区间上限与销售明细共用（两年含闰日），后端同样按 731 天校验。
const maxSalesDateRangeDays = MAX_REPORT_DAYS
const keywordDebounceMs = 300
// 中文注释：每页上限 500 与带图导出上限一致，「导出本页」永远不会超出嵌图上限；服务端同样钳到 500。
const pageSizeOptions = [50, 100, 200, MAX_PRODUCT_IMAGE_EXPORT_ROWS]
const defaultPageSize = 50
const statsExpandedStorageKey = 'hbweb.compactSalesBoard.statsExpanded'
// 中文注释：后台外壳（顶栏 + 标签页）约 160px；头部只剩一行标题栏，统计移到底部统计条。
// 底部统计条的实际高度（折叠约 38px、展开约 220px）由 ResizeObserver 写入 --cb-footer-h，三栏表格随之伸缩、整页不出现滚动。
const tableBodyHeight = 'calc(100vh - 333px - var(--cb-footer-h, 38px))'
const productTableBodyHeight = 'calc(100vh - 377px - var(--cb-footer-h, 38px))'
// 中文注释：导出进行中，商品栏标题下多一条 36px 的进度条。
const productTableBodyHeightExporting = 'calc(100vh - 413px - var(--cb-footer-h, 38px))'
// 中文注释：按页面实际可用宽度（扣除左侧菜单后）切换紧凑列宽，1440 笔记本展开菜单时也能放下三栏。
const compactLayoutWidth = 1400
// 中文注释：供应商、分店两栏较窄，页面宽度不足 1600px 时占比只显示百分比，把宽度让给名称列。
const sidePanelShareBarWidth = 1600

const emptyBoard: CompactSalesBoard = {
  stores: [],
  chinaSuppliers: [],
  productDetails: { data: [], total: 0, pageIndex: 1, pageSize: defaultPageSize, scopeAmount: 0 },
  summary: { totalAmount: 0, totalQuantity: 0, productCount: 0, storeCount: 0, supplierCount: 0, overallAmount: 0, overallQuantity: 0 },
  fromCache: false,
}

// 中文注释：快捷区间与销售明细同一套规则（ISO 周、截止到今天）。
// 旧写法「本周/本月」包含未来日期，未来日期没有统计状态，服务端会判为未发布，看板整页不出数。
function resolveQuickRange(range: QuickRange): [Dayjs, Dayjs] {
  const selection = quickDateSelection(range)
  return [dayjs(selection.startDate), dayjs(selection.endDate)]
}

function isDisabledDate(date: Dayjs, info: { from?: Dayjs }) {
  return date.isAfter(dayjs(), 'day') || Boolean(info.from && Math.abs(date.diff(info.from, 'day')) >= maxSalesDateRangeDays)
}

function toDateRange(dateRange: [Dayjs, Dayjs]): DateRange {
  return { startDate: dateRange[0].format('YYYY-MM-DD'), endDate: dateRange[1].format('YYYY-MM-DD') }
}

/** 看板查询与「导出全部结果」共用同一组条件，保证导出的行与页面排序、筛选一致。 */
function buildBoardRequest(filterState: BoardFilterState, managedStoreCodes: string[] | undefined, forceRefresh: boolean): CompactSalesBoardRequest {
  // 中文注释：branchCodes 只传授权范围；页面选中的分店单独传，服务端据此让分店栏不被自身收窄。
  return {
    dateRange: filterState.dateRange,
    branchCodes: managedStoreCodes,
    selectedBranchCode: filterState.branch?.code,
    selectedChinaSupplierCode: filterState.supplier?.code,
    selectedProductCode: filterState.product?.code,
    keyword: filterState.keyword,
    sortField: filterState.productSort.field,
    sortOrder: filterState.productSort.order,
    pageIndex: filterState.pageIndex,
    pageSize: filterState.pageSize,
    forceRefresh,
  }
}

function formatCurrency(value: number) {
  return compactCurrencyFormatter.format(value || 0)
}

function formatPrice(value: number) {
  return priceFormatter.format(value || 0)
}

function formatInteger(value: number) {
  return integerFormatter.format(value || 0)
}

function formatStatisticTime(value?: string) {
  if (!value) return ''
  const time = dayjs(value)
  if (!time.isValid()) return ''
  return time.isSame(dayjs(), 'day') ? time.format('HH:mm') : time.format('MM-DD HH:mm')
}

function isKeyboardSelection(event: React.KeyboardEvent) {
  return event.key === 'Enter' || event.key === ' '
}

function ShareBar({ value, total, compact, title }: { value: number; total: number; compact: boolean; title?: string }) {
  const share = shareOf(value, total)
  const hint = title ?? `合计 ${formatCurrency(total)}`
  if (compact) return <span className={styles.shareText} title={hint}>{formatShare(value, total)}</span>
  return (
    <div className={styles.share} title={hint}>
      <div className={styles.shareTrack}><i style={{ width: `${total > 0 ? Math.max(2, share * 100) : 0}%` }} /></div>
      <span>{formatShare(value, total)}</span>
    </div>
  )
}

const CompactSalesBoardPage: React.FC = () => {
  const { active } = useKeepAliveContext()
  const access = useAuthStore((state) => state.access)
  const rawManagedStoreCodes = access.managedStoreCodes?.() ?? undefined
  const managedStoreCodesKey = rawManagedStoreCodes?.join('|') ?? 'ALL'
  const managedStoreCodes = useMemo(
    () => rawManagedStoreCodes ? [...rawManagedStoreCodes] : undefined,
    [managedStoreCodesKey],
  )
  const [quickRange, setQuickRange] = useState<QuickRange | null>('today')
  const [dateRange, setDateRange] = useState<[Dayjs, Dayjs]>(() => resolveQuickRange('today'))
  const [board, setBoard] = useState<CompactSalesBoard>(emptyBoard)
  const [loadedKeys, setLoadedKeys] = useState<PanelKeys | null>(null)
  const [loading, setLoading] = useState(false)
  const [loadError, setLoadError] = useState<string | null>(null)
  const [cacheState, setCacheState] = useState<CacheState>('fresh')
  const [queryMs, setQueryMs] = useState<number | null>(null)
  const [selectedBranch, setSelectedBranch] = useState<BoardSelection | null>(null)
  const [selectedSupplier, setSelectedSupplier] = useState<BoardSelection | null>(null)
  const [selectedProduct, setSelectedProduct] = useState<BoardSelection | null>(null)
  const [keywordInput, setKeywordInput] = useState('')
  const [keyword, setKeyword] = useState('')
  const [compositionTick, setCompositionTick] = useState(0)
  const [supplierSearch, setSupplierSearch] = useState('')
  const [productSort, setProductSort] = useState<ProductSort>(defaultProductSort)
  const [pageIndex, setPageIndex] = useState(1)
  const [pageSize, setPageSize] = useState(defaultPageSize)
  const [reloadKey, setReloadKey] = useState(0)
  // 中文注释：底部统计默认折叠，展开与否按人记在本机浏览器。
  const [statsExpanded, setStatsExpanded] = useState(() => readStoredFlag(statsExpandedStorageKey))
  const [footerHeight, setFooterHeight] = useState<number | null>(null)
  const [exportProgress, setExportProgress] = useState<(CompactBoardExportProgress & { finalizing: boolean }) | null>(null)
  const forceRefreshRef = useRef(false)
  const composingRef = useRef(false)
  const boardRequestAbortRef = useRef<AbortController | null>(null)
  const exportAbortRef = useRef<AbortController | null>(null)
  const boardCacheRef = useRef(new Map<string, CompactBoardCacheEntry<CompactSalesBoard>>())
  const pageRef = useRef<HTMLDivElement>(null)
  const footerRef = useRef<HTMLElement>(null)
  const [compact, setCompact] = useState(false)
  const [sideCompact, setSideCompact] = useState(false)

  useEffect(() => {
    const element = pageRef.current
    if (!element || typeof ResizeObserver === 'undefined') return
    const observer = new ResizeObserver(([entry]) => {
      setCompact(entry.contentRect.width < compactLayoutWidth)
      setSideCompact(entry.contentRect.width < sidePanelShareBarWidth)
    })
    observer.observe(element)
    return () => observer.disconnect()
  }, [])

  // 中文注释：量底部统计条的实际高度（含边框），页面隐藏时高度为 0 不更新，避免切回时表格先跳高再缩回。
  useEffect(() => {
    const element = footerRef.current
    if (!element || typeof ResizeObserver === 'undefined') return
    const observer = new ResizeObserver(([entry]) => {
      // 向上取整：高度带小数时四舍五入会让整页多出 1px 滚动条。
      const height = Math.ceil(entry.borderBoxSize?.[0]?.blockSize ?? entry.target.getBoundingClientRect().height)
      if (height > 0) setFooterHeight(height)
    })
    observer.observe(element)
    return () => observer.disconnect()
  }, [])

  useEffect(() => {
    writeStoredFlag(statsExpandedStorageKey, statsExpanded)
  }, [statsExpanded])

  // 中文注释：离开页面（组件卸载）时中止仍在进行的导出，不在后台继续拉数据和图片。
  useEffect(() => () => exportAbortRef.current?.abort(), [])

  const dateRangeParams = useMemo(() => toDateRange(dateRange), [dateRange])
  const filterState = useMemo<BoardFilterState>(() => ({
    dateRange: dateRangeParams,
    scopeKey: managedStoreCodesKey,
    branch: selectedBranch,
    supplier: selectedSupplier,
    product: selectedProduct,
    keyword,
    productSort,
    pageIndex,
    pageSize,
  }), [dateRangeParams, keyword, managedStoreCodesKey, pageIndex, pageSize, productSort, selectedBranch, selectedProduct, selectedSupplier])
  const panelKeys = useMemo(() => buildPanelKeys(filterState), [filterState])

  const loadBoard = useCallback(async (signal?: AbortSignal) => {
    const forceRefresh = forceRefreshRef.current
    forceRefreshRef.current = false
    const requestKey = buildRequestKey(filterState)
    const requestPanelKeys = buildPanelKeys(filterState)
    const cachedBoard = !forceRefresh
      ? readCompactBoardCache(boardCacheRef.current, requestKey)
      : undefined
    if (cachedBoard) {
      setBoard(cachedBoard)
      setLoadedKeys(requestPanelKeys)
      setLoadError(null)
      setCacheState('cached')
      setLoading(false)
      return
    }

    setLoading(true)
    setLoadError(null)
    setCacheState(forceRefresh ? 'refreshing' : 'fresh')
    const startedAt = performance.now()
    try {
      const result = await getCompactSalesBoard(buildBoardRequest(filterState, managedStoreCodes, forceRefresh), signal)
      if (!signal?.aborted) {
        setBoard(result)
        setLoadedKeys(requestPanelKeys)
        setQueryMs(Math.round(performance.now() - startedAt))
        setCacheState('fresh')
        // 中文注释：仅复用完全相同的交互参数；按钮刷新始终绕过前后端缓存。
        writeCompactBoardCache(boardCacheRef.current, requestKey, {
          expiresAt: Date.now() + compactBoardClientCacheMs,
          data: result,
        })
      }
    } catch (error) {
      if (!signal?.aborted && (error as { name?: string })?.name !== 'AbortError') {
        setBoard(emptyBoard)
        setLoadedKeys(requestPanelKeys)
        setLoadError('销售看板数据加载失败，请重试。')
        setCacheState('error')
        message.error('销售看板数据加载失败')
      }
    } finally {
      if (!signal?.aborted) setLoading(false)
    }
  }, [filterState, managedStoreCodes])

  useEffect(() => {
    boardRequestAbortRef.current?.abort()
    if (!active) {
      setLoading(false)
      return
    }

    // 中文注释：新的点选会中止上一次请求；旧数据保留在界面上，只有受影响的栏显示加载条。
    const controller = new AbortController()
    boardRequestAbortRef.current = controller
    void loadBoard(controller.signal)
    return () => {
      controller.abort()
      if (boardRequestAbortRef.current === controller) {
        boardRequestAbortRef.current = null
      }
    }
  }, [active, loadBoard, reloadKey])

  // 中文注释：关键词防抖；中文输入法组词期间不触发查询，确认候选（compositionTick 变化）后再查。
  useEffect(() => {
    if (composingRef.current) return
    const nextKeyword = keywordInput.trim()
    if (nextKeyword === keyword) return
    const timer = window.setTimeout(() => {
      setKeyword(nextKeyword)
      setPageIndex(1)
    }, keywordDebounceMs)
    return () => window.clearTimeout(timer)
  }, [compositionTick, keyword, keywordInput])

  const isFilterActive = Boolean(selectedBranch || selectedSupplier || selectedProduct)
  const clearFilters = useCallback(() => {
    setSelectedBranch(null)
    setSelectedSupplier(null)
    setSelectedProduct(null)
    setPageIndex(1)
  }, [])

  useEffect(() => {
    if (!active || !isFilterActive) return
    const handleKeyDown = (event: KeyboardEvent) => {
      if (event.key === 'Escape' && shouldHandleEscape(event.target)) clearFilters()
    }
    window.addEventListener('keydown', handleKeyDown)
    return () => window.removeEventListener('keydown', handleKeyDown)
  }, [active, clearFilters, isFilterActive])

  const selectBranch = useCallback((next: BoardSelection) => {
    setSelectedBranch((current) => toggleSelection(current, next))
    setPageIndex(1)
  }, [])
  const selectSupplier = useCallback((next: BoardSelection) => {
    const resolved = resolveSupplierToggle({ supplier: selectedSupplier, product: selectedProduct }, next)
    setSelectedSupplier(resolved.supplier)
    setSelectedProduct(resolved.product)
    setPageIndex(1)
  }, [selectedProduct, selectedSupplier])
  const selectProduct = useCallback((next: BoardSelection) => {
    setSelectedProduct((current) => toggleSelection(current, next))
  }, [])

  const handleRangeChange = useCallback((value: [Dayjs | null, Dayjs | null] | null) => {
    if (!value?.[0] || !value[1]) return
    if (value[1].diff(value[0], 'day') + 1 > maxSalesDateRangeDays) {
      message.warning(`日期范围最多 ${maxSalesDateRangeDays} 天`)
      return
    }
    setQuickRange(null)
    setDateRange([value[0], value[1]])
    setPageIndex(1)
  }, [])

  const forceRefresh = useCallback(() => {
    forceRefreshRef.current = true
    setReloadKey((key) => key + 1)
  }, [])

  const isFirstLoad = loadedKeys === null
  const storesStale = loading && loadedKeys?.stores !== panelKeys.stores
  const suppliersStale = loading && loadedKeys?.suppliers !== panelKeys.suppliers
  const productsStale = loading && loadedKeys?.products !== panelKeys.products
  const summaryStale = loading && loadedKeys?.summary !== panelKeys.summary

  const supplierTotal = useMemo(() => board.chinaSuppliers.reduce((sum, row) => sum + row.totalAmount, 0), [board.chinaSuppliers])
  const visibleSuppliers = useMemo(
    () => board.chinaSuppliers.filter((row) => matchesSupplierSearch(row, supplierSearch)),
    [board.chinaSuppliers, supplierSearch],
  )

  // 中文注释：一年、两年区间的金额可达 7 位数，金额 104px、数量 72px 且不换行（紧凑布局各收窄 8px）。
  const amountWidth = compact ? 96 : 104
  const quantityWidth = compact ? 64 : 72

  const supplierColumns = useMemo<ColumnsType<CompactSalesBoardChinaSupplier>>(() => [
    {
      title: '国内供应商', dataIndex: 'supplierName', key: 'supplierName', ellipsis: true,
      sorter: (a, b) => a.supplierName.localeCompare(b.supplierName, 'zh-CN'), sortDirections: ['ascend', 'descend'],
      render: (_, record) => <div className={styles.primaryCell}><span>{record.supplierName || record.supplierCode}</span><small>{record.supplierCode}<i>·</i>{formatInteger(record.productCount)} 款</small></div>,
    },
    { title: '金额', dataIndex: 'totalAmount', key: 'totalAmount', align: 'right', width: amountWidth, className: styles.numCell, defaultSortOrder: 'descend', sorter: (a, b) => a.totalAmount - b.totalAmount, sortDirections: ['descend', 'ascend'], render: (value: number) => <span className={styles.num}>{formatCurrency(value)}</span> },
    { title: '数量', dataIndex: 'totalQuantity', key: 'totalQuantity', align: 'right', width: quantityWidth, className: styles.numCell, sorter: (a, b) => a.totalQuantity - b.totalQuantity, sortDirections: ['descend', 'ascend'], render: formatInteger },
    { title: <Tooltip title="分母为本栏合计">占比</Tooltip>, key: 'share', width: sideCompact ? 60 : 96, render: (_, record) => <ShareBar value={record.totalAmount} total={supplierTotal} compact={sideCompact} /> },
  ], [amountWidth, quantityWidth, sideCompact, supplierTotal])

  const branchColumns = useMemo<ColumnsType<CompactSalesBoardStore>>(() => [
    {
      title: '分店', dataIndex: 'branchName', key: 'branchName', ellipsis: true,
      sorter: (a, b) => a.branchName.localeCompare(b.branchName), sortDirections: ['ascend', 'descend'],
      render: (_, record) => <div className={styles.primaryCell}><span>{record.branchName || record.branchCode}</span><small>{record.branchCode}<i>·</i>{formatInteger(record.productCount)} 款</small></div>,
    },
    { title: '金额', dataIndex: 'totalAmount', key: 'totalAmount', align: 'right', width: amountWidth, className: styles.numCell, defaultSortOrder: 'descend', sorter: (a, b) => a.totalAmount - b.totalAmount, sortDirections: ['descend', 'ascend'], render: (value: number) => <span className={styles.num}>{formatCurrency(value)}</span> },
    { title: '数量', dataIndex: 'totalQuantity', key: 'totalQuantity', align: 'right', width: quantityWidth, className: styles.numCell, sorter: (a, b) => a.totalQuantity - b.totalQuantity, sortDirections: ['descend', 'ascend'], render: formatInteger },
    // 中文注释：占比 = 国内商品营业额 ÷ 该分店总营业额（营业额日报口径，含全部供应商）；选中供应商、商品只收窄分子，分母不变。
    {
      title: <Tooltip title="国内商品营业额 ÷ 分店总营业额（含全部供应商的商品）。选中供应商或商品后分子随之收窄，分母不变。"><span className={styles.headerHint}>{sideCompact ? '占比' : '占分店营业额'}{!sideCompact && <InfoCircleOutlined />}</span></Tooltip>,
      // 中文注释：笔记本宽度下表头缩成「占比」（说明在悬停提示里），把宽度留给分店名称列。
      key: 'branchShare', width: sideCompact ? 60 : 120,
      sorter: (a, b) => shareOf(a.totalAmount, a.branchTotalAmount) - shareOf(b.totalAmount, b.branchTotalAmount), sortDirections: ['descend', 'ascend'],
      render: (_, record) => <ShareBar value={record.totalAmount} total={record.branchTotalAmount} compact={sideCompact} title={`国内 ${formatCurrency(record.totalAmount)} ÷ 分店总营业额 ${formatCurrency(record.branchTotalAmount)}`} />,
    },
  ], [amountWidth, quantityWidth, sideCompact])

  const productRankOffset = (board.productDetails.pageIndex - 1) * board.productDetails.pageSize
  const productColumns = useMemo<ColumnsType<CompactSalesBoardProduct>>(() => [
    { title: '#', key: 'rank', width: 44, align: 'right', render: (_, __, index) => <span className={styles.rank}>{productRankOffset + index + 1}</span> },
    { title: '图片', dataIndex: 'productImage', key: 'productImage', width: 52, render: (value: string | undefined, record) => <div className={styles.productImageBox}>{value ? <Image src={value} alt={record.productName ?? record.itemNumber ?? record.productCode} width={30} height={30} loading="lazy" preview={false} className={styles.productImage} fallback="" /> : <span />}</div> },
    {
      title: '货号 / 名称', dataIndex: 'itemNumber', key: 'itemNumber', ellipsis: true,
      sorter: true, sortOrder: toAntdSortOrder(productSort, 'itemNumber'), sortDirections: ['ascend', 'descend'],
      render: (_, record) => <div className={styles.primaryCell}><span>{record.itemNumber || record.productCode}</span><small title={record.productName}>{record.productName || record.productCode}</small></div>,
    },
    {
      title: '供应商', dataIndex: 'chinaSupplierCode', key: 'chinaSupplierCode', width: 78,
      render: (value: string | undefined, record) => value
        ? (
          <Tooltip title={`${record.chinaSupplierName ?? value}：点击按该供应商筛选`}>
            <button
              type="button"
              className={[styles.supplierCode, selectedSupplier?.code === value ? styles.supplierCodeActive : ''].filter(Boolean).join(' ')}
              onClick={(event) => {
                // 中文注释：只切换供应商，不触发整行的商品选中。
                event.stopPropagation()
                selectSupplier({ code: value, label: record.chinaSupplierName || value, detail: value })
              }}
              onKeyDown={(event) => event.stopPropagation()}
            >
              {value}
            </button>
          </Tooltip>
        )
        : '-',
    },
    { title: '数量', dataIndex: 'totalQuantity', key: 'quantity', align: 'right', width: quantityWidth + 4, className: styles.numCell, sorter: true, sortOrder: toAntdSortOrder(productSort, 'quantity'), sortDirections: ['descend', 'ascend'], render: formatInteger },
    { title: '单价', dataIndex: 'unitPrice', key: 'unitPrice', align: 'right', width: 80, className: styles.numCell, sorter: true, sortOrder: toAntdSortOrder(productSort, 'unitPrice'), sortDirections: ['descend', 'ascend'], render: formatPrice },
    { title: '金额', dataIndex: 'totalAmount', key: 'amount', align: 'right', width: amountWidth, className: styles.numCell, sorter: true, sortOrder: toAntdSortOrder(productSort, 'amount'), sortDirections: ['descend', 'ascend'], render: (value: number) => <span className={styles.num}>{formatCurrency(value)}</span> },
    { title: <Tooltip title="分母为当前分店、供应商筛选下全部商品的合计（不受搜索影响）">占比</Tooltip>, key: 'share', width: compact ? 60 : 96, render: (_, record) => <ShareBar value={record.totalAmount} total={board.productDetails.scopeAmount} compact={compact} /> },
  ], [amountWidth, board.productDetails.scopeAmount, compact, productRankOffset, productSort, quantityWidth, selectSupplier, selectedSupplier?.code])

  const handleProductTableChange = useCallback<NonNullable<TableProps<CompactSalesBoardProduct>['onChange']>>((_pagination, _filters, sorter, extra) => {
    if (extra.action !== 'sort') return
    const current = Array.isArray(sorter) ? sorter[0] : sorter
    // 中文注释：商品明细分页展示，排序必须交给服务端对全部结果排序，不能只排当前页。
    setProductSort(resolveProductSort(current?.columnKey, current?.order))
    setPageIndex(1)
  }, [])

  // 中文注释：导出只用已显示的数据与同一组条件；导出全部结果按 500 行一批向服务端分页读取（同一排序）。
  const runExport = useCallback(async (mode: ExportMode) => {
    if (exportAbortRef.current) return
    const controller = new AbortController()
    exportAbortRef.current = controller
    const exportState = filterState
    const details = board.productDetails
    setExportProgress({ text: '正在准备导出…', finalizing: false })
    try {
      const { collectAllCompactBoardProducts, exportCompactBoardProducts } = await import('./export')
      const rows = mode === 'page'
        ? details.data
        : await collectAllCompactBoardProducts(
          async (batchIndex, batchSize, signal) => (await getCompactSalesBoard({
            ...buildBoardRequest(exportState, managedStoreCodes, false),
            pageIndex: batchIndex,
            pageSize: batchSize,
          }, signal)).productDetails,
          controller.signal,
          (progress) => setExportProgress({ ...progress, finalizing: false }),
        )
      const result = await exportCompactBoardProducts(rows, {
        startDate: exportState.dateRange.startDate,
        endDate: exportState.dateRange.endDate,
        filterLabel: describeExportFilters(exportState),
        sortLabel: describeProductSort(exportState.productSort),
        scopeLabel: mode === 'page' ? `第 ${details.pageIndex} 页（每页 ${details.pageSize} 行）` : '全部结果',
        fileSuffix: mode === 'page' ? `第${details.pageIndex}页` : '全部',
        scopeAmount: details.scopeAmount,
        firstRank: mode === 'page' ? (details.pageIndex - 1) * details.pageSize + 1 : 1,
        signal: controller.signal,
        onProgress: (progress) => setExportProgress({ ...progress, finalizing: false }),
        onFinalize: () => setExportProgress({ text: '正在生成 Excel 文件…', finalizing: true }),
      })
      const notes = [
        result.failedImages > 0 ? `${result.failedImages} 张图片读取失败` : '',
        result.count > result.imageRows ? `前 ${result.imageRows} 行含图片` : '',
      ].filter(Boolean)
      message.success(`已导出 ${formatInteger(result.count)} 件商品${notes.length > 0 ? `（${notes.join('，')}）` : ''}`)
    } catch (error) {
      if ((error as { name?: string })?.name === 'AbortError') message.info('已取消导出')
      else message.error('导出失败，请稍后重试')
    } finally {
      if (exportAbortRef.current === controller) exportAbortRef.current = null
      setExportProgress(null)
    }
  }, [board.productDetails, filterState, managedStoreCodes])

  const selectableRow = (selection: BoardSelection, selected: boolean, onSelect: (next: BoardSelection) => void) => ({
    role: 'button',
    tabIndex: 0,
    'aria-pressed': selected,
    onClick: () => onSelect(selection),
    onKeyDown: (event: React.KeyboardEvent) => {
      if (isKeyboardSelection(event)) {
        event.preventDefault()
        onSelect(selection)
      }
    },
  })
  const rowClassName = (selected: boolean) => [styles.clickableRow, selected ? styles.selectedRow : ''].filter(Boolean).join(' ')

  const summary = board.summary
  const statisticFresh = !board.statisticStatus || board.statisticStatus === 'Fresh'
  const emptyText = (panelLoading: boolean) => {
    if (isFirstLoad || panelLoading) return <div className={styles.skeleton} aria-label="加载中">{Array.from({ length: 8 }, (_, index) => <span key={index} />)}</div>
    if (!statisticFresh) return <div className={styles.empty}>{board.statisticMessage || '商品统计尚未就绪'}</div>
    return (
      <div className={styles.empty}>
        <p>{isFilterActive ? '当前筛选组合下没有销售记录' : '当前日期范围没有国内商品销售'}</p>
        {isFilterActive && <Button size="small" onClick={clearFilters}>清除筛选</Button>}
      </div>
    )
  }
  const [rangeStart, rangeEnd] = pageRange(board.productDetails.pageIndex, board.productDetails.pageSize, board.productDetails.total)
  const statisticTime = formatStatisticTime(board.statisticUpdatedAt)
  const branchHint = selectedProduct ? '该商品在各分店的销售' : selectedSupplier ? '该供应商在各分店的销售' : ''
  const supplierHint = selectedProduct ? '该商品的供应商' : selectedBranch ? '该分店的供应商销售' : ''
  const rangeDays = dateRange[1].diff(dateRange[0], 'day') + 1
  const productTotal = board.productDetails.total
  const exporting = exportProgress !== null
  const exportDisabled = exporting || isFirstLoad || productsStale || Boolean(loadError) || productTotal === 0
  const exportMenuItems: MenuProps['items'] = [
    { key: 'page', label: `导出本页 · ${formatInteger(board.productDetails.data.length)} 行（含图片）` },
    {
      key: 'all',
      label: `导出全部结果 · ${formatInteger(productTotal)} 行${productTotal > MAX_PRODUCT_IMAGE_EXPORT_ROWS ? `（前 ${MAX_PRODUCT_IMAGE_EXPORT_ROWS} 行含图片）` : '（含图片）'}`,
    },
  ]

  // 中文注释：底部统计条折叠、展开共用同一份状态与耗时说明。
  const statusNode = loadError
    ? <span className={styles.statusError}>加载失败</span>
    : isFirstLoad
      ? <span className={styles.statusMuted}>加载中…</span>
      : statisticFresh
        ? board.statisticMessage
          // 中文注释：对账未通过、历史缺口等只提示不阻断（与销售明细一致）；折叠时全文放在提示框里，展开后直接显示。
          ? <Tooltip title={statsExpanded ? undefined : board.statisticMessage}><span className={styles.statusWarn} tabIndex={0}><i />统计已更新{statisticTime ? ` · 截至 ${statisticTime}` : ''} · 含提示</span></Tooltip>
          : <span className={styles.statusOk}><i />统计已更新{statisticTime ? ` · 截至 ${statisticTime}` : ''}</span>
        : <span className={styles.statusWarn}><i />{board.statisticMessage || board.statisticStatus}</span>
  const queryNode = loadError ? null : (
    <small className={styles.queryInfo}>
      {cacheState === 'cached'
        ? '本地缓存'
        : loading
          ? (cacheState === 'refreshing' ? '强制刷新中…' : '查询中…')
          : queryMs !== null ? <>本次查询 <b>{formatInteger(queryMs)} ms</b></> : null}
      {!loading && cacheState !== 'cached' && queryMs !== null && <span className={styles.badge}>{board.fromCache ? '命中缓存' : '实时聚合'}</span>}
    </small>
  )
  const overallShare = <span className={styles.kpiHighlight}>占全部 {formatShare(summary.totalAmount, summary.overallAmount)}</span>
  const kpiValue = (value: string) => <strong className={isFirstLoad ? styles.kpiSkeleton : undefined}>{isFirstLoad ? '' : value}</strong>
  const kpiItems: { label: string; value: string; unit?: string; sub: React.ReactNode; main?: boolean }[] = [
    {
      label: '国内营业额', value: formatCurrency(summary.totalAmount), main: true,
      sub: isFilterActive
        ? <span className={styles.kpiHighlight}>占全部 {formatShare(summary.totalAmount, summary.overallAmount)} · 全部 {formatCurrency(summary.overallAmount)}</span>
        : '全部国内供应商 · 全部分店',
    },
    { label: '销量', value: formatInteger(summary.totalQuantity), unit: '件', sub: '件' },
    { label: '动销商品', value: formatInteger(summary.productCount), unit: '款', sub: '款' },
    { label: '平均单价', value: formatPrice(summary.totalQuantity > 0 ? summary.totalAmount / summary.totalQuantity : 0), sub: '营业额 ÷ 销量' },
    { label: '国内供应商', value: formatInteger(summary.supplierCount), sub: '有销售' },
    { label: '分店', value: formatInteger(summary.storeCount), sub: '有销售' },
  ]
  const statsToggle = (
    <Button
      size="small"
      className={styles.statsToggle}
      aria-expanded={statsExpanded}
      aria-controls="compact-sales-board-stats"
      icon={statsExpanded ? <DownOutlined /> : <UpOutlined />}
      iconPosition="end"
      onClick={() => setStatsExpanded((value) => !value)}
    >
      {statsExpanded ? '收起' : '展开统计'}
    </Button>
  )

  // 中文注释：选中项标签的顺序与三栏一致：国内供应商 → 分店 → 商品。
  const filterChips = (removable: boolean) => (
    <>
      {selectedSupplier && (
        <span className={`${styles.chip} ${styles.chipSupplier}`}>
          <i /><span>国内供应商</span><b>{selectedSupplier.label}</b><small>{selectedSupplier.detail}</small>
          {removable && <button type="button" aria-label="移除国内供应商筛选" onClick={() => { setSelectedSupplier(null); setPageIndex(1) }}><CloseOutlined /></button>}
        </span>
      )}
      {selectedBranch && (
        <span className={`${styles.chip} ${styles.chipBranch}`}>
          <i /><span>分店</span><b>{selectedBranch.label}</b><small>{selectedBranch.detail}</small>
          {removable && <button type="button" aria-label="移除分店筛选" onClick={() => { setSelectedBranch(null); setPageIndex(1) }}><CloseOutlined /></button>}
        </span>
      )}
      {selectedProduct && (
        <span className={`${styles.chip} ${styles.chipProduct}`}>
          <i /><span>商品</span><b>{selectedProduct.label}</b><small>{selectedProduct.detail}</small>
          {removable && <button type="button" aria-label="移除商品筛选" onClick={() => setSelectedProduct(null)}><CloseOutlined /></button>}
        </span>
      )}
    </>
  )

  const pageStyle = footerHeight ? ({ '--cb-footer-h': `${footerHeight}px` } as React.CSSProperties) : undefined

  return (
    <div ref={pageRef} className={styles.page} style={pageStyle} aria-busy={loading}>
      <header className={styles.boardHead}>
        <div className={styles.toolbar}>
          <div className={styles.titleBlock}>
            <h1>销售看板</h1>
            {isFilterActive ? (
              <div className={styles.toolbarFilters} aria-label="联动筛选">
                <span className={styles.filterLabel}>联动筛选</span>
                {filterChips(true)}
                <Button type="link" size="small" aria-label="清除筛选" onClick={clearFilters}>清除全部</Button>
                <kbd className={styles.kbd}>Esc</kbd>
              </div>
            ) : (
              <span className={styles.scope}><b>口径</b>澳洲供应商 200-hotbargain · 已映射国内供应商的商品</span>
            )}
          </div>
          <div className={styles.toolbarControls}>
            <Segmented size="small" value={quickRange ?? undefined} options={quickRangeOptions} onChange={(value) => { const nextRange = value as QuickRange; setQuickRange(nextRange); setDateRange(resolveQuickRange(nextRange)); setPageIndex(1) }} />
            <RangePicker size="small" value={dateRange} allowClear={false} disabledDate={isDisabledDate} onChange={handleRangeChange} />
            <Tooltip title="强制刷新（绕过缓存）">
              <Button size="small" type="primary" icon={<ReloadOutlined />} aria-label="强制刷新销售看板" loading={loading && cacheState === 'refreshing'} onClick={forceRefresh} />
            </Tooltip>
          </div>
        </div>
      </header>

      {loadError && <Alert className={styles.loadError} type="error" showIcon message={loadError} action={<Button size="small" onClick={() => setReloadKey((key) => key + 1)}>重试</Button>} />}

      {/* 中文注释：三栏从左到右是 国内供应商 → 分店 → 商品；每栏只被另外两栏的选中项收窄，便于在栏内换一行对比。 */}
      <div className={styles.grid}>
        <section className={[styles.panel, styles.panelSupplier, suppliersStale ? styles.panelLoading : ''].filter(Boolean).join(' ')} aria-label="国内供应商销售" aria-busy={suppliersStale}>
          <div className={styles.progress} aria-hidden="true" />
          <div className={styles.panelHeader}>
            <div className={styles.panelTitle}><i /><h2>国内供应商</h2><span><b>{visibleSuppliers.length}</b> 个</span>{supplierHint && <em className={styles.panelHint} title={supplierHint}>{supplierHint}</em>}</div>
            <Input size="small" allowClear className={styles.supplierSearch} prefix={<SearchOutlined />} placeholder="名称 / 代码" aria-label="搜索国内供应商" value={supplierSearch} onChange={(event) => setSupplierSearch(event.target.value)} />
          </div>
          <MeasuredTable
            metricId="compact-sales-board.suppliers"
            rowKey="supplierCode"
            size="small"
            columns={supplierColumns}
            dataSource={visibleSuppliers}
            pagination={false}
            showSorterTooltip={false}
            locale={{ emptyText: emptyText(suppliersStale) }}
            scroll={{ y: tableBodyHeight }}
            rowClassName={(record) => rowClassName(record.supplierCode === selectedSupplier?.code)}
            onRow={(record) => selectableRow({ code: record.supplierCode, label: record.supplierName || record.supplierCode, detail: record.supplierCode }, record.supplierCode === selectedSupplier?.code, selectSupplier)}
          />
        </section>

        <section className={[styles.panel, styles.panelBranch, storesStale ? styles.panelLoading : ''].filter(Boolean).join(' ')} aria-label="分店销售" aria-busy={storesStale}>
          <div className={styles.progress} aria-hidden="true" />
          <div className={styles.panelHeader}>
            <div className={styles.panelTitle}><i /><h2>分店销售</h2><span><b>{board.stores.length}</b> 家</span></div>
            {branchHint && <span className={styles.panelHint}>{branchHint}</span>}
          </div>
          <MeasuredTable
            metricId="compact-sales-board.stores"
            rowKey="branchCode"
            size="small"
            columns={branchColumns}
            dataSource={board.stores}
            pagination={false}
            showSorterTooltip={false}
            locale={{ emptyText: emptyText(storesStale) }}
            scroll={{ y: tableBodyHeight }}
            rowClassName={(record) => rowClassName(record.branchCode === selectedBranch?.code)}
            onRow={(record) => selectableRow({ code: record.branchCode, label: record.branchName || record.branchCode, detail: record.branchCode }, record.branchCode === selectedBranch?.code, selectBranch)}
          />
        </section>

        <section className={[styles.panel, styles.panelProduct, productsStale ? styles.panelLoading : ''].filter(Boolean).join(' ')} aria-label="国内商品明细" aria-busy={productsStale}>
          <div className={styles.progress} aria-hidden="true" />
          <div className={styles.panelHeader}>
            <div className={styles.panelTitle}><i /><h2>国内商品明细</h2><span><b>{formatInteger(productTotal)}</b> 款</span></div>
            <div className={styles.panelActions}>
              <Input
                size="small"
                allowClear
                className={styles.productSearch}
                prefix={<SearchOutlined />}
                placeholder="货号 / 名称，空格分隔多个词"
                aria-label="搜索商品"
                value={keywordInput}
                onChange={(event) => setKeywordInput(event.target.value)}
                onCompositionStart={() => { composingRef.current = true }}
                onCompositionEnd={(event) => { composingRef.current = false; setKeywordInput(event.currentTarget.value); setCompositionTick((tick) => tick + 1) }}
              />
              {/* 中文注释：触发按钮不用 loading（antd 会吞掉点击让菜单打不开），导出中直接禁用。 */}
              <Dropdown
                trigger={['click']}
                disabled={exportDisabled}
                menu={{ items: exportMenuItems, onClick: ({ key }) => { void runExport(key as ExportMode) } }}
              >
                <Button size="small" icon={<DownloadOutlined />} aria-label="导出商品明细 Excel（含图片）">
                  导出 Excel <DownOutlined className={styles.caret} />
                </Button>
              </Dropdown>
            </div>
          </div>
          {exportProgress && (
            <div className={styles.exportStatus} role="status">
              <DownloadOutlined />
              <span>{exportProgress.text}</span>
              <div className={styles.exportTrack} aria-hidden="true">{exportProgress.percent !== undefined && <i style={{ width: `${Math.round(exportProgress.percent * 100)}%` }} />}</div>
              {!exportProgress.finalizing && <Button type="link" size="small" onClick={() => exportAbortRef.current?.abort()}>取消</Button>}
            </div>
          )}
          <MeasuredTable
            metricId="compact-sales-board.products"
            rowKey="productCode"
            size="small"
            columns={productColumns}
            dataSource={board.productDetails.data}
            pagination={false}
            showSorterTooltip={false}
            locale={{ emptyText: emptyText(productsStale) }}
            scroll={{ x: 580, y: exporting ? productTableBodyHeightExporting : productTableBodyHeight }}
            onChange={handleProductTableChange}
            rowClassName={(record) => rowClassName(record.productCode === selectedProduct?.code)}
            onRow={(record) => selectableRow({
              code: record.productCode,
              label: record.itemNumber || record.productCode,
              detail: record.productName,
              supplierCode: record.chinaSupplierCode,
            }, record.productCode === selectedProduct?.code, selectProduct)}
          />
          <div className={styles.pager}>
            <span className={styles.pagerMeta}>
              第 <b>{rangeStart}–{rangeEnd}</b> 条 / 共 {formatInteger(productTotal)} 条 · {describeProductSort(productSort)}（全部结果排序）
            </span>
            <Pagination size="small" current={pageIndex} pageSize={pageSize} total={productTotal} showSizeChanger pageSizeOptions={pageSizeOptions} onChange={(page, size) => { setPageIndex(size !== pageSize ? 1 : page); setPageSize(size) }} />
          </div>
        </section>
      </div>

      <footer ref={footerRef} id="compact-sales-board-stats" className={[styles.statsBar, statsExpanded ? styles.statsBarExpanded : ''].filter(Boolean).join(' ')} aria-label="统计概览">
        <span className={styles.grabber} aria-hidden="true" />
        {statsExpanded ? (
          <div>
            <div className={styles.statsHead}>
              <div className={styles.statsTitle}>
                <h2>统计概览</h2>
                <span>{dateRangeParams.startDate} ~ {dateRangeParams.endDate} · {rangeDays} 天 · 澳洲供应商 200-hotbargain · 已映射国内供应商的商品</span>
              </div>
              {statsToggle}
            </div>
            <div className={[styles.kpiGrid, summaryStale ? styles.kpisStale : ''].filter(Boolean).join(' ')} aria-live="polite">
              {kpiItems.map((item) => (
                <div key={item.label} className={[styles.kpi, item.main ? styles.kpiMain : ''].filter(Boolean).join(' ')}>
                  <span>{item.label}</span>
                  {kpiValue(item.value)}
                  <em>{item.sub}</em>
                </div>
              ))}
            </div>
            <div className={styles.statsDetails}>
              <section>
                <h3>数据状态</h3>
                {statusNode}
                {!loadError && statisticFresh && board.statisticMessage && <p className={styles.statusMessage}>{board.statisticMessage}</p>}
              </section>
              <section>
                <h3>本次查询</h3>
                {queryNode ?? <p>—</p>}
                <p>相同条件短时间内再次打开会直接命中缓存；右上角刷新按钮会绕过缓存重新聚合。</p>
              </section>
              <section>
                <h3>联动筛选</h3>
                {isFilterActive ? (
                  <>
                    <div className={styles.staticChips}>{filterChips(false)}</div>
                    <p>每栏按另外两栏的选中项收窄，自身不收窄，便于在栏内换一行对比。</p>
                  </>
                ) : (
                  <p>点击任意供应商、分店或商品行即可联动筛选，再点一次取消；各栏不会被自身的选中项收窄。</p>
                )}
              </section>
            </div>
          </div>
        ) : (
          <div className={styles.statsLine}>
            <div className={[styles.kpiInline, summaryStale ? styles.kpisStale : ''].filter(Boolean).join(' ')} aria-live="polite">
              {kpiItems.map((item) => (
                <div key={item.label} className={[styles.kpiInlineItem, item.main ? styles.kpiInlineMain : ''].filter(Boolean).join(' ')}>
                  <span>{item.label}</span>
                  {kpiValue(item.value)}
                  {item.unit && <em>{item.unit}</em>}
                  {item.main && isFilterActive && !isFirstLoad && overallShare}
                </div>
              ))}
            </div>
            <div className={styles.statsMeta}>
              {statusNode}
              {queryNode}
              {statsToggle}
            </div>
          </div>
        )}
      </footer>
    </div>
  )
}

export default CompactSalesBoardPage
