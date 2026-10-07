import {
  DownOutlined,
  DownloadOutlined,
  ExclamationCircleOutlined,
  MoreOutlined,
  PictureOutlined,
  ReloadOutlined,
  SearchOutlined,
  ShoppingCartOutlined,
  SnippetsOutlined,
} from '@ant-design/icons'
import {
  Button,
  Checkbox,
  Dropdown,
  Image,
  Input,
  InputNumber,
  Modal,
  Radio,
  Progress,
  Select,
  Space,
  Tag,
  Tooltip,
  TreeSelect,
  Typography,
  message,
} from 'antd'
import type { MenuProps } from 'antd'
import type { FilterDropdownProps, FilterValue, SorterResult, TablePaginationConfig } from 'antd/es/table/interface'
import type { ColumnsType } from 'antd/es/table'
import type { Key } from 'react'
import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { useTranslation } from 'react-i18next'
import PageContainer from '../../../components/PageContainer'
import ActiveFilterBar, { type ActiveFilterItem } from '../../../components/listToolbar/ActiveFilterBar'
import SelectionActionBar from '../../../components/listToolbar/SelectionActionBar'
import StatusPill from '../../../components/listToolbar/StatusPill'
import StatusTabs, { type StatusTabItem } from '../../../components/listToolbar/StatusTabs'
import { registerPageMessages } from '../../../i18n/registerPageMessages'
import { getActiveChinaSuppliers } from '../../../services/chinaSupplierService'
import { exportProductGradesToExcel } from '../../../services/exportService'
import { getActiveStores, type StoreOption } from '../../../services/storeService'
import {
  batchAssignProducts,
  getCategoryTree,
  type WarehouseCategoryNode,
} from '../../../services/warehouseCategoryService'
import {
  batchAddStoreOrderLines,
  createStoreOrder,
  getStoreOrderList,
} from '../../../services/storeOrderService'
import {
  batchUpdateGrades,
  createOrUpdateProductGrade,
  deleteProductGrade,
  getGradesByProductCodes,
  getProductGradeList,
} from '../../../services/productGradeService'
import {
  PRODUCT_GRADE_CONFIG,
  type ProductGradeListItem,
  type ProductGradeListParams,
} from '../../../types/productGrade'
import {
  StoreOrderFlowStatus,
  StoreOrderStatusColorMap,
  type StoreOrderListItem,
} from '../../../types/storeOrder'
import {
  ALL_PRODUCTS_FILTER_KEY,
  UNCATEGORIZED_PRODUCTS_FILTER_KEY,
  buildFilterCategoryTreeOptions,
} from '../Categories/categoryProductFilters'
import CategoryTreePicker from '../Products/CategoryTreePicker'
import { formatWarehouseCategoryNodeName } from '../Products/categoryPath'
import BatchPriceModal from './BatchPriceModal'
import PasteImportModal from './PasteImportModal'
import {
  GRADE_KEYS,
  buildGradeCountPlan,
  collectGradeCounts,
  formatPriceRangeSummary,
  getGradeWarehouseMismatch,
  gradeFilterToTab,
  gradeTabToFilter,
  hasNonGradeFilters,
  isGradeKey,
  type GradeCounts,
  type GradeKey,
  type GradeTabKey,
} from './productGradeView'
import { MeasuredTable } from '../../../components/MeasuredTable'
import productGradesMessagesEn from './productGradesMessages.en.json'
import productGradesMessagesZh from './productGradesMessages.zh.json'
import './productGrades.css'

registerPageMessages({ zh: productGradesMessagesZh, en: productGradesMessagesEn })

interface SupplierOption {
  label: string
  value: string
}

type ProductGradeSortOrder = 'ascend' | 'descend' | null

interface ProductGradeColumnFilters {
  supplierCode?: string
  categoryGuid?: string
  uncategorizedOnly?: boolean
  grade?: string
  hbProductNo?: string
  warehouseIsActive?: boolean
  domesticPriceMin?: number
  domesticPriceMax?: number
  importPriceMin?: number
  importPriceMax?: number
  oemPriceMin?: number
  oemPriceMax?: number
}

/** 仍在列头设置的条件（货号文本、三个价格区间）；供应商/分类/仓库状态/等级已移到工具栏与页签。 */
type ProductGradeTableFilters = Pick<
  ProductGradeColumnFilters,
  | 'hbProductNo'
  | 'domesticPriceMin'
  | 'domesticPriceMax'
  | 'importPriceMin'
  | 'importPriceMax'
  | 'oemPriceMin'
  | 'oemPriceMax'
>

interface LoadListOptions {
  filters?: ProductGradeColumnFilters
  sortField?: string
  sortOrder?: ProductGradeSortOrder
  search?: string
}

type AddToOrderMode = 'existing' | 'new'

const EDITABLE_STORE_ORDER_STATUSES = [
  StoreOrderFlowStatus.ShoppingCart,
  StoreOrderFlowStatus.Submitted,
]
const ORDER_DROPDOWN_PAGE_SIZE = 20
const SEARCH_DEBOUNCE_MS = 300
const IMAGE_FALLBACK = 'data:image/svg+xml;base64,PHN2ZyB3aWR0aD0iNDgiIGhlaWdodD0iNDgiIHhtbG5zPSJodHRwOi8vd3d3LnczLm9yZy8yMDAwL3N2ZyI+PHJlY3Qgd2lkdGg9IjQ4IiBoZWlnaHQ9IjQ4IiBmaWxsPSIjZjBmMGYwIi8+PHRleHQgeD0iMjQiIHk9IjI4IiB0ZXh0LWFuY2hvcj0ibWlkZGxlIiBmb250LXNpemU9IjEyIiBmaWxsPSIjY2NjIj7ml6DnvKnnlaXimLQ8L3RleHQ+PC9zdmc+'

// 等级简称（页签、行内下拉、勾选条按钮提示共用）；用字面量键便于文案契约测试核对。
const GRADE_SHORT_LABEL_KEYS: Record<GradeKey, string> = {
  A: 'warehouseUi.productGrades.gradeShortA',
  B: 'warehouseUi.productGrades.gradeShortB',
  C: 'warehouseUi.productGrades.gradeShortC',
  D: 'warehouseUi.productGrades.gradeShortD',
}

function formatAmount(value?: number) {
  if (value === undefined || value === null) return '--'
  return value.toFixed(2)
}

function encodePriceRange(min?: number, max?: number) {
  if (min === undefined && max === undefined) return undefined
  return `${min ?? ''}|${max ?? ''}`
}

function parsePriceRange(value?: Key | boolean): { min?: number; max?: number } {
  if (typeof value !== 'string') return {}
  const [minText, maxText] = value.split('|')
  const min = minText === '' ? undefined : Number(minText)
  const max = maxText === '' ? undefined : Number(maxText)
  return {
    min: Number.isFinite(min) ? min : undefined,
    max: Number.isFinite(max) ? max : undefined,
  }
}

function getSingleFilterValue(value?: FilterValue | null) {
  const first = value?.[0]
  return first == null || first === '' ? undefined : String(first)
}

function normalizeFilterNumber(value: string | number | null) {
  if (value === null || value === '') return undefined
  const numberValue = Number(value)
  return Number.isFinite(numberValue) ? numberValue : undefined
}

function getOrderStatusI18nKey(status: StoreOrderFlowStatus) {
  return status === StoreOrderFlowStatus.ShoppingCart
    ? 'productGrade.orderStatusShoppingCart'
    : 'productGrade.orderStatusSubmitted'
}

/** 列表请求与页签计数共用同一套筛选参数映射，保证页签数与列表口径一致。 */
function toListFilterParams(activeFilters: ProductGradeColumnFilters, activeSearch: string): ProductGradeListParams {
  return {
    search: activeSearch.trim() || undefined,
    grade: activeFilters.grade || undefined,
    supplierCode: activeFilters.supplierCode,
    hbProductNo: activeFilters.hbProductNo,
    categoryGuid: activeFilters.categoryGuid,
    uncategorizedOnly: activeFilters.uncategorizedOnly,
    warehouseIsActive: activeFilters.warehouseIsActive,
    domesticPriceMin: activeFilters.domesticPriceMin,
    domesticPriceMax: activeFilters.domesticPriceMax,
    importPriceMin: activeFilters.importPriceMin,
    importPriceMax: activeFilters.importPriceMax,
    oemPriceMin: activeFilters.oemPriceMin,
    oemPriceMax: activeFilters.oemPriceMax,
  }
}

function collectCategoryExpandedKeys(nodes: WarehouseCategoryNode[], maxLevel: number, level = 1): string[] {
  if (level > maxLevel) {
    return []
  }

  return nodes.flatMap((node) => [
    node.categoryGUID,
    ...collectCategoryExpandedKeys(node.children || [], maxLevel, level + 1),
  ])
}

function findWarehouseCategory(
  nodes: WarehouseCategoryNode[],
  targetGuid?: string,
): WarehouseCategoryNode | undefined {
  if (!targetGuid) {
    return undefined
  }

  for (const node of nodes) {
    if (node.categoryGUID === targetGuid) {
      return node
    }
    const child = findWarehouseCategory(node.children || [], targetGuid)
    if (child) {
      return child
    }
  }

  return undefined
}

function collectCategoryAndDescendantGuids(nodes: WarehouseCategoryNode[], targetGuid?: string): Set<string> {
  const target = findWarehouseCategory(nodes, targetGuid)
  const result = new Set<string>()

  const visit = (node?: WarehouseCategoryNode) => {
    if (!node) {
      return
    }
    result.add(node.categoryGUID)
    ;(node.children || []).forEach(visit)
  }

  visit(target)
  return result
}

