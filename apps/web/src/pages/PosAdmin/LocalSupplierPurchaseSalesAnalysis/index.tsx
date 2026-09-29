import { ReloadOutlined, SearchOutlined } from '@ant-design/icons'
import {
  DndContext,
  PointerSensor,
  closestCenter,
  type DragEndEvent,
  useSensor,
  useSensors,
} from '@dnd-kit/core'
import {
  SortableContext,
  horizontalListSortingStrategy,
  useSortable,
} from '@dnd-kit/sortable'
import { CSS } from '@dnd-kit/utilities'
import {
  Button,
  Card,
  DatePicker,
  Empty,
  Input,
  Select,
  Space,
  Tag,
  TreeSelect,
  Typography,
  message,
} from 'antd'
import type {
  ColumnsType,
  FilterValue,
  SorterResult,
  TablePaginationConfig,
} from 'antd/es/table/interface'
import dayjs, { type Dayjs } from 'dayjs'
import {
  type CSSProperties,
  type HTMLAttributes,
  useCallback,
  useEffect,
  useLayoutEffect,
  useMemo,
  useRef,
  useState,
} from 'react'
import { useTranslation } from 'react-i18next'
import {
  getLocalSupplierPurchaseSalesAnalysis,
  getLocalSupplierPurchaseSalesAnalysisCategoryTree,
  getLocalSupplierPurchaseSalesAnalysisStoreOptions,
  getLocalSupplierPurchaseSalesAnalysisSupplierOptions,
} from '../../../services/localSupplierInvoiceService'
import type { StoreOption } from '../../../services/storeService'
import { useAuthStore } from '../../../store/auth'
import type {
  LocalSupplierPurchaseSalesAnalysisQueryDto,
  LocalSupplierPurchaseSalesAnalysisResponseDto,
  LocalSupplierPurchaseSalesAnalysisRowDto,
  LocalSupplierPurchaseSalesAnalysisSupplierOptionDto,
} from '../../../types/localSupplierInvoice'
import type { LocalSupplierCategoryNode } from '../../../types/localSupplierCategory'
import { buildStoreOptionsFromUserStores } from '../../../utils/managedStoreScope'
import {
  DEFAULT_PURCHASE_SALES_ANALYSIS_PAGE_SIZE,
  getDefaultPurchaseSalesAnalysisDateRange,
  normalizePurchaseSalesAnalysisPageSize,
  PURCHASE_SALES_ANALYSIS_DEFAULT_SORT_BY,
  PURCHASE_SALES_ANALYSIS_DEFAULT_SORT_ORDER,
  PURCHASE_SALES_ANALYSIS_MIN_TABLE_BODY_HEIGHT,
  PURCHASE_SALES_ANALYSIS_PAGE_SIZE_OPTIONS,
  resolvePurchaseSalesAnalysisTableBodyHeight,
  toPurchaseSalesAnalysisSort,
} from './helpers'
import {
  LOCAL_SUPPLIER_PURCHASE_SALES_ANALYSIS_DEFAULT_COLUMN_ORDER,
  isLocalSupplierPurchaseSalesAnalysisColumnOrderCustomized,
  mergeLocalSupplierPurchaseSalesAnalysisColumnOrder,
  moveLocalSupplierPurchaseSalesAnalysisColumnOrder,
  type LocalSupplierPurchaseSalesAnalysisColumnKey,
} from './columnOrder'
import { MeasuredTable } from '../../../components/MeasuredTable'
import ProductImageCell from './ProductImageCell'
import {
  PurchaseSalesDailyChart,
  PurchaseSalesSparkline,
  buildPurchaseSalesTrendMetrics,
  resolveAccordionExpandedKeys,
  resolveDefaultExpandedKeys,
  toWholeQuantity,
} from '../../../components/PurchaseSalesTrend'

const { RangePicker } = DatePicker
const { Text, Title } = Typography
const LOCAL_SUPPLIER_PURCHASE_SALES_ANALYSIS_COLUMN_ORDER_STORAGE_KEY =
  'hbweb_rv.localSupplierPurchaseSalesAnalysis.columnOrder.v2'
const STATIC_PURCHASE_SALES_ANALYSIS_COLUMN_KEYS = new Set(['image', 'itemNumber'])
const MAX_SUPPLIER_CATEGORY_SELECTIONS = 100

type DateRangeValue = [Dayjs, Dayjs]
type SortOrderState = 'asc' | 'desc'
type MetricTone = 'interval' | 'between'
type PurchaseTone = 'latest' | 'previous'
interface CategoryTreeOption {
  key: string
  value: string
  title: string
  children: CategoryTreeOption[]
}

interface SearchFilters {
  storeCode?: string
  supplierCode?: string
  supplierCategoryGuids: string[]
  keyword: string
  orderDateRange: DateRangeValue
}

function formatDate(value?: string | null) {
  if (!value) {
    return '--'
  }
  const parsed = dayjs(value)
  return parsed.isValid() ? parsed.format('YYYY-MM-DD') : value
}

function formatDateTime(value?: string | null) {
  if (!value) {
    return '--'
  }
  const parsed = dayjs(value)
  return parsed.isValid() ? parsed.format('YYYY-MM-DD HH:mm:ss') : value
}

