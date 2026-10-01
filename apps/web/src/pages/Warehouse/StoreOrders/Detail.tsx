import {
  closestCenter,
  DndContext,
  KeyboardSensor,
  PointerSensor,
  useSensor,
  useSensors,
  type DragEndEvent,
} from '@dnd-kit/core'
import {
  horizontalListSortingStrategy,
  SortableContext,
  sortableKeyboardCoordinates,
  useSortable,
} from '@dnd-kit/sortable'
import { CSS } from '@dnd-kit/utilities'
import {
  CheckOutlined,
  ContainerOutlined,
  CopyOutlined,
  DeleteOutlined,
  EditOutlined,
  FileTextOutlined,
  PlusOutlined,
  PrinterOutlined,
  SaveOutlined,
  SearchOutlined,
  SortAscendingOutlined,
  SyncOutlined,
} from '@ant-design/icons'
import {
  Alert,
  Button,
  Card,
  Checkbox,
  DatePicker,
  Descriptions,
  Empty,
  Grid,
  Image,
  Input,
  InputNumber,
  Modal,
  Popconfirm,
  Radio,
  Select,
  Space,
  Spin,
  Tag,
  Tooltip,
  Typography,
  message,
  notification,
} from 'antd'
import type { ColumnsType } from 'antd/es/table'
import type { FilterDropdownProps, SortOrder, SorterResult } from 'antd/es/table/interface'
import dayjs from 'dayjs'
import type { InputNumberRef } from 'rc-input-number'
import { useKeepAliveContext } from 'keepalive-for-react'
import {
  useCallback,
  useEffect,
  useMemo,
  useRef,
  useState,
  type CSSProperties,
  type HTMLAttributes,
  type KeyboardEvent,
  type PointerEvent as ReactPointerEvent,
  type ReactNode,
} from 'react'
import { useTranslation } from 'react-i18next'
import { useLocation, useNavigate } from 'react-router-dom'
import BarcodePreview from '../../../components/BarcodePreview'
import PageContainer from '../../../components/PageContainer'
import { requiresDelistSupplyNotice } from '../../../components/SupplyNotice/delistSupplyNoticeGate'
import SupplyNoticeModal from '../../../components/SupplyNotice/SupplyNoticeModal'
import { useIsMobile } from '../../../hooks/useIsMobile'
import { useStableRouteContext } from '../../../hooks/useStableRouteContext'
import { useAuthStore } from '../../../store/auth'
import { getActiveChinaSuppliers } from '../../../services/chinaSupplierService'
import { getStores } from '../../../services/storeService'
import type { SupplyNoticeInput } from '../../../types/supplyNotice'
import ContainerProductPicker from './components/ContainerProductPicker'
import {
  addStoreOrderLine,
  batchLookupStoreOrderProducts,
  batchAddStoreOrderLines,
  batchUpdateStoreOrderLines,
  batchUpdateStoreOrderProductStatus,
  createStoreOrderPasteReplaceJob,
  getStoreOrderDetail,
  getStoreOrderDetailFull,
  getStoreOrderDetailProductCodes,
  getStoreOrderPasteReplaceJob,
  getStoreOrderProducts,
  removeStoreOrderLine,
  refreshStoreOrderImportPrices,
  startPickingStoreOrder,
  updateStoreOrderHeader,
  updateStoreOrderLine,
  updateStoreOrderOutboundDate,
  updateStoreOrderStatus,
  updateStoreOrderStoreContact,
  updateStoreOrderProductStatus,
} from '../../../services/storeOrderService'
import type { StoreDto } from '../../../types/store'
import type { ChinaSupplierItem } from '../../../types/chinaSupplier'
import type {
  StoreOrderDetail,
  StoreOrderDetailColumnFilters,
  StoreOrderDetailLine,
  StoreOrderDetailQuery,
  StoreOrderDetailStatFilter,
  StoreOrderDetailSortField,
  StoreOrderPasteTargetField,
  StoreOrderProductColumnFilters,
  StoreOrderProductItem,
} from '../../../types/storeOrder'
import { StoreOrderFlowStatus, StoreOrderStatusColorMap } from '../../../types/storeOrder'
import { copyTextToClipboard } from '../../../utils/clipboard'
import { useDynamicTabTitle } from '../../../hooks/useDynamicTabTitle'
import { deriveStoreOrderDetailPermissions } from './storeOrderDetailPermissions'
import { shouldSkipDetailAutoReload } from '../../../utils/detailLoadState'
import { shouldLoadStoreOrderDetailPage, shouldShowStoreOrderDetailInitialLoading } from './detailLoadState'
import { InvoiceEmailSentStatusText } from './invoiceEmailSentInfo'
import { resolveStoreContactDraftValue } from './storeOrderStoreContact'
import { buildBatchCopyOrderQuantityPayload, shouldSubmitBatchCopyOrderQuantity } from './batchCopyOrderQuantity'
import {
  formatProductPickerSupplierLabel,
  matchesProductPickerSupplierOption,
  type ProductPickerSupplierOption,
} from './productPickerSupplierFilter'
import {
  buildPasteSubmitItems,
  createPastePreviewItems,
  filterPastePreviewItems,
  formatPastePreviewQuantity,
  parseStoreOrderPasteRows,
  setExistingPastePreviewAction,
  type StoreOrderPasteAction,
  type StoreOrderPasteQuantityMode,
  type StoreOrderPastePreviewFilter,
  type StoreOrderPastePreviewItem,
} from './pastePreview'
import {
  StoreOrderPasteReplacePollingCancelledError,
  StoreOrderPasteReplacePollingTimeoutError,
  createStoreOrderPasteReplaceJobPoller,
} from './pasteReplaceJobPolling'
import {
  applyPasteOptimisticRowsToDetail,
  buildPasteOptimisticRows,
  resolvePasteOptimisticPendingAfterJob,
  type StoreOrderPasteOptimisticPending,
} from './pasteOptimisticRows'
import PickingAssignmentSection, { AssigneeChip, type LineAssigneeMap } from './pickingAssignment/PickingAssignmentSection'
import { formatStoreOrderVolume } from './volumeFormat'
import {
  isStoreOrderDetailColumnOrderCustomized,
  mergeStoreOrderDetailColumnOrder,
  moveStoreOrderDetailColumnOrder,
  type StoreOrderDetailTableColumnKey,
} from './columnOrder'
import './compact.css'
import { MeasuredTable } from '../../../components/MeasuredTable'

function formatDateTime(value?: string) {
  if (!value) {
    return '--'
  }

  const date = new Date(value)
  if (Number.isNaN(date.getTime())) {
    return value
  }

  return date.toLocaleString('zh-CN', { hour12: false })
}

function formatLocalDateForInput(value = new Date()) {
  const year = value.getFullYear()
  const month = String(value.getMonth() + 1).padStart(2, '0')
  const day = String(value.getDate()).padStart(2, '0')
  return `${year}-${month}-${day}`
}

function formatAmount(value?: number) {
  if (value === undefined || value === null) {
    return '--'
  }
  return value.toFixed(2)
}

function formatCurrencyAmount(value?: number) {
  const amount = formatAmount(value)
  return amount === '--' ? amount : `$${amount}`
}

type DetailLoadStatus = 'idle' | 'loading' | 'loaded' | 'notFound' | 'error'
type DetailSortField = StoreOrderDetailSortField | null
type DetailEditableField = 'allocQuantity' | 'importPrice'
type StoreOrderDetailColumnWidthMap = Partial<Record<StoreOrderDetailTableColumnKey, number>>
type StoreOrderPasteWriteTarget = StoreOrderPasteTargetField | 'allocQuantityByInner' | 'quantityByInner'
type BatchEditType = 'allocQuantity' | 'importPrice' | 'status' | 'copyOrderQuantityToAllocQuantity'
type StoreOrderDetailTextFilterKey = 'itemNumber' | 'productName' | 'barcode' | 'locationCode'
type StoreOrderDetailNumberFilterKey =
  | 'quantityMin'
  | 'quantityMax'
  | 'allocQuantityMin'
  | 'allocQuantityMax'
  | 'importPriceMin'
  | 'importPriceMax'
type StoreOrderDetailNumberRange = {
  min: StoreOrderDetailNumberFilterKey
  max: StoreOrderDetailNumberFilterKey
}

const STORE_ORDER_DETAIL_COLUMN_ORDER_STORAGE_KEY = 'hbweb_rv.storeOrders.detail.columnOrder.v1'
const STORE_ORDER_DETAIL_COLUMN_WIDTH_STORAGE_KEY = 'hbweb_rv.storeOrders.detail.columnWidths.v1'
const STORE_ORDER_DETAIL_TABLE_MIN_SCROLL_X = 1290
const STORE_ORDER_DETAIL_SELECTION_COLUMN_WIDTH = 34
const STORE_ORDER_DETAIL_MAX_COLUMN_WIDTH = 420
const STORE_ORDER_DETAIL_DEFAULT_COLUMN_WIDTHS: Record<StoreOrderDetailTableColumnKey, number> = {
  index: 30,
  productImage: 42,
  itemNumber: 132,
  productName: 180,
  barcode: 112,
  price: 62,
  locationCode: 82,
  assignee: 88,
  quantity: 58,
  allocQuantity: 70,
  importPrice: 70,
  allocatedImportAmount: 76,
  orderVolume: 76,
  allocVolume: 76,
  isActive: 50,
  actions: 86,
}
const STORE_ORDER_DETAIL_MIN_COLUMN_WIDTHS: Record<StoreOrderDetailTableColumnKey, number> = {
  index: 30,
  productImage: 42,
  itemNumber: 112,
  productName: 140,
  barcode: 96,
  price: 56,
  locationCode: 70,
  assignee: 72,
  quantity: 54,
  allocQuantity: 64,
  importPrice: 64,
  allocatedImportAmount: 68,
  orderVolume: 68,
  allocVolume: 68,
  isActive: 50,
  actions: 80,
}

interface DraggableHeaderCellProps extends HTMLAttributes<HTMLTableCellElement> {
  'data-column-key'?: string
  'data-column-width'?: number
  'data-column-fixed'?: 'left' | 'right'
  onColumnResizeStart?: (
    columnKey: StoreOrderDetailTableColumnKey,
    width: number,
    resizeFromLeft: boolean,
    event: ReactPointerEvent<HTMLSpanElement>,
  ) => void
}

const STORE_ORDER_DETAIL_SORT_FIELDS: StoreOrderDetailSortField[] = [
  'itemNumber',
  'productName',
  'barcode',
  'locationCode',
  'quantity',
  'allocQuantity',
  'importPrice',
  'importAmount',
  'allocatedImportAmount',
  'isActive',
]

function isStoreOrderDetailSortField(field: unknown): field is StoreOrderDetailSortField {
  return typeof field === 'string' && STORE_ORDER_DETAIL_SORT_FIELDS.includes(field as StoreOrderDetailSortField)
}

function resolvePasteTargetField(writeTarget: StoreOrderPasteWriteTarget): StoreOrderPasteTargetField {
  // inner 仅影响 Excel 数量换算；提交接口仍使用既有的订货或发货字段。
  return writeTarget === 'allocQuantityByInner'
    ? 'allocQuantity'
    : writeTarget === 'quantityByInner'
      ? 'quantity'
      : writeTarget
}

function resolvePasteQuantityMode(writeTarget: StoreOrderPasteWriteTarget): StoreOrderPasteQuantityMode {
  return writeTarget === 'allocQuantityByInner' || writeTarget === 'quantityByInner' ? 'inner' : 'direct'
}

interface EditedLinePayload {
  detailGUID: string
  productCode: string
  quantity?: number
  importPrice?: number
  syncImportPrice?: boolean
  importPriceChanged: boolean
}

function toNumber(value?: number | null) {
  return Number(value ?? 0)
}

function isZeroOrEmpty(value: unknown) {
  return value === undefined || value === null || value === '' || value === 0
}

function isAbortError(error: unknown) {
  return typeof error === 'object' && error !== null && 'name' in error && error.name === 'AbortError'
}

function clampStoreOrderDetailColumnWidth(columnKey: StoreOrderDetailTableColumnKey, width: number) {
  const minWidth = STORE_ORDER_DETAIL_MIN_COLUMN_WIDTHS[columnKey] ?? 56
  return Math.min(STORE_ORDER_DETAIL_MAX_COLUMN_WIDTH, Math.max(minWidth, Math.round(width)))
}

function normalizeStoreOrderDetailColumnWidths(
  value: unknown,
  availableColumnKeys: readonly StoreOrderDetailTableColumnKey[],
): StoreOrderDetailColumnWidthMap {
  if (typeof value !== 'object' || value === null || Array.isArray(value)) {
    return {}
  }

  const availableSet = new Set(availableColumnKeys)
  const nextWidths: StoreOrderDetailColumnWidthMap = {}
  for (const [key, width] of Object.entries(value)) {
    if (!availableSet.has(key as StoreOrderDetailTableColumnKey) || typeof width !== 'number' || !Number.isFinite(width)) {
      continue
    }
    const columnKey = key as StoreOrderDetailTableColumnKey
    nextWidths[columnKey] = clampStoreOrderDetailColumnWidth(columnKey, width)
  }
  return nextWidths
}

function areStoreOrderDetailColumnWidthsEqual(
  left: StoreOrderDetailColumnWidthMap,
  right: StoreOrderDetailColumnWidthMap,
) {
  const leftKeys = Object.keys(left)
  const rightKeys = Object.keys(right)
  return leftKeys.length === rightKeys.length &&
    leftKeys.every((key) => left[key as StoreOrderDetailTableColumnKey] === right[key as StoreOrderDetailTableColumnKey])
}

function DraggableHeaderCell({ children, style, onColumnResizeStart, ...props }: DraggableHeaderCellProps) {
  const columnKey = props['data-column-key']
  const columnWidth = props['data-column-width']
  const resizeFromLeft = props['data-column-fixed'] === 'right'
  const {
    attributes,
    listeners,
    setNodeRef,
    transform,
    transition,
    isDragging,
  } = useSortable({
    id: columnKey ?? '__store-order-detail-static-column__',
    disabled: !columnKey,
  })

  if (!columnKey) {
    return <th style={style} {...props}>{children}</th>
  }

  const headerStyle: CSSProperties = {
    ...style,
    transform: CSS.Translate.toString(transform),
    transition,
    position: style?.position ?? 'relative',
    zIndex: isDragging ? 3 : style?.zIndex,
    opacity: isDragging ? 0.85 : style?.opacity,
  }

  return (
    <th ref={setNodeRef} style={headerStyle} {...props}>
      <div className="store-order-detail-draggable-header" {...attributes} {...listeners}>
        {children}
      </div>
      <span
        className={`store-order-detail-column-resize-handle${resizeFromLeft ? ' store-order-detail-column-resize-handle-left' : ''}`}
        aria-hidden="true"
        onPointerDown={(event) => {
          event.preventDefault()
          event.stopPropagation()
          if (typeof columnWidth !== 'number') return
          event.currentTarget.setPointerCapture(event.pointerId)
          onColumnResizeStart?.(columnKey as StoreOrderDetailTableColumnKey, columnWidth, resizeFromLeft, event)
        }}
        onClick={(event) => {
          event.preventDefault()
          event.stopPropagation()
        }}
      />
    </th>
  )
}

function renderDangerValue(value: string) {
  return (
    <span
      style={{
        display: 'inline-block',
        padding: '0 6px',
        borderRadius: 4,
        background: '#fff1f0',
        color: '#cf1322',
        fontWeight: 500,
      }}
    >
      {value}
    </span>
  )
}

function renderWarningValue(value: string) {
  return (
    <span
      style={{
        display: 'inline-block',
        padding: '0 6px',
        borderRadius: 4,
        background: '#fff7e6',
        color: '#d46b08',
        fontWeight: 500,
      }}
    >
      {value}
    </span>
  )
}

function renderStoreOrderDetailNumericCell(value: ReactNode) {
  return <span className="store-order-numeric-cell">{value}</span>
}

function renderStoreOrderTwoLineText(value?: string) {
  if (!value) {
    return <>--</>
  }
  return <span className="store-order-two-line-text" title={value}>{value}</span>
}

function isOrderedNotShipped(line: StoreOrderDetailLine) {
  return toNumber(line.quantity) > 0 && toNumber(line.allocQuantity) === 0
}

function isShippedWithoutOrder(line: StoreOrderDetailLine) {
  return toNumber(line.quantity) <= 0 && toNumber(line.allocQuantity) > 0
}

interface ProductPickerModalProps {
  open: boolean
  orderGUID: string
  loading?: boolean
  onCancel: () => void
  onConfirm: (items: Array<{ productCode: string; quantity: number; importPrice?: number }>) => Promise<void>
}

const PRODUCT_PICKER_DEFAULT_PAGE_SIZE = 100
const PRODUCT_PICKER_PAGE_SIZE_OPTIONS = ['50', '100', '500']
const STORE_ORDER_DETAIL_DEFAULT_PAGE_SIZE = 200
const STORE_ORDER_DETAIL_PAGE_SIZE_OPTIONS = ['50', '100', '200', '500', '1000']
type ProductPickerTextFilterKey = 'itemNumber' | 'productName' | 'supplierKeyword' | 'barcode'
type ProductPickerNumberFilterKey =
  | 'stockQuantityMin'
  | 'stockQuantityMax'
  | 'minOrderQuantityMin'
  | 'minOrderQuantityMax'
  | 'importPriceMin'
  | 'importPriceMax'
type ProductPickerNumberRange = {
  min: ProductPickerNumberFilterKey
  max: ProductPickerNumberFilterKey
}

function cleanProductPickerColumnFilters(
  filters?: StoreOrderProductColumnFilters,
): StoreOrderProductColumnFilters | undefined {
  if (!filters) {
    return undefined
  }

  const next: StoreOrderProductColumnFilters = {}
  const assignText = (key: ProductPickerTextFilterKey) => {
    const value = filters[key]?.trim()
    if (value) {
      next[key] = value
    }
  }
  const assignNumber = (key: ProductPickerNumberFilterKey) => {
    const value = filters[key]
    if (typeof value === 'number' && Number.isFinite(value)) {
      next[key] = value
    }
  }

  assignText('itemNumber')
  assignText('productName')
  assignText('supplierKeyword')
  assignText('barcode')
  assignNumber('stockQuantityMin')
  assignNumber('stockQuantityMax')
  assignNumber('minOrderQuantityMin')
  assignNumber('minOrderQuantityMax')
  assignNumber('importPriceMin')
  assignNumber('importPriceMax')

  return Object.keys(next).length ? next : undefined
}

function cleanStoreOrderDetailColumnFilters(
  filters?: StoreOrderDetailColumnFilters,
): StoreOrderDetailColumnFilters | undefined {
  if (!filters) {
    return undefined
  }

  const next: StoreOrderDetailColumnFilters = {}
  const assignText = (key: StoreOrderDetailTextFilterKey) => {
    const value = filters[key]?.trim()
    if (value) {
      next[key] = value
    }
  }
  const assignNumber = (key: StoreOrderDetailNumberFilterKey) => {
    const value = filters[key]
    if (typeof value === 'number' && Number.isFinite(value)) {
      next[key] = value
    }
  }

  assignText('itemNumber')
  assignText('productName')
  assignText('barcode')
  assignText('locationCode')
  assignNumber('quantityMin')
  assignNumber('quantityMax')
  assignNumber('allocQuantityMin')
  assignNumber('allocQuantityMax')
  assignNumber('importPriceMin')
  assignNumber('importPriceMax')
  if (typeof filters.isActive === 'boolean') {
    next.isActive = filters.isActive
  }

  return Object.keys(next).length ? next : undefined
}

interface ProductPickerTextFilterDropdownProps {
  value?: string
  placeholder: string
  applyLabel: string
  resetLabel: string
  confirm: FilterDropdownProps['confirm']
  onApply: (value: string, confirm: FilterDropdownProps['confirm']) => void
  onReset: (confirm: FilterDropdownProps['confirm']) => void
}

