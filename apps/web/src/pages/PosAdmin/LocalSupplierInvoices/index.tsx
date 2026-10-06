import {
  CopyOutlined,
  HolderOutlined,
  MoreOutlined,
  PlusOutlined,
  SettingOutlined,
  UploadOutlined,
} from '@ant-design/icons'
import {
  DndContext,
  KeyboardSensor,
  PointerSensor,
  closestCenter,
  type DragEndEvent,
  useSensor,
  useSensors,
} from '@dnd-kit/core'
import {
  SortableContext,
  horizontalListSortingStrategy,
  sortableKeyboardCoordinates,
  useSortable,
} from '@dnd-kit/sortable'
import { CSS } from '@dnd-kit/utilities'
import {
  Button,
  Checkbox,
  DatePicker,
  Dropdown,
  Form,
  Input,
  Modal,
  Pagination,
  Popover,
  Segmented,
  Select,
  Space,
  Tooltip,
  Typography,
  message,
} from 'antd'
import type { ColumnsType, TableRef } from 'antd/es/table'
import { useKeepAliveContext } from 'keepalive-for-react'
import {
  useCallback,
  useEffect,
  useLayoutEffect,
  useMemo,
  useRef,
  useState,
  type CSSProperties,
  type HTMLAttributes,
} from 'react'
import { useTranslation } from 'react-i18next'
import { useNavigate } from 'react-router-dom'
import { registerPageMessages } from '../../../i18n/registerPageMessages'
import { useAuthStore } from '../../../store/auth'
import { createLatestRequestGuard, runLatestGuardedRequest } from '../../../utils/latestRequestGuard'
import {
  checkInvoiceNoExists,
  createInvoice,
  deleteInvoice,
  getInvoiceGrid,
} from '../../../services/localSupplierInvoiceService'
import { getActiveLocalSuppliers } from '../../../services/localSupplierService'
import { getActiveStores } from '../../../services/storeService'
import type {
  LocalSupplierInvoiceListDto,
} from '../../../types/localSupplierInvoice'
import { copyTextToClipboard } from '../../../utils/clipboard'
import {
  buildStoreOptionsFromUserStores,
  buildScopedStoreCodeFilter,
  filterStoreOptionsByManagedCodes,
  isStoreCodeInManagedScope,
  shouldSkipScopedStoreQuery,
} from '../../../utils/managedStoreScope'
import ImportInvoiceModal from './ImportInvoiceModal'
import {
  getNextInvoiceTableScrollTop,
  scheduleInvoiceTableScrollRestore,
  shouldRestoreInvoiceTableScroll,
} from './invoiceTableScroll'
import {
  DEFAULT_LOCAL_SUPPLIER_INVOICE_COLUMN_ORDER,
  DEFAULT_LOCAL_SUPPLIER_INVOICE_HIDDEN_COLUMNS,
  LOCKED_LOCAL_SUPPLIER_INVOICE_COLUMNS,
  createLocalSupplierInvoiceDndAccessibility,
  dispatchLocalSupplierInvoiceDragHandleKeyDown,
  dispatchLocalSupplierInvoiceDragHandlePointerDown,
  dispatchLocalSupplierInvoiceSortableHeaderKeyDown,
  isLocalSupplierInvoiceColumnLayoutCustomized,
  moveLocalSupplierInvoiceColumnOrder,
  parseLocalSupplierInvoiceColumnOrder,
  parseLocalSupplierInvoiceHiddenColumns,
  toggleLocalSupplierInvoiceHiddenColumn,
  type LocalSupplierInvoiceColumnKey,
} from './columnOrder'
import { formatLocalSupplierInvoiceAuditTime, formatLocalSupplierInvoiceAuditTimeCompact } from './auditTime'
import { MeasuredTable } from '../../../components/MeasuredTable'
import invoiceMessagesEn from './invoiceMessages.en.json'
import invoiceMessagesZh from './invoiceMessages.zh.json'
import './localSupplierInvoices.css'

// 重设计新增的文案随页面代码块懒注册，不进首屏 i18n 包。
registerPageMessages({ zh: invoiceMessagesZh, en: invoiceMessagesEn })

// 2026-10 重设计调整了列（合并审计人、新增明细与价格变动），列序存储升到 v2，让所有人先看到新的默认布局。
const LOCAL_SUPPLIER_INVOICE_COLUMN_ORDER_STORAGE_KEY =
  'hbweb_rv.localSupplierInvoices.columnOrder.v2'
const LOCAL_SUPPLIER_INVOICE_HIDDEN_COLUMNS_STORAGE_KEY =
  'hbweb_rv.localSupplierInvoices.hiddenColumns.v1'

interface DraggableHeaderCellProps extends HTMLAttributes<HTMLTableCellElement> {
  'data-column-key'?: string
  'data-drag-label'?: string
  'data-sorter-enabled'?: boolean
}

interface SortableHeaderCellProps extends DraggableHeaderCellProps {
  columnKey: string
  dragLabel?: string
  sorterEnabled?: boolean
}

function SortableHeaderCell({
  children,
  style,
  columnKey,
  dragLabel,
  sorterEnabled = false,
  onKeyDown: headerKeyDownListener,
  onPointerDown: headerPointerDownListener,
  ...props
}: SortableHeaderCellProps) {
  const {
    attributes,
    listeners,
    setNodeRef,
    setActivatorNodeRef,
    transform,
    transition,
    isDragging,
  } = useSortable({
    id: columnKey,
  })

  const setHeaderNodeRef = useCallback(
    (node: HTMLTableCellElement | null) => {
      setNodeRef(node)
      if (sorterEnabled) setActivatorNodeRef(node)
    },
    [setActivatorNodeRef, setNodeRef, sorterEnabled],
  )
  const headerStyle: CSSProperties = {
    ...style,
    transform: CSS.Translate.toString(transform),
    transition,
    zIndex: isDragging ? 3 : style?.zIndex,
    opacity: isDragging ? 0.8 : style?.opacity,
    cursor: isDragging ? 'grabbing' : style?.cursor,
    touchAction: sorterEnabled ? 'none' : style?.touchAction,
  }
  const {
    onKeyDown: dndKeyDownListener,
    onPointerDown: dndPointerDownListener,
    ...otherListeners
  } = listeners ?? {}
  const sortableHeaderDndAttributes = sorterEnabled
    ? {
        'aria-describedby': attributes['aria-describedby'],
        'aria-roledescription': attributes['aria-roledescription'],
      }
    : {}

  return (
    <th
      ref={setHeaderNodeRef}
      style={headerStyle}
      {...props}
      {...sortableHeaderDndAttributes}
      onPointerDown={
        sorterEnabled
          ? (event) => {
              headerPointerDownListener?.(event)
              if (!event.defaultPrevented) dndPointerDownListener?.(event)
            }
          : headerPointerDownListener
      }
      onKeyDown={(event) => {
        if (!sorterEnabled) {
          headerKeyDownListener?.(event)
          return
        }
        dispatchLocalSupplierInvoiceSortableHeaderKeyDown(
          event,
          (dragEvent) => dndKeyDownListener?.(dragEvent),
          (sortEvent) => headerKeyDownListener?.(sortEvent),
        )
      }}
    >
      <div style={{ display: 'inline-flex', alignItems: 'center', gap: 6, width: '100%' }}>
        {sorterEnabled ? null : (
          <button
            ref={setActivatorNodeRef}
            type="button"
            aria-label={dragLabel}
            title={dragLabel}
            style={{
              display: 'inline-flex',
              flex: '0 0 auto',
              alignItems: 'center',
              justifyContent: 'center',
              width: 20,
              height: 20,
              padding: 0,
              color: 'rgba(0, 0, 0, 0.45)',
              cursor: isDragging ? 'grabbing' : 'grab',
              touchAction: 'none',
              background: 'transparent',
              border: 0,
              borderRadius: 2,
            }}
            {...attributes}
            {...otherListeners}
            onClick={(event) => event.stopPropagation()}
            onPointerDown={(event) => {
              dispatchLocalSupplierInvoiceDragHandlePointerDown(event, (dragEvent) => {
                dndPointerDownListener?.(dragEvent)
              })
            }}
            onKeyDown={(event) => {
              dispatchLocalSupplierInvoiceDragHandleKeyDown(event, (dragEvent) => {
                dndKeyDownListener?.(dragEvent)
              })
            }}
          >
            <HolderOutlined />
          </button>
        )}
        <div style={{ minWidth: 0 }}>{children}</div>
      </div>
    </th>
  )
}