function formatNumber(value?: number | null, digits = 0) {
  if (value === undefined || value === null || !Number.isFinite(value)) {
    return '--'
  }
  return value.toLocaleString(undefined, {
    minimumFractionDigits: digits,
    maximumFractionDigits: digits,
  })
}

const purchaseToneStyles: Record<PurchaseTone, { color: string; quantityColor: string; background: string }> = {
  latest: {
    color: '#1677ff',
    quantityColor: '#0958d9',
    background: '#e6f4ff',
  },
  previous: {
    color: '#d46b08',
    quantityColor: '#ad4e00',
    background: '#fff7e6',
  },
}

const metricToneStyles: Record<MetricTone, { color: string; background: string; borderColor: string }> = {
  interval: {
    color: '#ad6800',
    background: '#fff7e6',
    borderColor: '#ffd591',
  },
  between: {
    color: '#237804',
    background: '#f6ffed',
    borderColor: '#b7eb8f',
  },
}

function MetricBadge(props: { value?: number | null; tone: MetricTone; digits?: number }) {
  const style = metricToneStyles[props.tone]
  return (
    <Text
      style={{
        display: 'inline-flex',
        minWidth: 44,
        justifyContent: 'flex-end',
        padding: '1px 8px',
        borderRadius: 4,
        color: style.color,
        background: style.background,
        border: `1px solid ${style.borderColor}`,
        fontVariantNumeric: 'tabular-nums',
      }}
    >
      {formatNumber(props.value, props.digits)}
    </Text>
  )
}

function formatPurchase(date?: string | null, quantity?: number | null, tone: PurchaseTone = 'latest') {
  if (!date && (quantity === undefined || quantity === null)) {
    return '--'
  }

  const style = purchaseToneStyles[tone]
  return (
    <Space direction="vertical" size={0}>
      <Text strong style={{ color: style.color }}>
        {formatDate(date)}
      </Text>
      <Text
        style={{
          alignSelf: 'flex-start',
          padding: '1px 6px',
          borderRadius: 4,
          color: style.quantityColor,
          background: style.background,
          fontVariantNumeric: 'tabular-nums',
        }}
      >
        {formatNumber(toWholeQuantity(quantity))}
      </Text>
    </Space>
  )
}

interface DraggableHeaderCellProps extends HTMLAttributes<HTMLTableCellElement> {
  'data-column-key'?: string
}

function DraggableHeaderCell({ children, style, ...props }: DraggableHeaderCellProps) {
  const columnKey = props['data-column-key']
  const {
    attributes,
    listeners,
    setNodeRef,
    transform,
    transition,
    isDragging,
  } = useSortable({
    id: columnKey ?? '__local-supplier-purchase-sales-analysis-static-column__',
    disabled: !columnKey,
  })

  if (!columnKey) {
    return <th style={style} {...props}>{children}</th>
  }

  const headerStyle: CSSProperties = {
    ...style,
    transform: CSS.Translate.toString(transform),
    transition,
    cursor: 'move',
    zIndex: isDragging ? 3 : style?.zIndex,
    opacity: isDragging ? 0.85 : style?.opacity,
  }

  return (
    <th ref={setNodeRef} style={headerStyle} {...props} {...attributes} {...listeners}>
      {children}
    </th>
  )
}

function buildInitialFilters(): SearchFilters {
  return {
    storeCode: undefined,
    supplierCode: undefined,
    supplierCategoryGuids: [],
    keyword: '',
    orderDateRange: getDefaultPurchaseSalesAnalysisDateRange(),
  }
}

// 展开状态与表格共用同一行键，保证默认展开和手风琴切换命中同一行。
function getAnalysisRowKey(record: LocalSupplierPurchaseSalesAnalysisRowDto) {
  return `${record.storeCode}-${record.supplierCode}-${record.productCode}-${record.itemNumber || ''}`
}

interface LocalSupplierPurchaseSalesAnalysisPageProps {
  /** 嵌入「进货销量分析」标签页时由外层页面提供标题，这里不再渲染自带的页头。 */
  embedded?: boolean
}

