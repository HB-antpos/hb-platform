import {
  ArrowDownOutlined,
  CloudUploadOutlined,
  MoreOutlined,
  PlusOutlined,
  ReloadOutlined,
  SearchOutlined,
} from '@ant-design/icons'
import {
  Alert,
  Button,
  Card,
  Col,
  DatePicker,
  Dropdown,
  Form,
  Input,
  InputNumber,
  Modal,
  Row,
  Select,
  Skeleton,
  Space,
  Tooltip,
  message,
} from 'antd'
import type { MenuProps } from 'antd'
import type { ColumnsType, TablePaginationConfig } from 'antd/es/table'
import type { TFunction } from 'i18next'
import type { Dayjs } from 'dayjs'
import dayjs from 'dayjs'
import { useEffect, useLayoutEffect, useMemo, useRef, useState, type Key, type ReactNode } from 'react'
import { useTranslation } from 'react-i18next'
import { Link, useNavigate } from 'react-router-dom'
import PageContainer from '../../../components/PageContainer'
import ActiveFilterBar, { type ActiveFilterItem } from '../../../components/listToolbar/ActiveFilterBar'
import MoreFiltersButton from '../../../components/listToolbar/MoreFiltersButton'
import SelectionActionBar from '../../../components/listToolbar/SelectionActionBar'
import StatusPill, { type StatusPillTone } from '../../../components/listToolbar/StatusPill'
import StatusTabs from '../../../components/listToolbar/StatusTabs'
import { registerPageMessages } from '../../../i18n/registerPageMessages'
import {
  createContainer,
  getContainerList,
  pushContainersToHbSales,
  updateContainer,
} from '../../../services/containerService'
import { useAuthStore } from '../../../store/auth'
import type { ContainerMain, ContainerQueryRequest, CreateContainerRequest } from '../../../types/container'
import { createLatestRequestGuard, runLatestGuardedRequest } from '../../../utils/latestRequestGuard'
import { MeasuredTable } from '../../../components/MeasuredTable'
import {
  ARRIVAL_CALENDAR_PAGE_SIZE,
  CONTAINER_COUNT_BUCKETS,
  CONTAINER_STATUS_TAB_KEYS,
  CONTAINER_STATUS_TAB_STATUSES,
  DEFAULT_CONTAINER_STATUS_TAB,
  buildArrivalCalendar,
  buildStatusTabCounts,
  describeEtaHint,
  formatContainerDate,
  getIsoWeekNumber,
  getLoadRatePercent,
  isOpenContainerStatus,
  parseContainerDate,
  summarizeContainers,
  type ContainerCountBucket,
  type ContainerStatusTabKey,
} from './containersLogic'
import './containers.css'
import containersMessagesEn from './containersMessages.en.json'
import containersMessagesZh from './containersMessages.zh.json'

registerPageMessages({ zh: containersMessagesZh, en: containersMessagesEn })

type RangeValue = [Dayjs | null, Dayjs | null] | null
type ContainerColumnDateStartKey = 'loadingDateStart' | 'estimatedArrivalDateStart' | 'actualArrivalDateStart'
type ContainerColumnDateEndKey = 'loadingDateEnd' | 'estimatedArrivalDateEnd' | 'actualArrivalDateEnd'
type ContainerColumnNumberKey =
  | 'totalPiecesMin'
  | 'totalPiecesMax'
  | 'totalAmountMin'
  | 'totalAmountMax'
  | 'totalVolumeMin'
  | 'totalVolumeMax'

/**
 * 原列头里的条件（货柜编号、三种日期区间、件数/金额/体积区间）现收进「更多筛选」，
 * 仍随 getContainerList 发到服务端；状态改由页签决定，不再放在这里。
 */
interface ContainerColumnFilters {
  containerNumberFilter?: string
  loadingDateStart?: string
  loadingDateEnd?: string
  estimatedArrivalDateStart?: string
  estimatedArrivalDateEnd?: string
  actualArrivalDateStart?: string
  actualArrivalDateEnd?: string
  totalPiecesMin?: number
  totalPiecesMax?: number
  totalAmountMin?: number
  totalAmountMax?: number
  totalVolumeMin?: number
  totalVolumeMax?: number
}

interface LoadDataOptions {
  dateType?: string
  dateRange?: RangeValue
  itemNumberFilter?: string
  columnFilters?: ContainerColumnFilters
  statusTab?: ContainerStatusTabKey
}

interface PendingFirstPageRequest {
  options: LoadDataOptions
  resolve: () => void
}

interface AppliedContainerFilters {
  dateType: string
  dateRange: RangeValue
  itemNumberFilter: string
  columnFilters: ContainerColumnFilters
}

/** 页头副标题与到岸周历的数据：与列表筛选无关的全局口径。 */
interface ContainerOverviewState {
  loading: boolean
  error: boolean
  openContainers?: ContainerMain[]
  openTotal?: number
  allTotal?: number
}

const DATE_RANGE_FILTERS: Array<{
  startKey: ContainerColumnDateStartKey
  endKey: ContainerColumnDateEndKey
  labelKey: string
}> = [
  { startKey: 'loadingDateStart', endKey: 'loadingDateEnd', labelKey: 'warehouseUi.containers.moreLoadingDate' },
  { startKey: 'estimatedArrivalDateStart', endKey: 'estimatedArrivalDateEnd', labelKey: 'warehouseUi.containers.moreEstimatedArrival' },
  { startKey: 'actualArrivalDateStart', endKey: 'actualArrivalDateEnd', labelKey: 'warehouseUi.containers.moreActualArrival' },
]

const NUMBER_RANGE_FILTERS: Array<{
  minKey: ContainerColumnNumberKey
  maxKey: ContainerColumnNumberKey
  labelKey: string
}> = [
  { minKey: 'totalPiecesMin', maxKey: 'totalPiecesMax', labelKey: 'warehouseUi.containers.morePieces' },
  { minKey: 'totalAmountMin', maxKey: 'totalAmountMax', labelKey: 'warehouseUi.containers.moreAmount' },
  { minKey: 'totalVolumeMin', maxKey: 'totalVolumeMax', labelKey: 'warehouseUi.containers.moreVolume' },
]

function formatAmount(value?: number) {
  return value == null ? '--' : `¥${value.toLocaleString('zh-CN', { minimumFractionDigits: 2, maximumFractionDigits: 2 })}`
}

function formatNumber(value?: number, digits = 0) {
  return value == null ? '--' : value.toLocaleString('zh-CN', { maximumFractionDigits: digits, minimumFractionDigits: digits })
}

function formatCount(value: number) {
  return value.toLocaleString('en-US')
}

