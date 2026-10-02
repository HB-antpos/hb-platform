import { AlertOutlined, CheckOutlined, EyeOutlined, FlagOutlined, ReloadOutlined, SearchOutlined, WarningOutlined } from '@ant-design/icons'
import {
  Alert,
  Button,
  Card,
  Col,
  DatePicker,
  Drawer,
  Empty,
  Form,
  Grid,
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
import dayjs from 'dayjs'
import { useCallback, useEffect, useMemo, useRef, useState, type ReactNode } from 'react'
import { useTranslation } from 'react-i18next'

import { MeasuredTable } from '../../../components/MeasuredTable'
import PageContainer from '../../../components/PageContainer'
import { registerPageMessages } from '../../../i18n/registerPageMessages'
import {
  getLegacyEmployeeLogContext,
  getLegacyEmployeeLogEmployeeSummary,
  getLegacyEmployeeLogs,
} from '../../../services/legacyEmployeeLogService'
import { getActiveStores, type StoreOption } from '../../../services/storeService'
import { useAuthStore } from '../../../store/auth'
import type {
  LegacyEmployeeLogContext,
  LegacyEmployeeLogEmployeeSummary,
  LegacyEmployeeLogEmployeeSummaryResult,
  LegacyEmployeeLogItem,
  LegacyEmployeeLogListResult,
  LegacyEmployeeLogReview,
  LegacyReviewStatus,
  LegacyRiskLens,
} from '../../../types/legacyEmployeeLog'
import {
  buildStoreOptionsFromUserStores,
} from '../../../utils/managedStoreScope'

import type { EmployeeLogsHeader } from '../EmployeeLogs/employeeLogsSource'

import EmployeeSummaryTable from './EmployeeSummaryTable'
import {
  CATEGORY_TAG_COLOR,
  DEFAULT_RISK_FILTER,
  LEGACY_LOG_DEFAULT_PAGE_SIZE,
  LEGACY_LOG_LATE_UPLOAD_MINUTES,
  LEGACY_LOG_STORE_STORAGE_KEY,
  buildLegacyLogQuery,
  buildOperationOptions,
  buildStoreNameMap,
  createLatestRequestGuard,
  formatStoreLabel,
  formatWallClock,
  getDayRange,
  getOperationCategory,
  getUploadLagMinutes,
  isHighRiskOperation,
  normalizeStoreCodes,
  parseLegacyDetail,
  resolveInitialStores,
  splitLagMinutes,
  storeSelectionKey,
  toQueryRange,
  validateTimeRange,
  type LegacyLogFormValues,
  type LegacyRiskFilter,
  type ParsedLegacyDetail,
} from './legacyEmployeeLogsLogic'
import legacyEmployeeLogsMessagesEn from './legacyEmployeeLogsMessages.en.json'
import legacyEmployeeLogsMessagesZh from './legacyEmployeeLogsMessages.zh.json'
import LegacyLogDetailPanel from './LegacyLogDetailPanel'
import RiskLensBar from './RiskLensBar'

// 页面文案随页面代码块懒加载，不进首屏 i18n 包。
registerPageMessages({ zh: legacyEmployeeLogsMessagesZh, en: legacyEmployeeLogsMessagesEn })

type TableSortOrder = 'ascend' | 'descend'
type ResultView = 'records' | 'employees'

function readRememberedStores() {
  try {
    return localStorage.getItem(LEGACY_LOG_STORE_STORAGE_KEY)
  } catch {
    return null
  }
}

function rememberStores(storeCodes: string[]) {
  try {
    localStorage.setItem(LEGACY_LOG_STORE_STORAGE_KEY, JSON.stringify(storeCodes))
  } catch {
    // 浏览器存储不可用时只是不记住上次的分店，不影响查询。
  }
}

function errorText(error: unknown) {
  return error instanceof Error && error.message ? error.message : undefined
}

/** header 由员工操作日志合并页传入（统一标题与来源切换）；单独使用时沿用本页标题。 */
export default function PosAdminLegacyEmployeeLogsPage({ header }: { header?: EmployeeLogsHeader } = {}) {
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
  const employeesGuardRef = useRef(createLatestRequestGuard())
  // 风险入口与细分：状态驱动界面，ref 让查询函数总能读到最新值（切换入口后立即查询）。
  const [risk, setRiskState] = useState<LegacyRiskFilter>(DEFAULT_RISK_FILTER)
  const riskRef = useRef<LegacyRiskFilter>(DEFAULT_RISK_FILTER)
  const [view, setView] = useState<ResultView>('records')
  const viewRef = useRef<ResultView>('records')
  const [employeeSummary, setEmployeeSummary] = useState<LegacyEmployeeLogEmployeeSummaryResult | null>(null)
  const [employeeLoading, setEmployeeLoading] = useState(false)
  const [employeeError, setEmployeeError] = useState<string | null>(null)
  // 宽屏（≥1600px）把详情常驻在表格右侧，点行即切换；窄屏仍用抽屉。
  const screens = Grid.useBreakpoint()
  const sidePanel = Boolean(screens.xxl)
  const selectedOperations = Form.useWatch('operations', form)
  const selectedStoreCodes = Form.useWatch('storeCodes', form)
  const selectedStoresKey = storeSelectionKey(selectedStoreCodes)
  const hasStores = selectedStoresKey !== ''
  const [storeDropdownOpen, setStoreDropdownOpen] = useState(false)

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
      if (normalizeStoreCodes(values.storeCodes).length === 0) {
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
      }, riskRef.current)
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
        rememberStores(query.storeCodes)
        // 详情面板里的记录换成最新数据（核查结论、命中规则可能已变）。
        setDetailRecord((current) => (current ? data.items.find((item) => item.id === current.id) ?? current : current))
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

  // 按员工汇总只用分店、时间、设备条件，与列表的风险入口和其余筛选无关。
  const loadEmployees = useCallback(async () => {
    const values = form.getFieldsValue()
    const storeCodes = normalizeStoreCodes(values.storeCodes)
    if (storeCodes.length === 0 || !values.timeRange || validateTimeRange(values.timeRange)) {
      setEmployeeSummary(null)
      return
    }
    const [from, to] = toQueryRange(values.timeRange)
    const requestId = employeesGuardRef.current.begin()
    setEmployeeLoading(true)
    setEmployeeError(null)
    try {
      const data = await getLegacyEmployeeLogEmployeeSummary({
        storeCodes,
        from: formatWallClock(from),
        to: formatWallClock(to),
        deviceCode: values.deviceCode?.trim() || undefined,
      })
      if (employeesGuardRef.current.isLatest(requestId)) setEmployeeSummary(data)
    } catch (error) {
      if (!employeesGuardRef.current.isLatest(requestId)) return
      console.error(error)
      setEmployeeError(errorText(error) ?? t('legacyEmployeeLogs.employees.loadFailed'))
    } finally {
      if (employeesGuardRef.current.isLatest(requestId)) setEmployeeLoading(false)
    }
  }, [form, t])

  // 分店列表就绪后选定默认分店：恢复上次查询的分店（仍可选的部分），只有一个可选分店时直接选中。
  useEffect(() => {
    if (normalizeStoreCodes(form.getFieldValue('storeCodes')).length > 0 || visibleStoreOptions.length === 0) return
    const initial = resolveInitialStores(readRememberedStores(), visibleStoreOptions.map((option) => option.value))
    // 只设字段；查询由下面的分店变化监听统一发起。
    if (initial.length > 0) form.setFieldsValue({ storeCodes: initial })
  }, [form, visibleStoreOptions])

  // 分店选择变化后查询：下拉展开期间连续勾选不发请求，收起（或在收起状态下删掉标签、清空）后才查一次。
  // 员工与设备选项属于上一次的分店范围，范围变化时先清空，避免带着别店员工条件查询。
  const lastStoresKeyRef = useRef('')
  useEffect(() => {
    if (storeDropdownOpen || selectedStoresKey === lastStoresKeyRef.current) return
    const hadStores = lastStoresKeyRef.current !== ''
    lastStoresKeyRef.current = selectedStoresKey
    if (hadStores) {
      form.setFieldsValue({ employeeIds: undefined, deviceCode: undefined })
      setResult(null)
    }
    if (!selectedStoresKey) return
    setPageNumber(1)
    void loadData(1, pageSize, sortOrder)
    if (viewRef.current === 'employees') void loadEmployees()
    // 只在分店选择或下拉开合变化时触发；分页与排序变化各自发起查询。
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [selectedStoresKey, storeDropdownOpen])

  const initialValues = useMemo<LegacyLogFormValues>(() => ({ timeRange: getDayRange(dayjs()) }), [])

  const runQuery = () => {
    setPageNumber(1)
    void loadData(1, pageSize, sortOrder)
    if (viewRef.current === 'employees') void loadEmployees()
  }

  const setRisk = (next: LegacyRiskFilter) => {
    riskRef.current = next
    setRiskState(next)
  }

  const switchView = (next: ResultView) => {
    viewRef.current = next
    setView(next)
    if (next === 'employees' && hasStores) void loadEmployees()
  }

  // 切换入口时清掉上一个入口的细分（操作类型 / 规则 / 核查状态），并回到记录页。
  const changeLens = (lens: LegacyRiskLens) => {
    form.setFieldsValue({ operations: undefined })
    setRisk({ riskLens: lens, ruleCodes: [], reviewStatus: 'all' })
    switchView('records')
    runQuery()
  }

  const changeOperations = (operations: string[] | undefined) => {
    form.setFieldsValue({ operations })
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

  // 员工汇总「查看明细」：带上该员工，停在有异常就看异常、否则看危险的入口。
  const viewEmployee = useCallback((row: LegacyEmployeeLogEmployeeSummary) => {
    if (!row.employeeId) return
    form.setFieldsValue({ employeeIds: [row.employeeId], operations: undefined })
    const lens: LegacyRiskLens = row.abnormalCount > 0 ? 'abnormal' : 'danger'
    riskRef.current = { riskLens: lens, ruleCodes: [], reviewStatus: 'all' }
    setRiskState(riskRef.current)
    viewRef.current = 'records'
    setView('records')
    setPageNumber(1)
    void loadData(1, pageSize, sortOrder)
  }, [form, loadData, pageSize, sortOrder])

  const handleReset = () => {
    const storeCodes = form.getFieldValue('storeCodes') as string[] | undefined
    form.resetFields()
    // 重置只清筛选条件，保留所选分店，避免重置后页面变成空白。
    form.setFieldsValue({ storeCodes, timeRange: getDayRange(dayjs()) })
    setRisk(DEFAULT_RISK_FILTER)
    setSortOrder('descend')
    setPageNumber(1)
    void loadData(1, pageSize, 'descend')
    if (viewRef.current === 'employees') void loadEmployees()
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
      // 命中异常的记录看更长的上下文（例如开钱箱前最近一次结账）。
      const data = await getLegacyEmployeeLogContext(record.id, record.flags?.length ? 15 : undefined)
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

  // 核查后：本地先更新这一行，再静默刷新当前页，让入口计数（待核查）跟上；冲突时直接刷新。
  const handleReviewChanged = (record: LegacyEmployeeLogItem, review: LegacyEmployeeLogReview | null) => {
    if (review) {
      const patch = (item: LegacyEmployeeLogItem) => (item.id === record.id ? { ...item, review } : item)
      setDetailRecord((current) => (current ? patch(current) : current))
      setResult((current) => (current ? { ...current, items: current.items.map(patch) } : current))
    }
    void loadData(pageNumber, pageSize, sortOrder)
    if (viewRef.current === 'employees') void loadEmployees()
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
    setRisk(DEFAULT_RISK_FILTER)
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

  // 星期按页面语言显示，不依赖 dayjs 的全局语言（生产上它是英文，会显示成 Wed）。
  const weekdayLabel = useCallback(
    (day: number) => {
      const labels = t('legacyEmployeeLogs.weekdays', { returnObjects: true })
      return Array.isArray(labels) && typeof labels[day] === 'string' ? labels[day] : ''
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

  // 结果里出现多家分店时才显示分店列，单店时省出宽度。
  const multiStore = new Set((result?.items ?? []).map((item) => item.storeCode)).size > 1
    || normalizeStoreCodes(selectedStoreCodes).length > 1

  const columns = useMemo<ColumnsType<LegacyEmployeeLogItem>>(
    () => [
      {
        title: t('legacyEmployeeLogs.columns.time'),
        dataIndex: 'operationTime',
        key: 'operationTime',
        width: 130,
        sorter: true,
        sortOrder,
        render: (value: string) => {
          const time = dayjs(value)
          return (
            <div style={{ fontVariantNumeric: 'tabular-nums', whiteSpace: 'nowrap' }}>
              <div>{time.format('HH:mm:ss')}</div>
              <Typography.Text type="secondary" style={{ fontSize: 12 }}>{`${time.format('YYYY-MM-DD')} ${weekdayLabel(time.day())}`}</Typography.Text>
            </div>
          )
        },
      },
      ...(multiStore
        ? [{
            title: t('legacyEmployeeLogs.columns.store'),
            dataIndex: 'storeCode',
            key: 'storeCode',
            width: 170,
            render: (value?: string | null) => (
              <Typography.Text ellipsis={{ tooltip: true }} style={{ maxWidth: 160 }}>{formatStoreLabel(value, storeNames)}</Typography.Text>
            ),
          }]
        : []),
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
        // 详情列给足最小宽度：其余列都是固定宽，详情面板常驻时表格变窄，宁可横向滚动也不把详情挤成一列字。
        width: 320,
        render: (value: string | null | undefined, record) => renderParsedDetail(parseLegacyDetail(value, record.operation), value),
      },
      {
        title: t('legacyEmployeeLogs.columns.amount'),
        dataIndex: 'amountImpact',
        key: 'amountImpact',
        width: 100,
        align: 'right',
        render: (value?: number | null) => value ? (
          <span style={{ color: token.colorError, fontWeight: 600, fontVariantNumeric: 'tabular-nums' }}>−{value.toFixed(2)}</span>
        ) : <Typography.Text type="secondary">-</Typography.Text>,
      },
      {
        title: t('legacyEmployeeLogs.columns.risk'),
        key: 'risk',
        width: 190,
        // 与操作列一起固定在右侧：详情面板常驻、表格横向滚动时，风险标签始终可见。
        fixed: 'right',
        render: (_, record) => {
          const danger = record.isDanger ?? isHighRiskOperation(record.operation)
          const review = record.review && record.review.result !== 'revoked' ? record.review : null
          if (!danger && !record.flags?.length) return null
          return (
            <div style={{ display: 'flex', flexWrap: 'wrap', gap: 4 }}>
              {danger ? <Tag color="error" icon={<WarningOutlined />} style={{ marginInlineEnd: 0 }}>{t('legacyEmployeeLogs.badges.danger')}</Tag> : null}
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
    [multiStore, operationTag, renderParsedDetail, sortOrder, storeNames, t, token, weekdayLabel],
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
  const currentUserName = currentUser?.fullName || currentUser?.username || ''

  const detailPanel = detailRecord ? (
    <LegacyLogDetailPanel
      record={detailRecord}
      context={context}
      contextLoading={contextLoading}
      contextError={contextError}
      storeLabel={formatStoreLabel(detailRecord.storeCode, storeNames)}
      lag={formatLag(detailRecord)}
      canReview={access.canReviewLegacyEmployeeLogs}
      currentUserName={currentUserName}
      operationTag={operationTag}
      renderParsedDetail={renderParsedDetail}
      onShowEmployeeDay={showEmployeeDay}
      onReviewChanged={handleReviewChanged}
    />
  ) : null

  const lensName = t(`legacyEmployeeLogs.lens.${risk.riskLens}.title`)

  return (
    <PageContainer
      title={header?.title ?? t('legacyEmployeeLogs.pageTitle')}
      subtitle={header?.subtitle ?? t('legacyEmployeeLogs.pageSubtitle')}
      extra={(
        <Space wrap>
          {header?.switcher}
          <Button icon={<ReloadOutlined />} disabled={!hasStores} onClick={() => void loadData(pageNumber, pageSize, sortOrder)}>
            {t('common.refresh')}
          </Button>
        </Space>
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
                  name="storeCodes"
                  rules={[{ required: true, type: 'array', min: 1, message: t('legacyEmployeeLogs.validation.storeRequired') }]}
                >
                  <Select
                    mode="multiple"
                    allowClear
                    maxTagCount="responsive"
                    optionFilterProp="label"
                    options={visibleStoreOptions}
                    placeholder={t('legacyEmployeeLogs.filters.storePlaceholder')}
                    onOpenChange={setStoreDropdownOpen}
                    popupRender={(menu) => (
                      <>
                        {visibleStoreOptions.length > 1 ? (
                          <Space style={{ padding: '4px 8px' }}>
                            <Button
                              size="small"
                              type="link"
                              onMouseDown={(event) => event.preventDefault()}
                              onClick={() => form.setFieldsValue({ storeCodes: visibleStoreOptions.map((option) => option.value) })}
                            >
                              {t('legacyEmployeeLogs.filters.selectAllStores', { count: visibleStoreOptions.length })}
                            </Button>
                            <Button
                              size="small"
                              type="link"
                              onMouseDown={(event) => event.preventDefault()}
                              onClick={() => form.setFieldsValue({ storeCodes: [] })}
                            >
                              {t('legacyEmployeeLogs.filters.clearStores')}
                            </Button>
                          </Space>
                        ) : null}
                        {menu}
                      </>
                    )}
                  />
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
            <div style={{ display: 'flex', justifyContent: 'flex-end', gap: 8, paddingTop: 12, borderTop: `1px dashed ${token.colorSplit}` }}>
              <Button onClick={handleReset}>{t('common.reset')}</Button>
              <Button type="primary" htmlType="submit" icon={<SearchOutlined />} loading={loading}>
                {t('common.query')}
              </Button>
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
          <RiskLensBar
            risk={risk}
            operations={selectedOperations}
            total={result.total}
            counts={counts}
            summary={result.riskSummary}
            people={result.employees.length}
            devices={result.devices.length}
            onLensChange={changeLens}
            onOperationsChange={changeOperations}
            onRuleChange={changeRule}
            onReviewStatusChange={changeReviewStatus}
          />
        ) : null}

        <div style={{ display: 'flex', gap: 16, alignItems: 'flex-start' }}>
          <Card style={{ flex: 1, minWidth: 0 }} styles={{ body: { padding: view === 'records' ? undefined : 0 } }}>
            <Tabs
              activeKey={view}
              onChange={(key) => switchView(key as ResultView)}
              tabBarExtraContent={view === 'records' && result ? (
                <Typography.Text type="secondary" style={{ fontSize: 13 }}>
                  {`${lensName} · ${t('legacyEmployeeLogs.table.paginationTotal', { total: result.total })}`}
                </Typography.Text>
              ) : view === 'employees' ? (
                <Typography.Text type="secondary" style={{ fontSize: 12 }}>{t('legacyEmployeeLogs.employees.hint')}</Typography.Text>
              ) : null}
              tabBarStyle={view === 'employees' ? { paddingInline: 16, marginBottom: 0 } : undefined}
              items={[
                {
                  key: 'records',
                  label: t('legacyEmployeeLogs.tabs.records'),
                  children: (
                    <MeasuredTable<LegacyEmployeeLogItem>
                      metricId="pos-admin.legacy-employee-logs.table-1"
                      rowKey="id"
                      size="middle"
                      loading={loading}
                      columns={columns}
                      dataSource={result?.items ?? []}
                      scroll={{ x: 1420 }}
                      sortDirections={['descend', 'ascend', 'descend']}
                      locale={{
                        emptyText: (
                          <Empty
                            image={Empty.PRESENTED_IMAGE_SIMPLE}
                            description={hasStores ? t('legacyEmployeeLogs.table.empty') : t('legacyEmployeeLogs.table.selectStoreFirst')}
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
                  ),
                },
                {
                  key: 'employees',
                  label: t('legacyEmployeeLogs.tabs.employees'),
                  children: (
                    <EmployeeSummaryTable
                      data={employeeSummary}
                      loading={employeeLoading}
                      error={employeeError}
                      storeNames={storeNames}
                      onRetry={() => void loadEmployees()}
                      onViewEmployee={viewEmployee}
                    />
                  ),
                },
              ]}
            />
          </Card>

          {sidePanel && detailPanel ? (
            <Card
              title={t('legacyEmployeeLogs.detail.title')}
              extra={<Button type="text" size="small" onClick={closeDetail} aria-label={t('common.close')}>×</Button>}
              style={{ width: 440, flex: 'none', position: 'sticky', top: 16, maxHeight: 'calc(100vh - 32px)', overflow: 'auto' }}
            >
              {detailPanel}
            </Card>
          ) : null}
        </div>
      </Space>

      {!sidePanel ? (
        <Drawer
          open={Boolean(detailRecord)}
          onClose={closeDetail}
          width="min(560px, 100vw)"
          title={t('legacyEmployeeLogs.detail.title')}
        >
          {detailPanel}
        </Drawer>
      ) : null}
    </PageContainer>
  )
}
