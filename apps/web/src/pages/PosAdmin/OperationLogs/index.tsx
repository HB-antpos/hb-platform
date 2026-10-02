import {
  AlertOutlined,
  CheckOutlined,
  EyeOutlined,
  FlagOutlined,
  HolderOutlined,
  ReloadOutlined,
  SearchOutlined,
  WarningOutlined,
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
  Alert,
  Button,
  Card,
  Col,
  DatePicker,
  Drawer,
  Form,
  Input,
  Row,
  Select,
  Space,
  Tabs,
  Tag,
  Typography,
  message,
  theme,
} from 'antd'
import type { RangePickerProps } from 'antd/es/date-picker'
import type { ColumnsType, TableProps } from 'antd/es/table'
import dayjs, { type Dayjs } from 'dayjs'
import {
  useCallback,
  useEffect,
  useMemo,
  useRef,
  useState,
  type CSSProperties,
  type HTMLAttributes,
} from 'react'
import { useTranslation } from 'react-i18next'

import { MeasuredTable } from '../../../components/MeasuredTable'
import PageContainer from '../../../components/PageContainer'
import { registerPageMessages } from '../../../i18n/registerPageMessages'
import {
  getOperationAuditContext,
  getOperationAuditDetail,
  getOperationAuditEmployeeSummary,
  getOperationAudits,
  getOperationAuditSummary,
} from '../../../services/operationAuditService'
import { getActiveStores, type StoreOption } from '../../../services/storeService'
import { useAuthStore } from '../../../store/auth'
import type {
  LegacyEmployeeLogEmployeeSummary,
  LegacyEmployeeLogEmployeeSummaryResult,
  LegacyEmployeeLogReview,
  LegacyReviewStatus,
  LegacyRiskLens,
} from '../../../types/legacyEmployeeLog'
import type {
  OperationAuditContext,
  OperationAuditDetail,
  OperationAuditDeviceSystem,
  OperationAuditEmployeeSummaryResult,
  OperationAuditListItem,
  OperationAuditOutcome,
  OperationAuditSortField,
  OperationAuditSummary,
} from '../../../types/operationAudit'
import {
  buildStoreOptionsFromUserStores,
} from '../../../utils/managedStoreScope'
import type { EmployeeLogsHeader } from '../EmployeeLogs/employeeLogsSource'
import EmployeeSummaryTable from '../LegacyEmployeeLogs/EmployeeSummaryTable'
import {
  DEFAULT_RISK_FILTER,
  POS_DANGER_GROUP_OPERATIONS,
  POS_RULE_CODES,
  buildStoreNameMap,
  formatStoreLabel,
  sameOperations,
  type LegacyRiskFilter,
  type PosDangerGroupKey,
} from '../LegacyEmployeeLogs/legacyEmployeeLogsLogic'
import legacyEmployeeLogsMessagesEn from '../LegacyEmployeeLogs/legacyEmployeeLogsMessages.en.json'
import legacyEmployeeLogsMessagesZh from '../LegacyEmployeeLogs/legacyEmployeeLogsMessages.zh.json'
import RiskLensBar from '../LegacyEmployeeLogs/RiskLensBar'

import {
  DEFAULT_OPERATION_LOG_COLUMN_ORDER,
  createOperationLogDndAccessibility,
  dispatchOperationLogDragHandleKeyDown,
  isOperationLogColumnOrderCustomized,
  moveOperationLogColumnOrder,
  parseOperationLogColumnOrder,
  type OperationLogColumnKey,
} from './operationLogColumnOrder'
import {
  OPERATION_TYPE_KEYS,
  OPERATION_AUDIT_DEVICE_SYSTEM_OPTIONS,
  DEFAULT_OPERATION_AUDIT_SORT,
  buildOperationAuditQuery,
  createLatestOperationAuditRequestGuard,
  formatSignedMoney,
  resolveOperationAuditTableChange,
  toLegacyEmployeeSummary,
  type OperationAuditTableSortOrder,
} from './operationLogsLogic'
import PosLogDetailPanel from './PosLogDetailPanel'
import PosProductSummary from './PosProductSummary'

// 风险、核查文案与老收银共用（规则编号相同），随页面代码块懒加载。
registerPageMessages({ zh: legacyEmployeeLogsMessagesZh, en: legacyEmployeeLogsMessagesEn })

const POS_DANGER_KEYS = Object.keys(POS_DANGER_GROUP_OPERATIONS) as PosDangerGroupKey[]
type ResultView = 'records' | 'employees'

interface OperationAuditFormValues {
  timeRange: [Dayjs, Dayjs]
  storeCodes?: string[]
  cashierKeyword?: string
  deviceCode?: string
  deviceSystem?: OperationAuditDeviceSystem
  operationTypes?: string[]
  outcome?: string
  productKeyword?: string
  orderGuid?: string
  keyword?: string
}

const DEFAULT_PAGE_SIZE = 20
const OPERATION_LOG_COLUMN_ORDER_STORAGE_KEY = 'hbweb_rv.operationLogs.columnOrder.v1'
// 连续编号通常没有空格，允许在任意位置折行，避免终端号等内容被省略。
const WRAPPED_TABLE_CELL_STYLE: CSSProperties = {
  whiteSpace: 'normal',
  overflowWrap: 'anywhere',
  wordBreak: 'break-word',
}

interface DraggableHeaderCellProps extends HTMLAttributes<HTMLTableCellElement> {
  'data-column-key'?: string
  'data-drag-label'?: string
}