function ProductPickerTextFilterDropdown({
  value,
  placeholder,
  applyLabel,
  resetLabel,
  confirm,
  onApply,
  onReset,
}: ProductPickerTextFilterDropdownProps) {
  const [draft, setDraft] = useState(value ?? '')

  useEffect(() => {
    setDraft(value ?? '')
  }, [value])

  return (
    <div className="store-order-list-column-filter" onKeyDown={(event) => event.stopPropagation()} onMouseDown={(event) => event.stopPropagation()}>
      <Input
        allowClear
        value={draft}
        placeholder={placeholder}
        onChange={(event) => setDraft(event.target.value)}
        onPressEnter={() => onApply(draft, confirm)}
      />
      <Space>
        <Button size="small" type="primary" onClick={() => onApply(draft, confirm)}>{applyLabel}</Button>
        <Button size="small" onClick={() => onReset(confirm)}>{resetLabel}</Button>
      </Space>
    </div>
  )
}

interface ProductPickerNumberRangeFilterDropdownProps {
  minValue?: number
  maxValue?: number
  minPlaceholder: string
  maxPlaceholder: string
  applyLabel: string
  resetLabel: string
  confirm: FilterDropdownProps['confirm']
  onApply: (range: { min?: number; max?: number }, confirm: FilterDropdownProps['confirm']) => void
  onReset: (confirm: FilterDropdownProps['confirm']) => void
}

function ProductPickerNumberRangeFilterDropdown({
  minValue,
  maxValue,
  minPlaceholder,
  maxPlaceholder,
  applyLabel,
  resetLabel,
  confirm,
  onApply,
  onReset,
}: ProductPickerNumberRangeFilterDropdownProps) {
  const [minDraft, setMinDraft] = useState<number | undefined>(minValue)
  const [maxDraft, setMaxDraft] = useState<number | undefined>(maxValue)

  useEffect(() => {
    setMinDraft(minValue)
  }, [minValue])

  useEffect(() => {
    setMaxDraft(maxValue)
  }, [maxValue])

  return (
    <div className="store-order-list-column-filter" onKeyDown={(event) => event.stopPropagation()} onMouseDown={(event) => event.stopPropagation()}>
      <Space.Compact>
        <InputNumber
          controls={false}
          value={minDraft}
          placeholder={minPlaceholder}
          onChange={(value) => setMinDraft(value == null ? undefined : Number(value))}
        />
        <InputNumber
          controls={false}
          value={maxDraft}
          placeholder={maxPlaceholder}
          onChange={(value) => setMaxDraft(value == null ? undefined : Number(value))}
        />
      </Space.Compact>
      <Space>
        <Button size="small" type="primary" onClick={() => onApply({ min: minDraft, max: maxDraft }, confirm)}>{applyLabel}</Button>
        <Button size="small" onClick={() => onReset(confirm)}>{resetLabel}</Button>
      </Space>
    </div>
  )
}

interface StoreOrderDetailStatusFilterDropdownProps {
  value?: boolean
  activeLabel: string
  inactiveLabel: string
  applyLabel: string
  resetLabel: string
  confirm: FilterDropdownProps['confirm']
  onApply: (value: boolean | undefined, confirm: FilterDropdownProps['confirm']) => void
  onReset: (confirm: FilterDropdownProps['confirm']) => void
}

function StoreOrderDetailStatusFilterDropdown({
  value,
  activeLabel,
  inactiveLabel,
  applyLabel,
  resetLabel,
  confirm,
  onApply,
  onReset,
}: StoreOrderDetailStatusFilterDropdownProps) {
  const [draft, setDraft] = useState<boolean | undefined>(value)

  useEffect(() => {
    setDraft(value)
  }, [value])

  return (
    <div className="store-order-list-column-filter" onKeyDown={(event) => event.stopPropagation()} onMouseDown={(event) => event.stopPropagation()}>
      <Radio.Group
        value={draft}
        options={[
          { label: activeLabel, value: true },
          { label: inactiveLabel, value: false },
        ]}
        onChange={(event) => setDraft(event.target.value)}
      />
      <Space>
        <Button size="small" type="primary" onClick={() => onApply(draft, confirm)}>{applyLabel}</Button>
        <Button size="small" onClick={() => onReset(confirm)}>{resetLabel}</Button>
      </Space>
    </div>
  )
}

interface BatchEditPayload {
  type: BatchEditType
  allocQuantity?: number
  importPrice?: number
  isActive?: boolean
}

interface BatchEditModalProps {
  open: boolean
  loading?: boolean
  selectedCount: number
  onCancel: () => void
  onConfirm: (payload: BatchEditPayload) => Promise<void>
}

// 下架前的供货说明弹窗目标：行上的上/下架按钮（line）或批量改状态（batch）；为空即关闭。
type StoreOrderSupplyNoticeTarget =
  | { kind: 'line'; productCodes: string[]; line: StoreOrderDetailLine }
  | { kind: 'batch'; productCodes: string[]; payload: BatchEditPayload }

function ProductPickerModal({ open, orderGUID, loading, onCancel, onConfirm }: ProductPickerModalProps) {
  const { t } = useTranslation()
  const productRequestControllerRef = useRef<AbortController | null>(null)
  const supplierRequestControllerRef = useRef<AbortController | null>(null)
  const supplierOptionsLoadedRef = useRef(false)
  const [fetching, setFetching] = useState(false)
  const [keyword, setKeyword] = useState('')
  const [products, setProducts] = useState<StoreOrderProductItem[]>([])
  const [selectedRowKeys, setSelectedRowKeys] = useState<React.Key[]>([])
  const [pageNumber, setPageNumber] = useState(1)
  const [pageSize, setPageSize] = useState(PRODUCT_PICKER_DEFAULT_PAGE_SIZE)
  const [total, setTotal] = useState(0)
  const [supplierOptions, setSupplierOptions] = useState<ProductPickerSupplierOption[]>([])
  const [supplierCode, setSupplierCode] = useState<string>()
  const [supplierLoading, setSupplierLoading] = useState(false)
  const [productSortBy, setProductSortBy] = useState('Default')
  const [productSortDescending, setProductSortDescending] = useState(false)
  const [columnFilters, setColumnFilters] = useState<StoreOrderProductColumnFilters>({})
  const [selectedProductsByCode, setSelectedProductsByCode] = useState<Record<string, StoreOrderProductItem>>({})
  const [editingValues, setEditingValues] = useState<
    Record<string, { quantity?: number; importPrice?: number }>
  >({})

  const loadProducts = async (overrides?: {
    keyword?: string
    pageNumber?: number
    pageSize?: number
    supplierCode?: string
    sortBy?: string
    sortDescending?: boolean
    columnFilters?: StoreOrderProductColumnFilters
  }) => {
    const nextKeyword = overrides?.keyword ?? keyword
    const trimmedKeyword = nextKeyword.trim()
    const nextPageNumber = overrides?.pageNumber ?? pageNumber
    const nextPageSize = overrides?.pageSize ?? pageSize
    const nextSupplierCode = overrides?.supplierCode ?? supplierCode
    const nextSortBy = overrides?.sortBy ?? productSortBy
    const nextSortDescending = overrides?.sortDescending ?? productSortDescending
    const nextColumnFilters = overrides?.columnFilters ?? columnFilters
    const cleanedColumnFilters = cleanProductPickerColumnFilters(nextColumnFilters)

    productRequestControllerRef.current?.abort()
    const currentController = new AbortController()
    productRequestControllerRef.current = currentController

    setFetching(true)
    try {
      const result = await getStoreOrderProducts(
        {
          // 商品弹窗只有一个搜索框，需要同时覆盖货号/条码和商品名称。
          itemNumber: trimmedKeyword || undefined,
          productName: trimmedKeyword || undefined,
          supplierCode: nextSupplierCode || undefined,
          excludeOrderGUID: orderGUID,
          pageNumber: nextPageNumber,
          pageSize: nextPageSize,
          sortBy: nextSortBy,
          sortDescending: nextSortDescending,
          columnFilters: cleanedColumnFilters,
        },
        currentController.signal,
      )
      if (productRequestControllerRef.current !== currentController) {
        return
      }
      setProducts(result.items)
      setTotal(result.total)
      setPageNumber(nextPageNumber)
      setPageSize(nextPageSize)
      setProductSortBy(nextSortBy)
      setProductSortDescending(nextSortDescending)
      setColumnFilters(cleanedColumnFilters ?? {})
      setSelectedProductsByCode((current) => {
        if (!selectedRowKeys.length) {
          return current
        }

        const selectedKeySet = new Set(selectedRowKeys.map(String))
        const next = { ...current }
        result.items.forEach((item) => {
          if (selectedKeySet.has(item.productCode)) {
            next[item.productCode] = item
          }
        })
        return next
      })
    } catch (error) {
      if (isAbortError(error)) {
        return
      }
      console.error(error)
      message.error(error instanceof Error ? error.message : t('storeOrders.detail.loadProductsFailed'))
    } finally {
      if (productRequestControllerRef.current === currentController) {
        productRequestControllerRef.current = null
        setFetching(false)
      }
    }
  }

  const loadSupplierOptions = async () => {
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
      // 商品选择弹窗按国内供应商过滤，避免误用澳洲供应商编码导致候选商品为空。
      setSupplierOptions(
        suppliers
          .filter((item) => Boolean(item.supplierCode))
          .map((item) => ({
            // 下拉显示补充店铺号，搜索仍保留编码/名称/店铺号三种入口。
            label: formatProductPickerSupplierLabel(item.supplierName, item.supplierCode, item.shopNumber),
            value: item.supplierCode,
            supplierCode: item.supplierCode,
            supplierName: item.supplierName,
            shopNumber: item.shopNumber,
          })),
      )
      supplierOptionsLoadedRef.current = true
    } catch (error) {
      if (isAbortError(error)) {
        return
      }
      console.error(error)
      message.error(error instanceof Error ? error.message : t('storeOrders.detail.loadSuppliersFailed', '加载国内供应商失败'))
    } finally {
      if (supplierRequestControllerRef.current === currentController) {
        supplierRequestControllerRef.current = null
        setSupplierLoading(false)
      }
    }
  }

  useEffect(() => {
    if (!open) {
      return
    }
    void loadProducts({ pageNumber: 1 })
  }, [open])

  useEffect(() => {
    if (!open) {
      productRequestControllerRef.current?.abort()
      supplierRequestControllerRef.current?.abort()
      productRequestControllerRef.current = null
      supplierRequestControllerRef.current = null
      supplierOptionsLoadedRef.current = false
      setKeyword('')
      setProducts([])
      setSelectedRowKeys([])
      setPageNumber(1)
      setPageSize(PRODUCT_PICKER_DEFAULT_PAGE_SIZE)
      setTotal(0)
      setSupplierOptions([])
      setSupplierCode(undefined)
      setFetching(false)
      setSupplierLoading(false)
      setProductSortBy('Default')
      setProductSortDescending(false)
      setColumnFilters({})
      setSelectedProductsByCode({})
      setEditingValues({})
    }
  }, [open])

  const renderPickerCopyButton = (value: string, label: string) => (
    <Tooltip title={t('common.copy')}>
      <Button
        aria-label={label}
        className="store-order-picker-copy-button"
        icon={<CopyOutlined />}
        size="small"
        type="link"
        onClick={() => void copyTextToClipboard(value)}
      />
    </Tooltip>
  )

  const applyProductColumnFilterPatch = (
    patch: StoreOrderProductColumnFilters,
    confirm: FilterDropdownProps['confirm'],
  ) => {
    // 每个表头弹层只提交自己的 patch，避免未点击“应用”的其他列草稿被带入服务端查询。
    const cleanedFilters = cleanProductPickerColumnFilters({ ...columnFilters, ...patch })
    setColumnFilters(cleanedFilters ?? {})
    confirm()
    void loadProducts({ pageNumber: 1, columnFilters: cleanedFilters ?? {} })
  }

  const clearProductColumnFilter = (
    keys: Array<keyof StoreOrderProductColumnFilters>,
    confirm: FilterDropdownProps['confirm'],
  ) => {
    const patch: StoreOrderProductColumnFilters = {}
    keys.forEach((key) => {
      patch[key] = undefined
    })
    applyProductColumnFilterPatch(patch, confirm)
  }

  const makeProductTextFilterDropdown = (
    key: ProductPickerTextFilterKey,
    placeholder: string,
  ) => ({ confirm }: FilterDropdownProps) => (
    <ProductPickerTextFilterDropdown
      value={columnFilters[key]}
      placeholder={placeholder}
      applyLabel={t('containers.actions.applyColumnFilter', '应用')}
      resetLabel={t('containers.actions.resetColumnFilter', '重置')}
      confirm={confirm}
      onApply={(value, nextConfirm) => applyProductColumnFilterPatch({ [key]: value }, nextConfirm)}
      onReset={(nextConfirm) => clearProductColumnFilter([key], nextConfirm)}
    />
  )

  const makeProductNumberRangeFilterDropdown = (
    range: ProductPickerNumberRange,
  ) => ({ confirm }: FilterDropdownProps) => (
    <ProductPickerNumberRangeFilterDropdown
      minValue={columnFilters[range.min] as number | undefined}
      maxValue={columnFilters[range.max] as number | undefined}
      minPlaceholder={t('containers.placeholders.minValue', '最小值')}
      maxPlaceholder={t('containers.placeholders.maxValue', '最大值')}
      applyLabel={t('containers.actions.applyColumnFilter', '应用')}
      resetLabel={t('containers.actions.resetColumnFilter', '重置')}
      confirm={confirm}
      onApply={(value, nextConfirm) =>
        applyProductColumnFilterPatch(
          {
            [range.min]: value.min,
            [range.max]: value.max,
          },
          nextConfirm,
        )
      }
      onReset={(nextConfirm) => clearProductColumnFilter([range.min, range.max], nextConfirm)}
    />
  )

  const productFilterIcon = (active?: boolean) => (
    <SearchOutlined style={{ color: active ? '#1677ff' : undefined }} />
  )
  const productSortOrder = (field: string): SortOrder =>
    productSortBy === field ? (productSortDescending ? 'descend' : 'ascend') : null
  const hasProductNumberRangeFilter = (range: ProductPickerNumberRange) => (
    typeof columnFilters[range.min] === 'number' || typeof columnFilters[range.max] === 'number'
  )
  const productTextFilterProps = (key: ProductPickerTextFilterKey, placeholder: string) => ({
    filterDropdown: makeProductTextFilterDropdown(key, placeholder),
    filterIcon: productFilterIcon,
    filtered: Boolean(columnFilters[key]?.trim()),
  })
  const productNumberFilterProps = (range: ProductPickerNumberRange) => ({
    filterDropdown: makeProductNumberRangeFilterDropdown(range),
    filterIcon: productFilterIcon,
    filtered: hasProductNumberRangeFilter(range),
  })

  const columns: ColumnsType<StoreOrderProductItem> = [
    {
      title: t('column.image'),
      dataIndex: 'productImage',
      width: 48,
      render: (value: string | undefined, record) => (
        <Image
          src={value}
          alt={record.productName}
          width={32}
          height={32}
          style={{ borderRadius: 4, objectFit: 'cover' }}
          fallback="data:image/gif;base64,R0lGODlhAQABAAD/ACwAAAAAAQABAAACADs="
        />
      ),
    },
    {
      title: t('column.itemNumber'),
      dataIndex: 'itemNumber',
      width: 98,
      sorter: true,
      sortOrder: productSortOrder('itemNumber'),
      ...productTextFilterProps('itemNumber', t('storeOrders.detail.filterItemNumber', '过滤货号')),
      render: (value: string | undefined) =>
        value ? (
          <Space size={2} wrap={false} className="store-order-picker-inline-cell">
            <Typography.Text className="store-order-picker-nowrap" title={value}>{value}</Typography.Text>
            {renderPickerCopyButton(value, `${t('common.copy')} ${value}`)}
          </Space>
        ) : (
          renderDangerValue('--')
        ),
    },
    {
      title: t('column.productName'),
      dataIndex: 'productName',
      width: 170,
      sorter: true,
      sortOrder: productSortOrder('productName'),
      ...productTextFilterProps('productName', t('storeOrders.detail.filterProductName', '过滤商品名称')),
      render: (value: string | undefined) => (
        <span className="store-order-picker-two-line" title={value}>
          {value || '--'}
        </span>
      ),
    },
    {
      title: t('column.supplierName', '供应商名称'),
      dataIndex: 'domesticSupplierName',
      width: 108,
      ...productTextFilterProps('supplierKeyword', t('storeOrders.detail.filterSupplierKeyword', '过滤供应商')),
      render: (value: string | undefined, record) => {
        const displayValue = value || record.domesticSupplierCode || '--'
        return (
          <span className="store-order-picker-two-line" title={displayValue}>
            {displayValue}
          </span>
        )
      },
    },
    {
      title: t('column.barcode'),
      dataIndex: 'barcode',
      width: 122,
      sorter: true,
      sortOrder: productSortOrder('barcode'),
      ...productTextFilterProps('barcode', t('storeOrders.detail.filterBarcode', '过滤条码')),
      render: (value: string | undefined) =>
        value ? (
          <Space size={2} wrap={false} className="store-order-picker-inline-cell store-order-picker-barcode-cell">
            <Typography.Text className="store-order-picker-nowrap" title={value}>{value}</Typography.Text>
            {renderPickerCopyButton(value, `${t('common.copy')} ${value}`)}
          </Space>
        ) : (
          renderDangerValue('--')
        ),
    },
    {
      title: t('column.stock'),
      dataIndex: 'stockQuantity',
      width: 56,
      sorter: true,
      sortOrder: productSortOrder('stockQuantity'),
      ...productNumberFilterProps({ min: 'stockQuantityMin', max: 'stockQuantityMax' }),
    },
    {
      title: t('column.minOrder'),
      dataIndex: 'minOrderQuantity',
      width: 62,
      sorter: true,
      sortOrder: productSortOrder('minOrderQuantity'),
      ...productNumberFilterProps({ min: 'minOrderQuantityMin', max: 'minOrderQuantityMax' }),
    },
    {
      title: t('column.defaultImportPrice'),
      dataIndex: 'importPrice',
      width: 84,
      sorter: true,
      sortOrder: productSortOrder('importPrice'),
      ...productNumberFilterProps({ min: 'importPriceMin', max: 'importPriceMax' }),
      render: (value: number | undefined) => formatCurrencyAmount(value),
    },
    {
      title: t('column.oemPrice'),
      dataIndex: 'oemPrice',
      width: 62,
      render: (value: number | undefined) => formatCurrencyAmount(value),
    },
    {
      title: t('column.allocQuantity'),
      key: 'quantity',
      width: 70,
      render: (_, record) => (
        <InputNumber
          className="store-order-picker-number-input"
          min={0}
          precision={0}
          size="small"
          style={{ width: 58 }}
          value={editingValues[record.productCode]?.quantity ?? record.minOrderQuantity ?? 1}
          onChange={(value) =>
            setEditingValues((current) => ({
              ...current,
              [record.productCode]: {
                ...current[record.productCode],
                quantity: Number(value ?? record.minOrderQuantity ?? 1),
              },
            }))
          }
        />
      ),
    },
    {
      title: t('column.importPriceShort'),
      key: 'importPriceEdit',
      width: 82,
      render: (_, record) => (
        <InputNumber
          className="store-order-picker-number-input store-order-picker-price-input"
          min={0}
          // 商品弹窗价格直接显示 $，方便和数量列区分。
          prefix="$"
          precision={2}
          size="small"
          style={{ width: 70 }}
          value={editingValues[record.productCode]?.importPrice ?? record.importPrice}
          onChange={(value) =>
            setEditingValues((current) => ({
              ...current,
              [record.productCode]: {
                ...current[record.productCode],
                importPrice: value === null ? undefined : Number(value),
              },
            }))
          }
        />
      ),
    },
  ]

  const handleOk = async () => {
    if (!orderGUID) {
      message.error(t('storeOrders.detail.missingOrderNoError'))
      return
    }
    if (!selectedRowKeys.length) {
      message.warning(t('storeOrders.detail.selectProductsFirst'))
      return
    }

    const payload = selectedRowKeys
      .map((key) => selectedProductsByCode[String(key)] ?? products.find((item) => item.productCode === String(key)))
      .filter((item): item is StoreOrderProductItem => Boolean(item))
      .map((item) => ({
        productCode: item.productCode,
        quantity: editingValues[item.productCode]?.quantity ?? item.minOrderQuantity ?? 1,
        importPrice: editingValues[item.productCode]?.importPrice ?? item.importPrice,
      }))

    await onConfirm(payload)
  }

  return (
    <Modal
      className="store-order-product-picker-modal"
      title={t('storeOrders.selectProductTitle')}
      open={open}
      width={1080}
      destroyOnHidden
      okText={t('storeOrders.addSelected', { count: selectedRowKeys.length })}
      cancelText={t('common.close')}
      confirmLoading={loading}
      onCancel={onCancel}
      onOk={() => void handleOk()}
    >
      <Space direction="vertical" size={12} style={{ width: '100%' }}>
        <Space size={12} wrap>
          <Input.Search
            allowClear
            placeholder={t('storeOrders.detail.searchProductPlaceholder')}
            prefix={<SearchOutlined />}
            value={keyword}
            onChange={(event) => setKeyword(event.target.value)}
            onSearch={(value) => void loadProducts({ keyword: value, pageNumber: 1 })}
            style={{ width: 320 }}
          />
          <Select
            allowClear
            showSearch
            optionFilterProp="label"
            placeholder={t('storeOrders.detail.filterDomesticSupplier', '筛选国内供应商')}
            value={supplierCode}
            loading={supplierLoading}
            options={supplierOptions}
            filterOption={(input, option) => {
              return matchesProductPickerSupplierOption(input, option as ProductPickerSupplierOption | undefined)
            }}
            style={{ width: 260 }}
            onOpenChange={(visible) => {
              if (visible) {
                void loadSupplierOptions()
                return
              }
              // 下拉收起时取消尚未完成的供应商请求，避免旧请求回写已关闭的筛选框。
              supplierRequestControllerRef.current?.abort()
              supplierRequestControllerRef.current = null
              setSupplierLoading(false)
            }}
            onChange={(value) => {
              // 切换供应商后回到第一页，避免旧分页落在空页。
              setSupplierCode(value)
              void loadProducts({ pageNumber: 1, supplierCode: value })
            }}
          />
        </Space>
        <MeasuredTable metricId="warehouse.store-orders.detail.table-1"
          className="store-order-product-picker-table"
          rowKey="productCode"
          loading={fetching}
          size="small"
          tableLayout="fixed"
          dataSource={products}
          columns={columns}
          rowSelection={{
            selectedRowKeys,
            onChange: (nextSelectedRowKeys, nextSelectedRows) => {
              setSelectedRowKeys(nextSelectedRowKeys)
              setSelectedProductsByCode((current) => {
                const nextSelectedKeySet = new Set(nextSelectedRowKeys.map(String))
                const next: Record<string, StoreOrderProductItem> = {}
                Object.entries(current).forEach(([productCode, product]) => {
                  if (nextSelectedKeySet.has(productCode)) {
                    next[productCode] = product
                  }
                })
                // preserveSelectedRowKeys 只保留 key；这里补齐当前页实体，跨页/过滤后仍能提交。
                products.forEach((product) => {
                  if (nextSelectedKeySet.has(product.productCode)) {
                    next[product.productCode] = product
                  }
                })
                nextSelectedRows.forEach((product) => {
                  if (nextSelectedKeySet.has(product.productCode)) {
                    next[product.productCode] = product
                  }
                })
                return next
              })
            },
            preserveSelectedRowKeys: true,
            columnWidth: 32,
          }}
          pagination={{
            current: pageNumber,
            pageSize,
            total,
            showSizeChanger: true,
            pageSizeOptions: PRODUCT_PICKER_PAGE_SIZE_OPTIONS,
          }}
          onChange={(pagination, _filters, sorter, extra) => {
            if (extra.action === 'filter') {
              return
            }

            const nextSorter = Array.isArray(sorter) ? sorter[0] : sorter
            let nextSortBy = productSortBy
            let nextSortDescending = productSortDescending

            if (extra.action === 'sort') {
              if (
                (nextSorter?.order === 'ascend' || nextSorter?.order === 'descend') &&
                typeof nextSorter.field === 'string'
              ) {
                nextSortBy = nextSorter.field
                nextSortDescending = nextSorter.order === 'descend'
              } else {
                nextSortBy = 'Default'
                nextSortDescending = false
              }
            }

            void loadProducts({
              pageNumber: extra.action === 'sort' ? 1 : pagination.current || 1,
              pageSize: pagination.pageSize || pageSize,
              sortBy: nextSortBy,
              sortDescending: nextSortDescending,
            })
          }}
          scroll={{ y: 440 }}
        />
      </Space>
    </Modal>
  )
}