export default function LocalSupplierPurchaseSalesAnalysisPage({ embedded = false }: LocalSupplierPurchaseSalesAnalysisPageProps = {}) {
  const { t } = useTranslation()
  const currentUser = useAuthStore((state) => state.currentUser)
  const access = useAuthStore((state) => state.access)

  const scopedStoreCodes = useMemo(() => access.managedStoreCodes(), [access])
  const singleScopedStoreCode = useMemo(
    () => (Array.isArray(scopedStoreCodes) && scopedStoreCodes.length === 1 ? scopedStoreCodes[0] : undefined),
    [scopedStoreCodes],
  )
  const initialFilters = useMemo(() => {
    const filters = buildInitialFilters()
    filters.storeCode = singleScopedStoreCode
    return filters
  }, [singleScopedStoreCode])
  const [storeOptions, setStoreOptions] = useState<StoreOption[]>([])
  const [supplierOptions, setSupplierOptions] = useState<LocalSupplierPurchaseSalesAnalysisSupplierOptionDto[]>([])
  const [storeOptionsLoading, setStoreOptionsLoading] = useState(false)
  const [supplierOptionsLoading, setSupplierOptionsLoading] = useState(false)
  const [categoryTree, setCategoryTree] = useState<LocalSupplierCategoryNode[]>([])
  const [categoryTreeLoading, setCategoryTreeLoading] = useState(false)
  const [categoryTreeError, setCategoryTreeError] = useState(false)
  const [categoryTreeReloadToken, setCategoryTreeReloadToken] = useState(0)
  const [draftFilters, setDraftFilters] = useState<SearchFilters>(initialFilters)
  const [filters, setFilters] = useState<SearchFilters>(initialFilters)
  const [page, setPage] = useState(1)
  const [pageSize, setPageSize] = useState(DEFAULT_PURCHASE_SALES_ANALYSIS_PAGE_SIZE)
  const [sortBy, setSortBy] = useState(PURCHASE_SALES_ANALYSIS_DEFAULT_SORT_BY)
  const [sortOrder, setSortOrder] = useState<SortOrderState>(PURCHASE_SALES_ANALYSIS_DEFAULT_SORT_ORDER)
  const [loading, setLoading] = useState(false)
  const [result, setResult] = useState<LocalSupplierPurchaseSalesAnalysisResponseDto | null>(null)
  const [hasSearched, setHasSearched] = useState(false)
  const [expandedKeys, setExpandedKeys] = useState<string[]>([])
  const [queryVersion, setQueryVersion] = useState(0)
  const [tableScrollY, setTableScrollY] = useState<number>(PURCHASE_SALES_ANALYSIS_MIN_TABLE_BODY_HEIGHT)
  const [columnOrder, setColumnOrder] = useState<LocalSupplierPurchaseSalesAnalysisColumnKey[]>([])

  const wrapRef = useRef<HTMLDivElement>(null)
  const toolbarRef = useRef<HTMLDivElement>(null)
  const columnDragSensors = useSensors(
    useSensor(PointerSensor, {
      activationConstraint: {
        distance: 6,
      },
    }),
  )

  const pageSizeOptions = useMemo(
    () => PURCHASE_SALES_ANALYSIS_PAGE_SIZE_OPTIONS.map((item) => String(item)),
    [],
  )
  const fallbackStoreOptions = useMemo(
    () => buildStoreOptionsFromUserStores(currentUser?.stores, { manageableOnly: true }),
    [currentUser?.stores],
  )
  // 查询本身必须同时选定分店和供应商，未选分店时预拉全部门店的供应商候选要在库里扫几十万行进货明细、
  // 耗时接近 10 秒且对结果没有帮助，所以所有角色都等分店选定后再加载供应商。
  const requiresStoreSelectionBeforeSupplierOptions = !draftFilters.storeCode
  const hasRequiredDraftFilters = Boolean(draftFilters.storeCode && draftFilters.supplierCode)
  const hasRequiredCommittedFilters = Boolean(filters.storeCode && filters.supplierCode)

  useLayoutEffect(() => {
    const calc = () => {
      // 嵌入标签页时外层没有固定高度，clientHeight 只是内容高度；改按视口剩余空间估算表格可用高度。
      const containerHeight = embedded
        ? window.innerHeight - (wrapRef.current?.getBoundingClientRect().top ?? 0)
        : wrapRef.current?.clientHeight || window.innerHeight
      const toolbarHeight = toolbarRef.current?.getBoundingClientRect().height || 0
      const available = containerHeight - toolbarHeight - 250
      setTableScrollY(resolvePurchaseSalesAnalysisTableBodyHeight(available))
    }

    calc()
    window.addEventListener('resize', calc)
    return () => window.removeEventListener('resize', calc)
  }, [embedded, result?.items.length])

  useEffect(() => {
    let cancelled = false

    const loadStoreOptions = async () => {
      setStoreOptionsLoading(true)
      try {
        const stores = await getLocalSupplierPurchaseSalesAnalysisStoreOptions()
        if (!cancelled) {
          setStoreOptions(stores.length > 0 ? stores : fallbackStoreOptions)
        }
      } catch {
        if (!cancelled) {
          setStoreOptions(fallbackStoreOptions)
        }
      } finally {
        if (!cancelled) {
          setStoreOptionsLoading(false)
        }
      }
    }

    void loadStoreOptions()
    return () => {
      cancelled = true
    }
  }, [fallbackStoreOptions])

  useEffect(() => {
    if (!singleScopedStoreCode) {
      return
    }

    // 单门店用户默认带上唯一分店，避免页面看起来必须手动补一个已知条件。
    setDraftFilters((current) =>
      current.storeCode ? current : { ...current, storeCode: singleScopedStoreCode, supplierCode: undefined },
    )
    setFilters((current) =>
      current.storeCode ? current : { ...current, storeCode: singleScopedStoreCode, supplierCode: undefined },
    )
  }, [singleScopedStoreCode])

  useEffect(() => {
    let cancelled = false

    const loadSupplierOptions = async () => {
      if (requiresStoreSelectionBeforeSupplierOptions) {
        // 多门店普通用户必须先选分店；这里不预请求供应商，避免触发后端 STORE_REQUIRED。
        setSupplierOptionsLoading(false)
        setSupplierOptions([])
        setDraftFilters((current) =>
          current.supplierCode ? { ...current, supplierCode: undefined } : current,
        )
        return
      }

      setSupplierOptionsLoading(true)
      try {
        const suppliers = await getLocalSupplierPurchaseSalesAnalysisSupplierOptions(draftFilters.storeCode)
        if (cancelled) {
          return
        }

        setSupplierOptions(suppliers)
        setDraftFilters((current) => {
          if (!current.supplierCode || suppliers.some((supplier) => supplier.value === current.supplierCode)) {
            return current
          }

          // 分店变化后清掉不再属于该分店进货数据的供应商，避免提交出空结果。
          return { ...current, supplierCode: undefined }
        })
      } catch {
        if (!cancelled) {
          setSupplierOptions([])
        }
      } finally {
        if (!cancelled) {
          setSupplierOptionsLoading(false)
        }
      }
    }

    void loadSupplierOptions()
    return () => {
      cancelled = true
    }
  }, [draftFilters.storeCode, requiresStoreSelectionBeforeSupplierOptions])

  useEffect(() => {
    let cancelled = false
    const supplierCode = draftFilters.supplierCode
    if (!supplierCode) {
      setCategoryTree([])
      setCategoryTreeError(false)
      setDraftFilters((current) => current.supplierCategoryGuids.length ? { ...current, supplierCategoryGuids: [] } : current)
      return () => { cancelled = true }
    }
    const controller = new AbortController()
    setCategoryTreeError(false)
    setCategoryTreeLoading(true)
    void getLocalSupplierPurchaseSalesAnalysisCategoryTree(supplierCode, draftFilters.storeCode, controller.signal).then((tree) => {
      if (!cancelled) setCategoryTree(tree)
    }).catch(() => {
      if (!cancelled && !controller.signal.aborted) {
        setCategoryTree([])
        setCategoryTreeError(true)
      }
    }).finally(() => {
      if (!cancelled) setCategoryTreeLoading(false)
    })
    return () => { cancelled = true; controller.abort() }
  }, [draftFilters.supplierCode, draftFilters.storeCode, categoryTreeReloadToken])

  const categoryTreeData = useMemo(() => {
    const mapNodes = (nodes: LocalSupplierCategoryNode[]): CategoryTreeOption[] => nodes
      .filter((node) => node.isActive)
      .map((node) => ({
        key: node.categoryGuid,
        value: node.categoryGuid,
        title: node.name,
        children: mapNodes(node.children || []),
      }))
    return mapNodes(categoryTree)
  }, [categoryTree])

  const loadData = useCallback(
    async (signal?: AbortSignal) => {
      if (!filters.storeCode || !filters.supplierCode) {
        setResult(null)
        return
      }

      const query: LocalSupplierPurchaseSalesAnalysisQueryDto = {
        storeCode: filters.storeCode,
        supplierCode: filters.supplierCode,
        supplierCategoryGuids: filters.supplierCategoryGuids.length ? filters.supplierCategoryGuids : undefined,
        orderDateStart: filters.orderDateRange[0].format('YYYY-MM-DD'),
        orderDateEnd: filters.orderDateRange[1].format('YYYY-MM-DD'),
        keyword: filters.keyword || undefined,
        sortBy,
        sortOrder,
        page,
        pageSize,
      }

      setLoading(true)
      try {
        const data = await getLocalSupplierPurchaseSalesAnalysis(query, signal)
        setResult(data)
        // 每次新结果默认只展开第一行，与订货前台保持一致。
        setExpandedKeys(resolveDefaultExpandedKeys(data.items, getAnalysisRowKey))
      } catch (error) {
        if (!signal?.aborted) {
          message.error(
            error instanceof Error
              ? error.message
              : t(
                  'posAdmin.localSupplierPurchaseSalesAnalysis.loadFailed',
                  '分店供应商进货销量分析加载失败',
                ),
          )
        }
      } finally {
        if (!signal?.aborted) {
          setLoading(false)
        }
      }
    },
    [filters, page, pageSize, sortBy, sortOrder, t],
  )

  useEffect(() => {
    if (!hasSearched || !hasRequiredCommittedFilters) {
      setResult(null)
      return
    }

    const controller = new AbortController()
    void loadData(controller.signal)
    return () => controller.abort()
  }, [hasRequiredCommittedFilters, hasSearched, loadData, queryVersion])

  const baseColumns = useMemo<ColumnsType<LocalSupplierPurchaseSalesAnalysisRowDto>>(
    () => [
      {
        title: t('posAdmin.localSupplierPurchaseSalesAnalysis.columns.image', '图片'),
        key: 'image',
        width: 76,
        fixed: 'left',
        render: (_value, record) => (
          <ProductImageCell
            productImage={record.productImage}
            itemNumber={record.itemNumber}
            productCode={record.productCode}
            alt={record.productName || record.productCode}
          />
        ),
      },
      {
        title: t('posAdmin.localSupplierPurchaseSalesAnalysis.columns.product', '货号 / 名称'),
        key: 'itemNumber',
        dataIndex: 'itemNumber',
        width: 236,
        fixed: 'left',
        sorter: true,
        sortOrder: sortBy === 'itemNumber' ? (sortOrder === 'asc' ? 'ascend' : 'descend') : null,
        render: (_value, record) => (
          <Space direction="vertical" size={0}>
            <Text strong>{record.productName || '--'}</Text>
            <Text style={{ color: '#0958d9' }}>{record.itemNumber || record.productCode}</Text>
            <Text type="secondary">{record.barcode || '--'}</Text>
          </Space>
        ),
      },
      {
        title: t('posAdmin.localSupplierPurchaseSalesAnalysis.columns.supplier', '供应商'),
        dataIndex: 'supplierCode',
        key: 'supplierName',
        width: 140,
        render: (_value, record) => (
          <Tag color="purple" style={{ marginInlineEnd: 0 }}>
            {record.supplierName || record.supplierCode}
          </Tag>
        ),
      },
      {
        title: t('posAdmin.localSupplierPurchaseSalesAnalysis.columns.previousPurchase', '上次进货'),
        dataIndex: 'previousPurchaseDate',
        key: 'previousPurchaseDate',
        width: 130,
        sorter: true,
        sortOrder:
          sortBy === 'previousPurchaseDate' ? (sortOrder === 'asc' ? 'ascend' : 'descend') : null,
        render: (_value, record) => formatPurchase(record.previousPurchaseDate, record.previousPurchaseQty, 'previous'),
      },
      {
        title: t('posAdmin.localSupplierPurchaseSalesAnalysis.columns.latestPurchase', '最近进货'),
        dataIndex: 'latestPurchaseDate',
        key: 'latestPurchaseDate',
        width: 130,
        sorter: true,
        sortOrder: sortBy === 'latestPurchaseDate' ? (sortOrder === 'asc' ? 'ascend' : 'descend') : null,
        render: (_value, record) => formatPurchase(record.latestPurchaseDate, record.latestPurchaseQty, 'latest'),
      },
      {
        title: t('posAdmin.localSupplierPurchaseSalesAnalysis.columns.intervalSales', '间隔销量'),
        dataIndex: 'salesBetweenPurchases',
        key: 'salesBetweenPurchases',
        width: 100,
        align: 'right',
        sorter: true,
        sortOrder:
          sortBy === 'salesBetweenPurchases' ? (sortOrder === 'asc' ? 'ascend' : 'descend') : null,
        render: (value: number | null | undefined) => <MetricBadge value={value} tone="between" />,
      },
      {
        // 逐日销量与进货事件直接画在行内，取代原来的 30/60/90 天汇总列；该列不参与后端排序。
        title: t('purchaseSalesTrend.columns.dailyTrend', '日销量与进货'),
        key: 'dailyTrend',
        // 排序按后端聚合的总销量（最近进货后累计销量）执行；销量类字段点击先看最高，故降序优先。
        dataIndex: 'totalSalesSinceLatestPurchase',
        sorter: true,
        sortDirections: ['descend', 'ascend'],
        sortOrder: sortBy === 'totalSalesSinceLatestPurchase' ? (sortOrder === 'asc' ? 'ascend' : 'descend') : null,
        width: 340,
        render: (_value, record) => {
          if (!record.dailySales.length) {
            return <Text type="secondary">{t('purchaseSalesTrend.chart.noDaily', '该商品尚无逐日销量统计。')}</Text>
          }
          const metrics = buildPurchaseSalesTrendMetrics(record)
          return (
            <div>
              <PurchaseSalesSparkline row={record} />
              <Text type="secondary" style={{ fontSize: 12 }}>
                {t('purchaseSalesTrend.columns.trendCaption', '最近进货 {{purchased}} 件 · 其后 {{days}} 天售出 {{total}} 件 · 日均 {{average}}', {
                  purchased: formatNumber(metrics.purchasedQuantity),
                  days: metrics.sinceLatest.length,
                  total: formatNumber(metrics.totalSinceLatest),
                  average: metrics.averagePerDay.toFixed(1),
                })}
              </Text>
            </div>
          )
        },
      },
      {
        title: t('purchaseSalesTrend.columns.sellThrough', '售出比'),
        key: 'sellThrough',
        width: 100,
        align: 'right',
        render: (_value, record) => {
          const ratio = buildPurchaseSalesTrendMetrics(record).sellThrough
          if (ratio === null) {
            return '--'
          }
          // 售出比超过 100% 说明按进货量估算已卖完，用红色提示优先补货。
          return (
            <Text strong style={{ color: ratio >= 1 ? '#cf1322' : ratio >= 0.7 ? '#d46b08' : undefined, fontVariantNumeric: 'tabular-nums' }}>
              {Math.round(ratio * 100)}%
            </Text>
          )
        },
      },
      {
        title: t('posAdmin.localSupplierPurchaseSalesAnalysis.columns.updatedAt', '统计更新时间'),
        dataIndex: 'salesStatisticLastUpdate',
        key: 'salesStatisticLastUpdate',
        width: 170,
        render: (value: string | null | undefined) => formatDateTime(value),
      },
    ],
    [sortBy, sortOrder, t],
  )

  const draggableColumnKeys = useMemo(() => {
    const baseColumnKeys = new Set(baseColumns.map((column) => String(column.key)))
    return LOCAL_SUPPLIER_PURCHASE_SALES_ANALYSIS_DEFAULT_COLUMN_ORDER.filter((key) =>
      baseColumnKeys.has(key),
    )
  }, [baseColumns])
  const draggableColumnKeySignature = draggableColumnKeys.join('|')
  const isColumnOrderCustomized = isLocalSupplierPurchaseSalesAnalysisColumnOrderCustomized(
    columnOrder,
    draggableColumnKeys,
  )

  useEffect(() => {
    setColumnOrder((current) => {
      let savedOrder: unknown = null
      if (!current.length && typeof window !== 'undefined') {
        try {
          const raw = localStorage.getItem(LOCAL_SUPPLIER_PURCHASE_SALES_ANALYSIS_COLUMN_ORDER_STORAGE_KEY)
          savedOrder = raw ? JSON.parse(raw) : null
        } catch {
          savedOrder = null
        }
      }

      // 列顺序只管理右侧业务列；图片和货号名称固定在左侧，避免拖拽破坏阅读锚点。
      const nextOrder = mergeLocalSupplierPurchaseSalesAnalysisColumnOrder(
        current.length ? current : savedOrder,
        draggableColumnKeys,
      )
      if (current.length === nextOrder.length && current.every((key, index) => key === nextOrder[index])) {
        return current
      }
      return nextOrder
    })
  }, [draggableColumnKeySignature])

  const handleColumnDragEnd = ({ active, over }: DragEndEvent) => {
    if (!over || active.id === over.id) return
    setColumnOrder((current) => {
      const nextOrder = moveLocalSupplierPurchaseSalesAnalysisColumnOrder(current, active.id, over.id)
      try {
        localStorage.setItem(
          LOCAL_SUPPLIER_PURCHASE_SALES_ANALYSIS_COLUMN_ORDER_STORAGE_KEY,
          JSON.stringify(nextOrder),
        )
      } catch {
        // localStorage 不可用时不影响当前页面内拖拽排序。
      }
      return nextOrder
    })
  }

  const handleResetColumnOrder = () => {
    setColumnOrder(draggableColumnKeys)
    try {
      localStorage.removeItem(LOCAL_SUPPLIER_PURCHASE_SALES_ANALYSIS_COLUMN_ORDER_STORAGE_KEY)
    } catch {
      // localStorage 不可用时仍恢复当前页面内的默认列顺序。
    }
    message.success(t('containers.messages.columnOrderReset', '列设置已恢复默认'))
  }

  const orderedColumns = useMemo<ColumnsType<LocalSupplierPurchaseSalesAnalysisRowDto>>(() => {
    const activeOrder = columnOrder.length ? columnOrder : draggableColumnKeys
    const columnMap = new Map(baseColumns.map((column) => [String(column.key), column]))
    const fixedColumns = baseColumns.filter((column) =>
      STATIC_PURCHASE_SALES_ANALYSIS_COLUMN_KEYS.has(String(column.key)),
    )

    const draggableColumns = activeOrder
      .map((key) => columnMap.get(key))
      .filter((column): column is ColumnsType<LocalSupplierPurchaseSalesAnalysisRowDto>[number] => Boolean(column))
      .map((column) => ({
        ...column,
        onHeaderCell: () => ({
          'data-column-key': String(column.key),
        }),
      }))

    return [...fixedColumns, ...draggableColumns] as ColumnsType<LocalSupplierPurchaseSalesAnalysisRowDto>
  }, [baseColumns, columnOrder, draggableColumnKeySignature])

  const handleSearch = () => {
    if (!draftFilters.storeCode || !draftFilters.supplierCode) {
      setResult(null)
      message.warning(
        t(
          'posAdmin.localSupplierPurchaseSalesAnalysis.validation.requiredFilters',
          '请先选择分店和供应商，再查询进货销量分析。',
        ),
      )
      return
    }

    // 查询按钮只在用户确认后提交关键词，避免输入过程频繁触发表格请求。
    setFilters({
      storeCode: draftFilters.storeCode,
      supplierCode: draftFilters.supplierCode,
      supplierCategoryGuids: draftFilters.supplierCategoryGuids,
      keyword: draftFilters.keyword.trim(),
      orderDateRange: draftFilters.orderDateRange,
    })
    setPage(1)
    setHasSearched(true)
    setQueryVersion((current) => current + 1)
  }

  const handleReset = () => {
    const nextFilters = buildInitialFilters()
    nextFilters.storeCode = singleScopedStoreCode
    setDraftFilters(nextFilters)
    setFilters(nextFilters)
    setResult(null)
    setHasSearched(false)
    setPage(1)
    setPageSize(DEFAULT_PURCHASE_SALES_ANALYSIS_PAGE_SIZE)
    setSortBy(PURCHASE_SALES_ANALYSIS_DEFAULT_SORT_BY)
    setSortOrder(PURCHASE_SALES_ANALYSIS_DEFAULT_SORT_ORDER)
  }

  const handleRefresh = () => {
    if (!hasRequiredCommittedFilters) {
      return
    }

    setQueryVersion((current) => current + 1)
  }

  const handleTableChange = (
    pagination: TablePaginationConfig,
    _tableFilters: Record<string, FilterValue | null>,
    sorter:
      | SorterResult<LocalSupplierPurchaseSalesAnalysisRowDto>
      | SorterResult<LocalSupplierPurchaseSalesAnalysisRowDto>[],
  ) => {
    const singleSorter = Array.isArray(sorter) ? sorter[0] : sorter
    const nextPageSize = normalizePurchaseSalesAnalysisPageSize(pagination.pageSize)
    const nextSort = toPurchaseSalesAnalysisSort(
      singleSorter?.field ? String(singleSorter.field) : undefined,
      singleSorter?.order,
    )

    // 列头排序必须透传到后端，不能只在当前页本地排序。
    setSortBy(nextSort.sortBy)
    setSortOrder(nextSort.sortOrder)

    if (nextPageSize !== pageSize) {
      setPage(1)
      setPageSize(nextPageSize)
      return
    }

    setPage(pagination.current ?? 1)
    setPageSize(nextPageSize)
  }

  return (
    <div ref={wrapRef} style={{ height: '100%' }}>
      <Space direction="vertical" size={12} style={{ width: '100%' }}>
        {embedded ? null : (
          <Space direction="vertical" size={4}>
            <Title level={4} style={{ margin: 0 }}>
              {t('posAdmin.localSupplierPurchaseSalesAnalysis.title', '分店供应商进货销量分析')}
            </Title>
            <Text type="secondary">
              {t(
                'posAdmin.localSupplierPurchaseSalesAnalysis.subtitle',
                '按分店、供应商和订单日期范围查看商品最近进货与后续销量表现。',
              )}
            </Text>
          </Space>
        )}

        <div ref={toolbarRef}>
          <Card size="small">
            <Space wrap size={8}>
              <Select
                allowClear
                showSearch
                style={{ width: 240 }}
                placeholder={t('posAdmin.localSupplierPurchaseSalesAnalysis.filters.store', '分店')}
                optionFilterProp="label"
                value={draftFilters.storeCode}
                loading={storeOptionsLoading}
                notFoundContent={
                  storeOptionsLoading
                    ? t('common.loading', '加载中')
                    : t('posAdmin.localSupplierPurchaseSalesAnalysis.filters.noStores', '暂无可选分店')
                }
                options={storeOptions}
                onChange={(value) => {
                  setHasSearched(false)
                  setResult(null)
                  setDraftFilters((current) => ({ ...current, storeCode: value, supplierCode: undefined, supplierCategoryGuids: [] }))
                }}
              />
              <Select
                allowClear
                showSearch
                style={{ width: 240 }}
                placeholder={t('posAdmin.localSupplierPurchaseSalesAnalysis.filters.supplier', '供应商')}
                optionFilterProp="label"
                value={draftFilters.supplierCode}
                loading={supplierOptionsLoading}
                notFoundContent={
                  supplierOptionsLoading
                    ? t('common.loading', '加载中')
                    : requiresStoreSelectionBeforeSupplierOptions
                      ? t('posAdmin.localSupplierPurchaseSalesAnalysis.filters.selectStoreFirst', '请先选择分店')
                      : t('posAdmin.localSupplierPurchaseSalesAnalysis.filters.noSuppliers', '暂无可选供应商')
                }
                options={supplierOptions}
                onChange={(value) => {
                  setHasSearched(false)
                  setResult(null)
                  setDraftFilters((current) => ({ ...current, supplierCode: value, supplierCategoryGuids: [] }))
                }}
              />
              <div style={{ width: 280, minWidth: 220 }}>
                <TreeSelect
                  allowClear
                  treeCheckable
                  multiple
                  treeData={categoryTreeData}
                  showSearch
                  treeNodeFilterProp="title"
                  treeDefaultExpandAll
                  showCheckedStrategy={TreeSelect.SHOW_PARENT}
                  maxTagCount="responsive"
                  style={{ width: '100%' }}
                  placeholder={t('posAdmin.localSupplierPurchaseSalesAnalysis.filters.supplierCategory', '供应商分类（可多选）')}
                  value={draftFilters.supplierCategoryGuids}
                  loading={categoryTreeLoading}
                  disabled={!draftFilters.supplierCode || categoryTreeLoading || categoryTreeError}
                  notFoundContent={draftFilters.supplierCode ? t('posAdmin.localSupplierPurchaseSalesAnalysis.filters.noCategories', '暂无启用分类') : t('posAdmin.localSupplierPurchaseSalesAnalysis.filters.selectSupplierFirst', '请先选择供应商')}
                  onChange={(values) => {
                    const nextValues = (values as string[]) || []
                    if (nextValues.length > MAX_SUPPLIER_CATEGORY_SELECTIONS) {
                      message.warning(
                        t(
                          'posAdmin.localSupplierPurchaseSalesAnalysis.validation.categoryLimit',
                          '供应商分类最多选择 {{count}} 个。',
                          { count: MAX_SUPPLIER_CATEGORY_SELECTIONS },
                        ),
                      )
                    }
                    setDraftFilters((current) => ({
                      ...current,
                      supplierCategoryGuids: nextValues.slice(0, MAX_SUPPLIER_CATEGORY_SELECTIONS),
                    }))
                  }}
                />
                {categoryTreeError ? (
                  <Space size={4} style={{ marginTop: 4 }}>
                    <Text type="danger">
                      {t('posAdmin.localSupplierPurchaseSalesAnalysis.filters.categoryLoadFailed', '供应商分类加载失败')}
                    </Text>
                    <Button
                      type="link"
                      size="small"
                      style={{ paddingInline: 0 }}
                      onClick={() => setCategoryTreeReloadToken((value) => value + 1)}
                    >
                      {t('common.retry', '重试')}
                    </Button>
                  </Space>
                ) : null}
              </div>
              <RangePicker
                allowClear={false}
                value={draftFilters.orderDateRange}
                onChange={(value) => {
                  if (value?.[0] && value?.[1]) {
                    const [startDate, endDate] = value
                    setDraftFilters((current) => {
                      const nextRange: DateRangeValue = [startDate, endDate]
                      return {
                        ...current,
                        orderDateRange: nextRange,
                      }
                    })
                  }
                }}
              />
              <Input
                allowClear
                style={{ width: 280 }}
                placeholder={t('posAdmin.localSupplierPurchaseSalesAnalysis.filters.keyword', '货号 / 条码 / 名称')}
                value={draftFilters.keyword}
                onChange={(event) => {
                  const nextKeyword = event.target.value
                  setDraftFilters((current) => ({ ...current, keyword: nextKeyword }))
                }}
                onPressEnter={handleSearch}
              />
              <Button
                icon={<SearchOutlined />}
                type="primary"
                disabled={!hasRequiredDraftFilters}
                onClick={handleSearch}
              >
                {t('common.search', '查询')}
              </Button>
              <Button onClick={handleReset}>{t('common.reset', '重置')}</Button>
              <Button
                icon={<ReloadOutlined />}
                disabled={!hasSearched || !hasRequiredCommittedFilters}
                onClick={handleRefresh}
              >
                {t('posAdmin.localSupplierPurchaseSalesAnalysis.refresh', '刷新')}
              </Button>
              <Button
                icon={<ReloadOutlined />}
                disabled={!isColumnOrderCustomized}
                onClick={handleResetColumnOrder}
              >
                {t('containers.actions.resetColumns', '重置列')}
              </Button>
            </Space>
          </Card>
        </div>

        <Card size="small">
          <Space direction="vertical" size={8} style={{ width: '100%' }}>
            {hasSearched && result ? (
              <Space wrap>
                <Text type="secondary">{t('purchaseSalesTrend.calculationNote', '进货按订单日期范围过滤、按进货发生日期汇总；日销量从上次进货起逐日展示，售出比与累计销量从最近进货当天起统计。')}</Text>
                <Text type="secondary">
                  {t('posAdmin.localSupplierPurchaseSalesAnalysis.summary.updatedAt', '统计更新时间')}：
                  {formatDateTime(result.salesStatisticLastUpdate)}
                </Text>
              </Space>
            ) : (
              <Text type="secondary">
                {t(
                  'posAdmin.localSupplierPurchaseSalesAnalysis.emptyBeforeSearch',
                  '请选择分店和供应商后点击查询。',
                )}
              </Text>
            )}
            <DndContext sensors={columnDragSensors} collisionDetection={closestCenter} onDragEnd={handleColumnDragEnd}>
              <SortableContext
                items={columnOrder.length ? columnOrder : draggableColumnKeys}
                strategy={horizontalListSortingStrategy}
              >
                <MeasuredTable<LocalSupplierPurchaseSalesAnalysisRowDto> metricId="pos-admin.local-supplier-purchase-sales-analysis.table-1"
                  size="small"
                  rowKey={getAnalysisRowKey}
                  loading={loading}
                  components={{ header: { cell: DraggableHeaderCell } }}
                  columns={orderedColumns}
                  dataSource={hasSearched ? result?.items ?? [] : []}
                  locale={{
                    emptyText: (
                      <Empty
                        description={
                          hasSearched
                            ? t(
                                'posAdmin.localSupplierPurchaseSalesAnalysis.empty',
                                '当前条件下暂无分店供应商进货销量数据。',
                              )
                            : t(
                                'posAdmin.localSupplierPurchaseSalesAnalysis.emptyBeforeSearch',
                                '请选择分店和供应商后点击查询。',
                              )
                        }
                      />
                    ),
                  }}
                  scroll={{ x: 1440, y: tableScrollY }}
                  virtual
                  expandable={{
                    // 点击行展开大图：日销量柱、进货事件标记与进货后累计销量线。
                    expandRowByClick: true,
                    // 手风琴展开：展开一个商品时收起其它商品，页面只保留一张大图。
                    expandedRowKeys: expandedKeys,
                    onExpand: (expanded, record) =>
                      setExpandedKeys(resolveAccordionExpandedKeys(expanded, getAnalysisRowKey(record))),
                    rowExpandable: (record) => record.dailySales.length > 0,
                    expandedRowRender: (record) => (
                      <div style={{ padding: '4px 8px 8px' }}>
                        <PurchaseSalesDailyChart
                          row={record}
                          title={`${record.itemNumber || record.productCode} · ${record.productName || ''}`.trim()}
                        />
                      </div>
                    ),
                  }}
                  pagination={{
                    current: page,
                    pageSize,
                    total: hasSearched ? result?.total ?? 0 : 0,
                    showSizeChanger: true,
                    pageSizeOptions,
                    showTotal: (total) =>
                      t('posAdmin.localSupplierPurchaseSalesAnalysis.summary.total', '共 {{count}} 条', {
                        count: total,
                      }),
                  }}
                  onChange={handleTableChange}
                />
              </SortableContext>
            </DndContext>
          </Space>
        </Card>
      </Space>
    </div>
  )
}