function DraggableHeaderCell({ children, style, ...props }: DraggableHeaderCellProps) {
  const columnKey = props['data-column-key']
  const dragLabel = props['data-drag-label']
  const {
    attributes,
    listeners,
    setNodeRef,
    transform,
    transition,
    isDragging,
  } = useSortable({
    id: columnKey ?? '__operation-log-static-column__',
    disabled: !columnKey,
  })

  if (!columnKey) return <th style={style} {...props}>{children}</th>

  const headerStyle: CSSProperties = {
    ...style,
    transform: CSS.Translate.toString(transform),
    transition,
    zIndex: isDragging ? 3 : style?.zIndex,
    opacity: isDragging ? 0.8 : style?.opacity,
  }
  const { onKeyDown: dndKeyDownListener, ...pointerListeners } = listeners ?? {}

  return (
    <th ref={setNodeRef} style={headerStyle} {...props}>
      <div style={{ display: 'inline-flex', alignItems: 'center', gap: 6, width: '100%' }}>
        <button
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
          }}
          {...attributes}
          {...pointerListeners}
          // 只有把手接收拖拽监听；鼠标松手和键盘操作都不能冒泡触发表头排序。
          onClick={(event) => event.stopPropagation()}
          onKeyDown={(event) => {
            dispatchOperationLogDragHandleKeyDown(event, (dragEvent) => {
              dndKeyDownListener?.(dragEvent)
            })
          }}
        >
          <HolderOutlined />
        </button>
        <div style={{ minWidth: 0 }}>{children}</div>
      </div>
    </th>
  )
}

function getDefaultTimeRange(): [Dayjs, Dayjs] {
  return [dayjs().subtract(7, 'day'), dayjs()]
}

function getOutcomeColor(outcome: OperationAuditOutcome) {
  switch (outcome) {
    case 'Succeeded':
      return 'success'
    case 'Denied':
      return 'warning'
    case 'Failed':
      return 'error'
    default:
      return 'default'
  }
}

