import {
  ArrowDownOutlined,
  DollarOutlined,
  EditOutlined,
  InfoCircleOutlined,
  PictureOutlined,
  ReloadOutlined,
  SearchOutlined,
  SettingOutlined,
} from '@ant-design/icons'
import {
  App as AntdApp,
  Button,
  Checkbox,
  Col,
  DatePicker,
  Empty,
  Form,
  Input,
  InputNumber,
  Modal,
  Popover,
  Row,
  Segmented,
  Select,
  Space,
  Statistic,
  Tooltip,
  Typography,
} from 'antd'
import type { ColumnsType, TablePaginationConfig } from 'antd/es/table'
import type { InputRef } from 'antd/es/input'
import type { SorterResult } from 'antd/es/table/interface'
import dayjs, { type Dayjs } from 'dayjs'
import { useCallback, useEffect, useMemo, useRef, useState, type CSSProperties, type KeyboardEvent, type Key } from 'react'
import { Trans, useTranslation } from 'react-i18next'
import { useNavigate } from 'react-router-dom'
import PageContainer from '../../../components/PageContainer'
import ProductListImage from '../../../components/ProductListImage'
import ActiveFilterBar, { type ActiveFilterItem } from '../../../components/listToolbar/ActiveFilterBar'
import MoreFiltersButton from '../../../components/listToolbar/MoreFiltersButton'
import SelectionActionBar from '../../../components/listToolbar/SelectionActionBar'
import { registerPageMessages } from '../../../i18n/registerPageMessages'
import { getActiveChinaSuppliers } from '../../../services/chinaSupplierService'
import {
  batchUpdateStoreOrderImportPriceVarianceWarehouseImportPrice,
  getStoreOrderImportPriceVariance,
  getStoreOrderImportPriceVarianceDetails,
  updateStoreOrderImportPriceVarianceDomesticPrice,
  updateStoreOrderImportPriceVarianceWarehouseImportPrice,
} from '../../../services/storeOrderService'
import type { ChinaSupplierItem } from '../../../types/chinaSupplier'
import type {
  StoreOrderImportPriceVarianceDetailItem,
  StoreOrderImportPriceVarianceDirection,
  StoreOrderImportPriceVarianceItem,
  StoreOrderImportPriceVarianceQuery,
  StoreOrderImportPriceVarianceSummary,
  StoreOrderImportPriceVarianceSupplierSummary,
} from '../../../types/storeOrder'
import { createLatestRequestGuard } from '../../../utils/latestRequestGuard'
import { MeasuredTable } from '../../../components/MeasuredTable'
import {
  SUPPLIER_PREVIEW_COUNT,
  VARIANCE_TONE_COLORS,
  formatAmount,
  formatQuantity,
  formatSignedAmount,
  getFilterSignature,
  getMaxAbsVariance,
  getSupplierRankingKey,
  getSupplierRowKey,
  getVarianceBarWidths,
  getVarianceTone,
  getVisibleSupplierRows,
  sortSupplierSummaries,
  summarizeSupplierTotals,
  type SupplierSort,
  type SupplierSortKey,
} from './priceVariance.logic'
import priceVarianceMessagesEn from './priceVarianceMessages.en.json'
import priceVarianceMessagesZh from './priceVarianceMessages.zh.json'
import './styles.css'

// 页面重设计新增的文案随页面懒注册，不放进首屏全局语言包。
registerPageMessages({ zh: priceVarianceMessagesZh, en: priceVarianceMessagesEn })

const { RangePicker } = DatePicker

type RangeValue = [Dayjs | null, Dayjs | null] | null
type EditablePriceField = 'domesticPrice' | 'warehouseImportPrice'
/** 列设置里可选显示的低频列。 */
type OptionalColumnKey = 'unitVolume' | 'packingQuantity'

interface FilterValues {
  keyword?: string
  storeCode?: string
  supplierCode?: string
  orderNo?: string
  orderDateRange?: RangeValue
  varianceDirection?: StoreOrderImportPriceVarianceDirection
}

interface AppliedFilters {
  keyword?: string
  storeCode?: string
  supplierCode?: string
  orderNo?: string
  startDate?: string
  endDate?: string
  varianceDirection: StoreOrderImportPriceVarianceDirection
}

interface BatchWarehouseImportPriceFormValues {
  warehouseImportPrice?: number
}

interface SupplierOption {
  label: string
  value: string
}

interface DomesticSupplierFilterSelectProps {
  value?: string
  loading: boolean
  options: SupplierOption[]
  placeholder: string
  prefix?: string
  style?: CSSProperties
  onChange?: (value?: string) => void
  onOpenChange: (open: boolean) => void
}

/** 供应商排行：按「除供应商外的筛选条件」缓存，点选供应商后仍显示同条件下的全部供应商。 */
interface SupplierRanking {
  key: string
  rows: StoreOrderImportPriceVarianceSupplierSummary[]
}

const DEFAULT_PAGE_SIZE = 20
const DEFAULT_SORT_BY = 'absoluteVarianceAmount'
const DEFAULT_SORT_DESCENDING = true
const DEFAULT_DETAIL_SORT_BY = 'orderDate'
const DEFAULT_DETAIL_SORT_DESCENDING = true
/** 文本类筛选（关键字、分店编码、订单号）输入停顿后再查询。 */
const FILTER_DEBOUNCE_MS = 300
const INITIAL_FILTER_VALUES: FilterValues = { varianceDirection: 'all' }

const emptySummary: StoreOrderImportPriceVarianceSummary = {
  totalRows: 0,
  originalImportAmountTotal: 0,
  baselineImportAmountTotal: 0,
  varianceAmountTotal: 0,
}

function trimText(value?: string) {
  const text = value?.trim()
  return text || undefined
}

function formatDate(value?: string, language?: string) {
  if (!value) {
    return '--'
  }

  const date = new Date(value)
  if (Number.isNaN(date.getTime())) {
    return value
  }

  return date.toLocaleDateString(language?.startsWith('zh') ? 'zh-CN' : 'en-US')
}

/** 首次货柜日期：跨年数据多，统一显示完整日期。 */
function formatContainerDate(value?: string) {
  if (!value) return '--'
  const date = dayjs(value)
  return date.isValid() ? date.format('YYYY-MM-DD') : value
}

function formatMoney(value?: number) {
  return (value ?? 0).toFixed(2)
}

function formatNumber(value?: number, fractionDigits = 2) {
  const number = value ?? 0
  return Number.isInteger(number) ? String(number) : number.toFixed(fractionDigits)
}

function parsePriceDraft(value: string) {
  const normalized = value.trim().replace(/,/g, '')
  if (!normalized) {
    return null
  }

  const parsed = Number(normalized)
  if (!Number.isFinite(parsed) || parsed < 0) {
    return null
  }

  return Math.round(parsed * 100) / 100
}

function getRowKey(row: StoreOrderImportPriceVarianceItem) {
  return row.productCode || row.itemNumber || row.productName || 'product'
}

function getEditablePriceInputKey(row: StoreOrderImportPriceVarianceItem, field: EditablePriceField) {
  return `${field}:${getRowKey(row)}`
}

function getEditablePriceValue(row: StoreOrderImportPriceVarianceItem, field: EditablePriceField) {
  return field === 'domesticPrice' ? row.domesticPrice : row.warehouseImportPrice
}

function getDetailRowKey(row: StoreOrderImportPriceVarianceDetailItem) {
  return `${row.orderGUID || 'order'}-${row.detailGUID || row.productCode || row.itemNumber || 'detail'}`
}

function isAbortError(error: unknown) {
  return error instanceof DOMException && error.name === 'AbortError'
}

function normalizeFilters(values: FilterValues): AppliedFilters {
  return {
    keyword: trimText(values.keyword),
    storeCode: trimText(values.storeCode),
    supplierCode: trimText(values.supplierCode),
    orderNo: trimText(values.orderNo),
    // 日期范围统一落成后端契约字段，避免页面状态保存 Dayjs 对象。
    startDate: values.orderDateRange?.[0]?.format('YYYY-MM-DD'),
    endDate: values.orderDateRange?.[1]?.format('YYYY-MM-DD'),
    varianceDirection: values.varianceDirection ?? 'all',
  }
}

function DomesticSupplierFilterSelect({
  value,
  loading,
  options,
  placeholder,
  prefix,
  style,
  onChange,
  onOpenChange,
}: DomesticSupplierFilterSelectProps) {
  return (
    <Select
      allowClear
      showSearch
      value={value}
      loading={loading}
      options={options}
      placeholder={placeholder}
      prefix={prefix}
      style={style}
      aria-label={prefix ?? placeholder}
      onChange={onChange}
      onOpenChange={onOpenChange}
      filterOption={(input, option) =>
        String(option?.label ?? '').toLowerCase().includes(input.trim().toLowerCase()) ||
        String(option?.value ?? '').toLowerCase().includes(input.trim().toLowerCase())
      }
    />
  )
}

