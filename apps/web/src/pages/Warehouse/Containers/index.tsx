import {
  BarChartOutlined,
  CloudUploadOutlined,
  EyeOutlined,
  PlusOutlined,
  ReloadOutlined,
  SearchOutlined,
} from '@ant-design/icons'
import {
  Button,
  Card,
  Col,
  DatePicker,
  Form,
  Input,
  InputNumber,
  Modal,
  Row,
  Select,
  Space,
  Statistic,
  Tag,
  message,
} from 'antd'
import type { ColumnsType, TablePaginationConfig } from 'antd/es/table'
import type { FilterDropdownProps } from 'antd/es/table/interface'
import type { TFunction } from 'i18next'
import type { Dayjs } from 'dayjs'
import dayjs from 'dayjs'
import isoWeek from 'dayjs/plugin/isoWeek'
import { useEffect, useLayoutEffect, useMemo, useRef, useState, type Key } from 'react'
import { useTranslation } from 'react-i18next'
import { useNavigate } from 'react-router-dom'
import PageContainer from '../../../components/PageContainer'
import {
  createContainer,
  getContainerList,
  pushContainersToHbSales,
  updateContainer,
} from '../../../services/containerService'
import { useAuthStore } from '../../../store/auth'
import type { ContainerMain, CreateContainerRequest } from '../../../types/container'
import { createLatestRequestGuard, runLatestGuardedRequest } from '../../../utils/latestRequestGuard'
import { MeasuredTable } from '../../../components/MeasuredTable'

type RangeValue = [Dayjs | null, Dayjs | null] | null
type ContainerColumnTextFilterKey = 'containerNumberFilter'
type ContainerColumnDateStartKey = 'loadingDateStart' | 'estimatedArrivalDateStart' | 'actualArrivalDateStart'
type ContainerColumnDateEndKey = 'loadingDateEnd' | 'estimatedArrivalDateEnd' | 'actualArrivalDateEnd'
type ContainerColumnNumberKey =
  | 'totalPiecesMin'
  | 'totalPiecesMax'
  | 'totalAmountMin'
  | 'totalAmountMax'
  | 'totalVolumeMin'
  | 'totalVolumeMax'

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
  statuses?: number[]
}

interface LoadDataOptions {
  dateType?: string
  dateRange?: RangeValue
  itemNumberFilter?: string
  columnFilters?: ContainerColumnFilters
}

interface PendingFirstPageRequest {
  options: LoadDataOptions
  resolve: () => void
}

dayjs.extend(isoWeek)

function formatDate(value?: string) {
  return value ? dayjs(value).format('YYYY-MM-DD') : '--'
}

function formatAmount(value?: number) {
  return value == null ? '--' : `¥${value.toLocaleString('zh-CN', { minimumFractionDigits: 2, maximumFractionDigits: 2 })}`
}

function formatNumber(value?: number, digits = 0) {
  return value == null ? '--' : value.toLocaleString('zh-CN', { maximumFractionDigits: digits, minimumFractionDigits: digits })
}

const containerStatusMeta: Record<number, { color: string; labelKey: string }> = {
  0: { color: 'blue', labelKey: 'loaded' },
  1: { color: 'orange', labelKey: 'inTransit' },
  2: { color: 'success', labelKey: 'completed' },
  7: { color: 'error', labelKey: 'cancelled' },
}

const containerStatusValues = [0, 1, 2, 7]
const CONTAINER_STATUS_SELECT_WIDTH = 104
const containerDateWeekColors = [
  { background: '#e6f4ff', border: '#91caff', color: '#0958d9' },
  { background: '#f6ffed', border: '#b7eb8f', color: '#237804' },
  { background: '#fff7e6', border: '#ffd591', color: '#ad6800' },
  { background: '#f9f0ff', border: '#d3adf7', color: '#722ed1' },
  { background: '#e6fffb', border: '#87e8de', color: '#006d75' },
  { background: '#fff1f0', border: '#ffa39e', color: '#a8071a' },
  { background: '#fffbe6', border: '#ffe58f', color: '#ad8b00' },
  { background: '#f0f5ff', border: '#adc6ff', color: '#1d39c4' },
]