/** 状态只用颜色表达含义：已装柜蓝、运输中橙、已完成绿、已取消灰。 */
const containerStatusMeta: Record<number, { tone: StatusPillTone; labelKey: string }> = {
  0: { tone: 'blue', labelKey: 'loaded' },
  1: { tone: 'orange', labelKey: 'inTransit' },
  2: { tone: 'green', labelKey: 'completed' },
  7: { tone: 'gray', labelKey: 'cancelled' },
}

const containerStatusValues = [0, 1, 2, 7]

function itemKeyOf(record: ContainerMain) {
  return record.hguid || String(record.id)
}

function getStatusLabel(status: number | null | undefined, t: TFunction) {
  if (status == null) return t('containers.status.unknown')
  const item = containerStatusMeta[status]
  return item ? t(`containers.status.${item.labelKey}`) : t('containers.status.unknownWithCode', { status })
}

function getStatusPill(status: number | undefined, t: TFunction) {
  const item = status == null ? undefined : containerStatusMeta[status]
  return <StatusPill tone={item?.tone ?? 'gray'}>{getStatusLabel(status, t)}</StatusPill>
}

function getDateOptionLabel(value: string, t: TFunction) {
  const map: Record<string, string> = {
    预计到岸日期: 'containers.fields.estimatedArrivalDate',
    实际到货日期: 'containers.fields.actualArrivalDate',
    装柜日期: 'containers.fields.loadingDate',
  }
  return map[value] ? t(map[value]) : value
}

const dateTypeOptions = ['预计到岸日期', '实际到货日期', '装柜日期']

/** 列表查询的公共部分（不含状态与分页），计数请求复用同一份条件。 */
function buildContainerListQuery(filters: AppliedContainerFilters): ContainerQueryRequest {
  return {
    dateType: filters.dateType,
    startDate: filters.dateRange?.[0]?.format('YYYY-MM-DD'),
    endDate: filters.dateRange?.[1]?.format('YYYY-MM-DD'),
    itemNumberFilter: filters.itemNumberFilter || undefined,
    ...filters.columnFilters,
  }
}

function normalizeColumnFilters(filters: ContainerColumnFilters): ContainerColumnFilters {
  const next: ContainerColumnFilters = {}
  Object.entries(filters).forEach(([key, value]) => {
    if (typeof value === 'string' && value.trim()) {
      ;(next as Record<string, unknown>)[key] = value.trim()
    } else if (typeof value === 'number' && Number.isFinite(value)) {
      ;(next as Record<string, unknown>)[key] = value
    }
  })
  return next
}

function countActiveColumnFilters(filters: ContainerColumnFilters) {
  let count = filters.containerNumberFilter ? 1 : 0
  DATE_RANGE_FILTERS.forEach(({ startKey, endKey }) => {
    if (filters[startKey] || filters[endKey]) count += 1
  })
  NUMBER_RANGE_FILTERS.forEach(({ minKey, maxKey }) => {
    if (typeof filters[minKey] === 'number' || typeof filters[maxKey] === 'number') count += 1
  })
  return count
}

function getEstimatedArrivalDate(loadingDate?: Dayjs | null) {
  if (!loadingDate) return undefined

  // 预计到岸按装柜日期四周后计算，落在周末时顺延到下一个周一。
  let estimatedArrival = loadingDate.add(4, 'week')
  if (estimatedArrival.day() === 6) {
    estimatedArrival = estimatedArrival.add(2, 'day')
  }
  if (estimatedArrival.day() === 0) {
    estimatedArrival = estimatedArrival.add(1, 'day')
  }
  return estimatedArrival
}

interface CreateContainerModalProps {
  open: boolean
  loading: boolean
  onCancel: () => void
  onSubmit: (values: CreateContainerRequest) => Promise<void>
}

function CreateContainerModal({ open, loading, onCancel, onSubmit }: CreateContainerModalProps) {
  const { t } = useTranslation()
  const [form] = Form.useForm()
  const handleLoadingDateChange = (value: Dayjs | null) => {
    form.setFieldsValue({ 预计到岸日期: getEstimatedArrivalDate(value) })
  }

  return (
    <Modal
      title={t('containers.actions.createContainer')}
      open={open}
      width={640}
      confirmLoading={loading}
      okText={t('common.create')}
      cancelText={t('common.cancel')}
      destroyOnHidden
      onCancel={() => {
        form.resetFields()
        onCancel()
      }}
      onOk={async () => {
        const values = await form.validateFields()
        await onSubmit({
          货柜编号: values.货柜编号,
          装柜日期: values.装柜日期 ? dayjs(values.装柜日期).format('YYYY-MM-DD') : undefined,
          预计到岸日期: values.预计到岸日期 ? dayjs(values.预计到岸日期).format('YYYY-MM-DD') : undefined,
          汇率: values.汇率,
          运费: values.运费,
          备注: values.备注,
        })
        form.resetFields()
      }}
    >
      <Form form={form} layout="vertical" initialValues={{ 汇率: 4.7 }}>
        <Form.Item name="货柜编号" label={t('containers.fields.containerNumber')} rules={[{ required: true, message: t('containers.validation.enterContainerNumber') }]}>
          <Input placeholder={t('containers.validation.enterContainerNumber')} />
        </Form.Item>
        <Form.Item name="装柜日期" label={t('containers.fields.loadingDate')} rules={[{ required: true, message: t('containers.validation.selectLoadingDate') }]}>
          <DatePicker style={{ width: '100%' }} onChange={handleLoadingDateChange} />
        </Form.Item>
        <Form.Item name="预计到岸日期" label={t('containers.fields.estimatedArrivalDate')}>
          <DatePicker style={{ width: '100%' }} />
        </Form.Item>
        <Row gutter={12}>
          <Col span={12}>
            <Form.Item name="汇率" label={t('containers.fields.exchangeRate')}>
              <InputNumber min={0} precision={4} step={0.0001} style={{ width: '100%' }} />
            </Form.Item>
          </Col>
          <Col span={12}>
            <Form.Item name="运费" label={t('containers.fields.freightUsd')}>
              <InputNumber min={0} precision={2} step={100} style={{ width: '100%' }} />
            </Form.Item>
          </Col>
        </Row>
        <Form.Item name="备注" label={t('containers.fields.remark')}>
          <Input.TextArea rows={4} maxLength={500} showCount />
        </Form.Item>
      </Form>
    </Modal>
  )
}

