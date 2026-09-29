import {
  Alert,
  Button,
  Card,
  Col,
  Collapse,
  DatePicker,
  Descriptions,
  Drawer,
  Form,
  InputNumber,
  Modal,
  Row,
  Select,
  Space,
  Statistic,
  Switch,
  Tabs,
  Tag,
  Typography,
  message,
} from 'antd'
import type { ColumnsType } from 'antd/es/table'
import { ReloadOutlined, SearchOutlined } from '@ant-design/icons'
import dayjs from 'dayjs'
import type { Dayjs } from 'dayjs'
import { useEffect, useLayoutEffect, useMemo, useRef, useState } from 'react'
import PageContainer from '../../../components/PageContainer'
import ProductStatisticsPage from '../../ExecutiveSalesIntelligence/ProductStatistics'
import {
  batchFullRefreshConcurrent,
  batchUpdateDailyStatistics,
  batchUpdateHourlyStatistics,
  batchUpdateStoreStatistics,
  batchUpdateStoreSupplierStatistics,
  batchUpdateSupplierStatistics,
  getScheduledStatisticsActionErrorMessage,
  isScheduledStatisticsJobFailure,
  triggerDailyStatistics,
  triggerFullRefreshCurrentDay,
  triggerFullRefreshPreviousAndCurrentDay,
  triggerStoreStatistics,
  triggerStoreSupplierStatistics,
  triggerSupplierStatistics,
  type ScheduledStatisticsJobResult,
} from '../../../services/scheduledStatisticsService'
import {
  getDailyStatisticsAlignment,
  recalculateDailyStatisticsAlignment,
  type DailyStatisticsAlignmentResponse,
  type DailyStatisticsAlignmentRow,
  type DailyStatisticsAlignmentTableDetail,
} from '../../../services/salesStatisticsManagementService'
import {
  getScheduledTaskDetail,
  getScheduledTaskList,
  retryScheduledTask,
} from '../../../services/scheduledTaskRetryService'
import {
  getScheduledTaskRuntimeControl,
  updateScheduledTaskRuntimeControl,
} from '../../../services/scheduledTaskRuntimeControlService'
import { getActiveStores } from '../../../services/storeService'
import { getActiveLocalSuppliers } from '../../../services/localSupplierService'
import { getActiveChinaSuppliers } from '../../../services/chinaSupplierService'
import { useAuthStore } from '../../../store/auth'
import type { ScheduledTaskRuntimeControlStatus } from '../../../types/scheduledTaskRuntimeControl'
import type { ScheduledTaskListQuery, ScheduledTaskLogItem } from '../../../types/scheduledTaskRetry'
import {
  createLatestRequestGuard,
  runLatestGuardedRequest,
} from '../../../utils/latestRequestGuard'
import { MeasuredTable } from '../../../components/MeasuredTable'

const { RangePicker } = DatePicker

type DateRangeValue = [Dayjs, Dayjs] | null
type ScopeTaskKey = 'store' | 'supplier' | 'storeSupplier'
type OtherTaskKey = 'daily' | 'dailyRange' | 'hourly' | 'fullCurrentDay' | 'fullPreviousAndCurrent' | 'fullRange'

interface ScopeTaskFormValues {
  date?: Dayjs
  dateRange?: DateRangeValue
  branchCodes?: string[]
  supplierCodes?: string[]
}

interface OtherTaskFormValues {
  date?: Dayjs
  dateRange?: DateRangeValue
  hour?: number
  maxConcurrency?: number
}

interface TaskLogQueryValues {
  taskType?: string
  status?: string
  dateRange?: DateRangeValue
}

type DesiredTaskLogQuery = ScheduledTaskListQuery & {
  pageNumber: number
  pageSize: number
}

const taskTypeOptions = [
  { label: '分店统计', value: 'UpdateStoreStatistics' },
  { label: '分店统计批量', value: 'UpdateStoreStatisticsBatch' },
  { label: '供应商统计', value: 'UpdateSupplierStatistics' },
  { label: '供应商统计批量', value: 'UpdateSupplierStatisticsBatch' },
  { label: '门店供应商统计', value: 'UpdateStoreSupplierStatistics' },
  { label: '门店供应商统计批量', value: 'UpdateStoreSupplierStatisticsBatch' },
  { label: '商品/分店每日统计', value: 'UpdateProductStoreDailyStatistics' },
  { label: '商品/分店每日统计批量', value: 'UpdateProductStoreDailyStatisticsBatch' },
  { label: '每日统计', value: 'UpdateDailyStatistics' },
  { label: '每日统计批量', value: 'UpdateDailyStatisticsBatch' },
  { label: '分时统计批量', value: 'UpdateHourlyStatisticsBatch' },
  { label: '全量刷新前一天', value: 'FullRefreshPreviousDay' },
  { label: '全量刷新当天', value: 'FullRefreshCurrentDay' },
  { label: '并发全量刷新', value: 'BatchFullRefreshConcurrent' },
  { label: '数据对齐后台补算', value: 'RecalculateDailyStatisticsAlignment' },
  { label: '供应商分类每晚重新归类', value: 'ResolveLocalSupplierCategories' },
]

const taskStatusOptions = [
  { label: '全部状态', value: '' },
  { label: '运行中', value: 'Running' },
  { label: '成功', value: 'Success' },
  { label: '失败', value: 'Failed' },
  { label: '已取消', value: 'Cancelled' },
]

function formatDate(value?: Dayjs) {
  return value?.format('YYYY-MM-DD') ?? ''
}

function formatDateTime(value?: string) {
  if (!value) {
    return '--'
  }
  const parsed = dayjs(value)
  return parsed.isValid() ? parsed.format('YYYY-MM-DD HH:mm:ss') : value
}

function getStatusColor(status?: string) {
  switch ((status ?? '').toLowerCase()) {
    case 'success':
    case 'completed':
      return 'green'
    case 'running':
      return 'processing'
    case 'failed':
      return 'red'
    case 'cancelled':
      return 'default'
    default:
      return 'blue'
  }
}