function getContainerStatusOptionLabel(item: { color: string; labelKey: string }, t: TFunction) {
  return (
    <Tag color={item.color} style={{ minWidth: 54, marginInlineEnd: 0, textAlign: 'center' }}>
      {t(`containers.status.${item.labelKey}`)}
    </Tag>
  )
}

function itemKeyOf(record: ContainerMain) {
  return record.hguid || String(record.id)
}

function getStatusTag(status: number | undefined, t: TFunction) {
  if (status == null) return <Tag>{t('containers.status.unknown')}</Tag>
  const item = containerStatusMeta[status]
  return item ? getContainerStatusOptionLabel(item, t) : <Tag>{t('containers.status.unknownWithCode', { status })}</Tag>
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

function getContainerDateWeekKey(value?: string) {
  if (!value) return undefined
  const date = dayjs(value)
  if (!date.isValid()) return undefined
  return `${date.isoWeekYear()}-W${String(date.isoWeek()).padStart(2, '0')}`
}

function getContainerDateWeekColor(weekKey: string) {
  let hash = 0
  for (const char of weekKey) {
    hash = (hash * 31 + char.charCodeAt(0)) % containerDateWeekColors.length
  }
  return containerDateWeekColors[hash]
}

function renderContainerWeekDate(value?: string) {
  if (!value) return '--'
  const weekKey = getContainerDateWeekKey(value)
  if (!weekKey) return formatDate(value)
  const color = getContainerDateWeekColor(weekKey)

  return (
    <Tag
      title={weekKey}
      style={{
        backgroundColor: color.background,
        borderColor: color.border,
        color: color.color,
        fontVariantNumeric: 'tabular-nums',
        marginInlineEnd: 0,
      }}
    >
      {formatDate(value)}
    </Tag>
  )
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
  const [itemNumberFilter, setItemNumberFilter] = useState('')
  const [columnFilters, setColumnFilters] = useState<ContainerColumnFilters>({})
  const [selectedRowKeys, setSelectedRowKeys] = useState<Key[]>([])
  const [createOpen, setCreateOpen] = useState(false)
  const [createLoading, setCreateLoading] = useState(false)
  const [pushing, setPushing] = useState(false)
  const [statusUpdatingKeys, setStatusUpdatingKeys] = useState<string[]>([])
  const listRequestGuardRef = useRef(createLatestRequestGuard())
  const mountedRef = useRef(false)
  const latestLoadDataRef = useRef<(
    nextPage?: number,
    nextPageSize?: number,
    options?: LoadDataOptions,
  ) => Promise<void>>(async () => undefined)
  const pendingFirstPageRequestRef = useRef<PendingFirstPageRequest | null>(null)
  const activeFirstPageResolverRef = useRef<(() => void) | null>(null)
  const latestRequestFirstPageRef = useRef<(options?: LoadDataOptions) => Promise<void>>(async () => undefined)

  const containerStatusOptions = useMemo(
    () =>
      containerStatusValues.map((value) => {
        const item = containerStatusMeta[value]
        return {
          value,
          label: getContainerStatusOptionLabel(item, t),
        }
      }),
    [t],
  )

  const stats = useMemo(
    () =>
      containers.reduce(
        (acc, item) => ({
          count: acc.count + 1,
          pieces: acc.pieces + (item.合计件数 || 0),
          amount: acc.amount + (item.合计金额 || 0),
          volume: acc.volume + (item.总体积 || 0),
        }),
        { count: 0, pieces: 0, amount: 0, volume: 0 },
      ),
    [containers],
  )

  const loadData = async (nextPage = page, nextPageSize = pageSize, options: LoadDataOptions = {}) => {
    if (!mountedRef.current) return

    const activeDateType = options.dateType ?? dateType
    const activeDateRange = Object.prototype.hasOwnProperty.call(options, 'dateRange') ? options.dateRange : dateRange
    const activeItemNumberFilter = options.itemNumberFilter ?? itemNumberFilter
    const activeColumnFilters = options.columnFilters ?? columnFilters

    await runLatestGuardedRequest(
      listRequestGuardRef.current,
      () =>
        getContainerList({
          dateType: activeDateType,
          startDate: activeDateRange?.[0]?.format('YYYY-MM-DD'),
          endDate: activeDateRange?.[1]?.format('YYYY-MM-DD'),
          page: nextPage,
          pageSize: nextPageSize,
          itemNumberFilter: activeItemNumberFilter || undefined,
          ...activeColumnFilters,
        }),
      {
        onStart: () => setLoading(true),
        onSuccess: (result) => {
          setContainers(result.containers)
          setTotal(result.totalCount)
          setPage(result.page)
          setPageSize(result.pageSize)
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

  useLayoutEffect(() => {
    mountedRef.current = true

    return () => {
      mountedRef.current = false
      resolvePendingFirstPageRequest()
      resolveActiveFirstPageRequest()
      listRequestGuardRef.current.invalidate()
    }
  }, [])

  useLayoutEffect(() => {
    latestLoadDataRef.current = loadData
    latestRequestFirstPageRef.current = requestFirstPage
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
      await latestRequestFirstPageRef.current()
    } catch (error) {
      console.error(error)
      message.error(error instanceof Error ? error.message : t('containers.messages.createFailed'))
    } finally {
      setCreateLoading(false)
    }
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
    } catch (error) {
      console.error(error)
      setContainers((items) => items.map((item) => (itemKeyOf(item) === recordKey ? { ...item, 状态: previousStatus } : item)))
      message.error(error instanceof Error ? error.message : t('containers.messages.statusUpdateFailed'))
    } finally {
      setStatusUpdatingKeys((keys) => keys.filter((key) => key !== recordKey))
    }
  }

  const updateColumnFilters = (patch: ContainerColumnFilters) => {
    setColumnFilters((current) => {
      const next = { ...current, ...patch }
      Object.entries(patch).forEach(([key, value]) => {
        if (value === undefined || value === '' || (Array.isArray(value) && !value.length)) {
          delete next[key as keyof ContainerColumnFilters]
        }
      })
      return next
    })
  }

  const applyColumnFilters = (
    confirm: FilterDropdownProps['confirm'],
    nextFilters: ContainerColumnFilters = columnFilters,
  ) => {
    confirm()
    void requestFirstPage({ columnFilters: nextFilters })
  }

  const clearColumnFilter = (
    keys: Array<keyof ContainerColumnFilters>,
    confirm: FilterDropdownProps['confirm'],
  ) => {
    const nextFilters = { ...columnFilters }
    keys.forEach((key) => {
      delete nextFilters[key]
    })
    setColumnFilters(nextFilters)
    applyColumnFilters(confirm, nextFilters)
  }

  const getColumnDateRange = (
    startKey: ContainerColumnDateStartKey,
    endKey: ContainerColumnDateEndKey,
  ): RangeValue => {
    const start = columnFilters[startKey] ? dayjs(columnFilters[startKey]) : null
    const end = columnFilters[endKey] ? dayjs(columnFilters[endKey]) : null
    return start || end ? [start, end] : null
  }

  const updateColumnDateRange = (
    startKey: ContainerColumnDateStartKey,
    endKey: ContainerColumnDateEndKey,
    value: RangeValue,
  ) => {
    updateColumnFilters({
      [startKey]: value?.[0]?.format('YYYY-MM-DD'),
      [endKey]: value?.[1]?.format('YYYY-MM-DD'),
    } as ContainerColumnFilters)
  }

  const normalizeFilterNumber = (value: string | number | null) => {
    if (value === null || value === '') return undefined
    const numberValue = Number(value)
    return Number.isFinite(numberValue) ? numberValue : undefined
  }

  const filterIcon = (active?: boolean) => <SearchOutlined style={{ color: active ? '#1677ff' : undefined }} />
  const filterDropdownStyle = { padding: 8, width: 240 }

  const makeTextFilterDropdown = (key: ContainerColumnTextFilterKey, placeholder: string) => ({ confirm }: FilterDropdownProps) => (
    <div style={filterDropdownStyle} onKeyDown={(event) => event.stopPropagation()} onMouseDown={(event) => event.stopPropagation()}>
      <Space direction="vertical" style={{ width: '100%' }}>
        <Input
          autoFocus
          allowClear
          value={columnFilters[key] ?? ''}
          placeholder={placeholder}
          onChange={(event) => updateColumnFilters({ [key]: event.target.value } as ContainerColumnFilters)}
          onPressEnter={() => applyColumnFilters(confirm)}
        />
        <Space>
          <Button size="small" type="primary" onClick={() => applyColumnFilters(confirm)}>
            {t('containers.actions.applyColumnFilter')}
          </Button>
          <Button size="small" onClick={() => clearColumnFilter([key], confirm)}>
            {t('containers.actions.resetColumnFilter')}
          </Button>
        </Space>
      </Space>
    </div>
  )

  const makeDateRangeFilterDropdown = (
    startKey: ContainerColumnDateStartKey,
    endKey: ContainerColumnDateEndKey,
  ) => ({ confirm }: FilterDropdownProps) => (
    <div style={{ ...filterDropdownStyle, width: 280 }} onKeyDown={(event) => event.stopPropagation()} onMouseDown={(event) => event.stopPropagation()}>
      <Space direction="vertical" style={{ width: '100%' }}>
        <DatePicker.RangePicker
          value={getColumnDateRange(startKey, endKey)}
          onChange={(value) => updateColumnDateRange(startKey, endKey, value)}
        />
        <Space>
          <Button size="small" type="primary" onClick={() => applyColumnFilters(confirm)}>
            {t('containers.actions.applyColumnFilter')}
          </Button>
          <Button size="small" onClick={() => clearColumnFilter([startKey, endKey], confirm)}>
            {t('containers.actions.resetColumnFilter')}
          </Button>
        </Space>
      </Space>
    </div>
  )

  const makeNumberRangeFilterDropdown = (
    minKey: ContainerColumnNumberKey,
    maxKey: ContainerColumnNumberKey,
  ) => ({ confirm }: FilterDropdownProps) => (
    <div style={filterDropdownStyle} onKeyDown={(event) => event.stopPropagation()} onMouseDown={(event) => event.stopPropagation()}>
      <Space direction="vertical" style={{ width: '100%' }}>
        <Space.Compact style={{ width: '100%' }}>
          <InputNumber
            value={columnFilters[minKey]}
            placeholder={t('containers.placeholders.minValue')}
            controls={false}
            style={{ width: '50%' }}
            onChange={(value) => updateColumnFilters({ [minKey]: normalizeFilterNumber(value) } as ContainerColumnFilters)}
          />
          <InputNumber
            value={columnFilters[maxKey]}
            placeholder={t('containers.placeholders.maxValue')}
            controls={false}
            style={{ width: '50%' }}
            onChange={(value) => updateColumnFilters({ [maxKey]: normalizeFilterNumber(value) } as ContainerColumnFilters)}
          />
        </Space.Compact>
        <Space>
          <Button size="small" type="primary" onClick={() => applyColumnFilters(confirm)}>
            {t('containers.actions.applyColumnFilter')}
          </Button>
          <Button size="small" onClick={() => clearColumnFilter([minKey, maxKey], confirm)}>
            {t('containers.actions.resetColumnFilter')}
          </Button>
        </Space>
      </Space>
    </div>
  )

  const makeStatusFilterDropdown = ({ confirm }: FilterDropdownProps) => (
    <div style={filterDropdownStyle} onKeyDown={(event) => event.stopPropagation()} onMouseDown={(event) => event.stopPropagation()}>
      <Space direction="vertical" style={{ width: '100%' }}>
        <Select
          mode="multiple"
          allowClear
          value={columnFilters.statuses ?? []}
          options={containerStatusOptions}
          placeholder={t('containers.placeholders.selectStatus')}
          style={{ width: '100%' }}
          onChange={(values) => updateColumnFilters({ statuses: values })}
        />
        <Space>
          <Button size="small" type="primary" onClick={() => applyColumnFilters(confirm)}>
            {t('containers.actions.applyColumnFilter')}
          </Button>
          <Button size="small" onClick={() => clearColumnFilter(['statuses'], confirm)}>
            {t('containers.actions.resetColumnFilter')}
          </Button>
        </Space>
      </Space>
    </div>
  )

  const textFilterProps = (key: ContainerColumnTextFilterKey, placeholder: string) => ({
    filterDropdown: makeTextFilterDropdown(key, placeholder),
    filterIcon,
    filtered: Boolean(columnFilters[key]?.trim()),
  })

  const dateRangeFilterProps = (
    startKey: ContainerColumnDateStartKey,
    endKey: ContainerColumnDateEndKey,
  ) => ({
    filterDropdown: makeDateRangeFilterDropdown(startKey, endKey),
    filterIcon,
    filtered: Boolean(columnFilters[startKey] || columnFilters[endKey]),
  })

  const numberRangeFilterProps = (
    minKey: ContainerColumnNumberKey,
    maxKey: ContainerColumnNumberKey,
  ) => ({
    filterDropdown: makeNumberRangeFilterDropdown(minKey, maxKey),
    filterIcon,
    filtered: typeof columnFilters[minKey] === 'number' || typeof columnFilters[maxKey] === 'number',
  })

  const columns: ColumnsType<ContainerMain> = [
    {
      title: t('containers.columns.index'),
      width: 72,
      render: (_value, _record, index) => (page - 1) * pageSize + index + 1,
    },
    {
      title: t('containers.fields.containerNumber'),
      dataIndex: '货柜编号',
      width: 160,
      ...textFilterProps('containerNumberFilter', t('containers.placeholders.filterContainerNumber')),
      render: (text: string, record) => (
        <Button type="link" onClick={() => navigate(`/warehouse/container/detail/${record.hguid}`)}>
          {text || record.hguid}
        </Button>
      ),
    },
    {
      title: t('containers.fields.loadingDate'),
      dataIndex: '装柜日期',
      width: 120,
      ...dateRangeFilterProps('loadingDateStart', 'loadingDateEnd'),
      render: renderContainerWeekDate,
    },
    {
      title: t('containers.fields.estimatedArrivalDate'),
      dataIndex: '预计到岸日期',
      width: 130,
      ...dateRangeFilterProps('estimatedArrivalDateStart', 'estimatedArrivalDateEnd'),
      render: renderContainerWeekDate,
    },
    {
      title: t('containers.fields.actualArrivalDate'),
      dataIndex: '实际到货日期',
      width: 130,
      ...dateRangeFilterProps('actualArrivalDateStart', 'actualArrivalDateEnd'),
      render: renderContainerWeekDate,
    },
    {
      title: t('containers.fields.totalPieces'),
      dataIndex: '合计件数',
      width: 100,
      align: 'right',
      ...numberRangeFilterProps('totalPiecesMin', 'totalPiecesMax'),
      render: (v) => formatNumber(v),
    },
    {
      title: t('containers.fields.totalAmount'),
      dataIndex: '合计金额',
      width: 130,
      align: 'right',
      ...numberRangeFilterProps('totalAmountMin', 'totalAmountMax'),
      render: formatAmount,
    },
    {
      title: t('containers.fields.totalVolumeCbm'),
      dataIndex: '总体积',
      width: 120,
      align: 'right',
      ...numberRangeFilterProps('totalVolumeMin', 'totalVolumeMax'),
      render: (v) => formatNumber(v, 2),
    },
    {
      title: t('containers.fields.remark'),
      dataIndex: '备注',
      width: 180,
      ellipsis: true,
      render: (value?: string) => value?.trim() || '--',
    },
    {
      title: t('containers.fields.status'),
      dataIndex: '状态',
      width: 120,
      filterDropdown: makeStatusFilterDropdown,
      filterIcon,
      filtered: Boolean(columnFilters.statuses?.length),
      render: (_status, record) => {
        const recordKey = itemKeyOf(record)
        const canUpdateStatus = access.canEditContainer && Boolean(record.hguid) && record.状态 != null && Boolean(containerStatusMeta[record.状态])
        if (!canUpdateStatus) {
          return getStatusTag(record.状态, t)
        }

        return (
          <Select
            size="small"
            value={record.状态}
            options={containerStatusOptions}
            disabled={statusUpdatingKeys.includes(recordKey)}
            style={{ width: CONTAINER_STATUS_SELECT_WIDTH }}
            popupMatchSelectWidth={CONTAINER_STATUS_SELECT_WIDTH}
            onChange={(nextStatus) => void handleContainerStatusChange(record, nextStatus)}
          />
        )
      },
    },
    {
      title: t('common.action'),
      width: 220,
      fixed: 'right',
      render: (_, record) => (
        <Space size={0}>
          <Button icon={<EyeOutlined />} type="link" onClick={() => navigate(`/warehouse/container/detail/${record.hguid}`)}>
            {t('common.view')}
          </Button>
          <Button
            icon={<BarChartOutlined />}
            type="link"
            disabled={!record.hguid}
            onClick={() => navigate(`/warehouse/container/allocation-sales/${record.hguid}`)}
          >
            配销数据
          </Button>
        </Space>
      ),
    },
  ]

  return (
    <PageContainer title={t('containers.title')}>
      <Space direction="vertical" size={16} style={{ width: '100%' }}>
        <Card>
          <Space wrap style={{ width: '100%', justifyContent: 'space-between' }}>
            <Space wrap>
              <Select value={dateType} style={{ width: 160 }} options={dateTypeOptions.map((value) => ({ value, label: getDateOptionLabel(value, t) }))} onChange={setDateType} />
              <DatePicker.RangePicker value={dateRange} onChange={setDateRange} />
              <Input
                allowClear
                value={itemNumberFilter}
                placeholder={t('containers.placeholders.searchItemNumber')}
                prefix={<SearchOutlined />}
                style={{ width: 220 }}
                onChange={(event) => setItemNumberFilter(event.target.value)}
                onPressEnter={() => void requestFirstPage()}
              />
              <Button type="primary" icon={<SearchOutlined />} onClick={() => void requestFirstPage()}>
                {t('common.query')}
              </Button>
              <Button
                icon={<ReloadOutlined />}
                onClick={() => {
                  setDateType('预计到岸日期')
                  setDateRange(null)
                  setItemNumberFilter('')
                  setColumnFilters({})
                  // 顶部重置同时清空列头过滤，避免界面已清空但请求仍带旧条件。
                  void requestFirstPage({
                    dateType: '预计到岸日期',
                    dateRange: null,
                    itemNumberFilter: '',
                    columnFilters: {},
                  })
                }}
              >
                {t('common.reset')}
              </Button>
            </Space>
            <Space wrap>
              {access.canEditContainer ? (
                <>
                  <Button
                    icon={<CloudUploadOutlined />}
                    loading={pushing}
                    disabled={!selectedRowKeys.length}
                    onClick={handlePush}
                  >
                    {t('containers.actions.pushToHbSales')}{selectedRowKeys.length ? ` (${selectedRowKeys.length})` : ''}
                  </Button>
                </>
              ) : null}
              {access.canCreateContainer ? (
                <Button type="primary" icon={<PlusOutlined />} onClick={() => setCreateOpen(true)}>
                  {t('containers.actions.createContainer')}
                </Button>
              ) : null}
            </Space>
          </Space>
        </Card>

        <Row gutter={16}>
          <Col span={6}><Card><Statistic title={t('containers.stats.currentPageContainers')} value={stats.count} /></Card></Col>
          <Col span={6}><Card><Statistic title={t('containers.stats.currentPagePieces')} value={stats.pieces} /></Card></Col>
          <Col span={6}><Card><Statistic title={t('containers.stats.currentPageAmount')} value={stats.amount} precision={2} prefix="¥" /></Card></Col>
          <Col span={6}><Card><Statistic title={t('containers.stats.currentPageVolume')} value={stats.volume} precision={2} suffix="m³" /></Card></Col>
        </Row>

        <Card>
          <MeasuredTable metricId="warehouse.containers.table-1"
            rowKey={itemKeyOf}
            loading={loading}
            columns={columns}
            dataSource={containers}
            rowSelection={{ selectedRowKeys, onChange: setSelectedRowKeys }}
            scroll={{ x: 1500 }}
            pagination={{
              current: page,
              pageSize,
              total,
              showSizeChanger: true,
              showTotal: (value) => t('common.total', { count: value }),
              onChange: (nextPage: number, nextPageSize: number) => {
                setPage(nextPage)
                setPageSize(nextPageSize)
              },
            } satisfies TablePaginationConfig}
          />
        </Card>
      </Space>
      <CreateContainerModal open={createOpen} loading={createLoading} onCancel={() => setCreateOpen(false)} onSubmit={handleCreate} />
    </PageContainer>
  )
}
