import {
  DownloadOutlined,
  ReloadOutlined,
  SearchOutlined,
} from '@ant-design/icons'
import {
  Alert,
  Button,
  Card,
  Col,
  DatePicker,
  Form,
  Input,
  Row,
  Select,
  Space,
  Tag,
  Tooltip,
  Typography,
  message,
} from 'antd'
import type { ColumnsType, TableProps } from 'antd/es/table'
import dayjs, { type Dayjs } from 'dayjs'
import { useCallback, useEffect, useLayoutEffect, useMemo, useRef, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { Link } from 'react-router-dom'
import PageContainer from '../../../components/PageContainer'
import {
  downloadLinklySettlementExport,
  exportLinklySettlements,
  getLinklySettlementDevices,
  getLinklySettlements,
} from '../../../services/linklySettlementService'
import { getActiveStores, type StoreOption } from '../../../services/storeService'
import type {
  LinklyProviderSubmissionState,
  LinklySettlementConnectionMode,
  LinklySettlementDeviceOption,
  LinklySettlementEnvironment,
  LinklySettlementFilters,
  LinklySettlementListItem,
  LinklySettlementSortOrder,
  LinklySettlementStatus,
} from '../../../types/linklySettlement'
import {
  DEFAULT_LINKLY_SETTLEMENT_PAGE_SIZE,
  DEFAULT_LINKLY_SETTLEMENT_SORT,
  buildLinklyDeviceSelectOptions,
  buildLinklySettlementQuery,
  canExportLinklySettlementRange,
  createLatestAbortableRequestGuard,
  formatAmountMinor,
  getDefaultLinklySettlementDateRange,
  getAmountMinor,
  getAmountParseStatusColor,
  getProviderSubmissionColor,
  getSettlementStatusColor,
} from './logic'
import { MeasuredTable } from '../../../components/MeasuredTable'

const { RangePicker } = DatePicker

interface LinklySettlementFormValues {
  businessDateRange: [Dayjs, Dayjs]
  storeCode?: string
  deviceCode?: string
  connectionMode?: LinklySettlementConnectionMode
  environment?: LinklySettlementEnvironment
  status?: LinklySettlementStatus
  providerSubmissionState?: LinklyProviderSubmissionState
  keyword?: string
}

const AMOUNT_CELL_STYLE = {
  display: 'inline-block',
  minWidth: 88,
  fontVariantNumeric: 'tabular-nums',
  textAlign: 'right' as const,
}

function isAbortError(error: unknown) {
  return error instanceof DOMException && error.name === 'AbortError'
}

function formatDateTime(value?: string | null) {
  if (!value) return '--'
  const parsed = dayjs(value)
  return parsed.isValid() ? parsed.format('YYYY-MM-DD HH:mm:ss') : '--'
}

export default function LinklySettlementsPage() {
  const { t } = useTranslation()
  const initialBusinessDate = useMemo(() => getDefaultLinklySettlementDateRange()[0], [])
  const [form] = Form.useForm<LinklySettlementFormValues>()
  const [loading, setLoading] = useState(false)
  const [exporting, setExporting] = useState(false)
  const [items, setItems] = useState<LinklySettlementListItem[]>([])
  const [total, setTotal] = useState(0)
  const [pageNumber, setPageNumber] = useState(1)
  const [pageSize, setPageSize] = useState(DEFAULT_LINKLY_SETTLEMENT_PAGE_SIZE)
  const [sortBy, setSortBy] = useState(DEFAULT_LINKLY_SETTLEMENT_SORT.sortBy as string)
  const [sortOrder, setSortOrder] = useState<LinklySettlementSortOrder>(
    DEFAULT_LINKLY_SETTLEMENT_SORT.sortOrder,
  )
  const [storeOptions, setStoreOptions] = useState<StoreOption[]>([])
  const [deviceCandidates, setDeviceCandidates] = useState<LinklySettlementDeviceOption[]>([])
  const selectedStoreCode = Form.useWatch('storeCode', form)
  const requestGuardRef = useRef(createLatestAbortableRequestGuard())
  const exportControllerRef = useRef<AbortController | null>(null)

  const getCurrentFilters = useCallback(
    (nextSortBy = sortBy, nextSortOrder = sortOrder): LinklySettlementFilters => {
      const values = form.getFieldsValue()
      const defaultRange = getDefaultLinklySettlementDateRange()
      const range = values.businessDateRange ?? [dayjs(defaultRange[0]), dayjs(defaultRange[1])]
      return {
        businessDateFrom: range[0].format('YYYY-MM-DD'),
        businessDateTo: range[1].format('YYYY-MM-DD'),
        storeCode: values.storeCode?.trim() || undefined,
        deviceCode: values.deviceCode?.trim() || undefined,
        connectionMode: values.connectionMode,
        environment: values.environment,
        status: values.status,
        providerSubmissionState: values.providerSubmissionState,
        keyword: values.keyword?.trim() || undefined,
        sortBy: nextSortBy,
        sortOrder: nextSortOrder,
      }
    },
    [form, sortBy, sortOrder],
  )

  const loadData = useCallback(async (
    nextPageNumber = pageNumber,
    nextPageSize = pageSize,
    nextSortBy = sortBy,
    nextSortOrder = sortOrder,
  ) => {
    const currentRequest = requestGuardRef.current.begin()
    setLoading(true)
    try {
      const result = await getLinklySettlements(
        buildLinklySettlementQuery(
          getCurrentFilters(nextSortBy, nextSortOrder),
          nextPageNumber,
          nextPageSize,
        ),
        currentRequest.signal,
      )
      if (!requestGuardRef.current.isLatest(currentRequest.requestId)) return
      setItems(result.items)
      setTotal(result.total)
      setPageNumber(result.pageNumber)
      setPageSize(result.pageSize)
    } catch (error) {
      if (!requestGuardRef.current.isLatest(currentRequest.requestId) || isAbortError(error)) return
      console.error(error)
      message.error(error instanceof Error ? error.message : t('linklySettlements.messages.loadFailed'))
    } finally {
      if (requestGuardRef.current.isLatest(currentRequest.requestId)) setLoading(false)
    }
  }, [getCurrentFilters, pageNumber, pageSize, sortBy, sortOrder, t])

  // 门店 / 设备下拉候选：失败只影响候选，不阻塞列表查询。
  useEffect(() => {
    const controller = new AbortController()
    void getActiveStores()
      .then((stores) => {
        if (!controller.signal.aborted) setStoreOptions(stores)
      })
      .catch((error) => console.error(error))
    void getLinklySettlementDevices(controller.signal)
      .then((devices) => {
        if (!controller.signal.aborted) setDeviceCandidates(devices)
      })
      .catch((error) => {
        if (!isAbortError(error)) console.error(error)
      })
    return () => controller.abort()
  }, [])

  const storeSelectOptions = useMemo(
    () => storeOptions.map((option) => ({
      value: option.value,
      label: option.label && option.label !== option.value ? `${option.value} · ${option.label}` : option.value,
    })),
    [storeOptions],
  )
  const deviceSelectOptions = useMemo(
    () => buildLinklyDeviceSelectOptions(deviceCandidates, selectedStoreCode),
    [deviceCandidates, selectedStoreCode],
  )

  // 换门店后，已选设备若不属于新门店则清空，避免组合出永远查不到的条件。
  const handleStoreChange = (nextStoreCode?: string) => {
    const deviceCode = form.getFieldValue('deviceCode') as string | undefined
    if (!deviceCode || !nextStoreCode) return
    const belongs = deviceCandidates.some(
      (item) => item.storeCode === nextStoreCode && item.deviceCode === deviceCode,
    )
    if (!belongs) form.setFieldValue('deviceCode', undefined)
  }

  useLayoutEffect(() => () => {
    requestGuardRef.current.abort()
    exportControllerRef.current?.abort()
  }, [])

  useEffect(() => {
    void loadData(1, DEFAULT_LINKLY_SETTLEMENT_PAGE_SIZE)
    // 初次进入仅加载一次；后续查询由筛选、分页和排序显式触发。
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [])

  const handleSearch = () => {
    setPageNumber(1)
    void loadData(1, pageSize, sortBy, sortOrder)
  }

  const handleReset = () => {
    // keepAlive 页面可能跨过本地午夜，重置时必须重新读取当前日历日。
    const resetRange = getDefaultLinklySettlementDateRange()
    form.resetFields()
    form.setFieldsValue({ businessDateRange: [dayjs(resetRange[0]), dayjs(resetRange[1])] })
    setPageNumber(1)
    setPageSize(DEFAULT_LINKLY_SETTLEMENT_PAGE_SIZE)
    setSortBy(DEFAULT_LINKLY_SETTLEMENT_SORT.sortBy)
    setSortOrder(DEFAULT_LINKLY_SETTLEMENT_SORT.sortOrder)
    void loadData(
      1,
      DEFAULT_LINKLY_SETTLEMENT_PAGE_SIZE,
      DEFAULT_LINKLY_SETTLEMENT_SORT.sortBy,
      DEFAULT_LINKLY_SETTLEMENT_SORT.sortOrder,
    )
  }

  const handleExport = async () => {
    const filters = getCurrentFilters()
    if (!canExportLinklySettlementRange(filters.businessDateFrom, filters.businessDateTo)) {
      message.warning(t('linklySettlements.messages.exportRangeInvalid'))
      return
    }

    exportControllerRef.current?.abort()
    const controller = new AbortController()
    exportControllerRef.current = controller
    setExporting(true)
    try {
      const result = await exportLinklySettlements(filters, controller.signal)
      downloadLinklySettlementExport(result)
      message.success(t('linklySettlements.messages.exportSuccess'))
    } catch (error) {
      if (isAbortError(error)) return
      console.error(error)
      message.error(error instanceof Error ? error.message : t('linklySettlements.messages.exportFailed'))
    } finally {
      if (exportControllerRef.current === controller) {
        exportControllerRef.current = null
        setExporting(false)
      }
    }
  }

  const amountCell = useCallback((
    record: LinklySettlementListItem,
    field: 'purchaseAmountMinor' | 'refundAmountMinor' | 'cashOutAmountMinor' | 'totalAmountMinor',
    showParseTag = false,
  ) => {
    const display = formatAmountMinor(getAmountMinor(record.amountSummary, field), record.amountParseStatus)
    const statusLabel = t(`linklySettlements.amountParseStatus.${record.amountParseStatus}`)
    return (
      <Space size={4}>
        <Tooltip title={display === '--' ? statusLabel : undefined}>
          <span style={AMOUNT_CELL_STYLE}>{display}</span>
        </Tooltip>
        {showParseTag ? (
          <Tag color={getAmountParseStatusColor(record.amountParseStatus)} style={{ marginInlineEnd: 0 }}>
            {statusLabel}
          </Tag>
        ) : null}
      </Space>
    )
  }, [t])

  const columns = useMemo<ColumnsType<LinklySettlementListItem>>(() => [
    {
      title: t('linklySettlements.columns.requestedAt'),
      dataIndex: 'requestedAtUtc',
      key: 'requestedAtUtc',
      width: 168,
      sorter: true,
      sortOrder: sortBy === 'requestedAtUtc' ? (sortOrder === 'asc' ? 'ascend' : 'descend') : null,
      render: (value: string) => formatDateTime(value),
    },
    {
      title: t('linklySettlements.columns.businessDate'),
      dataIndex: 'businessDate',
      key: 'businessDate',
      width: 112,
      sorter: true,
      sortOrder: sortBy === 'businessDate' ? (sortOrder === 'asc' ? 'ascend' : 'descend') : null,
    },
    {
      title: t('linklySettlements.columns.store'),
      dataIndex: 'storeCode',
      key: 'storeCode',
      width: 104,
      sorter: true,
      sortOrder: sortBy === 'storeCode' ? (sortOrder === 'asc' ? 'ascend' : 'descend') : null,
    },
    {
      title: t('linklySettlements.columns.device'),
      dataIndex: 'deviceCode',
      key: 'deviceCode',
      width: 130,
      sorter: true,
      sortOrder: sortBy === 'deviceCode' ? (sortOrder === 'asc' ? 'ascend' : 'descend') : null,
    },
    {
      title: t('linklySettlements.columns.status'),
      key: 'status',
      width: 156,
      render: (_, record) => (
        <Space direction="vertical" size={2}>
          <Tag color={getSettlementStatusColor(record.status)}>{t(`linklySettlements.status.${record.status}`)}</Tag>
          <Tag color={getProviderSubmissionColor(record.providerSubmissionState)}>
            {record.providerSubmissionState
              ? t(`linklySettlements.submissionState.${record.providerSubmissionState}`)
              : '--'}
          </Tag>
        </Space>
      ),
    },
    {
      title: t('linklySettlements.columns.modeEnvironment'),
      key: 'modeEnvironment',
      width: 165,
      render: (_, record) => (
        <Space direction="vertical" size={2}>
          <Typography.Text>{t(`linklySettlements.connectionMode.${record.connectionMode}`)}</Typography.Text>
          <Tag color={record.environment === 'Production' ? 'blue' : 'orange'}>
            {t(`linklySettlements.environment.${record.environment}`)}
          </Tag>
        </Space>
      ),
    },
    {
      title: t('linklySettlements.columns.purchase'),
      key: 'purchaseAmountMinor',
      width: 112,
      align: 'right',
      render: (_, record) => amountCell(record, 'purchaseAmountMinor'),
    },
    {
      title: t('linklySettlements.columns.refund'),
      key: 'refundAmountMinor',
      width: 112,
      align: 'right',
      render: (_, record) => amountCell(record, 'refundAmountMinor'),
    },
    {
      title: t('linklySettlements.columns.cashOut'),
      key: 'cashOutAmountMinor',
      width: 112,
      align: 'right',
      render: (_, record) => amountCell(record, 'cashOutAmountMinor'),
    },
    {
      title: t('linklySettlements.columns.net'),
      key: 'totalAmountMinor',
      width: 174,
      align: 'right',
      render: (_, record) => amountCell(record, 'totalAmountMinor', true),
    },
    {
      title: t('linklySettlements.columns.receipts'),
      dataIndex: 'receiptCount',
      key: 'receiptCount',
      width: 88,
      align: 'right',
      render: (value: number) => <span style={{ fontVariantNumeric: 'tabular-nums' }}>{value}</span>,
    },
    {
      title: t('linklySettlements.columns.prints'),
      dataIndex: 'printCount',
      key: 'printCount',
      width: 80,
      align: 'right',
      render: (value: number) => <span style={{ fontVariantNumeric: 'tabular-nums' }}>{value}</span>,
    },
    {
      title: t('linklySettlements.columns.response'),
      key: 'response',
      width: 220,
      render: (_, record) => (
        <Space direction="vertical" size={2} style={{ maxWidth: 200 }}>
          <Typography.Text code>{record.responseCode || '--'}</Typography.Text>
          <Typography.Text ellipsis={{ tooltip: record.responseText }} type="secondary">
            {record.responseText || '--'}
          </Typography.Text>
        </Space>
      ),
    },
    {
      title: t('linklySettlements.columns.detail'),
      key: 'detail',
      width: 76,
      fixed: 'right',
      render: (_, record) => (
        <Link to={`/pos-admin/linkly-settlements/${record.id}`}>
          {t('linklySettlements.actions.view')}
        </Link>
      ),
    },
  ], [amountCell, sortBy, sortOrder, t])

  const handleTableChange: TableProps<LinklySettlementListItem>['onChange'] = (
    pagination,
    _filters,
    sorter,
  ) => {
    const currentSorter = Array.isArray(sorter) ? sorter[0] : sorter
    const sorting = currentSorter?.order && typeof currentSorter.field === 'string'
    const nextSortBy = sorting ? currentSorter.field as string : sortBy
    const nextSortOrder: LinklySettlementSortOrder = sorting && currentSorter.order === 'ascend'
      ? 'asc'
      : sorting
        ? 'desc'
        : sortOrder
    const sortChanged = nextSortBy !== sortBy || nextSortOrder !== sortOrder
    const nextPage = sortChanged ? 1 : (pagination.current ?? 1)
    const nextPageSize = pagination.pageSize ?? pageSize
    setPageNumber(nextPage)
    setPageSize(nextPageSize)
    setSortBy(nextSortBy)
    setSortOrder(nextSortOrder)
    void loadData(nextPage, nextPageSize, nextSortBy, nextSortOrder)
  }

  return (
    <PageContainer
      title={t('linklySettlements.title')}
      subtitle={t('linklySettlements.subtitle')}
      extra={(
        <Button icon={<DownloadOutlined />} loading={exporting} onClick={() => void handleExport()}>
          {t('linklySettlements.actions.export')}
        </Button>
      )}
    >
      <Space direction="vertical" size={12} style={{ width: '100%' }}>
        <Alert type="info" showIcon message={t('linklySettlements.syncedOnlyNotice')} />
        <Card size="small">
          <Form<LinklySettlementFormValues>
            form={form}
            layout="vertical"
            initialValues={{ businessDateRange: [dayjs(initialBusinessDate), dayjs(initialBusinessDate)] }}
            onFinish={handleSearch}
          >
            <Row gutter={[12, 0]}>
              <Col xs={24} sm={24} md={12} xl={8}>
                <Form.Item name="businessDateRange" label={t('linklySettlements.filters.businessDate')}>
                  <RangePicker allowClear={false} style={{ width: '100%' }} />
                </Form.Item>
              </Col>
              <Col xs={24} sm={12} md={6} xl={4}>
                <Form.Item name="storeCode" label={t('linklySettlements.filters.store')}>
                  <Select
                    allowClear
                    showSearch
                    optionFilterProp="label"
                    placeholder={t('linklySettlements.filters.storePlaceholder')}
                    options={storeSelectOptions}
                    onChange={handleStoreChange}
                  />
                </Form.Item>
              </Col>
              <Col xs={24} sm={12} md={6} xl={4}>
                <Form.Item name="deviceCode" label={t('linklySettlements.filters.device')}>
                  <Select
                    allowClear
                    showSearch
                    optionFilterProp="label"
                    placeholder={t('linklySettlements.filters.devicePlaceholder')}
                    options={deviceSelectOptions}
                  />
                </Form.Item>
              </Col>
              <Col xs={24} sm={12} md={6} xl={4}>
                <Form.Item name="connectionMode" label={t('linklySettlements.filters.connectionMode')}>
                  <Select allowClear options={(['LocalIp', 'CloudDirectSync', 'CloudBackendAsync'] as const).map((value) => ({
                    value,
                    label: t(`linklySettlements.connectionMode.${value}`),
                  }))} />
                </Form.Item>
              </Col>
              <Col xs={24} sm={12} md={6} xl={4}>
                <Form.Item name="environment" label={t('linklySettlements.filters.environment')}>
                  <Select allowClear options={(['Production', 'Sandbox'] as const).map((value) => ({
                    value,
                    label: t(`linklySettlements.environment.${value}`),
                  }))} />
                </Form.Item>
              </Col>
              <Col xs={24} sm={12} md={6} xl={4}>
                <Form.Item name="status" label={t('linklySettlements.filters.status')}>
                  <Select allowClear options={(['Pending', 'Succeeded', 'Failed', 'Unknown'] as const).map((value) => ({
                    value,
                    label: t(`linklySettlements.status.${value}`),
                  }))} />
                </Form.Item>
              </Col>
              <Col xs={24} sm={12} md={6} xl={4}>
                <Form.Item name="providerSubmissionState" label={t('linklySettlements.filters.submissionState')}>
                  <Select allowClear options={(['NotSubmitted', 'Submitted', 'Unknown'] as const).map((value) => ({
                    value,
                    label: t(`linklySettlements.submissionState.${value}`),
                  }))} />
                </Form.Item>
              </Col>
              <Col xs={24} sm={16} md={12} xl={8}>
                <Form.Item name="keyword" label={t('linklySettlements.filters.keyword')}>
                  <Input allowClear placeholder={t('linklySettlements.filters.keywordPlaceholder')} />
                </Form.Item>
              </Col>
              <Col xs={24} sm={8} md={6} xl={4}>
                <Form.Item label=" ">
                  <Space wrap>
                    <Button type="primary" htmlType="submit" icon={<SearchOutlined />}>
                      {t('linklySettlements.actions.search')}
                    </Button>
                    <Button icon={<ReloadOutlined />} onClick={handleReset}>
                      {t('linklySettlements.actions.reset')}
                    </Button>
                  </Space>
                </Form.Item>
              </Col>
            </Row>
          </Form>
        </Card>
        <Card size="small">
          <MeasuredTable<LinklySettlementListItem> metricId="pos-admin.linkly-settlements.table-1"
            rowKey={(record) => record.id}
            size="small"
            loading={loading}
            columns={columns}
            dataSource={items}
            scroll={{ x: 1910 }}
            pagination={{
              current: pageNumber,
              pageSize,
              total,
              showSizeChanger: true,
              showTotal: (count) => t('linklySettlements.paginationTotal', { count }),
            }}
            onChange={handleTableChange}
          />
        </Card>
      </Space>
    </PageContainer>
  )
}