function formatParameters(value?: Record<string, unknown> | string) {
  if (!value) {
    return '--'
  }
  if (typeof value === 'string') {
    return value
  }
  return JSON.stringify(value, null, 2)
}

function buildSubmitMessage(result: ScheduledStatisticsJobResult, fallback: string) {
  const parts = [result.message || fallback]
  if (typeof result.processedDays === 'number' && typeof result.totalDays === 'number') {
    parts.push(`已处理 ${result.processedDays}/${result.totalDays} 天`)
  }
  if (result.failedDates?.length) {
    parts.push(`失败日期：${result.failedDates.join(', ')}`)
  }
  if (result.skippedDates?.length) {
    parts.push(`跳过日期：${result.skippedDates.join(', ')}`)
  }
  if (result.jobId) {
    parts.push(`任务ID：${result.jobId}`)
  }
  return parts.join('；')
}

function getDateRangePayload(dateRange?: DateRangeValue) {
  if (!dateRange?.[0] || !dateRange?.[1]) {
    return null
  }
  return {
    startDate: dateRange[0].format('YYYY-MM-DD'),
    endDate: dateRange[1].format('YYYY-MM-DD'),
  }
}

function formatDateOnly(value?: string) {
  if (!value) {
    return '--'
  }
  const parsed = dayjs(value)
  return parsed.isValid() ? parsed.format('YYYY-MM-DD') : value
}

function formatMoney(value?: number) {
  return typeof value === 'number' && Number.isFinite(value)
    ? value.toLocaleString(undefined, { minimumFractionDigits: 2, maximumFractionDigits: 2 })
    : '--'
}

function getAlignmentStatusColor(status?: string) {
  switch ((status ?? '').toLowerCase()) {
    case 'aligned':
      return 'green'
    case 'running':
      return 'processing'
    case 'stale':
      return 'orange'
    case 'mismatch':
      return 'volcano'
    case 'missing':
    case 'failed':
      return 'red'
    default:
      return 'default'
  }
}

function isAlignmentAbnormal(status?: string) {
  return (status ?? '').toLowerCase() !== 'aligned'
}

