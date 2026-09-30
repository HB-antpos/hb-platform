import { EyeOutlined, ReloadOutlined, SearchOutlined, WarningOutlined } from '@ant-design/icons'
import {
  Alert,
  Button,
  Card,
  Col,
  DatePicker,
  Descriptions,
  Drawer,
  Empty,
  Form,
  Input,
  Row,
  Select,
  Space,
  Spin,
  Statistic,
  Tag,
  Timeline,
  Typography,
  message,
  theme,
} from 'antd'
import type { RangePickerProps } from 'antd/es/date-picker'
import type { ColumnsType, TableProps } from 'antd/es/table'
import dayjs from 'dayjs'
import { useCallback, useEffect, useMemo, useRef, useState, type ReactNode } from 'react'
import { useTranslation } from 'react-i18next'

import { MeasuredTable } from '../../../components/MeasuredTable'
import PageContainer from '../../../components/PageContainer'
import { registerPageMessages } from '../../../i18n/registerPageMessages'
import { getLegacyEmployeeLogContext, getLegacyEmployeeLogs } from '../../../services/legacyEmployeeLogService'
import { getActiveStores, type StoreOption } from '../../../services/storeService'
import { useAuthStore } from '../../../store/auth'
import type {
  LegacyEmployeeLogContext,
  LegacyEmployeeLogItem,
  LegacyEmployeeLogListResult,
} from '../../../types/legacyEmployeeLog'
import {
  buildStoreOptionsFromUserStores,
  filterStoreOptionsByManagedCodes,
} from '../../../utils/managedStoreScope'

import {
  CATEGORY_TAG_COLOR,
  LEGACY_LOG_DEFAULT_PAGE_SIZE,
  LEGACY_LOG_LATE_UPLOAD_MINUTES,
  LEGACY_LOG_STORE_STORAGE_KEY,
  QUICK_FILTER_OPERATIONS,
  buildLegacyLogQuery,
  buildOperationOptions,
  createLatestRequestGuard,
  getDayRange,
  getOperationCategory,
  getUploadLagMinutes,
  isHighRiskOperation,
  isQuickFilterActive,
  parseLegacyDetail,
  splitLagMinutes,
  sumOperationCounts,
  validateTimeRange,
  type LegacyLogFormValues,
  type LegacyQuickFilterKey,
  type ParsedLegacyDetail,
} from './legacyEmployeeLogsLogic'
import legacyEmployeeLogsMessagesEn from './legacyEmployeeLogsMessages.en.json'
import legacyEmployeeLogsMessagesZh from './legacyEmployeeLogsMessages.zh.json'

// 页面文案随页面代码块懒加载，不进首屏 i18n 包。
registerPageMessages({ zh: legacyEmployeeLogsMessagesZh, en: legacyEmployeeLogsMessagesEn })

const QUICK_FILTER_KEYS: LegacyQuickFilterKey[] = ['highRisk', 'price', 'return', 'auth', 'payment']
const PRICE_OPERATIONS = QUICK_FILTER_OPERATIONS.price
type TableSortOrder = 'ascend' | 'descend'

function readRememberedStore() {
  try {
    return localStorage.getItem(LEGACY_LOG_STORE_STORAGE_KEY) ?? undefined
  } catch {
    return undefined
  }
}

function rememberStore(storeCode: string) {
  try {
    localStorage.setItem(LEGACY_LOG_STORE_STORAGE_KEY, storeCode)
  } catch {
    // 浏览器存储不可用时只是不记住上次的分店，不影响查询。
  }
}

function errorText(error: unknown) {
  return error instanceof Error && error.message ? error.message : undefined
}

