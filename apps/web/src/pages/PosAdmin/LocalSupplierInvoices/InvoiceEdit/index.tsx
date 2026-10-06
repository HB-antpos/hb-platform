import {
  ArrowLeftOutlined,
  BarChartOutlined,
  BarcodeOutlined,
  CopyOutlined,
  DownOutlined,
  FilterOutlined,
  LoadingOutlined,
  MoreOutlined,
  PictureOutlined,
  PlayCircleOutlined,
  ScanOutlined,
  SearchOutlined,
  SnippetsOutlined,
} from '@ant-design/icons'
import {
  Alert,
  Badge,
  Button,
  Checkbox,
  Col,
  DatePicker,
  Dropdown,
  Form,
  Image,
  Input,
  InputNumber,
  Modal,
  Popconfirm,
  Popover,
  Radio,
  Row,
  Select,
  Skeleton,
  Space,
  Switch,
  Tag,
  Tooltip,
  Typography,
  message,
  notification,
} from 'antd'
import type { ColumnType, ColumnsType, TableProps } from 'antd/es/table'
import { useKeepAliveContext } from 'keepalive-for-react'
import type { CSSProperties, MouseEvent as ReactMouseEvent, ReactNode } from 'react'
import { useCallback, useEffect, useLayoutEffect, useMemo, useRef, useState } from 'react'
import { useNavigate } from 'react-router-dom'
import { useTranslation } from 'react-i18next'
import { useDynamicTabTitle } from '../../../../hooks/useDynamicTabTitle'
import { useStableRouteContext } from '../../../../hooks/useStableRouteContext'
import BarcodePreview from '../../../../components/BarcodePreview'
import ActiveFilterBar, { type ActiveFilterItem } from '../../../../components/listToolbar/ActiveFilterBar'
import SelectionActionBar from '../../../../components/listToolbar/SelectionActionBar'
import { registerPageMessages } from '../../../../i18n/registerPageMessages'
import { getProductById, updateProduct } from '../../../../services/posProductService'
import {
  batchExecuteActions,
  batchUpdateDetailAction,
  batchUpdateDetails,
  batchUpsertDetails,
  checkProducts,
  deleteDetails,
  getCheckProductsJob,
  getPasteDetailsJob,
  getProductsByBarcode,
  getInvoice,
  getInvoiceDetails,
  getUpdateHqProductsJob,
  getUpdateToStorePricesJob,
  hasUpdateHqProductsResultStatistics,
  pasteDetails,
  startCheckProductsJob,
  startPasteDetailsJob,
  startUpdateHqProductsJob,
  startUpdateToStorePricesJob,
  updateDetailAction,
  updateInvoice,
  updateLastPurchasePrices,
} from '../../../../services/localSupplierInvoiceService'
import {
  createHqSyncJobPoller,
  HqProductSyncPollingTimeoutError,
} from '../../../../services/productHqSyncPolling'
import { getActiveStores } from '../../../../services/storeService'
import { getActiveLocalSuppliers } from '../../../../services/localSupplierService'
import { useAuthStore } from '../../../../store/auth'
import type {
  BatchEditFields,
  BatchExecuteActionsResult,
  BatchResultDto,
  CheckProductsJobResult,
  CheckProductsResponse,
  BarcodeAbnormalMatchedProductDto,
  DetailAction,
  EnsureHqProductError,
  InvoiceDetailUpsertItemDto,
  LocalSupplierInvoiceDetailDto,
  LocalSupplierInvoiceItemDto,
  PasteDetailsJobResult,
  UpdateHqProductsResult,
  UpdateHqProductsJobResult,
  UpdateLastPurchasePricesResult,
  UpdateToStorePricesFields,
  UpdateToStorePricesRequest,
  UpdateToStorePricesResult,
  UpdateToStorePricesJobResult,
} from '../../../../types/localSupplierInvoice'
import { copyTextToClipboard } from '../../../../utils/clipboard'
import { shouldShowDetailInitialLoading, shouldSkipDetailAutoReload } from '../../../../utils/detailLoadState'
import { discountRateToDecimal, formatDiscountRate } from '../../../../utils/discountRate'
import { RequestError } from '../../../../utils/request'
import { DetailAction as DetailActionEnum } from '../../../../types/localSupplierInvoice'
import {
  buildStoreOptionsFromUserStores,
  filterStoreOptionsByManagedCodes,
  isStoreCodeInManagedScope,
} from '../../../../utils/managedStoreScope'
import {
  filterInvoiceDetails,
  actionTypeFilters,
  getBarcodeStatusFilter,
  getDetailStatusStats,
  getProductStatusFilter,
  toggleStatusFilter,
} from './statusFilters'
import {
  compareNullableNumbers,
  compareNullableText,
  filterBooleanColumn,
  matchesTextColumnFilter,
  matchesNumberColumnFilter,
  parseNumberColumnFilter,
  parseTextColumnFilter,
  serializeNumberColumnFilter,
  serializeTextColumnFilter,
  type NumberFilterField,
  type NumberFilterMode,
  type TextColumnFilterModel,
  type TextFilterField,
  type TextFilterMode,
} from './tableColumnFilters'
import {
  analyzePasteMultilineCells,
  defaultPasteFieldOrder,
  getPasteTextMaxColumnCount,
  parsePasteText,
  type PasteFieldKey,
  type PasteMultilineCellMode,
} from './pasteDetails'
import {
  buildBatchExecuteConfirmText,
  buildBatchExecuteSnapshot,
  constrainSelectedRowKeysToVisibleDetails,
  getBatchExecuteErrorFeedback,
  getNewProductWithAdditionalBarcodesRows,
  splitCreateProductDetailGuids,
} from './batchExecuteConfirm'
import {
  EditableNumberCell,
  EditableTextCell,
} from './EditableCells'
import { COMPACT_NUMBER_INPUT_WIDTH } from './editableCellLayout'
import {
  canApplyCheckProductsJobResult,
  canApplyInvoiceJobResult,
} from './backgroundJobGuards'
import {
  applyInvoiceDetailInlineEdit,
  applyInvoiceDetailBatchEdit,
  buildInvoiceDetailInlineNavigationDetails,
  buildInvoiceDetailSaveItems,
  normalizeInvoiceDetailInlineValue,
  resolveInvoiceDetailInlineNavigation,
  type InvoiceDetailInlineEditableField,
  type InvoiceDetailInlineNavigationKey,
  type InvoiceDetailInlineNavigationTarget,
  type InvoiceDetailInlineSortState,
} from './inlineEdit'
import {
  buildMatchedProductMasterUpdatePayload,
  getMatchedProductMasterUpdateTarget,
} from './matchedProductMasterUpdate'
import {
  canLinkMatchedProduct,
  isProductLinkConfirmed,
  mergeProductCheckResult,
} from './matchedProductLink'
import {
  buildInvoiceHeaderFormValues,
  buildInvoiceHeaderSavePayload,
  includeCurrentInvoiceHeaderOption,
  isInvoiceHeaderDirty,
  type InvoiceHeaderSelectOption,
} from './invoiceHeaderForm'
import ProductSetCodeMaintenanceModal from './ProductSetCodeMaintenanceModal'
import PricingEditor from './PricingEditor'
import type { PricingEditorChange } from './pricingEditorChanges'
import {
  DETAIL_PROGRESS_BUCKETS,
  EXECUTED_DETAIL_ACTION,
  filterDetailsByProgressBucket,
  getDetailProgressBucket,
  getDetailProgressStats,
  getExecutedPercent,
  getPendingExecutionDetailGuids,
  type DetailProgressBucket,
  type DetailProgressBucketFilter,
} from './progressBuckets'
import {
  buildInvoiceDetailSnapshotIndex,
  countEditedInvoiceDetailRows,
  getEditedInvoiceDetailFields,
  isInvoiceDetailFieldEdited,
} from './detailDirtyState'
import type {
  ActionTypeFilterValue,
  BarcodeStatusFilter,
  BarcodeStatusFilterValue,
  PriceFilter,
  ProductStatusFilter,
  StatusFilterValue,
} from './statusFilters'
import { MeasuredTable } from '../../../../components/MeasuredTable'
import { formatLocalSupplierInvoiceAuditTime, formatLocalSupplierInvoiceAuditTimeCompact } from '../auditTime'
import { publishLocalSupplierInvoiceChanged } from '../invoiceListSync'
import invoiceMessagesEn from '../invoiceMessages.en.json'
import invoiceMessagesZh from '../invoiceMessages.zh.json'
import '../localSupplierInvoices.css'

// 重设计新增的文案随页面代码块懒注册，不进首屏 i18n 包。
registerPageMessages({ zh: invoiceMessagesZh, en: invoiceMessagesEn })

/* ------------------------------------------------------------------ */
/*  辅助函数                                                           */
/* ------------------------------------------------------------------ */

function formatAmount(value?: number) {
  if (value === undefined || value === null) return '--'
  return value.toFixed(2)
}

function formatPricingFloatRate(value?: number) {
  if (value === undefined || value === null) return '--'
  return value.toFixed(2)
}

// 编辑页 Tab 使用“分店 + 供应商前 4 位 + 单号”快速识别订单，名称缺失时才回退编码。
function buildInvoiceTabTitle(invoice: LocalSupplierInvoiceDetailDto | null, fallbackTitle: string) {
  if (!invoice) return fallbackTitle

  const storeSegment = invoice.storeName?.trim() || invoice.storeCode?.trim()
  const supplierNameSegment = invoice.supplierName?.trim().slice(0, 4).toUpperCase()
  const supplierCodeSegment = invoice.supplierCode?.trim().slice(0, 4).toUpperCase()
  const supplierSegment = supplierNameSegment || supplierCodeSegment
  const invoiceNoSegment = invoice.invoiceNo?.trim()

  return [storeSegment, supplierSegment, invoiceNoSegment].filter(Boolean).join(' ') || fallbackTitle
}

function normalizeInvoiceSnapshot(data: LocalSupplierInvoiceDetailDto | null) {
  if (!data) return null
  return {
    invoiceGUID: data.invoiceGUID,
    appGUID: data.appGUID,
    pcGUID: data.pcGUID,
    storeCode: data.storeCode,
    storeName: data.storeName,
    supplierCode: data.supplierCode,
    supplierName: data.supplierName,
    invoiceNo: data.invoiceNo,
    orderDate: data.orderDate,
    inboundDate: data.inboundDate,
    totalAmount: data.totalAmount,
    remarks: data.remarks,
  }
}

function areLocalSupplierInvoicesEqual(
  current: LocalSupplierInvoiceDetailDto | null,
  next: LocalSupplierInvoiceDetailDto | null,
) {
  return JSON.stringify(normalizeInvoiceSnapshot(current)) === JSON.stringify(normalizeInvoiceSnapshot(next))
}

function normalizeInvoiceDetailSnapshot(item: LocalSupplierInvoiceItemDto) {
  return {
    detailGUID: item.detailGUID,
    invoiceGUID: item.invoiceGUID,
    storeCode: item.storeCode,
    supplierCode: item.supplierCode,
    productTagGUID: item.productTagGUID,
    productCategoryGUID: item.productCategoryGUID,
    storeProductCode: item.storeProductCode,
    productCode: item.productCode,
    itemNumber: item.itemNumber,
    barcode: item.barcode,
    additionalBarcodes: item.additionalBarcodes,
    productName: item.productName,
    specification: item.specification,
    unit: item.unit,
    quantity: item.quantity,
    lastPurchasePrice: item.lastPurchasePrice,
    purchasePrice: item.purchasePrice,
    retailPrice: item.retailPrice,
    amount: item.amount,
    existingProductCount: item.existingProductCount,
    isCreatedByThisInvoice: item.isCreatedByThisInvoice,
    barcodeStatus: item.barcodeStatus,
    barcodeMatchCount: item.barcodeMatchCount,
    productImage: item.productImage,
    activityType: item.activityType,
    discountRate: item.discountRate,
    autoPricing: item.autoPricing,
    pricingFloatRate: item.pricingFloatRate,
    newAutoRetailPrice: item.newAutoRetailPrice,
    isSpecialProduct: item.isSpecialProduct,
    oldStoreProductCode: item.oldStoreProductCode,
  }
}

function areLocalSupplierInvoiceDetailsEqual(
  current: LocalSupplierInvoiceItemDto[],
  next: LocalSupplierInvoiceItemDto[],
) {
  if (current.length !== next.length) return false
  return current.every((item, index) => (
    JSON.stringify(normalizeInvoiceDetailSnapshot(item)) === JSON.stringify(normalizeInvoiceDetailSnapshot(next[index]))
  ))
}

function buildInvoiceRowActions(data: LocalSupplierInvoiceItemDto[]) {
  return Object.fromEntries(
    data
      .filter((item) => item.activityType !== undefined && item.activityType !== null)
      .map((item) => [item.detailGUID, item.activityType as number]),
  )
}

const pasteFieldOrderStorageKey = 'hbweb_rv.localSupplierInvoice.pasteFieldOrder.v1'
const validPasteFieldKeys = new Set<PasteFieldKey>([
  'itemNumber',
  'barcode',
  'productName',
  'quantity',
  'purchasePrice',
  'newAutoRetailPrice',
  'retailPrice',
  'skip',
])

function normalizePasteFieldOrder(value: unknown): PasteFieldKey[] {
  if (!Array.isArray(value) || !value.length) {
    return [...defaultPasteFieldOrder]
  }

  const fields = value.filter((item): item is PasteFieldKey => typeof item === 'string' && validPasteFieldKeys.has(item as PasteFieldKey))
  return fields.length === value.length && !hasDuplicatePasteFields(fields) ? fields : [...defaultPasteFieldOrder]
}

function hasDuplicatePasteFields(fieldOrder: PasteFieldKey[]) {
  const fields = fieldOrder.filter((field) => field !== 'skip')
  return new Set(fields).size !== fields.length
}

function loadSavedPasteFieldOrder() {
  if (typeof window === 'undefined') return [...defaultPasteFieldOrder]

  try {
    const saved = window.localStorage.getItem(pasteFieldOrderStorageKey)
    return saved ? normalizePasteFieldOrder(JSON.parse(saved)) : [...defaultPasteFieldOrder]
  } catch {
    return [...defaultPasteFieldOrder]
  }
}

const matchedProductTableScrollX = 940

const matchedProductNameCellStyle: CSSProperties = {
  minWidth: 240,
  maxWidth: 280,
  whiteSpace: 'normal',
  wordBreak: 'break-word',
  lineHeight: '20px',
}

const matchedProductTagStyle: CSSProperties = {
  marginInlineEnd: 0,
  whiteSpace: 'nowrap',
}

const matchedProductActionButtonStyle: CSSProperties = {
  paddingInline: 0,
}

function renderNumericCell(value: ReactNode) {
  return <span className="lsi-num">{value}</span>
}

function formatQuantity(value?: number | null) {
  if (value === undefined || value === null) return '--'
  return value.toLocaleString('en-AU', { maximumFractionDigits: 2 })
}

function normalizeEnsureHqErrors(value: unknown): EnsureHqProductError[] {
  if (!Array.isArray(value)) return []

  return value.map((item) => {
    if (typeof item === 'string') {
      return { detailGuid: '', message: item }
    }
    if (item && typeof item === 'object') {
      const raw = item as Partial<EnsureHqProductError>
      return {
        detailGuid: String(raw.detailGuid ?? ''),
        storeCode: raw.storeCode ? String(raw.storeCode) : undefined,
        message: String(raw.message ?? ''),
      }
    }
    return { detailGuid: '', message: String(item) }
  })
}

function getUpdateHqProductsFailure(error: unknown): UpdateHqProductsResult | undefined {
  if (!(error instanceof RequestError)) return undefined

  const payload = error.payload as { data?: unknown; details?: unknown } | undefined
  const candidate = (payload?.details ?? payload?.data) as Partial<UpdateHqProductsResult> | undefined
  if (!hasUpdateHqProductsResultStatistics(candidate)) return undefined

  return {
    total: Number(candidate.total ?? 0),
    updated: Number(candidate.updated ?? 0),
    failed: Number(candidate.failed ?? 0),
    skipped: Number(candidate.skipped ?? 0),
    hqExisting: Number(candidate.hqExisting ?? 0),
    hbwebCreated: Number(candidate.hbwebCreated ?? 0),
    hqCreated: Number(candidate.hqCreated ?? 0),
    hqSynced: Number(candidate.hqSynced ?? 0),
    hqPurchasePricesUpdated: Number(candidate.hqPurchasePricesUpdated ?? 0),
    hqRetailPricesUpdated: Number(candidate.hqRetailPricesUpdated ?? 0),
    hqAutoPricingUpdated: Number(candidate.hqAutoPricingUpdated ?? 0),
    hqSpecialProductsUpdated: Number(candidate.hqSpecialProductsUpdated ?? 0),
    hqDiscountRatesUpdated: Number(candidate.hqDiscountRatesUpdated ?? 0),
    errors: normalizeEnsureHqErrors(candidate.errors),
  }
}

function isMissingBackgroundJobEndpoint(error: unknown) {
  // 关键位置：后端后台 job API 分批发布时，404 代表当前环境尚未支持新端点，可回退旧同步接口避免前台操作直接失败。
  return error instanceof RequestError && error.status === 404
}

function buildUpdatePriceFields(values: Record<string, unknown>): UpdateToStorePricesFields {
  return {
    updatePurchasePrice: values.updatePurchasePrice === true,
    updateRetailPrice: values.updateRetailPrice === true,
    updateIsAutoPricing: values.updateIsAutoPricing === true,
    updateIsSpecialProduct: values.updateIsSpecialProduct === true,
    updateDiscountRate: values.updateDiscountRate === true,
  }
}

// 「新建商品」同步 HQ 时写全部价格字段：与本地新建为全部启用分店建价一致，取值来自明细行。
const ALL_UPDATE_PRICE_FIELDS: UpdateToStorePricesFields = {
  updatePurchasePrice: true,
  updateRetailPrice: true,
  updateIsAutoPricing: true,
  updateIsSpecialProduct: true,
  updateDiscountRate: true,
}

function createHqIdempotencyKey(fallback: string) {
  return typeof crypto !== 'undefined' && 'randomUUID' in crypto ? crypto.randomUUID() : `${fallback}-${Date.now()}`
}

type BatchExecuteMode = 'createProducts' | 'otherActions'

function hasAnyUpdatePriceField(updateFields: UpdateToStorePricesFields) {
  return (
    updateFields.updatePurchasePrice ||
    updateFields.updateRetailPrice ||
    updateFields.updateIsAutoPricing ||
    updateFields.updateIsSpecialProduct ||
    updateFields.updateDiscountRate
  )
}

type PurchasePriceChange =
  | { kind: 'noPrice' }
  | { kind: 'noHistory' }
  | { kind: 'same'; last: number }
  | { kind: 'up' | 'down'; last: number; percent: number; className: string }

/** 本次进货价较上次的变化；涨幅分档沿用原来的底色阈值（>20% / >5% / >0），降价单独用蓝色。 */
function getPurchasePriceChange(lastPrice?: number, currentPrice?: number): PurchasePriceChange {
  if (currentPrice === undefined || currentPrice === null) return { kind: 'noPrice' }
  if (lastPrice === undefined || lastPrice === null || lastPrice === 0) return { kind: 'noHistory' }
  if (Math.abs(currentPrice - lastPrice) < 0.00005) return { kind: 'same', last: lastPrice }
  const changeRate = (currentPrice - lastPrice) / lastPrice
  const percent = Math.abs(changeRate * 100)
  if (changeRate < 0) {
    return { kind: 'down', last: lastPrice, percent, className: 'lsi-wb-delta lsi-wb-delta-down' }
  }
  const className = changeRate > 0.2
    ? 'lsi-wb-delta lsi-wb-delta-up-strong'
    : changeRate > 0.05
      ? 'lsi-wb-delta lsi-wb-delta-up'
      : 'lsi-wb-delta lsi-wb-delta-up-mild'
  return { kind: 'up', last: lastPrice, percent, className }
}

/** 处理进度分段的颜色：已执行绿、待执行蓝、等待操作橙、未检测紫、无需操作灰（明暗也有差异，不只靠色相区分）。 */
const PROGRESS_BUCKET_COLORS: Record<DetailProgressBucket, string> = {
  executed: '#389e0d',
  pending: '#1677ff',
  waiting: '#fa8c16',
  unchecked: '#8c6bd8',
  none: '#c9d0db',
}

const FLOW_STATUS_LABELS: Record<number, { labelKey: string; className: string }> = {
  0: { labelKey: 'posAdmin.invoices.draft', className: 'lsi-tag lsi-tag-neutral' },
  1: { labelKey: 'posAdmin.invoices.submitted', className: 'lsi-tag lsi-tag-blue' },
  2: { labelKey: 'posAdmin.invoices.approved', className: 'lsi-tag lsi-tag-green' },
  3: { labelKey: 'posAdmin.invoices.pushed', className: 'lsi-tag lsi-tag-purple' },
}

const INBOUND_STATUS_LABEL_KEYS: Record<number, string> = {
  0: 'posAdmin.invoices.notInbound',
  1: 'posAdmin.invoices.partialInbound',
  2: 'posAdmin.invoices.inbounded',
}

/** 操作类型配置 */
const DETAIL_ACTION_CONFIG = (t: ReturnType<typeof useTranslation>['t']): Record<number, { label: string; color: string; className: string }> => ({
  [DetailActionEnum.None]: { label: t('posAdmin.invoiceDetail.none', '无'), color: 'default', className: 'lsi-tag-neutral' },
  [DetailActionEnum.CreateProduct]: { label: t('posAdmin.invoiceDetail.createProduct', '新建商品'), color: 'blue', className: 'lsi-tag-blue' },
  [DetailActionEnum.UpdatePurchasePrice]: { label: t('posAdmin.invoiceDetail.updatePurchasePriceShort', '更新进货价'), color: 'green', className: 'lsi-tag-green' },
  [DetailActionEnum.WaitForOperation]: { label: t('posAdmin.invoiceDetail.waitForOperation', '等待操作'), color: 'orange', className: 'lsi-tag-orange' },
  [DetailActionEnum.UpdateItemNumber]: { label: t('posAdmin.invoiceDetail.updateItemNumber', '更新货号'), color: 'purple', className: 'lsi-tag-purple' },
  [DetailActionEnum.AddMultiCode]: { label: t('posAdmin.invoiceDetail.addMultiCode', '添加多码'), color: 'cyan', className: 'lsi-tag-cyan' },
  [EXECUTED_DETAIL_ACTION]: { label: t('posAdmin.invoiceDetail.executed', '已执行'), color: 'default', className: 'lsi-tag-neutral' },
})

/** 操作类型下拉菜单项 */
const ACTION_MENU_ITEMS = (t: ReturnType<typeof useTranslation>['t']) => [
  { key: '0', label: <Tag color="default">{t('posAdmin.invoiceDetail.none', '无')}</Tag> },
  { key: '1', label: <Tag color="blue">{t('posAdmin.invoiceDetail.createProduct', '新建商品')}</Tag> },
  { key: '2', label: <Tag color="green">{t('posAdmin.invoiceDetail.updatePurchasePriceShort', '更新进货价')}</Tag> },
  { key: '3', label: <Tag color="orange">{t('posAdmin.invoiceDetail.waitForOperation', '等待操作')}</Tag> },
  { key: '4', label: <Tag color="purple">{t('posAdmin.invoiceDetail.updateItemNumber', '更新货号')}</Tag> },
  { key: '5', label: <Tag color="cyan">{t('posAdmin.invoiceDetail.addMultiCode', '添加多码')}</Tag> },
]