function DailyAlignmentPanel({
  canRecalculate,
  onAfterRecalculate,
}: {
  canRecalculate: boolean
  onAfterRecalculate?: () => void
}) {
  const [dateRange, setDateRange] = useState<DateRangeValue>([dayjs().subtract(29, 'day'), dayjs()])
  const [onlyAbnormal, setOnlyAbnormal] = useState(false)
  const [maxConcurrency, setMaxConcurrency] = useState(3)
  const [loading, setLoading] = useState(false)
  const [recalculating, setRecalculating] = useState(false)
  const [alignment, setAlignment] = useState<DailyStatisticsAlignmentResponse | null>(null)

  const loadAlignment = async () => {
    const payload = getDateRangePayload(dateRange)
    if (!payload) {
      message.warning('请选择日期范围')
      return
    }

    setLoading(true)
    try {
      setAlignment(await getDailyStatisticsAlignment(payload))
    } catch (error) {
      console.error(error)
      message.error('加载数据对齐状态失败')
    } finally {
      setLoading(false)
    }
  }

  useEffect(() => {
    void loadAlignment()
  }, [])

  const visibleRows = useMemo(() => {
    const rows = alignment?.rows ?? []
    return onlyAbnormal
      ? rows.filter((row) => isAlignmentAbnormal(row.overallStatus))
      : rows
  }, [alignment, onlyAbnormal])

  const abnormalDates = useMemo(() => {
    return (alignment?.rows ?? [])
      .filter((row) => isAlignmentAbnormal(row.overallStatus) && row.overallStatus !== 'Running')
      .map((row) => formatDateOnly(row.date))
  }, [alignment])

  const handleRecalculateAbnormal = () => {
    if (!canRecalculate) {
      message.warning('补算统计需要管理员角色')
      return
    }
    if (!abnormalDates.length) {
      message.info('当前没有需要补算的异常日期')
      return
    }

    Modal.confirm({
      title: `确认提交 ${abnormalDates.length} 天异常统计后台补算？`,
      content: '提交后会在后台按日期抢占数据库租约，运行中的日期会自动跳过。',
      onOk: async () => {
        setRecalculating(true)
        try {
          const result = await recalculateDailyStatisticsAlignment({
            dates: abnormalDates,
            maxConcurrency,
          })
          if (result.success === false || result.failedDates.length) {
            const failedDateText = result.failedDates.length ? `：${result.failedDates.join(', ')}` : ''
            message.error(`${result.message ?? '后台补算提交失败'}${failedDateText}`)
          } else {
            message.success(result.message ?? '后台补算任务已提交')
          }
          await loadAlignment()
          onAfterRecalculate?.()
        } catch (error) {
          console.error(error)
          message.error('提交后台补算失败')
        } finally {
          setRecalculating(false)
        }
      },
    })
  }

  const detailColumns: ColumnsType<DailyStatisticsAlignmentTableDetail> = [
    {
      title: '统计表',
      dataIndex: 'displayName',
      width: 180,
      render: (value, record) => (
        <Space direction="vertical" size={0}>
          <Typography.Text>{value}</Typography.Text>
          <Typography.Text type="secondary">{record.tableName}</Typography.Text>
        </Space>
      ),
    },
    {
      title: '状态',
      dataIndex: 'status',
      width: 110,
      render: (value) => <Tag color={getAlignmentStatusColor(value)}>{value}</Tag>,
    },
    {
      title: '行数',
      dataIndex: 'rowCount',
      width: 90,
    },
    {
      title: '金额',
      dataIndex: 'totalAmount',
      width: 120,
      render: (value) => formatMoney(value),
    },
    {
      title: '数量',
      dataIndex: 'totalQuantity',
      width: 100,
    },
    {
      title: '订单数',
      dataIndex: 'orderCount',
      width: 100,
    },
    {
      title: '金额差异',
      dataIndex: 'amountDifference',
      width: 120,
      render: (value) => formatMoney(value),
    },
    {
      title: '数量差异',
      dataIndex: 'quantityDifference',
      width: 100,
    },
    {
      title: '水位',
      dataIndex: 'sourceWatermark',
      width: 170,
      render: formatDateTime,
    },
    {
      title: '最后统计',
      dataIndex: 'lastAggregatedAtUtc',
      width: 170,
      render: formatDateTime,
    },
    {
      title: '失败原因',
      dataIndex: 'errorMessage',
      render: (value) => value || '--',
    },
    {
      title: '具体原因',
      dataIndex: 'reason',
      width: 220,
      render: (value) => value || '--',
    },
    {
      title: '补救措施',
      dataIndex: 'remediation',
      width: 260,
      render: (value) => value || '--',
    },
  ]

  const columns: ColumnsType<DailyStatisticsAlignmentRow> = [
    {
      title: '日期',
      dataIndex: 'date',
      width: 130,
      render: formatDateOnly,
    },
    {
      title: '总体状态',
      dataIndex: 'overallStatus',
      width: 120,
      render: (value) => <Tag color={getAlignmentStatusColor(value)}>{value}</Tag>,
    },
    {
      title: '基准金额',
      dataIndex: 'baselineAmount',
      width: 130,
      render: (value) => formatMoney(value),
    },
    {
      title: '基准数量',
      dataIndex: 'baselineQuantity',
      width: 110,
    },
    {
      title: '订单数',
      dataIndex: 'baselineOrderCount',
      width: 100,
    },
    {
      title: '异常表',
      dataIndex: 'abnormalTables',
      render: (tables: string[]) => tables.length
        ? (
          <Space wrap size={[4, 4]}>
            {tables.map((table) => <Tag key={table}>{table}</Tag>)}
          </Space>
        )
        : '--',
    },
    {
      title: '原因',
      dataIndex: 'reason',
      width: 220,
      render: (value) => value || '--',
    },
    {
      title: '补救措施',
      dataIndex: 'remediation',
      width: 260,
      render: (value) => value || '--',
    },
    {
      title: 'POSM水位',
      dataIndex: 'latestSourceWatermark',
      width: 170,
      render: formatDateTime,
    },
    {
      title: '最后检查',
      dataIndex: 'lastCheckedAtUtc',
      width: 170,
      render: formatDateTime,
    },
  ]

  return (
    <Space direction="vertical" size={16} style={{ width: '100%' }}>
      <Form layout="vertical">
        <Row gutter={16} align="bottom">
          <Col xs={24} md={8}>
            <Form.Item label="日期范围">
              <RangePicker
                value={dateRange}
                style={{ width: '100%' }}
                allowClear={false}
                onChange={(value) => setDateRange(value as DateRangeValue)}
              />
            </Form.Item>
          </Col>
          <Col xs={12} md={4}>
            <Form.Item label="只看异常">
              <Switch checked={onlyAbnormal} onChange={setOnlyAbnormal} />
            </Form.Item>
          </Col>
          <Col xs={12} md={4}>
            <Form.Item label="补算并发">
              <InputNumber min={1} max={10} value={maxConcurrency} onChange={(value) => setMaxConcurrency(value ?? 3)} />
            </Form.Item>
          </Col>
          <Col xs={24} md={8}>
            <Form.Item label=" ">
              <Space wrap>
                <Button type="primary" icon={<SearchOutlined />} loading={loading} onClick={() => void loadAlignment()}>
                  查询
                </Button>
                <Button icon={<ReloadOutlined />} loading={loading} onClick={() => void loadAlignment()}>
                  刷新
                </Button>
                <Button
                  danger
                  disabled={!canRecalculate}
                  loading={recalculating}
                  onClick={handleRecalculateAbnormal}
                >
                  后台补算异常日期
                </Button>
              </Space>
            </Form.Item>
          </Col>
        </Row>
      </Form>

      <Row gutter={[16, 16]}>
        <Col xs={12} md={5}>
          <div style={{ border: '1px solid #f0f0f0', borderRadius: 6, padding: 12 }}>
            <Statistic title="已对齐天数" value={alignment?.overview.alignedDays ?? 0} />
          </div>
        </Col>
        <Col xs={12} md={5}>
          <div style={{ border: '1px solid #f0f0f0', borderRadius: 6, padding: 12 }}>
            <Statistic title="异常天数" value={alignment?.overview.abnormalDays ?? 0} valueStyle={{ color: '#cf1322' }} />
          </div>
        </Col>
        <Col xs={12} md={5}>
          <div style={{ border: '1px solid #f0f0f0', borderRadius: 6, padding: 12 }}>
            <Statistic title="缺失表数" value={alignment?.overview.missingTableCount ?? 0} />
          </div>
        </Col>
        <Col xs={12} md={5}>
          <div style={{ border: '1px solid #f0f0f0', borderRadius: 6, padding: 12 }}>
            <Statistic title="最大金额差异" value={alignment?.overview.maxAmountDifference ?? 0} precision={2} />
          </div>
        </Col>
        <Col xs={24} md={4}>
          <div style={{ border: '1px solid #f0f0f0', borderRadius: 6, padding: 12, minHeight: 75 }}>
            <Typography.Text type="secondary">最新 POSM 水位</Typography.Text>
            <Typography.Paragraph style={{ marginBottom: 0 }}>
              {formatDateTime(alignment?.overview.latestSourceWatermark)}
            </Typography.Paragraph>
          </div>
        </Col>
      </Row>

      <MeasuredTable<DailyStatisticsAlignmentRow> metricId="system.scheduled-statistics.table-1"
        rowKey={(row) => formatDateOnly(row.date)}
        loading={loading}
        columns={columns}
        dataSource={visibleRows}
        scroll={{ x: 1700 }}
        expandable={{
          expandedRowRender: (row) => (
            <MeasuredTable<DailyStatisticsAlignmentTableDetail> metricId="system.scheduled-statistics.table-2"
              rowKey="statisticType"
              size="small"
              columns={detailColumns}
              dataSource={row.details}
              pagination={false}
              scroll={{ x: 1800 }}
            />
          ),
        }}
        pagination={{ pageSize: 20, showSizeChanger: true }}
      />
    </Space>
  )
}