/** header 由员工操作日志合并页传入（统一标题与来源切换）；单独使用时沿用本页标题。 */
export default function PosAdminOperationLogsPage({ header }: { header?: EmployeeLogsHeader } = {}) {
  const { t } = useTranslation()
  const { token } = theme.useToken()
  const [form] = Form.useForm<OperationAuditFormValues>()
  const access = useAuthStore((state) => state.access)
  const currentUser = useAuthStore((state) => state.currentUser)
  const managedStoreCodes = access.managedStoreCodes?.()
  const managedStoreCodeKey = managedStoreCodes?.join(',') ?? 'all'
  const [storeOptions, setStoreOptions] = useState<StoreOption[]>([])
  const [loading, setLoading] = useState(false)
  const [loadError, setLoadError] = useState(false)
  const [data, setData] = useState<OperationAuditListItem[]>([])
  const [total, setTotal] = useState(0)
  const [pageNumber, setPageNumber] = useState(1)
  const [pageSize, setPageSize] = useState(DEFAULT_PAGE_SIZE)
  const [sortBy, setSortBy] = useState<OperationAuditSortField>(
    DEFAULT_OPERATION_AUDIT_SORT.sortBy,
  )
  const [sortOrder, setSortOrder] = useState<OperationAuditTableSortOrder>(
    DEFAULT_OPERATION_AUDIT_SORT.sortOrder,
  )
  const [columnOrder, setColumnOrder] = useState<OperationLogColumnKey[]>(() => {
    if (typeof window === 'undefined') return [...DEFAULT_OPERATION_LOG_COLUMN_ORDER]
    try {
      const saved = localStorage.getItem(OPERATION_LOG_COLUMN_ORDER_STORAGE_KEY)
      return parseOperationLogColumnOrder(saved)
    } catch {
      return [...DEFAULT_OPERATION_LOG_COLUMN_ORDER]
    }
  })
  const [detailRecord, setDetailRecord] = useState<OperationAuditListItem | null>(null)
  const [detail, setDetail] = useState<OperationAuditDetail | null>(null)
  const [detailLoading, setDetailLoading] = useState(false)
  const [context, setContext] = useState<OperationAuditContext | null>(null)
  const [contextLoading, setContextLoading] = useState(false)
  const [contextError, setContextError] = useState(false)
  const [summary, setSummary] = useState<OperationAuditSummary | null>(null)
  const requestGuardRef = useRef(createLatestOperationAuditRequestGuard())
  const detailGuardRef = useRef(createLatestOperationAuditRequestGuard())
  const employeesGuardRef = useRef(createLatestOperationAuditRequestGuard())
  // 风险入口与细分、收银员下钻：状态驱动界面，ref 让查询函数立即读到最新值。
  const [risk, setRiskState] = useState<LegacyRiskFilter>(DEFAULT_RISK_FILTER)
  const riskRef = useRef<LegacyRiskFilter>(DEFAULT_RISK_FILTER)
  const [cashier, setCashierState] = useState<{ id: string; name: string } | null>(null)
  const cashierRef = useRef<{ id: string; name: string } | null>(null)
  const [view, setView] = useState<ResultView>('records')
  const viewRef = useRef<ResultView>('records')
  const [employeeSummary, setEmployeeSummary] = useState<OperationAuditEmployeeSummaryResult | null>(null)
  const [employeeLoading, setEmployeeLoading] = useState(false)
  const [employeeError, setEmployeeError] = useState<string | null>(null)
  const selectedOperationTypes = Form.useWatch('operationTypes', form)

  const visibleStoreOptions = useMemo(
    () =>
      // 店长的选项已限定为本人全部关联分店，管理员为全部启用分店，这里不再按可管理（主）分店过滤。
      storeOptions.map((option) => ({
        value: option.value,
        label: option.label && option.label !== option.value ? `${option.value} · ${option.label}` : option.value,
      })),
    [storeOptions],
  )
  const storeNames = useMemo(() => buildStoreNameMap(storeOptions), [storeOptions])

  useEffect(() => {
    if (managedStoreCodes !== null) {
      // 店长按会话中全部关联分店查看日志（不只主分店），与后端 GetAssignedStoreScopeAsync 一致，服务端仍会再次校验；
      // 直接用会话分店，避免依赖 Stores.View 全店列表权限。
      setStoreOptions(buildStoreOptionsFromUserStores(currentUser?.stores))
      return
    }

    let disposed = false
    void getActiveStores()
      .then((stores) => {
        if (!disposed) {
          setStoreOptions(stores)
        }
      })
      .catch((error) => {
        console.error(error)
        if (!disposed) {
          message.error(t('operationLogs.loadStoresFailed'))
        }
      })
    return () => {
      disposed = true
    }
  }, [currentUser?.stores, managedStoreCodeKey, t])

  // 当前表单、风险入口与收银员下钻组装成查询参数；查询范围最终仍由服务端按可管理门店收窄。
  const buildQuery = useCallback(
    (page: number, size: number, nextSortBy: OperationAuditSortField, nextSortOrder: OperationAuditTableSortOrder) => {
      const values = form.getFieldsValue()
      const [from, to] = values.timeRange ?? getDefaultTimeRange()
      return buildOperationAuditQuery({
        startUtc: from.toISOString(),
        endUtc: to.toISOString(),
        storeCode: '',
        storeCodes: values.storeCodes,
        cashierKeyword: values.cashierKeyword ?? '',
        cashierId: cashierRef.current?.id,
        deviceCode: values.deviceCode ?? '',
        deviceSystem: values.deviceSystem ?? '',
        operationType: '',
        operationTypes: values.operationTypes,
        outcome: values.outcome ?? '',
        productKeyword: values.productKeyword ?? '',
        orderGuid: values.orderGuid ?? '',
        keyword: values.keyword ?? '',
        page,
        pageSize: size,
        sortBy: nextSortBy,
        sortOrder: nextSortOrder === 'descend' ? 'desc' : 'asc',
        riskLens: riskRef.current.riskLens,
        ruleCodes: riskRef.current.ruleCodes,
        reviewStatus: riskRef.current.reviewStatus,
      })
    },
    [form],
  )

  const loadData = useCallback(
    async (
      nextPage = pageNumber,
      nextPageSize = pageSize,
      nextSortBy = sortBy,
      nextSortOrder = sortOrder,
    ) => {
      // 只有最新查询可提交结果，避免旧页码或旧排序请求晚到后覆盖当前表格。
      const requestId = requestGuardRef.current.begin()
      const query = buildQuery(nextPage, nextPageSize, nextSortBy, nextSortOrder)
      setLoading(true)
      setLoadError(false)
      try {
        // 列表与入口计数并行取；计数失败不影响列表显示。
        const [result, nextSummary] = await Promise.all([
          getOperationAudits(query),
          getOperationAuditSummary(query).catch((error) => {
            console.error(error)
            return null
          }),
        ])
        if (!requestGuardRef.current.isLatest(requestId)) return
        setData(result.items)
        setTotal(result.total)
        setPageNumber(result.pageNumber)
        setPageSize(result.pageSize)
        setSummary(nextSummary)
        // 打开中的详情换成最新数据（核查结论、命中规则可能已变）。
        setDetailRecord((current) => (current ? result.items.find((item) => item.eventId === current.eventId) ?? current : current))
      } catch (error) {
        if (!requestGuardRef.current.isLatest(requestId)) return
        console.error(error)
        setLoadError(true)
        message.error(t('operationLogs.loadFailed'))
      } finally {
        if (requestGuardRef.current.isLatest(requestId)) setLoading(false)
      }
    },
    [buildQuery, pageNumber, pageSize, sortBy, sortOrder, t],
  )

  // 按员工汇总只用分店、时间、设备与关键字等基础条件，后端忽略收银员、操作类型、结果与风险入口。
  const loadEmployees = useCallback(async () => {
    const requestId = employeesGuardRef.current.begin()
    setEmployeeLoading(true)
    setEmployeeError(null)
    try {
      const result = await getOperationAuditEmployeeSummary(buildQuery(1, DEFAULT_PAGE_SIZE, DEFAULT_OPERATION_AUDIT_SORT.sortBy, DEFAULT_OPERATION_AUDIT_SORT.sortOrder))
      if (employeesGuardRef.current.isLatest(requestId)) setEmployeeSummary(result)
    } catch (error) {
      if (!employeesGuardRef.current.isLatest(requestId)) return
      console.error(error)
      setEmployeeError(error instanceof Error && error.message ? error.message : t('legacyEmployeeLogs.employees.loadFailed'))
    } finally {
      if (employeesGuardRef.current.isLatest(requestId)) setEmployeeLoading(false)
    }
  }, [buildQuery, t])

  useEffect(() => {
    form.setFieldsValue({ timeRange: getDefaultTimeRange() })
    void loadData(
      1,
      DEFAULT_PAGE_SIZE,
      DEFAULT_OPERATION_AUDIT_SORT.sortBy,
      DEFAULT_OPERATION_AUDIT_SORT.sortOrder,
    )
    // 首次加载只执行一次，后续查询由用户或分页动作触发。
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [])

  const setRisk = (next: LegacyRiskFilter) => {
    riskRef.current = next
    setRiskState(next)
  }

  const setCashier = (next: { id: string; name: string } | null) => {
    cashierRef.current = next
    setCashierState(next)
  }

  const switchView = (next: ResultView) => {
    viewRef.current = next
    setView(next)
    if (next === 'employees') void loadEmployees()
  }

  const runQuery = () => {
    setPageNumber(1)
    void loadData(1, pageSize, sortBy, sortOrder)
    if (viewRef.current === 'employees') void loadEmployees()
  }

  // 切换入口时清掉上一个入口的细分（操作类型 / 规则 / 核查状态），并回到记录页。
  const changeLens = (lens: LegacyRiskLens) => {
    form.setFieldsValue({ operationTypes: undefined })
    setRisk({ riskLens: lens, ruleCodes: [], reviewStatus: 'all' })
    viewRef.current = 'records'
    setView('records')
    runQuery()
  }

  const changeOperations = (operationTypes: string[] | undefined) => {
    form.setFieldsValue({ operationTypes })
    runQuery()
  }

  const changeRule = (ruleCode: string | null) => {
    setRisk({ ...riskRef.current, ruleCodes: ruleCode ? [ruleCode] : [] })
    runQuery()
  }

  const changeReviewStatus = (reviewStatus: LegacyReviewStatus) => {
    setRisk({ ...riskRef.current, reviewStatus })
    runQuery()
  }

  // 员工汇总「查看明细」：只看该收银员，有异常停在异常入口，否则停在危险入口。
  const viewEmployee = useCallback((row: LegacyEmployeeLogEmployeeSummary) => {
    if (!row.employeeId) return
    setCashier({ id: row.employeeId, name: row.employeeName || row.employeeId })
    form.setFieldsValue({ operationTypes: undefined, cashierKeyword: undefined })
    setRisk({ riskLens: row.abnormalCount > 0 ? 'abnormal' : 'danger', ruleCodes: [], reviewStatus: 'all' })
    viewRef.current = 'records'
    setView('records')
    setPageNumber(1)
    void loadData(1, pageSize, sortBy, sortOrder)
  }, [form, loadData, pageSize, sortBy, sortOrder])

  const clearCashier = () => {
    setCashier(null)
    runQuery()
  }

  const handleReset = () => {
    const storeCodes = form.getFieldValue('storeCodes') as string[] | undefined
    form.resetFields()
    // 重置只清筛选条件，保留所选分店。
    form.setFieldsValue({ storeCodes, timeRange: getDefaultTimeRange() })
    setRisk(DEFAULT_RISK_FILTER)
    setCashier(null)
    if (viewRef.current === 'employees') void loadEmployees()
    setPageNumber(1)
    setPageSize(DEFAULT_PAGE_SIZE)
    setSortBy(DEFAULT_OPERATION_AUDIT_SORT.sortBy)
    setSortOrder(DEFAULT_OPERATION_AUDIT_SORT.sortOrder)
    void loadData(
      1,
      DEFAULT_PAGE_SIZE,
      DEFAULT_OPERATION_AUDIT_SORT.sortBy,
      DEFAULT_OPERATION_AUDIT_SORT.sortOrder,
    )
  }

  const handleQuery = runQuery

  const handleOpenDetail = async (record: OperationAuditListItem) => {
    // 列表行立即显示，详情（商品明细）与前后操作并行补齐。
    const requestId = detailGuardRef.current.begin()
    setDetailRecord(record)
    setDetail(null)
    setContext(null)
    setDetailLoading(true)
    setContextLoading(true)
    setContextError(false)
    const [detailResult, contextResult] = await Promise.allSettled([
      getOperationAuditDetail(record.eventId),
      // 命中异常的记录看更长的上下文（例如开钱箱前最近一次销售）。
      getOperationAuditContext(record.eventId, record.flags?.length ? 15 : undefined),
    ])
    if (!detailGuardRef.current.isLatest(requestId)) return
    if (detailResult.status === 'fulfilled') {
      setDetail(detailResult.value)
    } else {
      console.error(detailResult.reason)
      message.error(t('operationLogs.loadDetailFailed'))
    }
    if (contextResult.status === 'fulfilled') {
      setContext(contextResult.value)
    } else {
      console.error(contextResult.reason)
      setContextError(true)
    }
    setDetailLoading(false)
    setContextLoading(false)
  }

  const closeDetail = () => {
    detailGuardRef.current.begin()
    setDetailRecord(null)
    setDetail(null)
  }

  // 核查后：本地先更新这一行，再静默刷新当前页与计数；冲突时直接刷新。
  const handleReviewChanged = (record: OperationAuditListItem, review: LegacyEmployeeLogReview | null) => {
    if (review) {
      const patch = (item: OperationAuditListItem) => (item.eventId === record.eventId ? { ...item, review } : item)
      setDetailRecord((current) => (current ? patch(current) : current))
      setData((current) => current.map(patch))
    }
    void loadData(pageNumber, pageSize, sortBy, sortOrder)
    if (viewRef.current === 'employees') void loadEmployees()
  }

  const showCashierDay = (record: OperationAuditListItem) => {
    if (!record.cashierId) return
    const day = dayjs(record.occurredAtUtc)
    form.setFieldsValue({
      timeRange: [day.startOf('day'), day.endOf('day')],
      deviceCode: undefined,
      operationTypes: undefined,
      keyword: undefined,
      cashierKeyword: undefined,
    })
    setCashier({ id: record.cashierId, name: record.cashierName || record.cashierId })
    setRisk(DEFAULT_RISK_FILTER)
    closeDetail()
    runQuery()
  }

  const operationLabel = useCallback(
    (operationType: string) => {
      const key = OPERATION_TYPE_KEYS[operationType]
      return key ? t(key) : operationType
    },
    [t],
  )

  const outcomeLabel = useCallback(
    (outcome: OperationAuditOutcome) => t(`operationLogs.outcomes.${outcome.toLowerCase()}`),
    [t],
  )

  const outcomeTag = useCallback(
    (outcome: OperationAuditOutcome) => (
      <Tag color={getOutcomeColor(outcome)} style={{ marginInlineEnd: 0 }}>{outcomeLabel(outcome)}</Tag>
    ),
    [outcomeLabel],
  )

  const deviceSystemLabel = useCallback(
    (deviceSystem?: string | null) => {
      if (deviceSystem === 'Windows' || deviceSystem === 'iPadOS') {
        return t(`operationLogs.platforms.${deviceSystem}`)
      }
      return t('operationLogs.platforms.Unknown')
    },
    [t],
  )

  const columnDragSensors = useSensors(
    useSensor(PointerSensor, { activationConstraint: { distance: 4 } }),
    useSensor(KeyboardSensor, { coordinateGetter: sortableKeyboardCoordinates }),
  )

  const operationLogColumnLabels = useMemo<Record<OperationLogColumnKey, string>>(
    () => ({
      occurredAtUtc: t('operationLogs.columns.time'),
      storeCode: t('operationLogs.columns.store'),
      employee: t('operationLogs.columns.employee'),
      operationType: t('operationLogs.columns.operation'),
      products: t('operationLogs.columns.products'),
      amountDelta: t('operationLogs.columns.amountChange'),
      deviceCode: t('operationLogs.columns.device'),
      deviceSystem: t('operationLogs.columns.platform'),
      outcome: t('operationLogs.columns.outcome'),
    }),
    [t],
  )

  const dndAccessibility = useMemo(
    () => createOperationLogDndAccessibility(operationLogColumnLabels, {
      instructions: t('operationLogs.dnd.instructions'),
      unknownColumn: t('operationLogs.dnd.unknownColumn'),
      dragStart: (column) => t('operationLogs.dnd.dragStart', { column }),
      dragOver: (column, overColumn) =>
        t('operationLogs.dnd.dragOver', { column, overColumn }),
      dragOverNone: (column) => t('operationLogs.dnd.dragOverNone', { column }),
      dragEnd: (column, overColumn) =>
        t('operationLogs.dnd.dragEnd', { column, overColumn }),
      dragCancel: (column) => t('operationLogs.dnd.dragCancel', { column }),
    }),
    [operationLogColumnLabels, t],
  )

  const baseColumns = useMemo<ColumnsType<OperationAuditListItem>>(
    () => [
      {
        title: t('operationLogs.columns.time'),
        dataIndex: 'occurredAtUtc',
        key: 'occurredAtUtc',
        width: 175,
        sorter: true,
        sortOrder: sortBy === 'occurredAtUtc' ? sortOrder : null,
        render: (value: string) => dayjs(value).format('YYYY-MM-DD HH:mm:ss'),
      },
      {
        title: t('operationLogs.columns.store'),
        dataIndex: 'storeCode',
        key: 'storeCode',
        width: 150,
        sorter: true,
        sortOrder: sortBy === 'storeCode' ? sortOrder : null,
        render: (value: string) => (
          <Typography.Text ellipsis={{ tooltip: true }} style={{ maxWidth: 140 }}>{formatStoreLabel(value, storeNames)}</Typography.Text>
        ),
      },
      {
        title: t('operationLogs.columns.employee'),
        key: 'employee',
        width: 150,
        render: (_, record) => (
          <span style={WRAPPED_TABLE_CELL_STYLE}>
            {record.cashierName || record.cashierId || record.userGuid || '-'}
          </span>
        ),
      },
      {
        title: t('operationLogs.columns.operation'),
        dataIndex: 'operationType',
        key: 'operationType',
        width: 185,
        sorter: true,
        sortOrder: sortBy === 'operationType' ? sortOrder : null,
        render: (value: string) => operationLabel(value),
      },
      {
        title: t('operationLogs.columns.products'),
        key: 'products',
        minWidth: 220,
        render: (_, record) => (
          <PosProductSummary record={record} fallbackName={t('operationLogs.detail.productFallback')} imageSize={40} />
        ),
      },
      {
        title: t('operationLogs.columns.amountChange'),
        dataIndex: 'amountDelta',
        key: 'amountDelta',
        width: 120,
        align: 'right',
        sorter: true,
        sortOrder: sortBy === 'amountDelta' ? sortOrder : null,
        render: (_, record) => formatSignedMoney(record.amountDelta, record.currencyCode || 'AUD'),
      },
      {
        title: t('operationLogs.columns.device'),
        dataIndex: 'deviceCode',
        key: 'deviceCode',
        width: 125,
        sorter: true,
        sortOrder: sortBy === 'deviceCode' ? sortOrder : null,
        render: (value: string) => (
          <span style={WRAPPED_TABLE_CELL_STYLE}>{value || '-'}</span>
        ),
      },
      {
        title: t('operationLogs.columns.platform'),
        dataIndex: 'deviceSystem',
        key: 'deviceSystem',
        width: 105,
        render: (value: string | null | undefined) => (
          <span style={WRAPPED_TABLE_CELL_STYLE}>{deviceSystemLabel(value)}</span>
        ),
      },
      {
        title: t('operationLogs.columns.outcome'),
        dataIndex: 'outcome',
        key: 'outcome',
        width: 105,
        sorter: true,
        sortOrder: sortBy === 'outcome' ? sortOrder : null,
        render: (value: OperationAuditOutcome) => outcomeTag(value),
      },
      {
        title: t('legacyEmployeeLogs.pos.riskColumn'),
        key: 'risk',
        width: 190,
        // 与操作列一起固定在右侧：表格横向滚动时风险标签始终可见。
        fixed: 'right',
        render: (_, record) => {
          const review = record.review && record.review.result !== 'revoked' ? record.review : null
          if (!record.isDanger && !record.flags?.length) return null
          return (
            <div style={{ display: 'flex', flexWrap: 'wrap', gap: 4 }}>
              {record.isDanger ? <Tag color="error" icon={<WarningOutlined />} style={{ marginInlineEnd: 0 }}>{t('legacyEmployeeLogs.badges.danger')}</Tag> : null}
              {(record.flags ?? []).map((flag) => (
                <Tag key={flag.ruleCode} color="warning" icon={<AlertOutlined />} style={{ marginInlineEnd: 0 }}>
                  {t(`legacyEmployeeLogs.rules.${flag.ruleCode}.label`)}
                </Tag>
              ))}
              {review ? (
                <Tag color={review.result === 'followUp' ? 'purple' : 'default'} icon={review.result === 'followUp' ? <FlagOutlined /> : <CheckOutlined />} style={{ marginInlineEnd: 0 }}>
                  {t(review.result === 'followUp' ? 'legacyEmployeeLogs.badges.followUp' : 'legacyEmployeeLogs.badges.reviewed')}
                </Tag>
              ) : null}
            </div>
          )
        },
      },
      {
        title: t('common.action'),
        key: 'actions',
        width: 90,
        fixed: 'right',
        render: (_, record) => (
          <Button
            type="link"
            icon={<EyeOutlined />}
            onClick={(event) => {
              event.stopPropagation()
              void handleOpenDetail(record)
            }}
          >
            {t('common.view')}
          </Button>
        ),
      },
    ],
    // handleOpenDetail 只读 ref 与 setState，不随渲染变化影响列定义。
    // eslint-disable-next-line react-hooks/exhaustive-deps
    [deviceSystemLabel, operationLabel, outcomeTag, sortBy, sortOrder, storeNames, t],
  )

  const isColumnOrderCustomized = isOperationLogColumnOrderCustomized(columnOrder)

  const columns = useMemo<ColumnsType<OperationAuditListItem>>(() => {
    const fixedKeys = new Set(['risk', 'actions'])
    const businessColumnMap = new Map(
      baseColumns
        .filter((column) => !fixedKeys.has(String(column.key)))
        .map((column) => [String(column.key), column]),
    )
    const fixedColumns = baseColumns.filter((column) => fixedKeys.has(String(column.key)))
    const orderedBusinessColumns = columnOrder
      .map((key) => businessColumnMap.get(key))
      .filter((column): column is ColumnsType<OperationAuditListItem>[number] => Boolean(column))
      .map((column) => ({
        ...column,
        onHeaderCell: () => ({
          'data-column-key': String(column.key),
          'data-drag-label': t('operationLogs.dragColumn', { column: String(column.title) }),
        } as DraggableHeaderCellProps),
      }))

    return [...orderedBusinessColumns, ...fixedColumns]
  }, [baseColumns, columnOrder, t])

  const handleColumnDragEnd = ({ active, over }: DragEndEvent) => {
    if (!over || active.id === over.id) return
    setColumnOrder((current) => {
      const nextOrder = moveOperationLogColumnOrder(current, active.id, over.id)
      try {
        localStorage.setItem(OPERATION_LOG_COLUMN_ORDER_STORAGE_KEY, JSON.stringify(nextOrder))
      } catch {
        // localStorage 不可用时仍保留当前页面内的列顺序。
      }
      return nextOrder
    })
  }

  const resetColumnOrder = () => {
    setColumnOrder([...DEFAULT_OPERATION_LOG_COLUMN_ORDER])
    try {
      localStorage.removeItem(OPERATION_LOG_COLUMN_ORDER_STORAGE_KEY)
    } catch {
      // localStorage 不可用时仍恢复当前页面内的默认列顺序。
    }
    message.success(t('operationLogs.columnOrderReset'))
  }

  const handleTableChange: NonNullable<TableProps<OperationAuditListItem>['onChange']> = (
    pagination,
    _filters,
    sorter,
    extra,
  ) => {
    const nextSorter = Array.isArray(sorter) ? sorter[0] : sorter
    const nextState = resolveOperationAuditTableChange(
      { page: pageNumber, pageSize, sortBy, sortOrder },
      {
        action: extra.action === 'sort' ? 'sort' : 'paginate',
        page: pagination.current ?? pageNumber,
        pageSize: pagination.pageSize ?? pageSize,
        sortBy: nextSorter?.field,
        sortOrder: nextSorter?.order,
      },
    )

    setPageNumber(nextState.page)
    setPageSize(nextState.pageSize)
    setSortBy(nextState.sortBy)
    setSortOrder(nextState.sortOrder)
    // 将本次表格状态显式传入请求，避免 setState 异步导致请求仍使用旧排序。
    void loadData(
      nextState.page,
      nextState.pageSize,
      nextState.sortBy,
      nextState.sortOrder,
    )
  }

  const timeRangePresets: RangePickerProps['presets'] = [
    { label: t('operationLogs.presets.today'), value: [dayjs().startOf('day'), dayjs()] },
    { label: t('operationLogs.presets.last7Days'), value: [dayjs().subtract(7, 'day'), dayjs()] },
    { label: t('operationLogs.presets.last30Days'), value: [dayjs().subtract(30, 'day'), dayjs()] },
  ]
  const currentUserName = currentUser?.fullName || currentUser?.username || ''
  const lensName = t(`legacyEmployeeLogs.lens.${risk.riskLens}.title`)
  const dangerChips = POS_DANGER_KEYS.map((key) => {
    const expected = POS_DANGER_GROUP_OPERATIONS[key]
    const active = sameOperations(expected, selectedOperationTypes)
    return {
      key,
      label: t(`legacyEmployeeLogs.dangerGroupsPos.${key}`),
      active,
      onClick: () => changeOperations(active ? undefined : [...expected]),
    }
  })
  const legacySummaryShape: LegacyEmployeeLogEmployeeSummaryResult | null = employeeSummary
    ? { ...employeeSummary, employees: employeeSummary.employees.map(toLegacyEmployeeSummary) }
    : null

  return (
    <PageContainer
      title={header?.title ?? t('operationLogs.pageTitle')}
      subtitle={header?.subtitle ?? t('operationLogs.pageSubtitle')}
      extra={
        <Space wrap>
          {header?.switcher}
          <Button
            icon={<ReloadOutlined />}
            onClick={() => void loadData(pageNumber, pageSize, sortBy, sortOrder)}
          >
            {t('common.refresh')}
          </Button>
        </Space>
      }
    >
      <Space direction="vertical" size={16} style={{ width: '100%' }}>
        <Card>
          <Form
            form={form}
            layout="vertical"
            onFinish={handleQuery}
          >
            <Row gutter={16}>
              <Col xs={24} lg={8}>
                <Form.Item label={t('operationLogs.filters.timeRange')} name="timeRange">
                  <DatePicker.RangePicker
                    showTime
                    presets={timeRangePresets}
                    style={{ width: '100%' }}
                  />
                </Form.Item>
              </Col>
              <Col xs={24} sm={12} lg={4}>
                <Form.Item label={t('operationLogs.filters.store')} name="storeCodes">
                  <Select
                    mode="multiple"
                    allowClear
                    maxTagCount="responsive"
                    optionFilterProp="label"
                    options={visibleStoreOptions}
                    placeholder={t('legacyEmployeeLogs.pos.storesPlaceholder')}
                  />
                </Form.Item>
              </Col>
              <Col xs={24} sm={12} lg={4}>
                <Form.Item label={t('operationLogs.filters.employee')} name="cashierKeyword">
                  <Input allowClear placeholder={t('operationLogs.filters.employeePlaceholder')} />
                </Form.Item>
              </Col>
              <Col xs={24} sm={12} lg={4}>
                <Form.Item label={t('operationLogs.filters.device')} name="deviceCode">
                  <Input allowClear placeholder={t('operationLogs.filters.devicePlaceholder')} />
                </Form.Item>
              </Col>
              <Col xs={24} sm={12} lg={4}>
                <Form.Item label={t('operationLogs.filters.platform')} name="deviceSystem">
                  <Select
                    allowClear
                    options={OPERATION_AUDIT_DEVICE_SYSTEM_OPTIONS.map((value) => ({
                      value,
                      label: deviceSystemLabel(value),
                    }))}
                  />
                </Form.Item>
              </Col>
              <Col xs={24} sm={12} lg={4}>
                <Form.Item label={t('operationLogs.filters.outcome')} name="outcome">
                  <Select
                    allowClear
                    options={(['Succeeded', 'Denied', 'Failed'] as OperationAuditOutcome[]).map((value) => ({
                      value,
                      label: outcomeLabel(value),
                    }))}
                  />
                </Form.Item>
              </Col>
              <Col xs={24} sm={12} lg={6}>
                <Form.Item label={t('operationLogs.filters.operation')} name="operationTypes">
                  <Select
                    mode="multiple"
                    allowClear
                    maxTagCount="responsive"
                    optionFilterProp="label"
                    placeholder={t('legacyEmployeeLogs.pos.operationTypesPlaceholder')}
                    options={Object.keys(OPERATION_TYPE_KEYS).map((value) => ({
                      value,
                      label: operationLabel(value),
                    }))}
                  />
                </Form.Item>
              </Col>
              <Col xs={24} sm={12} lg={6}>
                <Form.Item label={t('operationLogs.filters.product')} name="productKeyword">
                  <Input allowClear placeholder={t('operationLogs.filters.productPlaceholder')} />
                </Form.Item>
              </Col>
              <Col xs={24} sm={12} lg={6}>
                <Form.Item label={t('operationLogs.filters.order')} name="orderGuid">
                  <Input allowClear placeholder={t('operationLogs.filters.orderPlaceholder')} />
                </Form.Item>
              </Col>
              <Col xs={24} sm={12} lg={6}>
                <Form.Item label={t('operationLogs.filters.keyword')} name="keyword">
                  <Input allowClear placeholder={t('operationLogs.filters.keywordPlaceholder')} />
                </Form.Item>
              </Col>
            </Row>
            <Space wrap>
              <Button type="primary" htmlType="submit" icon={<SearchOutlined />}>
                {t('common.query')}
              </Button>
              <Button onClick={handleReset}>{t('common.reset')}</Button>
              {cashier ? (
                <Tag closable color="processing" onClose={(event) => { event.preventDefault(); clearCashier() }}>
                  {t('legacyEmployeeLogs.pos.onlyCashier', { name: cashier.name })}
                </Tag>
              ) : null}
            </Space>
          </Form>
        </Card>

        {loadError ? (
          <Alert
            type="error"
            showIcon
            message={t('operationLogs.loadFailed')}
            action={(
              <Button onClick={() => void loadData(pageNumber, pageSize, sortBy, sortOrder)}>
                {t('operationLogs.retry')}
              </Button>
            )}
          />
        ) : null}

        {summary ? (
          <RiskLensBar
            variant="pos"
            risk={risk}
            operations={selectedOperationTypes}
            total={total}
            counts={[]}
            summary={summary}
            people={0}
            devices={0}
            allTotal={summary.total}
            allScope={(
              <Typography.Text type="secondary" style={{ fontSize: 12 }}>
                · {t('legacyEmployeeLogs.pos.allScope', { succeeded: summary.succeeded, denied: summary.denied, failed: summary.failed })}
              </Typography.Text>
            )}
            ruleCodes={POS_RULE_CODES}
            allChips={[]}
            dangerChips={dangerChips}
            onLensChange={changeLens}
            onOperationsChange={changeOperations}
            onRuleChange={changeRule}
            onReviewStatusChange={changeReviewStatus}
          />
        ) : null}

        <Card styles={{ body: { padding: view === 'records' ? undefined : 0 } }}>
          <Tabs
            activeKey={view}
            onChange={(key) => switchView(key as ResultView)}
            tabBarStyle={view === 'employees' ? { paddingInline: 16, marginBottom: 0 } : undefined}
            tabBarExtraContent={view === 'records' ? (
              <Space size={8}>
                <Typography.Text type="secondary" style={{ fontSize: 13 }}>
                  {`${lensName} · ${t('operationLogs.paginationTotal', { total })}`}
                </Typography.Text>
                {isColumnOrderCustomized ? (
                  <Button size="small" icon={<ReloadOutlined />} onClick={resetColumnOrder}>
                    {t('operationLogs.resetColumns')}
                  </Button>
                ) : null}
              </Space>
            ) : (
              <Typography.Text type="secondary" style={{ fontSize: 12 }}>{t('legacyEmployeeLogs.employees.hint')}</Typography.Text>
            )}
            items={[
              {
                key: 'records',
                label: t('legacyEmployeeLogs.tabs.records'),
                children: (
                  <DndContext
                    sensors={columnDragSensors}
                    collisionDetection={closestCenter}
                    onDragEnd={handleColumnDragEnd}
                    accessibility={dndAccessibility}
                  >
                    <SortableContext items={columnOrder} strategy={horizontalListSortingStrategy}>
                      <MeasuredTable<OperationAuditListItem> metricId="pos-admin.operation-logs.table-1"
                        rowKey="eventId"
                        loading={loading}
                        components={{ header: { cell: DraggableHeaderCell } }}
                        columns={columns}
                        dataSource={data}
                        scroll={{ x: 1600 }}
                        locale={{ emptyText: t('operationLogs.empty') }}
                        sortDirections={['descend', 'ascend', 'descend']}
                        pagination={{
                          current: pageNumber,
                          pageSize,
                          total,
                          showSizeChanger: true,
                          pageSizeOptions: [20, 50, 100, 200],
                          showTotal: (value) => t('operationLogs.paginationTotal', { total: value }),
                        }}
                        onChange={handleTableChange}
                        onRow={(record) => ({
                          onClick: () => void handleOpenDetail(record),
                          style: {
                            cursor: 'pointer',
                            background: detailRecord?.eventId === record.eventId ? token.colorPrimaryBg : undefined,
                          },
                        })}
                      />
                    </SortableContext>
                  </DndContext>
                ),
              },
              {
                key: 'employees',
                label: t('legacyEmployeeLogs.tabs.employees'),
                children: (
                  <EmployeeSummaryTable
                    data={legacySummaryShape}
                    loading={employeeLoading}
                    error={employeeError}
                    storeNames={storeNames}
                    metricId="pos-admin.operation-logs.table-3"
                    footnote={t('legacyEmployeeLogs.employees.footnotePos')}
                    onRetry={() => void loadEmployees()}
                    onViewEmployee={viewEmployee}
                  />
                ),
              },
            ]}
          />
        </Card>
      </Space>

      <Drawer
        title={t('operationLogs.detailTitle')}
        width="min(720px, 100vw)"
        open={Boolean(detailRecord)}
        onClose={closeDetail}
        destroyOnHidden
      >
        {detailRecord ? (
          <PosLogDetailPanel
            record={detailRecord}
            detail={detail}
            detailLoading={detailLoading}
            context={context}
            contextLoading={contextLoading}
            contextError={contextError}
            storeLabel={formatStoreLabel(detailRecord.storeCode, storeNames)}
            canReview={access.canReviewLegacyEmployeeLogs}
            canViewSystemLogs={Boolean(access.canViewSystemLogs)}
            currentUserName={currentUserName}
            operationLabel={operationLabel}
            outcomeTag={outcomeTag}
            deviceSystemLabel={deviceSystemLabel}
            onShowCashierDay={showCashierDay}
            onReviewChanged={handleReviewChanged}
          />
        ) : null}
      </Drawer>
    </PageContainer>
  )
}