export default function InvoiceEditPage() {
  const { t } = useTranslation()
  const route = useStableRouteContext()
  const { active } = useKeepAliveContext()
  const invoiceGuid = route?.params.id
  const navigate = useNavigate()
  const { access, currentUser } = useAuthStore()
  const isAdmin = access.isAdmin
  const canManagePosProducts = access.canManagePosProducts
  const canWriteLocalPurchaseToHq = access.canEditLocalPurchase && access.canPushLocalPurchaseToHq
  const canRunGlobalLocalPurchaseBatchActions = access.canEditLocalPurchase && (access.isAdmin || access.isWarehouseManager)
  // 「查看」页已并入本页：有编辑权限才能改表头（后端 PUT 要求 LocalPurchase.Edit）；
  // 明细行编辑、粘贴、检测、删除等沿用原页面的管理员口径；其余用户（如店长）看到同一页面的只读视图。
  const canEditInvoice = access.canEditLocalPurchase
  const canEditDetailRows = isAdmin
  const canSelectRows = isAdmin || canWriteLocalPurchaseToHq || canRunGlobalLocalPurchaseBatchActions
  const managedStoreCodes = access.managedStoreCodes()
  const managedStoreCodeKey = managedStoreCodes?.join(',') ?? 'all'
  // 记录当前发票已完成首次加载，保活 Tab 恢复时保留订单头和明细表。
  const loadedInvoiceGuidRef = useRef<string | null>(null)
  const visibleInvoiceGuidRef = useRef<string | null>(null)
  const currentInvoiceGuidRef = useRef<string | undefined>(invoiceGuid)
  currentInvoiceGuidRef.current = invoiceGuid
  const lastLoadedManagedStoreCodeKeyRef = useRef<string | null>(null)
  const invoiceSnapshotRef = useRef<LocalSupplierInvoiceDetailDto | null>(null)
  const detailsSnapshotRef = useRef<LocalSupplierInvoiceItemDto[]>([])

  /* ---- 主表数据 ---- */
  const [invoice, setInvoice] = useState<LocalSupplierInvoiceDetailDto | null>(null)
  const invoiceTabTitle = useMemo(
    () => buildInvoiceTabTitle(invoice, t('menu.invoiceDetail', '进货单详情')),
    [invoice, t],
  )
  // 这里只更新当前编辑页的 KeepAlive Tab 标题，不改变路由标题或面包屑。
  useDynamicTabTitle(invoiceTabTitle)
  const [canAccessInvoice, setCanAccessInvoice] = useState(true)
  const [details, setDetails] = useState<LocalSupplierInvoiceItemDto[]>([])
  const [loading, setLoading] = useState(false)
  const [detailLoading, setDetailLoading] = useState(false)
  const [saving, setSaving] = useState(false)
  const [savingAll, setSavingAll] = useState(false)

  /* ---- 行选择 ---- */
  const [selectedRowKeys, setSelectedRowKeys] = useState<React.Key[]>([])
  const [setCodeMaintenanceTarget, setSetCodeMaintenanceTarget] = useState<LocalSupplierInvoiceItemDto | null>(null)

  /* ---- 行内数字编辑焦点 ---- */
  const [activeInlineNumberEdit, setActiveInlineNumberEdit] = useState<InvoiceDetailInlineNavigationTarget | null>(null)
  const [inlineNavigationSort, setInlineNavigationSort] = useState<InvoiceDetailInlineSortState | null>(null)

  /* ---- 行内操作类型 (本地临时存储) ---- */
  const [rowActions, setRowActions] = useState<Record<string, number>>({})

  /* ---- 搜索 ---- */
  const [searchText, setSearchText] = useState('')

  /* ---- 涨跌过滤 ---- */
  const [priceFilter, setPriceFilter] = useState<PriceFilter>('all')
  const [productTypeFilter, setProductTypeFilter] = useState<'all' | 'unknown' | 0 | 1 | 2>('all')
  const [productStatusFilter, setProductStatusFilter] = useState<StatusFilterValue<ProductStatusFilter>>('all')
  const [barcodeStatusFilter, setBarcodeStatusFilter] = useState<BarcodeStatusFilterValue>('all')
  const [actionTypeFilter, setActionTypeFilter] = useState<ActionTypeFilterValue>('all')
  // 处理进度条既是统计也是筛选入口；特殊商品筛选从原列头迁到「更多筛选」。
  const [progressBucketFilter, setProgressBucketFilter] = useState<DetailProgressBucketFilter>('all')
  const [specialProductFilter, setSpecialProductFilter] = useState<'all' | 'yes' | 'no'>('all')
  // 定价弹窗同一时间只开一行。
  const [pricingEditorDetailGuid, setPricingEditorDetailGuid] = useState<string | null>(null)
  // 列头过滤只作用于当前前端明细，不请求后端；保持受控后才能被“清空过滤”统一重置。
  const [columnFilteredValues, setColumnFilteredValues] = useState<Record<string, (React.Key | boolean)[] | null>>({})

  /* ---- 表单 ---- */
  const [form] = Form.useForm()
  const watchedStoreCode = Form.useWatch('storeCode', form)
  const watchedSupplierCode = Form.useWatch('supplierCode', form)
  const watchedOrderDate = Form.useWatch('orderDate', form)
  const watchedInboundDate = Form.useWatch('inboundDate', form)
  const watchedRemarks = Form.useWatch('remarks', form)

  /* ---- 分店选项 ---- */
  const [storeOptions, setStoreOptions] = useState<InvoiceHeaderSelectOption[]>([])
  const [storeOptionsLoading, setStoreOptionsLoading] = useState(false)
  const [supplierOptions, setSupplierOptions] = useState<InvoiceHeaderSelectOption[]>([])
  const [supplierOptionsLoading, setSupplierOptionsLoading] = useState(false)
  const allStoreCodes = useMemo(() => storeOptions.map((item) => item.value), [storeOptions])
  const headerStoreOptions = useMemo(
    () => includeCurrentInvoiceHeaderOption(
      storeOptions,
      invoice?.storeCode,
      invoice?.storeName,
      !storeOptions.some((option) => option.value === invoice?.storeCode)
        || !isStoreCodeInManagedScope(invoice?.storeCode, managedStoreCodes),
    ),
    [invoice?.storeCode, invoice?.storeName, managedStoreCodes, storeOptions],
  )
  const headerSupplierOptions = useMemo(
    () => includeCurrentInvoiceHeaderOption(
      supplierOptions,
      invoice?.supplierCode,
      invoice?.supplierName,
      true,
    ),
    [invoice?.supplierCode, invoice?.supplierName, supplierOptions],
  )

  /* ---- 粘贴数据 Modal ---- */
  const [pasteVisible, setPasteVisible] = useState(false)
  const [pasteMode, setPasteMode] = useState<'append' | 'replace'>('append')
  const [pasteText, setPasteText] = useState('')
  const [pasteLoading, setPasteLoading] = useState(false)
  const activePasteJobIdRef = useRef<string | null>(null)
  const [pasteFieldOrder, setPasteFieldOrder] = useState<PasteFieldKey[]>(loadSavedPasteFieldOrder)
  const [normalizeRetailPriceOnPaste, setNormalizeRetailPriceOnPaste] = useState(true)
  const [pasteMultilineCellMode, setPasteMultilineCellMode] = useState<PasteMultilineCellMode>('merge')

  /* ---- 批量编辑 Modal ---- */
  const [batchEditVisible, setBatchEditVisible] = useState(false)
  const [batchEditForm] = Form.useForm()
  const [batchEditLoading, setBatchEditLoading] = useState(false)

  /* ---- 更新到分店价格 Modal ---- */
  const [storePriceVisible, setStorePriceVisible] = useState(false)
  const [storePriceForm] = Form.useForm()
  const selectedStorePriceTargetCodes = (Form.useWatch('targetStoreCodes', storePriceForm) ?? []) as string[]
  const selectedStorePriceTargetCodeSet = useMemo(
    () => new Set<string>(selectedStorePriceTargetCodes),
    [selectedStorePriceTargetCodes],
  )
  const allStorePriceStoresSelected = allStoreCodes.length > 0 && allStoreCodes.every((storeCode) => selectedStorePriceTargetCodeSet.has(storeCode))
  const hasPartialStorePriceStoreSelection = selectedStorePriceTargetCodes.length > 0 && !allStorePriceStoresSelected
  const [storePriceLoading, setStorePriceLoading] = useState(false)

  /* ---- 更新 HQ 商品 Modal ---- */
  const [hqUpdateVisible, setHqUpdateVisible] = useState(false)
  const [hqUpdateForm] = Form.useForm()
  const selectedHqUpdateTargetCodes = (Form.useWatch('targetStoreCodes', hqUpdateForm) ?? []) as string[]
  const selectedHqUpdateTargetCodeSet = useMemo(
    () => new Set<string>(selectedHqUpdateTargetCodes),
    [selectedHqUpdateTargetCodes],
  )
  const allHqUpdateStoresSelected = allStoreCodes.length > 0 && allStoreCodes.every((storeCode) => selectedHqUpdateTargetCodeSet.has(storeCode))
  const hasPartialHqUpdateStoreSelection = selectedHqUpdateTargetCodes.length > 0 && !allHqUpdateStoresSelected
  const [hqUpdateLoading, setHqUpdateLoading] = useState(false)
  const hqUpdateIdempotencyKeyRef = useRef<string | null>(null)

  /* ---- 商品检测 ---- */
  const [checking, setChecking] = useState(false)
  const activeCheckProductsJobIdRef = useRef<string | null>(null)
  const [updatingLastPurchasePrices, setUpdatingLastPurchasePrices] = useState(false)

  /* ---- 批量执行操作 ---- */
  const [executing, setExecuting] = useState(false)

  /* ---- 未保存修改 ---- */
  const headerDirty = canEditInvoice && isInvoiceHeaderDirty({
    storeCode: watchedStoreCode,
    supplierCode: watchedSupplierCode,
    orderDate: watchedOrderDate,
    inboundDate: watchedInboundDate,
    remarks: watchedRemarks,
  }, invoice)
  // detailsSnapshotRef 只在 loadDetails / 批量编辑里与 details 一起更新，所以随 details 重建索引即可。
  // eslint-disable-next-line react-hooks/exhaustive-deps
  const detailSnapshotIndex = useMemo(() => buildInvoiceDetailSnapshotIndex(detailsSnapshotRef.current), [details])
  const editedDetailRowCount = useMemo(
    () => (canEditDetailRows ? countEditedInvoiceDetailRows(details, detailSnapshotIndex) : 0),
    [canEditDetailRows, details, detailSnapshotIndex],
  )
  const hasUnsavedChanges = headerDirty || editedDetailRowCount > 0
  const unsavedSummary = headerDirty && editedDetailRowCount > 0
    ? t('posAdmin.invoiceWorkbench.unsavedBoth', { count: editedDetailRowCount })
    : headerDirty
      ? t('posAdmin.invoiceWorkbench.unsavedHeader')
      : t('posAdmin.invoiceWorkbench.unsavedRows', { count: editedDetailRowCount })

  /* ---- 动态表格高度 ---- */
  const tableCardRef = useRef<HTMLDivElement>(null)
  const toolbarRef = useRef<HTMLDivElement>(null)
  const [tableScrollY, setTableScrollY] = useState<number>(400)

  /* ================================================================ */
  /*  数据加载                                                         */
  /* ================================================================ */

  useEffect(() => {
    activePasteJobIdRef.current = null
    activeCheckProductsJobIdRef.current = null
    setChecking(false)
  }, [invoiceGuid])

  const loadInvoice = useCallback(async (showLoading = true) => {
    if (!invoiceGuid) return false
    if (showLoading) {
      setLoading(true)
    }
    try {
      const data = await getInvoice(invoiceGuid)
      if (!isStoreCodeInManagedScope(data.storeCode, managedStoreCodes)) {
        loadedInvoiceGuidRef.current = null
        visibleInvoiceGuidRef.current = null
        lastLoadedManagedStoreCodeKeyRef.current = null
        invoiceSnapshotRef.current = null
        detailsSnapshotRef.current = []
        setCanAccessInvoice(false)
        setInvoice(null)
        setDetails([])
        setSelectedRowKeys([])
        setRowActions({})
        form.resetFields()
        message.error(t('message.noPermission', '无权查看该数据'))
        return false
      }
      loadedInvoiceGuidRef.current = invoiceGuid
      visibleInvoiceGuidRef.current = invoiceGuid
      lastLoadedManagedStoreCodeKeyRef.current = managedStoreCodeKey
      setCanAccessInvoice(true)
      if (!areLocalSupplierInvoicesEqual(invoiceSnapshotRef.current, data)) {
        invoiceSnapshotRef.current = data
        setInvoice(data)
        form.setFieldsValue(buildInvoiceHeaderFormValues(data))
      }
      return true
    } catch {
      if (showLoading) {
        visibleInvoiceGuidRef.current = null
      }
      message.error(t('posAdmin.invoiceDetail.loadInvoiceFailed', '加载进货单失败'))
      return false
    } finally {
      if (showLoading) {
        setLoading(false)
      }
    }
  }, [invoiceGuid, form, managedStoreCodeKey, t])

  const loadDetails = useCallback(async (showLoading = true) => {
    if (!invoiceGuid) return false
    if (showLoading) {
      setDetailLoading(true)
    }
    try {
      const data = await getInvoiceDetails(invoiceGuid)
      if (!areLocalSupplierInvoiceDetailsEqual(detailsSnapshotRef.current, data)) {
        detailsSnapshotRef.current = data
        setDetails(data)
        setRowActions(buildInvoiceRowActions(data))
      }
      return true
    } catch {
      message.error(t('posAdmin.invoiceDetail.loadDetailsFailed', '加载明细失败'))
      return false
    } finally {
      if (showLoading) {
        setDetailLoading(false)
      }
    }
  }, [invoiceGuid, t])

  const loadInvoiceAndDetails = useCallback(async (showLoading = true) => {
    if (!(await loadInvoice(showLoading))) return false
    return await loadDetails(showLoading)
  }, [loadInvoice, loadDetails])

  useEffect(() => {
    if (!active) return

    if (!shouldSkipDetailAutoReload({
      requestedDetailId: invoiceGuid || '',
      loadedDetailId: loadedInvoiceGuidRef.current,
      visibleDetailId: visibleInvoiceGuidRef.current,
      requestedDetailQueryKey: managedStoreCodeKey,
      loadedDetailQueryKey: lastLoadedManagedStoreCodeKeyRef.current,
    })) {
      // 未命中保活缓存或权限范围变化时才自动加载；同一编辑进货单 Tab 切回直接复用表格状态。
      // 隐藏的 KeepAlive 节点也会收到全局路由变化，必须只让当前激活节点发起请求。
      const shouldShowInitialLoading = shouldShowDetailInitialLoading({
        requestedDetailId: invoiceGuid || '',
        loadedDetailId: loadedInvoiceGuidRef.current,
        visibleDetailId: visibleInvoiceGuidRef.current,
      })
      void loadInvoiceAndDetails(shouldShowInitialLoading)
    }
  }, [active, currentUser?.stores, invoiceGuid, loadInvoiceAndDetails, managedStoreCodeKey])

  useEffect(() => {
    if (!active) return

    let cancelled = false
    const formatStoreOptions = (options: InvoiceHeaderSelectOption[]) => options.map((option) => ({
      ...option,
      label: option.label === option.value ? option.value : `${option.value} - ${option.label}`,
    }))

    if (managedStoreCodes === null) {
      setStoreOptionsLoading(true)
      getActiveStores()
        .then((stores) => {
          if (!cancelled) {
            setStoreOptions(formatStoreOptions(filterStoreOptionsByManagedCodes(stores, managedStoreCodes)))
          }
        })
        .catch(() => {
          if (!cancelled) {
            setStoreOptions([])
            message.warning(t('posAdmin.invoiceDetail.loadStoreOptionsFailed', '分店选项加载失败，当前订单仍可继续编辑'))
          }
        })
        .finally(() => {
          if (!cancelled) setStoreOptionsLoading(false)
        })
    } else {
      const activeManagedStores = currentUser?.stores?.filter((store) => store.isActive !== false)
      setStoreOptions(formatStoreOptions(buildStoreOptionsFromUserStores(activeManagedStores, { manageableOnly: true })))
      setStoreOptionsLoading(false)
    }

    setSupplierOptionsLoading(true)
    getActiveLocalSuppliers()
      .then((suppliers) => {
        if (!cancelled) {
          setSupplierOptions(suppliers.map((supplier) => ({
            value: supplier.localSupplierCode,
            label: supplier.name
              ? `${supplier.localSupplierCode} - ${supplier.name}`
              : supplier.localSupplierCode,
          })))
        }
      })
      .catch(() => {
        if (!cancelled) {
          setSupplierOptions([])
          message.warning(t('posAdmin.invoiceDetail.loadSupplierOptionsFailed', '供应商选项加载失败，当前订单仍可继续编辑'))
        }
      })
      .finally(() => {
        if (!cancelled) setSupplierOptionsLoading(false)
      })

    return () => {
      cancelled = true
    }
  }, [active, currentUser?.stores, managedStoreCodeKey, t])

  useEffect(() => {
    if (typeof window === 'undefined') return

    try {
      // 只在前端记住 Excel 列和业务字段的对应关系，提交给后端的契约仍然是字段名 payload。
      window.localStorage.setItem(pasteFieldOrderStorageKey, JSON.stringify(pasteFieldOrder))
    } catch {
      // localStorage 不可用时不影响粘贴提交，继续使用当前页面内的列配置。
    }
  }, [pasteFieldOrder])

  useEffect(() => {
    if (!hasUnsavedChanges) return
    const handleBeforeUnload = (event: BeforeUnloadEvent) => {
      // 关闭或刷新浏览器标签时由浏览器弹出原生确认。
      event.preventDefault()
      event.returnValue = ''
    }
    window.addEventListener('beforeunload', handleBeforeUnload)
    return () => window.removeEventListener('beforeunload', handleBeforeUnload)
  }, [hasUnsavedChanges])

  const ensureCanAccessInvoice = useCallback(() => {
    if (canAccessInvoice) {
      return true
    }
    message.error(t('message.noPermission', '无权操作该数据'))
    return false
  }, [canAccessInvoice, t])

  /* ---- 动态高度 ---- */
  const hasSelectedRows = selectedRowKeys.length > 0
  useLayoutEffect(() => {
    const calc = () => {
      // 表格区顶部以下：扣掉表头约 38px、底部合计栏约 42px 和页面底部内边距，让合计栏留在首屏。
      const available = window.innerHeight - (tableCardRef.current?.getBoundingClientRect().top ?? 200) - 112
      setTableScrollY(available > 240 ? available : 240)
    }
    calc()
    window.addEventListener('resize', calc)
    return () => window.removeEventListener('resize', calc)
  }, [details.length, hasSelectedRows, loading, invoice?.invoiceGUID])

  /* ================================================================ */
  /*  计算属性                                                         */
  /* ================================================================ */

  // 涨跌统计
  const priceStats = useMemo(() => {
    let upCount = 0
    let downCount = 0
    details.forEach((item) => {
      if (
        item.lastPurchasePrice !== undefined &&
        item.lastPurchasePrice !== null &&
        item.lastPurchasePrice > 0 &&
        item.purchasePrice !== undefined &&
        item.purchasePrice !== null
      ) {
        if (item.purchasePrice > item.lastPurchasePrice) upCount++
        else if (item.purchasePrice < item.lastPurchasePrice) downCount++
      }
    })
    return { upCount, downCount }
  }, [details])

  // 状态统计始终基于全部明细计算，不受当前搜索和过滤条件影响。
  const detailStatusStats = useMemo(() => getDetailStatusStats(details, rowActions), [details, rowActions])

  const productTypeStats = useMemo(() => [
    { value: 0 as const, label: t('posAdmin.products.normalProduct', '单品'), color: 'default' },
    { value: 1 as const, label: t('posAdmin.products.setProduct', '套装'), color: 'blue' },
    { value: 2 as const, label: t('posAdmin.products.multiCodeProductShort', '多码'), color: 'purple' },
    { value: 'unknown' as const, label: t('common.unknown', '未知'), color: 'default' },
  ].map((option) => ({
    ...option,
    count: details.filter((detail) => option.value === 'unknown'
      ? ![0, 1, 2].includes(detail.productType ?? -1)
      : detail.productType === option.value).length,
  })), [details, t])

  // 处理进度：五段互斥，合计等于明细行数；「执行全部待执行」按全部明细计算，不受当前筛选影响。
  const progressStats = useMemo(() => getDetailProgressStats(details, rowActions), [details, rowActions])
  const pendingExecutionDetailGuids = useMemo(
    () => getPendingExecutionDetailGuids(details, rowActions),
    [details, rowActions],
  )
  // 待执行拆成「新建商品」与其余操作两组，进度区分别给出按钮。
  const pendingExecutionSplit = useMemo(
    () => splitCreateProductDetailGuids(pendingExecutionDetailGuids, details, rowActions),
    [pendingExecutionDetailGuids, details, rowActions],
  )

  // 过滤后数据：处理进度、商品类型、特殊商品先过滤，再交给搜索/涨跌/状态/操作类型的行为级过滤链（全部按 AND 叠加）。
  const filteredDetails = useMemo(
    () =>
      filterInvoiceDetails(filterDetailsByProgressBucket(details, progressBucketFilter, rowActions).filter((detail) => (productTypeFilter === 'all'
        || (productTypeFilter === 'unknown'
          ? ![0, 1, 2].includes(detail.productType ?? -1)
          : detail.productType === productTypeFilter))
        && (specialProductFilter === 'all' || Boolean(detail.isSpecialProduct) === (specialProductFilter === 'yes'))), {
        searchText,
        priceFilter,
        productStatusFilter,
        barcodeStatusFilter,
        actionTypeFilter,
        rowActions,
    }),
    [details, searchText, priceFilter, productTypeFilter, productStatusFilter, barcodeStatusFilter, actionTypeFilter, rowActions, progressBucketFilter, specialProductFilter],
  )
  const inlineNavigationDetails = useMemo(
    () =>
      buildInvoiceDetailInlineNavigationDetails(
        filteredDetails,
        columnFilteredValues,
        rowActions,
        inlineNavigationSort,
      ),
    [filteredDetails, columnFilteredValues, rowActions, inlineNavigationSort],
  )

  useEffect(() => {
    if (!activeInlineNumberEdit) return
    if (!inlineNavigationDetails.some((detail) => detail.detailGUID === activeInlineNumberEdit.detailGuid)) {
      setActiveInlineNumberEdit(null)
    }
  }, [activeInlineNumberEdit, inlineNavigationDetails])

  const isInlineNumberEditActive = useCallback(
    (detailGuid: string, field: InvoiceDetailInlineEditableField) =>
      activeInlineNumberEdit?.detailGuid === detailGuid && activeInlineNumberEdit.field === field,
    [activeInlineNumberEdit],
  )

  const activateInlineNumberEdit = useCallback(
    (detailGuid: string, field: InvoiceDetailInlineEditableField) => {
      setActiveInlineNumberEdit({ detailGuid, field })
    },
    [],
  )

  const deactivateInlineNumberEdit = useCallback(
    (detailGuid: string, field: InvoiceDetailInlineEditableField) => {
      setActiveInlineNumberEdit((current) =>
        current?.detailGuid === detailGuid && current.field === field ? null : current,
      )
    },
    [],
  )

  const handleInlineNumberNavigate = useCallback(
    (
      detailGuid: string,
      field: InvoiceDetailInlineEditableField,
      key: InvoiceDetailInlineNavigationKey,
    ) => {
      const target = resolveInvoiceDetailInlineNavigation(inlineNavigationDetails, detailGuid, field, key)
      if (!target) return false
      setActiveInlineNumberEdit(target)
      return true
    },
    [inlineNavigationDetails],
  )

  const detailActionConfig = useMemo(() => DETAIL_ACTION_CONFIG(t), [t])

  const productStatusFilterLabels: Record<ProductStatusFilter, string> = useMemo(
    () => ({
      notDetected: t('posAdmin.invoiceDetail.notDetected', '未检测'),
      exists: t('posAdmin.invoiceDetail.exists', '已存在'),
      notExists: t('posAdmin.invoiceDetail.notExistsShort', '不存在'),
      createdHere: t('posAdmin.invoiceWorkbench.statusCreatedHere'),
    }),
    [t],
  )

  const barcodeStatusFilterLabels: Record<BarcodeStatusFilter | 'abnormal', string> = useMemo(
    () => ({
      notDetected: t('posAdmin.invoiceDetail.notDetected', '未检测'),
      normal: t('posAdmin.invoiceDetail.normal', '正常'),
      noMatch: t('posAdmin.invoiceDetail.noMatch', '无匹配'),
      multiMatch: t('posAdmin.invoiceWorkbench.filterMultiMatch'),
      abnormal: t('posAdmin.invoiceWorkbench.chipBarcodeAbnormal'),
    }),
    [t],
  )

  const progressBucketLabels: Record<DetailProgressBucket, string> = useMemo(
    () => ({
      executed: t('posAdmin.invoiceWorkbench.bucketExecuted'),
      pending: t('posAdmin.invoiceWorkbench.bucketPending'),
      waiting: t('posAdmin.invoiceWorkbench.bucketWaiting'),
      unchecked: t('posAdmin.invoiceWorkbench.bucketUnchecked'),
      none: t('posAdmin.invoiceWorkbench.bucketNone'),
    }),
    [t],
  )

  const pasteFieldLabels: Record<PasteFieldKey, string> = useMemo(
    () => ({
      itemNumber: t('posAdmin.invoiceDetail.itemNumber', '货号'),
      barcode: t('posAdmin.invoiceDetail.barcode', '条码'),
      productName: t('posAdmin.invoiceDetail.productName', '商品名称'),
      quantity: t('posAdmin.invoiceDetail.quantity', '数量'),
      purchasePrice: t('posAdmin.invoiceDetail.currentPurchasePrice', '本次进货价'),
      newAutoRetailPrice: t('posAdmin.invoiceDetail.newAutoRetailPrice', '新自动零售价'),
      retailPrice: t('posAdmin.invoiceDetail.retailPrice', '零售价'),
      skip: t('posAdmin.invoiceDetail.pasteFieldSkip', '跳过此列'),
    }),
    [t],
  )
  const pasteFieldOptions = useMemo(
    () => Array.from(validPasteFieldKeys).map((field) => ({ label: pasteFieldLabels[field], value: field })),
    [pasteFieldLabels],
  )
  const pasteColumnCount = useMemo(() => getPasteTextMaxColumnCount(pasteText), [pasteText])
  const hasDuplicatePasteField = useMemo(() => hasDuplicatePasteFields(pasteFieldOrder), [pasteFieldOrder])
  const pasteMultilineAnalysis = useMemo(
    () => analyzePasteMultilineCells(pasteText, pasteFieldOrder),
    [pasteText, pasteFieldOrder],
  )
  const pasteParseOptions = useMemo(
    () => ({
      normalizeRetailPrice: normalizeRetailPriceOnPaste,
      multilineCellMode: pasteMultilineCellMode,
    }),
    [normalizeRetailPriceOnPaste, pasteMultilineCellMode],
  )
  const parsedPasteRowCount = useMemo(
    () => parsePasteText(pasteText, pasteFieldOrder, pasteParseOptions).length,
    [pasteText, pasteFieldOrder, pasteParseOptions],
  )

  useEffect(() => {
    if (pasteColumnCount <= pasteFieldOrder.length) return

    setPasteFieldOrder((prev) => [
      ...prev,
      ...Array<PasteFieldKey>(pasteColumnCount - prev.length).fill('skip'),
    ])
  }, [pasteColumnCount, pasteFieldOrder.length])

  const handleClearAllOuterFilters = useCallback(() => {
    setSearchText('')
    setPriceFilter('all')
    setProductTypeFilter('all')
    setProductStatusFilter('all')
    setBarcodeStatusFilter('all')
    setActionTypeFilter('all')
    setProgressBucketFilter('all')
    setSpecialProductFilter('all')
    setColumnFilteredValues({})
  }, [])

  const activeColumnFilterCount = useMemo(
    () => Object.values(columnFilteredValues).filter((values) => Array.isArray(values) && values.length > 0).length,
    [columnFilteredValues],
  )

  // 已生效筛选条：把搜索、进度、涨跌、状态、操作类型、特殊商品和列头过滤汇总成可单独移除的标签（复用列表工具栏共用组件）。
  const activeFilterTags = useMemo<ActiveFilterItem[]>(() => {
    const items: ActiveFilterItem[] = []
    const keyword = searchText.trim()

    if (keyword) {
      items.push({
        key: 'search',
        label: t('posAdmin.invoiceWorkbench.filterSearch'),
        value: keyword,
        source: 'toolbar',
        onRemove: () => setSearchText(''),
      })
    }

    if (progressBucketFilter !== 'all') {
      items.push({
        key: 'progress-bucket',
        label: t('posAdmin.invoiceWorkbench.filterBucket'),
        value: progressBucketLabels[progressBucketFilter],
        source: 'toolbar',
        onRemove: () => setProgressBucketFilter('all'),
      })
    }

    if (priceFilter !== 'all') {
      items.push({
        key: 'price',
        label: t('posAdmin.invoiceWorkbench.filterPrice'),
        value: priceFilter === 'up'
          ? t('posAdmin.invoiceWorkbench.chipPriceUp')
          : t('posAdmin.invoiceWorkbench.chipPriceDown'),
        source: 'toolbar',
        onRemove: () => setPriceFilter('all'),
      })
    }

    if (productTypeFilter !== 'all') {
      const option = productTypeStats.find((item) => item.value === productTypeFilter)
      items.push({
        key: 'product-type',
        label: t('posAdmin.invoiceWorkbench.filterProductType'),
        value: option?.label ?? '',
        source: 'toolbar',
        onRemove: () => setProductTypeFilter('all'),
      })
    }

    if (productStatusFilter !== 'all') {
      items.push({
        key: 'product-status',
        label: t('posAdmin.invoiceWorkbench.filterProductStatus'),
        value: productStatusFilterLabels[productStatusFilter],
        source: 'toolbar',
        onRemove: () => setProductStatusFilter('all'),
      })
    }

    if (barcodeStatusFilter !== 'all') {
      items.push({
        key: 'barcode-status',
        label: t('posAdmin.invoiceWorkbench.filterBarcodeStatus'),
        value: barcodeStatusFilterLabels[barcodeStatusFilter],
        source: 'toolbar',
        onRemove: () => setBarcodeStatusFilter('all'),
      })
    }

    if (actionTypeFilter !== 'all') {
      items.push({
        key: 'action-type',
        label: t('posAdmin.invoiceWorkbench.filterActionType'),
        value: detailActionConfig[actionTypeFilter]?.label ?? detailActionConfig[DetailActionEnum.None].label,
        source: 'toolbar',
        onRemove: () => setActionTypeFilter('all'),
      })
    }

    if (specialProductFilter !== 'all') {
      items.push({
        key: 'special-product',
        label: t('posAdmin.invoiceWorkbench.filterSpecial'),
        value: specialProductFilter === 'yes' ? t('posAdmin.invoiceDetail.yes', '是') : t('posAdmin.invoiceDetail.no', '否'),
        source: 'toolbar',
        onRemove: () => setSpecialProductFilter('all'),
      })
    }

    if (activeColumnFilterCount > 0) {
      items.push({
        key: 'column-filters',
        label: t('posAdmin.invoiceWorkbench.filterColumns'),
        value: t('posAdmin.invoiceWorkbench.filterColumnCount', { count: activeColumnFilterCount }),
        source: 'column',
        onRemove: () => setColumnFilteredValues({}),
      })
    }

    return items
  }, [
    activeColumnFilterCount,
    actionTypeFilter,
    barcodeStatusFilter,
    barcodeStatusFilterLabels,
    detailActionConfig,
    priceFilter,
    productStatusFilter,
    productStatusFilterLabels,
    productTypeFilter,
    productTypeStats,
    progressBucketFilter,
    progressBucketLabels,
    searchText,
    specialProductFilter,
    t,
  ])

  useEffect(() => {
    setSelectedRowKeys((prev) => constrainSelectedRowKeysToVisibleDetails(prev, filteredDetails))
  }, [filteredDetails])

  /* ================================================================ */
  /*  操作处理函数                                                      */
  /* ================================================================ */

  // ---- 保存主表 ----
  const handleSave = async () => {
    if (!invoiceGuid || !ensureCanAccessInvoice()) return false
    const values = await form.validateFields()
    const payload = buildInvoiceHeaderSavePayload(values)
    if (!isStoreCodeInManagedScope(payload.storeCode, managedStoreCodes)) {
      message.error(t('message.noPermission', '无权操作该数据'))
      return false
    }
    setSaving(true)
    try {
      await updateInvoice(invoiceGuid, payload)
      publishLocalSupplierInvoiceChanged(invoiceGuid)
      // 分店或供应商变更会由后端级联到明细，保存后同步刷新两份快照避免继续编辑旧范围数据。
      const refreshed = await loadInvoiceAndDetails(false)
      if (!refreshed) {
        // 保存已落库但刷新失败时失效整页状态，阻断按旧分店或供应商继续执行任何写操作。
        invoiceSnapshotRef.current = null
        detailsSnapshotRef.current = []
        loadedInvoiceGuidRef.current = null
        visibleInvoiceGuidRef.current = null
        lastLoadedManagedStoreCodeKeyRef.current = null
        setInvoice(null)
        setDetails([])
        setSelectedRowKeys([])
        setRowActions({})
        form.resetFields()
        setCanAccessInvoice(false)
        message.warning(t(
          'posAdmin.invoiceDetail.savedButRefreshFailed',
          '订单已保存但最新数据刷新失败，请重新加载',
        ))
        return false
      }
      message.success(t('posAdmin.invoiceDetail.saveSuccess', '保存成功'))
      return true
    } catch {
      message.error(t('posAdmin.invoiceDetail.saveFailed', '保存失败'))
      return false
    } finally {
      setSaving(false)
    }
  }

  // ---- 行内双击编辑，先更新本地明细，统一由“保存明细”落库 ----
  const handleInlineDetailSave = useCallback(
    (detailGuid: string, field: InvoiceDetailInlineEditableField, value: unknown) => {
      try {
        const normalizedValue = normalizeInvoiceDetailInlineValue(field, value)
        setDetails((prev) => applyInvoiceDetailInlineEdit(prev, detailGuid, field, normalizedValue))
      } catch {
        message.error(t('posAdmin.invoiceDetail.invalidInlineValue', '请输入有效的明细内容'))
      }
    },
    [t],
  )

  // ---- 批量保存明细（含行内价格编辑） ----
  const handleSaveDetails = async (options: { reload?: boolean } = {}) => {
    if (!invoiceGuid || !ensureCanAccessInvoice()) return false
    const items: InvoiceDetailUpsertItemDto[] = buildInvoiceDetailSaveItems(details)
    setDetailLoading(true)
    try {
      await batchUpsertDetails(invoiceGuid, items)
      publishLocalSupplierInvoiceChanged(invoiceGuid)
      message.success(t('posAdmin.invoiceDetail.detailSaveSuccess', '明细保存成功'))
      if (options.reload !== false) await loadDetails()
      return true
    } catch {
      message.error(t('posAdmin.invoiceDetail.detailSaveFailed', '明细保存失败'))
      return false
    } finally {
      setDetailLoading(false)
    }
  }

  // ---- 统一保存：原来表头「保存」与工具栏「保存明细」分开，现在合成一个入口 ----
  // 顺序很重要：先存明细且不刷新，再存表头；表头保存会同时刷新订单头和明细（分店/供应商变更会级联到明细）。
  const handleSaveAll = async () => {
    if (savingAll || !hasUnsavedChanges) return !hasUnsavedChanges
    setSavingAll(true)
    try {
      if (editedDetailRowCount > 0) {
        const detailsSaved = await handleSaveDetails({ reload: !headerDirty })
        if (!detailsSaved) return false
      }
      if (headerDirty) {
        return await handleSave()
      }
      return true
    } catch {
      // 表头校验未通过时 validateFields 会抛错，错误已显示在表单上。
      return false
    } finally {
      setSavingAll(false)
    }
  }

  // 会刷新明细的操作（粘贴、检测、删除、批量执行等）完成后会用服务端数据覆盖页面，
  // 有未保存修改时先提示保存，避免用户改了一半的价格被静默冲掉。
  const runAfterUnsavedGuard = (action: () => void) => {
    if (!hasUnsavedChanges) {
      action()
      return
    }
    Modal.confirm({
      title: t('posAdmin.invoiceWorkbench.unsavedConfirmTitle'),
      content: t('posAdmin.invoiceWorkbench.unsavedConfirmContent'),
      okText: t('posAdmin.invoiceWorkbench.saveAndContinue'),
      cancelText: t('common.cancel', '取消'),
      onOk: async () => {
        if (await handleSaveAll()) action()
      },
    })
  }

  const handleBackToList = () => {
    if (!hasUnsavedChanges) {
      navigate('/pos-admin/local-supplier-invoices')
      return
    }
    Modal.confirm({
      title: t('posAdmin.invoiceWorkbench.leaveConfirmTitle'),
      content: t('posAdmin.invoiceWorkbench.leaveConfirmContent', { summary: unsavedSummary }),
      okText: t('posAdmin.invoiceWorkbench.leaveWithoutSaving'),
      okButtonProps: { danger: true },
      cancelText: t('common.cancel', '取消'),
      onOk: () => navigate('/pos-admin/local-supplier-invoices'),
    })
  }

  const handlePricingApply = (detailGuid: string, changes: PricingEditorChange[]) => {
    changes.forEach((change) => handleInlineDetailSave(detailGuid, change.field, change.value))
    setPricingEditorDetailGuid(null)
  }

  // ---- 粘贴数据 ----
  const handlePaste = async () => {
    if (!invoiceGuid || !ensureCanAccessInvoice()) return
    if (hasDuplicatePasteField) {
      message.warning(t('posAdmin.invoiceDetail.pasteFieldDuplicateWarning', '同一个字段不能选择多次，请把多余列设置为“跳过此列”'))
      return
    }

    const parsed = parsePasteText(pasteText, pasteFieldOrder, pasteParseOptions)
    if (!parsed.length) {
      message.warning(t('posAdmin.invoiceDetail.noValidData', '未检测到有效数据'))
      return
    }
    const submittedInvoiceGuid = invoiceGuid
    setPasteLoading(true)
    try {
      const job = await startPasteDetailsJob({
        invoiceGuid: submittedInvoiceGuid,
        mode: pasteMode,
        items: parsed,
      })
      activePasteJobIdRef.current = job.jobId
      setPasteVisible(false)
      setPasteText('')
      setPasteMultilineCellMode('merge')
      notifyBackgroundTaskSubmitted(t('posAdmin.invoiceDetail.pasteJobSubmitted', '粘贴数据任务已提交'))

      void (async () => {
        try {
          const completedJob = await pollPasteDetailsJob(submittedInvoiceGuid, job.jobId)
          if (activePasteJobIdRef.current !== job.jobId) {
            return
          }
          const result = completedJob.result
          if (!result) {
            throw new Error(completedJob.message || t('posAdmin.invoiceDetail.pasteFailed', '粘贴数据失败'))
          }

          const description = formatPasteDetailsResult(result)
          if (completedJob.status === 'Failed') {
            notification.error({
              message: t('posAdmin.invoiceDetail.pasteFailed', '粘贴数据失败'),
              description: completedJob.message || description,
              duration: 0,
            })
          } else {
            notification[result.failed > 0 ? 'warning' : 'success']({
              message: t('posAdmin.invoiceDetail.pasteCompletedTitle', '粘贴数据完成'),
              description,
              duration: result.failed > 0 ? 0 : 4,
            })
          }
          publishLocalSupplierInvoiceChanged(submittedInvoiceGuid)
          if (canApplyInvoiceJobResult(currentInvoiceGuidRef.current, submittedInvoiceGuid)) {
            await loadDetails()
          }
        } catch (error) {
          if (error instanceof HqProductSyncPollingTimeoutError) {
            notifyBatchJobTimeout()
            return
          }
          notification.error({
            message: t('posAdmin.invoiceDetail.pasteFailed', '粘贴数据失败'),
            description: error instanceof Error ? error.message : t('posAdmin.invoiceDetail.pasteFailed', '粘贴数据失败'),
            duration: 0,
          })
        } finally {
          if (activePasteJobIdRef.current === job.jobId) {
            activePasteJobIdRef.current = null
          }
        }
      })()
    } catch (error) {
      if (isMissingBackgroundJobEndpoint(error)) {
        // 后端后台粘贴 job 未发布时兼容旧同步接口，避免用户确认后直接看到 404 失败。
        try {
          const result = await pasteDetails({
            invoiceGuid: submittedInvoiceGuid,
            mode: pasteMode,
            items: parsed,
          })
          setPasteVisible(false)
          setPasteText('')
          message.success(formatPasteDetailsResult(result))
          publishLocalSupplierInvoiceChanged(submittedInvoiceGuid)
          if (canApplyInvoiceJobResult(currentInvoiceGuidRef.current, submittedInvoiceGuid)) {
            await loadDetails()
          }
        } catch (fallbackError) {
          message.error(fallbackError instanceof Error ? fallbackError.message : t('posAdmin.invoiceDetail.pasteFailed', '粘贴数据失败'))
        }
        return
      }
      message.error(error instanceof Error ? error.message : t('posAdmin.invoiceDetail.pasteFailed', '粘贴数据失败'))
    } finally {
      setPasteLoading(false)
    }
  }

  // ---- 批量编辑 ----
  const handleBatchEdit = async () => {
    if (!invoiceGuid || !ensureCanAccessInvoice()) return
    if (!selectedRowKeys.length) {
      message.warning(t('posAdmin.invoiceDetail.selectDetailRows', '请先选择明细行'))
      return
    }
    const values = await batchEditForm.validateFields()
    const editFields: BatchEditFields = {
      updatePurchasePrice: values.updatePurchasePrice ?? false,
      purchasePrice: values.updatePurchasePrice ? values.purchasePrice : undefined,
      updateRetailPrice: values.updateRetailPrice ?? false,
      retailPrice: values.updateRetailPrice ? values.retailPrice : undefined,
      updateIsAutoPricing: values.updateIsAutoPricing ?? false,
      isAutoPricing: values.updateIsAutoPricing ? values.isAutoPricing : undefined,
      updateIsSpecialProduct: values.updateIsSpecialProduct ?? false,
      isSpecialProduct: values.updateIsSpecialProduct ? values.isSpecialProduct : undefined,
      updateDiscountRate: values.updateDiscountRate ?? false,
      discountRate: values.updateDiscountRate ? discountRateToDecimal(values.discountRate) : undefined,
      updateAction: false,
    }

    const hasAnyField =
      editFields.updatePurchasePrice ||
      editFields.updateRetailPrice ||
      editFields.updateIsAutoPricing ||
      editFields.updateIsSpecialProduct ||
      editFields.updateDiscountRate
    if (!hasAnyField) {
      message.warning(t('posAdmin.invoiceDetail.selectUpdateField', '请至少选择一个要更新的字段'))
      return
    }

    const submittedInvoiceGuid = invoiceGuid
    const submittedDetailGuids = selectedRowKeys.map(String)
    const items = submittedDetailGuids.map((detailGUID) => ({ detailGUID }))

    setBatchEditLoading(true)
    // 批量编辑确认后先更新前端当前明细，后端批量落库在后台继续执行，避免弹窗等待长请求。
    setDetails((prev) => applyInvoiceDetailBatchEdit(prev, submittedDetailGuids, editFields))
    // 批量编辑由后端写入同样的值，快照同步推进，避免这些行被误标成「未保存」；后台失败时 loadDetails 会按服务端重置。
    detailsSnapshotRef.current = applyInvoiceDetailBatchEdit(detailsSnapshotRef.current, submittedDetailGuids, editFields)
    setBatchEditVisible(false)
    batchEditForm.resetFields()
    setSelectedRowKeys([])
    setBatchEditLoading(false)
    message.success(t('posAdmin.invoiceDetail.batchUpdateSubmitted', '批量更新已提交'))

    void (async () => {
      try {
        await batchUpdateDetails(submittedInvoiceGuid, items, editFields)
        publishLocalSupplierInvoiceChanged(submittedInvoiceGuid)
        message.success(t('posAdmin.invoiceDetail.batchUpdateSuccess', '批量更新成功'))
      } catch (error) {
        message.error(error instanceof Error ? error.message : t('posAdmin.invoiceDetail.batchUpdateFailed', '批量更新失败'))
        if (canApplyInvoiceJobResult(currentInvoiceGuidRef.current, submittedInvoiceGuid)) {
          await loadDetails()
        }
      }
    })()
  }

  const openStorePriceModal = () => {
    setStorePriceVisible(true)
  }

  const openHqUpdateModal = () => {
    if (!selectedRowKeys.length) {
      message.warning(t('posAdmin.invoiceDetail.selectDetailRows', '请先选择明细行'))
      return
    }
    hqUpdateForm.resetFields()
    setHqUpdateVisible(true)
  }

  const showUpdateHqProductsResult = (result: UpdateHqProductsResult) => {
    const hqErrors = normalizeEnsureHqErrors(result.errors)
    Modal.info({
      title: t('posAdmin.invoiceDetail.updateHqProductsResultTitle', '更新HQ商品结果'),
      width: 640,
      content: (
        <Space direction="vertical" size={4} style={{ width: '100%' }}>
          <div>{t('posAdmin.invoiceDetail.updateHqProductsTotal', '总处理：{{count}} 条', { count: result.total ?? 0 })}</div>
          <div>{t('posAdmin.invoiceDetail.updateHqProductsUpdated', '成功更新：{{count}} 条', { count: result.updated ?? 0 })}</div>
          <div>{t('posAdmin.invoiceDetail.updateHqProductsSkipped', '跳过：{{count}} 条', { count: result.skipped ?? 0 })}</div>
          <div>{t('posAdmin.invoiceDetail.updateHqProductsFailedCount', '失败：{{count}} 条', { count: result.failed ?? 0 })}</div>
          <div>{t('posAdmin.invoiceDetail.ensureHqExisting', 'HQ已存在：{{count}} 条', { count: result.hqExisting ?? 0 })}</div>
          <div>{t('posAdmin.invoiceDetail.ensureHqHbwebCreated', 'HBweb新建：{{count}} 条', { count: result.hbwebCreated ?? 0 })}</div>
          <div>{t('posAdmin.invoiceDetail.ensureHqCreated', 'HQ新建：{{count}} 条', { count: result.hqCreated ?? 0 })}</div>
          <div>{t('posAdmin.invoiceDetail.ensureHqSynced', 'HQ同步：{{count}} 条', { count: result.hqSynced ?? 0 })}</div>
          <div>{t('posAdmin.invoiceDetail.updateHqPurchasePricesUpdated', 'HQ进货价更新：{{count}} 条', { count: result.hqPurchasePricesUpdated ?? 0 })}</div>
          <div>{t('posAdmin.invoiceDetail.updateHqRetailPricesUpdated', 'HQ零售价更新：{{count}} 条', { count: result.hqRetailPricesUpdated ?? 0 })}</div>
          <div>{t('posAdmin.invoiceDetail.updateHqAutoPricingUpdated', 'HQ自动定价更新：{{count}} 条', { count: result.hqAutoPricingUpdated ?? 0 })}</div>
          <div>{t('posAdmin.invoiceDetail.updateHqSpecialProductsUpdated', 'HQ特殊商品更新：{{count}} 条', { count: result.hqSpecialProductsUpdated ?? 0 })}</div>
          <div>{t('posAdmin.invoiceDetail.updateHqDiscountRatesUpdated', 'HQ折扣率更新：{{count}} 条', { count: result.hqDiscountRatesUpdated ?? 0 })}</div>
          <div>{t('posAdmin.invoiceDetail.updateHqProductSetCodesCreated', 'HQ一品多码新增：{{count}} 条', { count: result.hqProductSetCodesCreated ?? 0 })}</div>
          <div>{t('posAdmin.invoiceDetail.updateHqProductSetCodesUpdated', 'HQ一品多码更新：{{count}} 条', { count: result.hqProductSetCodesUpdated ?? 0 })}</div>
          <div>{t('posAdmin.invoiceDetail.updateHqStoreMultiCodesCreated', 'HQ分店一品多码新增：{{count}} 条', { count: result.hqStoreMultiCodesCreated ?? 0 })}</div>
          <div>{t('posAdmin.invoiceDetail.updateHqStoreMultiCodesUpdated', 'HQ分店一品多码更新：{{count}} 条', { count: result.hqStoreMultiCodesUpdated ?? 0 })}</div>
          {hqErrors.length > 0 && (
            <div style={{ maxHeight: 220, overflow: 'auto', marginTop: 8 }}>
              {hqErrors.map((item, index) => (
                <div key={`${item.detailGuid || 'detail'}-${item.storeCode ?? 'store'}-${index}`} style={{ color: '#ff4d4f', fontSize: 12 }}>
                  {item.detailGuid ? `${item.detailGuid}：` : ''}{item.storeCode ? `${item.storeCode}：` : ''}{item.message}
                </div>
              ))}
            </div>
          )}
        </Space>
      ),
    })
  }

  const showUpdateLastPurchasePricesResult = (result: UpdateLastPurchasePricesResult) => {
    Modal.info({
      title: t('posAdmin.invoiceDetail.updateLastPurchasePricesResultTitle', '更新上次进货价结果'),
      width: 560,
      content: (
        <Space direction="vertical" size={4} style={{ width: '100%' }}>
          <div>{t('posAdmin.invoiceDetail.updateLastPurchasePricesTotal', '总处理：{{count}} 条', { count: result.total ?? 0 })}</div>
          <div>{t('posAdmin.invoiceDetail.updateLastPurchasePricesUpdated', '成功更新：{{count}} 条', { count: result.updated ?? 0 })}</div>
          <div>{t('posAdmin.invoiceDetail.updateLastPurchasePricesSkipped', '跳过：{{count}} 条', { count: result.skipped ?? 0 })}</div>
          {result.errors?.length > 0 && (
            <div style={{ maxHeight: 180, overflow: 'auto', marginTop: 8 }}>
              {result.errors.map((error, index) => (
                <div key={`${error}-${index}`} style={{ color: '#ff4d4f', fontSize: 12 }}>
                  {error}
                </div>
              ))}
            </div>
          )}
        </Space>
      ),
    })
  }

  const renderBackgroundTaskDetailsButton = (onClick: () => void) => (
    <Button type="link" size="small" style={{ padding: 0 }} onClick={onClick}>
      {t('posAdmin.invoiceDetail.backgroundTaskViewDetails', '查看详情')}
    </Button>
  )

  const notifyBackgroundTaskSubmitted = (messageText: string) => {
    notification.info({
      message: messageText,
      description: t('posAdmin.invoiceDetail.backgroundTaskSubmitted', '已提交到后台执行，完成后会在右上角通知结果。'),
      duration: 3,
    })
  }

  const notifyBatchJobTimeout = () => {
    notification.warning({
      message: t('posAdmin.invoiceDetail.localSupplierInvoiceBatchJobTimeoutTitle', '本地进货单批量任务仍在后台执行'),
      description: t('posAdmin.invoiceDetail.localSupplierInvoiceBatchJobTimeout', '前端已停止轮询该任务。你可以稍后刷新页面查看结果，或使用相同条件重新提交以接管后台任务。'),
      duration: 0,
    })
  }

  const formatPasteDetailsResult = (result: BatchResultDto) => {
    return t('posAdmin.invoiceDetail.pasteComplete', '粘贴完成：新增 {{inserted}} 条，更新 {{updated}} 条，失败 {{failed}} 条', {
      inserted: result.inserted ?? 0,
      updated: result.updated ?? 0,
      failed: result.failed ?? 0,
    })
  }

  const pollPasteDetailsJob = async (submittedInvoiceGuid: string, jobId: string) => {
    // 关键位置：粘贴数据可能触发大量写入，只保留 job 查询，避免弹窗确认一直等待长请求。
    const poller = createHqSyncJobPoller<PasteDetailsJobResult>({
      jobId,
      getJob: () => getPasteDetailsJob(submittedInvoiceGuid, jobId),
    })
    return poller.promise
  }

  const pollCheckProductsJob = async (submittedInvoiceGuid: string, jobId: string) => {
    // 关键位置：商品检测改为后台执行，前台只轮询终态并合并结果。
    const poller = createHqSyncJobPoller<CheckProductsJobResult>({
      jobId,
      getJob: () => getCheckProductsJob(submittedInvoiceGuid, jobId),
    })
    return poller.promise
  }

  const pollUpdateToStorePricesJob = async (jobId: string) => {
    // 关键位置：长任务只轮询后台 job，避免把浏览器请求保持到网关超时。
    const poller = createHqSyncJobPoller<UpdateToStorePricesJobResult>({
      jobId,
      getJob: () => getUpdateToStorePricesJob(jobId),
    })
    return poller.promise
  }

  const pollUpdateHqProductsJob = async (jobId: string) => {
    // 关键位置：更新 HQ 商品可能跨库写入，必须通过后台 job 查询最终结果。
    const poller = createHqSyncJobPoller<UpdateHqProductsJobResult>({
      jobId,
      getJob: () => getUpdateHqProductsJob(invoiceGuid!, jobId),
    })
    return poller.promise
  }

  const formatUpdateToStoreResult = (result: UpdateToStorePricesResult) => {
    return t(
      'posAdmin.invoiceDetail.updateToStoreResultWithSkipped',
      '更新完成：成功 {{updated}} 条，跳过 {{skipped}} 条，失败 {{failed}} 条',
      { updated: result.updated ?? 0, skipped: result.skipped ?? 0, failed: result.failed ?? 0 },
    )
  }

  const showUpdateToStoreErrors = (result: UpdateToStorePricesResult) => {
    Modal.error({
      title: t('posAdmin.invoiceDetail.updateToStoreResultTitle', '更新到分店价格结果'),
      content: (
        <Space direction="vertical" size={8} style={{ width: '100%' }}>
          <div>{formatUpdateToStoreResult(result)}</div>
          {!!result.errors?.length && (
            <div style={{ maxHeight: 240, overflow: 'auto' }}>
              {result.errors.map((err, i) => (
                <div key={i} style={{ color: '#ff4d4f', fontSize: 12 }}>{err}</div>
              ))}
            </div>
          )}
        </Space>
      ),
    })
  }

  const formatBatchExecuteResultParts = (result: BatchExecuteActionsResult) => {
    const parts: string[] = []
    if (result.createdProducts > 0) parts.push(t('posAdmin.invoiceDetail.createdProducts', '新建商品{{count}}条', { count: result.createdProducts }))
    if (result.updatedPurchasePrices > 0) parts.push(t('posAdmin.invoiceDetail.updatedPurchasePrices', '更新进货价{{count}}条', { count: result.updatedPurchasePrices }))
    if (result.updatedItemNumbers > 0) parts.push(t('posAdmin.invoiceDetail.updatedItemNumbers', '更新货号{{count}}条', { count: result.updatedItemNumbers }))
    if (result.addedMultiCodes > 0) parts.push(t('posAdmin.invoiceDetail.addedMultiCodes', '添加多码{{count}}条', { count: result.addedMultiCodes }))
    if (result.skipped > 0) parts.push(t('posAdmin.invoiceDetail.skipped', '跳过{{count}}条', { count: result.skipped }))
    if (result.failed > 0) parts.push(t('posAdmin.invoiceDetail.failed', '失败{{count}}条', { count: result.failed }))
    return parts.join('，') || t('posAdmin.invoiceDetail.noOperation', '无操作')
  }

  const showBatchExecuteResultDetails = (result: BatchExecuteActionsResult) => {
    Modal.error({
      title: t('posAdmin.invoiceDetail.partialFailed', '部分操作失败'),
      content: (
        <div>
          <p>{formatBatchExecuteResultParts(result)}</p>
          <div style={{ maxHeight: 200, overflow: 'auto', marginTop: 8 }}>
            {result.errors.map((err, i) => (
              <div key={i} style={{ color: '#ff4d4f', fontSize: 12 }}>{err}</div>
            ))}
          </div>
        </div>
      ),
    })
  }

  // ---- 更新到分店价格 ----
  const handleUpdateToStorePrices = async () => {
    if (!invoiceGuid || !ensureCanAccessInvoice()) return
    if (!selectedRowKeys.length) {
      message.warning(t('posAdmin.invoiceDetail.selectDetailRows', '请先选择明细行'))
      return
    }
    const values = await storePriceForm.validateFields()
    if (!values.targetStoreCodes?.length) {
      message.warning(t('posAdmin.invoiceDetail.selectTargetStore', '请选择目标分店'))
      return
    }
    if (!values.targetStoreCodes.every((storeCode: string) => isStoreCodeInManagedScope(storeCode, managedStoreCodes))) {
      message.error(t('message.noPermission', '无权操作该数据'))
      return
    }

    const updateFields = buildUpdatePriceFields(values)
    if (!hasAnyUpdatePriceField(updateFields)) {
      message.warning(t('posAdmin.invoiceDetail.selectUpdateField', '请至少选择一个要更新的字段'))
      return
    }

    const request: UpdateToStorePricesRequest = {
      invoiceGuid,
      detailGuids: selectedRowKeys.map(String),
      targetStoreCodes: values.targetStoreCodes,
      updateFields,
    }
    // 「同时更新 HQ 数据库」默认打开；没有写 HQ 权限时开关禁用，这里再兜底一次。
    const syncToHq = values.syncToHq === true && canWriteLocalPurchaseToHq

    setStorePriceVisible(false)
    storePriceForm.resetFields()
    setStorePriceLoading(true)

    void (async () => {
      try {
        const storePriceDetailSet = new Set(request.detailGuids)
        const storePriceDetails = details.filter((detail) => detail.detailGUID && storePriceDetailSet.has(detail.detailGUID))
        if (storePriceDetails.length !== request.detailGuids.length) {
          throw new Error(t('posAdmin.invoiceDetail.selectedDetailNotFound', '未找到选中的明细行'))
        }

        // 更新到分店按前端明细行取值；提交任务前先保存选中行，避免后端读取旧明细。
        await batchUpsertDetails(invoiceGuid, buildInvoiceDetailSaveItems(storePriceDetails))
        notifyBackgroundTaskSubmitted(t('posAdmin.invoiceDetail.updateToStoreSubmitted', '更新到分店价格已提交'))

        const job = await startUpdateToStorePricesJob(request)
        const completedJob = await pollUpdateToStorePricesJob(job.jobId)
        const result = completedJob.result
        if (!result) {
          throw new Error(completedJob.message || t('posAdmin.invoiceDetail.updateToStoreFailed', '更新到分店价格失败'))
        }
        const description = formatUpdateToStoreResult(result)
        const hasDetails = !!result.errors?.length || (result.skipped ?? 0) > 0 || (result.failed ?? 0) > 0
        if (completedJob.status === 'Failed') {
          notification.error({
            message: t('posAdmin.invoiceDetail.updateToStoreFailed', '更新到分店价格失败'),
            description: hasDetails ? (
              <Space direction="vertical" size={4}>
                <span>{completedJob.message || description}</span>
                {renderBackgroundTaskDetailsButton(() => showUpdateToStoreErrors(result))}
              </Space>
            ) : (completedJob.message || description),
            duration: 0,
          })
          return
        }
        notification[result.failed > 0 || result.errors?.length ? 'warning' : 'success']({
          message: t('posAdmin.invoiceDetail.updateToStoreCompleted', '更新到分店价格完成'),
          description: hasDetails ? (
            <Space direction="vertical" size={4}>
              <span>{description}</span>
              {renderBackgroundTaskDetailsButton(() => showUpdateToStoreErrors(result))}
            </Space>
          ) : description,
          duration: hasDetails ? 0 : 4,
        })
        // 本地分店价写完再按同样的明细、分店与字段补写 HQ；本地整单失败时（上面已 return）不写 HQ。
        if (syncToHq) {
          runUpdateHqProductsJob({
            invoiceGuid: request.invoiceGuid,
            detailGuids: request.detailGuids,
            targetStoreCodes: request.targetStoreCodes,
            updateFields: request.updateFields,
            idempotencyKey: createHqIdempotencyKey(request.invoiceGuid),
            saveSelectedDetails: false,
          })
        }
      } catch (error) {
        if (error instanceof HqProductSyncPollingTimeoutError) {
          notifyBatchJobTimeout()
          return
        }
        notification.error({
          message: t('posAdmin.invoiceDetail.updateToStoreFailed', '更新到分店价格失败'),
          description: error instanceof Error ? error.message : t('posAdmin.invoiceDetail.updateToStoreFailed', '更新到分店价格失败'),
          duration: 0,
        })
      } finally {
        setStorePriceLoading(false)
      }
    })()
  }

  // 后台提交「更新HQ商品」任务并通知结果。「更新HQ商品」按钮，以及「新建商品」「更新到分店」勾选「同时更新 HQ 数据库」后都走这里。
  const runUpdateHqProductsJob = (params: {
    invoiceGuid: string
    detailGuids: string[]
    targetStoreCodes: string[]
    updateFields: UpdateToStorePricesFields
    idempotencyKey: string
    /** 按钮直接提交时先保存前端选中行（HQ 按后端明细取值）；串联提交时上一步已落库，不再保存。 */
    saveSelectedDetails: boolean
    onFinished?: (keepIdempotencyKey: boolean) => void
  }) => {
    const { invoiceGuid, detailGuids, targetStoreCodes, updateFields, idempotencyKey } = params
    setHqUpdateLoading(true)

    void (async () => {
      let shouldClearIdempotencyKey = true
      try {
        if (params.saveSelectedDetails) {
          const selectedDetailSet = new Set(detailGuids)
          const selectedDetails = details.filter((detail) => detail.detailGUID && selectedDetailSet.has(detail.detailGUID))
          if (selectedDetails.length !== detailGuids.length) {
            throw new Error(t('posAdmin.invoiceDetail.selectedDetailNotFound', '未找到选中的明细行'))
          }

          // HQ 按后端明细行写入字段值；提交任务前先保存当前前端选中行，避免读到旧明细。
          await batchUpsertDetails(invoiceGuid, buildInvoiceDetailSaveItems(selectedDetails))
        }
        notifyBackgroundTaskSubmitted(t('posAdmin.invoiceDetail.updateHqProductsSubmitted', '更新HQ商品已提交'))

        const job = await startUpdateHqProductsJob(invoiceGuid, {
          detailGuids,
          targetStoreCodes,
          updateFields,
          idempotencyKey,
        })
        const completedJob = await pollUpdateHqProductsJob(job.jobId)
        const result = completedJob.result
        if (!result) {
          throw new Error(completedJob.message || t('posAdmin.invoiceDetail.updateHqProductsFailed', '更新HQ商品失败'))
        }
        const hasDetails = result.failed > 0 || (result.skipped ?? 0) > 0 || !!result.errors?.length
        if (completedJob.status === 'Failed') {
          notification.error({
            message: t('posAdmin.invoiceDetail.updateHqProductsFailed', '更新HQ商品失败'),
            description: (
              <Space direction="vertical" size={4}>
                <span>{completedJob.message || t('posAdmin.invoiceDetail.updateHqProductsFailedCount', '失败：{{count}} 条', { count: result.failed ?? 0 })}</span>
                {hasDetails && renderBackgroundTaskDetailsButton(() => showUpdateHqProductsResult(result))}
              </Space>
            ),
            duration: 0,
          })
          return
        }
        notification[result.failed > 0 || result.errors?.length ? 'warning' : 'success']({
          message: t('posAdmin.invoiceDetail.updateHqProductsCompleted', '更新HQ商品完成'),
          description: (
            <Space direction="vertical" size={4}>
              <span>{t('posAdmin.invoiceDetail.updateHqProductsUpdated', '成功更新：{{count}} 条', { count: result.updated ?? 0 })}</span>
              {hasDetails && renderBackgroundTaskDetailsButton(() => showUpdateHqProductsResult(result))}
            </Space>
          ),
          duration: hasDetails ? 0 : 4,
        })
        publishLocalSupplierInvoiceChanged(invoiceGuid)
        await loadDetails()
      } catch (error) {
        if (error instanceof HqProductSyncPollingTimeoutError) {
          shouldClearIdempotencyKey = false
          notifyBatchJobTimeout()
          return
        }
        const failure = getUpdateHqProductsFailure(error)
        notification.error({
          message: t('posAdmin.invoiceDetail.updateHqProductsFailed', '更新HQ商品失败'),
          description: failure ? (
            <Space direction="vertical" size={4}>
              <span>{t('posAdmin.invoiceDetail.updateHqProductsFailedCount', '失败：{{count}} 条', { count: failure.failed ?? 0 })}</span>
              {renderBackgroundTaskDetailsButton(() => showUpdateHqProductsResult(failure))}
            </Space>
          ) : (error instanceof Error ? error.message : t('posAdmin.invoiceDetail.updateHqProductsFailed', '更新HQ商品失败')),
          duration: 0,
        })
      } finally {
        params.onFinished?.(!shouldClearIdempotencyKey)
        setHqUpdateLoading(false)
      }
    })()
  }

  // ---- 更新 HQ 商品 ----
  const handleUpdateHqProducts = async () => {
    if (hqUpdateLoading) return
    if (!invoiceGuid || !ensureCanAccessInvoice()) return
    if (!canWriteLocalPurchaseToHq) {
      message.error(t('message.noPermission', '无权操作该数据'))
      return
    }
    if (!selectedRowKeys.length) {
      message.warning(t('posAdmin.invoiceDetail.selectDetailRows', '请先选择明细行'))
      return
    }
    const values = await hqUpdateForm.validateFields()
    if (!values.targetStoreCodes?.length) {
      message.warning(t('posAdmin.invoiceDetail.selectTargetStore', '请选择目标分店'))
      return
    }
    if (!values.targetStoreCodes.every((storeCode: string) => isStoreCodeInManagedScope(storeCode, managedStoreCodes))) {
      message.error(t('message.noPermission', '无权操作该数据'))
      return
    }

    const updateFields = buildUpdatePriceFields(values)
    if (!hasAnyUpdatePriceField(updateFields)) {
      message.warning(t('posAdmin.invoiceDetail.selectUpdateField', '请至少选择一个要更新的字段'))
      return
    }

    const detailGuids = selectedRowKeys.map(String)
    const targetStoreCodes = values.targetStoreCodes

    // 同一轮提交使用稳定幂等键，避免用户重复点击造成 HQ 重复写入。
    hqUpdateIdempotencyKeyRef.current =
      hqUpdateIdempotencyKeyRef.current ??
      (typeof crypto !== 'undefined' && 'randomUUID' in crypto
        ? crypto.randomUUID()
        : `${invoiceGuid}-${detailGuids.join(',')}-${targetStoreCodes.join(',')}`)
    const idempotencyKey = hqUpdateIdempotencyKeyRef.current

    setHqUpdateVisible(false)
    hqUpdateForm.resetFields()
    runUpdateHqProductsJob({
      invoiceGuid,
      detailGuids,
      targetStoreCodes,
      updateFields,
      idempotencyKey,
      saveSelectedDetails: true,
      // 轮询超时时 HQ 可能已写入：保留幂等键，用户重试沿用同一个键，避免重复写 HQ。
      onFinished: (keepIdempotencyKey) => {
        if (!keepIdempotencyKey) hqUpdateIdempotencyKeyRef.current = null
      },
    })
  }

  const applyCheckProductsResponse = (result: CheckProductsResponse) => {
    // 更新每行的商品状态和条码状态
    const statusMap = new Map(result.results.map((r) => [r.detailGuid, r]))
    setDetails((prev) =>
      prev.map((d) => {
        const checkResult = statusMap.get(d.detailGUID)
        return checkResult ? mergeProductCheckResult(d, checkResult) : d
      }),
    )
    // 更新行内操作类型
    const newActions: Record<string, number> = {}
    result.results.forEach((r) => {
      if (r.defaultAction !== undefined) {
        newActions[r.detailGuid] = r.defaultAction
      }
    })
    setRowActions((prev) => ({ ...prev, ...newActions }))
  }

  const handleUpdateLastPurchasePrices = () => {
    if (updatingLastPurchasePrices) return
    if (!invoiceGuid || !ensureCanAccessInvoice()) return
    if (!details.length) {
      message.warning(t('posAdmin.invoiceDetail.noDetailToUpdateLastPurchasePrice', '没有明细数据可更新'))
      return
    }

    const submittedInvoiceGuid = invoiceGuid
    const selectedDetailGuids = selectedRowKeys.map(String)
    const scopeLabel = selectedDetailGuids.length > 0
      ? t('posAdmin.invoiceDetail.selectedRows', '已选明细')
      : t('posAdmin.invoiceDetail.allRows', '全部明细')
    const scopeCount = selectedDetailGuids.length > 0 ? selectedDetailGuids.length : details.length

    Modal.confirm({
      title: t('posAdmin.invoiceDetail.confirmUpdateLastPurchasePricesTitle', '确认更新上次进货价？'),
      content: (
        <Space direction="vertical" size={4}>
          <div>{t('posAdmin.invoiceDetail.confirmUpdateLastPurchasePricesScope', '范围：{{scope}}（{{count}} 条）', { scope: scopeLabel, count: scopeCount })}</div>
          <div>{t('posAdmin.invoiceDetail.confirmUpdateLastPurchasePricesDesc', '将按当前分店进货价优先、商品主档进货价兜底，强制刷新进货单明细的上次进货价。')}</div>
        </Space>
      ),
      okText: t('posAdmin.invoiceDetail.updateLastPurchasePricesBtn', '更新上次进货价'),
      cancelText: t('common.cancel', '取消'),
      onOk: async () => {
        setUpdatingLastPurchasePrices(true)
        try {
          const result = await updateLastPurchasePrices(submittedInvoiceGuid, {
            detailGuids: selectedDetailGuids.length > 0 ? selectedDetailGuids : undefined,
          })
          const hasDetails = (result.errors?.length ?? 0) > 0
          notification.success({
            message: t('posAdmin.invoiceDetail.updateLastPurchasePricesCompleted', '更新上次进货价完成'),
            description: (
              <Space direction="vertical" size={4}>
                <span>{t('posAdmin.invoiceDetail.updateLastPurchasePricesSummary', '成功 {{updated}} 条，跳过 {{skipped}} 条', {
                  updated: result.updated ?? 0,
                  skipped: result.skipped ?? 0,
                })}</span>
                {hasDetails && renderBackgroundTaskDetailsButton(() => showUpdateLastPurchasePricesResult(result))}
              </Space>
            ),
            duration: hasDetails ? 0 : 4,
          })
          publishLocalSupplierInvoiceChanged(submittedInvoiceGuid)
          if (canApplyInvoiceJobResult(currentInvoiceGuidRef.current, submittedInvoiceGuid)) {
            await loadDetails()
          }
        } catch (error) {
          notification.error({
            message: t('posAdmin.invoiceDetail.updateLastPurchasePricesFailed', '更新上次进货价失败'),
            description: error instanceof Error ? error.message : t('posAdmin.invoiceDetail.updateLastPurchasePricesFailed', '更新上次进货价失败'),
            duration: 0,
          })
        } finally {
          setUpdatingLastPurchasePrices(false)
        }
      },
    })
  }

  // ---- 商品检测 ----
  const handleCheckProducts = async () => {
    if (checking) return
    if (!invoiceGuid || !ensureCanAccessInvoice()) return
    if (!details.length) {
      message.warning(t('posAdmin.invoiceDetail.noDetailToDetect', '没有明细数据可检测'))
      return
    }
    const submittedInvoiceGuid = invoiceGuid
    const detailGuids = selectedRowKeys.length > 0 ? selectedRowKeys.map(String) : undefined
    setChecking(true)
    try {
      const job = await startCheckProductsJob({
        invoiceGuid: submittedInvoiceGuid,
        detailGuids,
      })
      activeCheckProductsJobIdRef.current = job.jobId
      notifyBackgroundTaskSubmitted(t('posAdmin.invoiceDetail.checkProductsJobSubmitted', '商品检测任务已提交'))

      void (async () => {
        try {
          const completedJob = await pollCheckProductsJob(submittedInvoiceGuid, job.jobId)
          if (activeCheckProductsJobIdRef.current !== job.jobId) {
            return
          }
          const result = completedJob.result
          if (completedJob.status === 'Failed') {
            notification.error({
              message: t('posAdmin.invoiceDetail.detectFailed', '商品检测失败'),
              description: completedJob.message || t('posAdmin.invoiceDetail.detectFailed', '商品检测失败'),
              duration: 0,
            })
            return
          }
          if (!result) {
            throw new Error(completedJob.message || t('posAdmin.invoiceDetail.detectFailed', '商品检测失败'))
          }

          const description = t('posAdmin.invoiceDetail.detectCompleteMsg', '检测完成：共 {{total}}条，商品存在 {{productExists}}条，不存在 {{productNotExists}}条，条码正常 {{barcodeNormal}}条，异常 {{barcodeAbnormal}}条', result.summary)
          notification.success({
            message: t('posAdmin.invoiceDetail.detectCompletedTitle', '商品检测完成'),
            description,
            duration: 4,
          })
          publishLocalSupplierInvoiceChanged(submittedInvoiceGuid)
          if (canApplyCheckProductsJobResult({
            currentInvoiceGuid: currentInvoiceGuidRef.current,
            submittedInvoiceGuid,
            status: completedJob.status,
            hasResult: true,
          })) {
            applyCheckProductsResponse(result)
            await loadDetails()
          }
        } catch (error) {
          if (error instanceof HqProductSyncPollingTimeoutError) {
            notifyBatchJobTimeout()
            return
          }
          notification.error({
            message: t('posAdmin.invoiceDetail.detectFailed', '商品检测失败'),
            description: error instanceof Error ? error.message : t('posAdmin.invoiceDetail.detectFailed', '商品检测失败'),
            duration: 0,
          })
        } finally {
          if (activeCheckProductsJobIdRef.current === job.jobId) {
            activeCheckProductsJobIdRef.current = null
            setChecking(false)
          }
        }
      })()
    } catch (error) {
      if (isMissingBackgroundJobEndpoint(error)) {
        // 后端后台商品检测 job 未发布时兼容旧同步接口，避免商品检测按钮在当前环境直接失败。
        try {
          const result = await checkProducts({
            invoiceGuid: submittedInvoiceGuid,
            detailGuids,
          })
          const description = t('posAdmin.invoiceDetail.detectCompleteMsg', '检测完成：共 {{total}}条，商品存在 {{productExists}}条，不存在 {{productNotExists}}条，条码正常 {{barcodeNormal}}条，异常 {{barcodeAbnormal}}条', result.summary)
          message.success(description)
          publishLocalSupplierInvoiceChanged(submittedInvoiceGuid)
          if (canApplyCheckProductsJobResult({
            currentInvoiceGuid: currentInvoiceGuidRef.current,
            submittedInvoiceGuid,
            status: 'Succeeded',
            hasResult: true,
          })) {
            applyCheckProductsResponse(result)
            await loadDetails()
          }
        } catch (fallbackError) {
          message.error(fallbackError instanceof Error ? fallbackError.message : t('posAdmin.invoiceDetail.detectFailed', '商品检测失败'))
        } finally {
          setChecking(false)
        }
        return
      }
      message.error(error instanceof Error ? error.message : t('posAdmin.invoiceDetail.detectFailed', '商品检测失败'))
      setChecking(false)
    }
  }

  // ---- 行操作类型变更 ----
  const handleRowActionChange = async (detailGuid: string, actionKey: string) => {
    if (!ensureCanAccessInvoice()) return
    if (!isAdmin) {
      message.error(t('message.noPermission', '无权操作该数据'))
      return
    }
    const action = Number(actionKey) as DetailAction

    if (invoiceGuid) {
      try {
        await updateDetailAction(invoiceGuid, detailGuid, action)
        setRowActions((prev) => ({ ...prev, [detailGuid]: action }))
        setDetails((prev) =>
          prev.map((item) =>
            item.detailGUID === detailGuid ? { ...item, activityType: action } : item,
          ),
        )
      } catch (error) {
        message.error(error instanceof Error ? error.message : t('posAdmin.invoiceDetail.updateActionFailed', '更新操作类型失败'))
      }
    }
  }

  // 「新建商品」勾选「同时更新 HQ 数据库」：只把这次真正建好（已回填商品编码）的行交给 HQ 任务，为全部启用分店写全部价格字段。
  const syncCreatedProductsToHq = async (snapshot: ReturnType<typeof buildBatchExecuteSnapshot>) => {
    if (!invoiceGuid) return
    const createGuidSet = new Set(
      snapshot.expectedActions
        .filter((item) => item.action === DetailActionEnum.CreateProduct)
        .map((item) => item.detailGuid),
    )
    try {
      // 执行结果只有计数没有逐行结果，按服务端回填的商品编码判断哪些行真正建好。
      const refreshed = await getInvoiceDetails(invoiceGuid)
      const createdGuids = refreshed
        .filter((detail) => createGuidSet.has(detail.detailGUID) && detail.productCode)
        .map((detail) => detail.detailGUID)
      if (!createdGuids.length) return
      if (!allStoreCodes.length) {
        message.warning(t('posAdmin.invoiceWorkbench.syncToHqNoStores'))
        return
      }
      runUpdateHqProductsJob({
        invoiceGuid,
        detailGuids: createdGuids,
        targetStoreCodes: allStoreCodes,
        updateFields: ALL_UPDATE_PRICE_FIELDS,
        idempotencyKey: createHqIdempotencyKey(invoiceGuid),
        saveSelectedDetails: false,
      })
    } catch (error) {
      notification.error({
        message: t('posAdmin.invoiceDetail.updateHqProductsFailed', '更新HQ商品失败'),
        description: error instanceof Error ? error.message : undefined,
        duration: 0,
      })
    }
  }

  const executeSelectedBatchActions = async (
    snapshot: ReturnType<typeof buildBatchExecuteSnapshot>,
    options?: { syncCreatedToHq?: boolean },
  ) => {
    if (!invoiceGuid || !ensureCanAccessInvoice()) return
    setExecuting(true)
    notifyBackgroundTaskSubmitted(t('posAdmin.invoiceDetail.batchExecuteSubmitted', '批量执行操作已提交'))
    try {
      const result: BatchExecuteActionsResult = await batchExecuteActions({
        invoiceGuid,
        detailGuids: snapshot.detailGuids,
        expectedActions: snapshot.expectedActions,
        confirmedCreateProductCount: snapshot.confirmedCreateProductCount,
        confirmedAt: snapshot.confirmedAt ?? new Date().toISOString(),
        newProductProductTypeSelections: snapshot.newProductProductTypeSelections,
      })
      const parts = formatBatchExecuteResultParts(result)
      const hasDetails = !!result.errors?.length || result.failed > 0 || result.skipped > 0
      notification[result.failed > 0 || result.errors?.length ? 'warning' : 'success']({
        message: t('posAdmin.invoiceDetail.batchExecuteCompleted', '批量执行操作完成'),
        description: hasDetails ? (
          <Space direction="vertical" size={4}>
            <span>{t('posAdmin.invoiceDetail.executeResultMsg', '执行完成：{{parts}}', { parts })}</span>
            {renderBackgroundTaskDetailsButton(() => showBatchExecuteResultDetails(result))}
          </Space>
        ) : t('posAdmin.invoiceDetail.executeResultMsg', '执行完成：{{parts}}', { parts }),
        duration: hasDetails ? 0 : 4,
      })
      publishLocalSupplierInvoiceChanged(invoiceGuid)
      void loadDetails()
      if (options?.syncCreatedToHq && result.createdProducts > 0) {
        void syncCreatedProductsToHq(snapshot)
      }
    } catch (error) {
      const feedback = getBatchExecuteErrorFeedback(error, t('posAdmin.invoiceDetail.executeFailed', '批量执行操作失败'))
      notification.error({
        message: feedback.message,
        description: feedback.details.length ? (
          <Space direction="vertical" size={4}>
            {feedback.failure && <span>{formatBatchExecuteResultParts(feedback.failure)}</span>}
            {feedback.failure && renderBackgroundTaskDetailsButton(() => showBatchExecuteResultDetails(feedback.failure!))}
          </Space>
        ) : feedback.message,
        duration: 0,
      })
    } finally {
      setExecuting(false)
    }
  }

  // ---- 批量执行操作 ----
  const handleBatchExecute = (explicitDetailGuids?: string[], mode: BatchExecuteMode = 'otherActions') => {
    if (!invoiceGuid || !ensureCanAccessInvoice()) return
    // 「执行全部待执行」直接按全部明细里的待执行行提交（不受当前筛选影响）；勾选执行仍只执行当前可见的选中行。
    const visibleSelectedRowKeys = explicitDetailGuids ?? constrainSelectedRowKeysToVisibleDetails(selectedRowKeys, filteredDetails)
    if (!visibleSelectedRowKeys.length) {
      message.warning(t('posAdmin.invoiceDetail.selectDetailsFirst', '请先选择明细行'))
      return
    }
    if (!explicitDetailGuids && visibleSelectedRowKeys.length !== selectedRowKeys.length) {
      setSelectedRowKeys(visibleSelectedRowKeys)
    }

    // 「新建商品」与其余操作分开执行：新建单独确认（可同时更新 HQ），「执行操作」跳过新建行。
    const { createGuids, otherGuids } = splitCreateProductDetailGuids(visibleSelectedRowKeys, details, rowActions)
    const isCreateMode = mode === 'createProducts'
    const targetGuids = isCreateMode ? createGuids : otherGuids
    if (!targetGuids.length) {
      message.warning(isCreateMode
        ? t('posAdmin.invoiceWorkbench.noCreateProductRows')
        : t('posAdmin.invoiceWorkbench.onlyCreateProductRows'))
      return
    }
    const skippedCreateCount = isCreateMode ? 0 : createGuids.length
    // 写 HQ 需要 PushToHq 权限；没有时开关禁用且不勾选。确认框不受 React 状态管理，用普通对象记录勾选结果。
    const hqChoice = { syncToHq: canWriteLocalPurchaseToHq }

    const previewSnapshot = buildBatchExecuteSnapshot({
      selectedRowKeys: targetGuids,
      details,
      rowActions,
    })
    const newProductWithAdditionalBarcodesRows = getNewProductWithAdditionalBarcodesRows(
      targetGuids,
      details,
      rowActions,
    )
    const newProductProductTypeSelectionMap = new Map<string, 1 | 2>(
      newProductWithAdditionalBarcodesRows.map((row) => [row.detailGuid, 2]),
    )
    const confirmText = buildBatchExecuteConfirmText({
      selectedCount: previewSnapshot.selectedCount,
      createProductCount: previewSnapshot.confirmedCreateProductCount,
      labels: isCreateMode
        ? {
            title: t('posAdmin.invoiceWorkbench.createProductsConfirmTitle'),
            content: t('posAdmin.invoiceWorkbench.createProductsConfirmContent'),
            createProductNotice: t('posAdmin.invoiceDetail.batchExecuteCreateProductNotice', '其中 {{count}} 条会新建商品，请确认货号、条码和名称无误。'),
            okText: t('posAdmin.invoiceWorkbench.createProductsConfirmOk'),
            cancelText: t('common.cancel', '取消'),
          }
        : {
            title: t('posAdmin.invoiceDetail.batchExecuteConfirmTitle', '确认执行批量操作？'),
            content: t('posAdmin.invoiceDetail.batchExecuteConfirmContent', '将对 {{count}} 条明细执行已设置的操作。'),
            createProductNotice: t('posAdmin.invoiceDetail.batchExecuteCreateProductNotice', '其中 {{count}} 条会新建商品，请确认货号、条码和名称无误。'),
            okText: t('posAdmin.invoiceDetail.batchExecuteConfirmOk', '确认执行'),
            cancelText: t('common.cancel', '取消'),
          },
    })

    Modal.confirm({
      title: confirmText.title,
      content: (
        <Space direction="vertical" size={4}>
          {confirmText.content.split('\n').map((line) => (
            <div key={line}>{line}</div>
          ))}
          {skippedCreateCount > 0 && (
            <Alert type="info" showIcon message={t('posAdmin.invoiceWorkbench.skippedCreateRows', { count: skippedCreateCount })} />
          )}
          {newProductWithAdditionalBarcodesRows.length > 0 && (
            <Space direction="vertical" size={8} style={{ width: '100%', marginTop: 8 }}>
              <Alert
                type="warning"
                showIcon
                message={t('posAdmin.invoiceDetail.newProductMultiBarcodeTypeTitle', '检测到新商品包含副码')}
                description={t('posAdmin.invoiceDetail.newProductMultiBarcodeTypeDesc', '请选择主档商品类型；副码会写入总部多码关系和所有有效分店多码表。')}
              />
              {newProductWithAdditionalBarcodesRows.map((row) => (
                <div key={row.detailGuid} style={{ display: 'grid', gridTemplateColumns: '1fr auto', gap: 12, alignItems: 'center' }}>
                  <Space direction="vertical" size={2}>
                    <span>{row.itemNumber || '--'} · {row.productName || '--'}</span>
                    <Space size={4}>
                      <Tag>{row.barcode || '--'}</Tag>
                      <Tag color="cyan">
                        {t('posAdmin.invoiceDetail.additionalBarcodeCount', '副码 {{count}}', { count: row.additionalBarcodeCount })}
                      </Tag>
                    </Space>
                  </Space>
                  <Radio.Group
                    defaultValue={2}
                    optionType="button"
                    buttonStyle="solid"
                    onChange={(event) => {
                      newProductProductTypeSelectionMap.set(row.detailGuid, event.target.value as 1 | 2)
                    }}
                    options={[
                      { label: t('posAdmin.invoiceDetail.productTypeMultiCode', '多码'), value: 2 },
                      { label: t('posAdmin.invoiceDetail.productTypeSet', '套装'), value: 1 },
                    ]}
                  />
                </div>
              ))}
            </Space>
          )}
          {isCreateMode && (
            <div style={{ marginTop: 8 }}>
              <Checkbox
                defaultChecked={canWriteLocalPurchaseToHq}
                disabled={!canWriteLocalPurchaseToHq}
                onChange={(event) => {
                  hqChoice.syncToHq = event.target.checked
                }}
              >
                {t('posAdmin.invoiceWorkbench.syncToHq')}
              </Checkbox>
              <div className="lsi-wb-sync-hq-hint">
                {canWriteLocalPurchaseToHq
                  ? t('posAdmin.invoiceWorkbench.syncToHqCreateHint')
                  : t('posAdmin.invoiceWorkbench.syncToHqNoPermission')}
              </div>
            </div>
          )}
        </Space>
      ),
      okText: confirmText.okText,
      cancelText: confirmText.cancelText,
      okButtonProps: { danger: previewSnapshot.confirmedCreateProductCount > 0 },
      onOk: () => {
        void executeSelectedBatchActions(buildBatchExecuteSnapshot({
          selectedRowKeys: previewSnapshot.detailGuids,
          details,
          rowActions,
          // 关键位置：有副码的新商品必须在用户确认后带上主档类型，避免后台静默建成普通商品。
          newProductProductTypeSelections: newProductWithAdditionalBarcodesRows.map((row) => ({
            detailGuid: row.detailGuid,
            productType: newProductProductTypeSelectionMap.get(row.detailGuid) ?? 2,
          })),
          // 真正提交的确认时间在用户点击确认时生成。
          confirmedAt: new Date().toISOString(),
        }), { syncCreatedToHq: isCreateMode && hqChoice.syncToHq })
      },
    })
  }

  const handleExecuteAllPending = (mode: BatchExecuteMode) => {
    const guids = mode === 'createProducts' ? pendingExecutionSplit.createGuids : pendingExecutionSplit.otherGuids
    if (!guids.length) return
    runAfterUnsavedGuard(() => handleBatchExecute(guids, mode))
  }

  // ---- 删除选中 ----
  const handleDeleteSelected = async () => {
    if (!invoiceGuid || !ensureCanAccessInvoice()) return
    if (!selectedRowKeys.length) {
      message.warning(t('posAdmin.invoiceDetail.selectDeleteRows', '请先选择要删除的明细行'))
      return
    }
    setDetailLoading(true)
    try {
      await deleteDetails(invoiceGuid, selectedRowKeys.map(String))
      publishLocalSupplierInvoiceChanged(invoiceGuid)
      message.success(t('posAdmin.invoiceDetail.deleteSuccess', '删除成功'))
      setSelectedRowKeys([])
      loadDetails()
      loadInvoice()
    } catch {
      message.error(t('posAdmin.invoiceDetail.deleteFailed', '删除失败'))
    } finally {
      setDetailLoading(false)
    }
  }

  // ---- 批量设置操作类型 ----
  const handleBatchSetAction = async (actionKey: string) => {
    if (!invoiceGuid || !selectedRowKeys.length || !ensureCanAccessInvoice()) return
    const action = Number(actionKey)
    try {
      await batchUpdateDetailAction(invoiceGuid, selectedRowKeys.map(String), action)
      const newActions: Record<string, number> = {}
      selectedRowKeys.forEach((key) => {
        newActions[String(key)] = action
      })
      setRowActions((prev) => ({ ...prev, ...newActions }))
      setDetails((prev) =>
        prev.map((item) =>
          selectedRowKeys.map(String).includes(item.detailGUID)
            ? { ...item, activityType: action }
            : item,
        ),
      )
      message.success(t('posAdmin.invoiceDetail.batchSetActionSuccess', '批量设置操作类型成功'))
    } catch (error) {
      message.error(error instanceof Error ? error.message : t('posAdmin.invoiceDetail.batchSetActionFailed', '批量设置操作类型失败'))
    }
  }

  /* ================================================================ */
  /*  表格列定义                                                       */
  /* ================================================================ */

  const getTextColumnSearchProps = (
    field: TextFilterField,
    label: string,
  ): ColumnType<LocalSupplierInvoiceItemDto> => ({
    filteredValue: (columnFilteredValues[field] ?? null) as React.Key[] | null,
    filterDropdown: ({ setSelectedKeys, selectedKeys, confirm, clearFilters }) => (
      (() => {
        const current = parseTextColumnFilter(selectedKeys[0] ?? '')
        const setModel = (next: TextColumnFilterModel) => {
          if ((next.mode === 'empty' || next.mode === 'notEmpty') || String(next.value ?? '').trim()) {
            setSelectedKeys([serializeTextColumnFilter(next)])
          } else {
            setSelectedKeys([])
          }
        }
        const textModeOptions: Array<{ label: string; value: TextFilterMode }> = [
          { label: t('posAdmin.invoiceDetail.filterContains', '包含'), value: 'contains' },
          { label: t('posAdmin.invoiceDetail.filterEquals', '等于'), value: 'equals' },
          { label: t('posAdmin.invoiceDetail.filterStartsWith', '开头是'), value: 'startsWith' },
          { label: t('posAdmin.invoiceDetail.filterEndsWith', '结尾是'), value: 'endsWith' },
          { label: t('posAdmin.invoiceDetail.filterEmpty', '为空'), value: 'empty' },
          { label: t('posAdmin.invoiceDetail.filterNotEmpty', '非空'), value: 'notEmpty' },
        ]

        return (
          <div style={{ padding: 8 }} onKeyDown={(event) => event.stopPropagation()}>
            <Select<TextFilterMode>
              size="small"
              value={current.mode}
              options={textModeOptions}
              onChange={(mode) => setModel({ ...current, mode })}
              style={{ width: 180, marginBottom: 8, display: 'block' }}
            />
            {current.mode !== 'empty' && current.mode !== 'notEmpty' && (
              <Input
                autoFocus
                allowClear
                size="small"
                placeholder={t('posAdmin.invoiceDetail.columnSearchPlaceholder', '搜索{{label}}', { label })}
                value={String(current.value ?? '')}
                onChange={(event) => setModel({ ...current, value: event.target.value })}
                onPressEnter={() => confirm()}
                style={{ width: 180, marginBottom: 8, display: 'block' }}
              />
            )}
            <Space size={8}>
              <Button
                type="primary"
                size="small"
                icon={<SearchOutlined />}
                onClick={() => confirm()}
              >
                {t('common.search', '搜索')}
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
          </div>
        )
      })()
    ),
    filterIcon: (filtered) => (
      <SearchOutlined style={{ color: filtered ? '#1677ff' : undefined }} />
    ),
    onFilter: (value, record) => matchesTextColumnFilter(record, field, value),
  })

  const getNumberColumnFilterProps = (
    field: NumberFilterField,
    label: string,
  ): ColumnType<LocalSupplierInvoiceItemDto> => ({
    filteredValue: (columnFilteredValues[field] ?? null) as React.Key[] | null,
    filterDropdown: ({ setSelectedKeys, selectedKeys, confirm, clearFilters }) => (
      (() => {
        const current = parseNumberColumnFilter(selectedKeys[0] ?? '')
        const setModel = (next: ReturnType<typeof parseNumberColumnFilter>) => {
          if (
            next.mode === 'empty' ||
            next.mode === 'notEmpty' ||
            (next.mode === 'between' && (next.min !== undefined || next.max !== undefined)) ||
            (next.mode !== 'between' && next.value !== undefined)
          ) {
            setSelectedKeys([serializeNumberColumnFilter(next)])
          } else {
            setSelectedKeys([])
          }
        }
        const numberModeOptions: Array<{ label: string; value: NumberFilterMode }> = [
          { label: t('posAdmin.invoiceDetail.filterEquals', '等于'), value: 'equals' },
          { label: t('posAdmin.invoiceDetail.filterGreaterThan', '大于'), value: 'gt' },
          { label: t('posAdmin.invoiceDetail.filterGreaterThanOrEqual', '大于等于'), value: 'gte' },
          { label: t('posAdmin.invoiceDetail.filterLessThan', '小于'), value: 'lt' },
          { label: t('posAdmin.invoiceDetail.filterLessThanOrEqual', '小于等于'), value: 'lte' },
          { label: t('posAdmin.invoiceDetail.filterBetween', '区间'), value: 'between' },
          { label: t('posAdmin.invoiceDetail.filterEmpty', '为空'), value: 'empty' },
          { label: t('posAdmin.invoiceDetail.filterNotEmpty', '非空'), value: 'notEmpty' },
        ]

        return (
          <div style={{ padding: 8 }} onKeyDown={(event) => event.stopPropagation()}>
            <Select<NumberFilterMode>
              size="small"
              value={current.mode}
              options={numberModeOptions}
              onChange={(mode) => setModel({ ...current, mode })}
              style={{ width: 180, marginBottom: 8, display: 'block' }}
            />
            {current.mode === 'between' && (
              <Space size={6} style={{ marginBottom: 8 }}>
                <InputNumber
                  size="small"
                  placeholder={t('posAdmin.invoiceDetail.filterMin', '最小')}
                  value={current.min ?? null}
                  onChange={(value) => setModel({ ...current, min: value ?? undefined })}
                  style={{ width: 86 }}
                />
                <InputNumber
                  size="small"
                  placeholder={t('posAdmin.invoiceDetail.filterMax', '最大')}
                  value={current.max ?? null}
                  onChange={(value) => setModel({ ...current, max: value ?? undefined })}
                  style={{ width: 86 }}
                />
              </Space>
            )}
            {current.mode !== 'between' && current.mode !== 'empty' && current.mode !== 'notEmpty' && (
              <InputNumber
                autoFocus
                size="small"
                placeholder={t('posAdmin.invoiceDetail.columnNumberFilterPlaceholder', '过滤{{label}}', { label })}
                value={current.value ?? null}
                onChange={(value) => setModel({ ...current, value: value ?? undefined })}
                onPressEnter={() => confirm()}
                style={{ width: 180, marginBottom: 8, display: 'block' }}
              />
            )}
            <Space size={8}>
              <Button
                type="primary"
                size="small"
                icon={<SearchOutlined />}
                onClick={() => confirm()}
              >
                {t('common.search', '搜索')}
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
          </div>
        )
      })()
    ),
    filterIcon: (filtered) => (
      <SearchOutlined style={{ color: filtered ? '#1677ff' : undefined }} />
    ),
    onFilter: (value, record) => matchesNumberColumnFilter(record, field, value),
  })

  const handleTableChange: TableProps<LocalSupplierInvoiceItemDto>['onChange'] = (
    _pagination,
    filters,
    sorter,
    extra,
  ) => {
    const sorterInfo = Array.isArray(sorter) ? sorter[0] : sorter
    const sorterField = Array.isArray(sorterInfo?.field)
      ? sorterInfo.field.join('.')
      : sorterInfo?.field != null
        ? String(sorterInfo.field)
        : undefined
    setColumnFilteredValues(filters as Record<string, (React.Key | boolean)[] | null>)
    setInlineNavigationSort(sorterInfo?.order && sorterField ? { field: sorterField, order: sorterInfo.order } : null)
    // 选中行按过滤后可见数据收敛，避免隐藏明细继续参与批量执行或批量删除。
    setSelectedRowKeys((prev) => constrainSelectedRowKeysToVisibleDetails(prev, extra.currentDataSource))
  }

  const showBarcodeMatchedProducts = async (record: LocalSupplierInvoiceItemDto) => {
    if (!invoiceGuid || !record.barcode) {
      message.warning(t('posAdmin.invoiceDetail.noBarcodeToQuery', '当前行没有条码'))
      return
    }

    const barcode = record.barcode
    const modal = Modal.info({
      title: t('posAdmin.invoiceDetail.barcodeMatchedProductsTitle', '条码匹配商品：{{barcode}}', { barcode }),
      width: 920,
      okText: t('common.close', '关闭'),
      content: <div>{t('common.loading', '加载中...')}</div>,
    })

    const renderMatchedProductsContent = (
      matchedProducts: BarcodeAbnormalMatchedProductDto[],
      matchedProductColumns: ColumnsType<BarcodeAbnormalMatchedProductDto>,
    ) => matchedProducts.length ? (
      <MeasuredTable<BarcodeAbnormalMatchedProductDto> metricId="pos-admin.local-supplier-invoices.invoice-edit.table-1"
        size="small"
        rowKey={(item, index) => `${item.productCode || 'product'}-${item.barcode || barcode}-${index ?? 0}`}
        columns={matchedProductColumns}
        dataSource={matchedProducts}
        pagination={false}
        tableLayout="fixed"
        scroll={{ x: matchedProductTableScrollX, y: 320 }}
      />
    ) : (
      <div>{t('posAdmin.invoiceDetail.noBarcodeMatchedProducts', '没有匹配到商品')}</div>
    )

    try {
      const refreshMatchedProducts = async (
        matchedProductColumns: ColumnsType<BarcodeAbnormalMatchedProductDto>,
      ) => {
        const refreshed = await getProductsByBarcode(invoiceGuid, barcode)
        modal.update({
          content: renderMatchedProductsContent(refreshed?.matchedProducts ?? [], matchedProductColumns),
        })
      }

      const handleReplaceMatchedProductMaster = (
        matchedProduct: BarcodeAbnormalMatchedProductDto,
        matchedProductColumns: ColumnsType<BarcodeAbnormalMatchedProductDto>,
      ) => {
        const target = getMatchedProductMasterUpdateTarget(record, invoice)
        if (!target.itemNumber) {
          message.warning(t('posAdmin.invoiceDetail.replaceProductMasterMissingItemNumber', '当前明细缺少货号，无法更换'))
          return
        }
        if (!target.supplierCode) {
          message.warning(t('posAdmin.invoiceDetail.replaceProductMasterMissingSupplier', '当前明细缺少供应商，无法更换'))
          return
        }
        if (!matchedProduct.productCode) {
          message.warning(t('posAdmin.invoiceDetail.replaceProductMasterMissingProductCode', '匹配商品缺少商品编码，无法更换'))
          return
        }

        Modal.confirm({
          title: t('posAdmin.invoiceDetail.replaceProductMasterConfirmTitle', '确认更换匹配商品主档？'),
          content: (
            <Space direction="vertical" size={4}>
              <span>
                {t('posAdmin.invoiceDetail.replaceProductMasterSourceLine', '所选商品当前：货号 {{itemNumber}}，供应商 {{supplier}}', {
                  itemNumber: matchedProduct.itemNumber || '--',
                  supplier: matchedProduct.supplierName
                    ? `${matchedProduct.supplierCode || '--'} - ${matchedProduct.supplierName}`
                    : matchedProduct.supplierCode || '--',
                })}
              </span>
              <span>
                {t('posAdmin.invoiceDetail.replaceProductMasterTargetLine', '将写入当前明细：货号 {{itemNumber}}，供应商 {{supplier}}', {
                  itemNumber: target.itemNumber,
                  supplier: target.supplierCode,
                })}
              </span>
            </Space>
          ),
          okText: t('posAdmin.invoiceDetail.replaceProductMaster', '更换货号和供应商'),
          cancelText: t('common.cancel', '取消'),
          onOk: async () => {
            try {
              const fullProduct = await getProductById(matchedProduct.productCode)
              // 商品更新接口是完整 DTO 语义，这里先读取详情再覆盖目标字段，避免清空其它主档字段。
              const payload = buildMatchedProductMasterUpdatePayload(fullProduct, record, invoice)
              await updateProduct(matchedProduct.productCode, payload)
              message.success(t('posAdmin.invoiceDetail.replaceProductMasterSuccess', '商品主档已更新'))
              await refreshMatchedProducts(matchedProductColumns)
            } catch (error) {
              message.error(error instanceof Error ? error.message : t('posAdmin.invoiceDetail.replaceProductMasterFailed', '更换商品主档失败'))
              throw error
            }
          },
        })
      }

      const handleLinkMatchedProduct = (matchedProduct: BarcodeAbnormalMatchedProductDto) => {
        const productCode = matchedProduct.productCode?.trim()
        if (!productCode) {
          message.warning(t('posAdmin.invoiceDetail.linkProductMissingProductCode', '匹配商品缺少商品编码，无法选用'))
          return
        }
        // 选用会对本行重新检测并改写定价预览等字段，本行有未保存修改时先让用户保存，避免被覆盖。
        const currentDetail = details.find((item) => item.detailGUID === record.detailGUID) ?? record
        if (getEditedInvoiceDetailFields(currentDetail, detailSnapshotIndex).length > 0) {
          message.warning(t('posAdmin.invoiceDetail.linkProductSaveFirst', '本行有未保存的修改，请先保存再选用商品'))
          return
        }

        Modal.confirm({
          title: t('posAdmin.invoiceDetail.linkProductConfirmTitle', '选用该商品作为本行商品？'),
          content: (
            <Space direction="vertical" size={4}>
              <span>
                {t('posAdmin.invoiceDetail.linkProductTargetLine', '{{productName}}（货号 {{itemNumber}}）', {
                  productName: matchedProduct.productName || productCode,
                  itemNumber: matchedProduct.itemNumber || '--',
                })}
              </span>
              <span className="lsi-muted">
                {t('posAdmin.invoiceDetail.linkProductHint', '回填商品编码后本行按已有商品处理（默认更新进货价），商品主档的货号和供应商不变。')}
              </span>
            </Space>
          ),
          okText: t('posAdmin.invoiceDetail.linkProduct', '选用'),
          cancelText: t('common.cancel', '取消'),
          onOk: async () => {
            try {
              // 先写入商品编码，再只对本行重新检测：检测会认「已关联商品拥有本行条码」，由后端统一算出状态与默认操作。
              await batchUpsertDetails(invoiceGuid, [{ detailGUID: record.detailGUID, productCode }])
              const checkResponse = await checkProducts({ invoiceGuid, detailGuids: [record.detailGUID] })
              publishLocalSupplierInvoiceChanged(invoiceGuid)
              const checkResult = checkResponse.results.find((item) => item.detailGuid === record.detailGUID)
              if (checkResult) {
                // 检测结果已落库，同步到快照，避免定价预览等字段被误判为未保存修改。
                detailsSnapshotRef.current = detailsSnapshotRef.current.map((item) => (
                  item.detailGUID === record.detailGUID ? mergeProductCheckResult(item, checkResult) : item
                ))
                applyCheckProductsResponse(checkResponse)
              }
              if (!isProductLinkConfirmed(checkResult, productCode)) {
                message.warning(t('posAdmin.invoiceDetail.linkProductNotApplied', '该商品不拥有本行条码，未能关联'))
                return
              }
              message.success(t('posAdmin.invoiceDetail.linkProductSuccess', '已回填商品编码'))
              modal.destroy()
            } catch (error) {
              message.error(error instanceof Error ? error.message : t('posAdmin.invoiceDetail.linkProductFailed', '选用商品失败'))
              throw error
            }
          },
        })
      }

      const showLinkAction = canEditDetailRows && canLinkMatchedProduct(record)
      const result = await getProductsByBarcode(invoiceGuid, barcode)
      const matchedProducts = result?.matchedProducts ?? []
      const matchedProductColumns: ColumnsType<BarcodeAbnormalMatchedProductDto> = [
        {
          title: t('posAdmin.invoiceDetail.itemNumber', '货号'),
          dataIndex: 'itemNumber',
          width: 120,
          render: (value?: string) => value || '--',
        },
        {
          title: t('posAdmin.invoiceDetail.barcode', '条码'),
          dataIndex: 'barcode',
          width: 150,
          render: (value?: string) => value || '--',
        },
        {
          title: t('posAdmin.invoiceDetail.productName', '商品名称'),
          dataIndex: 'productName',
          width: 280,
          render: (value?: string) => (
            <div style={matchedProductNameCellStyle} title={value || undefined}>
              {value || '--'}
            </div>
          ),
        },
        {
          title: t('posAdmin.invoiceDetail.supplierName', '供应商名称'),
          dataIndex: 'supplierName',
          width: 150,
          render: (value?: string) => value || '--',
        },
        {
          title: t('posAdmin.invoiceDetail.matchSource', '来源'),
          dataIndex: 'isMultiCode',
          width: 100,
          render: (isMultiCode?: boolean) => (
            <Tag color={isMultiCode ? 'orange' : 'blue'} style={matchedProductTagStyle}>
              {isMultiCode
                ? t('posAdmin.invoiceDetail.multiBarcode', '分店多条码')
                : t('posAdmin.invoiceDetail.mainBarcode', '商品主条码')}
            </Tag>
          ),
        },
        ...(canManagePosProducts || showLinkAction ? [{
          title: t('posAdmin.invoiceDetail.action', '操作'),
          key: 'replaceProductMaster',
          width: 130,
          render: (_: unknown, matchedProduct: BarcodeAbnormalMatchedProductDto) => (
            <Space size={0}>
              {showLinkAction ? (
                <Tooltip title={t('posAdmin.invoiceDetail.linkProductTip', '本行就是这个商品：回填商品编码，不改主档')}>
                  <Button
                    size="small"
                    type="link"
                    style={matchedProductActionButtonStyle}
                    onClick={() => handleLinkMatchedProduct(matchedProduct)}
                  >
                    {t('posAdmin.invoiceDetail.linkProduct', '选用')}
                  </Button>
                </Tooltip>
              ) : null}
              {canManagePosProducts ? (
                <Tooltip title={t('posAdmin.invoiceDetail.replaceProductMaster', '更换货号和供应商')}>
                  <Button
                    size="small"
                    type="link"
                    style={matchedProductActionButtonStyle}
                    onClick={() => handleReplaceMatchedProductMaster(matchedProduct, matchedProductColumns)}
                  >
                    {t('posAdmin.invoiceDetail.replaceProductMasterShort', '更换')}
                  </Button>
                </Tooltip>
              ) : null}
            </Space>
          ),
        } satisfies ColumnType<BarcodeAbnormalMatchedProductDto>] : []),
      ]

      modal.update({
        content: renderMatchedProductsContent(matchedProducts, matchedProductColumns),
      })
    } catch {
      modal.destroy()
      message.error(t('posAdmin.invoiceDetail.queryBarcodeMatchedProductsFailed', '查询条码匹配商品失败'))
    }
  }

  // 改过但未保存的值用浅蓝底 + 圆点标出，与页头「n 行明细未保存」对应。
  const renderEditedValue = (
    record: LocalSupplierInvoiceItemDto,
    field: InvoiceDetailInlineEditableField,
    content: ReactNode,
  ) => (
    canEditDetailRows && isInvoiceDetailFieldEdited(record, detailSnapshotIndex, field)
      ? <span className="lsi-wb-edited">{content}</span>
      : renderNumericCell(content)
  )

  const renderPurchasePriceChange = (record: LocalSupplierInvoiceItemDto) => {
    const change = getPurchasePriceChange(record.lastPurchasePrice, record.purchasePrice)
    if (change.kind === 'noPrice') {
      return <span className="lsi-wb-delta lsi-muted">{t('posAdmin.invoiceWorkbench.noPurchasePrice')}</span>
    }
    if (change.kind === 'noHistory') {
      return <span className="lsi-wb-delta lsi-muted">{t('posAdmin.invoiceWorkbench.noHistory')}</span>
    }
    if (change.kind === 'same') {
      return (
        <span className="lsi-wb-delta lsi-muted">
          {t('posAdmin.invoiceWorkbench.samePrice', { price: formatAmount(change.last) })}
        </span>
      )
    }
    return (
      <span
        className={change.className}
        title={`${t('posAdmin.invoiceDetail.lastPurchasePrice', '上次进货价')}：${formatAmount(change.last)}`}
      >
        {change.kind === 'up' ? '↑' : '↓'}{change.percent.toFixed(1)}% · {formatAmount(change.last)}
      </span>
    )
  }

  const renderPricingSummary = (record: LocalSupplierInvoiceItemDto) => {
    const pricingEdited = (['autoPricing', 'pricingFloatRate', 'newAutoRetailPrice', 'isSpecialProduct', 'discountRate'] as const)
      .some((field) => canEditDetailRows && isInvoiceDetailFieldEdited(record, detailSnapshotIndex, field))
    const modeText = record.autoPricing == null
      ? '--'
      : record.autoPricing
        ? (record.pricingFloatRate != null
          ? t('posAdmin.invoiceWorkbench.pricingAuto', { rate: formatPricingFloatRate(record.pricingFloatRate) })
          : t('posAdmin.invoiceWorkbench.pricingAutoNoRate'))
        : t('posAdmin.invoiceWorkbench.pricingManual')
    const hasDiscount = record.discountRate != null && record.discountRate !== 0
    return (
      <>
        <span className="lsi-wb-pricing-line">
          <span className={pricingEdited ? 'lsi-wb-edited' : undefined}>{modeText}</span>
          {record.isSpecialProduct ? (
            <span className="lsi-tag lsi-tag-orange">{t('posAdmin.invoiceWorkbench.pricingSpecial')}</span>
          ) : null}
          {hasDiscount ? (
            <span className="lsi-tag lsi-tag-neutral">
              {t('posAdmin.invoiceWorkbench.pricingDiscount', { rate: formatDiscountRate(record.discountRate) })}
            </span>
          ) : null}
        </span>
        <span className="lsi-wb-sub lsi-num">
          {record.newAutoRetailPrice != null
            ? t('posAdmin.invoiceWorkbench.pricingNew', { price: formatAmount(record.newAutoRetailPrice) })
            : ''}
        </span>
      </>
    )
  }

  const renderHqStatus = (record: LocalSupplierInvoiceItemDto) => {
    const bucket = getDetailProgressBucket(record, rowActions)
    const statusMap: Record<DetailProgressBucket, { label: string; className: string }> = {
      executed: { label: t('posAdmin.invoiceWorkbench.hqDone'), className: 'lsi-wb-hq-status lsi-wb-match-ok' },
      pending: { label: t('posAdmin.invoiceWorkbench.hqPending'), className: 'lsi-wb-hq-status lsi-price-down' },
      waiting: { label: t('posAdmin.invoiceWorkbench.hqWaiting'), className: 'lsi-wb-hq-status lsi-wb-match-warn' },
      unchecked: { label: t('posAdmin.invoiceWorkbench.hqUnchecked'), className: 'lsi-wb-hq-status lsi-muted' },
      none: { label: t('posAdmin.invoiceWorkbench.hqNone'), className: 'lsi-wb-hq-status lsi-muted' },
    }
    const status = statusMap[bucket]
    return <span className={status.className}>{status.label}</span>
  }

  const columns: ColumnsType<LocalSupplierInvoiceItemDto> = [
    {
      title: t('posAdmin.invoiceWorkbench.colSeq'),
      key: 'seq',
      width: 44,
      align: 'center',
      fixed: 'left',
      render: (_, __, index) => <span className="lsi-num lsi-muted">{index + 1}</span>,
    },
    {
      // 商品列合并原来的图片/货号/条码/名称/商品类型 5 列；不设宽度，屏幕更宽时多出的空间只给它。
      title: (
        <span>
          {t('posAdmin.invoiceWorkbench.colProduct')}
          <span className="lsi-muted" style={{ fontWeight: 400, marginLeft: 6 }}>
            {t('posAdmin.invoiceWorkbench.colProductHint')}
          </span>
        </span>
      ),
      dataIndex: 'productName',
      key: 'productName',
      sorter: (a, b) => compareNullableText(a.productName, b.productName),
      ...getTextColumnSearchProps('productName', t('posAdmin.invoiceDetail.productName', '商品名称')),
      render: (v: string, record) => {
        const additionalBarcodeCount = record.additionalBarcodes?.length ?? 0
        const productType = record.productType
        // 仅显示主档返回的类型，不能按订单操作或副码数量推断；单品不加标记以减少噪音。
        const productTypeTag = productType === 1
          ? <span className="lsi-tag lsi-tag-blue">{t('posAdmin.products.setProduct', '套装')}</span>
          : productType === 2
            ? <span className="lsi-tag lsi-tag-purple">{t('posAdmin.products.multiCodeProductShort', '多码')}</span>
            : null
        return (
          <div className="lsi-wb-product">
            {record.productImage ? (
              <Image src={record.productImage} width={36} height={36} style={{ objectFit: 'cover', borderRadius: 6 }} />
            ) : (
              <span className="lsi-wb-thumb" aria-label={t('posAdmin.invoiceDetail.noImage', '无图')}>
                <PictureOutlined />
              </span>
            )}
            <div className="lsi-wb-cell" style={{ flex: '1 1 auto' }}>
              <div className="lsi-wb-product-name">
                <EditableTextCell
                  value={v}
                  detailGuid={record.detailGUID}
                  field="productName"
                  onSave={handleInlineDetailSave}
                  readOnly={!canEditDetailRows}
                  display={<span title={v || undefined}>{renderEditedValue(record, 'productName', v || '--')}</span>}
                />
                {productTypeTag}
                {additionalBarcodeCount > 0 && (
                  <Tooltip title={record.additionalBarcodes?.join(', ')}>
                    <span className="lsi-tag lsi-tag-cyan">
                      {t('posAdmin.invoiceDetail.additionalBarcodeCount', '副码 {{count}}', { count: additionalBarcodeCount })}
                    </span>
                  </Tooltip>
                )}
                {record.isCreatedByThisInvoice && (
                  <Tooltip title={t('posAdmin.invoiceWorkbench.createdHereTip')}>
                    <span className="lsi-tag lsi-tag-here">{t('posAdmin.invoiceWorkbench.statusCreatedHere')}</span>
                  </Tooltip>
                )}
                {!canEditInvoice && getProductStatusFilter(record) === 'notExists' && (
                  <Tooltip title={t('posAdmin.invoiceWorkbench.notExistsStoreTip')}>
                    <span className="lsi-tag lsi-tag-new">{t('posAdmin.invoiceWorkbench.chipNotExistsStore')}</span>
                  </Tooltip>
                )}
              </div>
              <div className="lsi-wb-product-codes">
                <EditableTextCell
                  value={record.itemNumber}
                  detailGuid={record.detailGUID}
                  field="itemNumber"
                  onSave={handleInlineDetailSave}
                  readOnly={!canEditDetailRows}
                  display={renderEditedValue(record, 'itemNumber', record.itemNumber || '--')}
                />
                <span aria-hidden="true">·</span>
                <EditableTextCell
                  value={record.barcode}
                  detailGuid={record.detailGUID}
                  field="barcode"
                  onSave={handleInlineDetailSave}
                  readOnly={!canEditDetailRows}
                  display={renderEditedValue(record, 'barcode', record.barcode || '--')}
                />
                {record.itemNumber && (
                  <Tooltip title={t('posAdmin.invoiceDetail.copyItemNumber', '复制货号')}>
                    <Button
                      className="lsi-copy-button"
                      type="text"
                      size="small"
                      icon={<CopyOutlined />}
                      aria-label={t('posAdmin.invoiceDetail.copyItemNumber', '复制货号')}
                      onDoubleClick={(event) => event.stopPropagation()}
                      onClick={(event) => {
                        event.stopPropagation()
                        void copyTextToClipboard(record.itemNumber!)
                      }}
                    />
                  </Tooltip>
                )}
                {record.barcode && (
                  <Popover
                    trigger="click"
                    content={<BarcodePreview value={record.barcode} compactCopy />}
                  >
                    <Button
                      className="lsi-copy-button"
                      type="text"
                      size="small"
                      icon={<BarcodeOutlined />}
                      aria-label={t('posAdmin.invoiceWorkbench.showBarcode')}
                      onDoubleClick={(event) => event.stopPropagation()}
                    />
                  </Popover>
                )}
              </div>
            </div>
          </div>
        )
      },
    },
    {
      title: t('posAdmin.invoiceWorkbench.colQuantity'),
      dataIndex: 'quantity',
      key: 'quantity',
      width: 80,
      align: 'right',
      sorter: (a, b) => compareNullableNumbers(a.quantity, b.quantity),
      ...getNumberColumnFilterProps('quantity', t('posAdmin.invoiceDetail.quantity', '数量')),
      render: (v: number, record) => (
        <EditableNumberCell
          value={v}
          detailGuid={record.detailGUID}
          field="quantity"
          onSave={handleInlineDetailSave}
          readOnly={!canEditDetailRows}
          precision={0}
          displayValue={renderEditedValue(record, 'quantity', formatQuantity(v))}
        />
      ),
    },
    {
      title: (
        <span>
          {t('posAdmin.invoiceWorkbench.colPurchasePrice')}
          <span className="lsi-muted" style={{ fontWeight: 400, marginLeft: 4 }}>
            {t('posAdmin.invoiceWorkbench.colPurchaseHint')}
          </span>
        </span>
      ),
      dataIndex: 'purchasePrice',
      key: 'purchasePrice',
      width: 124,
      align: 'right',
      sorter: (a, b) => compareNullableNumbers(a.purchasePrice, b.purchasePrice),
      ...getNumberColumnFilterProps('purchasePrice', t('posAdmin.invoiceDetail.currentPurchasePrice', '本次进货价')),
      render: (v: number, record) => (
        <div className="lsi-wb-cell lsi-wb-cell-end">
          <EditableNumberCell
            value={v}
            detailGuid={record.detailGUID}
            field="purchasePrice"
            onSave={handleInlineDetailSave}
            readOnly={!canEditDetailRows}
            displayValue={renderEditedValue(record, 'purchasePrice', formatAmount(v))}
          />
          {renderPurchasePriceChange(record)}
        </div>
      ),
    },
    {
      title: t('posAdmin.invoiceWorkbench.colAmount'),
      dataIndex: 'amount',
      key: 'amount',
      width: 92,
      align: 'right',
      sorter: (a, b) => compareNullableNumbers(a.amount, b.amount),
      ...getNumberColumnFilterProps('amount', t('posAdmin.invoiceDetail.amount', '金额')),
      render: (v: number) => renderNumericCell(formatAmount(v)),
    },
    {
      title: t('posAdmin.invoiceWorkbench.colRetailPrice'),
      dataIndex: 'retailPrice',
      key: 'retailPrice',
      width: 92,
      align: 'right',
      sorter: (a, b) => compareNullableNumbers(a.retailPrice, b.retailPrice),
      ...getNumberColumnFilterProps('retailPrice', t('posAdmin.invoiceDetail.retailPrice', '零售价')),
      render: (v: number, record) => (
        <EditableNumberCell
          value={v}
          detailGuid={record.detailGUID}
          field="retailPrice"
          onSave={handleInlineDetailSave}
          readOnly={!canEditDetailRows}
          active={isInlineNumberEditActive(record.detailGUID, 'retailPrice')}
          onActivate={() => activateInlineNumberEdit(record.detailGUID, 'retailPrice')}
          onDeactivate={() => deactivateInlineNumberEdit(record.detailGUID, 'retailPrice')}
          onNavigate={handleInlineNumberNavigate}
          // 零售价列较窄，编辑态使用紧凑输入框，避免撑开单元格；↑↓ 可连续录入相邻行。
          inputWidth={COMPACT_NUMBER_INPUT_WIDTH}
          controls={false}
          displayValue={renderEditedValue(record, 'retailPrice', formatAmount(v))}
        />
      ),
    },
    {
      // 定价列合并自动定价、定价浮率、新自动零售价、特殊商品、折扣率；点击打开定价弹窗统一修改。
      title: t('posAdmin.invoiceWorkbench.colPricing'),
      dataIndex: 'autoPricing',
      key: 'autoPricing',
      width: 140,
      filters: [
        { text: t('posAdmin.invoiceDetail.auto', '自动'), value: true },
        { text: t('posAdmin.invoiceDetail.manual', '手动'), value: false },
      ],
      filteredValue: (columnFilteredValues.autoPricing ?? null) as React.Key[] | null,
      onFilter: (value, record) => filterBooleanColumn(record.autoPricing, value),
      render: (_: boolean, record) => {
        if (!canEditDetailRows) {
          return <div className="lsi-wb-pricing">{renderPricingSummary(record)}</div>
        }
        const editorOpen = pricingEditorDetailGuid === record.detailGUID
        return (
          <Popover
            trigger="click"
            placement="bottomLeft"
            open={editorOpen}
            onOpenChange={(open) => setPricingEditorDetailGuid(open ? record.detailGUID : null)}
            title={t('posAdmin.invoiceWorkbench.pricingEditorTitle')}
            content={editorOpen ? (
              <PricingEditor
                detail={record}
                onApply={(changes) => handlePricingApply(record.detailGUID, changes)}
                onCancel={() => setPricingEditorDetailGuid(null)}
              />
            ) : null}
          >
            <button type="button" className="lsi-wb-pricing" title={t('posAdmin.invoiceWorkbench.pricingEditTip')}>
              {renderPricingSummary(record)}
            </button>
          </Popover>
        )
      },
    },
    ...(canEditInvoice ? [
      {
        // 商品匹配列合并商品状态与条码状态；条码状态可点开查看匹配商品（并可更换主档）。
        title: t('posAdmin.invoiceWorkbench.colMatch'),
        key: 'match',
        width: 128,
        render: (_: unknown, record: LocalSupplierInvoiceItemDto) => {
          const productStatus = getProductStatusFilter(record)
          const barcodeStatus = getBarcodeStatusFilter(record)
          const openMatchedProducts = (event: ReactMouseEvent) => {
            event.stopPropagation()
            void showBarcodeMatchedProducts(record)
          }
          const productLine = productStatus === 'notDetected'
            ? <span className="lsi-muted">{t('posAdmin.invoiceWorkbench.matchUnchecked')}</span>
            : productStatus === 'createdHere'
              ? <span className="lsi-wb-match-ok">{t('posAdmin.invoiceWorkbench.matchCreatedHere')}</span>
            : productStatus === 'exists'
              ? (
                <span className="lsi-wb-match-ok">
                  {(record.existingProductCount ?? 0) > 1
                    ? t('posAdmin.invoiceWorkbench.matchExistsCount', { count: record.existingProductCount })
                    : t('posAdmin.invoiceWorkbench.matchExists')}
                </span>
              )
              : <span className="lsi-wb-match-new">{t('posAdmin.invoiceWorkbench.matchNotExists')}</span>
          const barcodeLabel = barcodeStatus === 'normal'
            ? <span className="lsi-muted">{t('posAdmin.invoiceWorkbench.barcodeNormal')}</span>
            : barcodeStatus === 'noMatch'
              ? <span className="lsi-wb-match-bad">{t('posAdmin.invoiceWorkbench.barcodeNoMatch')}</span>
              : <span className="lsi-wb-match-warn">{t('posAdmin.invoiceWorkbench.barcodeMultiMatch', { count: record.barcodeMatchCount ?? 0 })}</span>
          return (
            <div className="lsi-wb-cell">
              {productLine}
              {barcodeStatus === 'notDetected' ? (
                <span className="lsi-wb-sub">{t('posAdmin.invoiceWorkbench.barcodeUnchecked')}</span>
              ) : (
                <Tooltip title={t('posAdmin.invoiceWorkbench.viewMatches')}>
                  <Button type="link" size="small" className="lsi-wb-match-link" onClick={openMatchedProducts}>
                    {barcodeLabel}
                  </Button>
                </Tooltip>
              )}
            </div>
          )
        },
      } satisfies ColumnType<LocalSupplierInvoiceItemDto>,
      {
        title: t('posAdmin.invoiceWorkbench.colAction'),
        key: 'action',
        width: isAdmin ? 144 : 112,
        fixed: 'right',
        render: (_: unknown, record: LocalSupplierInvoiceItemDto) => {
          const maintenanceProductCode = record.productCode?.trim()
          const currentAction = rowActions[record.detailGUID] ?? record.activityType ?? 0
          const actionConfig = DETAIL_ACTION_CONFIG(t)
          const config = actionConfig[currentAction] || actionConfig[0]
          const actionSelector = isAdmin ? (
            <Dropdown
              menu={{
                items: ACTION_MENU_ITEMS(t),
                onClick: ({ key }) => void handleRowActionChange(record.detailGUID, key),
                selectedKeys: [String(currentAction)],
              }}
              trigger={['click']}
            >
              <button type="button" className={`lsi-wb-action lsi-tag ${config.className}`}>
                {config.label}
                <DownOutlined style={{ fontSize: 9 }} />
              </button>
            </Dropdown>
          ) : (
            <span className={`lsi-wb-action lsi-wb-action-static lsi-tag ${config.className}`}>{config.label}</span>
          )

          return (
            <Space size={2}>
              {actionSelector}
              {isAdmin ? (
                // 多码/套装维护收进行尾菜单，省出宽度给商品匹配列；未检测或未匹配商品时禁用。
                <Dropdown
                  trigger={['click']}
                  menu={{
                    items: [
                      {
                        key: 'setCodeMaintenance',
                        label: maintenanceProductCode
                          ? t('posAdmin.invoiceDetail.setCodeMaintenanceShort', '多码/套装')
                          : t('posAdmin.invoiceDetail.setCodeMaintenanceNeedsProduct', '请先检测并匹配商品'),
                        disabled: !maintenanceProductCode,
                      },
                    ],
                    onClick: ({ key, domEvent }) => {
                      domEvent.stopPropagation()
                      if (key === 'setCodeMaintenance' && maintenanceProductCode) {
                        setSetCodeMaintenanceTarget({ ...record, productCode: maintenanceProductCode })
                      }
                    },
                  }}
                >
                  <Button
                    type="text"
                    size="small"
                    icon={<MoreOutlined />}
                    aria-label={t('posAdmin.invoiceDetail.setCodeMaintenanceTooltip', '维护该商品的多条码或套装条码')}
                  />
                </Dropdown>
              ) : null}
            </Space>
          )
        },
      } satisfies ColumnType<LocalSupplierInvoiceItemDto>,
    ] : [
      {
        // 只读视图：用一列总部处理状态代替商品匹配与操作两列。
        title: t('posAdmin.invoiceWorkbench.colHqStatus'),
        key: 'hqStatus',
        width: 112,
        render: (_: unknown, record: LocalSupplierInvoiceItemDto) => renderHqStatus(record),
      } satisfies ColumnType<LocalSupplierInvoiceItemDto>,
    ]),
  ]
  // 表格最小宽度 = 定宽列之和 + 商品列最小宽度 + 勾选列；更宽的屏幕把多余空间留给商品列。
  const detailTableScrollX = columns.reduce(
    (sum, column) => sum + (typeof column.width === 'number' ? column.width : 236),
    canSelectRows ? 36 : 0,
  )

  // 底部合计按当前可见行（外层筛选 + 列头筛选）计算。
  const visibleDetailTotals = useMemo(
    () => inlineNavigationDetails.reduce(
      (totals, detail) => ({
        quantity: totals.quantity + (detail.quantity ?? 0),
        amount: totals.amount + (detail.amount ?? 0),
      }),
      { quantity: 0, amount: 0 },
    ),
    [inlineNavigationDetails],
  )
  const isDetailFiltered = inlineNavigationDetails.length !== details.length

  const runningTaskLabels = [
    checking && t('posAdmin.invoiceWorkbench.taskCheck'),
    pasteLoading && t('posAdmin.invoiceWorkbench.taskPaste'),
    storePriceLoading && t('posAdmin.invoiceWorkbench.taskStorePrice'),
    hqUpdateLoading && t('posAdmin.invoiceWorkbench.taskHq'),
    executing && t('posAdmin.invoiceWorkbench.taskExecute'),
    updatingLastPurchasePrices && t('posAdmin.invoiceWorkbench.taskLastPrice'),
  ].filter((label): label is string => Boolean(label))

  const pendingBreakdown = [
    progressStats.pendingByAction[DetailActionEnum.CreateProduct]
      ? t('posAdmin.invoiceWorkbench.pendingCreate', { count: progressStats.pendingByAction[DetailActionEnum.CreateProduct] })
      : '',
    progressStats.pendingByAction[DetailActionEnum.UpdatePurchasePrice]
      ? t('posAdmin.invoiceWorkbench.pendingUpdatePrice', { count: progressStats.pendingByAction[DetailActionEnum.UpdatePurchasePrice] })
      : '',
    progressStats.pendingByAction[DetailActionEnum.UpdateItemNumber]
      ? t('posAdmin.invoiceWorkbench.pendingItemNumber', { count: progressStats.pendingByAction[DetailActionEnum.UpdateItemNumber] })
      : '',
    progressStats.pendingByAction[DetailActionEnum.AddMultiCode]
      ? t('posAdmin.invoiceWorkbench.pendingMultiCode', { count: progressStats.pendingByAction[DetailActionEnum.AddMultiCode] })
      : '',
  ].filter(Boolean).join(' · ')

  const moreFilterCount = [
    productTypeFilter !== 'all',
    productStatusFilter !== 'all',
    barcodeStatusFilter !== 'all',
    actionTypeFilter !== 'all',
    specialProductFilter !== 'all',
  ].filter(Boolean).length

  const withCount = (label: string, count: number) => `${label} ${count}`

  const moreFiltersContent = (
    <div className="lsi-wb-filter-panel">
      <span>{t('posAdmin.invoiceWorkbench.filterProductType')}</span>
      <Select<'all' | 'unknown' | 0 | 1 | 2>
        size="small"
        value={productTypeFilter}
        onChange={setProductTypeFilter}
        options={[
          { value: 'all', label: withCount(t('posAdmin.invoiceWorkbench.filterAll'), details.length) },
          ...productTypeStats
            .filter((option) => option.value !== 'unknown' || option.count > 0)
            .map((option) => ({ value: option.value, label: withCount(option.label, option.count) })),
        ]}
      />
      <span>{t('posAdmin.invoiceWorkbench.filterProductStatus')}</span>
      <Select<StatusFilterValue<ProductStatusFilter>>
        size="small"
        value={productStatusFilter}
        onChange={setProductStatusFilter}
        options={[
          { value: 'all', label: withCount(t('posAdmin.invoiceWorkbench.filterAll'), details.length) },
          ...(['notDetected', 'exists', 'notExists', 'createdHere'] as const).map((value) => ({
            value,
            label: withCount(productStatusFilterLabels[value], detailStatusStats.product[value]),
          })),
        ]}
      />
      <span>{t('posAdmin.invoiceWorkbench.filterBarcodeStatus')}</span>
      <Select<BarcodeStatusFilterValue>
        size="small"
        value={barcodeStatusFilter}
        onChange={setBarcodeStatusFilter}
        options={[
          { value: 'all', label: withCount(t('posAdmin.invoiceWorkbench.filterAll'), details.length) },
          ...(['notDetected', 'normal', 'noMatch', 'multiMatch'] as const).map((value) => ({
            value,
            label: withCount(barcodeStatusFilterLabels[value], detailStatusStats.barcode[value]),
          })),
          {
            value: 'abnormal',
            label: withCount(barcodeStatusFilterLabels.abnormal, detailStatusStats.barcode.noMatch + detailStatusStats.barcode.multiMatch),
          },
        ]}
      />
      <span>{t('posAdmin.invoiceWorkbench.filterActionType')}</span>
      <Select<ActionTypeFilterValue>
        size="small"
        value={actionTypeFilter}
        onChange={setActionTypeFilter}
        options={[
          { value: 'all', label: withCount(t('posAdmin.invoiceWorkbench.filterAll'), details.length) },
          ...actionTypeFilters.map((actionType) => ({
            value: actionType,
            label: withCount(
              (detailActionConfig[actionType] ?? detailActionConfig[DetailActionEnum.None]).label,
              detailStatusStats.action[actionType],
            ),
          })),
        ]}
      />
      <span>{t('posAdmin.invoiceWorkbench.filterSpecial')}</span>
      <Select<'all' | 'yes' | 'no'>
        size="small"
        value={specialProductFilter}
        onChange={setSpecialProductFilter}
        options={[
          { value: 'all', label: t('posAdmin.invoiceWorkbench.filterAll') },
          { value: 'yes', label: t('posAdmin.invoiceDetail.yes', '是') },
          { value: 'no', label: t('posAdmin.invoiceDetail.no', '否') },
        ]}
      />
    </div>
  )

  const renderQuickChip = (key: string, label: string, count: number, active: boolean, onToggle: () => void) => (
    <Button
      key={key}
      size="small"
      className="lsi-wb-chip"
      type={active ? 'primary' : 'default'}
      ghost={active}
      aria-pressed={active}
      onClick={onToggle}
    >
      {label}
      <span className="lsi-wb-chip-count">{count}</span>
    </Button>
  )

  const executedPercent = getExecutedPercent(progressStats)
  const selectedCount = selectedRowKeys.length
  const headerStoreText = invoice?.storeCode
    ? `${invoice.storeCode}${invoice.storeName ? ` ${invoice.storeName}` : ''}`
    : '--'
  const headerSupplierText = invoice?.supplierCode
    ? `${invoice.supplierCode}${invoice.supplierName ? ` ${invoice.supplierName}` : ''}`
    : '--'
  const flowStatusInfo = FLOW_STATUS_LABELS[invoice?.flowStatus ?? 0] ?? FLOW_STATUS_LABELS[0]
  const inboundStatusLabelKey = INBOUND_STATUS_LABEL_KEYS[invoice?.inboundStatus ?? 0] ?? INBOUND_STATUS_LABEL_KEYS[0]

  /* ================================================================ */
  /*  渲染                                                             */
  /* ================================================================ */

  return (
    <div className="lsi-wb">
      {/* ============================================================ */}
      {/* 页头：单号、状态、后台任务、统一保存 + 表头字段                    */}
      {/* ============================================================ */}
      <section className="lsi-wb-card lsi-wb-header" aria-busy={loading}>
        <div className="lsi-wb-titlebar">
          <Tooltip title={t('posAdmin.invoiceWorkbench.backToList')}>
            <Button
              type="text"
              icon={<ArrowLeftOutlined />}
              aria-label={t('posAdmin.invoiceWorkbench.backToList')}
              onClick={handleBackToList}
              style={{ marginLeft: -8 }}
            />
          </Tooltip>
          <h1 className="lsi-wb-title">{invoice?.invoiceNo || '--'}</h1>
          {invoice?.invoiceNo && (
            <Tooltip title={t('posAdmin.invoiceWorkbench.copyInvoiceNo')}>
              <Button
                type="text"
                size="small"
                icon={<CopyOutlined />}
                aria-label={t('posAdmin.invoiceWorkbench.copyInvoiceNo')}
                onClick={() => void copyTextToClipboard(invoice.invoiceNo!)}
              />
            </Tooltip>
          )}
          {invoice && <span className={flowStatusInfo.className}>{t(flowStatusInfo.labelKey)}</span>}
          {invoice && <span className="lsi-muted" style={{ fontSize: 12 }}>{t(inboundStatusLabelKey)}</span>}
          {!canEditInvoice && invoice && details.length > 0 && (
            <span className="lsi-wb-mini-progress">
              <span className="lsi-wb-mini-progress-track" aria-hidden="true">
                <span className="lsi-wb-mini-progress-fill" style={{ width: `${executedPercent}%` }} />
              </span>
              {t('posAdmin.invoiceWorkbench.hqProgress', { executed: progressStats.executed, total: progressStats.total })}
            </span>
          )}
          <span className="lsi-wb-spacer" />
          {runningTaskLabels.length > 0 && (
            <span className="lsi-wb-task" role="status">
              <LoadingOutlined />
              {t('posAdmin.invoiceWorkbench.runningTasks', { tasks: runningTaskLabels.join('、') })}
            </span>
          )}
          {invoiceGuid && (
            <Button
              icon={<BarChartOutlined />}
              onClick={() => navigate(`/pos-admin/local-supplier-invoices/${invoiceGuid}/sales-analysis`)}
            >
              {t('posAdmin.invoiceWorkbench.salesAnalysis')}
            </Button>
          )}
          {canEditInvoice && hasUnsavedChanges && (
            <span className="lsi-wb-unsaved" role="status">{unsavedSummary}</span>
          )}
          {canEditInvoice && (
            <Button
              type="primary"
              loading={saving || savingAll}
              disabled={!hasUnsavedChanges}
              onClick={() => void handleSaveAll()}
            >
              {t('posAdmin.invoiceWorkbench.saveChanges')}
            </Button>
          )}
        </div>

        {loading && !invoice ? <Skeleton active paragraph={{ rows: 1 }} title={false} /> : null}
        {/* 表单始终挂载（首次加载时只隐藏），loadInvoice 写入的值不会因为表单实例未连接而丢失。 */}
        <Form
          form={form}
          layout="vertical"
          className="lsi-wb-fields"
          requiredMark={false}
          style={loading && !invoice ? { display: 'none' } : undefined}
        >
          {canEditInvoice ? (
            <>
              <Form.Item
                name="storeCode"
                label={t('posAdmin.invoiceWorkbench.fieldStore')}
                rules={[{ required: true, message: t('posAdmin.invoiceDetail.storeRequired', '请选择分店') }]}
              >
                <Select
                  showSearch
                  variant="filled"
                  loading={storeOptionsLoading}
                  options={headerStoreOptions}
                  optionFilterProp="label"
                  placeholder={t('posAdmin.invoiceDetail.storePlaceholder', '请选择分店')}
                />
              </Form.Item>
              <Form.Item
                name="supplierCode"
                label={t('posAdmin.invoiceWorkbench.fieldSupplier')}
                rules={[{ required: true, message: t('posAdmin.invoiceDetail.supplierRequired', '请选择供应商') }]}
              >
                <Select
                  showSearch
                  variant="filled"
                  loading={supplierOptionsLoading}
                  options={headerSupplierOptions}
                  optionFilterProp="label"
                  placeholder={t('posAdmin.invoiceDetail.supplierPlaceholder', '请选择供应商')}
                />
              </Form.Item>
              <Form.Item name="orderDate" label={t('posAdmin.invoiceWorkbench.fieldOrderDate')}>
                <DatePicker variant="filled" style={{ width: '100%' }} />
              </Form.Item>
              <Form.Item name="inboundDate" label={t('posAdmin.invoiceWorkbench.fieldInboundDate')}>
                <DatePicker
                  variant="filled"
                  style={{ width: '100%' }}
                  placeholder={t('posAdmin.invoiceWorkbench.notFilled')}
                />
              </Form.Item>
              <Form.Item name="remarks" label={t('posAdmin.invoiceWorkbench.fieldRemarks')}>
                <Input variant="filled" placeholder={t('posAdmin.invoiceWorkbench.notFilled')} />
              </Form.Item>
            </>
          ) : (
            <>
              <Form.Item label={t('posAdmin.invoiceWorkbench.fieldStore')}>
                <div className="lsi-wb-readonly-value">{headerStoreText}</div>
              </Form.Item>
              <Form.Item label={t('posAdmin.invoiceWorkbench.fieldSupplier')}>
                <div className="lsi-wb-readonly-value">{headerSupplierText}</div>
              </Form.Item>
              <Form.Item label={t('posAdmin.invoiceWorkbench.fieldOrderDate')}>
                <div className="lsi-wb-readonly-value lsi-num">{invoice?.orderDate?.slice(0, 10) || '--'}</div>
              </Form.Item>
              <Form.Item label={t('posAdmin.invoiceWorkbench.fieldInboundDate')}>
                <div className="lsi-wb-readonly-value lsi-num">
                  {invoice?.inboundDate?.slice(0, 10) || t('posAdmin.invoiceWorkbench.notFilled')}
                </div>
              </Form.Item>
              <Form.Item label={t('posAdmin.invoiceWorkbench.fieldRemarks')}>
                <div className="lsi-wb-readonly-value" title={invoice?.remarks || undefined}>{invoice?.remarks || '--'}</div>
              </Form.Item>
            </>
          )}
          <Form.Item label={t('posAdmin.invoiceWorkbench.fieldTotalAmount')}>
            <div className="lsi-wb-readonly-value lsi-num" style={{ fontWeight: 600, fontSize: 15 }}>
              {formatAmount(invoice?.totalAmount)}
            </div>
          </Form.Item>
          <Form.Item label={t('posAdmin.invoiceWorkbench.fieldAudit')}>
            <div
              className="lsi-wb-readonly-value lsi-num lsi-muted"
              title={`${formatLocalSupplierInvoiceAuditTime(invoice?.createdAt)} · ${formatLocalSupplierInvoiceAuditTime(invoice?.updatedAt)}`}
            >
              {formatLocalSupplierInvoiceAuditTimeCompact(invoice?.createdAt)}
              {' · '}
              {formatLocalSupplierInvoiceAuditTimeCompact(invoice?.updatedAt)}
            </div>
          </Form.Item>
        </Form>
      </section>

      {/* ============================================================ */}
      {/* 明细：处理进度（同时是筛选）→ 工具栏 → 已生效筛选 → 勾选操作条 → 表格 → 合计 */}
      {/* ============================================================ */}
      <section className="lsi-wb-card">
        {canEditInvoice && (
          <div className="lsi-wb-progress">
            <div className="lsi-wb-progress-head">
              <Typography.Text strong style={{ fontSize: 14 }}>
                {t('posAdmin.invoiceWorkbench.detailsTitle')}
                <span className="lsi-muted lsi-num" style={{ fontWeight: 400, marginLeft: 6 }}>
                  {t('posAdmin.invoiceWorkbench.detailsCount', { count: details.length })}
                </span>
              </Typography.Text>
              <div
                className="lsi-wb-progress-track"
                role="img"
                aria-label={t('posAdmin.invoiceWorkbench.progressAria', {
                  executed: progressStats.executed,
                  pending: progressStats.pending,
                  waiting: progressStats.waiting,
                  unchecked: progressStats.unchecked,
                  none: progressStats.none,
                })}
              >
                {DETAIL_PROGRESS_BUCKETS.filter((bucket) => progressStats[bucket] > 0).map((bucket) => (
                  <span
                    key={bucket}
                    style={{ flex: `${progressStats[bucket]} 1 0`, background: PROGRESS_BUCKET_COLORS[bucket] }}
                  />
                ))}
              </div>
              <span className="lsi-muted lsi-num" style={{ fontSize: 12 }}>
                {t('posAdmin.invoiceWorkbench.executedPercent', { percent: executedPercent })}
              </span>
            </div>
            <div className="lsi-wb-progress-buckets">
              <button
                type="button"
                className={progressBucketFilter === 'all' ? 'lsi-wb-bucket lsi-wb-bucket-active' : 'lsi-wb-bucket'}
                aria-pressed={progressBucketFilter === 'all'}
                onClick={() => setProgressBucketFilter('all')}
              >
                {t('posAdmin.invoiceWorkbench.bucketAll')}
                <span className="lsi-wb-bucket-count">{details.length}</span>
              </button>
              {DETAIL_PROGRESS_BUCKETS
                .filter((bucket) => progressStats[bucket] > 0 || progressBucketFilter === bucket)
                .map((bucket) => {
                  const selected = progressBucketFilter === bucket
                  const hint = bucket === 'pending'
                    ? pendingBreakdown
                    : bucket === 'waiting'
                      ? t('posAdmin.invoiceWorkbench.waitingHint')
                      : ''
                  return (
                    <button
                      key={bucket}
                      type="button"
                      className={selected ? 'lsi-wb-bucket lsi-wb-bucket-active' : 'lsi-wb-bucket'}
                      aria-pressed={selected}
                      onClick={() => setProgressBucketFilter(selected ? 'all' : bucket)}
                    >
                      <span className="lsi-wb-swatch" style={{ background: PROGRESS_BUCKET_COLORS[bucket] }} />
                      {progressBucketLabels[bucket]}
                      <span className="lsi-wb-bucket-count">{progressStats[bucket]}</span>
                      {hint ? <span className="lsi-wb-bucket-hint">{hint}</span> : null}
                    </button>
                  )
                })}
              <span className="lsi-wb-spacer" />
              {canRunGlobalLocalPurchaseBatchActions && pendingExecutionSplit.createGuids.length > 0 && (
                <Button
                  type="primary"
                  size="small"
                  icon={executing ? <LoadingOutlined /> : <PlayCircleOutlined />}
                  disabled={executing}
                  onClick={() => handleExecuteAllPending('createProducts')}
                >
                  {t('posAdmin.invoiceWorkbench.createAllPending', { count: pendingExecutionSplit.createGuids.length })}
                </Button>
              )}
              {canRunGlobalLocalPurchaseBatchActions && pendingExecutionSplit.otherGuids.length > 0 && (
                <Button
                  type={pendingExecutionSplit.createGuids.length > 0 ? 'default' : 'primary'}
                  size="small"
                  icon={executing ? <LoadingOutlined /> : <PlayCircleOutlined />}
                  disabled={executing}
                  onClick={() => handleExecuteAllPending('otherActions')}
                >
                  {pendingExecutionSplit.createGuids.length > 0
                    ? t('posAdmin.invoiceWorkbench.executeOtherPending', { count: pendingExecutionSplit.otherGuids.length })
                    : t('posAdmin.invoiceWorkbench.executeAllPending', { count: pendingExecutionSplit.otherGuids.length })}
                </Button>
              )}
            </div>
          </div>
        )}

        <div ref={toolbarRef} className="lsi-wb-toolbar">
          {!canEditInvoice && (
            <Typography.Text strong style={{ fontSize: 14, marginRight: 8 }}>
              {t('posAdmin.invoiceWorkbench.detailsTitle')}
              <span className="lsi-muted lsi-num" style={{ fontWeight: 400, marginLeft: 6 }}>
                {t('posAdmin.invoiceWorkbench.detailsCount', { count: details.length })}
              </span>
            </Typography.Text>
          )}
          <Input
            allowClear
            prefix={<SearchOutlined className="lsi-muted" />}
            placeholder={t('posAdmin.invoiceWorkbench.searchPlaceholder')}
            style={{ width: 220 }}
            value={searchText}
            onChange={(e) => setSearchText(e.target.value)}
          />
          {renderQuickChip('price-up', t('posAdmin.invoiceWorkbench.chipPriceUp'), priceStats.upCount, priceFilter === 'up', () => setPriceFilter(priceFilter === 'up' ? 'all' : 'up'))}
          {renderQuickChip('price-down', t('posAdmin.invoiceWorkbench.chipPriceDown'), priceStats.downCount, priceFilter === 'down', () => setPriceFilter(priceFilter === 'down' ? 'all' : 'down'))}
          {renderQuickChip(
            'not-exists',
            canEditInvoice ? t('posAdmin.invoiceWorkbench.chipNotExists') : t('posAdmin.invoiceWorkbench.chipNotExistsStore'),
            detailStatusStats.product.notExists,
            productStatusFilter === 'notExists',
            () => setProductStatusFilter(toggleStatusFilter(productStatusFilter, 'notExists')),
          )}
          {canEditInvoice && renderQuickChip(
            'barcode-abnormal',
            t('posAdmin.invoiceWorkbench.chipBarcodeAbnormal'),
            detailStatusStats.barcode.noMatch + detailStatusStats.barcode.multiMatch,
            barcodeStatusFilter === 'abnormal',
            () => setBarcodeStatusFilter(barcodeStatusFilter === 'abnormal' ? 'all' : 'abnormal'),
          )}
          {canEditInvoice && (
            <Popover trigger="click" placement="bottomLeft" content={moreFiltersContent}>
              <Button size="small" className="lsi-wb-chip" icon={<FilterOutlined />}>
                {t('posAdmin.invoiceWorkbench.moreFilters')}
                {moreFilterCount > 0 && <Badge count={moreFilterCount} size="small" style={{ marginLeft: 4 }} />}
              </Button>
            </Popover>
          )}
          <span className="lsi-wb-spacer" />
          {canEditDetailRows && (
            <Button
              icon={<SnippetsOutlined />}
              onClick={() => runAfterUnsavedGuard(() => {
                setPasteMultilineCellMode('merge')
                setPasteVisible(true)
              })}
            >
              {t('posAdmin.invoiceDetail.pasteDataBtn', '粘贴数据')}
            </Button>
          )}
          {canEditDetailRows && (
            <Button
              icon={checking ? <LoadingOutlined /> : <ScanOutlined />}
              disabled={checking}
              onClick={() => runAfterUnsavedGuard(() => void handleCheckProducts())}
            >
              {/* 检测范围写在按钮上：有勾选只检测勾选行，否则检测全部 */}
              {selectedCount > 0
                ? t('posAdmin.invoiceWorkbench.checkSelected', { count: selectedCount })
                : t('posAdmin.invoiceWorkbench.checkAll', { count: details.length })}
            </Button>
          )}
          {canEditDetailRows && (
            <Dropdown
              trigger={['click']}
              menu={{
                items: [
                  {
                    key: 'updateLastPurchasePrices',
                    label: t('posAdmin.invoiceDetail.updateLastPurchasePricesBtn', '更新上次进货价'),
                    disabled: updatingLastPurchasePrices || !details.length,
                  },
                ],
                onClick: ({ key }) => {
                  if (key === 'updateLastPurchasePrices') {
                    runAfterUnsavedGuard(() => handleUpdateLastPurchasePrices())
                  }
                },
              }}
            >
              <Button icon={<MoreOutlined />} aria-label={t('posAdmin.invoiceWorkbench.moreActions')} />
            </Dropdown>
          )}
        </div>

        {activeFilterTags.length > 0 && (
          <div className="lsi-wb-strip">
            <ActiveFilterBar items={activeFilterTags} onClearAll={handleClearAllOuterFilters} />
          </div>
        )}

        {canSelectRows && selectedCount > 0 && (
          <div className="lsi-wb-strip">
            <SelectionActionBar selectedCount={selectedCount} onClearSelection={() => setSelectedRowKeys([])}>
              {isAdmin && (
                <Button size="small" onClick={() => setBatchEditVisible(true)}>
                  {t('posAdmin.invoiceWorkbench.batchEdit')}
                </Button>
              )}
              {canRunGlobalLocalPurchaseBatchActions && (
                <Dropdown
                  menu={{
                    items: ACTION_MENU_ITEMS(t),
                    onClick: ({ key }) => void handleBatchSetAction(key),
                  }}
                >
                  <Button size="small">
                    {t('posAdmin.invoiceWorkbench.setAction')}
                    <DownOutlined style={{ fontSize: 10 }} />
                  </Button>
                </Dropdown>
              )}
              {canRunGlobalLocalPurchaseBatchActions && (
                <Button
                  size="small"
                  type="primary"
                  ghost
                  icon={executing ? <LoadingOutlined /> : undefined}
                  disabled={executing}
                  onClick={() => runAfterUnsavedGuard(() => handleBatchExecute(undefined, 'createProducts'))}
                >
                  {t('posAdmin.invoiceWorkbench.createProducts')}
                </Button>
              )}
              {canRunGlobalLocalPurchaseBatchActions && (
                <Button
                  size="small"
                  type="primary"
                  ghost
                  icon={executing ? <LoadingOutlined /> : undefined}
                  disabled={executing}
                  onClick={() => runAfterUnsavedGuard(() => handleBatchExecute())}
                >
                  {t('posAdmin.invoiceWorkbench.executeSelected')}
                </Button>
              )}
              {isAdmin && (
                <Button size="small" disabled={storePriceLoading} onClick={() => openStorePriceModal()}>
                  {t('posAdmin.invoiceWorkbench.updateToStore')}
                </Button>
              )}
              {canWriteLocalPurchaseToHq && (
                <Button
                  size="small"
                  icon={hqUpdateLoading ? <LoadingOutlined /> : undefined}
                  disabled={hqUpdateLoading || !selectedRowKeys.length}
                  onClick={() => openHqUpdateModal()}
                >
                  {t('posAdmin.invoiceDetail.updateHqProductsBtn', '更新HQ商品')}
                </Button>
              )}
              {isAdmin && (
                <Popconfirm
                  title={t('posAdmin.invoiceDetail.confirmDeleteTitle', '确认删除选中的明细行吗？')}
                  description={t('posAdmin.invoiceDetail.willDeleteCount', '将删除 {{count}} 条记录', { count: selectedCount })}
                  okText={t('posAdmin.invoiceDetail.delete', '删除')}
                  cancelText={t('common.cancel', '取消')}
                  okButtonProps={{ danger: true }}
                  onConfirm={() => runAfterUnsavedGuard(() => void handleDeleteSelected())}
                >
                  <Button size="small" danger>
                    {t('posAdmin.invoiceWorkbench.deleteSelected')}
                  </Button>
                </Popconfirm>
              )}
            </SelectionActionBar>
          </div>
        )}

        {/* 明细表格 */}
        <div ref={tableCardRef}>
          <MeasuredTable metricId="pos-admin.local-supplier-invoices.invoice-edit.table-2"
            rowKey="detailGUID"
            loading={detailLoading}
            dataSource={filteredDetails}
            columns={columns}
            pagination={false}
            onChange={handleTableChange}
            scroll={{ x: detailTableScrollX, y: tableScrollY }}
            className="lsi-wb-table"
            rowSelection={canSelectRows ? {
              fixed: true,
              columnWidth: 36,
              selectedRowKeys,
              onChange: (keys) => setSelectedRowKeys(keys),
            } : undefined}
            size="small"
          />
        </div>

        <div className="lsi-wb-footer">
          <span className="lsi-muted lsi-num">
            {isDetailFiltered
              ? t('posAdmin.invoiceWorkbench.footerFiltered', { count: inlineNavigationDetails.length, total: details.length })
              : t('posAdmin.invoiceWorkbench.footerAll', { count: details.length })}
          </span>
          <span className="lsi-wb-spacer" />
          <span className="lsi-num">
            <span className="lsi-muted">{t('posAdmin.invoiceWorkbench.footerQuantity')}</span>
            {' '}
            {formatQuantity(visibleDetailTotals.quantity)}
          </span>
          <span className="lsi-num">
            <span className="lsi-muted">{t('posAdmin.invoiceWorkbench.footerAmount')}</span>
            {' '}
            <strong>{formatAmount(visibleDetailTotals.amount)}</strong>
          </span>
        </div>
      </section>

      {/* ============================================================ */}
      {/* 粘贴数据 Modal                                                 */}
      {/* ============================================================ */}
      <Modal
        open={pasteVisible}
        title={t('posAdmin.invoiceDetail.pasteTitle', '粘贴数据')}
        confirmLoading={pasteLoading}
        onCancel={() => {
          setPasteVisible(false)
          setPasteText('')
          setPasteMultilineCellMode('merge')
        }}
        onOk={() => void handlePaste()}
        width={700}
      >
        <div style={{ marginBottom: 16 }}>
          <Radio.Group
            value={pasteMode}
            onChange={(e) => setPasteMode(e.target.value)}
            optionType="button"
            buttonStyle="solid"
          >
            <Radio.Button value="append">{t('posAdmin.invoiceDetail.pasteModeAppend', '追加 (Append)')}</Radio.Button>
            <Radio.Button value="replace">{t('posAdmin.invoiceDetail.pasteModeReplace', '替换 (Replace)')}</Radio.Button>
          </Radio.Group>
          <span style={{ marginLeft: 12, color: '#999', fontSize: 12 }}>
            {pasteMode === 'append' ? t('posAdmin.invoiceDetail.appendDesc', '保留现有数据，追加新数据') : t('posAdmin.invoiceDetail.replaceDesc', '清除现有数据，替换为新数据')}
          </span>
        </div>
        <div style={{ marginBottom: 8, color: '#666', fontSize: 12 }}>
          {t('posAdmin.invoiceDetail.pasteHint', '请从 Excel 复制数据后粘贴到下方文本框。每行一条记录，可在下方调整列对应字段（Tab 分隔）')}
        </div>
        {pasteMultilineAnalysis.hasMultilineCells && (
          <Alert
            type="warning"
            showIcon
            style={{ marginBottom: 12 }}
            message={t('posAdmin.invoiceDetail.pasteMultilineDetectedTitle', '检测到单元格内有换行')}
            description={(
              <Space direction="vertical" size={8} style={{ width: '100%' }}>
                <div>
                  {t('posAdmin.invoiceDetail.pasteMultilineDetectedDesc', '请选择处理方式；无法安全拆分的记录会自动按单元格内合并处理。')}
                </div>
                <Radio.Group
                  value={pasteMultilineCellMode}
                  onChange={(event) => setPasteMultilineCellMode(event.target.value as PasteMultilineCellMode)}
                >
                  <Radio value="merge">
                    {t('posAdmin.invoiceDetail.pasteMultilineMerge', '单元格内合并（推荐）')}
                  </Radio>
                  <Radio value="smartSplit">
                    {t('posAdmin.invoiceDetail.pasteMultilineSmartSplit', '按换行智能拆分')}
                  </Radio>
                </Radio.Group>
                {pasteMultilineCellMode === 'smartSplit' && pasteMultilineAnalysis.unsafeRecordCount > 0 && (
                  <div style={{ color: '#ad6800', fontSize: 12 }}>
                    {t('posAdmin.invoiceDetail.pasteMultilineUnsafeWarning', '有 {{count}} 条记录的多行列数量不一致，将按单元格内合并处理。', { count: pasteMultilineAnalysis.unsafeRecordCount })}
                  </div>
                )}
              </Space>
            )}
          />
        )}
        <div style={{ marginBottom: 12 }}>
          <Space size={8} align="center" wrap>
            {/* 只影响粘贴映射为“零售价”的列，进货价和新自动零售价保持原始粘贴值。 */}
            <Switch
              size="small"
              checked={normalizeRetailPriceOnPaste}
              onChange={setNormalizeRetailPriceOnPaste}
            />
            <span style={{ color: '#666', fontSize: 12 }}>
              {t('posAdmin.invoiceDetail.normalizeRetailPriceOnPaste', '零售价小数规范化')}
            </span>
            <span style={{ color: '#999', fontSize: 12 }}>
              {t('posAdmin.invoiceDetail.normalizeRetailPriceOnPasteHint', '5→4.99，4.1→4.50，4.6→4.99；1和2不变')}
            </span>
          </Space>
        </div>
        <div style={{ marginBottom: 12 }}>
          <Space style={{ marginBottom: 8, width: '100%', justifyContent: 'space-between' }}>
            <span style={{ color: '#666', fontSize: 12 }}>
              {t('posAdmin.invoiceDetail.pasteFieldOrderTitle', '列对应字段')}
            </span>
            <Button size="small" onClick={() => setPasteFieldOrder([...defaultPasteFieldOrder])}>
              {t('posAdmin.invoiceDetail.pasteRestoreDefaultOrder', '恢复默认')}
            </Button>
          </Space>
          <Row gutter={[8, 8]}>
            {pasteFieldOrder.map((field, index) => (
              <Col span={8} key={`paste-field-${index}`}>
                <div style={{ color: '#999', fontSize: 12, marginBottom: 4 }}>
                  {t('posAdmin.invoiceDetail.pasteColumnLabel', '第 {{index}} 列', { index: index + 1 })}
                </div>
                <Select<PasteFieldKey>
                  size="small"
                  value={field}
                  options={pasteFieldOptions}
                  style={{ width: '100%' }}
                  onChange={(nextField) => {
                    setPasteFieldOrder((prev) => prev.map((item, itemIndex) => (itemIndex === index ? nextField : item)))
                  }}
                />
              </Col>
            ))}
          </Row>
          {hasDuplicatePasteField && (
            <div style={{ color: '#cf1322', fontSize: 12, marginTop: 8 }}>
              {t('posAdmin.invoiceDetail.pasteFieldDuplicateWarning', '同一个字段不能选择多次，请把多余列设置为“跳过此列”')}
            </div>
          )}
        </div>
        <Input.TextArea
          rows={12}
          value={pasteText}
          onChange={(e) => setPasteText(e.target.value)}
          placeholder={t('posAdmin.invoiceDetail.pastePlaceholder', '从 Excel 复制数据后粘贴到此处...')}
          style={{ fontFamily: 'monospace' }}
        />
        {pasteText.trim() && (
          <div style={{ marginTop: 8, color: '#999', fontSize: 12 }}>
            {t('posAdmin.invoiceDetail.parsedRows', '已识别 {{count}} 行数据', { count: parsedPasteRowCount })}
          </div>
        )}
      </Modal>

      {/* ============================================================ */}
      {/* 批量编辑 Modal                                                 */}
      {/* ============================================================ */}
      <Modal
        open={batchEditVisible}
        title={t('posAdmin.invoiceDetail.editCountTitle', '批量编辑 ({{count}} 条)', { count: selectedRowKeys.length })}
        confirmLoading={batchEditLoading}
        onCancel={() => {
          setBatchEditVisible(false)
          batchEditForm.resetFields()
        }}
        onOk={() => void handleBatchEdit()}
        width={600}
      >
        <Form form={batchEditForm} layout="vertical">
          <Form.Item name="updatePurchasePrice" valuePropName="checked">
            <Checkbox>{t('posAdmin.invoiceDetail.purchasePriceCheckbox', '进货价')}</Checkbox>
          </Form.Item>
          <Form.Item
            noStyle
            shouldUpdate={(prev, cur) => prev.updatePurchasePrice !== cur.updatePurchasePrice}
          >
            {({ getFieldValue }) =>
              getFieldValue('updatePurchasePrice') ? (
                <Form.Item name="purchasePrice" label={t('posAdmin.invoiceDetail.purchasePriceLabel', '进货价')} style={{ marginLeft: 24 }}>
                  <InputNumber min={0} precision={2} style={{ width: '100%' }} />
                </Form.Item>
              ) : null
            }
          </Form.Item>

          <Form.Item name="updateRetailPrice" valuePropName="checked">
            <Checkbox>{t('posAdmin.invoiceDetail.retailPriceCheckbox', '零售价')}</Checkbox>
          </Form.Item>
          <Form.Item
            noStyle
            shouldUpdate={(prev, cur) => prev.updateRetailPrice !== cur.updateRetailPrice}
          >
            {({ getFieldValue }) =>
              getFieldValue('updateRetailPrice') ? (
                <Form.Item name="retailPrice" label={t('posAdmin.invoiceDetail.retailPriceLabel', '零售价')} style={{ marginLeft: 24 }}>
                  <InputNumber min={0} precision={2} style={{ width: '100%' }} />
                </Form.Item>
              ) : null
            }
          </Form.Item>

          <Form.Item name="updateIsAutoPricing" valuePropName="checked">
            <Checkbox>{t('posAdmin.invoiceDetail.autoPricingCheckbox', '自动定价')}</Checkbox>
          </Form.Item>
          <Form.Item
            noStyle
            shouldUpdate={(prev, cur) => prev.updateIsAutoPricing !== cur.updateIsAutoPricing}
          >
            {({ getFieldValue }) =>
              getFieldValue('updateIsAutoPricing') ? (
                <Form.Item
                  name="isAutoPricing"
                  label={t('posAdmin.invoiceDetail.autoPricingLabel', '自动定价')}
                  valuePropName="checked"
                  initialValue={false}
                  style={{ marginLeft: 24 }}
                >
                  <Switch
                    checkedChildren={t('posAdmin.invoiceDetail.yes', '是')}
                    unCheckedChildren={t('posAdmin.invoiceDetail.no', '否')}
                  />
                </Form.Item>
              ) : null
            }
          </Form.Item>

          <Form.Item name="updateIsSpecialProduct" valuePropName="checked">
            <Checkbox>{t('posAdmin.invoiceDetail.specialProductLabel', '特殊商品')}</Checkbox>
          </Form.Item>
          <Form.Item
            noStyle
            shouldUpdate={(prev, cur) => prev.updateIsSpecialProduct !== cur.updateIsSpecialProduct}
          >
            {({ getFieldValue }) =>
              getFieldValue('updateIsSpecialProduct') ? (
                <Form.Item
                  name="isSpecialProduct"
                  label={t('posAdmin.invoiceDetail.specialProductLabel', '特殊商品')}
                  valuePropName="checked"
                  initialValue={false}
                  style={{ marginLeft: 24 }}
                >
                  <Switch
                    checkedChildren={t('posAdmin.invoiceDetail.yes', '是')}
                    unCheckedChildren={t('posAdmin.invoiceDetail.no', '否')}
                  />
                </Form.Item>
              ) : null
            }
          </Form.Item>

          <Form.Item name="updateDiscountRate" valuePropName="checked">
            <Checkbox>{t('posAdmin.invoiceDetail.discountRate', '折扣率')}</Checkbox>
          </Form.Item>
          <Form.Item
            noStyle
            shouldUpdate={(prev, cur) => prev.updateDiscountRate !== cur.updateDiscountRate}
          >
            {({ getFieldValue }) =>
              getFieldValue('updateDiscountRate') ? (
                <Form.Item name="discountRate" label={t('posAdmin.invoiceDetail.discountRate', '折扣率')} style={{ marginLeft: 24 }}>
                  <InputNumber
                    min={0}
                    max={100}
                    step={1}
                    precision={1}
                    addonAfter="%"
                    style={{ width: '100%' }}
                  />
                </Form.Item>
              ) : null
            }
          </Form.Item>
        </Form>
      </Modal>

      {/* ============================================================ */}
      {/* 更新到分店价格 Modal                                            */}
      {/* ============================================================ */}
      <Modal
        open={storePriceVisible}
        title={t('posAdmin.invoiceDetail.updateToStorePriceTitle2', '更新到分店价格 ({{count}} 条)', { count: selectedRowKeys.length })}
        confirmLoading={storePriceLoading}
        onCancel={() => {
          setStorePriceVisible(false)
          storePriceForm.resetFields()
        }}
        onOk={() => void handleUpdateToStorePrices()}
        width={600}
      >
        <Form
          form={storePriceForm}
          layout="vertical"
          initialValues={{ updatePurchasePrice: true, syncToHq: canWriteLocalPurchaseToHq }}
        >
          <Form.Item
            name="targetStoreCodes"
            label={t('posAdmin.invoiceDetail.targetStoreLabel', '目标分店')}
            rules={[{ required: true, message: t('posAdmin.invoiceDetail.selectTargetStore', '请选择目标分店') }]}
          >
            <Select
              mode="multiple"
              showSearch
              allowClear
              optionFilterProp="label"
              placeholder={t('posAdmin.invoiceDetail.selectTargetStore', '请选择目标分店')}
              options={storeOptions}
              popupRender={(menu) => (
                <>
                  <div style={{ padding: '4px 8px 8px', borderBottom: '1px solid #f0f0f0' }}>
                    {/* 全选只写入当前可选分店编码，提交和权限校验仍走原来的 targetStoreCodes 数组。 */}
                    <Checkbox
                      checked={allStorePriceStoresSelected}
                      indeterminate={hasPartialStorePriceStoreSelection}
                      disabled={!allStoreCodes.length}
                      onChange={(event) => {
                        storePriceForm.setFieldValue('targetStoreCodes', event.target.checked ? allStoreCodes : [])
                      }}
                    >
                      {t('posAdmin.invoiceDetail.selectAllStores', '全选所有分店 ({{count}} 个)', { count: allStoreCodes.length })}
                    </Checkbox>
                  </div>
                  {menu}
                </>
              )}
            />
          </Form.Item>

          <Form.Item name="updatePurchasePrice" valuePropName="checked">
            <Checkbox>{t('posAdmin.invoiceDetail.updatePurchasePriceLabel', '更新进货价')}</Checkbox>
          </Form.Item>
          <Form.Item name="updateRetailPrice" valuePropName="checked">
            <Checkbox>{t('posAdmin.invoiceDetail.updateRetailPrice', '更新零售价')}</Checkbox>
          </Form.Item>
          <Form.Item name="updateIsAutoPricing" valuePropName="checked">
            <Checkbox>{t('posAdmin.invoiceDetail.updateAutoPricing', '更新自动定价')}</Checkbox>
          </Form.Item>
          <Form.Item name="updateIsSpecialProduct" valuePropName="checked">
            <Checkbox>{t('posAdmin.invoiceDetail.updateSpecialProduct', '更新特殊商品')}</Checkbox>
          </Form.Item>
          <Form.Item name="updateDiscountRate" valuePropName="checked">
            <Checkbox>{t('posAdmin.invoiceDetail.updateDiscountRate', '更新折扣率')}</Checkbox>
          </Form.Item>
          <Form.Item
            name="syncToHq"
            valuePropName="checked"
            extra={canWriteLocalPurchaseToHq
              ? t('posAdmin.invoiceWorkbench.syncToHqStoreHint')
              : t('posAdmin.invoiceWorkbench.syncToHqNoPermission')}
          >
            <Checkbox disabled={!canWriteLocalPurchaseToHq}>{t('posAdmin.invoiceWorkbench.syncToHq')}</Checkbox>
          </Form.Item>
        </Form>
      </Modal>

      {/* ============================================================ */}
      {/* 更新 HQ 商品 Modal                                             */}
      {/* ============================================================ */}
      <Modal
        open={hqUpdateVisible}
        title={t('posAdmin.invoiceDetail.updateHqProductsTitle', '更新HQ商品 ({{count}} 条)', { count: selectedRowKeys.length })}
        confirmLoading={hqUpdateLoading}
        onCancel={() => {
          setHqUpdateVisible(false)
          hqUpdateForm.resetFields()
        }}
        onOk={() => void handleUpdateHqProducts()}
        width={600}
      >
        <Form form={hqUpdateForm} layout="vertical">
          <Form.Item
            name="targetStoreCodes"
            label={t('posAdmin.invoiceDetail.targetStoreLabel', '目标分店')}
            rules={[{ required: true, message: t('posAdmin.invoiceDetail.selectTargetStore', '请选择目标分店') }]}
          >
            <Select
              mode="multiple"
              showSearch
              allowClear
              optionFilterProp="label"
              placeholder={t('posAdmin.invoiceDetail.selectTargetStore', '请选择目标分店')}
              options={storeOptions}
              popupRender={(menu) => (
                <>
                  <div style={{ padding: '4px 8px 8px', borderBottom: '1px solid #f0f0f0' }}>
                    {/* 全选只写入当前可选分店编码，提交和权限校验仍走原来的 targetStoreCodes 数组。 */}
                    <Checkbox
                      checked={allHqUpdateStoresSelected}
                      indeterminate={hasPartialHqUpdateStoreSelection}
                      disabled={!allStoreCodes.length}
                      onChange={(event) => {
                        hqUpdateForm.setFieldValue('targetStoreCodes', event.target.checked ? allStoreCodes : [])
                      }}
                    >
                      {t('posAdmin.invoiceDetail.selectAllStores', '全选所有分店 ({{count}} 个)', { count: allStoreCodes.length })}
                    </Checkbox>
                  </div>
                  {menu}
                </>
              )}
            />
          </Form.Item>

          {/* 只把勾选字段提交给 HQ，后端据此避免改动未指定字段。 */}
          <Form.Item name="updatePurchasePrice" valuePropName="checked">
            <Checkbox>{t('posAdmin.invoiceDetail.updatePurchasePriceLabel', '更新进货价')}</Checkbox>
          </Form.Item>
          <Form.Item name="updateRetailPrice" valuePropName="checked">
            <Checkbox>{t('posAdmin.invoiceDetail.updateRetailPrice', '更新零售价')}</Checkbox>
          </Form.Item>
          <Form.Item name="updateIsAutoPricing" valuePropName="checked">
            <Checkbox>{t('posAdmin.invoiceDetail.updateAutoPricing', '更新自动定价')}</Checkbox>
          </Form.Item>
          <Form.Item name="updateIsSpecialProduct" valuePropName="checked">
            <Checkbox>{t('posAdmin.invoiceDetail.updateSpecialProduct', '更新特殊商品')}</Checkbox>
          </Form.Item>
          <Form.Item name="updateDiscountRate" valuePropName="checked">
            <Checkbox>{t('posAdmin.invoiceDetail.updateDiscountRate', '更新折扣率')}</Checkbox>
          </Form.Item>
        </Form>
      </Modal>

      <ProductSetCodeMaintenanceModal
        open={setCodeMaintenanceTarget !== null}
        productCode={setCodeMaintenanceTarget?.productCode}
        storeCode={setCodeMaintenanceTarget?.storeCode?.trim() || invoice?.storeCode?.trim()}
        onClose={() => setSetCodeMaintenanceTarget(null)}
        // 多码/套装保存后刷新明细，商品类型标记随之更新；有未保存修改时不刷新，避免覆盖用户改到一半的值。
        onSaved={async () => {
          if (!hasUnsavedChanges) await loadDetails(false)
        }}
      />
    </div>
  )
}