export default function ScheduledStatisticsPage() {
  const isAdmin = useAuthStore((state) => state.access.isAdmin)
  const canManageScheduledTasks = useAuthStore((state) => state.access.canManageScheduledTasks)
  const canTriggerStatisticsTasks = isAdmin
  const [storeForm] = Form.useForm<ScopeTaskFormValues>()
  const [supplierForm] = Form.useForm<ScopeTaskFormValues>()
  const [storeSupplierForm] = Form.useForm<ScopeTaskFormValues>()
  const [otherForm] = Form.useForm<OtherTaskFormValues>()
  const [taskLogForm] = Form.useForm<TaskLogQueryValues>()
  const [schedulerStatus, setSchedulerStatus] = useState<ScheduledTaskRuntimeControlStatus | null>(null)
  const [schedulerLoading, setSchedulerLoading] = useState(false)
  const [schedulerSaving, setSchedulerSaving] = useState(false)
  const [actionLoadingKey, setActionLoadingKey] = useState<string | null>(null)
  const [stores, setStores] = useState<Array<{ label: string; value: string }>>([])
  const [suppliers, setSuppliers] = useState<Array<{ label: string; value: string }>>([])
  const [optionsLoading, setOptionsLoading] = useState(false)
  const [taskRows, setTaskRows] = useState<ScheduledTaskLogItem[]>([])
  const [taskLogLoading, setTaskLogLoading] = useState(false)
  const [taskPage, setTaskPage] = useState(1)
  const [taskPageSize, setTaskPageSize] = useState(20)
  const [taskTotal, setTaskTotal] = useState(0)
  const taskLogsRequestGuardRef = useRef(createLatestRequestGuard())
  const mountedRef = useRef(false)
  const desiredTaskLogQueryRef = useRef<DesiredTaskLogQuery>({
    pageNumber: taskPage,
    pageSize: taskPageSize,
    sortBy: 'startedAt',
    sortDirection: 'desc',
  })
  const [detailOpen, setDetailOpen] = useState(false)
  const [detailLoading, setDetailLoading] = useState(false)
  const [detailTask, setDetailTask] = useState<ScheduledTaskLogItem | null>(null)

  const schedulerInstanceOptions =
    schedulerStatus?.knownInstances.map((instance) => ({
      value: instance.instanceId,
      label: `${instance.instanceId}${instance.isCurrent ? ' (当前)' : ''}`,
    })) ?? []
  const currentSchedulerInstance = schedulerStatus?.knownInstances.find((instance) => instance.isCurrent)
  const activeSchedulerInstance = schedulerStatus?.knownInstances.find((instance) => instance.isActive)

  const loadSchedulerStatus = async () => {
    setSchedulerLoading(true)
    try {
      setSchedulerStatus(await getScheduledTaskRuntimeControl())
    } catch (error) {
      console.error(error)
      message.error('加载调度状态失败')
    } finally {
      setSchedulerLoading(false)
    }
  }

  const loadOptions = async () => {
    setOptionsLoading(true)
    try {
      const [storeOptions, localSuppliers, chinaSuppliers] = await Promise.all([
        getActiveStores(),
        getActiveLocalSuppliers(),
        getActiveChinaSuppliers(),
      ])
      setStores(storeOptions)
      const localOptions = localSuppliers.map((item) => ({
        label: `${item.localSupplierCode} - ${item.name}`,
        value: item.localSupplierCode,
      }))
      const chinaOptions = chinaSuppliers.map((item) => ({
        label: `${item.supplierCode} - ${item.supplierName}`,
        value: item.supplierCode,
      }))
      const supplierMap = new Map([...localOptions, ...chinaOptions].map((item) => [item.value, item]))
      setSuppliers(Array.from(supplierMap.values()).sort((a, b) => a.value.localeCompare(b.value)))
    } catch (error) {
      console.error(error)
      message.error('加载分店或供应商选项失败')
    } finally {
      setOptionsLoading(false)
    }
  }

  const loadTaskLogs = async (overrides: Partial<DesiredTaskLogQuery> = {}) => {
    if (!mountedRef.current) {
      return
    }

    const values = taskLogForm.getFieldsValue()
    const dateRange = getDateRangePayload(values.dateRange)
    const query: DesiredTaskLogQuery = {
      taskType: values.taskType || undefined,
      status: values.status || undefined,
      startDate: dateRange?.startDate,
      endDate: dateRange?.endDate,
      pageNumber: taskPage,
      pageSize: taskPageSize,
      sortBy: 'startedAt',
      sortDirection: 'desc',
      ...overrides,
    }
    // 任务执行晚完成时刷新已开始的目标页和筛选，而不是最后成功页。
    desiredTaskLogQueryRef.current = query

    await runLatestGuardedRequest(taskLogsRequestGuardRef.current, () => getScheduledTaskList(query), {
      onStart: () => setTaskLogLoading(true),
      onSuccess: (result) => {
        setTaskRows(result.items)
        setTaskTotal(result.total)
        setTaskPage(result.page)
        setTaskPageSize(result.pageSize)
      },
      onError: (error) => {
        console.error(error)
        message.error('加载任务日志失败')
      },
      onSettled: () => setTaskLogLoading(false),
    })
  }

  const latestLoadTaskLogsRef = useRef(loadTaskLogs)

  useLayoutEffect(() => {
    latestLoadTaskLogsRef.current = loadTaskLogs
  })

  const refreshDesiredTaskLogs = (overrides: Partial<DesiredTaskLogQuery> = {}) =>
    latestLoadTaskLogsRef.current({ ...desiredTaskLogQueryRef.current, ...overrides })

  useLayoutEffect(() => {
    mountedRef.current = true
    return () => {
      mountedRef.current = false
      taskLogsRequestGuardRef.current.invalidate()
    }
  }, [])

  useEffect(() => {
    const defaultRange: DateRangeValue = [dayjs().subtract(6, 'day'), dayjs()]
    storeForm.setFieldsValue({ date: dayjs(), dateRange: defaultRange })
    supplierForm.setFieldsValue({ date: dayjs(), dateRange: defaultRange })
    storeSupplierForm.setFieldsValue({ date: dayjs(), dateRange: defaultRange })
    otherForm.setFieldsValue({ date: dayjs(), dateRange: defaultRange, hour: undefined, maxConcurrency: 3 })
    taskLogForm.setFieldsValue({ dateRange: defaultRange, status: '' })
    void loadSchedulerStatus()
    void loadOptions()
    void loadTaskLogs({ pageNumber: 1, pageSize: taskPageSize })
  }, [])

  const handleUpdateScheduler = async (next: {
    schedulerEnabled?: boolean
    activeInstanceId?: string | null
  }) => {
    if (!schedulerStatus) {
      return
    }
    if (!canManageScheduledTasks) {
      message.warning('只读：需要管理定时任务权限')
      return
    }

    setSchedulerSaving(true)
    try {
      const updated = await updateScheduledTaskRuntimeControl({
        schedulerEnabled: next.schedulerEnabled ?? schedulerStatus.schedulerEnabled,
        activeInstanceId: next.activeInstanceId ?? schedulerStatus.activeInstanceId,
      })
      setSchedulerStatus(updated)
      message.success('调度状态已更新')
    } catch (error) {
      console.error(error)
      message.error('更新调度状态失败')
    } finally {
      setSchedulerSaving(false)
    }
  }

  const runTaskAction = async (
    actionKey: string,
    action: () => Promise<ScheduledStatisticsJobResult>,
    fallbackMessage: string,
  ) => {
    if (!canTriggerStatisticsTasks) {
      message.warning('统计任务触发需要管理员角色')
      return
    }

    setActionLoadingKey(actionKey)
    try {
      const result = await action()
      if (isScheduledStatisticsJobFailure(result)) {
        message.error(buildSubmitMessage(result, fallbackMessage))
        await refreshDesiredTaskLogs({ pageNumber: 1 })
        return
      }
      message.success(buildSubmitMessage(result, fallbackMessage))
      await refreshDesiredTaskLogs({ pageNumber: 1 })
    } catch (error) {
      console.error(error)
      message.error(getScheduledStatisticsActionErrorMessage(error))
    } finally {
      setActionLoadingKey(null)
    }
  }

  const submitScopeTask = async (taskKey: ScopeTaskKey, mode: 'single' | 'range') => {
    const form =
      taskKey === 'store' ? storeForm : taskKey === 'supplier' ? supplierForm : storeSupplierForm
    const values = await form.validateFields()
    const actionKey = `${taskKey}-${mode}`
    const branchCodes = values.branchCodes?.length ? values.branchCodes : undefined
    const supplierCodes = values.supplierCodes?.length ? values.supplierCodes : undefined

    if (mode === 'single') {
      const date = formatDate(values.date)
      if (!date) {
        message.warning('请先选择日期')
        return
      }

      if (taskKey === 'store') {
        await runTaskAction(
          actionKey,
          () => triggerStoreStatistics({ date, branchCodes }),
          '分店统计任务已触发',
        )
        return
      }
      if (taskKey === 'supplier') {
        await runTaskAction(
          actionKey,
          () => triggerSupplierStatistics({ date, supplierCodes }),
          '供应商统计任务已触发',
        )
        return
      }
      await runTaskAction(
        actionKey,
        () => triggerStoreSupplierStatistics({ date, branchCodes, supplierCodes }),
        '门店供应商统计任务已触发',
      )
      return
    }

    const range = getDateRangePayload(values.dateRange)
    if (!range) {
      message.warning('请先选择日期范围')
      return
    }

    if (taskKey === 'store') {
      await runTaskAction(
        actionKey,
        () => batchUpdateStoreStatistics({ ...range, branchCodes }),
        '分店统计批量任务已触发',
      )
      return
    }
    if (taskKey === 'supplier') {
      await runTaskAction(
        actionKey,
        () => batchUpdateSupplierStatistics({ ...range, supplierCodes }),
        '供应商统计批量任务已触发',
      )
      return
    }
    await runTaskAction(
      actionKey,
      () => batchUpdateStoreSupplierStatistics({ ...range, branchCodes, supplierCodes }),
      '门店供应商统计批量任务已触发',
    )
  }

  const confirmOtherTask = async (taskKey: OtherTaskKey, title: string) => {
    const values = await otherForm.validateFields()
    Modal.confirm({
      title,
      content: '统计任务可能耗时较长，请确认当前不是业务高峰时段。',
      okText: '确认执行',
      cancelText: '取消',
      onOk: async () => {
        if (taskKey === 'daily') {
          const date = formatDate(values.date)
          if (!date) {
            message.warning('请先选择日期')
            return
          }
          await runTaskAction('daily', () => triggerDailyStatistics({ date }), '每日统计任务已触发')
          return
        }

        if (taskKey === 'hourly') {
          const range = getDateRangePayload(values.dateRange)
          if (!range) {
            message.warning('请先选择日期范围')
            return
          }
          await runTaskAction(
            'hourly',
            () => batchUpdateHourlyStatistics({ ...range, hour: values.hour }),
            '分时统计批量任务已触发',
          )
          return
        }

        if (taskKey === 'dailyRange') {
          const range = getDateRangePayload(values.dateRange)
          if (!range) {
            message.warning('请先选择日期范围')
            return
          }
          await runTaskAction(
            'dailyRange',
            () => batchUpdateDailyStatistics(range),
            '每日统计批量任务已触发',
          )
          return
        }

        if (taskKey === 'fullCurrentDay') {
          await runTaskAction('fullCurrentDay', triggerFullRefreshCurrentDay, '全量刷新当天任务已触发')
          return
        }

        if (taskKey === 'fullPreviousAndCurrent') {
          await runTaskAction(
            'fullPreviousAndCurrent',
            triggerFullRefreshPreviousAndCurrentDay,
            '全量刷新前一天和当天任务已触发',
          )
          return
        }

        const range = getDateRangePayload(values.dateRange)
        if (!range) {
          message.warning('请先选择日期范围')
          return
        }
        await runTaskAction(
          'fullRange',
          () => batchFullRefreshConcurrent({
            ...range,
            maxConcurrency: values.maxConcurrency ?? 3,
          }),
          '并发全量刷新任务已触发',
        )
      },
    })
  }

  const openTaskDetail = async (record: ScheduledTaskLogItem) => {
    setDetailOpen(true)
    setDetailLoading(true)
    setDetailTask(null)
    try {
      setDetailTask(await getScheduledTaskDetail(record.id))
    } catch (error) {
      console.error(error)
      message.error('加载任务详情失败')
    } finally {
      setDetailLoading(false)
    }
  }

  const handleRetryTask = async (record: ScheduledTaskLogItem) => {
    if (!canManageScheduledTasks) {
      message.warning('只读：需要管理定时任务权限')
      return
    }
    setActionLoadingKey(`retry-${record.id}`)
    try {
      await retryScheduledTask(record.id)
      message.success('任务重试已启动')
      await refreshDesiredTaskLogs()
    } catch (error) {
      console.error(error)
      message.error('任务重试失败')
    } finally {
      setActionLoadingKey(null)
    }
  }

  const renderScopeTaskForm = (
    taskKey: ScopeTaskKey,
    form: ReturnType<typeof Form.useForm<ScopeTaskFormValues>>[0],
    showStores: boolean,
    showSuppliers: boolean,
  ) => (
    <Form form={form} layout="vertical">
      <Row gutter={16}>
        <Col xs={24} md={6}>
          <Form.Item label="单日日期" name="date">
            <DatePicker style={{ width: '100%' }} allowClear={false} />
          </Form.Item>
        </Col>
        <Col xs={24} md={8}>
          <Form.Item label="批量日期范围" name="dateRange">
            <RangePicker style={{ width: '100%' }} allowClear={false} />
          </Form.Item>
        </Col>
        {showStores ? (
          <Col xs={24} md={10}>
            <Form.Item label="分店" name="branchCodes">
              <Select
                mode="multiple"
                allowClear
                maxTagCount="responsive"
                loading={optionsLoading}
                options={stores}
                placeholder="留空表示全部分店"
              />
            </Form.Item>
          </Col>
        ) : null}
        {showSuppliers ? (
          <Col xs={24} md={showStores ? 24 : 10}>
            <Form.Item label="供应商" name="supplierCodes">
              <Select
                mode="multiple"
                allowClear
                showSearch
                optionFilterProp="label"
                maxTagCount="responsive"
                loading={optionsLoading}
                options={suppliers}
                placeholder="留空表示全部供应商"
              />
            </Form.Item>
          </Col>
        ) : null}
      </Row>
      <Space>
        <Button
          type="primary"
          disabled={!canTriggerStatisticsTasks}
          loading={actionLoadingKey === `${taskKey}-single`}
          onClick={() => void submitScopeTask(taskKey, 'single')}
        >
          执行单日统计
        </Button>
        <Button
          disabled={!canTriggerStatisticsTasks}
          loading={actionLoadingKey === `${taskKey}-range`}
          onClick={() => void submitScopeTask(taskKey, 'range')}
        >
          执行日期范围
        </Button>
      </Space>
    </Form>
  )

  const taskColumns = useMemo<ColumnsType<ScheduledTaskLogItem>>(
    () => [
      { title: '任务类型', dataIndex: 'taskType', width: 230 },
      {
        title: '状态',
        dataIndex: 'status',
        width: 110,
        render: (value?: string) => <Tag color={getStatusColor(value)}>{value || '--'}</Tag>,
      },
      { title: '触发来源', dataIndex: 'triggeredBy', width: 120, render: (value, record) => value || record.trigger || '--' },
      { title: '开始时间', dataIndex: 'startedAt', width: 180, render: formatDateTime },
      { title: '完成时间', dataIndex: 'completedAt', width: 180, render: formatDateTime },
      { title: '失败原因', dataIndex: 'errorMessage', ellipsis: true, render: (value?: string) => value || '--' },
      {
        title: '操作',
        key: 'actions',
        fixed: 'right',
        width: 150,
        render: (_, record) => (
          <Space size="small">
            <Button type="link" size="small" onClick={() => void openTaskDetail(record)}>
              详情
            </Button>
            <Button
              type="link"
              size="small"
              disabled={!canManageScheduledTasks || record.status !== 'Failed'}
              loading={actionLoadingKey === `retry-${record.id}`}
              onClick={() => void handleRetryTask(record)}
            >
              重试
            </Button>
          </Space>
        ),
      },
    ],
    [actionLoadingKey, canManageScheduledTasks],
  )

  return (
    <PageContainer
      title="定时统计任务"
      subtitle="集中管理后台统计任务、调度实例和执行日志。"
      extra={
        <Button icon={<ReloadOutlined />} onClick={() => void Promise.all([loadSchedulerStatus(), refreshDesiredTaskLogs()])}>
          刷新
        </Button>
      }
    >
      <Space direction="vertical" size={16} style={{ width: '100%' }}>
        {!canTriggerStatisticsTasks ? (
          <Alert type="info" showIcon message="统计任务触发需要管理员角色；当前账号可查看任务记录并按权限管理调度实例。" />
        ) : null}

        <Card
          title="调度状态"
          extra={
            <Button
              icon={<ReloadOutlined />}
              loading={schedulerLoading}
              onClick={() => void loadSchedulerStatus()}
            >
              刷新
            </Button>
          }
        >
          <Row gutter={[16, 16]} align="middle">
            <Col xs={24} md={6}>
              <Statistic
                title="当前实例状态"
                value={schedulerStatus?.effectiveSchedulerEnabled ? '运行中' : '已停止'}
                valueStyle={{
                  color: schedulerStatus?.effectiveSchedulerEnabled ? '#1677ff' : '#cf1322',
                  fontSize: 20,
                }}
              />
            </Col>
            <Col xs={24} md={6}>
              <Typography.Text type="secondary">Active 实例</Typography.Text>
              <Typography.Paragraph copyable style={{ marginBottom: 0 }}>
                {schedulerStatus?.activeInstanceId ?? '-'}
              </Typography.Paragraph>
            </Col>
            <Col xs={24} md={6}>
              <Typography.Text type="secondary">Active 最后心跳</Typography.Text>
              <Typography.Paragraph style={{ marginBottom: 0 }}>
                {formatDateTime(activeSchedulerInstance?.lastSeenAtUtc)}
              </Typography.Paragraph>
            </Col>
            <Col xs={24} md={6}>
              <Typography.Text type="secondary">调度开关</Typography.Text>
              <div style={{ marginTop: 8 }}>
                <Switch
                  checked={!!schedulerStatus?.schedulerEnabled}
                  loading={schedulerSaving}
                  disabled={!schedulerStatus || !schedulerStatus.schedulerEnabledByConfig || !canManageScheduledTasks}
                  checkedChildren="启用"
                  unCheckedChildren="禁用"
                  onChange={(checked) => void handleUpdateScheduler({ schedulerEnabled: checked })}
                />
              </div>
              {!schedulerStatus?.schedulerEnabledByConfig ? (
                <Typography.Text type="danger">配置已禁用</Typography.Text>
              ) : null}
            </Col>
          </Row>
          {/* 低频运维控制默认收起，避免挤占数据对齐首屏。 */}
          <Collapse
            style={{ marginTop: 16 }}
            items={[
              {
                key: 'advanced',
                label: '高级运行控制',
                children: (
                  <Row gutter={[16, 16]}>
                    <Col xs={24} md={6}>
                      <Typography.Text type="secondary">当前实例</Typography.Text>
                      <Typography.Paragraph copyable style={{ marginBottom: 0 }}>
                        {schedulerStatus?.currentInstanceId ?? '-'}
                      </Typography.Paragraph>
                    </Col>
                    <Col xs={24} md={7}>
                      <Typography.Text type="secondary">调度实例</Typography.Text>
                      <Select
                        loading={schedulerLoading}
                        disabled={!schedulerStatus || schedulerSaving || !canManageScheduledTasks}
                        value={schedulerStatus?.activeInstanceId}
                        options={schedulerInstanceOptions}
                        style={{ width: '100%', marginTop: 4 }}
                        onChange={(value) => void handleUpdateScheduler({ activeInstanceId: value })}
                      />
                    </Col>
                    <Col xs={24} md={5}>
                      <Typography.Text type="secondary">当前实例心跳</Typography.Text>
                      <Typography.Paragraph style={{ marginBottom: 0 }}>
                        {formatDateTime(currentSchedulerInstance?.lastSeenAtUtc)}
                      </Typography.Paragraph>
                    </Col>
                    <Col xs={12} md={3}>
                      <Statistic title="运行中租约" value={schedulerStatus?.runningLeaseCount ?? 0} />
                    </Col>
                    <Col xs={12} md={3}>
                      <Statistic title="重复跳过" value={schedulerStatus?.recentDuplicateSkipCount ?? 0} />
                    </Col>
                  </Row>
                ),
              },
            ]}
          />
        </Card>

        <Card>
          <Tabs
            items={[
              {
                key: 'alignment',
                label: '数据对齐',
                children: (
                  <DailyAlignmentPanel
                    canRecalculate={canTriggerStatisticsTasks}
                    onAfterRecalculate={() => {
                      void loadSchedulerStatus()
                      void refreshDesiredTaskLogs({ pageNumber: 1 })
                    }}
                  />
                ),
              },
              {
                key: 'store',
                label: '分店统计',
                children: renderScopeTaskForm('store', storeForm, true, false),
              },
              {
                key: 'supplier',
                label: '供应商统计',
                children: renderScopeTaskForm('supplier', supplierForm, false, true),
              },
              {
                key: 'storeSupplier',
                label: '门店供应商统计',
                children: renderScopeTaskForm('storeSupplier', storeSupplierForm, true, true),
              },
              {
                key: 'productStoreDaily',
                label: '商品/分店每日统计',
                children: canTriggerStatisticsTasks
                  ? <ProductStatisticsPage />
                  : <Alert type="info" showIcon message="商品/分店每日统计重算需要管理员角色。" />,
              },
              {
                key: 'other',
                label: '其他统计',
                children: (
                  <Form form={otherForm} layout="vertical">
                    <Row gutter={16}>
                      <Col xs={24} md={6}>
                        <Form.Item label="单日日期" name="date">
                          <DatePicker style={{ width: '100%' }} allowClear={false} />
                        </Form.Item>
                      </Col>
                      <Col xs={24} md={8}>
                        <Form.Item label="日期范围" name="dateRange">
                          <RangePicker style={{ width: '100%' }} allowClear={false} />
                        </Form.Item>
                      </Col>
                      <Col xs={24} md={5}>
                        <Form.Item label="小时" name="hour">
                          <InputNumber min={0} max={23} style={{ width: '100%' }} placeholder="留空表示全部小时" />
                        </Form.Item>
                      </Col>
                      <Col xs={24} md={5}>
                        <Form.Item label="最大并发" name="maxConcurrency">
                          <InputNumber min={1} max={10} style={{ width: '100%' }} />
                        </Form.Item>
                      </Col>
                    </Row>
                    <Space wrap>
                      <Button
                        disabled={!canTriggerStatisticsTasks}
                        loading={actionLoadingKey === 'daily'}
                        onClick={() => void confirmOtherTask('daily', '确认执行每日统计？')}
                      >
                        每日统计
                      </Button>
                      <Button
                        disabled={!canTriggerStatisticsTasks}
                        loading={actionLoadingKey === 'hourly'}
                        onClick={() => void confirmOtherTask('hourly', '确认执行分时统计？')}
                      >
                        分时统计
                      </Button>
                      <Button
                        disabled={!canTriggerStatisticsTasks}
                        loading={actionLoadingKey === 'dailyRange'}
                        onClick={() => void confirmOtherTask('dailyRange', '确认批量执行每日统计？')}
                      >
                        每日统计范围
                      </Button>
                    </Space>
                    <Collapse
                      style={{ marginTop: 16 }}
                      items={[
                        {
                          key: 'advanced',
                          label: '高级刷新任务',
                          children: (
                            <Space wrap>
                              <Button
                                danger
                                disabled={!canTriggerStatisticsTasks}
                                loading={actionLoadingKey === 'fullCurrentDay'}
                                onClick={() => void confirmOtherTask('fullCurrentDay', '确认全量刷新当天数据？')}
                              >
                                全量刷新当天
                              </Button>
                              <Button
                                danger
                                disabled={!canTriggerStatisticsTasks}
                                loading={actionLoadingKey === 'fullPreviousAndCurrent'}
                                onClick={() => void confirmOtherTask('fullPreviousAndCurrent', '确认全量刷新前一天和当天？')}
                              >
                                全量刷新前一天和当天
                              </Button>
                              <Button
                                danger
                                disabled={!canTriggerStatisticsTasks}
                                loading={actionLoadingKey === 'fullRange'}
                                onClick={() => void confirmOtherTask('fullRange', '确认并发全量刷新日期范围？')}
                              >
                                并发全量刷新范围
                              </Button>
                            </Space>
                          ),
                        },
                      ]}
                    />
                  </Form>
                ),
              },
            ]}
          />
        </Card>

        <Card title="任务日志">
          <Form
            form={taskLogForm}
            layout="vertical"
            onFinish={() => void loadTaskLogs({ pageNumber: 1, pageSize: taskPageSize })}
          >
            <Row gutter={16}>
              <Col xs={24} md={7}>
                <Form.Item label="任务类型" name="taskType">
                  <Select allowClear showSearch optionFilterProp="label" options={taskTypeOptions} />
                </Form.Item>
              </Col>
              <Col xs={24} md={5}>
                <Form.Item label="状态" name="status">
                  <Select options={taskStatusOptions} />
                </Form.Item>
              </Col>
              <Col xs={24} md={8}>
                <Form.Item label="日期范围" name="dateRange">
                  <RangePicker style={{ width: '100%' }} allowClear />
                </Form.Item>
              </Col>
              <Col xs={24} md={4}>
                <Form.Item label=" ">
                  <Space>
                    <Button type="primary" htmlType="submit" icon={<SearchOutlined />}>
                      查询
                    </Button>
                    <Button onClick={() => {
                      taskLogForm.resetFields()
                      taskLogForm.setFieldsValue({ status: '', dateRange: [dayjs().subtract(6, 'day'), dayjs()] })
                      void loadTaskLogs({ pageNumber: 1, pageSize: taskPageSize })
                    }}
                    >
                      重置
                    </Button>
                  </Space>
                </Form.Item>
              </Col>
            </Row>
          </Form>
          <MeasuredTable<ScheduledTaskLogItem> metricId="system.scheduled-statistics.table-3"
            rowKey="id"
            loading={taskLogLoading}
            columns={taskColumns}
            dataSource={taskRows}
            scroll={{ x: 1180 }}
            pagination={{
              current: taskPage,
              pageSize: taskPageSize,
              total: taskTotal,
              showSizeChanger: true,
              showTotal: (value) => `共 ${value} 条`,
              onChange: (nextPage, nextPageSize) =>
                void loadTaskLogs({ pageNumber: nextPage, pageSize: nextPageSize }),
            }}
          />
        </Card>
      </Space>

      <Drawer
        title="任务详情"
        width={720}
        open={detailOpen}
        onClose={() => {
          setDetailOpen(false)
          setDetailTask(null)
        }}
        destroyOnHidden
      >
        {detailLoading ? (
          <Typography.Text type="secondary">正在加载...</Typography.Text>
        ) : detailTask ? (
          <Descriptions bordered column={1} size="small">
            <Descriptions.Item label="任务ID">{detailTask.id}</Descriptions.Item>
            <Descriptions.Item label="任务类型">{detailTask.taskType}</Descriptions.Item>
            <Descriptions.Item label="状态">
              <Tag color={getStatusColor(detailTask.status)}>{detailTask.status}</Tag>
            </Descriptions.Item>
            <Descriptions.Item label="触发来源">{detailTask.triggeredBy || detailTask.trigger || '--'}</Descriptions.Item>
            <Descriptions.Item label="开始时间">{formatDateTime(detailTask.startedAt)}</Descriptions.Item>
            <Descriptions.Item label="完成时间">{formatDateTime(detailTask.completedAt)}</Descriptions.Item>
            <Descriptions.Item label="失败原因">{detailTask.errorMessage || '--'}</Descriptions.Item>
            <Descriptions.Item label="参数">
              <Typography.Paragraph code style={{ whiteSpace: 'pre-wrap', marginBottom: 0 }}>
                {formatParameters(detailTask.parameters)}
              </Typography.Paragraph>
            </Descriptions.Item>
          </Descriptions>
        ) : null}
      </Drawer>
    </PageContainer>
  )
}