function BatchEditModal({ open, loading, selectedCount, onCancel, onConfirm }: BatchEditModalProps) {
  const { t } = useTranslation()
  const [type, setType] = useState<BatchEditType>('allocQuantity')
  const [allocQuantity, setAllocQuantity] = useState<number>()
  const [importPrice, setImportPrice] = useState<number>()
  const [isActive, setIsActive] = useState<boolean>(true)

  useEffect(() => {
    if (!open) {
      setType('allocQuantity')
      setAllocQuantity(undefined)
      setImportPrice(undefined)
      setIsActive(true)
    }
  }, [open])

  const handleOk = async () => {
    if (selectedCount === 0) {
      message.warning(t('storeOrders.detail.selectLinesFirst'))
      return
    }

    if (type === 'allocQuantity' && (allocQuantity === undefined || allocQuantity < 0)) {
      message.warning(t('storeOrders.detail.enterValidAllocQty'))
      return
    }

    if (type === 'importPrice' && (importPrice === undefined || importPrice < 0)) {
      message.warning(t('storeOrders.detail.enterValidImportPrice'))
      return
    }

    await onConfirm({
      type,
      allocQuantity,
      importPrice,
      isActive,
    })
  }

  return (
    <Modal
      title={t('storeOrders.batchModifyTitle')}
      open={open}
      destroyOnHidden
      okText={t('storeOrders.applyTo', { count: selectedCount })}
      cancelText={t('common.close')}
      confirmLoading={loading}
      onCancel={onCancel}
      onOk={() => void handleOk()}
    >
      <Space direction="vertical" size={16} style={{ width: '100%' }}>
        <Select
          value={type}
          options={[
            { value: 'allocQuantity', label: t('storeOrders.batchAllocQty') },
            { value: 'copyOrderQuantityToAllocQuantity', label: t('storeOrders.batchCopyOrderQuantityToAllocQuantity') },
            { value: 'importPrice', label: t('storeOrders.batchImportPrice') },
            { value: 'status', label: t('storeOrders.batchStatus') },
          ]}
          onChange={setType}
        />

        {type === 'allocQuantity' ? (
          <InputNumber
            min={0}
            precision={0}
            style={{ width: '100%' }}
            placeholder={t('storeOrders.newAllocQty')}
            value={allocQuantity}
            onChange={(value) => setAllocQuantity(value === null ? undefined : Number(value))}
          />
        ) : null}

        {type === 'importPrice' ? (
          <InputNumber
            min={0}
            precision={2}
            style={{ width: '100%' }}
            placeholder={t('storeOrders.newImportPrice')}
            value={importPrice}
            onChange={(value) => setImportPrice(value === null ? undefined : Number(value))}
          />
        ) : null}

        {type === 'status' ? (
          <Select
            value={isActive ? 'active' : 'inactive'}
            options={[
              { value: 'active', label: t('common.activeUpper') },
              { value: 'inactive', label: t('common.inactiveUpper') },
            ]}
            onChange={(value) => setIsActive(value === 'active')}
          />
        ) : null}
      </Space>
    </Modal>
  )
}

