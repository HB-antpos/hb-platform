import {
  CopyOutlined,
  PlusOutlined,
  ReloadOutlined,
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
import type { FilterDropdownProps, SorterResult } from 'antd/es/table/interface'
import {
  App as AntdApp,
  Button,
  Card,
  Checkbox,
  DatePicker,
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
  Typography,
} from 'antd'
import type { Dayjs } from 'dayjs'
import dayjs from 'dayjs'
import { useCallback, useEffect, useLayoutEffect, useMemo, useRef, useState, type CSSProperties, type HTMLAttributes, type PointerEvent as ReactPointerEvent, type ReactNode } from 'react'
import { useTranslation } from 'react-i18next'
import { useNavigate } from 'react-router-dom'
import PageContainer from '../../../components/PageContainer'
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
import {
  StoreOrderFlowStatus as FlowStatus,
  StoreOrderStatusColorMap,
} from '../../../types/storeOrder'
import { getDateTagColor } from '../../../utils/tagColors'
import { getStoreColor } from '../../../utils/userTableColors'
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
import { formatStoreOrderVolume } from './volumeFormat'
import './compact.css'
import { MeasuredTable } from '../../../components/MeasuredTable'

type RangeValue = [Dayjs | null, Dayjs | null] | null
type StoreOrderListTextFilterKey = 'orderNo' | 'remarks' | 'updatedBy'
type StoreOrderListDateStartKey = 'outboundDateStart' | 'createdAtStart' | 'updatedAtStart'
type StoreOrderListDateEndKey = 'outboundDateEnd' | 'createdAtEnd' | 'updatedAtEnd'
type StoreOrderListNumberRange = {
  min: keyof StoreOrderListColumnFilters
  max: keyof StoreOrderListColumnFilters
}
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

function formatAmount(value?: number) {
  if (value === undefined || value === null) {
    return '--'
  }
  return value.toFixed(2)
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

function renderDateTag(value?: string, language?: string) {
  const displayValue = formatDate(value, language)
  if (displayValue === '--') {
    return '--'
  }

  return <Tag className="store-order-nowrap" color={getDateTagColor(displayValue)}>{displayValue}</Tag>
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

const DEFAULT_STATUS_LIST = [FlowStatus.Submitted, FlowStatus.Picking]
const STATUS_FILTER_ORDER = [FlowStatus.Submitted, FlowStatus.Picking, FlowStatus.Completed]
const STORE_ORDER_LIST_SELECTION_COLUMN_WIDTH = 48
const STORE_ORDER_LIST_COLUMN_ORDER_STORAGE_KEY = 'hbweb_rv.storeOrders.list.columnOrder.v1'
const STORE_ORDER_LIST_COLUMN_WIDTH_STORAGE_KEY = 'hbweb_rv.storeOrders.list.columnWidths.v1'
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

function CopyOrderModal({ open, loading, onCancel, onConfirm }: CopyOrderModalProps) {
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

  const [loading, setLoading] = useState(false)
  const [creating, setCreating] = useState(false)
  const [copying, setCopying] = useState(false)
  const [data, setData] = useState<StoreOrderListItem[]>([])
  const [branches, setBranches] = useState<StoreOrderBranchOption[]>([])
  const [selectedRowKeys, setSelectedRowKeys] = useState<React.Key[]>([])
  const [keyword, setKeyword] = useState('')
  const [dateRange, setDateRange] = useState<RangeValue>(null)
  const [selectedStoreCodes, setSelectedStoreCodes] = useState<string[]>([])
  const [statusList, setStatusList] = useState<StoreOrderFlowStatus[]>(DEFAULT_STATUS_LIST)
  const [columnFilters, setColumnFilters] = useState<StoreOrderListColumnFilters>({})
  const [page, setPage] = useState(1)
  const [pageSize, setPageSize] = useState(20)
  const [total, setTotal] = useState(0)
  const [sortField, setSortField] = useState('orderDate')
  const [sortOrder, setSortOrder] = useState<'ascend' | 'descend'>('descend')
  const [storePickerOpen, setStorePickerOpen] = useState(false)
  const [copyModalOpen, setCopyModalOpen] = useState(false)
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

  const statusOptions = useMemo(
    () =>
      STATUS_FILTER_ORDER.map((status) => ({
        value: status,
        label: statusLabelMap[status],
      })),
    [statusLabelMap],
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
    statusList: hasStoreOrderListQueryOverride(overrides, 'statusList')
      ? overrides.statusList
      : statusList.length ? statusList : undefined,
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

  const loadData = async (
    overrides: Partial<StoreOrderListQuery & { pageNumber: number; pageSize: number }> = {},
  ) => {
    if (!isMountedRef.current) {
      return
    }
    const query = buildQuery(overrides)

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

  const updateColumnFilters = (patch: StoreOrderListColumnFilters) => {
    setColumnFilters((current) => ({ ...current, ...patch }))
  }

  const applyColumnFilters = (
    confirm: FilterDropdownProps['confirm'],
    nextFilters: StoreOrderListColumnFilters = columnFilters,
  ) => {
    confirm()
    void loadData({ pageNumber: 1, columnFilters: nextFilters })
  }

  const clearColumnFilter = (
    keys: Array<keyof StoreOrderListColumnFilters>,
    confirm: FilterDropdownProps['confirm'],
  ) => {
    const nextFilters = { ...columnFilters }
    keys.forEach((key) => {
      delete nextFilters[key]
    })
    setColumnFilters(nextFilters)
    applyColumnFilters(confirm, nextFilters)
  }

  const applyTopFilter = (
    confirm: FilterDropdownProps['confirm'],
    overrides: Partial<StoreOrderListQuery>,
  ) => {
    confirm()
    void loadData({ ...overrides, pageNumber: 1 })
  }

  const getColumnDateRange = (
    startKey: StoreOrderListDateStartKey,
    endKey: StoreOrderListDateEndKey,
  ): RangeValue => [
    columnFilters[startKey] ? dayjs(columnFilters[startKey]) : null,
    columnFilters[endKey] ? dayjs(columnFilters[endKey]) : null,
  ]

  const setColumnDateRange = (
    startKey: StoreOrderListDateStartKey,
    endKey: StoreOrderListDateEndKey,
    value: RangeValue,
  ) => {
    updateColumnFilters({
      [startKey]: value?.[0]?.startOf('day').toISOString(),
      [endKey]: value?.[1]?.endOf('day').toISOString(),
    })
  }

  const makeTextFilterDropdown = (
    key: StoreOrderListTextFilterKey,
    placeholder: string,
  ) => ({ confirm }: FilterDropdownProps) => (
    <div className="store-order-list-column-filter" onKeyDown={(event) => event.stopPropagation()} onMouseDown={(event) => event.stopPropagation()}>
      <Input
        value={columnFilters[key] ?? ''}
        allowClear
        placeholder={placeholder}
        onChange={(event) => updateColumnFilters({ [key]: event.target.value })}
        onPressEnter={() => applyColumnFilters(confirm)}
      />
      <Space>
        <Button size="small" type="primary" onClick={() => applyColumnFilters(confirm)}>{t('containers.actions.applyColumnFilter', '应用')}</Button>
        <Button size="small" onClick={() => clearColumnFilter([key], confirm)}>{t('containers.actions.resetColumnFilter', '重置')}</Button>
      </Space>
    </div>
  )

  const makeDateRangeFilterDropdown = (
    startKey: StoreOrderListDateStartKey,
    endKey: StoreOrderListDateEndKey,
  ) => ({ confirm }: FilterDropdownProps) => (
    <div className="store-order-list-column-filter" onKeyDown={(event) => event.stopPropagation()} onMouseDown={(event) => event.stopPropagation()}>
      <DatePicker.RangePicker
        value={getColumnDateRange(startKey, endKey)}
        onChange={(value) => setColumnDateRange(startKey, endKey, value)}
      />
      <Space>
        <Button size="small" type="primary" onClick={() => applyColumnFilters(confirm)}>{t('containers.actions.applyColumnFilter', '应用')}</Button>
        <Button size="small" onClick={() => clearColumnFilter([startKey, endKey], confirm)}>{t('containers.actions.resetColumnFilter', '重置')}</Button>
      </Space>
    </div>
  )

  const makeNumberRangeFilterDropdown = (
    range: StoreOrderListNumberRange,
  ) => ({ confirm }: FilterDropdownProps) => (
    <div className="store-order-list-column-filter" onKeyDown={(event) => event.stopPropagation()} onMouseDown={(event) => event.stopPropagation()}>
      <Space.Compact>
        <InputNumber
          value={columnFilters[range.min] as number | undefined}
          placeholder={t('containers.placeholders.minValue', '最小值')}
          controls={false}
          onChange={(value) => updateColumnFilters({ [range.min]: value == null ? undefined : Number(value) })}
        />
        <InputNumber
          value={columnFilters[range.max] as number | undefined}
          placeholder={t('containers.placeholders.maxValue', '最大值')}
          controls={false}
          onChange={(value) => updateColumnFilters({ [range.max]: value == null ? undefined : Number(value) })}
        />
      </Space.Compact>
      <Space>
        <Button size="small" type="primary" onClick={() => applyColumnFilters(confirm)}>{t('containers.actions.applyColumnFilter', '应用')}</Button>
        <Button size="small" onClick={() => clearColumnFilter([range.min, range.max], confirm)}>{t('containers.actions.resetColumnFilter', '重置')}</Button>
      </Space>
    </div>
  )

  const makeStoreFilterDropdown = ({ confirm }: FilterDropdownProps) => (
    <div className="store-order-list-column-filter" onKeyDown={(event) => event.stopPropagation()} onMouseDown={(event) => event.stopPropagation()}>
      <Select
        mode="multiple"
        value={selectedStoreCodes}
        allowClear
        showSearch
        style={{ minWidth: 240 }}
        placeholder={t('storeOrders.allStores')}
        optionFilterProp="label"
        filterOption={filterStoreOption}
        options={storeFilterOptions}
        onChange={(value) => setSelectedStoreCodes(value)}
      />
      <Space>
        <Button size="small" type="primary" onClick={() => applyTopFilter(confirm, { storeCodes: selectedStoreCodes.length ? selectedStoreCodes : undefined })}>{t('containers.actions.applyColumnFilter', '应用')}</Button>
        <Button size="small" onClick={() => {
          setSelectedStoreCodes([])
          applyTopFilter(confirm, { storeCodes: undefined })
        }}>{t('containers.actions.resetColumnFilter', '重置')}</Button>
      </Space>
    </div>
  )

  const makeOrderDateFilterDropdown = ({ confirm }: FilterDropdownProps) => (
    <div className="store-order-list-column-filter" onKeyDown={(event) => event.stopPropagation()} onMouseDown={(event) => event.stopPropagation()}>
      <DatePicker.RangePicker value={dateRange} onChange={(value) => setDateRange(value)} />
      <Space>
        <Button size="small" type="primary" onClick={() => applyTopFilter(confirm, {
          startDate: dateRange?.[0]?.startOf('day').toISOString(),
          endDate: dateRange?.[1]?.endOf('day').toISOString(),
        })}>{t('containers.actions.applyColumnFilter', '应用')}</Button>
        <Button size="small" onClick={() => {
          setDateRange(null)
          applyTopFilter(confirm, { startDate: undefined, endDate: undefined })
        }}>{t('containers.actions.resetColumnFilter', '重置')}</Button>
      </Space>
    </div>
  )

  const makeStatusFilterDropdown = ({ confirm }: FilterDropdownProps) => (
    <div className="store-order-list-column-filter" onKeyDown={(event) => event.stopPropagation()} onMouseDown={(event) => event.stopPropagation()}>
      <Checkbox.Group
        value={statusList}
        options={statusOptions}
        onChange={(value) => setStatusList(value as StoreOrderFlowStatus[])}
      />
      <Space>
        <Button size="small" type="primary" onClick={() => applyTopFilter(confirm, { statusList: statusList.length ? statusList : undefined })}>{t('containers.actions.applyColumnFilter', '应用')}</Button>
        <Button size="small" onClick={() => {
          setStatusList(DEFAULT_STATUS_LIST)
          applyTopFilter(confirm, { statusList: DEFAULT_STATUS_LIST })
        }}>{t('containers.actions.resetColumnFilter', '重置')}</Button>
      </Space>
    </div>
  )

  const filterIcon = (active?: boolean) => <SearchOutlined style={{ color: active ? '#1677ff' : undefined }} />
  const hasNumberRangeFilter = (range: StoreOrderListNumberRange) => (
    typeof columnFilters[range.min] === 'number' || typeof columnFilters[range.max] === 'number'
  )
  const textFilterProps = (key: StoreOrderListTextFilterKey, placeholder: string) => ({
    filterDropdown: makeTextFilterDropdown(key, placeholder),
    filterIcon,
    filtered: Boolean(columnFilters[key]?.trim()),
  })
  const dateRangeFilterProps = (
    startKey: StoreOrderListDateStartKey,
    endKey: StoreOrderListDateEndKey,
  ) => ({
    filterDropdown: makeDateRangeFilterDropdown(startKey, endKey),
    filterIcon,
    filtered: Boolean(columnFilters[startKey] || columnFilters[endKey]),
  })
  const numberFilterProps = (range: StoreOrderListNumberRange) => ({
    filterDropdown: makeNumberRangeFilterDropdown(range),
    filterIcon,
    filtered: hasNumberRangeFilter(range),
  })

  useEffect(() => {
    void Promise.all([loadData({ pageNumber: 1 }), loadBranches()])
  }, [])

  useLayoutEffect(() => {
    isMountedRef.current = true
    return () => {
      isMountedRef.current = false
      listRequestGuardRef.current.invalidate()
    }
  }, [])

  // 分店订货的 HQ 全量/增量同步（HQ 订货单 → HBweb）已于 2026-09-29 停用：
  // 2026-09-21 只读核查时 HQ 分店订货单主表最后变更停在 2026-06-21，订货业务已迁到 HBweb。
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

  const baseColumns = useMemo<ColumnsType<StoreOrderListItem>>(
    () => [
      {
        key: 'index',
        title: t('column.index'),
        dataIndex: 'index',
        width: 64,
        fixed: 'left',
        render: (_, __, index) => renderStoreOrderNumericCell((page - 1) * pageSize + index + 1),
      },
      {
        key: 'orderNo',
        title: t('column.orderNo'),
        dataIndex: 'orderNo',
        width: 152,
        sorter: true,
        ...textFilterProps('orderNo', t('storeOrders.filterOrderNo', '过滤订单号')),
        fixed: 'left',
        render: (value: string, record) => (
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
        ),
      },
      {
        key: 'storeCode',
        title: t('column.store'),
        dataIndex: 'storeCode',
        width: 180,
        sorter: true,
        filterDropdown: makeStoreFilterDropdown,
        filterIcon,
        filtered: selectedStoreCodes.length > 0,
        render: (value: string | undefined, record) => {
          const code = value || '--'
          const name = record.storeName || (value ? branchMap[value] : undefined)
          return (
            <Tag
              className="store-order-store-tag"
              color={getStoreColor(code)}
              title={name ? `${code} - ${name}` : code}
              style={{ cursor: value ? 'pointer' : 'default' }}
              onClick={() => {
                if (!value) {
                  return
                }
                setSelectedStoreCodes([value])
                void loadData({ pageNumber: 1, storeCodes: [value] })
              }}
            >
              {name ? `${code} - ${name}` : code}
            </Tag>
          )
        },
      },
      {
        key: 'orderDate',
        title: t('column.orderDate'),
        dataIndex: 'orderDate',
        width: 124,
        sorter: true,
        filterDropdown: makeOrderDateFilterDropdown,
        filterIcon,
        filtered: Boolean(dateRange?.[0] || dateRange?.[1]),
        render: (value: string | undefined) => renderDateTag(value, i18n.language),
      },
      {
        key: 'outboundDate',
        title: t('storeOrders.outboundDate'),
        dataIndex: 'outboundDate',
        width: 124,
        sorter: true,
        ...dateRangeFilterProps('outboundDateStart', 'outboundDateEnd'),
        render: (value: string | undefined) => renderDateTag(value, i18n.language),
      },
      {
        key: 'flowStatus',
        title: t('column.status'),
        dataIndex: 'flowStatus',
        width: 104,
        sorter: true,
        filterDropdown: makeStatusFilterDropdown,
        filterIcon,
        filtered: statusList.length > 0,
        render: (value: StoreOrderFlowStatus, record) => (
          <Tag
            color={StoreOrderStatusColorMap[value] || 'default'}
            style={{
              cursor:
                canUseWarehouseManagerActions &&
                (value === FlowStatus.Submitted || value === FlowStatus.Completed)
                  ? 'pointer'
                  : 'default',
            }}
            onClick={() => handleStatusToggle(record)}
          >
            {statusLabelMap[value] || `${t('column.status')} ${value}`}
          </Tag>
        ),
      },
      {
        key: 'totalQuantity',
        title: t('storeOrders.orderQuantity'),
        dataIndex: 'totalQuantity',
        width: 116,
        sorter: true,
        ...numberFilterProps({ min: 'totalQuantityMin', max: 'totalQuantityMax' }),
        render: (value: number | undefined) => renderStoreOrderNumericCell(value ?? '--'),
      },
      {
        key: 'totalOrderAmount',
        title: t('storeOrders.orderAmount'),
        dataIndex: 'totalOrderAmount',
        width: 116,
        sorter: true,
        ...numberFilterProps({ min: 'totalOrderAmountMin', max: 'totalOrderAmountMax' }),
        render: (value: number) => renderStoreOrderNumericCell(formatAmount(value)),
      },
      {
        key: 'totalOrderVolume',
        title: t('storeOrders.orderVolume'),
        dataIndex: 'totalOrderVolume',
        width: 112,
        ...numberFilterProps({ min: 'totalOrderVolumeMin', max: 'totalOrderVolumeMax' }),
        render: (value: number | undefined) => renderStoreOrderNumericCell(formatStoreOrderVolume(value)),
      },
      {
        key: 'importTotalAmount',
        title: t('storeOrders.shipAmount'),
        dataIndex: 'importTotalAmount',
        width: 116,
        sorter: true,
        ...numberFilterProps({ min: 'importTotalAmountMin', max: 'importTotalAmountMax' }),
        render: (value: number) => renderStoreOrderNumericCell(formatAmount(value)),
      },
      {
        key: 'remarks',
        title: t('common.remarks'),
        dataIndex: 'remarks',
        width: 220,
        ...textFilterProps('remarks', t('storeOrders.filterRemarks', '过滤备注')),
        render: (value: string | undefined) => renderStoreOrderTwoLineText(value),
      },
      {
        key: 'createdAt',
        title: t('column.createTime'),
        dataIndex: 'createdAt',
        width: 160,
        ...dateRangeFilterProps('createdAtStart', 'createdAtEnd'),
        render: (value: string | undefined) => <span className="store-order-nowrap">{formatDateTime(value, i18n.language)}</span>,
      },
      {
        key: 'updatedBy',
        title: t('column.updater'),
        dataIndex: 'updatedBy',
        width: 120,
        ...textFilterProps('updatedBy', t('storeOrders.filterUpdatedBy', '过滤更新人')),
        render: (value: string | undefined) => <span className="store-order-nowrap">{value || '--'}</span>,
      },
      {
        key: 'updatedAt',
        title: t('column.updateTime'),
        dataIndex: 'updatedAt',
        width: 160,
        ...dateRangeFilterProps('updatedAtStart', 'updatedAtEnd'),
        render: (value: string | undefined) => <span className="store-order-nowrap">{formatDateTime(value, i18n.language)}</span>,
      },
      ...(canUseWarehouseManagerActions
        ? [
            {
              key: 'pickingAssignment',
              title: t('storeOrders.pickingAssignment.listColumn', '拣货分配'),
              width: 200,
              render: (_: unknown, record: StoreOrderListItem) => {
                const assignees = assignmentSummaries[record.orderGUID]
                return assignees?.length ? (
                  <Space size={4} wrap>
                    {assignees.map((assignee) => (
                      <AssigneeChip key={assignee.segmentNo} segmentNo={assignee.segmentNo} name={assignee.pickerName} lineCount={assignee.lineCount} />
                    ))}
                  </Space>
                ) : (
                  <span className="store-order-nowrap" style={{ color: '#8c8c8c' }}>
                    {t('storeOrders.pickingAssignment.unassignedShort', '未分配')}
                  </span>
                )
              },
            },
          ]
        : []),
      {
        title: t('column.action'),
        key: 'action',
        fixed: 'right',
        width: 172,
        render: (_, record) => (
          <Space size={0} wrap={false}>
            {canUseWarehouseManagerActions && (record.flowStatus === FlowStatus.Submitted || record.flowStatus === FlowStatus.Picking) ? (
              <Button size="small" type="link" onClick={() => openShippingModal(record)}>
                {t('storeOrders.shipOrder')}
              </Button>
            ) : null}
            <Button size="small" type="link" onClick={() => openDetail(record)}>
              {t('common.view')}
            </Button>
            {canDeleteStoreOrder ? (
              <Popconfirm
                title={t('storeOrders.confirmDeleteOrder', { orderNo: record.orderNo })}
                okText={t('common.delete')}
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
                  }
                }}
              >
                <Button danger type="link">
                  {t('common.delete')}
                </Button>
              </Popconfirm>
            ) : null}
          </Space>
        ),
      },
    ],
    [
      assignmentSummaries,
      branchMap,
      canUseWarehouseManagerActions,
      canDeleteStoreOrder,
      columnFilters,
      dateRange,
      i18n.language,
      page,
      pageSize,
      refreshCurrentList,
      selectedStoreCodes,
      statusLabelMap,
      statusList,
      statusOptions,
      storeFilterOptions,
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
    return activeOrder
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
  }, [baseColumns, columnOrder, columnWidths, draggableColumnKeys, handleColumnResizeStart])
  const tableScrollX =
    (canUseWarehouseManagerActions ? STORE_ORDER_LIST_SELECTION_COLUMN_WIDTH : 0)
    + columns.reduce((total, column) => {
      const width = typeof column.width === 'number' ? column.width : Number(column.width)
      return total + (Number.isFinite(width) ? width : 0)
    }, 0)

  return (
    <PageContainer
      title={t('storeOrders.title')}
      subtitle={t('storeOrders.subtitle')}
      extra={
        <Space wrap>
          {canUseWarehouseManagerActions ? (
            <Button
              icon={<ToolOutlined />}
              loading={unmatchedStoreLoading}
              onClick={openUnmatchedStoreModal}
            >
              {t('storeOrders.fixStoreGuid', '修复分店 GUID')}
            </Button>
          ) : null}
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
          {canUseWarehouseManagerActions ? (
            <Button
              icon={<CopyOutlined />}
              disabled={!selectedRowKeys.length}
              onClick={() => setCopyModalOpen(true)}
            >
              {t('storeOrders.copyOrder', { count: selectedRowKeys.length })}
            </Button>
          ) : null}
          {canUseWarehouseManagerActions ? (
            <Button
              icon={<TeamOutlined />}
              disabled={!selectedRowKeys.length}
              onClick={() => setBatchAssignOpen(true)}
            >
              {t('storeOrders.pickingAssignment.batchButton', '分配拣货（{{count}}）', { count: selectedRowKeys.length })}
            </Button>
          ) : null}
          <Button
            icon={<ReloadOutlined />}
            onClick={() => {
              setKeyword('')
              setDateRange(null)
              setSelectedStoreCodes([])
              setStatusList(DEFAULT_STATUS_LIST)
              setColumnFilters({})
              setSortField('orderDate')
              setSortOrder('descend')
              void loadData({
                keyword: undefined,
                startDate: undefined,
                endDate: undefined,
                storeCodes: undefined,
                statusList: DEFAULT_STATUS_LIST,
                columnFilters: undefined,
                pageNumber: 1,
                pageSize,
                sortBy: 'orderDate',
                sortDescending: true,
              })
            }}
          >
            {t('common.reset')}
          </Button>
          {isColumnSettingsCustomized ? (
            <Button icon={<ReloadOutlined />} onClick={resetColumnOrder}>
              {t('containers.actions.resetColumns', '重置列')}
            </Button>
          ) : null}
          {canUseWarehouseManagerActions ? (
            <>
              <Button
                disabled={!selectedRowKeys.length}
                onClick={() => handleBatchStatusChange(FlowStatus.Submitted)}
              >
                {t('storeOrders.batchSubmitted')}
              </Button>
              <Button
                disabled={!selectedRowKeys.length}
                onClick={() => handleBatchStatusChange(FlowStatus.Completed)}
              >
                {t('storeOrders.batchCompleted')}
              </Button>
            </>
          ) : null}
        </Space>
      }
    >
      <Card>
        <Space wrap className="store-order-list-filter-bar">
          <Input
            value={keyword}
            style={{ width: 260 }}
            allowClear
            prefix={<SearchOutlined />}
            placeholder={t('storeOrders.searchPlaceholder')}
            onChange={(event) => setKeyword(event.target.value)}
          />
          <DatePicker.RangePicker value={dateRange} onChange={(value) => setDateRange(value)} />
          <Select
            mode="multiple"
            value={selectedStoreCodes}
            allowClear
            showSearch
            style={{ width: 280 }}
            placeholder={t('storeOrders.allStores')}
            optionFilterProp="label"
            filterOption={filterStoreOption}
            options={storeFilterOptions}
            onChange={(value) => setSelectedStoreCodes(value)}
          />
          <Checkbox.Group
            value={statusList}
            options={statusOptions}
            onChange={(value) => setStatusList(value as StoreOrderFlowStatus[])}
          />
          <Button type="primary" onClick={() => void loadData({ pageNumber: 1 })}>
            {t('common.query')}
          </Button>
        </Space>

        <DndContext sensors={columnDragSensors} collisionDetection={closestCenter} onDragEnd={handleColumnDragEnd}>
          <SortableContext items={columnOrder} strategy={horizontalListSortingStrategy}>
            <MeasuredTable metricId="warehouse.store-orders.table-3"
              className="store-order-list-table"
              rowKey="orderGUID"
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
      </Card>

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
          orders={data.filter((order) => selectedRowKeys.includes(order.orderGUID))}
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
        open={copyModalOpen}
        loading={copying}
        onCancel={() => setCopyModalOpen(false)}
        onConfirm={async (payload) => {
          if (!selectedRowKeys.length) {
            message.warning(t('storeOrders.selectOrdersFirst'))
            return
          }

          setCopying(true)
          try {
            const sourceOrderGUID = String(selectedRowKeys[0])
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
            setCopyModalOpen(false)
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