function DraggableHeaderCell({ children, style, ...props }: DraggableHeaderCellProps) {
  const columnKey = props['data-column-key']
  if (!columnKey) return <th style={style} {...props}>{children}</th>

  return (
    <SortableHeaderCell
      columnKey={columnKey}
      dragLabel={props['data-drag-label']}
      sorterEnabled={props['data-sorter-enabled']}
      style={style}
      {...props}
    >
      {children}
    </SortableHeaderCell>
  )
}


const SORT_FIELD_MAP: Record<string, string> = {
  storeName: 'storeName',
  supplierName: 'supplierName',
  invoiceNo: 'invoiceNo',
  orderDate: 'orderDate',
  inboundDate: 'inboundDate',
  totalAmount: 'totalAmount',
  receivedTotalAmount: 'receivedTotalAmount',
  flowStatus: 'flowStatus',
  inboundStatus: 'inboundStatus',
  createdAt: 'createdAt',
  createdBy: 'createdBy',
  updatedAt: 'updatedAt',
  updatedBy: 'updatedBy',
}

const FLOW_STATUS_MAP: Record<number, { labelKey: string; className: string }> = {
  0: { labelKey: 'posAdmin.invoices.draft', className: 'lsi-tag lsi-tag-neutral' },
  1: { labelKey: 'posAdmin.invoices.submitted', className: 'lsi-tag lsi-tag-blue' },
  2: { labelKey: 'posAdmin.invoices.approved', className: 'lsi-tag lsi-tag-green' },
  3: { labelKey: 'posAdmin.invoices.pushed', className: 'lsi-tag lsi-tag-purple' },
}

const INBOUND_STATUS_MAP: Record<number, { labelKey: string; className: string }> = {
  0: { labelKey: 'posAdmin.invoices.notInbound', className: 'lsi-tag lsi-tag-neutral' },
  1: { labelKey: 'posAdmin.invoices.partialInbound', className: 'lsi-tag lsi-tag-orange' },
  2: { labelKey: 'posAdmin.invoices.inbounded', className: 'lsi-tag lsi-tag-green' },
}

// 列宽：供应商列不设宽度，屏幕更宽时多出的空间只给它，其余列保持紧凑。
// 列宽按 1440 宽屏幕、侧栏展开时正好放下默认列来分配（中等密度表格左右内边距各 8px）。
const COLUMN_WIDTHS: Partial<Record<LocalSupplierInvoiceColumnKey | 'action', number>> = {
  invoiceNo: 130,
  storeCode: 134,
  orderDate: 100,
  detailCount: 64,
  priceChange: 96,
  totalAmount: 100,
  isProductChecked: 148,
  flowStatus: 88,
  createdAt: 150,
  inboundDate: 104,
  inboundStatus: 100,
  receivedTotalAmount: 116,
  remarks: 180,
  updatedAt: 150,
  action: 88,
}
const FLEX_COLUMN_MIN_WIDTH = 104

function formatDate(value?: string) {
  if (!value) return '--'
  // 后端返回 ISO 日期时直接截取日期部分，避免按浏览器时区换算后跨天。
  if (/^\d{4}-\d{2}-\d{2}/.test(value)) return value.slice(0, 10)
  const date = new Date(value)
  if (Number.isNaN(date.getTime())) return value
  return date.toLocaleDateString('zh-CN')
}

function formatAmount(value?: number) {
  if (value === undefined || value === null) return '--'
  return value.toLocaleString('en-AU', { minimumFractionDigits: 2, maximumFractionDigits: 2 })
}

function formatCount(value: number) {
  return value.toLocaleString('en-AU')
}

function readStoredColumnOrder() {
  if (typeof window === 'undefined') {
    return [...DEFAULT_LOCAL_SUPPLIER_INVOICE_COLUMN_ORDER]
  }
  try {
    return parseLocalSupplierInvoiceColumnOrder(
      localStorage.getItem(LOCAL_SUPPLIER_INVOICE_COLUMN_ORDER_STORAGE_KEY),
    )
  } catch {
    return [...DEFAULT_LOCAL_SUPPLIER_INVOICE_COLUMN_ORDER]
  }
}

function readStoredHiddenColumns() {
  if (typeof window === 'undefined') {
    return [...DEFAULT_LOCAL_SUPPLIER_INVOICE_HIDDEN_COLUMNS]
  }
  try {
    return parseLocalSupplierInvoiceHiddenColumns(
      localStorage.getItem(LOCAL_SUPPLIER_INVOICE_HIDDEN_COLUMNS_STORAGE_KEY),
    )
  } catch {
    return [...DEFAULT_LOCAL_SUPPLIER_INVOICE_HIDDEN_COLUMNS]
  }
}

function writeStoredValue(key: string, value: unknown) {
  try {
    localStorage.setItem(key, JSON.stringify(value))
  } catch {
    // localStorage 不可用时仍保留当前页面内的列配置。
  }
}

type SearchScope = 'invoiceNo' | 'product'
type ProductCheckedSegment = 'all' | 'pending' | 'checked'

function toProductCheckedSegment(value: boolean | undefined): ProductCheckedSegment {
  if (value === undefined) return 'all'
  return value ? 'checked' : 'pending'
}

function fromProductCheckedSegment(value: ProductCheckedSegment): boolean | undefined {
  if (value === 'all') return undefined
  return value === 'checked'
}