/** 等级字母徽标：A 紫 / B 蓝 / C 橙 / D 红；后端扩展的其他等级用中性色。 */
function GradeLetter({ grade }: { grade: string }) {
  return (
    <span
      className={`wh-grades-letter ${isGradeKey(grade) ? `wh-grades-letter-${grade}` : 'wh-grades-letter-other'}`}
      aria-hidden="true"
    >
      {grade || '?'}
    </span>
  )
}

/** 商品单元格：图片 + 货号 + 名称。图片懒加载、异步解码、固定尺寸，避免虚拟滚动时抖动。 */
function ProductGradeProductCell({ record }: { record: ProductGradeListItem }) {
  return (
    <div className="wh-grades-product">
      {record.productImage ? (
        <Image
          src={record.productImage}
          alt={record.productName || record.hbProductNo || record.productCode}
          width={40}
          height={40}
          loading="lazy"
          decoding="async"
          className="wh-grades-product-image"
          preview={{ mask: '' }}
          fallback={IMAGE_FALLBACK}
        />
      ) : (
        <span className="wh-grades-product-placeholder" aria-hidden="true">
          <PictureOutlined />
        </span>
      )}
      <div className="wh-grades-product-text">
        <div className="wh-grades-product-code">{record.hbProductNo || '--'}</div>
        <Tooltip title={record.productName || undefined}>
          <div className="wh-grades-product-name">{record.productName || '--'}</div>
        </Tooltip>
      </div>
    </div>
  )
}

function ProductGradeSupplierCell({ record }: { record: ProductGradeListItem }) {
  if (!record.supplierName && !record.supplierCode) {
    return <>--</>
  }
  return (
    <div className="wh-grades-two-line">
      <div className="wh-grades-two-line-main" title={record.supplierName || undefined}>
        {record.supplierName || record.supplierCode}
      </div>
      {record.supplierName && record.supplierCode ? (
        <div className="wh-grades-two-line-sub">{record.supplierCode}</div>
      ) : null}
    </div>
  )
}

/** 仓库状态胶囊 + 等级与上下架不一致的琥珀色提示（只按当前行字段判断）。 */
function ProductGradeWarehouseStatusCell({ record }: { record: ProductGradeListItem }) {
  const { t } = useTranslation()
  const mismatch = getGradeWarehouseMismatch(record.grade, record.warehouseIsActive)

  return (
    <div className="wh-grades-status">
      {record.warehouseIsActive === true ? (
        <StatusPill tone="green">{t('productGrade.warehouseActive')}</StatusPill>
      ) : record.warehouseIsActive === false ? (
        <StatusPill tone="gray">{t('productGrade.warehouseInactive')}</StatusPill>
      ) : (
        <StatusPill tone="gray">{t('productGrade.warehouseStatusUnknown')}</StatusPill>
      )}
      {mismatch ? (
        <div className="wh-grades-mismatch">
          <ExclamationCircleOutlined aria-hidden="true" />
          {mismatch === 'coreDelisted'
            ? t('warehouseUi.productGrades.mismatchCoreDelisted')
            : t('warehouseUi.productGrades.mismatchNoStockListed')}
        </div>
      ) : null}
    </div>
  )
}

