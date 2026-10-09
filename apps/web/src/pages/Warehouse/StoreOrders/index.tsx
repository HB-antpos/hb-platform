import {
  ArrowDownOutlined,
  ArrowUpOutlined,
  CheckCircleOutlined,
  ColumnWidthOutlined,
  CopyOutlined,
  DeleteOutlined,
  DownOutlined,
  FileSearchOutlined,
  FileTextOutlined,
  LoadingOutlined,
  MoreOutlined,
  PlusOutlined,
  PrinterOutlined,
  ReloadOutlined,
  RollbackOutlined,
  SearchOutlined,
  TeamOutlined,
  ToolOutlined,
} from '@ant-design/icons'
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
import type { ColumnsType, TablePaginationConfig } from 'antd/es/table'
import type { SorterResult, SortOrder } from 'antd/es/table/interface'
import {
  App as AntdApp,
  Button,
  DatePicker,
  Dropdown,
  Form,
  Input,
  InputNumber,
  Modal,
  Popconfirm,
  Radio,
  Select,
  Space,
  Switch,
  Tag,
  Tooltip,
  Typography,
} from 'antd'
import type { MenuProps } from 'antd'
import type { Dayjs } from 'dayjs'
import dayjs from 'dayjs'
import { useCallback, useEffect, useLayoutEffect, useMemo, useRef, useState, type CSSProperties, type HTMLAttributes, type PointerEvent as ReactPointerEvent, type ReactNode } from 'react'
import { useTranslation } from 'react-i18next'
import { useNavigate } from 'react-router-dom'
import PageContainer from '../../../components/PageContainer'
import ActiveFilterBar, { type ActiveFilterItem } from '../../../components/listToolbar/ActiveFilterBar'
import MoreFiltersButton from '../../../components/listToolbar/MoreFiltersButton'
import SelectionActionBar from '../../../components/listToolbar/SelectionActionBar'
import StatusPill from '../../../components/listToolbar/StatusPill'
import StatusTabs, { type StatusTabItem } from '../../../components/listToolbar/StatusTabs'
import { registerPageMessages } from '../../../i18n/registerPageMessages'
import {
  batchMapStoreOrderStoreCode,
  batchUpdateStoreOrderStatus,
  copyStoreOrder,
  createStoreOrder,
  deleteStoreOrder,
  getStoreOrderList,
  getUnmatchedStoreOrderGroups,
  getUsedStoreOrderBranches,
  updateStoreOrderOutboundDate,
  updateStoreOrderStatus,
} from '../../../services/storeOrderService'
import { listAssignmentSummaries, type Assignee } from '../../../services/warehousePickingAssignmentService'
import { createStore, getNextStoreCode, getStores } from '../../../services/storeService'
import { useAuthStore } from '../../../store/auth'
import type { CreateStoreDto, StoreDto } from '../../../types/store'
import type {
  CopyStoreOrderPayload,
  StoreOrderBranchOption,
  StoreOrderFlowStatus,
  StoreOrderListColumnFilters,
  StoreOrderListItem,
  StoreOrderListQuery,
  UnmatchedStoreOrderGroup,
} from '../../../types/storeOrder'
import { StoreOrderFlowStatus as FlowStatus } from '../../../types/storeOrder'
import { copyTextToClipboard } from '../../../utils/clipboard'
import { RequestError } from '../../../utils/request'
import { createLatestRequestGuard, runLatestGuardedRequest } from '../../../utils/latestRequestGuard'
import {
  isStoreOrderListColumnOrderCustomized,
  mergeStoreOrderListColumnOrder,
  moveStoreOrderListColumnOrder,
  type StoreOrderListTableColumnKey,
} from './columnOrder'
import BatchAssignModal from './pickingAssignment/BatchAssignModal'
import { AssigneeChip, pickingSlipsPath } from './pickingAssignment/PickingAssignmentSection'
import {
  DEFAULT_STORE_ORDER_STATUS_TAB,
  STORE_ORDER_COUNTED_STATUSES,
  buildStoreOrderStatusCountQuery,
  buildStoreOrderStatusCountSignature,
  buildStoreOrderStatusTabCounts,
  canCopySelectedStoreOrders,
  describeStoreOrderUpdatedAt,
  formatStoreOrderDateRange,
  formatStoreOrderInteger,
  formatStoreOrderListDate,
  formatStoreOrderMoney,
  formatStoreOrderNumberRange,
  getActiveStoreOrderMoreFilterGroups,
  getStoreOrderOutboundState,
  getStoreOrderStatusPillTone,
  getStoreOrderStatusTabStatusList,
  removeStoreOrderMoreFilterGroup,
  summarizeStoreOrders,
  toStoreOrderStatusCounts,
  type StoreOrderMoreFilterGroup,
  type StoreOrderStatusCounts,
  type StoreOrderStatusTabKey,
} from './storeOrderListLogic'
// 序号规则与明细页相同：(页码-1)×每页条数 + 行下标 + 1。
import { resolveStoreOrderDetailRowNumber } from './storeOrderDetailLogic'
import storeOrdersMessagesEn from './storeOrdersMessages.en.json'
import storeOrdersMessagesZh from './storeOrdersMessages.zh.json'
import { formatStoreOrderVolume } from './volumeFormat'
import { applyFlowStatusToOrderList, subscribeStoreOrderFlowStatusChanged } from './storeOrderFlowStatusSync'
import './compact.css'
import { MeasuredTable } from '../../../components/MeasuredTable'

// 列表页改版新增文案随页面懒注册，不进入首屏全局语言包。
registerPageMessages({ zh: storeOrdersMessagesZh, en: storeOrdersMessagesEn })

type RangeValue = [Dayjs | null, Dayjs | null] | null
type StoreOrderListTextFilterKey = 'orderNo' | 'remarks' | 'updatedBy'
type StoreOrderListDateStartKey = 'outboundDateStart' | 'createdAtStart' | 'updatedAtStart'
type StoreOrderListDateEndKey = 'outboundDateEnd' | 'createdAtEnd' | 'updatedAtEnd'
type StoreOrderListNumberFilterKey =
  | 'totalQuantityMin'
  | 'totalQuantityMax'
  | 'totalOrderAmountMin'
  | 'totalOrderAmountMax'
  | 'totalOrderVolumeMin'
  | 'totalOrderVolumeMax'
  | 'importTotalAmountMin'
  | 'importTotalAmountMax'
type StoreOrderRowMenuAction = 'detail' | 'picking' | 'invoice' | 'copy' | 'markCompleted' | 'markSubmitted' | 'delete'
type StoreCashRegisterFilter = 'all' | 'enabled' | 'disabled'
const UNMATCHED_TARGET_STORE_PAGE_SIZE = 500

interface StorePickerModalProps {
  open: boolean
  title: string
  loading?: boolean
  onCancel: () => void
  onSelect: (store: StoreDto) => void
}

interface CopyOrderModalProps {
  open: boolean
  loading?: boolean
  /** 源订单号：行内菜单和勾选条都会打开本弹窗，显示出来避免复制错单。 */
  sourceOrderNo?: string
  onCancel: () => void
  onConfirm: (payload: Omit<CopyStoreOrderPayload, 'sourceOrderGUID'>) => void
}

function getLocale(language?: string) {
  return language?.startsWith('zh') ? 'zh-CN' : 'en-US'
}

function formatDateTime(value?: string, language?: string) {
  if (!value) {
    return '--'
  }

  const date = new Date(value)
  if (Number.isNaN(date.getTime())) {
    return value
  }

  return date.toLocaleString(getLocale(language), { hour12: false })
}

function formatDate(value?: string, language?: string) {
  if (!value) {
    return '--'
  }

  const date = new Date(value)
  if (Number.isNaN(date.getTime())) {
    return value
  }

  return date.toLocaleDateString(getLocale(language))
}

function renderStoreOrderNumericCell(value: ReactNode) {
  return <span className="store-order-numeric-cell">{value}</span>
}

function renderStoreOrderTwoLineText(value?: string) {
  if (!value) {
    return <>--</>
  }
  return <span className="store-order-two-line-text" title={value}>{value}</span>
}

function normalizeStoreFilterText(value: unknown) {
  return String(value ?? '').trim().toLowerCase()
}

function filterStoreOption(input: string, option?: { label?: unknown; value?: unknown }) {
  const keyword = normalizeStoreFilterText(input)
  if (!keyword) {
    return true
  }

  return (
    normalizeStoreFilterText(option?.label).includes(keyword)
    || normalizeStoreFilterText(option?.value).includes(keyword)
  )
}

function getApiErrorCode(error: unknown) {
  if (!(error instanceof RequestError)) {
    return undefined
  }
  const payload = error.payload
  return typeof payload === 'object' && payload !== null && 'errorCode' in payload
    ? String(payload.errorCode)
    : undefined
}

function buildUnmatchedTargetStoreLabel(store: StoreDto) {
  const storeName = store.storeName || store.storeCode
  const displayName = store.isActive ? storeName : `${storeName}（停用）`
  const labelParts = [
    `${store.storeCode} - ${displayName}`,
    store.brandName?.trim(),
    store.address?.trim(),
  ].filter(Boolean)

  return labelParts.join('｜')
}

async function loadAllUnmatchedTargetStores() {
  const stores: StoreDto[] = []
  let page = 1

  while (true) {
    const result = await getStores({
      page,
      pageSize: UNMATCHED_TARGET_STORE_PAGE_SIZE,
      sortField: 'storeName',
      sortOrder: 'ascend',
    })
    stores.push(...result.items)

    if (stores.length >= result.total || result.items.length < UNMATCHED_TARGET_STORE_PAGE_SIZE) {
      break
    }

    page += 1
  }

  return stores
}

function hasStoreOrderListQueryOverride(
  overrides: Partial<StoreOrderListQuery>,
  key: keyof StoreOrderListQuery,
) {
  return Object.prototype.hasOwnProperty.call(overrides, key)
}

function cleanStoreOrderListColumnFilters(
  filters?: StoreOrderListColumnFilters,
): StoreOrderListColumnFilters | undefined {
  if (!filters) {
    return undefined
  }

  const next: StoreOrderListColumnFilters = {}
  const assignText = (key: StoreOrderListTextFilterKey) => {
    const value = filters[key]?.trim()
    if (value) {
      next[key] = value
    }
  }
  const assignDate = (key: StoreOrderListDateStartKey | StoreOrderListDateEndKey) => {
    const value = filters[key]
    if (value) {
      next[key] = value
    }
  }
  const assignNumber = (key: keyof StoreOrderListColumnFilters) => {
    const value = filters[key]
    if (typeof value === 'number' && Number.isFinite(value)) {
      next[key] = value as never
    }
  }

  assignText('orderNo')
  assignText('remarks')
  assignText('updatedBy')
  assignDate('outboundDateStart')
  assignDate('outboundDateEnd')
  assignDate('createdAtStart')
  assignDate('createdAtEnd')
  assignDate('updatedAtStart')
  assignDate('updatedAtEnd')
  assignNumber('totalQuantityMin')
  assignNumber('totalQuantityMax')
  assignNumber('totalOrderAmountMin')
  assignNumber('totalOrderAmountMax')
  assignNumber('totalOrderVolumeMin')
  assignNumber('totalOrderVolumeMax')
  assignNumber('importTotalAmountMin')
  assignNumber('importTotalAmountMax')

  return Object.keys(next).length ? next : undefined
}

const STORE_ORDER_LIST_SELECTION_COLUMN_WIDTH = 40
// 序号列在可拖拽/可调宽的列体系之外：固定宽度、固定在订单号列左侧，不进列顺序/列宽存储。
const STORE_ORDER_LIST_INDEX_COLUMN_KEY = 'rowIndex'
const STORE_ORDER_LIST_INDEX_COLUMN_WIDTH = 52
// 改版合并了日期、数量、金额列，旧版（v1）保存的列序与列宽对不上新列，换 v2 让所有人从新默认布局开始。
const STORE_ORDER_LIST_COLUMN_ORDER_STORAGE_KEY = 'hbweb_rv.storeOrders.list.columnOrder.v2'
const STORE_ORDER_LIST_COLUMN_WIDTH_STORAGE_KEY = 'hbweb_rv.storeOrders.list.columnWidths.v2'
// 默认列宽合计（含勾选列）约 1104px：1440 宽屏减去侧栏与内边距后无需横向滚动。
const STORE_ORDER_LIST_DEFAULT_COLUMN_WIDTHS = {
  orderNo: 148,
  storeCode: 120,
  orderOutboundDate: 108,
  flowStatus: 88,
  pickingAssignment: 176,
  quantityVolume: 92,
  orderShipAmount: 124,
  remarks: 112,
  action: 96,
} as const
const STORE_ORDER_KEYWORD_DEBOUNCE_MS = 300
// 受控排序且不允许「取消排序」：原先取消后列头没有箭头、请求却仍按该列排序，容易误读。
const STORE_ORDER_TEXT_SORT_DIRECTIONS: SortOrder[] = ['ascend', 'descend', 'ascend']
const STORE_ORDER_NUMBER_SORT_DIRECTIONS: SortOrder[] = ['descend', 'ascend', 'descend']
const STORE_ORDER_LIST_MIN_COLUMN_WIDTH = 48
const STORE_ORDER_LIST_MAX_COLUMN_WIDTH = 420
type StoreOrderListColumnWidthMap = Partial<Record<StoreOrderListTableColumnKey, number>>