export default function StoreOrderDetailPage() {
  const { t, i18n } = useTranslation()
  const route = useStableRouteContext()
  const { active } = useKeepAliveContext()
  const isMobileLayout = useIsMobile()
  const canLoadDetail = shouldLoadStoreOrderDetailPage({
    keepAliveActive: active,
    isMobileLayout,
  })
  const location = useLocation()
  const navigate = useNavigate()
  const screens = Grid.useBreakpoint()
  const { access, currentUser } = useAuthStore()
  const isWarehouseStaffOnly =
    access.isWarehouseStaff &&
    !access.isAdmin &&
    !access.isWarehouseManager &&
    (access.hasRole('WarehouseStaff') || access.hasRole('仓库员工'))
  // 分店订货写入、状态流转和批量动作统一跟随仓库订货管理权限。
  const canUseWarehouseManagerActions = access.canManageWarehouseOrders && !isWarehouseStaffOnly
  // 只用于配货单等只读文档入口，不开放订单编辑、状态流转或明细写入能力。
  const canUseStoreOrderDocumentActions = access.isWarehouseStaff
  const canUseStoreOrderDetailExtraActions = canUseWarehouseManagerActions || canUseStoreOrderDocumentActions
  const id = route?.params.id || ''
  const isDesktop = Boolean(screens.xl)
  const detailRequestControllerRef = useRef<AbortController | null>(null)
  const stopDetailColumnResizeRef = useRef<(() => void) | null>(null)
  const detailColumnDragSensors = useSensors(
    useSensor(PointerSensor, {
      activationConstraint: {
        distance: 6,
      },
    }),
    useSensor(KeyboardSensor, {
      coordinateGetter: sortableKeyboardCoordinates,
    }),
  )
  // 记录当前订单和查询条件已完成首次加载，保活 Tab 恢复时避免同条件自动刷新。
  const loadedDetailIdRef = useRef<string | null>(null)
  const visibleDetailIdRef = useRef<string | null>(null)
  const lastLoadedDetailQueryKeyRef = useRef<string | null>(null)
  const lastLoadedStoresQueryKeyRef = useRef<string | null>(null)
  const stopPasteReplacePollingRef = useRef<(() => void) | null>(null)
  const detailInputRefs = useRef<Record<string, InputNumberRef | null>>({})

  useEffect(() => () => {
    stopDetailColumnResizeRef.current?.()
  }, [])

  const [detailLoadStatus, setDetailLoadStatus] = useState<DetailLoadStatus>('idle')
  const [detailErrorMessage, setDetailErrorMessage] = useState('')
  const [detail, setDetail] = useState<StoreOrderDetail | null>(null)
  const [stores, setStores] = useState<StoreDto[]>([])
  const [storesLoading, setStoresLoading] = useState(false)
  const [savingHeader, setSavingHeader] = useState(false)
  const [statusChanging, setStatusChanging] = useState(false)
  const [lineActionLoading, setLineActionLoading] = useState(false)
  const [batchLoading, setBatchLoading] = useState(false)
  const [pickerOpen, setPickerOpen] = useState(false)
  const [containerPickerOpen, setContainerPickerOpen] = useState(false)
  const [containerPickerLoading, setContainerPickerLoading] = useState(false)
  const [containerExistingProductCodes, setContainerExistingProductCodes] = useState<string[]>([])
  const [batchModalOpen, setBatchModalOpen] = useState(false)
  const [supplyNoticeTarget, setSupplyNoticeTarget] = useState<StoreOrderSupplyNoticeTarget | null>(null)
  const [supplyNoticeSaving, setSupplyNoticeSaving] = useState(false)
  const [pasteModalOpen, setPasteModalOpen] = useState(false)
  const [quickAddItemNumber, setQuickAddItemNumber] = useState('')
  const [quickAddQuantity, setQuickAddQuantity] = useState<number>(1)
  const [pasteData, setPasteData] = useState('')
  const [pasteTargetField, setPasteTargetField] = useState<StoreOrderPasteWriteTarget>('allocQuantity')
  const [detailItemFilter, setDetailItemFilter] = useState('')
  const [columnMapping, setColumnMapping] = useState({
    itemNumber: 0,
    quantity: 1,
    price: -1,
  })
  const [pastePreviewItems, setPastePreviewItems] = useState<StoreOrderPastePreviewItem[]>([])
  const [pastePreviewFilter, setPastePreviewFilter] = useState<StoreOrderPastePreviewFilter>('all')
  const [parsingPaste, setParsingPaste] = useState(false)
  const [submittingPaste, setSubmittingPaste] = useState(false)
  const [pasteOptimisticPending, setPasteOptimisticPending] = useState<StoreOrderPasteOptimisticPending | null>(null)
  const [refreshImportPriceLoading, setRefreshImportPriceLoading] = useState(false)
  const [detailPage, setDetailPage] = useState(1)
  const [detailPageSize, setDetailPageSize] = useState(STORE_ORDER_DETAIL_DEFAULT_PAGE_SIZE)
  const [detailStatFilter, setDetailStatFilter] = useState<StoreOrderDetailStatFilter>('all')
  const [detailColumnFilters, setDetailColumnFilters] = useState<StoreOrderDetailColumnFilters>({})
  const [detailSortField, setDetailSortField] = useState<DetailSortField>('locationCode')
  const [detailSortOrder, setDetailSortOrder] = useState<SortOrder>('ascend')
  const [detailColumnOrder, setDetailColumnOrder] = useState<StoreOrderDetailTableColumnKey[]>([])
  const [detailColumnWidths, setDetailColumnWidths] = useState<StoreOrderDetailColumnWidthMap>({})
  const [headerForm, setHeaderForm] = useState<{
    storeCode?: string
    orderDate?: string
    outboundDate?: string
    shippingFee?: number
    address: string
    contactEmail: string
    remarks: string
  }>({
    storeCode: undefined,
    orderDate: undefined,
    outboundDate: undefined,
    shippingFee: undefined,
    address: '',
    contactEmail: '',
    remarks: '',
  })
  const [storeContactBaseline, setStoreContactBaseline] = useState({
    address: '',
    contactEmail: '',
  })
  const [selectedLineKeys, setSelectedLineKeys] = useState<React.Key[]>([])
  // 拣货分配卡片加载后回传每行负责人，明细表“负责人”列用。
  const [lineAssignees, setLineAssignees] = useState<LineAssigneeMap>({})
  const [editingRows, setEditingRows] = useState<Record<string, { allocQuantity?: number; importPrice?: number }>>({})
  const initialOrderNo =
    typeof location.state === 'object' &&
    location.state !== null &&
    'orderNo' in location.state &&
    typeof location.state.orderNo === 'string'
      ? location.state.orderNo
      : ''
  const tabTitle = detail?.orderNo || initialOrderNo || t('storeOrders.orderDetail')

  useDynamicTabTitle(tabTitle)

  const cleanedDetailColumnFilters = useMemo(
    () => cleanStoreOrderDetailColumnFilters(detailColumnFilters),
    [detailColumnFilters],
  )

  // 明细表只请求当前页；翻页、筛选、排序都会带着 pageSize 重新向服务端取数。
  const detailQuery = useMemo<StoreOrderDetailQuery>(
    () => ({
      pageNumber: detailPage,
      pageSize: detailPageSize,
      keyword: detailItemFilter.trim() || undefined,
      statFilter: detailStatFilter === 'all' ? undefined : detailStatFilter,
      columnFilters: cleanedDetailColumnFilters,
      sortBy: detailSortField || undefined,
      sortDescending: detailSortField ? detailSortOrder === 'descend' : undefined,
    }),
    [cleanedDetailColumnFilters, detailItemFilter, detailPage, detailPageSize, detailSortField, detailSortOrder, detailStatFilter],
  )
  const detailQueryKey = useMemo(() => JSON.stringify(detailQuery), [detailQuery])
  const storesQueryKey = useMemo(
    () =>
      JSON.stringify({
        id,
        isAdmin: access.isAdmin,
        isWarehouseManager: access.isWarehouseManager,
        userGUID: currentUser?.userGUID ?? '',
      }),
    [access.isAdmin, access.isWarehouseManager, currentUser?.userGUID, id],
  )

  const loadDetail = async (showLoading = true) => {
    if (!id) {
      return
    }

    detailRequestControllerRef.current?.abort()
    const currentController = new AbortController()
    detailRequestControllerRef.current = currentController

    if (showLoading) {
      setDetailLoadStatus('loading')
    }

    try {
      const result = await getStoreOrderDetail(id, detailQuery, detailRequestControllerRef.current.signal)

      if (detailRequestControllerRef.current !== currentController) {
        return
      }

      if (!result) {
        loadedDetailIdRef.current = null
        visibleDetailIdRef.current = null
        lastLoadedDetailQueryKeyRef.current = null
        setDetail(null)
        setDetailLoadStatus('notFound')
        setDetailErrorMessage('')
        return
      }

      const maxPage = Math.max(1, Math.ceil((result.itemsTotal ?? result.items.length) / detailPageSize))
      if (detailPage > maxPage) {
        // 删除或筛选后当前页可能超过服务端总页数，回退后由 effect 重新请求有效页。
        setDetailPage(maxPage)
        return
      }

      loadedDetailIdRef.current = result.orderGUID || id
      visibleDetailIdRef.current = result.orderGUID || id
      lastLoadedDetailQueryKeyRef.current = detailQueryKey
      setDetail(result)
      setHeaderForm({
        storeCode: result?.storeCode,
        orderDate: result?.orderDate,
        outboundDate: result?.outboundDate,
        shippingFee: result?.shippingFee,
        address: result?.storeAddress || '',
        contactEmail: result?.storeContactEmail || '',
        remarks: result?.remarks || '',
      })
      setStoreContactBaseline({
        address: result?.storeAddress || '',
        contactEmail: result?.storeContactEmail || '',
      })
      setEditingRows({})
      setDetailLoadStatus('loaded')
      setDetailErrorMessage('')
    } catch (error) {
      if (isAbortError(error)) {
        return
      }

      if (detailRequestControllerRef.current !== currentController) {
        return
      }

      console.error(error)
      const errorMessage = error instanceof Error ? error.message : t('storeOrders.detail.loadDetailFailed')
      if (showLoading) {
        visibleDetailIdRef.current = null
        lastLoadedDetailQueryKeyRef.current = null
        setDetail(null)
        setDetailLoadStatus('error')
        setDetailErrorMessage(errorMessage)
      } else {
        setDetailErrorMessage(errorMessage)
      }
      message.error(errorMessage)
    } finally {
      if (detailRequestControllerRef.current === currentController) {
        detailRequestControllerRef.current = null
      }
    }
  }

  const loadStores = async () => {
    if (!canUseWarehouseManagerActions) {
      // 仓库员工只能查看当前订单分店，不能访问完整分店下拉接口；避免辅助数据 403 阻断明细展示。
      setStores([])
      lastLoadedStoresQueryKeyRef.current = storesQueryKey
      return
    }

    setStoresLoading(true)
    try {
      const result = await getStores({
        page: 1,
        pageSize: 300,
        isActive: true,
        sortField: 'storeName',
        sortOrder: 'ascend',
      })
      setStores(result.items)
      lastLoadedStoresQueryKeyRef.current = storesQueryKey
    } catch (error) {
      console.error(error)
      // 分店下拉是辅助数据，加载失败不应误导用户以为订货明细主数据失败。
      message.warning(t('storeOrders.detail.loadStoreOptionsFailed'))
    } finally {
      setStoresLoading(false)
    }
  }

  useEffect(() => {
    if (!canLoadDetail) return

    if (!id) {
      return
    }
    // 隐藏的 KeepAlive 节点也会收到全局路由变化，必须只让当前激活节点发起请求。
    // 保活 Tab 切回时只有同订单且同查询条件命中才跳过，分页/搜索/排序变化必须重新请求。
    if (shouldSkipDetailAutoReload({
      requestedDetailId: id,
      loadedDetailId: loadedDetailIdRef.current,
      visibleDetailId: visibleDetailIdRef.current,
      requestedDetailQueryKey: detailQueryKey,
      loadedDetailQueryKey: lastLoadedDetailQueryKeyRef.current,
    })) {
      return
    }

    const shouldShowInitialLoading = shouldShowStoreOrderDetailInitialLoading({
      requestedOrderId: id,
      loadedOrderId: loadedDetailIdRef.current,
      visibleDetailId: visibleDetailIdRef.current,
    })
    void loadDetail(shouldShowInitialLoading)
    return () => {
      detailRequestControllerRef.current?.abort()
    }
  }, [canLoadDetail, detailQuery, detailQueryKey, id])

  useEffect(() => {
    if (!canLoadDetail) return

    if (!id) {
      return
    }
    if (lastLoadedStoresQueryKeyRef.current === storesQueryKey) {
      return
    }
    void loadStores()
  }, [canLoadDetail, storesQueryKey, id])

  useEffect(() => {
    if (!containerPickerOpen) {
      setContainerExistingProductCodes([])
    }
  }, [containerPickerOpen])

  useEffect(() => {
    return () => {
      stopPasteReplacePollingRef.current?.()
      stopPasteReplacePollingRef.current = null
      setPasteOptimisticPending(null)
    }
  }, [id])

  const handleOpenContainerPicker = async () => {
    if (!detail?.orderGUID) {
      return
    }

    setContainerPickerLoading(true)
    try {
      // 必须先加载整单商品编码，避免分页详情页只按当前页做货柜选品去重。
      const productCodes = await getStoreOrderDetailProductCodes(detail.orderGUID)
      setContainerExistingProductCodes(productCodes)
      setContainerPickerOpen(true)
    } catch (error) {
      console.error(error)
      setContainerExistingProductCodes([])
      setContainerPickerOpen(false)
      message.error(error instanceof Error ? error.message : t('storeOrders.detail.loadDetailFailed'))
    } finally {
      setContainerPickerLoading(false)
    }
  }

  const storeOptions = useMemo(
    () => {
      const options = stores.map((item) => ({
        value: item.storeCode,
        label: `${item.storeName} (${item.storeCode})`,
      }))

      if (headerForm.storeCode && !options.some((item) => item.value === headerForm.storeCode)) {
        const currentStoreLabel = detail?.storeName
          ? `${detail.storeName} (${headerForm.storeCode})`
          : `${headerForm.storeCode} (${t('column.currentStore')})`

        options.push({
          value: headerForm.storeCode,
          label: currentStoreLabel,
        })
      }

      return options
    },
    [detail?.storeName, headerForm.storeCode, stores, t],
  )

  const totalAllocQuantity = useMemo(() => {
    const savedTotal =
      detail?.totalAllocQuantity ?? detail?.items?.reduce((sum, item) => sum + Number(item.allocQuantity ?? 0), 0) ?? 0
    const draftDelta =
      detail?.items?.reduce((sum, item) => {
        const editedAllocQuantity = editingRows[item.detailGUID]?.allocQuantity
        if (editedAllocQuantity === undefined) {
          return sum
        }
        return sum + Number(editedAllocQuantity) - Number(item.allocQuantity ?? 0)
      }, 0) ?? 0
    return savedTotal + draftDelta
  }, [detail?.items, detail?.totalAllocQuantity, editingRows])

  const totalOrderVolume =
    detail?.totalOrderVolume ??
    detail?.totalVolume ??
    detail?.items?.reduce(
      (sum, item) =>
        sum +
        (item.orderVolume ??
          item.totalVolume ??
          ((item.volume ?? 0) * Number(item.quantity ?? 0))),
      0,
    ) ??
    0

  const totalAllocVolume = useMemo(() => {
    const savedTotal =
      detail?.totalAllocVolume ??
      detail?.items?.reduce((sum, item) => sum + (item.allocVolume ?? ((item.volume ?? 0) * Number(item.allocQuantity ?? 0))), 0) ??
      0
    const draftDelta =
      detail?.items?.reduce((sum, item) => {
        const editedAllocQuantity = editingRows[item.detailGUID]?.allocQuantity
        if (editedAllocQuantity === undefined || item.volume === undefined || item.volume === null) {
          return sum
        }
        return sum + Number(item.volume) * (Number(editedAllocQuantity) - Number(item.allocQuantity ?? 0))
      }, 0) ?? 0
    return savedTotal + draftDelta
  }, [detail?.items, detail?.totalAllocVolume, editingRows])

  const estimatedSalesAmount = useMemo(
    () =>
      detail?.items.reduce((sum, line) => {
        const allocQuantity = editingRows[line.detailGUID]?.allocQuantity ?? line.allocQuantity ?? 0
        return sum + Number(line.price ?? 0) * Number(allocQuantity)
      }, 0) ?? 0,
    [detail?.items, editingRows],
  )

  const draftTotalImportAmount = useMemo(() => {
    const savedTotal =
      detail?.totalAllocatedImportAmount ??
      detail?.items.reduce(
        (sum, line) =>
          sum + (line.allocatedImportAmount ?? Number(line.allocQuantity ?? 0) * Number(line.importPrice ?? 0)),
        0,
      ) ??
      0
    const draftDelta =
      detail?.items.reduce((sum, line) => {
        const edited = editingRows[line.detailGUID]
        if (!edited || (edited.allocQuantity === undefined && edited.importPrice === undefined)) {
          return sum
        }
        const savedAmount = line.allocatedImportAmount ?? Number(line.allocQuantity ?? 0) * Number(line.importPrice ?? 0)
        const allocQuantity = edited.allocQuantity ?? line.allocQuantity ?? 0
        const importPrice = edited.importPrice ?? line.importPrice ?? 0
        return sum + Number(allocQuantity) * Number(importPrice) - Number(savedAmount)
      }, 0) ?? 0
    return savedTotal + draftDelta
  }, [detail?.items, detail?.totalAllocatedImportAmount, editingRows])

  const gstAmount = useMemo(
    () => Number((Number(draftTotalImportAmount) * 0.1).toFixed(2)),
    [draftTotalImportAmount],
  )

  const validPastePreviewCount = useMemo(
    () => buildPasteSubmitItems(pastePreviewItems).length,
    [pastePreviewItems],
  )
  const pasteQuantityMode = resolvePasteQuantityMode(pasteTargetField)
  const pasteApiTargetField = resolvePasteTargetField(pasteTargetField)

  const filteredPastePreviewItems = useMemo(
    () => filterPastePreviewItems(pastePreviewItems, pastePreviewFilter),
    [pastePreviewFilter, pastePreviewItems],
  )

  const existingPastePreviewCount = useMemo(
    () => pastePreviewItems.filter((item) => item.status === 'existing' && item.valid).length,
    [pastePreviewItems],
  )

  const selectedLines = useMemo(
    () => detail?.items.filter((item) => selectedLineKeys.includes(item.detailGUID)) ?? [],
    [detail?.items, selectedLineKeys],
  )

  const getEditedLinePayloads = (syncImportPrice = true) =>
    (detail?.items ?? []).reduce<EditedLinePayload[]>((payloads, line) => {
      const edited = editingRows[line.detailGUID]
      if (!edited) {
        return payloads
      }

      const allocQuantityChanged =
        edited.allocQuantity !== undefined && Number(edited.allocQuantity) !== Number(line.allocQuantity ?? 0)
      const importPriceChanged =
        edited.importPrice !== undefined && Number(edited.importPrice) !== Number(line.importPrice ?? 0)

      if (!allocQuantityChanged && !importPriceChanged) {
        return payloads
      }

      payloads.push({
        detailGUID: line.detailGUID,
        productCode: line.productCode,
        quantity: allocQuantityChanged ? edited.allocQuantity : undefined,
        // 进口价本身始终保存到订单明细；syncImportPrice 只控制是否同步商品/分店主档。
        importPrice: importPriceChanged ? edited.importPrice : undefined,
        syncImportPrice: importPriceChanged ? syncImportPrice : undefined,
        importPriceChanged,
      })

      return payloads
    }, [])

  const editedLineCount = useMemo(() => getEditedLinePayloads().length, [detail?.items, editingRows])

  const statSummary = useMemo(() => {
    const items = detail?.items ?? []
    const total = detail?.itemsTotal ?? items.length

    return {
      all: total,
      orderedNotShipped: detail?.orderedNotShippedCount ?? items.filter(isOrderedNotShipped).length,
      shippedWithoutOrder: detail?.shippedWithoutOrderCount ?? items.filter(isShippedWithoutOrder).length,
    }
  }, [detail?.items, detail?.itemsTotal, detail?.orderedNotShippedCount, detail?.shippedWithoutOrderCount])

  const statusLabelMap = useMemo(
    () => ({
      0: t('storeOrders.statusShoppingCart'),
      1: t('storeOrders.statusSubmitted'),
      2: t('storeOrders.statusCompleted'),
      3: t('storeOrders.statusPicking'),
    }),
    [t],
  )

  const orderStatusChangeOptions = useMemo(
    () =>
      [
        StoreOrderFlowStatus.Submitted,
        StoreOrderFlowStatus.Picking,
        StoreOrderFlowStatus.Completed,
      ].map((value) => ({
        value,
        label: statusLabelMap[value],
        disabled: value === detail?.flowStatus,
      })),
    [detail?.flowStatus, statusLabelMap],
  )

  const {
    canEditOrder,
    canEditOutboundDate,
    canStartPicking,
    canCompleteOrder,
    isReadonlyOrder,
  } = deriveStoreOrderDetailPermissions(detail?.flowStatus)
  const isPasteOptimisticPreviewActive = pasteOptimisticPending?.orderGUID === detail?.orderGUID

  function ensureOrderEditable() {
    if (canUseWarehouseManagerActions && canEditOrder) {
      return true
    }

    message.warning(t('storeOrders.detail.orderReadonlyRefresh'))
    return false
  }

  const hasImportPriceChanged = (line: StoreOrderDetailLine) => {
    const edited = editingRows[line.detailGUID]
    return edited?.importPrice !== undefined && Number(edited.importPrice) !== Number(line.importPrice ?? 0)
  }

  const confirmImportPriceSync = () =>
    new Promise<boolean | null>((resolve) => {
      let syncImportPrice = true
      Modal.confirm({
        title: t('storeOrders.detail.importPriceSyncConfirmTitle'),
        content: (
          <Space direction="vertical" size={8}>
            <Typography.Text>{t('storeOrders.detail.importPriceSyncConfirmContent')}</Typography.Text>
            <Checkbox
              defaultChecked
              onChange={(event) => {
                syncImportPrice = event.target.checked
              }}
            >
              {t('storeOrders.detail.syncImportPriceCheckbox')}
            </Checkbox>
          </Space>
        ),
        okText: t('common.confirm'),
        cancelText: t('common.cancel'),
        onOk: () => resolve(syncImportPrice),
        onCancel: () => resolve(null),
      })
    })

  const handleSaveHeader = async () => {
    if (!detail) {
      return
    }
    if (!canUseWarehouseManagerActions) {
      message.warning(t('storeOrders.detail.orderReadonlyRefresh'))
      return
    }
    setSavingHeader(true)
    try {
      if (canEditOrder) {
        await updateStoreOrderHeader({
          orderGUID: detail.orderGUID,
          storeCode: headerForm.storeCode,
          orderDate: headerForm.orderDate,
          shippingFee: headerForm.shippingFee,
          remarks: headerForm.remarks,
        })
        const nextStoreAddress = headerForm.address.trim() ? headerForm.address : ''
        const nextStoreContactEmail = headerForm.contactEmail.trim() ? headerForm.contactEmail : ''
        const hasStoreContactChanged =
          (nextStoreAddress || '') !== storeContactBaseline.address ||
          (nextStoreContactEmail || '') !== storeContactBaseline.contactEmail

        if (hasStoreContactChanged && detail.orderGUID && headerForm.storeCode) {
          await updateStoreOrderStoreContact({
            orderGUID: detail.orderGUID,
            storeCode: headerForm.storeCode,
            address: nextStoreAddress,
            contactEmail: nextStoreContactEmail,
          })
          // 保存成功后把当前编辑值视为这家分店的最新默认值，避免继续误判成旧默认值。
          setStoreContactBaseline({
            address: nextStoreAddress,
            contactEmail: nextStoreContactEmail,
          })
        }
      }

      const currentOutboundDate = detail.outboundDate?.slice(0, 10) || ''
      const nextOutboundDate = headerForm.outboundDate?.slice(0, 10) || ''
      if (currentOutboundDate !== nextOutboundDate) {
        await updateStoreOrderOutboundDate({
          orderGUID: detail.orderGUID,
          outboundDate: nextOutboundDate || undefined,
          completeOrder: false,
        })
      }

      message.success(t('storeOrders.detail.headerSaveSuccess'))
      await loadDetail(false)
    } catch (error) {
      console.error(error)
      message.error(error instanceof Error ? error.message : t('storeOrders.detail.headerSaveFailed'))
    } finally {
      setSavingHeader(false)
    }
  }

  const handleQuickAdd = async () => {
    if (!detail) {
      return
    }
    if (!ensureOrderEditable()) {
      return
    }
    const normalizedItemNumber = quickAddItemNumber.trim()
    if (!normalizedItemNumber) {
      message.warning(t('storeOrders.detail.enterItemNumber'))
      return
    }
    if (!quickAddQuantity || quickAddQuantity <= 0) {
      message.warning(t('storeOrders.detail.enterValidAllocQty'))
      return
    }

    setLineActionLoading(true)
    try {
      const result = await getStoreOrderProducts({
        itemNumber: normalizedItemNumber,
        // 快速加入允许下架商品按货号加入，但后端仍会排除已删除商品。
        includeInactiveWarehouseProducts: true,
        pageNumber: 1,
        pageSize: 50,
        sortBy: 'Default',
      })
      const exactMatches = result.items.filter(
        (item) => item.itemNumber?.trim().toLowerCase() === normalizedItemNumber.toLowerCase(),
      )

      if (exactMatches.length === 0) {
        message.warning(t('storeOrders.detail.noExactItemMatch'))
        return
      }

      if (exactMatches.length > 1) {
        message.warning(t('storeOrders.detail.multipleExactItemMatches'))
        return
      }

      const target = exactMatches[0]

      await addStoreOrderLine({
        orderGUID: detail.orderGUID,
        productCode: target.productCode,
        quantity: quickAddQuantity,
      })
      message.success(t('storeOrders.detail.productAdded'))
      setQuickAddItemNumber('')
      setQuickAddQuantity(1)
      await loadDetail(false)
    } catch (error) {
      console.error(error)
      message.error(error instanceof Error ? error.message : t('storeOrders.detail.quickAddFailed'))
    } finally {
      setLineActionLoading(false)
    }
  }

  const handlePickerConfirm = async (items: Array<{ productCode: string; quantity: number; importPrice?: number }>) => {
    if (!detail) {
      return
    }
    if (!ensureOrderEditable()) {
      return
    }
    setLineActionLoading(true)
    try {
      if (items.length === 1) {
        await addStoreOrderLine({
          orderGUID: detail.orderGUID,
          productCode: items[0].productCode,
          quantity: items[0].quantity,
        })
      } else {
        await batchAddStoreOrderLines({
          orderGUID: detail.orderGUID,
          items,
        })
      }
      message.success(t('storeOrders.addProductsSuccess', { count: items.length }))
      setPickerOpen(false)
      setContainerPickerOpen(false)
      await loadDetail(false)
    } catch (error) {
      console.error(error)
      message.error(error instanceof Error ? error.message : t('storeOrders.detail.addProductsFailed'))
    } finally {
      setLineActionLoading(false)
    }
  }

  const handleSaveLine = async (line: StoreOrderDetailLine) => {
    if (!detail) {
      return
    }
    if (!ensureOrderEditable()) {
      return
    }

    const edited = editingRows[line.detailGUID]
    const allocQuantity = edited?.allocQuantity ?? line.allocQuantity ?? 0
    const importPrice = edited?.importPrice ?? line.importPrice

    if (allocQuantity < 0) {
      message.warning(t('storeOrders.detail.allocQtyNonNegative'))
      return
    }

    const importPriceChanged = hasImportPriceChanged(line)
    let syncImportPrice = true
    if (importPriceChanged) {
      const syncChoice = await confirmImportPriceSync()
      if (syncChoice === null) {
        return
      }
      syncImportPrice = syncChoice
    }

    setLineActionLoading(true)
    try {
      await updateStoreOrderLine({
        orderGUID: detail.orderGUID,
        productCode: line.productCode,
        allocQuantity,
        // 取消勾选只取消主档同步，不取消订单明细价格保存。
        importPrice: importPriceChanged ? importPrice : undefined,
        syncImportPrice: importPriceChanged ? syncImportPrice : undefined,
      })
      message.success(t('storeOrders.detail.lineSaved'))
      await loadDetail(false)
    } catch (error) {
      console.error(error)
      message.error(error instanceof Error ? error.message : t('storeOrders.detail.lineSaveFailed'))
    } finally {
      setLineActionLoading(false)
    }
  }

  const handleSaveEditedLines = async () => {
    if (!detail) {
      return
    }
    if (!ensureOrderEditable()) {
      return
    }

    const payloads = getEditedLinePayloads()
    if (!payloads.length) {
      message.warning(t('storeOrders.detail.noEditedLines'))
      return
    }

    const invalidQuantity = payloads.find((item) => item.quantity !== undefined && item.quantity < 0)
    if (invalidQuantity) {
      message.warning(t('storeOrders.detail.allocQtyNonNegative'))
      return
    }

    let syncImportPrice = true
    if (payloads.some((item) => item.importPriceChanged)) {
      const syncChoice = await confirmImportPriceSync()
      if (syncChoice === null) {
        return
      }
      syncImportPrice = syncChoice
      const nextPayloads = getEditedLinePayloads(syncImportPrice)
      payloads.splice(0, payloads.length, ...nextPayloads)
    }

    setLineActionLoading(true)
    try {
      await batchUpdateStoreOrderLines({
        orderGUID: detail.orderGUID,
        items: payloads.map((item) => ({
          detailGUID: item.detailGUID,
          productCode: item.productCode,
          quantity: item.quantity,
          importPrice: item.importPrice,
          syncImportPrice: item.syncImportPrice,
        })),
      })
      message.success(t('storeOrders.detail.editedLinesSaved', { count: payloads.length }))
      const savedDetailGUIDs = new Set(payloads.map((item) => item.detailGUID))
      setEditingRows((current) => {
        const next = { ...current }
        savedDetailGUIDs.forEach((detailGUID) => {
          delete next[detailGUID]
        })
        return next
      })
      await loadDetail(false)
    } catch (error) {
      console.error(error)
      message.error(error instanceof Error ? error.message : t('storeOrders.detail.batchUpdateFailed'))
    } finally {
      setLineActionLoading(false)
    }
  }

  const handleRemoveLine = async (line: StoreOrderDetailLine) => {
    if (!detail) {
      return
    }
    if (!ensureOrderEditable()) {
      return
    }
    setLineActionLoading(true)
    try {
      await removeStoreOrderLine({
        orderGUID: detail.orderGUID,
        detailGUID: line.detailGUID,
      })
      message.success(t('storeOrders.detail.lineDeleted'))
      setSelectedLineKeys((current) => current.filter((key) => key !== line.detailGUID))
      await loadDetail(false)
    } catch (error) {
      console.error(error)
      message.error(error instanceof Error ? error.message : t('storeOrders.detail.lineDeleteFailed'))
    } finally {
      setLineActionLoading(false)
    }
  }

  const handleToggleLineStatus = async (line: StoreOrderDetailLine, supplyNotice?: SupplyNoticeInput) => {
    if (!ensureOrderEditable()) {
      return
    }
    const nextIsActive = !line.isActive
    // 下架前必须先填写供货说明（与仓库商品页批量上下架一致），说明随下架同一请求提交；上架不需要。
    if (!supplyNotice && requiresDelistSupplyNotice(nextIsActive, line.isActive)) {
      setSupplyNoticeTarget({ kind: 'line', productCodes: [line.productCode], line })
      return
    }
    setLineActionLoading(true)
    try {
      await updateStoreOrderProductStatus({
        productCode: line.productCode,
        isActive: nextIsActive,
        ...(supplyNotice && !nextIsActive ? { supplyNotice } : {}),
      })
      message.success(t('storeOrders.detail.productStatusUpdated', { status: line.isActive ? t('common.inactiveUpper') : t('common.activeUpper') }))
      await loadDetail(false)
    } catch (error) {
      console.error(error)
      message.error(error instanceof Error ? error.message : t('storeOrders.detail.productStatusUpdateFailed'))
    } finally {
      setLineActionLoading(false)
    }
  }

  const handleBatchConfirm = async (payload: BatchEditPayload, supplyNotice?: SupplyNoticeInput) => {
    if (!detail || selectedLines.length === 0) {
      return
    }
    if (!ensureOrderEditable()) {
      return
    }
    // 批量改状态为下架时同样先填写供货说明；填写完成后带着说明回到这里继续提交。
    if (payload.type === 'status' && !supplyNotice && requiresDelistSupplyNotice(payload.isActive ?? true)) {
      setSupplyNoticeTarget({
        kind: 'batch',
        productCodes: Array.from(new Set(selectedLines.map((item) => item.productCode))),
        payload,
      })
      return
    }

    const copyOrderQuantityPayload =
      payload.type === 'copyOrderQuantityToAllocQuantity'
        ? buildBatchCopyOrderQuantityPayload(selectedLines)
        : null
    if (copyOrderQuantityPayload?.shouldConfirm) {
      const confirmed = await new Promise<boolean>((resolve) => {
        Modal.confirm({
          title: t('storeOrders.batchCopyOrderQuantityConfirmTitle'),
          content: (
            <Space direction="vertical" size={8}>
              <Typography.Text>
                {t('storeOrders.batchCopyOrderQuantityConfirmContent', { count: selectedLines.length })}
              </Typography.Text>
              {copyOrderQuantityPayload.overwriteCount > 0 ? (
                <Typography.Text type="warning">
                  {t('storeOrders.batchCopyOrderQuantityOverwriteWarning', {
                    count: copyOrderQuantityPayload.overwriteCount,
                  })}
                </Typography.Text>
              ) : null}
              {copyOrderQuantityPayload.zeroOrderQuantityCount > 0 ? (
                <Typography.Text type="danger">
                  {t('storeOrders.batchCopyOrderQuantityZeroWarning', {
                    count: copyOrderQuantityPayload.zeroOrderQuantityCount,
                  })}
                </Typography.Text>
              ) : null}
            </Space>
          ),
          okText: t('common.confirm'),
          cancelText: t('common.cancel'),
          onOk: () => resolve(true),
          onCancel: () => resolve(false),
        })
      })

      if (!shouldSubmitBatchCopyOrderQuantity(copyOrderQuantityPayload, confirmed)) {
        return
      }
    }

    setBatchLoading(true)
    try {
      let successMessage = t('storeOrders.batchUpdateSuccess', { count: selectedLines.length })
      let successMessageType: 'success' | 'info' = 'success'
      if (payload.type === 'status') {
        const nextIsActive = payload.isActive ?? true
        await batchUpdateStoreOrderProductStatus({
          productCodes: selectedLines.map((item) => item.productCode),
          isActive: nextIsActive,
          ...(supplyNotice && !nextIsActive ? { supplyNotice } : {}),
        })
      } else if (payload.type === 'copyOrderQuantityToAllocQuantity' && copyOrderQuantityPayload) {
        const changedCopyLines = selectedLines.filter((line) => {
          const currentAllocQuantity = editingRows[line.detailGUID]?.allocQuantity ?? line.allocQuantity ?? 0
          return Number(currentAllocQuantity) !== Number(line.quantity ?? 0)
        })
        // 批量复制只写入页面草稿，用户确认无误后再通过“整单保存”统一提交后端。
        setEditingRows((current) => {
          const next = { ...current }
          changedCopyLines.forEach((line) => {
            next[line.detailGUID] = {
              ...next[line.detailGUID],
              allocQuantity: Number(line.quantity ?? 0),
            }
          })
          return next
        })
        successMessage =
          changedCopyLines.length > 0
            ? t('storeOrders.batchCopyOrderQuantityDraftSuccess', { count: changedCopyLines.length })
            : t('storeOrders.batchCopyOrderQuantityNoChange')
        successMessageType = changedCopyLines.length > 0 ? 'success' : 'info'
      } else {
        await batchUpdateStoreOrderLines({
          orderGUID: detail.orderGUID,
          items: selectedLines.map((item) => ({
            productCode: item.productCode,
            quantity: payload.type === 'allocQuantity' ? payload.allocQuantity : undefined,
            importPrice: payload.type === 'importPrice' ? payload.importPrice : undefined,
          })),
        })
      }

      if (successMessageType === 'info') {
        message.info(successMessage)
      } else {
        message.success(successMessage)
      }
      setBatchModalOpen(false)
      setSelectedLineKeys([])
      if (payload.type !== 'copyOrderQuantityToAllocQuantity') {
        await loadDetail(false)
      }
    } catch (error) {
      console.error(error)
      message.error(error instanceof Error ? error.message : t('storeOrders.detail.batchUpdateFailed'))
    } finally {
      setBatchLoading(false)
    }
  }

  // 说明弹窗提交：按来源转交行开关或批量改状态，说明随下架同一请求提交；取消则不下架。
  const handleSupplyNoticeSubmit = async (notice: SupplyNoticeInput) => {
    if (!supplyNoticeTarget) {
      return
    }
    setSupplyNoticeSaving(true)
    try {
      if (supplyNoticeTarget.kind === 'line') {
        await handleToggleLineStatus(supplyNoticeTarget.line, notice)
      } else {
        await handleBatchConfirm(supplyNoticeTarget.payload, notice)
      }
      setSupplyNoticeTarget(null)
    } finally {
      setSupplyNoticeSaving(false)
    }
  }

  const handleResetDetailDefaultSort = () => {
    // 默认排序与配货单保持一致：空货位在前，再按货位升序查看整单明细。
    setSelectedLineKeys([])
    setDetailPage(1)
    setDetailSortField('locationCode')
    setDetailSortOrder('ascend')
  }

  const handleRefreshImportPricesFromWarehouse = () => {
    if (!detail || !canUseWarehouseManagerActions) {
      return
    }

    const targetDetailGUIDs = selectedLineKeys.map(String).filter(Boolean)
    const isSelectedScope = targetDetailGUIDs.length > 0
    const selectedCount = targetDetailGUIDs.length

    Modal.confirm({
      title: t('storeOrders.detail.refreshImportPricesTitle'),
      content: (
        <Space direction="vertical" size={8}>
          <Typography.Text>
            {isSelectedScope
              ? t('storeOrders.detail.refreshImportPricesSelectedContent', { count: selectedCount })
              : t('storeOrders.detail.refreshImportPricesWholeOrderContent')}
          </Typography.Text>
          <Typography.Text type="secondary">
            {t('storeOrders.detail.refreshImportPricesWarning')}
          </Typography.Text>
        </Space>
      ),
      okText: t('common.confirm'),
      cancelText: t('common.cancel'),
      onOk: async () => {
        setRefreshImportPriceLoading(true)
        try {
          const result = await refreshStoreOrderImportPrices({
            orderGUID: detail.orderGUID,
            detailGUIDs: isSelectedScope ? targetDetailGUIDs : undefined,
          })
          message.success(
            t('storeOrders.detail.refreshImportPricesSuccess', {
              updated: result.updatedCount,
              unchanged: result.unchangedCount,
              skipped: result.skippedCount,
              missing: result.missingWarehousePriceCount,
            }),
          )
          setSelectedLineKeys([])
          setEditingRows({})
          await loadDetail(false)
        } catch (error) {
          console.error(error)
          message.error(error instanceof Error ? error.message : t('storeOrders.detail.refreshImportPricesFailed'))
        } finally {
          setRefreshImportPriceLoading(false)
        }
      },
    })
  }

  const resetPasteState = (targetField: StoreOrderPasteWriteTarget = 'allocQuantity') => {
    setPasteData('')
    setPasteTargetField(targetField)
    setColumnMapping({
      itemNumber: 0,
      quantity: 1,
      price: -1,
    })
    setPastePreviewItems([])
    setPastePreviewFilter('all')
  }

  const handleChangePasteTargetField = (targetField: StoreOrderPasteWriteTarget) => {
    setPasteTargetField(targetField)
    // 写入目标会影响空数量解析，切换后清空预览，避免沿用旧模式的结果。
    setPastePreviewItems([])
    setPastePreviewFilter('all')
  }

  const handleParsePasteData = async () => {
    if (!detail) {
      return
    }
    if (!pasteData.trim()) {
      message.warning(t('storeOrders.detail.pasteExcelFirst'))
      return
    }

    setParsingPaste(true)
    try {
      const items = parseStoreOrderPasteRows(pasteData, columnMapping, pasteQuantityMode)

      if (!items.length) {
        message.warning(t('storeOrders.detail.noValidPasteItems'))
        setPastePreviewItems([])
        return
      }

      const [lookupResult, fullDetail] = await Promise.all([
        batchLookupStoreOrderProducts({
          codes: Array.from(new Set(items.map((item) => item.itemNumber.trim()).filter(Boolean))),
        }),
        getStoreOrderDetailFull(detail.orderGUID),
      ])
      const existingLines = (fullDetail?.items ?? []).map((item) => ({
        productCode: item.productCode,
        quantity: item.quantity,
        allocQuantity: item.allocQuantity,
      }))
      const preview = createPastePreviewItems(items, lookupResult, existingLines)

      setPastePreviewItems(preview)
      setPastePreviewFilter('all')
      message.success(t('storeOrders.detail.pasteParseSuccess', { total: items.length, valid: buildPasteSubmitItems(preview).length }))
    } catch (error) {
      console.error(error)
      message.error(error instanceof Error ? error.message : t('storeOrders.detail.pasteParseFailed'))
    } finally {
      setParsingPaste(false)
    }
  }

  const handleConfirmPaste = async () => {
    if (!detail) {
      return
    }
    if (!ensureOrderEditable()) {
      return
    }

    const validItems = buildPasteSubmitItems(pastePreviewItems, { quantityMode: pasteQuantityMode })

    if (!validItems.length) {
      message.warning(t('storeOrders.detail.noValidImportProducts'))
      return
    }

    setSubmittingPaste(true)
    try {
      const orderGUID = detail.orderGUID
      stopPasteReplacePollingRef.current?.()
      stopPasteReplacePollingRef.current = null
      setPasteOptimisticPending(null)

      const createdJob = await createStoreOrderPasteReplaceJob({
        orderGUID,
        targetField: pasteApiTargetField,
        items: validItems,
      })
      if (!createdJob.jobId) {
        message.error(createdJob.message || t('storeOrders.detail.pasteImportFailed'))
        return
      }

      const optimisticRows = buildPasteOptimisticRows({
        currentItems: detail.items,
        previewItems: pastePreviewItems,
        targetField: pasteApiTargetField,
        quantityMode: pasteQuantityMode,
      })

      // 后端批量导入改为后台 job，前端只负责等待终态并刷新当前订单。
      const poller = createStoreOrderPasteReplaceJobPoller({
        jobId: createdJob.jobId,
        getJob: getStoreOrderPasteReplaceJob,
      })
      stopPasteReplacePollingRef.current = poller.stop

      // Job 创建成功后先展示 Excel 预览行，避免大批量写入期间表格长时间没有反馈。
      setPasteOptimisticPending({
        jobId: createdJob.jobId,
        orderGUID,
      })
      setDetail((current) =>
        current?.orderGUID === orderGUID
          ? applyPasteOptimisticRowsToDetail(current, optimisticRows)
          : current,
      )
      setDetailPage(1)
      setSelectedLineKeys([])
      setEditingRows({})
      setPasteModalOpen(false)
      resetPasteState(pasteTargetField)
      message.success(t('storeOrders.detail.pasteJobSubmitted', '已先显示本次 Excel 预览，后台正在写入；完成后会自动刷新确认。'))

      void poller.promise
        .then(async (result) => {
          if (stopPasteReplacePollingRef.current === poller.stop) {
            stopPasteReplacePollingRef.current = null
          }
          setPasteOptimisticPending((current) => resolvePasteOptimisticPendingAfterJob(current, result))

          if (result.status === 'Failed') {
            notification.error({
              message: t('storeOrders.detail.pasteImportFailed'),
              description: result.message,
            })
            if (visibleDetailIdRef.current === orderGUID) {
              await loadDetail(false)
            }
            return
          }

          notification.success({
            message: t('storeOrders.pasteUpdateSuccess', { count: result.importedCount ?? validItems.length }),
            description: result.skippedCount
              ? t('storeOrders.detail.pasteSkippedCount', '已跳过 {{count}} 行', { count: result.skippedCount })
              : undefined,
          })
          if (visibleDetailIdRef.current === orderGUID) {
            await loadDetail(false)
          }
        })
        .catch(async (error) => {
          if (error instanceof StoreOrderPasteReplacePollingCancelledError) {
            return
          }
          if (stopPasteReplacePollingRef.current === poller.stop) {
            stopPasteReplacePollingRef.current = null
          }
          setPasteOptimisticPending((current) =>
            current?.jobId === createdJob.jobId ? null : current,
          )
          if (error instanceof StoreOrderPasteReplacePollingTimeoutError) {
            notification.warning({
              message: t('storeOrders.detail.pasteImportTimeout', 'Excel 粘贴导入仍在后台执行'),
              description: t('storeOrders.detail.pasteImportTimeoutDesc', '后台任务仍可能继续执行，请稍后刷新订单明细确认结果。'),
            })
            if (visibleDetailIdRef.current === orderGUID) {
              await loadDetail(false)
            }
            return
          }
          notification.error({
            message: t('storeOrders.detail.pasteImportFailed'),
            description: error instanceof Error ? error.message : undefined,
          })
          if (visibleDetailIdRef.current === orderGUID) {
            await loadDetail(false)
          }
        })
    } catch (error) {
      console.error(error)
      message.error(error instanceof Error ? error.message : t('storeOrders.detail.pasteImportFailed'))
    } finally {
      setSubmittingPaste(false)
    }
  }

  const handleSetExistingPastePreviewAction = (action: StoreOrderPasteAction) => {
    setPastePreviewItems((current) => setExistingPastePreviewAction(current, action))
  }

  const handleChangePastePreviewAction = (rowIndex: number, action: StoreOrderPasteAction) => {
    setPastePreviewItems((current) => current.map((item) => (item.rowIndex === rowIndex ? { ...item, action } : item)))
  }

  const getDetailInputKey = (detailGUID: string, field: DetailEditableField) => `${detailGUID}:${field}`

  const registerDetailInput = (detailGUID: string, field: DetailEditableField, node: InputNumberRef | null) => {
    const key = getDetailInputKey(detailGUID, field)
    if (node) {
      detailInputRefs.current[key] = node
      return
    }
    delete detailInputRefs.current[key]
  }

  const focusDetailInput = (detailGUID: string, field: DetailEditableField) => {
    const input = detailInputRefs.current[getDetailInputKey(detailGUID, field)]
    // 方向键切入单元格后全选文本，方便直接覆盖价格或发货数。
    window.setTimeout(() => input?.focus?.({ cursor: 'all' }), 0)
  }

  const handleDetailInputKeyDown = (
    event: KeyboardEvent<HTMLInputElement>,
    detailGUID: string,
    field: DetailEditableField,
  ) => {
    if (
      event.key !== 'ArrowDown' &&
      event.key !== 'ArrowUp' &&
      event.key !== 'Enter'
    ) {
      return
    }

    // 只接管上下换行和 Enter；左右键保留输入框原生光标移动。
    event.preventDefault()
    event.stopPropagation()

    const rows = detail?.items ?? []
    const currentIndex = rows.findIndex((item) => item.detailGUID === detailGUID)
    if (currentIndex < 0) {
      return
    }

    const nextIndex = event.key === 'ArrowUp' ? currentIndex - 1 : currentIndex + 1

    const nextRow = rows[nextIndex]
    if (!nextRow) {
      return
    }

    focusDetailInput(nextRow.detailGUID, field)
  }

  const handleCompleteOrder = async () => {
    if (!detail) {
      return
    }
    if (!canUseWarehouseManagerActions || !canCompleteOrder) {
      message.warning(t('storeOrders.detail.orderReadonlyRefresh'))
      return
    }
    Modal.confirm({
      title: t('storeOrders.detail.confirmCompleteTitle'),
      content: t('storeOrders.detail.confirmCompleteContent'),
      okText: t('common.confirm'),
      cancelText: t('common.cancel'),
      onOk: async () => {
        try {
          setLineActionLoading(true)
          const currentOutboundDate = headerForm.outboundDate?.slice(0, 10)
          // 完成订单时只在出库日期为空时补当天，避免覆盖已录入或刚在表单中填写的日期。
          const nextOutboundDate = currentOutboundDate || formatLocalDateForInput()
          await updateStoreOrderOutboundDate({
            orderGUID: detail.orderGUID,
            outboundDate: nextOutboundDate,
            completeOrder: true,
          })
          message.success(t('storeOrders.detail.orderCompleted'))
          await loadDetail(false)
        } catch (error) {
          console.error(error)
          message.error(error instanceof Error ? error.message : t('storeOrders.detail.completeOrderFailed'))
        } finally {
          setLineActionLoading(false)
        }
      },
    })
  }

  const handleChangeOrderStatus = (newStatus: StoreOrderFlowStatus) => {
    if (!detail || detail.flowStatus === newStatus) {
      return
    }
    if (!canUseWarehouseManagerActions) {
      message.warning(t('storeOrders.detail.orderReadonlyRefresh'))
      return
    }

    Modal.confirm({
      title: t('storeOrders.detail.statusChangeConfirmTitle'),
      content: t('storeOrders.detail.statusChangeConfirmContent', {
        orderNo: detail.orderNo || detail.orderGUID,
        status: statusLabelMap[newStatus],
      }),
      okText: t('common.confirm'),
      cancelText: t('common.cancel'),
      onOk: async () => {
        try {
          setStatusChanging(true)
          await updateStoreOrderStatus({
            orderGUID: detail.orderGUID,
            newStatus,
          })
          message.success(t('storeOrders.detail.statusChangeSuccess'))
          await loadDetail(false)
        } catch (error) {
          console.error(error)
          message.error(error instanceof Error ? error.message : t('storeOrders.detail.statusChangeFailed'))
        } finally {
          setStatusChanging(false)
        }
      },
    })
  }

  const handleStartPicking = async () => {
    if (!detail) {
      return
    }
    if (!canUseWarehouseManagerActions || !canStartPicking) {
      message.warning(t('storeOrders.detail.orderReadonlyRefresh'))
      return
    }
    try {
      setLineActionLoading(true)
      await startPickingStoreOrder(detail.orderGUID)
      message.success(t('storeOrders.detail.orderPickingStarted'))
      await loadDetail(false)
    } catch (error) {
      console.error(error)
      message.error(error instanceof Error ? error.message : t('storeOrders.detail.startPickingFailed'))
    } finally {
      setLineActionLoading(false)
    }
  }

  const applyDetailColumnFilters = (
    nextFilters: StoreOrderDetailColumnFilters,
    confirm: FilterDropdownProps['confirm'],
  ) => {
    const cleanedFilters = cleanStoreOrderDetailColumnFilters(nextFilters)
    // 列头过滤切换服务端结果集，必须清空旧勾选，避免批量动作误用旧行。
    setSelectedLineKeys([])
    setDetailPage(1)
    setDetailColumnFilters(cleanedFilters ?? {})
    confirm()
  }

  const applyDetailColumnFilterPatch = (
    patch: StoreOrderDetailColumnFilters,
    confirm: FilterDropdownProps['confirm'],
  ) => {
    applyDetailColumnFilters({ ...detailColumnFilters, ...patch }, confirm)
  }

  const clearDetailColumnFilter = (
    keys: Array<keyof StoreOrderDetailColumnFilters>,
    confirm: FilterDropdownProps['confirm'],
  ) => {
    const nextFilters = { ...detailColumnFilters }
    keys.forEach((key) => {
      delete nextFilters[key]
    })
    applyDetailColumnFilters(nextFilters, confirm)
  }

  const makeDetailTextFilterDropdown = (
    key: StoreOrderDetailTextFilterKey,
    placeholder: string,
  ) => ({ confirm }: FilterDropdownProps) => (
    <ProductPickerTextFilterDropdown
      value={detailColumnFilters[key]}
      placeholder={placeholder}
      applyLabel={t('containers.actions.applyColumnFilter', '应用')}
      resetLabel={t('containers.actions.resetColumnFilter', '重置')}
      confirm={confirm}
      onApply={(value, nextConfirm) =>
        applyDetailColumnFilterPatch({ [key]: value } as StoreOrderDetailColumnFilters, nextConfirm)
      }
      onReset={(nextConfirm) => clearDetailColumnFilter([key], nextConfirm)}
    />
  )

  const makeDetailNumberRangeFilterDropdown = (
    range: StoreOrderDetailNumberRange,
  ) => ({ confirm }: FilterDropdownProps) => (
    <ProductPickerNumberRangeFilterDropdown
      minValue={detailColumnFilters[range.min]}
      maxValue={detailColumnFilters[range.max]}
      minPlaceholder={t('containers.placeholders.minValue', '最小值')}
      maxPlaceholder={t('containers.placeholders.maxValue', '最大值')}
      applyLabel={t('containers.actions.applyColumnFilter', '应用')}
      resetLabel={t('containers.actions.resetColumnFilter', '重置')}
      confirm={confirm}
      onApply={(value, nextConfirm) =>
        applyDetailColumnFilterPatch(
          {
            [range.min]: value.min,
            [range.max]: value.max,
          } as StoreOrderDetailColumnFilters,
          nextConfirm,
        )
      }
      onReset={(nextConfirm) => clearDetailColumnFilter([range.min, range.max], nextConfirm)}
    />
  )

  const makeDetailStatusFilterDropdown = ({ confirm }: FilterDropdownProps) => (
    <StoreOrderDetailStatusFilterDropdown
      value={detailColumnFilters.isActive}
      activeLabel={t('common.activeUpper')}
      inactiveLabel={t('common.inactiveUpper')}
      applyLabel={t('containers.actions.applyColumnFilter', '应用')}
      resetLabel={t('containers.actions.resetColumnFilter', '重置')}
      confirm={confirm}
      onApply={(value, nextConfirm) => applyDetailColumnFilterPatch({ isActive: value }, nextConfirm)}
      onReset={(nextConfirm) => clearDetailColumnFilter(['isActive'], nextConfirm)}
    />
  )

  const detailFilterIcon = (active?: boolean) => (
    <SearchOutlined style={{ color: active ? '#1677ff' : undefined }} />
  )
  const detailColumnSortOrder = (field: StoreOrderDetailSortField): SortOrder =>
    detailSortField === field ? detailSortOrder : null
  const hasDetailNumberRangeFilter = (range: StoreOrderDetailNumberRange) => (
    typeof detailColumnFilters[range.min] === 'number' || typeof detailColumnFilters[range.max] === 'number'
  )
  const detailTextFilterProps = (key: StoreOrderDetailTextFilterKey, placeholder: string) => ({
    filterDropdown: makeDetailTextFilterDropdown(key, placeholder),
    filterIcon: detailFilterIcon,
    filtered: Boolean(detailColumnFilters[key]?.trim()),
  })
  const detailNumberFilterProps = (range: StoreOrderDetailNumberRange) => ({
    filterDropdown: makeDetailNumberRangeFilterDropdown(range),
    filterIcon: detailFilterIcon,
    filtered: hasDetailNumberRangeFilter(range),
  })
  const detailStatusFilterProps = () => ({
    filterDropdown: makeDetailStatusFilterDropdown,
    filterIcon: detailFilterIcon,
    filtered: typeof detailColumnFilters.isActive === 'boolean',
  })

  const renderZeroOrEmptyCell = (value: string | number | undefined | null) => {
    if (value === undefined || value === null || value === '') {
      return renderDangerValue('--')
    }
    if (value === 0) {
      return renderDangerValue('0')
    }
    return value
  }

  const getQuantityHighlight = (line: StoreOrderDetailLine) => {
    const quantity = toNumber(line.quantity)
    const allocQuantity = toNumber(editingRows[line.detailGUID]?.allocQuantity ?? line.allocQuantity)

    if (quantity === 0 || allocQuantity === 0 || isZeroOrEmpty(line.quantity) || isZeroOrEmpty(line.allocQuantity)) {
      return 'error' as const
    }
    if (quantity !== allocQuantity) {
      return 'warning' as const
    }
    return undefined
  }

  const renderQuantityText = (line: StoreOrderDetailLine) => {
    const quantity = toNumber(line.quantity)
    const allocQuantity = toNumber(editingRows[line.detailGUID]?.allocQuantity ?? line.allocQuantity)

    if (quantity === 0 || isZeroOrEmpty(line.quantity)) {
      return renderDangerValue(quantity.toString())
    }
    if (quantity !== allocQuantity) {
      return renderWarningValue(quantity.toString())
    }
    return quantity
  }

  const baseDetailColumns: ColumnsType<StoreOrderDetailLine> = ([
    {
      title: '#',
      key: 'index',
      dataIndex: 'index',
      width: STORE_ORDER_DETAIL_DEFAULT_COLUMN_WIDTHS.index,
      fixed: isDesktop ? 'left' : undefined,
      render: (_, __, index) => renderStoreOrderDetailNumericCell((detailPage - 1) * detailPageSize + index + 1),
    },
    {
      title: t('column.image'),
      key: 'productImage',
      dataIndex: 'productImage',
      width: STORE_ORDER_DETAIL_DEFAULT_COLUMN_WIDTHS.productImage,
      fixed: isDesktop ? 'left' : undefined,
      render: (value: string | undefined, record) => (
        <Image
          src={value}
          alt={record.productName}
          loading="lazy"
          width={30}
          height={30}
          style={{ borderRadius: 4, objectFit: 'cover' }}
          fallback="data:image/gif;base64,R0lGODlhAQABAAD/ACwAAAAAAQABAAACADs="
        />
      ),
    },
    {
      title: t('column.itemNumber'),
      key: 'itemNumber',
      dataIndex: 'itemNumber',
      width: STORE_ORDER_DETAIL_DEFAULT_COLUMN_WIDTHS.itemNumber,
      fixed: isDesktop ? 'left' : undefined,
      sorter: true,
      sortOrder: detailColumnSortOrder('itemNumber'),
      ...detailTextFilterProps('itemNumber', t('storeOrders.detail.filterItemNumber', '过滤货号')),
      render: (value: string | undefined) =>
        value ? (
          <span className="store-order-detail-item-number-cell">
            <Typography.Text className="store-order-detail-item-number-text" ellipsis={{ tooltip: value }}>
              {value}
            </Typography.Text>
            <Button
              size="small"
              type="text"
              icon={<CopyOutlined />}
              title={t('common.copy')}
              aria-label={`${t('common.copy')} ${value}`}
              className="store-order-detail-copy-button"
              onClick={() => void copyTextToClipboard(value)}
            />
          </span>
        ) : (
          renderDangerValue('--')
        ),
    },
    {
      title: t('column.productName'),
      key: 'productName',
      dataIndex: 'productName',
      width: STORE_ORDER_DETAIL_DEFAULT_COLUMN_WIDTHS.productName,
      sorter: true,
      sortOrder: detailColumnSortOrder('productName'),
      ...detailTextFilterProps('productName', t('storeOrders.detail.filterProductName', '过滤商品名称')),
      render: (value: string | undefined) =>
        value ? renderStoreOrderTwoLineText(value) : '--',
    },
    {
      title: t('column.barcode'),
      key: 'barcode',
      dataIndex: 'barcode',
      width: STORE_ORDER_DETAIL_DEFAULT_COLUMN_WIDTHS.barcode,
      sorter: true,
      sortOrder: detailColumnSortOrder('barcode'),
      ...detailTextFilterProps('barcode', t('storeOrders.detail.filterBarcode', '过滤条码')),
      render: (value: string | undefined) => (
        <BarcodePreview value={value} className="store-order-barcode-cell" textNoWrap showCopy={false} />
      ),
    },
    {
      title: t('column.oemPrice'),
      key: 'price',
      dataIndex: 'price',
      width: STORE_ORDER_DETAIL_DEFAULT_COLUMN_WIDTHS.price,
      render: (value: number | undefined) => renderStoreOrderDetailNumericCell(formatAmount(value)),
    },
    {
      title: t('column.location'),
      key: 'locationCode',
      dataIndex: 'locationCode',
      width: STORE_ORDER_DETAIL_DEFAULT_COLUMN_WIDTHS.locationCode,
      sorter: true,
      sortOrder: detailColumnSortOrder('locationCode'),
      ...detailTextFilterProps('locationCode', t('storeOrders.detail.filterLocation', '过滤货位')),
      render: (value: string | undefined) => <span className="store-order-nowrap">{renderZeroOrEmptyCell(value)}</span>,
    },
    {
      title: t('storeOrders.pickingAssignment.assignee', '负责人'),
      key: 'assignee',
      width: STORE_ORDER_DETAIL_DEFAULT_COLUMN_WIDTHS.assignee,
      render: (_: unknown, record: StoreOrderDetailLine) => {
        const assignee = lineAssignees[record.detailGUID]
        return assignee ? <AssigneeChip segmentNo={assignee.segmentNo} name={assignee.pickerName} /> : '—'
      },
    },
    {
      title: t('column.orderQuantity'),
      key: 'quantity',
      dataIndex: 'quantity',
      width: STORE_ORDER_DETAIL_DEFAULT_COLUMN_WIDTHS.quantity,
      sorter: true,
      sortOrder: detailColumnSortOrder('quantity'),
      ...detailNumberFilterProps({ min: 'quantityMin', max: 'quantityMax' }),
      render: (_, record) => renderStoreOrderDetailNumericCell(renderQuantityText(record)),
    },
    {
      title: t('column.allocQuantity'),
      key: 'allocQuantity',
      dataIndex: 'allocQuantity',
      width: STORE_ORDER_DETAIL_DEFAULT_COLUMN_WIDTHS.allocQuantity,
      sorter: true,
      sortOrder: detailColumnSortOrder('allocQuantity'),
      ...detailNumberFilterProps({ min: 'allocQuantityMin', max: 'allocQuantityMax' }),
      render: (value: number | undefined, record) => (
        <InputNumber
          ref={(node) => registerDetailInput(record.detailGUID, 'allocQuantity', node)}
          min={0}
          precision={0}
          disabled={!canUseWarehouseManagerActions || isReadonlyOrder || isPasteOptimisticPreviewActive}
          status={getQuantityHighlight(record)}
          style={{ width: 60 }}
          value={editingRows[record.detailGUID]?.allocQuantity ?? value ?? 0}
          onKeyDown={(event) => handleDetailInputKeyDown(event, record.detailGUID, 'allocQuantity')}
          onChange={(nextValue) =>
            setEditingRows((current) => ({
              ...current,
              [record.detailGUID]: {
                ...current[record.detailGUID],
                allocQuantity: nextValue === null ? undefined : Number(nextValue),
              },
            }))
          }
        />
      ),
    },
    {
      title: t('column.importPrice'),
      key: 'importPrice',
      dataIndex: 'importPrice',
      width: STORE_ORDER_DETAIL_DEFAULT_COLUMN_WIDTHS.importPrice,
      sorter: true,
      sortOrder: detailColumnSortOrder('importPrice'),
      ...detailNumberFilterProps({ min: 'importPriceMin', max: 'importPriceMax' }),
      render: (value: number | undefined, record) => (
        <InputNumber
          ref={(node) => registerDetailInput(record.detailGUID, 'importPrice', node)}
          min={0}
          precision={2}
          controls={false}
          disabled={!canUseWarehouseManagerActions || isReadonlyOrder || isPasteOptimisticPreviewActive}
          status={isZeroOrEmpty(editingRows[record.detailGUID]?.importPrice ?? value) ? 'error' : undefined}
          style={{ width: 60 }}
          value={editingRows[record.detailGUID]?.importPrice ?? value}
          onKeyDown={(event) => handleDetailInputKeyDown(event, record.detailGUID, 'importPrice')}
          onChange={(nextValue) =>
            setEditingRows((current) => ({
              ...current,
              [record.detailGUID]: {
                ...current[record.detailGUID],
                importPrice: nextValue === null ? undefined : Number(nextValue),
              },
            }))
          }
        />
      ),
    },
    {
      title: t('column.importAmount'),
      key: 'allocatedImportAmount',
      dataIndex: 'allocatedImportAmount',
      width: STORE_ORDER_DETAIL_DEFAULT_COLUMN_WIDTHS.allocatedImportAmount,
      sorter: true,
      sortOrder: detailColumnSortOrder('allocatedImportAmount'),
      render: (value: number | undefined, record) => {
        const edited = editingRows[record.detailGUID]
        const nextValue =
          edited?.allocQuantity !== undefined || edited?.importPrice !== undefined
            ? Number(edited.allocQuantity ?? record.allocQuantity ?? 0) * Number(edited.importPrice ?? record.importPrice ?? 0)
            : value
        return renderStoreOrderDetailNumericCell(
          nextValue === undefined || nextValue === null
            ? renderDangerValue('--')
            : nextValue === 0
              ? renderDangerValue('0.00')
              : formatAmount(nextValue),
        )
      },
    },
    {
      title: t('column.orderVolume'),
      key: 'orderVolume',
      dataIndex: 'orderVolume',
      width: STORE_ORDER_DETAIL_DEFAULT_COLUMN_WIDTHS.orderVolume,
      render: (value: number | undefined, record) => {
        const nextValue =
          value ??
          record.totalVolume ??
          (record.volume === undefined || record.volume === null
            ? undefined
            : Number(record.volume) * Number(record.quantity ?? 0))
        return nextValue === undefined || nextValue === null
          ? renderStoreOrderDetailNumericCell(renderDangerValue('--'))
          : nextValue === 0
            ? renderStoreOrderDetailNumericCell(renderDangerValue('0.00'))
            : renderStoreOrderDetailNumericCell(formatStoreOrderVolume(nextValue))
      },
    },
    {
      title: t('column.shipVolume'),
      key: 'allocVolume',
      dataIndex: 'allocVolume',
      width: STORE_ORDER_DETAIL_DEFAULT_COLUMN_WIDTHS.allocVolume,
      render: (value: number | undefined, record) => {
        const editedAllocQuantity = editingRows[record.detailGUID]?.allocQuantity
        const nextValue =
          editedAllocQuantity !== undefined
            ? record.volume === undefined || record.volume === null
              ? undefined
              : Number(record.volume) * Number(editedAllocQuantity)
            : value ??
              (record.volume === undefined || record.volume === null
                ? undefined
                : Number(record.volume) * Number(record.allocQuantity ?? 0))
        return nextValue === undefined || nextValue === null
          ? renderStoreOrderDetailNumericCell(renderDangerValue('--'))
          : nextValue === 0
            ? renderStoreOrderDetailNumericCell(renderDangerValue('0.00'))
            : renderStoreOrderDetailNumericCell(formatStoreOrderVolume(nextValue))
      },
    },
    {
      title: t('column.status'),
      key: 'isActive',
      dataIndex: 'isActive',
      width: STORE_ORDER_DETAIL_DEFAULT_COLUMN_WIDTHS.isActive,
      sorter: true,
      sortOrder: detailColumnSortOrder('isActive'),
      ...detailStatusFilterProps(),
      render: (value: boolean) => <Tag color={value ? 'success' : 'default'}>{value ? t('common.activeUpper') : t('common.inactiveUpper')}</Tag>,
    },
    {
      title: t('column.action'),
      key: 'actions',
      width: STORE_ORDER_DETAIL_DEFAULT_COLUMN_WIDTHS.actions,
      fixed: isDesktop ? 'right' : undefined,
      render: (_, record) => (
        <Space size={4} wrap={false}>
          <Tooltip title={t('common.save')}>
            <Button
              size="small"
              type="text"
              icon={<SaveOutlined />}
              aria-label={t('common.save')}
              className="store-order-detail-action-button"
              disabled={!canUseWarehouseManagerActions || isReadonlyOrder || isPasteOptimisticPreviewActive}
              onClick={() => void handleSaveLine(record)}
            />
          </Tooltip>
          <Tooltip title={record.isActive ? t('common.inactiveUpper') : t('common.activeUpper')}>
            <Button
              size="small"
              type="text"
              icon={<EditOutlined />}
              aria-label={record.isActive ? t('common.inactiveUpper') : t('common.activeUpper')}
              className="store-order-detail-action-button"
              disabled={!canUseWarehouseManagerActions || isReadonlyOrder || isPasteOptimisticPreviewActive}
              onClick={() => void handleToggleLineStatus(record)}
            />
          </Tooltip>
          <Popconfirm
            title={t('storeOrders.detail.confirmDeleteLine')}
            okText={t('common.delete')}
            cancelText={t('common.cancel')}
            onConfirm={() => void handleRemoveLine(record)}
          >
            <Tooltip title={t('common.delete')}>
              <Button
                size="small"
                danger
                type="text"
                icon={<DeleteOutlined />}
                aria-label={t('common.delete')}
                className="store-order-detail-action-button"
                disabled={!canUseWarehouseManagerActions || isReadonlyOrder || isPasteOptimisticPreviewActive}
              />
            </Tooltip>
          </Popconfirm>
        </Space>
      ),
    },
  ] as ColumnsType<StoreOrderDetailLine>).filter(
    // 操作列与负责人列只给仓库订货管理者（派单接口也只对他们开放）。
    (column) => canUseWarehouseManagerActions || (column.key !== 'actions' && column.key !== 'assignee'),
  )
  const detailDraggableColumnKeys = baseDetailColumns.map(
    (column) => String(column.key) as StoreOrderDetailTableColumnKey,
  )
  const detailSortableColumnKeys = detailColumnOrder.length ? detailColumnOrder : detailDraggableColumnKeys
  const isDetailColumnOrderCustomized = isStoreOrderDetailColumnOrderCustomized(
    detailColumnOrder,
    detailDraggableColumnKeys,
  )
  const isDetailColumnWidthCustomized = Object.keys(detailColumnWidths).length > 0
  const isDetailColumnSettingsCustomized = isDetailColumnOrderCustomized || isDetailColumnWidthCustomized

  useEffect(() => {
    setDetailColumnOrder((current) => {
      let savedOrder: unknown[] | null = null
      if (!current.length && typeof window !== 'undefined') {
        try {
          const raw = localStorage.getItem(STORE_ORDER_DETAIL_COLUMN_ORDER_STORAGE_KEY)
          savedOrder = raw ? JSON.parse(raw) : null
        } catch {
          savedOrder = null
        }
      }

      // 只持久化业务列顺序；选择列仍由 rowSelection 管理，操作列按权限自动补齐或移除。
      const nextOrder = mergeStoreOrderDetailColumnOrder(
        current.length ? current : savedOrder,
        detailDraggableColumnKeys,
      )
      if (current.length === nextOrder.length && current.every((key, index) => key === nextOrder[index])) {
        return current
      }
      return nextOrder
    })
  }, [detailDraggableColumnKeys.join('|')])

  useEffect(() => {
    setDetailColumnWidths((current) => {
      let savedWidths: unknown = null
      if (!Object.keys(current).length && typeof window !== 'undefined') {
        try {
          const raw = localStorage.getItem(STORE_ORDER_DETAIL_COLUMN_WIDTH_STORAGE_KEY)
          savedWidths = raw ? JSON.parse(raw) : null
        } catch {
          savedWidths = null
        }
      }

      // 列宽只保留当前可见列，避免权限切换或旧版本字段污染表头。
      const nextWidths = normalizeStoreOrderDetailColumnWidths(
        Object.keys(current).length ? current : savedWidths,
        detailDraggableColumnKeys,
      )
      if (areStoreOrderDetailColumnWidthsEqual(current, nextWidths)) {
        return current
      }
      return nextWidths
    })
  }, [detailDraggableColumnKeys.join('|')])

  const handleColumnDragEnd = ({ active: dragActive, over }: DragEndEvent) => {
    if (!over || dragActive.id === over.id) return
    setDetailColumnOrder((current) => {
      const nextOrder = moveStoreOrderDetailColumnOrder(
        current.length ? current : detailDraggableColumnKeys,
        dragActive.id,
        over.id,
      )
      try {
        localStorage.setItem(STORE_ORDER_DETAIL_COLUMN_ORDER_STORAGE_KEY, JSON.stringify(nextOrder))
      } catch {
        // localStorage 不可用时不影响当前页面内拖拽排序。
      }
      return nextOrder
    })
  }

  const persistDetailColumnWidths = (nextWidths: StoreOrderDetailColumnWidthMap) => {
    try {
      localStorage.setItem(STORE_ORDER_DETAIL_COLUMN_WIDTH_STORAGE_KEY, JSON.stringify(nextWidths))
    } catch {
      // localStorage 不可用时不影响当前页面内列宽拖拽。
    }
  }

  const handleColumnResizeStart = useCallback((
    columnKey: StoreOrderDetailTableColumnKey,
    startWidth: number,
    resizeFromLeft: boolean,
    event: ReactPointerEvent<HTMLSpanElement>,
  ) => {
    stopDetailColumnResizeRef.current?.()

    const startX = event.clientX
    const pointerId = event.pointerId
    const resizeHandle = event.currentTarget
    const previousCursor = document.body.style.cursor
    const previousUserSelect = document.body.style.userSelect
    let latestWidth = startWidth
    let didResize = false

    document.body.style.cursor = 'col-resize'
    document.body.style.userSelect = 'none'

    const handlePointerMove = (pointerEvent: PointerEvent) => {
      if (pointerEvent.pointerId !== pointerId) return
      const pointerDelta = pointerEvent.clientX - startX
      const nextWidth = clampStoreOrderDetailColumnWidth(
        columnKey,
        startWidth + (resizeFromLeft ? -pointerDelta : pointerDelta),
      )
      if (nextWidth === latestWidth) return
      latestWidth = nextWidth
      didResize = true
      setDetailColumnWidths((current) => {
        return { ...current, [columnKey]: nextWidth }
      })
    }

    function cleanupResize() {
      document.removeEventListener('pointermove', handlePointerMove)
      document.removeEventListener('pointerup', finishResize)
      document.removeEventListener('pointercancel', finishResize)
      window.removeEventListener('blur', finishResize)
      resizeHandle.removeEventListener('lostpointercapture', finishResize)
      if (resizeHandle.hasPointerCapture(pointerId)) {
        resizeHandle.releasePointerCapture(pointerId)
      }
      document.body.style.cursor = previousCursor
      document.body.style.userSelect = previousUserSelect
      if (stopDetailColumnResizeRef.current === cleanupResize) {
        stopDetailColumnResizeRef.current = null
      }
    }

    function suppressResizeClick() {
      const headerCell = resizeHandle.closest('th')
      const suppressHeaderClick = (clickEvent: MouseEvent) => {
        if (!headerCell?.contains(clickEvent.target as Node)) return
        clickEvent.preventDefault()
        clickEvent.stopPropagation()
      }
      document.addEventListener('click', suppressHeaderClick, { capture: true, once: true })
      // 浏览器若未在 pointerup 后派发 click，及时撤掉保护，避免影响下一次正常点击。
      window.setTimeout(() => document.removeEventListener('click', suppressHeaderClick, true), 0)
    }

    function finishResize(finishEvent: Event) {
      if (finishEvent instanceof PointerEvent && finishEvent.pointerId !== pointerId) return
      cleanupResize()
      if (!didResize) return
      suppressResizeClick()
      setDetailColumnWidths((current) => {
        const nextWidths = { ...current, [columnKey]: latestWidth }
        persistDetailColumnWidths(nextWidths)
        return nextWidths
      })
    }

    // 使用 document 级监听，指针拖出表头区域时仍能连续调整列宽。
    stopDetailColumnResizeRef.current = cleanupResize
    resizeHandle.addEventListener('lostpointercapture', finishResize, { once: true })
    document.addEventListener('pointermove', handlePointerMove)
    document.addEventListener('pointerup', finishResize)
    document.addEventListener('pointercancel', finishResize)
    window.addEventListener('blur', finishResize, { once: true })
  }, [])

  const resetDetailColumnLayout = () => {
    setDetailColumnOrder(detailDraggableColumnKeys)
    setDetailColumnWidths({})
    try {
      localStorage.removeItem(STORE_ORDER_DETAIL_COLUMN_ORDER_STORAGE_KEY)
      localStorage.removeItem(STORE_ORDER_DETAIL_COLUMN_WIDTH_STORAGE_KEY)
    } catch {
      // localStorage 不可用时仍恢复当前页面内的默认列布局。
    }
    message.success(t('containers.messages.columnOrderReset', '列设置已恢复默认'))
  }

  const orderedBaseDetailColumns = (() => {
    const columnMap = new Map(baseDetailColumns.map((column) => [String(column.key), column]))
    return detailSortableColumnKeys
      .map((key) => columnMap.get(key))
      .filter((column): column is ColumnsType<StoreOrderDetailLine>[number] => Boolean(column))
  })()

  const columns = orderedBaseDetailColumns.map((column) => {
    const columnKey = String(column.key) as StoreOrderDetailTableColumnKey
    const width = detailColumnWidths[columnKey] ?? column.width ?? STORE_ORDER_DETAIL_DEFAULT_COLUMN_WIDTHS[columnKey]
    return {
      ...column,
      width,
      onHeaderCell: () => ({
        'data-column-key': columnKey,
        'data-column-width': typeof width === 'number' ? width : STORE_ORDER_DETAIL_DEFAULT_COLUMN_WIDTHS[columnKey],
        'data-column-fixed': column.fixed,
        onColumnResizeStart: handleColumnResizeStart,
      } as DraggableHeaderCellProps),
    }
  }) as ColumnsType<StoreOrderDetailLine>
  const detailTableScrollX = Math.max(
    STORE_ORDER_DETAIL_TABLE_MIN_SCROLL_X,
    (canUseWarehouseManagerActions ? STORE_ORDER_DETAIL_SELECTION_COLUMN_WIDTH : 0) +
      columns.reduce((total, column) => {
        const width = typeof column.width === 'number' ? column.width : Number(column.width)
        return total + (Number.isFinite(width) ? width : 0)
      }, 0),
  )

  if (!id) {
    return (
      <PageContainer title={t('storeOrders.orderDetail')} subtitle={t('storeOrders.detail.missingOrderNoSubtitle')}>
        <Card>
          <Empty description={t('storeOrders.missingOrderNo')} />
        </Card>
      </PageContainer>
    )
  }

  return (
    <PageContainer
      title={t('storeOrders.detailTitleWithNo', { orderNo: tabTitle })}
      subtitle={t('storeOrders.orderDetailSubtitle')}
    >
      {detailLoadStatus === 'idle' || detailLoadStatus === 'loading' ? (
        <Card>
          <div
            style={{
              minHeight: 240,
              display: 'flex',
              alignItems: 'center',
              justifyContent: 'center',
            }}
          >
            <Spin />
          </div>
        </Card>
      ) : detailLoadStatus === 'notFound' ? (
        <Card>
          <Empty description={t('storeOrders.detail.notFound')} />
        </Card>
      ) : detailLoadStatus === 'error' ? (
        <Card>
          <Empty description={detailErrorMessage || t('storeOrders.detail.loadDetailFailed')}>
            <Button type="primary" onClick={() => void loadDetail()}>
              {t('common.retry', '重试')}
            </Button>
          </Empty>
        </Card>
      ) : detail ? (
        <Space direction="vertical" size={16} style={{ width: '100%' }}>
            <Card>
              {isReadonlyOrder ? (
                <Alert
                  type="warning"
                  showIcon
                  message={t('storeOrders.detail.orderReadonlyTitle')}
                  description={t('storeOrders.detail.orderReadonlyDescription')}
                  style={{ marginBottom: 16 }}
                />
              ) : null}
              <Descriptions
                column={3}
                size="small"
                extra={
                  canUseWarehouseManagerActions ? (
                    <Space wrap>
                      <Button
                        icon={<SaveOutlined />}
                        loading={savingHeader}
                        onClick={() => void handleSaveHeader()}
                      >
                        {t('storeOrders.saveOrderHeader')}
                      </Button>
                      <Button
                        icon={<CheckOutlined />}
                        loading={lineActionLoading}
                        disabled={isReadonlyOrder || !canStartPicking}
                        onClick={() => void handleStartPicking()}
                      >
                        {t('storeOrders.startPicking')}
                      </Button>
                      <Button
                        type="primary"
                        icon={<CheckOutlined />}
                        loading={lineActionLoading}
                        disabled={!canCompleteOrder}
                        onClick={() => void handleCompleteOrder()}
                      >
                        {t('storeOrders.completeOrder')}
                      </Button>
                      <Space size={4}>
                        <Typography.Text type="secondary">{t('storeOrders.detail.changeOrderStatus')}</Typography.Text>
                        <Select
                          style={{ width: 150 }}
                          placeholder={t('storeOrders.detail.changeOrderStatus')}
                          value={
                            orderStatusChangeOptions.some((item) => item.value === detail.flowStatus)
                              ? detail.flowStatus
                              : undefined
                          }
                          loading={statusChanging}
                          disabled={statusChanging}
                          options={orderStatusChangeOptions}
                          onChange={(value) => handleChangeOrderStatus(value)}
                        />
                      </Space>
                    </Space>
                  ) : undefined
                }
              >
                <Descriptions.Item label={t('storeOrders.orderNoLabel')}>{detail.orderNo || '--'}</Descriptions.Item>
                <Descriptions.Item label={t('storeOrders.storeLabel')}>
                  <Select
                    showSearch
                    style={{ width: '100%' }}
                    loading={storesLoading}
                    disabled={!canUseWarehouseManagerActions || isReadonlyOrder}
                    value={headerForm.storeCode}
                    options={storeOptions}
                    optionFilterProp="label"
                    onChange={(value) => {
                      const nextStore = stores.find((item) => item.storeCode === value)
                      const nextStoreAddress = nextStore?.address || ''
                      const nextStoreContactEmail = nextStore?.contactEmail || ''

                      setHeaderForm((current) => ({
                        ...current,
                        storeCode: value,
                        address: resolveStoreContactDraftValue({
                          currentValue: current.address,
                          previousStoreValue: storeContactBaseline.address,
                          nextStoreValue: nextStoreAddress,
                        }),
                        contactEmail: resolveStoreContactDraftValue({
                          currentValue: current.contactEmail,
                          previousStoreValue: storeContactBaseline.contactEmail,
                          nextStoreValue: nextStoreContactEmail,
                        }),
                      }))
                      setStoreContactBaseline({
                        address: nextStoreAddress,
                        contactEmail: nextStoreContactEmail,
                      })
                    }}
                  />
                </Descriptions.Item>
                <Descriptions.Item label={t('storeOrders.statusLabel')}>
                  <Tag color={StoreOrderStatusColorMap[(detail.flowStatus || 0) as StoreOrderFlowStatus] || 'default'}>
                    {statusLabelMap[(detail.flowStatus || 0) as StoreOrderFlowStatus] || t('common.statusN', { n: detail.flowStatus ?? '--' })}
                  </Tag>
                </Descriptions.Item>
                <Descriptions.Item label={t('storeOrders.orderDateLabel')}>
                  <DatePicker
                    style={{ width: '100%' }}
                    disabled={!canUseWarehouseManagerActions || isReadonlyOrder}
                    value={headerForm.orderDate ? dayjs(headerForm.orderDate.slice(0, 10)) : null}
                    onChange={(value) =>
                      setHeaderForm((current) => ({
                        ...current,
                        orderDate: value ? new Date(value.format('YYYY-MM-DD')).toISOString() : undefined,
                      }))
                    }
                  />
                </Descriptions.Item>
                <Descriptions.Item label={t('storeOrders.outboundDate')}>
                  <DatePicker
                    style={{ width: '100%' }}
                    disabled={!canUseWarehouseManagerActions || !canEditOutboundDate}
                    value={headerForm.outboundDate ? dayjs(headerForm.outboundDate.slice(0, 10)) : null}
                    onChange={(value) =>
                      setHeaderForm((current) => ({
                        ...current,
                        outboundDate: value?.format('YYYY-MM-DD'),
                      }))
                    }
                  />
                </Descriptions.Item>
                <Descriptions.Item label={t('storeOrders.orderQtyLabel')}>{detail.totalQuantity}</Descriptions.Item>
                <Descriptions.Item label={t('storeOrders.shipQtyLabel')}>{totalAllocQuantity}</Descriptions.Item>
                <Descriptions.Item label={t('storeOrders.orderVolumeLabel')}>{formatStoreOrderVolume(totalOrderVolume)}</Descriptions.Item>
                <Descriptions.Item label={t('storeOrders.shipVolumeLabel')}>{formatStoreOrderVolume(totalAllocVolume)}</Descriptions.Item>
                <Descriptions.Item label={t('storeOrders.orderAmountLabel')}>{formatAmount(estimatedSalesAmount)}</Descriptions.Item>
                <Descriptions.Item label={t('storeOrders.importAmountLabel')}>{formatAmount(draftTotalImportAmount)}</Descriptions.Item>
                <Descriptions.Item label={t('storeOrders.gstAmountLabel')}>{formatAmount(gstAmount)}</Descriptions.Item>
                <Descriptions.Item label={t('storeOrders.freightLabel')}>
                  <InputNumber
                    min={0}
                    precision={2}
                    disabled={!canUseWarehouseManagerActions || isReadonlyOrder}
                    style={{ width: '100%' }}
                    value={headerForm.shippingFee}
                    onChange={(value) =>
                      setHeaderForm((current) => ({
                        ...current,
                        shippingFee: value === null ? undefined : Number(value),
                      }))
                    }
                  />
                </Descriptions.Item>
                <Descriptions.Item label={t('storeOrders.addressLabel')} span={2}>
                  <Input.TextArea
                    rows={2}
                    disabled={!canUseWarehouseManagerActions || isReadonlyOrder}
                    value={headerForm.address}
                    onChange={(event) =>
                      setHeaderForm((current) => ({
                        ...current,
                        address: event.target.value,
                      }))
                    }
                    placeholder={t('storeOrders.addressLabel')}
                  />
                </Descriptions.Item>
                <Descriptions.Item label={t('storeOrders.contactEmailLabel')}>
                  <Space direction="vertical" size={4} style={{ width: '100%' }}>
                    <Input
                      type="email"
                      disabled={!canUseWarehouseManagerActions || isReadonlyOrder}
                      value={headerForm.contactEmail}
                      onChange={(event) =>
                        setHeaderForm((current) => ({
                          ...current,
                          contactEmail: event.target.value,
                        }))
                      }
                      placeholder={t('storeOrders.contactEmailLabel')}
                    />
                    <InvoiceEmailSentStatusText info={detail.invoiceEmailSentInfo} t={t} lng={i18n.language} />
                  </Space>
                </Descriptions.Item>
                <Descriptions.Item label={t('storeOrders.skuCountLabel')}>{detail.totalSKU ?? detail.items.length}</Descriptions.Item>
                <Descriptions.Item label={t('storeOrders.remarksLabel')} span={3}>
                  <Input.TextArea
                    rows={3}
                    disabled={!canUseWarehouseManagerActions || isReadonlyOrder}
                    value={headerForm.remarks}
                    onChange={(event) =>
                      setHeaderForm((current) => ({
                        ...current,
                        remarks: event.target.value,
                      }))
                    }
                    placeholder={t('common.enterRemarks')}
                  />
                </Descriptions.Item>
              </Descriptions>
            </Card>

            {canUseWarehouseManagerActions ? (
              <PickingAssignmentSection
                orderGuid={detail.orderGUID}
                orderNo={detail.orderNo}
                storeName={detail.storeName}
                assignable={
                  detail.flowStatus === StoreOrderFlowStatus.Submitted || detail.flowStatus === StoreOrderFlowStatus.Picking
                }
                onAssignmentChange={setLineAssignees}
              />
            ) : null}

            <Card
              title={t('storeOrders.orderDetailSection')}
              extra={
                canUseStoreOrderDetailExtraActions ? (
                  <Space wrap>
                    {canUseWarehouseManagerActions ? (
                      <Input
                        allowClear
                        disabled={isReadonlyOrder || isPasteOptimisticPreviewActive}
                        placeholder={t('storeOrders.quickAddPlaceholder')}
                        style={{ width: 220 }}
                        value={quickAddItemNumber}
                        onChange={(event) => setQuickAddItemNumber(event.target.value)}
                        onPressEnter={() => void handleQuickAdd()}
                      />
                    ) : null}
                    {canUseWarehouseManagerActions ? (
                      <InputNumber
                        min={1}
                        precision={0}
                        disabled={isReadonlyOrder || isPasteOptimisticPreviewActive}
                        placeholder={t('storeOrders.allocQtyPlaceholder')}
                        value={quickAddQuantity}
                        onChange={(value) => setQuickAddQuantity(Number(value ?? 1))}
                      />
                    ) : null}
                    {canUseWarehouseManagerActions ? (
                      <Button
                        icon={<PlusOutlined />}
                        loading={lineActionLoading}
                        disabled={isReadonlyOrder || isPasteOptimisticPreviewActive}
                        onClick={() => void handleQuickAdd()}
                      >
                        {t('storeOrders.quickAdd')}
                      </Button>
                    ) : null}
                    {canUseWarehouseManagerActions ? (
                      <Button icon={<SearchOutlined />} disabled={isReadonlyOrder || isPasteOptimisticPreviewActive} onClick={() => setPickerOpen(true)}>
                        {t('storeOrders.selectProduct')}
                      </Button>
                    ) : null}
                    {canUseWarehouseManagerActions ? (
                      <Button
                        icon={<ContainerOutlined />}
                        loading={containerPickerLoading}
                        disabled={isReadonlyOrder || isPasteOptimisticPreviewActive}
                        onClick={() => void handleOpenContainerPicker()}
                      >
                        {t('storeOrders.containerPicker')}
                      </Button>
                    ) : null}
                    {canUseWarehouseManagerActions ? (
                      <Button
                        icon={<CopyOutlined />}
                        loading={batchLoading}
                        disabled={isReadonlyOrder || isPasteOptimisticPreviewActive || !selectedLineKeys.length}
                        onClick={() => void handleBatchConfirm({ type: 'copyOrderQuantityToAllocQuantity' })}
                      >
                        {t('storeOrders.batchCopyOrderQuantityButton')}
                      </Button>
                    ) : null}
                    {detail ? (
                      <Button
                        icon={<PrinterOutlined />}
                        onClick={() => navigate(`/warehouse/store-order/picking/${detail.orderGUID}`)}
                      >
                        {t('storeOrders.pickingList')}
                      </Button>
                    ) : null}
                    {canUseWarehouseManagerActions ? (
                      <Button
                        icon={<FileTextOutlined />}
                        onClick={() => detail && navigate(`/warehouse/store-order/invoice/${detail.orderGUID}`)}
                      >
                        {t('storeOrders.invoice')}
                      </Button>
                    ) : null}
                    {canUseWarehouseManagerActions ? (
                      <Button
                        icon={<CopyOutlined />}
                        className="store-order-excel-paste-button"
                        disabled={isReadonlyOrder || isPasteOptimisticPreviewActive}
                        onClick={() => {
                          resetPasteState('allocQuantity')
                          setPasteModalOpen(true)
                        }}
                      >
                        {t('storeOrders.excelPaste')}
                      </Button>
                    ) : null}
                    {canUseWarehouseManagerActions ? (
                      <Button
                        icon={<SaveOutlined />}
                        className="store-order-save-edited-lines-button"
                        loading={lineActionLoading}
                        disabled={isReadonlyOrder || isPasteOptimisticPreviewActive || editedLineCount === 0}
                        onClick={() => void handleSaveEditedLines()}
                      >
                        {t('storeOrders.detail.saveEditedLines')}
                      </Button>
                    ) : null}
                    {canUseWarehouseManagerActions ? (
                      <Button
                        icon={<SyncOutlined />}
                        loading={refreshImportPriceLoading}
                        disabled={!detail || isPasteOptimisticPreviewActive || refreshImportPriceLoading}
                        onClick={handleRefreshImportPricesFromWarehouse}
                      >
                        {t('storeOrders.detail.refreshImportPrices')}
                      </Button>
                    ) : null}
                    <Button
                      icon={<SortAscendingOutlined />}
                      disabled={isPasteOptimisticPreviewActive}
                      onClick={handleResetDetailDefaultSort}
                    >
                      {t('storeOrders.detail.defaultSort')}
                    </Button>
                    {canUseWarehouseManagerActions ? (
                      <Button disabled={isReadonlyOrder || isPasteOptimisticPreviewActive || !selectedLineKeys.length} onClick={() => setBatchModalOpen(true)}>
                        {t('storeOrders.batchModify')}
                      </Button>
                    ) : null}
                    {canUseWarehouseManagerActions ? (
                      <Typography.Text type="secondary">{t('storeOrders.detail.selectedRows', { count: selectedLineKeys.length })}</Typography.Text>
                    ) : null}
                  </Space>
                ) : undefined
              }
            >
              {isPasteOptimisticPreviewActive ? (
                <Alert
                  type="info"
                  showIcon
                  message={t('storeOrders.detail.pasteOptimisticPreviewTitle', '已先显示本次 Excel 预览')}
                  description={t(
                    'storeOrders.detail.pasteOptimisticPreviewDescription',
                    '后台正在写入；完成后会自动刷新确认。失败时会刷新服务器数据并提示原因。',
                  )}
                  style={{ marginBottom: 12 }}
                />
              ) : null}
              <div className="store-order-detail-filter-bar">
                <Space wrap size={[8, 8]}>
                  <Typography.Text strong>{t('storeOrders.statsFilter')}</Typography.Text>
                  <Input
                    allowClear
                    placeholder={t('storeOrders.filterByItemNumber')}
                    style={{ width: 220 }}
                    value={detailItemFilter}
                    onChange={(event) => {
                      // 远程筛选会切换结果集，先清空勾选，避免对旧行执行批量操作。
                      setSelectedLineKeys([])
                      setDetailPage(1)
                      setDetailItemFilter(event.target.value)
                    }}
                  />
                  <Tag
                    color={detailStatFilter === 'all' ? 'processing' : 'default'}
                    style={{ cursor: 'pointer' }}
                    onClick={() => {
                      setSelectedLineKeys([])
                      setDetailPage(1)
                      setDetailStatFilter('all')
                    }}
                  >
                    {t('storeOrders.allItems', { count: statSummary.all })}
                  </Tag>
                  <Tag
                    color={detailStatFilter === 'orderedNotShipped' ? 'orange' : 'gold'}
                    style={{ cursor: 'pointer' }}
                    onClick={() => {
                      setSelectedLineKeys([])
                      setDetailPage(1)
                      setDetailStatFilter((current) => (current === 'orderedNotShipped' ? 'all' : 'orderedNotShipped'))
                    }}
                  >
                    {t('storeOrders.orderedNotShipped', { count: statSummary.orderedNotShipped })}
                  </Tag>
                  <Tag
                    color={detailStatFilter === 'shippedWithoutOrder' ? 'geekblue' : 'blue'}
                    style={{ cursor: 'pointer' }}
                    onClick={() => {
                      setSelectedLineKeys([])
                      setDetailPage(1)
                      setDetailStatFilter((current) => (current === 'shippedWithoutOrder' ? 'all' : 'shippedWithoutOrder'))
                    }}
                  >
                    {t('storeOrders.shippedWithoutOrder', { count: statSummary.shippedWithoutOrder })}
                  </Tag>
                  <Typography.Text type="secondary">
                    {t('storeOrders.detail.currentRows', { count: detail.itemsTotal ?? detail.items.length })}
                  </Typography.Text>
                  <Button
                    size="small"
                    disabled={!isDetailColumnSettingsCustomized}
                    onClick={resetDetailColumnLayout}
                  >
                    {t('storeOrders.detail.resetColumnLayout', '重置列布局')}
                  </Button>
                </Space>
              </div>
              <DndContext sensors={detailColumnDragSensors} collisionDetection={closestCenter} onDragEnd={handleColumnDragEnd}>
                <SortableContext items={detailSortableColumnKeys} strategy={horizontalListSortingStrategy}>
                  <MeasuredTable
                    metricId="warehouse.store-orders.detail.table-2"
                    className="store-order-detail-table"
                    rowKey="detailGUID"
                    virtual
                    loading={lineActionLoading}
                    columns={columns}
                    dataSource={detail.items}
                    components={{ header: { cell: DraggableHeaderCell } }}
                    rowSelection={
                      canUseWarehouseManagerActions
                        ? {
                            selectedRowKeys: selectedLineKeys,
                            onChange: setSelectedLineKeys,
                            preserveSelectedRowKeys: false,
                            columnWidth: STORE_ORDER_DETAIL_SELECTION_COLUMN_WIDTH,
                          }
                        : undefined
                    }
                    pagination={{
                      current: detailPage,
                      pageSize: detailPageSize,
                      total: detail.itemsTotal ?? detail.items.length,
                      showSizeChanger: true,
                      // 图片交给浏览器懒加载，表格本身按服务端分页分批请求商品明细。
                      pageSizeOptions: STORE_ORDER_DETAIL_PAGE_SIZE_OPTIONS,
                      onChange: (nextPage, nextPageSize) => {
                        setSelectedLineKeys([])
                        setDetailPage(nextPage)
                        setDetailPageSize(nextPageSize)
                      },
                    }}
                    onChange={(_, __, sorter, extra) => {
                      if (extra.action === 'paginate' || extra.action === 'filter') {
                        return
                      }

                      const nextSorter = Array.isArray(sorter) ? sorter[0] : (sorter as SorterResult<StoreOrderDetailLine>)
                      const field = nextSorter?.field
                      setSelectedLineKeys([])
                      setDetailPage(1)
                      if (isStoreOrderDetailSortField(field) && nextSorter.order) {
                        setDetailSortField(field)
                        setDetailSortOrder(nextSorter.order)
                        return
                      }
                      setDetailSortField(null)
                      setDetailSortOrder(null)
                    }}
                    scroll={{ x: detailTableScrollX, y: 620 }}
                  />
                </SortableContext>
              </DndContext>
            </Card>

            <Card>
              <Typography.Text type="secondary">
                {t('storeOrders.stage2Note')}
              </Typography.Text>
              <div style={{ marginTop: 8 }}>
                <Typography.Text type="secondary">
                  {t('storeOrders.lastUpdateTime', { time: formatDateTime(new Date().toISOString()) })}
                </Typography.Text>
              </div>
            </Card>

            <ProductPickerModal
              open={pickerOpen}
              orderGUID={detail.orderGUID}
              loading={lineActionLoading}
              onCancel={() => setPickerOpen(false)}
              onConfirm={handlePickerConfirm}
            />

            <ContainerProductPicker
              open={containerPickerOpen}
              loading={lineActionLoading || containerPickerLoading}
              alreadySelectedCodes={containerExistingProductCodes}
              onClose={() => setContainerPickerOpen(false)}
              onConfirm={handlePickerConfirm}
            />

            <BatchEditModal
              open={batchModalOpen}
              loading={batchLoading}
              selectedCount={selectedLineKeys.length}
              onCancel={() => setBatchModalOpen(false)}
              onConfirm={(payload) => handleBatchConfirm(payload)}
            />

            <SupplyNoticeModal
              open={Boolean(supplyNoticeTarget)}
              mode="delist"
              productCount={supplyNoticeTarget?.productCodes.length ?? 0}
              confirmLoading={supplyNoticeSaving}
              onCancel={() => setSupplyNoticeTarget(null)}
              onSubmit={handleSupplyNoticeSubmit}
            />

            <Modal
              title={t('storeOrders.detail.excelPasteTitle')}
              open={pasteModalOpen}
              width={880}
              destroyOnHidden
              onCancel={() => setPasteModalOpen(false)}
              footer={[
                <Button key="cancel" onClick={() => setPasteModalOpen(false)}>
                  {t('common.close')}
                </Button>,
                <Button key="parse" type="primary" loading={parsingPaste} onClick={() => void handleParsePasteData()}>
                  {t('storeOrders.detail.parseData')}
                </Button>,
                <Button
                  key="confirm"
                  type="primary"
                  loading={submittingPaste}
                  disabled={!canUseWarehouseManagerActions || isReadonlyOrder || validPastePreviewCount === 0}
                  onClick={() => void handleConfirmPaste()}
                >
                  {t('storeOrders.detail.importValidRows', { count: validPastePreviewCount })}
                </Button>,
              ]}
            >
              <Space direction="vertical" size={16} style={{ width: '100%' }}>
                <div>
                  <Typography.Text strong>{t('storeOrders.detail.writeTarget')}</Typography.Text>
                  <div style={{ marginTop: 8 }}>
                    <Radio.Group
                      value={pasteTargetField}
                      onChange={(event) => handleChangePasteTargetField(event.target.value as StoreOrderPasteWriteTarget)}
                    >
                      <Radio value="allocQuantity">{t('storeOrders.detail.allocQuantityDefault')}</Radio>
                      <Radio value="allocQuantityByInner">{t('storeOrders.detail.allocQuantityByInner')}</Radio>
                      <Radio value="quantity">{t('storeOrders.detail.orderQuantity')}</Radio>
                      <Radio value="quantityByInner">{t('storeOrders.detail.orderQuantityByInner')}</Radio>
                    </Radio.Group>
                  </div>
                  <Typography.Text type="secondary" style={{ display: 'block', marginTop: 6 }}>
                    {pasteTargetField === 'allocQuantityByInner'
                      ? t('storeOrders.detail.allocQuantityByInnerHelp')
                      : pasteTargetField === 'quantityByInner'
                        ? t('storeOrders.detail.orderQuantityByInnerHelp')
                      : t('storeOrders.detail.writeTargetHelp')}
                  </Typography.Text>
                </div>

                <div>
                  <Typography.Text strong>{t('storeOrders.detail.excelText')}</Typography.Text>
                  <Input.TextArea
                    rows={7}
                    value={pasteData}
                    onChange={(event) => setPasteData(event.target.value)}
                    placeholder={t('storeOrders.detail.excelPastePlaceholder')}
                    style={{ marginTop: 8 }}
                  />
                </div>

                <div>
                  <Typography.Text strong>{t('storeOrders.detail.columnMapping')}</Typography.Text>
                  <Space wrap size={[12, 12]} style={{ display: 'flex', marginTop: 8 }}>
                    <Space>
                      <Typography.Text>{t('storeOrders.detail.itemNumberColumn')}</Typography.Text>
                      <Select
                        style={{ width: 100 }}
                        value={columnMapping.itemNumber}
                        options={[0, 1, 2, 3, 4].map((index) => ({
                          value: index,
                          label: t('storeOrders.detail.columnNumber', { number: index + 1 }),
                        }))}
                        onChange={(value) =>
                          setColumnMapping((current) => ({
                            ...current,
                            itemNumber: Number(value),
                          }))
                        }
                      />
                    </Space>
                    <Space>
                      <Typography.Text>{t('storeOrders.detail.quantityColumn')}</Typography.Text>
                      <Select
                        style={{ width: 120 }}
                        value={columnMapping.quantity}
                        options={[
                          { value: -1, label: t('storeOrders.detail.noneDefaultOne') },
                          ...[0, 1, 2, 3, 4].map((index) => ({
                            value: index,
                            label: t('storeOrders.detail.columnNumber', { number: index + 1 }),
                          })),
                        ]}
                        onChange={(value) =>
                          setColumnMapping((current) => ({
                            ...current,
                            quantity: Number(value),
                          }))
                        }
                      />
                    </Space>
                    <Space>
                      <Typography.Text>{t('storeOrders.detail.priceColumn')}</Typography.Text>
                      <Select
                        style={{ width: 120 }}
                        value={columnMapping.price}
                        options={[
                          { value: -1, label: t('storeOrders.detail.none') },
                          ...[0, 1, 2, 3, 4].map((index) => ({
                            value: index,
                            label: t('storeOrders.detail.columnNumber', { number: index + 1 }),
                          })),
                        ]}
                        onChange={(value) =>
                          setColumnMapping((current) => ({
                            ...current,
                            price: Number(value),
                          }))
                        }
                      />
                    </Space>
                  </Space>
                </div>

                {pastePreviewItems.length ? (
                  <div>
                    <Space direction="vertical" size={8} style={{ width: '100%' }}>
                      <Space wrap size={[12, 8]} style={{ justifyContent: 'space-between', width: '100%' }}>
                        <Typography.Text strong>{t('storeOrders.detail.previewResult', { valid: validPastePreviewCount, total: pastePreviewItems.length })}</Typography.Text>
                        <Radio.Group
                          size="small"
                          value={pastePreviewFilter}
                          onChange={(event) => setPastePreviewFilter(event.target.value as StoreOrderPastePreviewFilter)}
                        >
                          <Radio.Button value="all">{t('storeOrders.detail.pasteFilterAll', '全部')}</Radio.Button>
                          <Radio.Button value="importable">{t('storeOrders.detail.pasteFilterImportable', '可导入')}</Radio.Button>
                          <Radio.Button value="invalid">{t('storeOrders.detail.pasteFilterInvalid', '异常')}</Radio.Button>
                          <Radio.Button value="unmatched">{t('storeOrders.detail.pasteFilterUnmatched', '未匹配')}</Radio.Button>
                          <Radio.Button value="existing">{t('storeOrders.detail.pasteFilterExisting', '已存在')}</Radio.Button>
                        </Radio.Group>
                      </Space>
                      <Space wrap size={[8, 8]}>
                        <Typography.Text type="secondary">
                          {t('storeOrders.detail.pasteExistingCount', '已存在 {{count}} 行', { count: existingPastePreviewCount })}
                        </Typography.Text>
                        <Button size="small" disabled={!existingPastePreviewCount} onClick={() => handleSetExistingPastePreviewAction('replace')}>
                          {t('storeOrders.detail.pasteActionReplaceAll', '全部覆盖')}
                        </Button>
                        <Button size="small" disabled={!existingPastePreviewCount} onClick={() => handleSetExistingPastePreviewAction('append')}>
                          {t('storeOrders.detail.pasteActionAppendAll', '全部追加')}
                        </Button>
                        <Button size="small" disabled={!existingPastePreviewCount} onClick={() => handleSetExistingPastePreviewAction('skip')}>
                          {t('storeOrders.detail.pasteActionSkipAll', '全部跳过')}
                        </Button>
                      </Space>
                    </Space>
                    <MeasuredTable<StoreOrderPastePreviewItem> metricId="warehouse.store-orders.detail.table-3"
                      size="small"
                      rowKey={(record) => `${record.itemNumber}-${record.rowIndex}`}
                      style={{ marginTop: 8 }}
                      dataSource={filteredPastePreviewItems}
                      pagination={false}
                      scroll={{ y: 280 }}
                      columns={[
                        {
                          title: '#',
                          key: 'rowIndex',
                          width: 48,
                          align: 'center',
                          // 显示 Excel 原始行号，筛选后也能快速定位粘贴文本中的异常行。
                          render: (_, record) => record.rowIndex + 1,
                        },
                        {
                          title: t('column.status'),
                          dataIndex: 'status',
                          width: 100,
                          render: (_, record) => {
                            if (record.status === 'invalidQuantity') {
                              return <Tag color="warning">{t('storeOrders.detail.invalidQuantity', '数量异常')}</Tag>
                            }
                            if (record.status === 'unmatched') {
                              return <Tag color="error">{t('storeOrders.detail.unmatched')}</Tag>
                            }
                            if (record.status === 'existing') {
                              return <Tag color="processing">{t('storeOrders.detail.existingLine', '已存在')}</Tag>
                            }
                            return <Tag color="success">{t('storeOrders.detail.valid')}</Tag>
                          },
                        },
                        {
                          title: t('column.itemNumber'),
                          dataIndex: 'itemNumber',
                          width: 140,
                        },
                        {
                          title: t('column.productName'),
                          key: 'productName',
                          ellipsis: true,
                          render: (_, record) => record.product?.productName || '--',
                        },
                        {
                          title: t('storeOrders.detail.currentQuantity', '当前数量'),
                          key: 'existingQuantity',
                          width: 100,
                          render: (_, record) => {
                            const value = pasteApiTargetField === 'allocQuantity' ? record.existingAllocQuantity : record.existingQuantity
                            return value === undefined ? '--' : value
                          },
                        },
                        {
                          title: pasteApiTargetField === 'allocQuantity' ? t('column.shipQuantity') : t('column.orderQuantity'),
                          dataIndex: 'quantity',
                          width: 110,
                          render: (_, record) => formatPastePreviewQuantity(record, pasteQuantityMode),
                        },
                        {
                          title: t('storeOrders.detail.pasteAction', '处理方式'),
                          dataIndex: 'action',
                          width: 130,
                          render: (value: StoreOrderPasteAction, record) =>
                            record.valid ? (
                              <Select
                                size="small"
                                style={{ width: 112 }}
                                value={value}
                                options={[
                                  { value: 'replace', label: t('storeOrders.detail.pasteActionReplace', '覆盖') },
                                  { value: 'append', label: t('storeOrders.detail.pasteActionAppend', '追加') },
                                  { value: 'skip', label: t('storeOrders.detail.pasteActionSkip', '跳过') },
                                ]}
                                onChange={(nextValue) => handleChangePastePreviewAction(record.rowIndex, nextValue)}
                              />
                            ) : (
                              '--'
                            ),
                        },
                        {
                          title: t('column.importPriceShort'),
                          dataIndex: 'price',
                          width: 110,
                          render: (value: number | undefined) => (value === undefined ? '--' : formatAmount(value)),
                        },
                      ]}
                    />
                  </div>
                ) : null}
              </Space>
            </Modal>
        </Space>
      ) : (
        <Card>
          <Empty description={t('storeOrders.detail.notFound')} />
        </Card>
      )}
    </PageContainer>
  )
}
