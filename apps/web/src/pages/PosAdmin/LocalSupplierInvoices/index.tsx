import {
  CopyOutlined,
  HolderOutlined,
  PlusOutlined,
  ReloadOutlined,
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
  Card,
  DatePicker,
  Form,
  Input,
  Modal,
  Pagination,
  Popconfirm,
  Select,
  Space,
  Tag,
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
import { useAuthStore } from '../../../store/auth'
import { createLatestRequestGuard, runLatestGuardedRequest } from '../../../utils/latestRequestGuard'
import { getStableTagColor } from '../../../utils/tagColors'
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
  createLocalSupplierInvoiceDndAccessibility,
  dispatchLocalSupplierInvoiceDragHandleKeyDown,
  dispatchLocalSupplierInvoiceDragHandlePointerDown,
  dispatchLocalSupplierInvoiceSortableHeaderKeyDown,
  isLocalSupplierInvoiceColumnOrderCustomized,
  moveLocalSupplierInvoiceColumnOrder,
  parseLocalSupplierInvoiceColumnOrder,
  type LocalSupplierInvoiceColumnKey,
} from './columnOrder'
import { formatLocalSupplierInvoiceAuditTime } from './auditTime'
import { MeasuredTable } from '../../../components/MeasuredTable'

const LOCAL_SUPPLIER_INVOICE_COLUMN_ORDER_STORAGE_KEY =
  'hbweb_rv.localSupplierInvoices.columnOrder.v1'

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

const FLOW_STATUS_MAP: Record<number, { labelKey: string; color: string }> = {
  0: { labelKey: 'posAdmin.invoices.draft', color: 'default' },
  1: { labelKey: 'posAdmin.invoices.submitted', color: 'blue' },
  2: { labelKey: 'posAdmin.invoices.approved', color: 'green' },
  3: { labelKey: 'posAdmin.invoices.pushed', color: 'purple' },
}

const INBOUND_STATUS_MAP: Record<number, { labelKey: string; color: string }> = {
  0: { labelKey: 'posAdmin.invoices.notInbound', color: 'default' },
  1: { labelKey: 'posAdmin.invoices.partialInbound', color: 'orange' },
  2: { labelKey: 'posAdmin.invoices.inbounded', color: 'green' },
}

function formatDate(value?: string) {
  if (!value) return '--'
  const date = new Date(value)
  if (Number.isNaN(date.getTime())) return value
  return date.toLocaleDateString('zh-CN')
}

function formatAmount(value?: number) {
  if (value === undefined || value === null) return '--'
  return value.toFixed(2)
}