function clampStoreOrderListColumnWidth(width: number) {
  return Math.max(STORE_ORDER_LIST_MIN_COLUMN_WIDTH, Math.min(STORE_ORDER_LIST_MAX_COLUMN_WIDTH, Math.round(width)))
}

function normalizeStoreOrderListColumnWidths(
  value: unknown,
  allowedKeys: readonly StoreOrderListTableColumnKey[],
): StoreOrderListColumnWidthMap {
  if (!value || typeof value !== 'object') {
    return {}
  }

  const allowedSet = new Set(allowedKeys)
  const nextWidths: StoreOrderListColumnWidthMap = {}
  Object.entries(value as Record<string, unknown>).forEach(([key, width]) => {
    if (!allowedSet.has(key) || typeof width !== 'number' || !Number.isFinite(width)) {
      return
    }
    nextWidths[key] = clampStoreOrderListColumnWidth(width)
  })
  return nextWidths
}

function areStoreOrderListColumnWidthsEqual(
  left: StoreOrderListColumnWidthMap,
  right: StoreOrderListColumnWidthMap,
) {
  const leftKeys = Object.keys(left)
  const rightKeys = Object.keys(right)
  return leftKeys.length === rightKeys.length && leftKeys.every((key) => left[key] === right[key])
}

function persistStoreOrderListColumnWidths(nextWidths: StoreOrderListColumnWidthMap) {
  try {
    if (Object.keys(nextWidths).length) {
      localStorage.setItem(STORE_ORDER_LIST_COLUMN_WIDTH_STORAGE_KEY, JSON.stringify(nextWidths))
    } else {
      localStorage.removeItem(STORE_ORDER_LIST_COLUMN_WIDTH_STORAGE_KEY)
    }
  } catch {
    // localStorage 不可用时不影响当前页面内拖拽列宽。
  }
}

interface DraggableHeaderCellProps extends HTMLAttributes<HTMLTableCellElement> {
  'data-column-key'?: string
  'data-column-width'?: number
  'data-column-fixed'?: 'right'
  onColumnResizeStart?: (
    columnKey: StoreOrderListTableColumnKey,
    width: number,
    resizeFromLeft: boolean,
    event: ReactPointerEvent<HTMLSpanElement>,
  ) => void
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
    id: columnKey ?? '__store-order-list-static-column__',
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
      <div className="store-order-list-draggable-header" {...attributes} {...listeners}>
        {children}
      </div>
      <span
        className={`store-order-list-column-resize-handle${resizeFromLeft ? ' store-order-list-column-resize-handle-left' : ''}`}
        aria-hidden="true"
        onPointerDown={(event) => {
          event.preventDefault()
          event.stopPropagation()
          if (!columnKey || typeof columnWidth !== 'number') return
          event.currentTarget.setPointerCapture(event.pointerId)
          onColumnResizeStart?.(columnKey, columnWidth, resizeFromLeft, event)
        }}
        onClick={(event) => {
          event.preventDefault()
          event.stopPropagation()
        }}
      />
    </th>
  )
}

function StorePickerModal({ open, title, loading, onCancel, onSelect }: StorePickerModalProps) {
  const { t } = useTranslation()
  const { message } = AntdApp.useApp()
  const [createForm] = Form.useForm<CreateStoreDto>()
  const [stores, setStores] = useState<StoreDto[]>([])
  const [fetching, setFetching] = useState(false)
  const [keyword, setKeyword] = useState('')
  const [cashRegisterFilter, setCashRegisterFilter] = useState<StoreCashRegisterFilter>('all')
  const [createOpen, setCreateOpen] = useState(false)
  const [createSaving, setCreateSaving] = useState(false)
  const [storeCodeLoading, setStoreCodeLoading] = useState(false)
  const [reloadToken, setReloadToken] = useState(0)

  useEffect(() => {
    if (!open) {
      return
    }

    let cancelled = false

    const loadStores = async () => {
      setFetching(true)
      try {
        const result = await getStores({
          page: 1,
          pageSize: 200,
          ...(cashRegisterFilter === 'all' ? {} : { isActive: cashRegisterFilter === 'enabled' }),
          search: keyword || undefined,
          sortField: 'storeName',
          sortOrder: 'ascend',
        })

        if (!cancelled) {
          setStores(result.items)
        }
      } catch (error) {
        console.error(error)
        if (!cancelled) {
          message.error(t('storeOrders.loadStoresFailed'))
        }
      } finally {
        if (!cancelled) {
          setFetching(false)
        }
      }
    }

    void loadStores()

    return () => {
      cancelled = true
    }
  }, [cashRegisterFilter, keyword, message, open, reloadToken, t])

  const resetCreateForm = () => {
    createForm.resetFields()
    createForm.setFieldsValue({ isActive: false })
  }

  const loadNextStoreCode = async () => {
    setStoreCodeLoading(true)
    try {
      const nextCode = await getNextStoreCode()
      createForm.setFieldsValue({ storeCode: nextCode })
    } catch (error) {
      console.error(error)
      message.error(error instanceof Error ? error.message : t('storeOrders.loadNextStoreCodeFailed'))
    } finally {
      setStoreCodeLoading(false)
    }
  }

  const handleClose = () => {
    setKeyword('')
    setCashRegisterFilter('all')
    setCreateOpen(false)
    resetCreateForm()
    onCancel()
  }

  const handleOpenCreate = () => {
    setCreateOpen(true)
    resetCreateForm()
    void loadNextStoreCode()
  }

  const handleCreateStore = async () => {
    try {
      const values = await createForm.validateFields()
      setCreateSaving(true)
      // 创建订单弹窗中新建的分店默认不启用收银系统，避免误进入 POS 侧业务范围。
      const created = await createStore({ ...values, isActive: values.isActive ?? false })
      message.success(t('storeOrders.createStoreSuccess'))
      setCreateOpen(false)
      resetCreateForm()
      setReloadToken((current) => current + 1)
      onSelect(created)
    } catch (error) {
      if (typeof error === 'object' && error !== null && 'errorFields' in error) {
        return
      }
      console.error(error)
      message.error(
        getApiErrorCode(error) === 'DUPLICATE_STORE_CODE'
          ? t('storeOrders.duplicateStoreCode')
          : error instanceof Error ? error.message : t('storeOrders.createStoreFailed'),
      )
    } finally {
      setCreateSaving(false)
    }
  }

  return (
    <Modal
      title={title}
      open={open}
      width={860}
      footer={null}
      destroyOnHidden
      onCancel={handleClose}
    >
      <Space direction="vertical" size={12} style={{ width: '100%' }}>
        <Space wrap style={{ width: '100%', justifyContent: 'space-between' }}>
          <Space wrap>
            <Input
              value={keyword}
              allowClear
              placeholder={t('storeOrders.searchStorePlaceholder')}
              prefix={<SearchOutlined />}
              style={{ width: 260 }}
              onChange={(event) => setKeyword(event.target.value)}
            />
            <Radio.Group
              value={cashRegisterFilter}
              onChange={(event) => setCashRegisterFilter(event.target.value)}
            >
              <Radio.Button value="all">{t('storeOrders.storeCashRegisterAll')}</Radio.Button>
              <Radio.Button value="enabled">{t('storeOrders.storeCashRegisterEnabled')}</Radio.Button>
              <Radio.Button value="disabled">{t('storeOrders.storeCashRegisterDisabled')}</Radio.Button>
            </Radio.Group>
          </Space>
          <Button icon={<PlusOutlined />} onClick={handleOpenCreate}>
            {t('storeOrders.createStore')}
          </Button>
        </Space>

        {createOpen ? (
          <Form
            form={createForm}
            layout="vertical"
            initialValues={{ isActive: false }}
            style={{ padding: 12, border: '1px solid #f0f0f0', borderRadius: 6 }}
          >
            <Space direction="vertical" size={0} style={{ width: '100%' }}>
              <Space wrap style={{ width: '100%' }}>
                <Form.Item
                  label={t('system.stores.storeName')}
                  name="storeName"
                  rules={[
                    { required: true, message: t('system.stores.storeNameRequired') },
                    { max: 100, message: t('system.stores.storeNameMaxLength') },
                  ]}
                  style={{ width: 250 }}
                >
                  <Input />
                </Form.Item>
                <Form.Item
                  label={t('system.stores.storeCode')}
                  name="storeCode"
                  rules={[
                    { required: true, message: t('system.stores.storeCodeRequired') },
                    { max: 20, message: t('system.stores.storeCodeMaxLength') },
                  ]}
                  style={{ width: 250 }}
                >
                  <Input
                    addonAfter={(
                      <Button
                        type="link"
                        size="small"
                        icon={<ReloadOutlined />}
                        loading={storeCodeLoading}
                        onClick={() => void loadNextStoreCode()}
                      >
                        {t('storeOrders.regenerateStoreCode')}
                      </Button>
                    )}
                  />
                </Form.Item>
                <Form.Item
                  label={t('system.stores.brandName')}
                  name="brandName"
                  rules={[{ max: 100, message: t('system.stores.brandNameMaxLength') }]}
                  style={{ width: 180 }}
                >
                  <Input />
                </Form.Item>
                <Form.Item
                  label={t('system.stores.cashRegisterEnabled')}
                  name="isActive"
                  valuePropName="checked"
                  style={{ width: 160 }}
                >
                  <Switch checkedChildren={t('common.active')} unCheckedChildren={t('common.inactive')} />
                </Form.Item>
              </Space>
              <Space wrap style={{ width: '100%' }}>
                <Form.Item
                  label={t('system.stores.contactPhone')}
                  name="contactPhone"
                  rules={[{ max: 20, message: t('system.stores.contactPhoneMaxLength') }]}
                  style={{ width: 200 }}
                >
                  <Input />
                </Form.Item>
                <Form.Item
                  label={t('system.stores.contactEmail')}
                  name="contactEmail"
                  rules={[{ type: 'email', message: t('system.users.emailInvalid') }]}
                  style={{ width: 260 }}
                >
                  <Input />
                </Form.Item>
                <Form.Item
                  label={t('system.stores.address')}
                  name="address"
                  rules={[{ max: 200, message: t('system.stores.addressMaxLength') }]}
                  style={{ width: 320 }}
                >
                  <Input />
                </Form.Item>
              </Space>
              <Form.Item
                label={t('column.description')}
                name="description"
                rules={[{ max: 500, message: t('system.stores.descriptionMaxLength') }]}
              >
                <Input.TextArea rows={2} />
              </Form.Item>
              <Space>
                <Button
                  type="primary"
                  loading={createSaving}
                  onClick={() => void handleCreateStore()}
                >
                  {t('common.confirm')}
                </Button>
                <Button
                  disabled={createSaving}
                  onClick={() => {
                    setCreateOpen(false)
                    resetCreateForm()
                  }}
                >
                  {t('common.cancel')}
                </Button>
              </Space>
            </Space>
          </Form>
        ) : null}

        <MeasuredTable metricId="warehouse.store-orders.table-1"
          rowKey="storeGUID"
          loading={fetching || loading || createSaving}
          size="small"
          pagination={false}
          dataSource={stores}
          scroll={{ y: 360 }}
          columns={[
            {
              title: t('common.index'),
              key: 'rowIndex',
              width: 64,
              render: (_value, _record, index) => index + 1,
            },
            { title: t('column.storeName'), dataIndex: 'storeName' },
            { title: t('column.storeCode'), dataIndex: 'storeCode', width: 140 },
            {
              title: t('system.stores.cashRegisterEnabled'),
              dataIndex: 'isActive',
              width: 150,
              render: (value: boolean) => (
                <Tag color={value ? 'success' : 'default'}>
                  {value ? t('common.active') : t('common.inactive')}
                </Tag>
              ),
            },
            {
              title: t('common.address'),
              dataIndex: 'address',
              render: (value: string | undefined) => value || '--',
            },
          ]}
          onRow={(record) => ({
            onClick: () => onSelect(record),
            style: { cursor: 'pointer' },
          })}
        />
      </Space>
    </Modal>
  )
}