export default function ProductGradeManagementPage() {
  const { t, i18n } = useTranslation()
  const [loading, setLoading] = useState(false)
  const [data, setData] = useState<ProductGradeListItem[]>([])
  const [page, setPage] = useState(1)
  const [pageSize, setPageSize] = useState(50)
  const [total, setTotal] = useState(0)
  const [search, setSearch] = useState('')
  const [columnFilters, setColumnFilters] = useState<ProductGradeColumnFilters>({})
  const [sortField, setSortField] = useState<string | undefined>(undefined)
  const [sortOrder, setSortOrder] = useState<ProductGradeSortOrder>(null)
  const [gradeCounts, setGradeCounts] = useState<GradeCounts>({})
  const [suppliers, setSuppliers] = useState<SupplierOption[]>([])
  const [supplierLoading, setSupplierLoading] = useState(false)
  const [suppliersLoaded, setSuppliersLoaded] = useState(false)
  const [categoryTree, setCategoryTree] = useState<WarehouseCategoryNode[]>([])
  const [categoryLoading, setCategoryLoading] = useState(false)
  const [categoriesLoaded, setCategoriesLoaded] = useState(false)
  const [categoryEditOpen, setCategoryEditOpen] = useState(false)
  const [categoryEditRecord, setCategoryEditRecord] = useState<ProductGradeListItem | null>(null)
  const [batchCategoryOpen, setBatchCategoryOpen] = useState(false)
  const [targetCategoryGuid, setTargetCategoryGuid] = useState<string | undefined>(undefined)
  const [categoryExpandedKeys, setCategoryExpandedKeys] = useState<string[]>([])
  const [categorySaving, setCategorySaving] = useState(false)
  const [batchCategorySaving, setBatchCategorySaving] = useState(false)
  const [selectedRowKeys, setSelectedRowKeys] = useState<string[]>([])
  const [pasteImportOpen, setPasteImportOpen] = useState(false)
  const [batchPriceOpen, setBatchPriceOpen] = useState(false)
  const [exportOpen, setExportOpen] = useState(false)
  const [exportIncludeImage, setExportIncludeImage] = useState(true)
  const [exporting, setExporting] = useState(false)
  const [exportProgress, setExportProgress] = useState(0)
  const [exportMessage, setExportMessage] = useState('')
  const [addOrderOpen, setAddOrderOpen] = useState(false)
  const [addOrderMode, setAddOrderMode] = useState<AddToOrderMode>('existing')
  // 本次要加入订单的商品：勾选条入口为全部已选，行内 ⋯ 入口只有当前行。
  const [addOrderProductCodes, setAddOrderProductCodes] = useState<string[]>([])
  const [editableOrders, setEditableOrders] = useState<StoreOrderListItem[]>([])
  const [orderPage, setOrderPage] = useState(0)
  const [orderTotal, setOrderTotal] = useState(0)
  const [orderOptionsLoaded, setOrderOptionsLoaded] = useState(false)
  const [orderHasMore, setOrderHasMore] = useState(false)
  const [storeOptions, setStoreOptions] = useState<StoreOption[]>([])
  const [orderLoading, setOrderLoading] = useState(false)
  const [storeLoading, setStoreLoading] = useState(false)
  const [addOrderSubmitting, setAddOrderSubmitting] = useState(false)
  const [targetOrderGuid, setTargetOrderGuid] = useState<string | undefined>(undefined)
  const [targetStoreCode, setTargetStoreCode] = useState<string | undefined>(undefined)
  const [orderKeyword, setOrderKeyword] = useState('')
  const [newOrderRemarks, setNewOrderRemarks] = useState('')
  const listAbortRef = useRef<AbortController | null>(null)
  const listRequestSeqRef = useRef(0)
  const countAbortRef = useRef<AbortController | null>(null)
  const countRequestSeqRef = useRef(0)
  const orderRequestSeqRef = useRef(0)
  const supplierAbortRef = useRef<AbortController | null>(null)
  // 最近一次真正发出列表请求时用的关键词：防抖到期时若与它相同（下拉筛选已顺带查过）就不重复请求。
  const lastAppliedSearchRef = useRef('')
  // 写操作后的「刷新列表 + 页签计数」，始终按最新页码与筛选执行（撤销按钮等延迟回调不会用到旧闭包）。
  const refreshListAndCountsRef = useRef<() => void>(() => undefined)

  const gradeFullLabel = useCallback(
    (grade: GradeKey) => t(`productGrade.${PRODUCT_GRADE_CONFIG[grade].i18nKey}`),
    [t],
  )
  const categoryFilterTreeOptions = useMemo(
    // 「全部分类」用清空表示，树里只保留「未分类商品」快捷项和真实分类。
    () => buildFilterCategoryTreeOptions(categoryTree, t, i18n.language).filter(
      (option) => option.value !== ALL_PRODUCTS_FILTER_KEY,
    ),
    [categoryTree, i18n.language, t],
  )
  const selectedTargetCategory = useMemo(
    () => findWarehouseCategory(categoryTree, targetCategoryGuid),
    [categoryTree, targetCategoryGuid],
  )
  const activeGradeTab = gradeFilterToTab(columnFilters.grade)

  const loadSuppliers = useCallback(async () => {
    if (suppliersLoaded || supplierLoading) {
      return
    }

    supplierAbortRef.current?.abort()
    const controller = new AbortController()
    supplierAbortRef.current = controller
    setSupplierLoading(true)
    try {
      const result = await getActiveChinaSuppliers(controller.signal)
      setSuppliers(
        result.map((item) => ({
          label: `${item.supplierCode} - ${item.supplierName}`,
          value: item.supplierCode,
        })),
      )
      setSuppliersLoaded(true)
    } catch (error) {
      if (controller.signal.aborted) return
      console.error(error)
      message.error(t('productGrade.loadSuppliersFailed'))
    } finally {
      if (supplierAbortRef.current === controller) {
        supplierAbortRef.current = null
        setSupplierLoading(false)
      }
    }
  }, [supplierLoading, suppliersLoaded, t])

  const loadCategories = useCallback(async () => {
    if (categoriesLoaded || categoryLoading) {
      return
    }

    setCategoryLoading(true)
    try {
      setCategoryTree(await getCategoryTree())
      setCategoriesLoaded(true)
    } catch (error) {
      console.error(error)
      message.error(t('productGrade.loadCategoriesFailed'))
    } finally {
      setCategoryLoading(false)
    }
  }, [categoriesLoaded, categoryLoading, t])

  const ensureCategoriesLoaded = useCallback(async () => {
    if (categoriesLoaded || categoryLoading) {
      return categoryTree
    }

    setCategoryLoading(true)
    try {
      const tree = await getCategoryTree()
      setCategoryTree(tree)
      setCategoriesLoaded(true)
      return tree
    } catch (error) {
      console.error(error)
      message.error(t('productGrade.loadCategoriesFailed'))
      return []
    } finally {
      setCategoryLoading(false)
    }
  }, [categoriesLoaded, categoryLoading, categoryTree, t])

  const loadList = useCallback(async (
    nextPage = page,
    nextPageSize = pageSize,
    options: LoadListOptions = {},
  ) => {
    const activeFilters = options.filters ?? columnFilters
    // 显式传入排序（包括清除排序时的 undefined / null）就以传入为准；用 ?? 会把「清除」回退成旧排序。
    const activeSortField = 'sortField' in options ? options.sortField : sortField
    const activeSortOrder = 'sortOrder' in options ? options.sortOrder : sortOrder
    const activeSearch = options.search ?? search
    lastAppliedSearchRef.current = activeSearch
    const requestSeq = listRequestSeqRef.current + 1
    listRequestSeqRef.current = requestSeq
    listAbortRef.current?.abort()
    const controller = new AbortController()
    listAbortRef.current = controller
    setLoading(true)
    try {
      const result = await getProductGradeList({
        page: nextPage,
        pageSize: nextPageSize,
        ...toListFilterParams(activeFilters, activeSearch),
        sortField: activeSortField,
        sortDirection: activeSortOrder === 'ascend' ? 'asc' : activeSortOrder === 'descend' ? 'desc' : undefined,
        signal: controller.signal,
      })
      if (requestSeq !== listRequestSeqRef.current) {
        return
      }
      setData(result.items)
      setTotal(result.total)
      setPage(result.page)
      setPageSize(result.pageSize)
    } catch (error) {
      if (controller.signal.aborted) return
      console.error(error)
      message.error(t('productGrade.loadListFailed'))
    } finally {
      if (requestSeq === listRequestSeqRef.current) {
        setLoading(false)
      }
      if (listAbortRef.current === controller) {
        listAbortRef.current = null
      }
    }
  }, [columnFilters, page, pageSize, search, sortField, sortOrder, t])

  /**
   * 页签计数：每个等级各发一条 pageSize=1 的请求取 total，带上除等级外的同样筛选。
   * 新一轮发出时取消上一轮，只采纳最新一轮结果；单条失败时该页签不显示计数。
   */
  const loadGradeCounts = useCallback(async (activeFilters: ProductGradeColumnFilters, activeSearch: string) => {
    const requestSeq = countRequestSeqRef.current + 1
    countRequestSeqRef.current = requestSeq
    countAbortRef.current?.abort()
    const controller = new AbortController()
    countAbortRef.current = controller

    const plan = buildGradeCountPlan(hasNonGradeFilters(activeFilters, activeSearch))
    const filterParams = toListFilterParams({ ...activeFilters, grade: undefined }, activeSearch)
    const results = await Promise.allSettled(
      plan.map((request) => getProductGradeList({
        ...(request.withFilters ? filterParams : {}),
        grade: request.grade,
        page: 1,
        pageSize: 1,
        signal: controller.signal,
      })),
    )

    if (requestSeq !== countRequestSeqRef.current) {
      return
    }
    countAbortRef.current = null
    setGradeCounts(collectGradeCounts(plan, results))
  }, [])

  useEffect(() => {
    refreshListAndCountsRef.current = () => {
      void loadList(page, pageSize)
      void loadGradeCounts(columnFilters, search)
    }
  })

  useEffect(() => {
    void loadList(1, pageSize)
    void loadGradeCounts({}, '')
    return () => {
      listAbortRef.current?.abort()
      countAbortRef.current?.abort()
      supplierAbortRef.current?.abort()
    }
  }, [])

  useEffect(() => {
    // 关键词防抖约 300ms 后即时查询（不再需要「查询」按钮）；已被其他筛选顺带查过的关键词不重复请求。
    if (search === lastAppliedSearchRef.current) {
      return
    }
    const timer = window.setTimeout(() => {
      if (search === lastAppliedSearchRef.current) {
        return
      }
      void loadList(1, pageSize, { search })
      void loadGradeCounts(columnFilters, search)
    }, SEARCH_DEBOUNCE_MS)
    return () => window.clearTimeout(timer)
  }, [search])

  /** 工具栏筛选与页签：改了立即查第 1 页；等级页签切换不影响各页签计数，无需重算。 */
  const applyFilters = (
    nextFilters: ProductGradeColumnFilters,
    options: { search?: string; refreshCounts?: boolean } = {},
  ) => {
    const activeSearch = options.search ?? search
    setColumnFilters(nextFilters)
    void loadList(1, pageSize, { filters: nextFilters, search: activeSearch })
    if (options.refreshCounts !== false) {
      void loadGradeCounts(nextFilters, activeSearch)
    }
  }

  const removeFilterFields = (fields: Array<keyof ProductGradeColumnFilters>) => {
    const nextFilters = { ...columnFilters }
    fields.forEach((field) => {
      delete nextFilters[field]
    })
    applyFilters(nextFilters)
  }

  const handleGradeTabChange = (tab: GradeTabKey) => {
    applyFilters({ ...columnFilters, grade: gradeTabToFilter(tab) }, { refreshCounts: false })
  }

  const handleCategoryFilterChange = (value?: string) => {
    // 「未分类商品」哨兵值转成 uncategorizedOnly；清空即全部分类。分类筛选含子分类由后端处理。
    const categoryFilterValue = value || undefined
    applyFilters({
      ...columnFilters,
      categoryGuid: categoryFilterValue
        && categoryFilterValue !== UNCATEGORIZED_PRODUCTS_FILTER_KEY
        && categoryFilterValue !== ALL_PRODUCTS_FILTER_KEY
        ? categoryFilterValue
        : undefined,
      uncategorizedOnly: categoryFilterValue === UNCATEGORIZED_PRODUCTS_FILTER_KEY ? true : undefined,
    })
  }

  const handleClearAllFilters = () => {
    // 「清空全部」清掉关键词与工具栏/列头条件；等级页签是主筛选，保持当前页签。
    setSearch('')
    applyFilters({ grade: columnFilters.grade }, { search: '' })
  }

  const handleDelete = useCallback(async (id: string) => {
    try {
      await deleteProductGrade(id)
      message.success(t('common.deleteSuccess'))
      refreshListAndCountsRef.current()
    } catch (error) {
      console.error(error)
      message.error(t('common.deleteFailed'))
    }
  }, [t])

  const handleBatchUpdate = async (targetGrade: GradeKey) => {
    if (selectedRowKeys.length === 0) {
      message.warning(t('productGrade.selectProductsFirst'))
      return
    }
    try {
      await batchUpdateGrades({
        items: selectedRowKeys.map((productCode) => ({
          productCode,
          grade: targetGrade,
        })),
      })
      message.success(t('productGrade.batchUpdateSuccess', { count: selectedRowKeys.length }))
      setSelectedRowKeys([])
      refreshListAndCountsRef.current()
    } catch (error) {
      console.error(error)
      message.error(t('productGrade.batchUpdateFailed'))
    }
  }

  const confirmBatchGrade = (targetGrade: GradeKey) => {
    if (selectedRowKeys.length === 0) {
      message.warning(t('productGrade.selectProductsFirst'))
      return
    }
    // 批量改等级影响多行，先确认再提交（行内单个修改可直接撤销，不再弹确认）。
    Modal.confirm({
      title: t('warehouseUi.productGrades.batchSetGradeConfirm', { count: selectedRowKeys.length, grade: targetGrade }),
      content: t('warehouseUi.productGrades.batchSetGradeConfirmHint', { label: gradeFullLabel(targetGrade) }),
      okText: t('common.confirm'),
      cancelText: t('common.cancel'),
      onOk: () => handleBatchUpdate(targetGrade),
    })
  }

  const handleUndoGradeChange = useCallback(async (productCode: string, productLabel: string, previousGrade: string) => {
    try {
      // 撤销 = 用修改前的等级再调用一次同一个保存接口。
      await createOrUpdateProductGrade({ productCode, grade: previousGrade })
      message.success(t('warehouseUi.productGrades.gradeChangeUndone', { code: productLabel, grade: previousGrade }))
      refreshListAndCountsRef.current()
    } catch (error) {
      console.error(error)
      message.error(t('productGrade.updateFailed'))
    }
  }, [t])

  const handleInlineGradeChange = useCallback(async (record: ProductGradeListItem, newGrade: string) => {
    const previousGrade = record.grade
    if (newGrade === previousGrade) {
      return
    }
    const productLabel = record.hbProductNo || record.productCode
    try {
      await createOrUpdateProductGrade({ productCode: record.productCode, grade: newGrade })
      const messageKey = `product-grade-change-${record.productCode}`
      message.success({
        key: messageKey,
        duration: 6,
        content: (
          <span className="wh-grades-undo-message">
            {t('warehouseUi.productGrades.gradeChanged', { code: productLabel, from: previousGrade || '--', to: newGrade })}
            {previousGrade ? (
              <Button
                type="link"
                size="small"
                onClick={() => {
                  message.destroy(messageKey)
                  void handleUndoGradeChange(record.productCode, productLabel, previousGrade)
                }}
              >
                {t('warehouseUi.productGrades.undo')}
              </Button>
            ) : null}
          </span>
        ),
      })
      refreshListAndCountsRef.current()
    } catch (error) {
      console.error(error)
      message.error(t('productGrade.updateFailed'))
    }
  }, [handleUndoGradeChange, t])

  const openExportModal = () => {
    if (selectedRowKeys.length === 0) {
      message.warning(t('productGrade.selectProductsFirst'))
      return
    }
    setExportIncludeImage(true)
    setExportProgress(0)
    setExportMessage('')
    setExportOpen(true)
  }

  const handleExportExcel = async () => {
    if (selectedRowKeys.length === 0) {
      message.warning(t('productGrade.selectProductsFirst'))
      return
    }

    try {
      setExporting(true)
      setExportProgress(0)
      setExportMessage(t('productGrade.exportPreparing'))

      const selectedProductCodes = selectedRowKeys.map(String)
      // 选中项可能来自跨页选择，导出前按商品编码重新拉完整字段，避免只导出当前页残缺数据。
      const exportRows = await getGradesByProductCodes(selectedProductCodes)
      const rowOrder = new Map(selectedProductCodes.map((code, index) => [code, index]))
      const orderedRows = exportRows
        .filter((item) => rowOrder.has(item.productCode))
        .sort((a, b) => (rowOrder.get(a.productCode) ?? 0) - (rowOrder.get(b.productCode) ?? 0))

      if (!orderedRows.length) {
        message.warning(t('productGrade.noDataToExport'))
        return
      }

      const result = await exportProductGradesToExcel(orderedRows, {
        includeProductImage: exportIncludeImage,
        fileName: t('productGrade.exportFileName'),
        onProgress: (progress, nextMessage) => {
          setExportProgress(progress)
          setExportMessage(nextMessage)
        },
      })

      if (result.failedProductImages.length) {
        message.warning(t('productGrade.exportImageFailed', { count: result.failedProductImages.length }))
      } else {
        message.success(t('productGrade.exportSuccess'))
      }
      setExportOpen(false)
      setExportProgress(0)
      setExportMessage('')
    } catch (error) {
      console.error(error)
      message.error(error instanceof Error ? error.message : t('productGrade.exportFailed'))
    } finally {
      setExporting(false)
    }
  }

  const loadEditableOrders = useCallback(async (
    options: { keyword?: string; pageNumber?: number; append?: boolean } = {},
  ) => {
    const keyword = options.keyword ?? orderKeyword
    const pageNumber = options.pageNumber ?? 1
    const append = options.append ?? false
    const requestSeq = orderRequestSeqRef.current + 1
    orderRequestSeqRef.current = requestSeq
    if (!append) {
      setEditableOrders([])
      setOrderPage(0)
      setOrderTotal(0)
      setOrderHasMore(false)
      setOrderOptionsLoaded(false)
    }
    setOrderLoading(true)
    try {
      const result = await getStoreOrderList({
        keyword: keyword.trim() || undefined,
        pageNumber,
        pageSize: ORDER_DROPDOWN_PAGE_SIZE,
        statusList: EDITABLE_STORE_ORDER_STATUSES,
        // 商品等级弹窗只取下拉首屏，按创建时间倒序保证最新订单优先出现。
        sortBy: 'createdAt',
        sortDescending: true,
      })
      if (requestSeq !== orderRequestSeqRef.current) return
      const nextItems = result.items.filter((item) => EDITABLE_STORE_ORDER_STATUSES.includes(item.flowStatus))
      setEditableOrders((current) => {
        if (!append) return nextItems
        const byGuid = new Map(current.map((item) => [item.orderGUID, item]))
        nextItems.forEach((item) => byGuid.set(item.orderGUID, item))
        return Array.from(byGuid.values())
      })
      const nextPage = result.page || pageNumber
      const nextPageSize = result.pageSize || ORDER_DROPDOWN_PAGE_SIZE
      setOrderPage(nextPage)
      setOrderTotal(result.total)
      setOrderHasMore(nextPage * nextPageSize < result.total)
      setOrderOptionsLoaded(true)
    } catch (error) {
      if (requestSeq !== orderRequestSeqRef.current) return
      console.error(error)
      message.error(t('productGrade.loadStoreOrdersFailed'))
    } finally {
      if (requestSeq === orderRequestSeqRef.current) {
        setOrderLoading(false)
      }
    }
  }, [orderKeyword, t])

  const loadStores = useCallback(async () => {
    if (storeOptions.length > 0 || storeLoading) return
    setStoreLoading(true)
    try {
      setStoreOptions(await getActiveStores())
    } catch (error) {
      console.error(error)
      message.error(t('productGrade.loadStoresFailed'))
    } finally {
      setStoreLoading(false)
    }
  }, [storeLoading, storeOptions.length, t])

  const openAddOrderModal = (productCodes?: string[]) => {
    // 勾选条入口加入全部已选商品；行内 ⋯ 入口只加入这一行，不改变已勾选的其他商品。
    const codes = productCodes ?? selectedRowKeys.map(String)
    if (codes.length === 0) {
      message.warning(t('productGrade.selectProductsFirst'))
      return
    }
    setAddOrderProductCodes(codes)
    setAddOrderMode('existing')
    setTargetOrderGuid(undefined)
    setTargetStoreCode(undefined)
    setOrderKeyword('')
    setEditableOrders([])
    setOrderPage(0)
    setOrderTotal(0)
    setOrderOptionsLoaded(false)
    setOrderHasMore(false)
    setNewOrderRemarks('')
    setAddOrderOpen(true)
    void loadStores()
  }

  const handleAddToStoreOrder = async () => {
    if (addOrderProductCodes.length === 0) {
      message.warning(t('productGrade.selectProductsFirst'))
      return
    }
    if (addOrderMode === 'existing' && !targetOrderGuid) {
      message.warning(t('productGrade.selectTargetOrder'))
      return
    }
    if (addOrderMode === 'new' && !targetStoreCode) {
      message.warning(t('productGrade.selectTargetStore'))
      return
    }

    setAddOrderSubmitting(true)
    try {
      const selectedProductCodes = addOrderProductCodes
      // 跨页选择时当前表格不一定有完整行数据，提交前按商品编码回查最新商品字段。
      const latestRows = await getGradesByProductCodes(selectedProductCodes)
      const latestByCode = new Map(latestRows.map((item) => [item.productCode, item]))
      const missingProductCodes = selectedProductCodes.filter((productCode) => !latestByCode.has(productCode))
      if (missingProductCodes.length > 0) {
        message.warning(t('productGrade.selectedProductsMissing', {
          count: missingProductCodes.length,
          codes: missingProductCodes.slice(0, 10).join(', '),
        }))
        return
      }
      const items = selectedProductCodes
        .map((productCode) => {
          const item = latestByCode.get(productCode)!
          const minOrderQuantity = item?.minOrderQuantity
          const orderItem: { productCode: string; quantity: number; importPrice?: number } = {
            productCode,
            quantity: minOrderQuantity && minOrderQuantity > 0 ? minOrderQuantity : 1,
          }
          if (item?.importPrice !== undefined) {
            orderItem.importPrice = item.importPrice
          }
          return orderItem
        })
        .filter((item) => item.productCode)

      if (!items.length) {
        message.warning(t('productGrade.noMatchedProducts'))
        return
      }

      let orderGUID = targetOrderGuid!
      if (addOrderMode === 'new') {
        const createPayload = { storeCode: targetStoreCode!, remarks: newOrderRemarks.trim() }
        orderGUID = await createStoreOrder(createPayload.remarks ? createPayload : { storeCode: createPayload.storeCode })
      }

      await batchAddStoreOrderLines({ orderGUID, items })
      message.success(t('productGrade.addToStoreOrderSuccess', { count: items.length }))
      setAddOrderOpen(false)
      // 已加入订单的商品从勾选中移除：勾选条入口等同清空勾选，行内入口只去掉这一行。
      const addedCodeSet = new Set(selectedProductCodes)
      setSelectedRowKeys((keys) => keys.filter((key) => !addedCodeSet.has(key)))
      setAddOrderProductCodes([])
      setTargetOrderGuid(undefined)
      setTargetStoreCode(undefined)
      setOrderKeyword('')
      setOrderOptionsLoaded(false)
      setOrderHasMore(false)
      setOrderPage(0)
      setOrderTotal(0)
      setNewOrderRemarks('')
      if (addOrderMode === 'new') {
        void loadEditableOrders({ keyword: '', pageNumber: 1 })
      }
    } catch (error) {
      console.error(error)
      message.error(error instanceof Error ? error.message : t('productGrade.addToStoreOrderFailed'))
    } finally {
      setAddOrderSubmitting(false)
    }
  }

  const openCategoryEditModal = useCallback(async (record: ProductGradeListItem) => {
    setCategoryEditRecord(record)
    setTargetCategoryGuid(record.categoryGuid)
    setCategoryEditOpen(true)
    const tree = await ensureCategoriesLoaded()
    setCategoryExpandedKeys(collectCategoryExpandedKeys(tree, 1))
  }, [ensureCategoriesLoaded])

  const handleCategoryEditCancel = () => {
    if (categorySaving) {
      return
    }
    setCategoryEditOpen(false)
    setCategoryEditRecord(null)
    setTargetCategoryGuid(undefined)
  }

  const openBatchCategoryModal = useCallback(async () => {
    if (selectedRowKeys.length === 0) {
      message.warning(t('productGrade.selectProductsFirst'))
      return
    }

    setTargetCategoryGuid(undefined)
    setBatchCategoryOpen(true)
    const tree = await ensureCategoriesLoaded()
    setCategoryExpandedKeys(collectCategoryExpandedKeys(tree, 1))
  }, [ensureCategoriesLoaded, selectedRowKeys.length, t])

  const handleBatchCategoryCancel = () => {
    if (batchCategorySaving) {
      return
    }
    setBatchCategoryOpen(false)
    setTargetCategoryGuid(undefined)
  }

  const handleBatchCategorySave = async () => {
    const selectedProductCodes = selectedRowKeys.map(String)
    if (selectedProductCodes.length === 0) {
      message.warning(t('productGrade.selectProductsFirst'))
      return
    }
    if (!targetCategoryGuid) {
      message.warning(t('productGrade.selectTargetCategory'))
      return
    }

    setBatchCategorySaving(true)
    try {
      // 批量分类只修改本地 Product.WarehouseCategoryGUID，不影响等级、价格和上下架状态。
      const affected = await batchAssignProducts(targetCategoryGuid, selectedProductCodes)
      if (affected < selectedProductCodes.length) {
        throw new Error(t('productGrade.batchCategoryPartialFailed', {
          affected,
          total: selectedProductCodes.length,
        }))
      }

      const nextCategory = findWarehouseCategory(categoryTree, targetCategoryGuid)
      const nextCategoryName = nextCategory
        ? formatWarehouseCategoryNodeName(nextCategory, i18n.language)
        : ''
      const selectedCodeSet = new Set(selectedProductCodes)
      const filteredCategoryGuids = collectCategoryAndDescendantGuids(
        categoryTree,
        columnFilters.categoryGuid,
      )
      const shouldRemoveFromCurrentPage = Boolean(
        columnFilters.uncategorizedOnly
          || (columnFilters.categoryGuid && !filteredCategoryGuids.has(targetCategoryGuid)),
      )

      if (shouldRemoveFromCurrentPage) {
        const currentPageSelectedCount = data.filter((item) => selectedCodeSet.has(item.productCode)).length
        setData((items) => items.filter((item) => !selectedCodeSet.has(item.productCode)))
        setTotal((current) => Math.max(0, current - currentPageSelectedCount))
        // 商品移出当前分类筛选后，各等级页签的数量也随之变化。
        void loadGradeCounts(columnFilters, search)
      } else {
        setData((items) => items.map((item) => (
          selectedCodeSet.has(item.productCode)
            ? {
              ...item,
              categoryGuid: targetCategoryGuid,
              categoryName: nextCategory?.categoryName || nextCategoryName,
              categoryChineseName: nextCategory?.chineseName,
            }
            : item
        )))
      }

      message.success(t('productGrade.batchCategoryUpdateSuccess', { count: selectedProductCodes.length }))
      setBatchCategoryOpen(false)
      setSelectedRowKeys([])
      setTargetCategoryGuid(undefined)
    } catch (error) {
      console.error(error)
      message.error(error instanceof Error ? error.message : t('productGrade.categoryUpdateFailed'))
    } finally {
      setBatchCategorySaving(false)
    }
  }

  const handleCategoryEditSave = async () => {
    if (!categoryEditRecord) {
      return
    }
    if (!targetCategoryGuid) {
      message.warning(t('productGrade.selectTargetCategory'))
      return
    }
    if (targetCategoryGuid === categoryEditRecord.categoryGuid) {
      message.info(t('productGrade.categoryUnchanged'))
      return
    }

    setCategorySaving(true)
    try {
      // 分类修改只提交商品编码和目标分类，不触碰等级、价格、上下架等其他字段。
      const affected = await batchAssignProducts(targetCategoryGuid, [categoryEditRecord.productCode])
      if (affected < 1) {
        // 后端没有实际更新本地 Product 行时，不做前端本地覆盖，避免刷新后分类回退。
        throw new Error(t('productGrade.categoryUpdateFailed'))
      }
      const nextCategory = findWarehouseCategory(categoryTree, targetCategoryGuid)
      const nextCategoryName = nextCategory
        ? formatWarehouseCategoryNodeName(nextCategory, i18n.language)
        : categoryEditRecord.categoryName
      const filteredCategoryGuids = collectCategoryAndDescendantGuids(
        categoryTree,
        columnFilters.categoryGuid,
      )
      const shouldRemoveFromCurrentPage = Boolean(
        columnFilters.uncategorizedOnly
          || (columnFilters.categoryGuid && !filteredCategoryGuids.has(targetCategoryGuid)),
      )

      if (shouldRemoveFromCurrentPage) {
        setData((items) => items.filter((item) => item.productCode !== categoryEditRecord.productCode))
        setTotal((current) => Math.max(0, current - 1))
        void loadGradeCounts(columnFilters, search)
      } else {
        setData((items) => items.map((item) => (
          item.productCode === categoryEditRecord.productCode
            ? {
              ...item,
              categoryGuid: targetCategoryGuid,
              categoryName: nextCategory?.categoryName || nextCategoryName,
              categoryChineseName: nextCategory?.chineseName,
            }
            : item
        )))
      }

      message.success(t('productGrade.categoryUpdateSuccess'))
      setCategoryEditOpen(false)
      setCategoryEditRecord(null)
      setTargetCategoryGuid(undefined)
    } catch (error) {
      console.error(error)
      message.error(error instanceof Error ? error.message : t('productGrade.categoryUpdateFailed'))
    } finally {
      setCategorySaving(false)
    }
  }

  /** 只读取仍在列头的条件（货号、价格区间）；工具栏条件保留在 columnFilters 里不被覆盖。 */
  const getFiltersFromTable = (filters: Record<string, FilterValue | null>): ProductGradeTableFilters => {
    const domesticRange = parsePriceRange(filters.domesticPrice?.[0])
    const importRange = parsePriceRange(filters.importPrice?.[0])
    const oemRange = parsePriceRange(filters.oemPrice?.[0])

    return {
      hbProductNo: getSingleFilterValue(filters.hbProductNo),
      domesticPriceMin: domesticRange.min,
      domesticPriceMax: domesticRange.max,
      importPriceMin: importRange.min,
      importPriceMax: importRange.max,
      oemPriceMin: oemRange.min,
      oemPriceMax: oemRange.max,
    }
  }

  const handleTableChange = (
    pagination: TablePaginationConfig,
    filters: Record<string, FilterValue | null>,
    sorter: SorterResult<ProductGradeListItem> | SorterResult<ProductGradeListItem>[],
    extra: { action: 'paginate' | 'sort' | 'filter' },
  ) => {
    const currentSorter = Array.isArray(sorter) ? sorter[0] : sorter
    const rawField = currentSorter?.field || currentSorter?.column?.dataIndex
    const field = Array.isArray(rawField) ? rawField.join('.') : rawField ? String(rawField) : undefined
    const order = currentSorter?.order as ProductGradeSortOrder | undefined
    const nextSortField = field && order ? field : undefined
    const nextSortOrder = field && order ? order : null
    const nextFilters = { ...columnFilters, ...getFiltersFromTable(filters) }
    // 列头排序/过滤都走服务端，变化时回到第一页，避免只处理当前页数据。
    const nextPage = extra.action === 'paginate' ? pagination.current ?? 1 : 1
    // 表头受控状态在发请求前同步，异步成功回调不再用旧闭包覆盖当前筛选/排序。
    setColumnFilters(nextFilters)
    setSortField(nextSortField)
    setSortOrder(nextSortOrder)

    void loadList(nextPage, pagination.pageSize ?? pageSize, {
      filters: nextFilters,
      sortField: nextSortField,
      sortOrder: nextSortOrder,
    })
    if (extra.action === 'filter') {
      // 列头条件也参与页签计数口径。
      void loadGradeCounts(nextFilters, search)
    }
  }

  const formatProductGradeCategory = useCallback((record: ProductGradeListItem) => {
    const name = formatWarehouseCategoryNodeName({
      categoryName: record.categoryName || '',
      chineseName: record.categoryChineseName,
    }, i18n.language)

    return name || '--'
  }, [i18n.language])

  const renderTextFilterDropdown = ({
    selectedKeys,
    setSelectedKeys,
    confirm,
    clearFilters,
  }: FilterDropdownProps) => (
    <Space direction="vertical" style={{ padding: 8 }}>
      <Input
        autoFocus
        allowClear
        placeholder={t('productGrade.itemNumberFilterPlaceholder', '输入货号')}
        value={selectedKeys[0] as string | undefined}
        onChange={(event) => setSelectedKeys(event.target.value ? [event.target.value] : [])}
        onPressEnter={() => confirm()}
      />
      <Space>
        <Button type="primary" size="small" onClick={() => confirm()}>
          {t('common.query')}
        </Button>
        <Button
          size="small"
          onClick={() => {
            clearFilters?.()
            confirm()
          }}
        >
          {t('common.reset', '重置')}
        </Button>
      </Space>
    </Space>
  )

  const renderPriceFilterDropdown = (
    minValue: number | undefined,
    maxValue: number | undefined,
  ) => ({
    selectedKeys,
    setSelectedKeys,
    confirm,
    clearFilters,
  }: FilterDropdownProps) => {
    const selectedRangeValue = selectedKeys[0]
    const hasSelectedRange = typeof selectedRangeValue === 'string'
    const range = parsePriceRange(selectedRangeValue)
    const min = hasSelectedRange ? range.min : minValue
    const max = hasSelectedRange ? range.max : maxValue

    const updateRange = (nextMin?: number, nextMax?: number) => {
      const encoded = encodePriceRange(nextMin, nextMax)
      setSelectedKeys(encoded ? [encoded] : [])
    }

    return (
      <Space direction="vertical" style={{ padding: 8, width: 220 }}>
        <InputNumber
          placeholder={t('common.min', '最小值')}
          value={min}
          min={0}
          precision={2}
          style={{ width: '100%' }}
          onChange={(value) => updateRange(normalizeFilterNumber(value), max)}
        />
        <InputNumber
          placeholder={t('common.max', '最大值')}
          value={max}
          min={0}
          precision={2}
          style={{ width: '100%' }}
          onChange={(value) => updateRange(min, normalizeFilterNumber(value))}
        />
        <Space>
          <Button type="primary" size="small" onClick={() => confirm()}>
            {t('common.query')}
          </Button>
          <Button
            size="small"
            onClick={() => {
              clearFilters?.()
              confirm()
            }}
          >
            {t('common.reset', '重置')}
          </Button>
        </Space>
      </Space>
    )
  }

  const gradeSelectOptions = useMemo(
    () => GRADE_KEYS.map((grade) => ({
      value: grade,
      title: gradeFullLabel(grade),
      label: (
        <span className="wh-grades-grade-option">
          <GradeLetter grade={grade} />
          {t(GRADE_SHORT_LABEL_KEYS[grade])}
        </span>
      ),
    })),
    [gradeFullLabel, t],
  )

  const rowMenuItems = useMemo<MenuProps['items']>(
    () => [
      { key: 'category', label: t('warehouseUi.productGrades.changeCategory') },
      { key: 'order', label: t('productGrade.addToStoreOrder') },
      { type: 'divider' },
      { key: 'remove', label: t('warehouseUi.productGrades.removeGrade'), danger: true },
    ],
    [t],
  )

  const handleRowAction = (key: string, record: ProductGradeListItem) => {
    if (key === 'category') {
      void openCategoryEditModal(record)
      return
    }
    if (key === 'order') {
      openAddOrderModal([record.productCode])
      return
    }
    if (key === 'remove') {
      // 移除等级即原「删除等级」，保留二次确认。
      Modal.confirm({
        title: t('productGrade.confirmDelete'),
        content: t('productGrade.deleteGradeHint'),
        okText: t('warehouseUi.productGrades.removeGrade'),
        okButtonProps: { danger: true },
        cancelText: t('common.cancel'),
        onOk: () => handleDelete(record.id),
      })
    }
  }
  // 列定义做了 memo，行内 ⋯ 菜单通过 ref 调用最新的处理函数，避免读到旧的勾选/分店加载状态。
  const rowActionRef = useRef(handleRowAction)
  useEffect(() => {
    rowActionRef.current = handleRowAction
  })

  const columns = useMemo<ColumnsType<ProductGradeListItem>>(
    () => [
      {
        title: t('warehouseUi.productGrades.columnProduct'),
        dataIndex: 'hbProductNo',
        width: 192,
        sorter: true,
        sortOrder: sortField === 'hbProductNo' ? sortOrder : null,
        filterDropdown: renderTextFilterDropdown,
        filteredValue: columnFilters.hbProductNo ? [columnFilters.hbProductNo] : null,
        render: (_value: string | undefined, record) => <ProductGradeProductCell record={record} />,
      },
      {
        title: t('warehouseUi.productGrades.columnSupplier'),
        dataIndex: 'supplierName',
        width: 132,
        sorter: true,
        sortOrder: sortField === 'supplierName' ? sortOrder : null,
        render: (_value: string | undefined, record) => <ProductGradeSupplierCell record={record} />,
      },
      {
        title: t('productGrade.category'),
        dataIndex: 'categoryGuid',
        width: 96,
        render: (_value: string | undefined, record) => (
          <Button
            type="link"
            size="small"
            className="wh-grades-category-link"
            onClick={(event) => {
              event.stopPropagation()
              void openCategoryEditModal(record)
            }}
          >
            {formatProductGradeCategory(record)}
          </Button>
        ),
      },
      {
        title: t('warehouseUi.productGrades.columnGrade'),
        dataIndex: 'grade',
        width: 176,
        sorter: true,
        sortOrder: sortField === 'grade' ? sortOrder : null,
        render: (grade: string, record) => (
          <Select
            value={grade}
            size="small"
            className="wh-grades-grade-select"
            popupMatchSelectWidth={false}
            aria-label={t('warehouseUi.productGrades.changeGradeOf', { code: record.hbProductNo || record.productCode })}
            options={gradeSelectOptions}
            onChange={(value: string) => void handleInlineGradeChange(record, value)}
          />
        ),
      },
      {
        title: t('productGrade.warehouseStatus'),
        dataIndex: 'warehouseIsActive',
        width: 130,
        sorter: true,
        sortOrder: sortField === 'warehouseIsActive' ? sortOrder : null,
        render: (_value: boolean | null | undefined, record) => <ProductGradeWarehouseStatusCell record={record} />,
      },
      {
        title: t('warehouseUi.productGrades.columnDomesticPrice'),
        dataIndex: 'domesticPrice',
        width: 100,
        align: 'right',
        sorter: true,
        sortOrder: sortField === 'domesticPrice' ? sortOrder : null,
        filterDropdown: renderPriceFilterDropdown(
          columnFilters.domesticPriceMin,
          columnFilters.domesticPriceMax,
        ),
        filteredValue: encodePriceRange(columnFilters.domesticPriceMin, columnFilters.domesticPriceMax)
          ? [encodePriceRange(columnFilters.domesticPriceMin, columnFilters.domesticPriceMax)!]
          : null,
        render: (value?: number) => <span className="wh-grades-number wh-grades-number-muted">{formatAmount(value)}</span>,
      },
      {
        title: t('warehouseUi.productGrades.columnImportPrice'),
        dataIndex: 'importPrice',
        width: 100,
        align: 'right',
        sorter: true,
        sortOrder: sortField === 'importPrice' ? sortOrder : null,
        filterDropdown: renderPriceFilterDropdown(
          columnFilters.importPriceMin,
          columnFilters.importPriceMax,
        ),
        filteredValue: encodePriceRange(columnFilters.importPriceMin, columnFilters.importPriceMax)
          ? [encodePriceRange(columnFilters.importPriceMin, columnFilters.importPriceMax)!]
          : null,
        render: (value?: number) => <span className="wh-grades-number">{formatAmount(value)}</span>,
      },
      {
        title: t('warehouseUi.productGrades.columnRetailPrice'),
        dataIndex: 'oemPrice',
        width: 100,
        align: 'right',
        sorter: true,
        sortOrder: sortField === 'oemPrice' ? sortOrder : null,
        filterDropdown: renderPriceFilterDropdown(
          columnFilters.oemPriceMin,
          columnFilters.oemPriceMax,
        ),
        filteredValue: encodePriceRange(columnFilters.oemPriceMin, columnFilters.oemPriceMax)
          ? [encodePriceRange(columnFilters.oemPriceMin, columnFilters.oemPriceMax)!]
          : null,
        render: (value?: number) => <span className="wh-grades-number wh-grades-number-strong">{formatAmount(value)}</span>,
      },
      {
        title: '',
        key: 'action',
        width: 44,
        fixed: 'right',
        render: (_, record) => (
          <Dropdown
            trigger={['click']}
            menu={{
              items: rowMenuItems,
              onClick: ({ key, domEvent }) => {
                domEvent.stopPropagation()
                rowActionRef.current(String(key), record)
              },
            }}
          >
            <Button
              type="text"
              size="small"
              icon={<MoreOutlined />}
              aria-label={t('warehouseUi.productGrades.rowActions')}
              onClick={(event) => event.stopPropagation()}
            />
          </Dropdown>
        ),
      },
    ],
    [
      columnFilters,
      formatProductGradeCategory,
      gradeSelectOptions,
      handleInlineGradeChange,
      openCategoryEditModal,
      rowMenuItems,
      sortField,
      sortOrder,
      t,
    ],
  )

  const gradeTabItems = useMemo<StatusTabItem<GradeTabKey>[]>(
    () => [
      { key: 'all', label: t('warehouseUi.productGrades.tabAll'), count: gradeCounts.all },
      ...GRADE_KEYS.map((grade) => ({
        key: grade,
        label: t(GRADE_SHORT_LABEL_KEYS[grade]),
        count: gradeCounts[grade],
        prefix: <GradeLetter grade={grade} />,
      })),
    ],
    [gradeCounts, t],
  )

  const supplierFilterLabel = columnFilters.supplierCode
    ? suppliers.find((option) => option.value === columnFilters.supplierCode)?.label ?? columnFilters.supplierCode
    : undefined
  const categoryFilterNode = findWarehouseCategory(categoryTree, columnFilters.categoryGuid)
  const categoryFilterLabel = columnFilters.uncategorizedOnly
    ? t('warehouse.categories.uncategorizedOption', '未分类商品')
    : columnFilters.categoryGuid
      ? (categoryFilterNode ? formatWarehouseCategoryNodeName(categoryFilterNode, i18n.language) : columnFilters.categoryGuid)
      : undefined

  // 已生效筛选条：工具栏条件（关键词/供应商/分类/仓库状态）+ 列头条件（货号/价格区间）。等级由页签显示，不重复列出。
  const activeFilterItems: ActiveFilterItem[] = []
  if (search.trim()) {
    activeFilterItems.push({
      key: 'search',
      label: t('warehouseUi.productGrades.filterKeyword'),
      value: search.trim(),
      source: 'toolbar',
      onRemove: () => {
        setSearch('')
        applyFilters(columnFilters, { search: '' })
      },
    })
  }
  if (supplierFilterLabel) {
    activeFilterItems.push({
      key: 'supplierCode',
      label: t('warehouseUi.productGrades.filterSupplier'),
      value: supplierFilterLabel,
      source: 'toolbar',
      onRemove: () => removeFilterFields(['supplierCode']),
    })
  }
  if (categoryFilterLabel) {
    activeFilterItems.push({
      key: 'category',
      label: t('warehouseUi.productGrades.filterCategory'),
      value: categoryFilterLabel,
      source: 'toolbar',
      onRemove: () => removeFilterFields(['categoryGuid', 'uncategorizedOnly']),
    })
  }
  if (columnFilters.warehouseIsActive !== undefined) {
    activeFilterItems.push({
      key: 'warehouseIsActive',
      label: t('warehouseUi.productGrades.filterWarehouseStatus'),
      value: columnFilters.warehouseIsActive ? t('productGrade.warehouseActive') : t('productGrade.warehouseInactive'),
      source: 'toolbar',
      onRemove: () => removeFilterFields(['warehouseIsActive']),
    })
  }
  if (columnFilters.hbProductNo) {
    activeFilterItems.push({
      key: 'hbProductNo',
      label: t('warehouseUi.productGrades.filterItemNumber'),
      value: columnFilters.hbProductNo,
      source: 'column',
      onRemove: () => removeFilterFields(['hbProductNo']),
    })
  }
  const priceFilterChips: Array<{
    key: string
    label: string
    min?: number
    max?: number
    fields: Array<keyof ProductGradeColumnFilters>
  }> = [
    {
      key: 'domesticPrice',
      label: t('warehouseUi.productGrades.filterDomesticPrice'),
      min: columnFilters.domesticPriceMin,
      max: columnFilters.domesticPriceMax,
      fields: ['domesticPriceMin', 'domesticPriceMax'],
    },
    {
      key: 'importPrice',
      label: t('warehouseUi.productGrades.filterImportPrice'),
      min: columnFilters.importPriceMin,
      max: columnFilters.importPriceMax,
      fields: ['importPriceMin', 'importPriceMax'],
    },
    {
      key: 'oemPrice',
      label: t('warehouseUi.productGrades.filterRetailPrice'),
      min: columnFilters.oemPriceMin,
      max: columnFilters.oemPriceMax,
      fields: ['oemPriceMin', 'oemPriceMax'],
    },
  ]
  priceFilterChips.forEach((chip) => {
    const summary = formatPriceRangeSummary(chip.min, chip.max)
    if (summary) {
      activeFilterItems.push({
        key: chip.key,
        label: chip.label,
        value: summary,
        source: 'column',
        onRemove: () => removeFilterFields(chip.fields),
      })
    }
  })

  const selectionMoreItems: MenuProps['items'] = [
    { key: 'export', icon: <DownloadOutlined />, label: t('warehouseUi.productGrades.exportSelected') },
    // 批量改价可直接写 HQ，保留危险色；弹窗内仍有目标库选择与风险提示。
    { key: 'batchPrice', label: t('warehouseUi.productGrades.batchPrice'), danger: true },
  ]

  const categoryEditCurrentText = categoryEditRecord ? formatProductGradeCategory(categoryEditRecord) : '--'
  const categoryEditTargetText = selectedTargetCategory
    ? formatWarehouseCategoryNodeName(selectedTargetCategory, i18n.language)
    : ''

  return (
    <PageContainer
      compact
      title={t('productGrade.title')}
      subtitle={
        gradeCounts.graded === undefined
          ? undefined
          : t('warehouseUi.productGrades.subtitle', { count: gradeCounts.graded })
      }
      extra={
        <Space size={8} wrap>
          {/* 导出只导出已勾选的商品：未勾选时禁用并说明原因（勾选条「更多」里也有同一入口）。 */}
          <Tooltip title={selectedRowKeys.length ? undefined : t('warehouseUi.productGrades.exportNeedSelection')}>
            <Button icon={<DownloadOutlined />} disabled={selectedRowKeys.length === 0} onClick={openExportModal}>
              {t('warehouseUi.productGrades.export')}
            </Button>
          </Tooltip>
          <Button type="primary" icon={<SnippetsOutlined />} onClick={() => setPasteImportOpen(true)}>
            {t('warehouseUi.productGrades.pasteImport')}
          </Button>
        </Space>
      }
    >
      <section className="wh-grades-card" aria-label={t('warehouseUi.productGrades.listRegion')}>
        <div className="wh-grades-tabs">
          <StatusTabs<GradeTabKey>
            items={gradeTabItems}
            activeKey={activeGradeTab}
            onChange={handleGradeTabChange}
            ariaLabel={t('warehouseUi.productGrades.tabsLabel')}
          />
        </div>

        <div className="wh-grades-toolbar">
          <Input
            allowClear
            prefix={<SearchOutlined />}
            placeholder={t('warehouseUi.productGrades.searchPlaceholder')}
            aria-label={t('warehouseUi.productGrades.searchPlaceholder')}
            value={search}
            onChange={(event) => setSearch(event.target.value)}
            className="wh-grades-search"
          />
          <Select
            showSearch
            allowClear
            placeholder={t('warehouseUi.productGrades.supplierAll')}
            value={columnFilters.supplierCode}
            onDropdownVisibleChange={(open) => {
              if (open) void loadSuppliers()
            }}
            onChange={(value?: string) => applyFilters({ ...columnFilters, supplierCode: value || undefined })}
            options={suppliers}
            loading={supplierLoading}
            optionFilterProp="label"
            className="wh-grades-filter-supplier"
            popupMatchSelectWidth={300}
          />
          <TreeSelect
            showSearch
            allowClear
            placeholder={t('warehouseUi.productGrades.categoryAll')}
            value={columnFilters.uncategorizedOnly ? UNCATEGORIZED_PRODUCTS_FILTER_KEY : columnFilters.categoryGuid}
            treeData={categoryFilterTreeOptions}
            treeNodeFilterProp="searchText"
            loading={categoryLoading}
            onOpenChange={(open) => {
              if (open) void loadCategories()
            }}
            onChange={(value?: string) => handleCategoryFilterChange(value)}
            className="wh-grades-filter-category"
            popupMatchSelectWidth={320}
            listHeight={360}
            notFoundContent={categoryLoading ? t('common.loading') : t('warehouse.categories.noCategoryData')}
          />
          <Select
            allowClear
            placeholder={t('warehouseUi.productGrades.warehouseStatusAll')}
            value={columnFilters.warehouseIsActive === undefined ? undefined : String(columnFilters.warehouseIsActive)}
            options={[
              { label: t('productGrade.warehouseActive'), value: 'true' },
              { label: t('productGrade.warehouseInactive'), value: 'false' },
            ]}
            onChange={(value?: string) => applyFilters({
              ...columnFilters,
              warehouseIsActive: value === 'true' ? true : value === 'false' ? false : undefined,
            })}
            className="wh-grades-filter-status"
          />
          <span className="wh-grades-spacer" />
          <Tooltip title={t('common.refresh')}>
            <Button
              icon={<ReloadOutlined />}
              aria-label={t('common.refresh')}
              onClick={() => refreshListAndCountsRef.current()}
            />
          </Tooltip>
        </div>

        <div className="wh-grades-active-filters">
          {/* 没有生效条件时整行隐藏，与其他仓库列表页一致。 */}
          {activeFilterItems.length ? <ActiveFilterBar items={activeFilterItems} onClearAll={handleClearAllFilters} /> : null}
        </div>

        <div className="wh-grades-selection">
          <SelectionActionBar selectedCount={selectedRowKeys.length} onClearSelection={() => setSelectedRowKeys([])}>
            <span className="wh-grades-selection-label">{t('warehouseUi.productGrades.setGradeTo')}</span>
            <span className="wh-grades-grade-buttons">
              {GRADE_KEYS.map((grade) => (
                <Tooltip key={grade} title={gradeFullLabel(grade)}>
                  <Button
                    size="small"
                    className={`wh-grades-grade-button wh-grades-grade-button-${grade}`}
                    aria-label={t('warehouseUi.productGrades.setGradeTitle', { label: gradeFullLabel(grade) })}
                    onClick={() => confirmBatchGrade(grade)}
                  >
                    {grade}
                  </Button>
                </Tooltip>
              ))}
            </span>
            <Button size="small" onClick={() => void openBatchCategoryModal()}>
              {t('warehouseUi.productGrades.changeCategory')}
            </Button>
            <Button size="small" icon={<ShoppingCartOutlined />} onClick={() => openAddOrderModal()}>
              {t('productGrade.addToStoreOrder')}
            </Button>
            <Dropdown
              trigger={['click']}
              menu={{
                items: selectionMoreItems,
                onClick: ({ key }) => {
                  if (key === 'export') {
                    openExportModal()
                  } else if (key === 'batchPrice') {
                    setBatchPriceOpen(true)
                  }
                },
              }}
            >
              <Button size="small">
                {t('common.more')}
                <DownOutlined />
              </Button>
            </Dropdown>
          </SelectionActionBar>
        </div>

        <MeasuredTable<ProductGradeListItem> metricId="warehouse.product-grade-management.table-1"
          className="wh-grades-table"
          rowKey="productCode"
          virtual
          loading={loading}
          columns={columns}
          dataSource={data}
          rowSelection={{
            selectedRowKeys,
            onChange: (keys) => setSelectedRowKeys(keys as string[]),
            columnWidth: 48,
            fixed: true,
            preserveSelectedRowKeys: true,
          }}
          pagination={{
            current: page,
            pageSize,
            total,
            showSizeChanger: true,
            pageSizeOptions: [20, 50, 100, 200, 500, 1000],
            showQuickJumper: true,
            showTotal: (count) => t('warehouseUi.productGrades.paginationTotal', { count }),
          }}
          scroll={{ x: 1110, y: 600 }}
          onChange={handleTableChange}
        />
      </section>

      <PasteImportModal
        open={pasteImportOpen}
        onClose={() => setPasteImportOpen(false)}
        onSuccess={() => refreshListAndCountsRef.current()}
      />

      <BatchPriceModal
        open={batchPriceOpen}
        selectedCount={selectedRowKeys.length}
        productCodes={selectedRowKeys}
        onClose={() => setBatchPriceOpen(false)}
        onSuccess={() => {
          setSelectedRowKeys([])
        }}
      />

      <Modal
        title={t('productGrade.addToStoreOrderTitle')}
        open={addOrderOpen}
        onCancel={() => {
          if (!addOrderSubmitting) setAddOrderOpen(false)
        }}
        onOk={() => void handleAddToStoreOrder()}
        okText={t('productGrade.confirmAddToStoreOrder')}
        cancelText={t('common.cancel')}
        confirmLoading={addOrderSubmitting}
        maskClosable={!addOrderSubmitting}
      >
        <Space direction="vertical" style={{ width: '100%' }} size={12}>
          <span>{t('productGrade.addToStoreOrderSelected', { count: addOrderProductCodes.length })}</span>
          <Radio.Group
            value={addOrderMode}
            onChange={(event) => setAddOrderMode(event.target.value)}
            disabled={addOrderSubmitting}
          >
            <Radio.Button value="existing">{t('productGrade.useExistingOrder')}</Radio.Button>
            <Radio.Button value="new">{t('productGrade.createNewOrder')}</Radio.Button>
          </Radio.Group>

          {addOrderMode === 'existing' ? (
            <Select
              showSearch
              allowClear
              placeholder={t('productGrade.selectTargetOrder')}
              value={targetOrderGuid}
              loading={orderLoading}
              disabled={addOrderSubmitting}
              optionFilterProp="label"
              filterOption={false}
              onSearch={(value) => {
                setOrderKeyword(value)
                void loadEditableOrders({ keyword: value, pageNumber: 1 })
              }}
              style={{ width: '100%' }}
              onDropdownVisibleChange={(open) => {
                if (open && !orderOptionsLoaded) {
                  void loadEditableOrders({ keyword: orderKeyword, pageNumber: 1 })
                }
              }}
              onPopupScroll={(event) => {
                const target = event.currentTarget
                const nearBottom = target.scrollTop + target.clientHeight >= target.scrollHeight - 24
                if (nearBottom && orderHasMore && editableOrders.length < orderTotal && !orderLoading) {
                  void loadEditableOrders({ keyword: orderKeyword, pageNumber: orderPage + 1, append: true })
                }
              }}
              onChange={setTargetOrderGuid}
              options={editableOrders.map((order) => {
                const status = EDITABLE_STORE_ORDER_STATUSES.includes(order.flowStatus)
                  ? order.flowStatus
                  : StoreOrderFlowStatus.Submitted
                return {
                  value: order.orderGUID,
                  label: `${order.orderNo || order.orderGUID} - ${order.storeName || order.storeCode || '--'} - ${t(getOrderStatusI18nKey(status))}`,
                }
              })}
            />
          ) : (
            <>
              <Select
                showSearch
                allowClear
                placeholder={t('productGrade.selectTargetStore')}
                value={targetStoreCode}
                loading={storeLoading}
                disabled={addOrderSubmitting}
                optionFilterProp="label"
                style={{ width: '100%' }}
                onDropdownVisibleChange={(open) => {
                  if (open) void loadStores()
                }}
                onChange={setTargetStoreCode}
                options={storeOptions}
              />
              <Input.TextArea
                placeholder={t('productGrade.newOrderRemarksPlaceholder')}
                value={newOrderRemarks}
                disabled={addOrderSubmitting}
                autoSize={{ minRows: 2, maxRows: 4 }}
                onChange={(event) => setNewOrderRemarks(event.target.value)}
              />
            </>
          )}

          <Space wrap>
            {EDITABLE_STORE_ORDER_STATUSES.map((status) => (
              <Tag key={status} color={StoreOrderStatusColorMap[status]}>
                {t(getOrderStatusI18nKey(status))}
              </Tag>
            ))}
          </Space>
        </Space>
      </Modal>

      <Modal
        title={t('productGrade.batchCategoryTitle')}
        open={batchCategoryOpen}
        onCancel={handleBatchCategoryCancel}
        onOk={() => void handleBatchCategorySave()}
        okText={t('common.save')}
        cancelText={t('common.cancel')}
        confirmLoading={batchCategorySaving}
        maskClosable={!batchCategorySaving}
        width={640}
        destroyOnHidden
        okButtonProps={{
          disabled:
            categoryLoading
            || !targetCategoryGuid
            || !categoryTree.length,
        }}
      >
        <Space direction="vertical" size={12} style={{ width: '100%' }}>
          <Typography.Paragraph style={{ marginBottom: 0 }}>
            {t('productGrade.batchCategorySelected', { count: selectedRowKeys.length })}
          </Typography.Paragraph>
          {selectedTargetCategory ? (
            <Tag color="blue">{t('productGrade.targetCategory')}: {categoryEditTargetText}</Tag>
          ) : null}
          <Typography.Paragraph type="secondary" style={{ marginBottom: 0 }}>
            {t('productGrade.batchCategoryHint')}
          </Typography.Paragraph>
          <CategoryTreePicker
            categories={categoryTree}
            selectedKey={targetCategoryGuid}
            expandedKeys={categoryExpandedKeys}
            onExpand={setCategoryExpandedKeys}
            onSelect={setTargetCategoryGuid}
            language={i18n.language}
            t={t}
            maxHeight={420}
          />
        </Space>
      </Modal>

      <Modal
        title={t('productGrade.editCategoryTitle')}
        open={categoryEditOpen}
        onCancel={handleCategoryEditCancel}
        onOk={() => void handleCategoryEditSave()}
        okText={t('common.save')}
        cancelText={t('common.cancel')}
        confirmLoading={categorySaving}
        maskClosable={!categorySaving}
        width={640}
        destroyOnHidden
        okButtonProps={{
          disabled:
            categoryLoading
            || !targetCategoryGuid
            || targetCategoryGuid === categoryEditRecord?.categoryGuid
            || !categoryTree.length,
        }}
      >
        <Space direction="vertical" size={12} style={{ width: '100%' }}>
          <Typography.Paragraph style={{ marginBottom: 0 }}>
            <Typography.Text strong>{categoryEditRecord?.hbProductNo || categoryEditRecord?.productCode || '--'}</Typography.Text>
            {categoryEditRecord?.productName ? ` - ${categoryEditRecord.productName}` : ''}
          </Typography.Paragraph>
          <Space wrap>
            <Tag>{t('productGrade.currentCategory')}: {categoryEditCurrentText}</Tag>
            {selectedTargetCategory ? (
              <Tag color="blue">{t('productGrade.targetCategory')}: {categoryEditTargetText}</Tag>
            ) : null}
          </Space>
          <Typography.Paragraph type="secondary" style={{ marginBottom: 0 }}>
            {t('productGrade.editCategoryHint')}
          </Typography.Paragraph>
          <CategoryTreePicker
            categories={categoryTree}
            selectedKey={targetCategoryGuid}
            expandedKeys={categoryExpandedKeys}
            onExpand={setCategoryExpandedKeys}
            onSelect={setTargetCategoryGuid}
            language={i18n.language}
            t={t}
            maxHeight={420}
          />
        </Space>
      </Modal>

      <Modal
        title={t('productGrade.exportExcelTitle')}
        open={exportOpen}
        onCancel={() => {
          if (!exporting) setExportOpen(false)
        }}
        onOk={() => void handleExportExcel()}
        okText={t('productGrade.startExport')}
        cancelText={t('common.cancel')}
        confirmLoading={exporting}
        maskClosable={!exporting}
      >
        <Space direction="vertical" style={{ width: '100%' }} size={12}>
          <span>{t('productGrade.exportSelectedProducts', { count: selectedRowKeys.length })}</span>
          <Checkbox
            checked={exportIncludeImage}
            disabled={exporting}
            onChange={(event) => setExportIncludeImage(event.target.checked)}
          >
            {t('productGrade.includeProductImage')}
          </Checkbox>
          {exporting && (
            <div>
              <Progress percent={exportProgress} size="small" />
              <div style={{ color: '#666', marginTop: 4 }}>{exportMessage}</div>
            </div>
          )}
        </Space>
      </Modal>
    </PageContainer>
  )
}