export default function LocalSupplierInvoicesPage() {
  const { t } = useTranslation()
  const navigate = useNavigate()
  const { active } = useKeepAliveContext()
  const { access, currentUser } = useAuthStore()
  const isAdmin = access.isAdmin
  // 有编辑权限的人进入明细工作台处理；只读用户（如店长）进入同一页面的只读视图。
  const canEditInvoices = access.canEditLocalPurchase
  const managedStoreCodes = access.managedStoreCodes()
  const managedStoreCodeKey = managedStoreCodes?.join(',') ?? 'all'

  const [loading, setLoading] = useState(false)
  const [data, setData] = useState<LocalSupplierInvoiceListDto[]>([])
  const [total, setTotal] = useState(0)
  const [page, setPage] = useState(1)
  const [pageSize, setPageSize] = useState(20)
  const [sortBy, setSortBy] = useState('createdAt')
  const [sortOrder, setSortOrder] = useState<'ascend' | 'descend'>('descend')
  const [columnOrder, setColumnOrder] = useState<LocalSupplierInvoiceColumnKey[]>(readStoredColumnOrder)
  const [hiddenColumns, setHiddenColumns] = useState<LocalSupplierInvoiceColumnKey[]>(readStoredHiddenColumns)

  // 筛选条件
  const [storeCode, setStoreCode] = useState<string | undefined>(undefined)
  const [supplierCode, setSupplierCode] = useState<string | undefined>(undefined)
  const [invoiceNo, setInvoiceNo] = useState('')
  const [keyword, setKeyword] = useState('')
  const [productChecked, setProductChecked] = useState<boolean | undefined>(undefined)
  // 随货单号与商品关键词共用一个搜索框，用前缀下拉切换搜索范围。
  const [searchScope, setSearchScope] = useState<SearchScope>('invoiceNo')
  // 状态分段上的数量：只随分店/供应商/搜索条件变化重新统计，翻页和排序不重复请求。
  const [segmentCounts, setSegmentCounts] = useState<{ all: number; pending: number } | null>(null)

  // 下拉选项
  const [storeOptions, setStoreOptions] = useState<{ label: string; value: string }[]>([])
  const [supplierOptions, setSupplierOptions] = useState<{ label: string; value: string }[]>([])

  // 创建 Modal
  const [createVisible, setCreateVisible] = useState(false)
  const [importVisible, setImportVisible] = useState(false)
  const [createForm] = Form.useForm()
  const [creating, setCreating] = useState(false)
  const [_invoiceNoChecking, setInvoiceNoChecking] = useState(false)

  // 动态高度
  const wrapRef = useRef<HTMLDivElement>(null)
  const toolbarRef = useRef<HTMLDivElement>(null)
  const tableRegionRef = useRef<HTMLDivElement>(null)
  const pagerRef = useRef<HTMLDivElement>(null)
  const [tableScrollY, setTableScrollY] = useState<number | undefined>(undefined)
  // KeepAlive 切换业务 Tab 时保留表体纵向位置，避免返回列表后跳回第一行。
  const invoiceTableRef = useRef<TableRef | null>(null)
  const lastInvoiceTableScrollTopRef = useRef(0)
  const wasInvoiceListTabActiveRef = useRef(active)
  const listRequestGuardRef = useRef(createLatestRequestGuard())
  const countRequestGuardRef = useRef(createLatestRequestGuard())
  const lastCountFilterKeyRef = useRef<string | null>(null)
  const mountedRef = useRef(false)
  const latestLoadDataRef = useRef<() => Promise<void>>(async () => undefined)

  const columnDragSensors = useSensors(
    useSensor(PointerSensor, { activationConstraint: { distance: 8 } }),
    useSensor(KeyboardSensor, {
      coordinateGetter: sortableKeyboardCoordinates,
      keyboardCodes: {
        start: ['Space'],
        cancel: ['Escape'],
        end: ['Space'],
      },
    }),
  )
  const columnLabels = useMemo<Record<LocalSupplierInvoiceColumnKey, string>>(
    () => ({
      invoiceNo: t('posAdmin.invoices.invoiceNo'),
      storeCode: t('column.store'),
      supplierCode: t('column.supplier'),
      orderDate: t('posAdmin.invoices.orderDate'),
      detailCount: t('posAdmin.invoiceList.columnDetailCount'),
      priceChange: t('posAdmin.invoiceList.columnPriceChange'),
      totalAmount: t('column.totalAmount'),
      isProductChecked: t('posAdmin.invoiceList.columnCheck'),
      flowStatus: t('posAdmin.invoices.flowStatus', '流程状态'),
      createdAt: t('posAdmin.invoiceList.columnCreated'),
      inboundDate: t('posAdmin.invoices.inboundDate'),
      inboundStatus: t('posAdmin.invoices.inboundStatus', '入库状态'),
      receivedTotalAmount: t('posAdmin.invoices.receivedTotal', '已收总金额'),
      remarks: t('column.remarks'),
      updatedAt: t('posAdmin.invoiceList.columnUpdated'),
    }),
    [t],
  )
  const dndAccessibility = useMemo(
    () => createLocalSupplierInvoiceDndAccessibility(columnLabels, {
      instructions: t('posAdmin.invoices.dnd.instructions'),
      unknownColumn: t('posAdmin.invoices.dnd.unknownColumn'),
      dragStart: (column) => t('posAdmin.invoices.dnd.dragStart', { column }),
      dragOver: (column, overColumn) =>
        t('posAdmin.invoices.dnd.dragOver', { column, overColumn }),
      dragOverNone: (column) => t('posAdmin.invoices.dnd.dragOverNone', { column }),
      dragEnd: (column, overColumn) =>
        t('posAdmin.invoices.dnd.dragEnd', { column, overColumn }),
      dragCancel: (column) => t('posAdmin.invoices.dnd.dragCancel', { column }),
    }),
    [columnLabels, t],
  )

  const loadData = async () => {
    if (!mountedRef.current) return

    if (shouldSkipScopedStoreQuery(managedStoreCodes)) {
      listRequestGuardRef.current.invalidate()
      countRequestGuardRef.current.invalidate()
      lastCountFilterKeyRef.current = null
      setData([])
      setTotal(0)
      setSegmentCounts(null)
      setLoading(false)
      return
    }

    const startRow = (page - 1) * pageSize
    // 分段计数共用除「是否检测」以外的全部条件。
    const baseFilterModel: Record<string, unknown> = {}
    const scopedStoreFilter = buildScopedStoreCodeFilter(storeCode, managedStoreCodes)
    if (scopedStoreFilter) {
      baseFilterModel.storeCode = scopedStoreFilter
    }
    if (supplierCode) {
      baseFilterModel.supplierCode = { filterType: 'text', type: 'equals', filter: supplierCode }
    }
    if (invoiceNo) {
      baseFilterModel.invoiceNo = { filterType: 'text', type: 'contains', filter: invoiceNo }
    }
    if (keyword) {
      baseFilterModel.productKeyword = { filterType: 'text', filter: keyword }
    }
    const filterModel: Record<string, unknown> = { ...baseFilterModel }
    if (productChecked !== undefined) {
      // 后端按有效明细是否全部完成检测筛选，口径与「是否检测商品」列一致。
      filterModel.isProductChecked = { filterType: 'text', type: 'equals', filter: String(productChecked) }
    }
    const sortField = SORT_FIELD_MAP[sortBy] || sortBy
    const sortModel = [{ colId: sortField, sort: sortOrder === 'ascend' ? 'asc' : 'desc' }]

    const countFilterKey = JSON.stringify(baseFilterModel)
    if (countFilterKey !== lastCountFilterKeyRef.current) {
      lastCountFilterKeyRef.current = countFilterKey
      const baseModel = Object.keys(baseFilterModel).length ? baseFilterModel : undefined
      // 只要总数：取 1 行即可。已检测数量 = 全部 − 待检测，少发一次请求。
      void runLatestGuardedRequest(
        countRequestGuardRef.current,
        () =>
          Promise.all([
            getInvoiceGrid({ startRow: 0, endRow: 1, pageSize: 1, filterModel: baseModel } as Record<string, unknown>),
            getInvoiceGrid({
              startRow: 0,
              endRow: 1,
              pageSize: 1,
              filterModel: {
                ...baseFilterModel,
                isProductChecked: { filterType: 'text', type: 'equals', filter: 'false' },
              },
            } as Record<string, unknown>),
          ]),
        {
          onSuccess: ([allResult, pendingResult]) => {
            setSegmentCounts({ all: allResult?.total ?? 0, pending: pendingResult?.total ?? 0 })
          },
          onError: () => {
            // 计数失败不影响列表本身，下次加载再重试。
            lastCountFilterKeyRef.current = null
            setSegmentCounts(null)
          },
        },
      )
    }

    await runLatestGuardedRequest(
      listRequestGuardRef.current,
      () =>
        getInvoiceGrid({
          startRow,
          endRow: startRow + pageSize,
          pageSize,
          filterModel: Object.keys(filterModel).length ? filterModel : undefined,
          sortModel,
        } as Record<string, unknown>),
      {
        onStart: () => setLoading(true),
        onSuccess: (result) => {
          setData(result?.items ?? [])
          setTotal(result?.total ?? 0)
        },
        onError: () => message.error(t('posAdmin.invoices.loadFailed', '加载进货单列表失败')),
        onSettled: () => setLoading(false),
      },
    )
  }

  useLayoutEffect(() => {
    mountedRef.current = true

    return () => {
      mountedRef.current = false
      listRequestGuardRef.current.invalidate()
      countRequestGuardRef.current.invalidate()
    }
  }, [])

  useLayoutEffect(() => {
    latestLoadDataRef.current = loadData
  })

  useEffect(() => {
    void latestLoadDataRef.current()
  }, [page, pageSize, sortBy, sortOrder, managedStoreCodeKey])

  useLayoutEffect(() => {
    let frameId: number | null = null

    const readOuterHeight = (element: HTMLElement | null) => {
      if (!element) {
        return 0
      }

      const style = window.getComputedStyle(element)
      const marginTop = Number.parseFloat(style.marginTop) || 0
      const marginBottom = Number.parseFloat(style.marginBottom) || 0
      return Math.ceil(element.getBoundingClientRect().height + marginTop + marginBottom)
    }

    const calc = () => {
      const region = tableRegionRef.current
      if (!region) {
        return
      }

      const tableHeader = region.querySelector('.ant-table-thead') as HTMLElement | null
      const tableBody = region.querySelector('.ant-table-body') as HTMLElement | null
      const tableHeaderHeight = readOuterHeight(tableHeader)
      const horizontalScrollbarHeight = tableBody ? Math.max(0, tableBody.offsetHeight - tableBody.clientHeight) : 0
      // 分页在表格外层，scroll.y 只给表体，避免固定列继续画到分页区域。
      const available = Math.floor(region.clientHeight - tableHeaderHeight - horizontalScrollbarHeight - 8)
      const nextScrollY = available > 200 ? available : 200
      setTableScrollY((current) => (
        current === undefined || Math.abs(current - nextScrollY) > 4 ? nextScrollY : current
      ))
    }

    const scheduleCalc = () => {
      if (frameId != null) {
        window.cancelAnimationFrame(frameId)
      }
      frameId = window.requestAnimationFrame(() => {
        frameId = null
        calc()
      })
    }

    scheduleCalc()
    window.addEventListener('resize', scheduleCalc)

    if (typeof ResizeObserver === 'undefined') {
      return () => {
        if (frameId != null) window.cancelAnimationFrame(frameId)
        window.removeEventListener('resize', scheduleCalc)
      }
    }

    const observer = new ResizeObserver(scheduleCalc)
    for (const element of [wrapRef.current, toolbarRef.current, tableRegionRef.current, pagerRef.current]) {
      if (element) {
        observer.observe(element)
      }
    }

    return () => {
      if (frameId != null) window.cancelAnimationFrame(frameId)
      window.removeEventListener('resize', scheduleCalc)
      observer.disconnect()
    }
  }, [data.length, pageSize, total])

  useEffect(() => {
    const wasActive = wasInvoiceListTabActiveRef.current
    wasInvoiceListTabActiveRef.current = active

    if (!shouldRestoreInvoiceTableScroll(wasActive, active)) {
      return
    }

    const scrollTop = lastInvoiceTableScrollTopRef.current
    return scheduleInvoiceTableScrollRestore({
      requestFrame: (callback) => window.requestAnimationFrame(callback),
      cancelFrame: (frameId) => window.cancelAnimationFrame(frameId),
      restore: () => invoiceTableRef.current?.scrollTo?.({ top: scrollTop }),
    })
  }, [active])

  const handleInvoiceTableScroll = (event: React.UIEvent<HTMLDivElement>) => {
    lastInvoiceTableScrollTopRef.current = getNextInvoiceTableScrollTop(
      active,
      lastInvoiceTableScrollTopRef.current,
      event.currentTarget.scrollTop,
    )
  }

  useEffect(() => {
    const loadOptions = async () => {
      const suppliersResult = await getActiveLocalSuppliers()
        .then((value) => ({ status: 'fulfilled' as const, value }))
        .catch(() => ({ status: 'rejected' as const }))

      try {
        const stores = managedStoreCodes === null
          ? filterStoreOptionsByManagedCodes(await getActiveStores(), managedStoreCodes)
          : buildStoreOptionsFromUserStores(currentUser?.stores, { manageableOnly: true })
        setStoreOptions(stores)
        if (storeCode && !stores.some((store) => store.value === storeCode)) {
          setStoreCode(undefined)
        }
      } catch {
        setStoreOptions([])
      }

      if (suppliersResult.status === 'fulfilled') {
        setSupplierOptions(
          suppliersResult.value.map((s) => ({
            label: s.name ? `${s.localSupplierCode} - ${s.name}` : s.localSupplierCode,
            value: s.localSupplierCode,
          })),
        )
      } else {
        setSupplierOptions([])
      }
    }
    loadOptions()
  }, [currentUser?.stores, managedStoreCodeKey, storeCode])

  const requestFirstPage = (deferUntilCommitted = false, reloadFromDependencies = page !== 1) => {
    if (!mountedRef.current) return

    if (reloadFromDependencies) {
      listRequestGuardRef.current.invalidate()
      if (page !== 1) setPage(1)
      return
    }

    if (deferUntilCommitted) {
      setTimeout(() => void latestLoadDataRef.current(), 0)
      return
    }

    void latestLoadDataRef.current()
  }

  const handleSearch = () => {
    requestFirstPage()
  }

  const handleReset = () => {
    const reloadFromDependencies = page !== 1 || sortBy !== 'createdAt' || sortOrder !== 'descend'
    setStoreCode(undefined)
    setSupplierCode(undefined)
    setInvoiceNo('')
    setKeyword('')
    setProductChecked(undefined)
    setSortBy('createdAt')
    setSortOrder('descend')
    requestFirstPage(true, reloadFromDependencies)
  }

  const handleDelete = async (invoiceGuid: string) => {
    try {
      await deleteInvoice(invoiceGuid)
      message.success(t('message.deleteSuccess'))
      // 删除会改变分段计数，强制下次加载重新统计。
      lastCountFilterKeyRef.current = null
      void latestLoadDataRef.current()
    } catch {
      message.error(t('message.deleteFailed'))
    }
  }

  const handleCreate = async () => {
    const values = await createForm.validateFields()
    if (!isStoreCodeInManagedScope(values.storeCode, managedStoreCodes)) {
      message.error(t('message.noPermission', '无权操作该数据'))
      return
    }

    // 随货单号重复检测
    const invoiceNoValue = values.invoiceNo?.trim()
    if (invoiceNoValue) {
      setInvoiceNoChecking(true)
      try {
        const checkResult = await checkInvoiceNoExists({
          storeCode: values.storeCode,
          supplierCode: values.supplierCode,
          invoiceNo: invoiceNoValue,
        })
        if (checkResult.exists) {
          message.error(t('posAdmin.invoices.invoiceNoDuplicate'))
          setInvoiceNoChecking(false)
          return
        }
      } catch {
        // 检测失败不阻止创建
      }
      setInvoiceNoChecking(false)
    }

    setCreating(true)
    try {
      const newGuid = await createInvoice({
        storeCode: values.storeCode,
        supplierCode: values.supplierCode,
        invoiceNo: invoiceNoValue,
        orderDate: values.orderDate?.format('YYYY-MM-DD'),
        inboundDate: values.inboundDate?.format('YYYY-MM-DD'),
        remarks: values.remarks?.trim() || undefined,
      })
      message.success(t('message.createSuccess'))
      setCreateVisible(false)
      createForm.resetFields()
      lastCountFilterKeyRef.current = null
      navigate(`/pos-admin/local-supplier-invoices/${newGuid}`)
    } catch {
      message.error(t('message.createFailed'))
    } finally {
      setCreating(false)
    }
  }

  const handleImportedInvoiceCreated = async (invoiceGuid: string) => {
    setImportVisible(false)
    lastCountFilterKeyRef.current = null
    await latestLoadDataRef.current()
    navigate(`/pos-admin/local-supplier-invoices/${invoiceGuid}`)
  }

  const openInvoice = useCallback(
    (invoiceGuid: string) => navigate(`/pos-admin/local-supplier-invoices/${invoiceGuid}`),
    [navigate],
  )

  const confirmDeleteInvoice = (record: LocalSupplierInvoiceListDto) => {
    Modal.confirm({
      title: t('posAdmin.invoices.confirmDeleteInvoice'),
      content: t('posAdmin.invoices.deleteIrreversible'),
      okText: t('common.delete'),
      cancelText: t('common.cancel'),
      okButtonProps: { danger: true },
      onOk: () => handleDelete(record.invoiceGUID),
    })
  }

  const renderEntity = (code?: string, name?: string) => {
    if (!code && !name) return '--'
    const title = name ? `${code ?? ''} - ${name}` : code
    return (
      <span className="lsi-entity" title={title}>
        {code ? <span className="lsi-code">{code}</span> : null}
        <span className="lsi-entity-name">{name || code}</span>
      </span>
    )
  }

  // 「从HQ同步」（HQ 进货单 → HBweb）已于 2026-09-29 停用；编辑页「更新HQ商品」等写 HQ 的操作不受影响。
  const baseColumns: ColumnsType<LocalSupplierInvoiceListDto> = [
    {
      title: t('posAdmin.invoices.invoiceNo'),
      dataIndex: 'invoiceNo',
      key: 'invoiceNo',
      width: COLUMN_WIDTHS.invoiceNo,
      sorter: true,
      sortOrder: sortBy === 'invoiceNo' ? sortOrder : undefined,
      render: (value: string, record) => (
        <Space size={2} style={{ maxWidth: '100%' }}>
          <Typography.Link
            className="lsi-invoice-link"
            ellipsis
            href={`/pos-admin/local-supplier-invoices/${record.invoiceGUID}`}
            onClick={(event) => {
              // 保留浏览器的新标签打开能力，普通点击走单页路由与 KeepAlive Tab。
              if (event.metaKey || event.ctrlKey || event.shiftKey) return
              event.preventDefault()
              openInvoice(record.invoiceGUID)
            }}
          >
            {value || '--'}
          </Typography.Link>
          {value && (
            <Tooltip title={t('posAdmin.invoiceList.copyInvoiceNo')}>
              <Button
                className="lsi-copy-button"
                type="text"
                size="small"
                aria-label={t('posAdmin.invoiceList.copyInvoiceNo')}
                icon={<CopyOutlined />}
                onClick={() => void copyTextToClipboard(value)}
              />
            </Tooltip>
          )}
        </Space>
      ),
    },
    {
      title: t('column.store'),
      dataIndex: 'storeCode',
      key: 'storeCode',
      width: COLUMN_WIDTHS.storeCode,
      sorter: true,
      sortOrder: sortBy === 'storeCode' ? sortOrder : undefined,
      render: (_: string, record) => renderEntity(record.storeCode, record.storeName),
    },
    {
      title: t('column.supplier'),
      dataIndex: 'supplierCode',
      key: 'supplierCode',
      sorter: true,
      sortOrder: sortBy === 'supplierCode' ? sortOrder : undefined,
      render: (_: string, record) => renderEntity(record.supplierCode, record.supplierName),
    },
    {
      title: t('posAdmin.invoices.orderDate'),
      dataIndex: 'orderDate',
      key: 'orderDate',
      width: COLUMN_WIDTHS.orderDate,
      sorter: true,
      sortOrder: sortBy === 'orderDate' ? sortOrder : undefined,
      className: 'lsi-num',
      render: (v: string) => formatDate(v),
    },
    {
      title: t('posAdmin.invoiceList.columnDetailCount'),
      dataIndex: 'detailCount',
      key: 'detailCount',
      width: COLUMN_WIDTHS.detailCount,
      align: 'right',
      className: 'lsi-num',
      render: (value?: number) => (typeof value === 'number' ? formatCount(value) : '--'),
    },
    {
      title: t('posAdmin.invoiceList.columnPriceChange'),
      key: 'priceChange',
      width: COLUMN_WIDTHS.priceChange,
      className: 'lsi-num',
      render: (_, record) => {
        const up = record.priceIncreaseItemCount ?? 0
        const down = record.priceDecreaseItemCount ?? 0
        if (!up && !down) return <span className="lsi-muted">—</span>
        return (
          <Space size={8}>
            {up > 0 && (
              <span className="lsi-price-up" title={t('posAdmin.invoiceList.priceUpTip', { count: up })}>
                ↑{up}
              </span>
            )}
            {down > 0 && (
              <span className="lsi-price-down" title={t('posAdmin.invoiceList.priceDownTip', { count: down })}>
                ↓{down}
              </span>
            )}
          </Space>
        )
      },
    },
    {
      title: t('column.totalAmount'),
      dataIndex: 'totalAmount',
      key: 'totalAmount',
      width: COLUMN_WIDTHS.totalAmount,
      align: 'right',
      sorter: true,
      sortOrder: sortBy === 'totalAmount' ? sortOrder : undefined,
      className: 'lsi-num',
      render: (v: number) => <strong style={{ fontWeight: 600 }}>{formatAmount(v)}</strong>,
    },
    {
      title: t('posAdmin.invoiceList.columnCheck'),
      dataIndex: 'isProductChecked',
      key: 'isProductChecked',
      width: COLUMN_WIDTHS.isProductChecked,
      render: (value: boolean | undefined, record) => {
        // 兼容尚未返回汇总字段的后端，缺失值不能误报为未检测。
        if (typeof value !== 'boolean') return '--'
        const unchecked = record.uncheckedDetailCount ?? 0
        const newProducts = record.newProductDetailCount ?? 0
        return (
          <Space size={6}>
            {value ? (
              <span className="lsi-pill lsi-pill-ok">{t('posAdmin.invoiceList.checked')}</span>
            ) : (
              <span className="lsi-pill lsi-pill-warn">
                {unchecked > 0
                  ? t('posAdmin.invoiceList.pendingCount', { count: unchecked })
                  : t('posAdmin.invoiceList.pending')}
              </span>
            )}
            {newProducts > 0 && (
              <Tooltip title={t('posAdmin.invoiceList.newProductsTip')}>
                <span className="lsi-tag lsi-tag-new">{t('posAdmin.invoiceList.newProducts', { count: newProducts })}</span>
              </Tooltip>
            )}
          </Space>
        )
      },
    },
    {
      title: t('posAdmin.invoices.flowStatus', '流程状态'),
      dataIndex: 'flowStatus',
      key: 'flowStatus',
      width: COLUMN_WIDTHS.flowStatus,
      sorter: true,
      sortOrder: sortBy === 'flowStatus' ? sortOrder : undefined,
      render: (v: number) => {
        const info = FLOW_STATUS_MAP[v]
        return info ? <span className={info.className}>{t(info.labelKey)}</span> : '--'
      },
    },
    {
      title: t('posAdmin.invoiceList.columnCreated'),
      dataIndex: 'createdAt',
      key: 'createdAt',
      width: COLUMN_WIDTHS.createdAt,
      sorter: true,
      sortOrder: sortBy === 'createdAt' ? sortOrder : undefined,
      className: 'lsi-num',
      render: (v: string, record) => (
        <span title={formatLocalSupplierInvoiceAuditTime(v)}>
          {formatLocalSupplierInvoiceAuditTimeCompact(v)}
          {record.createdBy ? <span className="lsi-muted"> · {record.createdBy}</span> : null}
        </span>
      ),
    },
    {
      title: t('posAdmin.invoices.inboundDate'),
      dataIndex: 'inboundDate',
      key: 'inboundDate',
      width: COLUMN_WIDTHS.inboundDate,
      sorter: true,
      sortOrder: sortBy === 'inboundDate' ? sortOrder : undefined,
      className: 'lsi-num',
      render: (v: string) => formatDate(v),
    },
    {
      title: t('posAdmin.invoices.inboundStatus', '入库状态'),
      dataIndex: 'inboundStatus',
      key: 'inboundStatus',
      width: COLUMN_WIDTHS.inboundStatus,
      sorter: true,
      sortOrder: sortBy === 'inboundStatus' ? sortOrder : undefined,
      render: (v: number) => {
        const info = INBOUND_STATUS_MAP[v]
        return info ? <span className={info.className}>{t(info.labelKey)}</span> : '--'
      },
    },
    {
      title: t('posAdmin.invoices.receivedTotal', '已收总金额'),
      dataIndex: 'receivedTotalAmount',
      key: 'receivedTotalAmount',
      width: COLUMN_WIDTHS.receivedTotalAmount,
      align: 'right',
      sorter: true,
      sortOrder: sortBy === 'receivedTotalAmount' ? sortOrder : undefined,
      className: 'lsi-num',
      render: (v: number) => formatAmount(v),
    },
    {
      title: t('column.remarks'),
      dataIndex: 'remarks',
      key: 'remarks',
      width: COLUMN_WIDTHS.remarks,
      ellipsis: true,
      render: (v: string) => v || '--',
    },
    {
      title: t('posAdmin.invoiceList.columnUpdated'),
      dataIndex: 'updatedAt',
      key: 'updatedAt',
      width: COLUMN_WIDTHS.updatedAt,
      sorter: true,
      sortOrder: sortBy === 'updatedAt' ? sortOrder : undefined,
      className: 'lsi-num',
      render: (v: string, record) => (
        <span title={formatLocalSupplierInvoiceAuditTime(v)}>
          {formatLocalSupplierInvoiceAuditTimeCompact(v)}
          {record.updatedBy ? <span className="lsi-muted"> · {record.updatedBy}</span> : null}
        </span>
      ),
    },
    {
      title: t('column.action'),
      key: 'action',
      fixed: 'right',
      width: COLUMN_WIDTHS.action,
      render: (_, record) => (
        <Space size={0}>
          <Button type="link" size="small" onClick={() => openInvoice(record.invoiceGUID)}>
            {canEditInvoices ? t('posAdmin.invoiceList.process') : t('posAdmin.invoiceList.view')}
          </Button>
          <Dropdown
            trigger={['click']}
            menu={{
              items: [
                { key: 'salesAnalysis', label: t('posAdmin.invoiceList.salesAnalysis') },
                ...(isAdmin ? [{ key: 'delete', label: t('common.delete'), danger: true }] : []),
              ],
              onClick: ({ key }) => {
                if (key === 'salesAnalysis') {
                  navigate(`/pos-admin/local-supplier-invoices/${record.invoiceGUID}/sales-analysis`)
                } else if (key === 'delete') {
                  confirmDeleteInvoice(record)
                }
              },
            }}
          >
            <Button
              type="text"
              size="small"
              icon={<MoreOutlined />}
              aria-label={t('posAdmin.invoiceList.moreActions')}
            />
          </Dropdown>
        </Space>
      ),
    },
  ]

  const hiddenColumnSet = useMemo(() => new Set(hiddenColumns), [hiddenColumns])
  const isColumnLayoutCustomized = isLocalSupplierInvoiceColumnLayoutCustomized(columnOrder, hiddenColumns)

  const handleColumnDragEnd = ({ active, over }: DragEndEvent) => {
    if (!over || active.id === over.id) return
    const nextOrder = moveLocalSupplierInvoiceColumnOrder(columnOrder, active.id, over.id)
    setColumnOrder(nextOrder)
    writeStoredValue(LOCAL_SUPPLIER_INVOICE_COLUMN_ORDER_STORAGE_KEY, nextOrder)
  }

  const handleToggleColumn = (key: LocalSupplierInvoiceColumnKey) => {
    const nextHidden = toggleLocalSupplierInvoiceHiddenColumn(hiddenColumns, key)
    setHiddenColumns(nextHidden)
    writeStoredValue(LOCAL_SUPPLIER_INVOICE_HIDDEN_COLUMNS_STORAGE_KEY, nextHidden)
  }

  const resetColumnLayout = () => {
    setColumnOrder([...DEFAULT_LOCAL_SUPPLIER_INVOICE_COLUMN_ORDER])
    setHiddenColumns([...DEFAULT_LOCAL_SUPPLIER_INVOICE_HIDDEN_COLUMNS])
    try {
      localStorage.removeItem(LOCAL_SUPPLIER_INVOICE_COLUMN_ORDER_STORAGE_KEY)
      localStorage.removeItem(LOCAL_SUPPLIER_INVOICE_HIDDEN_COLUMNS_STORAGE_KEY)
    } catch {
      // localStorage 不可用时仍恢复当前页面内的默认列配置。
    }
    message.success(t('posAdmin.invoiceList.layoutReset'))
  }

  const visibleColumnOrder = columnOrder.filter((key) => !hiddenColumnSet.has(key))
  const columnMap = new Map(baseColumns.map((column) => [String(column.key), column]))
  const columns = [
    ...visibleColumnOrder.map((key) => {
      const column = columnMap.get(key)
      if (!column) return undefined
      return {
        ...column,
        onHeaderCell: () => ({
          'data-column-key': key,
          'data-sorter-enabled': Boolean(column.sorter),
          'data-drag-label': t('posAdmin.invoices.dragColumn', {
            column: columnLabels[key],
          }),
        } as DraggableHeaderCellProps),
      }
    }),
    columnMap.get('action'),
  ].filter(
    (column): column is ColumnsType<LocalSupplierInvoiceListDto>[number] => Boolean(column),
  )
  // 表格最小宽度 = 定宽列之和 + 弹性列（供应商）最小宽度；更宽的屏幕把多余空间留给弹性列。
  const tableScrollX = columns.reduce(
    (sum, column) => sum + (typeof column.width === 'number' ? column.width : FLEX_COLUMN_MIN_WIDTH),
    0,
  )

  const hasActiveFilters = Boolean(storeCode || supplierCode || invoiceNo || keyword || productChecked !== undefined)
  const searchValue = searchScope === 'invoiceNo' ? invoiceNo : keyword
  const pendingCount = segmentCounts?.pending
  const checkedCount = segmentCounts ? Math.max(0, segmentCounts.all - segmentCounts.pending) : undefined

  const renderSegmentLabel = (label: string, count?: number, warn = false) => (
    <span>
      {label}
      {typeof count === 'number' && (
        <span className={warn && count > 0 ? 'lsi-segment-count lsi-segment-count-warn' : 'lsi-segment-count'}>
          {formatCount(count)}
        </span>
      )}
    </span>
  )

  const handleSearchTextChange = (value: string) => {
    if (searchScope === 'invoiceNo') setInvoiceNo(value)
    else setKeyword(value)
    // 点清除图标时立即刷新；普通输入等回车再查。
    if (!value && searchValue) requestFirstPage(true)
  }

  const handleSearchScopeChange = (scope: SearchScope) => {
    const text = searchValue
    setSearchScope(scope)
    setInvoiceNo(scope === 'invoiceNo' ? text : '')
    setKeyword(scope === 'product' ? text : '')
    if (text.trim()) requestFirstPage(true)
  }

  const columnSettingsContent = (
    <div className="lsi-column-settings">
      <Typography.Text type="secondary" style={{ fontSize: 12, padding: '0 4px 4px' }}>
        {t('posAdmin.invoiceList.columnSettingsHint')}
      </Typography.Text>
      {columnOrder.map((key) => {
        const locked = LOCKED_LOCAL_SUPPLIER_INVOICE_COLUMNS.includes(key)
        return (
          <div key={key} className="lsi-column-settings-row">
            <Checkbox
              checked={!hiddenColumnSet.has(key)}
              disabled={locked}
              onChange={() => handleToggleColumn(key)}
            >
              {columnLabels[key]}
            </Checkbox>
            {locked && (
              <Typography.Text type="secondary" style={{ fontSize: 12 }}>
                {t('posAdmin.invoiceList.columnLocked')}
              </Typography.Text>
            )}
          </div>
        )
      })}
      <div className="lsi-column-settings-footer">
        <span />
        <Button size="small" disabled={!isColumnLayoutCustomized} onClick={resetColumnLayout}>
          {t('posAdmin.invoiceList.restoreDefault')}
        </Button>
      </div>
    </div>
  )

  return (
    <div className="lsi-list-page">
      <div className="page-header page-header-compact">
        <div className="page-header-compact-title">
          <Typography.Title level={4} style={{ margin: 0 }}>
            {t('posAdmin.invoices.title')}
          </Typography.Title>
          <Typography.Text type="secondary" className="page-header-compact-subtitle">
            {t('posAdmin.invoiceList.totalCount', { count: formatCount(segmentCounts?.all ?? total) })}
          </Typography.Text>
        </div>
        <Space wrap>
          {isAdmin && (
            <Button
              icon={<UploadOutlined />}
              disabled={storeOptions.length === 0}
              onClick={() => setImportVisible(true)}
            >
              {t('posAdmin.invoices.import.uploadButton')}
            </Button>
          )}
          {isAdmin && (
            <Button
              type="primary"
              icon={<PlusOutlined />}
              disabled={storeOptions.length === 0}
              onClick={() => setCreateVisible(true)}
            >
              {t('posAdmin.invoices.createInvoice')}
            </Button>
          )}
        </Space>
      </div>

      <section
        ref={wrapRef}
        className="lsi-list-card"
        style={{
          height: 'calc(100vh - 252px)',
          minHeight: 420,
          display: 'flex',
          flexDirection: 'column',
          overflow: 'hidden',
        }}
      >
        <div ref={toolbarRef} className="lsi-list-toolbar">
          <Segmented<ProductCheckedSegment>
            aria-label={t('posAdmin.invoiceList.segmentAria')}
            value={toProductCheckedSegment(productChecked)}
            onChange={(value) => {
              setProductChecked(fromProductCheckedSegment(value))
              requestFirstPage(true)
            }}
            options={[
              { value: 'all', label: renderSegmentLabel(t('posAdmin.invoiceList.segmentAll'), segmentCounts?.all) },
              { value: 'pending', label: renderSegmentLabel(t('posAdmin.invoiceList.segmentPending'), pendingCount, true) },
              { value: 'checked', label: renderSegmentLabel(t('posAdmin.invoiceList.segmentChecked'), checkedCount) },
            ]}
          />
          <span className="lsi-list-toolbar-divider" />
          <Select
            allowClear
            showSearch
            optionFilterProp="label"
            prefix={t('posAdmin.invoiceList.storeFilter')}
            placeholder={t('posAdmin.invoiceList.allOption')}
            style={{ width: 180 }}
            value={storeCode}
            onChange={(v) => {
              setStoreCode(v)
              requestFirstPage(true)
            }}
            options={storeOptions}
          />
          <Select
            allowClear
            showSearch
            optionFilterProp="label"
            prefix={t('posAdmin.invoiceList.supplierFilter')}
            placeholder={t('posAdmin.invoiceList.allOption')}
            style={{ width: 200 }}
            value={supplierCode}
            onChange={(v) => {
              setSupplierCode(v)
              requestFirstPage(true)
            }}
            options={supplierOptions}
          />
          <Space.Compact>
            <Select<SearchScope>
              value={searchScope}
              style={{ width: 104 }}
              onChange={handleSearchScopeChange}
              options={[
                { value: 'invoiceNo', label: t('posAdmin.invoiceList.searchScopeInvoiceNo') },
                { value: 'product', label: t('posAdmin.invoiceList.searchScopeProduct') },
              ]}
            />
            <Input
              allowClear
              placeholder={searchScope === 'invoiceNo'
                ? t('posAdmin.invoiceList.searchPlaceholderInvoiceNo')
                : t('posAdmin.invoiceList.searchPlaceholderProduct')}
              style={{ width: 200 }}
              value={searchValue}
              onChange={(e) => handleSearchTextChange(e.target.value)}
              onPressEnter={handleSearch}
            />
          </Space.Compact>
          {hasActiveFilters && (
            <Button type="link" onClick={handleReset} style={{ paddingInline: 4 }}>
              {t('posAdmin.invoiceList.clearFilters')}
            </Button>
          )}
          <span className="lsi-list-toolbar-spacer" />
          <Popover
            trigger="click"
            placement="bottomRight"
            title={t('posAdmin.invoiceList.columnSettingsTitle')}
            content={columnSettingsContent}
          >
            <Tooltip title={t('posAdmin.invoiceList.columnSettings')}>
              <Button type="text" icon={<SettingOutlined />} aria-label={t('posAdmin.invoiceList.columnSettings')} />
            </Tooltip>
          </Popover>
        </div>

        <div ref={tableRegionRef} style={{ flex: 1, minHeight: 0, overflow: 'hidden', borderTop: '1px solid #eef1f5' }}>
          <DndContext
            sensors={columnDragSensors}
            collisionDetection={closestCenter}
            onDragEnd={handleColumnDragEnd}
            accessibility={dndAccessibility}
          >
            <SortableContext items={visibleColumnOrder} strategy={horizontalListSortingStrategy}>
              <MeasuredTable metricId="pos-admin.local-supplier-invoices.table-1"
                ref={invoiceTableRef}
                rowKey="invoiceGUID"
                loading={loading}
                dataSource={data}
                components={{ header: { cell: DraggableHeaderCell } }}
                columns={columns}
                pagination={false}
                size="middle"
                scroll={{ x: tableScrollX, y: tableScrollY }}
                onScroll={handleInvoiceTableScroll}
                onChange={(_pagination, _filters, sorter) => {
                  const s = Array.isArray(sorter) ? sorter[0] : sorter
                  const field = s?.field || s?.column?.dataIndex
                  const order = s?.order as 'ascend' | 'descend' | undefined
                  if (field && order) {
                    setSortBy(String(field))
                    setSortOrder(order)
                  } else {
                    setSortBy('createdAt')
                    setSortOrder('descend')
                  }
                }}
              />
            </SortableContext>
          </DndContext>
        </div>

        <div
          ref={pagerRef}
          style={{
            padding: '8px 16px',
            display: 'flex',
            justifyContent: 'space-between',
            alignItems: 'center',
            gap: 12,
            width: '100%',
            background: '#fff',
            borderTop: '1px solid #eef1f5',
            position: 'relative',
            zIndex: 3,
            flexShrink: 0,
          }}
        >
          <Typography.Text type="secondary" className="lsi-num">
            {t('posAdmin.invoiceList.totalCount', { count: formatCount(total) })}
          </Typography.Text>
          <Pagination
            current={page}
            pageSize={pageSize}
            total={total}
            onChange={(p, ps) => {
              setPage(p)
              setPageSize(ps)
            }}
            showSizeChanger
            responsive={false}
            pageSizeOptions={[10, 20, 50, 100, 200]}
          />
        </div>
      </section>

      <Modal
        open={createVisible}
        title={t('posAdmin.invoices.createTitle')}
        confirmLoading={creating}
        onCancel={() => {
          setCreateVisible(false)
          createForm.resetFields()
        }}
        onOk={() => void handleCreate()}
      >
        <Form form={createForm} layout="vertical">
          <Form.Item
            name="storeCode"
            label={t('column.store')}
            rules={[{ required: true, message: t('form.pleaseSelectStore') }]}
          >
            <Select
              showSearch
              optionFilterProp="label"
              placeholder={t('form.pleaseSelectStore')}
              options={storeOptions}
            />
          </Form.Item>
          <Form.Item
            name="supplierCode"
            label={t('column.supplier')}
            rules={[{ required: true, message: t('form.pleaseSelectSupplier') }]}
          >
            <Select
              showSearch
              optionFilterProp="label"
              placeholder={t('form.pleaseSelectSupplier')}
              options={supplierOptions}
            />
          </Form.Item>
          <Form.Item
            name="invoiceNo"
            label={t('posAdmin.invoices.invoiceNo')}
            validateTrigger={['onBlur']}
            rules={[
              { required: true, message: t('posAdmin.invoices.invoiceNoRequired') },
              {
                validator: async (_, value) => {
                  if (!value?.trim()) return
                  const storeCodeValue = createForm.getFieldValue('storeCode')
                  const supplierCodeValue = createForm.getFieldValue('supplierCode')
                  if (!storeCodeValue || !supplierCodeValue) return
                  try {
                    const result = await checkInvoiceNoExists({
                      storeCode: storeCodeValue,
                      supplierCode: supplierCodeValue,
                      invoiceNo: value.trim(),
                    })
                    if (result.exists) {
                      throw new Error(t('posAdmin.invoices.invoiceNoDuplicate'))
                    }
                  } catch (err) {
                    if (err instanceof Error && err.message === t('posAdmin.invoices.invoiceNoDuplicate')) {
                      throw err
                    }
                  }
                },
              },
            ]}
          >
            <Input placeholder={t('posAdmin.invoices.invoiceNoRequired')} />
          </Form.Item>
          <Form.Item name="orderDate" label={t('posAdmin.invoices.orderDate')}>
            <DatePicker style={{ width: '100%' }} />
          </Form.Item>
          <Form.Item name="inboundDate" label={t('posAdmin.invoices.inboundDate')}>
            <DatePicker style={{ width: '100%' }} />
          </Form.Item>
          <Form.Item name="remarks" label={t('column.remarks')}>
            <Input.TextArea rows={3} placeholder={t('form.pleaseInput')} />
          </Form.Item>
        </Form>
      </Modal>

      <ImportInvoiceModal
        open={importVisible}
        storeOptions={storeOptions}
        supplierOptions={supplierOptions}
        onCancel={() => setImportVisible(false)}
        onCreated={handleImportedInvoiceCreated}
      />
    </div>
  )
}