function CopyOrderModal({ open, loading, sourceOrderNo, onCancel, onConfirm }: CopyOrderModalProps) {
  const { t } = useTranslation()
  const { message } = AntdApp.useApp()
  const [stores, setStores] = useState<StoreDto[]>([])
  const [fetching, setFetching] = useState(false)
  const [keyword, setKeyword] = useState('')
  const [selectedStore, setSelectedStore] = useState<StoreDto | null>(null)
  const [copyOrderQuantity, setCopyOrderQuantity] = useState(true)
  const [copyAllocQuantity, setCopyAllocQuantity] = useState(false)

  useEffect(() => {
    if (!open) {
      return
    }

    let cancelled = false

    const loadStores = async () => {
      setFetching(true)
      try {
        const result = await getStores({
          page: 1,
          pageSize: 200,
          isActive: true,
          search: keyword || undefined,
          sortField: 'storeName',
          sortOrder: 'ascend',
        })

        if (!cancelled) {
          setStores(result.items)
        }
      } catch (error) {
        console.error(error)
        if (!cancelled) {
          message.error(t('storeOrders.loadStoresFailed'))
        }
      } finally {
        if (!cancelled) {
          setFetching(false)
        }
      }
    }

    void loadStores()

    return () => {
      cancelled = true
    }
  }, [keyword, message, open, t])

  const handleClose = () => {
    setKeyword('')
    setSelectedStore(null)
    setCopyOrderQuantity(true)
    setCopyAllocQuantity(false)
    onCancel()
  }

  return (
    <Modal
      title={t('storeOrders.copyOrderTitle')}
      open={open}
      width={860}
      destroyOnHidden
      confirmLoading={loading}
      okText={t('storeOrders.confirmCopy')}
      cancelText={t('common.cancel')}
      okButtonProps={{ disabled: !selectedStore }}
      onCancel={handleClose}
      onOk={() => {
        if (!selectedStore) {
          message.warning(t('storeOrders.selectTargetStore'))
          return
        }

        onConfirm({
          targetStoreCode: selectedStore.storeCode,
          copyOrderQuantity,
          copyAllocQuantity,
        })
      }}
    >
      <Space direction="vertical" size={12} style={{ width: '100%' }}>
        {sourceOrderNo ? (
          <Typography.Text strong>
            {t('warehouseUi.storeOrders.copySource', { orderNo: sourceOrderNo })}
          </Typography.Text>
        ) : null}
        <Space>
          <Button
            type={copyOrderQuantity ? 'primary' : 'default'}
            onClick={() => setCopyOrderQuantity((current) => !current)}
          >
            {t('storeOrders.copyOrderQty')}
          </Button>
          <Button
            type={copyAllocQuantity ? 'primary' : 'default'}
            onClick={() => setCopyAllocQuantity((current) => !current)}
          >
            {t('storeOrders.copyShipQty')}
          </Button>
        </Space>
        <Input
          value={keyword}
          allowClear
          placeholder={t('storeOrders.searchStorePlaceholder')}
          prefix={<SearchOutlined />}
          onChange={(event) => setKeyword(event.target.value)}
        />
        <Typography.Text type="secondary">
          {t('storeOrders.currentSelection')}
          {selectedStore
            ? `${selectedStore.storeName} (${selectedStore.storeCode})`
            : t('storeOrders.noneSelected')}
        </Typography.Text>
        <MeasuredTable metricId="warehouse.store-orders.table-2"
          rowKey="storeGUID"
          loading={fetching || loading}
          size="small"
          pagination={false}
          dataSource={stores}
          scroll={{ y: 320 }}
          rowClassName={(record) =>
            record.storeGUID === selectedStore?.storeGUID ? 'ant-table-row-selected' : ''
          }
          columns={[
            { title: t('column.storeName'), dataIndex: 'storeName' },
            { title: t('column.storeCode'), dataIndex: 'storeCode', width: 140 },
            {
              title: t('common.address'),
              dataIndex: 'address',
              render: (value: string | undefined) => value || '--',
            },
          ]}
          onRow={(record) => ({
            onClick: () => setSelectedStore(record),
            style: { cursor: 'pointer' },
          })}
        />
      </Space>
    </Modal>
  )
}