export default function LocalSupplierInvoicesPage() {
  const { t } = useTranslation()
  const navigate = useNavigate()
  const { active } = useKeepAliveContext()
  const { access, currentUser } = useAuthStore()
  const isAdmin = access.isAdmin
  const managedStoreCodes = access.managedStoreCodes()
  const managedStoreCodeKey = managedStoreCodes?.join(',') ?? 'all'

  const [loading, setLoading] = useState(false)
  const [data, setData] = useState<LocalSupplierInvoiceListDto[]>([])
  const [total, setTotal] = useState(0)
  const [page, setPage] = useState(1)
  const [pageSize, setPageSize] = useState(20)
  const [sortBy, setSortBy] = useState('createdAt')
  const [sortOrder, setSortOrder] = useState<'ascend' | 'descend'>('descend')
  const [columnOrder, setColumnOrder] = useState<LocalSupplierInvoiceColumnKey[]>(() => {
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
  })

  // 筛选条件
  const [storeCode, setStoreCode] = useState<string | undefined>(undefined)
  const [supplierCode, setSupplierCode] = useState<string | undefined>(undefined)
  const [invoiceNo, setInvoiceNo] = useState('')
  const [keyword, setKeyword] = useState('')

  // 下拉选项
  const [storeOptions, setStoreOptions] = useState<{ label: string; value: string }[]>([])
  const [supplierOptions, setSupplierOptions] = useState<{ label: string; value: string }[]>([])

  // 行选择
  const [selectedRowKeys, setSelectedRowKeys] = useState<React.Key[]>([])

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
      storeCode: t('column.store'),
      supplierCode: t('column.supplier'),
      invoiceNo: t('posAdmin.invoices.invoiceNo'),
      orderDate: t('posAdmin.invoices.orderDate'),
      inboundDate: t('posAdmin.invoices.inboundDate'),
      totalAmount: t('column.totalAmount'),
      receivedTotalAmount: t('posAdmin.invoices.receivedTotal', '已收总金额'),
      isProductChecked: t('posAdmin.invoices.isProductChecked'),
      flowStatus: t('posAdmin.invoices.flowStatus', '流程状态'),
      inboundStatus: t('posAdmin.invoices.inboundStatus', '入库状态'),
      remarks: t('column.remarks'),
      createdAt: t('column.createTime'),
      createdBy: t('column.creator'),
      updatedAt: t('column.updateTime'),
      updatedBy: t('column.updater'),
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
      setData([])
      setTotal(0)
      setSelectedRowKeys([])
      setLoading(false)
      return
    }

    const startRow = (page - 1) * pageSize
    const filterModel: Record<string, unknown> = {}
    const scopedStoreFilter = buildScopedStoreCodeFilter(storeCode, managedStoreCodes)
    if (scopedStoreFilter) {
      filterModel.storeCode = scopedStoreFilter
    }
    if (supplierCode) {
      filterModel.supplierCode = { filterType: 'text', type: 'equals', filter: supplierCode }
    }
    if (invoiceNo) {
      filterModel.invoiceNo = { filterType: 'text', type: 'contains', filter: invoiceNo }
    }
    if (keyword) {
      filterModel.productKeyword = { filterType: 'text', filter: keyword }
    }
    const sortField = SORT_FIELD_MAP[sortBy] || sortBy
    const sortModel = [{ colId: sortField, sort: sortOrder === 'ascend' ? 'asc' : 'desc' }]

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
            label: s.name || s.localSupplierCode,
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
    setSortBy('createdAt')
    setSortOrder('descend')
    requestFirstPage(true, reloadFromDependencies)
  }

  const handleDelete = async (invoiceGuid: string) => {
    try {
      await deleteInvoice(invoiceGuid)
      message.success(t('message.deleteSuccess'))
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
      navigate(`/pos-admin/local-supplier-invoices/${newGuid}`)
    } catch {
      message.error(t('message.createFailed'))
    } finally {
      setCreating(false)
    }
  }

  const handleImportedInvoiceCreated = async (invoiceGuid: string) => {
    setImportVisible(false)
    await latestLoadDataRef.current()
    navigate(`/pos-admin/local-supplier-invoices/${invoiceGuid}`)
  }

  // 「从HQ同步」（HQ 进货单 → HBweb）已于 2026-09-29 停用；编辑页「更新HQ商品」等写 HQ 的操作不受影响。
  const baseColumns: ColumnsType<LocalSupplierInvoiceListDto> = [
    {
      title: t('column.index'),
      key: 'index',
      width: 60,
      align: 'right',
      render: (_, __, index) => (page - 1) * pageSize + index + 1,
    },
    {
      title: t('column.store'),
      dataIndex: 'storeCode',
      key: 'storeCode',
      width: 160,
      sorter: true,
      sortOrder: sortBy === 'storeCode' ? sortOrder : undefined,
      render: (_: string, record) => {
        const storeText = record.storeName ? `${record.storeCode} - ${record.storeName}` : record.storeCode || '--'

        return (
          <Tag
            color={getStableTagColor(record.storeCode || '')}
            style={{
              maxWidth: '100%',
              whiteSpace: 'normal',
            }}
            title={storeText}
          >
            {/* 分店名称较长时允许自动换行，最多展示两行，避免挤压后续列。 */}
            <span
              style={{
                display: '-webkit-box',
                overflow: 'hidden',
                lineHeight: '18px',
                overflowWrap: 'anywhere',
                WebkitBoxOrient: 'vertical',
                WebkitLineClamp: 2,
              }}
            >
              {storeText}
            </span>
          </Tag>
        )
      },
    },
    {
      title: t('column.supplier'),
      dataIndex: 'supplierCode',
      key: 'supplierCode',
      width: 160,
      sorter: true,
      sortOrder: sortBy === 'supplierCode' ? sortOrder : undefined,
      render: (_: string, record) => {
        const supplierText = record.supplierName ? `${record.supplierCode} - ${record.supplierName}` : record.supplierCode || '--'

        return (
          <Tag
            color={getStableTagColor(record.supplierCode || '')}
            style={{
              maxWidth: '100%',
              whiteSpace: 'normal',
            }}
            title={supplierText}
          >
            {/* 供应商内容较长时允许自动换行，最多展示两行，避免挤压后续列。 */}
            <span
              style={{
                display: '-webkit-box',
                overflow: 'hidden',
                lineHeight: '18px',
                overflowWrap: 'anywhere',
                WebkitBoxOrient: 'vertical',
                WebkitLineClamp: 2,
              }}
            >
              {supplierText}
            </span>
          </Tag>
        )
      },
    },
    {
      title: t('posAdmin.invoices.invoiceNo'),
      dataIndex: 'invoiceNo',
      key: 'invoiceNo',
      width: 160,
      sorter: true,
      sortOrder: sortBy === 'invoiceNo' ? sortOrder : undefined,
      render: (value: string) => (
        <Space size={4}>
          <span>{value || '--'}</span>
          {value && (
            <Button
              type="text"
              size="small"
              icon={<CopyOutlined />}
              onClick={() => void copyTextToClipboard(value)}
            />
          )}
        </Space>
      ),
    },
    {
      title: t('posAdmin.invoices.orderDate'),
      dataIndex: 'orderDate',
      key: 'orderDate',
      width: 120,
      sorter: true,
      sortOrder: sortBy === 'orderDate' ? sortOrder : undefined,
      render: (v: string) => formatDate(v),
    },
    {
      title: t('posAdmin.invoices.inboundDate'),
      dataIndex: 'inboundDate',
      key: 'inboundDate',
      width: 120,
      sorter: true,
      sortOrder: sortBy === 'inboundDate' ? sortOrder : undefined,
      render: (v: string) => formatDate(v),
    },
    {
      title: t('column.totalAmount'),
      dataIndex: 'totalAmount',
      key: 'totalAmount',
      width: 120,
      align: 'right',
      sorter: true,
      sortOrder: sortBy === 'totalAmount' ? sortOrder : undefined,
      render: (v: number) => formatAmount(v),
    },
    {
      title: t('posAdmin.invoices.receivedTotal', '已收总金额'),
      dataIndex: 'receivedTotalAmount',
      key: 'receivedTotalAmount',
      width: 120,
      align: 'right',
      sorter: true,
      sortOrder: sortBy === 'receivedTotalAmount' ? sortOrder : undefined,
      render: (v: number) => formatAmount(v),
    },
    {
      title: t('posAdmin.invoices.isProductChecked'),
      dataIndex: 'isProductChecked',
      key: 'isProductChecked',
      width: 120,
      align: 'center',
      render: (value?: boolean) => {
        // 兼容尚未返回汇总字段的后端，缺失值不能误报为未检测。
        if (typeof value !== 'boolean') return '--'
        return <Tag color={value ? 'green' : 'default'}>{t(value ? 'common.yes' : 'common.no')}</Tag>
      },
    },
    {
      title: t('posAdmin.invoices.flowStatus', '流程状态'),
      dataIndex: 'flowStatus',
      key: 'flowStatus',
      width: 100,
      sorter: true,
      sortOrder: sortBy === 'flowStatus' ? sortOrder : undefined,
      render: (v: number) => {
        const info = FLOW_STATUS_MAP[v] || { labelKey: String(v), color: 'default' }
        return <Tag color={info.color}>{t(info.labelKey)}</Tag>
      },
    },
    {
      title: t('posAdmin.invoices.inboundStatus', '入库状态'),
      dataIndex: 'inboundStatus',
      key: 'inboundStatus',
      width: 100,
      sorter: true,
      sortOrder: sortBy === 'inboundStatus' ? sortOrder : undefined,
      render: (v: number) => {
        const info = INBOUND_STATUS_MAP[v] || { labelKey: String(v), color: 'default' }
        return <Tag color={info.color}>{t(info.labelKey)}</Tag>
      },
    },
    {
      title: t('column.remarks'),
      dataIndex: 'remarks',
      key: 'remarks',
      width: 180,
      ellipsis: true,
      render: (v: string) => v || '--',
    },
    {
      title: t('column.createTime'),
      dataIndex: 'createdAt',
      key: 'createdAt',
      width: 170,
      sorter: true,
      sortOrder: sortBy === 'createdAt' ? sortOrder : undefined,
      render: (v: string) => formatLocalSupplierInvoiceAuditTime(v),
    },
    {
      title: t('column.creator'),
      dataIndex: 'createdBy',
      key: 'createdBy',
      width: 120,
      render: (v: string) => v || '--',
    },
    {
      title: t('column.updateTime'),
      dataIndex: 'updatedAt',
      key: 'updatedAt',
      width: 170,
      sorter: true,
      sortOrder: sortBy === 'updatedAt' ? sortOrder : undefined,
      render: (v: string) => formatLocalSupplierInvoiceAuditTime(v),
    },
    {
      title: t('column.updater'),
      dataIndex: 'updatedBy',
      key: 'updatedBy',
      width: 120,
      render: (v: string) => v || '--',
    },
    {
      title: t('column.action'),
      key: 'action',
      fixed: 'right',
      width: 180,
      render: (_, record) => (
        <Space size={0}>
          <Button
            type="link"
            onClick={() => navigate(`/pos-admin/invoice-detail/${record.invoiceGUID}`)}
          >
            {t('common.view')}
          </Button>
          {isAdmin && (
            <Button
              type="link"
              onClick={() => navigate(`/pos-admin/local-supplier-invoices/${record.invoiceGUID}`)}
            >
              {t('common.edit')}
            </Button>
          )}
          {isAdmin && (
            <Popconfirm
              title={t('posAdmin.invoices.confirmDeleteInvoice')}
              description={t('posAdmin.invoices.deleteIrreversible')}
              okText={t('common.delete')}
              cancelText={t('common.cancel')}
              okButtonProps={{ danger: true }}
              onConfirm={() => void handleDelete(record.invoiceGUID)}
            >
              <Button type="link" danger>
                {t('common.delete')}
              </Button>
            </Popconfirm>
          )}
        </Space>
      ),
    },
  ]

  const isColumnOrderCustomized =
    isLocalSupplierInvoiceColumnOrderCustomized(columnOrder)

  const handleColumnDragEnd = ({ active, over }: DragEndEvent) => {
    if (!over || active.id === over.id) return
    const nextOrder = moveLocalSupplierInvoiceColumnOrder(columnOrder, active.id, over.id)
    setColumnOrder(nextOrder)
    try {
      localStorage.setItem(
        LOCAL_SUPPLIER_INVOICE_COLUMN_ORDER_STORAGE_KEY,
        JSON.stringify(nextOrder),
      )
    } catch {
      // localStorage 不可用时仍保留当前页面内的列顺序。
    }
  }

  const resetColumnOrder = () => {
    setColumnOrder([...DEFAULT_LOCAL_SUPPLIER_INVOICE_COLUMN_ORDER])
    try {
      localStorage.removeItem(LOCAL_SUPPLIER_INVOICE_COLUMN_ORDER_STORAGE_KEY)
    } catch {
      // localStorage 不可用时仍恢复当前页面内的默认列顺序。
    }
    message.success(t('posAdmin.invoices.columnOrderReset'))
  }

  const columnMap = new Map(baseColumns.map((column) => [String(column.key), column]))
  const columns = [
    columnMap.get('index'),
    ...columnOrder.map((key) => {
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

  return (
    <Card
      title={t('posAdmin.invoices.title')}
      styles={{ body: { padding: 0 } }}
      extra={
        <Space>
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
      }
    >
      <div
        ref={wrapRef}
        style={{
          height: 'calc(100vh - 160px)',
          display: 'flex',
          flexDirection: 'column',
          overflow: 'hidden',
        }}
      >
        <div ref={toolbarRef} style={{ padding: 16 }}>
          <Space wrap>
            <Select
              allowClear
              showSearch
              optionFilterProp="label"
              placeholder={t('form.pleaseSelectStore')}
              style={{ width: 200 }}
              value={storeCode}
              onChange={(v) => {
                setStoreCode(v)
              }}
              options={storeOptions}
            />
            <Select
              allowClear
              showSearch
              optionFilterProp="label"
              placeholder={t('form.pleaseSelectSupplier')}
              style={{ width: 200 }}
              value={supplierCode}
              onChange={(v) => {
                setSupplierCode(v)
              }}
              options={supplierOptions}
            />
            <Input
              allowClear
              placeholder={t('posAdmin.invoices.invoiceNo')}
              style={{ width: 180 }}
              value={invoiceNo}
              onChange={(e) => setInvoiceNo(e.target.value)}
            />
            <Input
              allowClear
              placeholder={t('posAdmin.invoices.productKeyword', '商品关键词')}
              style={{ width: 180 }}
              value={keyword}
              onChange={(e) => setKeyword(e.target.value)}
            />
            <Button type="primary" onClick={handleSearch}>
              {t('common.query')}
            </Button>
            <Button onClick={handleReset}>{t('common.reset')}</Button>
            {isColumnOrderCustomized ? (
              <Button icon={<ReloadOutlined />} onClick={resetColumnOrder}>
                {t('posAdmin.invoices.resetColumns')}
              </Button>
            ) : null}
          </Space>
        </div>

        <div ref={tableRegionRef} style={{ flex: 1, minHeight: 0, overflow: 'hidden' }}>
          <DndContext
            sensors={columnDragSensors}
            collisionDetection={closestCenter}
            onDragEnd={handleColumnDragEnd}
            accessibility={dndAccessibility}
          >
            <SortableContext items={columnOrder} strategy={horizontalListSortingStrategy}>
              <MeasuredTable metricId="pos-admin.local-supplier-invoices.table-1"
                ref={invoiceTableRef}
                rowKey="invoiceGUID"
                loading={loading}
                dataSource={data}
                components={{ header: { cell: DraggableHeaderCell } }}
                columns={columns}
                pagination={false}
                scroll={{ x: 2320, y: tableScrollY }}
                rowSelection={
                  isAdmin
                    ? {
                        selectedRowKeys,
                        onChange: (keys) => setSelectedRowKeys(keys),
                      }
                    : undefined
                }
                rowClassName={(_, index) => (index % 2 === 1 ? 'table-row-striped' : '')}
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
            width: '100%',
            background: '#fff',
            position: 'relative',
            zIndex: 3,
            flexShrink: 0,
          }}
        >
          <div />
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
      </div>

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
    </Card>
  )
}