/** 差额双向条：中线左侧蓝色为少收、右侧橙色为多收，长度按当前列表最大绝对值归一。 */
const PRODUCT_TABLE_BASE_WIDTH = 1096
const PRODUCT_OPTIONAL_COLUMN_WIDTH = 96

function VarianceBar({ value, maxAbs, width }: { value?: number; maxAbs: number; width: number }) {
  const bar = getVarianceBarWidths(value, maxAbs)
  return (
    <span className="wh-pv-bar" style={{ width }} aria-hidden="true">
      <span className="wh-pv-bar-half wh-pv-bar-under-half">
        <span className="wh-pv-bar-under" style={{ width: `${bar.under}%` }} />
      </span>
      <span className="wh-pv-bar-half">
        <span className="wh-pv-bar-over" style={{ width: `${bar.over}%` }} />
      </span>
    </span>
  )
}

function SignedVariance({ value }: { value?: number }) {
  return <span className={`wh-pv-variance wh-pv-tone-${getVarianceTone(value)}`}>{formatSignedAmount(value)}</span>
}

export default function StoreOrderImportPriceVariancePage() {
  const { t, i18n } = useTranslation()
  const navigate = useNavigate()
  const { message } = AntdApp.useApp()
  const [batchWarehouseImportPriceForm] = Form.useForm<BatchWarehouseImportPriceFormValues>()
  // 筛选栏的即时取值（文本框防抖前的草稿）与已生效条件分开保存：只有已生效条件驱动查询。
  const [filterValues, setFilterValues] = useState<FilterValues>(INITIAL_FILTER_VALUES)
  const filterValuesRef = useRef<FilterValues>(INITIAL_FILTER_VALUES)
  const filterDebounceTimerRef = useRef<number | null>(null)
  const [filters, setFilters] = useState<AppliedFilters>({ varianceDirection: 'all' })
  const appliedFiltersRef = useRef<AppliedFilters>({ varianceDirection: 'all' })
  const [items, setItems] = useState<StoreOrderImportPriceVarianceItem[]>([])
  const [summary, setSummary] = useState<StoreOrderImportPriceVarianceSummary>(emptySummary)
  const [supplierSummaries, setSupplierSummaries] = useState<StoreOrderImportPriceVarianceSupplierSummary[]>([])
  const [supplierRanking, setSupplierRanking] = useState<SupplierRanking | null>(null)
  const [supplierSort, setSupplierSort] = useState<SupplierSort>(null)
  const [supplierExpanded, setSupplierExpanded] = useState(false)
  const [total, setTotal] = useState(0)
  const [pageNumber, setPageNumber] = useState(1)
  const [pageSize, setPageSize] = useState(DEFAULT_PAGE_SIZE)
  const [sortBy, setSortBy] = useState(DEFAULT_SORT_BY)
  const [sortDescending, setSortDescending] = useState(DEFAULT_SORT_DESCENDING)
  const [loading, setLoading] = useState(false)
  const [optionalColumns, setOptionalColumns] = useState<OptionalColumnKey[]>([])
  // 筛选即查询后请求更密集：主表、供应商排行、明细弹窗各自只允许最后一次请求写入页面。
  const listRequestGuardRef = useRef(createLatestRequestGuard())
  const rankingRequestGuardRef = useRef(createLatestRequestGuard())
  const detailRequestGuardRef = useRef(createLatestRequestGuard())
  const priceInputRefs = useRef<Record<string, InputRef | null>>({})
  const [editingPriceKey, setEditingPriceKey] = useState<string | null>(null)
  const [priceDrafts, setPriceDrafts] = useState<Record<string, string>>({})
  // 键盘保存会紧接着触发 blur，用 ref 做同步防重，避免重复提交同一格。
  const savingPriceKeyRef = useRef<string | null>(null)
  const [savingPriceKey, setSavingPriceKey] = useState<string | null>(null)
  const [supplierOptions, setSupplierOptions] = useState<SupplierOption[]>([])
  const [supplierLoading, setSupplierLoading] = useState(false)
  const supplierOptionsLoadedRef = useRef(false)
  const supplierRequestControllerRef = useRef<AbortController | null>(null)
  const [detailModalOpen, setDetailModalOpen] = useState(false)
  const [selectedProduct, setSelectedProduct] = useState<StoreOrderImportPriceVarianceItem | null>(null)
  const [detailItems, setDetailItems] = useState<StoreOrderImportPriceVarianceDetailItem[]>([])
  const [detailSummary, setDetailSummary] = useState<StoreOrderImportPriceVarianceSummary>(emptySummary)
  const [detailTotal, setDetailTotal] = useState(0)
  const [detailPageNumber, setDetailPageNumber] = useState(1)
  const [detailPageSize, setDetailPageSize] = useState(DEFAULT_PAGE_SIZE)
  const [detailSortBy, setDetailSortBy] = useState(DEFAULT_DETAIL_SORT_BY)
  const [detailSortDescending, setDetailSortDescending] = useState(DEFAULT_DETAIL_SORT_DESCENDING)
  const [detailLoading, setDetailLoading] = useState(false)
  const [selectedRowKeys, setSelectedRowKeys] = useState<Key[]>([])
  const [batchWarehouseImportPriceOpen, setBatchWarehouseImportPriceOpen] = useState(false)
  const [batchWarehouseImportPriceSaving, setBatchWarehouseImportPriceSaving] = useState(false)

  const loadData = useCallback(async () => {
    const listGuard = listRequestGuardRef.current
    const requestId = listGuard.begin()
    setLoading(true)
    try {
      const query: StoreOrderImportPriceVarianceQuery = {
        ...filters,
        pageNumber,
        pageSize,
        sortBy,
        sortDescending,
      }
      const result = await getStoreOrderImportPriceVariance(query)
      if (!listGuard.isLatest(requestId)) {
        return
      }
      setItems(result.items)
      setTotal(result.total)
      setPageNumber(result.page)
      setPageSize(result.pageSize)
      setSummary(result.summary)
      setSupplierSummaries(result.supplierSummaries)
      // 没有选国内供应商时，接口返回的就是同条件下的全部供应商，顺手更新排行缓存。
      if (!filters.supplierCode) {
        setSupplierRanking({ key: getSupplierRankingKey(filters), rows: result.supplierSummaries })
      }
    } catch (error) {
      if (!listGuard.isLatest(requestId)) {
        return
      }
      console.error(error)
      void message.error(t('storeOrders.importPriceVariance.loadFailed'))
    } finally {
      if (listGuard.isLatest(requestId)) {
        setLoading(false)
      }
    }
  }, [filters, message, pageNumber, pageSize, sortBy, sortDescending, t])

  useEffect(() => {
    void loadData()
  }, [loadData])

  const supplierRankingKey = getSupplierRankingKey(filters)
  const cachedRankingKey = supplierRanking?.key

  // 点选了国内供应商且排行缓存不是当前条件时，补一次不带供应商的请求（只取第 1 页 1 条）拿全部供应商排行。
  // 失败静默降级：排行只显示当前供应商。
  useEffect(() => {
    if (!filters.supplierCode || cachedRankingKey === supplierRankingKey) {
      return
    }
    const rankingGuard = rankingRequestGuardRef.current
    const requestId = rankingGuard.begin()
    void getStoreOrderImportPriceVariance({
      ...filters,
      supplierCode: undefined,
      pageNumber: 1,
      pageSize: 1,
      sortBy: DEFAULT_SORT_BY,
      sortDescending: DEFAULT_SORT_DESCENDING,
    })
      .then((result) => {
        if (rankingGuard.isLatest(requestId)) {
          setSupplierRanking({ key: supplierRankingKey, rows: result.supplierSummaries })
        }
      })
      .catch((error: unknown) => console.error(error))
  }, [cachedRankingKey, filters, supplierRankingKey])

  useEffect(
    () => () => {
      supplierRequestControllerRef.current?.abort()
      if (filterDebounceTimerRef.current !== null) {
        window.clearTimeout(filterDebounceTimerRef.current)
      }
      listRequestGuardRef.current.invalidate()
      rankingRequestGuardRef.current.invalidate()
      detailRequestGuardRef.current.invalidate()
    },
    [],
  )

  /** 应用已生效条件：条件确实变了才回到第 1 页并清空勾选（与原「查询」按钮一致）。 */
  const applyFilters = useCallback((next: AppliedFilters) => {
    if (getFilterSignature(appliedFiltersRef.current) === getFilterSignature(next)) {
      return
    }
    appliedFiltersRef.current = next
    setFilters(next)
    setPageNumber(1)
    setSelectedRowKeys([])
  }, [])

  /**
   * 更新筛选栏取值。下拉、日期、分段是 immediate：立即查询；文本框是 debounced：停顿 300ms 再查询。
   * 立即生效时会一并带上还在防抖中的文本，避免两次查询。
   */
  const updateFilterValues = useCallback(
    (patch: Partial<FilterValues>, mode: 'immediate' | 'debounced') => {
      const next = { ...filterValuesRef.current, ...patch }
      filterValuesRef.current = next
      setFilterValues(next)
      if (filterDebounceTimerRef.current !== null) {
        window.clearTimeout(filterDebounceTimerRef.current)
        filterDebounceTimerRef.current = null
      }
      if (mode === 'debounced') {
        filterDebounceTimerRef.current = window.setTimeout(() => {
          filterDebounceTimerRef.current = null
          applyFilters(normalizeFilters(filterValuesRef.current))
        }, FILTER_DEBOUNCE_MS)
        return
      }
      applyFilters(normalizeFilters(next))
    },
    [applyFilters],
  )

  const loadSupplierOptions = useCallback(async () => {
    if (supplierOptionsLoadedRef.current || supplierLoading) {
      return
    }

    supplierRequestControllerRef.current?.abort()
    const currentController = new AbortController()
    supplierRequestControllerRef.current = currentController
    setSupplierLoading(true)

    try {
      const suppliers: ChinaSupplierItem[] = await getActiveChinaSuppliers(currentController.signal)
      if (supplierRequestControllerRef.current !== currentController) {
        return
      }

      // 供应商筛选只使用国内供应商编码，后端会按 DomesticProduct.SupplierCode 汇总商品。
      setSupplierOptions(
        suppliers
          .filter((item) => Boolean(item.supplierCode))
          .map((item) => ({
            label: `${item.supplierCode} - ${item.supplierName || item.supplierCode}`,
            value: item.supplierCode,
          })),
      )
      supplierOptionsLoadedRef.current = true
    } catch (error) {
      if (isAbortError(error)) {
        return
      }
      console.error(error)
      void message.error(t('storeOrders.importPriceVariance.loadSuppliersFailed'))
    } finally {
      if (supplierRequestControllerRef.current === currentController) {
        supplierRequestControllerRef.current = null
        setSupplierLoading(false)
      }
    }
  }, [message, supplierLoading, t])

  const handleSupplierOpenChange = useCallback(
    (open: boolean) => {
      if (open) {
        void loadSupplierOptions()
      }
    },
    [loadSupplierOptions],
  )

  const openOrderDetail = useCallback(
    (row: StoreOrderImportPriceVarianceDetailItem) => {
      if (!row.orderGUID) {
        return
      }

      navigate(`/warehouse/store-order/detail/${row.orderGUID}`, {
        state: { orderNo: row.orderNo },
      })
    },
    [navigate],
  )

  const openContainerDetail = useCallback(
    (row: { firstContainerCode?: string }) => {
      if (!row.firstContainerCode) {
        return
      }

      navigate(`/warehouse/container/detail/${row.firstContainerCode}`)
    },
    [navigate],
  )

  const openProductDetails = useCallback((row: StoreOrderImportPriceVarianceItem) => {
    setSelectedProduct(row)
    setDetailItems([])
    setDetailSummary(emptySummary)
    setDetailTotal(0)
    setDetailPageNumber(1)
    setDetailPageSize(DEFAULT_PAGE_SIZE)
    setDetailSortBy(DEFAULT_DETAIL_SORT_BY)
    setDetailSortDescending(DEFAULT_DETAIL_SORT_DESCENDING)
    setDetailModalOpen(true)
  }, [])

  const closeProductDetails = useCallback(() => {
    detailRequestGuardRef.current.invalidate()
    setDetailModalOpen(false)
    setSelectedProduct(null)
    setDetailItems([])
    setDetailSummary(emptySummary)
    setDetailTotal(0)
  }, [])

  const loadDetailData = useCallback(async () => {
    if (!detailModalOpen || !selectedProduct?.productCode) {
      return
    }

    // 弹窗内翻页 / 排序 / 换商品时只采纳最后一次响应。
    const detailGuard = detailRequestGuardRef.current
    const requestId = detailGuard.begin()
    setDetailLoading(true)
    try {
      const result = await getStoreOrderImportPriceVarianceDetails({
        ...filters,
        productCode: selectedProduct.productCode,
        pageNumber: detailPageNumber,
        pageSize: detailPageSize,
        sortBy: detailSortBy,
        sortDescending: detailSortDescending,
      })
      if (!detailGuard.isLatest(requestId)) {
        return
      }
      setDetailItems(result.items)
      setDetailSummary(result.summary)
      setDetailTotal(result.total)
      setDetailPageNumber(result.page)
      setDetailPageSize(result.pageSize)
    } catch (error) {
      if (!detailGuard.isLatest(requestId)) {
        return
      }
      console.error(error)
      void message.error(t('storeOrders.importPriceVariance.loadDetailsFailed'))
    } finally {
      if (detailGuard.isLatest(requestId)) {
        setDetailLoading(false)
      }
    }
  }, [
    detailModalOpen,
    detailPageNumber,
    detailPageSize,
    detailSortBy,
    detailSortDescending,
    filters,
    message,
    selectedProduct,
    t,
  ])

  useEffect(() => {
    void loadDetailData()
  }, [loadDetailData])

  const registerPriceInput = useCallback((key: string, node: InputRef | null) => {
    if (node) {
      priceInputRefs.current[key] = node
      return
    }

    delete priceInputRefs.current[key]
  }, [])

  const focusPriceInput = useCallback((row: StoreOrderImportPriceVarianceItem, field: EditablePriceField) => {
    const key = getEditablePriceInputKey(row, field)
    setEditingPriceKey(key)
    setPriceDrafts((current) => ({
      ...current,
      [key]: formatMoney(getEditablePriceValue(row, field)),
    }))

    // 进入编辑后全选文本，方便仓库同事直接覆盖当前价格。
    window.setTimeout(() => priceInputRefs.current[key]?.focus?.({ cursor: 'all' }), 0)
  }, [])

  const clearPriceDraft = useCallback((key: string) => {
    setPriceDrafts((current) => {
      const next = { ...current }
      delete next[key]
      return next
    })
  }, [])

  const cancelPriceEdit = useCallback(
    (row: StoreOrderImportPriceVarianceItem, field: EditablePriceField) => {
      const key = getEditablePriceInputKey(row, field)
      setEditingPriceKey(null)
      clearPriceDraft(key)
    },
    [clearPriceDraft],
  )

  const saveEditablePrice = useCallback(
    async (
      row: StoreOrderImportPriceVarianceItem,
      field: EditablePriceField,
      nextRow?: StoreOrderImportPriceVarianceItem,
    ) => {
      const key = getEditablePriceInputKey(row, field)
      if (savingPriceKeyRef.current === key) {
        return false
      }

      const draft = priceDrafts[key] ?? formatMoney(getEditablePriceValue(row, field))
      const nextPrice = parsePriceDraft(draft)
      if (nextPrice == null) {
        const invalidKey =
          field === 'domesticPrice'
            ? 'storeOrders.importPriceVariance.invalidDomesticPrice'
            : 'storeOrders.importPriceVariance.invalidWarehouseImportPrice'
        void message.error(t(invalidKey))
        window.setTimeout(() => priceInputRefs.current[key]?.focus?.({ cursor: 'all' }), 0)
        return false
      }

      const currentPrice = Math.round((getEditablePriceValue(row, field) ?? 0) * 100) / 100
      if (nextPrice === currentPrice) {
        setEditingPriceKey(null)
        clearPriceDraft(key)
        if (nextRow) {
          focusPriceInput(nextRow, field)
        }
        return true
      }

      const failedKey =
        field === 'domesticPrice'
          ? 'storeOrders.importPriceVariance.saveDomesticPriceFailed'
          : 'storeOrders.importPriceVariance.saveWarehouseImportPriceFailed'
      if (!row.productCode) {
        void message.error(t(failedKey))
        return false
      }

      savingPriceKeyRef.current = key
      setSavingPriceKey(key)
      try {
        let resultProductCode = ''
        let resultPrice = 0
        if (field === 'domesticPrice') {
          const result = await updateStoreOrderImportPriceVarianceDomesticPrice({
            productCode: row.productCode,
            domesticPrice: nextPrice,
          })
          resultProductCode = result.productCode
          resultPrice = result.domesticPrice
        } else {
          const result = await updateStoreOrderImportPriceVarianceWarehouseImportPrice({
            productCode: row.productCode,
            warehouseImportPrice: nextPrice,
          })
          resultProductCode = result.productCode
          resultPrice = result.warehouseImportPrice
        }

        setSelectedProduct((current) =>
          current?.productCode === resultProductCode ? { ...current, [field]: resultPrice } : current,
        )

        const shouldReloadSortedData = sortBy === field
        if (shouldReloadSortedData) {
          // 当前排序依赖被编辑价格，保存后必须重新走服务端排序，避免表格停留在旧顺序。
          await loadData()
        } else {
          setItems((current) =>
            current.map((item) =>
              item.productCode === resultProductCode ? { ...item, [field]: resultPrice } : item,
            ),
          )
        }
        setEditingPriceKey(null)
        clearPriceDraft(key)
        const successKey =
          field === 'domesticPrice'
            ? 'storeOrders.importPriceVariance.saveDomesticPriceSuccess'
            : 'storeOrders.importPriceVariance.saveWarehouseImportPriceSuccess'
        void message.success(t(successKey))
        if (!shouldReloadSortedData && nextRow) {
          focusPriceInput(nextRow, field)
        }
        return true
      } catch (error) {
        const errorMessage = error instanceof Error && error.message ? error.message : t(failedKey)
        void message.error(errorMessage)
        window.setTimeout(() => priceInputRefs.current[key]?.focus?.({ cursor: 'all' }), 0)
        return false
      } finally {
        savingPriceKeyRef.current = null
        setSavingPriceKey(null)
      }
    },
    [
      clearPriceDraft,
      focusPriceInput,
      loadData,
      message,
      priceDrafts,
      sortBy,
      t,
    ],
  )

  const handlePriceKeyDown = useCallback(
    (event: KeyboardEvent<HTMLInputElement>, row: StoreOrderImportPriceVarianceItem, field: EditablePriceField) => {
      if (event.key === 'Enter') {
        event.preventDefault()
        event.stopPropagation()
        void saveEditablePrice(row, field)
        return
      }

      if (event.key === 'Escape') {
        event.preventDefault()
        event.stopPropagation()
        cancelPriceEdit(row, field)
        return
      }

      const isArrowUp = event.key === 'ArrowUp'
      const isArrowDown = event.key === 'ArrowDown'
      if (!isArrowUp && !isArrowDown) {
        return
      }

      event.preventDefault()
      event.stopPropagation()
      const currentIndex = items.findIndex(
        (item) => getEditablePriceInputKey(item, field) === getEditablePriceInputKey(row, field),
      )
      const nextIndex = isArrowUp ? currentIndex - 1 : currentIndex + 1
      const nextRow = nextIndex >= 0 && nextIndex < items.length ? items[nextIndex] : undefined
      void saveEditablePrice(row, field, nextRow)
    },
    [cancelPriceEdit, items, saveEditablePrice],
  )

  const renderEditablePriceCell = useCallback(
    (value: number | undefined, row: StoreOrderImportPriceVarianceItem, field: EditablePriceField) => {
      const rowKey = getEditablePriceInputKey(row, field)
      if (editingPriceKey !== rowKey) {
        const fieldLabel =
          field === 'domesticPrice'
            ? t('warehouseUi.priceVariance.colDomesticPrice')
            : t('warehouseUi.priceVariance.colWarehousePrice')
        // 虚线下划线提示可直接改价；用按钮承载，键盘也能进入编辑。
        return (
          <button
            type="button"
            className="wh-pv-editable-price"
            aria-label={t('warehouseUi.priceVariance.editPriceAria', {
              field: fieldLabel,
              item: row.itemNumber || row.productCode || '--',
            })}
            onClick={() => focusPriceInput(row, field)}
          >
            {formatAmount(value, field === 'domesticPrice' ? '¥' : '$')}
          </button>
        )
      }

      return (
        <Input
          ref={(node) => registerPriceInput(rowKey, node)}
          size="small"
          inputMode="decimal"
          autoComplete="off"
          value={priceDrafts[rowKey] ?? formatMoney(value)}
          disabled={savingPriceKey === rowKey}
          className="wh-pv-price-input"
          style={{ textAlign: 'right', width: '100%' }}
          onChange={(event) =>
            setPriceDrafts((current) => ({
              ...current,
              [rowKey]: event.target.value,
            }))
          }
          onFocus={(event) => event.currentTarget.select()}
          onClick={(event) => event.currentTarget.select()}
          onBlur={() => {
            if (editingPriceKey === rowKey) {
              void saveEditablePrice(row, field)
            }
          }}
          onKeyDown={(event) => handlePriceKeyDown(event, row, field)}
        />
      )
    },
    [
      editingPriceKey,
      focusPriceInput,
      handlePriceKeyDown,
      priceDrafts,
      registerPriceInput,
      saveEditablePrice,
      savingPriceKey,
      t,
    ],
  )

  const openBatchWarehouseImportPriceModal = useCallback(() => {
    if (!selectedRowKeys.length) {
      void message.warning(t('storeOrders.importPriceVariance.selectProductsFirst'))
      return
    }

    batchWarehouseImportPriceForm.resetFields()
    setBatchWarehouseImportPriceOpen(true)
  }, [batchWarehouseImportPriceForm, message, selectedRowKeys.length, t])

  const closeBatchWarehouseImportPriceModal = useCallback(() => {
    if (batchWarehouseImportPriceSaving) {
      return
    }

    setBatchWarehouseImportPriceOpen(false)
    batchWarehouseImportPriceForm.resetFields()
  }, [batchWarehouseImportPriceForm, batchWarehouseImportPriceSaving])

  const handleBatchWarehouseImportPriceSave = useCallback(async () => {
    const productCodes = selectedRowKeys.map(String).filter(Boolean)
    if (!productCodes.length) {
      void message.warning(t('storeOrders.importPriceVariance.selectProductsFirst'))
      return
    }

    try {
      const values = await batchWarehouseImportPriceForm.validateFields()
      setBatchWarehouseImportPriceSaving(true)

      // 批量修改只提交商品编码和统一的新当前参考进货价，避免误覆盖国内价格、商品主档或分店价格。
      const result = await batchUpdateStoreOrderImportPriceVarianceWarehouseImportPrice({
        productCodes,
        warehouseImportPrice: values.warehouseImportPrice ?? 0,
      })

      void message.success(
        t('storeOrders.importPriceVariance.batchSaveWarehouseImportPriceSuccess', {
          count: result.updatedCount || productCodes.length,
        }),
      )
      setBatchWarehouseImportPriceOpen(false)
      batchWarehouseImportPriceForm.resetFields()
      setSelectedRowKeys([])
      await loadData()
    } catch (error) {
      if (typeof error === 'object' && error !== null && 'errorFields' in error) {
        return
      }

      console.error(error)
      const errorMessage =
        error instanceof Error
          ? error.message
          : t('storeOrders.importPriceVariance.batchSaveWarehouseImportPriceFailed')
      void message.error(errorMessage)
    } finally {
      setBatchWarehouseImportPriceSaving(false)
    }
  }, [batchWarehouseImportPriceForm, loadData, message, selectedRowKeys, t])

  const productMaxAbsVariance = useMemo(() => getMaxAbsVariance(items.map((item) => item.varianceAmountTotal)), [items])
  const isDefaultSort = sortBy === DEFAULT_SORT_BY && sortDescending === DEFAULT_SORT_DESCENDING

  const productColumns = useMemo<ColumnsType<StoreOrderImportPriceVarianceItem>>(() => {
    const columns: ColumnsType<StoreOrderImportPriceVarianceItem> = [
      {
        title: t('warehouseUi.priceVariance.colProduct'),
        dataIndex: 'productName',
        key: 'itemNumber',
        width: 248,
        sorter: true,
        render: (_value, row) => (
          <div className="wh-pv-product">
            {row.productImage ? (
              <ProductListImage src={row.productImage} size={38} radius={6} className="wh-pv-product-image" />
            ) : (
              <span className="wh-pv-product-placeholder" aria-hidden="true">
                <PictureOutlined />
              </span>
            )}
            <div className="wh-pv-product-text">
              <div className="wh-pv-product-line">
                <span className="wh-pv-item-number">{row.itemNumber || row.productCode || '--'}</span>
                <span className="wh-pv-product-name">{row.productName || '--'}</span>
              </div>
              <div className="wh-pv-product-supplier">
                {row.supplierName || row.supplierCode
                  ? [row.supplierName, row.supplierCode].filter(Boolean).join(' · ')
                  : t('storeOrders.importPriceVariance.unknownSupplier')}
              </div>
            </div>
          </div>
        ),
      },
      {
        title: t('warehouseUi.priceVariance.colDomesticPrice'),
        dataIndex: 'domesticPrice',
        key: 'domesticPrice',
        align: 'right',
        width: 88,
        sorter: true,
        render: (value: number | undefined, row) => renderEditablePriceCell(value, row, 'domesticPrice'),
      },
      {
        title: t('warehouseUi.priceVariance.colWarehousePrice'),
        dataIndex: 'warehouseImportPrice',
        key: 'warehouseImportPrice',
        align: 'right',
        width: 104,
        sorter: true,
        render: (value: number | undefined, row) => renderEditablePriceCell(value, row, 'warehouseImportPrice'),
      },
      {
        title: t('storeOrders.importPriceVariance.firstContainerImportPrice'),
        dataIndex: 'firstContainerImportPrice',
        key: 'firstContainerImportPrice',
        align: 'right',
        width: 152,
        sorter: true,
        render: (value: number | undefined, row) => {
          const containerText = row.firstContainerNumber || row.firstContainerCode
          return (
            <div className="wh-pv-two-line">
              <div className="wh-pv-strong">{formatAmount(value)}</div>
              <div className="wh-pv-small">
                {containerText && row.firstContainerCode ? (
                  <button type="button" className="wh-pv-link-button" onClick={() => openContainerDetail(row)}>
                    {containerText}
                  </button>
                ) : (
                  <span className="wh-pv-muted">{containerText || '--'}</span>
                )}
                <span className="wh-pv-muted"> · {formatContainerDate(row.firstContainerDate)}</span>
              </div>
            </div>
          )
        },
      },
    ]

    // 体积、装箱数是低频参考列，默认收进「列设置」，需要时再打开（仍支持服务端排序）。
    if (optionalColumns.includes('unitVolume')) {
      columns.push({
        title: t('storeOrders.importPriceVariance.unitVolume'),
        dataIndex: 'unitVolume',
        key: 'unitVolume',
        align: 'right',
        width: 96,
        sorter: true,
        render: (value?: number) => formatNumber(value, 4),
      })
    }
    if (optionalColumns.includes('packingQuantity')) {
      columns.push({
        title: t('storeOrders.importPriceVariance.packingQuantity'),
        dataIndex: 'packingQuantity',
        key: 'packingQuantity',
        align: 'right',
        width: 96,
        sorter: true,
        render: (value?: number) => formatNumber(value, 0),
      })
    }

    columns.push(
      {
        title: t('warehouseUi.priceVariance.colQuantity'),
        dataIndex: 'allocQuantityTotal',
        key: 'allocQuantityTotal',
        align: 'right',
        width: 84,
        sorter: true,
        render: (value?: number) => formatQuantity(value),
      },
      {
        title: t('warehouseUi.priceVariance.colAmounts'),
        dataIndex: 'originalImportAmountTotal',
        key: 'originalImportAmountTotal',
        align: 'right',
        width: 128,
        sorter: true,
        render: (value: number | undefined, row) => (
          <div className="wh-pv-two-line">
            <div>{formatAmount(value)}</div>
            <div className="wh-pv-small wh-pv-muted">
              {t('warehouseUi.priceVariance.baselineLine', { amount: formatAmount(row.baselineImportAmountTotal) })}
            </div>
          </div>
        ),
      },
      {
        // 默认按差额绝对值倒序（不对应任何列头排序），在表头写明；点列头排序发送带符号的 varianceAmountTotal。
        title: isDefaultSort ? (
          <Tooltip title={t('warehouseUi.priceVariance.defaultSortHint')}>
            <span className="wh-pv-default-sort">
              {t('warehouseUi.priceVariance.colVarianceDefault')}
              <ArrowDownOutlined />
            </span>
          </Tooltip>
        ) : (
          t('warehouseUi.priceVariance.colVariance')
        ),
        dataIndex: 'varianceAmountTotal',
        key: 'varianceAmountTotal',
        width: 176,
        sorter: true,
        // 默认排序时表头已有自己的说明浮层，避免和 antd 的排序提示叠在一起。
        showSorterTooltip: !isDefaultSort,
        render: (value?: number) => (
          <span className="wh-pv-variance-cell">
            <VarianceBar value={value} maxAbs={productMaxAbsVariance} width={64} />
            <SignedVariance value={value} />
          </span>
        ),
      },
      {
        title: t('warehouseUi.priceVariance.colDetails'),
        dataIndex: 'detailCount',
        key: 'detailCount',
        align: 'right',
        fixed: 'right',
        width: 72,
        sorter: true,
        render: (value: number | undefined, row) => (
          <button type="button" className="wh-pv-link-button wh-pv-number" onClick={() => openProductDetails(row)}>
            {t('warehouseUi.priceVariance.detailLink', { count: value ?? 0 })}
          </button>
        ),
      },
    )
    return columns
  }, [
    isDefaultSort,
    openContainerDetail,
    openProductDetails,
    optionalColumns,
    productMaxAbsVariance,
    renderEditablePriceCell,
    t,
  ])

  const detailColumns = useMemo<ColumnsType<StoreOrderImportPriceVarianceDetailItem>>(
    () => [
      {
        title: t('storeOrders.importPriceVariance.orderNo'),
        dataIndex: 'orderNo',
        key: 'orderNo',
        width: 150,
        sorter: true,
        render: (value: string | undefined, row) => {
          const text = value || '--'
          if (!row.orderGUID || !value) {
            return text
          }

          return (
            <Button type="link" size="small" onClick={() => openOrderDetail(row)}>
              {text}
            </Button>
          )
        },
      },
      {
        title: t('storeOrders.importPriceVariance.orderDate'),
        dataIndex: 'orderDate',
        key: 'orderDate',
        width: 130,
        sorter: true,
        render: (value?: string) => formatDate(value, i18n.language),
      },
      {
        title: t('storeOrders.importPriceVariance.store'),
        dataIndex: 'storeName',
        key: 'storeCode',
        width: 180,
        render: (_value, row) => (
          <Space direction="vertical" size={0}>
            <Typography.Text>{row.storeName || '--'}</Typography.Text>
            <Typography.Text type="secondary">{row.storeCode || '--'}</Typography.Text>
          </Space>
        ),
      },
      {
        title: t('storeOrders.importPriceVariance.orderImportPrice'),
        dataIndex: 'orderImportPrice',
        key: 'orderImportPrice',
        align: 'right',
        width: 130,
        sorter: true,
        render: (value?: number) => formatMoney(value),
      },
      {
        title: t('storeOrders.importPriceVariance.firstContainerImportPrice'),
        dataIndex: 'firstContainerImportPrice',
        key: 'firstContainerImportPrice',
        align: 'right',
        width: 130,
        sorter: true,
        render: (value?: number) => formatMoney(value),
      },
      {
        title: t('storeOrders.importPriceVariance.allocQuantity'),
        dataIndex: 'allocQuantity',
        key: 'allocQuantity',
        align: 'right',
        width: 100,
        sorter: true,
        render: (value?: number) => formatNumber(value, 2),
      },
      {
        title: t('storeOrders.importPriceVariance.originalImportAmount'),
        dataIndex: 'originalImportAmount',
        key: 'originalImportAmount',
        align: 'right',
        width: 130,
        sorter: true,
        render: (value?: number) => formatMoney(value),
      },
      {
        title: t('storeOrders.importPriceVariance.baselineImportAmount'),
        dataIndex: 'baselineImportAmount',
        key: 'baselineImportAmount',
        align: 'right',
        width: 130,
        sorter: true,
        render: (value?: number) => formatMoney(value),
      },
      {
        title: t('storeOrders.importPriceVariance.varianceAmount'),
        dataIndex: 'varianceAmount',
        key: 'varianceAmount',
        align: 'right',
        width: 130,
        sorter: true,
        render: (value?: number) => <SignedVariance value={value} />,
      },
      {
        title: t('storeOrders.importPriceVariance.firstContainerNumber'),
        dataIndex: 'firstContainerNumber',
        key: 'firstContainerNumber',
        width: 150,
        render: (_value, row) => {
          const text = row.firstContainerNumber || row.firstContainerCode || '--'
          if (!row.firstContainerCode) {
            return text
          }

          return (
            <Button type="link" size="small" onClick={() => openContainerDetail(row)}>
              {text}
            </Button>
          )
        },
      },
      {
        title: t('storeOrders.importPriceVariance.firstContainerDate'),
        dataIndex: 'firstContainerDate',
        key: 'firstContainerDate',
        width: 130,
        sorter: true,
        render: (value?: string) => formatDate(value, i18n.language),
      },
    ],
    [i18n.language, openContainerDetail, openOrderDetail, t],
  )

  // 供应商排行：选了国内供应商时用缓存的同条件全量排行，否则直接用本次结果。
  const supplierRows =
    filters.supplierCode && supplierRanking?.key === supplierRankingKey ? supplierRanking.rows : supplierSummaries
  const sortedSupplierRows = useMemo(() => sortSupplierSummaries(supplierRows, supplierSort), [supplierRows, supplierSort])
  const visibleSupplierRows = useMemo(
    () => getVisibleSupplierRows(sortedSupplierRows, supplierExpanded, filters.supplierCode),
    [filters.supplierCode, sortedSupplierRows, supplierExpanded],
  )
  const supplierMaxAbsVariance = useMemo(
    () => getMaxAbsVariance(supplierRows.map((row) => row.varianceAmountTotal)),
    [supplierRows],
  )

  /** 点供应商行 = 设置国内供应商筛选并联动商品表；再点同一行取消。没有编码的「未识别供应商」不能作为筛选条件。 */
  const toggleSupplierFilter = useCallback(
    (row: StoreOrderImportPriceVarianceSupplierSummary) => {
      if (!row.supplierCode) {
        return
      }
      updateFilterValues(
        { supplierCode: filterValuesRef.current.supplierCode === row.supplierCode ? undefined : row.supplierCode },
        'immediate',
      )
    },
    [updateFilterValues],
  )

  const supplierSummaryColumns = useMemo<ColumnsType<StoreOrderImportPriceVarianceSupplierSummary>>(() => {
    const sortOrderOf = (key: SupplierSortKey) => (supplierSort?.key === key ? supplierSort.order : null)
    return [
      {
        title: t('storeOrders.importPriceVariance.domesticSupplier'),
        key: 'supplier',
        sorter: true,
        sortOrder: sortOrderOf('supplier'),
        render: (_value, row) => {
          const supplierName =
            row.supplierName ||
            row.supplierCode ||
            t('storeOrders.importPriceVariance.unknownSupplier')
          const selected = Boolean(row.supplierCode) && row.supplierCode === filters.supplierCode
          return (
            <span className="wh-pv-supplier-cell">
              {row.supplierCode ? (
                // 点击冒泡到行上统一切换筛选；按钮本身负责键盘可达与选中态播报。
                <button
                  type="button"
                  className={`wh-pv-radio${selected ? ' is-selected' : ''}`}
                  aria-pressed={selected}
                  aria-label={t('warehouseUi.priceVariance.supplierSelectAria', { name: supplierName })}
                />
              ) : (
                <span className="wh-pv-radio is-placeholder" aria-hidden="true" />
              )}
              <span className={`wh-pv-supplier-name${selected ? ' is-selected' : ''}`}>{supplierName}</span>
              {/* 没有名称时名称位已经显示编码，不再重复。 */}
              {row.supplierCode && row.supplierName ? <span className="wh-pv-muted wh-pv-small">{row.supplierCode}</span> : null}
            </span>
          )
        },
      },
      {
        title: t('warehouseUi.priceVariance.colProducts'),
        dataIndex: 'productCount',
        key: 'productCount',
        align: 'right',
        width: 80,
        sorter: true,
        sortOrder: sortOrderOf('productCount'),
        render: (value?: number) => formatQuantity(value),
      },
      {
        title: t('warehouseUi.priceVariance.colDetails'),
        dataIndex: 'detailCount',
        key: 'detailCount',
        align: 'right',
        width: 88,
        sorter: true,
        sortOrder: sortOrderOf('detailCount'),
        render: (value?: number) => formatQuantity(value),
      },
      {
        title: t('storeOrders.importPriceVariance.increaseVarianceAmountTotal'),
        dataIndex: 'increaseVarianceAmountTotal',
        key: 'increase',
        align: 'right',
        width: 130,
        sorter: true,
        sortOrder: sortOrderOf('increase'),
        render: (value?: number) => <span className="wh-pv-tone-over">{formatSignedAmount(value)}</span>,
      },
      {
        // 接口返回少收金额的绝对值，显示时带上负号，与净差额的符号口径一致。
        title: t('storeOrders.importPriceVariance.decreaseVarianceAmountTotal'),
        dataIndex: 'decreaseVarianceAmountTotal',
        key: 'decrease',
        align: 'right',
        width: 130,
        sorter: true,
        sortOrder: sortOrderOf('decrease'),
        render: (value?: number) => <span className="wh-pv-tone-under">{formatSignedAmount(-(value ?? 0))}</span>,
      },
      {
        title: t('warehouseUi.priceVariance.colNet'),
        dataIndex: 'varianceAmountTotal',
        key: 'net',
        width: 260,
        sorter: true,
        sortOrder: sortOrderOf('net'),
        render: (value?: number) => (
          <span className="wh-pv-variance-cell">
            <VarianceBar value={value} maxAbs={supplierMaxAbsVariance} width={120} />
            <SignedVariance value={value} />
          </span>
        ),
      },
    ]
  }, [filters.supplierCode, supplierMaxAbsVariance, supplierSort, t])

  const handleSupplierTableChange = (
    _pagination: TablePaginationConfig,
    _filters: Record<string, unknown>,
    sorter:
      | SorterResult<StoreOrderImportPriceVarianceSupplierSummary>
      | SorterResult<StoreOrderImportPriceVarianceSupplierSummary>[],
  ) => {
    const nextSorter = Array.isArray(sorter) ? sorter[0] : sorter
    // 取消列排序时回到默认的「净差额绝对值倒序」。
    setSupplierSort(
      nextSorter?.order ? { key: String(nextSorter.columnKey) as SupplierSortKey, order: nextSorter.order } : null,
    )
  }

  const handleTableChange = (
    pagination: TablePaginationConfig,
    _filters: Record<string, unknown>,
    sorter: SorterResult<StoreOrderImportPriceVarianceItem> | SorterResult<StoreOrderImportPriceVarianceItem>[],
  ) => {
    const nextSorter = Array.isArray(sorter) ? sorter[0] : sorter
    setPageNumber(pagination.current ?? 1)
    setPageSize(pagination.pageSize ?? DEFAULT_PAGE_SIZE)

    // Antd 清空排序时回到“绝对差额倒序”，保持商品汇总首屏最关注差异最大的商品。
    if (nextSorter?.order) {
      setSortBy(String(nextSorter.columnKey ?? nextSorter.field ?? DEFAULT_SORT_BY))
      setSortDescending(nextSorter.order === 'descend')
    } else {
      setSortBy(DEFAULT_SORT_BY)
      setSortDescending(DEFAULT_SORT_DESCENDING)
    }
  }

  const handleDetailTableChange = (
    pagination: TablePaginationConfig,
    _filters: Record<string, unknown>,
    sorter:
      | SorterResult<StoreOrderImportPriceVarianceDetailItem>
      | SorterResult<StoreOrderImportPriceVarianceDetailItem>[],
  ) => {
    const nextSorter = Array.isArray(sorter) ? sorter[0] : sorter
    setDetailPageNumber(pagination.current ?? 1)
    setDetailPageSize(pagination.pageSize ?? DEFAULT_PAGE_SIZE)

    if (nextSorter?.order) {
      setDetailSortBy(String(nextSorter.columnKey ?? nextSorter.field ?? DEFAULT_DETAIL_SORT_BY))
      setDetailSortDescending(nextSorter.order === 'descend')
    } else {
      setDetailSortBy(DEFAULT_DETAIL_SORT_BY)
      setDetailSortDescending(DEFAULT_DETAIL_SORT_DESCENDING)
    }
  }

  // 下拉选项按需加载；从供应商排行点选时选项可能还没加载，补一个当前值的选项避免只显示编码。
  const supplierSelectOptions = useMemo(() => {
    const code = filterValues.supplierCode
    if (!code || supplierOptions.some((option) => option.value === code)) {
      return supplierOptions
    }
    const known = supplierRows.find((row) => row.supplierCode === code)
    return [{ value: code, label: `${code} - ${known?.supplierName || code}` }, ...supplierOptions]
  }, [filterValues.supplierCode, supplierOptions, supplierRows])

  const directionLabel = (direction: StoreOrderImportPriceVarianceDirection) =>
    direction === 'increase'
      ? t('storeOrders.importPriceVariance.directionIncrease')
      : t('storeOrders.importPriceVariance.directionDecrease')

  // 已生效条件：逐个可移除，「清空全部」回到初始条件（排序与分页大小保持不变）。
  const activeFilterItems: ActiveFilterItem[] = []
  if (filters.keyword) {
    activeFilterItems.push({
      key: 'keyword',
      label: t('storeOrders.importPriceVariance.keyword'),
      value: filters.keyword,
      source: 'toolbar',
      onRemove: () => updateFilterValues({ keyword: undefined }, 'immediate'),
    })
  }
  if (filters.startDate || filters.endDate) {
    activeFilterItems.push({
      key: 'orderDate',
      label: t('storeOrders.importPriceVariance.orderDate'),
      value: `${filters.startDate ?? '…'} ~ ${filters.endDate ?? '…'}`,
      source: 'toolbar',
      onRemove: () => updateFilterValues({ orderDateRange: null }, 'immediate'),
    })
  }
  if (filters.supplierCode) {
    activeFilterItems.push({
      key: 'supplierCode',
      label: t('storeOrders.importPriceVariance.domesticSupplier'),
      value: supplierSelectOptions.find((option) => option.value === filters.supplierCode)?.label ?? filters.supplierCode,
      source: 'toolbar',
      onRemove: () => updateFilterValues({ supplierCode: undefined }, 'immediate'),
    })
  }
  if (filters.varianceDirection !== 'all') {
    activeFilterItems.push({
      key: 'varianceDirection',
      label: t('storeOrders.importPriceVariance.varianceDirection'),
      value: directionLabel(filters.varianceDirection),
      source: 'toolbar',
      onRemove: () => updateFilterValues({ varianceDirection: 'all' }, 'immediate'),
    })
  }
  if (filters.storeCode) {
    activeFilterItems.push({
      key: 'storeCode',
      label: t('warehouseUi.priceVariance.storeCodeLabel'),
      value: filters.storeCode,
      source: 'toolbar',
      onRemove: () => updateFilterValues({ storeCode: undefined }, 'immediate'),
    })
  }
  if (filters.orderNo) {
    activeFilterItems.push({
      key: 'orderNo',
      label: t('storeOrders.importPriceVariance.orderNo'),
      value: filters.orderNo,
      source: 'toolbar',
      onRemove: () => updateFilterValues({ orderNo: undefined }, 'immediate'),
    })
  }
  const moreFilterCount = [filters.storeCode, filters.orderNo].filter(Boolean).length

  // 汇总条：明细行 / 多收 / 少收来自供应商汇总，只有核对出是全量时才显示这几格。
  const supplierTotals = summarizeSupplierTotals(summary, supplierSummaries)
  const metrics: Array<{ key: string; label: string; value: string; tone?: 'over' | 'under' | 'none' }> = [
    ...(supplierTotals
      ? [{ key: 'detailRows', label: t('warehouseUi.priceVariance.metricDetailRows'), value: formatQuantity(supplierTotals.detailRows) }]
      : []),
    { key: 'original', label: t('storeOrders.importPriceVariance.originalImportAmountTotal'), value: formatAmount(summary.originalImportAmountTotal) },
    { key: 'baseline', label: t('storeOrders.importPriceVariance.baselineImportAmountTotal'), value: formatAmount(summary.baselineImportAmountTotal) },
    ...(supplierTotals
      ? [
          {
            key: 'increase',
            label: t('storeOrders.importPriceVariance.increaseVarianceAmountTotal'),
            value: formatSignedAmount(supplierTotals.increaseTotal),
            tone: 'over' as const,
          },
          {
            key: 'decrease',
            label: t('storeOrders.importPriceVariance.decreaseVarianceAmountTotal'),
            value: formatSignedAmount(-supplierTotals.decreaseTotal),
            tone: 'under' as const,
          },
        ]
      : []),
    {
      key: 'net',
      label: t('warehouseUi.priceVariance.metricNet'),
      value: formatSignedAmount(summary.varianceAmountTotal),
      tone: getVarianceTone(summary.varianceAmountTotal),
    },
  ]

  const varianceStatisticStyle = (value: number) => {
    const tone = getVarianceTone(value)
    return { color: tone === 'none' ? undefined : VARIANCE_TONE_COLORS[tone] }
  }

  return (
    <PageContainer
      compact
      title={t('menu.storeOrderImportPriceVariance')}
      extra={
        <p className="wh-pv-formula">
          <InfoCircleOutlined className="wh-pv-formula-icon" />
          <span>
            <Trans
              i18nKey="warehouseUi.priceVariance.formula"
              components={{ over: <strong className="wh-pv-tone-over" />, under: <strong className="wh-pv-tone-under" /> }}
            />
          </span>
        </p>
      }
    >
      <section className="wh-pv-toolbar">
        <div className="list-toolbar-filter-row">
          <Input
            allowClear
            prefix={<SearchOutlined />}
            className="wh-pv-search"
            placeholder={t('warehouseUi.priceVariance.searchPlaceholder')}
            aria-label={t('storeOrders.importPriceVariance.keyword')}
            value={filterValues.keyword ?? ''}
            onChange={(event) => updateFilterValues({ keyword: event.target.value }, 'debounced')}
          />
          <RangePicker
            prefix={<span className="wh-pv-field-prefix">{t('storeOrders.importPriceVariance.orderDate')}</span>}
            value={filterValues.orderDateRange}
            onChange={(value) => updateFilterValues({ orderDateRange: value }, 'immediate')}
          />
          <DomesticSupplierFilterSelect
            value={filterValues.supplierCode}
            loading={supplierLoading}
            options={supplierSelectOptions}
            prefix={t('storeOrders.importPriceVariance.domesticSupplier')}
            placeholder={t('storeOrders.importPriceVariance.directionAll')}
            style={{ width: 220 }}
            onChange={(value) => updateFilterValues({ supplierCode: value }, 'immediate')}
            onOpenChange={handleSupplierOpenChange}
          />
          <Segmented<StoreOrderImportPriceVarianceDirection>
            aria-label={t('storeOrders.importPriceVariance.varianceDirection')}
            value={filterValues.varianceDirection ?? 'all'}
            onChange={(value) => updateFilterValues({ varianceDirection: value }, 'immediate')}
            options={[
              { value: 'all', label: t('storeOrders.importPriceVariance.directionAll') },
              { value: 'increase', label: t('storeOrders.importPriceVariance.directionIncrease') },
              { value: 'decrease', label: t('storeOrders.importPriceVariance.directionDecrease') },
            ]}
          />
          {/* 分店编码（精确匹配）与订单号（只匹配订单号）是低频条件，收进「更多筛选」，保留原有能力。 */}
          <MoreFiltersButton activeCount={moreFilterCount}>
            <label className="wh-pv-more-field">
              <span>{t('warehouseUi.priceVariance.storeCodeLabel')}</span>
              <Input
                allowClear
                placeholder={t('storeOrders.importPriceVariance.storeCodePlaceholder')}
                value={filterValues.storeCode ?? ''}
                onChange={(event) => updateFilterValues({ storeCode: event.target.value }, 'debounced')}
              />
              <span className="wh-pv-muted wh-pv-small">{t('warehouseUi.priceVariance.storeCodeHint')}</span>
            </label>
            <label className="wh-pv-more-field">
              <span>{t('storeOrders.importPriceVariance.orderNo')}</span>
              <Input
                allowClear
                placeholder={t('storeOrders.importPriceVariance.orderNoPlaceholder')}
                value={filterValues.orderNo ?? ''}
                onChange={(event) => updateFilterValues({ orderNo: event.target.value }, 'debounced')}
              />
              <span className="wh-pv-muted wh-pv-small">{t('warehouseUi.priceVariance.orderNoHint')}</span>
            </label>
          </MoreFiltersButton>
          <span className="list-toolbar-filter-spacer" />
          <Tooltip title={t('common.refresh')}>
            <Button icon={<ReloadOutlined />} aria-label={t('common.refresh')} onClick={() => void loadData()} />
          </Tooltip>
        </div>
        {activeFilterItems.length ? (
          <ActiveFilterBar
            items={activeFilterItems}
            onClearAll={() => {
              filterValuesRef.current = INITIAL_FILTER_VALUES
              updateFilterValues({}, 'immediate')
            }}
          />
        ) : null}
      </section>

      <div className="wh-pv-metrics" role="group" aria-label={t('warehouseUi.priceVariance.metricsLabel')}>
        {metrics.map((metric) => (
          <div key={metric.key} className="wh-pv-metric">
            <span className="wh-pv-metric-label">{metric.label}</span>
            <span className={`wh-pv-metric-value wh-pv-tone-${metric.tone ?? 'none'}`}>{metric.value}</span>
          </div>
        ))}
      </div>

      <section className="wh-pv-card" aria-label={t('warehouseUi.priceVariance.supplierTitle')}>
        <div className="wh-pv-card-head">
          <h2 className="wh-pv-card-title">{t('warehouseUi.priceVariance.supplierTitle')}</h2>
          <span className="wh-pv-muted wh-pv-small">{t('warehouseUi.priceVariance.supplierHint')}</span>
          <span className="wh-pv-spacer" />
          <span className="wh-pv-muted wh-pv-small wh-pv-number">
            {supplierSort
              ? t('warehouseUi.priceVariance.supplierCount', { count: sortedSupplierRows.length })
              : t('warehouseUi.priceVariance.supplierDefaultSort', { count: sortedSupplierRows.length })}
          </span>
        </div>
        <MeasuredTable<StoreOrderImportPriceVarianceSupplierSummary> metricId="warehouse.store-order-import-price-variance.table-1"
          className="wh-pv-table wh-pv-supplier-table"
          rowKey={getSupplierRowKey}
          loading={loading}
          columns={supplierSummaryColumns}
          dataSource={visibleSupplierRows}
          size="small"
          scroll={{ x: 860 }}
          pagination={false}
          onChange={handleSupplierTableChange}
          rowClassName={(row) =>
            row.supplierCode
              ? `wh-pv-supplier-row${row.supplierCode === filters.supplierCode ? ' is-selected' : ''}`
              : ''
          }
          onRow={(row) => ({ onClick: () => toggleSupplierFilter(row) })}
          locale={{
            emptyText: <Empty description={t('storeOrders.importPriceVariance.noSupplierVarianceData')} />,
          }}
        />
        {sortedSupplierRows.length > SUPPLIER_PREVIEW_COUNT ? (
          <div className="wh-pv-card-foot">
            <Button type="link" size="small" className="wh-pv-expand" onClick={() => setSupplierExpanded((current) => !current)}>
              {supplierExpanded
                ? t('warehouseUi.priceVariance.supplierCollapse')
                : t('warehouseUi.priceVariance.supplierExpandAll', { count: sortedSupplierRows.length })}
            </Button>
          </div>
        ) : null}
      </section>

      <section className="wh-pv-card" aria-label={t('warehouseUi.priceVariance.productsTitle')}>
        <div className="wh-pv-card-head">
          <h2 className="wh-pv-card-title">{t('warehouseUi.priceVariance.productsTitle')}</h2>
          <span className="wh-pv-muted wh-pv-small wh-pv-number">
            {t('warehouseUi.priceVariance.productsCount', { count: total })}
          </span>
          <span className="wh-pv-spacer" />
          <span className="wh-pv-edit-hint">
            <EditOutlined />
            {t('warehouseUi.priceVariance.editHint')}
          </span>
          <Popover
            trigger="click"
            placement="bottomRight"
            content={
              <Checkbox.Group<OptionalColumnKey>
                className="wh-pv-column-settings"
                value={optionalColumns}
                onChange={(value) => setOptionalColumns(value)}
                options={[
                  { value: 'unitVolume', label: t('storeOrders.importPriceVariance.unitVolume') },
                  { value: 'packingQuantity', label: t('storeOrders.importPriceVariance.packingQuantity') },
                ]}
              />
            }
          >
            <Button size="small" icon={<SettingOutlined />}>
              {t('common.listToolbar.columnSettings')}
            </Button>
          </Popover>
        </div>
        {selectedRowKeys.length > 0 ? (
          <div className="wh-pv-selection">
            <SelectionActionBar selectedCount={selectedRowKeys.length} onClearSelection={() => setSelectedRowKeys([])}>
              <Button
                size="small"
                icon={<DollarOutlined />}
                loading={batchWarehouseImportPriceSaving}
                disabled={batchWarehouseImportPriceSaving}
                onClick={openBatchWarehouseImportPriceModal}
              >
                {t('warehouseUi.priceVariance.batchButton')}
              </Button>
            </SelectionActionBar>
          </div>
        ) : null}
        <MeasuredTable<StoreOrderImportPriceVarianceItem> metricId="warehouse.store-order-import-price-variance.table-2"
          className="wh-pv-table wh-pv-product-table"
          rowKey={getRowKey}
          loading={loading}
          columns={productColumns}
          dataSource={items}
          rowSelection={{
            fixed: true,
            columnWidth: 44,
            selectedRowKeys,
            preserveSelectedRowKeys: true,
            onChange: setSelectedRowKeys,
            getCheckboxProps: (row) => ({ disabled: !row.productCode }),
          }}
          rowClassName={(row) => (selectedRowKeys.includes(getRowKey(row)) ? 'wh-pv-row-selected' : '')}
          // 默认列宽合计约 1096px（含勾选列），1440 宽屏下不出横向滚动；列设置里每打开一个可选列再加 96px。
          scroll={{ x: PRODUCT_TABLE_BASE_WIDTH + optionalColumns.length * PRODUCT_OPTIONAL_COLUMN_WIDTH }}
          onChange={handleTableChange}
          pagination={{
            current: pageNumber,
            pageSize,
            total,
            showSizeChanger: true,
            showTotal: (value) => t('warehouseUi.priceVariance.productsCount', { count: value }),
          }}
        />
      </section>

      <Modal
        open={batchWarehouseImportPriceOpen}
        title={t('warehouseUi.priceVariance.batchTitle', {
          count: selectedRowKeys.length,
        })}
        okText={t('common.save')}
        cancelText={t('common.cancel')}
        confirmLoading={batchWarehouseImportPriceSaving}
        maskClosable={!batchWarehouseImportPriceSaving}
        closable={!batchWarehouseImportPriceSaving}
        destroyOnHidden
        onCancel={closeBatchWarehouseImportPriceModal}
        onOk={() => void handleBatchWarehouseImportPriceSave()}
      >
        <Form form={batchWarehouseImportPriceForm} layout="vertical" preserve={false}>
          <Typography.Paragraph type="secondary">
            {t('warehouseUi.priceVariance.batchHint', {
              count: selectedRowKeys.length,
            })}
          </Typography.Paragraph>
          <Form.Item
            name="warehouseImportPrice"
            label={t('warehouseUi.priceVariance.colWarehousePrice')}
            rules={[
              {
                required: true,
                message: t('storeOrders.importPriceVariance.invalidWarehouseImportPrice'),
              },
              {
                type: 'number',
                min: 0,
                message: t('storeOrders.importPriceVariance.invalidWarehouseImportPrice'),
              },
            ]}
          >
            <InputNumber
              min={0}
              precision={2}
              autoFocus
              prefix="$"
              style={{ width: '100%' }}
              placeholder={t('warehouseUi.priceVariance.batchPlaceholder')}
            />
          </Form.Item>
        </Form>
      </Modal>

      <Modal
        open={detailModalOpen}
        title={t('storeOrders.importPriceVariance.detailModalTitle', {
          item: selectedProduct?.itemNumber || selectedProduct?.productCode || '--',
        })}
        width={1280}
        footer={null}
        destroyOnHidden
        onCancel={closeProductDetails}
      >
        <Row gutter={16} style={{ marginBottom: 16 }}>
          <Col xs={24} md={8}>
            <Statistic
              title={t('storeOrders.importPriceVariance.originalImportAmountTotal')}
              value={formatMoney(detailSummary.originalImportAmountTotal)}
            />
          </Col>
          <Col xs={24} md={8}>
            <Statistic
              title={t('storeOrders.importPriceVariance.baselineImportAmountTotal')}
              value={formatMoney(detailSummary.baselineImportAmountTotal)}
            />
          </Col>
          <Col xs={24} md={8}>
            <Statistic
              title={t('storeOrders.importPriceVariance.varianceAmountTotal')}
              value={formatMoney(detailSummary.varianceAmountTotal)}
              valueStyle={varianceStatisticStyle(detailSummary.varianceAmountTotal)}
            />
          </Col>
        </Row>
        <MeasuredTable<StoreOrderImportPriceVarianceDetailItem> metricId="warehouse.store-order-import-price-variance.table-3"
          rowKey={getDetailRowKey}
          loading={detailLoading}
          columns={detailColumns}
          dataSource={detailItems}
          scroll={{ x: 1450 }}
          onChange={handleDetailTableChange}
          pagination={{
            current: detailPageNumber,
            pageSize: detailPageSize,
            total: detailTotal,
            showSizeChanger: true,
            showTotal: (value) => t('storeOrders.importPriceVariance.totalRows', { total: value }),
          }}
        />
      </Modal>
    </PageContainer>
  )
}