export default function StoreOrdersPage() {
  const { t, i18n } = useTranslation()
  const navigate = useNavigate()
  const { message, modal } = AntdApp.useApp()
  const { access } = useAuthStore()
  const isWarehouseStaffOnly =
    access.isWarehouseStaff &&
    !access.isAdmin &&
    !access.isWarehouseManager &&
    (access.hasRole('WarehouseStaff') || access.hasRole('仓库员工'))
  // 分店订货管理动作跟随 Warehouse.ManageOrders 权限；纯 WarehouseStaff 仅保留只读文档入口。
  const canUseWarehouseManagerActions = access.canManageWarehouseOrders && !isWarehouseStaffOnly
  const canCreateStoreOrder = access.canWriteOrder || canUseWarehouseManagerActions
  const canDeleteStoreOrder = access.canDeleteOrder || canUseWarehouseManagerActions
  // 配货单是只读文档：与订货明细页一致，仓库员工也能打开；发票仍只给仓库订货管理权限。
  const canOpenPickingList = canUseWarehouseManagerActions || access.isWarehouseStaff

  const [loading, setLoading] = useState(false)
  const [creating, setCreating] = useState(false)
  const [copying, setCopying] = useState(false)
  const [data, setData] = useState<StoreOrderListItem[]>([])
  const [branches, setBranches] = useState<StoreOrderBranchOption[]>([])
  const [selectedRowKeys, setSelectedRowKeys] = useState<React.Key[]>([])
  // 搜索框输入值；停顿约 300ms 后才提交为查询关键字 keyword。
  const [keywordInput, setKeywordInput] = useState('')
  const [keyword, setKeyword] = useState('')
  const [dateRange, setDateRange] = useState<RangeValue>(null)
  const [selectedStoreCodes, setSelectedStoreCodes] = useState<string[]>([])
  const [statusTab, setStatusTab] = useState<StoreOrderStatusTabKey>(DEFAULT_STORE_ORDER_STATUS_TAB)
  const [statusCounts, setStatusCounts] = useState<StoreOrderStatusCounts | null>(null)
  const [columnFilters, setColumnFilters] = useState<StoreOrderListColumnFilters>({})
  const [moreFiltersOpen, setMoreFiltersOpen] = useState(false)
  const [moreFiltersDraft, setMoreFiltersDraft] = useState<StoreOrderListColumnFilters>({})
  const [page, setPage] = useState(1)
  const [pageSize, setPageSize] = useState(20)
  const [total, setTotal] = useState(0)
  const [sortField, setSortField] = useState('orderDate')
  const [sortOrder, setSortOrder] = useState<'ascend' | 'descend'>('descend')
  const [storePickerOpen, setStorePickerOpen] = useState(false)
  // 复制弹窗的源订单：来自行内「⋯ → 复制为新订单」，或勾选恰好 1 单后的勾选条按钮。
  const [copySourceOrder, setCopySourceOrder] = useState<Pick<StoreOrderListItem, 'orderGUID' | 'orderNo'> | null>(null)
  // 行内 ⋯ 菜单点「删除」后，在 ⋯ 按钮处弹出原有的气泡确认。
  const [deleteConfirmOrderGuid, setDeleteConfirmOrderGuid] = useState<string | null>(null)
  const [batchAssignOpen, setBatchAssignOpen] = useState(false)
  // 当前页各订单的拣货分配（负责人与品种数），“拣货分配”列与批量分配的覆盖提示用。
  const [assignmentSummaries, setAssignmentSummaries] = useState<Record<string, Assignee[]>>({})
  const [assignmentSummaryNonce, setAssignmentSummaryNonce] = useState(0)
  const [shippingOrder, setShippingOrder] = useState<StoreOrderListItem | null>(null)
  const [shippingDate, setShippingDate] = useState<Dayjs>(() => dayjs())
  const [shippingLoading, setShippingLoading] = useState(false)
  const [unmatchedStoreOpen, setUnmatchedStoreOpen] = useState(false)
  const [unmatchedStoreLoading, setUnmatchedStoreLoading] = useState(false)
  const [unmatchedStoreSaving, setUnmatchedStoreSaving] = useState(false)
  const [unmatchedStoreGroups, setUnmatchedStoreGroups] = useState<UnmatchedStoreOrderGroup[]>([])
  const [unmatchedStoreTargets, setUnmatchedStoreTargets] = useState<Record<string, string>>({})
  const [unmatchedTargetStores, setUnmatchedTargetStores] = useState<StoreDto[]>([])
  const [columnOrder, setColumnOrder] = useState<StoreOrderListTableColumnKey[]>([])
  const [columnWidths, setColumnWidths] = useState<StoreOrderListColumnWidthMap>({})
  const stopColumnResizeRef = useRef<(() => void) | null>(null)
  // 避免卸载后继续 setState，防止轮询尾声触发无效更新。
  const isMountedRef = useRef(true)
  const loadDataRef = useRef<((
    overrides?: Partial<StoreOrderListQuery & { pageNumber: number; pageSize: number }>,
  ) => Promise<void>) | null>(null)
  const listRequestGuardRef = useRef(createLatestRequestGuard())
  // 状态页签计数单独一条守卫：筛选连续变化时只采纳最新一轮计数。
  const statusCountRequestGuardRef = useRef(createLatestRequestGuard())
  // 上一轮计数对应的筛选签名；置为 null 表示下次加载必须重新计数（改状态、发货、删除等操作之后）。
  const statusCountSignatureRef = useRef<string | null>(null)

  useEffect(() => () => {
    stopColumnResizeRef.current?.()
  }, [])

  const branchMap = useMemo(
    () => Object.fromEntries(branches.map((item) => [item.code, item.name])) as Record<string, string>,
    [branches],
  )

  const storeFilterOptions = useMemo(
    () =>
      [...branches]
        .sort((left, right) => {
          const nameCompare = (left.name || '').localeCompare(right.name || '', 'zh-Hans-CN', { numeric: true, sensitivity: 'base' })
          return nameCompare || left.code.localeCompare(right.code, 'zh-Hans-CN', { numeric: true, sensitivity: 'base' })
        })
        .map((item) => ({
          value: item.code,
          label: `${item.code} - ${item.name}`,
        })),
    [branches],
  )

  const unmatchedTargetStoreOptions = useMemo(
    () =>
      [...unmatchedTargetStores]
        .sort((left, right) => {
          const nameCompare = (left.storeName || '').localeCompare(right.storeName || '', 'zh-Hans-CN', { numeric: true, sensitivity: 'base' })
          return nameCompare || left.storeCode.localeCompare(right.storeCode, 'zh-Hans-CN', { numeric: true, sensitivity: 'base' })
        })
        .map((item) => ({
          value: item.storeCode,
          // 展示品牌和地址用于人工确认 GUID 对应分店，保存时仍只提交目标 StoreCode。
          label: buildUnmatchedTargetStoreLabel(item),
          title: buildUnmatchedTargetStoreLabel(item),
        })),
    [unmatchedTargetStores],
  )

  const statusLabelMap = useMemo(
    () =>
      ({
        [FlowStatus.ShoppingCart]: t('storeOrders.statusShoppingCart'),
        [FlowStatus.Submitted]: t('storeOrders.statusSubmitted'),
        [FlowStatus.Completed]: t('storeOrders.statusCompleted'),
        [FlowStatus.Picking]: t('storeOrders.statusPicking'),
      }) as Record<StoreOrderFlowStatus, string>,
    [t],
  )

  const buildQuery = (
    overrides: Partial<StoreOrderListQuery & { pageNumber: number; pageSize: number }> = {},
  ): StoreOrderListQuery => ({
    keyword: hasStoreOrderListQueryOverride(overrides, 'keyword') ? overrides.keyword : keyword || undefined,
    storeCodes: hasStoreOrderListQueryOverride(overrides, 'storeCodes')
      ? overrides.storeCodes
      : selectedStoreCodes.length ? selectedStoreCodes : undefined,
    startDate: hasStoreOrderListQueryOverride(overrides, 'startDate')
      ? overrides.startDate
      : dateRange?.[0]?.startOf('day').toISOString(),
    endDate: hasStoreOrderListQueryOverride(overrides, 'endDate')
      ? overrides.endDate
      : dateRange?.[1]?.endOf('day').toISOString(),
    // 状态只由页签决定：进行中/已提交/配货中/已完成/全部（全部也只含这三种状态，不含购物车）。
    statusList: hasStoreOrderListQueryOverride(overrides, 'statusList')
      ? overrides.statusList
      : getStoreOrderStatusTabStatusList(statusTab),
    columnFilters: cleanStoreOrderListColumnFilters(
      hasStoreOrderListQueryOverride(overrides, 'columnFilters')
        ? overrides.columnFilters
        : columnFilters,
    ),
    pageNumber: overrides.pageNumber ?? page,
    pageSize: overrides.pageSize ?? pageSize,
    sortBy: overrides.sortBy ?? sortField,
    sortDescending: overrides.sortDescending ?? sortOrder === 'descend',
  })

  const openDetail = (record: Pick<StoreOrderListItem, 'orderGUID' | 'orderNo'>) => {
    navigate(`/warehouse/store-order/detail/${record.orderGUID}`, {
      state: { orderNo: record.orderNo },
    })
  }

  const loadBranches = async () => {
    try {
      const result = await getUsedStoreOrderBranches()
      setBranches(result)
    } catch (error) {
      console.error(error)
      message.error(t('storeOrders.loadBranchFiltersFailed'))
    }
  }

  // 页签计数：同样的其他筛选下，按三个状态各发一条 pageSize=1 的请求取 total。
  // 只有状态以外的筛选变了才重发；失败时不显示数字，也不打断列表。
  const loadStatusCounts = (query: StoreOrderListQuery) => {
    const signature = buildStoreOrderStatusCountSignature(query)
    if (signature === statusCountSignatureRef.current) {
      return
    }
    statusCountSignatureRef.current = signature

    void runLatestGuardedRequest(
      statusCountRequestGuardRef.current,
      () => Promise.all(
        STORE_ORDER_COUNTED_STATUSES.map(async (status) => {
          const result = await getStoreOrderList(buildStoreOrderStatusCountQuery(query, status))
          return result.total
        }),
      ),
      {
        onSuccess: (totals) => setStatusCounts(toStoreOrderStatusCounts(totals)),
        onError: (countError) => {
          console.error(countError)
          // 清掉签名，下一次加载列表时再重试计数。
          statusCountSignatureRef.current = null
          setStatusCounts(null)
        },
      },
    )
  }

  const loadData = async (
    overrides: Partial<StoreOrderListQuery & { pageNumber: number; pageSize: number }> = {},
  ) => {
    if (!isMountedRef.current) {
      return
    }
    const query = buildQuery(overrides)
    loadStatusCounts(query)

    await runLatestGuardedRequest(listRequestGuardRef.current, () => getStoreOrderList(query), {
      onStart: () => setLoading(true),
      onSuccess: (result) => {
        setData(result.items)
        setTotal(result.total)
        setPage(result.page)
        setPageSize(result.pageSize)
        setSelectedRowKeys([])
      },
      onError: (error) => {
        console.error(error)
        message.error(error instanceof Error ? error.message : t('storeOrders.loadListFailed'))
      },
      onSettled: () => setLoading(false),
    })
  }

  useLayoutEffect(() => {
    loadDataRef.current = loadData
  })

  const refreshCurrentList = useCallback((
    overrides: Partial<StoreOrderListQuery & { pageNumber: number; pageSize: number }> = {},
  ) => {
    if (!isMountedRef.current) {
      return Promise.resolve()
    }
    // 新建、复制、改状态、发货、删除、修复分店都会改变各状态单数：强制这次加载重新计数。
    statusCountSignatureRef.current = null
    return loadDataRef.current?.(overrides) ?? Promise.resolve()
  }, [])

  const loadUnmatchedStoreGroups = async () => {
    setUnmatchedStoreLoading(true)
    try {
      const [groups, stores] = await Promise.all([
        getUnmatchedStoreOrderGroups(),
        loadAllUnmatchedTargetStores(),
      ])
      setUnmatchedStoreGroups(groups)
      setUnmatchedTargetStores(stores)
      setUnmatchedStoreTargets((current) => {
        const availableSources = new Set(groups.map((item) => item.sourceStoreCode))
        return Object.fromEntries(
          Object.entries(current).filter(([sourceStoreCode]) => availableSources.has(sourceStoreCode)),
        )
      })
    } catch (error) {
      console.error(error)
      message.error(error instanceof Error ? error.message : t('storeOrders.loadUnmatchedStoresFailed', '加载未匹配分店失败'))
    } finally {
      setUnmatchedStoreLoading(false)
    }
  }

  const openUnmatchedStoreModal = () => {
    setUnmatchedStoreOpen(true)
    void loadUnmatchedStoreGroups()
  }

  const closeUnmatchedStoreModal = () => {
    setUnmatchedStoreOpen(false)
    setUnmatchedStoreTargets({})
  }

  const handleSaveUnmatchedStoreMappings = async () => {
    const mappings = unmatchedStoreGroups
      .map((group) => ({
        sourceStoreCode: group.sourceStoreCode,
        targetStoreCode: unmatchedStoreTargets[group.sourceStoreCode],
      }))
      .filter((item) => item.targetStoreCode)

    if (!mappings.length) {
      message.warning(t('storeOrders.selectStoreMappingsFirst', '请至少选择一个目标分店'))
      return
    }

    setUnmatchedStoreSaving(true)
    try {
      const result = await batchMapStoreOrderStoreCode({ mappings })
      message.success(
        t('storeOrders.fixStoreGuidSuccess', {
          updated: result.updatedCount ?? 0,
          skipped: result.skippedCount ?? 0,
        }),
      )
      await Promise.all([refreshCurrentList(), loadBranches(), loadUnmatchedStoreGroups()])
    } catch (error) {
      console.error(error)
      message.error(error instanceof Error ? error.message : t('storeOrders.fixStoreGuidFailed', '修复分店 GUID 失败'))
    } finally {
      setUnmatchedStoreSaving(false)
    }
  }

  // 更多筛选（原列头放大镜里的条件）提交后立即按服务端参数重新查询。
  const updateColumnFilters = (nextFilters: StoreOrderListColumnFilters) => {
    setColumnFilters(nextFilters)
    void loadData({ pageNumber: 1, columnFilters: nextFilters })
  }

  const handleStatusTabChange = (nextTab: StoreOrderStatusTabKey) => {
    setStatusTab(nextTab)
    void loadData({ pageNumber: 1, statusList: getStoreOrderStatusTabStatusList(nextTab) })
  }

  const handleStoreCodesChange = (nextStoreCodes: string[]) => {
    setSelectedStoreCodes(nextStoreCodes)
    void loadData({ pageNumber: 1, storeCodes: nextStoreCodes.length ? nextStoreCodes : undefined })
  }

  const handleDateRangeChange = (nextRange: RangeValue) => {
    setDateRange(nextRange)
    void loadData({
      pageNumber: 1,
      startDate: nextRange?.[0]?.startOf('day').toISOString(),
      endDate: nextRange?.[1]?.endOf('day').toISOString(),
    })
  }

  const commitKeyword = (value: string) => {
    const nextKeyword = value.trim()
    if (nextKeyword === keyword) {
      return
    }
    setKeyword(nextKeyword)
    void loadData({ pageNumber: 1, keyword: nextKeyword || undefined })
  }

  useEffect(() => {
    const nextKeyword = keywordInput.trim()
    if (nextKeyword === keyword) {
      return
    }
    // 关键字停顿约 300ms 即时查询；计时结束时走 current loader，拿到计时期间其他筛选的最新值。
    const timer = window.setTimeout(() => {
      if (!isMountedRef.current) {
        return
      }
      setKeyword(nextKeyword)
      void loadDataRef.current?.({ pageNumber: 1, keyword: nextKeyword || undefined })
    }, STORE_ORDER_KEYWORD_DEBOUNCE_MS)
    return () => window.clearTimeout(timer)
  }, [keyword, keywordInput])

  // 「清空全部」清掉已生效筛选条里列出的条件（分店、订单日期、更多筛选）；状态页签、搜索词和排序保持不变。
  const clearAllFilters = () => {
    setSelectedStoreCodes([])
    setDateRange(null)
    setColumnFilters({})
    setMoreFiltersDraft({})
    void loadData({
      pageNumber: 1,
      storeCodes: undefined,
      startDate: undefined,
      endDate: undefined,
      columnFilters: undefined,
    })
  }

  const handleMoreFiltersOpenChange = (open: boolean) => {
    if (open) {
      // 每次打开都从已生效条件开始编辑；未点「应用」的草稿不会生效。
      setMoreFiltersDraft(columnFilters)
    }
    setMoreFiltersOpen(open)
  }

  const patchMoreFiltersDraft = (patch: StoreOrderListColumnFilters) => {
    setMoreFiltersDraft((current) => ({ ...current, ...patch }))
  }

  const applyMoreFilters = (nextFilters: StoreOrderListColumnFilters) => {
    setMoreFiltersOpen(false)
    updateColumnFilters(nextFilters)
  }

  const handleHeaderSort = (field: string) => {
    // 合并列表头里的排序按钮：再次点同一字段切换升降序，换字段时默认降序（日期、金额先看最新/最大）。
    const nextOrder: 'ascend' | 'descend' = sortField === field && sortOrder === 'descend' ? 'ascend' : 'descend'
    setSortField(field)
    setSortOrder(nextOrder)
    // 该函数被缓存在表头列定义里，必须走 current loader，才能带上之后改过的分店、日期等筛选。
    void loadDataRef.current?.({ pageNumber: 1, sortBy: field, sortDescending: nextOrder === 'descend' })
  }

  useEffect(() => {
    void Promise.all([loadData({ pageNumber: 1 }), loadBranches()])
  }, [])

  // 列表是保活页面，只在挂载时加载一次；其它页面（如打印配货单自动开始配货）改了订单流程状态后，
  // 这里把对应行改显示为新状态，避免回到列表仍看到旧的「已提交」。
  useEffect(
    () =>
      subscribeStoreOrderFlowStatusChanged((orderGuid, flowStatus) => {
        setData((current) => applyFlowStatusToOrderList(current, orderGuid, flowStatus))
      }),
    [],
  )

  useLayoutEffect(() => {
    isMountedRef.current = true
    return () => {
      isMountedRef.current = false
      listRequestGuardRef.current.invalidate()
      statusCountRequestGuardRef.current.invalidate()
    }
  }, [])

  // 分店订货的 HQ 全量/增量同步（HQ 订货单 → HBweb）已于 2026-09-29 停用：
  // 2026-09-21 只读核查时 HQ 分店订货单主表最后变更停在 2026-06-21，订货业务已迁到 HBweb。
  // 单张订单改状态：原先点状态标签直接触发（隐式交互），改为行内 ⋯ 菜单的明确入口，确认流程不变。
  const handleStatusToggle = (record: StoreOrderListItem) => {
    if (!canUseWarehouseManagerActions) {
      return
    }

    if (record.flowStatus !== FlowStatus.Submitted && record.flowStatus !== FlowStatus.Completed) {
      return
    }

    const nextStatus =
      record.flowStatus === FlowStatus.Submitted ? FlowStatus.Completed : FlowStatus.Submitted
    const actionLabel =
      nextStatus === FlowStatus.Completed
        ? t('storeOrders.markCompleted')
        : t('storeOrders.markSubmitted')

    modal.confirm({
      title: t('storeOrders.updateStatusTitle'),
      content: t('storeOrders.updateStatusConfirm', {
        orderNo: record.orderNo,
        action: actionLabel,
      }),
      okText: t('common.confirm'),
      cancelText: t('common.cancel'),
      onOk: async () => {
        try {
          await updateStoreOrderStatus({
            orderGUID: record.orderGUID,
            newStatus: nextStatus,
          })
          message.success(t('storeOrders.updateStatusSuccess'))
          void refreshCurrentList()
        } catch (error) {
          console.error(error)
          message.error(error instanceof Error ? error.message : t('storeOrders.updateStatusFailed'))
        }
      },
    })
  }

  const handleBatchStatusChange = (newStatus: StoreOrderFlowStatus) => {
    if (!selectedRowKeys.length) {
      message.warning(t('storeOrders.selectOrdersFirst'))
      return
    }

    modal.confirm({
      title: t('storeOrders.batchUpdateStatusTitle'),
      content: t('storeOrders.batchUpdateStatusConfirm', {
        count: selectedRowKeys.length,
        status: statusLabelMap[newStatus],
      }),
      okText: t('common.confirm'),
      cancelText: t('common.cancel'),
      onOk: async () => {
        try {
          await batchUpdateStoreOrderStatus({
            orderGUIDs: selectedRowKeys.map(String),
            newStatus,
          })
          message.success(t('storeOrders.batchUpdateStatusSuccess'))
          void refreshCurrentList()
        } catch (error) {
          console.error(error)
          message.error(
            error instanceof Error ? error.message : t('storeOrders.batchUpdateStatusFailed'),
          )
        }
      },
    })
  }

  const handleCopyOrderNo = async (orderNo: string) => {
    await copyTextToClipboard(orderNo, {
      successMessage: t('storeOrders.copyOrderNoSuccess', { orderNo }),
      failureMessage: t('storeOrders.copyOrderNoFailed'),
    })
  }

  const openShippingModal = (record: StoreOrderListItem) => {
    setShippingOrder(record)
    setShippingDate(record.outboundDate ? dayjs(record.outboundDate) : dayjs())
  }

  const closeShippingModal = () => {
    setShippingOrder(null)
    setShippingDate(dayjs())
  }

  const handleConfirmShipping = async () => {
    if (!shippingOrder) {
      return
    }

    setShippingLoading(true)
    try {
      await updateStoreOrderOutboundDate({
        orderGUID: shippingOrder.orderGUID,
        outboundDate: shippingDate.format('YYYY-MM-DD'),
        completeOrder: true,
      })
      message.success(t('storeOrders.shipOrderSuccess'))
      closeShippingModal()
      void refreshCurrentList()
    } catch (error) {
      console.error(error)
      message.error(error instanceof Error ? error.message : t('storeOrders.shipOrderFailed'))
    } finally {
      setShippingLoading(false)
    }
  }

  const columnDragSensors = useSensors(
    useSensor(PointerSensor, {
      activationConstraint: {
        distance: 4,
      },
    }),
  )

  useEffect(() => {
    if (!canUseWarehouseManagerActions || data.length === 0) {
      setAssignmentSummaries({})
      return
    }
    const controller = new AbortController()
    listAssignmentSummaries(
      data.map((order) => order.orderGUID),
      controller.signal,
    )
      .then((summaries) => {
        if (!controller.signal.aborted) setAssignmentSummaries(summaries)
      })
      .catch(() => {
        // 分配列只是辅助信息：加载失败时留空，不打断订单列表。
        if (!controller.signal.aborted) setAssignmentSummaries({})
      })
    return () => controller.abort()
  }, [assignmentSummaryNonce, canUseWarehouseManagerActions, data])

  const selectedOrders = useMemo(
    () => data.filter((order) => selectedRowKeys.includes(order.orderGUID)),
    [data, selectedRowKeys],
  )
  const selectedTotals = useMemo(() => summarizeStoreOrders(selectedOrders), [selectedOrders])
  const pageTotals = useMemo(() => summarizeStoreOrders(data), [data])
  const canCopySelection = canCopySelectedStoreOrders(selectedOrders.length)

  const describeUpdatedLine = (record: StoreOrderListItem) => {
    const updatedAt = describeStoreOrderUpdatedAt(record.updatedAt)
    let updatedAgoText = ''
    switch (updatedAt?.kind) {
      case 'today':
        updatedAgoText = t('warehouseUi.storeOrders.updatedToday', { time: updatedAt.time })
        break
      case 'yesterday':
        updatedAgoText = t('warehouseUi.storeOrders.updatedYesterday', { time: updatedAt.time })
        break
      case 'date':
        updatedAgoText = t('warehouseUi.storeOrders.updatedOn', { date: updatedAt.label })
        break
      default:
        break
    }
    return [updatedAgoText, record.updatedBy?.trim()].filter(Boolean).join(' · ') || '--'
  }

  const renderOutboundLine = (outboundDate?: string) => {
    const outbound = getStoreOrderOutboundState(outboundDate)
    if (outbound.kind === 'unset') {
      return <span className="wh-orders-cell-sub wh-orders-outbound-unset">{t('warehouseUi.storeOrders.outboundUnset')}</span>
    }
    if (outbound.kind === 'today') {
      return <span className="wh-orders-cell-sub wh-orders-outbound-today">{t('warehouseUi.storeOrders.outboundToday')}</span>
    }
    return (
      <span className="wh-orders-cell-sub" title={formatDate(outboundDate, i18n.language)}>
        {t('warehouseUi.storeOrders.outboundOn', { date: outbound.label })}
      </span>
    )
  }

  // 合并列（订单/出库、订货/发货金额）的表头里各放两个排序按钮，保留拆列前每个字段都能排序的能力。
  const renderHeaderSortToggle = (field: string, label: string, ariaLabel: string) => {
    const active = sortField === field
    return (
      <button
        type="button"
        className={`wh-orders-sort-toggle${active ? ' is-active' : ''}`}
        aria-label={ariaLabel}
        aria-pressed={active}
        onClick={(event) => {
          event.stopPropagation()
          handleHeaderSort(field)
        }}
      >
        {label}
        {active ? (sortOrder === 'descend' ? <ArrowDownOutlined /> : <ArrowUpOutlined />) : null}
      </button>
    )
  }

  // 单字段列沿用 antd 表头排序，但改为受控：排序状态只有一份，合并列的排序按钮切换时这里的箭头同步熄灭。
  const getControlledSortOrder = (field: string): SortOrder => (sortField === field ? sortOrder : null)

  const buildRowMenuItems = (record: StoreOrderListItem): MenuProps['items'] => {
    const documentItems: NonNullable<MenuProps['items']> = [
      { key: 'detail', icon: <FileSearchOutlined />, label: t('warehouseUi.storeOrders.viewDetail') },
    ]
    if (canOpenPickingList) {
      documentItems.push({ key: 'picking', icon: <PrinterOutlined />, label: t('storeOrders.pickingList') })
    }
    if (canUseWarehouseManagerActions) {
      documentItems.push({ key: 'invoice', icon: <FileTextOutlined />, label: t('storeOrders.invoice') })
    }

    const manageItems: NonNullable<MenuProps['items']> = []
    if (canUseWarehouseManagerActions) {
      manageItems.push({ key: 'copy', icon: <CopyOutlined />, label: t('warehouseUi.storeOrders.copyAsNew') })
      // 单张改状态与改版前点状态标签的口径一致：只在已提交与已完成之间切换。
      if (record.flowStatus === FlowStatus.Submitted) {
        manageItems.push({ key: 'markCompleted', icon: <CheckCircleOutlined />, label: t('warehouseUi.storeOrders.markCompleted') })
      }
      if (record.flowStatus === FlowStatus.Completed) {
        manageItems.push({ key: 'markSubmitted', icon: <RollbackOutlined />, label: t('warehouseUi.storeOrders.markSubmitted') })
      }
    }
    if (canDeleteStoreOrder) {
      manageItems.push({ key: 'delete', icon: <DeleteOutlined />, danger: true, label: t('common.delete') })
    }

    return manageItems.length ? [...documentItems, { type: 'divider' }, ...manageItems] : documentItems
  }

  const handleRowMenuAction = (record: StoreOrderListItem, action: StoreOrderRowMenuAction) => {
    switch (action) {
      case 'detail':
        openDetail(record)
        break
      case 'picking':
        navigate(`/warehouse/store-order/picking/${record.orderGUID}`)
        break
      case 'invoice':
        navigate(`/warehouse/store-order/invoice/${record.orderGUID}`)
        break
      case 'copy':
        setCopySourceOrder(record)
        break
      case 'markCompleted':
      case 'markSubmitted':
        handleStatusToggle(record)
        break
      case 'delete':
        setDeleteConfirmOrderGuid(record.orderGUID)
        break
      default:
        break
    }
  }

  const renderRowMoreDropdown = (record: StoreOrderListItem) => (
    <Dropdown
      trigger={['click']}
      placement="bottomRight"
      menu={{
        items: buildRowMenuItems(record),
        onClick: ({ key, domEvent }) => {
          // 菜单渲染在 Portal 中，点击仍会沿 React 树冒泡到外层气泡确认的触发区，这里拦截。
          domEvent.stopPropagation()
          handleRowMenuAction(record, key as StoreOrderRowMenuAction)
        },
      }}
    >
      <Button
        type="text"
        size="small"
        className="wh-orders-row-more"
        icon={<MoreOutlined />}
        aria-label={`${t('warehouseUi.storeOrders.moreActions')} ${record.orderNo}`}
      />
    </Dropdown>
  )

  const baseColumns = useMemo<ColumnsType<StoreOrderListItem>>(
    () => [
      {
        key: 'orderNo',
        title: t('column.orderNo'),
        dataIndex: 'orderNo',
        width: STORE_ORDER_LIST_DEFAULT_COLUMN_WIDTHS.orderNo,
        sorter: true,
        sortOrder: getControlledSortOrder('orderNo'),
        sortDirections: STORE_ORDER_TEXT_SORT_DIRECTIONS,
        fixed: 'left',
        render: (value: string, record) => (
          <div className="wh-orders-cell-stack">
            <Space size={4} wrap={false} className="store-order-list-order-cell">
              <Button type="link" className="store-order-list-order-no" onClick={() => openDetail(record)}>
                {value}
              </Button>
              <Button
                type="text"
                size="small"
                icon={<CopyOutlined />}
                className="store-order-copy-button"
                aria-label={`${t('common.copy')} ${value}`}
                onClick={() => void handleCopyOrderNo(value)}
              />
            </Space>
            <span
              className="wh-orders-cell-sub wh-orders-ellipsis"
              title={record.updatedAt ? formatDateTime(record.updatedAt, i18n.language) : undefined}
            >
              {describeUpdatedLine(record)}
            </span>
          </div>
        ),
      },
      {
        key: 'storeCode',
        title: t('column.store'),
        dataIndex: 'storeCode',
        width: STORE_ORDER_LIST_DEFAULT_COLUMN_WIDTHS.storeCode,
        sorter: true,
        sortOrder: getControlledSortOrder('storeCode'),
        sortDirections: STORE_ORDER_TEXT_SORT_DIRECTIONS,
        render: (value: string | undefined, record) => {
          const name = record.storeName || (value ? branchMap[value] : undefined)
          // 分店不再按编码哈希上色：名称一行、编码一行，颜色只留给状态。
          return (
            <div className="wh-orders-cell-stack" title={name && value ? `${value} - ${name}` : value || name}>
              <span className="wh-orders-store-name">{name || value || '--'}</span>
              {name && value ? <span className="wh-orders-cell-sub wh-orders-ellipsis wh-orders-num">{value}</span> : null}
            </div>
          )
        },
      },
      {
        key: 'orderOutboundDate',
        title: (
          <span className="wh-orders-dual-sort">
            {renderHeaderSortToggle(
              'orderDate',
              t('warehouseUi.storeOrders.sortOrderShort'),
              t('warehouseUi.storeOrders.sortByOrderDate'),
            )}
            <span className="wh-orders-dual-sort-sep" aria-hidden="true">/</span>
            {renderHeaderSortToggle(
              'outboundDate',
              t('warehouseUi.storeOrders.sortOutboundShort'),
              t('warehouseUi.storeOrders.sortByOutboundDate'),
            )}
          </span>
        ),
        dataIndex: 'orderDate',
        width: STORE_ORDER_LIST_DEFAULT_COLUMN_WIDTHS.orderOutboundDate,
        render: (_: unknown, record) => (
          <div className="wh-orders-cell-stack wh-orders-num">
            <span title={formatDate(record.orderDate, i18n.language)}>{formatStoreOrderListDate(record.orderDate) ?? '--'}</span>
            {renderOutboundLine(record.outboundDate)}
          </div>
        ),
      },
      {
        key: 'flowStatus',
        title: t('column.status'),
        dataIndex: 'flowStatus',
        width: STORE_ORDER_LIST_DEFAULT_COLUMN_WIDTHS.flowStatus,
        sorter: true,
        sortOrder: getControlledSortOrder('flowStatus'),
        sortDirections: STORE_ORDER_TEXT_SORT_DIRECTIONS,
        render: (value: StoreOrderFlowStatus) => (
          <StatusPill tone={getStoreOrderStatusPillTone(value)}>
            {statusLabelMap[value] || `${t('column.status')} ${value}`}
          </StatusPill>
        ),
      },
      ...(canUseWarehouseManagerActions
        ? [
            {
              key: 'pickingAssignment',
              title: t('storeOrders.pickingAssignment.listColumn', '拣货分配'),
              width: STORE_ORDER_LIST_DEFAULT_COLUMN_WIDTHS.pickingAssignment,
              render: (_: unknown, record: StoreOrderListItem) => {
                const assignees = assignmentSummaries[record.orderGUID]
                return assignees?.length ? (
                  <Space size={4} wrap className="wh-orders-picking-cell">
                    {assignees.map((assignee) => (
                      <AssigneeChip key={assignee.segmentNo} segmentNo={assignee.segmentNo} name={assignee.pickerName} lineCount={assignee.lineCount} />
                    ))}
                  </Space>
                ) : (
                  <span className="wh-orders-unassigned">
                    {t('storeOrders.pickingAssignment.unassignedShort', '未分配')}
                  </span>
                )
              },
            },
          ]
        : []),
      {
        key: 'quantityVolume',
        title: t('warehouseUi.storeOrders.colQuantityVolume'),
        dataIndex: 'totalQuantity',
        width: STORE_ORDER_LIST_DEFAULT_COLUMN_WIDTHS.quantityVolume,
        align: 'right',
        sorter: true,
        sortOrder: getControlledSortOrder('totalQuantity'),
        sortDirections: STORE_ORDER_NUMBER_SORT_DIRECTIONS,
        render: (_: unknown, record) => (
          <div className="wh-orders-cell-stack wh-orders-num-stack">
            <span>
              {typeof record.totalQuantity === 'number'
                ? t('warehouseUi.storeOrders.quantityValue', { value: formatStoreOrderInteger(record.totalQuantity) })
                : '--'}
            </span>
            <span className="wh-orders-cell-sub">
              {typeof record.totalOrderVolume === 'number'
                ? t('warehouseUi.storeOrders.volumeValue', { value: formatStoreOrderVolume(record.totalOrderVolume) })
                : '--'}
            </span>
          </div>
        ),
      },
      {
        key: 'orderShipAmount',
        title: (
          <span className="wh-orders-dual-sort">
            {renderHeaderSortToggle(
              'totalOrderAmount',
              t('warehouseUi.storeOrders.sortOrderAmountShort'),
              t('warehouseUi.storeOrders.sortByOrderAmount'),
            )}
            <span className="wh-orders-dual-sort-sep" aria-hidden="true">/</span>
            {renderHeaderSortToggle(
              'importTotalAmount',
              t('warehouseUi.storeOrders.sortShipAmountShort'),
              t('warehouseUi.storeOrders.sortByShipAmount'),
            )}
          </span>
        ),
        dataIndex: 'totalOrderAmount',
        width: STORE_ORDER_LIST_DEFAULT_COLUMN_WIDTHS.orderShipAmount,
        align: 'right',
        // 第一行订货金额 = Σ订货数×进口价；第二行发货金额 = Σ发货数×进口价（即原「发货金额」列 importTotalAmount）。
        render: (_: unknown, record) => (
          <div className="wh-orders-cell-stack wh-orders-num-stack">
            <span className="wh-orders-amount">{formatStoreOrderMoney(record.totalOrderAmount)}</span>
            <span className="wh-orders-cell-sub">
              {record.importTotalAmount > 0
                ? t('warehouseUi.storeOrders.shipAmountValue', { value: formatStoreOrderMoney(record.importTotalAmount) })
                : '—'}
            </span>
          </div>
        ),
      },
      {
        key: 'remarks',
        title: t('common.remarks'),
        dataIndex: 'remarks',
        width: STORE_ORDER_LIST_DEFAULT_COLUMN_WIDTHS.remarks,
        render: (value: string | undefined) => renderStoreOrderTwoLineText(value),
      },
      {
        title: t('column.action'),
        key: 'action',
        fixed: 'right',
        align: 'right',
        width: STORE_ORDER_LIST_DEFAULT_COLUMN_WIDTHS.action,
        render: (_, record) => (
          <div className="wh-orders-row-actions">
            {canUseWarehouseManagerActions && (record.flowStatus === FlowStatus.Submitted || record.flowStatus === FlowStatus.Picking) ? (
              <Button size="small" autoInsertSpace={false} onClick={() => openShippingModal(record)}>
                {t('storeOrders.shipOrder')}
              </Button>
            ) : null}
            {canDeleteStoreOrder ? (
              <Popconfirm
                open={deleteConfirmOrderGuid === record.orderGUID}
                onOpenChange={(open) => {
                  // 气泡只由 ⋯ 菜单里的「删除」打开；这里只处理关闭（点外部、取消、确认完成）。
                  if (!open) {
                    setDeleteConfirmOrderGuid(null)
                  }
                }}
                placement="bottomRight"
                title={t('storeOrders.confirmDeleteOrder', { orderNo: record.orderNo })}
                okText={t('common.delete')}
                okButtonProps={{ danger: true }}
                cancelText={t('common.cancel')}
                onConfirm={async () => {
                  try {
                    await deleteStoreOrder(record.orderGUID)
                    message.success(t('common.deleteSuccess'))
                    void refreshCurrentList({ pageNumber: 1 })
                  } catch (error) {
                    console.error(error)
                    message.error(
                      error instanceof Error ? error.message : t('storeOrders.deleteFailed'),
                    )
                  } finally {
                    setDeleteConfirmOrderGuid(null)
                  }
                }}
              >
                <span className="wh-orders-row-more-anchor">{renderRowMoreDropdown(record)}</span>
              </Popconfirm>
            ) : renderRowMoreDropdown(record)}
          </div>
        ),
      },
    ],
    [
      assignmentSummaries,
      branchMap,
      canDeleteStoreOrder,
      canOpenPickingList,
      canUseWarehouseManagerActions,
      deleteConfirmOrderGuid,
      i18n.language,
      refreshCurrentList,
      sortField,
      sortOrder,
      statusLabelMap,
      t,
    ],
  )

  const draggableColumnKeys = baseColumns.map((column) => String(column.key) as StoreOrderListTableColumnKey)
  const isColumnOrderCustomized = isStoreOrderListColumnOrderCustomized(columnOrder, draggableColumnKeys)
  const isColumnWidthCustomized = Object.keys(columnWidths).length > 0
  const isColumnSettingsCustomized = isColumnOrderCustomized || isColumnWidthCustomized

  useEffect(() => {
    setColumnOrder((current) => {
      let savedOrder: unknown[] | null = null
      if (!current.length && typeof window !== 'undefined') {
        try {
          const raw = localStorage.getItem(STORE_ORDER_LIST_COLUMN_ORDER_STORAGE_KEY)
          savedOrder = raw ? JSON.parse(raw) : null
        } catch {
          savedOrder = null
        }
      }

      // 列顺序只管理业务列；选择列继续由 rowSelection 管理，新增/删除列在这里自动兼容。
      const nextOrder = mergeStoreOrderListColumnOrder(current.length ? current : savedOrder, draggableColumnKeys)
      if (current.length === nextOrder.length && current.every((key, index) => key === nextOrder[index])) {
        return current
      }
      return nextOrder
    })
  }, [draggableColumnKeys.join('|')])

  useEffect(() => {
    setColumnWidths((current) => {
      let savedWidths: unknown = null
      let hasSavedWidths = false
      if (!Object.keys(current).length && typeof window !== 'undefined') {
        try {
          const raw = localStorage.getItem(STORE_ORDER_LIST_COLUMN_WIDTH_STORAGE_KEY)
          hasSavedWidths = raw !== null
          savedWidths = raw ? JSON.parse(raw) : null
        } catch {
          savedWidths = null
          hasSavedWidths = true
        }
      }

      // 只恢复当前可见业务列的宽度，自动清理已隐藏或废弃列留下的本地设置。
      const nextWidths = normalizeStoreOrderListColumnWidths(
        Object.keys(current).length ? current : savedWidths,
        draggableColumnKeys,
      )
      if (hasSavedWidths) {
        persistStoreOrderListColumnWidths(nextWidths)
      }
      if (areStoreOrderListColumnWidthsEqual(current, nextWidths)) {
        return current
      }
      return nextWidths
    })
  }, [draggableColumnKeys.join('|')])

  const handleColumnDragEnd = ({ active, over }: DragEndEvent) => {
    if (!over || active.id === over.id) return
    setColumnOrder((current) => {
      const nextOrder = moveStoreOrderListColumnOrder(current, active.id, over.id)
      try {
        localStorage.setItem(STORE_ORDER_LIST_COLUMN_ORDER_STORAGE_KEY, JSON.stringify(nextOrder))
      } catch {
        // localStorage 不可用时不影响当前页面内拖拽排序。
      }
      return nextOrder
    })
  }

  const handleColumnResizeStart = useCallback((
    columnKey: StoreOrderListTableColumnKey,
    startWidth: number,
    resizeFromLeft: boolean,
    event: ReactPointerEvent<HTMLSpanElement>,
  ) => {
    stopColumnResizeRef.current?.()

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
      const nextWidth = clampStoreOrderListColumnWidth(
        startWidth + (resizeFromLeft ? -pointerDelta : pointerDelta),
      )
      if (nextWidth === latestWidth) return
      latestWidth = nextWidth
      didResize = true
      setColumnWidths((current) => {
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
      if (stopColumnResizeRef.current === cleanupResize) {
        stopColumnResizeRef.current = null
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
      setColumnWidths((current) => {
        const nextWidths = { ...current, [columnKey]: latestWidth }
        persistStoreOrderListColumnWidths(nextWidths)
        return nextWidths
      })
    }

    // 使用 document 级监听，指针离开表头后仍可连续调整列宽。
    stopColumnResizeRef.current = cleanupResize
    resizeHandle.addEventListener('lostpointercapture', finishResize, { once: true })
    document.addEventListener('pointermove', handlePointerMove)
    document.addEventListener('pointerup', finishResize)
    document.addEventListener('pointercancel', finishResize)
    window.addEventListener('blur', finishResize, { once: true })
  }, [])

  const resetColumnOrder = () => {
    setColumnOrder(draggableColumnKeys)
    setColumnWidths({})
    try {
      localStorage.removeItem(STORE_ORDER_LIST_COLUMN_ORDER_STORAGE_KEY)
      localStorage.removeItem(STORE_ORDER_LIST_COLUMN_WIDTH_STORAGE_KEY)
    } catch {
      // localStorage 不可用时仍恢复当前页面内的默认列设置。
    }
    message.success(t('containers.messages.columnOrderReset', '列设置已恢复默认'))
  }

  const columns = useMemo(() => {
    const activeOrder = columnOrder.length ? columnOrder : draggableColumnKeys
    const columnMap = new Map(baseColumns.map((column) => [String(column.key), column]))
    // 序号接着服务端分页往下数；没有 data-column-key，表头不可拖拽、不可调宽。
    const indexColumn: ColumnsType<StoreOrderListItem>[number] = {
      key: STORE_ORDER_LIST_INDEX_COLUMN_KEY,
      title: t('column.index'),
      width: STORE_ORDER_LIST_INDEX_COLUMN_WIDTH,
      align: 'center',
      fixed: 'left',
      render: (_: unknown, __: StoreOrderListItem, index: number) => (
        <span className="wh-orders-row-index">{resolveStoreOrderDetailRowNumber(page, pageSize, index)}</span>
      ),
    }
    const orderedColumns = activeOrder
      .map((key) => columnMap.get(key))
      .filter((column): column is ColumnsType<StoreOrderListItem>[number] => Boolean(column))
      .map((column) => {
        const columnKey = String(column.key) as StoreOrderListTableColumnKey
        const width = columnWidths[columnKey] ?? column.width
        return {
          ...column,
          width,
          onHeaderCell: () => ({
            'data-column-key': columnKey,
            'data-column-width': typeof width === 'number' ? width : STORE_ORDER_LIST_MIN_COLUMN_WIDTH,
            'data-column-fixed': column.fixed === 'right' ? 'right' : undefined,
            onColumnResizeStart: handleColumnResizeStart,
          } as DraggableHeaderCellProps),
        }
      }) as ColumnsType<StoreOrderListItem>
    return [indexColumn, ...orderedColumns]
  }, [baseColumns, columnOrder, columnWidths, draggableColumnKeys, handleColumnResizeStart, page, pageSize, t])
  const tableScrollX =
    (canUseWarehouseManagerActions ? STORE_ORDER_LIST_SELECTION_COLUMN_WIDTH : 0)
    + columns.reduce((total, column) => {
      const width = typeof column.width === 'number' ? column.width : Number(column.width)
      return total + (Number.isFinite(width) ? width : 0)
    }, 0)

  const tabCounts = buildStoreOrderStatusTabCounts(statusCounts)
  const statusTabItems: StatusTabItem<StoreOrderStatusTabKey>[] = [
    { key: 'active', label: t('warehouseUi.storeOrders.tabActive'), count: tabCounts.active },
    { key: 'submitted', label: statusLabelMap[FlowStatus.Submitted], count: tabCounts.submitted },
    { key: 'picking', label: statusLabelMap[FlowStatus.Picking], count: tabCounts.picking, tone: 'warning' },
    { key: 'completed', label: statusLabelMap[FlowStatus.Completed], count: tabCounts.completed },
    // 「全部」的总数放在页头副标题里，页签上不再重复。
    { key: 'all', label: t('warehouseUi.storeOrders.tabAll') },
  ]
  const headerSubtitle = typeof tabCounts.active === 'number' && typeof tabCounts.all === 'number'
    ? t('warehouseUi.storeOrders.subtitleCounts', {
        active: formatStoreOrderInteger(tabCounts.active),
        all: formatStoreOrderInteger(tabCounts.all),
      })
    : undefined

  const activeMoreFilterGroups = getActiveStoreOrderMoreFilterGroups(columnFilters)
  const moreFilterGroupLabels: Record<StoreOrderMoreFilterGroup, string> = {
    outboundDate: t('storeOrders.outboundDate'),
    totalQuantity: t('storeOrders.orderQuantity'),
    totalOrderAmount: t('storeOrders.orderAmount'),
    totalOrderVolume: t('storeOrders.orderVolume'),
    importTotalAmount: t('storeOrders.shipAmount'),
    remarks: t('common.remarks'),
    updatedBy: t('column.updater'),
    createdAt: t('column.createTime'),
    updatedAt: t('column.updateTime'),
    orderNo: t('column.orderNo'),
  }
  const describeMoreFilterGroup = (group: StoreOrderMoreFilterGroup) => {
    switch (group) {
      case 'outboundDate':
        return formatStoreOrderDateRange(columnFilters.outboundDateStart, columnFilters.outboundDateEnd)
      case 'createdAt':
        return formatStoreOrderDateRange(columnFilters.createdAtStart, columnFilters.createdAtEnd)
      case 'updatedAt':
        return formatStoreOrderDateRange(columnFilters.updatedAtStart, columnFilters.updatedAtEnd)
      case 'totalQuantity':
        return formatStoreOrderNumberRange(columnFilters.totalQuantityMin, columnFilters.totalQuantityMax)
      case 'totalOrderAmount':
        return formatStoreOrderNumberRange(columnFilters.totalOrderAmountMin, columnFilters.totalOrderAmountMax)
      case 'totalOrderVolume':
        return formatStoreOrderNumberRange(columnFilters.totalOrderVolumeMin, columnFilters.totalOrderVolumeMax)
      case 'importTotalAmount':
        return formatStoreOrderNumberRange(columnFilters.importTotalAmountMin, columnFilters.importTotalAmountMax)
      case 'remarks':
        return columnFilters.remarks?.trim() || null
      case 'updatedBy':
        return columnFilters.updatedBy?.trim() || null
      case 'orderNo':
        return columnFilters.orderNo?.trim() || null
      default:
        return null
    }
  }

  // 已生效筛选条：顶部分店、订单日期与收进「更多筛选」的条件都在这里显示，可逐个移除。
  const activeFilterItems: ActiveFilterItem[] = []
  if (selectedStoreCodes.length) {
    const storeNames = selectedStoreCodes.map((code) => branchMap[code] || code)
    activeFilterItems.push({
      key: 'stores',
      label: t('warehouseUi.storeOrders.storeLabel'),
      value: storeNames.length > 2
        ? t('warehouseUi.storeOrders.storeChipMany', {
            first: storeNames.slice(0, 2).join('、'),
            count: storeNames.length,
            rest: storeNames.length - 2,
          })
        : storeNames.join('、'),
      source: 'toolbar',
      onRemove: () => handleStoreCodesChange([]),
    })
  }
  const orderDateSummary = formatStoreOrderDateRange(
    dateRange?.[0]?.startOf('day').toISOString(),
    dateRange?.[1]?.endOf('day').toISOString(),
  )
  if (orderDateSummary) {
    activeFilterItems.push({
      key: 'orderDate',
      label: t('warehouseUi.storeOrders.orderDateLabel'),
      value: orderDateSummary,
      source: 'toolbar',
      onRemove: () => handleDateRangeChange(null),
    })
  }
  activeMoreFilterGroups.forEach((group) => {
    activeFilterItems.push({
      key: group,
      label: moreFilterGroupLabels[group],
      value: describeMoreFilterGroup(group) ?? '',
      source: 'toolbar',
      onRemove: () => updateColumnFilters(removeStoreOrderMoreFilterGroup(columnFilters, group)),
    })
  })

  const renderMoreDateRange = (
    label: string,
    startKey: StoreOrderListDateStartKey,
    endKey: StoreOrderListDateEndKey,
  ) => (
    <div className="wh-orders-more-field">
      <span className="wh-orders-more-label">{label}</span>
      <DatePicker.RangePicker
        aria-label={label}
        value={[
          moreFiltersDraft[startKey] ? dayjs(moreFiltersDraft[startKey]) : null,
          moreFiltersDraft[endKey] ? dayjs(moreFiltersDraft[endKey]) : null,
        ]}
        onChange={(value) =>
          patchMoreFiltersDraft({
            [startKey]: value?.[0]?.startOf('day').toISOString(),
            [endKey]: value?.[1]?.endOf('day').toISOString(),
          })
        }
      />
    </div>
  )

  const renderMoreNumberRange = (
    label: string,
    minKey: StoreOrderListNumberFilterKey,
    maxKey: StoreOrderListNumberFilterKey,
    precision: number,
  ) => (
    <div className="wh-orders-more-field">
      <span className="wh-orders-more-label">{label}</span>
      <Space.Compact block>
        <InputNumber
          aria-label={`${label} ${t('containers.placeholders.minValue', '最小值')}`}
          value={moreFiltersDraft[minKey] ?? null}
          placeholder={t('containers.placeholders.minValue', '最小值')}
          controls={false}
          precision={precision}
          onChange={(value) => patchMoreFiltersDraft({ [minKey]: value == null ? undefined : Number(value) })}
        />
        <InputNumber
          aria-label={`${label} ${t('containers.placeholders.maxValue', '最大值')}`}
          value={moreFiltersDraft[maxKey] ?? null}
          placeholder={t('containers.placeholders.maxValue', '最大值')}
          controls={false}
          precision={precision}
          onChange={(value) => patchMoreFiltersDraft({ [maxKey]: value == null ? undefined : Number(value) })}
        />
      </Space.Compact>
    </div>
  )

  const renderMoreText = (label: string, key: StoreOrderListTextFilterKey) => (
    <div className="wh-orders-more-field">
      <span className="wh-orders-more-label">{label}</span>
      <Input
        aria-label={label}
        allowClear
        value={moreFiltersDraft[key] ?? ''}
        onChange={(event) => patchMoreFiltersDraft({ [key]: event.target.value || undefined })}
        onPressEnter={() => applyMoreFilters(moreFiltersDraft)}
      />
    </div>
  )

  const headerMenuItems: MenuProps['items'] = [
    ...(canUseWarehouseManagerActions
      ? [{ key: 'fixStoreGuid', icon: <ToolOutlined />, label: t('storeOrders.fixStoreGuid', '修复分店 GUID') }]
      : []),
    {
      key: 'resetColumns',
      icon: <ColumnWidthOutlined />,
      label: t('warehouseUi.storeOrders.resetColumns'),
      // 没拖过列序、没调过列宽时无需恢复。
      disabled: !isColumnSettingsCustomized,
    },
  ]

  const renderSummaryRow = () => {
    if (!data.length) {
      return null
    }
    const offset = canUseWarehouseManagerActions ? 1 : 0
    // 合计标签放在第一个非数字列；列可拖动排序，所以按当前列顺序逐格生成，保证与表头对齐。
    const labelColumnIndex = columns.findIndex(
      (column) =>
        column.key !== STORE_ORDER_LIST_INDEX_COLUMN_KEY && column.key !== 'quantityVolume' && column.key !== 'orderShipAmount',
    )
    return (
      <MeasuredTable.Summary fixed="bottom">
        <MeasuredTable.Summary.Row className="wh-orders-summary-row">
          {offset ? <MeasuredTable.Summary.Cell index={0} /> : null}
          {columns.map((column, columnIndex) => {
            const cellIndex = columnIndex + offset
            if (column.key === 'quantityVolume') {
              return (
                <MeasuredTable.Summary.Cell key={String(column.key)} index={cellIndex} align="right">
                  <div className="wh-orders-cell-stack wh-orders-num-stack">
                    <strong>{t('warehouseUi.storeOrders.quantityValue', { value: formatStoreOrderInteger(pageTotals.totalQuantity) })}</strong>
                    <span className="wh-orders-cell-sub">
                      {pageTotals.hasVolume
                        ? t('warehouseUi.storeOrders.volumeValue', { value: formatStoreOrderVolume(pageTotals.totalVolume) })
                        : '--'}
                    </span>
                  </div>
                </MeasuredTable.Summary.Cell>
              )
            }
            if (column.key === 'orderShipAmount') {
              return (
                <MeasuredTable.Summary.Cell key={String(column.key)} index={cellIndex} align="right">
                  <div className="wh-orders-cell-stack wh-orders-num-stack">
                    <strong>{formatStoreOrderMoney(pageTotals.totalOrderAmount)}</strong>
                    <span className="wh-orders-cell-sub">
                      {pageTotals.totalShipAmount > 0
                        ? t('warehouseUi.storeOrders.shipAmountValue', { value: formatStoreOrderMoney(pageTotals.totalShipAmount) })
                        : '—'}
                    </span>
                  </div>
                </MeasuredTable.Summary.Cell>
              )
            }
            return (
              <MeasuredTable.Summary.Cell key={String(column.key)} index={cellIndex}>
                {columnIndex === labelColumnIndex ? (
                  // 只有当前页数据，文案必须是「本页合计」，不能写成筛选结果合计。
                  <span className="wh-orders-summary-label">
                    {t('warehouseUi.storeOrders.pageTotal', { count: pageTotals.count })}
                  </span>
                ) : null}
              </MeasuredTable.Summary.Cell>
            )
          })}
        </MeasuredTable.Summary.Row>
      </MeasuredTable.Summary>
    )
  }

  return (
    <PageContainer
      compact
      title={t('storeOrders.title')}
      subtitle={headerSubtitle}
      extra={
        <div className="wh-orders-header-actions">
          <Dropdown
            trigger={['click']}
            placement="bottomRight"
            menu={{
              items: headerMenuItems,
              onClick: ({ key }) => {
                if (key === 'fixStoreGuid') {
                  openUnmatchedStoreModal()
                } else if (key === 'resetColumns') {
                  resetColumnOrder()
                }
              },
            }}
          >
            <Button
              icon={unmatchedStoreLoading ? <LoadingOutlined /> : <MoreOutlined />}
              aria-label={t('warehouseUi.storeOrders.moreActions')}
            />
          </Dropdown>
          {canUseWarehouseManagerActions ? (
            <Button
              type="primary"
              icon={<PlusOutlined />}
              disabled={!canCreateStoreOrder}
              onClick={() => setStorePickerOpen(true)}
            >
              {t('storeOrders.newOrder')}
            </Button>
          ) : null}
        </div>
      }
    >
      <section className="wh-orders-panel" aria-label={t('storeOrders.title')}>
        <div className="wh-orders-tabs">
          <StatusTabs
            items={statusTabItems}
            activeKey={statusTab}
            onChange={handleStatusTabChange}
            ariaLabel={t('warehouseUi.storeOrders.tabsLabel')}
          />
        </div>

        <div className="wh-orders-toolbar">
          <Input
            className="wh-orders-search"
            value={keywordInput}
            allowClear
            prefix={<SearchOutlined />}
            aria-label={t('warehouseUi.storeOrders.searchLabel')}
            placeholder={t('warehouseUi.storeOrders.searchPlaceholder')}
            onChange={(event) => setKeywordInput(event.target.value)}
            onPressEnter={() => commitKeyword(keywordInput)}
          />
          <Select
            mode="multiple"
            className="wh-orders-store-select"
            prefix={<span className="wh-orders-field-prefix">{t('warehouseUi.storeOrders.storeLabel')}</span>}
            aria-label={t('warehouseUi.storeOrders.storeLabel')}
            value={selectedStoreCodes}
            allowClear
            showSearch
            maxTagCount="responsive"
            placeholder={t('storeOrders.allStores')}
            optionFilterProp="label"
            filterOption={filterStoreOption}
            options={storeFilterOptions}
            onChange={handleStoreCodesChange}
          />
          <DatePicker.RangePicker
            className={`wh-orders-date-range${orderDateSummary ? ' is-active' : ''}`}
            prefix={<span className="wh-orders-field-prefix">{t('warehouseUi.storeOrders.orderDateLabel')}</span>}
            value={dateRange}
            onChange={(value) => handleDateRangeChange(value)}
          />
          <MoreFiltersButton
            activeCount={activeMoreFilterGroups.length}
            open={moreFiltersOpen}
            onOpenChange={handleMoreFiltersOpenChange}
          >
            <div className="wh-orders-more-grid">
              {renderMoreDateRange(t('storeOrders.outboundDate'), 'outboundDateStart', 'outboundDateEnd')}
              {renderMoreNumberRange(t('storeOrders.orderQuantity'), 'totalQuantityMin', 'totalQuantityMax', 0)}
              {renderMoreNumberRange(t('storeOrders.orderAmount'), 'totalOrderAmountMin', 'totalOrderAmountMax', 2)}
              {renderMoreNumberRange(t('storeOrders.orderVolume'), 'totalOrderVolumeMin', 'totalOrderVolumeMax', 2)}
              {renderMoreNumberRange(t('storeOrders.shipAmount'), 'importTotalAmountMin', 'importTotalAmountMax', 2)}
              {renderMoreText(t('common.remarks'), 'remarks')}
              {renderMoreText(t('column.updater'), 'updatedBy')}
              {renderMoreDateRange(t('column.createTime'), 'createdAtStart', 'createdAtEnd')}
              {renderMoreDateRange(t('column.updateTime'), 'updatedAtStart', 'updatedAtEnd')}
              <div className="wh-orders-more-actions">
                <Button onClick={() => {
                  setMoreFiltersDraft({})
                  applyMoreFilters({})
                }}>
                  {t('warehouseUi.storeOrders.moreClear')}
                </Button>
                <Button type="primary" onClick={() => applyMoreFilters(moreFiltersDraft)}>
                  {t('warehouseUi.storeOrders.moreApply')}
                </Button>
              </div>
            </div>
          </MoreFiltersButton>
          <span className="list-toolbar-filter-spacer" />
          <Tooltip title={t('common.refresh')}>
            <Button
              icon={<ReloadOutlined />}
              aria-label={t('common.refresh')}
              loading={loading}
              onClick={() => void refreshCurrentList()}
            />
          </Tooltip>
        </div>

        {activeFilterItems.length ? (
          <div className="wh-orders-active-filters">
            <ActiveFilterBar items={activeFilterItems} onClearAll={clearAllFilters} />
          </div>
        ) : null}

        {canUseWarehouseManagerActions && selectedOrders.length ? (
          <div className="wh-orders-selection">
            <SelectionActionBar selectedCount={selectedOrders.length} onClearSelection={() => setSelectedRowKeys([])}>
              <span className="wh-orders-selection-summary">
                {t('warehouseUi.storeOrders.selectionSummary', {
                  quantity: formatStoreOrderInteger(selectedTotals.totalQuantity),
                  amount: formatStoreOrderMoney(selectedTotals.totalOrderAmount),
                })}
              </span>
              <span className="wh-orders-selection-divider" aria-hidden="true" />
              <Button size="small" icon={<TeamOutlined />} onClick={() => setBatchAssignOpen(true)}>
                {t('warehouseUi.storeOrders.assignPicking')}
              </Button>
              <Dropdown
                trigger={['click']}
                menu={{
                  // 批量改状态沿用原「批量改已提交 / 批量改已完成」两个入口与确认弹窗。
                  items: [FlowStatus.Submitted, FlowStatus.Completed].map((status) => ({
                    key: String(status),
                    label: statusLabelMap[status],
                  })),
                  onClick: ({ key }) => handleBatchStatusChange(Number(key) as StoreOrderFlowStatus),
                }}
              >
                <Button size="small">
                  {t('warehouseUi.storeOrders.changeStatus')}
                  <DownOutlined />
                </Button>
              </Dropdown>
              <Tooltip title={canCopySelection ? undefined : t('warehouseUi.storeOrders.copyNeedsOne')}>
                <Button
                  size="small"
                  icon={<CopyOutlined />}
                  disabled={!canCopySelection}
                  onClick={() => {
                    if (canCopySelection) {
                      setCopySourceOrder(selectedOrders[0])
                    }
                  }}
                >
                  {t('warehouseUi.storeOrders.copyAsNew')}
                </Button>
              </Tooltip>
            </SelectionActionBar>
          </div>
        ) : null}

        <DndContext sensors={columnDragSensors} collisionDetection={closestCenter} onDragEnd={handleColumnDragEnd}>
          <SortableContext items={columnOrder} strategy={horizontalListSortingStrategy}>
            <MeasuredTable metricId="warehouse.store-orders.table-3"
              className="store-order-list-table"
              rowKey="orderGUID"
              size="small"
              loading={loading}
              dataSource={data}
              components={{ header: { cell: DraggableHeaderCell } }}
              columns={columns}
              rowSelection={
                canUseWarehouseManagerActions
                  ? {
                      columnWidth: STORE_ORDER_LIST_SELECTION_COLUMN_WIDTH,
                      selectedRowKeys,
                      onChange: setSelectedRowKeys,
                    }
                  : undefined
              }
              scroll={{ x: tableScrollX, y: 620 }}
              summary={renderSummaryRow}
              pagination={{
                current: page,
                pageSize,
                total,
                showSizeChanger: true,
                showTotal: (value) => t('common.total', { count: value }),
              }}
              onChange={(
                pagination: TablePaginationConfig,
                _,
                sorter: SorterResult<StoreOrderListItem> | SorterResult<StoreOrderListItem>[],
              ) => {
                const nextSorter = Array.isArray(sorter) ? sorter[0] : sorter
                const nextSortField =
                  typeof nextSorter?.field === 'string' ? nextSorter.field : sortField
                const nextSortOrder =
                  nextSorter?.order === 'ascend' || nextSorter?.order === 'descend'
                    ? nextSorter.order
                    : sortOrder

                setSortField(nextSortField)
                setSortOrder(nextSortOrder)
                void loadData({
                  pageNumber: pagination.current || 1,
                  pageSize: pagination.pageSize || pageSize,
                  sortBy: nextSortField,
                  sortDescending: nextSortOrder === 'descend',
                })
              }}
            />
          </SortableContext>
        </DndContext>
      </section>

      <StorePickerModal
        open={storePickerOpen}
        title={t('storeOrders.selectStoreCreate')}
        loading={creating}
        onCancel={() => setStorePickerOpen(false)}
        onSelect={async (store) => {
          setCreating(true)
          try {
            const orderGuid = await createStoreOrder({ storeCode: store.storeCode })
            message.success(
              t('storeOrders.createOrderSuccess', { storeName: store.storeName || store.storeCode }),
            )
            setStorePickerOpen(false)
            navigate(`/warehouse/store-order/detail/${orderGuid}`)
            void refreshCurrentList({ pageNumber: 1 })
          } catch (error) {
            console.error(error)
            message.error(error instanceof Error ? error.message : t('storeOrders.createOrderFailed'))
          } finally {
            setCreating(false)
          }
        }}
      />

      {batchAssignOpen ? (
        <BatchAssignModal
          open
          orders={selectedOrders}
          existing={assignmentSummaries}
          onClose={() => setBatchAssignOpen(false)}
          onDone={(_items, printOrderGuids) => {
            setAssignmentSummaryNonce((value) => value + 1)
            if (printOrderGuids.length > 0) {
              setBatchAssignOpen(false)
              navigate(pickingSlipsPath(printOrderGuids))
            }
          }}
        />
      ) : null}

      <CopyOrderModal
        open={Boolean(copySourceOrder)}
        loading={copying}
        sourceOrderNo={copySourceOrder?.orderNo}
        onCancel={() => setCopySourceOrder(null)}
        onConfirm={async (payload) => {
          if (!copySourceOrder) {
            message.warning(t('storeOrders.selectOrdersFirst'))
            return
          }

          setCopying(true)
          try {
            // 源订单固定为打开弹窗时指定的那一张（行内菜单或恰好勾选的 1 单），不再隐式取勾选的第一张。
            const sourceOrderGUID = copySourceOrder.orderGUID
            const result = await copyStoreOrder({
              sourceOrderGUID,
              ...payload,
            })

            const orderGuid = typeof result === 'string' ? result : result.orderGUID
            const orderNo = typeof result === 'string' ? '' : result.orderNo

            message.success(
              orderNo
                ? t('storeOrders.copyOrderSuccessWithNo', { orderNo })
                : t('storeOrders.copyOrderSuccess'),
            )
            setCopySourceOrder(null)
            setSelectedRowKeys([])
            navigate(`/warehouse/store-order/detail/${orderGuid}`, {
              state: {
                orderNo: orderNo || undefined,
              },
            })
            void refreshCurrentList({ pageNumber: 1 })
          } catch (error) {
            console.error(error)
            message.error(error instanceof Error ? error.message : t('storeOrders.copyOrderFailed'))
          } finally {
            setCopying(false)
          }
        }}
      />

      <Modal
        title={t('storeOrders.fixStoreGuidTitle', '修复未匹配分店 GUID')}
        open={unmatchedStoreOpen}
        width="min(1280px, calc(100vw - 48px))"
        okText={t('common.save')}
        cancelText={t('common.cancel')}
        confirmLoading={unmatchedStoreSaving}
        destroyOnHidden
        onCancel={closeUnmatchedStoreModal}
        onOk={() => void handleSaveUnmatchedStoreMappings()}
      >
        <Space direction="vertical" size={12} style={{ width: '100%' }}>
          <Typography.Text type="secondary">
            {t('storeOrders.fixStoreGuidHint', '按旧分店 GUID 聚合订单，为每个 GUID 选择对应本地分店后保存。')}
          </Typography.Text>
          <MeasuredTable metricId="warehouse.store-orders.table-4"
            rowKey="sourceStoreCode"
            size="small"
            loading={unmatchedStoreLoading}
            dataSource={unmatchedStoreGroups}
            pagination={false}
            scroll={{ x: 1138, y: 420 }}
            locale={{ emptyText: t('storeOrders.noUnmatchedStoreGuid', '暂无未匹配分店 GUID') }}
            columns={[
              {
                title: t('storeOrders.sourceStoreCode', '旧 GUID / 标识'),
                dataIndex: 'sourceStoreCode',
                width: 260,
                render: (value: string) => (
                  <Typography.Text code copyable className="store-order-unmatched-source">
                    {value}
                  </Typography.Text>
                ),
              },
              {
                title: t('storeOrders.sourceStoreName', 'HQ 客户名称'),
                dataIndex: 'sourceStoreName',
                width: 150,
                render: (value: string | undefined) => value || '--',
              },
              {
                title: t('storeOrders.unmatchedOrderCount', '订单数量'),
                dataIndex: 'orderCount',
                width: 88,
                render: (value: number) => renderStoreOrderNumericCell(value),
              },
              {
                title: t('storeOrders.latestOrderDate', '最近订单日期'),
                dataIndex: 'latestOrderDate',
                width: 120,
                render: (value: string | undefined) => formatDate(value, i18n.language),
              },
              {
                title: t('storeOrders.targetStore', '目标本地分店'),
                dataIndex: 'sourceStoreCode',
                width: 520,
                render: (sourceStoreCode: string) => (
                  <Select
                    value={unmatchedStoreTargets[sourceStoreCode]}
                    allowClear
                    showSearch
                    style={{ width: '100%' }}
                    popupMatchSelectWidth={640}
                    classNames={{ popup: { root: 'store-order-unmatched-target-popup' } }}
                    placeholder={t('storeOrders.selectTargetStore')}
                    optionFilterProp="label"
                    filterOption={filterStoreOption}
                    options={unmatchedTargetStoreOptions}
                    onChange={(value) => {
                      setUnmatchedStoreTargets((current) => {
                        const next = { ...current }
                        if (value) {
                          next[sourceStoreCode] = value
                        } else {
                          delete next[sourceStoreCode]
                        }
                        return next
                      })
                    }}
                  />
                ),
              },
            ]}
          />
        </Space>
      </Modal>

      <Modal
        title={t('storeOrders.shipOrderTitle')}
        open={Boolean(shippingOrder)}
        okText={t('storeOrders.confirmShipOrder')}
        cancelText={t('common.cancel')}
        confirmLoading={shippingLoading}
        destroyOnHidden
        onCancel={closeShippingModal}
        onOk={() => void handleConfirmShipping()}
      >
        <Space direction="vertical" size={12} style={{ width: '100%' }}>
          <Typography.Text>
            {t('storeOrders.shipOrderConfirm', {
              orderNo: shippingOrder?.orderNo || '--',
            })}
          </Typography.Text>
          <DatePicker
            style={{ width: '100%' }}
            value={shippingDate}
            onChange={(value) => setShippingDate(value ?? dayjs())}
          />
        </Space>
      </Modal>
    </PageContainer>
  )
}