export default function ContainersPage() {
  const { t } = useTranslation()
  const navigate = useNavigate()
  const access = useAuthStore((state) => state.access)
  const [loading, setLoading] = useState(false)
  const [containers, setContainers] = useState<ContainerMain[]>([])
  const [total, setTotal] = useState(0)
  const [page, setPage] = useState(1)
  const [pageSize, setPageSize] = useState(20)
  const [dateType, setDateType] = useState('预计到岸日期')
  const [dateRange, setDateRange] = useState<RangeValue>(null)
  // 输入框草稿与已生效货号分开：输入时防抖 300ms 再生效，计数请求只跟随已生效条件。
  const [keywordDraft, setKeywordDraft] = useState('')
  const [itemNumberFilter, setItemNumberFilter] = useState('')
  const [columnFilters, setColumnFilters] = useState<ContainerColumnFilters>({})
  const [statusTab, setStatusTab] = useState<ContainerStatusTabKey>(DEFAULT_CONTAINER_STATUS_TAB)
  const [tabCounts, setTabCounts] = useState<Record<ContainerStatusTabKey, number> | null>(null)
  const [countsVersion, setCountsVersion] = useState(0)
  const [overview, setOverview] = useState<ContainerOverviewState>({ loading: true, error: false })
  const [moreOpen, setMoreOpen] = useState(false)
  const [moreDraft, setMoreDraft] = useState<ContainerColumnFilters>({})
  const [selectedRowKeys, setSelectedRowKeys] = useState<Key[]>([])
  const [createOpen, setCreateOpen] = useState(false)
  const [createLoading, setCreateLoading] = useState(false)
  const [pushing, setPushing] = useState(false)
  const [statusUpdatingKeys, setStatusUpdatingKeys] = useState<string[]>([])
  const listRequestGuardRef = useRef(createLatestRequestGuard())
  const countsRequestGuardRef = useRef(createLatestRequestGuard())
  const overviewRequestGuardRef = useRef(createLatestRequestGuard())
  const keywordTimerRef = useRef<number | undefined>(undefined)
  const mountedRef = useRef(false)
  const latestLoadDataRef = useRef<(
    nextPage?: number,
    nextPageSize?: number,
    options?: LoadDataOptions,
  ) => Promise<void>>(async () => undefined)
  const pendingFirstPageRequestRef = useRef<PendingFirstPageRequest | null>(null)
  const activeFirstPageResolverRef = useRef<(() => void) | null>(null)
  const latestRequestFirstPageRef = useRef<(options?: LoadDataOptions) => Promise<void>>(async () => undefined)
  const latestApplyKeywordRef = useRef<(value: string) => void>(() => undefined)

  const today = dayjs().startOf('day')
  const todayKey = today.format('YYYY-MM-DD')

  const loadData = async (nextPage = page, nextPageSize = pageSize, options: LoadDataOptions = {}) => {
    if (!mountedRef.current) return

    const activeDateType = options.dateType ?? dateType
    const activeDateRange = Object.prototype.hasOwnProperty.call(options, 'dateRange') ? options.dateRange : dateRange
    const activeItemNumberFilter = options.itemNumberFilter ?? itemNumberFilter
    const activeColumnFilters = options.columnFilters ?? columnFilters
    const activeStatusTab = options.statusTab ?? statusTab

    await runLatestGuardedRequest(
      listRequestGuardRef.current,
      () =>
        getContainerList({
          ...buildContainerListQuery({
            dateType: activeDateType,
            dateRange: activeDateRange ?? null,
            itemNumberFilter: activeItemNumberFilter,
            columnFilters: activeColumnFilters,
          }),
          // 状态由页签决定：「未完成」= 已装柜 + 运输中，「全部」不过滤。
          statuses: CONTAINER_STATUS_TAB_STATUSES[activeStatusTab],
          page: nextPage,
          pageSize: nextPageSize,
        }),
      {
        onStart: () => setLoading(true),
        onSuccess: (result) => {
          setContainers(result.containers)
          setTotal(result.totalCount)
          setPage(result.page)
          setPageSize(result.pageSize)
          // 只保留当前结果里仍可见的勾选，避免把已筛掉、看不见的货柜发送到 HBSales。
          const visibleKeys = new Set(result.containers.map(itemKeyOf))
          setSelectedRowKeys((keys) => keys.filter((key) => visibleKeys.has(String(key))))
        },
        onError: (error) => {
          console.error(error)
          message.error(error instanceof Error ? error.message : t('containers.messages.loadListFailed'))
        },
        onSettled: () => setLoading(false),
      },
    )
  }

  const resolvePendingFirstPageRequest = () => {
    const pendingRequest = pendingFirstPageRequestRef.current
    pendingFirstPageRequestRef.current = null
    pendingRequest?.resolve()
  }

  const resolveActiveFirstPageRequest = (expectedResolver?: () => void) => {
    const activeResolver = activeFirstPageResolverRef.current
    if (!activeResolver || (expectedResolver && activeResolver !== expectedResolver)) return

    activeFirstPageResolverRef.current = null
    activeResolver()
  }

  const startFirstPageRequest = (options: LoadDataOptions, resolve: () => void) => {
    activeFirstPageResolverRef.current = resolve
    void latestLoadDataRef.current(1, pageSize, options).finally(() => {
      resolveActiveFirstPageRequest(resolve)
    })
  }

  const requestFirstPage = (options: LoadDataOptions = {}): Promise<void> => {
    if (!mountedRef.current) return Promise.resolve()

    // 新的第一页请求会同步结束被替代调用者的等待，但旧网络响应仍由 guard 丢弃。
    resolvePendingFirstPageRequest()
    resolveActiveFirstPageRequest()

    if (page === 1) {
      return new Promise((resolve) => {
        startFirstPageRequest(options, resolve)
      })
    }

    listRequestGuardRef.current.invalidate()
    return new Promise((resolve) => {
      pendingFirstPageRequestRef.current = { options, resolve }
      setPage(1)
    })
  }

  // 关键字防抖：输入停顿 300ms 后生效；回车或清空立即生效。
  const applyKeyword = (value: string) => {
    window.clearTimeout(keywordTimerRef.current)
    const nextKeyword = value.trim()
    if (nextKeyword === itemNumberFilter) return
    setItemNumberFilter(nextKeyword)
    void requestFirstPage({ itemNumberFilter: nextKeyword })
  }

  useLayoutEffect(() => {
    mountedRef.current = true

    return () => {
      mountedRef.current = false
      resolvePendingFirstPageRequest()
      resolveActiveFirstPageRequest()
      listRequestGuardRef.current.invalidate()
      countsRequestGuardRef.current.invalidate()
      overviewRequestGuardRef.current.invalidate()
      window.clearTimeout(keywordTimerRef.current)
    }
  }, [])

  useLayoutEffect(() => {
    latestLoadDataRef.current = loadData
    latestRequestFirstPageRef.current = requestFirstPage
    latestApplyKeywordRef.current = applyKeyword
  })

  useEffect(() => {
    const pendingRequest = pendingFirstPageRequestRef.current
    if (page === 1 && pendingRequest) {
      pendingFirstPageRequestRef.current = null
      startFirstPageRequest(pendingRequest.options, pendingRequest.resolve)
      return
    }

    resolvePendingFirstPageRequest()
    resolveActiveFirstPageRequest()
    void latestLoadDataRef.current()
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [page, pageSize])

  const handleCreate = async (values: CreateContainerRequest) => {
    setCreateLoading(true)
    try {
      await createContainer(values)
      message.success(t('containers.messages.createSuccess'))
      setCreateOpen(false)
      refreshSummaries()
      await latestRequestFirstPageRef.current()
    } catch (error) {
      console.error(error)
      message.error(error instanceof Error ? error.message : t('containers.messages.createFailed'))
    } finally {
      setCreateLoading(false)
    }
  }

  // 计数请求：每个状态发一次 pageSize=1 的请求取 total（带同样的其他筛选条件），
  // 只采纳最新一轮结果；任一失败则不显示计数，页签照常可用。
  const countsQuery = buildContainerListQuery({ dateType, dateRange, itemNumberFilter, columnFilters })
  // 没有日期区间时日期类型只影响排序，不影响计数，避免切换日期类型时白发计数请求。
  const countsQueryKey = JSON.stringify({ ...countsQuery, dateType: countsQuery.startDate ? countsQuery.dateType : undefined })

  useEffect(() => {
    const query = JSON.parse(countsQueryKey) as ContainerQueryRequest
    void runLatestGuardedRequest(
      countsRequestGuardRef.current,
      async () => {
        const entries = await Promise.all(
          CONTAINER_COUNT_BUCKETS.map(async (bucket) => {
            const result = await getContainerList({
              ...query,
              statuses: CONTAINER_STATUS_TAB_STATUSES[bucket],
              page: 1,
              pageSize: 1,
            })
            return [bucket, result.totalCount] as const
          }),
        )
        return buildStatusTabCounts(Object.fromEntries(entries) as Record<ContainerCountBucket, number>)
      },
      {
        onSuccess: (counts) => setTabCounts(counts),
        onError: (error) => {
          console.error(error)
          setTabCounts(null)
        },
      },
    )
  }, [countsQueryKey, countsVersion])

  // 到岸周历与页头副标题：一次拉取全部未完成货柜（按状态过滤，页长取足够大），再加一次只取总数的请求。
  const loadOverview = () => {
    void runLatestGuardedRequest(
      overviewRequestGuardRef.current,
      async () => {
        const [openResult, allResult] = await Promise.all([
          getContainerList({ statuses: CONTAINER_STATUS_TAB_STATUSES.open, page: 1, pageSize: ARRIVAL_CALENDAR_PAGE_SIZE }),
          getContainerList({ page: 1, pageSize: 1 }),
        ])
        return {
          openContainers: openResult.containers,
          openTotal: openResult.totalCount,
          allTotal: allResult.totalCount,
        }
      },
      {
        onStart: () => setOverview((current) => ({ ...current, loading: true, error: false })),
        onSuccess: (result) => setOverview({ loading: false, error: false, ...result }),
        onError: (error) => {
          console.error(error)
          setOverview((current) => ({ ...current, loading: false, error: true }))
        },
      },
    )
  }

  useEffect(() => {
    loadOverview()
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [])

  /** 新建、改状态、手动刷新后，页签计数、副标题和到岸周历一起重新统计。 */
  const refreshSummaries = () => {
    setCountsVersion((version) => version + 1)
    loadOverview()
  }

  const handleRefresh = () => {
    refreshSummaries()
    void latestLoadDataRef.current()
  }

  // 「从HQ同步」（HQ 货柜 → HBweb）已于 2026-09-29 停用；「推送到 HBSales」方向相反，继续保留。
  const handlePush = () => {
    if (!selectedRowKeys.length) {
      message.warning(t('containers.messages.selectContainersToPush'))
      return
    }
    Modal.confirm({
      title: t('containers.modals.pushTitle'),
      content: t('containers.modals.pushContent', { count: selectedRowKeys.length }),
      okText: t('containers.actions.confirmPush'),
      cancelText: t('common.cancel'),
      onOk: async () => {
        setPushing(true)
        try {
          const result = await pushContainersToHbSales(selectedRowKeys.map(String))
          const success = result.isSuccess ?? result.IsSuccess ?? true
          const msg = result.message ?? result.Message ?? t('containers.messages.pushComplete')
          success ? message.success(msg) : message.error(msg)
          setSelectedRowKeys([])
        } catch (error) {
          console.error(error)
          message.error(error instanceof Error ? error.message : t('containers.messages.pushFailed'))
        } finally {
          setPushing(false)
        }
      },
    })
  }

  const handleContainerStatusChange = async (record: ContainerMain, nextStatus: number) => {
    const recordKey = itemKeyOf(record)
    if (record.状态 === nextStatus || statusUpdatingKeys.includes(recordKey)) {
      return
    }

    if (!record.hguid) {
      message.error(t('containers.messages.missingContainerGuid'))
      return
    }

    const previousStatus = record.状态
    // 先更新当前行，让状态切换立即反馈；接口失败时再回滚原值。
    setStatusUpdatingKeys((keys) => (keys.includes(recordKey) ? keys : [...keys, recordKey]))
    setContainers((items) => items.map((item) => (itemKeyOf(item) === recordKey ? { ...item, 状态: nextStatus } : item)))

    try {
      await updateContainer(record.hguid, { 状态: nextStatus })
      message.success(t('containers.messages.statusUpdateSuccess'))
      refreshSummaries()
    } catch (error) {
      console.error(error)
      setContainers((items) => items.map((item) => (itemKeyOf(item) === recordKey ? { ...item, 状态: previousStatus } : item)))
      message.error(error instanceof Error ? error.message : t('containers.messages.statusUpdateFailed'))
    } finally {
      setStatusUpdatingKeys((keys) => keys.filter((key) => key !== recordKey))
    }
  }

  // 原先行内下拉一改就保存、没有确认；现在收进「⋯」菜单，选中目标状态后二次确认再保存。
  const confirmContainerStatusChange = (record: ContainerMain, nextStatus: number) => {
    Modal.confirm({
      title: t('warehouseUi.containers.changeStatusTitle', {
        number: record.货柜编号 || record.hguid,
        status: getStatusLabel(nextStatus, t),
      }),
      content: t('warehouseUi.containers.changeStatusContent', { current: getStatusLabel(record.状态, t) }),
      okText: t('warehouseUi.containers.changeStatusOk'),
      cancelText: t('common.cancel'),
      onOk: () => handleContainerStatusChange(record, nextStatus),
    })
  }

  const handleStatusTabChange = (key: ContainerStatusTabKey) => {
    setStatusTab(key)
    void requestFirstPage({ statusTab: key })
  }

  const handleDateTypeChange = (value: string) => {
    setDateType(value)
    // 日期类型同时决定服务端排序字段，没有日期区间时也要重新查询。
    void requestFirstPage({ dateType: value })
  }

  const handleDateRangeChange = (value: RangeValue) => {
    setDateRange(value)
    void requestFirstPage({ dateRange: value })
  }

  const handleKeywordChange = (value: string) => {
    setKeywordDraft(value)
    window.clearTimeout(keywordTimerRef.current)
    if (!value) {
      latestApplyKeywordRef.current('')
      return
    }
    keywordTimerRef.current = window.setTimeout(() => latestApplyKeywordRef.current(value), 300)
  }

  const applyColumnFilters = (filters: ContainerColumnFilters) => {
    const nextFilters = normalizeColumnFilters(filters)
    setColumnFilters(nextFilters)
    setMoreDraft(nextFilters)
    void requestFirstPage({ columnFilters: nextFilters })
  }

  const removeColumnFilters = (keys: Array<keyof ContainerColumnFilters>) => {
    const nextFilters = { ...columnFilters }
    keys.forEach((key) => {
      delete nextFilters[key]
    })
    applyColumnFilters(nextFilters)
  }

  const clearAllFilters = () => {
    window.clearTimeout(keywordTimerRef.current)
    setKeywordDraft('')
    setItemNumberFilter('')
    setDateRange(null)
    setColumnFilters({})
    setMoreDraft({})
    // 清空全部同时清掉「更多筛选」里的原列头条件，避免界面已清空但请求仍带旧条件。
    void requestFirstPage({ dateRange: null, itemNumberFilter: '', columnFilters: {} })
  }

  const formatRangeValue = (min?: string | number, max?: string | number) => {
    const hasMin = min !== undefined && min !== ''
    const hasMax = max !== undefined && max !== ''
    if (hasMin && hasMax) return t('warehouseUi.containers.rangeBetween', { min, max })
    if (hasMin) return t('warehouseUi.containers.rangeAtLeast', { value: min })
    return t('warehouseUi.containers.rangeAtMost', { value: max })
  }

  const activeFilterItems: ActiveFilterItem[] = []
  if (itemNumberFilter) {
    activeFilterItems.push({
      key: 'itemNumber',
      label: t('warehouseUi.containers.chipItemNumber'),
      value: itemNumberFilter,
      source: 'toolbar',
      onRemove: () => {
        setKeywordDraft('')
        applyKeyword('')
      },
    })
  }
  if (dateRange?.[0] || dateRange?.[1]) {
    activeFilterItems.push({
      key: 'dateRange',
      label: getDateOptionLabel(dateType, t),
      value: t('warehouseUi.containers.dateRangeChip', {
        start: dateRange?.[0]?.format('YYYY-MM-DD') ?? '',
        end: dateRange?.[1]?.format('YYYY-MM-DD') ?? '',
      }),
      source: 'toolbar',
      onRemove: () => handleDateRangeChange(null),
    })
  }
  if (columnFilters.containerNumberFilter) {
    activeFilterItems.push({
      key: 'containerNumber',
      label: t('warehouseUi.containers.moreContainerNumber'),
      value: columnFilters.containerNumberFilter,
      source: 'toolbar',
      onRemove: () => removeColumnFilters(['containerNumberFilter']),
    })
  }
  DATE_RANGE_FILTERS.forEach(({ startKey, endKey, labelKey }) => {
    if (!columnFilters[startKey] && !columnFilters[endKey]) return
    activeFilterItems.push({
      key: startKey,
      label: t(labelKey),
      value: formatRangeValue(columnFilters[startKey], columnFilters[endKey]),
      source: 'toolbar',
      onRemove: () => removeColumnFilters([startKey, endKey]),
    })
  })
  NUMBER_RANGE_FILTERS.forEach(({ minKey, maxKey, labelKey }) => {
    if (typeof columnFilters[minKey] !== 'number' && typeof columnFilters[maxKey] !== 'number') return
    activeFilterItems.push({
      key: minKey,
      label: t(labelKey),
      value: formatRangeValue(columnFilters[minKey], columnFilters[maxKey]),
      source: 'toolbar',
      onRemove: () => removeColumnFilters([minKey, maxKey]),
    })
  })

  const statusTabItems = CONTAINER_STATUS_TAB_KEYS.map((key) => ({
    key,
    label:
      key === 'open'
        ? t('warehouseUi.containers.tabOpen')
        : key === 'all'
          ? t('warehouseUi.containers.tabAll')
          : t(`containers.status.${key}`),
    count: tabCounts?.[key],
  }))

  const calendar = useMemo(
    () => (overview.openContainers ? buildArrivalCalendar(overview.openContainers, dayjs(todayKey)) : undefined),
    [overview.openContainers, todayKey],
  )

  const selectedKeySet = new Set(selectedRowKeys.map(String))
  const selectedTotals = summarizeContainers(containers.filter((item) => selectedKeySet.has(itemKeyOf(item))))
  const pageTotals = summarizeContainers(containers)

  const renderMoreFilterRange = (minKey: ContainerColumnNumberKey, maxKey: ContainerColumnNumberKey, label: string) => (
    <div className="wh-containers-more-field" key={minKey}>
      <span className="wh-containers-more-label">{label}</span>
      <Space.Compact style={{ width: '100%' }}>
        <InputNumber
          aria-label={`${label} ${t('containers.placeholders.minValue')}`}
          value={moreDraft[minKey]}
          placeholder={t('containers.placeholders.minValue')}
          controls={false}
          style={{ width: '50%' }}
          onChange={(value) => setMoreDraft((draft) => ({ ...draft, [minKey]: value ?? undefined }))}
        />
        <InputNumber
          aria-label={`${label} ${t('containers.placeholders.maxValue')}`}
          value={moreDraft[maxKey]}
          placeholder={t('containers.placeholders.maxValue')}
          controls={false}
          style={{ width: '50%' }}
          onChange={(value) => setMoreDraft((draft) => ({ ...draft, [maxKey]: value ?? undefined }))}
        />
      </Space.Compact>
    </div>
  )

  const renderMoreFilterDateRange = (startKey: ContainerColumnDateStartKey, endKey: ContainerColumnDateEndKey, label: string) => (
    <div className="wh-containers-more-field" key={startKey}>
      <span className="wh-containers-more-label">{label}</span>
      <DatePicker.RangePicker
        aria-label={label}
        allowEmpty={[true, true]}
        value={
          moreDraft[startKey] || moreDraft[endKey]
            ? [moreDraft[startKey] ? dayjs(moreDraft[startKey]) : null, moreDraft[endKey] ? dayjs(moreDraft[endKey]) : null]
            : null
        }
        onChange={(value) =>
          setMoreDraft((draft) => ({
            ...draft,
            [startKey]: value?.[0]?.format('YYYY-MM-DD'),
            [endKey]: value?.[1]?.format('YYYY-MM-DD'),
          }))
        }
      />
    </div>
  )

  const sortedColumnTitle = (field: string, label: string) =>
    dateType === field ? (
      <span>
        {label}
        <Tooltip title={t('warehouseUi.containers.sortedDescTitle', { field: label })}>
          <ArrowDownOutlined className="wh-containers-sort-hint" aria-label={t('warehouseUi.containers.sortedDescTitle', { field: label })} />
        </Tooltip>
      </span>
    ) : (
      label
    )

  const renderWeekLine = (value?: string) => {
    const date = parseContainerDate(value)
    return date ? <div className="wh-containers-sub">{t('warehouseUi.containers.weekLabel', { week: getIsoWeekNumber(date) })}</div> : null
  }

  const renderEtaCell = (value: string | undefined, record: ContainerMain) => {
    const formatted = formatContainerDate(value)
    if (!formatted) return '--'
    const hint = describeEtaHint(record, today)
    let hintNode: ReactNode = null
    if (hint.kind === 'overdue') {
      hintNode = <div className="wh-containers-eta-overdue">{t('warehouseUi.containers.etaOverdue', { days: hint.days })}</div>
    } else if (hint.kind === 'today') {
      hintNode = <div className="wh-containers-eta-today">{t('warehouseUi.containers.etaToday')}</div>
    } else if (hint.kind === 'soon') {
      hintNode = <div className="wh-containers-eta-soon">{t('warehouseUi.containers.etaSoon', { days: hint.days })}</div>
    } else if (hint.kind === 'upcoming') {
      hintNode = <div className="wh-containers-sub">{t('warehouseUi.containers.etaUpcoming', { week: hint.week, days: hint.days })}</div>
    } else if (hint.kind === 'week') {
      hintNode = <div className="wh-containers-sub">{t('warehouseUi.containers.weekLabel', { week: hint.week })}</div>
    }
    return (
      <div className="wh-containers-date">
        <div>{formatted}</div>
        {hintNode}
      </div>
    )
  }

  const columns: ColumnsType<ContainerMain> = [
    {
      title: t('containers.fields.containerNumber'),
      dataIndex: '货柜编号',
      key: 'containerNumber',
      width: 172,
      render: (text: string, record) => {
        const remark = record.备注?.trim()
        return (
          <div className="wh-containers-number-cell">
            {record.hguid ? (
              <Link className="wh-containers-number" to={`/warehouse/container/detail/${record.hguid}`}>
                {text || record.hguid}
              </Link>
            ) : (
              <span className="wh-containers-number">{text || '--'}</span>
            )}
            {/* 备注并入货柜编号第二行，原来单独一列常年只显示 -- */}
            {remark ? <div className="wh-containers-sub" title={remark}>{remark}</div> : null}
          </div>
        )
      },
    },
    {
      title: t('containers.fields.status'),
      dataIndex: '状态',
      key: 'status',
      width: 84,
      render: (status?: number) => getStatusPill(status, t),
    },
    {
      title: sortedColumnTitle('装柜日期', t('containers.fields.loadingDate')),
      dataIndex: '装柜日期',
      key: 'loadingDate',
      width: 92,
      render: (value?: string) =>
        formatContainerDate(value) ? (
          <div className="wh-containers-date">
            <div>{formatContainerDate(value)}</div>
            {renderWeekLine(value)}
          </div>
        ) : (
          '--'
        ),
    },
    {
      title: sortedColumnTitle('预计到岸日期', t('containers.fields.estimatedArrival')),
      dataIndex: '预计到岸日期',
      key: 'estimatedArrivalDate',
      width: 124,
      render: renderEtaCell,
    },
    {
      title: sortedColumnTitle('实际到货日期', t('containers.fields.actualArrival')),
      dataIndex: '实际到货日期',
      key: 'actualArrivalDate',
      width: 92,
      render: (value: string | undefined, record) => {
        const formatted = formatContainerDate(value)
        if (formatted) return <span className="wh-containers-date">{formatted}</span>
        // 未完成的货柜才算「未到货」；已完成/已取消但没录实际日期的照实显示 --。
        return isOpenContainerStatus(record.状态) ? (
          <span className="wh-containers-not-arrived">{t('warehouseUi.containers.notArrived')}</span>
        ) : (
          '--'
        )
      },
    },
    {
      title: t('warehouseUi.containers.columnPieces'),
      dataIndex: '合计件数',
      key: 'totalPieces',
      width: 72,
      align: 'right',
      render: (value?: number) => <span className="wh-containers-num">{formatNumber(value)}</span>,
    },
    {
      title: t('warehouseUi.containers.columnAmount'),
      dataIndex: '合计金额',
      key: 'totalAmount',
      width: 120,
      align: 'right',
      render: (value?: number) => <span className="wh-containers-num">{formatAmount(value)}</span>,
    },
    {
      title: t('warehouseUi.containers.columnVolumeLoad'),
      dataIndex: '总体积',
      key: 'totalVolume',
      width: 144,
      render: (value?: number) => {
        const rate = getLoadRatePercent(value)
        if (value == null) return '--'
        return (
          <div className="wh-containers-load" title={rate === undefined ? undefined : t('warehouseUi.containers.loadRateTitle', { rate })}>
            <div className="wh-containers-load-text">
              <span>{formatNumber(value, 2)} m³</span>
              {rate === undefined ? null : <span className="wh-containers-load-rate">{rate}%</span>}
            </div>
            <div className="wh-containers-load-track" aria-hidden="true">
              <div className="wh-containers-load-fill" style={{ width: `${Math.min(rate ?? 0, 100)}%` }} />
            </div>
          </div>
        )
      },
    },
    {
      title: t('common.action'),
      key: 'action',
      width: 128,
      fixed: 'right',
      align: 'right',
      render: (_, record) => {
        const recordKey = itemKeyOf(record)
        const canUpdateStatus =
          access.canEditContainer && Boolean(record.hguid) && record.状态 != null && Boolean(containerStatusMeta[record.状态])
        const statusMenu: MenuProps['items'] = canUpdateStatus
          ? [
              {
                type: 'group',
                key: 'changeStatus',
                label: t('warehouseUi.containers.changeStatusGroup'),
                children: containerStatusValues
                  .filter((value) => value !== record.状态)
                  .map((value) => ({
                    key: `status:${value}`,
                    label: t('warehouseUi.containers.changeStatusTo', { status: getStatusLabel(value, t) }),
                  })),
              },
            ]
          : []

        return (
          <div className="wh-containers-actions">
            <Button size="small" autoInsertSpace={false} disabled={!record.hguid} onClick={() => navigate(`/warehouse/container/detail/${record.hguid}`)}>
              {t('warehouseUi.containers.actionDetail')}
            </Button>
            {/* 配销数据入口：跳到隐藏子页（货柜配销数据），文案走 i18n */}
            <Button
              size="small"
              autoInsertSpace={false}
              disabled={!record.hguid}
              onClick={() => navigate(`/warehouse/container/allocation-sales/${record.hguid}`)}
            >
              {t('warehouseUi.containers.actionAllocation')}
            </Button>
            {canUpdateStatus ? (
              <Dropdown
                trigger={['click']}
                disabled={statusUpdatingKeys.includes(recordKey)}
                menu={{
                  items: statusMenu,
                  onClick: ({ key }) => confirmContainerStatusChange(record, Number(String(key).split(':')[1])),
                }}
              >
                <Button
                  size="small"
                  type="text"
                  icon={<MoreOutlined />}
                  loading={statusUpdatingKeys.includes(recordKey)}
                  aria-label={t('warehouseUi.containers.actionMore', { number: record.货柜编号 || record.hguid })}
                />
              </Dropdown>
            ) : null}
          </div>
        )
      },
    },
  ]

  const rowSelectionEnabled = access.canEditContainer
  const summaryOffset = rowSelectionEnabled ? 1 : 0

  const subtitle =
    overview.openTotal !== undefined && overview.allTotal !== undefined
      ? t('warehouseUi.containers.subtitle', { open: formatCount(overview.openTotal), total: formatCount(overview.allTotal) })
      : undefined

  const calendarTruncated =
    overview.openContainers !== undefined &&
    overview.openTotal !== undefined &&
    overview.openTotal > overview.openContainers.length

  return (
    <PageContainer
      compact
      title={t('containers.title')}
      subtitle={subtitle}
      extra={
        access.canCreateContainer ? (
          <Button type="primary" icon={<PlusOutlined />} onClick={() => setCreateOpen(true)}>
            {t('containers.actions.createContainer')}
          </Button>
        ) : null
      }
    >
      <div className="wh-containers-stack">
        <section className="wh-containers-calendar" aria-label={t('warehouseUi.containers.calendarTitle')}>
          <div className="wh-containers-calendar-head">
            <h2 className="wh-containers-calendar-title">{t('warehouseUi.containers.calendarTitle')}</h2>
            <span className="wh-containers-calendar-hint">{t('warehouseUi.containers.calendarHint')}</span>
            <span className="wh-containers-calendar-legend">
              <span className="wh-containers-legend-item">
                <span className="wh-containers-dot wh-containers-dot-in-transit" aria-hidden="true" />
                {t('containers.status.inTransit')}
              </span>
              <span className="wh-containers-legend-item">
                <span className="wh-containers-dot wh-containers-dot-loaded" aria-hidden="true" />
                {t('containers.status.loaded')}
              </span>
            </span>
          </div>
          {calendar ? (
            <div className="wh-containers-calendar-scroll">
              <div className="wh-containers-calendar-grid">
                {calendar.columns.map((column) => {
                  const columnClass =
                    column.kind === 'overdue'
                      ? ' wh-containers-week-overdue'
                      : column.isCurrentWeek
                        ? ' wh-containers-week-current'
                        : ''
                  const title =
                    column.kind === 'overdue'
                      ? t('warehouseUi.containers.calendarOverdue')
                      : column.isCurrentWeek
                        ? t('warehouseUi.containers.calendarThisWeek', { week: column.week })
                        : t('warehouseUi.containers.calendarWeek', { week: column.week })
                  return (
                    <div key={column.key} className={`wh-containers-week${columnClass}`}>
                      <div className="wh-containers-week-head">
                        <span className="wh-containers-week-title">{title}</span>
                        {column.start && column.end ? (
                          <span className="wh-containers-week-range">
                            {column.start.format('MM-DD')} ~ {column.end.format('MM-DD')}
                          </span>
                        ) : null}
                      </div>
                      {column.items.map(({ container, eta, volume }) => {
                        const number = container.货柜编号 || container.hguid
                        const content = (
                          <>
                            <span className="wh-containers-chip-number">
                              <span
                                className={`wh-containers-dot ${container.状态 === 1 ? 'wh-containers-dot-in-transit' : 'wh-containers-dot-loaded'}`}
                                aria-hidden="true"
                              />
                              {number}
                            </span>
                            <span className="wh-containers-chip-meta">
                              {eta.format('MM-DD')} · {formatNumber(volume, 1)} m³
                            </span>
                          </>
                        )
                        const chipTitle = `${t('warehouseUi.containers.calendarChipTitle', {
                          number,
                          date: eta.format('YYYY-MM-DD'),
                        })} · ${getStatusLabel(container.状态, t)}`
                        return container.hguid ? (
                          <Link
                            key={itemKeyOf(container)}
                            className="wh-containers-chip"
                            to={`/warehouse/container/detail/${container.hguid}`}
                            title={chipTitle}
                          >
                            {content}
                          </Link>
                        ) : (
                          <div key={itemKeyOf(container)} className="wh-containers-chip" title={chipTitle}>
                            {content}
                          </div>
                        )
                      })}
                      <span className="wh-containers-week-sum">
                        {column.items.length
                          ? t('warehouseUi.containers.calendarSum', {
                              count: column.items.length,
                              volume: formatNumber(column.totalVolume, 1),
                            })
                          : t('warehouseUi.containers.calendarEmpty')}
                      </span>
                    </div>
                  )
                })}
              </div>
            </div>
          ) : overview.error ? (
            <Alert
              type="warning"
              showIcon
              message={t('warehouseUi.containers.calendarLoadFailed')}
              action={
                <Button size="small" onClick={loadOverview}>
                  {t('warehouseUi.containers.calendarRetry')}
                </Button>
              }
            />
          ) : (
            <Skeleton active title={false} paragraph={{ rows: 3 }} />
          )}
          {calendar && (calendar.outsideCount > 0 || calendarTruncated) ? (
            <div className="wh-containers-calendar-foot">
              {calendar.outsideCount > 0 ? (
                <span>
                  {t('warehouseUi.containers.calendarOutside', {
                    count: calendar.outsideCount,
                    date: calendar.lastDay.format('MM-DD'),
                  })}
                </span>
              ) : null}
              {calendarTruncated ? (
                <span>{t('warehouseUi.containers.calendarTruncated', { count: overview.openContainers?.length ?? 0 })}</span>
              ) : null}
            </div>
          ) : null}
        </section>

        <Card className="wh-containers-list">
          <StatusTabs
            items={statusTabItems}
            activeKey={statusTab}
            onChange={handleStatusTabChange}
            ariaLabel={t('warehouseUi.containers.tabsAria')}
          />

          <div className="wh-containers-toolbar">
            <Input
              allowClear
              className="wh-containers-search"
              value={keywordDraft}
              placeholder={t('warehouseUi.containers.searchPlaceholder')}
              aria-label={t('warehouseUi.containers.searchAria')}
              prefix={<SearchOutlined />}
              onChange={(event) => handleKeywordChange(event.target.value)}
              onPressEnter={() => applyKeyword(keywordDraft)}
            />
            <Space.Compact>
              <Select
                className="wh-containers-date-type"
                aria-label={t('warehouseUi.containers.dateTypeAria')}
                value={dateType}
                options={dateTypeOptions.map((value) => ({ value, label: getDateOptionLabel(value, t) }))}
                onChange={handleDateTypeChange}
              />
              <DatePicker.RangePicker value={dateRange} onChange={handleDateRangeChange} />
            </Space.Compact>
            <MoreFiltersButton
              activeCount={countActiveColumnFilters(columnFilters)}
              open={moreOpen}
              onOpenChange={(open) => {
                // 打开时用已生效条件初始化草稿，关闭不应用的修改会被丢弃。
                if (open) setMoreDraft(columnFilters)
                setMoreOpen(open)
              }}
            >
              <div className="wh-containers-more-field">
                <span className="wh-containers-more-label">{t('warehouseUi.containers.moreContainerNumber')}</span>
                <Input
                  allowClear
                  aria-label={t('warehouseUi.containers.moreContainerNumber')}
                  placeholder={t('warehouseUi.containers.moreContainerNumberPlaceholder')}
                  value={moreDraft.containerNumberFilter ?? ''}
                  onChange={(event) => setMoreDraft((draft) => ({ ...draft, containerNumberFilter: event.target.value || undefined }))}
                  onPressEnter={() => {
                    setMoreOpen(false)
                    applyColumnFilters(moreDraft)
                  }}
                />
              </div>
              {DATE_RANGE_FILTERS.map(({ startKey, endKey, labelKey }) => renderMoreFilterDateRange(startKey, endKey, t(labelKey)))}
              {NUMBER_RANGE_FILTERS.map(({ minKey, maxKey, labelKey }) => renderMoreFilterRange(minKey, maxKey, t(labelKey)))}
              <div className="wh-containers-more-actions">
                <Button
                  size="small"
                  onClick={() => {
                    setMoreOpen(false)
                    applyColumnFilters({})
                  }}
                >
                  {t('containers.actions.resetColumnFilter')}
                </Button>
                <Button
                  size="small"
                  type="primary"
                  onClick={() => {
                    setMoreOpen(false)
                    applyColumnFilters(moreDraft)
                  }}
                >
                  {t('containers.actions.applyColumnFilter')}
                </Button>
              </div>
            </MoreFiltersButton>
            <span className="wh-containers-toolbar-spacer" />
            <Tooltip title={t('warehouseUi.containers.refresh')}>
              <Button icon={<ReloadOutlined />} aria-label={t('warehouseUi.containers.refresh')} onClick={handleRefresh} />
            </Tooltip>
          </div>

          {activeFilterItems.length ? <ActiveFilterBar items={activeFilterItems} onClearAll={clearAllFilters} /> : null}

          {rowSelectionEnabled ? (
            <SelectionActionBar selectedCount={selectedRowKeys.length} onClearSelection={() => setSelectedRowKeys([])}>
              <span className="wh-containers-selection-summary">
                {t('warehouseUi.containers.selectionSummary', {
                  pieces: formatNumber(selectedTotals.pieces),
                  volume: formatNumber(selectedTotals.volume, 2),
                })}
              </span>
              <Button
                size="small"
                icon={<CloudUploadOutlined />}
                loading={pushing}
                disabled={!selectedRowKeys.length}
                onClick={handlePush}
              >
                {t('containers.actions.pushToHbSales')}
              </Button>
            </SelectionActionBar>
          ) : null}

          <MeasuredTable metricId="warehouse.containers.table-1"
            className="wh-containers-table"
            rowKey={itemKeyOf}
            loading={loading}
            columns={columns}
            dataSource={containers}
            rowSelection={rowSelectionEnabled ? { selectedRowKeys, onChange: setSelectedRowKeys, columnWidth: 40 } : undefined}
            // 默认列宽合计约 1068px（含勾选列），1440 宽屏且出现纵向滚动条时也不需要横向滚动。
            scroll={{ x: 1068 }}
            summary={() =>
              containers.length ? (
                // 只有当前页数据，合计必须写明「本页合计」，不能当成筛选结果合计。
                <MeasuredTable.Summary.Row className="wh-containers-summary-row">
                  <MeasuredTable.Summary.Cell index={0} colSpan={summaryOffset + 5}>
                    <span className="wh-containers-summary-label">
                      {t('warehouseUi.containers.pageTotal', { count: pageTotals.count })}
                    </span>
                  </MeasuredTable.Summary.Cell>
                  <MeasuredTable.Summary.Cell index={summaryOffset + 5} align="right">
                    <span className="wh-containers-summary-value">{formatNumber(pageTotals.pieces)}</span>
                  </MeasuredTable.Summary.Cell>
                  <MeasuredTable.Summary.Cell index={summaryOffset + 6} align="right">
                    <span className="wh-containers-summary-value">{formatAmount(pageTotals.amount)}</span>
                  </MeasuredTable.Summary.Cell>
                  <MeasuredTable.Summary.Cell index={summaryOffset + 7}>
                    <span className="wh-containers-summary-value">{formatNumber(pageTotals.volume, 2)} m³</span>
                  </MeasuredTable.Summary.Cell>
                  <MeasuredTable.Summary.Cell index={summaryOffset + 8} />
                </MeasuredTable.Summary.Row>
              ) : null
            }
            pagination={{
              current: page,
              pageSize,
              total,
              showSizeChanger: true,
              showTotal: (value) => t('warehouseUi.containers.paginationTotal', { count: value }),
              onChange: (nextPage: number, nextPageSize: number) => {
                setPage(nextPage)
                setPageSize(nextPageSize)
              },
            } satisfies TablePaginationConfig}
          />
        </Card>
      </div>
      <CreateContainerModal open={createOpen} loading={createLoading} onCancel={() => setCreateOpen(false)} onSubmit={handleCreate} />
    </PageContainer>
  )
}