export default function PosAdminLegacyEmployeeLogsPage() {
  const { t } = useTranslation()
  const { token } = theme.useToken()
  const [form] = Form.useForm<LegacyLogFormValues>()
  const access = useAuthStore((state) => state.access)
  const currentUser = useAuthStore((state) => state.currentUser)
  const managedStoreCodes = access.managedStoreCodes?.()
  const managedStoreCodeKey = managedStoreCodes?.join(',') ?? 'all'
  const [storeOptions, setStoreOptions] = useState<StoreOption[]>([])
  const [loading, setLoading] = useState(false)
  const [loadError, setLoadError] = useState<string | null>(null)
  const [result, setResult] = useState<LegacyEmployeeLogListResult | null>(null)
  const [pageNumber, setPageNumber] = useState(1)
  const [pageSize, setPageSize] = useState(LEGACY_LOG_DEFAULT_PAGE_SIZE)
  const [sortOrder, setSortOrder] = useState<TableSortOrder>('descend')
  const [detailRecord, setDetailRecord] = useState<LegacyEmployeeLogItem | null>(null)
  const [context, setContext] = useState<LegacyEmployeeLogContext | null>(null)
  const [contextLoading, setContextLoading] = useState(false)
  const [contextError, setContextError] = useState(false)
  const listGuardRef = useRef(createLatestRequestGuard())
  const contextGuardRef = useRef(createLatestRequestGuard())
  const selectedOperations = Form.useWatch('operations', form)
  const selectedStoreCode = Form.useWatch('storeCode', form)

  const visibleStoreOptions = useMemo(
    () =>
      filterStoreOptionsByManagedCodes(storeOptions, managedStoreCodes).map((option) => ({
        value: option.value,
        label: option.label && option.label !== option.value ? `${option.value} · ${option.label}` : option.value,
      })),
    [managedStoreCodes, storeOptions],
  )

  useEffect(() => {
    if (managedStoreCodes !== null) {
      // 店长直接使用当前会话已授权的可管理门店，避免依赖 Stores.View 全店列表权限；服务端仍会再次校验。
      setStoreOptions(buildStoreOptionsFromUserStores(currentUser?.stores, { manageableOnly: true }))
      return
    }
    let disposed = false
    void getActiveStores()
      .then((stores) => {
        if (!disposed) setStoreOptions(stores)
      })
      .catch((error) => {
        console.error(error)
        if (!disposed) message.error(t('legacyEmployeeLogs.loadStoresFailed'))
      })
    return () => {
      disposed = true
    }
  }, [currentUser?.stores, managedStoreCodeKey, t])

  const loadData = useCallback(
    async (nextPage: number, nextPageSize: number, nextSortOrder: TableSortOrder) => {
      const values = form.getFieldsValue()
      if (!values.storeCode) {
        setResult(null)
        return
      }
      const rangeError = validateTimeRange(values.timeRange)
      if (rangeError) {
        message.warning(t(`legacyEmployeeLogs.validation.${rangeError === 'required' ? 'rangeRequired' : rangeError === 'reversed' ? 'rangeReversed' : 'rangeTooLong'}`))
        return
      }
      const query = buildLegacyLogQuery(values, {
        pageNumber: nextPage,
        pageSize: nextPageSize,
        sortOrder: nextSortOrder === 'ascend' ? 'asc' : 'desc',
      })
      if (!query) return

      const requestId = listGuardRef.current.begin()
      setLoading(true)
      setLoadError(null)
      try {
        const data = await getLegacyEmployeeLogs(query)
        if (!listGuardRef.current.isLatest(requestId)) return
        setResult(data)
        setPageNumber(data.pageNumber)
        setPageSize(data.pageSize)
        rememberStore(query.storeCode)
      } catch (error) {
        if (!listGuardRef.current.isLatest(requestId)) return
        console.error(error)
        setLoadError(errorText(error) ?? t('legacyEmployeeLogs.loadFailed'))
      } finally {
        if (listGuardRef.current.isLatest(requestId)) setLoading(false)
      }
    },
    [form, t],
  )

  // 分店列表就绪后选定默认分店：优先上次查询的分店，只有一个可选分店时直接选中，然后自动查询。
  useEffect(() => {
    if (form.getFieldValue('storeCode') || visibleStoreOptions.length === 0) return
    const remembered = readRememberedStore()
    const initial = visibleStoreOptions.find((option) => option.value === remembered)?.value
      ?? (visibleStoreOptions.length === 1 ? visibleStoreOptions[0].value : undefined)
    // 只设字段；查询由下面的分店变化监听统一发起。
    if (initial) form.setFieldsValue({ storeCode: initial })
  }, [form, visibleStoreOptions])

  // 分店变化（默认选中或用户切换）后立即查询。员工与设备选项属于上一家分店，换店时先清空，避免带着别家员工条件查询。
  const lastStoreRef = useRef<string | undefined>(undefined)
  useEffect(() => {
    if (!selectedStoreCode || selectedStoreCode === lastStoreRef.current) return
    if (lastStoreRef.current) {
      form.setFieldsValue({ employeeIds: undefined, deviceCode: undefined })
      setResult(null)
    }
    lastStoreRef.current = selectedStoreCode
    setPageNumber(1)
    void loadData(1, pageSize, sortOrder)
    // 只在分店变化时触发；分页与排序变化各自发起查询。
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [selectedStoreCode])

  const initialValues = useMemo<LegacyLogFormValues>(() => ({ timeRange: getDayRange(dayjs()) }), [])

  const runQuery = () => {
    setPageNumber(1)
    void loadData(1, pageSize, sortOrder)
  }

  const handleReset = () => {
    const storeCode = form.getFieldValue('storeCode') as string | undefined
    form.resetFields()
    // 重置只清筛选条件，保留所选分店，避免重置后页面变成空白。
    form.setFieldsValue({ storeCode, timeRange: getDayRange(dayjs()) })
    setSortOrder('descend')
    setPageNumber(1)
    void loadData(1, pageSize, 'descend')
  }

  const applyQuickFilter = (key: LegacyQuickFilterKey) => {
    const active = isQuickFilterActive(key, selectedOperations)
    form.setFieldsValue({ operations: active ? undefined : [...QUICK_FILTER_OPERATIONS[key]] })
    runQuery()
  }

  const handleTableChange: NonNullable<TableProps<LegacyEmployeeLogItem>['onChange']> = (pagination, _filters, sorter) => {
    const nextSorter = Array.isArray(sorter) ? sorter[0] : sorter
    const nextOrder: TableSortOrder = nextSorter?.order === 'ascend' ? 'ascend' : 'descend'
    const orderChanged = nextOrder !== sortOrder
    const nextSize = pagination.pageSize ?? pageSize
    // 排序或每页条数变化时回到第一页。
    const nextPage = orderChanged || nextSize !== pageSize ? 1 : pagination.current ?? pageNumber
    setSortOrder(nextOrder)
    setPageSize(nextSize)
    setPageNumber(nextPage)
    void loadData(nextPage, nextSize, nextOrder)
  }

  const openDetail = async (record: LegacyEmployeeLogItem) => {
    setDetailRecord(record)
    setContext(null)
    setContextError(false)
    setContextLoading(true)
    const requestId = contextGuardRef.current.begin()
    try {
      const data = await getLegacyEmployeeLogContext(record.id)
      if (contextGuardRef.current.isLatest(requestId)) setContext(data)
    } catch (error) {
      console.error(error)
      if (contextGuardRef.current.isLatest(requestId)) setContextError(true)
    } finally {
      if (contextGuardRef.current.isLatest(requestId)) setContextLoading(false)
    }
  }

  const closeDetail = () => {
    contextGuardRef.current.begin()
    setDetailRecord(null)
  }

  const showEmployeeDay = (record: LegacyEmployeeLogItem) => {
    if (!record.employeeId) return
    form.setFieldsValue({
      timeRange: getDayRange(dayjs(record.operationTime)),
      employeeIds: [record.employeeId],
      deviceCode: undefined,
      operations: undefined,
      keyword: undefined,
    })
    closeDetail()
    runQuery()
  }

  const formatLag = useCallback(
    (record: LegacyEmployeeLogItem) => {
      const minutes = getUploadLagMinutes(record.operationTime, record.lastUploadTime)
      if (minutes === null) return { text: t('legacyEmployeeLogs.lag.unknown'), late: false }
      const parts = splitLagMinutes(minutes)
      const text = parts.days > 0
        ? t('legacyEmployeeLogs.lag.days', parts)
        : parts.hours > 0
          ? t('legacyEmployeeLogs.lag.hours', parts)
          : t('legacyEmployeeLogs.lag.minutes', parts)
      return { text, late: minutes >= LEGACY_LOG_LATE_UPLOAD_MINUTES }
    },
    [t],
  )

  const operationTag = useCallback(
    (operation?: string | null) =>
      operation ? <Tag color={CATEGORY_TAG_COLOR[getOperationCategory(operation)]} style={{ marginInlineEnd: 0 }}>{operation}</Tag> : '-',
    [],
  )

  const renderParsedDetail = useCallback(
    (parsed: ParsedLegacyDetail, raw?: string | null): ReactNode => {
      if (!parsed.productName && parsed.fields.length === 0 && !parsed.orderGuid) {
        return raw || '-'
      }
      const toneColor = (tone?: string) => (tone === 'danger' ? token.colorError : tone === 'money' ? token.colorSuccess : token.colorText)
      return (
        <span style={{ display: 'inline', lineHeight: '24px' }}>
          {parsed.texts.length ? <span style={{ marginInlineEnd: 6 }}>{parsed.texts.join('，')}</span> : null}
          {parsed.productName ? <span style={{ marginInlineEnd: 6, fontWeight: 500 }}>{parsed.productName}</span> : null}
          {parsed.orderGuid ? (
            <Typography.Text code copyable={{ text: parsed.orderGuid }} style={{ fontSize: 12, marginInlineEnd: 6 }}>
              {`${parsed.orderGuid.slice(0, 8)}…${parsed.orderGuid.slice(-4)}`}
            </Typography.Text>
          ) : null}
          {parsed.fields.map((field) => (
            <span
              key={`${field.key}-${field.value}`}
              style={{
                display: 'inline-block',
                background: token.colorFillTertiary,
                borderRadius: token.borderRadiusSM,
                padding: '0 6px',
                marginInlineEnd: 4,
                fontSize: 12,
                lineHeight: '20px',
                color: token.colorTextSecondary,
              }}
            >
              {field.key}{' '}
              <b style={{ color: toneColor(field.tone), fontWeight: 600 }}>{field.value}</b>
            </span>
          ))}
        </span>
      )
    },
    [token],
  )

  const columns = useMemo<ColumnsType<LegacyEmployeeLogItem>>(
    () => [
      {
        title: t('legacyEmployeeLogs.columns.time'),
        dataIndex: 'operationTime',
        key: 'operationTime',
        width: 130,
        sorter: true,
        sortOrder,
        render: (value: string, record) => {
          const time = dayjs(value)
          const risky = isHighRiskOperation(record.operation)
          return (
            <div
              style={{
                borderInlineStart: `3px solid ${risky ? token.colorError : 'transparent'}`,
                paddingInlineStart: 8,
                marginInlineStart: -8,
                fontVariantNumeric: 'tabular-nums',
                whiteSpace: 'nowrap',
              }}
            >
              <div>{time.format('HH:mm:ss')}</div>
              <Typography.Text type="secondary" style={{ fontSize: 12 }}>{time.format('YYYY-MM-DD ddd')}</Typography.Text>
            </div>
          )
        },
      },
      {
        title: t('legacyEmployeeLogs.columns.device'),
        dataIndex: 'deviceCode',
        key: 'deviceCode',
        width: 140,
        render: (value?: string | null) => <Typography.Text style={{ fontSize: 12 }} code={Boolean(value)}>{value || '-'}</Typography.Text>,
      },
      {
        title: t('legacyEmployeeLogs.columns.employee'),
        dataIndex: 'employeeName',
        key: 'employeeName',
        width: 130,
        render: (value?: string | null) => value || '-',
      },
      {
        title: t('legacyEmployeeLogs.columns.operation'),
        dataIndex: 'operation',
        key: 'operation',
        width: 150,
        render: (value?: string | null) => operationTag(value),
      },
      {
        title: t('legacyEmployeeLogs.columns.detail'),
        dataIndex: 'operationDetail',
        key: 'operationDetail',
        render: (value: string | null | undefined, record) => renderParsedDetail(parseLegacyDetail(value, record.operation), value),
      },
      {
        title: t('legacyEmployeeLogs.columns.lag'),
        key: 'lag',
        width: 120,
        render: (_, record) => {
          const lag = formatLag(record)
          return <span style={{ fontSize: 12, color: lag.late ? token.colorWarning : token.colorTextSecondary }}>{lag.text}</span>
        },
      },
      {
        title: t('legacyEmployeeLogs.columns.action'),
        key: 'action',
        width: 80,
        fixed: 'right',
        render: (_, record) => (
          <Button
            type="link"
            size="small"
            icon={<EyeOutlined />}
            onClick={(event) => {
              event.stopPropagation()
              void openDetail(record)
            }}
          >
            {t('common.view')}
          </Button>
        ),
      },
    ],
    [formatLag, operationTag, renderParsedDetail, sortOrder, t, token],
  )

  const operationOptions = useMemo(
    () =>
      buildOperationOptions(result?.operationCounts ?? []).map((row) => ({
        value: row.operation,
        label: result ? `${row.operation} (${row.count})` : row.operation,
      })),
    [result],
  )

  const employeeOptions = useMemo(
    () =>
      (result?.employees ?? [])
        .filter((row) => row.employeeId)
        .map((row) => ({ value: row.employeeId!, label: `${row.employeeName || row.employeeId} (${row.count})` })),
    [result],
  )

  const deviceOptions = useMemo(
    () =>
      (result?.devices ?? [])
        .filter((row) => row.deviceCode)
        .map((row) => ({ value: row.deviceCode!, label: `${row.deviceCode} (${row.count})` })),
    [result],
  )

  const timeRangePresets: RangePickerProps['presets'] = [
    { label: t('legacyEmployeeLogs.presets.today'), value: getDayRange(dayjs()) },
    { label: t('legacyEmployeeLogs.presets.yesterday'), value: getDayRange(dayjs().subtract(1, 'day')) },
    { label: t('legacyEmployeeLogs.presets.last7Days'), value: [dayjs().subtract(6, 'day').startOf('day'), getDayRange(dayjs())[1]] },
    { label: t('legacyEmployeeLogs.presets.last31Days'), value: [dayjs().subtract(30, 'day').startOf('day'), getDayRange(dayjs())[1]] },
  ]

  const counts = result?.operationCounts ?? []
  const detailParsed = detailRecord ? parseLegacyDetail(detailRecord.operationDetail, detailRecord.operation) : null
  const detailLag = detailRecord ? formatLag(detailRecord) : null

  return (
    <PageContainer
      title={t('legacyEmployeeLogs.pageTitle')}
      subtitle={t('legacyEmployeeLogs.pageSubtitle')}
      extra={(
        <Button icon={<ReloadOutlined />} disabled={!selectedStoreCode} onClick={() => void loadData(pageNumber, pageSize, sortOrder)}>
          {t('common.refresh')}
        </Button>
      )}
    >
      <Space direction="vertical" size={16} style={{ width: '100%' }}>
        <Alert type="info" showIcon message={t('legacyEmployeeLogs.wallClockNotice')} />

        <Card>
          <Form form={form} layout="vertical" initialValues={initialValues} onFinish={runQuery}>
            <Row gutter={16}>
              <Col xs={24} lg={12}>
                <Form.Item label={t('legacyEmployeeLogs.filters.timeRange')} name="timeRange" required>
                  <DatePicker.RangePicker showTime={{ format: 'HH:mm' }} format="YYYY-MM-DD HH:mm" presets={timeRangePresets} allowClear={false} style={{ width: '100%' }} />
                </Form.Item>
              </Col>
              <Col xs={24} sm={12} lg={6}>
                <Form.Item
                  label={t('legacyEmployeeLogs.filters.store')}
                  name="storeCode"
                  rules={[{ required: true, message: t('legacyEmployeeLogs.validation.storeRequired') }]}
                >
                  <Select showSearch optionFilterProp="label" options={visibleStoreOptions} placeholder={t('legacyEmployeeLogs.filters.storePlaceholder')} />
                </Form.Item>
              </Col>
              <Col xs={24} sm={12} lg={6}>
                <Form.Item label={t('legacyEmployeeLogs.filters.device')} name="deviceCode" tooltip={t('legacyEmployeeLogs.filters.optionsHint')}>
                  <Select allowClear showSearch options={deviceOptions} placeholder={t('legacyEmployeeLogs.filters.devicePlaceholder')} />
                </Form.Item>
              </Col>
              <Col xs={24} sm={12} lg={6}>
                <Form.Item label={t('legacyEmployeeLogs.filters.employee')} name="employeeIds" tooltip={t('legacyEmployeeLogs.filters.optionsHint')}>
                  <Select
                    mode="multiple"
                    allowClear
                    maxTagCount="responsive"
                    optionFilterProp="label"
                    options={employeeOptions}
                    placeholder={t('legacyEmployeeLogs.filters.employeePlaceholder')}
                  />
                </Form.Item>
              </Col>
              <Col xs={24} sm={12} lg={12}>
                <Form.Item label={t('legacyEmployeeLogs.filters.operation')} name="operations">
                  <Select
                    mode="multiple"
                    allowClear
                    maxTagCount="responsive"
                    optionFilterProp="value"
                    options={operationOptions}
                    placeholder={t('legacyEmployeeLogs.filters.operationPlaceholder')}
                  />
                </Form.Item>
              </Col>
              <Col xs={24} lg={6}>
                <Form.Item label={t('legacyEmployeeLogs.filters.keyword')} name="keyword">
                  <Input allowClear maxLength={100} placeholder={t('legacyEmployeeLogs.filters.keywordPlaceholder')} />
                </Form.Item>
              </Col>
            </Row>
            <div
              style={{
                display: 'flex',
                flexWrap: 'wrap',
                alignItems: 'center',
                gap: 8,
                paddingTop: 12,
                borderTop: `1px dashed ${token.colorSplit}`,
              }}
            >
              <Typography.Text type="secondary">{t('legacyEmployeeLogs.quick.label')}</Typography.Text>
              {QUICK_FILTER_KEYS.map((key) => {
                const active = isQuickFilterActive(key, selectedOperations)
                return (
                  <Tag.CheckableTag
                    key={key}
                    checked={active}
                    onChange={() => applyQuickFilter(key)}
                    style={{ border: `1px solid ${active ? token.colorPrimary : token.colorBorder}`, borderRadius: 12, paddingInline: 10 }}
                  >
                    {key === 'highRisk' ? <WarningOutlined style={{ marginInlineEnd: 4 }} /> : null}
                    {t(`legacyEmployeeLogs.quick.${key}`)}
                  </Tag.CheckableTag>
                )
              })}
              <Space style={{ marginInlineStart: 'auto' }}>
                <Button onClick={handleReset}>{t('common.reset')}</Button>
                <Button type="primary" htmlType="submit" icon={<SearchOutlined />} loading={loading}>
                  {t('common.query')}
                </Button>
              </Space>
            </div>
          </Form>
        </Card>

        {loadError ? (
          <Alert
            type="error"
            showIcon
            message={t('legacyEmployeeLogs.loadFailed')}
            description={loadError}
            action={<Button onClick={() => void loadData(pageNumber, pageSize, sortOrder)}>{t('legacyEmployeeLogs.retry')}</Button>}
          />
        ) : null}

        {result ? (
          <Card styles={{ body: { padding: 0 } }}>
            <Row>
              {[
                { key: 'total', value: result.total, suffix: t('legacyEmployeeLogs.stats.unitRows') },
                { key: 'deleted', value: sumOperationCounts(counts, ['删除商品']), danger: true },
                { key: 'priceChanges', value: sumOperationCounts(counts, PRICE_OPERATIONS), danger: true },
                { key: 'cashDrawer', value: sumOperationCounts(counts, ['开钱箱']), danger: true },
              ].map((item) => (
                <Col key={item.key} xs={12} md={6} lg={5} style={{ padding: '14px 20px', borderInlineEnd: `1px solid ${token.colorSplit}` }}>
                  <Statistic
                    title={item.key === 'total' ? t('legacyEmployeeLogs.stats.total') : t(`legacyEmployeeLogs.stats.${item.key}`)}
                    value={item.value}
                    suffix={item.suffix}
                    valueStyle={{ fontSize: 22, color: item.danger && item.value > 0 ? token.colorError : undefined }}
                  />
                </Col>
              ))}
              <Col xs={24} md={24} lg={4} style={{ padding: '14px 20px' }}>
                <Statistic
                  title={t('legacyEmployeeLogs.stats.scope')}
                  valueRender={() => (
                    <span style={{ fontSize: 22 }}>
                      {result.employees.length}
                      <Typography.Text type="secondary" style={{ fontSize: 13, marginInline: 4 }}>{t('legacyEmployeeLogs.stats.unitPeople')}</Typography.Text>
                      · {result.devices.length}
                      <Typography.Text type="secondary" style={{ fontSize: 13, marginInlineStart: 4 }}>{t('legacyEmployeeLogs.stats.unitDevices')}</Typography.Text>
                    </span>
                  )}
                />
              </Col>
            </Row>
          </Card>
        ) : null}

        <Card
          title={(
            <Space size={8}>
              <span>{t('legacyEmployeeLogs.table.title')}</span>
              <Typography.Text type="secondary" style={{ fontSize: 13, fontWeight: 400 }}>{t('legacyEmployeeLogs.table.hint')}</Typography.Text>
            </Space>
          )}
          extra={result && selectedOperations?.length ? (
            <Typography.Text type="secondary" style={{ fontSize: 12 }}>{t('legacyEmployeeLogs.stats.basisHint')}</Typography.Text>
          ) : null}
        >
          <MeasuredTable<LegacyEmployeeLogItem>
            metricId="pos-admin.legacy-employee-logs.table-1"
            rowKey="id"
            size="middle"
            loading={loading}
            columns={columns}
            dataSource={result?.items ?? []}
            scroll={{ x: 1000 }}
            sortDirections={['descend', 'ascend', 'descend']}
            locale={{
              emptyText: (
                <Empty
                  image={Empty.PRESENTED_IMAGE_SIMPLE}
                  description={selectedStoreCode ? t('legacyEmployeeLogs.table.empty') : t('legacyEmployeeLogs.table.selectStoreFirst')}
                />
              ),
            }}
            onRow={(record) => ({
              onClick: () => void openDetail(record),
              style: {
                cursor: 'pointer',
                background: detailRecord?.id === record.id ? token.colorPrimaryBg : undefined,
              },
            })}
            pagination={{
              current: pageNumber,
              pageSize,
              total: result?.total ?? 0,
              showSizeChanger: true,
              pageSizeOptions: [20, 50, 100, 200],
              showTotal: (value) => t('legacyEmployeeLogs.table.paginationTotal', { total: value }),
            }}
            onChange={handleTableChange}
          />
        </Card>
      </Space>

      <Drawer
        open={Boolean(detailRecord)}
        onClose={closeDetail}
        width="min(560px, 100vw)"
        title={t('legacyEmployeeLogs.detail.title')}
        extra={detailRecord && isHighRiskOperation(detailRecord.operation) ? (
          <Tag color="error" icon={<WarningOutlined />}>{t('legacyEmployeeLogs.detail.highRisk')}</Tag>
        ) : null}
      >
        {detailRecord && detailParsed && detailLag ? (
          <Space direction="vertical" size={20} style={{ width: '100%' }}>
            <Descriptions bordered size="small" column={1} labelStyle={{ width: 110 }}>
              <Descriptions.Item label={t('legacyEmployeeLogs.detail.operationTime')}>
                {dayjs(detailRecord.operationTime).format('YYYY-MM-DD HH:mm:ss')}
                <Typography.Text type="secondary">{`（${t('legacyEmployeeLogs.detail.localTime')}）`}</Typography.Text>
              </Descriptions.Item>
              <Descriptions.Item label={t('legacyEmployeeLogs.detail.uploadTime')}>
                {dayjs(detailRecord.lastUploadTime).format('YYYY-MM-DD HH:mm:ss')}
                <Typography.Text type={detailLag.late ? 'warning' : 'secondary'}>
                  {` · ${t('legacyEmployeeLogs.detail.lagSuffix', { lag: detailLag.text })}`}
                </Typography.Text>
              </Descriptions.Item>
              <Descriptions.Item label={t('legacyEmployeeLogs.detail.storeDevice')}>
                {detailRecord.storeCode || '-'} · <Typography.Text code>{detailRecord.deviceCode || '-'}</Typography.Text>
              </Descriptions.Item>
              <Descriptions.Item label={t('legacyEmployeeLogs.detail.employee')}>
                {detailRecord.employeeName || '-'}
                {detailRecord.employeeId ? (
                  <Typography.Text type="secondary" copyable={{ text: detailRecord.employeeId }} style={{ fontSize: 11, marginInlineStart: 6 }}>
                    {detailRecord.employeeId}
                  </Typography.Text>
                ) : null}
              </Descriptions.Item>
              <Descriptions.Item label={t('legacyEmployeeLogs.detail.operation')}>{operationTag(detailRecord.operation)}</Descriptions.Item>
            </Descriptions>

            {detailParsed.productName || detailParsed.fields.length || detailParsed.orderGuid ? (
              <div>
                <DrawerSectionTitle title={t('legacyEmployeeLogs.detail.parsedTitle')} hint={t('legacyEmployeeLogs.detail.parsedHint')} />
                <Descriptions bordered size="small" column={1} labelStyle={{ width: 110 }}>
                  {detailParsed.productName ? (
                    <Descriptions.Item label={t('legacyEmployeeLogs.detail.product')}>{detailParsed.productName}</Descriptions.Item>
                  ) : null}
                  {detailParsed.orderGuid ? (
                    <Descriptions.Item label={t('legacyEmployeeLogs.detail.order')}>
                      <Typography.Text code copyable style={{ fontSize: 12 }}>{detailParsed.orderGuid}</Typography.Text>
                    </Descriptions.Item>
                  ) : null}
                  {detailParsed.fields.map((field) => (
                    <Descriptions.Item key={`${field.key}-${field.value}`} label={field.key}>
                      <span
                        style={{
                          fontVariantNumeric: 'tabular-nums',
                          fontWeight: field.tone ? 600 : undefined,
                          color: field.tone === 'danger' ? token.colorError : field.tone === 'money' ? token.colorSuccess : undefined,
                        }}
                      >
                        {field.value}
                      </span>
                    </Descriptions.Item>
                  ))}
                </Descriptions>
              </div>
            ) : null}

            <div>
              <DrawerSectionTitle title={t('legacyEmployeeLogs.detail.rawTitle')} />
              <Typography.Paragraph
                copyable={detailRecord.operationDetail ? { text: detailRecord.operationDetail } : false}
                style={{
                  background: token.colorFillQuaternary,
                  border: `1px solid ${token.colorSplit}`,
                  borderRadius: token.borderRadius,
                  padding: '10px 12px',
                  whiteSpace: 'pre-wrap',
                  wordBreak: 'break-all',
                  marginBottom: 0,
                }}
              >
                {detailRecord.operationDetail || '-'}
              </Typography.Paragraph>
            </div>

            <div>
              <DrawerSectionTitle
                title={t('legacyEmployeeLogs.detail.contextTitle')}
                hint={`${detailRecord.deviceCode || '-'} · ${t('legacyEmployeeLogs.detail.contextHint', { minutes: context?.windowMinutes ?? 5 })}`}
              />
              {contextLoading ? (
                <Spin />
              ) : contextError ? (
                <Alert type="warning" showIcon message={t('legacyEmployeeLogs.detail.contextFailed')} />
              ) : context && context.neighbors.length > 1 ? (
                <>
                  {context.truncated ? (
                    <Alert type="info" showIcon style={{ marginBottom: 12 }} message={t('legacyEmployeeLogs.detail.contextTruncated', { count: context.neighbors.length })} />
                  ) : null}
                  <Timeline
                    items={context.neighbors.map((item) => {
                      const isCurrent = item.id === detailRecord.id
                      const parsed = parseLegacyDetail(item.operationDetail, item.operation)
                      return {
                        key: item.id,
                        color: isCurrent ? 'red' : 'gray',
                        children: (
                          <div style={{ fontWeight: isCurrent ? 500 : undefined, fontSize: 13 }}>
                            <Typography.Text type="secondary" style={{ fontSize: 12, marginInlineEnd: 8, fontVariantNumeric: 'tabular-nums' }}>
                              {dayjs(item.operationTime).format('HH:mm:ss')}
                            </Typography.Text>
                            {operationTag(item.operation)}
                            {item.employeeName && item.employeeName !== detailRecord.employeeName ? (
                              <Typography.Text type="secondary" style={{ marginInlineStart: 6 }}>{item.employeeName}</Typography.Text>
                            ) : null}
                            <div style={{ marginTop: 2 }}>{renderParsedDetail(parsed, item.operationDetail)}</div>
                          </div>
                        ),
                      }
                    })}
                  />
                </>
              ) : (
                <Typography.Text type="secondary">{t('legacyEmployeeLogs.detail.contextEmpty')}</Typography.Text>
              )}
              {detailRecord.employeeId ? (
                <Button size="small" onClick={() => showEmployeeDay(detailRecord)}>
                  {t('legacyEmployeeLogs.detail.onlyEmployeeDay')} →
                </Button>
              ) : null}
            </div>
          </Space>
        ) : null}
      </Drawer>
    </PageContainer>
  )
}

function DrawerSectionTitle({ title, hint }: { title: string; hint?: string }) {
  return (
    <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'baseline', gap: 12, marginBottom: 10 }}>
      <Typography.Text strong>{title}</Typography.Text>
      {hint ? <Typography.Text type="secondary" style={{ fontSize: 12 }}>{hint}</Typography.Text> : null}
    </div>
  )
}
